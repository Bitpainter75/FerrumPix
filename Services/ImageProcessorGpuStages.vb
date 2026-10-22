Imports System
Imports SkiaSharp

Namespace Services

    ' Die raeumlichen Stufen der Pixelkette (Schaerfe, Rauschen, Klarheit ...) auf der Grafikkarte.
    '
    ' DAS BILD BLEIBT AUF DER KARTE: ApplyDetailAdjustmentStagesCore haelt eine GpuDetailSession.
    ' Die erste Stufe, die die Karte kann, laedt das Bild hoch; jede weitere rechnet auf der Textur
    ' der vorigen. Zurueckgelesen wird erst vor einer Stufe, die nur der Prozessor kann, und am Ende.
    ' Hin und zurueck kosten bei 20 MP zusammen 50 bis 100 ms; je Stufe einzeln waere der Gewinn weg.
    '
    ' JEDE STUFE RECHNET WIE IHR PROZESSOR-GEGENSTUECK, auf Bytes: Lesen wie ReadUnpremultiplied
    ' (ganzzahlig geteilt), Runden wie ClampToByte (Math.Round, zur geraden Zahl), Schreiben wie
    ' WritePremultiplied. Zwischen den Stufen liegt wie auf dem Prozessor ein 8-Bit-Bild. Was die
    ' Karte noch nicht kann, liefert Nothing statt eines Shaders, und die Stufe laeuft wie bisher.
    Partial Public Class ImageProcessor

        ''' <summary>Ein Bild auf der Grafikkarte, das durch mehrere Stufen geht. Alle GPU-Objekte
        ''' werden nur auf dem GPU-Faden angefasst (GpuImageService.TryRun) und dort freigegeben.</summary>
        Private NotInheritable Class GpuDetailSession
            Implements IDisposable

            Private _surface As SKSurface
            Private _info As SKImageInfo
            Private ReadOnly _raster As Boolean

            ''' <summary>Die Stufen, die seit dem Hochladen auf der Karte liefen, mit ihrem
            ''' Prozessor-Gegenstueck. Das Prozessorbild steht solange auf dem Stand VOR ihnen;
            ''' scheitert das Zurueckholen, werden sie damit nachgerechnet, statt still zu fehlen.</summary>
            Public ReadOnly Property PendingOnGpu As New List(Of (Name As String, Cpu As Func(Of SKBitmap, SKBitmap)))()

            Private Sub New(raster As Boolean)
                _raster = raster
            End Sub

            ''' <summary>Liegt das Bild gerade auf der Karte?</summary>
            Public ReadOnly Property OnGpu As Boolean
                Get
                    Return _surface IsNot Nothing
                End Get
            End Property

            ''' <summary>Eine Sitzung, wenn die Einstellung an ist, die Selbstpruefung bestanden ist und
            ''' das Bild ein Format hat, das die Stufen kennen. Sonst Nothing.</summary>
            Public Shared Function TryCreate(source As SKBitmap) As GpuDetailSession
                If source Is Nothing Then Return Nothing
                If source.ColorType <> SKColorType.Bgra8888 AndAlso source.ColorType <> SKColorType.Rgba8888 Then Return Nothing
                If source.AlphaType = SKAlphaType.Unpremul Then Return Nothing
                If DetailStageShadersOnRaster Then Return New GpuDetailSession(raster:=True)
                If Not GpuImageService.Enabled Then Return Nothing
                If Not GpuImageService.IsAvailable Then Return Nothing
                If LastGpuCheck.State = GpuCheckState.Untested Then CheckGpuImageProcessing()
                If LastGpuCheck.State <> GpuCheckState.Works Then Return Nothing
                Return New GpuDetailSession(raster:=False)
            End Function

            ''' <summary>Auf dem GPU-Faden, oder im Pruefmodus direkt hier mit Skia auf dem Prozessor
            ''' (GRContext dann Nothing, die Flaechen liegen im Speicher).</summary>
            Private Function Run(Of T)(work As Func(Of GRContext, T), ByRef result As T) As Boolean
                If Not _raster Then Return GpuImageService.TryRun(work, result)
                Try
                    result = work(Nothing)
                    Return True
                Catch ex As Exception
                    DiagnosticLogService.LogException("GpuImage", ex)
                    result = Nothing
                    Return False
                End Try
            End Function

            ''' <summary>Rechnet eine Stufe auf der Karte. <paramref name="current"/> ist der Stand
            ''' auf dem Prozessor, falls das Bild noch nicht oben liegt. False, wenn die Karte
            ''' scheitert; das Bild liegt dann unveraendert da, wo es vorher lag.</summary>
            Public Function TryApply(current As SKBitmap, stage As Func(Of GRContext, SKImage, SKSurface)) As Boolean
                Dim ok As Boolean
                ' Liegt das Ergebnis der Punktkette noch auf der Karte und ist es genau dieses Bild,
                ' wird es uebernommen statt hochgeladen (auf dem rufenden Faden geholt, die Ablage
                ' gilt je Faden).
                Dim handover = If(_surface Is Nothing AndAlso Not _raster, TakePointOpHandover(current), Nothing)
                Dim done = Run(
                    Function(gr)
                        If _surface Is Nothing Then
                            _info = current.Info
                            _surface = If(handover, Upload(gr, current))
                            handover = Nothing
                        End If
                        Using snapshot = _surface.Snapshot()
                            Dim nextSurface = stage(gr, snapshot)
                            If nextSurface Is Nothing Then Throw New InvalidOperationException("Zielflaeche auf der Grafikkarte nicht anlegbar.")
                            _surface.Dispose()
                            _surface = nextSurface
                        End Using
                        Return True
                    End Function, ok)
                ' Kam der Lauf gar nicht erst an (Karte verloren), liegt die Uebergabe noch hier.
                If handover IsNot Nothing Then DisposeOnGpu(handover)
                Return done AndAlso ok
            End Function

            ''' <summary>Holt das Bild zurueck auf den Prozessor und gibt die Textur frei. Nothing,
            ''' wenn das Zuruecklesen scheitert.</summary>
            Public Function Download() As SKBitmap
                If _surface Is Nothing Then Return Nothing
                If FailNextDetailDownloadForCheck Then
                    FailNextDetailDownloadForCheck = False
                    Dispose()
                    Return Nothing
                End If
                Dim result As SKBitmap = Nothing
                Run(
                    Function(gr)
                        Try
                            If gr IsNot Nothing Then
                                gr.Flush()
                                gr.Submit(True)
                            End If
                            Dim bmp = New SKBitmap(_info)
                            If Not _surface.ReadPixels(bmp.Info, bmp.GetPixels(), bmp.RowBytes, 0, 0) Then
                                bmp.Dispose()
                                Throw New InvalidOperationException("Zuruecklesen von der Grafikkarte gescheitert.")
                            End If
                            Return bmp
                        Finally
                            _surface.Dispose()
                            _surface = Nothing
                        End Try
                    End Function, result)
                ' Ist der GPU-Faden schon tot, laesst sich die Textur nicht mehr freigeben; dann
                ' gilt sie hier als verloren und wird nicht noch einmal angefasst.
                _surface = Nothing
                Return result
            End Function

            Public Sub Dispose() Implements IDisposable.Dispose
                If _surface Is Nothing Then Return
                Dim ignored As Boolean
                Run(
                    Function(gr)
                        _surface?.Dispose()
                        Return True
                    End Function, ignored)
                _surface = Nothing
            End Sub

            Private Shared Function Upload(gr As GRContext, source As SKBitmap) As SKSurface
                Dim surface = NewStageSurface(gr, source.Width, source.Height)
                Using raster = SKImage.FromBitmap(source)
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        surface.Canvas.DrawImage(raster, 0, 0, paint)
                    End Using
                End Using
                Return surface
            End Function
        End Class

        ''' <summary>Pruefmodus fuer den Pruefstand: die Stufen-Shader laufen ueber Skia auf dem
        ''' Prozessor statt auf der Karte, damit ihr Vergleich mit den Prozessor-Stufen ohne
        ''' Grafikkarte laeuft. Dort viel langsamer und deshalb nie im Betrieb.</summary>
        Friend Shared DetailStageShadersOnRaster As Boolean = False

        ''' <summary>Pruefschalter: das naechste Zurueckholen einer Sitzung scheitert, als waere die
        ''' Karte verloren. Damit prueft der Pruefstand, dass die Stufen dann auf dem Prozessor
        ''' nachgerechnet werden (BringDetailSessionHome). Setzt sich nach einem Gebrauch zurueck.</summary>
        Friend Shared FailNextDetailDownloadForCheck As Boolean = False

        ''' <summary>Eine Zielflaeche fuer eine Stufe: 8 Bit, vormultipliziert, wie das Zwischenbild
        ''' auf dem Prozessor. Ohne GRContext (Pruefmodus) im Speicher.</summary>
        Private Shared Function NewStageSurface(gr As GRContext, width As Integer, height As Integer) As SKSurface
            Dim info = New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
            Return If(gr Is Nothing, SKSurface.Create(info), SKSurface.Create(gr, False, info))
        End Function

        ''' <summary>Fuehrt eine raeumliche Stufe aus: auf der Karte, wenn es eine Sitzung gibt und
        ''' die Stufe einen Shader hat (<paramref name="gpu"/> nicht Nothing), sonst auf dem
        ''' Prozessor. Liegt das Bild oben und die Stufe kann nur der Prozessor, wird es vorher
        ''' zurueckgeholt.</summary>
        Private Shared Function RunDetailStage(session As GpuDetailSession, processed As SKBitmap, ByRef owned As Boolean,
                                               measureName As String,
                                               gpu As Func(Of GRContext, SKImage, SKSurface),
                                               cpu As Func(Of SKBitmap, SKBitmap)) As SKBitmap
            If session IsNot Nothing AndAlso gpu IsNot Nothing Then
                Dim done = PerformanceTraceService.Measure(measureName & " (Grafikkarte)",
                                                           Function() session.TryApply(processed, gpu))
                If done Then
                    session.PendingOnGpu.Add((measureName, cpu))
                    Return processed
                End If
                NoteGpuFallback(measureName & ": Lauf auf der Grafikkarte gescheitert (Fehlerdatei)")
            End If
            processed = BringDetailSessionHome(session, processed, owned)
            Dim input = processed
            Return ReplaceBitmapOwned(processed, PerformanceTraceService.Measure(measureName, Function() cpu(input)), owned)
        End Function

        ''' <summary>Liegt das Bild auf der Karte, holt es zurueck; sonst bleibt alles, wie es ist.</summary>
        Private Shared Function BringDetailSessionHome(session As GpuDetailSession, processed As SKBitmap,
                                                       ByRef owned As Boolean) As SKBitmap
            If session Is Nothing OrElse Not session.OnGpu Then Return processed
            Dim back = PerformanceTraceService.Measure("Pixel: Zuruecklesen (Grafikkarte)", Function() session.Download())
            Dim pending = session.PendingOnGpu.ToList()
            session.PendingOnGpu.Clear()
            If back IsNot Nothing Then Return ReplaceBitmapOwned(processed, back, owned)

            ' Das Ergebnis der Karte ist verloren (Geraet weg, Zuruecklesen gescheitert). Das
            ' Prozessorbild steht noch auf dem Stand vor den Stufen, die dort liefen; ohne sie
            ' weiterzumachen hiesse, ein Bild ohne Schaerfe oder Rauschminderung zu speichern, und
            ' niemand saehe es. Deshalb werden genau diese Stufen hier auf dem Prozessor
            ' nachgerechnet, in derselben Reihenfolge.
            NoteGpuFallback($"Zuruecklesen gescheitert, {pending.Count} Stufe(n) werden auf dem Prozessor nachgerechnet")
            For Each stage In pending
                Dim input = processed
                processed = ReplaceBitmapOwned(processed, PerformanceTraceService.Measure(stage.Name, Function() stage.Cpu(input)), owned)
            Next
            Return processed
        End Function

        ' ── Gemeinsame Bausteine der Stufen-Shader ───────────────────────────────

        ''' <summary>Byte-Lesen und -Schreiben wie im Prozessor. Eine Konstante, die jeder Stufen-
        ''' Shader vor seinen eigenen Text setzt.</summary>
        Private Const StageShaderCommon As String = "
