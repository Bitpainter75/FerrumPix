Imports System
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ' Die Punktkette als Skia-Shader (SKRuntimeEffect).
    '
    ' Der Shader laeuft auf der Grafikkarte (GpuImageService) und rechnet dieselben Formeln wie
    ' RunPointOpChain, mit denselben Tabellen (als Textur, eine Zeile je Tabelle, linear abgetastet
    ' wie SampleTable) und demselben 8x8-Dithering. Was ueber den Farbkreis geht (die Baender des
    ' Farbmischers, die Dynamik je Farbton), wird auf dem Prozessor einmal als Tabelle ueber den
    ' Farbton vorgerechnet; der Shader liest dort nur ab. Gemessen an einem Foto mit 20 MP:
    ' hoechstens eine Tonstufe Abweichung, im Mittel unter 0,03.
    '
    ' DER RUECKFALL IST RunPointOpChain, NICHT DIESER SHADER AUF DEM PROZESSOR: ueber Skia auf dem
    ' Prozessor laeuft er gemessen rund zehnmal langsamer als die Kette (20 MP, alle Kerne: 6,6 s
    ' gegen 0,6 s). Er dient dort nur der Vergleichspruefung, die ohne Karte laufen muss.
    '
    ' UMFANG, Stand 2026-10-22: die ganze Kette. Oertliche Lichter und Tiefen: ihre Basisschicht
    ' entsteht wie bisher auf dem Prozessor (ein Raster von hoechstens 1024 Punkten), der Shader liest
    ' sie als kleine Textur. Farbgradierung: die vier Zonentabellen sind vorab zu drei Tabellen
    ' zusammengefasst. Punktfarbe: bis acht Farbfelder (PointColorSwatch.MaxSwatches) als
    ' Uniform-Felder. Cube-LUT: als Bild size*size x size. Den Prozessor nimmt die Kette nur noch fuer
    ' Quellen, die nicht 8 Bit und vormultipliziert sind (CanRunPointOpChainShader nennt den Grund).
    '
    ' FALLE, gemessen: Skias Prozessor-Weg fuer Laufzeit-Shader rechnet einen Kind-Aufruf als
    ' Argument eines zweiten falsch (tab(5.0, tab(1.0, x)) wurde viel zu hell), die Karte richtig.
    ' Solche Aufrufe deshalb immer ueber einen Zwischenwert; sonst faellt es nur in der
    ' Vergleichspruefung auf, die genau diesen Weg nimmt.
    '
    ' UEBERGABE: die Zielflaeche bleibt auf der Karte liegen (_pointOpHandoverSurface), und die
    ' Umgebungsstufen uebernehmen sie, wenn sie mit genau diesem Ergebnisbild beginnen. Das
    ' Zurueckholen bleibt, weil der Zwischenspeicher der Tonstufe ein Prozessorbild haelt; das
    ' erneute Hochladen faellt weg.
    '
    ' AN- UND ABSCHALTEN: GpuImageService.Enabled (Einstellung, ab Werk aus). Vor dem ersten Bild
    ' laeuft die Selbstpruefung (CheckGpuImageProcessing); erst wenn sie besteht, rechnet die Karte.
    Partial Public Class ImageProcessor

        ''' <summary>Ergebnis der Selbstpruefung des GPU-Wegs (<see cref="CheckGpuImageProcessing"/>).</summary>
        Friend Enum GpuCheckState
            Untested
            Works
            ''' <summary>Keine Karte, kein Kontext, Fehler beim Rechnen; der Grund steht in Detail.</summary>
            Unavailable
            ''' <summary>Die Karte rechnet, aber weiter vom Prozessor entfernt als erlaubt.</summary>
            Deviates
        End Enum

        Friend Structure GpuCheckResult
            Public State As GpuCheckState
            Public Renderer As String
            Public Detail As String
            Public MaxDeviation As Integer
        End Structure

        ''' <summary>Hoechste Abweichung eines Kanals in Tonstufen, die die Selbstpruefung noch
        ''' annimmt. Die Kette selbst kommt an Fotos auf eine (siehe Kopf), an der kuenstlichen
        ''' Pruefflaeche vereinzelt auf zwei.</summary>
        Private Const GpuCheckTolerance As Integer = 2

        Private Shared _gpuCheck As New GpuCheckResult With {.State = GpuCheckState.Untested, .Renderer = "", .Detail = ""}
        Private Shared ReadOnly _gpuCheckLock As New Object()

        ''' <summary>Der letzte Stand der Selbstpruefung, fuer die Einstellungen.</summary>
        Friend Shared ReadOnly Property LastGpuCheck As GpuCheckResult
            Get
                SyncLock _gpuCheckLock
                    Return _gpuCheck
                End SyncLock
            End Get
        End Property

        ''' <summary>Rechnet eine Pruefflaeche (Farbkreis mal Helligkeit, ein Teil halbtransparent)
        ''' mit einem Rezept aus dreizehn Reglern einmal auf der Karte und einmal mit RunPointOpChain und
        ''' vergleicht. Laeuft beim ersten Einsatz nach dem Einschalten von selbst und ueber
        ''' "Pruefen" in den Einstellungen. Weicht die Karte ab, sperrt sie den Weg fuer diese
        ''' Sitzung: eine Karte, die anders rechnet, ist schlechter als keine. Das Ergebnis steht
        ''' immer im Diagnoseprotokoll, ein Fehlschlag zusaetzlich in der Fehlerdatei.</summary>
        Friend Shared Function CheckGpuImageProcessing() As GpuCheckResult
            SyncLock _gpuCheckLock
                _gpuCheck = RunGpuCheck()
                Return _gpuCheck
            End SyncLock
        End Function

        Private Shared Function RunGpuCheck() As GpuCheckResult
            Dim result As New GpuCheckResult With {.Renderer = "", .Detail = ""}
            If Not GpuImageService.IsAvailable Then
                result.State = GpuCheckState.Unavailable
                result.Detail = GpuImageService.FailureReason
                Return result
            End If
            result.Renderer = GpuImageService.Renderer
            Dim clock = Diagnostics.Stopwatch.StartNew()
            Using src = BuildGpuCheckImage()
                Dim adj As New ImageAdjustments With {
                    .Exposure = 0.3F, .Contrast = 20, .Whites = 20, .Blacks = -10, .Temperature = -10,
                    .Saturation = 10, .Vibrance = 30, .RedHue = 15, .BlueSaturation = -20,
                    .CalibrationShadowTint = 10, .ChannelMixerRedGreen = 10, .Highlights = -40, .ShadowsLevel = 30}
                Dim chain = BuildPointOpChain(adj, src)
                Dim localBase = BuildLocalToneBaseFor(src, chain)
                Dim onGpu As SKBitmap = Nothing
                If Not GpuImageService.TryRun(Function(gr) RenderPointOpShaderOnGpu(gr, src, chain, localBase), onGpu) OrElse onGpu Is Nothing Then
                    result.State = GpuCheckState.Unavailable
                    result.Detail = If(String.IsNullOrEmpty(GpuImageService.FailureReason),
                                       "Der Shader laesst sich auf der Grafikkarte nicht ausfuehren.", GpuImageService.FailureReason)
                    Return result
                End If
                Using onGpu
                    Using onCpu = RunPointOpChain(src, chain)
                        Dim a = onCpu.GetPixelSpan()
                        Dim b = onGpu.GetPixelSpan()
                        Dim worst = 0
                        For i = 0 To a.Length - 1
                            Dim d = Math.Abs(CInt(a(i)) - CInt(b(i)))
                            If d > worst Then worst = d
                        Next
                        result.MaxDeviation = worst
                    End Using
                End Using
            End Using
            If result.MaxDeviation > GpuCheckTolerance Then
                result.State = GpuCheckState.Deviates
                result.Detail = $"Die Grafikkarte {result.Renderer} weicht in der Selbstpruefung um {result.MaxDeviation} Tonstufen vom Prozessor ab."
                GpuImageService.MarkUnusable(result.Detail)
            Else
                result.State = GpuCheckState.Works
                DiagnosticLogService.LogAlways("GpuImage",
                    $"Selbstpruefung bestanden: {result.Renderer}, hoechstens {result.MaxDeviation} Tonstufe(n), {clock.ElapsedMilliseconds} ms")
            End If
            Return result
        End Function

        Private Shared Function BuildGpuCheckImage() As SKBitmap
            Const W = 360, H = 200
            Dim bmp As New SKBitmap(New SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul))
            For y = 0 To H - 1
                For x = 0 To W - 1
                    Dim c = SKColor.FromHsl(x, 15 + (y Mod 50) * 1.7F, 4 + (y \ 50) * 22 + (y Mod 7))
                    Dim alpha = If(y >= 150, CByte(40 + (x Mod 200)), CByte(255))
                    bmp.SetPixel(x, y, c.WithAlpha(alpha))
                Next
            Next
            Return bmp
        End Function

        Private Const ShaderTableWidth As Integer = PointOpTableSize ' 4097
        Private Const RowToLinear As Integer = 0
        Private Const RowToGamma As Integer = 1
        Private Const RowNegR As Integer = 2
        Private Const RowNegG As Integer = 3
        Private Const RowNegB As Integer = 4
        Private Const RowScalarR As Integer = 5
        Private Const RowScalarG As Integer = 6
        Private Const RowScalarB As Integer = 7
        Private Const RowToneBefore As Integer = 8
        Private Const RowToneZones As Integer = 9
        Private Const RowLumCurve As Integer = 10
        Private Const RowHslHue As Integer = 11
        Private Const RowHslSat As Integer = 12
        Private Const RowHslLum As Integer = 13
        Private Const RowVibranceHue As Integer = 14
        Private Const RowLocalZones As Integer = 15
        Private Const RowLocalDetail As Integer = 16
        Private Const RowLocalColor As Integer = 17
        Private Const ShaderTableRows As Integer = 18

        Private Shared _pointOpEffect As SKRuntimeEffect
        Private Shared ReadOnly _pointOpEffectLock As New Object()
        Private Shared _lastGpuFallbackReason As String = ""

        ''' <summary>Kann der Shader diese Kette rechnen? Liefert sonst den Grund (fuer das
        ''' Diagnoseprotokoll).</summary>
        Friend Shared Function CanRunPointOpChainShader(source As SKBitmap, chain As PointOpChain, ByRef reason As String) As Boolean
            reason = ""
            If source Is Nothing Then reason = "keine Quelle" : Return False
            If source.ColorType <> SKColorType.Bgra8888 AndAlso source.ColorType <> SKColorType.Rgba8888 Then
                reason = $"Pixelformat {source.ColorType}" : Return False
            End If
            If source.AlphaType = SKAlphaType.Unpremul Then reason = "nicht vormultiplizierte Quelle" : Return False
            If chain.PointColors IsNot Nothing AndAlso chain.PointColors.Length > PointColorSwatch.MaxSwatches Then
                reason = "mehr Farbfelder der Punktfarbe als der Shader kennt" : Return False
            End If
            If chain.CubeTable IsNot Nothing AndAlso chain.CubeSize * chain.CubeSize > 16384 Then
                reason = $"Cube-LUT mit {chain.CubeSize} Stufen" : Return False
            End If
            Return True
        End Function

        ''' <summary>Die Punktkette auf der Grafikkarte, wenn die Einstellung an ist, die Karte
        ''' bereitsteht, die Selbstpruefung bestanden ist und der Shader die Kette kann. Sonst
        ''' Nothing, und der Aufrufer rechnet auf dem Prozessor.</summary>
        Private Shared Function TryRunPointOpChainOnGpu(source As SKBitmap, chain As PointOpChain) As SKBitmap
            If Not GpuImageService.Enabled Then Return Nothing
            Dim reason As String = Nothing
            If Not CanRunPointOpChainShader(source, chain, reason) Then
                NoteGpuFallback("Punktkette, Stufe ohne Shader: " & reason)
                Return Nothing
            End If
            If Not GpuImageService.IsAvailable Then
                NoteGpuFallback("Punktkette, Grafikkarte nicht bereit: " & GpuImageService.FailureReason)
                Return Nothing
            End If
            ' Vor dem ersten echten Bild einmal gegen den Prozessor rechnen.
            If LastGpuCheck.State = GpuCheckState.Untested Then CheckGpuImageProcessing()
            If LastGpuCheck.State <> GpuCheckState.Works Then
                NoteGpuFallback("Punktkette, Selbstpruefung nicht bestanden: " & If(LastGpuCheck.Detail, ""))
                Return Nothing
            End If
            ' Die oertliche Basis auf DIESEM Faden: ihr Bezug (_localToneReferenceEdge) ist je
            ' Faden gesetzt, auf dem GPU-Faden stuende er auf 0.
            Dim localBase = BuildLocalToneBaseFor(source, chain)
            ReleasePointOpHandover()
            Dim result As SKBitmap = Nothing
            Dim kept As SKSurface = Nothing
            If Not PerformanceTraceService.Measure("Pixel: Punktkette (Grafikkarte)",
                    Function() GpuImageService.TryRun(Function(gr)
                                                          Dim surface As SKSurface = Nothing
                                                          Dim bitmap = RenderPointOpShaderOnGpu(gr, source, chain, localBase, True, surface)
                                                          kept = surface
                                                          Return bitmap
                                                      End Function, result)) Then
                NoteGpuFallback("Punktkette, Lauf auf der Grafikkarte gescheitert (Fehlerdatei)")
                If kept IsNot Nothing Then DisposeOnGpu(kept)
                Return Nothing
            End If
            If Not AcceptGpuResult(JudgeGpuResult(SampleForGpuResult(source), result), "Punktkette") Then
                result?.Dispose()
                If kept IsNot Nothing Then DisposeOnGpu(kept)
                Return Nothing
            End If
            ' Die Flaeche bleibt fuer die Umgebungsstufen liegen, die meist gleich danach mit genau
            ' diesem Bild beginnen: das Hochladen dort faellt dann weg.
            _pointOpHandoverBitmap = result
            _pointOpHandoverSurface = kept
            Return result
        End Function

        ''' <summary>Die Basisschicht fuer oertliche Lichter und Tiefen, gebaut wie in
        ''' RunPointOpChain auf dem Prozessor. Sie ist ein Raster von hoechstens
        ''' LocalToneGridEdge Punkten an der langen Kante; der Shader liest es als kleine Textur.
        ''' Nothing ohne oertliche Zonen.</summary>
        Private Shared Function BuildLocalToneBaseFor(source As SKBitmap, chain As PointOpChain) As LocalToneBase
            If chain.LocalToneZones Is Nothing Then Return Nothing
            Dim srcBuf As Byte() = Nothing
            Dim length, stride, ri, gi, bi, ai As Integer
            If Not TryRentRgbaLikeBuffer(source, srcBuf, length, stride, ri, gi, bi, ai) Then Return Nothing
            Try
                Dim edge = _localToneReferenceEdge
                Return PerformanceTraceService.Measure("Pixel: Punktkette, oertliche Basis",
                    Function() BuildLocalToneBase(srcBuf, stride, source.Width, source.Height, ri, gi, bi, ai,
                                                  source.AlphaType = SKAlphaType.Premul, chain, edge))
            Finally
                ReturnPooledBuffer(srcBuf)
            End Try
        End Function

        ''' <summary>Das Raster der Basis als Bild: drei Bloecke uebereinander (A, B, geglaettetes
        ''' Grau), je Wert zwei Kanaele wie bei den Tabellen. Ohne Basis ein Bildpunkt, damit der
        ''' Shader immer ein Kind hat.</summary>
        Private Shared Function BuildLocalBaseImage(localBase As LocalToneBase) As SKImage
            Dim w = If(localBase Is Nothing, 1, localBase.Width)
            Dim h = If(localBase Is Nothing, 1, localBase.Height)
            Dim data(w * h * 3 * 4 - 1) As Short
            Dim one = BitConverter.HalfToInt16Bits(CType(1.0F, Half))
            Dim put = Sub(block As Integer, values As Single())
                          For i = 0 To w * h - 1
                              Dim o = (block * w * h + i) * 4
                              Dim v = If(values Is Nothing, 0.0F, values(i))
                              Dim hi = CType(v, Half)
                              data(o) = BitConverter.HalfToInt16Bits(hi)
                              data(o + 1) = BitConverter.HalfToInt16Bits(CType(v - CSng(hi), Half))
                              data(o + 3) = one
                          Next
                      End Sub
            put(0, localBase?.MeanA)
            put(1, localBase?.MeanB)
            put(2, localBase?.Smooth)
            Using bitmap = New SKBitmap(New SKImageInfo(w, h * 3, SKColorType.RgbaF16, SKAlphaType.Premul))
                Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length)
                Return SKImage.FromBitmap(bitmap)
            End Using
        End Function

        ''' <summary>Schreibt einen Rueckfall ins Diagnoseprotokoll, aber nur bei neuem Grund - beim
        ''' Ziehen eines Reglers liefe sonst je Bild eine Zeile.</summary>
        Private Shared Sub NoteGpuFallback(reason As String)
            If String.Equals(reason, _lastGpuFallbackReason, StringComparison.Ordinal) Then Return
            _lastGpuFallbackReason = reason
            DiagnosticLogService.LogAlways("GpuImage", "Auf dem Prozessor: " & reason)
        End Sub

        ''' <summary>Wie ein Ergebnis der Karte aussieht, gemessen an Stichproben aus dem Bild, das
        ''' hinaufging.</summary>
        Friend Enum GpuResultVerdict
            Plausible
            ''' <summary>Deckende Bildpunkte kamen durchsichtig zurueck: die Karte hat nicht oder
            ''' nicht ganz geschrieben. Der Weg bleibt fuer die Sitzung zu.</summary>
            Incomplete
            ''' <summary>Alles schwarz, obwohl das Bild es nicht war. Dieser Durchgang rechnet auf dem
            ''' Prozessor; ist das Bild wirklich schwarz, kostet das nur Zeit.</summary>
            Black
        End Enum

        ''' <summary>Stichproben eines Bildes auf einem Raster von etwa 64 mal 64 Punkten: Deckung
        ''' und ob der Punkt Farbe traegt. Anderswo belegt: ein Treiber, der lange Arbeit
        ''' zuruecksetzt, liefert gueltige, aber leere Bilder, ohne einen Fehler zu melden.</summary>
        Friend NotInheritable Class GpuResultSample
            Public Width As Integer
            Public Height As Integer
            Public StepX As Integer
            Public StepY As Integer
            Public Alpha As Byte()
            Public Lit As Boolean()
        End Class

        Private Const GpuResultGrid As Integer = 64

        ''' <summary>Nothing fuer Formate, die nicht vier Bytes je Punkt haben; dann wird nicht
        ''' geprueft.</summary>
        Friend Shared Function SampleForGpuResult(bitmap As SKBitmap) As GpuResultSample
            If bitmap Is Nothing OrElse bitmap.BytesPerPixel <> 4 Then Return Nothing
            If bitmap.ColorType <> SKColorType.Bgra8888 AndAlso bitmap.ColorType <> SKColorType.Rgba8888 Then Return Nothing
            Dim sample As New GpuResultSample With {
                .Width = bitmap.Width, .Height = bitmap.Height,
                .StepX = Math.Max(1, bitmap.Width \ GpuResultGrid), .StepY = Math.Max(1, bitmap.Height \ GpuResultGrid)}
            Dim count = ((sample.Width - 1) \ sample.StepX + 1) * ((sample.Height - 1) \ sample.StepY + 1)
            ReDim sample.Alpha(count - 1)
            ReDim sample.Lit(count - 1)
            ReadGpuResultSamples(bitmap, sample, sample.Alpha, sample.Lit)
            Return sample
        End Function

        Private Shared Sub ReadGpuResultSamples(bitmap As SKBitmap, sample As GpuResultSample, alpha As Byte(), lit As Boolean())
            Dim pixels = bitmap.GetPixelSpan()
            Dim rowBytes = bitmap.RowBytes
            Dim i = 0
            For y = 0 To sample.Height - 1 Step sample.StepY
                For x = 0 To sample.Width - 1 Step sample.StepX
                    Dim o = y * rowBytes + x * 4
                    ' Rot und Blau stehen je nach Format vertauscht; fuer "traegt Farbe" ist das gleich.
                    alpha(i) = pixels(o + 3)
                    lit(i) = pixels(o) <> 0 OrElse pixels(o + 1) <> 0 OrElse pixels(o + 2) <> 0
                    i += 1
                Next
            Next
        End Sub

        ''' <summary>Vergleicht ein zurueckgelesenes Ergebnis mit den Stichproben seines Eingangs.
        ''' Die Schwellen lassen jeder Stufe ihre Wirkung: ein Weichzeichner darf Deckung an einer
        ''' Kante verschieben, aber nicht die Haelfte aller deckenden Punkte leeren.</summary>
        Friend Shared Function JudgeGpuResult(sample As GpuResultSample, result As SKBitmap) As GpuResultVerdict
            If sample Is Nothing OrElse result Is Nothing Then Return GpuResultVerdict.Plausible
            If result.Width <> sample.Width OrElse result.Height <> sample.Height OrElse result.BytesPerPixel <> 4 Then Return GpuResultVerdict.Plausible
            Dim alpha(sample.Alpha.Length - 1) As Byte
            Dim lit(sample.Lit.Length - 1) As Boolean
            ReadGpuResultSamples(result, sample, alpha, lit)
            Dim opaque = 0, emptied = 0, colored = 0, blackened = 0
            For i = 0 To alpha.Length - 1
                If sample.Alpha(i) <> 255 Then Continue For
                opaque += 1
                If alpha(i) = 0 Then emptied += 1
                If sample.Lit(i) Then
                    colored += 1
                    If alpha(i) = 255 AndAlso Not lit(i) Then blackened += 1
                End If
            Next
            If opaque >= 16 AndAlso emptied * 2 >= opaque Then Return GpuResultVerdict.Incomplete
            If colored >= 16 AndAlso blackened = colored Then Return GpuResultVerdict.Black
            Return GpuResultVerdict.Plausible
        End Function

        ''' <summary>Folgt aus dem Urteil: bei Incomplete den Weg fuer die Sitzung schliessen. True,
        ''' wenn das Ergebnis taugt.</summary>
        Friend Shared Function AcceptGpuResult(verdict As GpuResultVerdict, what As String) As Boolean
            Select Case verdict
                Case GpuResultVerdict.Incomplete
                    GpuImageService.MarkUnusable($"{what}: die Grafikkarte lieferte ein unvollstaendiges Bild (deckende Punkte kamen leer zurueck).")
                    Return False
                Case GpuResultVerdict.Black
                    NoteGpuFallback($"{what}: die Grafikkarte lieferte ein schwarzes Bild, dieser Durchgang rechnet auf dem Prozessor")
                    Return False
                Case Else
                    Return True
            End Select
        End Function

        ''' <summary>Derselbe Shader ueber Skia auf dem Prozessor, in Streifen parallel. Nur fuer die
        ''' Vergleichspruefung im Pruefstand (sie braucht keine Karte); als Rueckfall zu langsam,
        ''' siehe Kopf der Datei. Nothing, wenn der Shader die Kette nicht kann.</summary>
        Friend Shared Function RunPointOpChainShaderOnCpu(source As SKBitmap, chain As PointOpChain) As SKBitmap
            Dim reason As String = Nothing
            If Not CanRunPointOpChainShader(source, chain, reason) Then Return Nothing
            Dim effect = PointOpEffect()
            Dim info = New SKImageInfo(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim result = New SKBitmap(info)
            Dim localBase = BuildLocalToneBaseFor(source, chain)
            Using table = BuildShaderTables(chain), dither = BuildDitherImage(), image = SKImage.FromBitmap(source),
                  baseImage = BuildLocalBaseImage(localBase),
                  cubeA = BuildCubeImage(chain, 0), cubeB = BuildCubeImage(chain, 1)
                Const BandRows = 128
                Dim bands = (source.Height + BandRows - 1) \ BandRows
                Dim basePtr = result.GetPixels()
                Dim rowBytes = result.RowBytes
                Parallel.For(0, bands,
                    Sub(band As Integer)
                        Dim y0 = band * BandRows
                        Dim rows = Math.Min(BandRows, source.Height - y0)
                        Dim bandInfo = New SKImageInfo(source.Width, rows, source.ColorType, source.AlphaType)
                        Using surface = SKSurface.Create(bandInfo, basePtr + y0 * rowBytes, rowBytes)
                            Using shader = PointOpShader(effect, chain, image, table, dither, baseImage, cubeA, cubeB, localBase,
                                                         source.AlphaType = SKAlphaType.Premul)
                                Using paint = New SKPaint With {.Shader = shader, .BlendMode = SKBlendMode.Src}
                                    surface.Canvas.Translate(0, -y0)
                                    surface.Canvas.DrawRect(0, y0, source.Width, rows, paint)
                                End Using
                            End Using
                        End Using
                    End Sub)
            End Using
            Return result
        End Function

        ''' <summary>Die Flaeche der letzten Punktkette auf der Karte, fuer die Umgebungsstufen danach
        ''' (siehe GpuDetailSession.TryApply). Je Faden, weil Bildrender und Kompositor gleichzeitig
        ''' rechnen; die Flaeche selbst wird nur auf dem GPU-Faden angefasst.</summary>
        <ThreadStatic> Private Shared _pointOpHandoverBitmap As SKBitmap
        <ThreadStatic> Private Shared _pointOpHandoverSurface As SKSurface

        ''' <summary>Uebernimmt die Flaeche der Punktkette, wenn <paramref name="current"/> genau deren
        ''' Ergebnis ist; sonst Nothing. Eine nicht passende Flaeche wird freigegeben.</summary>
        Private Shared Function TakePointOpHandover(current As SKBitmap) As SKSurface
            Dim surface = _pointOpHandoverSurface
            Dim matches = surface IsNot Nothing AndAlso Object.ReferenceEquals(current, _pointOpHandoverBitmap)
            _pointOpHandoverSurface = Nothing
            _pointOpHandoverBitmap = Nothing
            If matches Then Return surface
            If surface IsNot Nothing Then DisposeOnGpu(surface)
            Return Nothing
        End Function

        ''' <summary>Gibt eine liegengebliebene Uebergabe frei.</summary>
        Private Shared Sub ReleasePointOpHandover()
            Dim surface = _pointOpHandoverSurface
            _pointOpHandoverSurface = Nothing
            _pointOpHandoverBitmap = Nothing
            If surface IsNot Nothing Then DisposeOnGpu(surface)
        End Sub

        Private Shared Sub DisposeOnGpu(surface As SKSurface)
            Dim ignored As Boolean
            GpuImageService.TryRun(Function(gr)
                                       surface.Dispose()
                                       Return True
                                   End Function, ignored)
        End Sub

        Private Shared Function RenderPointOpShaderOnGpu(gr As GRContext, source As SKBitmap, chain As PointOpChain,
                                                         localBase As LocalToneBase) As SKBitmap
            Dim kept As SKSurface = Nothing
            Return RenderPointOpShaderOnGpu(gr, source, chain, localBase, False, kept)
        End Function

        ''' <summary>Mit <paramref name="keepSurface"/> bleibt die Zielflaeche auf der Karte und kommt
        ''' in <paramref name="kept"/> zurueck, fuer die Umgebungsstufen danach.</summary>
        Private Shared Function RenderPointOpShaderOnGpu(gr As GRContext, source As SKBitmap, chain As PointOpChain,
                                                         localBase As LocalToneBase, keepSurface As Boolean,
                                                         ByRef kept As SKSurface) As SKBitmap
            kept = Nothing
            Dim effect = PointOpEffect()
            Dim gpuInfo = New SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
            Using rasterImage = SKImage.FromBitmap(source),
                  table = BuildShaderTables(chain), dither = BuildDitherImage(), baseImage = BuildLocalBaseImage(localBase),
                  cubeA = BuildCubeImage(chain, 0), cubeB = BuildCubeImage(chain, 1)
                Using image = rasterImage.ToTextureImage(gr),
                      gpuTable = table.ToTextureImage(gr), gpuDither = dither.ToTextureImage(gr),
                      gpuBase = baseImage.ToTextureImage(gr),
                      gpuCubeA = cubeA.ToTextureImage(gr), gpuCubeB = cubeB.ToTextureImage(gr)
                    Dim surface = SKSurface.Create(gr, False, gpuInfo)
                    Try
                        If image Is Nothing OrElse gpuTable Is Nothing OrElse gpuDither Is Nothing OrElse
                           gpuBase Is Nothing OrElse gpuCubeA Is Nothing OrElse gpuCubeB Is Nothing OrElse surface Is Nothing Then
                            Throw New InvalidOperationException("Textur oder Zielflaeche auf der Grafikkarte nicht anlegbar.")
                        End If
                        Using shader = PointOpShader(effect, chain, image, gpuTable, gpuDither, gpuBase, gpuCubeA, gpuCubeB, localBase,
                                                     source.AlphaType = SKAlphaType.Premul)
                            Using paint = New SKPaint With {.Shader = shader, .BlendMode = SKBlendMode.Src}
                                surface.Canvas.DrawRect(0, 0, source.Width, source.Height, paint)
                            End Using
                        End Using
                        gr.Flush()
                        gr.Submit(True)
                        Dim result = New SKBitmap(New SKImageInfo(source.Width, source.Height, source.ColorType, source.AlphaType))
                        If Not surface.ReadPixels(result.Info, result.GetPixels(), result.RowBytes, 0, 0) Then
                            result.Dispose()
                            Throw New InvalidOperationException("Zuruecklesen von der Grafikkarte gescheitert.")
                        End If
                        If keepSurface Then
                            kept = surface
                            surface = Nothing
                        End If
                        Return result
                    Finally
                        surface?.Dispose()
                    End Try
                End Using
            End Using
        End Function

        Private Shared Function PointOpEffect() As SKRuntimeEffect
            SyncLock _pointOpEffectLock
                If _pointOpEffect Is Nothing Then
                    Dim errors As String = Nothing
                    _pointOpEffect = SKRuntimeEffect.CreateShader(PointOpShaderSource, errors)
                    If _pointOpEffect Is Nothing Then
                        Throw New InvalidOperationException("Shader der Punktkette uebersetzt nicht: " & errors)
                    End If
                End If
                Return _pointOpEffect
            End SyncLock
        End Function

        Private Shared Function PointOpShader(effect As SKRuntimeEffect, chain As PointOpChain, image As SKImage,
                                              table As SKImage, dither As SKImage, baseImage As SKImage,
                                              cubeA As SKImage, cubeB As SKImage,
                                              localBase As LocalToneBase, premul As Boolean) As SKShader
            Dim u = New SKRuntimeEffectUniforms(effect)
            u("premulIn") = If(premul, 1.0F, 0.0F)
            u("useLocal") = If(localBase IsNot Nothing, 1.0F, 0.0F)
            u("useLocalDetail") = If(chain.LocalDetailGain IsNot Nothing, 1.0F, 0.0F)
            u("useLocalColor") = If(chain.LocalColorGain IsNot Nothing, 1.0F, 0.0F)
            u("baseSize") = If(localBase IsNot Nothing, New Single() {localBase.Width, localBase.Height}, New Single() {1, 1})
            u("baseOrigin") = If(localBase IsNot Nothing, New Single() {localBase.OriginX, localBase.OriginY}, New Single() {0, 0})
            u("baseStep") = If(localBase IsNot Nothing, CSng(localBase.StepSize), 1.0F)
            u("useNeg") = If(chain.NegR IsNot Nothing, 1.0F, 0.0F)
            u("negMono") = If(chain.NegMonochrome, 1.0F, 0.0F)
            SetMatrixRows(u, "wb", chain.WhiteBalanceMatrix, 3, 0)
            u("useWb") = If(chain.WhiteBalanceMatrix IsNot Nothing, 1.0F, 0.0F)
            SetMatrixRows(u, "cm", chain.ColorMatrix, 5, 4)
            u("useCm") = If(chain.ColorMatrix IsNot Nothing, 1.0F, 0.0F)
            u("useScalar") = If(chain.ScalarR IsNot Nothing, 1.0F, 0.0F)
            u("useToneBefore") = If(chain.ToneBefore IsNot Nothing, 1.0F, 0.0F)
            u("useToneZones") = If(chain.ToneZones IsNot Nothing, 1.0F, 0.0F)
            u("useLum") = If(chain.LuminanceCurve IsNot Nothing, 1.0F, 0.0F)
            u("useHsl") = If(chain.Hsl IsNot Nothing, 1.0F, 0.0F)
            ' Wie hslBlockAktiv in RunPointOpChain: auch Farbgradierung und Punktfarbe gehen durch
            ' den HSL-Hin- und Rueckweg.
            Dim hslActive = chain.LuminanceCurve IsNot Nothing OrElse chain.Hsl IsNot Nothing OrElse chain.Vibrance <> 0.0F OrElse
                            chain.SplitToning IsNot Nothing OrElse chain.PointColors IsNot Nothing
            u("hslActive") = If(hslActive, 1.0F, 0.0F)
            u("useMelissa") = If(chain.CurvesInMelissa, 1.0F, 0.0F)
            SetColorGradeUniforms(u, chain)
            SetPointColorUniforms(u, chain.PointColors)
            u("useCube") = If(chain.CubeTable IsNot Nothing, 1.0F, 0.0F)
            u("cubeSize") = CSng(Math.Max(1, chain.CubeSize))
            u("cubeStrength") = chain.CubeStrength
            u("vibMode") = If(chain.VibranceExponential, 1.0F, If(chain.VibranceByHue IsNot Nothing, 2.0F, If(chain.Vibrance <> 0.0F, 3.0F, 0.0F)))
            u("vibrance") = chain.Vibrance
            u("shadowTint") = chain.ShadowTint
            SetMatrixRows(u, "mix", chain.Mixer, 4, 3)
            u("useMixer") = If(chain.Mixer IsNot Nothing, 1.0F, 0.0F)
            u("useMap") = If(chain.MapShadow IsNot Nothing, 1.0F, 0.0F)
            u("mapS") = If(chain.MapShadow IsNot Nothing, New Single() {chain.MapShadow(0), chain.MapShadow(1), chain.MapShadow(2)}, New Single() {0, 0, 0})
            u("mapH") = If(chain.MapHighlight IsNot Nothing, New Single() {chain.MapHighlight(0), chain.MapHighlight(1), chain.MapHighlight(2)}, New Single() {0, 0, 0})
            u("mapAmount") = chain.MapAmount
            SetMatrixRows(u, "pm", chain.PresetMatrix, 5, 4)
            u("usePreset") = If(chain.PresetMatrix IsNot Nothing, 1.0F, 0.0F)
            u("presetStrength") = chain.PresetStrength
            u("posterize") = CSng(chain.PosterizeLevels)
            u("threshold") = CSng(chain.ThresholdLevel)

            ' Die Teil-Shader SOFORT wieder freigeben; der fertige Shader haelt eigene Verweise.
            ' Blieben sie dem Speicherbereiniger, gaebe dessen Faden irgendwann die letzte Referenz
            ' auf eine Textur der Karte frei - ohne OpenGL-Kontext, gemessen ein
            ' Speicherzugriffsfehler in libGLdispatch. So endet alles mit dem fertigen Shader, und
            ' den gibt der Aufrufer auf dem GPU-Faden frei.
            Dim parts = {
                image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, New SKSamplingOptions(SKFilterMode.Nearest)),
                table.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, New SKSamplingOptions(SKFilterMode.Linear)),
                dither.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, New SKSamplingOptions(SKFilterMode.Nearest)),
                baseImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, New SKSamplingOptions(SKFilterMode.Linear)),
                cubeA.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, New SKSamplingOptions(SKFilterMode.Nearest)),
                cubeB.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, New SKSamplingOptions(SKFilterMode.Nearest))}
            Try
                Dim children = New SKRuntimeEffectChildren(effect)
                children("src") = parts(0)
                children("tables") = parts(1)
                children("dither") = parts(2)
                children("baseGrid") = parts(3)
                children("cubeA") = parts(4)
                children("cubeB") = parts(5)
                Return effect.ToShader(u, children)
            Finally
                For Each part In parts
                    part.Dispose()
                Next
            End Try
        End Function

        ''' <summary>Die Farbgradierung fuer den Shader: die vier Zonentabellen (je 20 Stufen) mal
        ''' ihre Versaetze, schon addiert. SampleGradeWeight tastet alle an derselben Stelle ab, und
        ''' lineare Abtastung vertraegt das Zusammenfassen vorher.</summary>
        Private Shared Sub SetColorGradeUniforms(u As SKRuntimeEffectUniforms, chain As PointOpChain)
            Dim dY = New Single(19) {}, dCb = New Single(19) {}, dCr = New Single(19) {}
            Dim active = chain.SplitToning IsNot Nothing
            If active Then
                Dim o = chain.SplitOffsets
                For k = 0 To 19
                    Dim wS = chain.SplitShadowTable(k), wM = GradeMid(k), wH = chain.SplitHighTable(k), wG = GradeGlobal(k)
                    dY(k) = wS * o(0) + wM * o(3) + wH * o(6) + wG * o(9) + chain.SplitLumTable(k)
                    dCb(k) = wS * o(1) + wM * o(4) + wH * o(7) + wG * o(10)
                    dCr(k) = wS * o(2) + wM * o(5) + wH * o(8) + wG * o(11)
                Next
            End If
            u("useGrade") = If(active, 1.0F, 0.0F)
            u("gradeAfterPreset") = If(chain.ToneAfterPreset, 1.0F, 0.0F)
            u("gradeY") = dY
            u("gradeCb") = dCb
            u("gradeCr") = dCr
        End Sub

        ''' <summary>Die Farbfelder der Punktfarbe (PointColorMath.Prepared) als Uniform-Felder, je
        ''' Groesse ein Feld mit acht Plaetzen, dazu die Matrizen nach ProPhoto und zurueck.</summary>
        Private Shared Sub SetPointColorUniforms(u As SKRuntimeEffectUniforms, swatches As PointColorMath.Prepared())
            Dim count = If(swatches Is Nothing, 0, swatches.Length)
            Dim field = Function(getter As Func(Of PointColorMath.Prepared, Double)) As Single()
                            Dim values = New Single(7) {}
                            For i = 0 To Math.Min(count, 8) - 1
                                values(i) = CSng(getter(swatches(i)))
                            Next
                            Return values
                        End Function
            u("pointCount") = CSng(count)
            u("pcHue") = field(Function(p) p.Hue)
            u("pcSat") = field(Function(p) p.Saturation)
            u("pcLum") = field(Function(p) p.Luminance)
            u("pcScale0") = field(Function(p) p.Scale(0))
            u("pcScale1") = field(Function(p) p.Scale(1))
            u("pcScale2") = field(Function(p) p.Scale(2))
            For k = 0 To 3
                Dim index = k
                u("pcH" & k) = field(Function(p) p.HuePoints(index))
                u("pcS" & k) = field(Function(p) p.SaturationPoints(index))
                u("pcL" & k) = field(Function(p) p.LuminancePoints(index))
            Next
            u("pcTurn") = field(Function(p) p.HueTurn)
            u("pcLogS") = field(Function(p) p.LogSaturation)
            u("pcLogV") = field(Function(p) p.LogValue)
            u("pcVar") = field(Function(p) p.Variance)
            u("pcVis") = field(Function(p) If(p.Visualize, 1.0, 0.0))
            Dim rows = Sub(prefix As String, m As Double())
                           For r = 0 To 2
                               u(prefix & r) = New Single() {CSng(m(r * 3)), CSng(m(r * 3 + 1)), CSng(m(r * 3 + 2))}
                           Next
                       End Sub
            rows("toPro", PointColorMath.SrgbToProPhoto)
            rows("fromPro", PointColorMath.ProPhotoToSrgb)
        End Sub

        ''' <summary>Die Cube-LUT als Bild size*size x size in halber Gleitkomma, je Wert zwei Kanaele:
        ''' <paramref name="part"/> 0 Rot und Gruen, 1 Blau. Ohne LUT ein Bildpunkt.</summary>
        Private Shared Function BuildCubeImage(chain As PointOpChain, part As Integer) As SKImage
            Dim size = If(chain.CubeTable Is Nothing, 0, chain.CubeSize)
            Dim w = Math.Max(1, size * size), h = Math.Max(1, size)
            Dim data(w * h * 4 - 1) As Short
            If size > 0 Then
                Dim table = chain.CubeTable
                For b = 0 To size - 1
                    For g = 0 To size - 1
                        For r = 0 To size - 1
                            Dim src = (b * size * size + g * size + r) * 3
                            Dim o = (g * w + r + b * size) * 4
                            For c = 0 To If(part = 0, 1, 0)
                                Dim v = table(src + If(part = 0, c, 2))
                                Dim hi = CType(v, Half)
                                data(o + c * 2) = BitConverter.HalfToInt16Bits(hi)
                                data(o + c * 2 + 1) = BitConverter.HalfToInt16Bits(CType(v - CSng(hi), Half))
                            Next
                        Next
                    Next
                Next
            End If
            Using bitmap = New SKBitmap(New SKImageInfo(w, h, SKColorType.RgbaF16, SKAlphaType.Premul))
                Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length)
                Return SKImage.FromBitmap(bitmap)
            End Using
        End Function

        ''' <summary>Setzt drei Zeilen einer Matrix als float3 (name0, name1, name2) und den
        ''' Versatz (nameOff), Einheitsmatrix ohne Matrix. <paramref name="stride"/> ist die Breite
        ''' einer Zeile im Feld, <paramref name="offsetColumn"/> die Spalte des Versatzes (0 = keiner).</summary>
        Private Shared Sub SetMatrixRows(u As SKRuntimeEffectUniforms, name As String, m As Single(),
                                         stride As Integer, offsetColumn As Integer)
            For row = 0 To 2
                Dim values = New Single(2) {}
                For c = 0 To 2
                    values(c) = If(m IsNot Nothing, m(row * stride + c), If(row = c, 1.0F, 0.0F))
                Next
                u(name & row.ToString()) = values
            Next
            If offsetColumn > 0 Then
                u(name & "Off") = If(m IsNot Nothing,
                                     New Single() {m(offsetColumn), m(stride + offsetColumn), m(2 * stride + offsetColumn)},
                                     New Single() {0, 0, 0})
            End If
        End Sub

        ''' <summary>Alle Tabellen der Kette als EIN Bild in halber Gleitkomma, eine Zeile je Tabelle,
        ''' 4097 Punkte breit wie die Tabellen selbst. Fehlt eine Tabelle, bleibt ihre Zeile leer -
        ''' der Shader fragt sie dann nicht. Die Farbtonzeilen gehen ueber 0 bis 360 Grad.
        '''
        ''' ZWEI KANAELE JE WERT: Rot traegt den Wert in halber Gleitkomma (11 Bit Mantisse, nahe 1
        ''' also ein Achtel Tonstufe), Gruen den Rest bis zum vollen Wert. Der Shader addiert beide;
        ''' weil die lineare Abtastung linear ist, gilt das auch zwischen den Punkten. Zusammen rund
        ''' 22 Bit, ohne auf Gleitkommatexturen mit 32 Bit angewiesen zu sein.</summary>
        Private Shared Function BuildShaderTables(chain As PointOpChain) As SKImage
            EnsureGammaTables()
            Dim data(ShaderTableWidth * ShaderTableRows * 4 - 1) As Short
            Dim one = BitConverter.HalfToInt16Bits(CType(1.0F, Half))
            For row = 0 To ShaderTableRows - 1
                For i = 0 To ShaderTableWidth - 1
                    data((row * ShaderTableWidth + i) * 4 + 3) = one
                Next
            Next
            Dim put = Sub(row As Integer, table As Single())
                          If table Is Nothing Then Return
                          For i = 0 To ShaderTableWidth - 1
                              Dim hi = CType(table(i), Half)
                              data((row * ShaderTableWidth + i) * 4) = BitConverter.HalfToInt16Bits(hi)
                              data((row * ShaderTableWidth + i) * 4 + 1) = BitConverter.HalfToInt16Bits(CType(table(i) - CSng(hi), Half))
                          Next
                      End Sub
            put(RowToLinear, _srgbToLinearTable)
            put(RowToGamma, _linearToSrgbTable)
            put(RowNegR, chain.NegR)
            put(RowNegG, chain.NegG)
            put(RowNegB, chain.NegB)
            put(RowScalarR, chain.ScalarR)
            put(RowScalarG, chain.ScalarG)
            put(RowScalarB, chain.ScalarB)
            put(RowToneBefore, chain.ToneBefore)
            put(RowToneZones, chain.ToneZones)
            put(RowLumCurve, chain.LuminanceCurve)
            put(RowLocalZones, chain.LocalToneZones)
            put(RowLocalDetail, chain.LocalDetailGain)
            put(RowLocalColor, chain.LocalColorGain)
            If chain.Hsl IsNot Nothing OrElse chain.VibranceByHue IsNot Nothing Then
                Dim hue(ShaderTableWidth - 1) As Single, sat(ShaderTableWidth - 1) As Single
                Dim lum(ShaderTableWidth - 1) As Single, vib(ShaderTableWidth - 1) As Single
                For i = 0 To ShaderTableWidth - 1
                    Dim h = i * 360.0 / (ShaderTableWidth - 1)
                    If h >= 360.0 Then h -= 360.0
                    If chain.Hsl IsNot Nothing Then
                        Dim hs As Single = 0, ss As Single = 0, ls As Single = 0
                        GetHslBandAdjustments(ReferenceBandHue(h), chain.Hsl, hs, ss, ls)
                        ' Normiert, damit halbe Gleitkomma genau genug bleibt: Grad durch 360,
                        ' Reglerwerte durch 100.
                        hue(i) = hs / 360.0F : sat(i) = ss / 100.0F : lum(i) = ls / 100.0F
                    End If
                    If chain.VibranceByHue IsNot Nothing Then vib(i) = CSng(SampleVibranceByHue(chain.VibranceByHue, h))
                Next
                put(RowHslHue, hue) : put(RowHslSat, sat) : put(RowHslLum, lum) : put(RowVibranceHue, vib)
            End If
            Dim info = New SKImageInfo(ShaderTableWidth, ShaderTableRows, SKColorType.RgbaF16, SKAlphaType.Premul)
            Using bitmap = New SKBitmap(info)
                Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length)
                Return SKImage.FromBitmap(bitmap)
            End Using
        End Function

        ''' <summary>Die 8x8-Bayer-Matrix als Bild, im Shader mit Wiederholung abgetastet.</summary>
        Private Shared Function BuildDitherImage() As SKImage
            Dim data(8 * 8 * 4 - 1) As Short
            Dim one = BitConverter.HalfToInt16Bits(CType(1.0F, Half))
            For i = 0 To 63
                data(i * 4) = BitConverter.HalfToInt16Bits(CType(DitherMatrix(i), Half))
                data(i * 4 + 3) = one
            Next
            Using bitmap = New SKBitmap(New SKImageInfo(8, 8, SKColorType.RgbaF16, SKAlphaType.Premul))
                Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length)
                Return SKImage.FromBitmap(bitmap)
            End Using
        End Function

        ' Der Shader. Jede Stufe steht in derselben Reihenfolge und mit denselben Formeln wie in
        ' RunPointOpChain; die Kommentare dort gelten hier mit.
        Private Const PointOpShaderSource As String = "
uniform shader src;
uniform shader tables;
uniform shader dither;
uniform shader baseGrid;
uniform float premulIn;
uniform float useLocal; uniform float useLocalDetail; uniform float useLocalColor;
uniform float2 baseSize; uniform float2 baseOrigin; uniform float baseStep;
uniform float useNeg; uniform float negMono;
uniform float useWb; uniform float3 wb0; uniform float3 wb1; uniform float3 wb2;
uniform float useCm; uniform float3 cm0; uniform float3 cm1; uniform float3 cm2; uniform float3 cmOff;
uniform float useScalar; uniform float useToneBefore; uniform float useToneZones;
uniform float hslActive; uniform float useLum; uniform float useHsl;
uniform float vibMode; uniform float vibrance;
uniform float shadowTint;
uniform float useMixer; uniform float3 mix0; uniform float3 mix1; uniform float3 mix2; uniform float3 mixOff;
uniform float useMap; uniform float3 mapS; uniform float3 mapH; uniform float mapAmount;
uniform float usePreset; uniform float3 pm0; uniform float3 pm1; uniform float3 pm2; uniform float3 pmOff; uniform float presetStrength;
uniform float posterize; uniform float threshold;
uniform float useMelissa;
uniform float useGrade; uniform float gradeAfterPreset;
uniform float gradeY[20]; uniform float gradeCb[20]; uniform float gradeCr[20];
uniform shader cubeA; uniform shader cubeB;
uniform float useCube; uniform float cubeSize; uniform float cubeStrength;
uniform float pointCount;
uniform float3 toPro0; uniform float3 toPro1; uniform float3 toPro2;
uniform float3 fromPro0; uniform float3 fromPro1; uniform float3 fromPro2;
uniform float pcHue[8]; uniform float pcSat[8]; uniform float pcLum[8];
uniform float pcScale0[8]; uniform float pcScale1[8]; uniform float pcScale2[8];
uniform float pcH0[8]; uniform float pcH1[8]; uniform float pcH2[8]; uniform float pcH3[8];
uniform float pcS0[8]; uniform float pcS1[8]; uniform float pcS2[8]; uniform float pcS3[8];
uniform float pcL0[8]; uniform float pcL1[8]; uniform float pcL2[8]; uniform float pcL3[8];
uniform float pcTurn[8]; uniform float pcLogS[8]; uniform float pcLogV[8]; uniform float pcVar[8]; uniform float pcVis[8];

float tab(float row, float v) {
    v = clamp(v, 0.0, 1.0);
    half4 t = tables.eval(float2(v * 4096.0 + 0.5, row + 0.5));
    return float(t.r) + float(t.g);
}
float hueTab(float row, float h) {
    half4 t = tables.eval(float2(h / 360.0 * 4096.0 + 0.5, row + 0.5));
    return float(t.r) + float(t.g);
}
// Ein Wert des Basisrasters, bilinear; g ist schon geklemmt und auf die Punktmitte verschoben,
// bleibt also innerhalb seines Blocks.
float gridValue(float2 g, float block) {
    half4 t = baseGrid.eval(float2(g.x, g.y + block * baseSize.y));
    return float(t.r) + float(t.g);
}
float3 rgbToHsl(float3 c) {
    float mx = max(c.r, max(c.g, c.b));
    float mn = min(c.r, min(c.g, c.b));
    float l = (mx + mn) / 2.0;
    if (abs(mx - mn) < 0.00001) return float3(0.0, 0.0, l);
    float d = mx - mn;
    float s = l > 0.5 ? d / (2.0 - mx - mn) : d / (mx + mn);
    float h;
    if (mx == c.r) { h = (c.g - c.b) / d + (c.g < c.b ? 6.0 : 0.0); }
    else if (mx == c.g) { h = (c.b - c.r) / d + 2.0; }
    else { h = (c.r - c.g) / d + 4.0; }
    return float3(h * 60.0, s, l);
}
float hueToRgb(float p, float q, float t) {
    if (t < 0.0) t += 1.0;
    if (t > 1.0) t -= 1.0;
    if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
    if (t < 0.5) return q;
    if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
    return p;
}
float3 hslToRgb(float h, float s, float l) {
    if (s <= 0.0) return float3(l);
    float q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
    float p = 2.0 * l - q;
    float hk = h / 360.0;
    return float3(hueToRgb(p, q, hk + 1.0 / 3.0), hueToRgb(p, q, hk), hueToRgb(p, q, hk - 1.0 / 3.0));
}
float satGain(float v, float a) {
    a = clamp(a, -1.0, 1.0);
    if (a == 0.0) return v;
    if (a < 0.0) return v * (1.0 + a);
    if (v <= 0.0) return 0.0;
    if (v >= 1.0) return 1.0;
    return 1.0 - (1.0 - v) * exp(-a * v / (1.0 - v));
}
float bandGain(float v, float a) {
    a = clamp(a, -1.0, 1.0);
    if (a >= 0.0) return v * (1.0 + a * (1.0 - v));
    return v * (1.0 + a);
}
float quant(float v, float d) {
    float s = v * 255.0 + 0.5 + d;
    if (s <= 0.0) return 0.0;
    if (s >= 255.0) return 255.0;
    return floor(s);
}
// Rundet wie Math.Round: genau auf der Mitte zur geraden Zahl. Die Tontrennung trifft die Mitte
// wirklich, bei halbtransparenten Bildpunkten (Byte durch Alpha). roundEven selbst ist in
// Laufzeit-Shadern nicht freigegeben.
float roundHalfEven(float x) {
    float r = floor(x + 0.5);
    if (r - x == 0.5 && mod(r, 2.0) == 1.0) r -= 1.0;
    return r;
}
float stepRound(float v, float steps) {
    return roundHalfEven(roundHalfEven(clamp(v, 0.0, 1.0) * steps) / steps * 255.0) / 255.0;
}

// ApplyColorGrade. Die vier Zonentabellen sind auf dem Prozessor schon mit ihren Versaetzen zu
// drei Tabellen zusammengefasst; die Abtastung ist SampleGradeWeight.
float3 colorGrade(float3 c) {
    float y = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
    float pos = y * 20.0 - 0.5;
    float i0 = clamp(floor(pos), 0.0, 19.0);
    float i1 = min(19.0, i0 + 1.0);
    float f = clamp(pos - i0, 0.0, 1.0);
    if (pos <= 0.0) { i0 = 0.0; i1 = 0.0; f = 0.0; }
    if (pos >= 19.0) { i0 = 19.0; i1 = 19.0; f = 0.0; }
    float3 a = float3(0.0); float3 b = float3(0.0);
    for (int k = 0; k < 20; k++) {
        float3 v = float3(gradeY[k], gradeCb[k], gradeCr[k]);
        if (float(k) == i0) a = v;
        if (float(k) == i1) b = v;
    }
    float3 d = a + (b - a) * f;
    float dR = d.x + d.z / 0.713;
    float dB = d.x + d.y / 0.564;
    float dG = d.x - (0.299 * d.z / 0.713 + 0.114 * d.y / 0.564) / 0.587;
    return clamp(c + float3(dR, dG, dB), 0.0, 1.0);
}

// SampleCubeLutF: trilinear, wie TrilinearChannel. Die Tabelle liegt als Bild size*size x size
// (x = r + b * size, y = g), Rot und Gruen in cubeA, Blau in cubeB, je Wert zwei Kanaele.
float3 cubeAt(float r, float g, float b) {
    float2 q = float2(r + b * cubeSize + 0.5, g + 0.5);
    half4 ta = cubeA.eval(q);
    half4 tb = cubeB.eval(q);
    return float3(float(ta.x) + float(ta.y), float(ta.z) + float(ta.w), float(tb.x) + float(tb.y));
}
float3 sampleCube(float3 c) {
    float maxIndex = cubeSize - 1.0;
    float3 fc = clamp(c, 0.0, 1.0) * maxIndex;
    float3 c0 = floor(fc);
    float3 c1 = min(float3(maxIndex), c0 + 1.0);
    float3 t = fc - c0;
    float3 c000 = cubeAt(c0.r, c0.g, c0.b); float3 c100 = cubeAt(c1.r, c0.g, c0.b);
    float3 c010 = cubeAt(c0.r, c1.g, c0.b); float3 c110 = cubeAt(c1.r, c1.g, c0.b);
    float3 c001 = cubeAt(c0.r, c0.g, c1.b); float3 c101 = cubeAt(c1.r, c0.g, c1.b);
    float3 c011 = cubeAt(c0.r, c1.g, c1.b); float3 c111 = cubeAt(c1.r, c1.g, c1.b);
    float3 c00 = c000 + (c100 - c000) * t.r;
    float3 c10 = c010 + (c110 - c010) * t.r;
    float3 c01 = c001 + (c101 - c001) * t.r;
    float3 c11 = c011 + (c111 - c011) * t.r;
    float3 cc0 = c00 + (c10 - c00) * t.g;
    float3 cc1 = c01 + (c11 - c01) * t.g;
    return cc0 + (cc1 - cc0) * t.b;
}

// PointColorMath, in Gleitkomma statt Double. VB-Mod behaelt das Vorzeichen des Dividenden.
const float TWO_PI = 6.283185307179586;
float vbMod(float x, float m) {
    float q = x / m;
    return x - m * (q < 0.0 ? -floor(-q) : floor(q));
}
float srgbEncode(float v) {
    if (v <= 0.0031308) return 12.92 * v;
    return 1.055 * pow(v, 1.0 / 2.4) - 0.055;
}
float srgbDecode(float v) {
    if (v <= 0.04045) return v / 12.92;
    return pow((v + 0.055) / 1.055, 2.4);
}
// x: Farbton, y: Saettigung, z: Wert; w: 0, wenn der Bildpunkt zu dunkel ist.
float4 toHsv(float3 c) {
    float mx = max(c.r, max(c.g, c.b));
    float mn = min(c.r, min(c.g, c.b));
    if (mx <= 0.000001) return float4(0.0, 0.0, mx, 0.0);
    float ch = mx - mn;
    float s = ch / mx;
    float h;
    if (ch <= 0.0) h = 0.0;
    else if (mx == c.r) h = vbMod((c.g - c.b) / ch, 6.0);
    else if (mx == c.g) h = (c.b - c.r) / ch + 2.0;
    else h = (c.r - c.g) / ch + 4.0;
    h = h / 6.0 * TWO_PI;
    if (h < 0.0) h += TWO_PI;
    return float4(h, s, mx, 1.0);
}
float3 fromHsv(float h, float s, float v) {
    float hh = vbMod(h / TWO_PI * 6.0, 6.0);
    if (hh < 0.0) hh += 6.0;
    float c = v * s;
    float x = c * (1.0 - abs(vbMod(hh, 2.0) - 1.0));
    float m = v - c;
    float sector = floor(hh);
    float3 o;
    if (sector == 0.0) o = float3(c, x, 0.0);
    else if (sector == 1.0) o = float3(x, c, 0.0);
    else if (sector == 2.0) o = float3(0.0, c, x);
    else if (sector == 3.0) o = float3(0.0, x, c);
    else if (sector == 4.0) o = float3(x, 0.0, c);
    else o = float3(c, 0.0, x);
    return o + m;
}
float wrapAngle(float d) {
    float r = vbMod(d + 3.141592653589793, TWO_PI);
    if (r < 0.0) r += TWO_PI;
    return r - 3.141592653589793;
}
float smoothedRamp(float x, float p0, float p1, float p2, float p3, float riseExp, float fallExp) {
    float rise;
    if (x >= p1) rise = 1.0;
    else if (x <= p0) rise = 0.0;
    else { float t = (x - p0) / (p1 - p0); rise = pow(t * t * (3.0 - 2.0 * t), riseExp); }
    float fall;
    if (x <= p2) fall = 1.0;
    else if (x >= p3) fall = 0.0;
    else { float t = (p3 - x) / (p3 - p2); fall = pow(t * t * (3.0 - 2.0 * t), fallExp); }
    return min(rise, fall);
}
// Die Felder eines Farbfelds, ausgelesen in der Schleife: SkSL erlaubt Feldzugriffe nur mit
// Schleifenindex oder Konstanten, nicht mit einem Funktionsparameter.
float selectionWeight(float s, float ev, float d, float pSat, float pLum, float3 scale,
                      float4 hp, float4 sp, float4 lp) {
    return smoothedRamp(d * scale.x, hp.x, hp.y, hp.z, hp.w, 1.0282, 1.0282) *
           smoothedRamp(pSat + (s - pSat) * scale.y, sp.x, sp.y, sp.z, sp.w, 1.8348, 0.9897) *
           smoothedRamp(pLum + (ev - pLum) * scale.z, lp.x, lp.y, lp.z, lp.w, 2.7765, 1.1354) *
           min(1.0, s / 0.0578);
}
// PointColorMath.Apply: alle Farbfelder nacheinander; Rueckgabe das Gewicht des angezeigten.
float applyPointColors(inout float3 c) {
    float shown = -1.0;
    for (int i = 0; i < 8; i++) {
        if (float(i) >= pointCount) break;
        float pHue = pcHue[i]; float pSat = pcSat[i]; float pLum = pcLum[i]; float pVar = pcVar[i];
        float3 scale = float3(pcScale0[i], pcScale1[i], pcScale2[i]);
        float4 hp = float4(pcH0[i], pcH1[i], pcH2[i], pcH3[i]);
        float4 sp = float4(pcS0[i], pcS1[i], pcS2[i], pcS3[i]);
        float4 lp = float4(pcL0[i], pcL1[i], pcL2[i], pcL3[i]);
        float3 q = max(c, float3(0.0));
        float4 hsv = toHsv(q);
        if (pcVis[i] > 0.5) {
            shown = hsv.w > 0.5 ? selectionWeight(hsv.y, srgbEncode(min(1.0, hsv.z)), wrapAngle(hsv.x - pHue), pSat, pLum, scale, hp, sp, lp) : 0.0;
        }
        if (hsv.w < 0.5) continue;
        float ev = srgbEncode(min(1.0, hsv.z));
        float d = wrapAngle(hsv.x - pHue);
        float w = selectionWeight(hsv.y, ev, d, pSat, pLum, scale, hp, sp, lp);
        if (w <= 0.0) continue;
        float turn = w * (pcTurn[i] + pVar * d);
        float logS = w * pcLogS[i];
        float logV = w * pcLogV[i];
        if (pVar != 0.0) {
            float s2 = max(0.0001, min(1.0, pSat + (hsv.y - pSat) * exp2(0.7904 * pVar)));
            float e2 = max(0.0001, min(1.0, pLum + (ev - pLum) * exp2(0.2123 * pVar)));
            logS += w * log2(s2 / max(0.0001, hsv.y));
            logV += w * log2(srgbDecode(e2) / max(0.000001, srgbDecode(ev)));
        }
        float3 n = fromHsv(hsv.x + turn, clamp(hsv.y * exp2(logS), 0.0, 1.0), hsv.z * exp2(logV));
        c = min(n, max(q, float3(1.0))) + (c - q);
    }
    return shown;
}

half4 main(float2 p) {
    half4 pix = src.eval(p);
    float a = float(pix.a);
    float a255 = floor(a * 255.0 + 0.5);
    if (a255 <= 0.0) return half4(0.0);
    // Aus dem Byte gerechnet wie im Prozessor (Byte / 255, vormultipliziert Byte * (1 / Alpha)),
    // damit Werte auf einer Stufengrenze auf derselben Seite landen.
    float3 c = floor(float3(pix.rgb) * 255.0 + 0.5);
    if (a255 < 255.0 && premulIn > 0.5) {
        c = min(c * (1.0 / a255), float3(1.0));
    } else {
        c = c / 255.0;
    }
    float rr = c.r; float gg = c.g; float bb = c.b;

    if (useNeg > 0.5) {
        rr = tab(2.0, rr); gg = tab(3.0, gg); bb = tab(4.0, bb);
        if (negMono > 0.5) { float gray = 0.299 * rr + 0.587 * gg + 0.114 * bb; rr = gray; gg = gray; bb = gray; }
    }
    if (useWb > 0.5) {
        float3 lin = float3(tab(0.0, rr), tab(0.0, gg), tab(0.0, bb));
        float3 ad = float3(dot(wb0, lin), dot(wb1, lin), dot(wb2, lin));
        rr = tab(1.0, clamp(ad.r, 0.0, 1.0)); gg = tab(1.0, clamp(ad.g, 0.0, 1.0)); bb = tab(1.0, clamp(ad.b, 0.0, 1.0));
    }
    if (useCm > 0.5) {
        float3 v = float3(rr, gg, bb);
        float3 n = float3(dot(cm0, v), dot(cm1, v), dot(cm2, v)) + cmOff;
        rr = clamp(n.r, 0.0, 1.0); gg = clamp(n.g, 0.0, 1.0); bb = clamp(n.b, 0.0, 1.0);
    }

    float satVorTon = 0.0;
    if (useScalar > 0.5) {
        float vMax = max(rr, max(gg, bb));
        float vMin = min(rr, min(gg, bb));
        float lVor = (vMax + vMin) / 2.0;
        float chromaVor = vMax - vMin;
        if (lVor < 0.25 && chromaVor > 0.02) {
            float nenner = 1.0 - abs(2.0 * lVor - 1.0);
            if (nenner > 0.00001) {
                satVorTon = chromaVor / nenner;
                float torC = min(1.0, (chromaVor - 0.02) / 0.04);
                torC = torC * torC * (3.0 - 2.0 * torC);
                float tFade = 1.0;
                if (lVor > 0.1) { tFade = 1.0 - (lVor - 0.1) / 0.15; tFade = tFade * tFade * (3.0 - 2.0 * tFade); }
                satVorTon *= torC * tFade;
            }
        }
        if (useToneZones > 0.5 || useLocal > 0.5 || useMelissa > 0.5) {
            if (useToneBefore > 0.5) { rr = tab(8.0, rr); gg = tab(8.0, gg); bb = tab(8.0, bb); }
        }
        if (useLocal > 0.5) {
            // LocalToneGray, LocalToneBase.Sample und ApplyLocalToneZones.
            float yl = 0.2126 * tab(0.0, rr) + 0.7152 * tab(0.0, gg) + 0.0722 * tab(0.0, bb);
            float gray = tab(1.0, clamp(yl, 0.0, 1.0));
            float2 g = clamp((p - baseOrigin) / baseStep - 0.5, float2(0.0), baseSize - 1.0) + 0.5;
            float baseA = gridValue(g, 0.0);
            float baseB = gridValue(g, 1.0);
            float smoothValue = gridValue(g, 2.0);
            float baseValue = baseA * gray + baseB;
            float smoothAt = clamp(smoothValue, 0.0, 1.0);
            float detailShift = useLocalDetail > 0.5 ? tab(16.0, smoothAt) * (gray - smoothValue) : 0.0;
            float colorExcess = useLocalColor > 0.5 ? tab(17.0, smoothAt) : 0.0;
            float at = clamp(baseValue, 0.0, 1.0);
            float shift = tab(15.0, at) - at + detailShift;
            float lMax = max(rr, max(gg, bb));
            float lMin = min(rr, min(gg, bb));
            float nMax = clamp(tab(9.0, lMax) + shift + colorExcess * (lMax - gray), 0.0, 1.0);
            if (lMax - lMin < 0.000001) { rr = nMax; gg = nMax; bb = nMax; }
            else {
                float nMin = clamp(tab(9.0, lMin) + shift + colorExcess * (lMin - gray), 0.0, 1.0);
                if (nMax < nMin) { float mid = (nMax + nMin) * 0.5; rr = mid; gg = mid; bb = mid; }
                else {
                    float sc = (nMax - nMin) / (lMax - lMin);
                    rr = nMin + (rr - lMin) * sc; gg = nMin + (gg - lMin) * sc; bb = nMin + (bb - lMin) * sc;
                }
            }
        } else if (useToneZones > 0.5) {
            float zMax = max(rr, max(gg, bb));
            float zMin = min(rr, min(gg, bb));
            float newMax = tab(9.0, zMax);
            float newMin = tab(9.0, zMin);
            if (zMax - zMin < 0.000001) { rr = newMax; gg = newMax; bb = newMax; }
            else {
                float sc = (newMax - newMin) / (zMax - zMin);
                rr = newMin + (rr - zMin) * sc; gg = newMin + (gg - zMin) * sc; bb = newMin + (bb - zMin) * sc;
            }
        }
        if (useMelissa > 0.5) {
            // ApplyCurvesInMelissa: Kurven in ProPhoto-Primaerfarben mit sRGB-Gammakurve.
            float3 l = float3(tab(0.0, rr), tab(0.0, gg), tab(0.0, bb));
            float3 pp = float3(0.529214 * l.r + 0.330063 * l.g + 0.140723 * l.b,
                               0.098324 * l.r + 0.873625 * l.g + 0.028051 * l.b,
                               0.016879 * l.r + 0.117714 * l.g + 0.865407 * l.b);
            // Ohne verschachtelte Tabellenzugriffe: Skias Prozessor-Weg fuer Laufzeit-Shader rechnete
            // tab(5.0, tab(1.0, x)) gemessen falsch (viel zu hell), die Karte richtig.
            float gr0 = tab(1.0, clamp(pp.r, 0.0, 1.0));
            float gg0 = tab(1.0, clamp(pp.g, 0.0, 1.0));
            float gb0 = tab(1.0, clamp(pp.b, 0.0, 1.0));
            float cr0 = tab(5.0, gr0);
            float cg0 = tab(6.0, gg0);
            float cb0 = tab(7.0, gb0);
            pp = float3(tab(0.0, cr0), tab(0.0, cg0), tab(0.0, cb0));
            float3 nn = float3(2.034515 * pp.r - 0.727257 * pp.g - 0.307258 * pp.b,
                               -0.228704 * pp.r + 1.23143 * pp.g - 0.002726 * pp.b,
                               -0.008573 * pp.r - 0.153316 * pp.g + 1.161889 * pp.b);
            rr = tab(1.0, clamp(nn.r, 0.0, 1.0)); gg = tab(1.0, clamp(nn.g, 0.0, 1.0)); bb = tab(1.0, clamp(nn.b, 0.0, 1.0));
        } else {
            rr = tab(5.0, rr); gg = tab(6.0, gg); bb = tab(7.0, bb);
        }
    }

    float pointShown = -1.0;
    if (hslActive > 0.5) {
        float3 hsl = rgbToHsl(float3(rr, gg, bb));
        float h = hsl.x; float sat = hsl.y; float lum = hsl.z;
        if (sat < satVorTon) sat = satVorTon;
        if (useLum > 0.5) lum = tab(10.0, clamp(lum, 0.0, 1.0));
        if (vibMode == 1.0) {
            sat = sat * exp(vibrance * (1.0 - sat));
        } else if (vibMode == 2.0) {
            float v = hueTab(14.0, h);
            float satNew = clamp(sat * (1.0 + v * (1.0 - sat)), 0.0, 1.0);
            if (satNew != sat) {
                float3 a0 = hslToRgb(h, sat, lum);
                float3 b0 = hslToRgb(h, satNew, lum);
                float3 lb = float3(tab(0.0, b0.r), tab(0.0, b0.g), tab(0.0, b0.b));
                float y0 = 0.2126 * tab(0.0, a0.r) + 0.7152 * tab(0.0, a0.g) + 0.0722 * tab(0.0, a0.b);
                float y1 = 0.2126 * lb.r + 0.7152 * lb.g + 0.0722 * lb.b;
                if (y1 > 0.000001) {
                    float k = y0 / y1;
                    b0 = float3(tab(1.0, clamp(lb.r * k, 0.0, 1.0)), tab(1.0, clamp(lb.g * k, 0.0, 1.0)), tab(1.0, clamp(lb.b * k, 0.0, 1.0)));
                }
                float3 back = rgbToHsl(b0);
                h = back.x; sat = back.y; lum = back.z;
            }
        } else if (vibMode == 3.0) {
            sat = clamp(sat * (1.0 + vibrance * (1.0 - sat)), 0.0, 1.0);
        }
        if (useHsl > 0.5) {
            float hueShift = hueTab(11.0, h) * 360.0;
            float satShift = hueTab(12.0, h) * 100.0;
            float lumShift = hueTab(13.0, h) * 100.0;
            float chromaC = sat * (1.0 - abs(2.0 * lum - 1.0));
            float gateCs = min(1.0, chromaC / 0.2);
            gateCs = gateCs * gateCs * (3.0 - 2.0 * gateCs);
            float chromaW = min(1.0, sat / 0.1);
            chromaW = chromaW * chromaW * (3.0 - 2.0 * chromaW);
            h = mod(h + hueShift * gateCs + 360.0, 360.0);
            sat = satGain(sat, satShift * gateCs / 100.0);
            lum = bandGain(lum, lumShift * chromaW / 100.0);
        }
        float3 o = hslToRgb(h, sat, lum);
        if (pointCount > 0.0) {
            // Punktfarbe im HSV von linearem ProPhoto, wie dort.
            float3 lin = float3(srgbDecode(clamp(o.r, 0.0, 1.0)), srgbDecode(clamp(o.g, 0.0, 1.0)), srgbDecode(clamp(o.b, 0.0, 1.0)));
            lin = float3(dot(toPro0, lin), dot(toPro1, lin), dot(toPro2, lin));
            pointShown = applyPointColors(lin);
            lin = float3(dot(fromPro0, lin), dot(fromPro1, lin), dot(fromPro2, lin));
            o = float3(srgbEncode(clamp(lin.r, 0.0, 1.0)), srgbEncode(clamp(lin.g, 0.0, 1.0)), srgbEncode(clamp(lin.b, 0.0, 1.0)));
        }
        rr = o.r; gg = o.g; bb = o.b;
        if (useGrade > 0.5 && gradeAfterPreset < 0.5) {
            float3 gr = colorGrade(float3(rr, gg, bb));
            rr = gr.r; gg = gr.g; bb = gr.b;
        }
    } else if (useScalar > 0.5 && satVorTon > 0.0) {
        float3 hn = rgbToHsl(float3(rr, gg, bb));
        if (hn.y < satVorTon) { float3 w = hslToRgb(hn.x, satVorTon, hn.z); rr = w.r; gg = w.g; bb = w.b; }
    }

    if (shadowTint != 0.0) {
        float brightness = 0.299 * rr + 0.587 * gg + 0.114 * bb;
        float gewicht = max(0.0, 1.0 - brightness * 2.0);
        if (gewicht > 0.0) {
            float strength = shadowTint / 100.0 * 0.12 * gewicht;
            gg = clamp(gg - strength, 0.0, 1.0);
            rr = clamp(rr + strength * 0.5, 0.0, 1.0);
            bb = clamp(bb + strength * 0.5, 0.0, 1.0);
        }
    }
    if (useMixer > 0.5) {
        float3 v = float3(rr, gg, bb);
        float3 n = float3(dot(mix0, v), dot(mix1, v), dot(mix2, v)) + mixOff;
        rr = clamp(n.r, 0.0, 1.0); gg = clamp(n.g, 0.0, 1.0); bb = clamp(n.b, 0.0, 1.0);
    }
    if (useMap > 0.5) {
        float lumMap = clamp(0.299 * rr + 0.587 * gg + 0.114 * bb, 0.0, 1.0);
        float3 t = mapS + (mapH - mapS) * lumMap;
        rr += (t.r - rr) * mapAmount; gg += (t.g - gg) * mapAmount; bb += (t.b - bb) * mapAmount;
    }
    if (usePreset > 0.5) {
        float3 v = float3(rr, gg, bb);
        float3 f = clamp(float3(dot(pm0, v), dot(pm1, v), dot(pm2, v)) + pmOff, 0.0, 1.0);
        rr += (f.r - rr) * presetStrength; gg += (f.g - gg) * presetStrength; bb += (f.b - bb) * presetStrength;
        if (useGrade > 0.5 && gradeAfterPreset > 0.5) {
            float3 gr = colorGrade(float3(rr, gg, bb));
            rr = gr.r; gg = gr.g; bb = gr.b;
        }
    }
    if (useCube > 0.5) {
        float3 l = sampleCube(float3(rr, gg, bb));
        if (cubeStrength >= 0.999) { rr = l.r; gg = l.g; bb = l.b; }
        else { rr += (l.r - rr) * cubeStrength; gg += (l.g - gg) * cubeStrength; bb += (l.b - bb) * cubeStrength; }
    }
    if (posterize >= 2.0) {
        float steps = posterize - 1.0;
        rr = stepRound(rr, steps); gg = stepRound(gg, steps); bb = stepRound(bb, steps);
    }
    if (threshold >= 1.0) {
        float lumT = (0.299 * rr + 0.587 * gg + 0.114 * bb) * 255.0;
        float t = lumT >= threshold - 0.0001 ? 1.0 : 0.0;
        rr = t; gg = t; bb = t;
    }
    if (pointShown >= 0.0) {
        // PointColorMath.Visualize
        float yv = 0.2126 * srgbDecode(clamp(rr, 0.0, 1.0)) + 0.7152 * srgbDecode(clamp(gg, 0.0, 1.0)) +
                   0.0722 * srgbDecode(clamp(bb, 0.0, 1.0));
        float grayV = srgbEncode(yv);
        rr = grayV + (rr - grayV) * pointShown; gg = grayV + (gg - grayV) * pointShown; bb = grayV + (bb - grayV) * pointShown;
    }

    float d = float(dither.eval(p).r);
    float3 q = float3(quant(rr, d), quant(gg, d), quant(bb, d));
    if (a255 < 255.0 && premulIn > 0.5) {
        float af = a255 / 255.0;
        q = min(float3(a255), floor(q * af + 0.5));
    }
    return half4(half3(q / 255.0), half(a255 / 255.0));
}
"

    End Class

End Namespace
