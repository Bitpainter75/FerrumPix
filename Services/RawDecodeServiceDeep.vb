Imports System
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ' Die RAW-Stufe fuer die Ausgabe in 16 Bit.
    '
    ' Die Stufe rechnet ohnehin in 16 Bit und rundet erst in der letzten Zeile mit Dither auf 8
    ' (Convert16Rows). Hier kommt dieselbe Rechnung ohne diese Zeile heraus: ein Bild mit 16 Bit je
    ' Kanal, das die Kette als tiefes Bild neben dem 8-Bit-Bild mitfuehrt (ImageProcessorDeep.vb).
    ' Sensorrand und Verzeichnung laufen danach wie beim 8-Bit-Bild, die Verzeichnung auf dem
    ' Prozessor mit demselben Lanczos-3.
    '
    ' Gelesen wird aus dem 16-Bit-Zwischenspeicher der linearen Daten. Der liegt nach dem
    ' 8-Bit-Decode derselben Datei bereit; fehlt er, laeuft LibRaw einmal, um ihn zu fuellen.
    ' Der 8-Bit-Zwischenspeicher bleibt unberuehrt.
    Partial Public NotInheritable Class RawDecodeService

        ''' <summary>Das tiefe Bild zu einem Decode mit denselben Angaben wie <see cref="TryDecode"/>:
        ''' 16 Bit je Kanal, Rgba16161616, deckend. Nothing, wenn die Datei nicht durch die RAW-Stufe
        ''' laeuft (<see cref="DevelopsThroughRawStage"/>) oder der Decode scheitert; dann gibt es
        ''' fuer diese Datei keine Ausgabe in 16 Bit aus der RAW-Stufe.</summary>
        Friend Shared Function TryDecodeDeep(path As String,
                                             Optional lensChoice As LensDataService.Wahl = Nothing,
                                             Optional recoverHighlights As Boolean = True,
                                             Optional knee As Double = HighlightRecoveryKnee,
                                             Optional exposureEv As Double = 0.0) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not DevelopsThroughRawStage(path) Then Return Nothing
            Return DecodeGate.Run(Function() DecodeDeepIntern(path, lensChoice, recoverHighlights, knee, exposureEv))
        End Function

        Private Shared Function DecodeDeepIntern(path As String,
                                                 lensChoice As LensDataService.Wahl,
                                                 recoverHighlights As Boolean,
                                                 knee As Double,
                                                 exposureEv As Double) As SKBitmap
            Try
                ' Dieselben Groessen wie in DecodeIntern und DecodeCore, damit beide Bilder aus
                ' derselben Rechnung stammen.
                Dim writeTime = File.GetLastWriteTimeUtc(path)
                Dim baseEv = BaseExposureForFile(path) + exposureEv
                Dim lens = LensCorrectionForFile(path, lensChoice)
                Dim demosaic = ConfiguredDemosaic()
                Dim wantedKnee = If(recoverHighlights OrElse knee = LinearToneKnee, knee, 1.0)
                Dim modelDenoise = If(lensChoice IsNot Nothing AndAlso lensChoice.ModelDenoiseStrength >= 0 AndAlso
                                      HasModelDenoised(path), lensChoice.ModelDenoiseStrength, -1.0)
                ' Zuerst das tiefe Bild, das der 8-Bit-Decode derselben Ausgabe gleich mit umgesetzt
                ' hat (BeginDeepCapture). Es gilt nur, wenn es mit genau den Angaben entstand, mit
                ' denen es hier entstuende; sonst wird es freigegeben und wie bisher gerechnet.
                Dim deep = TakeCapturedDeep(DeepCaptureKey(RawDataIdentity(path, writeTime, demosaic, modelDenoise),
                                                           lens, baseEv, wantedKnee, exposureEv,
                                                           ShadowSaturationForFile(path), Not IsLinearLightSource(path)))
                If deep Is Nothing Then
                    deep = TryBuildFromCachedRaw(path, writeTime, demosaic, lens, baseEv, wantedKnee, exposureEv,
                                                 halfSize:=False, modelDenoise:=modelDenoise, deep:=True)
                End If
                If deep Is Nothing Then
                    ' Die linearen Daten liegen nicht mehr da: einmal fuellen, dann noch einmal.
                    If _reentrant Then
                        DecodeCore(path, baseEv, lens, warmOnly:=True)
                    Else
                        SyncLock _nativeLock
                            DecodeCore(path, baseEv, lens, warmOnly:=True)
                        End SyncLock
                    End If
                    deep = TryBuildFromCachedRaw(path, writeTime, demosaic, lens, baseEv, wantedKnee, exposureEv,
                                                 halfSize:=False, modelDenoise:=modelDenoise, deep:=True)
                End If
                If deep Is Nothing Then Return Nothing
                Return WithDistortionDeep(WithSensorEdgeCrop(deep, path, lensChoice), lens)
            Catch ex As Exception
                DiagnosticLogService.LogException("RawDecodeService.Deep", ex)
                Return Nothing
            End Try
        End Function

        ' ── Das tiefe Bild aus demselben Umsetzen wie das 8-Bit-Bild ──────────────────
        '
        ' Ohne diesen Weg lief die RAW-Stufe einer Ausgabe in 16 Bit zweimal ueber dieselben
        ' linearen Daten: einmal fuer das 8-Bit-Bild (BuildFrom16Bit), einmal fuer das tiefe
        ' (BuildDeepFrom16Bit). Convert16Rows gibt beide Zeilen aber in EINEM Lauf aus (onDeepRow).
        ' Verlangt die Ausgabe 16 Bit, nimmt der 8-Bit-Decode deshalb das tiefe Bild gleich mit, und
        ' TryDecodeDeep holt es dort ab, statt neu umzusetzen. Das 8-Bit-Bild bleibt Byte fuer Byte,
        ' was es war (onRow bekommt dieselbe Zeile), und das tiefe ist dieselbe Rechnung mit
        ' denselben Angaben.
        '
        ' DIE ANGABEN ENTSCHEIDEN, NICHT DER ZUFALL DER REIHENFOLGE. Das mitgenommene Bild gilt nur,
        ' wenn Herkunft der linearen Daten (Datei, Stand, Demosaic, Entklemmen, Farbweg, Entrauschen)
        ' und jede Groesse, die das Umsetzen steuert, mit dem uebereinstimmen, womit TryDecodeDeep es
        ' selbst rechnen wuerde (DeepCaptureKey). Sonst wird es freigegeben, und es bleibt beim
        ' zweiten Umsetzen; das kostet Zeit, aendert aber kein Bild.
        '
        ' JE FADEN. Die Ausgabe laeuft samt Decode auf dem Faden ihres Aufrufers (DecodeGate laesst
        ' denselben Faden durch); ein Decode auf einem anderen Faden, etwa fuer eine Kachel, nimmt
        ' nichts mit. Mitgenommen wird nur das erste Bild nach dem Beginn; ein zweiter Decode in
        ' derselben Ausgabe (das Ursprungsbild einer Protokollebene) ersetzt es nicht.

        ''' <summary>Das mitgenommene tiefe Bild einer Ausgabe und die Angaben, mit denen es
        ''' entstand.</summary>
        Private NotInheritable Class DeepCapture
            ''' Noch nichts mitgenommen: der naechste Decode darf.
            Public Wanted As Boolean = True
            ''' Herkunft der linearen Daten des laufenden Decodes (RawDataIdentity), nur waehrend
            ''' DecodeIntern umsetzt.
            Public Source As String
            Public Bitmap As SKBitmap
            Public Key As String
        End Class

        <ThreadStatic> Private Shared _deepCapture As DeepCapture

        ''' <summary>MESSSCHALTER, ab Werk an: das tiefe Bild beim 8-Bit-Decode mitnehmen. Aus
        ''' laeuft die RAW-Stufe wieder zweimal; der Pruefstand vergleicht beide Wege Byte fuer
        ''' Byte (gesetzt per Reflexion).</summary>
        Friend Shared DeepCaptureEnabled As Boolean = True

        ''' <summary>Beginnt fuer die Dauer des Using, auf diesem Faden das tiefe Bild beim
        ''' 8-Bit-Decode mitzunehmen. Mit <paramref name="wanted"/> = False geschieht nichts
        ''' (Nothing ist im Using erlaubt). Am Ende wird ein nicht abgeholtes Bild freigegeben.</summary>
        Friend Shared Function BeginDeepCapture(wanted As Boolean) As IDisposable
            If Not wanted OrElse Not DeepCaptureEnabled Then Return Nothing
            Return New DeepCaptureScope()
        End Function

        Private NotInheritable Class DeepCaptureScope
            Implements IDisposable

            Private ReadOnly _previous As DeepCapture
            Private _done As Boolean

            Public Sub New()
                _previous = _deepCapture
                _deepCapture = New DeepCapture()
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                If _done Then Return
                _done = True
                _deepCapture?.Bitmap?.Dispose()
                _deepCapture = _previous
            End Sub
        End Class

        ''' <summary>Fuer die Dauer des Using: der Decode auf diesem Faden setzt gerade die
        ''' linearen Daten dieser Herkunft um. Nothing, wenn nichts mitzunehmen ist.</summary>
        Private Shared Function ArmDeepCapture(path As String, writeTime As DateTime, demosaic As Integer,
                                               modelDenoise As Double) As IDisposable
            Dim capture = _deepCapture
            If capture Is Nothing OrElse Not capture.Wanted OrElse capture.Source IsNot Nothing Then Return Nothing
            capture.Source = RawDataIdentity(path, writeTime, demosaic, modelDenoise)
            Return New DeepCaptureArm(capture)
        End Function

        Private NotInheritable Class DeepCaptureArm
            Implements IDisposable

            Private ReadOnly _capture As DeepCapture

            Public Sub New(capture As DeepCapture)
                _capture = capture
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                _capture.Source = Nothing
            End Sub
        End Class

        ''' <summary>Woher die linearen Daten kommen: dieselben Groessen, nach denen
        ''' TryBuildFromCachedRaw den Zwischenspeicher annimmt, dazu die Staerke eines Entrauschens
        ''' mit Modell.</summary>
        Private Shared Function RawDataIdentity(path As String, writeTime As DateTime, demosaic As Integer,
                                                modelDenoise As Double) As String
            Return String.Join("|", path, writeTime.Ticks.ToString(Globalization.CultureInfo.InvariantCulture),
                               demosaic.ToString(Globalization.CultureInfo.InvariantCulture),
                               ConfiguredHighlightUnclip().ToString(), ColorPathState().ToString(Globalization.CultureInfo.InvariantCulture),
                               modelDenoise.ToString("R", Globalization.CultureInfo.InvariantCulture))
        End Function

        ''' <summary>Alles, was das Umsetzen einer Herkunft steuert: Objektiv (ohne Verzeichnung,
        ''' die laeuft danach), Grundbelichtung, Knie, Belichtung des Reglers, Daempfung der Tiefen
        ''' und die Korrektur rosa Lichter. Normierung und Farbmatrix gehoeren zur Herkunft.</summary>
        Private Shared Function DeepCaptureKey(identity As String, lens As LensDataService.Korrektur,
                                               baseEv As Double, knee As Double, sliderEv As Double,
                                               shadowSaturation As Single(), magentaRecovery As Boolean) As String
            Dim c = Globalization.CultureInfo.InvariantCulture
            Dim shadow = "-"
            If shadowSaturation IsNot Nothing Then
                Dim hash As New HashCode()
                For Each v In shadowSaturation
                    hash.Add(BitConverter.SingleToInt32Bits(v))
                Next
                shadow = shadowSaturation.Length.ToString(c) & ":" & hash.ToHashCode().ToString(c)
            End If
            Dim vignette = If(lens Is Nothing, "", String.Format(c, "{0:R};{1:R};{2:R}", lens.Vk1, lens.Vk2, lens.Vk3))
            Return String.Join("|", identity, LensKey(lens), vignette, baseEv.ToString("R", c), knee.ToString("R", c),
                               sliderEv.ToString("R", c), shadow, magentaRecovery.ToString())
        End Function

        ''' <summary>Fuer BuildFrom16Bit: ein leeres tiefes Bild, in das derselbe Lauf seine
        ''' Zeilen vor dem Dither schreibt, wenn dieser Decode mitnehmen soll. Sonst Nothing.</summary>
        Private Shared Function StartDeepCapture(width As Integer, height As Integer, lens As LensDataService.Korrektur,
                                                 baseEv As Double, knee As Double, sliderEv As Double,
                                                 shadowSaturation As Single(), magentaRecovery As Boolean) As SKBitmap
            Dim capture = _deepCapture
            If capture Is Nothing OrElse Not capture.Wanted OrElse capture.Source Is Nothing Then Return Nothing
            ' Nur der erste Decode der Ausgabe nimmt mit, auch wenn es danach scheitert.
            capture.Wanted = False
            Dim deep = ImageProcessor.NewDeepBitmap(width, height, SKAlphaType.Opaque)
            If deep Is Nothing Then Return Nothing
            capture.Key = DeepCaptureKey(capture.Source, lens, baseEv, knee, sliderEv, shadowSaturation, magentaRecovery)
            Return deep
        End Function

        ''' <summary>Legt das fertig umgesetzte tiefe Bild ab; der Besitz geht an die Ablage.</summary>
        Private Shared Sub KeepCapturedDeep(deep As SKBitmap)
            If deep Is Nothing Then Return
            Dim capture = _deepCapture
            If capture Is Nothing Then
                deep.Dispose()
                Return
            End If
            capture.Bitmap?.Dispose()
            capture.Bitmap = deep
        End Sub

        ''' <summary>Das mitgenommene tiefe Bild, wenn es mit <paramref name="key"/> entstand; der
        ''' Besitz geht an den Aufrufer. Passt es nicht, wird es freigegeben (es kaeme in dieser
        ''' Ausgabe nicht mehr zum Zug), und das Ergebnis ist Nothing.</summary>
        Private Shared Function TakeCapturedDeep(key As String) As SKBitmap
            Dim capture = _deepCapture
            If capture Is Nothing OrElse capture.Bitmap Is Nothing Then Return Nothing
            Dim deep = capture.Bitmap
            capture.Bitmap = Nothing
            If String.Equals(capture.Key, key, StringComparison.Ordinal) Then Return deep
            DiagnosticLogService.LogAlways("RawDecodeService.Deep", "das mitgenommene tiefe Bild passt nicht zu den Angaben, es wird neu umgesetzt")
            deep.Dispose()
            Return Nothing
        End Function

        ''' <summary>Wie <see cref="BuildFrom16Bit"/>, aber ohne die Rundung auf 8 Bit: jede Zeile
        ''' geht mit 16 Bit je Kanal ins Bild.</summary>
        Private Shared Function BuildDeepFrom16Bit(data As IntPtr, width As Integer, height As Integer,
                                                   lens As LensDataService.Korrektur,
                                                   baseEv As Double, normalization As Double,
                                                   knee As Double, sliderEv As Double,
                                                   shadowSaturation As Single(),
                                                   toneMatrix As Single(),
                                                   Optional magentaRecovery As Boolean = True) As SKBitmap
            Dim bitmap = ImageProcessor.NewDeepBitmap(width, height, SKAlphaType.Opaque)
            If bitmap Is Nothing Then Return Nothing
            Try
                Dim targetPtr = bitmap.GetPixels()
                Dim targetStride = bitmap.RowBytes
                Dim rowLength = width * 4
                ' Wie BuildFrom16Bit: linear (Filmnegativ) ohne Grundbelichtung und Daempfung der Tiefen.
                Dim linear = knee = LinearToneKnee
                Dim ramp = If(linear, LinearToneRampFor(normalization), ToneRampFor(baseEv, normalization, sliderEv))
                Convert16Rows(data, width, height,
                              Sub(y, row)
                              End Sub,
                              lens, ramp.ExposureEv, ramp.BlackLevel, knee, If(linear, Nothing, shadowSaturation), toneMatrix,
                              Sub(y, row) Marshal.Copy(row, 0, IntPtr.Add(targetPtr, y * targetStride), rowLength),
                              magentaRecovery:=magentaRecovery)
                Return bitmap
            Catch ex As Exception
                DiagnosticLogService.LogException("RawDecodeService.BuildDeepFrom16Bit", ex)
                bitmap.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Die Verzeichnungsstufe auf dem tiefen Bild. Besitz geht an den Aufrufer; das
        ''' uebergebene Bild wird verbraucht.</summary>
        Private Shared Function WithDistortionDeep(image As SKBitmap, lens As LensDataService.Korrektur) As SKBitmap
            If image Is Nothing Then Return Nothing
            If lens Is Nothing OrElse Not lens.HasDistortion Then Return image
            Dim corrected = RemoveDistortionDeep(image, lens)
            If corrected Is Nothing Then Return image
            image.Dispose()
            Return corrected
        End Function

        ''' <summary>Dieselbe Entzerrung wie <see cref="RemoveDistortionFromPixels"/> auf dem
        ''' Prozessor, mit 16 Bit je Kanal. Das Bild der RAW-Stufe ist deckend, gerechnet werden
        ''' deshalb nur die drei Farbkanaele.</summary>
        Private Shared Function RemoveDistortionDeep(source As SKBitmap, k As LensDataService.Korrektur) As SKBitmap
            Dim width = source.Width, height = source.Height
            If k Is Nothing OrElse Not k.HasDistortion OrElse width < 2 OrElse height < 2 Then Return Nothing
            Dim cx = (width - 1) / 2.0, cy = (height - 1) / 2.0
            Dim norm = LensDataService.NormScaleFor(k, width, height)
            If norm <= 0.0 Then Return Nothing
            Dim zoom = DistortionFillScale(k, width, height, norm)

            ' Die Quelle als ein Feld mit drei Werten je Bildpunkt, zeilenweise aus dem Bild geholt.
            Dim sourcePointer = source.GetPixels()
            If sourcePointer = IntPtr.Zero Then Return Nothing
            Dim sourceStride = source.RowBytes
            Dim values(width * 3 * height - 1) As Short
            Parallel.For(0, height,
                Sub(y)
                    Dim row(width * 4 - 1) As Short
                    Marshal.Copy(IntPtr.Add(sourcePointer, y * sourceStride), row, 0, width * 4)
                    Dim o = y * width * 3
                    For x = 0 To width - 1
                        values(o) = row(x * 4)
                        values(o + 1) = row(x * 4 + 1)
                        values(o + 2) = row(x * 4 + 2)
                        o += 3
                    Next
                End Sub)

            Dim target = ImageProcessor.NewDeepBitmap(width, height, SKAlphaType.Opaque)
            If target Is Nothing Then Return Nothing
            Dim targetPointer = target.GetPixels()
            Dim targetStride = target.RowBytes
            Parallel.For(0, height,
                Sub(y)
                    Dim dy = (y - cy) / zoom
                    Dim output(width * 4 - 1) As Short
                    Dim z = 0
                    For x = 0 To width - 1
                        Dim dx = (x - cx) / zoom
                        Dim rPix = Math.Sqrt(dx * dx + dy * dy)
                        Dim sx = cx, sy = cy
                        If rPix > 0.0 Then
                            Dim rNorm = rPix * norm
                            Dim factor = LensDataService.DistortionRadius(k, rNorm) / rNorm
                            sx = cx + dx * factor
                            sy = cy + dy * factor
                        End If
                        SampleLanczos3Deep(values, width, height, sx, sy, output, z)
                        z += 4
                    Next
                    Marshal.Copy(output, 0, IntPtr.Add(targetPointer, y * targetStride), output.Length)
                End Sub)
            Return target
        End Function

        ''' <summary>Wie <see cref="SampleLanczos3"/>, auf drei 16-Bit-Kanaelen ohne Alpha: derselbe
        ''' Einzug am Rand, dieselben Gewichte, geklemmt auf 0 bis 65535.</summary>
        Private Shared Sub SampleLanczos3Deep(source As Short(), width As Integer, height As Integer,
                                              sx As Double, sy As Double,
                                              target As Short(), targetOffset As Integer)
            Dim inset = If(width > 4 * DistortionEdgeInset AndAlso height > 4 * DistortionEdgeInset,
                           DistortionEdgeInset, 0)
            If sx < inset Then sx = inset
            If sy < inset Then sy = inset
            If sx > width - 1.001 - inset Then sx = width - 1.001 - inset
            If sy > height - 1.001 - inset Then sy = height - 1.001 - inset
            Dim ring = If(inset > 0, 1, 0)
            Dim maxX = width - 1 - ring, maxY = height - 1 - ring
            Dim x0 = CInt(Math.Floor(sx)), y0 = CInt(Math.Floor(sy))
            Dim tableX = CInt(Math.Round((sx - x0) * LanczosSteps)) * 6
            Dim tableY = CInt(Math.Round((sy - y0) * LanczosSteps)) * 6
            Dim weights = LanczosWeights
            Dim r = 0.0, g = 0.0, b = 0.0
            Dim rowStride = width * 3
            For j = 0 To 5
                Dim yy = Math.Min(maxY, Math.Max(ring, y0 - 2 + j))
                Dim row = yy * rowStride
                Dim wy = weights(tableY + j)
                For i = 0 To 5
                    Dim xx = Math.Min(maxX, Math.Max(ring, x0 - 2 + i))
                    Dim o = row + xx * 3
                    Dim w = wy * weights(tableX + i)
                    r += (source(o) And &HFFFF) * w
                    g += (source(o + 1) And &HFFFF) * w
                    b += (source(o + 2) And &HFFFF) * w
                Next
            Next
            target(targetOffset) = ImageProcessor.ToDeepShort(CInt(Math.Round(Math.Min(65535.0, Math.Max(0.0, r)))))
            target(targetOffset + 1) = ImageProcessor.ToDeepShort(CInt(Math.Round(Math.Min(65535.0, Math.Max(0.0, g)))))
            target(targetOffset + 2) = ImageProcessor.ToDeepShort(CInt(Math.Round(Math.Min(65535.0, Math.Max(0.0, b)))))
            target(targetOffset + 3) = -1S
        End Sub

    End Class

End Namespace