float4 toBytes(half4 c) {
    return floor(float4(c) * 255.0 + 0.5);
}
// ReadUnpremultiplied: ganzzahlig geteilt. Der wahre Quotient hat Nachkommastellen in Schritten
// von 1/Alpha, also mindestens 1/255; ein Tausendstel faengt die Gleitkomma-Ungenauigkeit ab.
float3 unpremulBytes(float4 b) {
    if (b.a >= 255.0) return b.rgb;
    if (b.a <= 0.0) return float3(0.0);
    return min(float3(255.0), floor(b.rgb * 255.0 / b.a + 0.001));
}
float roundHalfEven(float x) {
    float r = floor(x + 0.5);
    if (r - x == 0.5 && mod(r, 2.0) == 1.0) r -= 1.0;
    return r;
}
// ClampToByte: Math.Round, dann auf 0 bis 255.
float clampByte(float v) {
    return clamp(roundHalfEven(v), 0.0, 255.0);
}
// WritePremultiplied: ganzzahlig, nie ueber Alpha.
half4 writePremul(float3 rgb, float a) {
    if (a <= 0.0) return half4(0.0);
    if (a < 255.0) rgb = min(float3(a), floor(rgb * a / 255.0 + 0.001));
    return half4(half3(rgb / 255.0), half(a / 255.0));
}
"

        Private Shared ReadOnly _stageEffects As New Dictionary(Of String, SKRuntimeEffect)()

        ''' <summary>Uebersetzt einen Stufen-Shader einmal und haelt ihn.</summary>
        Private Shared Function StageEffect(name As String, body As String) As SKRuntimeEffect
            SyncLock _stageEffects
                Dim effect As SKRuntimeEffect = Nothing
                If _stageEffects.TryGetValue(name, effect) Then Return effect
                Dim errors As String = Nothing
                effect = SKRuntimeEffect.CreateShader(StageShaderCommon & body, errors)
                If effect Is Nothing Then Throw New InvalidOperationException($"Shader {name} uebersetzt nicht: {errors}")
                _stageEffects(name) = effect
                Return effect
            End SyncLock
        End Function

        ''' <summary>Zeichnet einen Stufen-Shader ueber die ganze Flaeche einer neuen Zielflaeche.
        ''' Die Kinder werden sofort wieder freigegeben (siehe PointOpShader: sonst gibt sie der
        ''' Speicherbereiniger ohne OpenGL-Kontext frei).</summary>
        Private Shared Function DrawStage(gr As GRContext, width As Integer, height As Integer, effect As SKRuntimeEffect,
                                          uniforms As SKRuntimeEffectUniforms,
                                          ParamArray children As (Name As String, Image As SKImage)()) As SKSurface
            Dim surface = NewStageSurface(gr, width, height)
            If surface Is Nothing Then Return Nothing
            Dim parts = New List(Of SKShader)()
            Try
                Dim childSet = New SKRuntimeEffectChildren(effect)
                For Each child In children
                    Dim part = child.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                                                    New SKSamplingOptions(SKFilterMode.Nearest))
                    parts.Add(part)
                    childSet(child.Name) = part
                Next
                Using shader = effect.ToShader(uniforms, childSet),
                      paint = New SKPaint With {.Shader = shader, .BlendMode = SKBlendMode.Src}
                    surface.Canvas.DrawRect(0, 0, width, height, paint)
                End Using
            Finally
                For Each part In parts
                    part.Dispose()
                Next
            End Try
            Return surface
        End Function

        ' ── Weichzeichnen wie Skia auf dem Prozessor ─────────────────────────────
        '
        ' Viele Stufen weichzeichnen mit SKImageFilter.CreateBlur. Auf dem Prozessor rechnet Skia
        ' das NICHT als Gaussglocke, sondern mit drei Kastenfiltern je Richtung (GaussPass in
        ' SkBlurImageFilter.cpp, Skia m119): Fensterbreite w = floor(sigma * 3 * sqrt(2 pi) / 4
        ' + 0,5), ganzzahlig summiert, geteilt durch w^3 (gerades w: w^2 * (w + 1)) mit einer
        ' genaeherten Division (Multiplikation mit round(2^32 / Teiler), dann 32 Bit nach rechts),
        ' erst waagerecht mit Ergebnis in 8 Bit, dann senkrecht; ausserhalb des Bildes zaehlt 0.
        ' Skias eigener Weichzeichner auf der Karte ist eine echte Gaussglocke und wich gemessen
        ' bis 7 Stufen ab, sobald die Schaerfe den Unterschied verstaerkte. Dieser Nachbau liefert
        ' dieselben Bytes wie der Prozessor.

        ''' <summary>Laengster Kern, den der Shader kann: 3w - 1 Punkte, also w bis 85 (sigma bis
        ''' etwa 45). Darueber bleibt die Stufe auf dem Prozessor. Ab w 41 uebersteigen die Summen die
        ''' 24 Bit einer Gleitkommazahl; dann kann ein Bildpunkt um eine Stufe abweichen.</summary>
        Private Const RasterBlurMaxTaps As Integer = 256

        ''' <summary>Skias Fensterbreite zu einem Sigma (calculate_window).</summary>
        Private Shared Function RasterBlurWindow(sigma As Single) As Integer
            Return Math.Max(1, CInt(Math.Floor(CDbl(sigma) * 3.0 * Math.Sqrt(2.0 * Math.PI) / 4.0 + 0.5)))
        End Function

        ''' <summary>Kann der Nachbau dieses Sigma? Bis zur Grenze von Skias GaussPass (Fenster unter
        ''' 255, Sigma bis etwa 135); darueber nimmt Skia ein anderes Verfahren.</summary>
        Private Shared Function RasterBlurSupported(sigma As Single) As Boolean
            Return RasterBlurWindow(sigma) < 255
        End Function

        ''' <summary>Der Weichzeichner fuer grosse Fenster: die drei Kaesten einzeln, je Richtung.
        ''' Ein einziger Kern haette 3w - 1 Punkte und spraengte den Shader. Die Zwischensummen
        ''' stehen in halber Gleitkomma (je Kanal zwei Werte, also zwei Flaechen fuer vier Kanaele)
        ''' auf einem Feld, das um den Kernradius ueber das Bild hinausreicht: dort laufen auch bei
        ''' Skia die Summen weiter. Ab etwa 4 Millionen sind die Summen nicht mehr exakt; dann kann
        ''' ein Bildpunkt um eine Stufe abweichen.</summary>
        Private Shared Function RasterBlurLargeOnGpu(gr As GRContext, image As SKImage, window As Integer, divisor As Integer,
                                                     factor As Double, clampEdges As Boolean) As SKSurface
            Dim odd = (window And 1) = 1
            ' Lage der drei Kaesten relativ zum Bildpunkt (erster Index) und ihre Breite.
            Dim boxes As (Start As Integer, Width As Integer)()
            If odd Then
                Dim r = (window - 1) \ 2
                boxes = {(-r, window), (-r, window), (-r, window)}
            Else
                boxes = {(-(window \ 2), window), (-(window \ 2 - 1), window), (-(window \ 2), window + 1)}
            End If
            Dim border = If(odd, 3 * ((window - 1) \ 2), 3 * (window \ 2) - 1)
            Dim partialBytes = StageEffect("Kasten Teilsumme Bytes", FloatShaderCommon & BoxPartialBytesShader)
            Dim partialPairs = StageEffect("Kasten Teilsumme Paare", FloatShaderCommon & BoxPartialPairsShader)
            Dim first = StageEffect("Kasten erster", FloatShaderCommon & BoxFirstShader)
            Dim middle = StageEffect("Kasten mittel", FloatShaderCommon & BoxMiddleShader)
            Dim last = StageEffect("Kasten letzter", FloatShaderCommon & BoxLastShader)

            ' JEDER KASTEN IN ZWEI DURCHGAENGEN. Ein Kasten der Breite w las je Bildpunkt w Werte,
            ' bei w um 140 also gut 800 je Richtung. Zerlegt in Teilsummen ueber m (etwa Wurzel w)
            ' Punkte und dann w \ m Teilsummen im Abstand m plus die restlichen w Mod m Punkte sind es
            ' rund 3 Wurzel w. Die Teilsumme an t steht fuer die Punkte ab t + offset; die Flaeche
            ' reicht um (w \ m - 1) m ueber die Ausgabe hinaus, so weit greift die Summe.
            Dim direction = Function(input As SKImage, horizontal As Boolean) As SKSurface
                                Dim w = input.Width, h = input.Height
                                Dim extW = If(horizontal, w + 2 * border, w)
                                Dim extH = If(horizontal, h, h + 2 * border)
                                Dim dirValue = If(horizontal, New Single() {1, 0}, New Single() {0, 1})
                                ' Lage der Eingabe gegenueber der Ausgabe: der erste Kasten liest das Bild
                                ' und schreibt das erweiterte Feld, der letzte umgekehrt.
                                Dim shifts = {-border, 0, border}
                                Dim boxUniforms = Function(effect As SKRuntimeEffect, box As Integer, half As Single) As SKRuntimeEffectUniforms
                                                      Dim u = New SKRuntimeEffectUniforms(effect)
                                                      Dim boxWidth = boxes(box).Width
                                                      Dim stride = Math.Max(1, CInt(Math.Round(Math.Sqrt(boxWidth))))
                                                      Dim count = boxWidth \ stride
                                                      u("direction") = dirValue
                                                      u("offset") = CSng(shifts(box) + boxes(box).Start)
                                                      u("stride") = CSng(stride)
                                                      u("count") = CSng(count)
                                                      u("boxWidth") = CSng(boxWidth)
                                                      ' Mittel statt Summe: die Summe des zweiten Kastens
                                                      ' laege bis 255 w^2 und damit ueber dem Hoechstwert
                                                      ' der halben Gleitkomma (65504).
                                                      u("scale") = If(box < 2, 1.0F / boxWidth, CSng(boxes(0).Width) * boxes(1).Width)
                                                      u("imageSize") = New Single() {w, h}
                                                      u("extSize") = New Single() {extW, extH}
                                                      u("part") = half
                                                      Return u
                                                  End Function
                                ' Groesse der Teilsummenflaeche zu einem Kasten mit Ausgabe outW x outH.
                                Dim partialSize = Function(box As Integer, outW As Integer, outH As Integer) As (W As Integer, H As Integer)
                                                      Dim boxWidth = boxes(box).Width
                                                      Dim stride = Math.Max(1, CInt(Math.Round(Math.Sqrt(boxWidth))))
                                                      Dim reach = (boxWidth \ stride - 1) * stride
                                                      Return If(horizontal, (outW + reach, outH), (outW, outH + reach))
                                                  End Function
                                Dim partialOf = Function(effect As SKRuntimeEffect, box As Integer, half As Single, outW As Integer, outH As Integer,
                                                         children As (Name As String, Image As SKImage)()) As SKSurface
                                                    Dim size = partialSize(box, outW, outH)
                                                    Dim u = boxUniforms(effect, box, half)
                                                    If effect Is partialBytes Then u("clampEdges") = If(clampEdges, 1.0F, 0.0F)
                                                    Return DrawInto(NewFloatSurface(gr, size.W, size.H), size.W, size.H, effect, u, children)
                                                End Function
                                Dim a1 As SKSurface = Nothing, a2 As SKSurface = Nothing, b1 As SKSurface = Nothing, b2 As SKSurface = Nothing
                                Dim p1 As SKSurface = Nothing, p2 As SKSurface = Nothing
                                Dim freePartials = Sub()
                                                       p1?.Dispose() : p2?.Dispose()
                                                       p1 = Nothing : p2 = Nothing
                                                   End Sub
                                Try
                                    ' Erster Kasten: aus den Bytes, je Haelfte der Kanaele eine Flaeche.
                                    p1 = partialOf(partialBytes, 0, 0.0F, extW, extH, {("src", input)})
                                    p2 = partialOf(partialBytes, 0, 1.0F, extW, extH, {("src", input)})
                                    If p1 Is Nothing OrElse p2 Is Nothing Then Return Nothing
                                    Using pi1 = p1.Snapshot(), pi2 = p2.Snapshot()
                                        Dim fu1 = boxUniforms(first, 0, 0.0F) : fu1("clampEdges") = If(clampEdges, 1.0F, 0.0F)
                                        Dim fu2 = boxUniforms(first, 0, 1.0F) : fu2("clampEdges") = If(clampEdges, 1.0F, 0.0F)
                                        a1 = DrawInto(NewFloatSurface(gr, extW, extH), extW, extH, first, fu1, ("src", input), ("partial", pi1))
                                        a2 = DrawInto(NewFloatSurface(gr, extW, extH), extW, extH, first, fu2, ("src", input), ("partial", pi2))
                                    End Using
                                    freePartials()
                                    If a1 Is Nothing OrElse a2 Is Nothing Then Return Nothing
                                    Using ai1 = a1.Snapshot(), ai2 = a2.Snapshot()
                                        p1 = partialOf(partialPairs, 1, 0.0F, extW, extH, {("lowPair", ai1), ("highPair", ai2)})
                                        p2 = partialOf(partialPairs, 1, 1.0F, extW, extH, {("lowPair", ai1), ("highPair", ai2)})
                                        If p1 Is Nothing OrElse p2 Is Nothing Then Return Nothing
                                        Using pi1 = p1.Snapshot(), pi2 = p2.Snapshot()
                                            b1 = DrawInto(NewFloatSurface(gr, extW, extH), extW, extH, middle, boxUniforms(middle, 1, 0.0F),
                                                          ("lowPair", ai1), ("highPair", ai2), ("partial", pi1))
                                            b2 = DrawInto(NewFloatSurface(gr, extW, extH), extW, extH, middle, boxUniforms(middle, 1, 1.0F),
                                                          ("lowPair", ai1), ("highPair", ai2), ("partial", pi2))
                                        End Using
                                    End Using
                                    freePartials()
                                    a1.Dispose() : a2.Dispose() : a1 = Nothing : a2 = Nothing
                                    If b1 Is Nothing OrElse b2 Is Nothing Then Return Nothing
                                    Using bi1 = b1.Snapshot(), bi2 = b2.Snapshot()
                                        p1 = partialOf(partialPairs, 2, 0.0F, w, h, {("lowPair", bi1), ("highPair", bi2)})
                                        p2 = partialOf(partialPairs, 2, 1.0F, w, h, {("lowPair", bi1), ("highPair", bi2)})
                                        If p1 Is Nothing OrElse p2 Is Nothing Then Return Nothing
                                        Using pi1 = p1.Snapshot(), pi2 = p2.Snapshot()
                                            Dim lu = boxUniforms(last, 2, 0.0F)
                                            lu("divisor") = CSng(divisor)
                                            lu("halfDivisor") = CSng((divisor + 1) >> 1)
                                            lu("factorError") = CSng((factor - 4294967296.0 / divisor) / 4294967296.0)
                                            Return DrawStage(gr, w, h, last, lu, ("lowPair", bi1), ("highPair", bi2),
                                                             ("partialLow", pi1), ("partialHigh", pi2))
                                        End Using
                                    End Using
                                Finally
                                    a1?.Dispose() : a2?.Dispose() : b1?.Dispose() : b2?.Dispose()
                                    freePartials()
                                End Try
                            End Function
            Using across = direction(image, True)
                If across Is Nothing Then Return Nothing
                Using acrossImage = across.Snapshot()
                    Return direction(acrossImage, False)
                End Using
            End Using
        End Function

        ' Gemeinsam: der Punkt p der Ausgabe liest die Eingabe ab p + offset, Kastenbreite boxWidth.
        ' Der erste Kasten liest die Bytes des Bildes (ausserhalb 0 oder der Rand fortgesetzt), die
        ' anderen die Wertepaare des erweiterten Feldes (ausserhalb 0). Die Teilsumme an t ist die
        ' Summe ueber stride Punkte ab t + offset; die Summe nimmt count Teilsummen im Abstand
        ' stride und dazu die restlichen Punkte einzeln.
        Private Const BoxBytesInput As String = "
uniform shader src;
uniform float clampEdges;
float2 inputAt(float2 q) {
    float4 b = float4(0.0);
    if (clampEdges > 0.5) {
        b = toBytes(src.eval(clamp(q, float2(0.5), imageSize - 0.5)));
    } else if (q.x > 0.0 && q.y > 0.0 && q.x < imageSize.x && q.y < imageSize.y) {
        b = toBytes(src.eval(q));
    }
    return part < 0.5 ? b.rg : b.ba;
}
"

        Private Const BoxPairsInput As String = "
uniform shader lowPair;
uniform shader highPair;
float2 inputAt(float2 q) {
    if (q.x > 0.0 && q.y > 0.0 && q.x < extSize.x && q.y < extSize.y) {
        half4 t = part < 0.5 ? lowPair.eval(q) : highPair.eval(q);
        return float2(joinValue(t.xy), joinValue(t.zw));
    }
    return float2(0.0);
}
"

        Private Const BoxCommonUniforms As String = "
uniform float2 direction;
uniform float offset;
uniform float stride;
uniform float count;
uniform float boxWidth;
uniform float scale;
uniform float2 imageSize;
uniform float2 extSize;
uniform float part;
half4 writePair(float2 v) {
    float2 v1 = splitValue(v.x);
    float2 v2 = splitValue(v.y);
    return half4(half(v1.x), half(v1.y), half(v2.x), half(v2.y));
}
"

        Private Const BoxPartialMain As String = "
half4 main(float2 p) {
    float2 base = p + direction * offset;
    float2 sum = float2(0.0);
    for (int k = 0; k < 32; k++) {
        if (float(k) >= stride) break;
        sum += inputAt(base + direction * float(k));
    }
    return writePair(sum);
}
"

        ' Summe ueber die Teilsummen und den Rest, fuer EINE Haelfte der Kanaele.
        Private Const BoxSumMain As String = "
uniform shader partial;
float2 boxSum(float2 p) {
    float2 sum = float2(0.0);
    for (int j = 0; j < 256; j++) {
        if (float(j) >= count) break;
        half4 t = partial.eval(p + direction * (float(j) * stride));
        sum += float2(joinValue(t.xy), joinValue(t.zw));
    }
    float2 base = p + direction * offset;
    for (int k = 0; k < 32; k++) {
        float i = count * stride + float(k);
        if (i >= boxWidth) break;
        sum += inputAt(base + direction * i);
    }
    return sum;
}
half4 main(float2 p) {
    return writePair(boxSum(p) * scale);
}
"

        Private Const BoxPartialBytesShader As String = BoxCommonUniforms & BoxBytesInput & BoxPartialMain
        Private Const BoxPartialPairsShader As String = BoxCommonUniforms & BoxPairsInput & BoxPartialMain
        Private Const BoxFirstShader As String = BoxCommonUniforms & BoxBytesInput & BoxSumMain
        Private Const BoxMiddleShader As String = BoxCommonUniforms & BoxPairsInput & BoxSumMain

        ' Der letzte Kasten liest im erweiterten Feld und schreibt das Bild, mit Skias Division.
        Private Const BoxLastShader As String = "
