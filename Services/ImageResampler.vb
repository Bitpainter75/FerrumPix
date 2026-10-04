Imports System
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Verkleinern ohne Streifen.
    '''
    ''' PROBLEM: Skia rechnet kubisch und bilinear mit einem FESTEN Filter von vier bzw. zwei
    ''' Quellpunkten je Achse, gleich wie stark verkleinert wird. Beim Faktor 4 fallen drei von vier
    ''' Quellspalten einfach durch, und ein feines, regelmaessiges Muster (Filmkorn, das Zeilenraster
    ''' eines Filmscanners, Stoff) faltet sich in grobe Streifen. Befund an einem Filmscan, auf
    ''' 1500 Punkte verkleinert: senkrechte Baender im Abstand von rund 9 Punkten, die in voller
    ''' Aufloesung nicht zu sehen sind. Gegenprobe mit SkiaSharp an einem Streifenmuster, Faktor
    ''' 3,85: mit Mitchell bleibt mehr als die Haelfte des Musters als grobe Streifen stehen.
    ''' Mipmaps helfen nur halb und machen weich; vorher halbieren und danach Mitchell laesst bei
    ''' Mustern knapp unter der Grenze immer noch rund 40 Prozent stehen.
    '''
    ''' LOESUNG: ein trennbarer Filter, dessen Breite mit dem Faktor waechst - die uebliche Art, wie
    ''' Bildprogramme verkleinern. Jeder Zielpunkt mittelt ueber ALLE Quellpunkte, die auf ihn
    ''' fallen, gewichtet nach dem Kern. Der Kern bleibt der, den der Nutzer gewaehlt hat:
    ''' "Bikubisch" ist Catmull-Rom (der gewohnte scharfe Kubus), "Bilinear" das Dreieck.
    '''
    ''' Gerechnet wird in Baendern von Zielzeilen, jedes Band mit eigenem kleinen Zwischenpuffer;
    ''' ein Zwischenbild ueber die ganze Hoehe der Quelle waere bei 100 Megapixeln fast ein
    ''' Gigabyte. Nur 8 Bit je Kanal (BGRA oder RGBA): das ist das Format der Pixelkette. Alles
    ''' andere liefert Nothing, und der Aufrufer zeichnet wie bisher ueber Skia.
    ''' </summary>
    Friend NotInheritable Class ImageResampler

        Private Sub New()
        End Sub

        Friend Enum Kernel
            Triangle
            CatmullRom
        End Enum

        ''' <summary>Zielzeilen je Band. Klein genug fuer den Zwischenpuffer, gross genug, dass
        ''' die Ueberlappung der Baender (der Kernradius) kaum doppelt gerechnet wird.</summary>
        Private Const BandRows As Integer = 32

        ''' <summary>Ob dieser Weg fuer die Groessen zustaendig ist: nur wenn mindestens eine Achse
        ''' verkleinert wird. Beim Vergroessern faltet sich nichts, dort bleibt Skia.</summary>
        Friend Shared Function IsDownscale(sourceWidth As Integer, sourceHeight As Integer,
                                           targetWidth As Integer, targetHeight As Integer) As Boolean
            Return targetWidth < sourceWidth OrElse targetHeight < sourceHeight
        End Function

        ''' <summary>Verkleinert auf genau <paramref name="targetWidth"/> x
        ''' <paramref name="targetHeight"/>. Nothing, wenn der Farbtyp nicht 8 Bit je Kanal ist; der
        ''' Aufrufer faellt dann auf Skia zurueck.</summary>
        Friend Shared Function Resize(source As SKBitmap, targetWidth As Integer, targetHeight As Integer,
                                      kernel As Kernel) As SKBitmap
            If source Is Nothing OrElse targetWidth <= 0 OrElse targetHeight <= 0 Then Return Nothing
            Dim info = source.Info
            If info.ColorType <> SKColorType.Bgra8888 AndAlso info.ColorType <> SKColorType.Rgba8888 Then Return Nothing
            Dim sourcePixels = source.GetPixels()
            If sourcePixels = IntPtr.Zero Then Return Nothing

            Dim srcW = info.Width
            Dim srcH = info.Height
            Dim srcStride = source.RowBytes
            Dim premul = info.AlphaType = SKAlphaType.Premul

            Dim horizontal = BuildContributions(srcW, targetWidth, kernel)
            Dim vertical = BuildContributions(srcH, targetHeight, kernel)

            Dim result = New SKBitmap(New SKImageInfo(targetWidth, targetHeight, info.ColorType, info.AlphaType, info.ColorSpace))
            Dim targetPixels = result.GetPixels()
            Dim dstStride = result.RowBytes
            Dim bandCount = (targetHeight + BandRows - 1) \ BandRows

            Parallel.For(0, bandCount,
                Sub(band)
                    Dim y0 = band * BandRows
                    Dim y1 = Math.Min(targetHeight, y0 + BandRows)
                    Dim firstSource = vertical(y0).Start
                    Dim lastSource = 0
                    For y = y0 To y1 - 1
                        lastSource = Math.Max(lastSource, vertical(y).Start + vertical(y).Weights.Length)
                    Next
                    Dim rowsInBand = lastSource - firstSource
                    Dim rowFloats = targetWidth * 4
                    Dim buffer(rowsInBand * rowFloats - 1) As Single
                    Dim sourceRow(srcW * 4 - 1) As Byte

                    ' Erst waagrecht: jede benoetigte Quellzeile auf die Zielbreite.
                    For r = 0 To rowsInBand - 1
                        Marshal.Copy(sourcePixels + (firstSource + r) * srcStride, sourceRow, 0, srcW * 4)
                        Dim baseOut = r * rowFloats
                        For x = 0 To targetWidth - 1
                            Dim c = horizontal(x)
                            Dim w = c.Weights
                            Dim p = c.Start * 4
                            Dim a0 = 0.0F, a1 = 0.0F, a2 = 0.0F, a3 = 0.0F
                            For k = 0 To w.Length - 1
                                Dim wk = w(k)
                                a0 += sourceRow(p) * wk
                                a1 += sourceRow(p + 1) * wk
                                a2 += sourceRow(p + 2) * wk
                                a3 += sourceRow(p + 3) * wk
                                p += 4
                            Next
                            Dim o = baseOut + x * 4
                            buffer(o) = a0
                            buffer(o + 1) = a1
                            buffer(o + 2) = a2
                            buffer(o + 3) = a3
                        Next
                    Next

                    ' Dann senkrecht aus dem Puffer in die Zielzeilen.
                    Dim targetRow(targetWidth * 4 - 1) As Byte
                    For y = y0 To y1 - 1
                        Dim c = vertical(y)
                        Dim w = c.Weights
                        Dim firstRow = c.Start - firstSource
                        For x = 0 To targetWidth - 1
                            Dim o = x * 4
                            Dim a0 = 0.0F, a1 = 0.0F, a2 = 0.0F, a3 = 0.0F
                            Dim p = firstRow * rowFloats + o
                            For k = 0 To w.Length - 1
                                Dim wk = w(k)
                                a0 += buffer(p) * wk
                                a1 += buffer(p + 1) * wk
                                a2 += buffer(p + 2) * wk
                                a3 += buffer(p + 3) * wk
                                p += rowFloats
                            Next
                            ' Der kubische Kern schwingt an harten Kanten ueber; geklemmt wird auf
                            ' 0..255 und, bei vormultiplizierter Deckung, die Farbe auf die Deckung.
                            Dim alpha = ToByte(a3)
                            Dim limit As Byte = If(premul, alpha, CByte(255))
                            targetRow(o) = Math.Min(ToByte(a0), limit)
                            targetRow(o + 1) = Math.Min(ToByte(a1), limit)
                            targetRow(o + 2) = Math.Min(ToByte(a2), limit)
                            targetRow(o + 3) = alpha
                        Next
                        Marshal.Copy(targetRow, 0, targetPixels + y * dstStride, targetWidth * 4)
                    Next
                End Sub)

            result.NotifyPixelsChanged()
            Return result
        End Function

        Private Shared Function ToByte(value As Single) As Byte
            If value <= 0.0F Then Return 0
            If value >= 255.0F Then Return 255
            Return CByte(Math.Floor(value + 0.5F))
        End Function

        Private Structure Contribution
            Public Start As Integer
            Public Weights As Single()
        End Structure

        ''' <summary>Je Zielpunkt der erste Quellpunkt und die Gewichte der folgenden. Der Kern wird
        ''' beim Verkleinern um den Faktor gestreckt; am Rand fallen die Punkte ausserhalb weg, und
        ''' die Gewichte werden auf die Summe 1 gebracht, damit der Rand nicht dunkler wird.</summary>
        Private Shared Function BuildContributions(sourceSize As Integer, targetSize As Integer, kernel As Kernel) As Contribution()
            Dim scale = sourceSize / CDbl(targetSize)
            Dim stretch = Math.Max(1.0, scale)
            Dim radius = If(kernel = Kernel.CatmullRom, 2.0, 1.0) * stretch
            Dim result(targetSize - 1) As Contribution
            For i = 0 To targetSize - 1
                Dim center = (i + 0.5) * scale
                Dim first = Math.Max(0, CInt(Math.Floor(center - radius)))
                Dim last = Math.Min(sourceSize - 1, CInt(Math.Ceiling(center + radius)))
                Dim weights(last - first) As Double
                Dim sum = 0.0
                For j = first To last
                    Dim w = Evaluate(kernel, (j + 0.5 - center) / stretch)
                    weights(j - first) = w
                    sum += w
                Next
                Dim normalized(last - first) As Single
                If Math.Abs(sum) < 0.000001 Then
                    ' Kann nur bei einem Zielpunkt zwischen zwei Quellpunkten mit Gewicht 0 passieren;
                    ' dann gilt der naechstgelegene allein.
                    normalized(Math.Min(last - first, Math.Max(0, CInt(Math.Floor(center)) - first))) = 1.0F
                Else
                    For k = 0 To weights.Length - 1
                        normalized(k) = CSng(weights(k) / sum)
                    Next
                End If
                result(i) = New Contribution With {.Start = first, .Weights = normalized}
            Next
            Return result
        End Function

        Private Shared Function Evaluate(kernel As Kernel, x As Double) As Double
            x = Math.Abs(x)
            If kernel = Kernel.Triangle Then Return If(x < 1.0, 1.0 - x, 0.0)
            ' Catmull-Rom, also der kubische Kern mit a = -0,5.
            If x < 1.0 Then Return 1.5 * x * x * x - 2.5 * x * x + 1.0
            If x < 2.0 Then Return -0.5 * x * x * x + 2.5 * x * x - 4.0 * x + 2.0
            Return 0.0
        End Function

    End Class

End Namespace
