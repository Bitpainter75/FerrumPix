Imports System
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ' Entrauschen mit Modell in der RAW-Stufe (DenoiseModelService).
    '
    ' Das Modell rechnet auf den LINEAREN 16-Bit-Daten, die auch der 16-Bit-Zwischenspeicher haelt,
    ' und sein Ergebnis bekommt einen eigenen Speicher daneben. Jeder Decode mit eingeschaltetem
    ' Entrauschen setzt dann aus diesem Speicher um statt aus den verrauschten Daten; Belichtung,
    ' Lichter und Objektiv rechnen danach wie immer, ohne dass das Modell neu laufen muss.
    '
    ' DER DECODE RECHNET DAS MODELL NIE SELBST. Er nimmt das Ergebnis, wenn es da ist, und sonst die
    ' verrauschten Daten. Das Modell kostet Sekunden bis Minuten; ausgeloest wird es nur von dem, der
    ' darauf warten will (Editor mit Fortschritt und Abbruch, Speichern und Stapel), ueber
    ' PrepareModelDenoise. Eine Vorschau, eine Kachel oder der Betrachter loesen es nie aus.
    '
    ' AUF DER PLATTE. Mit der Einstellung KeepModelDenoiseFiles geht das Ergebnis als .fpdnz neben
    ' die RAW (ModelDenoiseFileService, StoreModelDenoiseFile), und PrepareModelDenoise sowie das
    ' Oeffnen im Editor holen es von dort (LoadModelDenoiseFile), bevor das Modell rechnet. Die
    ' Datei haelt nur die entrauschten Daten, keine Mischung: die Staerke bleibt frei.
    '
    ' DIE STAERKE IST EINE MISCHUNG, und sie gilt NUR FUER DIE HELLIGKEIT: die Farbe kommt immer
    ' voll aus dem Modell. Die Ausgabe des Modells wird je Bildpunkt um denselben Betrag in allen
    ' drei Kanaelen verschoben, bis ihre Helligkeit zwischen der verrauschten und der entrauschten
    ' liegt; die Abstaende der Kanaele zueinander, die Farbe also, bleiben die des Modells.
    ' Farbrauschen hat keine Entsprechung im Motiv, beim Helligkeitsrauschen ist die Staerke ein
    ' echter Handel zwischen Korn und feiner Zeichnung.

    Partial Public NotInheritable Class RawDecodeService

        ''' <summary>Das Farbmuster des Sensors, so weit es fuer das Entrauschen zaehlt.</summary>
        Public Enum SensorPattern
            ''' <summary>Nicht lesbar oder nicht bestaetigt.</summary>
            Unknown = 0
            ''' <summary>Rot, Gruen, Blau im 2x2-Muster.</summary>
            Bayer = 1
            ''' <summary>Fujis 6x6-Muster.</summary>
            XTrans = 2
            ''' <summary>Kein Farbmuster: lineare DNG, verkleinerte RAW (sRAW, mRAW), Schwarzweiss,
            ''' vier Farben (CMYG) oder ein Sondermuster. LibRaw entwickelt sie trotzdem in die
            ''' RAW-Stufe, und TreeNet rechnet dort wie bei einem Muster (gemessen, siehe
            ''' Diagnostics/TreeNetMessung, "RAWs ohne Farbmuster").</summary>
            Other = 3
            ''' <summary>Eine DNG-Huelle um ein fertig gerendertes Bild (IsFinishedRgb). Sie laeuft an
            ''' der RAW-Stufe vorbei; entrauscht wird sie wie ein fertiges Bild.</summary>
            Finished = 4
        End Enum

        ' libraw_iparams_t hinter normalized_model (Versatz 260, 64 Byte): maker_index, raw_count,
        ' dng_version, is_foveon (je 4 Byte), colors (int), filters (unsigned), xtrans[6][6],
        ' xtrans_abs[6][6], cdesc[5].
        Private Const IparamsColorsOffset As Integer = 340
        Private Const IparamsFiltersOffset As Integer = 344
        Private Const IparamsCdescOffset As Integer = 420

        ''' <summary>Das Farbmuster der Datei, nur ueber open_file (unter einer Millisekunde).
        '''
        ''' Gelesen wird an festen Versaetzen, wie bei <see cref="ReadFileMetadata"/>, und deshalb
        ''' nur, was sich PRUEFEN laesst: der Herstellername muss Text sein (dann stimmt der Aufbau
        ''' bis dorthin), die Farbzahl 1, 3 oder 4 und die Farbbeschreibung cdesc druckbare
        ''' Buchstaben. Faellt eine Pruefung durch, ist das Muster unbekannt, und das Entrauschen
        ''' wird nicht angeboten - kein falsches Ja.
        '''
        ''' filters: 0 heisst kein Muster, 9 ist X-Trans, kleine Zahlen sind Sondermuster (Leaf), alles
        ''' ueber 999 ist ein Bayer-Muster (dieselbe Grenze wie in dcraw).</summary>
        Public Shared Function ReadSensorPattern(path As String) As SensorPattern
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable OrElse _getIparams Is Nothing Then Return SensorPattern.Unknown
            If IsFinishedRgb(path) Then Return SensorPattern.Finished
            If Not _reentrant Then
                SyncLock _nativeLock
                    Return ReadSensorPatternCore(path)
                End SyncLock
            End If
            Return ReadSensorPatternCore(path)
        End Function

        Private Shared Function ReadSensorPatternCore(path As String) As SensorPattern
            Dim handle = _init(0UI)
            If handle = IntPtr.Zero Then Return SensorPattern.Unknown
            Dim pathPtr As IntPtr = IntPtr.Zero
            Try
                pathPtr = StringToUtf8(path)
                If _openFile(handle, pathPtr) <> 0 Then Return SensorPattern.Unknown
                Dim iparams = _getIparams(handle)
                If iparams = IntPtr.Zero Then Return SensorPattern.Unknown
                If Not IsPlausibleText(FixedText(iparams, IparamsNormalizedMakeOffset)) Then Return SensorPattern.Unknown
                Dim colors = Marshal.ReadInt32(iparams, IparamsColorsOffset)
                If colors <> 1 AndAlso colors <> 3 AndAlso colors <> 4 Then Return SensorPattern.Unknown
                Dim cdescBytes(4) As Byte
                Marshal.Copy(IntPtr.Add(iparams, IparamsCdescOffset), cdescBytes, 0, 5)
                Dim cdescLength = Array.IndexOf(cdescBytes, CByte(0))
                If cdescLength < 3 Then Return SensorPattern.Unknown
                Dim cdesc = Text.Encoding.ASCII.GetString(cdescBytes, 0, cdescLength)
                For Each c In cdesc
                    If Not Char.IsLetter(c) Then Return SensorPattern.Unknown
                Next
                Dim filters = CUInt(Marshal.ReadInt32(iparams, IparamsFiltersOffset) And &HFFFFFFFFL)
                If colors <> 3 OrElse Not cdesc.StartsWith("RGB", StringComparison.Ordinal) Then Return SensorPattern.Other
                If filters = 9UI Then Return SensorPattern.XTrans
                If filters > 999UI Then Return SensorPattern.Bayer
                Return SensorPattern.Other
            Catch ex As Exception
                DiagnosticLogService.LogException("RawDecodeService.ReadSensorPattern", ex)
                Return SensorPattern.Unknown
            Finally
                _close(handle)
                If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
            End Try
        End Function

        ' ── Der Speicher fuer das Ergebnis des Modells ─────────────────────────────────────────

        ''' <summary>Die entrauschten linearen Daten, im Aufbau des 16-Bit-Zwischenspeichers. Er
        ''' haelt EINE Datei, wie die beiden anderen, und kostet dasselbe (sechs Byte je
        ''' Bildpunkt). Er wird NICHT mit dem verrauschten Speicher verdraengt: der laesst sich mit
        ''' einem LibRaw-Lauf wiederholen, das Ergebnis des Modells nur mit dem Modell.</summary>
        Private Shared _denoisedBytes As Byte()
        ''' <summary>Woraus die entrauschten Daten entstanden sind: Datei, Aenderungszeit, Demosaic,
        ''' Entklemmen, Farbweg und die Modelldatei. Weicht eines davon ab, passen sie nicht mehr zu
        ''' den verrauschten Daten, mit denen gemischt wird.</summary>
        Private Shared _denoisedKey As String = ""
        ''' <summary>Breite und Hoehe der entrauschten Daten, fuer die Datei auf der Platte. Der
        ''' 16-Bit-Zwischenspeicher kann dann schon einer anderen Datei gehoeren.</summary>
        Private Shared _denoisedWidth As Integer
        Private Shared _denoisedHeight As Integer
        ''' <summary>Die Mischung fuer die zuletzt verlangte Staerke; bei voller Staerke nicht belegt.</summary>
        Private Shared _blendBytes As Byte()
        Private Shared _blendStrength As Double = Double.NaN
        Private Shared _blendKey As String = ""

        ''' <summary>Der Schluessel der entrauschten Daten fuer eine Datei, mit allem, was die
        ''' verrauschten Daten veraendert. Leer, wenn die Datei nicht lesbar ist.</summary>
        Private Shared Function DenoiseKeyFor(path As String) As String
            Dim writeTime As DateTime
            Try
                writeTime = File.GetLastWriteTimeUtc(path)
            Catch
                Return ""
            End Try
            Return String.Join("|", path, writeTime.Ticks.ToString(Globalization.CultureInfo.InvariantCulture),
                               ConfiguredDemosaic().ToString(Globalization.CultureInfo.InvariantCulture),
                               If(ConfiguredHighlightUnclip(), "u", "c"),
                               ColorPathState().ToString(Globalization.CultureInfo.InvariantCulture),
                               AiModelService.BestFile(DenoiseModelService.ModelFile))
        End Function

        ''' <summary>Der Schluessel der Datei auf der Platte (ModelDenoiseFileService): alles aus
        ''' <see cref="DenoiseKeyFor"/> ausser dem Pfad, dafuer die Groesse der RAW. Die Datei wandert
        ''' mit der RAW; was die verrauschten Daten veraendert, macht sie ungueltig. Von der
        ''' Modelldatei zaehlen Name und Groesse, nicht der Ort: der Modellordner ist je Rechner
        ''' ein anderer.
        '''
        ''' FEHLT DAS MODELL, zaehlt beim Lesen nur der Teil der RAW (<paramref name="forReading"/>,
        ''' Schluessel endet auf ModelDenoiseFileService.AnyModel). Die Datei ist ja gerade dafuer da,
        ''' ohne Modell auszukommen; mit dem Modellteil passte sie nach dem Entfernen des Modells nie
        ''' mehr. Ein ANDERES Modell (Update) macht sie dagegen weiter ungueltig. Leer heisst: die
        ''' RAW ist nicht lesbar, oder es soll geschrieben werden und das Modell fehlt.</summary>
        Friend Shared Function DenoiseFileKeyFor(path As String, Optional forReading As Boolean = False) As String
            Dim info As FileInfo
            Try
                info = New FileInfo(path)
                If Not info.Exists Then Return ""
            Catch
                Return ""
            End Try
            ' BestFile liefert nur den Dateinamen, die Groesse kommt ueber ModelPath.
            Dim model = AiModelService.BestFile(DenoiseModelService.ModelFile)
            Dim inv = Globalization.CultureInfo.InvariantCulture
            Dim rawPart = String.Join("|", info.Length.ToString(inv), info.LastWriteTimeUtc.Ticks.ToString(inv),
                                      ConfiguredDemosaic().ToString(inv),
                                      If(ConfiguredHighlightUnclip(), "u", "c"),
                                      ColorPathState().ToString(inv))
            If String.IsNullOrEmpty(model) Then Return If(forReading, rawPart & "|" & ModelDenoiseFileService.AnyModel, "")
            Dim modelLength = 0L
            Try
                Dim modelFile As New FileInfo(AiModelService.ModelPath(model))
                If modelFile.Exists Then modelLength = modelFile.Length
            Catch
            End Try
            Return String.Join("|", rawPart, model, modelLength.ToString(inv))
        End Function

        ''' <summary>Liegt eine .fpdnz neben der RAW, die zu ihr passt? Liest nur den Kopf. Fuer die
        ''' Frage, ob ein Ausgabeweg auf das Modell warten muss: mit passender Datei nicht.</summary>
        Public Shared Function HasMatchingModelDenoiseFile(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) OrElse Not ModelDenoiseFileService.Exists(path) Then Return False
            Dim fileKey = DenoiseFileKeyFor(path, forReading:=True)
            Return fileKey.Length > 0 AndAlso ModelDenoiseFileService.Matches(path, fileKey)
        End Function

        ''' <summary>Das Ergebnis aus der .fpdnz neben der RAW in den Speicher holen, wenn sie zu
        ''' dieser Datei passt. Kein Modell, kein LibRaw: nur Lesen und Entpacken, bei 24 Megapixeln
        ''' gut eine Sekunde. True, wenn das Ergebnis danach bereitliegt.</summary>
        Public Shared Function LoadModelDenoiseFile(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            If HasModelDenoised(path) Then Return True
            If Not ModelDenoiseFileService.Exists(path) Then Return False
            Dim key = DenoiseKeyFor(path)
            Dim fileKey = DenoiseFileKeyFor(path, forReading:=True)
            If key.Length = 0 OrElse fileKey.Length = 0 Then Return False
            Dim width = 0, height = 0
            Dim data = PerformanceTraceService.Measure("RAW: Entrauschtes aus Datei",
                Function() ModelDenoiseFileService.TryRead(path, fileKey, width, height))
            If data Is Nothing Then
                DiagnosticLogService.LogAlways("Entrauschen", $"{IO.Path.GetFileName(path)}: .fpdnz passt nicht oder ist beschaedigt - Modell rechnet")
                Return False
            End If
            SyncLock _cacheLock
                _denoisedBytes = data
                _denoisedKey = key
                _denoisedWidth = width
                _denoisedHeight = height
                _blendBytes = Nothing
                _blendStrength = Double.NaN
                _blendKey = ""
            End SyncLock
            DiagnosticLogService.LogAlways("Entrauschen", $"{IO.Path.GetFileName(path)}: aus .fpdnz geladen ({width}x{height})")
            Return True
        End Function

        ''' <summary>Das Ergebnis dieser Datei als .fpdnz neben die RAW schreiben, wenn die
        ''' Einstellung an ist und es im Speicher liegt. Fuer Wege, die ohnehin im Hintergrund
        ''' laufen (Export, Stapel).</summary>
        Public Shared Function StoreModelDenoiseFile(path As String) As Boolean
            Dim pending = StoreModelDenoiseFileAsync(path)
            Return pending IsNot Nothing AndAlso pending.GetAwaiter().GetResult()
        End Function

        ''' <summary>Wie <see cref="StoreModelDenoiseFile"/>, das Schreiben aber im Hintergrund. Die
        ''' Daten werden SOFORT genommen: wechselt der Nutzer danach das Bild, verwirft der Speicher
        ''' sein Ergebnis, die Datei entsteht trotzdem. Nothing, wenn nichts zu schreiben ist
        ''' (Einstellung aus, kein Ergebnis, Datei schon aktuell).</summary>
        Public Shared Function StoreModelDenoiseFileAsync(path As String) As Task(Of Boolean)
            If Not ModelDenoiseFileService.Enabled Then Return Nothing
            ' Arbeitskopien (Immich, entpackte Buendel, Stapelkopien) liegen im Temp-Ordner: die
            ' grosse Datei daneben bliebe dort liegen und hilft niemandem.
            If PathIdentity.IsAncestorOrSelf(PathIdentity.Normalize(IO.Path.GetTempPath()), path) Then Return Nothing
            If Not HasModelDenoised(path) Then Return Nothing
            Dim fileKey = DenoiseFileKeyFor(path)
            If fileKey.Length = 0 Then Return Nothing
            ' Schon geschrieben und unveraendert: nicht noch einmal 100 MB auf die Platte.
            If ModelDenoiseFileService.Matches(path, fileKey) Then Return Nothing
            Dim memoryKey = DenoiseKeyFor(path)
            Dim data As Byte()
            Dim width = 0, height = 0
            SyncLock _cacheLock
                ' Unter der Sperre noch einmal: dazwischen kann das Ergebnis einer anderen Datei gehoeren.
                If Not String.Equals(_denoisedKey, memoryKey, StringComparison.Ordinal) Then Return Nothing
                data = _denoisedBytes
                width = _denoisedWidth
                height = _denoisedHeight
            End SyncLock
            If data Is Nothing OrElse width <= 0 OrElse height <= 0 Then Return Nothing
            Return Task.Run(Function() ModelDenoiseFileService.TryWrite(path, fileKey, width, height, data))
        End Function

        ''' <summary>Liegen die entrauschten Daten dieser Datei bereit?</summary>
        Public Shared Function HasModelDenoised(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Dim key = DenoiseKeyFor(path)
            If key.Length = 0 Then Return False
            SyncLock _cacheLock
                Return _denoisedBytes IsNot Nothing AndAlso String.Equals(_denoisedKey, key, StringComparison.Ordinal)
            End SyncLock
        End Function

        ''' <summary>Das Modell ueber die linearen Daten dieser Datei laufen lassen und das Ergebnis
        ''' ablegen. True, wenn es danach bereitliegt (auch, wenn es schon da war).
        '''
        ''' Braucht die verrauschten Daten im 16-Bit-Zwischenspeicher und waermt ihn dafuer
        ''' notfalls vor (ein LibRaw-Lauf durch die Schleuse). Das Modell selbst laeuft AUSSERHALB
        ''' der Schleuse: es dauert Sekunden bis Minuten, und so lange soll keine Kachel und kein
        ''' Betrachter warten. Der Speicher wird dabei nicht gesperrt; die Daten werden unter der
        ''' Sperre nur genommen, ihr Inhalt aendert sich nach dem Fuellen nie.</summary>
        Public Shared Function PrepareModelDenoise(path As String, cancel As CancellationToken) As Boolean
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return False
            If HasModelDenoised(path) Then Return True
            ' Liegt das Ergebnis schon als Datei neben der RAW, rechnet das Modell nicht. VOR der
            ' Frage nach dem Modell: die Datei taugt auch, wenn es inzwischen entfernt wurde.
            If LoadModelDenoiseFile(path) Then Return True
            If Not DenoiseModelService.Available OrElse Not DenoiseModelService.SupportsFile(path) Then Return False
            Dim key = DenoiseKeyFor(path)
            If key.Length = 0 Then Return False
            If Not HasRawCacheFor(path) AndAlso Not WarmRawCache(path) Then
                DiagnosticLogService.LogAlways("Entrauschen", $"{IO.Path.GetFileName(path)}: keine linearen Daten - Lauf faellt aus")
                Return False
            End If
            If cancel.IsCancellationRequested Then Return False

            Dim bytes As Byte() = Nothing
            Dim width = 0, height = 0
            Dim normalization = 1.0
            Dim toneMatrix As Single() = Nothing
            SyncLock _cacheLock
                If _cachedRawBytes Is Nothing OrElse _cachedRawHalf OrElse
                   Not String.Equals(_cachedRawPath, path, StringComparison.Ordinal) Then Return False
                bytes = _cachedRawBytes
                width = _cachedRawWidth
                height = _cachedRawHeight
                normalization = _cachedRawNormalization
                toneMatrix = _cachedRawToneMatrix
            End SyncLock

            Dim iso = 0.0
            Dim facts = ReadFileMetadata(path)
            If facts IsNot Nothing AndAlso facts.Iso > 0 Then iso = facts.Iso
            Dim denoised = PerformanceTraceService.Measure("RAW: mit Modell entrauschen",
                Function() DenoiseModelService.DenoiseLinear(bytes, width, height, normalization, toneMatrix, iso, cancel))
            If denoised Is Nothing Then Return False
            SyncLock _cacheLock
                _denoisedBytes = denoised
                _denoisedKey = key
                _denoisedWidth = width
                _denoisedHeight = height
                _blendBytes = Nothing
                _blendStrength = Double.NaN
                _blendKey = ""
            End SyncLock
            Return True
        End Function

        ''' <summary>Die Daten, aus denen ein Decode mit Entrauschen umsetzt, oder Nothing, wenn es
        ''' fuer diese Datei kein Ergebnis gibt: dann bleibt es bei den verrauschten.
        ''' <paramref name="noisy"/> sind die verrauschten Daten derselben Datei (aus LibRaw oder
        ''' dem Zwischenspeicher), <paramref name="strength"/> 0 bis 100.</summary>
        Private Shared Function ModelDenoisedSource(path As String, noisy As IntPtr, width As Integer, height As Integer,
                                                    strength As Double, toneMatrix As Single()) As Byte()
            If strength < 0 Then Return Nothing
            Dim key = DenoiseKeyFor(path)
            If key.Length = 0 Then Return Nothing
            Dim denoised As Byte()
            SyncLock _cacheLock
                If _denoisedBytes Is Nothing OrElse Not String.Equals(_denoisedKey, key, StringComparison.Ordinal) Then Return Nothing
                denoised = _denoisedBytes
                ' Erst die Groesse: die Daten koennen aus einer Datei stammen, und bei voller Staerke
                ' liest der Aufbau sie ohne weitere Pruefung.
                If CLng(denoised.Length) <> CLng(width) * height * 6 Then Return Nothing
                If strength >= 100.0 Then Return denoised
                If _blendBytes IsNot Nothing AndAlso _blendStrength = strength AndAlso
                   String.Equals(_blendKey, key, StringComparison.Ordinal) Then Return _blendBytes
            End SyncLock
            Dim blend = BlendDenoised(noisy, denoised, width, height, CSng(strength / 100.0), toneMatrix)
            SyncLock _cacheLock
                If String.Equals(_denoisedKey, key, StringComparison.Ordinal) Then
                    _blendBytes = blend
                    _blendStrength = strength
                    _blendKey = key
                End If
            End SyncLock
            Return blend
        End Function

        ''' <summary>Verrauscht und entrauscht nach der Staerke mischen, nur in der Helligkeit (siehe
        ''' oben). Die Helligkeit wird in linearem sRGB bewertet; liegen die Daten im Kameraraum,
        ''' kommen die Gewichte durch die Matrix der Tonstufe.</summary>
        Private Shared Function BlendDenoised(noisy As IntPtr, denoised As Byte(), width As Integer, height As Integer,
                                              amount As Single, toneMatrix As Single()) As Byte()
            Dim m = If(toneMatrix, New Single() {1, 0, 0, 0, 1, 0, 0, 0, 1})
            Dim wr = 0.2126F * m(0) + 0.7152F * m(3) + 0.0722F * m(6)
            Dim wg = 0.2126F * m(1) + 0.7152F * m(4) + 0.0722F * m(7)
            Dim wb = 0.2126F * m(2) + 0.7152F * m(5) + 0.0722F * m(8)
            ' Die Verschiebung in Helligkeit wird auf alle drei Kanaele gleich verteilt; damit das in
            ' der Helligkeit genau den Betrag ergibt, durch die Summe der Gewichte teilen.
            Dim weightSum = wr + wg + wb
            If Math.Abs(weightSum) < 0.0001F Then weightSum = 1.0F
            Dim keep = 1.0F - amount
            Dim result(denoised.Length - 1) As Byte
            Dim rowBytes = width * 6
            Threading.Tasks.Parallel.For(0, height,
                Sub(y)
                    Dim row(rowBytes - 1) As Byte
                    Marshal.Copy(IntPtr.Add(noisy, y * rowBytes), row, 0, rowBytes)
                    Dim baseOffset = y * rowBytes
                    For x = 0 To width - 1
                        Dim p = x * 6
                        Dim nr = CSng(CInt(row(p)) Or (CInt(row(p + 1)) << 8))
                        Dim ng = CSng(CInt(row(p + 2)) Or (CInt(row(p + 3)) << 8))
                        Dim nb = CSng(CInt(row(p + 4)) Or (CInt(row(p + 5)) << 8))
                        Dim q = baseOffset + p
                        Dim dr = CSng(CInt(denoised(q)) Or (CInt(denoised(q + 1)) << 8))
                        Dim dg = CSng(CInt(denoised(q + 2)) Or (CInt(denoised(q + 3)) << 8))
                        Dim db = CSng(CInt(denoised(q + 4)) Or (CInt(denoised(q + 5)) << 8))
                        Dim shift = (wr * (nr - dr) + wg * (ng - dg) + wb * (nb - db)) * keep / weightSum
                        WriteBlendSample(result, q, dr + shift)
                        WriteBlendSample(result, q + 2, dg + shift)
                        WriteBlendSample(result, q + 4, db + shift)
                    Next
                End Sub)
            Return result
        End Function

        Private Shared Sub WriteBlendSample(target As Byte(), offset As Integer, value As Single)
            Dim v = If(value <= 0.0F, 0, If(value >= 65535.0F, 65535, CInt(Math.Round(value))))
            target(offset) = CByte(v And &HFF)
            target(offset + 1) = CByte(v >> 8)
        End Sub

        ''' <summary>Das Ergebnis des Modells verwerfen (Bildwechsel, Speicher frei).</summary>
        Private Shared Sub DropModelDenoised()
            _denoisedBytes = Nothing
            _denoisedKey = ""
            _denoisedWidth = 0
            _denoisedHeight = 0
            _blendBytes = Nothing
            _blendStrength = Double.NaN
            _blendKey = ""
        End Sub

    End Class

End Namespace