uniform shader lowPair;
uniform shader highPair;
uniform shader partialLow;
uniform shader partialHigh;
uniform float divisor;
uniform float halfDivisor;
uniform float factorError;
" & BoxCommonUniforms & "
float4 pairsAt(float2 q) {
    if (q.x > 0.0 && q.y > 0.0 && q.x < extSize.x && q.y < extSize.y) {
        half4 a = lowPair.eval(q);
        half4 b = highPair.eval(q);
        return float4(joinValue(a.xy), joinValue(a.zw), joinValue(b.xy), joinValue(b.zw));
    }
    return float4(0.0);
}
half4 main(float2 p) {
    float4 sum = float4(0.0);
    for (int j = 0; j < 256; j++) {
        if (float(j) >= count) break;
        float2 q = p + direction * (float(j) * stride);
        half4 a = partialLow.eval(q);
        half4 b = partialHigh.eval(q);
        sum += float4(joinValue(a.xy), joinValue(a.zw), joinValue(b.xy), joinValue(b.zw));
    }
    float2 base = p + direction * offset;
    for (int k = 0; k < 32; k++) {
        float i = count * stride + float(k);
        if (i >= boxWidth) break;
        sum += pairsAt(base + direction * i);
    }
    // Zurueck auf die ganzzahlige Summe; bei grossen Fenstern nur noch auf Bruchteile genau.
    float4 n = floor(sum * scale + 0.5) + halfDivisor;
    float4 q0 = floor(n / divisor);
    float4 r = n - q0 * divisor;
    q0 += float4(greaterThanEqual(r, float4(divisor))) - float4(lessThan(r, float4(0.0)));
    r = n - q0 * divisor;
    float4 frac = r / divisor + n * factorError;
    q0 += float4(greaterThanEqual(frac, float4(1.0))) - float4(lessThan(frac, float4(0.0)));
    return half4(clamp(q0, 0.0, 255.0) / 255.0);
}
"

        ''' <summary>Der Kern als Faltung der drei Kaesten, ganzzahlige Gewichte, symmetrisch um die
        ''' Mitte. Gerades w: zwei Kaesten der Breite w, gegeneinander um einen Punkt versetzt, und
        ''' einer der Breite w + 1.</summary>
        Private Shared Function RasterBlurKernel(window As Integer) As Integer()
            Dim boxes As Integer()
            If (window And 1) = 1 Then
                boxes = {window, window, window}
            Else
                boxes = {window, window, window + 1}
            End If
            Dim kernel = New Integer() {1}
            For Each width In boxes
                Dim nextKernel = New Integer(kernel.Length + width - 2) {}
                For i = 0 To kernel.Length - 1
                    For j = 0 To width - 1
                        nextKernel(i + j) += kernel(i)
                    Next
                Next
                kernel = nextKernel
            Next
            Return kernel
        End Function

        ''' <summary>Weichzeichnet ein Bild auf der Karte so, wie Skia es auf dem Prozessor tut.
        ''' Nothing, wenn das Sigma zu gross ist.</summary>
        Private Shared Function RasterBlurOnGpu(gr As GRContext, image As SKImage, sigma As Single,
                                                Optional clampEdges As Boolean = False) As SKSurface
            Dim window = RasterBlurWindow(sigma)
            If window <= 1 Then
                Dim copy = NewStageSurface(gr, image.Width, image.Height)
                Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                    copy.Canvas.DrawImage(image, 0, 0, paint)
                End Using
                Return copy
            End If
            If Not RasterBlurSupported(sigma) Then Return Nothing
            Dim divisor = If((window And 1) = 1, window * window * window, window * window * window + window * window)
            Dim factor = Math.Round(4294967296.0 / divisor, MidpointRounding.AwayFromZero)
            If 3 * window - 1 > RasterBlurMaxTaps Then
                Return RasterBlurLargeOnGpu(gr, image, window, divisor, factor, clampEdges)
            End If
            Dim kernel = RasterBlurKernel(window)
            Dim weights = New Single(RasterBlurMaxTaps - 1) {}
            For i = 0 To kernel.Length - 1
                weights(i) = kernel(i)
            Next
            Dim effect = StageEffect("Weichzeichnen wie Skia", RasterBlurShader)
            Dim pass = Function(input As SKImage, horizontal As Boolean) As SKSurface
                           Dim u = New SKRuntimeEffectUniforms(effect)
                           u("weights") = weights
                           u("radius") = CSng((kernel.Length - 1) \ 2)
                           u("divisor") = CSng(divisor)
                           u("halfDivisor") = CSng((divisor + 1) >> 1)
                           u("factorError") = CSng((factor - 4294967296.0 / divisor) / 4294967296.0)
                           u("direction") = If(horizontal, New Single() {1, 0}, New Single() {0, 1})
                           u("size") = New Single() {input.Width, input.Height}
                           u("clampEdges") = If(clampEdges, 1.0F, 0.0F)
                           Return DrawStage(gr, input.Width, input.Height, effect, u, ("src", input))
                       End Function
            Using across = pass(image, True)
                If across Is Nothing Then Return Nothing
                Using acrossImage = across.Snapshot()
                    Return pass(acrossImage, False)
                End Using
            End Using
        End Function

        Private Const RasterBlurShader As String = "
uniform shader src;
uniform float weights[256];
uniform float clampEdges;
uniform float radius;
uniform float divisor;
uniform float halfDivisor;
uniform float factorError;
uniform float2 direction;
uniform float2 size;
half4 main(float2 p) {
    float4 sum = float4(0.0);
    for (int i = 0; i < 256; i++) {
        float k = float(i) - radius;
        if (k > radius) break;
        float2 q = p + direction * k;
        if (clampEdges > 0.5) {
            // SKShaderTileMode.Clamp: Skia setzt den Rand vor dem Weichzeichnen fort.
            sum += weights[i] * toBytes(src.eval(clamp(q, float2(0.5), size - 0.5)));
        } else if (q.x > 0.0 && q.y > 0.0 && q.x < size.x && q.y < size.y) {
            sum += weights[i] * toBytes(src.eval(q));
        }
    }
    // Skias Division: ((sum + half) * round(2^32 / d)) >> 32. Das ist floor((sum + half) / d),
    // ausser der Quotient liegt so nah an einer ganzen Zahl, dass der Rundungsfehler des Faktors
    // ihn ueber oder unter die Grenze schiebt. Rest und Fehler werden deshalb genau gerechnet.
    float4 n = sum + halfDivisor;
    float4 q0 = floor(n / divisor);
    float4 r = n - q0 * divisor;
    q0 += float4(greaterThanEqual(r, float4(divisor))) - float4(lessThan(r, float4(0.0)));
    r = n - q0 * divisor;
    float4 frac = r / divisor + n * factorError;
    q0 += float4(greaterThanEqual(frac, float4(1.0))) - float4(lessThan(frac, float4(0.0)));
    float4 b = clamp(q0, 0.0, 255.0);
    return half4(b / 255.0);
}
"

        ' ── Rauschminderung (Gauss), Weichzeichner, Farbrauschen ─────────────────

        ''' <summary>ApplyNoiseReduction: weichzeichnen, mit Detail das Original an Kanten anteilig
        ''' zurueckmischen. Auch die negative Haelfte von "Rauschen hinzufuegen" rechnet so.</summary>
        Private Shared Function NoiseReductionOnGpu(amount As Single, Optional detail As Single = 0) As Func(Of GRContext, SKImage, SKSurface)
            Dim sigma = 0.25F + Clamp(amount, 0, 1) * 2.2F
            Dim d = Clamp(detail, 0, 1)
            Return Function(gr, image)
                       Dim blurred = RasterBlurOnGpu(gr, image, sigma, clampEdges:=True)
                       If blurred Is Nothing OrElse d <= 0 Then Return blurred
                       Try
                           Using blurImage = blurred.Snapshot()
                               Dim effect = StageEffect("Rauschen Kantenerhalt", NoiseDetailShader)
                               Dim u = New SKRuntimeEffectUniforms(effect)
                               u("detail") = d
                               Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image), ("blur", blurImage))
                           End Using
                       Finally
                           blurred.Dispose()
                       End Try
                   End Function
        End Function

        Private Const NoiseDetailShader As String = "
uniform shader src;
uniform shader blur;
uniform float detail;
half4 main(float2 p) {
    float4 c = toBytes(src.eval(p));
    float3 cc = unpremulBytes(c);
    float3 lb = unpremulBytes(toBytes(blur.eval(p)));
    float diff = abs(cc.r - lb.r) + abs(cc.g - lb.g) + abs(cc.b - lb.b);
    float m = clamp(diff / 48.0, 0.0, 1.0) * detail;
    float3 o = float3(clampByte(lb.r + (cc.r - lb.r) * m),
                      clampByte(lb.g + (cc.g - lb.g) * m),
                      clampByte(lb.b + (cc.b - lb.b) * m));
    return writePremul(o, c.a);
}
"

        ''' <summary>ApplyImageBlur: der Weichzeichner des Panels, Rand fortgesetzt.</summary>
        Private Shared Function ImageBlurOnGpu(source As SKBitmap, amount As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim a = Clamp(amount, 0, 1)
            Dim sigma = Math.Min(source.Width, source.Height) * ImageBlurSigmaShare * a * a
            ' Unter einem halben Punkt laesst der Prozessor das Bild stehen; die Kopie tut dasselbe.
            If sigma < 0.5F Then sigma = 0
            If Not RasterBlurSupported(sigma) Then Return Nothing
            Return Function(gr, image) RasterBlurOnGpu(gr, image, sigma, clampEdges:=True)
        End Function

        ''' <summary>ApplyColorNoiseReduction: Farbe aus der Weichzeichnung, Helligkeit vom
        ''' Original, mit Schutz fuer kraeftige Farben. Rechnet wie dort auf den vormultiplizierten
        ''' Bytes und nur fuer Bgra8888.</summary>
        Private Shared Function ColorNoiseReductionOnGpu(source As SKBitmap, amount As Single) As Func(Of GRContext, SKImage, SKSurface)
            If source.ColorType <> SKColorType.Bgra8888 Then Return Nothing
            amount = Clamp(amount, 0, 1)
            Dim fine = Clamp(amount * ChromaFineGain, 0, 1)
            Dim sigma = 0.5F + fine * 2.0F
            Dim coarseSigma = CoarseChromaSigma(source, amount)
            If Not RasterBlurSupported(sigma) OrElse (coarseSigma > 0.5F AndAlso Not RasterBlurSupported(coarseSigma)) Then Return Nothing
            Return Function(gr, image)
                       Using blurred = RasterBlurOnGpu(gr, image, sigma, clampEdges:=True)
                           If blurred Is Nothing Then Return Nothing
                           Using blurImage = blurred.Snapshot()
                               Dim effect = StageEffect("Farbrauschen", ColorNoiseShader)
                               Dim u = New SKRuntimeEffectUniforms(effect)
                               u("fine") = fine
                               If coarseSigma <= 0.5F Then
                                   Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image), ("mixed", blurImage))
                               End If
                               ' Der grobe Durchgang setzt auf dem feinen auf.
                               Using coarse = RasterBlurOnGpu(gr, blurImage, coarseSigma, clampEdges:=True)
                                   If coarse Is Nothing Then Return Nothing
                                   Using coarseImage = coarse.Snapshot()
                                       Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image), ("mixed", coarseImage))
                                   End Using
                               End Using
                           End Using
                       End Using
                   End Function
        End Function

        Private Const ColorNoiseShader As String = "
uniform shader src;
uniform shader mixed;
uniform float fine;
half4 main(float2 p) {
    float4 c = toBytes(src.eval(p));
    float4 m = toBytes(mixed.eval(p));
    // Rec.601-Luma ganzzahlig wie im Prozessor (x1000, abgeschnitten).
    float lumaSrc = floor((299.0 * c.r + 587.0 * c.g + 114.0 * c.b) / 1000.0 + 0.0001);
    float chroma = (abs(c.r - lumaSrc) + abs(c.b - lumaSrc)) / 2.0;
    float schutz = 1.0 - clamp((chroma - 30.0) / 60.0, 0.0, 1.0);
    float wirkung = fine * schutz;
    float lumaMix = (299.0 * m.r + 587.0 * m.g + 114.0 * m.b) / 1000.0;
    float delta = lumaSrc - lumaMix;
    float3 nb = m.rgb + delta;
    float3 o = float3(clampByte(c.r + (nb.r - c.r) * wirkung),
                      clampByte(c.g + (nb.g - c.g) * wirkung),
                      clampByte(c.b + (nb.b - c.b) * wirkung));
    return half4(half3(o / 255.0), half(c.a / 255.0));
}
"

        ' ── Fuehrungsfilter gegen Rauschen (fest und angepasst, ueber mehrere Groessen) ──
        '
        ' ZWISCHENWERTE IN HALBER GLEITKOMMA, JE WERT ZWEI KANAELE. Skia legt auf der Karte keine
        ' Zielflaeche mit 32 Bit je Kanal an (gemessen), wohl aber mit 16; die reichen alle vier
        ' Kanaele unveraendert durch, auch negative Werte. Jeder Wert steht deshalb als grober Teil
        ' (11 Bit, in halber Gleitkomma exakt) plus Rest, zusammen rund 22 Bit. Die Quadrate werden
        ' um 128 zentriert, damit sie unter dem Hoechstwert der halben Gleitkomma bleiben.

        Private Const FloatShaderCommon As String = "
float2 splitValue(float v) {
    if (v == 0.0) return float2(0.0);
    float e = floor(log2(abs(v))) + 1.0;
    float st = exp2(e - 11.0);
    float hi = floor(v / st + 0.5) * st;
    return float2(hi, v - hi);
}
float joinValue(half2 t) {
    return float(t.x) + float(t.y);
}
float lumaOf(float3 c) {
    return 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
}
"

        ''' <summary>Eine Zielflaeche fuer Zwischenwerte: halbe Gleitkomma, vier Kanaele frei.</summary>
        Private Shared Function NewFloatSurface(gr As GRContext, width As Integer, height As Integer) As SKSurface
            Dim info = New SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul)
            Return If(gr Is Nothing, SKSurface.Create(info), SKSurface.Create(gr, False, info))
        End Function

        ''' <summary>Zeichnet einen Shader in eine Zielflaeche beliebiger Groesse und Art.</summary>
        Private Shared Function DrawInto(surface As SKSurface, width As Integer, height As Integer, effect As SKRuntimeEffect,
                                         uniforms As SKRuntimeEffectUniforms,
                                         ParamArray children As (Name As String, Image As SKImage)()) As SKSurface
            If surface Is Nothing Then Return Nothing
            Dim parts = New List(Of SKShader)()
            Try
                Dim childSet = New SKRuntimeEffectChildren(effect)
                For Each child In children
                    Dim part = child.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                                                    New SKSamplingOptions(SKFilterMode.Nearest))
                    parts.Add(part)
                    childSet(child.Name) = part
                Next
                Using shader = effect.ToShader(uniforms, childSet),
                      paint = New SKPaint With {.Shader = shader, .BlendMode = SKBlendMode.Src}
                    surface.Canvas.DrawRect(0, 0, width, height, paint)
                End Using
            Finally
                For Each part In parts
                    part.Dispose()
                Next
            End Try
            Return surface
        End Function

        ''' <summary>Der Fuehrungsfilter auf der Karte: ApplyGuidedNoiseReduction, mit
        ''' <paramref name="multiScale"/> ApplyMultiScaleGuidedNoiseReduction.</summary>
        Private Shared Function GuidedNoiseReductionOnGpu(amount As Single, detail As Single, adaptive As Boolean,
                                                          multiScale As Boolean) As Func(Of GRContext, SKImage, SKSurface)
            If Not multiScale Then
                Return Function(gr, image) GuidedLevelOnGpu(gr, image, amount, detail, adaptive)
            End If
            Return Function(gr, image)
                       Dim pyramid = New List(Of SKImage) From {image}
                       Try
                           For level = 1 To MultiScaleGuidedLevels
                               Dim previous = pyramid(level - 1)
                               If Math.Min(previous.Width, previous.Height) < 2 * MultiScaleGuidedMinEdge Then Exit For
                               Using half = DownsampleHalfOnGpu(gr, previous)
                                   If half Is Nothing Then Exit For
                                   pyramid.Add(half.Snapshot())
                               End Using
                           Next
                           Dim denoised As SKSurface = Nothing
                           For level = pyramid.Count - 1 To 0 Step -1
                               Dim input = pyramid(level)
                               Dim corrected As SKImage = Nothing
                               If denoised IsNot Nothing Then
                                   Using denoisedImage = denoised.Snapshot()
                                       Using correction = CoarseCorrectionOnGpu(gr, input, denoisedImage, pyramid(level + 1))
                                           If correction Is Nothing Then Return Nothing
                                           corrected = correction.Snapshot()
                                       End Using
                                   End Using
                                   denoised.Dispose()
                                   denoised = Nothing
                                   input = corrected
                               End If
                               Try
                                   denoised = GuidedLevelOnGpu(gr, input, If(level = 0, amount * MultiScaleFineWeight, amount),
                                                               If(level = 0, detail, 0.0F), adaptive)
                                   If denoised Is Nothing Then Return Nothing
                               Finally
                                   corrected?.Dispose()
                               End Try
                           Next
                           Return denoised
                       Finally
                           For i = 1 To pyramid.Count - 1
                               pyramid(i).Dispose()
                           Next
                       End Try
                   End Function
        End Function

        ''' <summary>Eine Groesse des Fuehrungsfilters, wie ApplyGuidedNoiseReduction samt GuidedTile.
        ''' Die Kacheln dort haben einen Rand von zwei Radien; fuer jeden inneren Bildpunkt sind die
        ''' Fenstermittel damit dieselben wie ueber das ganze Bild, und so rechnet es die Karte.</summary>
        Private Shared Function GuidedLevelOnGpu(gr As GRContext, image As SKImage, amount As Single, detail As Single,
                                                 adaptive As Boolean) As SKSurface
            Dim w = image.Width, h = image.Height
            Dim epsTable As Single() = Nothing
            If adaptive Then
                Dim profile = NoiseProfileOnGpu(gr, image)
                If profile IsNot Nothing Then epsTable = AdaptiveNoiseEpsilonTable(profile, amount)
            End If
            If epsTable Is Nothing Then
                epsTable = New Single(255) {}
                Array.Fill(epsTable, GuidedNoiseEpsilon(amount))
            End If

            Dim prep = StageEffect("Fuehrung vorbereiten", FloatShaderCommon & GuidedPrepShader)
            Dim box = StageEffect("Fuehrung Kastenmittel", FloatShaderCommon & GuidedBoxShader)
            Dim coef = StageEffect("Fuehrung Gerade", FloatShaderCommon & GuidedCoefShader)
            Dim apply = StageEffect("Fuehrung anwenden", FloatShaderCommon & GuidedApplyShader)

            Dim boxMean = Function(input As SKImage) As SKSurface
                              Dim pass = Function(source As SKImage, horizontal As Boolean) As SKSurface
                                             Dim u = New SKRuntimeEffectUniforms(box)
                                             u("direction") = If(horizontal, New Single() {1, 0}, New Single() {0, 1})
                                             u("size") = New Single() {w, h}
                                             Return DrawInto(NewFloatSurface(gr, w, h), w, h, box, u, ("data", source))
                                         End Function
                              Using across = pass(input, True)
                                  If across Is Nothing Then Return Nothing
                                  Using acrossImage = across.Snapshot()
                                      Return pass(acrossImage, False)
                                  End Using
                              End Using
                          End Function

            Dim prepare = Function(mode As Single) As SKImage
                              Dim u = New SKRuntimeEffectUniforms(prep)
                              u("mode") = mode
                              Using s = DrawInto(NewFloatSurface(gr, w, h), w, h, prep, u, ("src", image))
                                  If s Is Nothing Then Return Nothing
                                  Using values = s.Snapshot()
                                      Using meanSurface = boxMean(values)
                                          Return meanSurface?.Snapshot()
                                      End Using
                                  End Using
                              End Using
                          End Function

            Using meanLuma = prepare(0.0F), meanChroma = prepare(1.0F), epsImage = EpsilonTableImage(epsTable)
                If meanLuma Is Nothing OrElse meanChroma Is Nothing Then Return Nothing
                Dim cu = New SKRuntimeEffectUniforms(coef)
                Using lines = DrawInto(NewFloatSurface(gr, w, h), w, h, coef, cu, ("means", meanLuma), ("eps", epsImage))
                    If lines Is Nothing Then Return Nothing
                    Using lineImage = lines.Snapshot(), meanLines = boxMean(lineImage)
                        If meanLines Is Nothing Then Return Nothing
                        Using meanLineImage = meanLines.Snapshot()
                            Dim au = New SKRuntimeEffectUniforms(apply)
                            au("detail") = Clamp(detail, 0, 1)
                            Return DrawStage(gr, w, h, apply, au, ("src", image), ("chroma", meanChroma), ("lines", meanLineImage))
                        End Using
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Die Schwellen je Helligkeitswert als Bild 256 x 1, je Wert zwei Kanaele.</summary>
        Private Shared Function EpsilonTableImage(table As Single()) As SKImage
            Dim data(256 * 4 - 1) As Short
            Dim one = BitConverter.HalfToInt16Bits(CType(1.0F, Half))
            For i = 0 To 255
                Dim hi = CType(table(i), Half)
                data(i * 4) = BitConverter.HalfToInt16Bits(hi)
                data(i * 4 + 1) = BitConverter.HalfToInt16Bits(CType(table(i) - CSng(hi), Half))
                data(i * 4 + 3) = one
            Next
            Using bitmap = New SKBitmap(New SKImageInfo(256, 1, SKColorType.RgbaF16, SKAlphaType.Premul))
                Runtime.InteropServices.Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length)
                Return SKImage.FromBitmap(bitmap)
            End Using
        End Function

        ''' <summary>Das Rauschprofil wie EstimateNoiseProfile: Streuung und Helligkeit je
        ''' Stichprobenblock auf der Karte, zurueckgelesen als kleines Feld, ausgewertet mit
        ''' NoiseProfileFromBlocks.</summary>
        Private Shared Function NoiseProfileOnGpu(gr As GRContext, image As SKImage) As Single()
            Dim blocksX = image.Width \ NoiseProfileBlock
            Dim blocksY = image.Height \ NoiseProfileBlock
            If blocksX < 1 OrElse blocksY < 1 Then Return Nothing
            Dim stepSize = Math.Max(1, CInt(Math.Ceiling(Math.Sqrt(CDbl(blocksX) * blocksY / NoiseProfileMaxBlocks))))
            Dim usedX = (blocksX + stepSize - 1) \ stepSize
            Dim usedY = (blocksY + stepSize - 1) \ stepSize
            Dim effect = StageEffect("Rauschprofil Bloecke", FloatShaderCommon & NoiseBlockShader)
            Dim u = New SKRuntimeEffectUniforms(effect)
            u("stepPixels") = CSng(stepSize * NoiseProfileBlock)
            Using stats = DrawInto(NewFloatSurface(gr, usedX, usedY), usedX, usedY, effect, u, ("src", image))
                If stats Is Nothing Then Return Nothing
                Dim values = New Single(usedX * usedY * 4 - 1) {}
                Dim handle = Runtime.InteropServices.GCHandle.Alloc(values, Runtime.InteropServices.GCHandleType.Pinned)
                Try
                    If Not stats.ReadPixels(New SKImageInfo(usedX, usedY, SKColorType.RgbaF32, SKAlphaType.Premul),
                                            handle.AddrOfPinnedObject(), usedX * 16, 0, 0) Then Return Nothing
                Finally
                    handle.Free()
                End Try
                Dim sigmas = New Single(usedX * usedY - 1) {}
                Dim levels = New Byte(usedX * usedY - 1) {}
                For i = 0 To sigmas.Length - 1
                    sigmas(i) = values(i * 4) + values(i * 4 + 1)
                    levels(i) = CByte(Math.Min(255, Math.Max(0, CInt(values(i * 4 + 2)))))
                Next
                Return NoiseProfileFromBlocks(sigmas, levels)
            End Using
        End Function

        ''' <summary>DownsampleHalf auf der Karte: je vier Bildpunkte gemittelt, ganzzahlig gerundet.</summary>
        Private Shared Function DownsampleHalfOnGpu(gr As GRContext, image As SKImage) As SKSurface
            Dim hw = (image.Width + 1) \ 2, hh = (image.Height + 1) \ 2
            Dim effect = StageEffect("Halbe Groesse", DownsampleHalfShader)
            Dim u = New SKRuntimeEffectUniforms(effect)
            u("size") = New Single() {image.Width, image.Height}
            Return DrawStage(gr, hw, hh, effect, u, ("src", image))
        End Function

        ''' <summary>AddCoarseCorrection auf der Karte.</summary>
        Private Shared Function CoarseCorrectionOnGpu(gr As GRContext, fine As SKImage, coarseDenoised As SKImage,
                                                      coarseOriginal As SKImage) As SKSurface
            Dim effect = StageEffect("Grobe Korrektur", CoarseCorrectionShader)
            Dim u = New SKRuntimeEffectUniforms(effect)
            u("coarseSize") = New Single() {coarseOriginal.Width, coarseOriginal.Height}
            Return DrawStage(gr, fine.Width, fine.Height, effect, u, ("src", fine), ("den", coarseDenoised), ("orig", coarseOriginal))
        End Function

        ' Je Stichprobenblock: Streuung nach Abzug der schiefen Ebene und Helligkeit, wie in
        ' EstimateNoiseProfile. Die Werte sind um 128 verschoben summiert; Streuung und Neigung
        ' haengen davon nicht ab, die Rundungsfehler der Summen werden aber kleiner.
        Private Const NoiseBlockShader As String = "
uniform shader src;
uniform float stepPixels;
half4 main(float2 p) {
    float2 origin = floor(p) * stepPixels;
    float sum = 0.0; float sumSquares = 0.0; float sumX = 0.0; float sumY = 0.0;
    for (int y = 0; y < 8; y++) {
        for (int x = 0; x < 8; x++) {
            float v = lumaOf(unpremulBytes(toBytes(src.eval(origin + float2(float(x) + 0.5, float(y) + 0.5))))) - 128.0;
            sum += v;
            sumSquares += v * v;
            sumX += (float(x) - 3.5) * v;
            sumY += (float(y) - 3.5) * v;
        }
    }
    float variance = (sumSquares - sum * sum / 64.0 - (sumX * sumX + sumY * sumY) / 336.0) / 64.0;
    float mean = sum / 64.0 + 128.0;
    float2 s = splitValue(sqrt(max(0.0, variance)));
    return half4(half(s.x), half(s.y), half(clamp(floor(mean), 0.0, 255.0)), 1.0);
}
"

        ' mode 0: Helligkeit und ihr Quadrat (um 128 zentriert); mode 1: die beiden Farbabstaende.
        Private Const GuidedPrepShader As String = "
uniform shader src;
uniform float mode;
half4 main(float2 p) {
    float3 c = unpremulBytes(toBytes(src.eval(p)));
    float y601 = lumaOf(c);
    float2 first; float2 second;
    if (mode < 0.5) {
        first = splitValue(y601);
        second = splitValue((y601 - 128.0) * (y601 - 128.0));
    } else {
        first = splitValue(c.r - y601);
        second = splitValue(c.b - y601);
    }
    return half4(half(first.x), half(first.y), half(second.x), half(second.y));
}
"

        ' Fenstermittel mit Radius 4 in einer Richtung; am Bildrand zaehlt nur, was da ist (BoxMean).
        Private Const GuidedBoxShader As String = "
uniform shader data;
uniform float2 direction;
uniform float2 size;
half4 main(float2 p) {
    float first = 0.0; float second = 0.0; float count = 0.0;
    for (int i = -4; i <= 4; i++) {
        float2 q = p + direction * float(i);
        if (q.x > 0.0 && q.y > 0.0 && q.x < size.x && q.y < size.y) {
            half4 t = data.eval(q);
            first += joinValue(t.xy);
            second += joinValue(t.zw);
            count += 1.0;
        }
    }
    float2 a = splitValue(first / count);
    float2 b = splitValue(second / count);
    return half4(half(a.x), half(a.y), half(b.x), half(b.y));
}
"

        ' Die Gerade je Bildpunkt: a = Varianz / (Varianz + Schwelle), b = (1 - a) * Mittel. Die
        ' Schwelle nach dem Mittel, gerundet wie CInt (zur geraden Zahl).
        Private Const GuidedCoefShader As String = "
uniform shader means;
uniform shader eps;
half4 main(float2 p) {
    half4 t = means.eval(p);
    float meanI = joinValue(t.xy);
    float centered = meanI - 128.0;
    float variance = max(0.0, joinValue(t.zw) - centered * centered);
    float index = clamp(roundHalfEven(meanI), 0.0, 255.0);
    float e = joinValue(eps.eval(float2(index + 0.5, 0.5)).xy);
    float a = variance / (variance + e);
    float2 sa = splitValue(a);
    float2 sb = splitValue((1.0 - a) * meanI);
    return half4(half(sa.x), half(sa.y), half(sb.x), half(sb.y));
}
"

        ' Die Anwendung wie im inneren Teil von GuidedTile.
        Private Const GuidedApplyShader As String = "
uniform shader src;
uniform shader chroma;
uniform shader lines;
uniform float detail;
half4 main(float2 p) {
    float4 b = toBytes(src.eval(p));
    float3 c = unpremulBytes(b);
    float before = lumaOf(c);
    half4 l = lines.eval(p);
    float a = joinValue(l.xy);
    float after = a * before + joinValue(l.zw);
    float shift = after - before;
    float keep = 0.0;
    if (detail > 0.0) {
        keep = clamp(abs(shift) / 16.0, 0.0, 1.0) * detail;
        shift *= (1.0 - keep);
    }
    half4 m = chroma.eval(p);
    float chromaR = c.r - before;
    float chromaB = c.b - before;
    float mixv = (1.0 - a) * (1.0 - keep);
    float newChromaR = chromaR + (joinValue(m.xy) - chromaR) * mixv;
    float newChromaB = chromaB + (joinValue(m.zw) - chromaB) * mixv;
    float newLuma = before + shift;
    float newR = newLuma + newChromaR;
    float newB = newLuma + newChromaB;
    float newG = (newLuma - 0.299 * newR - 0.114 * newB) / 0.587;
    return writePremul(float3(clampByte(newR), clampByte(newG), clampByte(newB)), b.a);
}
"

        Private Const DownsampleHalfShader As String = "
uniform shader src;
uniform float2 size;
half4 main(float2 p) {
    float2 o = floor(p) * 2.0;
    float4 s = float4(0.0);
    float n = 0.0;
    for (int dy = 0; dy < 2; dy++) {
        for (int dx = 0; dx < 2; dx++) {
            float2 q = o + float2(float(dx), float(dy));
            if (q.x < size.x && q.y < size.y) {
                float4 b = toBytes(src.eval(q + 0.5));
                s += float4(unpremulBytes(b), b.a);
                n += 1.0;
            }
        }
    }
    // (Summe + n \ 2) \ n, ganzzahlig.
    float4 r = floor((s + floor(n / 2.0)) / n + 0.0001);
    return writePremul(r.rgb, r.a);
}
"

        Private Const CoarseCorrectionShader As String = "
uniform shader src;
uniform shader den;
uniform shader orig;
uniform float2 coarseSize;
float3 diffAt(float2 c) {
    float2 q = c + 0.5;
    return unpremulBytes(toBytes(den.eval(q))) - unpremulBytes(toBytes(orig.eval(q)));
}
half4 main(float2 p) {
    float2 v = floor(p) + 0.5;
    v = v / 2.0 - 0.5;
    float2 c0 = floor(v);
    float2 t = v - c0;
    float2 a = clamp(c0, float2(0.0), coarseSize - 1.0);
    float2 b = clamp(c0 + 1.0, float2(0.0), coarseSize - 1.0);
    float3 add = diffAt(a) * (1.0 - t.x) * (1.0 - t.y) + diffAt(float2(b.x, a.y)) * t.x * (1.0 - t.y) +
                 diffAt(float2(a.x, b.y)) * (1.0 - t.x) * t.y + diffAt(b) * t.x * t.y;
    float4 f = toBytes(src.eval(p));
    float3 c = unpremulBytes(f);
    return writePremul(float3(clampByte(c.r + add.r), clampByte(c.g + add.g), clampByte(c.b + add.b)), f.a);
}
"

        ' ── Struktur und Klarheit ────────────────────────────────────────────────

        ''' <summary>ApplyStructure ueber ApplyLocalContrast: Weichzeichnung wie die
        ''' Rauschminderung mit 0,45, dann die Differenz verstaerkt zurueck.</summary>
        Private Shared Function StructureOnGpu(amount As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim factor = Clamp(amount, -1, 1) * 2.4F
            Dim sigma = 0.25F + Clamp(3.6F / 8.0F, 0, 1) * 2.2F
            Return Function(gr, image)
                       Using blurred = RasterBlurOnGpu(gr, image, sigma, clampEdges:=True)
                           If blurred Is Nothing Then Return Nothing
                           Using blurImage = blurred.Snapshot()
                               Dim effect = StageEffect("Lokalkontrast", LocalContrastShader)
                               Dim u = New SKRuntimeEffectUniforms(effect)
                               u("factor") = factor
                               Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image), ("blur", blurImage))
                           End Using
                       End Using
                   End Function
        End Function

        ' ApplyLocalContrast. Bei deckendem Bildpunkt liest der Prozessor die Weichzeichnung roh,
        ' auch wo sie am Rand teiltransparent ist; das bleibt hier so.
        Private Const LocalContrastShader As String = "
uniform shader src;
uniform shader blur;
uniform float factor;
half4 main(float2 p) {
    float4 c = toBytes(src.eval(p));
    if (c.a <= 0.0) return half4(0.0);
    float4 bl = toBytes(blur.eval(p));
    float3 cc; float3 bb;
    if (c.a >= 255.0) {
        cc = c.rgb; bb = bl.rgb;
    } else {
        cc = unpremulBytes(c);
        bb = unpremulBytes(bl);
    }
    float3 o = float3(clampByte(cc.r + (cc.r - bb.r) * factor),
                      clampByte(cc.g + (cc.g - bb.g) * factor),
                      clampByte(cc.b + (cc.b - bb.b) * factor));
    return writePremul(o, c.a);
}
"

        ''' <summary>ApplyClarity: das verkleinerte Helligkeitsraster entsteht auf der Karte, die
        ''' Geraden darauf rechnet ClarityCoefficients wie fuer den Prozessor, angewandt wird wieder
        ''' auf der Karte.</summary>
        Private Shared Function ClarityOnGpu(amount As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return Nothing
            Dim gain = If(strength > 0, strength * ClarityPositiveGain, strength)
            Return Function(gr, image)
                       Dim width = image.Width, height = image.Height
                       Dim radius = Math.Max(1.0, Math.Min(width, height) * ClarityRadiusShare)
                       Dim factor = Math.Max(1, CInt(Math.Floor(radius / 4.0)))
                       Dim smallWidth = (width + factor - 1) \ factor
                       Dim smallHeight = (height + factor - 1) \ factor
                       Dim small = CellLumaMeansOnGpu(gr, image, factor, smallWidth, smallHeight)
                       If small Is Nothing Then Return Nothing
                       Dim coefA As Single() = Nothing, coefB As Single() = Nothing
                       ClarityCoefficients(small, smallWidth, smallHeight, Math.Max(1, CInt(Math.Round(radius / factor))), coefA, coefB)
                       Using coefImage = PairImage(coefA, coefB, smallWidth, smallHeight)
                           Dim effect = StageEffect("Klarheit", FloatShaderCommon & ClarityShader)
                           Dim u = New SKRuntimeEffectUniforms(effect)
                           u("factor") = CSng(factor)
                           u("smallSize") = New Single() {smallWidth, smallHeight}
                           u("gain") = gain
                           Return DrawStage(gr, width, height, effect, u, ("src", image), ("coef", coefImage))
                       End Using
                   End Function
        End Function

        ''' <summary>Je Feld von factor x factor Bildpunkten der Mittelwert der Helligkeit, 0 bis 1,
        ''' wie in ApplyClarity. Zurueckgelesen als kleines Feld.</summary>
        Private Shared Function CellLumaMeansOnGpu(gr As GRContext, image As SKImage, factor As Integer,
                                                   smallWidth As Integer, smallHeight As Integer) As Single()
            ' Zwei Durchgaenge statt einer Doppelschleife: SkSL rollt Schleifen aus, und factor mal
            ' factor Schritte waren gemessen "program is too large".
            If factor > CellLumaMaxFactor Then Return Nothing
            Dim rowsEffect = StageEffect("Feldmittel Zeilen", FloatShaderCommon & CellLumaRowsShader)
            Dim ru = New SKRuntimeEffectUniforms(rowsEffect)
            ru("factor") = CSng(factor)
            ru("size") = New Single() {image.Width, image.Height}
            Dim effect = StageEffect("Feldmittel Helligkeit", FloatShaderCommon & CellLumaShader)
            Dim u = New SKRuntimeEffectUniforms(effect)
            u("factor") = CSng(factor)
            u("height") = CSng(image.Height)
            Using rows = DrawInto(NewFloatSurface(gr, smallWidth, image.Height), smallWidth, image.Height, rowsEffect, ru, ("src", image)),
                  rowImage = rows?.Snapshot(),
                  cells = If(rowImage Is Nothing, Nothing,
                             DrawInto(NewFloatSurface(gr, smallWidth, smallHeight), smallWidth, smallHeight, effect, u, ("rows", rowImage)))
                If cells Is Nothing Then Return Nothing
                Dim values = New Single(smallWidth * smallHeight * 4 - 1) {}
                Dim handle = Runtime.InteropServices.GCHandle.Alloc(values, Runtime.InteropServices.GCHandleType.Pinned)
                Try
                    If Not cells.ReadPixels(New SKImageInfo(smallWidth, smallHeight, SKColorType.RgbaF32, SKAlphaType.Premul),
                                            handle.AddrOfPinnedObject(), smallWidth * 16, 0, 0) Then Return Nothing
                Finally
                    handle.Free()
                End Try
                Dim small = New Single(smallWidth * smallHeight - 1) {}
                For i = 0 To small.Length - 1
                    small(i) = values(i * 4) + values(i * 4 + 1)
                Next
                Return small
            End Using
        End Function

        ''' <summary>Zwei Felder gleicher Groesse als ein Bild in halber Gleitkomma, je Wert zwei
        ''' Kanaele (grob und Rest).</summary>
        Private Shared Function PairImage(first As Single(), second As Single(), width As Integer, height As Integer) As SKImage
            Dim data(width * height * 4 - 1) As Short
            For i = 0 To width * height - 1
                Dim hi1 = CType(first(i), Half)
                Dim hi2 = CType(second(i), Half)
                data(i * 4) = BitConverter.HalfToInt16Bits(hi1)
                data(i * 4 + 1) = BitConverter.HalfToInt16Bits(CType(first(i) - CSng(hi1), Half))
                data(i * 4 + 2) = BitConverter.HalfToInt16Bits(hi2)
                data(i * 4 + 3) = BitConverter.HalfToInt16Bits(CType(second(i) - CSng(hi2), Half))
            Next
            Using bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul))
                Runtime.InteropServices.Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length)
                Return SKImage.FromBitmap(bitmap)
            End Using
        End Function

        ''' <summary>Groesste Feldkante, die die beiden Durchgaenge koennen.</summary>
        Private Const CellLumaMaxFactor As Integer = 64

        ' Erster Durchgang: je Zeile die Summe der Helligkeit ueber factor Spalten, dazu die Anzahl.
        Private Const CellLumaRowsShader As String = "
uniform shader src;
uniform float factor;
uniform float2 size;
half4 main(float2 p) {
    float left = floor(p.x) * factor;
    float sum = 0.0;
    float count = 0.0;
    for (int x = 0; x < 64; x++) {
        if (float(x) >= factor) break;
        float qx = left + float(x);
        if (qx < size.x) {
            sum += lumaOf(unpremulBytes(toBytes(src.eval(float2(qx + 0.5, p.y)))));
            count += 1.0;
        }
    }
    float2 v = splitValue(sum);
    return half4(half(v.x), half(v.y), half(count), 1.0);
}
"

        ' Zweiter Durchgang: ueber factor Zeilen, dann das Mittel auf 0 bis 1.
        Private Const CellLumaShader As String = "
uniform shader rows;
uniform float factor;
uniform float height;
half4 main(float2 p) {
    float top = floor(p.y) * factor;
    float sum = 0.0;
    float count = 0.0;
    for (int y = 0; y < 64; y++) {
        if (float(y) >= factor) break;
        float qy = top + float(y);
        if (qy < height) {
            half4 t = rows.eval(float2(p.x, qy + 0.5));
            sum += joinValue(t.xy);
            count += float(t.z);
        }
    }
    float2 v = splitValue(sum / (count * 255.0));
    return half4(half(v.x), half(v.y), 0.0, 1.0);
}
"

        Private Const ClarityShader As String = "
uniform shader src;
uniform shader coef;
uniform float factor;
uniform float2 smallSize;
uniform float gain;
float2 coefAt(float2 c) {
    half4 t = coef.eval(c + 0.5);
    return float2(joinValue(t.xy), joinValue(t.zw));
}
half4 main(float2 p) {
    float2 f = clamp(floor(p) + 0.5, float2(0.0), float2(1.0e9));
    f = clamp(f / factor - 0.5, float2(0.0), smallSize - 1.0);
    float2 c0 = floor(f);
    float2 c1 = min(smallSize - 1.0, c0 + 1.0);
    float2 t = f - c0;
    float2 v00 = coefAt(c0);
    float2 v01 = coefAt(float2(c1.x, c0.y));
    float2 v10 = coefAt(float2(c0.x, c1.y));
    float2 v11 = coefAt(c1);
    float2 v = (v00 * (1.0 - t.x) + v01 * t.x) * (1.0 - t.y) + (v10 * (1.0 - t.x) + v11 * t.x) * t.y;
    float4 b = toBytes(src.eval(p));
    float3 c = unpremulBytes(b);
    float luma = lumaOf(c) / 255.0;
    float baseLuma = v.x * luma + v.y;
    float tt = 2.0 * baseLuma - 1.0;
    float weight = max(0.0, 1.0 - tt * tt);
    float delta = (luma - baseLuma) * gain * weight * 255.0;
    return writePremul(float3(clampByte(c.r + delta), clampByte(c.g + delta), clampByte(c.b + delta)), b.a);
}
"

        ' ── Median ───────────────────────────────────────────────────────────────

        ''' <summary>ApplyMedianBlur: je Kanal der Median im Fenster (Radius 1 bis 3, am Rand
        ''' gekuerzt), auf den vormultiplizierten Bytes, Alpha bleibt. Nur Bgra8888; andere Formate
        ''' nehmen dort einen Weg ueber GetPixel.</summary>
        Private Shared Function MedianOnGpu(source As SKBitmap, amount As Single) As Func(Of GRContext, SKImage, SKSurface)
            If source.ColorType <> SKColorType.Bgra8888 Then Return Nothing
            Dim radius = Math.Max(1, CInt(Math.Round(1 + Clamp(amount, 0, 1) * 2)))
            Return Function(gr, image)
                       Dim effect = StageEffect("Median", MedianShader)
                       Dim u = New SKRuntimeEffectUniforms(effect)
                       u("radius") = CSng(radius)
                       u("size") = New Single() {image.Width, image.Height}
                       Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image))
                   End Function
        End Function

        ' Der Wert an Stelle count \ 2 der sortierten Fensterwerte, gefunden durch Halbieren ueber
        ' die 256 Tonwerte: der kleinste Wert, bis zu dem mehr als count \ 2 Werte liegen. Das ist
        ' genau HistogramMedian, ohne Histogramm.
        Private Const MedianShader As String = "
