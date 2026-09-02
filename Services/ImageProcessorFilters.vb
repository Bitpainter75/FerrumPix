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

' Die Filterstufen der Pixelkette, die nicht Punktoperation sind: Rauschminderung in Farbe und
' Helligkeit, Median, lokaler Kontrast, Klarheit, Struktur, Dunst, Leuchten, Staub und Kratzer,
' Filmnegativ, Tonwertkurve, HSL-Baender, Filtervorgaben, Weichzeichner, LUT, Vignette und Korn.
' Eigener Zustand: der LUT-Zwischenspeicher und die Mitten der HSL-Baender.
' Herausgeloest am 2026-08-06 aus ImageProcessor.vb, Zeile fuer Zeile unveraendert.
Namespace Services

    Partial Public Class ImageProcessor

        ''' <summary>Radius des GROBEN Chroma-Durchgangs in Pixeln des GERADE bearbeiteten Bildes.
        '''
        ''' Bewusst relativ zur Bildkante: Farbflecken sind ein Anteil des Bildes, kein absolutes
        ''' Pixelmass. Ein fester Radius traefe in der Vorschau (lange Kante ~1500 px) eine ganz
        ''' andere Fleckengroesse als im Export (6000 px) - Vorschau und Ergebnis saehen verschieden
        ''' aus. Weil `source` hier immer das ist, was auch angezeigt bzw. gespeichert wird, stimmen
        ''' beide ueberein. Der Deckel haelt die Rechenzeit bei sehr grossen Bildern im Rahmen.</summary>
        Private Shared Function CoarseChromaSigma(source As SKBitmap, amount As Single) As Single
            Dim longEdge = CSng(Math.Max(source.Width, source.Height))
            ' Der Teiler ist gemessen, nicht geschaetzt: bei 1600 px langer Kante und Flecken von
            ' 6 bis 12 px bleiben mit /300 noch 36 bzw. 56 Prozent stehen, mit /150 nur noch 27
            ' bzw. 39 Prozent - erst da ist der Vollausschlag das, was er verspricht. Feines
            ' Pixelrauschen faellt dabei von 11 auf 0,5 Prozent.
            Return Clamp(amount * longEdge / 150.0F, 0, 24.0F)
        End Function

        ''' <summary>Farb-Rauschreduzierung (crs:ColorNoiseReduction): das Bild wird weichgezeichnet,
        ''' danach bekommt jedes Pixel seine ORIGINAL-Helligkeit zurueck (Differenz der Rec.601-Luma
        ''' auf alle Kanaele addiert). Ergebnis: Chroma aus dem Blur, Luminanz vom Original - Kanten
        ''' und Details bleiben stehen, Farbflecken verschwinden. Chroma vertraegt deutlich mehr
        ''' Glaettung als Helligkeit, daher ein groesseres Sigma als bei ApplyNoiseReduction.
        '''
        ''' ZWEI DURCHGAENGE (Messung): der feine Pass allein (Sigma bis 2,5 px) loescht
        ''' pixelweises Farbrauschen restlos aus - genau das prueft die Diagnose seit.
        ''' Das Farbrauschen eines hochgezogenen Nachthimmels ist aber TIEFFREQUENT: Flecken von
        ''' 5 bis 15 px. Davon liessen 2,5 px gemessen 61 bis 78 Prozent stehen, der Regler war fuer
        ''' den eigentlichen Anwendungsfall wirkungslos (Nachtaufnahme).
        ''' Der grobe Pass setzt darum auf dem feinen auf; kaskadiert wirken beide wie ein Radius
        ''' von Wurzel(fein^2 + grob^2), ohne dass der feine Anteil unten am Regler seine Wirkung
        ''' verliert.</summary>
        ''' <summary>Wie schnell der FEINE Durchgang mit dem Regler hochlaeuft. Er war frueher
        ''' linear an den Reglerwert gekoppelt: bei 25 wurden 25 Prozent geglaettete Chroma mit
        ''' 1,0 px Radius beigemischt. Gemessen an einem Konzert-RAW liess das vom Farbrauschen
        ''' 3,26 von 4,39 stehen, waehrend die Referenz beim GLEICHEN Wert auf 1,25 kommt - unsere 25
        ''' wirkten wie deren 5. Feines Pixelrauschen ist aber bei kleinem Radius erledigt oder gar
        ''' nicht; ein Regler, der es erst bei Vollausschlag wegnimmt, ist falsch geeicht.
        ''' Der feine Durchgang (Radius UND Beimischung) ist deshalb bei 42 voll ausgefahren -
        ''' damit trifft 25, der uebliche Standardwert fuer RAWs, auch bei uns dessen Wirkung.
        ''' Der obere Reglerbereich bleibt nicht tot: der GROBE Durchgang skaliert weiterhin mit
        ''' dem rohen Reglerwert ueber die ganze Strecke, und nur er erreicht die tieffrequenten
        ''' Farbflecken (siehe CoarseChromaSigma).</summary>
        Private Const ChromaFineGain As Single = 3.2F

        Private Shared Function ApplyColorNoiseReduction(source As SKBitmap, amount As Single) As SKBitmap
            amount = Clamp(amount, 0, 1)
            Dim fein = Clamp(amount * ChromaFineGain, 0, 1)
            ' Feiner Pass: Sigma waechst bis 2,5 Pixel, und dieselbe Staerke blendet zwischen
            ' Original-Chroma und geglaetteter Chroma ueber. BEIDES haengt an fein, nicht am rohen
            ' Reglerwert - sonst waere der untere Reglerbereich zu schwach (siehe ChromaFineGain).
            ' Historie, damit es nicht rueckwaerts repariert wird: als NUR das Sigma gesteuert wurde,
            ' war der Regler ab etwa 30 wirkungslos (50 und 100 aenderten dieselben 53 bzw. 54 % der
            ' Pixel um maximal 7 bzw. 6 Tonwerte) - deshalb kam die Ueberblendung dazu. Sie linear an
            ' den Reglerwert zu haengen war dann die Uebertreibung in die andere Richtung.
            Dim sigma = 0.5F + fein * 2.0F
            Dim blurred = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using filter = SKImageFilter.CreateBlur(sigma, sigma)
                Using paint = New SKPaint With {.ImageFilter = filter}
                    Using canvas = New SKCanvas(blurred)
                        canvas.DrawBitmap(source, 0, 0, paint)
                    End Using
                End Using
            End Using

            ' Grober Pass auf dem feinen: nimmt die FLECKEN, die der feine Radius nicht erreicht.
            Dim coarseSigma = CoarseChromaSigma(source, amount)
            Dim coarse As SKBitmap = Nothing
            If coarseSigma > 0.5F Then
                coarse = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
                Using filter = SKImageFilter.CreateBlur(coarseSigma, coarseSigma)
                    Using paint = New SKPaint With {.ImageFilter = filter}
                        Using canvas = New SKCanvas(coarse)
                            canvas.DrawBitmap(blurred, 0, 0, paint)
                        End Using
                    End Using
                End Using
            End If

            Dim srcBuf As Byte() = Nothing
            Dim srcStride As Integer = 0
            Dim blurBuf As Byte() = Nothing
            Dim blurStride As Integer = 0
            Dim coarseBuf As Byte() = Nothing
            Dim coarseStride As Integer = 0
            If Not TryBorrowBgraBuffer(source, srcBuf, srcStride) OrElse
               Not TryBorrowBgraBuffer(blurred, blurBuf, blurStride) Then
                coarse?.Dispose()
                Return blurred
            End If
            If coarse IsNot Nothing AndAlso Not TryBorrowBgraBuffer(coarse, coarseBuf, coarseStride) Then
                coarseBuf = Nothing
            End If

            ' SCHUTZ FUER ECHTE FARBEN: ein Chroma-Blur macht keinen Unterschied zwischen einem
            ' Farbfleck und einem kleinen bunten Licht - beide sind "Farbe auf kleiner Flaeche".
            ' Gemessen verlor eine 3x3 px grosse Fensterlampe bei Vollausschlag 80 Prozent ihrer
            ' Saettigung. Rauschen ist aber SCHWACH gesaettigt und echte Lichter sind es stark:
            ' ab einer Farbabweichung von 30 nimmt die Wirkung ab, ab 90 bleibt das Pixel ganz in
            ' Ruhe. Der Schutz gilt fuer BEIDE Durchgaenge - am feinen allein vorbeigezogen haette
            ' er nichts genuetzt, denn schon dessen 2,5 px bleichen ein so kleines Licht aus.
            ' Farbsaum um ein Licht herum entsteht dadurch nicht nennenswert: die Farbe einer
            ' 3x3-Lampe verteilt sich im Blur auf einige hundert Pixel und geht darin unter.
            Const schutzVon As Double = 30.0
            Const schutzBis As Double = 90.0
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim dstBuf(srcBuf.Length - 1) As Byte
            ForEachRow(source.Width, source.Height, Sub(y)
                                                        Dim so = y * srcStride
                                                        Dim bo = y * blurStride
                                                        Dim co = y * coarseStride
                                                        For x = 0 To source.Width - 1
                                                            Dim colIdx = so + x * 4
                                                            Dim bi = bo + x * 4
                                                            ' Rec.601-Luma in Ganzzahlarithmetik (x1024).
                                                            Dim lumaSrc = (299 * CInt(srcBuf(colIdx + 2)) + 587 * CInt(srcBuf(colIdx + 1)) + 114 * CInt(srcBuf(colIdx))) \ 1000
                                                            Dim mix0 = CDbl(blurBuf(bi))
                                                            Dim mix1 = CDbl(blurBuf(bi + 1))
                                                            Dim mix2 = CDbl(blurBuf(bi + 2))
                                                            If coarseBuf IsNot Nothing Then
                                                                Dim ci = co + x * 4
                                                                mix0 = CDbl(coarseBuf(ci))
                                                                mix1 = CDbl(coarseBuf(ci + 1))
                                                                mix2 = CDbl(coarseBuf(ci + 2))
                                                            End If
                                                            Dim chroma = (Math.Abs(CInt(srcBuf(colIdx + 2)) - lumaSrc) + Math.Abs(CInt(srcBuf(colIdx)) - lumaSrc)) / 2.0
                                                            Dim schutz = 1.0 - Math.Max(0.0, Math.Min(1.0, (chroma - schutzVon) / (schutzBis - schutzVon)))
                                                            Dim wirkung = fein * schutz
                                                            Dim lumaMix = (299 * mix2 + 587 * mix1 + 114 * mix0) / 1000.0
                                                            Dim delta = lumaSrc - lumaMix
                                                            ' Chroma aus dem Blur, Luminanz vom Original - und beides
                                                            ' anteilig ueber das Original geblendet, damit der Regler
                                                            ' ueber den ganzen Weg etwas tut.
                                                            Dim nb0 = mix0 + delta
                                                            Dim nb1 = mix1 + delta
                                                            Dim nb2 = mix2 + delta
                                                            dstBuf(colIdx) = ClampToByte(CInt(srcBuf(colIdx)) + (nb0 - CInt(srcBuf(colIdx))) * wirkung)
                                                            dstBuf(colIdx + 1) = ClampToByte(CInt(srcBuf(colIdx + 1)) + (nb1 - CInt(srcBuf(colIdx + 1))) * wirkung)
                                                            dstBuf(colIdx + 2) = ClampToByte(CInt(srcBuf(colIdx + 2)) + (nb2 - CInt(srcBuf(colIdx + 2))) * wirkung)
                                                            dstBuf(colIdx + 3) = srcBuf(colIdx + 3)
                                                        Next
                                                    End Sub)
            blurred.Dispose()
            coarse?.Dispose()
            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            Return result
        End Function

        ''' <summary>Die PLUS-Seite des Farbrauschen-Reglers: faerbt das Bild ein, ohne es heller oder
        ''' dunkler zu machen. Pro Pixel wandern Rot und Blau gegenlaeufig, Gruen gleicht die Helligkeit
        ''' aus - das ist genau die Gegenrichtung zur Reduzierung (die Chroma glaettet und die Luma
        ''' stehen laesst). Vom monochromen „Koernung" (ApplyGrain) unterscheidet es sich dadurch, dass
        ''' die Helligkeit unangetastet bleibt und nur die Farbe zappelt.
        ''' Der Zufall haengt allein an der Bildgroesse (wie ApplyAddNoise): derselbe Regler ergibt
        ''' zweimal dasselbe Bild, und die Vorschau flackert beim Ziehen nicht.</summary>
        Private Shared Function ApplyColorNoiseAdd(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return source
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            Dim sLen = 0
            Dim dstBuf As Byte() = Nothing
            Try
            If Not TryRentRgbaLikeBuffer(source, srcBuf, sLen, stride, ri, gi, bi, ai) Then Return result
            dstBuf = ArrayPool(Of Byte).Shared.Rent(sLen)
            If stride <> source.Width * 4 Then Array.Clear(dstBuf, 0, sLen)

            Dim random = New Random(source.Width * 733 Xor source.Height * 397)
            Dim amplitude = strength * 48.0

            For y As Integer = 0 To source.Height - 1
                Dim rowOffset = y * stride
                For x As Integer = 0 To source.Width - 1
                    Dim o = rowOffset + x * 4
                    Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                    ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                    Dim dr = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    Dim db = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    ' Rec.601: Gruen traegt 0,587 der Helligkeit - genau so viel gegensteuern, wie
                    ' Rot (0,299) und Blau (0,114) zusammen einbringen, dann bleibt die Luma gleich.
                    Dim dg = -(0.299 * dr + 0.114 * db) / 0.587
                    WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                                       ClampToByte(cr + dr), ClampToByte(cg + dg), ClampToByte(cb + db), a)
                Next
            Next

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), sLen)
            Return result
            Finally
                ReturnPooledBuffer(srcBuf)
                ReturnPooledBuffer(dstBuf)
            End Try
        End Function

        Private Shared Function ApplyNoiseReduction(source As SKBitmap, amount As Single, Optional detail As Single = 0) As SKBitmap
            Dim sigma = 0.25F + Clamp(amount, 0, 1) * 2.2F
            Dim filter = SKImageFilter.CreateBlur(sigma, sigma)
            Dim paint = New SKPaint With {.ImageFilter = filter}
            Dim blurred = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(blurred)
                canvas.DrawBitmap(source, 0, 0, paint)
            End Using
            filter.Dispose()
            paint.Dispose()

            ' Detail 0 = reines Weichzeichnen wie bisher (bitgenau der frühere Rückgabewert).
            Dim d = Clamp(detail, 0, 1)
            If d <= 0 Then Return blurred

            ' Kantenerhalt: wo Original und Weichzeichnung stark abweichen (= eine Kante), das Original
            ' anteilig zurückmischen. Flache, verrauschte Flächen bleiben geglättet.
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing, blurBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            Dim bStride, bri, bgi, bbi, bai As Integer
            Dim sLen = 0, bLen = 0
            Dim dstBuf As Byte() = Nothing
            Try
            If Not TryRentRgbaLikeBuffer(source, srcBuf, sLen, stride, ri, gi, bi, ai) OrElse
               Not TryRentRgbaLikeBuffer(blurred, blurBuf, bLen, bStride, bri, bgi, bbi, bai) Then
                blurred.Dispose()
                Return result
            End If
            dstBuf = ArrayPool(Of Byte).Shared.Rent(sLen)
            If stride <> source.Width * 4 Then Array.Clear(dstBuf, 0, sLen)
            Dim w = source.Width, h = source.Height

            ForEachRow(w, h,
                Sub(y)
                    Dim rowOffset = y * stride
                    Dim bRow = y * bStride
                    For x = 0 To w - 1
                        Dim o = rowOffset + x * 4
                        Dim bo = bRow + x * 4
                        Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                        ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                        Dim lr As Integer, lg As Integer, lb As Integer, la As Integer
                        ReadUnpremultiplied(blurBuf, bo, bri, bgi, bbi, bai, lr, lg, lb, la)
                        Dim diff = Math.Abs(cr - lr) + Math.Abs(cg - lg) + Math.Abs(cb - lb)
                        Dim mask = Clamp(diff / 48.0F, 0, 1) * d
                        WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                            ClampToByte(lr + (cr - lr) * mask),
                            ClampToByte(lg + (cg - lg) * mask),
                            ClampToByte(lb + (cb - lb) * mask), a)
                    Next
                End Sub)

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), sLen)
            blurred.Dispose()
            Return result
            Finally
                ReturnPooledBuffer(srcBuf)
                ReturnPooledBuffer(blurBuf)
                ReturnPooledBuffer(dstBuf)
            End Try
        End Function

        ''' <summary>Der Wert an Position <paramref name="mid"/> der sortierten Fensterwerte, gelesen aus dem
        ''' Histogramm: der kleinste Tonwert, bis zu dem mehr als <paramref name="mid"/> Werte liegen. Das ist
        ''' exakt das, was `sortierteListe(mid)` liefert - nur ohne die Liste und ohne das Sortieren.</summary>
        Private Shared Function HistogramMedian(histogram As Integer(), mid As Integer) As Byte
            Dim running As Integer = 0
            For v As Integer = 0 To 255
                running += histogram(v)
                If running > mid Then Return CByte(v)
            Next
            Return 255
        End Function

        ''' <summary>Kantenerhaltender Medianfilter - echte Rauschunterdrückung statt gleichmäßigem
        ''' Weichzeichnen.
        ''' Das Fenster wandert als HISTOGRAMM mit: beim Schritt nach rechts wird nur die austretende Spalte
        ''' ab- und die eintretende zugezählt, statt für jedes Pixel alle (2r+1)² Werte neu einzusammeln und
        ''' zu sortieren. Die vorherige Fassung baute pro Pixel drei Listen auf und rief `List.Sort` -
        ''' bei Radius 3 also 49 Elemente, sortiert, 2,56 Millionen Mal. Das war die Zähigkeit, die man an
        ''' den Reglern für Rauschreduzierung und Staub/Kratzer gespürt hat.
        ''' Der Median ist exakt, das Ergebnis daher BITGLEICH zur alten Fassung - kein Kompromiss auf
        ''' Kosten der Bildqualität.</summary>
        Private Shared Function ApplyMedianBlur(source As SKBitmap, amount As Single) As SKBitmap
            Dim clamped = Clamp(amount, 0, 1)
            Dim radius = Math.Max(1, CInt(Math.Round(1 + clamped * 2)))
            Dim w = source.Width
            Dim h = source.Height
            Dim result = New SKBitmap(w, h, source.ColorType, source.AlphaType)
            Dim rWindow As New List(Of Byte)()
            Dim gWindow As New List(Of Byte)()
            Dim bWindow As New List(Of Byte)()

            Dim srcBuf As Byte() = Nothing
            Dim stride As Integer = 0
            If TryBorrowBgraBuffer(source, srcBuf, stride) Then
                Dim dstBuf = New Byte(srcBuf.Length - 1) {}
                ' Jede Zeile bekommt eigene Histogramme - parallel dürfen sie nicht geteilt werden.
                ForEachRow(w, h, Sub(y)
                                     Dim histB = New Integer(255) {}
                                     Dim histG = New Integer(255) {}
                                     Dim histR = New Integer(255) {}
                                     Dim count As Integer = 0
                                     Dim rowOffset = y * stride
                                     Dim yFrom = Math.Max(0, y - radius)
                                     Dim yTo = Math.Min(h - 1, y + radius)

                                     ' Startfenster für x = 0 aufbauen; danach nur noch verschieben.
                                     For xx As Integer = 0 To Math.Min(w - 1, radius)
                                         For yy As Integer = yFrom To yTo
                                             Dim oo = yy * stride + xx * 4
                                             histB(srcBuf(oo)) += 1
                                             histG(srcBuf(oo + 1)) += 1
                                             histR(srcBuf(oo + 2)) += 1
                                             count += 1
                                         Next
                                     Next

                                     For x As Integer = 0 To w - 1
                                         If x > 0 Then
                                             Dim leaving = x - radius - 1
                                             If leaving >= 0 Then
                                                 For yy As Integer = yFrom To yTo
                                                     Dim oo = yy * stride + leaving * 4
                                                     histB(srcBuf(oo)) -= 1
                                                     histG(srcBuf(oo + 1)) -= 1
                                                     histR(srcBuf(oo + 2)) -= 1
                                                     count -= 1
                                                 Next
                                             End If
                                             Dim entering = x + radius
                                             If entering <= w - 1 Then
                                                 For yy As Integer = yFrom To yTo
                                                     Dim oo = yy * stride + entering * 4
                                                     histB(srcBuf(oo)) += 1
                                                     histG(srcBuf(oo + 1)) += 1
                                                     histR(srcBuf(oo + 2)) += 1
                                                     count += 1
                                                 Next
                                             End If
                                         End If

                                         Dim centerO = rowOffset + x * 4
                                         Dim mid = count \ 2
                                         dstBuf(centerO) = HistogramMedian(histB, mid)
                                         dstBuf(centerO + 1) = HistogramMedian(histG, mid)
                                         dstBuf(centerO + 2) = HistogramMedian(histR, mid)
                                         dstBuf(centerO + 3) = srcBuf(centerO + 3)
                                     Next
                                 End Sub)
                CommitBgraBuffer(result, dstBuf)
                Return result
            End If

            For y As Integer = 0 To h - 1
                For x As Integer = 0 To w - 1
                    rWindow.Clear() : gWindow.Clear() : bWindow.Clear()
                    Dim alpha = source.GetPixel(x, y).Alpha
                    For yy As Integer = Math.Max(0, y - radius) To Math.Min(h - 1, y + radius)
                        For xx As Integer = Math.Max(0, x - radius) To Math.Min(w - 1, x + radius)
                            Dim c = source.GetPixel(xx, yy)
                            rWindow.Add(c.Red)
                            gWindow.Add(c.Green)
                            bWindow.Add(c.Blue)
                        Next
                    Next
                    rWindow.Sort() : gWindow.Sort() : bWindow.Sort()
                    Dim mid = rWindow.Count \ 2
                    result.SetPixel(x, y, New SKColor(rWindow(mid), gWindow(mid), bWindow(mid), alpha))
                Next
            Next

            Return result
        End Function

        ''' Gemeinsamer Unsharp-Mask-artiger Lokalkontrast-Kern für Clarity/Structure - Unterschied
        ''' zwischen beiden Reglern ist ausschließlich blurSigma (Frequenzband) und strengthMultiplier.
        ''' <summary>Lokaler Kontrast (Unschaerfemaske) - Grundlage von Klarheit und Struktur.
        '''
        ''' Lief bis ueber GetPixel/SetPixel, also mit einem P/Invoke JE PIXEL. Gemessen
        ''' kostete Klarheit dadurch 4,3 s bei 6,3 MP - waehrend die gesamte verschmolzene Farbkette
        ''' 17 ms braucht. Jetzt ueber geliehene Puffer und ForEachRow, wie der Rest der Pipeline.
        '''
        ''' WICHTIG fuer die Bitgleichheit: GetPixel ENTpremultipliziert und SetPixel premultipliziert
        ''' wieder (gemessen: gespeichert (100,50,25,128) liefert GetPixel (199,100,50,128)).
        ''' Ein naiver Umbau auf Rohbytes wuerde deshalb bei teiltransparenten Pixeln ANDERE Ergebnisse
        ''' liefern. Das Verhalten ist unten exakt nachgebildet.</summary>
        ''' <remarks>ZWEI MESSPUNKTE, weil die Stufe zwei ganz verschiedene Dinge tut: eine
        ''' Weichzeichnung des ganzen Bildes (Skia, separierbar, Sigma hoechstens 2,45) und danach
        ''' das Verrechnen beider Bilder ueber alle Bildpunkte. Klarheit und Struktur waren mit
        ''' zusammen 325 ms der groesste Posten der Kette, nachdem die Koernung erledigt war
        ''' (Patricks Protokoll vom 2026-08-28) - und ohne diese Teilung ist nicht zu sagen, welche
        ''' Haelfte das ist. Beide Aufrufer teilen sich die Namen; die Summe steht im Protokoll.</remarks>
        Private Shared Function ApplyLocalContrast(source As SKBitmap, blurSigma As Single, amount As Single, strengthMultiplier As Single) As SKBitmap
            Using blurred = PerformanceTraceService.Measure(
                "Pixel: Lokalkontrast Unschaerfe", Function() ApplyNoiseReduction(source, blurSigma / 8.0F))
                Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)

                ' GELIEHEN STATT NEU: drei Felder in Bildgroesse je Aufruf, und die Stufe laeuft
                ' zweimal je Render. Frisch angelegt sind das bei 9,85 Megapixeln rund 156 MB auf
                ' dem Large Object Heap - siehe TryRentRgbaLikeBuffer. Das Ergebnis bleibt bitgleich.
                Dim srcBuf As Byte() = Nothing, blurBuf As Byte() = Nothing, dstBuf As Byte() = Nothing
                Dim sStride, bStride, ri, gi, bi, ai As Integer
                Dim bri, bgi, bbi, bai As Integer
                Dim sLen = 0, bLen = 0
                Try
                If Not TryRentRgbaLikeBuffer(source, srcBuf, sLen, sStride, ri, gi, bi, ai) OrElse
                   Not TryRentRgbaLikeBuffer(blurred, blurBuf, bLen, bStride, bri, bgi, bbi, bai) Then
                    Return result
                End If

                dstBuf = ArrayPool(Of Byte).Shared.Rent(sLen)
                ' Ein geliehenes Feld ist nicht genullt. Die Schleife unten beschreibt je Bildpunkt
                ' alle vier Bytes, also bleibt hoechstens die ZEILENAUFFUELLUNG stehen - die gibt es
                ' bei Bgra8888/Rgba8888 normalerweise nicht, und wenn doch, wird sie hier geraeumt.
                If sStride <> source.Width * 4 Then Array.Clear(dstBuf, 0, sLen)
                Dim factor = amount * strengthMultiplier
                Dim width = source.Width

                ' Der Messpunkt umschliesst die Schleife UND die Kopie zurueck ins Bitmap. Vorher lag
                ' er nur um die Schleife, und dadurch fehlten in der Rechnung "Unschaerfe plus
                ' Verrechnen gegen die ganze Stufe" rund 60 ms - genau die Belegungen und die
                ' Kopiererei, um die es hier geht.
                PerformanceTraceService.Measure("Pixel: Lokalkontrast Verrechnen",
                    Sub()
                ForEachRow(width, source.Height,
                    Sub(y)
                        Dim so = y * sStride
                        Dim bo = y * bStride
                        For x = 0 To width - 1
                            Dim o = so + x * 4
                            Dim p = bo + x * 4
                            Dim a = srcBuf(o + ai)
                            If a = 0 Then
                                dstBuf(o) = 0 : dstBuf(o + 1) = 0 : dstBuf(o + 2) = 0 : dstBuf(o + 3) = 0
                                Continue For
                            End If

                            ' Entpremultiplizieren wie GetPixel es tut - sonst weicht das Ergebnis
                            ' bei teiltransparenten Pixeln vom bisherigen Verhalten ab.
                            Dim cr As Integer, cg As Integer, cb As Integer
                            Dim br As Integer, bg As Integer, bb As Integer
                            If a = 255 Then
                                cr = srcBuf(o + ri) : cg = srcBuf(o + gi) : cb = srcBuf(o + bi)
                                br = blurBuf(p + bri) : bg = blurBuf(p + bgi) : bb = blurBuf(p + bbi)
                            Else
                                cr = Math.Min(255, srcBuf(o + ri) * 255 \ a)
                                cg = Math.Min(255, srcBuf(o + gi) * 255 \ a)
                                cb = Math.Min(255, srcBuf(o + bi) * 255 \ a)
                                Dim ba = blurBuf(p + bai)
                                If ba = 0 Then
                                    br = 0 : bg = 0 : bb = 0
                                Else
                                    br = Math.Min(255, blurBuf(p + bri) * 255 \ ba)
                                    bg = Math.Min(255, blurBuf(p + bgi) * 255 \ ba)
                                    bb = Math.Min(255, blurBuf(p + bbi) * 255 \ ba)
                                End If
                            End If

                            Dim nr = ClampToByte(cr + (cr - br) * factor)
                            Dim ng = ClampToByte(cg + (cg - bg) * factor)
                            Dim nb = ClampToByte(cb + (cb - bb) * factor)

                            If a <> 255 Then
                                ' Zurueck nach premultipliziert, wie SetPixel es tut.
                                nr = CByte(Math.Min(CInt(a), CInt(nr) * a \ 255))
                                ng = CByte(Math.Min(CInt(a), CInt(ng) * a \ 255))
                                nb = CByte(Math.Min(CInt(a), CInt(nb) * a \ 255))
                            End If

                            dstBuf(o + ri) = nr
                            dstBuf(o + gi) = ng
                            dstBuf(o + bi) = nb
                            dstBuf(o + ai) = a
                        Next
                    End Sub)
                        ' sLen, NICHT dstBuf.Length: das geliehene Feld ist meist groesser als
                        ' angefordert, und eine Kopie ueber seine volle Laenge schriebe ueber das
                        ' Bitmap hinaus.
                        Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), sLen)
                    End Sub)

                Return result
                Finally
                    ReturnPooledBuffer(srcBuf)
                    ReturnPooledBuffer(blurBuf)
                    ReturnPooledBuffer(dstBuf)
                End Try
            End Using
        End Function

        Private Shared Function ApplyClarity(source As SKBitmap, amount As Single) As SKBitmap
            ' Clarity = breiter Mitteltonkontrast: bewusst deutlich größerer Blur-Radius als Structure.
            ' Untergrenze so wählen, dass ApplyNoiseReduction einen effektiven Gauß-Sigma > ~1.6 liefert
            ' (blurSigma 5.0 → 0.625 → eff. ~1.6); bei 2.0 war Clarity bei kleinen Stärken fast ein No-Op
            ' und vom feinen Structure-Radius (eff. ~1.24) kaum zu unterscheiden.
            Dim sigma = 5.0F + Math.Abs(amount) * 5.0F
            Return ApplyLocalContrast(source, sigma, amount, 1.6F)
        End Function

        ''' Anders als Clarity (breiter, mit der Stärke wachsender Mitteltonkontrast-Radius): Structure
        ''' arbeitet auf einem festen, kleinen Blur-Radius (feine Textur/Detailkontrast) - vorher rief
        ''' diese Funktion nur ApplyClarity mit abgeschwächter Stärke auf und war dadurch visuell nicht
        ''' von Clarity unterscheidbar, nur schwächer.
        Private Shared Function ApplyStructure(source As SKBitmap, amount As Single) As SKBitmap
            Dim clamped = Clamp(amount, -1, 1)
            ' blurSigma muss über ApplyNoiseReduction einen effektiven Gauß-Sigma > ~1.0 ergeben, sonst
            ' ist SkiaSharps CreateBlur praktisch ein No-Op und Structure wirkungslos (Original minus
            ' unveränderte "Weichzeichnung" = 0). 1.2 lag darunter und machte den Regler tot; 3.6 ergibt
            ' effektiv ~1.24 - klar wirksam, aber weiter kleinerer Radius als Clarity (feinere Textur).
            Return ApplyLocalContrast(source, 3.6F, clamped, 2.4F)
        End Function

        ''' <summary>Dunst. Lief bis ueber GetPixel/SetPixel (P/Invoke je Pixel, gemessen
        ''' 4,3 s bei 6,3 MP); jetzt ueber geliehene Puffer. Die Alpha-Semantik von GetPixel/SetPixel
        ''' ist ueber ReadUnpremultiplied/WritePremultiplied exakt nachgebildet.</summary>
        Private Shared Function ApplyHaze(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return source
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)

            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            Dim sLen = 0
            Dim dstBuf As Byte() = Nothing
            Try
            If Not TryRentRgbaLikeBuffer(source, srcBuf, sLen, stride, ri, gi, bi, ai) Then Return result
            dstBuf = ArrayPool(Of Byte).Shared.Rent(sLen)
            If stride <> source.Width * 4 Then Array.Clear(dstBuf, 0, sLen)
            Dim width = source.Width

            ForEachRow(width, source.Height,
                Sub(y)
                    Dim rowOffset = y * stride
                    For x = 0 To width - 1
                        Dim o = rowOffset + x * 4
                        Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                        ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                        Dim nr As Byte, ng As Byte, nb As Byte
                        If strength > 0 Then
                            Dim sv = strength * 0.45F
                            nr = ClampToByte(cr + (255 - cr) * sv)
                            ng = ClampToByte(cg + (255 - cg) * sv)
                            nb = ClampToByte(cb + (255 - cb) * sv)
                        Else
                            Dim sv = -strength
                            Dim contrast = 1.0F + sv * 0.55F
                            nr = ClampToByte((cr - 128) * contrast + 128 - sv * 10)
                            ng = ClampToByte((cg - 128) * contrast + 128 - sv * 10)
                            nb = ClampToByte((cb - 128) * contrast + 128 - sv * 10)
                        End If
                        WritePremultiplied(dstBuf, o, ri, gi, bi, ai, nr, ng, nb, a)
                    Next
                End Sub)

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), sLen)
            Return result
            Finally
                ReturnPooledBuffer(srcBuf)
                ReturnPooledBuffer(dstBuf)
            End Try
        End Function

        ''' <summary>Leuchten. Wie ApplyHaze von GetPixel/SetPixel auf Puffer umgestellt
        ''' (gemessen 4,5 s bei 6,3 MP).</summary>
        Private Shared Function ApplyImageGlow(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return source
            Using blurred = ApplyNoiseReduction(source, 0.8F)
                Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)

                Dim srcBuf As Byte() = Nothing, blurBuf As Byte() = Nothing
                Dim sStride, bStride, ri, gi, bi, ai As Integer
                Dim bri, bgi, bbi, bai As Integer
                Dim sLen = 0, bLen = 0
                Dim dstBuf As Byte() = Nothing
                Try
                If Not TryRentRgbaLikeBuffer(source, srcBuf, sLen, sStride, ri, gi, bi, ai) OrElse
                   Not TryRentRgbaLikeBuffer(blurred, blurBuf, bLen, bStride, bri, bgi, bbi, bai) Then
                    Return result
                End If
                dstBuf = ArrayPool(Of Byte).Shared.Rent(sLen)
                If sStride <> source.Width * 4 Then Array.Clear(dstBuf, 0, sLen)
                Dim width = source.Width
                Dim positiv = strength > 0
                Dim sv = If(positiv, strength * 0.55F, -strength * 0.55F)

                ForEachRow(width, source.Height,
                    Sub(y)
                        Dim so = y * sStride
                        Dim bo = y * bStride
                        For x = 0 To width - 1
                            Dim o = so + x * 4
                            Dim p = bo + x * 4
                            Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                            Dim br As Integer, bg As Integer, bb As Integer, ba As Integer
                            ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                            ReadUnpremultiplied(blurBuf, p, bri, bgi, bbi, bai, br, bg, bb, ba)
                            Dim nr As Byte, ng As Byte, nb As Byte
                            If positiv Then
                                nr = ClampToByte(cr + br * sv) : ng = ClampToByte(cg + bg * sv) : nb = ClampToByte(cb + bb * sv)
                            Else
                                nr = ClampToByte(cr - br * sv) : ng = ClampToByte(cg - bg * sv) : nb = ClampToByte(cb - bb * sv)
                            End If
                            WritePremultiplied(dstBuf, o, ri, gi, bi, ai, nr, ng, nb, a)
                        Next
                    End Sub)

                Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), sLen)
                Return result
                Finally
                    ReturnPooledBuffer(srcBuf)
                    ReturnPooledBuffer(blurBuf)
                    ReturnPooledBuffer(dstBuf)
                End Try
            End Using
        End Function

        ''' <summary>Staub/Kratzer, Richtung wie bei den Nachbarn im Panel (positiv = den benannten
        ''' Effekt HINZUFUEGEN): positiv streut Staubkoerner und wenige fast senkrechte Kratzer wie
        ''' auf gescanntem Film, negativ ENTFERNT Stoerungen per Medianfilter (vorher war es genau
        ''' umgekehrt und die zufaelligen Querstriche sahen nach nichts aus -).</summary>
        Private Shared Function ApplyDustScratches(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return source

            If strength < 0 Then
                ' ENTFERNEN. Der Median-Radius ist zwangslaeufig ganzzahlig - er laeuft ueber den
                ' vollen Reglerbereich, die Zwischenstufen kommen aus der Deckkraft, mit der das
                ' Medianbild ueber das Original gelegt wird (siehe Regler-Bereichs-Diagnose).
                Dim removeStrength = -strength
                Using median = ApplyMedianBlur(source, removeStrength)
                    Dim blended = CloneBitmap(source)
                    Using canvas = New SKCanvas(blended)
                        Using paint = New SKPaint With {.Color = New SKColor(255, 255, 255, ClampToByte(255.0F * removeStrength))}
                            canvas.DrawBitmap(median, 0, 0, paint)
                        End Using
                    End Using
                    Return blended
                End Using
            End If

            ' HINZUFUEGEN: ueberwiegend helle Staubkoerner (Film: Staub streut Licht) plus wenige
            ' lange, fast senkrechte, leicht gewellte Kratzer - senkrecht, weil echte Kratzer vom
            ' Filmtransport in Laufrichtung entstehen. Fester Seed: gleiches Bild -> gleiches Muster.
            Dim result = CloneBitmap(source)
            Dim random = New Random(source.Width * 997 Xor source.Height * 331)
            Using canvas = New SKCanvas(result)
                Dim speckCount = CInt(Math.Round(source.Width * source.Height / 4500.0 * strength))
                For i = 0 To speckCount - 1
                    Dim bright = random.NextDouble() < 0.72
                    Dim tone = If(bright, CByte(210 + random.Next(0, 46)), CByte(random.Next(0, 40)))
                    Dim alpha = CByte(60 + random.Next(0, CInt(70 + 90 * strength)))
                    Using paint = New SKPaint With {.Color = New SKColor(tone, tone, tone, alpha), .IsAntialias = True}
                        canvas.DrawCircle(random.Next(0, source.Width), random.Next(0, source.Height),
                                          0.5F + CSng(random.NextDouble()) * 1.1F, paint)
                    End Using
                Next

                Dim scratchCount = Math.Max(1, CInt(Math.Round(9.0 * strength)))
                For i = 0 To scratchCount - 1
                    Dim x = CSng(random.NextDouble() * source.Width)
                    Dim y = CSng(random.NextDouble() * source.Height * 0.6)
                    Dim length = CSng(source.Height * (0.15 + random.NextDouble() * 0.35))
                    Dim bright = random.NextDouble() < 0.65
                    Dim tone = If(bright, CByte(225), CByte(25))
                    Dim alpha = CByte(35 + random.Next(0, CInt(30 + 60 * strength)))
                    Using path = New SKPath()
                        path.MoveTo(x, y)
                        Dim segments = Math.Max(3, CInt(length / 14.0F))
                        For seg = 1 To segments
                            ' Leichte seitliche Wanderung - ein schnurgerader Strich wirkt kuenstlich.
                            x += CSng((random.NextDouble() - 0.5) * 2.4)
                            path.LineTo(x, y + length * seg / segments)
                        Next
                        Using paint = New SKPaint With {.Color = New SKColor(tone, tone, tone, alpha),
                                                        .Style = SKPaintStyle.Stroke, .StrokeWidth = 1.0F, .IsAntialias = True}
                            canvas.DrawPath(path, paint)
                        End Using
                    End Using
                Next
            End Using
            Return result
        End Function



        Private Shared Function ResolveFilmNegativeStats(source As SKBitmap, adj As ImageAdjustments) As (BaseColor As SKColor, DensityColor As SKColor)
            Dim hasBase = Not String.IsNullOrWhiteSpace(adj.NegativeBaseColor)
            Dim hasDensity = Not String.IsNullOrWhiteSpace(adj.NegativeDensityColor)
            If hasBase AndAlso hasDensity Then
                Return (ParseColor(adj.NegativeBaseColor, SKColors.White), ParseColor(adj.NegativeDensityColor, SKColors.Black))
            End If

            ' Normalerweise misst der Editor einmal beim Einschalten und legt die Werte in den
            ' Anpassungen ab - dann sind Vorschau und Export garantiert identisch. Kommen hier trotzdem
            ' leere Werte an (Stapelverarbeitung, wiederhergestellte Anpassungen), wird eben aus dem
            ' Bild geschätzt, das gerade vorliegt.
            Dim measured = AnalyzeFilmNegativeCore(source)
            Return (If(hasBase, ParseColor(adj.NegativeBaseColor, measured.BaseColor), measured.BaseColor),
                    If(hasDensity, ParseColor(adj.NegativeDensityColor, measured.DensityColor), measured.DensityColor))
        End Function

        ''' <summary>Schätzt Filmbasis und dichtesten Punkt eines Negativscans. Misst auf dem BESCHNITTENEN
        ''' Bild, weil der Beschnitt in der Pipeline vor der Umkehr liegt: ein weggeschnittener schwarzer
        ''' Scannerrand darf den Dichtepunkt nicht mehr bestimmen.</summary>
        Public Shared Function AnalyzeFilmNegative(source As SKBitmap, adj As ImageAdjustments) As (BaseColor As SKColor, DensityColor As SKColor)
            If source Is Nothing Then Return (SKColors.White, SKColors.Black)
            ' DIE QUELLE GEHOERT DEM AUFRUFER. Die Geometriekette entsorgt jede Zwischenstufe, die
            ' sie ersetzt - beim ersten wirksamen Schritt waere das die uebergebene Vorschau-Bitmap
            ' des Editors gewesen, die danach weiterverwendet wird. Die Owned-Fassung gibt erst ab
            ' der ersten selbst erzeugten Stufe frei.
            Dim owned = False
            Dim cropped = ApplyGeometryPipelineOwned(source, If(adj, New ImageAdjustments()), owned)
            Try
                Return AnalyzeFilmNegativeCore(cropped)
            Finally
                If owned Then cropped?.Dispose()
            End Try
        End Function

        ''' <summary>Filmbasis = das hellste Tonwertniveau je Kanal (unbelichteter Träger), dichtester Punkt
        ''' = das dunkelste. Beides als Perzentil statt als Min/Max: ein einzelnes Staubkorn oder ein Kratzer
        ''' würde sonst die gesamte Umrechnung des Bildes festlegen.</summary>
        Private Shared Function AnalyzeFilmNegativeCore(bmp As SKBitmap) As (BaseColor As SKColor, DensityColor As SKColor)
            Dim histR = New Integer(255) {}
            Dim histG = New Integer(255) {}
            Dim histB = New Integer(255) {}
            Dim total As Integer = 0

            Dim buffer As Byte() = Nothing
            Dim stride As Integer = 0
            If bmp IsNot Nothing AndAlso TryBorrowBgraBuffer(bmp, buffer, stride) Then
                ' Perzentile sind ab gut hunderttausend Stichproben stabil - jedes Pixel eines 40-MP-Scans
                ' anzufassen würde die Messung nur verlangsamen, nicht verbessern.
                Dim stepPx = Math.Max(1, CInt(Math.Sqrt(bmp.Width * CDbl(bmp.Height) / 250000.0)))
                For y As Integer = 0 To bmp.Height - 1 Step stepPx
                    Dim row = y * stride
                    For x As Integer = 0 To bmp.Width - 1 Step stepPx
                        Dim o = row + x * 4
                        If buffer(o + 3) < 8 Then Continue For
                        histB(buffer(o)) += 1
                        histG(buffer(o + 1)) += 1
                        histR(buffer(o + 2)) += 1
                        total += 1
                    Next
                Next
            End If

            If total = 0 Then Return (SKColors.White, SKColors.Black)
            Dim baseColor = New SKColor(HistogramPercentile(histR, total, 0.995), HistogramPercentile(histG, total, 0.995), HistogramPercentile(histB, total, 0.995), 255)
            Dim densityColor = New SKColor(HistogramPercentile(histR, total, 0.005), HistogramPercentile(histG, total, 0.005), HistogramPercentile(histB, total, 0.005), 255)
            Return (baseColor, densityColor)
        End Function

        ''' <summary>Kleinster Tonwert, unterhalb dessen <paramref name="fraction"/> aller gezählten Pixel liegen.</summary>
        Private Shared Function HistogramPercentile(histogram As Integer(), total As Integer, fraction As Double) As Byte
            Dim target = Math.Max(1L, Math.Min(CLng(total), CLng(Math.Round(total * fraction))))
            Dim running As Long = 0
            For i As Integer = 0 To 255
                running += histogram(i)
                If running >= target Then Return CByte(i)
            Next
            Return 255
        End Function


        ' Parst "x1,y1;x2,y2;..." zu sortierten, X-eindeutigen Stützpunkten (0..255) für die Tonwertkurve.
        ' Friend, damit die Preset-Faltung (XmpPresetService.ApplyParametricCurve) die Punktkurve
        ' mit demselben Spline auswertet wie die Engine - frueher lief die Faltung ueber eine eigene
        ' LINEARE Naeherung, die die Kurvenkruemmung verwarf.
        Friend Shared Function ParseCurvePoints(pointsCsv As String) As List(Of (X As Double, Y As Double))
            Dim result As New List(Of (X As Double, Y As Double))()
            If Not String.IsNullOrWhiteSpace(pointsCsv) Then
                For Each pair In pointsCsv.Split(";"c)
                    Dim parts = pair.Split(","c)
                    If parts.Length = 2 Then
                        Dim x As Double
                        Dim y As Double
                        If Double.TryParse(parts(0), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, x) AndAlso
                           Double.TryParse(parts(1), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, y) Then
                            result.Add((Math.Max(0.0, Math.Min(255.0, x)), Math.Max(0.0, Math.Min(255.0, y))))
                        End If
                    End If
                Next
            End If
            result = result.GroupBy(Function(p) p.X).Select(Function(g) g.First()).OrderBy(Function(p) p.X).ToList()
            If result.Count = 0 Then
                result.Add((0, 0))
                result.Add((255, 255))
            ElseIf result.Count = 1 Then
                If result(0).X > 0.0001 Then
                    result.Insert(0, (0, 0))
                Else
                    result.Add((255, 255))
                End If
            End If
            If result(0).X > 0.0001 Then result.Insert(0, (0, result(0).Y))
            If result(result.Count - 1).X < 254.9999 Then result.Add((255, result(result.Count - 1).Y))
            Return result
        End Function


        ''' <summary>Catmull-Rom durch die Kurvenpunkte. GEPRUEFT UND VERWORFEN: Adobes DNG-SDK
        ''' interpoliert Tonkurven mit einem NATUERLICHEN kubischen Spline, der an derselben
        ''' Kennlinie systematisch hoeher laeuft (Rot-Kurve des Testpresets bei x=20: 7,7 gegen
        ''' unsere 6,5). Ein A/B mit ausgetauschter Interpolation, sonst identischer Kette, ergab
        ''' auf der besseren Basis eine GROESSERE Abweichung zur Referenz (8,25 -> 9,39 am einen
        ''' Motiv, 8,08 -> 9,30 am anderen) und nur auf der heutigen Basis eine kleinere
        ''' (16,85 -> 15,93). Widerspruechlich, also nicht umgestellt - erst wenn ein Referenz-
        ''' Export OHNE Preset vorliegt, laesst sich das sauber entscheiden.</summary>
        Friend Shared Function EvaluateCurveSpline(points As List(Of (X As Double, Y As Double)), x As Double) As Double
            Dim n = points.Count
            If n = 0 Then Return x
            If x <= points(0).X Then Return points(0).Y
            If x >= points(n - 1).X Then Return points(n - 1).Y

            Dim segIndex = 0
            For i As Integer = 0 To n - 2
                If x >= points(i).X AndAlso x <= points(i + 1).X Then
                    segIndex = i
                    Exit For
                End If
            Next

            Dim p0 = If(segIndex > 0, points(segIndex - 1), points(segIndex))
            Dim p1 = points(segIndex)
            Dim p2 = points(segIndex + 1)
            Dim p3 = If(segIndex + 2 < n, points(segIndex + 2), points(segIndex + 1))

            Dim span = p2.X - p1.X
            If span <= 0.0001 Then Return p1.Y
            Dim t = (x - p1.X) / span
            Dim t2 = t * t
            Dim t3 = t2 * t

            Return 0.5 * ((2 * p1.Y) +
                          (-p0.Y + p2.Y) * t +
                          (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                          (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)
        End Function

        ' Rohe Histogramm-Zähldaten je Kanal (R/G/B/Luminanz) für den Kurven-Editor.
        Public Shared Function BuildChannelHistogramCounts(source As SKBitmap) As (R As Integer(), G As Integer(), B As Integer(), L As Integer())
            Dim r = New Integer(255) {}
            Dim g = New Integer(255) {}
            Dim b = New Integer(255) {}
            Dim l = New Integer(255) {}
            If source Is Nothing Then Return (r, g, b, l)

            Dim stepX = Math.Max(1, source.Width \ 400)
            Dim stepY = Math.Max(1, source.Height \ 400)
            For y As Integer = 0 To source.Height - 1 Step stepY
                For x As Integer = 0 To source.Width - 1 Step stepX
                    Dim c = source.GetPixel(x, y)
                    r(c.Red) += 1
                    g(c.Green) += 1
                    b(c.Blue) += 1
                    Dim lum = CInt(Math.Max(0, Math.Min(255, c.Red * 0.299 + c.Green * 0.587 + c.Blue * 0.114)))
                    l(lum) += 1
                Next
            Next
            Return (r, g, b, l)
        End Function

        Public Shared Function BuildChannelHistogramCounts(sourcePath As String) As (R As Integer(), G As Integer(), B As Integer(), L As Integer())
            Try
                Using original = DecodeHistogramSource(sourcePath)
                    Return BuildChannelHistogramCounts(original)
                End Using
            Catch
                Return (New Integer(255) {}, New Integer(255) {}, New Integer(255) {}, New Integer(255) {})
            End Try
        End Function



        ''' <summary>ANKER-Farbtöne der acht HSL-Bänder (Grad), Reihenfolge Rot..Magenta. Adobes
        ''' HSL-Modell interpoliert die Regler GLATT zwischen diesen Ankern: ein Pixel genau auf einem
        ''' Anker bekommt den vollen Bandwert, dazwischen eine weiche Mischung der beiden Nachbaranker.
        ''' Frueher lagen hier Band-GRENZEN mit flachem Kern + 10 Grad Rand-Blending - das ueberdehnte
        ''' jedes Band (voller Wert ueber den ganzen Kern), waehrend Adobe schon ab dem Anker abfaellt.
        ''' Rot ueberlaeuft den Nullpunkt: nach Magenta (315) folgt Rot (0 bzw. 360).</summary>
        Private Shared ReadOnly HslBandCenters As Double() = {0.0, 30.0, 60.0, 120.0, 180.0, 225.0, 270.0, 315.0}

        Private Shared Sub GetHslBandValues(index As Integer, adj As ImageAdjustments,
                                            ByRef hueShift As Single, ByRef satShift As Single, ByRef lumShift As Single)
            Select Case index
                Case 0 : hueShift = adj.RedHue : satShift = adj.RedSaturation : lumShift = adj.RedLuminance
                Case 1 : hueShift = adj.OrangeHue : satShift = adj.OrangeSaturation : lumShift = adj.OrangeLuminance
                Case 2 : hueShift = adj.YellowHue : satShift = adj.YellowSaturation : lumShift = adj.YellowLuminance
                Case 3 : hueShift = adj.GreenHue : satShift = adj.GreenSaturation : lumShift = adj.GreenLuminance
                Case 4 : hueShift = adj.AquaHue : satShift = adj.AquaSaturation : lumShift = adj.AquaLuminance
                Case 5 : hueShift = adj.BlueHue : satShift = adj.BlueSaturation : lumShift = adj.BlueLuminance
                Case 6 : hueShift = adj.PurpleHue : satShift = adj.PurpleSaturation : lumShift = adj.PurpleLuminance
                Case Else : hueShift = adj.MagentaHue : satShift = adj.MagentaSaturation : lumShift = adj.MagentaLuminance
            End Select
        End Sub

        ''' <summary>Die Regler des Farbbands, in dem der Farbton liegt - an den Bandgrenzen WEICH in
        ''' das Nachbarband übergeblendet.
        '''
        ''' Vorher wählte diese Funktion hart per Select Case. Das machte das Ergebnis genau auf einer
        ''' Grenze unstetig: gemessen ergaben (255,191,0) und (255,192,0) - ein Tonwert
        ''' Unterschied im Grünkanal, Farbton 44,94 gegen 45,18 - einen Sprung von 153 Tonwerten,
        ''' sobald die Nachbarbänder verschieden eingestellt waren. Im Bild war das eine sichtbare
        ''' harte Kante quer durch jeden weichen Farbverlauf, der eine Bandgrenze kreuzt (Himmel,
        ''' Hauttöne). Aufgefallen ist es, weil derselbe Farbton in Single und Double auf verschiedene
        ''' Seiten der Grenze fiel - das war aber nur der Bote, nicht die Ursache.
        '''
        ''' Auf der Grenze selbst mischen beide Bänder je zur Hälfte, der Verlauf dorthin ist
        ''' smoothstep-geglättet (Ableitung an beiden Enden null, sonst wäre die Kante nur verschoben
        ''' statt beseitigt).</summary>
        Private Shared Sub GetHslBandAdjustments(hue As Double, adj As ImageAdjustments, ByRef hueShift As Single, ByRef satShift As Single, ByRef lumShift As Single)
            Dim h = ((hue Mod 360.0) + 360.0) Mod 360.0

            ' Anker-Paar finden, zwischen dem der Farbton liegt (Kreis: nach Magenta 315 folgt Rot 360).
            Dim lo = 7
            For k = 0 To 7
                If h < HslBandCenters(k) Then
                    lo = (k + 7) Mod 8          ' voriger Anker
                    Exit For
                End If
            Next
            Dim hi = (lo + 1) Mod 8

            ' Kreis-Distanz vom unteren Anker und Spannweite zum oberen (beide ueber 360 gewickelt).
            Dim span = HslBandCenters(hi) - HslBandCenters(lo)
            If span <= 0.0 Then span += 360.0
            Dim pos = h - HslBandCenters(lo)
            If pos < 0.0 Then pos += 360.0

            ' KERN VOLL, nur um die Bandgrenze herum ueberblenden. Ohne den Kern lief die Kurve
            ' vom einen Anker glatt zum naechsten durch, und volle Wirkung hatte ein Regler NUR
            ' genau auf seinem Ankerfarbton - gemessen auf 11 Prozent des Farbkreises. Ein Laub bei
            ' Farbton 93 bekam vom Gruen-Regler 0,58, ein Himmel bei 208 vom Blau-Regler 0,69: der
            ' Regler machte dort knapp die Haelfte dessen, was draufsteht. Mit dem Kern hat jedes
            ' Band auf seinem inneren Bereich die volle Wirkung (55 Prozent des Farbkreises), die
            ' Ueberblendung passiert im mittleren Bereich zwischen zwei Ankern. Die Kernbreite ist
            ' gemessen: 0,25 / 0,30 / 0,35 brachten an echten Bildfarben 68/70/70 Prozent mehr
            ' Chroma bei Laub und 62/68/72 bei Nadelgruen - ueber 0,30 hinaus wird nur noch der
            ' Uebergang schmaler, ohne dass die Wirkung nennenswert steigt.
            '
            ' Die Ableitung bleibt an BEIDEN Enden null (smoothstep auf dem gestauchten Bereich,
            ' danach konstant) - genau darum ging es beim Umbau von den harten Grenzen weg: keine
            ' Kante quer durch einen Verlauf, der eine Bandgrenze kreuzt.
            Const kern As Double = 0.30
            Dim t = If(span > 0.0, pos / span, 0.0)
            t = (t - kern) / (1.0 - 2.0 * kern)
            If t < 0.0 Then t = 0.0 Else If t > 1.0 Then t = 1.0
            Dim w = CSng(t * t * (3.0 - 2.0 * t))

            Dim h0, s0, l0, h1, s1, l1 As Single
            GetHslBandValues(lo, adj, h0, s0, l0)
            GetHslBandValues(hi, adj, h1, s1, l1)
            hueShift = h0 + (h1 - h0) * w
            satShift = s0 + (s1 - s0) * w
            lumShift = l0 + (l1 - l0) * w
        End Sub




        ''' <summary>Die Farbmatrix eines Presets - einzige Quelle für ApplyFilterPreset UND die
        ''' verschmolzene Punktoperationskette. Nothing heißt "keine Matrix": unbekanntes Preset oder
        ''' "weich" (das ist ein Weichzeichner, kein Farbfilter).
        ''' Skia liest die 5. Matrixspalte (Offset) in der Skala 0..1, NICHT 0..255 - gemessen
        ''': Offset 0.1 auf Grau 100 ergibt 126, also +25.5 Tonwerte. Die Offsets unten
        ''' sind aber als TONWERTE gemeint. Ohne die Division waren fuenf Presets unbrauchbar:
        ''' "Fade"/"Vintage" lieferten reines Weiss, "Kontrast" reines Schwarz, "Warm"/"Kuehl"
        ''' knallorange bzw. knallblau. Die Zahlen bleiben in Tonwerten lesbar, geteilt wird hier.
        ''' </summary>
        Friend Shared Function BuildFilterPresetMatrix(preset As String) As Single()
            Dim matrix As Single() = Nothing
            Select Case preset.Trim().ToLowerInvariant()
                Case "s/w", "schwarzweiss", "schwarzweiß"
                    matrix = New Single() {
                        0.299F, 0.587F, 0.114F, 0, 0,
                        0.299F, 0.587F, 0.114F, 0, 0,
                        0.299F, 0.587F, 0.114F, 0, 0,
                        0, 0, 0, 1, 0
                    }
                Case "warm"
                    ' Werte angezogen: bei 50 % Standardstaerke war der Look zuvor
                    ' praktisch unsichtbar (Kanalshift 12) - siehe Kommentar an DefaultFilterStrength.
                    matrix = New Single() {
                        1.12F, 0.02F, 0, 0, 8.0F / 255.0F,
                        0, 1.03F, 0, 0, 2.0F / 255.0F,
                        0, 0, 0.88F, 0, -10.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "kühl", "kuehl"
                    matrix = New Single() {
                        0.92F, 0, 0, 0, -4.0F / 255.0F,
                        0, 1.01F, 0, 0, 0,
                        0, 0, 1.1F, 0, 8.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "fade"
                    ' Werte angezogen (vorher Kanalshift 10, unsichtbar): ein "Fade"
                    ' lebt vom angehobenen Schwarzpunkt - Koeffizienten runter, Offset deutlich rauf.
                    matrix = New Single() {
                        0.80F, 0.04F, 0.04F, 0, 30.0F / 255.0F,
                        0.04F, 0.80F, 0.04F, 0, 30.0F / 255.0F,
                        0.04F, 0.04F, 0.82F, 0, 34.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "kontrast"
                    matrix = New Single() {
                        1.16F, 0, 0, 0, -18.0F / 255.0F,
                        0, 1.16F, 0, 0, -18.0F / 255.0F,
                        0, 0, 1.16F, 0, -18.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "sepia"
                    matrix = New Single() {
                        0.393F, 0.769F, 0.189F, 0, 0,
                        0.349F, 0.686F, 0.168F, 0, 0,
                        0.272F, 0.534F, 0.131F, 0, 0,
                        0, 0, 0, 1, 0
                    }
                Case "matt"
                    ' Werte angezogen (vorher Kanalshift 15). Matt = flacher Kontrast mit
                    ' leicht warmem Grundton, deutlicher abgesetzt von "Fade" (neutral).
                    matrix = New Single() {
                        0.84F, 0.06F, 0.04F, 0, 26.0F / 255.0F,
                        0.04F, 0.84F, 0.04F, 0, 24.0F / 255.0F,
                        0.04F, 0.06F, 0.80F, 0, 20.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "cross"
                    ' Werte angezogen (vorher Kanalshift 15). Kreuzentwicklung lebt von
                    ' GEGENLAEUFIGEN Kanaelen: Lichter ins Gruengelbe, Schatten ins Blaue.
                    matrix = New Single() {
                        1.22F, 0, 0, 0, -16.0F / 255.0F,
                        0, 1.06F, 0, 0, 6.0F / 255.0F,
                        0, 0, 0.82F, 0, 26.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "dramatisch"
                    matrix = New Single() {
                        1.24F, 0, 0, 0, -28.0F / 255.0F,
                        0, 1.18F, 0, 0, -24.0F / 255.0F,
                        0, 0, 1.12F, 0, -18.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "weich"
                    ' Echte räumliche Unschärfe statt nur einer Farbmatrix - eine Farbmatrix wirkt pro
                    ' Pixel und kann strukturell nicht weichzeichnen. Steht nur im selben Select Case,
                    ' ist aber KEIN Farbfilter: der Aufrufer fängt es vor der Matrix ab.
                    Return Nothing
                Case "noir"
                    matrix = New Single() {
                        0.404F, 0.792F, 0.154F, 0, -38.0F / 255.0F,
                        0.404F, 0.792F, 0.154F, 0, -38.0F / 255.0F,
                        0.404F, 0.792F, 0.154F, 0, -38.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "duoton", "duotone"
                    ' Echter Zweifarb-Verlauf über die Luminanz: dunkles Indigo (Schatten) zu sattem Orange (Lichter).
                    ' Jeder Pixel landet exakt auf der Verlaufslinie zwischen den beiden Zielfarben - unabhängig
                    ' vom Ausgangston. Endpunkte sind bewusst kräftig/gesättigt, nicht Richtung Weiß.
                    matrix = New Single() {
                        0.2815F, 0.5525F, 0.1073F, 0, 15.0F / 255.0F,
                        0.1759F, 0.3453F, 0.0671F, 0, 15.0F / 255.0F,
                        -0.0235F, -0.0460F, -0.0089F, 0, 60.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "polaroid"
                    ' Kräftiger warmer Gelbstich mit moderat angehobenen Schwarzwerten (Sofortbild-Charakter),
                    ' Kontrast bleibt weitgehend erhalten statt komplett verwaschen wie bei "Fade".
                    matrix = New Single() {
                        0.95F, 0.15F, -0.05F, 0, 10.0F / 255.0F,
                        0.05F, 0.90F, 0.05F, 0, 6.0F / 255.0F,
                        -0.05F, 0.05F, 0.75F, 0, 2.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "vhs"
                    ' Deutlich kühlerer Cyan-/Blaustich mit sichtbarem Kanal-Bluten, typisch für Analogvideo.
                    matrix = New Single() {
                        0.70F, 0.15F, 0.15F, 0, 0,
                        0.10F, 0.85F, 0.15F, 0, 4.0F / 255.0F,
                        0.05F, 0.20F, 0.85F, 0, 10.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "bild auf alt", "alt", "antik", "vintage"
                    matrix = New Single() {
                        0.78F, 0.26F, 0.08F, 0, 18.0F / 255.0F,
                        0.18F, 0.74F, 0.10F, 0, 10.0F / 255.0F,
                        0.06F, 0.18F, 0.62F, 0, 2.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case Else
                    Return Nothing
            End Select
            Return matrix
        End Function

        ''' Blendet eine leicht gaußgeweichzeichnete Kopie über das scharfe Original - der Radius
        ''' bleibt bewusst klein/fest ("leicht"), die Stärke steuert nur die Überblend-Deckkraft.
        Private Shared Function ApplySoftFocusBlur(source As SKBitmap, strength As Single) As SKBitmap
            strength = Clamp(strength, 0, 1)
            If strength <= 0 Then Return source
            Using blurred = ApplyNoiseReduction(source, 0.3F)
                Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
                Using canvas = New SKCanvas(result)
                    canvas.DrawBitmap(source, 0, 0)
                    Using paint = New SKPaint With {.Color = New SKColor(255, 255, 255, ClampToByte(255 * strength))}
                        canvas.DrawBitmap(blurred, 0, 0, paint)
                    End Using
                End Using
                Return result
            End Using
        End Function

        Private Class Lut3DData
            Public Property Size As Integer
            ''' Flach abgelegt, R am schnellsten laufend (Standard-.cube-Reihenfolge): Index = (b*Size*Size + g*Size + r)*3 + Kanal.
            Public Property Table As Single()
        End Class

        Private Shared ReadOnly _cubeLutCache As New Dictionary(Of String, Lut3DData)(StringComparer.OrdinalIgnoreCase)
        Private Shared ReadOnly _cubeLutCacheLock As New Object()

        ''' Lädt und parst eine .cube-3D-LUT-Datei (nur LUT_3D_SIZE wird unterstützt, kein LUT_1D_SIZE,
        ''' Domain wird als 0..1 angenommen). Ergebnis wird pro Dateipfad gecacht, da eine LUT beim
        ''' Ziehen an einem Stärke-Regler sonst bei jedem Preview-Frame neu von der Platte geparst würde.
        ''' Speicher- (SaveImage) und Vorschau-Rendering (ApplyAdjustments) können diese Methode
        ''' gleichzeitig von verschiedenen Threads aufrufen, daher SyncLock um das Dictionary.
        '''
        ''' GESPERRT WIRD NUR DAS NACHSCHLAGEN UND DAS EINTRAGEN, nicht das Parsen. Vorher lag die
        ''' Datei-Arbeit innerhalb der Sperre: der erste Zugriff auf eine LUT hielt damit JEDEN
        ''' anderen Renderfaden an, solange die Datei gelesen und zerlegt wurde - bei einer
        ''' 64er-LUT sind das über 780 000 Zahlen. Draussen geparst kann es passieren, dass zwei
        ''' Faeden dieselbe Datei gleichzeitig lesen; das kostet einmal doppelte Arbeit und ist
        ''' ungefaehrlich, weil beide dasselbe Ergebnis haben und der Erste im Cache gewinnt.
        Private Shared Function LoadCubeLut(path As String) As Lut3DData
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return Nothing

            SyncLock _cubeLutCacheLock
                Dim cached As Lut3DData = Nothing
                If _cubeLutCache.TryGetValue(path, cached) Then Return cached
            End SyncLock

            ' Ausserhalb der Sperre: hier liegt die ganze Datei-Arbeit.
            Dim parsed = ParseCubeLut(path)

            SyncLock _cubeLutCacheLock
                ' Zweite Frage unter der Sperre: war ein anderer Faden schneller, gewinnt sein
                ' Eintrag. Sonst stuenden zwei gleichwertige Tabellen im Speicher, und welche ein
                ' Aufrufer bekommt, haenge am Zufall.
                Dim winner As Lut3DData = Nothing
                If _cubeLutCache.TryGetValue(path, winner) Then Return winner
                _cubeLutCache(path) = parsed
                Return parsed
            End SyncLock
        End Function

        ''' <summary>Die reine Datei-Arbeit, OHNE Cache und ohne Sperre - genau deshalb darf sie
        ''' ausserhalb des SyncLock laufen. Nothing heisst "nicht brauchbar" (fehlende Groesse,
        ''' falsche Anzahl Werte, unlesbare Datei); was damit geschieht, entscheidet der
        ''' Aufrufer.</summary>
        Private Shared Function ParseCubeLut(path As String) As Lut3DData
            Dim size As Integer = 0
            Dim values As New List(Of Single)()
            Try
                For Each rawLine In File.ReadLines(path)
                    Dim line = rawLine.Trim()
                    If line.Length = 0 OrElse line.StartsWith("#") Then Continue For
                    If line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase) Then
                        Dim parts = line.Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                        If parts.Length >= 2 Then Integer.TryParse(parts(1), Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, size)
                        Continue For
                    End If
                    If Not Char.IsDigit(line(0)) AndAlso line(0) <> "-"c AndAlso line(0) <> "+"c AndAlso line(0) <> "."c Then Continue For

                    Dim comps = line.Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                    If comps.Length < 3 Then Continue For
                    Dim r, g, b As Single
                    If Single.TryParse(comps(0), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, r) AndAlso
                       Single.TryParse(comps(1), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, g) AndAlso
                       Single.TryParse(comps(2), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, b) Then
                        values.Add(r)
                        values.Add(g)
                        values.Add(b)
                    End If
                Next
            Catch
                ' Unlesbare Datei: wie ein Parse-Fehler behandeln (negativ cachen, s.u.).
                size = 0
            End Try

            If size < 2 OrElse values.Count <> size * size * size * 3 Then
                ' Nothing heisst hier "unbrauchbar", und der Aufrufer legt genau das in den Cache.
                ' Das NEGATIVE Merken ist wichtig: ohne Eintrag wurde eine defekte .cube-Datei beim
                ' Ziehen am LUT-Staerke-Regler mit JEDEM Vorschau-Frame neu geparst. Es gilt (wie
                ' der Positiv-Cache) bis zum Programmende - eine extern reparierte Datei braucht
                ' einen Neustart bzw. eine Neu-Auswahl.
                Return Nothing
            End If

            Return New Lut3DData With {.Size = size, .Table = values.ToArray()}
        End Function

        Private Shared Function LutChannel(table As Single(), size As Integer, r As Integer, g As Integer, b As Integer, channel As Integer) As Single
            Return table((b * size * size + g * size + r) * 3 + channel)
        End Function

        Private Shared Function TrilinearChannel(table As Single(), size As Integer, r0 As Integer, r1 As Integer, g0 As Integer, g1 As Integer, b0 As Integer, b1 As Integer, rt As Single, gt As Single, bt As Single, channel As Integer) As Single
            Dim c000 = LutChannel(table, size, r0, g0, b0, channel)
            Dim c100 = LutChannel(table, size, r1, g0, b0, channel)
            Dim c010 = LutChannel(table, size, r0, g1, b0, channel)
            Dim c110 = LutChannel(table, size, r1, g1, b0, channel)
            Dim c001 = LutChannel(table, size, r0, g0, b1, channel)
            Dim c101 = LutChannel(table, size, r1, g0, b1, channel)
            Dim c011 = LutChannel(table, size, r0, g1, b1, channel)
            Dim c111 = LutChannel(table, size, r1, g1, b1, channel)

            Dim c00 = c000 + (c100 - c000) * rt
            Dim c10 = c010 + (c110 - c010) * rt
            Dim c01 = c001 + (c101 - c001) * rt
            Dim c11 = c011 + (c111 - c011) * rt
            Dim c0 = c00 + (c10 - c00) * gt
            Dim c1 = c01 + (c11 - c01) * gt
            Return c0 + (c1 - c0) * bt
        End Function



        ''' <summary>Wendet den gewählten Vignetten-Stil auf ein abzudunkelndes Pixel an. ColorPriority
        ''' ist bitgenau das frühere multiplikative Abdunkeln (Farbton bleibt). HighlightPriority schont
        ''' helle Bereiche (Lichter bleiben stehen), PaintOverlay dunkelt ab UND entsättigt zu einem
        ''' flachen, grauen Verlauf. mix ist 0..~1 (Stärke am Pixel).</summary>
        Private Shared Sub VignetteDarken(rr As Single, gg As Single, bb As Single, mix As Single, style As VignetteStyle,
                                          ByRef outR As Byte, ByRef outG As Byte, ByRef outB As Byte)
            Select Case style
                Case VignetteStyle.HighlightPriority
                    ' Helle Pixel weniger abdunkeln: der Schutz wächst mit dem Quadrat der Luminanz.
                    Dim luma = (0.299F * rr + 0.587F * gg + 0.114F * bb) / 255.0F
                    Dim factor = 1.0F - mix * (1.0F - luma * luma * 0.7F)
                    outR = ClampToByte(rr * factor)
                    outG = ClampToByte(gg * factor)
                    outB = ClampToByte(bb * factor)
                Case VignetteStyle.PaintOverlay
                    ' Erst zur Luminanz hin entsättigen, dann flach abdunkeln - grauer Wasch-Look.
                    Dim luma = 0.299F * rr + 0.587F * gg + 0.114F * bb
                    Dim desat = mix * 0.5F
                    Dim dark = 1.0F - mix
                    outR = ClampToByte((rr + (luma - rr) * desat) * dark)
                    outG = ClampToByte((gg + (luma - gg) * desat) * dark)
                    outB = ClampToByte((bb + (luma - bb) * desat) * dark)
                Case Else ' ColorPriority: bisheriges Verhalten
                    outR = ClampToByte(rr * (1 - mix))
                    outG = ClampToByte(gg * (1 - mix))
                    outB = ClampToByte(bb * (1 - mix))
            End Select
        End Sub

        Private Shared Function ApplyVignette(source As SKBitmap, amount As Single, transition As Single, roundness As Single, feather As Single, centerXPercent As Single, centerYPercent As Single, style As VignetteStyle) As SKBitmap
            ''' Obergrenze 1.5 statt 1 - der Stärke-Regler in EffectsPanel.axaml geht bis ±150, was hier
            ''' zu amount=±1.5 wird (amount/100 im Aufrufer). Bei Clamp(...,0,1) hatte das letzte Drittel
            ''' des Reglerwegs (100..150) keinerlei sichtbaren Effekt mehr (toter Reglerbereich).
            Dim strength = Clamp(Math.Abs(amount), 0, 1.5)
            If strength <= 0 Then Return source

            Dim result = CloneBitmap(source)

            Dim cx = source.Width * Clamp(centerXPercent, 0, 100) / 100.0F
            Dim cy = source.Height * Clamp(centerYPercent, 0, 100) / 100.0F
            Dim roundedness = Clamp(roundness, -100, 100) / 100.0F
            Dim radiusX = source.Width * (0.52F - Math.Min(0, roundedness) * 0.18F)
            Dim radiusY = source.Height * (0.52F + Math.Max(0, roundedness) * 0.18F)
            Dim inner = 0.2F + Clamp(transition, 0, 100) / 100.0F * 0.55F
            Dim softness = 0.04F + Clamp(feather, 0, 100) / 100.0F * 0.42F
            Dim edgeAlpha = If(amount > 0, 255.0F, 220.0F) * strength
            Dim darken = amount > 0

            Dim srcBuf As Byte() = Nothing
            Dim stride As Integer = 0
            If TryBorrowBgraBuffer(result, srcBuf, stride) Then
                Dim dstBuf = CType(srcBuf.Clone(), Byte())
                ForEachRow(result.Width, result.Height, Sub(y)
                                                            Dim rowOffset = y * stride
                                                            For x = 0 To result.Width - 1
                                                                Dim dx = (x - cx) / Math.Max(1.0F, radiusX)
                                                                Dim dy = (y - cy) / Math.Max(1.0F, radiusY)
                                                                Dim distance = CSng(Math.Sqrt(dx * dx + dy * dy))
                                                                Dim t = Clamp((distance - inner) / softness, 0, 1)
                                                                If t <= 0 Then Continue For
                                                                t = t * t * (3.0F - 2.0F * t)

                                                                Dim o = rowOffset + x * 4
                                                                Dim bB = srcBuf(o)
                                                                Dim gG = srcBuf(o + 1)
                                                                Dim rR = srcBuf(o + 2)
                                                                Dim mix = t * edgeAlpha / 255.0F
                                                                If darken Then
                                                                    VignetteDarken(rR, gG, bB, mix, style, dstBuf(o + 2), dstBuf(o + 1), dstBuf(o))
                                                                Else
                                                                    ' Aufhellen "v + (255-v)*mix" gilt fuer STRAIGHT-Werte. Auf den
                                                                    ' PREMUL-Bytes gerechnet entstand bei Teiltransparenz (Radierer-
                                                                    ' Loecher) Farbe > Alpha - ungueltiges Premul, zu starke
                                                                    ' Aufhellung, und der GetPixel-Fallback unten rechnete anders
                                                                    '. Der Zielweiss-Wert im Premul-Raum ist
                                                                    ' schlicht das Alpha selbst: v + (a-v)*mix. Bei a=255 bitgleich
                                                                    ' zur alten Formel; Abdunkeln (Multiplikation) ist premul-korrekt.
                                                                    Dim aA = CSng(srcBuf(o + 3))
                                                                    dstBuf(o) = ClampToByte(bB + (aA - bB) * mix)
                                                                    dstBuf(o + 1) = ClampToByte(gG + (aA - gG) * mix)
                                                                    dstBuf(o + 2) = ClampToByte(rR + (aA - rR) * mix)
                                                                End If
                                                            Next
                                                        End Sub)
                CommitBgraBuffer(result, dstBuf)
                Return result
            End If

            For y = 0 To result.Height - 1
                For x = 0 To result.Width - 1
                    Dim dx = (x - cx) / Math.Max(1.0F, radiusX)
                    Dim dy = (y - cy) / Math.Max(1.0F, radiusY)
                    Dim distance = CSng(Math.Sqrt(dx * dx + dy * dy))
                    Dim t = Clamp((distance - inner) / softness, 0, 1)
                    If t <= 0 Then Continue For
                    t = t * t * (3.0F - 2.0F * t)

                    Dim c = result.GetPixel(x, y)
                    Dim mix = t * edgeAlpha / 255.0F
                    If darken Then
                        Dim vr As Byte, vg As Byte, vb As Byte
                        VignetteDarken(c.Red, c.Green, c.Blue, mix, style, vr, vg, vb)
                        result.SetPixel(x, y, New SKColor(vr, vg, vb, c.Alpha))
                    Else
                        result.SetPixel(x, y, New SKColor(ClampToByte(c.Red + (255 - c.Red) * mix),
                                                          ClampToByte(c.Green + (255 - c.Green) * mix),
                                                          ClampToByte(c.Blue + (255 - c.Blue) * mix),
                                                          c.Alpha))
                    End If
                Next
            Next
            Return result
        End Function

        ''' <summary>Koernung. Von GetPixel/SetPixel auf Puffer umgestellt (gemessen
        ''' 4,3 s bei 6,3 MP).
        ''' BEWUSST SERIELL: der Zufallsstrom haengt an der Durchlaufreihenfolge. Parallel wuerde das
        ''' Korn bei jedem Lauf anders fallen - wiederholte Laeufe muessen bitgleich sein, und
        ''' ein Bild, das sich beim zweiten Rendern aendert, waere auch fuer den Nutzer falsch.
        ''' Der Gewinn kommt allein aus dem Wegfall des P/Invoke je Pixel.</summary>
        ''' <summary>Körnung. Ohne Größe, Rauheit und Farbe (alle 0) das bisherige feine 1-px-Korn,
        ''' bitgenau unverändert; sonst zellenweise gröber (Größe), optional fleckig (Rauheit) und
        ''' optional farbig (Farbe).</summary>
        Private Shared Function ApplyGrain(source As SKBitmap, amount As Single, Optional sizeAmount As Single = 0,
                                           Optional freqAmount As Single = 0, Optional colorAmount As Single = 0) As SKBitmap
            If sizeAmount <= 0 AndAlso freqAmount <= 0 AndAlso colorAmount <= 0 Then Return ApplyGrainFine(source, amount)
            Return ApplyGrainTextured(source, amount, sizeAmount, freqAmount, colorAmount)
        End Function

        ''' <summary>Streuwert einer Zelle je Kanal, -1..1. Bewusst KEIN Zug aus dem Zufallsstrom der
        ''' Körnung: der bleibt damit Bit für Bit unberührt, das Aufziehen des Farbreglers lässt das
        ''' Kornmuster also stehen und ändert nur die Kanaldrift. Zweiter Grund ist der Speicher, ein
        ''' zweites Feld in Bildgröße wären bei 45 Megapixeln drei Puffer zu je 360 MB.
        ''' Gerechnet wird in ULong und nach jedem Schritt auf 32 Bit maskiert: VB prüft Überläufe,
        ''' und die Zwischenwerte bleiben so unter ULong.MaxValue.</summary>
        ''' <summary>Der Streuwert des Korns an einer Stelle, -1..1 - als reine Funktion von Ort und
        ''' Startwert, nicht als Zug aus einem Strom.
        '''
        ''' <para>WARUM DAS DEN STROM ABLOEST: die Koernung war mit 345 ms die teuerste Stufe der
        ''' ganzen Pixelkette, ein knappes Drittel (Messung an 3840x2564, Patricks Protokoll vom
        ''' 2026-08-28). Sie stand ganz am Ende, also zahlte JEDER Regler sie mit - auch die
        ''' Belichtung. Der Grund war nicht die Rechnung, sondern die Reihenfolge: ein
        ''' <c>Random</c>-Strom laesst sich nicht vorspulen, also musste die Schleife seriell
        ''' bleiben. Ein Ortshash haengt an gar nichts und laeuft zeilenparallel.</para>
        '''
        ''' <para>DER PREIS IST EIN ANDERES KORNMUSTER. Gleich stark und gleich fein, aber nicht
        ''' dasselbe Korn wie vorher - gespeicherte Rezepte mit Koernung sehen danach anders aus.
        ''' Bewusst so entschieden (Patrick am 2026-08-28).</para>
        '''
        ''' <para>Der Startwert kommt wie vorher aus der Bildgroesse, damit zwei verschiedene
        ''' Groessen nicht dasselbe Muster tragen.</para></summary>
        Private Shared Function GrainNoiseAt(index As Integer, seed As Integer) As Double
            Dim h As ULong = (CULng(CUInt(index)) * 2654435761UL + CULng(CUInt(seed)) * 40503UL) And &HFFFFFFFFUL
            h = ((h Xor (h >> 15)) * 2246822519UL) And &HFFFFFFFFUL
            h = ((h Xor (h >> 13)) * 3266489917UL) And &HFFFFFFFFUL
            h = h Xor (h >> 16)
            Return CDbl(h) / 2147483647.5 - 1.0
        End Function

        Private Shared Function CellChannelNoise(cellIndex As Integer, channel As Integer) As Double
            Dim h As ULong = (CULng(cellIndex) * 2654435761UL + CULng(channel) * 2246822519UL) And &HFFFFFFFFUL
            h = ((h Xor (h >> 15)) * 2246822519UL) And &HFFFFFFFFUL
            h = ((h Xor (h >> 13)) * 3266489917UL) And &HFFFFFFFFUL
            h = h Xor (h >> 16)
            Return CDbl(h) / 2147483647.5 - 1.0
        End Function

        Private Shared Function ApplyGrainFine(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return source

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing, dstBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            Dim sLen = 0
            Try
            If Not TryRentRgbaLikeBuffer(source, srcBuf, sLen, stride, ri, gi, bi, ai) Then Return result
            dstBuf = ArrayPool(Of Byte).Shared.Rent(sLen)
            If stride <> source.Width * 4 Then Array.Clear(dstBuf, 0, sLen)

            Dim seed = source.Width * 397 Xor source.Height * 151
            Dim amplitude = 8.0 + strength * 34.0
            Dim w = source.Width, h = source.Height

            ' Zeilenparallel: jede Zeile schreibt nur in ihre eigenen Bytes und liest sonst nichts,
            ' was sich aendert. Der Streuwert kommt aus dem Ort (GrainNoiseAt), nicht aus einem
            ' Strom - deshalb ist das Ergebnis unabhaengig von der Aufteilung.
            ForEachRow(w, h,
                Sub(y As Integer)
                    Dim rowOffset = y * stride
                    Dim rowBase = y * w
                    For x As Integer = 0 To w - 1
                        Dim o = rowOffset + x * 4
                        Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                        ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                        Dim noise = GrainNoiseAt(rowBase + x, seed) * amplitude
                        WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                                           ClampToByte(cr + noise), ClampToByte(cg + noise), ClampToByte(cb + noise), a)
                    Next
                End Sub)

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), sLen)
            Return result
            Finally
                ReturnPooledBuffer(srcBuf)
                ReturnPooledBuffer(dstBuf)
            End Try
        End Function

        ''' <summary>Gröberes/unregelmäßigeres Korn. Größe = Zellkantenlänge (Pixel einer Zelle teilen
        ''' sich denselben Rauschwert). Frequenz = UNREGELMÄSSIGKEIT: eine grobe, niederfrequente
        ''' Amplituden-Modulation, die das Korn fleckig macht (manche Bereiche stärker, manche schwächer)
        ''' - sichtbar bei JEDER Größe, auch 0, und unabhängig von der Korn-Skala. BEWUSST SERIELL wie
        ''' <see cref="ApplyGrainFine"/>: erst das Zellraster, dann das grobe Modulationsraster - der
        ''' Zufallsstrom hängt an der Reihenfolge, damit Vorschau und Backen bitgleich bleiben.
        ''' Farbe = wie weit die drei Kanäle auseinanderdriften: 0 legt denselben Wert auf R, G und B
        ''' (monochromes Korn), höher mischt je Zelle eine eigene Kanalabweichung dazu und erzeugt
        ''' farbige Speckles. Bei Größe 0 und Rauheit 0 rechnet diese Fassung bitgleich mit
        ''' <see cref="ApplyGrainFine"/> (Zellkante 1, dieselbe Zugreihenfolge) - deshalb darf allein
        ''' die Farbe hierher umlenken, ohne dass sich das Kornmuster ändert.</summary>
        Private Shared Function ApplyGrainTextured(source As SKBitmap, amount As Single, sizeAmount As Single,
                                                   freqAmount As Single, colorAmount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return source

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing, dstBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            Dim sLen = 0
            Try
            If Not TryRentRgbaLikeBuffer(source, srcBuf, sLen, stride, ri, gi, bi, ai) Then Return result
            dstBuf = ArrayPool(Of Byte).Shared.Rent(sLen)
            If stride <> source.Width * 4 Then Array.Clear(dstBuf, 0, sLen)

            Dim w = source.Width, h = source.Height
            Dim cell = 1 + CInt(Math.Round(Clamp(sizeAmount, 0, 1) * 5))   ' 1..6 px
            Dim freq = Clamp(freqAmount, 0, 1)
            Dim colorMix = CDbl(Clamp(colorAmount, 0, 1))
            ' Mischen zweier unabhaengiger Gleichverteilungen mit (1-k) und k ergibt die Varianz
            ' (1-k)^2 + k^2: ohne Gegenrechnung waere das Korn in der Reglermitte um rund 30 Prozent
            ' schwaecher als an beiden Enden. Der Kehrwert der Wurzel haelt die Staerke konstant.
            Dim colorNorm = If(colorMix <= 0, 1.0,
                               1.0 / Math.Sqrt((1.0 - colorMix) * (1.0 - colorMix) + colorMix * colorMix))
            Dim amplitude = 8.0 + strength * 34.0
            Dim seed = w * 397 Xor h * 151

            ' KEINE VORAB GEZOGENEN FELDER MEHR. Zell- und Modulationsrauschen kommen aus dem Ort
            ' (GrainNoiseAt) statt aus einem Strom. Das kostet zweierlei nicht mehr:
            '
            ' Erstens die Reihenfolge - der Strom zwang die Schleife seriell, jetzt laeuft sie
            ' zeilenparallel. Zweitens den Speicher: bei Korngroesse 0 ist die Zellkante 1, das
            ' Zellfeld hatte also ein Element JE BILDPUNKT - bei 45 Megapixeln 360 MB, genau der
            ' Posten, den der Kommentar an CellChannelNoise vermeiden wollte.
            '
            ' Das Kornmuster aendert sich dadurch einmalig, siehe GrainNoiseAt. Die Zusage
            ' "Groesse 0 und Rauheit 0 rechnet bitgleich mit ApplyGrainFine" gilt weiterhin: dort
            ' ist die Zellkante 1, der Zellindex also y*w+x - genau der Index, den die feine
            ' Fassung benutzt.
            Dim gridW = (w + cell - 1) \ cell

            Const ModCell As Integer = 16
            Dim modW = (w + ModCell - 1) \ ModCell

            ForEachRow(w, h,
                Sub(y As Integer)
                    Dim rowOffset = y * stride
                    Dim gy = y \ cell
                    Dim my = y \ ModCell
                    ' Die drei Kanalabweichungen gelten je ZELLE, nicht je Bildpunkt - sonst zerfiele
                    ' ein grobes Korn farblich wieder in Einzelpunkte. Der Merker gilt jetzt JE ZEILE
                    ' statt ueber die Zeilen hinweg; weil CellChannelNoise eine reine Funktion des
                    ' Zellindex ist, kommen dabei dieselben Werte heraus.
                    Dim lastCellIndex As Integer = -1
                    Dim devR As Double = 0, devG As Double = 0, devB As Double = 0
                    For x As Integer = 0 To w - 1
                        Dim o = rowOffset + x * 4
                        Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                        ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                        Dim cellIndex = gy * gridW + (x \ cell)
                        Dim n = GrainNoiseAt(cellIndex, seed)
                        Dim amp = amplitude
                        If freq > 0 Then
                            ' 0..1 -> -1..1, mit K=0.9 skaliert: Faktor in [1-0.9*freq, 1+0.9*freq], nie <= 0.
                            ' Eigener Startwert, damit die Modulation nicht mit dem Korn gleichlaeuft.
                            Dim m = GrainNoiseAt(my * modW + (x \ ModCell), seed Xor &H5BF03635)
                            amp = amplitude * (1.0 + freq * m * 0.9)
                        End If
                        If colorMix <= 0 Then
                            Dim noise = n * amp
                            WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                                               ClampToByte(cr + noise), ClampToByte(cg + noise), ClampToByte(cb + noise), a)
                        Else
                            If cellIndex <> lastCellIndex Then
                                devR = CellChannelNoise(cellIndex, 1)
                                devG = CellChannelNoise(cellIndex, 2)
                                devB = CellChannelNoise(cellIndex, 3)
                                lastCellIndex = cellIndex
                            End If
                            Dim mono = (1.0 - colorMix) * n
                            Dim scale = amp * colorNorm
                            WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                                               ClampToByte(cr + (mono + colorMix * devR) * scale),
                                               ClampToByte(cg + (mono + colorMix * devG) * scale),
                                               ClampToByte(cb + (mono + colorMix * devB) * scale), a)
                        End If
                    Next
                End Sub)

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), sLen)
            Return result
            Finally
                ReturnPooledBuffer(srcBuf)
                ReturnPooledBuffer(dstBuf)
            End Try
        End Function

        ''' <summary>Rauschen hinzufuegen. Von GetPixel/SetPixel auf Puffer umgestellt.
        ''' Seriell aus demselben Grund wie ApplyGrain: der Zufallsstrom haengt an der Reihenfolge.</summary>
        Private Shared Function ApplyAddNoise(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return source
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            Dim sLen = 0
            Dim dstBuf As Byte() = Nothing
            Try
            If Not TryRentRgbaLikeBuffer(source, srcBuf, sLen, stride, ri, gi, bi, ai) Then Return result
            dstBuf = ArrayPool(Of Byte).Shared.Rent(sLen)
            If stride <> source.Width * 4 Then Array.Clear(dstBuf, 0, sLen)

            Dim random = New Random(source.Width * 541 Xor source.Height * 877)
            Dim amplitude = strength * 72.0

            For y As Integer = 0 To source.Height - 1
                Dim rowOffset = y * stride
                For x As Integer = 0 To source.Width - 1
                    Dim o = rowOffset + x * 4
                    Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                    ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                    ' Digitales Rauschen ist chromatisch: pro Kanal ein eigener Zufallswert, damit
                    ' farbige Sensor-Speckles entstehen. Das unterscheidet "Rauschen" klar von der
                    ' monochromen "Koernung" (ApplyGrain), die denselben Wert auf alle Kanaele legt.
                    Dim noiseR = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    Dim noiseG = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    Dim noiseB = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                                       ClampToByte(cr + noiseR), ClampToByte(cg + noiseG), ClampToByte(cb + noiseB), a)
                Next
            Next

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), sLen)
            Return result
            Finally
                ReturnPooledBuffer(srcBuf)
                ReturnPooledBuffer(dstBuf)
            End Try
        End Function

    End Class

End Namespace
