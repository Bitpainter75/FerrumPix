Imports System
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ' Der Einstieg fuer LINEARES LICHT in die RAW-Grundstufe: OpenEXR, Radiance HDR und TIFF mit
    ' Gleitkomma. Diese Dateien tragen Licht ueber Weiss, und sie werden wie ein schon entwickeltes,
    ' weissabgeglichenes RAW behandelt: die Werte gehen als lineare 16-Bit-Daten in denselben
    ' Zwischenspeicher wie LibRaws Ausgabe, und von dort laeuft alles wie bei jeder RAW durch
    ' Convert16Rows, also Belichtung als Faktor in der Rampe, Lichterschulter samt Normierung,
    ' ACR3-Kurve in ProPhoto, sRGB, Dither; das tiefe Bild fuer die Ausgabe in 16 Bit ebenso.
    '
    ' WAS VON DER STUFE BLEIBT UND WAS NICHT, je mit Grund:
    ' - Belichtung (Regler Belichtung, RawExposureEv): bleibt, sie ist ein Faktor auf das Licht.
    ' - Lichterschulter und das mitgezogene Knie (Regler Lichter, RawHighlightKnee): bleiben, genau
    '   dafuer ist der Weg da; Werte ueber 1 rollen weich ins Weiss und lassen sich mit Belichtung
    '   und Lichtern zurueckholen.
    ' - Schwarzabzug der Rampe und die ACR3-Kurve: bleiben, sie gehoeren zur Grundentwicklung und
    '   nicht zu einer Kamera.
    ' - Normierung: bleibt, aber aus den DATEN statt aus den Multiplikatoren (LinearNormalizationFor):
    '   die 16 Bit muessen den Kopfraum der Datei fassen.
    ' - Grundbelichtung (die feste und die je Kamera): faellt weg. Beide gleichen den Abstand aus,
    '   mit dem ein Sensor das Mittelgrau unter seine Saettigung legt; lineares Licht liegt schon in
    '   Szenenwerten (Mittelgrau um 0,18), und 0 EV entwickelt es wie eine richtig belichtete RAW.
    ' - Demosaic, Rauschminderung vor dem Demosaic, Pegel, Sensorrand, Seitenverhaeltnis der Kamera,
    '   Abschattung aus OpcodeList: fallen weg, es gibt keinen Sensor.
    ' - Objektivprofile (Verzeichnung, Farbquerfehler, Vignettierung): fallen weg, es gibt weder
    '   Kamera noch Objektiv in der Datei.
    ' - Farbmatrix: an ihrer Stelle die Matrix der Datei nach sRGB (chromaticities, PRIMARIES), sonst
    '   die Einheitsmatrix; sie wirkt wie die Kameramatrix erst in der Tonstufe, in Gleitkomma.
    ' - Weissabgleich der Aufnahme, zwei Matrizen nach Lichtart, Kalibrierung je Kamera, Daempfung
    '   der Tiefen je Kamerageneration: fallen weg; die Daten sind schon abgeglichen, der Regler
    '   Weissabgleich rechnet wie bei einem JPEG in der Kette.
    ' - Korrektur rosa Lichter: faellt weg, sie gilt einzeln klemmenden Sensorkanaelen.
    ' - Entrauschen mit Modell in der RAW-Stufe: faellt weg, es braucht das Farbmuster des Sensors.
    Partial Public NotInheritable Class RawDecodeService

        ''' <summary>Die Grundbelichtung fuer lineares Licht: keine. Siehe den Kopf dieser Datei.</summary>
        Friend Const LinearBaseExposureEv As Double = 0.0

        ''' <summary>Hoechster Kopfraum, den die 16-Bit-Daten fassen, als Vielfaches von Weiss: vier
        ''' Blendenstufen. Mehr kostete Feinheit in den Tiefen (bei 16 liegt eine 16-Bit-Stufe bei
        ''' 0,00024 des Weiss, also unter einer Stufe des 8-Bit-Bilds bis tief in die Schatten);
        ''' Licht darueber wird abgeschnitten und laesst sich mit Belichtung nicht mehr holen.</summary>
        Friend Const LinearHeadroomLimit As Double = 16.0

        ''' <summary>Geht diese Datei als lineares Licht durch die RAW-Grundstufe? EXR und Radiance
        ''' nach der Endung, ein TIFF nach seinem Kopf (Gleitkomma).</summary>
        Public Shared Function IsLinearLightSource(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Return ExrDecodeService.IsSupportedExr(path) OrElse
                   RadianceHdrDecodeService.IsSupportedRadiance(path) OrElse
                   TiffPreviewService.IsFloatTiff(path)
        End Function

        ''' <summary>Kann die RAW-Stufe DIESE Datei entwickeln? Fuer lineares Licht entscheidet sein
        ''' eigener Leser (<see cref="LinearLightReaderAvailable"/>), denn LibRaw kennt die Formate
        ''' nicht und wird dafuer nie aufgerufen; fuer alles andere, ob LibRaw geladen ist. Jeder
        ''' Aufrufer, der nach einer bestimmten Datei fragt, fragt hier statt nach
        ''' <see cref="IsAvailable"/>: sonst blieben EXR, Radiance und TIFF mit Gleitkomma auf einem
        ''' System ohne LibRaw leer, obwohl ihr Leser da ist.</summary>
        Public Shared Function CanDevelop(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            If IsLinearLightSource(path) Then Return LinearLightReaderAvailable(path)
            Return IsAvailable
        End Function

        ''' <summary>Ist der Leser fuer dieses lineare Licht da? EXR braucht OpenEXRCore, Radiance
        ''' liest ein eigener Leser, das TIFF mit Gleitkomma LibTiff.NET; beide sind verwaltet und
        ''' immer da.</summary>
        Friend Shared Function LinearLightReaderAvailable(path As String) As Boolean
            If ExrDecodeService.IsSupportedExr(path) Then Return ExrDecodeService.IsAvailable
            Return RadianceHdrDecodeService.IsSupportedRadiance(path) OrElse TiffPreviewService.IsFloatTiff(path)
        End Function

        ''' <summary>Das lineare Licht der Datei aus dem zustaendigen Leser, oder Nothing.</summary>
        Friend Shared Function ReadLinearLight(path As String) As LinearLightImage
            If ExrDecodeService.IsSupportedExr(path) Then Return ExrDecodeService.TryReadLinear(path)
            If RadianceHdrDecodeService.IsSupportedRadiance(path) Then Return RadianceHdrDecodeService.TryReadLinear(path)
            If TiffPreviewService.IsFloatTiff(path) Then Return TiffPreviewService.TryReadLinear(path)
            Return Nothing
        End Function

        ''' <summary>Die Masse aus dem Kopf, wie der Decode sie liefert, oder (0, 0).</summary>
        Public Shared Function LinearLightSize(path As String) As (Width As Integer, Height As Integer)
            If ExrDecodeService.IsSupportedExr(path) Then Return ExrDecodeService.TryGetSize(path)
            If RadianceHdrDecodeService.IsSupportedRadiance(path) Then Return RadianceHdrDecodeService.TryGetSize(path)
            If TiffPreviewService.IsFloatTiff(path) Then Return TiffPreviewService.TryGetSize(path)
            Return (0, 0)
        End Function

        ''' <summary>Was <see cref="ReadFileMetadata"/> fuer lineares Licht meldet: nur die Masse,
        ''' keine Kamera, keine Drehung. Nothing, wenn der Kopf nicht lesbar ist.</summary>
        Private Shared Function LinearLightMetadata(path As String) As FileMetadata
            Dim size = LinearLightSize(path)
            If size.Width <= 0 OrElse size.Height <= 0 Then Return Nothing
            Return New FileMetadata With {
                .Width = size.Width, .Height = size.Height,
                .OrientedWidth = size.Width, .OrientedHeight = size.Height,
                .RawFrameWidth = size.Width, .RawFrameHeight = size.Height,
                .Flip = 0
            }
        End Function

        ''' <summary>Der Kopfraum dieses Bildes: das Vielfache von Weiss, auf das die 16-Bit-Daten
        ''' gestreckt werden. Gemessen wird das hellste Promille der Promille (99,99 Prozent) des
        ''' hellsten Kanals, damit einzelne Ausreisser (eine Spiegelung der Sonne) den Bereich nicht
        ''' fuer das ganze Bild aufspannen; aufgerundet auf eine ganze Blendenstufe, zwischen 1 und
        ''' <see cref="LinearHeadroomLimit"/>.</summary>
        Friend Shared Function LinearNormalizationFor(image As LinearLightImage) As Double
            If image Is Nothing OrElse image.Rgb Is Nothing Then Return 1.0
            ' Ein Achtel Blendenstufe je Fach, von 2^-16 bis 2^16.
            Const BinsPerStop As Integer = 8
            Const LowestStop As Integer = -16
            Const Bins As Integer = 32 * BinsPerStop + 1
            Dim histogram(Bins - 1) As Long
            Dim rgb = image.Rgb
            Dim pixels = rgb.Length \ 3
            Dim gate As New Object()
            Parallel.For(0, Math.Max(1, Environment.ProcessorCount), Sub(worker)
                Dim local(Bins - 1) As Long
                Dim from = CInt(CLng(pixels) * worker \ Math.Max(1, Environment.ProcessorCount))
                Dim [to] = CInt(CLng(pixels) * (worker + 1) \ Math.Max(1, Environment.ProcessorCount))
                For i = from To [to] - 1
                    Dim v = Math.Max(rgb(i * 3), Math.Max(rgb(i * 3 + 1), rgb(i * 3 + 2)))
                    If Not Single.IsFinite(v) OrElse v <= 0.0F Then Continue For
                    Dim bin = CInt(Math.Floor((Math.Log(v, 2.0) - LowestStop) * BinsPerStop))
                    local(Math.Max(0, Math.Min(Bins - 1, bin))) += 1
                Next
                SyncLock gate
                    For b = 0 To Bins - 1
                        histogram(b) += local(b)
                    Next
                End SyncLock
            End Sub)
            Dim total = 0L
            For Each count In histogram
                total += count
            Next
            If total = 0 Then Return 1.0
            Dim allowed = Math.Max(1L, total \ 10000)
            Dim above = 0L
            Dim top = 0.0
            For b = Bins - 1 To 0 Step -1
                above += histogram(b)
                If above > allowed Then
                    ' Die obere Kante dieses Fachs.
                    top = 2.0 ^ ((b + 1) / CDbl(BinsPerStop) + LowestStop)
                    Exit For
                End If
            Next
            If top <= 1.0 Then Return 1.0
            Return Math.Min(LinearHeadroomLimit, 2.0 ^ Math.Ceiling(Math.Log(top, 2.0) - 0.000001))
        End Function

        ''' <summary>Lineares Licht als lineare 16-Bit-Daten (drei Werte je Bildpunkt, wie LibRaws
        ''' Ausgabe), Weiss bei 65535 geteilt durch <paramref name="normalization"/>. Mit
        ''' <paramref name="half"/> je zwei mal zwei Bildpunkte gemittelt, in linearem Licht, wie
        ''' LibRaws halbe Kantenlaenge fuer die Kacheln. Werte unter null werden null.</summary>
        Private Shared Function LinearTo16Bit(image As LinearLightImage, normalization As Double, half As Boolean,
                                              ByRef width As Integer, ByRef height As Integer) As Byte()
            Dim sourceWidth = image.Width
            Dim sourceHeight = image.Height
            Dim factor = If(half AndAlso sourceWidth >= 2 AndAlso sourceHeight >= 2, 2, 1)
            Dim w = sourceWidth \ factor
            Dim h = sourceHeight \ factor
            width = w
            height = h
            Dim bytes(CInt(CLng(w) * h * 6) - 1) As Byte
            Dim scale = 65535.0 / normalization / (factor * factor)
            Dim rgb = image.Rgb
            Parallel.For(0, h,
                Sub(y)
                    Dim row(w * 3 - 1) As Short
                    For x = 0 To w - 1
                        For c = 0 To 2
                            Dim sum = 0.0
                            For dy = 0 To factor - 1
                                Dim sourceRow = (y * factor + dy) * sourceWidth
                                For dx = 0 To factor - 1
                                    Dim v = rgb((sourceRow + x * factor + dx) * 3 + c)
                                    If Single.IsFinite(v) AndAlso v > 0.0F Then sum += v
                                Next
                            Next
                            Dim q = CInt(Math.Round(Math.Min(65535.0, sum * scale)))
                            row(x * 3 + c) = CShort(If(q >= 32768, q - 65536, q))
                        Next
                    Next
                    Buffer.BlockCopy(row, 0, bytes, y * w * 6, w * 6)
                End Sub)
            Return bytes
        End Function

        ''' <summary>DecodeCore fuer lineares Licht. Dieselben Schritte und derselbe Zwischenspeicher
        ''' wie nach LibRaw: die 16-Bit-Daten sichern (nicht fuer die halbe Kantenlaenge), dann
        ''' umsetzen. Laeuft unter DecodeGate wie jeder Decode.</summary>
        Private Shared Function DecodeLinearCore(path As String, baseEv As Double, useHalfSize As Boolean,
                                                 knee As Double, sliderEv As Double, warmOnly As Boolean,
                                                 keepRaw As Boolean, writeTime As DateTime, demosaic As Integer) As SKBitmap
            Dim clock = Diagnostics.Stopwatch.StartNew()
            Dim image = ReadLinearLight(path)
            If image Is Nothing OrElse image.Rgb Is Nothing Then
                DiagnosticLogService.LogAlways("RawDecodeService.Linear", $"nicht lesbar: {IO.Path.GetFileName(path)}")
                Return Nothing
            End If
            Dim readMs = clock.Elapsed.TotalMilliseconds
            Dim normalization = LinearNormalizationFor(image)
            Dim width, height As Integer
            Dim bytes = LinearTo16Bit(image, normalization, useHalfSize, width, height)
            Dim matrix = image.ToSrgb
            image = Nothing
            ' Die Matrix der Datei wie die Kameramatrix: in der Tonstufe in ProPhoto erst beim
            ' Umsetzen, in Gleitkomma; auf dem alten Weg (Messschalter) gleich hier in 16 Bit.
            Dim toneMatrix As Single() = If(WideToneStage, ToneStageMatrix(matrix), Nothing)
            Dim pin = GCHandle.Alloc(bytes, GCHandleType.Pinned)
            Try
                If toneMatrix Is Nothing AndAlso matrix IsNot Nothing Then ApplyCameraMatrix(pin.AddrOfPinnedObject(), width, height, matrix)
                DiagnosticLogService.LogAlways("RawDecodeService.Linear",
                    $"file={IO.Path.GetFileName(path)}; size={width}x{height}; half={useHalfSize}; normalization={normalization:F2}; " &
                    $"matrix={If(matrix Is Nothing, "keine", "aus der Datei")}; lesen={readMs:F0} ms")
                If keepRaw Then
                    SyncLock _cacheLock
                        _cachedRawBytes = bytes
                        FringePlane.Drop()
                        _cachedRawWidth = width
                        _cachedRawHeight = height
                        _cachedRawNormalization = normalization
                        _cachedRawToneMatrix = toneMatrix
                        _cachedRawPath = path
                        _cachedRawWriteTimeUtc = writeTime
                        _cachedRawDemosaic = demosaic
                        _cachedRawUnclip = ConfiguredHighlightUnclip()
                        _cachedRawColorPath = ColorPathState()
                        _cachedRawHalf = useHalfSize
                    End SyncLock
                End If
                If warmOnly Then Return Nothing
                Return BuildFrom16Bit(pin.AddrOfPinnedObject(), width, height, Nothing, baseEv, normalization, knee, sliderEv,
                                      Nothing, toneMatrix, magentaRecovery:=False)
            Finally
                pin.Free()
            End Try
        End Function

    End Class

End Namespace
