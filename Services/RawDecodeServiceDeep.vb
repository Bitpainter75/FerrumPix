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
                Dim wantedKnee = If(recoverHighlights, knee, 1.0)
                Dim modelDenoise = If(lensChoice IsNot Nothing AndAlso lensChoice.ModelDenoiseStrength >= 0 AndAlso
                                      HasModelDenoised(path), lensChoice.ModelDenoiseStrength, -1.0)
                Dim deep = TryBuildFromCachedRaw(path, writeTime, demosaic, lens, baseEv, wantedKnee, exposureEv,
                                                 halfSize:=False, modelDenoise:=modelDenoise, deep:=True)
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

        ''' <summary>Wie <see cref="BuildFrom16Bit"/>, aber ohne die Rundung auf 8 Bit: jede Zeile
        ''' geht mit 16 Bit je Kanal ins Bild.</summary>
        Private Shared Function BuildDeepFrom16Bit(data As IntPtr, width As Integer, height As Integer,
                                                   lens As LensDataService.Korrektur,
                                                   baseEv As Double, normalization As Double,
                                                   knee As Double, sliderEv As Double,
                                                   shadowSaturation As Single(),
                                                   toneMatrix As Single()) As SKBitmap
            Dim bitmap = ImageProcessor.NewDeepBitmap(width, height, SKAlphaType.Opaque)
            If bitmap Is Nothing Then Return Nothing
            Try
                Dim targetPtr = bitmap.GetPixels()
                Dim targetStride = bitmap.RowBytes
                Dim rowLength = width * 4
                Dim ramp = ToneRampFor(baseEv, normalization, sliderEv)
                Convert16Rows(data, width, height,
                              Sub(y, row)
                              End Sub,
                              lens, ramp.ExposureEv, ramp.BlackLevel, knee, shadowSaturation, toneMatrix,
                              Sub(y, row) Marshal.Copy(row, 0, IntPtr.Add(targetPtr, y * targetStride), rowLength))
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