uniform shader src;
uniform float radius;
uniform float2 size;
half4 main(float2 p) {
    float count = 0.0;
    for (int dy = -3; dy <= 3; dy++) {
        for (int dx = -3; dx <= 3; dx++) {
            float2 q = p + float2(float(dx), float(dy));
            if (abs(float(dx)) <= radius && abs(float(dy)) <= radius && q.x > 0.0 && q.y > 0.0 && q.x < size.x && q.y < size.y) count += 1.0;
        }
    }
    float mid = floor(count / 2.0);
    float3 lo = float3(0.0);
    float3 hi = float3(255.0);
    for (int step = 0; step < 8; step++) {
        float3 m = floor((lo + hi) / 2.0);
        float3 below = float3(0.0);
        for (int dy = -3; dy <= 3; dy++) {
            for (int dx = -3; dx <= 3; dx++) {
                float2 q = p + float2(float(dx), float(dy));
                if (abs(float(dx)) <= radius && abs(float(dy)) <= radius && q.x > 0.0 && q.y > 0.0 && q.x < size.x && q.y < size.y) {
                    float3 v = toBytes(src.eval(q)).rgb;
                    below += float3(lessThanEqual(v, m));
                }
            }
        }
        float3 enough = float3(greaterThan(below, float3(mid)));
        hi = mix(hi, m, enough);
        lo = mix(m + 1.0, lo, enough);
    }
    half4 c = src.eval(p);
    return half4(half3(lo / 255.0), c.a);
}
"

        ' ── Dunst entfernen ──────────────────────────────────────────────────────

        ''' <summary>ApplyDehaze: die verkleinerte Kopie (Flaechenmittel) entsteht auf der Karte, die
        ''' Schaetzung von Dunstlicht und Durchlaessigkeit rechnet DehazeTransmission wie fuer den
        ''' Prozessor, zurueckgerechnet wird auf der Karte.</summary>
        Private Shared Function DehazeOnGpu(amount As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim strength = Math.Max(0.0, Math.Min(1.0, CDbl(Clamp(-amount, 0, 1))))
            If strength <= 0.001 Then Return Nothing
            Return Function(gr, image)
                       Dim width = image.Width, height = image.Height
                       Dim scale = Math.Min(1.0, DehazeWorkMaxDimension / CDbl(Math.Max(width, height)))
                       Dim lw = Math.Max(1, CInt(Math.Round(width * scale)))
                       Dim lh = Math.Max(1, CInt(Math.Round(height * scale)))
                       ' Die groesste Zelle; mehr als DehazeCellMax Punkte je Richtung kann der Shader nicht.
                       If (width + lw - 1) \ lw + 1 > DehazeCellMax OrElse (height + lh - 1) \ lh + 1 > DehazeCellMax Then Return Nothing
                       Dim lowR = New Double(lw * lh - 1) {}, lowG = New Double(lw * lh - 1) {}, lowB = New Double(lw * lh - 1) {}
                       If Not DehazeCellMeansOnGpu(gr, image, lw, lh, lowR, lowG, lowB) Then Return Nothing
                       Dim atmR, atmG, atmB As Double
                       Dim transmission = DehazeTransmission(lowR, lowG, lowB, lw, lh, strength, atmR, atmG, atmB)
                       Dim t = New Single(transmission.Length - 1) {}
                       For i = 0 To t.Length - 1
                           t(i) = CSng(transmission(i))
                       Next
                       Using tImage = PairImage(t, t, lw, lh)
                           Dim effect = StageEffect("Dunst entfernen", FloatShaderCommon & DehazeShader)
                           Dim u = New SKRuntimeEffectUniforms(effect)
                           u("lowSize") = New Single() {lw, lh}
                           u("size") = New Single() {width, height}
                           u("atmosphere") = New Single() {CSng(atmR), CSng(atmG), CSng(atmB)}
                           u("floorFactor") = CSng(1.0 - (1.0 - DehazeDarkeningFloor) * strength)
                           u("minTransmission") = CSng(DehazeMinTransmission)
                           u("toe") = CSng(DehazeToe)
                           u("shadowProtection") = CSng(DehazeShadowProtection)
                           Return DrawStage(gr, width, height, effect, u, ("src", image), ("transmission", tImage))
                       End Using
                   End Function
        End Function

        Private Const DehazeCellMax As Integer = 24

        ''' <summary>Die Flaechenmittel der verkleinerten Kopie, wie Schritt 1 von ApplyDehaze:
        ''' Zellgrenzen ganzzahlig aus lx * Breite \ lw, Mittel der entmultiplizierten Bytes.</summary>
        Private Shared Function DehazeCellMeansOnGpu(gr As GRContext, image As SKImage, lw As Integer, lh As Integer,
                                                     lowR As Double(), lowG As Double(), lowB As Double()) As Boolean
            Dim effect = StageEffect("Dunst Zellmittel", FloatShaderCommon & DehazeCellShader)
            Dim read = Function(mode As Single) As Single()
                           Dim u = New SKRuntimeEffectUniforms(effect)
                           u("mode") = mode
                           u("size") = New Single() {image.Width, image.Height}
                           u("lowSize") = New Single() {lw, lh}
                           Using cells = DrawInto(NewFloatSurface(gr, lw, lh), lw, lh, effect, u, ("src", image))
                               If cells Is Nothing Then Return Nothing
                               Dim values = New Single(lw * lh * 4 - 1) {}
                               Dim handle = Runtime.InteropServices.GCHandle.Alloc(values, Runtime.InteropServices.GCHandleType.Pinned)
                               Try
                                   If Not cells.ReadPixels(New SKImageInfo(lw, lh, SKColorType.RgbaF32, SKAlphaType.Premul),
                                                           handle.AddrOfPinnedObject(), lw * 16, 0, 0) Then Return Nothing
                               Finally
                                   handle.Free()
                               End Try
                               Return values
                           End Using
                       End Function
            Dim first = read(0.0F)
            Dim second = read(1.0F)
            If first Is Nothing OrElse second Is Nothing Then Return False
            For i = 0 To lw * lh - 1
                lowR(i) = CDbl(first(i * 4)) + first(i * 4 + 1)
                lowG(i) = CDbl(first(i * 4 + 2)) + first(i * 4 + 3)
                lowB(i) = CDbl(second(i * 4)) + second(i * 4 + 1)
            Next
            Return True
        End Function

        ' mode 0: Rot und Gruen, mode 1: Blau; je als Mittel 0 bis 1, zwei Kanaele je Wert. Die
        ' Summen sind ganzzahlig und bleiben unter 2^24, also exakt.
        Private Const DehazeCellShader As String = "
uniform shader src;
uniform float mode;
uniform float2 size;
uniform float2 lowSize;
half4 main(float2 p) {
    float2 c = floor(p);
    float2 a = floor(c * size / lowSize + 0.0001);
    float2 b = max(a + 1.0, floor((c + 1.0) * size / lowSize + 0.0001));
    float3 sum = float3(0.0);
    float n = 0.0;
    for (int y = 0; y < 24; y++) {
        float qy = a.y + float(y);
        if (qy >= b.y) break;
        for (int x = 0; x < 24; x++) {
            float qx = a.x + float(x);
            if (qx >= b.x) break;
            sum += unpremulBytes(toBytes(src.eval(float2(qx + 0.5, qy + 0.5))));
            n += 1.0;
        }
    }
    float3 m = sum / (255.0 * n);
    float2 first = splitValue(mode < 0.5 ? m.r : m.b);
    float2 second = splitValue(mode < 0.5 ? m.g : 0.0);
    return half4(half(first.x), half(first.y), half(second.x), half(second.y));
}
"

        Private Const DehazeShader As String = "
uniform shader src;
uniform shader transmission;
uniform float2 lowSize;
uniform float2 size;
uniform float3 atmosphere;
uniform float floorFactor;
uniform float minTransmission;
uniform float toe;
uniform float shadowProtection;
float tAt(float2 c) {
    return joinValue(transmission.eval(c + 0.5).xy);
}
float channel(float value, float atm, float t, float weight) {
    float c = value / 255.0;
    float j = (c - atm) / t + atm;
    if (j < toe) j = toe * exp((j - toe) / toe);
    j = c + (j - c) * weight;
    j = max(j, c * floorFactor * (1.0 - weight));
    return clampByte(j * 255.0);
}
half4 main(float2 p) {
    float2 f = clamp((floor(p) + 0.5) * lowSize / size - 0.5, float2(0.0), lowSize - 1.0);
    float2 c0 = floor(f);
    float2 c1 = min(lowSize - 1.0, c0 + 1.0);
    float2 w = f - c0;
    float t = (tAt(c0) * (1.0 - w.x) + tAt(float2(c1.x, c0.y)) * w.x) * (1.0 - w.y) +
              (tAt(float2(c0.x, c1.y)) * (1.0 - w.x) + tAt(c1) * w.x) * w.y;
    t = max(minTransmission, min(1.0, t));
    float4 b = toBytes(src.eval(p));
    float3 cc = unpremulBytes(b);
    float luminance = (0.2126 * cc.r + 0.7152 * cc.g + 0.0722 * cc.b) / 255.0;
    float u = min(1.0, luminance / shadowProtection);
    float weight = u * u * (3.0 - 2.0 * u);
    float3 o = float3(channel(cc.r, atmosphere.r, t, weight), channel(cc.g, atmosphere.g, t, weight), channel(cc.b, atmosphere.b, t, weight));
    return writePremul(o, b.a);
}
"

        ' ── Gluehen, Filter "weich", Tilt-Shift, Staub und Kratzer ────────────────

        ''' <summary>ApplyImageGlow: Weichzeichnung wie die Rauschminderung mit 0,8, aufaddiert oder
        ''' abgezogen.</summary>
        Private Shared Function GlowOnGpu(amount As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return Nothing
            Dim sigma = 0.25F + 0.8F * 2.2F
            Dim sv = strength * 0.55F
            Return Function(gr, image)
                       Using blurred = RasterBlurOnGpu(gr, image, sigma, clampEdges:=True)
                           If blurred Is Nothing Then Return Nothing
                           Using blurImage = blurred.Snapshot()
                               Dim effect = StageEffect("Gluehen", GlowShader)
                               Dim u = New SKRuntimeEffectUniforms(effect)
                               u("sv") = sv
                               Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image), ("blur", blurImage))
                           End Using
                       End Using
                   End Function
        End Function

        ' sv positiv: c + b * sv; negativ: c - b * |sv|. Beides ist c + b * sv.
        Private Const GlowShader As String = "
uniform shader src;
uniform shader blur;
uniform float sv;
half4 main(float2 p) {
    float4 c = toBytes(src.eval(p));
    float3 cc = unpremulBytes(c);
    float3 bb = unpremulBytes(toBytes(blur.eval(p)));
    float3 o = float3(clampByte(cc.r + bb.r * sv), clampByte(cc.g + bb.g * sv), clampByte(cc.b + bb.b * sv));
    return writePremul(o, c.a);
}
"

        ''' <summary>ApplySoftFocusBlur: die Weichzeichnung (Rauschminderung 0,3) mit der Staerke als
        ''' Deckkraft ueber das Bild gelegt. Die Mischung macht der Zeichenweg der Karte; gegen
        ''' Skias Prozessor-Mischung kann ein Kanal um eine Stufe anders runden.</summary>
        Private Shared Function SoftFocusOnGpu(strength As Single) As Func(Of GRContext, SKImage, SKSurface)
            strength = Clamp(strength, 0, 1)
            If strength <= 0 Then Return Nothing
            Dim sigma = 0.25F + 0.3F * 2.2F
            Return Function(gr, image)
                       Using blurred = RasterBlurOnGpu(gr, image, sigma, clampEdges:=True)
                           If blurred Is Nothing Then Return Nothing
                           Using blurImage = blurred.Snapshot()
                               Return OverlayOnGpu(gr, image, blurImage, ClampToByte(255 * strength))
                           End Using
                       End Using
                   End Function
        End Function

        ''' <summary>Bild, darueber ein zweites mit Deckkraft, wie canvas.DrawBitmap mit Paint-Alpha
        ''' auf dem Prozessor. Skia nimmt dafuer dort nicht die Pipeline, sondern seinen Zeilen-
        ''' Blitter fuer 32-Bit-Bilder (SkBlendARGB32 in SkBlitRow_D32.cpp, m119):
        ''' A = Deckkraft + 1, Zielgewicht aus 0xFFFF - Quellalpha * A, alles ganzzahlig und um 8 Bit
        ''' verschoben. Gemessen an 4000 Zufallsfaellen trifft der Nachbau jeden ausser Deckkraft 0,
        ''' wo Skia gar nicht zeichnet. Die Mischung in Gleitkomma lag im Mittel 0,3 Stufen daneben.</summary>
        Private Shared Function OverlayOnGpu(gr As GRContext, image As SKImage, overlay As SKImage, alpha As Byte) As SKSurface
            Dim effect = StageEffect("Ueberblenden", OverlayShader)
            Dim u = New SKRuntimeEffectUniforms(effect)
            u("alpha") = CSng(alpha)
            Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image), ("overlay", overlay))
        End Function

        Private Const OverlayShader As String = "
