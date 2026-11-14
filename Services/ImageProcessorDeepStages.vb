Imports System
Imports System.Collections.Generic
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ' Die Umgebungsstufen der Pixelkette fuer das tiefe Bild der 16-Bit-Ausgabe (ImageProcessorDeep.vb).
    '
    ' WARUM EIGENE DURCHLAEUFE. Die Stufen der Vorschau rechnen auf Bytes: jede liest 8 Bit, rundet
    ' auf 8 Bit, und ihre Shader bilden genau das nach, damit Karte und Prozessor dieselben Bytes
    ' liefern. Fuer das tiefe Bild taugt keine von beiden Fassungen. Hier steht je Stufe dieselbe
    ' Rechnung in Gleitkomma, mit denselben Kennzahlen und in derselben Einheit (0 bis 255, damit
    ' Schwellen und Massstaebe unveraendert gelten), aber ohne Rundung dazwischen; gerundet wird
    ' einmal am Ende auf 16 Bit.
    '
    ' DAS 8-BIT-BILD FOLGT DEM TIEFEN. Rechnet eine Stufe tief, entsteht ihr 8-Bit-Ergebnis aus dem
    ' tiefen mit Dither (ShallowFromDeep) und nicht aus einem zweiten Lauf der 8-Bit-Stufe. Eine
    ' Stufe wie die Schaerfe verstaerkt den Dither ihres 8-Bit-Eingangs um ein Mehrfaches; zwei
    ' getrennte Laeufe laegen dort mehrere Stufen auseinander, und das Paar waere nicht mehr
    ' dasselbe Bild.
    Partial Public Class ImageProcessor

        ''' <summary>Das 8-Bit-Bild zu einem tiefen: jeder Wert mit dem Dither der Punktkette auf
        ''' 8 Bit gebracht, dieselbe Schwelle fuer alle drei Kanaele eines Bildpunkts. Nothing, wenn
        ''' der Farbtyp nicht BGRA oder RGBA mit 8 Bit ist.</summary>
        Friend Shared Function ShallowFromDeep(deep As SKBitmap, colorType As SKColorType, alphaType As SKAlphaType) As SKBitmap
            If deep Is Nothing OrElse deep.ColorType <> DeepColorType Then Return Nothing
            Dim ri, bi As Integer
            Select Case colorType
                Case SKColorType.Bgra8888
                    ri = 2 : bi = 0
                Case SKColorType.Rgba8888
                    ri = 0 : bi = 2
                Case Else
                    Return Nothing
            End Select
            Dim width = deep.Width, height = deep.Height
            Dim deepPixels = deep.GetPixels()
            If deepPixels = IntPtr.Zero Then Return Nothing
            Dim result = New SKBitmap(New SKImageInfo(width, height, colorType, alphaType))
            Dim target = result.GetPixels()
            If target = IntPtr.Zero Then
                result.Dispose()
                Return Nothing
            End If
            Dim targetStride = result.RowBytes
            Parallel.For(0, height,
                Sub(y)
                    Dim deepRow(width * 4 - 1) As Short
                    Dim row(width * 4 - 1) As Byte
                    ReadDeepRow(deep, deepPixels, y, deepRow)
                    Dim ditherRow = (y And 7) << 3
                    For x = 0 To width - 1
                        Dim o = x * 4
                        Dim dth = DitherMatrix(ditherRow Or (x And 7))
                        Dim a = CByte(((deepRow(o + 3) And &HFFFF) + 128) \ 257)
                        Dim r = QuantizeDithered((deepRow(o) And &HFFFF) / 65535.0F, dth)
                        Dim g = QuantizeDithered((deepRow(o + 1) And &HFFFF) / 65535.0F, dth)
                        Dim b = QuantizeDithered((deepRow(o + 2) And &HFFFF) / 65535.0F, dth)
                        If alphaType = SKAlphaType.Premul AndAlso a < 255 Then
                            ' Vormultipliziert darf keine Farbe ueber der Deckung liegen.
                            If r > a Then r = a
                            If g > a Then g = a
                            If b > a Then b = a
                        End If
                        row(o + ri) = r
                        row(o + 1) = g
                        row(o + bi) = b
                        row(o + 3) = a
                    Next
                    Marshal.Copy(row, 0, IntPtr.Add(target, y * targetStride), width * 4)
                End Sub)
            result.NotifyPixelsChanged()
            Return result
        End Function

        ''' <summary>Ein Bildpunkt des tiefen Bilds als Farbe ohne Vormultiplikation, in der Einheit
        ''' der 8-Bit-Stufen (0 bis 255, mit Nachkommastellen), dazu die Deckung als 16-Bit-Wert.</summary>
        Private Shared Sub ReadDeepUnpremultiplied(row As Short(), o As Integer, premul As Boolean,
                                                   ByRef r As Single, ByRef g As Single, ByRef b As Single,
                                                   ByRef alpha As Integer)
            alpha = row(o + 3) And &HFFFF
            If alpha = 65535 OrElse Not premul Then
                r = (row(o) And &HFFFF) / 257.0F
                g = (row(o + 1) And &HFFFF) / 257.0F
                b = (row(o + 2) And &HFFFF) / 257.0F
            ElseIf alpha = 0 Then
                r = 0.0F : g = 0.0F : b = 0.0F
            Else
                Dim scale = 255.0F / alpha
                r = Math.Min(255.0F, (row(o) And &HFFFF) * scale)
                g = Math.Min(255.0F, (row(o + 1) And &HFFFF) * scale)
                b = Math.Min(255.0F, (row(o + 2) And &HFFFF) * scale)
            End If
        End Sub

        ''' <summary>Schreibt eine Farbe in der Einheit der 8-Bit-Stufen als Bildpunkt des tiefen
        ''' Bilds, geklemmt auf 0 bis 255 und bei vormultiplizierter Deckung auf die Deckung.</summary>
        Private Shared Sub WriteDeepPremultiplied(row As Short(), o As Integer, premul As Boolean,
                                                  r As Single, g As Single, b As Single, alpha As Integer)
            If alpha = 0 AndAlso premul Then
                row(o) = 0S : row(o + 1) = 0S : row(o + 2) = 0S : row(o + 3) = 0S
                Return
            End If
            Dim scale = If(premul AndAlso alpha < 65535, alpha / 255.0F, 257.0F)
            Dim limit = If(premul, alpha, 65535)
            row(o) = ToDeepShort(Math.Min(limit, CInt(Math.Floor(Clamp(r, 0.0F, 255.0F) * scale + 0.5F))))
            row(o + 1) = ToDeepShort(Math.Min(limit, CInt(Math.Floor(Clamp(g, 0.0F, 255.0F) * scale + 0.5F))))
            row(o + 2) = ToDeepShort(Math.Min(limit, CInt(Math.Floor(Clamp(b, 0.0F, 255.0F) * scale + 0.5F))))
            row(o + 3) = ToDeepShort(alpha)
        End Sub

        ''' <summary>Weichzeichnet eine Ebene so, wie Skia ein Bild auf dem Prozessor weichzeichnet
        ''' (drei Kastenfilter je Richtung, RasterBlurKernel), mit fortgesetztem Rand wie
        ''' GaussianClamped, aber in Gleitkomma und ohne das 8-Bit-Zwischenbild zwischen den beiden
        ''' Richtungen.</summary>
        Private Shared Function BlurPlaneLikeSkia(plane As Single(), width As Integer, height As Integer, sigma As Single) As Single()
            Dim window = RasterBlurWindow(sigma)
            Dim result(width * height - 1) As Single
            If window <= 1 Then
                Array.Copy(plane, result, width * height)
                Return result
            End If
            ' Die drei Kaesten wie in RasterBlurKernel, jeder als (Punkte davor, Punkte danach).
            ' Ungerades w: dreimal dieselbe Breite um die Mitte. Gerades w: zwei Kaesten der Breite
            ' w, gegeneinander um einen Punkt versetzt, und einer der Breite w + 1.
            Dim half = window \ 2
            Dim boxes As (Before As Integer, After As Integer)()
            If (window And 1) = 1 Then
                boxes = {(half, half), (half, half), (half, half)}
            Else
                boxes = {(half, half - 1), (half - 1, half), (half, half)}
            End If
            ' DER RAND WIRD EINMAL FORTGESETZT, nicht in jedem Durchgang neu: die Zeile bekommt vorn
            ' und hinten ihren Randwert angesetzt, dann laufen die drei Kaesten ueber die
            ' verlaengerte Zeile. Setzte jeder Kasten sein Zwischenergebnis selbst fort, laege das
            ' Ergebnis am Bildrand um mehrere Stufen neben Skias (gemessen bis 5,8 bei Sigma 20).
            ' Angesetzt wird die doppelte Reichweite aller drei Kaesten: was die Kaesten am Ende
            ' der verlaengerten Zeile verfaelschen, kommt so nicht bis ins Bild.
            Dim pad = 2 * (boxes(0).Before + boxes(1).Before + boxes(2).Before + 1)
            Parallel.For(0, height,
                Sub(y)
                    Dim line(width + 2 * pad - 1) As Single
                    Dim scratch(width + 2 * pad - 1) As Single
                    Array.Copy(plane, y * width, line, pad, width)
                    BlurPaddedLine(line, scratch, width, pad, boxes)
                    Array.Copy(line, pad, result, y * width, width)
                End Sub)
            Parallel.For(0, width,
                Sub(x)
                    Dim line(height + 2 * pad - 1) As Single
                    Dim scratch(height + 2 * pad - 1) As Single
                    For y = 0 To height - 1
                        line(pad + y) = result(y * width + x)
                    Next
                    BlurPaddedLine(line, scratch, height, pad, boxes)
                    For y = 0 To height - 1
                        result(y * width + x) = line(pad + y)
                    Next
                End Sub)
            Return result
        End Function

        ''' <summary>Setzt den Rand einer Zeile fort (die Werte stehen ab <paramref name="pad"/>) und
        ''' laesst die drei Kaesten darueber laufen. Das Ergebnis steht wieder in
        ''' <paramref name="line"/> an derselben Stelle.</summary>
        Private Shared Sub BlurPaddedLine(line As Single(), scratch As Single(), length As Integer, pad As Integer,
                                          boxes As (Before As Integer, After As Integer)())
            Dim first = line(pad), last = line(pad + length - 1)
            For i = 0 To pad - 1
                line(i) = first
                line(pad + length + i) = last
            Next
            ' Drei Durchgaenge: hin, zurueck, hin. Das Ergebnis des dritten steht in scratch.
            BoxPass(line, scratch, line.Length, boxes(0).Before, boxes(0).After)
            BoxPass(scratch, line, line.Length, boxes(1).Before, boxes(1).After)
            BoxPass(line, scratch, line.Length, boxes(2).Before, boxes(2).After)
            Array.Copy(scratch, pad, line, pad, length)
        End Sub

        ''' <summary>Ein Kastenfilter ueber eine Zeile mit fortgesetztem Rand: jeder Punkt wird der
        ''' Mittelwert von <paramref name="before"/> Punkten davor, sich selbst und
        ''' <paramref name="after"/> Punkten danach. Als mitlaufende Summe, die Zeit haengt also
        ''' nicht an der Breite des Kastens.</summary>
        Private Shared Sub BoxPass(source As Single(), target As Single(), length As Integer, before As Integer, after As Integer)
            Dim count = before + after + 1
            Dim sum = 0.0
            For j = -before To after
                sum += source(Math.Min(length - 1, Math.Max(0, j)))
            Next
            For i = 0 To length - 1
                target(i) = CSng(sum / count)
                sum += source(Math.Min(length - 1, i + 1 + after)) - source(Math.Max(0, i - before))
            Next
        End Sub

        ''' <summary>Ein tiefes Bild als Ebenen in Gleitkomma: Rot, Gruen und Blau ohne
        ''' Vormultiplikation in der Einheit der 8-Bit-Stufen (0 bis 255), dazu die Deckung in
        ''' derselben Einheit. Die Form, in der die tiefen Stufen rechnen.</summary>
        Private NotInheritable Class DeepPlanes
            Public Width As Integer
            Public Height As Integer
            Public R As Single()
            Public G As Single()
            Public B As Single()
            ''' Die Deckung, oder Nothing bei einem ueberall deckenden Bild.
            Public A As Single()
            Public AlphaType As SKAlphaType

            Public Shared Function Load(deep As SKBitmap) As DeepPlanes
                If deep Is Nothing OrElse deep.ColorType <> DeepColorType Then Return Nothing
                Dim w = deep.Width, h = deep.Height
                Dim pixels = deep.GetPixels()
                If pixels = IntPtr.Zero OrElse w <= 0 OrElse h <= 0 Then Return Nothing
                Dim planes = New DeepPlanes With {.Width = w, .Height = h, .AlphaType = deep.AlphaType,
                                                  .R = New Single(w * h - 1) {}, .G = New Single(w * h - 1) {},
                                                  .B = New Single(w * h - 1) {}}
                Dim premul = deep.AlphaType = SKAlphaType.Premul
                Dim alpha As Single() = If(deep.AlphaType = SKAlphaType.Opaque, Nothing, New Single(w * h - 1) {})
                Dim transparentRows = 0
                Parallel.For(0, h,
                    Sub(y)
                        Dim row(w * 4 - 1) As Short
                        ReadDeepRow(deep, pixels, y, row)
                        Dim at = y * w
                        Dim transparent = False
                        For x = 0 To w - 1
                            Dim a As Integer
                            ReadDeepUnpremultiplied(row, x * 4, premul, planes.R(at + x), planes.G(at + x), planes.B(at + x), a)
                            If alpha IsNot Nothing Then
                                alpha(at + x) = a / 257.0F
                                If a < 65535 Then transparent = True
                            End If
                        Next
                        If transparent Then Threading.Interlocked.Increment(transparentRows)
                    End Sub)
                planes.A = If(transparentRows > 0, alpha, Nothing)
                Return planes
            End Function

            ''' <summary>Leere Ebenen in denselben Massen, mit Deckung, wenn dieses Bild eine hat.</summary>
            Public Function NewLike() As DeepPlanes
                Dim n = Width * Height
                Return New DeepPlanes With {.Width = Width, .Height = Height, .AlphaType = AlphaType,
                                            .R = New Single(n - 1) {}, .G = New Single(n - 1) {}, .B = New Single(n - 1) {},
                                            .A = If(A Is Nothing, Nothing, New Single(n - 1) {})}
            End Function

            Public Function ToBitmap() As SKBitmap
                Dim result = NewDeepBitmap(Width, Height, AlphaType)
                If result Is Nothing Then Return Nothing
                Dim pixels = result.GetPixels()
                Dim premul = AlphaType = SKAlphaType.Premul
                Dim w = Width
                Parallel.For(0, Height,
                    Sub(y)
                        Dim row(w * 4 - 1) As Short
                        Dim at = y * w
                        For x = 0 To w - 1
                            ' Nicht "a" nennen: VB unterscheidet es nicht vom Feld A.
                            Dim cover = 65535
                            If Me.A IsNot Nothing Then cover = CInt(Math.Floor(Clamp(Me.A(at + x), 0.0F, 255.0F) * 257.0F + 0.5F))
                            WriteDeepPremultiplied(row, x * 4, premul, R(at + x), G(at + x), B(at + x), cover)
                        Next
                        WriteDeepRow(result, pixels, y, row)
                    End Sub)
                Return result
            End Function

            ''' <summary>Weichgezeichnet wie GaussianClamped: Skias drei Kastenfilter je Richtung mit
            ''' fortgesetztem Rand. Mit Deckung auf den vormultiplizierten Werten, wie Skia es tut.</summary>
            Public Function BlurredLikeSkia(sigma As Single) As DeepPlanes
                Dim blurred = New DeepPlanes With {.Width = Width, .Height = Height, .AlphaType = AlphaType}
                If A Is Nothing Then
                    blurred.R = BlurPlaneLikeSkia(R, Width, Height, sigma)
                    blurred.G = BlurPlaneLikeSkia(G, Width, Height, sigma)
                    blurred.B = BlurPlaneLikeSkia(B, Width, Height, sigma)
                    Return blurred
                End If
                Dim n = Width * Height
                Dim scaled(n - 1) As Single
                Dim blurredAlpha = BlurPlaneLikeSkia(A, Width, Height, sigma)
                Dim channel = Function(source As Single()) As Single()
                                  For i = 0 To n - 1
                                      scaled(i) = source(i) * A(i) / 255.0F
                                  Next
                                  Dim result = BlurPlaneLikeSkia(scaled, Width, Height, sigma)
                                  For i = 0 To n - 1
                                      Dim cover = blurredAlpha(i)
                                      result(i) = If(cover <= 0.0F, 0.0F, Math.Min(255.0F, result(i) * 255.0F / cover))
                                  Next
                                  Return result
                              End Function
                blurred.R = channel(R)
                blurred.G = channel(G)
                blurred.B = channel(B)
                blurred.A = blurredAlpha
                Return blurred
            End Function
        End Class

        ''' <summary>Rechnet eine Stufe auf dem tiefen Bild von <paramref name="processed"/> und liefert
        ''' das 8-Bit-Bild dazu (aus dem tiefen Ergebnis, mit Dither), mit dem tiefen Ergebnis als
        ''' Begleiter. Nothing, wenn kein tiefes Bild daran haengt oder die Stufe keines liefert;
        ''' der Aufrufer rechnet dann die 8-Bit-Stufe wie gewohnt.</summary>
        Private Shared Function RunDeepStage(processed As SKBitmap, measureName As String,
                                             deepStage As Func(Of SKBitmap, SKBitmap)) As SKBitmap
            If deepStage Is Nothing Then Return Nothing
            Dim deepIn = DeepOf(processed)
            If deepIn Is Nothing Then Return Nothing
            Dim deepOut = PerformanceTraceService.Measure(measureName & " (16 Bit)", Function() deepStage(deepIn))
            If deepOut Is Nothing Then Return Nothing
            Dim shallow = ShallowFromDeep(deepOut, processed.ColorType, processed.AlphaType)
            If shallow Is Nothing Then
                deepOut.Dispose()
                Return Nothing
            End If
            AttachDeep(shallow, deepOut)
            Return shallow
        End Function

        ''' <summary>16-Bit-Wert nach Wurzel des Linearlichts, die Kodierung, in der das Farbrauschen
        ''' rechnet (ChromaToRoot fuer Bytes).</summary>
        Private Shared ReadOnly DeepToRoot As New Lazy(Of Single())(
            Function()
                Dim table = New Single(65535) {}
                For v = 0 To 65535
                    table(v) = CSng(Math.Sqrt(SrgbToLinear(v / 65535.0)))
                Next
                Return table
            End Function)

        Private Const DeepRootBackSize As Integer = 16384

        ''' <summary>Und zurueck: Wurzel des Linearlichts (0 bis 1 in DeepRootBackSize Schritten)
        ''' nach dem Tonwert 0 bis 1; dazwischen wird linear gelesen.</summary>
        Private Shared ReadOnly DeepRootToValue As New Lazy(Of Single())(
            Function()
                Dim table = New Single(DeepRootBackSize + 1) {}
                For j = 0 To DeepRootBackSize + 1
                    Dim root = Math.Min(1.0, j / CDbl(DeepRootBackSize))
                    table(j) = CSng(LinearToSrgb(root * root))
                Next
                Return table
            End Function)

        Private Shared Function DeepValueOfRoot(table As Single(), root As Single) As Single
            If Not root > 0.0F Then Return 0.0F
            If root >= 1.0F Then Return 1.0F
            Dim position = root * DeepRootBackSize
            Dim index = CInt(Math.Floor(position))
            Return table(index) + (table(index + 1) - table(index)) * (position - index)
        End Function

        ''' <summary>Das Farbrauschen auf dem tiefen Bild, dieselbe Rechnung wie
        ''' <see cref="ApplyColorNoiseReduction"/> mit denselben Bausteinen (die Farbebenen in der
        ''' Wurzel des Linearlichts sind dort schon Gleitkomma). Anders ist nur der Eingang und der
        ''' Ausgang: die Wurzel kommt aus dem 16-Bit-Wert statt aus dem Byte, und zurueck geht es
        ''' auf 16 Bit statt ueber die Bytetabelle.</summary>
        Private Shared Function ApplyColorNoiseReductionDeep(deep As SKBitmap, amount As Single) As SKBitmap
            amount = Clamp(amount, 0, 1)
            Dim w = deep.Width, h = deep.Height
            Dim sourcePixels = deep.GetPixels()
            If sourcePixels = IntPtr.Zero OrElse w <= 0 OrElse h <= 0 Then Return Nothing
            Dim premul = deep.AlphaType = SKAlphaType.Premul
            Dim toRoot = DeepToRoot.Value
            Dim toValue = DeepRootToValue.Value
            Dim rootOf = Function(byteUnits As Single) toRoot(Math.Min(65535, Math.Max(0, CInt(byteUnits * 257.0F + 0.5F))))

            Dim plane As ChromaPlane = Nothing
            Dim prepared As ChromaLevel = Nothing
            Try
                plane = New ChromaPlane(w, h)
                Using mask = New ChromaMeasureMask(w, h)
                    Dim filledPlane = plane
                    Parallel.For(0, h,
                        Sub(y)
                            Dim row(w * 4 - 1) As Short
                            ReadDeepRow(deep, sourcePixels, y, row)
                            For x = 0 To w - 1
                                Dim cr As Single, cg As Single, cb As Single, a As Integer
                                ReadDeepUnpremultiplied(row, x * 4, premul, cr, cg, cb, a)
                                Dim i = y * w + x
                                Dim sr = rootOf(cr), sg = rootOf(cg), sb = rootOf(cb)
                                filledPlane.Red(i) = sr - sg
                                filledPlane.Blue(i) = sb - sg
                                ' Dieselben Grenzen wie in der 8-Bit-Stufe: nahe am Ausbrennen wird
                                ' nicht gemessen, und Schwarz ist, was dort als Byte 0 stuende.
                                mask.Unclipped(i) = cr < 249.0F AndAlso cg < 249.0F AndAlso cb < 249.0F
                                mask.Black(i) = If(cr < 0.5F OrElse cg < 0.5F OrElse cb < 0.5F, 1.0F, 0.0F)
                            Next
                        End Sub)
                    prepared = ChromaPrepareLevel(plane, mask, 0, 0, 0, amount)
                End Using

                Dim result = NewDeepBitmap(w, h, deep.AlphaType)
                If result Is Nothing Then Return Nothing
                Dim targetPixels = result.GetPixels()
                Dim finest = plane
                Dim level0 = prepared
                Parallel.For(0, h,
                    Sub(y)
                        Dim filtered(2 * w - 1) As Single
                        If level0 IsNot Nothing Then
                            ChromaCombineRow(finest, level0, y, filtered, 0, filtered, w)
                        Else
                            Array.Copy(finest.Red, y * w, filtered, 0, w)
                            Array.Copy(finest.Blue, y * w, filtered, w, w)
                        End If
                        Dim row(w * 4 - 1) As Short
                        ReadDeepRow(deep, sourcePixels, y, row)
                        For x = 0 To w - 1
                            Dim cr As Single, cg As Single, cb As Single, a As Integer
                            ReadDeepUnpremultiplied(row, x * 4, premul, cr, cg, cb, a)
                            Dim luma = (rootOf(cr) + 2.0F * rootOf(cg) + rootOf(cb)) * 0.25F
                            Dim fr = filtered(x), fb = filtered(w + x)
                            Dim g = luma - (fr + fb) * 0.25F
                            Dim r = g + fr
                            Dim b = g + fb
                            WriteDeepPremultiplied(row, x * 4, premul,
                                                   DeepValueOfRoot(toValue, r) * 255.0F,
                                                   DeepValueOfRoot(toValue, g) * 255.0F,
                                                   DeepValueOfRoot(toValue, b) * 255.0F, a)
                        Next
                        WriteDeepRow(result, targetPixels, y, row)
                    End Sub)
                Return result
            Finally
                prepared?.Dispose()
                plane?.Dispose()
            End Try
        End Function

        ' ── Rauschminderung mit Fuehrungsfilter ──────────────────────────────────
        '
        ' Dieselbe Rechnung wie ApplyGuidedNoiseReduction und ApplyMultiScaleGuidedNoiseReduction,
        ' auf Ebenen in Gleitkomma. Der Kern der 8-Bit-Stufe (GuidedTile) rechnet schon so und
        ' liest und schreibt nur Bytes; hier steht derselbe Kern mit Ebenen als Ein- und Ausgang,
        ' mit denselben Kacheln und Raendern.
        '
        ' GEMESSEN WIRD WIE IN DER VORSCHAU: das Rauschprofil, nach dem sich die Schwelle richtet,
        ' kommt aus einem 8-Bit-Abbild der jeweiligen Stufe (EstimateNoiseProfile), auf der feinsten
        ' mit Dither wie das 8-Bit-Bild der Kette, auf den groeberen gerundet wie deren
        ' Zwischenbilder dort. Ein Profil aus den feinen Werten truege das Rauschen der Rundung
        ' nicht und ergaebe eine andere Schwelle als in der Vorschau.

        ''' <summary>Die Ebenen als vormultiplizierte Bytes (Rot, Gruen, Blau, Deckung), fuer die
        ''' Messungen, die ein 8-Bit-Bild lesen.</summary>
        Private Shared Function PlanesToBytes(planes As DeepPlanes, dithered As Boolean) As Byte()
            Dim w = planes.Width, h = planes.Height
            Dim bytes(w * h * 4 - 1) As Byte
            Parallel.For(0, h,
                Sub(y)
                    Dim ditherRow = (y And 7) << 3
                    For x = 0 To w - 1
                        Dim i = y * w + x
                        Dim o = i * 4
                        Dim cover = If(planes.A Is Nothing, 255.0F, Clamp(planes.A(i), 0.0F, 255.0F))
                        Dim scale = cover / 255.0F / 255.0F
                        Dim dth = If(dithered, DitherMatrix(ditherRow Or (x And 7)), 0.0F)
                        bytes(o) = QuantizeDithered(Clamp(planes.R(i), 0.0F, 255.0F) * scale, dth)
                        bytes(o + 1) = QuantizeDithered(Clamp(planes.G(i), 0.0F, 255.0F) * scale, dth)
                        bytes(o + 2) = QuantizeDithered(Clamp(planes.B(i), 0.0F, 255.0F) * scale, dth)
                        bytes(o + 3) = ClampToByte(cover)
                    Next
                End Sub)
            Return bytes
        End Function

        Private Shared Function GuidedNoiseReductionPlanes(source As DeepPlanes, amount As Single, detail As Single,
                                                           adaptive As Boolean, dithered As Boolean) As DeepPlanes
            Dim w = source.Width, h = source.Height
            Dim epsTable As Single() = Nothing
            If adaptive Then
                Dim profile = EstimateNoiseProfile(PlanesToBytes(source, dithered), w * 4, 0, 1, 2, 3, w, h)
                If profile IsNot Nothing Then epsTable = AdaptiveNoiseEpsilonTable(profile, amount)
            End If
            If epsTable Is Nothing Then
                epsTable = New Single(255) {}
                Array.Fill(epsTable, GuidedNoiseEpsilon(amount))
            End If
            Dim texture = GuidedDetailTexture(amount, detail)

            Dim result = source.NewLike()
            ' Die Deckung bleibt die der Quelle, wie in der 8-Bit-Stufe.
            result.A = source.A
            Dim tilesX = (w + GuidedTileEdge - 1) \ GuidedTileEdge
            Dim tilesY = (h + GuidedTileEdge - 1) \ GuidedTileEdge
            Dim options = New ParallelOptions With {
                .MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, GuidedMaxParallelTiles))
            }
            Parallel.For(0, tilesX * tilesY, options,
                Sub(index)
                    GuidedTilePlanes(source, result, (index Mod tilesX) * GuidedTileEdge, (index \ tilesX) * GuidedTileEdge,
                                     epsTable, texture)
                End Sub)
            Return result
        End Function

        ''' <summary>Der Kern wie <see cref="GuidedTile"/>, mit Ebenen statt Bytes. Wer den einen
        ''' aendert, aendert den anderen mit.</summary>
        Private Shared Sub GuidedTilePlanes(source As DeepPlanes, target As DeepPlanes,
                                            tileLeft As Integer, tileTop As Integer,
                                            epsTable As Single(), texture As Single)
            Dim imageWidth = source.Width, imageHeight = source.Height
            Dim innerRight = Math.Min(tileLeft + GuidedTileEdge, imageWidth)
            Dim innerBottom = Math.Min(tileTop + GuidedTileEdge, imageHeight)
            Dim left = Math.Max(0, tileLeft - GuidedMargin)
            Dim top = Math.Max(0, tileTop - GuidedMargin)
            Dim right = Math.Min(imageWidth, innerRight + GuidedMargin)
            Dim bottom = Math.Min(imageHeight, innerBottom + GuidedMargin)
            Dim w = right - left, h = bottom - top
            If w <= 0 OrElse h <= 0 Then Return

            Dim n = w * h
            Dim pool = Buffers.ArrayPool(Of Single).Shared
            Dim luma = pool.Rent(n)
            Dim chromaR = pool.Rent(n)
            Dim chromaB = pool.Rent(n)
            Dim meanI = pool.Rent(n)
            Dim meanSquares = pool.Rent(n)
            Dim aCoef = pool.Rent(n)
            Dim bCoef = pool.Rent(n)
            Dim meanChromaR = pool.Rent(n)
            Dim meanChromaB = pool.Rent(n)
            Dim scratch = pool.Rent(n)
            Try
                For y = 0 To h - 1
                    Dim row = (top + y) * imageWidth + left
                    Dim at = y * w
                    For x = 0 To w - 1
                        Dim cr = source.R(row + x), cg = source.G(row + x), cb = source.B(row + x)
                        Dim y601 = 0.299F * cr + 0.587F * cg + 0.114F * cb
                        luma(at + x) = y601
                        chromaR(at + x) = cr - y601
                        chromaB(at + x) = cb - y601
                    Next
                Next

                For i = 0 To n - 1
                    meanSquares(i) = luma(i) * luma(i)
                Next
                BoxMean(meanSquares, meanSquares, scratch, w, h, GuidedRadius)
                BoxMean(luma, meanI, scratch, w, h, GuidedRadius)

                For i = 0 To n - 1
                    Dim variance = meanSquares(i) - meanI(i) * meanI(i)
                    If variance < 0.0F Then variance = 0.0F
                    Dim eps = epsTable(Math.Min(255, Math.Max(0, CInt(meanI(i)))))
                    If texture > 0.0F AndAlso eps > 0.0F Then
                        Dim ratio = variance / eps
                        eps /= 1.0F + texture * ratio * ratio
                    End If
                    Dim gain = variance / (variance + eps)
                    aCoef(i) = gain
                    bCoef(i) = (1.0F - gain) * meanI(i)
                Next
                Dim meanA = meanI
                Dim meanB = meanSquares
                BoxMean(aCoef, meanA, scratch, w, h, GuidedRadius)
                BoxMean(bCoef, meanB, scratch, w, h, GuidedRadius)
                BoxMean(chromaR, meanChromaR, scratch, w, h, GuidedRadius)
                BoxMean(chromaB, meanChromaB, scratch, w, h, GuidedRadius)

                For y = tileTop To innerBottom - 1
                    Dim at = (y - top) * w - left
                    Dim row = y * imageWidth
                    For x = tileLeft To innerRight - 1
                        Dim i = at + x
                        Dim before = luma(i)
                        Dim gain = meanA(i)
                        Dim newLuma = gain * before + meanB(i)
                        Dim mix = 1.0F - gain
                        Dim newChromaR = chromaR(i) + (meanChromaR(i) - chromaR(i)) * mix
                        Dim newChromaB = chromaB(i) + (meanChromaB(i) - chromaB(i)) * mix
                        Dim newR = newLuma + newChromaR
                        Dim newB = newLuma + newChromaB
                        Dim newG = (newLuma - 0.299F * newR - 0.114F * newB) / 0.587F
                        ' Geklemmt wie ClampToByte in der 8-Bit-Stufe, nur ohne zu runden.
                        target.R(row + x) = Clamp(newR, 0.0F, 255.0F)
                        target.G(row + x) = Clamp(newG, 0.0F, 255.0F)
                        target.B(row + x) = Clamp(newB, 0.0F, 255.0F)
                    Next
                Next
            Finally
                pool.Return(luma)
                pool.Return(chromaR)
                pool.Return(chromaB)
                pool.Return(meanI)
                pool.Return(meanSquares)
                pool.Return(aCoef)
                pool.Return(bCoef)
                pool.Return(meanChromaR)
                pool.Return(meanChromaB)
                pool.Return(scratch)
            End Try
        End Sub

        ''' <summary>Halbiert die Ebenen wie <see cref="DownsampleHalf"/>: je Zielpunkt der Mittelwert
        ''' der bis zu vier Quellpunkte, Farbe und Deckung getrennt.</summary>
        Private Shared Function DownsampleHalfPlanes(source As DeepPlanes) As DeepPlanes
            Dim w = source.Width, h = source.Height
            Dim hw = (w + 1) \ 2, hh = (h + 1) \ 2
            Dim result = New DeepPlanes With {.Width = hw, .Height = hh, .AlphaType = source.AlphaType,
                                              .R = New Single(hw * hh - 1) {}, .G = New Single(hw * hh - 1) {},
                                              .B = New Single(hw * hh - 1) {},
                                              .A = If(source.A Is Nothing, Nothing, New Single(hw * hh - 1) {})}
            Parallel.For(0, hh,
                Sub(y)
                    For x = 0 To hw - 1
                        Dim sr = 0.0F, sg = 0.0F, sb = 0.0F, sa = 0.0F
                        Dim n = 0
                        For dy = 0 To 1
                            Dim yy = 2 * y + dy
                            If yy >= h Then Continue For
                            For dx = 0 To 1
                                Dim xx = 2 * x + dx
                                If xx >= w Then Continue For
                                Dim i = yy * w + xx
                                sr += source.R(i) : sg += source.G(i) : sb += source.B(i)
                                If source.A IsNot Nothing Then sa += source.A(i)
                                n += 1
                            Next
                        Next
                        Dim o = y * hw + x
                        result.R(o) = sr / n : result.G(o) = sg / n : result.B(o) = sb / n
                        If result.A IsNot Nothing Then result.A(o) = sa / n
                    Next
                End Sub)
            Return result
        End Function

        ''' <summary>Wie <see cref="AddCoarseCorrection"/>: was die groebere Stufe geaendert hat
        ''' (entrauscht minus Original), bilinear auf die feinere gebracht und dazugezaehlt.</summary>
        Private Shared Function AddCoarseCorrectionPlanes(fine As DeepPlanes, coarseDenoised As DeepPlanes,
                                                          coarseOriginal As DeepPlanes) As DeepPlanes
            Dim cw = coarseOriginal.Width, ch = coarseOriginal.Height
            Dim w = fine.Width, h = fine.Height
            Dim result = fine.NewLike()
            result.A = fine.A
            Dim blend = Sub(target As Single(), source As Single(), denoised As Single(), original As Single())
                            Parallel.For(0, h,
                                Sub(y)
                                    Dim v = (y + 0.5F) / 2.0F - 0.5F
                                    Dim y0 = CInt(Math.Floor(v))
                                    Dim ty = v - y0
                                    Dim yA = Math.Min(ch - 1, Math.Max(0, y0))
                                    Dim yB = Math.Min(ch - 1, Math.Max(0, y0 + 1))
                                    For x = 0 To w - 1
                                        Dim u = (x + 0.5F) / 2.0F - 0.5F
                                        Dim x0 = CInt(Math.Floor(u))
                                        Dim tx = u - x0
                                        Dim xA = Math.Min(cw - 1, Math.Max(0, x0))
                                        Dim xB = Math.Min(cw - 1, Math.Max(0, x0 + 1))
                                        Dim i00 = yA * cw + xA, i01 = yA * cw + xB, i10 = yB * cw + xA, i11 = yB * cw + xB
                                        Dim add = (denoised(i00) - original(i00)) * (1.0F - tx) * (1.0F - ty) +
                                                  (denoised(i01) - original(i01)) * tx * (1.0F - ty) +
                                                  (denoised(i10) - original(i10)) * (1.0F - tx) * ty +
                                                  (denoised(i11) - original(i11)) * tx * ty
                                        target(y * w + x) = Clamp(source(y * w + x) + add, 0.0F, 255.0F)
                                    Next
                                End Sub)
                        End Sub
            blend(result.R, fine.R, coarseDenoised.R, coarseOriginal.R)
            blend(result.G, fine.G, coarseDenoised.G, coarseOriginal.G)
            blend(result.B, fine.B, coarseDenoised.B, coarseOriginal.B)
            Return result
        End Function

        ''' <summary>Der Fuehrungsfilter mit fester Schwelle auf dem tiefen Bild, wie
        ''' <see cref="ApplyGuidedNoiseReduction"/> ohne Anpassung.</summary>
        Private Shared Function ApplyGuidedNoiseReductionDeep(deep As SKBitmap, amount As Single, detail As Single) As SKBitmap
            Dim source = DeepPlanes.Load(deep)
            If source Is Nothing Then Return Nothing
            Return GuidedNoiseReductionPlanes(source, amount, detail, adaptive:=False, dithered:=True).ToBitmap()
        End Function

        ''' <summary>Der angepasste Fuehrungsfilter ueber mehrere Stufen auf dem tiefen Bild, wie
        ''' <see cref="ApplyMultiScaleGuidedNoiseReduction"/>: dieselbe Pyramide, dieselben Gewichte.</summary>
        Private Shared Function ApplyMultiScaleGuidedNoiseReductionDeep(deep As SKBitmap, amount As Single, detail As Single) As SKBitmap
            Dim first = DeepPlanes.Load(deep)
            If first Is Nothing Then Return Nothing
            Dim pyramid = New List(Of DeepPlanes) From {first}
            For level = 1 To MultiScaleGuidedLevels
                Dim previous = pyramid(level - 1)
                If Math.Min(previous.Width, previous.Height) < 2 * MultiScaleGuidedMinEdge Then Exit For
                pyramid.Add(DownsampleHalfPlanes(previous))
            Next
            Dim denoised As DeepPlanes = Nothing
            For level = pyramid.Count - 1 To 0 Step -1
                Dim input = pyramid(level)
                If denoised IsNot Nothing Then input = AddCoarseCorrectionPlanes(input, denoised, pyramid(level + 1))
                denoised = GuidedNoiseReductionPlanes(input, If(level = 0, amount, amount * MultiScaleCoarseWeight),
                                                      detail, adaptive:=True, dithered:=level = 0)
            Next
            Return denoised.ToBitmap()
        End Function

        ''' <summary>Die einfache Rauschminderung auf dem tiefen Bild, dieselbe Rechnung wie
        ''' <see cref="ApplyNoiseReduction"/>: weichgezeichnet, und mit Detail das Original dort
        ''' anteilig zurueckgemischt, wo beide stark abweichen (eine Kante).</summary>
        Private Shared Function ApplyNoiseReductionDeep(deep As SKBitmap, amount As Single, detail As Single) As SKBitmap
            Dim sigma = 0.25F + Clamp(amount, 0, 1) * 2.2F
            Dim source = DeepPlanes.Load(deep)
            If source Is Nothing Then Return Nothing
            Dim blurred = source.BlurredLikeSkia(sigma)
            Dim d = Clamp(detail, 0, 1)
            If d <= 0 Then Return blurred.ToBitmap()

            Dim w = source.Width
            Parallel.For(0, source.Height,
                Sub(y)
                    Dim at = y * w
                    For x = 0 To w - 1
                        Dim i = at + x
                        Dim cr = source.R(i), cg = source.G(i), cb = source.B(i)
                        Dim lr = blurred.R(i), lg = blurred.G(i), lb = blurred.B(i)
                        Dim diff = Math.Abs(cr - lr) + Math.Abs(cg - lg) + Math.Abs(cb - lb)
                        Dim mask = Clamp(diff / 48.0F, 0, 1) * d
                        blurred.R(i) = lr + (cr - lr) * mask
                        blurred.G(i) = lg + (cg - lg) * mask
                        blurred.B(i) = lb + (cb - lb) * mask
                    Next
                End Sub)
            ' Die Deckung bleibt die der Quelle, wie in der 8-Bit-Stufe.
            blurred.A = source.A
            Return blurred.ToBitmap()
        End Function

        ''' <summary>Der Weichzeichner des Panels auf dem tiefen Bild, wie <see cref="ApplyImageBlur"/>.
        ''' Nothing, wenn das Sigma unter einem halben Punkt liegt: dann tut auch die 8-Bit-Stufe
        ''' nichts und gibt ihre Quelle zurueck, samt deren tiefem Bild.</summary>
        Private Shared Function ApplyImageBlurDeep(deep As SKBitmap, amount As Single) As SKBitmap
            Dim a = Clamp(amount, 0, 1)
            If a <= 0 Then Return Nothing
            Dim sigma = Math.Min(deep.Width, deep.Height) * ImageBlurSigmaShare * a * a
            If sigma < 0.5F Then Return Nothing
            Return DeepPlanes.Load(deep)?.BlurredLikeSkia(sigma).ToBitmap()
        End Function

        ''' <summary>Der Filter "weich" auf dem tiefen Bild, wie <see cref="ApplySoftFocusBlur"/>: eine
        ''' leicht weichgezeichnete Kopie mit der Staerke als Deckung ueber das Original gelegt,
        ''' gerechnet auf den vormultiplizierten Werten wie beim Zeichnen.</summary>
        Private Shared Function ApplySoftFocusDeep(deep As SKBitmap, strength As Single) As SKBitmap
            strength = Clamp(strength, 0, 1)
            If strength <= 0 Then Return Nothing
            Dim source = DeepPlanes.Load(deep)
            If source Is Nothing Then Return Nothing
            Dim blurred = source.BlurredLikeSkia(0.25F + 0.3F * 2.2F)
            ' Die Deckung der Kopie ist ein Byte, wie beim Zeichnen mit einer Farbe.
            Dim cover = ClampToByte(255 * strength) / 255.0F
            Parallel.For(0, source.Height,
                Sub(y)
                    Dim at = y * source.Width
                    For i = at To at + source.Width - 1
                        If source.A Is Nothing Then
                            source.R(i) += (blurred.R(i) - source.R(i)) * cover
                            source.G(i) += (blurred.G(i) - source.G(i)) * cover
                            source.B(i) += (blurred.B(i) - source.B(i)) * cover
                        Else
                            Dim sa = source.A(i) * (1.0F - cover), ba = blurred.A(i) * cover
                            Dim total = sa + ba
                            If total > 0.0F Then
                                source.R(i) = (source.R(i) * sa + blurred.R(i) * ba) / total
                                source.G(i) = (source.G(i) * sa + blurred.G(i) * ba) / total
                                source.B(i) = (source.B(i) * sa + blurred.B(i) * ba) / total
                            End If
                            source.A(i) = total
                        End If
                    Next
                End Sub)
            Return source.ToBitmap()
        End Function

        ''' <summary>Die Klarheit auf dem tiefen Bild, dieselbe Rechnung wie <see cref="ApplyClarity"/>:
        ''' die Helligkeit auf ein grobes Raster gemittelt, daraus die glatte Basis ueber
        ''' <see cref="ClarityCoefficients"/>, und der Abstand zur Basis verstaerkt, in den
        ''' Mitteltoenen voll und zu Tiefen und Lichtern auslaufend.</summary>
        Private Shared Function ApplyClarityDeep(deep As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return Nothing
            Dim gain = If(strength > 0, strength * ClarityPositiveGain, strength)
            Dim source = DeepPlanes.Load(deep)
            If source Is Nothing Then Return Nothing
            Dim width = source.Width, height = source.Height

            Dim radius = Math.Max(1.0, Math.Min(width, height) * ClarityRadiusShare)
            Dim factor = Math.Max(1, CInt(Math.Floor(radius / 4.0)))
            Dim smallWidth = (width + factor - 1) \ factor
            Dim smallHeight = (height + factor - 1) \ factor
            Dim small(smallWidth * smallHeight - 1) As Single
            Parallel.For(0, smallHeight,
                Sub(sy)
                    Dim yEnd = Math.Min(height, (sy + 1) * factor)
                    For sx = 0 To smallWidth - 1
                        Dim xEnd = Math.Min(width, (sx + 1) * factor)
                        Dim sum = 0.0
                        Dim count = 0
                        For y = sy * factor To yEnd - 1
                            Dim row = y * width
                            For x = sx * factor To xEnd - 1
                                sum += 0.299 * source.R(row + x) + 0.587 * source.G(row + x) + 0.114 * source.B(row + x)
                                count += 1
                            Next
                        Next
                        small(sy * smallWidth + sx) = CSng(sum / (count * 255.0))
                    Next
                End Sub)

            Dim coefA As Single() = Nothing, coefB As Single() = Nothing
            ClarityCoefficients(small, smallWidth, smallHeight, Math.Max(1, CInt(Math.Round(radius / factor))), coefA, coefB)

            Parallel.For(0, height,
                Sub(y)
                    Dim fy = Math.Max(0.0, Math.Min(smallHeight - 1, (y + 0.5) / factor - 0.5))
                    Dim y0 = CInt(Math.Floor(fy))
                    Dim y1 = Math.Min(smallHeight - 1, y0 + 1)
                    Dim ty = CSng(fy - y0)
                    Dim row = y * width
                    For x = 0 To width - 1
                        Dim fx = Math.Max(0.0, Math.Min(smallWidth - 1, (x + 0.5) / factor - 0.5))
                        Dim x0 = CInt(Math.Floor(fx))
                        Dim x1 = Math.Min(smallWidth - 1, x0 + 1)
                        Dim tx = CSng(fx - x0)
                        Dim i00 = y0 * smallWidth + x0, i01 = y0 * smallWidth + x1
                        Dim i10 = y1 * smallWidth + x0, i11 = y1 * smallWidth + x1
                        Dim baseA = (coefA(i00) * (1 - tx) + coefA(i01) * tx) * (1 - ty) +
                                    (coefA(i10) * (1 - tx) + coefA(i11) * tx) * ty
                        Dim baseB = (coefB(i00) * (1 - tx) + coefB(i01) * tx) * (1 - ty) +
                                    (coefB(i10) * (1 - tx) + coefB(i11) * tx) * ty

                        Dim i = row + x
                        Dim luma = CSng((0.299 * source.R(i) + 0.587 * source.G(i) + 0.114 * source.B(i)) / 255.0)
                        Dim baseLuma = baseA * luma + baseB
                        Dim t = 2.0F * baseLuma - 1.0F
                        Dim weight = Math.Max(0.0F, 1.0F - t * t)
                        Dim delta = (luma - baseLuma) * gain * weight * 255.0F
                        source.R(i) += delta
                        source.G(i) += delta
                        source.B(i) += delta
                    Next
                End Sub)
            Return source.ToBitmap()
        End Function

        ''' <summary>Die Struktur auf dem tiefen Bild, wie <see cref="ApplyStructure"/> ueber
        ''' <see cref="ApplyLocalContrast"/>: der Abstand zur weichgezeichneten Kopie, verstaerkt
        ''' und dazugezaehlt. Dieselben Kennzahlen (Sigma aus 3,6 / 8, Faktor 2,4).</summary>
        Private Shared Function ApplyStructureDeep(deep As SKBitmap, amount As Single) As SKBitmap
            Dim factor = Clamp(amount, -1, 1) * 2.4F
            Dim source = DeepPlanes.Load(deep)
            If source Is Nothing Then Return Nothing
            Dim blurred = source.BlurredLikeSkia(0.25F + (3.6F / 8.0F) * 2.2F)
            Parallel.For(0, source.Height,
                Sub(y)
                    Dim at = y * source.Width
                    For i = at To at + source.Width - 1
                        source.R(i) += (source.R(i) - blurred.R(i)) * factor
                        source.G(i) += (source.G(i) - blurred.G(i)) * factor
                        source.B(i) += (source.B(i) - blurred.B(i)) * factor
                    Next
                End Sub)
            Return source.ToBitmap()
        End Function

        ''' <summary>Das Gluehen auf dem tiefen Bild, wie <see cref="ApplyImageGlow"/>: die
        ''' weichgezeichnete Kopie anteilig dazu (positiv) oder davon ab (negativ).</summary>
        Private Shared Function ApplyImageGlowDeep(deep As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return Nothing
            Dim source = DeepPlanes.Load(deep)
            If source Is Nothing Then Return Nothing
            Dim blurred = source.BlurredLikeSkia(0.25F + 0.8F * 2.2F)
            ' Die 8-Bit-Stufe unterscheidet die Vorzeichen; beide Zweige ergeben diesen einen Faktor.
            Dim sv = strength * 0.55F
            Parallel.For(0, source.Height,
                Sub(y)
                    Dim at = y * source.Width
                    For i = at To at + source.Width - 1
                        source.R(i) += blurred.R(i) * sv
                        source.G(i) += blurred.G(i) * sv
                        source.B(i) += blurred.B(i) * sv
                    Next
                End Sub)
            Return source.ToBitmap()
        End Function

        ''' <summary>Das grobe Farbrauschen auf dem tiefen Bild, wie <see cref="ApplyMultiScaleDenoise"/>:
        ''' die beiden Farbabstaende zur Helligkeit ueber mehrere Stufen geschrumpft
        ''' (<see cref="ShrinkSteps"/>, dort schon in Gleitkomma), die Helligkeit bleibt stehen.</summary>
        Private Shared Function ApplyMultiScaleDenoiseDeep(deep As SKBitmap, strength As Single, coarse As Single) As SKBitmap
            If strength <= 0.001F Then Return Nothing
            Dim source = DeepPlanes.Load(deep)
            If source Is Nothing Then Return Nothing
            Dim w = source.Width, h = source.Height
            If w < 8 OrElse h < 8 Then Return Nothing
            Dim steps = Math.Max(3, Math.Min(7, CInt(Math.Round(3.0 + coarse * 4.0))))
            Dim n = w * h
            Dim luma(n - 1) As Single, chromaB(n - 1) As Single, chromaR(n - 1) As Single
            Parallel.For(0, h,
                Sub(y)
                    For i = y * w To y * w + w - 1
                        luma(i) = 0.299F * source.R(i) + 0.587F * source.G(i) + 0.114F * source.B(i)
                        chromaB(i) = source.B(i) - luma(i)
                        chromaR(i) = source.R(i) - luma(i)
                    Next
                End Sub)
            Dim thresholdC = 1.0F + 19.0F * CSng(Math.Pow(strength, 1.5))
            ShrinkSteps(chromaB, w, h, steps, thresholdC, 1.0F)
            ShrinkSteps(chromaR, w, h, steps, thresholdC, 1.0F)
            Parallel.For(0, h,
                Sub(y)
                    For i = y * w To y * w + w - 1
                        Dim r = luma(i) + chromaR(i)
                        Dim b = luma(i) + chromaB(i)
                        source.R(i) = r
                        source.B(i) = b
                        source.G(i) = (luma(i) - 0.299F * r - 0.114F * b) / 0.587F
                    Next
                End Sub)
            Return source.ToBitmap()
        End Function

        ''' <summary>Die Rauschminderung mit Median auf dem tiefen Bild, wie <see cref="ApplyMedianBlur"/>:
        ''' je Kanal der mittlere Wert des Fensters, auf den Werten, wie sie im Bild stehen, die
        ''' Deckung bleibt. Bei 16 Bit taugt das mitlaufende Histogramm der 8-Bit-Stufe nicht (65536
        ''' Faecher je Kanal); hier wird das Fenster je Bildpunkt eingesammelt und sortiert, bei
        ''' hoechstens 49 Werten.</summary>
        Private Shared Function ApplyMedianBlurDeep(deep As SKBitmap, amount As Single) As SKBitmap
            Dim radius = Math.Max(1, CInt(Math.Round(1 + Clamp(amount, 0, 1) * 2)))
            Dim w = deep.Width, h = deep.Height
            Dim sourcePixels = deep.GetPixels()
            If sourcePixels = IntPtr.Zero OrElse w <= 0 OrElse h <= 0 Then Return Nothing
            Dim values(w * h * 4 - 1) As Short
            Dim stride = deep.RowBytes
            Parallel.For(0, h, Sub(y) Marshal.Copy(IntPtr.Add(sourcePixels, y * stride), values, y * w * 4, w * 4))
            Dim result = NewDeepBitmap(w, h, deep.AlphaType)
            If result Is Nothing Then Return Nothing
            Dim targetPixels = result.GetPixels()
            Dim side = 2 * radius + 1
            Parallel.For(0, h,
                Sub(y)
                    Dim row(w * 4 - 1) As Short
                    Dim window(side * side - 1) As Integer
                    Dim yFrom = Math.Max(0, y - radius), yTo = Math.Min(h - 1, y + radius)
                    For x = 0 To w - 1
                        Dim xFrom = Math.Max(0, x - radius), xTo = Math.Min(w - 1, x + radius)
                        Dim count = (yTo - yFrom + 1) * (xTo - xFrom + 1)
                        For channel = 0 To 2
                            Dim n = 0
                            For yy = yFrom To yTo
                                Dim at = (yy * w + xFrom) * 4 + channel
                                For xx = xFrom To xTo
                                    window(n) = values(at) And &HFFFF
                                    n += 1
                                    at += 4
                                Next
                            Next
                            Array.Sort(window, 0, count)
                            row(x * 4 + channel) = ToDeepShort(window(count \ 2))
                        Next
                        row(x * 4 + 3) = values((y * w + x) * 4 + 3)
                    Next
                    WriteDeepRow(result, targetPixels, y, row)
                End Sub)
            Return result
        End Function

        ''' <summary>Dunst auf dem tiefen Bild in beiden Richtungen, wie <see cref="ApplyHaze"/>.
        '''
        ''' Die Karte (wo Dunst liegt, wie hell das Dunstlicht ist) entsteht wie in der Vorschau
        ''' aus den Zellmitteln eines 8-Bit-Abbilds: sie ist eine Messung am Bild, und ueber ein
        ''' ganzes Feld gemittelt aendert die Tiefe daran nichts. Tief gerechnet wird, was danach
        ''' je Bildpunkt geschieht.</summary>
        Private Shared Function ApplyHazeDeep(deep As SKBitmap, amount As Single) As SKBitmap
            Dim signed = Clamp(amount, -1, 1)
            If Math.Abs(signed) <= 0.001F Then Return Nothing
            Dim strength = Math.Abs(CDbl(signed))
            Dim source = DeepPlanes.Load(deep)
            If source Is Nothing Then Return Nothing
            Dim width = source.Width, height = source.Height

            Dim lw, lh As Integer
            Dim lowR As Double() = Nothing, lowG As Double() = Nothing, lowB As Double() = Nothing
            Dim lowOwn As Double() = Nothing
            HazeCellMeans(PlanesToBytes(source, dithered:=True), width * 4, 0, 1, 2, 3, width, height, lw, lh, lowR, lowG, lowB, lowOwn)

            If signed > 0 Then
                ' Dunst hinzufuegen, wie ApplyAddedHaze.
                Dim veil As Double() = Nothing
                Dim lightFactor As Double
                Dim map = AddedHazeMap(lowR, lowG, lowB, lowOwn, lw, lh, veil, lightFactor)
                Dim amountPow = Math.Pow(strength, AddedHazeExponent)
                Dim back = Function(u As Double) CSng(Math.Pow(Math.Max(0.0, Math.Min(1.0, u)), 1.0 / AddedHazePower) * 255.0)
                Parallel.For(0, height,
                    Sub(y)
                        Dim fy = Math.Max(0.0, Math.Min(lh - 1.0, (y + 0.5) * lh / height - 0.5))
                        Dim y0 = CInt(Math.Floor(fy)), y1 = Math.Min(lh - 1, y0 + 1)
                        Dim wy = fy - y0
                        For x = 0 To width - 1
                            Dim fx = Math.Max(0.0, Math.Min(lw - 1.0, (x + 0.5) * lw / width - 0.5))
                            Dim x0 = CInt(Math.Floor(fx)), x1 = Math.Min(lw - 1, x0 + 1)
                            Dim wx = fx - x0
                            Dim haze = (map(y0 * lw + x0) * (1 - wx) + map(y0 * lw + x1) * wx) * (1 - wy) +
                                       (map(y1 * lw + x0) * (1 - wx) + map(y1 * lw + x1) * wx) * wy
                            haze = Math.Max(0.0, haze) * lightFactor
                            Dim added = Math.Min(1.0 - Math.Exp(-amountPow * (AddedHazeFloor + AddedHazeScale * haze)), AddedHazeCeiling)
                            Dim keep = 1.0 - AddedHazeGreying * added * strength
                            Dim i = y * width + x
                            Dim ur = Math.Pow(Clamp(source.R(i), 0.0F, 255.0F) / 255.0, AddedHazePower) * (1 - added) + veil(0) * added
                            Dim ug = Math.Pow(Clamp(source.G(i), 0.0F, 255.0F) / 255.0, AddedHazePower) * (1 - added) + veil(1) * added
                            Dim ub = Math.Pow(Clamp(source.B(i), 0.0F, 255.0F) / 255.0, AddedHazePower) * (1 - added) + veil(2) * added
                            Dim mean = 0.25 * ur + 0.5 * ug + 0.25 * ub
                            source.R(i) = back(mean + (ur - mean) * keep)
                            source.G(i) = back(mean + (ug - mean) * keep)
                            source.B(i) = back(mean + (ub - mean) * keep)
                        Next
                    End Sub)
                Return source.ToBitmap()
            End If

            ' Dunst entfernen, wie ApplyDehaze.
            Dim atmR, atmG, atmB As Double
            Dim transmission = DehazeTransmission(lowR, lowG, lowB, lw, lh, strength, atmR, atmG, atmB)
            Dim floor = 1.0 - (1.0 - DehazeDarkeningFloor) * strength
            Dim blackPoint = DehazeBlackPoint * strength
            Parallel.For(0, height,
                Sub(y)
                    Dim fy = Math.Max(0.0, Math.Min(lh - 1.0, (y + 0.5) * lh / height - 0.5))
                    Dim y0 = CInt(Math.Floor(fy)), y1 = Math.Min(lh - 1, y0 + 1)
                    Dim wy = fy - y0
                    For x = 0 To width - 1
                        Dim fx = Math.Max(0.0, Math.Min(lw - 1.0, (x + 0.5) * lw / width - 0.5))
                        Dim x0 = CInt(Math.Floor(fx)), x1 = Math.Min(lw - 1, x0 + 1)
                        Dim wx = fx - x0
                        Dim t = (transmission(y0 * lw + x0) * (1 - wx) + transmission(y0 * lw + x1) * wx) * (1 - wy) +
                                (transmission(y1 * lw + x0) * (1 - wx) + transmission(y1 * lw + x1) * wx) * wy
                        t = Math.Max(DehazeMinTransmission, Math.Min(1.0, t))
                        Dim i = y * width + x
                        Dim cr = source.R(i), cg = source.G(i), cb = source.B(i)
                        Dim luminance = (0.2126 * cr + 0.7152 * cg + 0.0722 * cb) / 255.0
                        Dim u = Math.Min(1.0, luminance / DehazeShadowProtection)
                        Dim weight = u * u * (3.0 - 2.0 * u)
                        source.R(i) = DehazeChannelDeep(cr, atmR, t, weight, floor, blackPoint)
                        source.G(i) = DehazeChannelDeep(cg, atmG, t, weight, floor, blackPoint)
                        source.B(i) = DehazeChannelDeep(cb, atmB, t, weight, floor, blackPoint)
                    Next
                End Sub)
            Return source.ToBitmap()
        End Function

        ''' <summary>Dieselbe Rechnung wie <see cref="DehazeChannel"/>, ohne die Rundung auf das Byte.</summary>
        Private Shared Function DehazeChannelDeep(value As Single, atmosphere As Double, t As Double, weight As Double,
                                                  floor As Double, blackPoint As Double) As Single
            Dim c = value / 255.0
            Dim j = (c - atmosphere) / t + atmosphere
            If j < DehazeToe Then j = DehazeToe * Math.Exp((j - DehazeToe) / DehazeToe)
            j = c + (j - c) * weight
            j = Math.Max(j, c * floor * (1.0 - weight))
            If blackPoint > 0 Then
                Dim k = (j - blackPoint) / (1.0 - blackPoint)
                j = If(k < DehazeToe, DehazeToe * Math.Exp((k - DehazeToe) / DehazeToe) * Math.Min(1.0, j / DehazeToe), k)
            End If
            Return CSng(Math.Max(0.0, Math.Min(1.0, j)) * 255.0)
        End Function

        ''' <summary>Tilt-Shift auf dem tiefen Bild, wie <see cref="ApplyTiltShift"/>: ein scharfes Band,
        ''' quer dazu der Uebergang vom Original ueber eine mittlere zur vollen Unschaerfe. Nothing,
        ''' wenn das Sigma unter einem halben Punkt liegt (dann tut auch die 8-Bit-Stufe nichts).</summary>
        Private Shared Function ApplyTiltShiftDeep(deep As SKBitmap, amount As Single, centerXPercent As Single, centerYPercent As Single,
                                                   angleDegrees As Single, focus As Single, transition As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return Nothing
            Dim w = deep.Width, h = deep.Height
            Dim sigma = Math.Min(w, h) * TiltShiftSigmaShare * strength
            If sigma < 0.5F Then Return Nothing

            Dim theta = Clamp(angleDegrees, -90, 90) * CSng(Math.PI) / 180.0F
            Dim nx = CSng(Math.Sin(theta)), ny = CSng(Math.Cos(theta))
            Dim extent = Math.Max(1.0F, Math.Abs(w * nx) + Math.Abs(h * ny))
            Dim cx = w * Clamp(centerXPercent, 0, 100) / 100.0F
            Dim cy = h * Clamp(centerYPercent, 0, 100) / 100.0F
            Dim half = Clamp(focus, 0, 100) / 100.0F * 0.5F
            Dim ramp = Math.Max(0.01F, Clamp(transition, 0, 100) / 100.0F * 0.5F)

            Dim source = DeepPlanes.Load(deep)
            If source Is Nothing Then Return Nothing
            Dim full = source.BlurredLikeSkia(sigma)
            Dim midSigma = sigma * TiltShiftMidShare
            Dim low = If(midSigma >= 0.5F, source.BlurredLikeSkia(midSigma), source)
            Dim result = source.NewLike()
            Dim blend = Sub(target As Single(), original As Single(), middle As Single(), blurred As Single())
                            If target Is Nothing Then Return
                            Parallel.For(0, h,
                                Sub(y)
                                    Dim dy = (y + 0.5F - cy) * ny
                                    For x = 0 To w - 1
                                        Dim i = y * w + x
                                        Dim u = Math.Abs((x + 0.5F - cx) * nx + dy) / extent
                                        Dim t = Clamp((u - half) / ramp, 0, 1)
                                        If t <= 0 Then
                                            target(i) = original(i)
                                            Continue For
                                        End If
                                        t = t * t * (3.0F - 2.0F * t)
                                        If t < 0.5F Then
                                            target(i) = original(i) + (middle(i) - original(i)) * (t * 2.0F)
                                        Else
                                            target(i) = middle(i) + (blurred(i) - middle(i)) * ((t - 0.5F) * 2.0F)
                                        End If
                                    Next
                                End Sub)
                        End Sub
            blend(result.R, source.R, low.R, full.R)
            blend(result.G, source.G, low.G, full.G)
            blend(result.B, source.B, low.B, full.B)
            blend(result.A, source.A, low.A, full.A)
            Return result.ToBitmap()
        End Function

        ''' <summary>Staub und Kratzer auf dem tiefen Bild. Die hinzufuegende Seite zeichnet ueber
        ''' Skia und rechnet deshalb mit jedem Farbtyp; sie laeuft hier unveraendert mit dem tiefen
        ''' Bild als Quelle. Die entfernende Seite (Median, nur noch in alten Rezepten) hat keine
        ''' tiefe Fassung.</summary>
        Private Shared Function ApplyDustScratchesDeep(deep As SKBitmap, amount As Single) As SKBitmap
            If Clamp(amount, -1, 1) <= 0.001F Then Return Nothing
            Dim result = ApplyDustScratches(deep, amount)
            If result Is Nothing OrElse ReferenceEquals(result, deep) Then Return Nothing
            If result.ColorType <> DeepColorType Then
                result.Dispose()
                Return Nothing
            End If
            Return result
        End Function

        ''' <summary>Geht ein tiefes Bild zeilenweise durch und schreibt das Ergebnis in ein neues:
        ''' <paramref name="body"/> bekommt die Zeilennummer und die Zeile (vier 16-Bit-Werte je
        ''' Bildpunkt) und aendert sie an Ort und Stelle. Nebeneinander, wenn die Stufe das vertraegt;
        ''' eine Stufe, die ihren Zufall aus einem Strom zieht, braucht die Reihenfolge.</summary>
        Private Shared Function MapDeepRows(deep As SKBitmap, parallel As Boolean, body As Action(Of Integer, Short())) As SKBitmap
            Dim w = deep.Width, h = deep.Height
            Dim sourcePixels = deep.GetPixels()
            If sourcePixels = IntPtr.Zero OrElse w <= 0 OrElse h <= 0 Then Return Nothing
            Dim result = NewDeepBitmap(w, h, deep.AlphaType)
            If result Is Nothing Then Return Nothing
            Dim targetPixels = result.GetPixels()
            Dim work = Sub(y As Integer)
                           Dim row(w * 4 - 1) As Short
                           ReadDeepRow(deep, sourcePixels, y, row)
                           body(y, row)
                           WriteDeepRow(result, targetPixels, y, row)
                       End Sub
            If parallel Then
                Threading.Tasks.Parallel.For(0, h, work)
            Else
                For y = 0 To h - 1
                    work(y)
                Next
            End If
            Return result
        End Function

        ''' <summary>Das Korn auf dem tiefen Bild, dieselbe Rechnung wie <see cref="ApplyGrain"/>:
        ''' derselbe Streuwert je Zelle aus dem Ort (<see cref="GrainNoiseAt"/>), dieselbe Rauheit
        ''' und dieselbe Farbigkeit. Ohne Groesse, Rauheit und Farbe ist die Zelle ein Bildpunkt,
        ''' und es kommt das feine Korn heraus.</summary>
        Private Shared Function ApplyGrainDeep(deep As SKBitmap, amount As Single, sizeAmount As Single,
                                               freqAmount As Single, colorAmount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return Nothing
            Dim w = deep.Width, h = deep.Height
            Dim premul = deep.AlphaType = SKAlphaType.Premul
            Dim cell = 1 + CInt(Math.Round(Clamp(sizeAmount, 0, 1) * 5))
            Dim freq = Clamp(freqAmount, 0, 1)
            Dim colorMix = CDbl(Clamp(colorAmount, 0, 1))
            Dim colorNorm = If(colorMix <= 0, 1.0,
                               1.0 / Math.Sqrt((1.0 - colorMix) * (1.0 - colorMix) + colorMix * colorMix))
            Dim amplitude = 8.0 + strength * 34.0
            Dim seed = w * 397 Xor h * 151
            Dim gridW = (w + cell - 1) \ cell
            Const ModCell As Integer = 16
            Dim modW = (w + ModCell - 1) \ ModCell

            Return MapDeepRows(deep, True,
                Sub(y, row)
                    Dim gy = y \ cell
                    Dim my = y \ ModCell
                    For x = 0 To w - 1
                        Dim o = x * 4
                        Dim cr As Single, cg As Single, cb As Single, a As Integer
                        ReadDeepUnpremultiplied(row, o, premul, cr, cg, cb, a)
                        Dim cellIndex = gy * gridW + (x \ cell)
                        Dim n = GrainNoiseAt(cellIndex, seed)
                        Dim amp = amplitude
                        If freq > 0 Then
                            Dim m = GrainNoiseAt(my * modW + (x \ ModCell), seed Xor &H5BF03635)
                            amp = amplitude * (1.0 + freq * m * 0.9)
                        End If
                        If colorMix <= 0 Then
                            Dim noise = CSng(n * amp)
                            WriteDeepPremultiplied(row, o, premul, cr + noise, cg + noise, cb + noise, a)
                        Else
                            Dim mono = (1.0 - colorMix) * n
                            Dim scale = amp * colorNorm
                            WriteDeepPremultiplied(row, o, premul,
                                                   cr + CSng((mono + colorMix * CellChannelNoise(cellIndex, 1)) * scale),
                                                   cg + CSng((mono + colorMix * CellChannelNoise(cellIndex, 2)) * scale),
                                                   cb + CSng((mono + colorMix * CellChannelNoise(cellIndex, 3)) * scale), a)
                        End If
                    Next
                End Sub)
        End Function

        ''' <summary>Rauschen hinzufuegen auf dem tiefen Bild, wie <see cref="ApplyAddNoise"/>: je Kanal
        ''' ein eigener Zufallswert aus demselben Strom in derselben Reihenfolge, deshalb Zeile fuer
        ''' Zeile nacheinander.</summary>
        Private Shared Function ApplyAddNoiseDeep(deep As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return Nothing
            Dim w = deep.Width
            Dim premul = deep.AlphaType = SKAlphaType.Premul
            Dim random = New Random(deep.Width * 541 Xor deep.Height * 877)
            Dim amplitude = strength * 72.0
            Return MapDeepRows(deep, False,
                Sub(y, row)
                    For x = 0 To w - 1
                        Dim cr As Single, cg As Single, cb As Single, a As Integer
                        ReadDeepUnpremultiplied(row, x * 4, premul, cr, cg, cb, a)
                        Dim noiseR = (random.NextDouble() * 2.0 - 1.0) * amplitude
                        Dim noiseG = (random.NextDouble() * 2.0 - 1.0) * amplitude
                        Dim noiseB = (random.NextDouble() * 2.0 - 1.0) * amplitude
                        WriteDeepPremultiplied(row, x * 4, premul, cr + CSng(noiseR), cg + CSng(noiseG), cb + CSng(noiseB), a)
                    Next
                End Sub)
        End Function

        ''' <summary>Die Farbflecken auf dem tiefen Bild, wie <see cref="ApplyColorNoiseAdd"/>: Rot
        ''' und Blau wandern nach dem Zufall, Gruen gleicht die Helligkeit aus. Derselbe Strom in
        ''' derselben Reihenfolge.</summary>
        Private Shared Function ApplyColorNoiseAddDeep(deep As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return Nothing
            Dim w = deep.Width
            Dim premul = deep.AlphaType = SKAlphaType.Premul
            Dim random = New Random(deep.Width * 733 Xor deep.Height * 397)
            Dim amplitude = strength * 48.0
            Return MapDeepRows(deep, False,
                Sub(y, row)
                    For x = 0 To w - 1
                        Dim cr As Single, cg As Single, cb As Single, a As Integer
                        ReadDeepUnpremultiplied(row, x * 4, premul, cr, cg, cb, a)
                        Dim dr = (random.NextDouble() * 2.0 - 1.0) * amplitude
                        Dim db = (random.NextDouble() * 2.0 - 1.0) * amplitude
                        Dim dg = -(0.299 * dr + 0.114 * db) / 0.587
                        WriteDeepPremultiplied(row, x * 4, premul, cr + CSng(dr), cg + CSng(dg), cb + CSng(db), a)
                    Next
                End Sub)
        End Function

        ''' <summary>Die Vignette auf dem tiefen Bild, dieselbe Rechnung wie <see cref="ApplyVignette"/>
        ''' und wie dort auf den Werten, wie sie im Bild stehen (bei Deckung vormultipliziert): der
        ''' Verlauf nach Abstand von der Mitte, abgedunkelt nach dem gewaehlten Stil
        ''' (<see cref="VignetteDarken"/>) oder zur Deckung hin aufgehellt.</summary>
        Private Shared Function ApplyVignetteDeep(deep As SKBitmap, amount As Single, transition As Single, roundness As Single,
                                                  feather As Single, centerXPercent As Single, centerYPercent As Single,
                                                  style As VignetteStyle) As SKBitmap
            Dim strength = Clamp(Math.Abs(amount), 0, 1.5)
            Dim w = deep.Width, h = deep.Height
            Dim sourcePixels = deep.GetPixels()
            If strength <= 0 OrElse sourcePixels = IntPtr.Zero Then Return Nothing

            Dim cx = w * Clamp(centerXPercent, 0, 100) / 100.0F
            Dim cy = h * Clamp(centerYPercent, 0, 100) / 100.0F
            Dim roundedness = Clamp(roundness, -100, 100) / 100.0F
            Dim radiusX = w * (0.52F - Math.Min(0, roundedness) * 0.18F)
            Dim radiusY = h * (0.52F + Math.Max(0, roundedness) * 0.18F)
            Dim inner = 0.2F + Clamp(transition, 0, 100) / 100.0F * 0.55F
            Dim softness = 0.04F + Clamp(feather, 0, 100) / 100.0F * 0.42F
            Dim edgeAlpha = If(amount > 0, 255.0F, 220.0F) * strength
            Dim darken = amount > 0

            Dim result = NewDeepBitmap(w, h, deep.AlphaType)
            If result Is Nothing Then Return Nothing
            Dim targetPixels = result.GetPixels()
            Parallel.For(0, h,
                Sub(y)
                    Dim row(w * 4 - 1) As Short
                    ReadDeepRow(deep, sourcePixels, y, row)
                    For x = 0 To w - 1
                        Dim dx = (x - cx) / Math.Max(1.0F, radiusX)
                        Dim dy = (y - cy) / Math.Max(1.0F, radiusY)
                        Dim distance = CSng(Math.Sqrt(dx * dx + dy * dy))
                        Dim t = Clamp((distance - inner) / softness, 0, 1)
                        If t <= 0 Then Continue For
                        t = t * t * (3.0F - 2.0F * t)

                        Dim o = x * 4
                        Dim rr = (row(o) And &HFFFF) / 257.0F
                        Dim gg = (row(o + 1) And &HFFFF) / 257.0F
                        Dim bb = (row(o + 2) And &HFFFF) / 257.0F
                        Dim mix = t * edgeAlpha / 255.0F
                        If darken Then
                            Select Case style
                                Case VignetteStyle.HighlightPriority
                                    Dim luma = (0.299F * rr + 0.587F * gg + 0.114F * bb) / 255.0F
                                    Dim factor = 1.0F - mix * (1.0F - luma * luma * 0.7F)
                                    rr *= factor : gg *= factor : bb *= factor
                                Case VignetteStyle.PaintOverlay
                                    Dim luma = 0.299F * rr + 0.587F * gg + 0.114F * bb
                                    Dim desat = mix * 0.5F
                                    Dim dark = 1.0F - mix
                                    rr = (rr + (luma - rr) * desat) * dark
                                    gg = (gg + (luma - gg) * desat) * dark
                                    bb = (bb + (luma - bb) * desat) * dark
                                Case Else
                                    rr *= 1 - mix : gg *= 1 - mix : bb *= 1 - mix
                            End Select
                        Else
                            ' Zielweiss im vormultiplizierten Raum ist die Deckung selbst.
                            Dim aa = (row(o + 3) And &HFFFF) / 257.0F
                            rr += (aa - rr) * mix
                            gg += (aa - gg) * mix
                            bb += (aa - bb) * mix
                        End If
                        row(o) = QuantizeDeep(Clamp(rr, 0.0F, 255.0F) / 255.0F)
                        row(o + 1) = QuantizeDeep(Clamp(gg, 0.0F, 255.0F) / 255.0F)
                        row(o + 2) = QuantizeDeep(Clamp(bb, 0.0F, 255.0F) / 255.0F)
                    Next
                    WriteDeepRow(result, targetPixels, y, row)
                End Sub)
            Return result
        End Function

        ''' <summary>Die Schaerfe auf dem tiefen Bild. Nothing fuer ein Verfahren ohne tiefen
        ''' Durchlauf (das Zurueckrechnen); die Stufe laeuft dann wie gewohnt in 8 Bit, und das
        ''' tiefe Bild endet dort.</summary>
        Private Shared Function ApplySharpnessDeep(deep As SKBitmap, amount As Single, radiusAmount As Single,
                                                   detailAmount As Single, maskingAmount As Single,
                                                   method As SharpenMethod) As SKBitmap
            If method = SharpenMethod.Deconvolution Then
                Return ApplyDeconvolutionSharpeningDeep(deep, amount, radiusAmount, detailAmount, maskingAmount)
            End If
            Return ApplyUnsharpMaskDeep(deep, amount, radiusAmount, detailAmount, maskingAmount)
        End Function

        ''' <summary>Schaerfen durch Zurueckrechnen auf dem tiefen Bild, wie
        ''' <see cref="ApplyDeconvolutionSharpening"/>: Richardson-Lucy auf der Helligkeit, in
        ''' denselben Kacheln mit demselben Rand, die Aenderung der Helligkeit gleich auf alle drei
        ''' Kanaele, mit der Maskierung nach der Kantenstaerke.</summary>
        Private Shared Function ApplyDeconvolutionSharpeningDeep(deep As SKBitmap, amount As Single, radiusAmount As Single,
                                                                 detailAmount As Single, maskingAmount As Single) As SKBitmap
            Dim source = DeepPlanes.Load(deep)
            If source Is Nothing Then Return Nothing
            Dim gain = Math.Max(0.0F, amount) * DeconvolutionGainScale
            If gain <= 0.0F Then Return source.ToBitmap()
            Dim w = source.Width, h = source.Height
            Dim sigma = DeconvolutionSigmaBase + Clamp(radiusAmount, 0, 1) * DeconvolutionSigmaRange
            Dim iterations = DeconvolutionMinIterations +
                             CInt(Math.Round(Clamp(detailAmount, 0, 1) * (DeconvolutionMaxIterations - DeconvolutionMinIterations)))
            Dim masking = Clamp(maskingAmount, 0, 1)
            Dim kernel = GaussianKernel(sigma)

            ' Die Helligkeit einmal fuer das ganze Bild: die Kacheln lesen daraus, und die
            ' Kantenmessung der Maskierung braucht die Nachbarn ueber Kachelgrenzen hinweg.
            Dim luma(w * h - 1) As Single
            Parallel.For(0, h,
                Sub(y)
                    For i = y * w To y * w + w - 1
                        luma(i) = 0.299F * source.R(i) + 0.587F * source.G(i) + 0.114F * source.B(i)
                    Next
                End Sub)

            Dim result = source.NewLike()
            result.A = source.A
            Dim tilesX = (w + DeconvolutionTileEdge - 1) \ DeconvolutionTileEdge
            Dim tilesY = (h + DeconvolutionTileEdge - 1) \ DeconvolutionTileEdge
            Dim options = New ParallelOptions With {
                .MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, DeconvolutionMaxParallelTiles))
            }
            Parallel.For(0, tilesX * tilesY, options,
                Sub(index)
                    Dim tileLeft = (index Mod tilesX) * DeconvolutionTileEdge
                    Dim tileTop = (index \ tilesX) * DeconvolutionTileEdge
                    Dim innerRight = Math.Min(tileLeft + DeconvolutionTileEdge, w)
                    Dim innerBottom = Math.Min(tileTop + DeconvolutionTileEdge, h)
                    Dim left = Math.Max(0, tileLeft - DeconvolutionMargin)
                    Dim top = Math.Max(0, tileTop - DeconvolutionMargin)
                    Dim right = Math.Min(w, innerRight + DeconvolutionMargin)
                    Dim bottom = Math.Min(h, innerBottom + DeconvolutionMargin)
                    Dim tw = right - left, th = bottom - top
                    If tw <= 0 OrElse th <= 0 Then Return

                    Dim n = tw * th
                    Dim pool = Buffers.ArrayPool(Of Single).Shared
                    Dim observed = pool.Rent(n)
                    Dim estimate = pool.Rent(n)
                    Dim blurred = pool.Rent(n)
                    Dim ratio = pool.Rent(n)
                    Dim scratch = pool.Rent(n)
                    Try
                        For y = 0 To th - 1
                            Array.Copy(luma, (top + y) * w + left, observed, y * tw, tw)
                            Array.Copy(luma, (top + y) * w + left, estimate, y * tw, tw)
                        Next
                        For iteration = 1 To iterations
                            SeparableConvolve(estimate, blurred, scratch, tw, th, kernel)
                            For i = 0 To n - 1
                                ratio(i) = observed(i) / Math.Max(0.5F, blurred(i))
                            Next
                            SeparableConvolve(ratio, ratio, scratch, tw, th, kernel)
                            For i = 0 To n - 1
                                Dim factor = Math.Min(DeconvolutionStepLimit, Math.Max(1.0F / DeconvolutionStepLimit, ratio(i)))
                                estimate(i) = Math.Min(255.0F, estimate(i) * factor)
                            Next
                        Next
                        For y = tileTop To innerBottom - 1
                            Dim up = Math.Max(y - 1, 0) * w, down = Math.Min(y + 1, h - 1) * w
                            Dim at = (y - top) * tw - left
                            For x = tileLeft To innerRight - 1
                                Dim weight = gain
                                If masking > 0.0F Then
                                    Dim rowAt = y * w
                                    Dim edge = Clamp((Math.Abs(luma(rowAt + Math.Max(x - 1, 0)) - luma(rowAt + Math.Min(x + 1, w - 1))) +
                                                      Math.Abs(luma(up + x) - luma(down + x))) / SharpenEdgeFullScale, 0, 1)
                                    edge = edge * edge * (3.0F - 2.0F * edge)
                                    weight = gain * ((1.0F - masking) + masking * edge)
                                End If
                                Dim shift = (estimate(at + x) - observed(at + x)) * weight
                                Dim i = y * w + x
                                result.R(i) = source.R(i) + shift
                                result.G(i) = source.G(i) + shift
                                result.B(i) = source.B(i) + shift
                            Next
                        Next
                    Finally
                        pool.Return(observed)
                        pool.Return(estimate)
                        pool.Return(blurred)
                        pool.Return(ratio)
                        pool.Return(scratch)
                    End Try
                End Sub)
            Return result.ToBitmap()
        End Function

        ''' <summary>Dieselbe Unschaerfemaske wie <see cref="ApplyUnsharpMask"/> in Gleitkomma:
        ''' Bandpass der Rec.-709-Luma (kleine Unschaerfe minus grosse), geformt nach
        ''' <see cref="SharpenDelta"/>, derselbe Betrag auf alle drei Kanaele, mit der Maskierung
        ''' nach der Kantenstaerke. Kennzahlen aus <see cref="SharpenParameters"/>. Wer die eine
        ''' Fassung aendert, aendert die andere mit.</summary>
        Private Shared Function ApplyUnsharpMaskDeep(deep As SKBitmap, amount As Single, radiusAmount As Single,
                                                     detailAmount As Single, maskingAmount As Single) As SKBitmap
            Dim p = SharpenParameters(amount, radiusAmount, detailAmount)
            Dim gain = p.Gain, halo = p.Halo, dark = p.Dark
            Dim masking = Clamp(maskingAmount, 0, 1)
            Dim w = deep.Width, h = deep.Height
            Dim sourcePixels = deep.GetPixels()
            If sourcePixels = IntPtr.Zero OrElse w <= 0 OrElse h <= 0 Then Return Nothing
            Dim premul = deep.AlphaType = SKAlphaType.Premul

            ' Die Luma fuer den Bandpass (Rec. 709) und, nur mit Maskierung, die fuer die
            ' Kantenmessung (dieselben Gewichte wie LumaAt).
            Dim luma(w * h - 1) As Single
            Dim edgeLuma As Single() = If(masking > 0, New Single(w * h - 1) {}, Nothing)
            Parallel.For(0, h,
                Sub(y)
                    Dim row(w * 4 - 1) As Short
                    ReadDeepRow(deep, sourcePixels, y, row)
                    Dim at = y * w
                    For x = 0 To w - 1
                        Dim cr As Single, cg As Single, cb As Single, a As Integer
                        ReadDeepUnpremultiplied(row, x * 4, premul, cr, cg, cb, a)
                        luma(at + x) = 0.2126F * cr + 0.7152F * cg + 0.0722F * cb
                        If edgeLuma IsNot Nothing Then edgeLuma(at + x) = 0.299F * cr + 0.587F * cg + 0.114F * cb
                    Next
                End Sub)

            Dim k = SharpenInnerWeights(p.InnerSigma)
            Dim k0 = k.W0, k1 = k.W1, k2 = k.W2
            Dim innerPlane(w * h - 1) As Single
            Parallel.For(0, h,
                Sub(y)
                    Dim at = y * w
                    For x = 0 To w - 1
                        Dim l1 = at + Math.Max(x - 1, 0), r1 = at + Math.Min(x + 1, w - 1)
                        Dim l2 = at + Math.Max(x - 2, 0), r2 = at + Math.Min(x + 2, w - 1)
                        innerPlane(at + x) = k0 * luma(at + x) + k1 * (luma(l1) + luma(r1)) + k2 * (luma(l2) + luma(r2))
                    Next
                End Sub)
            Dim blurred = BlurPlaneLikeSkia(luma, w, h, p.Sigma)

            Dim result = NewDeepBitmap(w, h, deep.AlphaType)
            If result Is Nothing Then Return Nothing
            Dim targetPixels = result.GetPixels()
            Parallel.For(0, h,
                Sub(y)
                    Dim row(w * 4 - 1) As Short
                    ReadDeepRow(deep, sourcePixels, y, row)
                    Dim at = y * w
                    Dim u1 = Math.Max(y - 1, 0) * w, d1 = Math.Min(y + 1, h - 1) * w
                    Dim u2 = Math.Max(y - 2, 0) * w, d2 = Math.Min(y + 2, h - 1) * w
                    For x = 0 To w - 1
                        Dim o = x * 4
                        Dim cr As Single, cg As Single, cb As Single, a As Integer
                        ReadDeepUnpremultiplied(row, o, premul, cr, cg, cb, a)

                        Dim pixelGain = gain
                        If edgeLuma IsNot Nothing Then
                            Dim left = at + Math.Max(x - 1, 0), right = at + Math.Min(x + 1, w - 1)
                            Dim edge = Clamp((Math.Abs(edgeLuma(left) - edgeLuma(right)) +
                                              Math.Abs(edgeLuma(u1 + x) - edgeLuma(d1 + x))) / SharpenEdgeFullScale, 0, 1)
                            edge = edge * edge * (3.0F - 2.0F * edge)
                            pixelGain = gain * ((1.0F - masking) + masking * edge)
                        End If

                        Dim inner = k0 * innerPlane(at + x) + k1 * (innerPlane(u1 + x) + innerPlane(d1 + x)) +
                                    k2 * (innerPlane(u2 + x) + innerPlane(d2 + x))
                        Dim d = (inner - blurred(at + x)) / 255.0F
                        Dim delta = SharpenDelta(d, pixelGain, halo, dark) * 255.0F
                        WriteDeepPremultiplied(row, o, premul, cr + delta, cg + delta, cb + delta, a)
                    Next
                    WriteDeepRow(result, targetPixels, y, row)
                End Sub)
            Return result
        End Function

    End Class

End Namespace