uniform shader src;
uniform shader overlay;
uniform float alpha;
half4 main(float2 p) {
    half4 dst = src.eval(p);
    if (alpha <= 0.0) return dst;
    float4 d = toBytes(dst);
    float4 s = toBytes(overlay.eval(p));
    float scaleA = alpha + 1.0;
    float inv = 65535.0 - s.a * scaleA;
    float dstScale = floor((inv + floor(inv / 256.0)) / 256.0);
    float4 o = floor((s * scaleA + d * dstScale) / 256.0);
    return half4(min(o, float4(255.0)) / 255.0);
}
"

        ''' <summary>ApplyTiltShift: zwei Weichzeichnungen mit fortgesetztem Rand, je Bildpunkt nach
        ''' dem Abstand zum Band gemischt, auf allen vier Bytes.</summary>
        Private Shared Function TiltShiftOnGpu(source As SKBitmap, amount As Single, centerXPercent As Single, centerYPercent As Single,
                                               angleDegrees As Single, focus As Single, transition As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim a = Clamp(amount, 0, 1)
            Dim w = source.Width, h = source.Height
            Dim sigma = Math.Min(w, h) * TiltShiftSigmaShare * a
            If a <= 0 OrElse sigma < 0.5F Then Return Nothing
            If Not RasterBlurSupported(sigma) Then Return Nothing
            Dim theta = Clamp(angleDegrees, -90, 90) * CSng(Math.PI) / 180.0F
            Dim nx = CSng(Math.Sin(theta)), ny = CSng(Math.Cos(theta))
            Dim extent = Math.Max(1.0F, Math.Abs(w * nx) + Math.Abs(h * ny))
            Dim cx = w * Clamp(centerXPercent, 0, 100) / 100.0F
            Dim cy = h * Clamp(centerYPercent, 0, 100) / 100.0F
            Dim halfBand = Clamp(focus, 0, 100) / 100.0F * 0.5F
            Dim ramp = Math.Max(0.01F, Clamp(transition, 0, 100) / 100.0F * 0.5F)
            Dim midSigma = sigma * TiltShiftMidShare
            Return Function(gr, image)
                       Using full = RasterBlurOnGpu(gr, image, sigma, clampEdges:=True)
                           If full Is Nothing Then Return Nothing
                           Using mid = If(midSigma >= 0.5F, RasterBlurOnGpu(gr, image, midSigma, clampEdges:=True), Nothing)
                               Using fullImage = full.Snapshot(), midImage = If(mid IsNot Nothing, mid.Snapshot(), Nothing)
                                   Dim effect = StageEffect("Tilt-Shift", TiltShiftShader)
                                   Dim u = New SKRuntimeEffectUniforms(effect)
                                   u("normal") = New Single() {nx, ny}
                                   u("center") = New Single() {cx, cy}
                                   u("extent") = extent
                                   u("halfBand") = halfBand
                                   u("ramp") = ramp
                                   Return DrawStage(gr, w, h, effect, u, ("src", image), ("low", If(midImage, image)), ("full", fullImage))
                               End Using
                           End Using
                       End Using
                   End Function
        End Function

        Private Const TiltShiftShader As String = "
uniform shader src;
uniform shader low;
uniform shader full;
uniform float2 normal;
uniform float2 center;
uniform float extent;
uniform float halfBand;
uniform float ramp;
half4 main(float2 p) {
    float2 c = floor(p) + 0.5;
    float u = abs((c.x - center.x) * normal.x + (c.y - center.y) * normal.y) / extent;
    float t = clamp((u - halfBand) / ramp, 0.0, 1.0);
    half4 s = src.eval(p);
    if (t <= 0.0) return s;
    t = t * t * (3.0 - 2.0 * t);
    float4 f; float4 g; float k;
    if (t < 0.5) {
        f = toBytes(s); g = toBytes(low.eval(p)); k = t * 2.0;
    } else {
        f = toBytes(low.eval(p)); g = toBytes(full.eval(p)); k = (t - 0.5) * 2.0;
    }
    float4 o = float4(clampByte(f.r + (g.r - f.r) * k), clampByte(f.g + (g.g - f.g) * k),
                      clampByte(f.b + (g.b - f.b) * k), clampByte(f.a + (g.a - f.a) * k));
    return half4(o / 255.0);
}
"

        ''' <summary>ApplyDustScratches. Entfernen: Median, mit der Staerke als Deckkraft darueber.
        ''' Hinzufuegen: dieselben Staubkoerner und Kratzer aus demselben Zufallsstrom, gezeichnet auf
        ''' der Karte. Ihre Kanten glaettet die Karte etwas anders als Skia auf dem Prozessor; die
        ''' Koerner sind einen bis zwei Punkte gross, dort kann ein Wert sichtbar anders ausfallen.</summary>
        Private Shared Function DustScratchesOnGpu(source As SKBitmap, amount As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return Nothing
            If strength < 0 Then
                Dim median = MedianOnGpu(source, -strength)
                If median Is Nothing Then Return Nothing
                Return Function(gr, image)
                           Using medianSurface = median(gr, image)
                               If medianSurface Is Nothing Then Return Nothing
                               Using medianImage = medianSurface.Snapshot()
                                   Return OverlayOnGpu(gr, image, medianImage, ClampToByte(255.0F * -strength))
                               End Using
                           End Using
                       End Function
            End If
            Dim width = source.Width, height = source.Height
            Return Function(gr, image)
                       Dim surface = NewStageSurface(gr, width, height)
                       If surface Is Nothing Then Return Nothing
                       Using copyPaint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                           surface.Canvas.DrawImage(image, 0, 0, copyPaint)
                       End Using
                       DrawDustAndScratches(surface.Canvas, width, height, strength)
                       Return surface
                   End Function
        End Function

        ' ── Grobes Farbrauschen (ApplyMultiScaleDenoise) ─────────────────────────

        ''' <summary>ApplyMultiScaleDenoise: die beiden Farbdifferenzen ueber bis zu sieben Stufen
        ''' (Kastenmittel mit Radius 1 bis 64, Rand fortgesetzt) zerlegt und je Stufe geschrumpft.
        ''' Entmultipliziert wird mit derselben gemessenen Tabelle wie dort.</summary>
        Private Shared Function MultiScaleDenoiseOnGpu(strength As Single, coarse As Single) As Func(Of GRContext, SKImage, SKSurface)
            If strength <= 0.001F Then Return Nothing
            Dim steps = Math.Max(3, Math.Min(7, CInt(Math.Round(3.0 + coarse * 4.0))))
            Dim threshold = 1.0F + 19.0F * CSng(Math.Pow(strength, 1.5))
            Return Function(gr, image)
                       Dim w = image.Width, h = image.Height
                       If w < 8 OrElse h < 8 Then Return Nothing
                       Dim prep = StageEffect("Farbstufen zerlegen", FloatShaderCommon & ChromaSplitShader)
                       Dim box = StageEffect("Farbstufen Kasten", FloatShaderCommon & ChromaBoxShader)
                       Dim shrink = StageEffect("Farbstufen schrumpfen", FloatShaderCommon & ChromaShrinkShader)
                       Dim join = StageEffect("Farbstufen zusammensetzen", FloatShaderCommon & ChromaJoinShader)
                       Using table = UnpremultiplyTableImage()
                           Dim baseline = DrawInto(NewFloatSurface(gr, w, h), w, h, prep, New SKRuntimeEffectUniforms(prep), ("src", image), ("unpremulTable", table))
                           If baseline Is Nothing Then Return Nothing
                           ' Das Ergebnis der Stufen beginnt bei null.
                           Dim result = NewFloatSurface(gr, w, h)
                           Try
                               If result Is Nothing Then Return Nothing
                               result.Canvas.Clear(SKColors.Transparent)
                               For level = 1 To steps
                                   Dim radius = CInt(Math.Pow(2, level - 1))
                                   Dim boxPass = Function(input As SKImage, horizontal As Boolean) As SKSurface
                                                     Dim u = New SKRuntimeEffectUniforms(box)
                                                     u("direction") = If(horizontal, New Single() {1, 0}, New Single() {0, 1})
                                                     u("radius") = CSng(radius)
                                                     u("size") = New Single() {w, h}
                                                     Return DrawInto(NewFloatSurface(gr, w, h), w, h, box, u, ("data", input))
                                                 End Function
                                   Dim smoothed As SKSurface
                                   Using baseImage = baseline.Snapshot()
                                       Using across = boxPass(baseImage, True)
                                           If across Is Nothing Then Return Nothing
                                           Using acrossImage = across.Snapshot()
                                               smoothed = boxPass(acrossImage, False)
                                           End Using
                                       End Using
                                       If smoothed Is Nothing Then Return Nothing
                                       Using smoothImage = smoothed.Snapshot(), resultImage = result.Snapshot()
                                           Dim su = New SKRuntimeEffectUniforms(shrink)
                                           su("threshold") = threshold
                                           Dim nextResult = DrawInto(NewFloatSurface(gr, w, h), w, h, shrink, su,
                                                                     ("baseline", baseImage), ("smoothed", smoothImage), ("result", resultImage))
                                           If nextResult Is Nothing Then smoothed.Dispose() : Return Nothing
                                           result.Dispose()
                                           result = nextResult
                                       End Using
                                   End Using
                                   baseline.Dispose()
                                   baseline = smoothed
                               Next
                               Using baseImage = baseline.Snapshot(), resultImage = result.Snapshot()
                                   Return DrawStage(gr, w, h, join, New SKRuntimeEffectUniforms(join),
                                                    ("src", image), ("unpremulTable", table), ("baseline", baseImage), ("result", resultImage))
                               End Using
                           Finally
                               baseline?.Dispose()
                               result?.Dispose()
                           End Try
                       End Using
                   End Function
        End Function

        ''' <summary>Skias gemessene Entmultiplikation (UnpremultiplyByte) als Bild 256 x 256:
        ''' Zeile Alpha, Spalte Wert, Ergebnis im Rotkanal.</summary>
        Private Shared Function UnpremultiplyTableImage() As SKImage
            Dim table = _unpremultiplyTable.Value
            Dim data = New Byte(256 * 256 * 4 - 1) {}
            For i = 0 To 256 * 256 - 1
                data(i * 4) = table(i)
                data(i * 4 + 3) = 255
            Next
            Using bitmap = New SKBitmap(New SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul))
                Runtime.InteropServices.Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length)
                Return SKImage.FromBitmap(bitmap)
            End Using
        End Function

        Private Const ChromaTableCommon As String = "
float unpremulByTable(float v, float a) {
    return floor(float(unpremulTable.eval(float2(v + 0.5, a + 0.5)).r) * 255.0 + 0.5);
}
float3 unpremulPixel(float4 b) {
    return float3(unpremulByTable(b.r, b.a), unpremulByTable(b.g, b.a), unpremulByTable(b.b, b.a));
}
"

        ' Ausgang: Cb und Cr, je zwei Kanaele.
        Private Const ChromaSplitShader As String = "
uniform shader src;
uniform shader unpremulTable;
" & ChromaTableCommon & "
half4 main(float2 p) {
    float3 c = unpremulPixel(toBytes(src.eval(p)));
    float y = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
    float2 cb = splitValue(c.b - y);
    float2 cr = splitValue(c.r - y);
    return half4(half(cb.x), half(cb.y), half(cr.x), half(cr.y));
}
"

        ' BoxBlur: festes Fenster 2r + 1, am Rand auf den letzten Punkt geklemmt.
        Private Const ChromaBoxShader As String = "
uniform shader data;
uniform float2 direction;
uniform float radius;
uniform float2 size;
half4 main(float2 p) {
    float2 sum = float2(0.0);
    for (int i = -64; i <= 64; i++) {
        if (abs(float(i)) > radius) continue;
        half4 t = data.eval(clamp(p + direction * float(i), float2(0.5), size - 0.5));
        sum += float2(joinValue(t.xy), joinValue(t.zw));
    }
    sum /= (2.0 * radius + 1.0);
    float2 a = splitValue(sum.x);
    float2 b = splitValue(sum.y);
    return half4(half(a.x), half(a.y), half(b.x), half(b.y));
}
"

        Private Const ChromaShrinkShader As String = "
uniform shader baseline;
uniform shader smoothed;
uniform shader result;
uniform float threshold;
float shrinkValue(float x) {
    float a = abs(x);
    if (a <= threshold) return 0.0;
    if (a >= threshold * 1.5) return x;
    float soft = x - threshold * threshold / x;
    float t = (a - threshold) / (threshold * 0.5);
    return soft * (1.0 - t) + x * t;
}
half4 main(float2 p) {
    half4 b = baseline.eval(p);
    half4 s = smoothed.eval(p);
    half4 r = result.eval(p);
    float cb = joinValue(r.xy) + shrinkValue(joinValue(b.xy) - joinValue(s.xy));
    float cr = joinValue(r.zw) + shrinkValue(joinValue(b.zw) - joinValue(s.zw));
    float2 a = splitValue(cb);
    float2 c = splitValue(cr);
    return half4(half(a.x), half(a.y), half(c.x), half(c.y));
}
"

        ' Zusammensetzen wie dort: Helligkeit vom Original, Farbe aus Stufen und Rest, KlemmeByte
        ' (Math.Round, zur geraden Zahl), Premultiplizieren wie SetPixel (SkMulDiv255Round).
        Private Const ChromaJoinShader As String = "
uniform shader src;
uniform shader unpremulTable;
uniform shader baseline;
uniform shader result;
" & ChromaTableCommon & "
float premulByte(float v, float a) {
    float product = v * a + 128.0;
    return floor((product + floor(product / 256.0)) / 256.0);
}
half4 main(float2 p) {
    float4 b = toBytes(src.eval(p));
    float3 c = unpremulPixel(b);
    float y = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
    half4 bl = baseline.eval(p);
    half4 rs = result.eval(p);
    float cb = joinValue(rs.xy) + joinValue(bl.xy);
    float cr = joinValue(rs.zw) + joinValue(bl.zw);
    float r = y + cr;
    float bb = y + cb;
    float g = (y - 0.299 * r - 0.114 * bb) / 0.587;
    float3 o = clamp(float3(roundHalfEven(r), roundHalfEven(g), roundHalfEven(bb)), 0.0, 255.0);
    return half4(half3(float3(premulByte(o.r, b.a), premulByte(o.g, b.a), premulByte(o.b, b.a)) / 255.0), half(b.a / 255.0));
}
"

        ' ── Koernung, Rauschen hinzufuegen, Farbflecken ──────────────────────────
        '
        ' Alle drei ziehen ihren Zufall nur aus der Bildgroesse: die Koernung aus einem Ortshash
        ' (GrainNoiseAt), die beiden anderen aus System.Random mit festem Startwert. Skias
        ' Laufzeit-Shader erlauben weder Bitoperationen noch vorzeichenlose Ganzzahlen (gemessen),
        ' der Hash laesst sich dort also nicht rechnen, der Zufallsstrom ohnehin nicht. Die Felder
        ' entstehen deshalb einmal auf dem Prozessor, genau wie dort, und bleiben als Textur auf der
        ' Karte, solange die Groesse gleich bleibt. Je Wert 16 Bit Festkomma: Fehler hoechstens
        ' 1/65535 des Einheitswerts, bei der staerksten Stufe ein Tausendstel Tonstufe.

        Private Shared ReadOnly _noiseFields As New Dictionary(Of String, SKImage)()

        ''' <summary>Ein Rauschfeld aus dem Zwischenspeicher oder frisch gebaut. Nur auf dem GPU-Faden
        ''' (oder im Pruefmodus auf dem rufenden) angefasst; je Art bleibt ein Feld liegen.</summary>
        Private Shared Function NoiseFieldTexture(gr As GRContext, kind As String, key As String,
                                                  build As Func(Of SKImage)) As SKImage
            Dim slot = kind & If(gr Is Nothing, ":raster", ":gpu")
            SyncLock _noiseFields
                Dim existing As SKImage = Nothing
                If _noiseFields.TryGetValue(slot & "|" & key, existing) Then Return existing
                ' Ein Feld anderer Groesse derselben Art faellt heraus.
                For Each stale In _noiseFields.Keys.Where(Function(k) k.StartsWith(slot & "|", StringComparison.Ordinal)).ToList()
                    _noiseFields(stale).Dispose()
                    _noiseFields.Remove(stale)
                Next
                Dim raster = build()
                If raster Is Nothing Then Return Nothing
                Dim image = raster
                If gr IsNot Nothing Then
                    image = raster.ToTextureImage(gr)
                    raster.Dispose()
                    If image Is Nothing Then Return Nothing
                End If
                _noiseFields(slot & "|" & key) = image
                Return image
            End SyncLock
        End Function

        ''' <summary>Werte -1 bis 1 als 16 Bit Festkomma, zwei Werte je Bildpunkt (Rot/Gruen und
        ''' Blau/Alpha je hohes und niedriges Byte).</summary>
        Private Shared Function NoisePairImage(width As Integer, height As Integer,
                                               fill As Action(Of Integer, Double(), Double())) As SKImage
            Dim data = New Byte(width * height * 4 - 1) {}
            ForEachRow(width, height,
                Sub(y)
                    Dim first = New Double(width - 1) {}
                    Dim second = New Double(width - 1) {}
                    fill(y, first, second)
                    For x = 0 To width - 1
                        Dim o = (y * width + x) * 4
                        Dim q1 = CInt(Math.Round((Math.Max(-1.0, Math.Min(1.0, first(x))) + 1.0) * 32767.5))
                        Dim q2 = CInt(Math.Round((Math.Max(-1.0, Math.Min(1.0, second(x))) + 1.0) * 32767.5))
                        data(o) = CByte(q1 >> 8) : data(o + 1) = CByte(q1 And 255)
                        data(o + 2) = CByte(q2 >> 8) : data(o + 3) = CByte(q2 And 255)
                    Next
                End Sub)
            Using bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                Runtime.InteropServices.Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length)
                Return SKImage.FromBitmap(bitmap)
            End Using
        End Function

        ''' <summary>Rauschfelder aus einem System.Random-Strom, in der Reihenfolge des
        ''' Prozessorwegs: zeilenweise, je Punkt <paramref name="perPixel"/> Zuege (NextDouble mal 2
        ''' minus 1). Der Strom laesst sich nicht vorspulen, gebaut wird deshalb seriell und direkt in
        ''' die Bytes; je Bild zwei Werte, also ein Bild je zwei Zuege.</summary>
        Private Shared Function RandomStreamImages(seed As Integer, width As Integer, height As Integer,
                                                   perPixel As Integer) As SKImage()
            Dim imageCount = (perPixel + 1) \ 2
            Dim data = New Byte(imageCount - 1)() {}
            For k = 0 To imageCount - 1
                data(k) = New Byte(width * height * 4 - 1) {}
            Next
            Dim random = New Random(seed)
            For y = 0 To height - 1
                For x = 0 To width - 1
                    Dim o = (y * width + x) * 4
                    For d = 0 To perPixel - 1
                        Dim v = random.NextDouble() * 2.0 - 1.0
                        Dim q = CInt(Math.Round((v + 1.0) * 32767.5))
                        Dim target = data(d \ 2)
                        Dim at = o + (d And 1) * 2
                        target(at) = CByte(q >> 8) : target(at + 1) = CByte(q And 255)
                    Next
                Next
            Next
            Dim images = New SKImage(imageCount - 1) {}
            For k = 0 To imageCount - 1
                Using bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                    Runtime.InteropServices.Marshal.Copy(data(k), 0, bitmap.GetPixels(), data(k).Length)
                    images(k) = SKImage.FromBitmap(bitmap)
                End Using
            Next
            Return images
        End Function

        Private Const NoiseFieldCommon As String = "
float noiseValue(half2 t) {
    float q = floor(float(t.x) * 255.0 + 0.5) * 256.0 + floor(float(t.y) * 255.0 + 0.5);
    return q / 32767.5 - 1.0;
}
"

        ''' <summary>ApplyGrain, fein und strukturiert. Das Zellfeld traegt je Zelle den Kornwert und,
        ''' mit Farbe, die drei Kanalabweichungen; die Rauheit hat ein eigenes Feld je 16er-Zelle.</summary>
        Private Shared Function GrainOnGpu(source As SKBitmap, amount As Single, sizeAmount As Single,
                                           freqAmount As Single, colorAmount As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return Nothing
            Dim textured = Not (sizeAmount <= 0 AndAlso freqAmount <= 0 AndAlso colorAmount <= 0)
            Dim cell = If(textured, 1 + CInt(Math.Round(Clamp(sizeAmount, 0, 1) * 5)), 1)
            Dim freq = If(textured, Clamp(freqAmount, 0, 1), 0.0F)
            Dim colorMix = If(textured, CDbl(Clamp(colorAmount, 0, 1)), 0.0)
            Dim colorNorm = If(colorMix <= 0, 1.0,
                               1.0 / Math.Sqrt((1.0 - colorMix) * (1.0 - colorMix) + colorMix * colorMix))
            Dim amplitude = 8.0 + strength * 34.0
            Return Function(gr, image)
                       Dim w = image.Width, h = image.Height
                       Dim seed = w * 397 Xor h * 151
                       Dim gridW = (w + cell - 1) \ cell, gridH = (h + cell - 1) \ cell
                       Const ModCell As Integer = 16
                       Dim modW = (w + ModCell - 1) \ ModCell, modH = (h + ModCell - 1) \ ModCell
                       Dim cells = NoiseFieldTexture(gr, "Koernung Zellen", $"{w}x{h}:{cell}",
                           Function() NoisePairImage(gridW, gridH,
                               Sub(gy, first, second)
                                   For gx = 0 To gridW - 1
                                       Dim index = gy * gridW + gx
                                       first(gx) = GrainNoiseAt(index, seed)
                                       second(gx) = CellChannelNoise(index, 1)
                                   Next
                               End Sub))
                       Dim colors = If(colorMix <= 0, cells, NoiseFieldTexture(gr, "Koernung Farbe", $"{w}x{h}:{cell}",
                           Function() NoisePairImage(gridW, gridH,
                               Sub(gy, first, second)
                                   For gx = 0 To gridW - 1
                                       Dim index = gy * gridW + gx
                                       first(gx) = CellChannelNoise(index, 2)
                                       second(gx) = CellChannelNoise(index, 3)
                                   Next
                               End Sub)))
                       Dim modulation = If(freq <= 0, cells, NoiseFieldTexture(gr, "Koernung Rauheit", $"{w}x{h}",
                           Function() NoisePairImage(modW, modH,
                               Sub(my, first, second)
                                   For mx = 0 To modW - 1
                                       first(mx) = GrainNoiseAt(my * modW + mx, seed Xor &H5BF03635)
                                   Next
                               End Sub)))
                       If cells Is Nothing OrElse colors Is Nothing OrElse modulation Is Nothing Then Return Nothing
                       Dim effect = StageEffect("Koernung", NoiseFieldCommon & GrainShader)
                       Dim u = New SKRuntimeEffectUniforms(effect)
                       u("cell") = CSng(cell)
                       u("amplitude") = CSng(amplitude)
                       u("freq") = freq
                       u("colorMix") = CSng(colorMix)
                       u("colorScale") = CSng(amplitude * colorNorm)
                       Return DrawStage(gr, w, h, effect, u, ("src", image), ("cells", cells), ("colors", colors), ("modulation", modulation))
                   End Function
        End Function

        Private Const GrainShader As String = "
uniform shader src;
uniform shader cells;
uniform shader colors;
uniform shader modulation;
uniform float cell;
uniform float amplitude;
uniform float freq;
uniform float colorMix;
uniform float colorScale;
half4 main(float2 p) {
    float4 b = toBytes(src.eval(p));
    float3 c = unpremulBytes(b);
    float2 pix = floor(p);
    float2 cellPos = floor(pix / cell + 0.0001) + 0.5;
    half4 ct = cells.eval(cellPos);
    float n = noiseValue(ct.xy);
    float amp = amplitude;
    if (freq > 0.0) {
        float m = noiseValue(modulation.eval(floor(pix / 16.0 + 0.0001) + 0.5).xy);
        amp = amplitude * (1.0 + freq * m * 0.9);
    }
    float3 o;
    if (colorMix <= 0.0) {
        float noise = n * amp;
        o = float3(clampByte(c.r + noise), clampByte(c.g + noise), clampByte(c.b + noise));
    } else {
        half4 cc = colors.eval(cellPos);
        float3 dev = float3(noiseValue(ct.zw), noiseValue(cc.xy), noiseValue(cc.zw));
        float mono = (1.0 - colorMix) * n;
        float scale = amp / amplitude * colorScale;
        o = float3(clampByte(c.r + (mono + colorMix * dev.r) * scale),
                   clampByte(c.g + (mono + colorMix * dev.g) * scale),
                   clampByte(c.b + (mono + colorMix * dev.b) * scale));
    }
    return writePremul(o, b.a);
}
"

        ''' <summary>ApplyAddNoise: je Kanal ein Zufallswert aus dem Strom, Zeile fuer Zeile.</summary>
        Private Shared Function AddNoiseOnGpu(amount As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return Nothing
            Return Function(gr, image)
                       Dim w = image.Width, h = image.Height
                       ' Beide Felder kommen aus EINEM Strom; gebaut werden sie zusammen, gehalten
                       ' einzeln.
                       Dim built As SKImage() = Nothing
                       Dim part = Function(index As Integer) As SKImage
                                      If built Is Nothing Then built = RandomStreamImages(w * 541 Xor h * 877, w, h, 3)
                                      Dim image0 = built(index)
                                      built(index) = Nothing
                                      Return image0
                                  End Function
                       Dim first = NoiseFieldTexture(gr, "Rauschen Rot Gruen", $"{w}x{h}", Function() part(0))
                       Dim second = NoiseFieldTexture(gr, "Rauschen Blau", $"{w}x{h}", Function() part(1))
                       If built IsNot Nothing Then
                           For Each leftover In built
                               leftover?.Dispose()
                           Next
                       End If
                       If first Is Nothing OrElse second Is Nothing Then Return Nothing
                       Dim effect = StageEffect("Rauschen hinzufuegen", NoiseFieldCommon & AddNoiseShader)
                       Dim u = New SKRuntimeEffectUniforms(effect)
                       u("amplitude") = CSng(strength * 72.0)
                       Return DrawStage(gr, w, h, effect, u, ("src", image), ("first", first), ("second", second))
                   End Function
        End Function

        Private Const AddNoiseShader As String = "
uniform shader src;
uniform shader first;
uniform shader second;
uniform float amplitude;
half4 main(float2 p) {
    float4 b = toBytes(src.eval(p));
    float3 c = unpremulBytes(b);
    half4 f = first.eval(p);
    float3 noise = float3(noiseValue(f.xy), noiseValue(f.zw), noiseValue(second.eval(p).xy)) * amplitude;
    return writePremul(float3(clampByte(c.r + noise.r), clampByte(c.g + noise.g), clampByte(c.b + noise.b)), b.a);
}
"

        ''' <summary>ApplyColorNoiseAdd: Rot und Blau aus dem Strom, Gruen haelt die Helligkeit.</summary>
        Private Shared Function ColorNoiseAddOnGpu(amount As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return Nothing
            Return Function(gr, image)
                       Dim w = image.Width, h = image.Height
                       Dim field = NoiseFieldTexture(gr, "Farbflecken", $"{w}x{h}",
                                                     Function() RandomStreamImages(w * 733 Xor h * 397, w, h, 2)(0))
                       If field Is Nothing Then Return Nothing
                       Dim effect = StageEffect("Farbflecken", NoiseFieldCommon & ColorNoiseAddShader)
                       Dim u = New SKRuntimeEffectUniforms(effect)
                       u("amplitude") = CSng(strength * 48.0)
                       Return DrawStage(gr, w, h, effect, u, ("src", image), ("field", field))
                   End Function
        End Function

        Private Const ColorNoiseAddShader As String = "
uniform shader src;
uniform shader field;
uniform float amplitude;
half4 main(float2 p) {
    float4 b = toBytes(src.eval(p));
    float3 c = unpremulBytes(b);
    half4 f = field.eval(p);
    float dr = noiseValue(f.xy) * amplitude;
    float db = noiseValue(f.zw) * amplitude;
    float dg = -(0.299 * dr + 0.114 * db) / 0.587;
    return writePremul(float3(clampByte(c.r + dr), clampByte(c.g + dg), clampByte(c.b + db)), b.a);
}
"

        ' ── Schaerfe durch Zurueckrechnen (Richardson-Lucy) ──────────────────────
        '
        ' Der Prozessor rechnet in Kacheln von DeconvolutionTileEdge Punkten mit
        ' DeconvolutionMargin Rand, und jede Kachel klemmt die Faltung an IHREM Rand. Bei bis zu 30
        ' Durchlaeufen reicht der Einfluss weiter als der Rand; das Ergebnis haengt deshalb an der
        ' Kachelung. Die Karte bildet sie nach: alle Kacheln samt Rand liegen nebeneinander in einem
        ' Feld (je Kachel ein Platz von Kante plus zweimal Rand), jeder Bildpunkt kennt aus seiner
        ' Lage Kachel und Grenzen, und ein Durchgang rechnet alle Kacheln zugleich.

        Private Shared Function DeconvolutionOnGpu(amount As Single, radiusAmount As Single, detailAmount As Single,
                                                   maskingAmount As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim gain = Math.Max(0.0F, amount) * DeconvolutionGainScale
            Dim sigma = DeconvolutionSigmaBase + Clamp(radiusAmount, 0, 1) * DeconvolutionSigmaRange
            Dim iterations = DeconvolutionMinIterations +
                             CInt(Math.Round(Clamp(detailAmount, 0, 1) * (DeconvolutionMaxIterations - DeconvolutionMinIterations)))
            Dim masking = Clamp(maskingAmount, 0, 1)
            Dim kernel = GaussianKernel(sigma)
            If kernel.Length > 16 Then Return Nothing
            Return Function(gr, image)
                       Dim w = image.Width, h = image.Height
                       If gain <= 0.0F Then
                           Dim copy = NewStageSurface(gr, w, h)
                           Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                               copy.Canvas.DrawImage(image, 0, 0, paint)
                           End Using
                           Return copy
                       End If
                       Dim slot = DeconvolutionTileEdge + 2 * DeconvolutionMargin
                       Dim tilesX = (w + DeconvolutionTileEdge - 1) \ DeconvolutionTileEdge
                       Dim tilesY = (h + DeconvolutionTileEdge - 1) \ DeconvolutionTileEdge
                       Dim atlasW = tilesX * slot, atlasH = tilesY * slot
                       Dim weights = New Single(15) {}
                       Array.Copy(kernel, weights, kernel.Length)
                       Dim tileUniforms = Sub(u As SKRuntimeEffectUniforms)
                                              u("imageSize") = New Single() {w, h}
                                              u("tileEdge") = CSng(DeconvolutionTileEdge)
                                              u("margin") = CSng(DeconvolutionMargin)
                                          End Sub
                       Dim init = StageEffect("Zurueckrechnen Anfang", FloatShaderCommon & DeconvTileCommon & DeconvInitShader)
                       Dim conv = StageEffect("Zurueckrechnen Falten", FloatShaderCommon & DeconvTileCommon & DeconvConvShader)
                       Dim final = StageEffect("Zurueckrechnen Ende", FloatShaderCommon & DeconvFinalShader)
                       Dim iu = New SKRuntimeEffectUniforms(init) : tileUniforms(iu)
                       Dim state = DrawInto(NewFloatSurface(gr, atlasW, atlasH), atlasW, atlasH, init, iu, ("src", image))
                       Try
                           If state Is Nothing Then Return Nothing
                           ' mode 0: Schaetzung waagerecht falten; 1: senkrecht falten, Verhaeltnis
                           ' bilden; 2: Verhaeltnis waagerecht falten; 3: senkrecht falten, Schaetzung
                           ' damit malnehmen.
                           Dim passOver = Function(input As SKImage, stateImage As SKImage, mode As Single) As SKSurface
                                              Dim u = New SKRuntimeEffectUniforms(conv)
                                              tileUniforms(u)
                                              u("weights") = weights
                                              u("radius") = CSng(kernel.Length \ 2)
                                              u("mode") = mode
                                              u("stepLimit") = DeconvolutionStepLimit
                                              Return DrawInto(NewFloatSurface(gr, atlasW, atlasH), atlasW, atlasH, conv, u, ("data", input), ("state", stateImage))
                                          End Function
                           For iteration = 1 To iterations
                               Using stateImage = state.Snapshot()
                                   Dim current = stateImage
                                   Dim owned As SKSurface = Nothing
                                   Try
                                       For mode = 0 To 2
                                           Dim nextSurface = passOver(current, stateImage, mode)
                                           If nextSurface Is Nothing Then Return Nothing
                                           If owned IsNot Nothing Then current.Dispose() : owned.Dispose()
                                           owned = nextSurface
                                           current = nextSurface.Snapshot()
                                       Next
                                       Dim updated = passOver(current, stateImage, 3)
                                       If updated Is Nothing Then Return Nothing
                                       state.Dispose()
                                       state = updated
                                   Finally
                                       If owned IsNot Nothing Then current.Dispose() : owned.Dispose()
                                   End Try
                               End Using
                           Next
                           Using stateImage = state.Snapshot()
                               Dim fu = New SKRuntimeEffectUniforms(final)
                               fu("tileEdge") = CSng(DeconvolutionTileEdge)
                               fu("margin") = CSng(DeconvolutionMargin)
                               fu("imageSize") = New Single() {w, h}
                               fu("gain") = gain
                               fu("masking") = masking
                               Return DrawStage(gr, w, h, final, fu, ("src", image), ("state", stateImage))
                           End Using
                       Finally
                           state?.Dispose()
                       End Try
                   End Function
        End Function

        ' Lage im Feld: Platz (Kachel), Ort darin, Grenzen der Kachel samt Rand, wie in
        ' DeconvolutionTile.
        Private Const DeconvTileCommon As String = "
uniform float2 imageSize;
uniform float tileEdge;
uniform float margin;
float2 slotOf(float2 a) {
    return floor(a / (tileEdge + 2.0 * margin));
}
float2 regionStart(float2 tile) {
    return max(float2(0.0), tile * tileEdge - margin);
}
float2 regionSize(float2 tile) {
    float2 innerEnd = min(tile * tileEdge + tileEdge, imageSize);
    return min(imageSize, innerEnd + margin) - regionStart(tile);
}
"

        ' Zustand: Schaetzung und Beobachtung (Helligkeit), je zwei Kanaele.
        Private Const DeconvInitShader As String = "
uniform shader src;
half4 main(float2 p) {
    float2 a = floor(p);
    float2 tile = slotOf(a);
    float2 local = a - tile * (tileEdge + 2.0 * margin);
    float2 size = regionSize(tile);
    if (local.x >= size.x || local.y >= size.y) return half4(0.0);
    float3 c = unpremulBytes(toBytes(src.eval(regionStart(tile) + local + 0.5)));
    float2 v = splitValue(lumaOf(c));
    return half4(half(v.x), half(v.y), half(v.x), half(v.y));
}
"

        Private Const DeconvConvShader As String = "
uniform shader data;
uniform shader state;
uniform float weights[16];
uniform float radius;
uniform float mode;
uniform float stepLimit;
half4 main(float2 p) {
    float2 a = floor(p);
    float2 tile = slotOf(a);
    float2 origin = tile * (tileEdge + 2.0 * margin);
    float2 local = a - origin;
    float2 size = regionSize(tile);
    if (local.x >= size.x || local.y >= size.y) return half4(0.0);
    float2 direction = (mode == 0.0 || mode == 2.0) ? float2(1.0, 0.0) : float2(0.0, 1.0);
    float sum = 0.0;
    for (int i = 0; i < 16; i++) {
        float k = float(i) - radius;
        if (k > radius) break;
        float2 q = clamp(local + direction * k, float2(0.0), size - 1.0);
        half4 t = data.eval(origin + q + 0.5);
        sum += weights[i] * joinValue(t.xy);
    }
    half4 s = state.eval(p);
    float estimate = joinValue(s.xy);
    float observed = joinValue(s.zw);
    float2 o;
    if (mode == 1.0) {
        o = splitValue(observed / max(0.5, sum));
    } else if (mode == 3.0) {
        float factor = min(stepLimit, max(1.0 / stepLimit, sum));
        float2 e = splitValue(min(255.0, estimate * factor));
        return half4(half(e.x), half(e.y), s.z, s.w);
    } else {
        o = splitValue(sum);
    }
    return half4(half(o.x), half(o.y), 0.0, 0.0);
}
"

        Private Const DeconvFinalShader As String = "
uniform shader src;
uniform shader state;
uniform float tileEdge;
uniform float margin;
uniform float2 imageSize;
uniform float gain;
uniform float masking;
float lumaAt(float2 q) {
    float3 v = unpremulBytes(toBytes(src.eval(clamp(q, float2(0.5), imageSize - 0.5))));
    return 0.299 * v.r + 0.587 * v.g + 0.114 * v.b;
}
half4 main(float2 p) {
    float2 a = floor(p);
    float2 tile = floor(a / tileEdge);
    float2 start = max(float2(0.0), tile * tileEdge - margin);
    float2 atlas = tile * (tileEdge + 2.0 * margin) + (a - start) + 0.5;
    half4 s = state.eval(atlas);
    float estimate = joinValue(s.xy);
    float observed = joinValue(s.zw);
    float weight = gain;
    if (masking > 0.0) {
        float edge = clamp((abs(lumaAt(p + float2(-1.0, 0.0)) - lumaAt(p + float2(1.0, 0.0))) +
                            abs(lumaAt(p + float2(0.0, -1.0)) - lumaAt(p + float2(0.0, 1.0)))) / 48.0, 0.0, 1.0);
        edge = edge * edge * (3.0 - 2.0 * edge);
        weight = gain * ((1.0 - masking) + masking * edge);
    }
    float shift = (estimate - observed) * weight;
    float4 b = toBytes(src.eval(p));
    float3 c = unpremulBytes(b);
    return writePremul(float3(clampByte(c.r + shift), clampByte(c.g + shift), clampByte(c.b + shift)), b.a);
}
"

        ' ── Dunst hinzufuegen ────────────────────────────────────────────────────

        ''' <summary>Der positive Dunst (ApplyHaze): ein Anheben zum Weiss hin. Das Entfernen
        ''' (negativ, ApplyDehaze) ist eine oertliche Stufe und bleibt auf dem Prozessor.</summary>
        Private Shared Function HazeOnGpu(amount As Single) As Func(Of GRContext, SKImage, SKSurface)
            Dim strength = Clamp(amount, -1, 1)
            If strength <= 0.001F Then Return Nothing
            Dim sv = strength * 0.45F
            Return Function(gr, image)
                       Dim effect = StageEffect("Dunst", HazeShader)
                       Dim u = New SKRuntimeEffectUniforms(effect)
                       u("sv") = sv
                       Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image))
                   End Function
        End Function

        Private Const HazeShader As String = "
uniform shader src;
uniform float sv;
half4 main(float2 p) {
    float4 c = toBytes(src.eval(p));
    float3 cc = unpremulBytes(c);
    float3 o = float3(clampByte(cc.r + (255.0 - cc.r) * sv),
                      clampByte(cc.g + (255.0 - cc.g) * sv),
                      clampByte(cc.b + (255.0 - cc.b) * sv));
    return writePremul(o, c.a);
}
"

        ' ── Vignette ─────────────────────────────────────────────────────────────

        ''' <summary>Die Vignette (ApplyVignette). Sie rechnet auf den vormultiplizierten Bytes,
        ''' aber nur fuer Bgra8888; andere Formate nehmen dort einen anderen Weg ueber GetPixel, und
        ''' den bildet der Shader nicht nach.</summary>
        Private Shared Function VignetteOnGpu(source As SKBitmap, amount As Single, transition As Single, roundness As Single,
                                              feather As Single, centerXPercent As Single, centerYPercent As Single,
                                              style As VignetteStyle) As Func(Of GRContext, SKImage, SKSurface)
            If source.ColorType <> SKColorType.Bgra8888 Then Return Nothing
            Dim strength = Clamp(Math.Abs(amount), 0, 1.5)
            If strength <= 0 Then Return Nothing
            Dim cx = source.Width * Clamp(centerXPercent, 0, 100) / 100.0F
            Dim cy = source.Height * Clamp(centerYPercent, 0, 100) / 100.0F
            Dim roundedness = Clamp(roundness, -100, 100) / 100.0F
            Dim radiusX = source.Width * (0.52F - Math.Min(0, roundedness) * 0.18F)
            Dim radiusY = source.Height * (0.52F + Math.Max(0, roundedness) * 0.18F)
            Dim inner = 0.2F + Clamp(transition, 0, 100) / 100.0F * 0.55F
            Dim softness = 0.04F + Clamp(feather, 0, 100) / 100.0F * 0.42F
            Dim edgeAlpha = If(amount > 0, 255.0F, 220.0F) * strength
            Dim mode = If(amount <= 0, 0.0F,
                          If(style = VignetteStyle.HighlightPriority, 2.0F,
                             If(style = VignetteStyle.PaintOverlay, 3.0F, 1.0F)))
            Return Function(gr, image)
                       Dim effect = StageEffect("Vignette", VignetteShader)
                       Dim u = New SKRuntimeEffectUniforms(effect)
                       u("center") = New Single() {cx, cy}
                       u("radius") = New Single() {Math.Max(1.0F, radiusX), Math.Max(1.0F, radiusY)}
                       u("inner") = inner
                       u("softness") = softness
                       u("edgeAlpha") = edgeAlpha
                       u("mode") = mode
                       Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image))
                   End Function
        End Function

        Private Const VignetteShader As String = "
uniform shader src;
uniform float2 center;
uniform float2 radius;
uniform float inner;
uniform float softness;
uniform float edgeAlpha;
uniform float mode;
half4 main(float2 p) {
    half4 pix = src.eval(p);
    float2 d = (floor(p) - center) / radius;
    float t = clamp((sqrt(d.x * d.x + d.y * d.y) - inner) / softness, 0.0, 1.0);
    if (t <= 0.0) return pix;
    t = t * t * (3.0 - 2.0 * t);
    float4 c = toBytes(pix);
    float mixv = t * edgeAlpha / 255.0;
    float3 o;
    if (mode == 0.0) {
        // Aufhellen auf den vormultiplizierten Werten: Ziel ist das Alpha selbst.
        o = float3(clampByte(c.r + (c.a - c.r) * mixv), clampByte(c.g + (c.a - c.g) * mixv), clampByte(c.b + (c.a - c.b) * mixv));
    } else if (mode == 2.0) {
        float luma = (0.299 * c.r + 0.587 * c.g + 0.114 * c.b) / 255.0;
        float factor = 1.0 - mixv * (1.0 - luma * luma * 0.7);
        o = float3(clampByte(c.r * factor), clampByte(c.g * factor), clampByte(c.b * factor));
    } else if (mode == 3.0) {
        float luma = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
        float desat = mixv * 0.5;
        float dark = 1.0 - mixv;
        o = float3(clampByte((c.r + (luma - c.r) * desat) * dark),
                   clampByte((c.g + (luma - c.g) * desat) * dark),
                   clampByte((c.b + (luma - c.b) * desat) * dark));
    } else {
        o = float3(clampByte(c.r * (1.0 - mixv)), clampByte(c.g * (1.0 - mixv)), clampByte(c.b * (1.0 - mixv)));
    }
    return half4(half3(o / 255.0), pix.a);
}
"

        ' ── Schaerfe ─────────────────────────────────────────────────────────────

        ''' <summary>Die Schaerfe auf der Karte, oder Nothing fuer das Zurueckrechnen (das bleibt auf
        ''' dem Prozessor). Rechnet wie ApplySharpness3x3 und ApplyUnsharpMask.</summary>
        Private Shared Function SharpnessOnGpu(amount As Single, radiusAmount As Single, detailAmount As Single,
                                               maskingAmount As Single, method As SharpenMethod) As Func(Of GRContext, SKImage, SKSurface)
            If method = SharpenMethod.Deconvolution Then Return DeconvolutionOnGpu(amount, radiusAmount, detailAmount, maskingAmount)
            If radiusAmount <= 0 AndAlso detailAmount <= 0 AndAlso maskingAmount <= 0 Then
                Return Function(gr, image)
                           Dim effect = StageEffect("Schaerfe 3x3", Sharpen3x3Shader)
                           Dim u = New SKRuntimeEffectUniforms(effect)
                           u("amount") = amount
                           u("center") = 1.0F + 4.0F * amount
                           Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image))
                       End Function
            End If
            Dim sigma = 0.8F + Clamp(radiusAmount, 0, 1) * 2.7F
            Dim gain = amount * (1.0F + Clamp(detailAmount, 0, 1) * 1.5F)
            Dim masking = Clamp(maskingAmount, 0, 1)
            If Not RasterBlurSupported(sigma) Then Return Nothing
            Return Function(gr, image)
                       ' Die Unschaerfe wie Skia auf dem Prozessor (SKImageFilter.CreateBlur).
                       Using blurSurface = RasterBlurOnGpu(gr, image, sigma, clampEdges:=True)
                           If blurSurface Is Nothing Then Return Nothing
                           Using blurred = blurSurface.Snapshot()
                               Dim effect = StageEffect("Unschaerfemaske", UnsharpMaskShader)
                               Dim u = New SKRuntimeEffectUniforms(effect)
                               u("gain") = gain
                               u("masking") = masking
                               Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image), ("blur", blurred))
                           End Using
                       End Using
                   End Function
        End Function

        Private Const Sharpen3x3Shader As String = "
uniform shader src;
uniform float amount;
uniform float center;
half4 main(float2 p) {
    float4 c = toBytes(src.eval(p));
    float3 cc = unpremulBytes(c);
    float3 l = unpremulBytes(toBytes(src.eval(p + float2(-1.0, 0.0))));
    float3 r = unpremulBytes(toBytes(src.eval(p + float2(1.0, 0.0))));
    float3 t = unpremulBytes(toBytes(src.eval(p + float2(0.0, -1.0))));
    float3 b = unpremulBytes(toBytes(src.eval(p + float2(0.0, 1.0))));
    float3 s = l + r + t + b;
    float3 o = float3(clampByte(cc.r * center - amount * s.r),
                      clampByte(cc.g * center - amount * s.g),
                      clampByte(cc.b * center - amount * s.b));
    return writePremul(o, c.a);
}
"

        Private Const UnsharpMaskShader As String = "
uniform shader src;
uniform shader blur;
uniform float gain;
uniform float masking;
float lumaAt(float2 q) {
    float3 v = unpremulBytes(toBytes(src.eval(q)));
    return 0.299 * v.r + 0.587 * v.g + 0.114 * v.b;
}
half4 main(float2 p) {
    float4 c = toBytes(src.eval(p));
    float3 cc = unpremulBytes(c);
    float3 lb = unpremulBytes(toBytes(blur.eval(p)));
    float pixelGain = gain;
    if (masking > 0.0) {
        // SharpenEdgeFactor
        float edge = clamp((abs(lumaAt(p + float2(-1.0, 0.0)) - lumaAt(p + float2(1.0, 0.0))) +
                            abs(lumaAt(p + float2(0.0, -1.0)) - lumaAt(p + float2(0.0, 1.0)))) / 48.0, 0.0, 1.0);
        edge = edge * edge * (3.0 - 2.0 * edge);
        pixelGain = gain * ((1.0 - masking) + masking * edge);
    }
    float3 o = float3(clampByte(cc.r + pixelGain * (cc.r - lb.r)),
                      clampByte(cc.g + pixelGain * (cc.g - lb.g)),
                      clampByte(cc.b + pixelGain * (cc.b - lb.b)));
    return writePremul(o, c.a);
}
"

    End Class

End Namespace
