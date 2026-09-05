Imports System
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Echte RAW-Entwicklung über libraw (Demosaicing der Sensordaten), statt nur die eingebettete
    ''' JPEG-Vorschau zu lesen. Bewusst dynamisch geladen statt über NuGet gebunden - die Bindings
    ''' liefern keine Binaries für macOS/ARM64 und die Linux-Binary ist an Ubuntus glibc gebunden.
    '''
    ''' Reihenfolge: ERST die Bibliothek des Systems (deb/rpm/AUR verlangen sie, der Flatpak baut
    ''' sie mit), DANN die mitgelieferte neben der Anwendung. Die des Systems bekommt Sicherheits-
    ''' aktualisierungen und die Kameraunterstützung neuerer Modelle; die mitgelieferte ist der
    ''' Rückfall für Windows und die portablen Pakete.
    '''
    ''' Fehlt beides, meldet IsAvailable False und ALLES läuft wie bisher über RawPreviewService
    ''' (eingebettete Vorschau) - kein Absturz, keine Verhaltensänderung.
    '''
    ''' Geladen wird über NativeLibrary.Load + Delegates statt DllImport: der DllImport-Resolver
    ''' der Assembly ist bereits durch MpvInterop belegt (nur EINER erlaubt), und so bleibt die
    ''' Verfügbarkeit sauber prüfbar.
    '''
    ''' ENTWICKELT WIRD IN 16 BIT LINEAR (DecodeOutputBits), mit Kamera-Weißabgleich (cam_mul als
    ''' user_mul - libraw hat keinen C-API-Setter für use_camera_wb), sRGB-Primärfarben, ohne
    ''' LibRaws automatische Aufhellung und mit neutraler Gammakurve. Die Tonabbildung macht
    ''' Convert16 selbst: Belichtungsrampe, ACR3-Tonkurve und die Quantisierung auf 8 Bit mit
    ''' geordnetem Dither. LibRaws Auto-Aufhellung ist ein Histogramm-Stretch und fällt gemessen
    ''' motivabhängig in BEIDE Richtungen falsch aus, deshalb ersetzt eine feste Wiedergabe sie.
    ''' Objektiv-Messwerte (Farbquerfehler, Vignettierung) rechnet Convert16 mit, solange die
    ''' Werte noch linear sind; die Verzeichnung ist eine eigene Stufe dahinter.
    '''
    ''' Auf 8 Bit fällt der Decode nur in zwei Fällen zurück: eine exotische libraw ohne die
    ''' Schalter für Gamma und Auto-Aufhellung (dann kämen gamma-kodierte Daten, zu denen die
    ''' Tabellen nicht passen), und eine DNG-Hülle um ein bereits fertig gerendertes RGB-Bild
    ''' (siehe IsFinishedRgb - dort läge sonst eine zweite Tonkurve über der ersten).
    '''
    ''' MRU-1-Cache: der Editor ruft DecodeOriented beim Öffnen mehrfach (Arbeitsbild,
    ''' Vergleichsquelle, Export), das Demosaic eines 45-MP-RAW kostet aber Sekunden. Der letzte
    ''' dekodierte Stand (Pfad + Änderungszeit) bleibt deshalb im Speicher (~180 MB) und wird als
    ''' Kopie herausgegeben; beim nächsten anderen Pfad wird er ersetzt.
    ''' </summary>
    Public NotInheritable Class RawDecodeService

        Private Sub New()
        End Sub

        ' ── Native Bindung ───────────────────────────────────────────────────────

        Private Delegate Function InitFn(flags As UInteger) As IntPtr
        Private Delegate Function OpenFileFn(handle As IntPtr, path As IntPtr) As Integer
        Private Delegate Function IntFn(handle As IntPtr) As Integer
        Private Delegate Sub SetIntFn(handle As IntPtr, value As Integer)
        Private Delegate Function GetCamMulFn(handle As IntPtr, index As Integer) As Single
        Private Delegate Sub SetUserMulFn(handle As IntPtr, index As Integer, value As Single)
        Private Delegate Sub SetGammaFn(handle As IntPtr, index As Integer, value As Single)   ' libraw_set_gamma nimmt FLOAT, nicht double
        Private Delegate Function MakeMemImageFn(handle As IntPtr, ByRef errc As Integer) As IntPtr
        Private Delegate Sub PtrFn(handle As IntPtr)

        Private Shared ReadOnly _initLock As New Object()

        ''' <summary>Serialisiert die nativen Aufrufe, wenn nur die NICHT-reentrante libraw da ist.
        ''' Mit der _r-Variante bleibt der Sperrbereich ungenutzt und die Threads laufen parallel.</summary>
        Private Shared ReadOnly _nativeLock As New Object()
        Private Shared _reentrant As Boolean
        Private Shared _loadedLibrary As String

        ''' <summary>Welche libraw-Variante geladen wurde - für die Diagnose und für Feldberichte,
        ''' damit ein gemeldeter Absturz der Variante zugeordnet werden kann.</summary>
        Friend Shared ReadOnly Property LoadedLibraryName As String
            Get
                EnsureLoaded()
                Return If(_loadedLibrary, "")
            End Get
        End Property

        Friend Shared ReadOnly Property IsReentrant As Boolean
            Get
                EnsureLoaded()
                Return _reentrant
            End Get
        End Property
        Private Shared _initialized As Boolean
        Private Shared _library As IntPtr

        Private Shared _init As InitFn
        Private Shared _openFile As OpenFileFn
        Private Shared _unpack As IntFn
        Private Shared _setOutputBps As SetIntFn
        Private Shared _setOutputColor As SetIntFn
        Private Shared _getCamMul As GetCamMulFn
        Private Shared _setUserMul As SetUserMulFn
        Private Shared _setNoAutoBright As SetIntFn
        Private Shared _setGamma As SetGammaFn
        Private Shared _setFbdd As SetIntFn
        Private Shared _setDemosaic As SetIntFn
        Private Shared _process As IntFn
        Private Shared _unpackThumb As IntFn
        Private Shared _makeMemThumb As MakeMemImageFn
        Private Shared _makeMemImage As MakeMemImageFn
        Private Shared _clearMem As PtrFn
        Private Shared _close As PtrFn

        ''' <summary>True, wenn das System-libraw geladen werden konnte (Ergebnis wird gecacht).</summary>
        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                EnsureLoaded()
                Return _library <> IntPtr.Zero
            End Get
        End Property

        ''' <summary>Die Pfade, unter denen eine mitgelieferte LibRaw liegen kann: direkt neben der
        ''' Anwendung oder unter runtimes/&lt;rid&gt;/native, wohin packaging/package.sh sie kopiert.</summary>
        ''' <summary>Dieselben Namen in den beiden Homebrew-Verzeichnissen, mit vollem Pfad.
        ''' Ausserhalb von macOS leer - dort gibt es Homebrew nicht, und der Suchpfad des Systems
        ''' findet die Bibliothek ohnehin.</summary>
        Private Shared Iterator Function HomebrewCandidates(namen As String()) As IEnumerable(Of String)
            If Not OperatingSystem.IsMacOS() Then Return
            For Each prefix In {"/opt/homebrew/lib", "/usr/local/lib"}
                For Each name In namen
                    Yield Path.Combine(prefix, name)
                Next
            Next
        End Function

        Private Shared Iterator Function BundledCandidates(namen As String()) As IEnumerable(Of String)
            Dim baseDir = AppContext.BaseDirectory

            Dim archSuffix = If(RuntimeInformation.ProcessArchitecture = Architecture.Arm64, "arm64", "x64")
            Dim rid As String = ""
            If OperatingSystem.IsWindows() Then
                rid = $"win-{archSuffix}"
            ElseIf OperatingSystem.IsLinux() Then
                rid = $"linux-{archSuffix}"
            ElseIf OperatingSystem.IsMacOS() Then
                rid = $"osx-{archSuffix}"
            End If

            For Each name In namen
                Yield Path.Combine(baseDir, name)
                If rid.Length > 0 Then Yield Path.Combine(baseDir, "runtimes", rid, "native", name)
            Next
        End Function

        Private Shared Sub EnsureLoaded()
            SyncLock _initLock
                If _initialized Then Return
                _initialized = True

                Dim candidates As String()
                If OperatingSystem.IsWindows() Then
                    candidates = {"libraw.dll", "raw.dll", "libraw-23.dll"}
                ElseIf OperatingSystem.IsMacOS() Then
                    ' Sonamen wie unter Linux (25 = LibRaw 0.22, 23 = 0.21); der nackte Name
                    ' existiert nur mit Entwicklungspaket. Die 25 fehlte hier, waehrend die
                    ' Linux-Liste sie laengst fuehrt.
                    candidates = {"libraw.dylib", "libraw.25.dylib", "libraw.24.dylib",
                                  "libraw.23.dylib", "libraw.22.dylib"}
                Else
                    ' Sonamen der verbreiteten Versionen (25 = LibRaw 0.22, 23 = 0.21); das nackte
                    ' libraw.so existiert nur mit Dev-Paket.
                    ' Die _r-Varianten stehen ZUERST: das ist die reentrant gebaute Bibliothek, und
                    ' die Thumbnail-Erzeugung ruft hier aus ProcessorCount\2 Threads gleichzeitig
                    ' hinein (GalleryViewModel, Parallel.For). Vorher stand libraw.so vorn und wurde
                    ' auf einem Standard-Arch-System auch tatsächlich geladen - parallele native
                    ' Aufrufe in die nicht-reentrante Variante sind eine Absturzquelle.
                    candidates = {"libraw_r.so.25", "libraw_r.so.24", "libraw_r.so.23", "libraw_r.so.22", "libraw_r.so",
                                  "libraw.so.25", "libraw.so.24", "libraw.so.23", "libraw.so.22", "libraw.so"}
                End If

                Dim handle As IntPtr
                Dim geladen As String = Nothing

                ' ERST das System: eine vom Paketverwalter gepflegte LibRaw bekommt Sicherheits-
                ' aktualisierungen und vor allem die Kameraunterstützung neuerer Modelle. Die
                ' nackten Namen gehen über den Suchpfad des Betriebssystems.
                For Each candidate In candidates
                    If NativeLibrary.TryLoad(candidate, handle) Then
                        geladen = candidate
                        Exit For
                    End If
                    handle = IntPtr.Zero
                Next

                ' DANN, unter macOS, dieselben Namen mit vollem Pfad in den Homebrew-Verzeichnissen.
                ' Der Grund steht bei libmpv genauso: eine aus dem Finder gestartete .app erbt weder
                ' die Shell-Umgebung noch einen Homebrew-Pfad, und dyld sucht einen Namen ohne
                ' Schrägstrich nur in $HOME/lib, /usr/local/lib und /usr/lib. Auf Apple Silicon liegt
                ' Homebrew unter /opt/homebrew/lib - dort fand FerrumPix eine vorhandene LibRaw also
                ' nie und blieb wortlos bei der eingebetteten Vorschau. Diese Pfade gehoeren noch zur
                ' SYSTEMbibliothek und stehen deshalb vor der mitgelieferten.
                If handle = IntPtr.Zero Then
                    For Each filePath In HomebrewCandidates(candidates)
                        If NativeLibrary.TryLoad(filePath, handle) Then
                            geladen = filePath
                            Exit For
                        End If
                        handle = IntPtr.Zero
                    Next
                End If

                ' DANN die mitgelieferte: Windows und die portablen Pakete haben keine System-
                ' bibliothek. Sie liegt neben der Anwendung bzw. unter runtimes/<rid>/native -
                ' ein nackter Name findet sie dort NICHT, es braucht den vollen Pfad.
                If handle = IntPtr.Zero Then
                    For Each filePath In BundledCandidates(candidates)
                        If NativeLibrary.TryLoad(filePath, handle) Then
                            geladen = Path.GetFileName(filePath)
                            Exit For
                        End If
                        handle = IntPtr.Zero
                    Next
                End If

                If handle = IntPtr.Zero Then Return

                ' Ist es die reentrante Variante? Wenn nicht, werden die nativen Aufrufe serialisiert -
                ' lieber langsamere Thumbnails als sporadische Abstürze.
                _loadedLibrary = geladen
                _reentrant = geladen IsNot Nothing AndAlso
                             (geladen.Contains("_r.") OrElse OperatingSystem.IsWindows() OrElse OperatingSystem.IsMacOS())

                Try
                    _init = GetExport(Of InitFn)(handle, "libraw_init")
                    _openFile = GetExport(Of OpenFileFn)(handle, "libraw_open_file")
                    _unpack = GetExport(Of IntFn)(handle, "libraw_unpack")
                    _setOutputBps = GetExport(Of SetIntFn)(handle, "libraw_set_output_bps")
                    _setOutputColor = GetExport(Of SetIntFn)(handle, "libraw_set_output_color")
                    _getCamMul = GetExport(Of GetCamMulFn)(handle, "libraw_get_cam_mul")
                    _setUserMul = GetExport(Of SetUserMulFn)(handle, "libraw_set_user_mul")
                    _setNoAutoBright = GetExport(Of SetIntFn)(handle, "libraw_set_no_auto_bright")
                    _setGamma = GetExport(Of SetGammaFn)(handle, "libraw_set_gamma")
                    _process = GetExport(Of IntFn)(handle, "libraw_dcraw_process")
                    _unpackThumb = GetExport(Of IntFn)(handle, "libraw_unpack_thumb")
                    _makeMemThumb = GetExport(Of MakeMemImageFn)(handle, "libraw_dcraw_make_mem_thumb")
                    _makeMemImage = GetExport(Of MakeMemImageFn)(handle, "libraw_dcraw_make_mem_image")
                    _clearMem = GetExport(Of PtrFn)(handle, "libraw_dcraw_clear_mem")
                    _close = GetExport(Of PtrFn)(handle, "libraw_close")
                    ' OPTIONAL, deshalb eigenes Try: eine aeltere libraw ohne diesen Export ist
                    ' voll brauchbar, sie rauscht nur mehr. Im Hauptblock wuerde sie hier
                    ' komplett durchfallen und die RAW-Entwicklung ganz abschalten.
                    Try
                        _setFbdd = GetExport(Of SetIntFn)(handle, "libraw_set_fbdd_noiserd")
                    Catch
                        _setFbdd = Nothing
                    End Try
                    Try
                        _setDemosaic = GetExport(Of SetIntFn)(handle, "libraw_set_demosaic")
                    Catch
                        _setDemosaic = Nothing
                    End Try
                    _library = handle
                Catch
                    ' Ein fehlender Export = Bibliothek unbrauchbar; alles auf Anfang.
                    _init = Nothing : _openFile = Nothing : _unpack = Nothing
                    _setOutputBps = Nothing : _setOutputColor = Nothing
                    _getCamMul = Nothing : _setUserMul = Nothing
                    _setNoAutoBright = Nothing : _setGamma = Nothing : _setFbdd = Nothing : _setDemosaic = Nothing
                    _process = Nothing : _makeMemImage = Nothing : _clearMem = Nothing : _close = Nothing
                    _unpackThumb = Nothing : _makeMemThumb = Nothing
                    NativeLibrary.Free(handle)
                    _library = IntPtr.Zero
                End Try
            End SyncLock
        End Sub

        Private Shared Function GetExport(Of T)(handle As IntPtr, name As String) As T
            Return Marshal.GetDelegateForFunctionPointer(Of T)(NativeLibrary.GetExport(handle, name))
        End Function

        ' ── Eingebettetes Vorschaubild (Thumbnails) ──────────────────────────────

        ''' <summary>Liest das in der RAW-Datei eingebettete Vorschaubild ueber LibRaws eigene
        ''' Thumbnail-API. KEIN Demosaic - das ist ein reiner Extraktionsschritt und damit fast so
        ''' schnell wie unser eigener JPEG-Scanner, aber formatkundig statt geraten: LibRaw weiss,
        ''' WO die Vorschau liegt, waehrend der Scanner die Datei nach JPEG-Signaturen durchsucht.
        ''' Deshalb steht dieser Weg in der Thumbnail-Erzeugung an erster Stelle
        ''' (ThumbnailCacheService), der Scanner nur noch als Rueckfall.
        ''' Liefert einen dekodierbaren Strom (JPEG oder PNG) oder Nothing.</summary>
        Public Shared Function TryExtractThumbnail(path As String) As MemoryStream
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            If Not _reentrant Then
                SyncLock _nativeLock
                    Return ExtractThumbnailCore(path)
                End SyncLock
            End If
            Return ExtractThumbnailCore(path)
        End Function

        Private Shared Function ExtractThumbnailCore(path As String) As MemoryStream
            Dim handle = _init(0UI)
            If handle = IntPtr.Zero Then Return Nothing
            Dim pathPtr As IntPtr = IntPtr.Zero
            Dim thumb As IntPtr = IntPtr.Zero
            Try
                pathPtr = StringToUtf8(path)
                If _openFile(handle, pathPtr) <> 0 Then Return Nothing
                If _unpackThumb(handle) <> 0 Then Return Nothing
                Dim errc = 0
                thumb = _makeMemThumb(handle, errc)
                If thumb = IntPtr.Zero OrElse errc <> 0 Then Return Nothing

                ' libraw_processed_image_t: type(4) height(2) width(2) colors(2) bits(2) data_size(4) data.
                Dim imageType = Marshal.ReadInt32(thumb, 0)
                Dim height = CInt(CUShort(Marshal.ReadInt16(thumb, 4)))
                Dim width = CInt(CUShort(Marshal.ReadInt16(thumb, 6)))
                Dim colors = CInt(CUShort(Marshal.ReadInt16(thumb, 8)))
                Dim bits = CInt(CUShort(Marshal.ReadInt16(thumb, 10)))
                Dim dataSize = Marshal.ReadInt32(thumb, 12)
                If dataSize <= 0 Then Return Nothing

                Dim payload(dataSize - 1) As Byte
                Marshal.Copy(thumb + 16, payload, 0, dataSize)

                Select Case imageType
                    Case 1 ' LIBRAW_IMAGE_JPEG: die Nutzlast IST eine JPEG-Datei
                        Return New MemoryStream(payload)
                    Case 2 ' LIBRAW_IMAGE_BITMAP: rohe RGB-Pixel -> als PNG herausgeben
                        If colors <> 3 OrElse bits <> 8 Then Return Nothing
                        Dim pixelCount = CheckedPixelCount(width, height)
                        If pixelCount <= 0 OrElse dataSize < pixelCount * 3L Then Return Nothing
                        Return EncodeRgbToPng(payload, width, height)
                    Case Else
                        ' JPEG-XL/H265-Vorschauen (neuere Canon) kann SkiaSharp nicht dekodieren -
                        ' dafuer greift der Rueckfall auf den eigenen Scanner.
                        Return Nothing
                End Select
            Catch
                Return Nothing
            Finally
                If thumb <> IntPtr.Zero Then _clearMem(thumb)
                _close(handle)
                If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
            End Try
        End Function

        Private Shared Function EncodeRgbToPng(rgb As Byte(), width As Integer, height As Integer) As MemoryStream
            Dim pixelCount = CheckedPixelCount(width, height)
            If pixelCount <= 0 OrElse pixelCount > Integer.MaxValue \ 4 Then Return Nothing
            Using bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque))
                Dim pixels(CInt(pixelCount * 4L - 1)) As Byte
                For i = 0 To CInt(pixelCount - 1)
                    pixels(i * 4) = rgb(i * 3 + 2)      ' B
                    pixels(i * 4 + 1) = rgb(i * 3 + 1)  ' G
                    pixels(i * 4 + 2) = rgb(i * 3)      ' R
                    pixels(i * 4 + 3) = 255
                Next
                Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length)
                Using image = SKImage.FromBitmap(bitmap)
                    Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                        If data Is Nothing Then Return Nothing
                        Dim ms As New MemoryStream()
                        data.SaveTo(ms)
                        ms.Position = 0
                        Return ms
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Letzter Ausweg fuer Dateien OHNE eingebettete Vorschau (z.B. Leica Digilux 2
        ''' .RAW): entwickelt das Bild wirklich und gibt es als PNG heraus. Teuer - nur aufrufen,
        ''' wenn Scanner UND Thumbnail-API nichts geliefert haben. Nutzt den MRU-Cache mit, ein
        ''' direkt folgendes Oeffnen im Editor ist dadurch umsonst.</summary>
        Public Shared Function TryRenderPreviewPng(path As String) As MemoryStream
            Using developed = TryDecode(path)
                If developed Is Nothing Then Return Nothing
                Using image = SKImage.FromBitmap(developed)
                    Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                        If data Is Nothing Then Return Nothing
                        Dim ms As New MemoryStream()
                        data.SaveTo(ms)
                        ms.Position = 0
                        Return ms
                    End Using
                End Using
            End Using
        End Function

        ' ── Dekodieren mit MRU-1-Cache ───────────────────────────────────────────

        Private Shared ReadOnly _cacheLock As New Object()
        Private Shared _cachedPath As String = ""
        Private Shared _cachedWriteTimeUtc As DateTime
        ''' <summary>Die Grundbelichtung, mit der der zwischengespeicherte Decode gerechnet wurde.
        ''' Sie gehoert in den Schluessel: schaltet der Nutzer die Kamera-Referenzwerte um, ist der
        ''' alte Decode veraltet, obwohl Pfad und Aenderungszeit gleich bleiben.</summary>
        Private Shared _cachedGrundbelichtung As Double = Double.NaN
        Private Shared _cachedObjektiv As String = ""
        ''' <summary>Das Demosaic-Verfahren des zwischengespeicherten Decodes. Es gehoert aus
        ''' demselben Grund in den Schluessel wie die Grundbelichtung: die Wahl aendert die Pixel,
        ''' waehrend Pfad und Aenderungszeit gleich bleiben.</summary>
        Private Shared _cachedDemosaic As Integer = -1
        Private Shared _cachedBitmap As SKBitmap

        ''' <summary>Voll aufgelöster, fertig entwickelter Decode (Besitz beim Aufrufer) oder Nothing.
        ''' Orientierung ist bereits angewandt (libraw dreht nach dem Kamera-Flip).</summary>
        ''' <param name="lensChoice">Nothing = die Einstellung entscheidet fuer alle drei
        ''' Korrekturen; sonst uebersteuert sie jede einzeln fuer dieses eine Bild (die Schalter im
        ''' Werkzeug).</param>
        ''' <summary>Entwickelt ein RAW. Laeuft immer nur EINMAL gleichzeitig - siehe
        ''' <see cref="DecodeGate"/>. Der Zwischenspeicher wird INNERHALB der Schleuse gefragt: wer
        ''' hinter einem laufenden Decode wartet, bekommt danach dessen Ergebnis, statt denselben
        ''' Lauf ein zweites Mal anzuwerfen.</summary>
        Public Shared Function TryDecode(path As String,
                                         Optional lensChoice As LensDataService.Wahl = Nothing) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            Return DecodeGate.Run(Function() DecodeIntern(path, lensChoice))
        End Function

        ''' <summary>Reduzierter RAW-Decode ausschliesslich fuer kleine Vorschauen. LibRaw kann nur
        ''' halbe Kantenlaenge liefern; das spart Demosaic und FBDD, bevor die Kachel auf ihre
        ''' Zielgroesse skaliert wird. Dieser Weg beruehrt bewusst weder den Editor-MRU-Cache noch
        ''' dessen Schluessel: ein 1/2-Decode darf niemals als Arbeitsbild wieder herauskommen.</summary>
        Friend Shared Function TryDecodeThumbnail(path As String,
                                                   Optional lensChoice As LensDataService.Wahl = Nothing) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            Return DecodeGate.Run(Function() DecodeThumbnailIntern(path, lensChoice))
        End Function

        Private Shared Function DecodeThumbnailIntern(path As String,
                                                       lensChoice As LensDataService.Wahl) As SKBitmap
            Try
                Dim baseEv = BaseExposureForFile(path)
                Dim lens = LensCorrectionForFile(path, lensChoice)
                Dim decoded As SKBitmap
                If _reentrant Then
                    decoded = DecodeCore(path, baseEv, lens, useHalfSize:=True)
                Else
                    SyncLock _nativeLock
                        decoded = DecodeCore(path, baseEv, lens, useHalfSize:=True)
                    End SyncLock
                End If
                Return WithDistortion(decoded, lens)
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function DecodeIntern(path As String,
                                             lensChoice As LensDataService.Wahl) As SKBitmap
            Try
                Dim writeTime = File.GetLastWriteTimeUtc(path)
                Dim baseEv = BaseExposureForFile(path)
                Dim lens = LensCorrectionForFile(path, lensChoice)
                ' Der Schluessel MUSS jede Groesse tragen, die das Ergebnis veraendert. Fehlt eine,
                ' liefert der Cache das Bild der vorigen Einstellung zurueck, und der Schalter
                ' sieht aus, als tue er nichts.
                ' Die VERZEICHNUNG steht bewusst NICHT im Schluessel: sie ist eine eigene Stufe
                ' hinter dem Decode, und im Zwischenspeicher liegt das Bild VOR ihr. Sonst warf ihr
                ' Ein- und Ausschalten den ganzen Decode weg und liess libraw erneut laufen -
                ' Sekunden fuer eine Umrechnung, die selbst Millisekunden braucht.
                Dim objKey = LensKey(lens)
                Dim demosaic = ConfiguredDemosaic()
                SyncLock _cacheLock
                    If _cachedBitmap IsNot Nothing AndAlso
                       String.Equals(_cachedPath, path, StringComparison.Ordinal) AndAlso
                       _cachedWriteTimeUtc = writeTime AndAlso
                       _cachedGrundbelichtung = baseEv AndAlso
                       _cachedDemosaic = demosaic AndAlso
                       String.Equals(_cachedObjektiv, objKey, StringComparison.Ordinal) Then
                        Return WithDistortion(_cachedBitmap.Copy(), lens)
                    End If
                End SyncLock

                ' Ohne reentrante libraw laufen die nativen Aufrufe nacheinander - die
                ' Thumbnail-Erzeugung ruft aus mehreren Threads hier herein (Parallel.For).
                Dim decoded As SKBitmap
                If _reentrant Then
                    decoded = DecodeCore(path, baseEv, lens)
                Else
                    SyncLock _nativeLock
                        decoded = DecodeCore(path, baseEv, lens)
                    End SyncLock
                End If
                If decoded Is Nothing Then Return Nothing

                SyncLock _cacheLock
                    _cachedBitmap?.Dispose()
                    _cachedBitmap = decoded
                    _cachedPath = path
                    _cachedGrundbelichtung = baseEv
                    _cachedObjektiv = objKey
                    _cachedDemosaic = demosaic
                    _cachedWriteTimeUtc = writeTime
                    Return WithDistortion(_cachedBitmap.Copy(), lens)
                End SyncLock
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Die Verzeichnungsstufe auf ein frisch aus dem Zwischenspeicher geholtes Bild.
        ''' Besitz geht an den Aufrufer; das uebergebene Bild wird verbraucht.</summary>
        Private Shared Function WithDistortion(image As SKBitmap,
                                                lens As LensDataService.Korrektur) As SKBitmap
            If image Is Nothing Then Return Nothing
            If lens Is Nothing OrElse Not lens.HasDistortion Then Return image
            Dim entzerrt = RemoveDistortion(image, lens)
            If entzerrt Is Nothing Then Return image
            image.Dispose()
            Return entzerrt
        End Function

        ''' <summary>Alles, was am Ergebnis der Objektivkorrektur haengt UND schon im Decode steckt,
        ''' in einer Zeichenkette - fuer den Decode-Zwischenspeicher. Die Verzeichnung fehlt hier
        ''' absichtlich: sie laeuft erst danach, siehe MitVerzeichnung.</summary>
        Private Shared Function LensKey(k As LensDataService.Korrektur) As String
            If k Is Nothing Then Return ""
            Return String.Format(Globalization.CultureInfo.InvariantCulture,
                "{0}|{1}|{2:R}|{3:R}|{4:R}|{5:R}|{6:R}|{7:R}|{8:R}|{9:R}|{10:R}|{11:R}",
                k.HasChromaticAberration, k.HasVignetting,
                k.TcaBr, k.TcaCr, k.TcaVr, k.TcaBb, k.TcaCb, k.TcaVb,
                k.NormScale, k.Vk1 + k.Vk2 * 3 + k.Vk3 * 7,
                k.ChromaticAberrationStrength, k.VignettingStrength)
        End Function

        ''' <summary>Die Kennlinien fuer diese Datei, sofern die Korrektur gilt. Die Verzeichnung
        ''' bleibt hier aussen vor - sie ist eine eigene Stufe hinter dem Decode.</summary>
        Private Shared Function LensCorrectionForFile(path As String,
                                                           wahl As LensDataService.Wahl) As LensDataService.Korrektur
            Try
                Dim vorgabe = AppSettingsService.Load().LensCorrectionEnabled
                Dim modell = If(wahl IsNot Nothing, wahl.LensModel, "")
                Return LensDataService.Filtere(
                    LensDataService.FindCorrectionForFile(path, modell), wahl, vorgabe)
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Verzeichnung entfernen: das Bild wird so umgerechnet, dass gerade Linien wieder
        ''' gerade sind.
        '''
        ''' BEWUSST EINE EIGENE STUFE hinter dem Decode und nicht im Umsetzungsschritt: der
        ''' Farbquerfehler verschiebt Bruchteile eines Pixels und kommt mit dem dortigen Zeilenring
        ''' aus, die Verzeichnung dagegen um Dutzende Zeilen. Sie dort einzubauen hiesse, den
        ''' delikatesten Teil des Decodes umzubauen. Der Preis ist eine Interpolation im 8-Bit-Bild
        ''' statt im linearen Rohbild; bei einer reinen Geometrie-Umsetzung ist das der uebliche Weg.
        '''
        ''' Die Bildgroesse bleibt gleich. Das ist eine Entscheidung, keine Vereinfachung: die
        ''' Alternative waere, auf den gueltigen Bereich zu beschneiden und damit die Bildmasse zu
        ''' aendern - dann muessten Beschnitt, Masken, Objekte und Rezepte mitwandern. Genau das
        ''' soll nicht passieren, weil die Korrektur eine Anfangs-Entscheidung ist und die
        ''' Bearbeitung danach kommt. Am Rand entstehen dadurch schmale leere Streifen, die mit den
        ''' Randpixeln gefuellt werden.</summary>
        Public Shared Function RemoveDistortion(source As SKBitmap,
                                                    k As LensDataService.Korrektur) As SKBitmap
            If source Is Nothing OrElse k Is Nothing OrElse Not k.HasDistortion Then Return Nothing
            Dim width = source.Width, height = source.Height
            If width < 2 OrElse height < 2 Then Return Nothing

            Dim target = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, source.AlphaType))
            ' VB kann keinen Span als Parameter fuehren, deshalb eine Kopie der Quelle. Bei 20 MP
            ' sind das 80 MB fuer die Dauer der Umrechnung - vertretbar, weil die Stufe nur laeuft,
            ' wenn fuer dieses Objektiv wirklich Messwerte vorliegen.
            Dim stepQ = source.RowBytes
            Dim quellPixel(stepQ * height - 1) As Byte
            Marshal.Copy(source.GetPixels(), quellPixel, 0, quellPixel.Length)
            Dim targetPointer = target.GetPixels()
            Dim ausgabe(width * height * 4 - 1) As Byte

            Dim cx = (width - 1) / 2.0, cy = (height - 1) / 2.0
            ' Die Kennlinie rechnet im normierten System (r = 1 an der Mitte der langen Kante), die
            ' Bildpunkte in Pixeln - der Faktor bringt beide zusammen.
            Dim norm = k.NormScale
            If norm <= 0.0 Then Return Nothing

            ' ZEILENWEISE PARALLEL. Jede Zeile schreibt ausschliesslich in ihren eigenen Abschnitt
            ' von "ausgabe" und liest nur aus der unveraenderlichen Quellkopie - die Zeilen sind
            ' voneinander unabhaengig, es braucht keine Sperre. Der Grund: die Schleife rechnet je
            ' Pixel eine Wurzel und ein Polynom, und bei 45 MP dauerte sie auf einem Kern so lange,
            ' dass ein Objektivwechsel spuerbar stand.
            Parallel.For(0, height,
                Sub(y)
                    Dim dy = y - cy
                    Dim z = y * width * 4
                    For x = 0 To width - 1
                        Dim dx = x - cx
                        Dim rPix = Math.Sqrt(dx * dx + dy * dy)
                        Dim sx = cx, sy = cy
                        If rPix > 0.0 Then
                            ' Die Kennlinie bildet den KORRIGIERTEN Radius auf den VERZEICHNETEN ab.
                            ' Das sieht verkehrt herum aus, ist aber genau richtig: gerechnet wird vom
                            ' fertigen Zielbild rueckwaerts, um in der Quelle nachzuschlagen.
                            Dim rNorm = rPix * norm
                            Dim rVerz = LensDataService.DistortionRadius(k, rNorm)
                            Dim factor = rVerz / rNorm
                            sx = cx + dx * factor
                            sy = cy + dy * factor
                        End If
                        ZieheBilinear(quellPixel, width, height, stepQ, sx, sy, ausgabe, z)
                        z += 4
                    Next
                End Sub)
            Marshal.Copy(ausgabe, 0, targetPointer, ausgabe.Length)
            Return target
        End Function

        ''' <summary>Ein Bgra-Pixel bilinear aus der Quelle ziehen. Ausserhalb wird auf den Rand
        ''' geklemmt: die Korrektur zieht das Bild an den Ecken ueber den Rand hinaus, und ein
        ''' geklemmter Streifen ist unauffaelliger als ein schwarzer.</summary>
        Private Shared Sub ZieheBilinear(source As Byte(), width As Integer, height As Integer,
                                         schritt As Integer, sx As Double, sy As Double,
                                         target As Byte(), zielOffset As Integer)
            If sx < 0 Then sx = 0
            If sy < 0 Then sy = 0
            If sx > width - 1.001 Then sx = width - 1.001
            If sy > height - 1.001 Then sy = height - 1.001
            Dim x0 = CInt(Math.Floor(sx)), y0 = CInt(Math.Floor(sy))
            Dim fx = sx - x0, fy = sy - y0
            Dim x1 = Math.Min(x0 + 1, width - 1)
            Dim y1 = Math.Min(y0 + 1, height - 1)
            Dim o00 = y0 * schritt + x0 * 4, o01 = y0 * schritt + x1 * 4
            Dim o10 = y1 * schritt + x0 * 4, o11 = y1 * schritt + x1 * 4
            For k = 0 To 3
                Dim top = source(o00 + k) * (1.0 - fx) + source(o01 + k) * fx
                Dim bottom = source(o10 + k) * (1.0 - fx) + source(o11 + k) * fx
                target(zielOffset + k) = CByte(Math.Min(255.0, Math.Max(0.0, top * (1.0 - fy) + bottom * fy)))
            Next
        End Sub

        ''' <summary>Maße des fertigen Decodes - nur wenn der Cache sie schon kennt (kein Demosaic
        ''' nur für eine Größenabfrage; die billigen Pfade bleiben billig und fallen sonst auf die
        ''' eingebettete Vorschau zurück, deren Maße bei modernen Kameras übereinstimmen).</summary>
        Public Shared Function TryGetCachedSize(path As String) As (Width As Integer, Height As Integer)
            If String.IsNullOrWhiteSpace(path) Then Return (0, 0)
            Try
                SyncLock _cacheLock
                    If _cachedBitmap IsNot Nothing AndAlso
                       String.Equals(_cachedPath, path, StringComparison.Ordinal) AndAlso
                       _cachedWriteTimeUtc = File.GetLastWriteTimeUtc(path) Then
                        Return (_cachedBitmap.Width, _cachedBitmap.Height)
                    End If
                End SyncLock
            Catch
            End Try
            Return (0, 0)
        End Function

        ''' Bildwechsel im Editor: der ~180-MB-Eintrag muss nicht auf den nächsten RAW-Decode warten.
        Public Shared Sub ClearCache()
            SyncLock _cacheLock
                _cachedBitmap?.Dispose()
                _cachedBitmap = Nothing
                _cachedPath = ""
                _cachedGrundbelichtung = Double.NaN
            End SyncLock
        End Sub

        ''' <summary>Ausgabetiefe, die von LibRaw angefordert wird - der Schalter fuer den
        ''' 16-Bit-Zweig. DERZEIT 8: der Zweig ist STILLGELEGT, nicht entfernt.
        '''
        ''' Auf 16 gestellt dekodiert LibRaw mit voller Tiefe und Convert16 quantisiert mit
        ''' Bayer-Dither statt durch Abschneiden. Gemessen bringt das wenig: an der dunkelsten
        ''' 64x64-Kachel eines Konzert-RAW 481 statt 459 unterscheidbare Farbwerte (+5 %),
        ''' Chroma-Median unveraendert - Sensorrauschen dithert dort bereits selbst. Spuerbar
        ''' waere es nur auf rauscharmen dunklen Flaechen. Dagegen steht LibRaws doppelt so
        ''' grosser Zwischenpuffer (20 MP: 121 statt 60 MB, bei 45 MP 270 statt 135).
        '''
        ''' Der 16-Bit-Zweig in DecodeCore und Convert16 bleiben deshalb lauffaehig stehen; die
        ''' Diagnose ruft Convert16 direkt auf, damit er nicht unbemerkt verrottet. Umschalten
        ''' ist genau diese eine Zahl.</summary>
        Private Const DecodeOutputBits As Integer = 16

        ''' <summary>LibRaws Nummer fuer das voreingestellte Verfahren. NACHGEMESSEN, nicht
        ''' angenommen: ein Decode ohne gesetztes Verfahren liefert bitgleich dieselben Bilddaten
        ''' wie <c>libraw_set_demosaic(3)</c>, also AHD. Die 0 daneben ist die lineare
        ''' Interpolation und waere ein sichtbarer Rueckschritt.</summary>
        Private Const DefaultDemosaic As Integer = 3

        ''' <summary>Der Name aus den Einstellungen als LibRaw-Nummer. Die Zuordnung steht NUR hier;
        ''' gespeichert wird der Name, damit eine verschobene Nummer in einer anderen
        ''' LibRaw-Fassung nicht stillschweigend ein anderes Verfahren waehlt.</summary>
        Private Shared Function DemosaicNumber(name As String) As Integer
            Select Case AppSettingsService.NormalizeRawDemosaicAlgorithm(name)
                Case "VNG" : Return 1
                Case "PPG" : Return 2
                Case "DCB" : Return 4
                Case Else : Return DefaultDemosaic ' AHD
            End Select
        End Function

        ''' <summary>Das eingestellte Verfahren, oder die Vorgabe, wenn die Einstellungen nicht
        ''' lesbar sind. Der Decode darf daran nicht scheitern.</summary>
        Private Shared Function ConfiguredDemosaic() As Integer
            Try
                Return DemosaicNumber(AppSettingsService.Load().RawDemosaicAlgorithm)
            Catch
                Return DefaultDemosaic
            End Try
        End Function

        ' Offsets INNERHALB libraw_output_params_t. Die Basis dieses Feldes in libraw_data_t ist
        ' absichtlich nicht fest verdrahtet: sie liegt z.B. bei 5024 (LibRaw 0.21.4) bzw. 5232
        ' (0.22.2) und kann sich bei jeder Systembibliothek wieder verschieben.
        Private Const HalfSizeOffset As Integer = 136
        Private Const OutputColorOffset As Integer = 160
        Private Const OutputBpsOffset As Integer = 200
        Private Const DemosaicOffset As Integer = 216
        Private Const ParamsSearchStart As Integer = 4096
        Private Const ParamsSearchEnd As Integer = 8192

        ''' <summary>Setzt params.half_size nur, wenn die params-Basis im nativen Handle eindeutig
        ''' belegt ist. Die drei Werte werden ausschliesslich vor dcraw_process als Landmarken
        ''' gesetzt und danach sofort auf die normalen FerrumPix-Werte zurueckgestellt. Jede
        ''' unbekannte LibRaw-Struktur bleibt damit beim sicheren Voll-Decode.</summary>
        Private Shared Function TryEnableHalfSize(handle As IntPtr) As Boolean
            If handle = IntPtr.Zero OrElse _setDemosaic Is Nothing Then Return False

            Const markerOutputColor As Integer = 5
            Const markerOutputBps As Integer = 13
            Const markerDemosaic As Integer = 7
            Dim foundBase As Integer = -1
            Try
                _setOutputColor(handle, markerOutputColor)
                _setOutputBps(handle, markerOutputBps)
                _setDemosaic(handle, markerDemosaic)

                For candidate = ParamsSearchStart To ParamsSearchEnd Step 4
                    If Marshal.ReadInt32(handle, candidate + OutputColorOffset) <> markerOutputColor Then Continue For
                    If Marshal.ReadInt32(handle, candidate + OutputBpsOffset) <> markerOutputBps Then Continue For
                    If Marshal.ReadInt32(handle, candidate + DemosaicOffset) <> markerDemosaic Then Continue For

                    ' Ein frisch erzeugter LibRaw-Kontext hat half_size = 0. Das ist eine vierte,
                    ' passive Plausibilitaetspruefung gegen zufaellige Treffermuster im Handle.
                    If Marshal.ReadInt32(handle, candidate + HalfSizeOffset) <> 0 Then Continue For
                    If foundBase >= 0 Then Return False ' nicht eindeutig: nichts schreiben
                    foundBase = candidate
                Next

                If foundBase < 0 Then Return False
                Marshal.WriteInt32(handle, foundBase + HalfSizeOffset, 1)
                Return True
            Catch
                Return False
            Finally
                ' DecodeCore setzt Bittiefe/Farbraum anschliessend ohnehin verbindlich. user_qual
                ' muss hier dagegen selbst zurueckgestellt werden, und zwar auf das EINGESTELLTE
                ' Verfahren.
                ' Hier stand eine 0, mit dem Kommentar, das sei LibRaws Vorgabe. Nachgemessen ist
                ' sie es nicht: ohne gesetztes Verfahren entwickelt LibRaw mit AHD (3), waehrend 0
                ' die lineare Interpolation ist. Wo die Suche nach der Feldbasis scheiterte und
                ' danach ein VOLLER Decode lief, wurde er also mit dem schwaechsten Verfahren
                ' gerechnet.
                Try
                    _setOutputColor(handle, 1)
                    _setOutputBps(handle, DecodeOutputBits)
                    _setDemosaic(handle, ConfiguredDemosaic())
                Catch
                End Try
            End Try
        End Function

        Private Shared Function DecodeCore(path As String, baseEv As Double,
                                           lens As LensDataService.Korrektur,
                                           Optional useHalfSize As Boolean = False) As SKBitmap
            Dim handle = _init(0UI)
            If handle = IntPtr.Zero Then Return Nothing
            Dim pathPtr As IntPtr = IntPtr.Zero
            Dim image As IntPtr = IntPtr.Zero
            Try
                ' UTF-8-Pfad: korrekt auf Linux/macOS; unter Windows scheitern Nicht-ASCII-Pfade
                ' im ANSI-Marshalling der C-API - dort greift dann der Vorschau-Rückfall.
                pathPtr = StringToUtf8(path)
                If _openFile(handle, pathPtr) <> 0 Then Return Nothing
                If _unpack(handle) <> 0 Then Return Nothing

                ' Fuer eine Kachel reichen die 2x2-Bayer-Zellen von half_size. Einen C-API-Setter
                ' gibt es dafuer nicht. Der Zugriff ueber params ist von LibRaw vorgesehen, seine
                ' Basis in libraw_data_t aber versionsabhaengig. TryEnableHalfSize findet sie daher
                ' erst anhand dreier Setter-Landmarken und faellt bei jeder Unsicherheit auf den
                ' unveraenderten Voll-Decode zurueck.
                If useHalfSize Then TryEnableHalfSize(handle)

                _setOutputBps(handle, DecodeOutputBits)
                _setOutputColor(handle, 1) ' sRGB
                ' LINEAR und OHNE Auto-Aufhellung dekodieren - die Tonabbildung macht Convert16.
                ' LibRaws Auto-Aufhellung ist ein Histogramm-Stretch: gemessen faellt sie an einem
                ' dunklen Motiv zu HELL und an einem hellen zu DUNKEL aus (P50 0,140 gegen die
                ' 0,086 bzw. 0,304 gegen 0,396). Motivabhaengig in beide Richtungen - kein
                ' Korrekturfaktor kann das beheben, deshalb ersetzt sie eine feste Wiedergabe.
                ' Fehlen die Schalter (exotische libraw), bleibt es beim alten Verhalten: dann
                ' liefert LibRaw gamma-kodierte Daten und die Tabellen unten passen nicht - darum
                ' faellt der Decode in diesem Fall auf 8 Bit zurueck.
                ' AUSNAHME: eine DNG-Huelle um ein bereits fertig gerendertes RGB-Bild. Solche
                ' Dateien entstehen beim Umwandeln aus JPEG oder aus manchen Scannern; sie tragen
                ' PhotometricInterpretation = 2 (RGB) statt eines Sensormusters. Ihre Werte sind
                ' schon tonwertkorrigiert - unsere Basisstufe legte eine ZWEITE Tonkurve darueber
                ' und riss das Bild auf: gemessen 0,7 Blendenstufen zu hell und 14 % ausgefressene
                ' Pixel gegen 0,4 % in der eingebetteten Vorschau. Fuer sie bleibt es bei LibRaws
                ' eigener Gamma-Ausgabe, nur ohne Histogramm-Stretch.
                Dim bereitsGerendert = IsFinishedRgb(path)
                Dim linearMoeglich = Not bereitsGerendert AndAlso
                                     _setNoAutoBright IsNot Nothing AndAlso _setGamma IsNot Nothing
                If bereitsGerendert AndAlso _setNoAutoBright IsNot Nothing Then _setNoAutoBright(handle, 1)
                If linearMoeglich Then
                    _setNoAutoBright(handle, 1)
                    _setGamma(handle, 0, 1.0F) ' Gamma-Kurve neutral: 1/1 = linear
                    _setGamma(handle, 1, 1.0F)
                Else
                    _setOutputBps(handle, 8)
                End If
                ' Rauschminderung VOR dem Demosaic. Sie greift an der einzigen Stelle, an der das
                ' Muster des Sensors noch bekannt ist - danach hat das Demosaic den Fehler bereits
                ' ueber die Nachbarschaft verteilt, und jede spaetere Glaettung muss ihn wieder
                ' einsammeln. Gemessen am dunklen Bildteil eines Konzert-RAW, durch die Basisstufe:
                ' Farbrauschen 2,47 -> 1,31, Luminanzrauschen 1,03 -> 0,85, Detail 0,66 -> 0,64.
                ' Kostet rund eine halbe Sekunde bei 20 MP.
                ' LEICHT (1), nicht VOLL (2): voll ist beim Farbrauschen gemessen SCHLECHTER
                ' (1,74 gegen 1,31) - hier gilt nicht "mehr ist besser".
                If _setFbdd IsNot Nothing Then _setFbdd(handle, FbddRauschminderung)
                ' Das gewaehlte Demosaic-Verfahren. Fehlt der Setter (aeltere libraw), bleibt es bei
                ' LibRaws Vorgabe - die Auswahl ist dann wirkungslos, aber nichts geht kaputt.
                If _setDemosaic IsNot Nothing Then _setDemosaic(handle, ConfiguredDemosaic())
                ' Kamera-Weißabgleich: die As-Shot-Multiplikatoren als user_mul setzen (die C-API
                ' hat keinen use_camera_wb-Setter). Ohne gültige cam_mul bleibt der Standard.
                Dim mul0 = _getCamMul(handle, 0)
                If Single.IsFinite(mul0) AndAlso mul0 > 0 Then
                    For i = 0 To 3
                        _setUserMul(handle, i, _getCamMul(handle, i))
                    Next
                End If

                If _process(handle) <> 0 Then Return Nothing
                Dim errc = 0
                image = _makeMemImage(handle, errc)
                If image = IntPtr.Zero OrElse errc <> 0 Then Return Nothing

                ' libraw_processed_image_t: type(4) height(2) width(2) colors(2) bits(2) data_size(4) data.
                Dim imageType = Marshal.ReadInt32(image, 0)
                Dim height = CInt(CUShort(Marshal.ReadInt16(image, 4)))
                Dim width = CInt(CUShort(Marshal.ReadInt16(image, 6)))
                Dim colors = CInt(CUShort(Marshal.ReadInt16(image, 8)))
                Dim bits = CInt(CUShort(Marshal.ReadInt16(image, 10)))
                Dim dataSize = Marshal.ReadInt32(image, 12)
                ' 2 = Bitmap. Erwartet werden 16 Bit (output_bps oben); eine exotische libraw,
                ' die trotzdem 8 Bit liefert, wird unveraendert umgepackt.
                If imageType <> 2 OrElse colors <> 3 OrElse (bits <> 8 AndAlso bits <> 16) Then Return Nothing
                Dim pixelCount = CheckedPixelCount(width, height)
                ' Die Schranke muss den GROESSTEN Schritt abdecken, der weiter unten gerechnet
                ' wird, nicht nur den des Zielpuffers. Der ist 4 Byte je Pixel, die 16-Bit-Quelle
                ' in Convert16 aber 6. Dort wird der Zeigerversatz (Zeile mal Schrittweite) fuer
                ' IntPtr.op_Addition auf Integer verengt - mit einer festen 4-Byte-Schranke lag
                ' der Ueberlaufpunkt INNERHALB des Erlaubten statt dahinter.
                Dim maxBytesProPixel = Math.Max(4, 3 * (bits \ 8))
                If pixelCount <= 0 OrElse pixelCount > Integer.MaxValue \ maxBytesProPixel Then Return Nothing
                If dataSize < pixelCount * 3L * (bits \ 8) Then Return Nothing

                Dim bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque))
                Try
                    ' ZEILENWEISE direkt in die Bitmap. Vorher stand hier ein 8-Bit-Vollbildpuffer
                    ' (bei 45 Megapixeln rund 180 MB auf dem Haufen fuer grosse Objekte), der danach
                    ' noch einmal komplett kopiert wurde. Eine Zeile ist ein paar Kilobyte und geht
                    ' direkt an ihr Ziel; die Bitmapzeile kann breiter sein als das Bild, deshalb
                    ' laeuft der Versatz ueber RowBytes und nicht ueber die Breite.
                    Dim targetPtr = bitmap.GetPixels()
                    If targetPtr = IntPtr.Zero Then
                        bitmap.Dispose()
                        Return Nothing
                    End If
                    Dim targetStride = bitmap.RowBytes
                    Dim rowLength = width * 4
                    Dim row(rowLength - 1) As Byte
                    If bits = 16 Then
                        ' Objektivkorrektur: Farbquerfehler und Vignettierung kommen aus der
                        ' mitgelieferten Sammlung von Messwerten. Die frueher hier stehende eigene
                        ' SCHAETZUNG aus dem Bildinhalt ist stillgelegt - sie fand auf echten Fotos
                        ' nur ein Neuntel des Farbsaums. Mit Messwerten aus der Sammlung sind es
                        ' gemessen 30 bis 45 Prozent (siehe RAW_UND_FARBE.md); damit lohnt die
                        ' Stelle, an der die Korrektur sitzt, obwohl sie hinter dem Demosaic liegt.
                        Convert16Rows(image + 16, width, height, row,
                                      Sub(y) Marshal.Copy(row, 0, IntPtr.Add(targetPtr, y * targetStride), rowLength),
                                      lens, baseEv)
                    Else
                        Dim sourceStride = width * 3
                        Dim rgb(sourceStride - 1) As Byte
                        For y = 0 To height - 1
                            Marshal.Copy(image + 16 + y * sourceStride, rgb, 0, sourceStride)
                            Dim d = 0
                            For x = 0 To width - 1
                                row(d) = rgb(x * 3 + 2)      ' B
                                row(d + 1) = rgb(x * 3 + 1)  ' G
                                row(d + 2) = rgb(x * 3)      ' R
                                row(d + 3) = 255
                                d += 4
                            Next
                            Marshal.Copy(row, 0, IntPtr.Add(targetPtr, y * targetStride), rowLength)
                        Next
                    End If
                    ' Reihenfolge: erst Vignettierung und Farbquerfehler (beide im Umsetzungsschritt
                    ' oben, auf den unveraenderten Bildpunkten gemessen), DANN die Verzeichnung.
                    ' Andersherum wuerden beide an verschobenen Stellen rechnen. Die Verzeichnung
                    ' laeuft aber nicht mehr hier, sondern hinter dem Zwischenspeicher
                    ' (MitVerzeichnung) - so wirft ihr Umschalten den Decode nicht weg. An der
                    ' Reihenfolge aendert das nichts: was hier zurueckkommt, ist genau das Bild vor
                    ' der Verzeichnung.
                    Return bitmap
                Catch
                    bitmap.Dispose()
                    Return Nothing
                End Try
            Catch
                Return Nothing
            Finally
                If image <> IntPtr.Zero Then _clearMem(image)
                _close(handle)
                If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
            End Try
        End Function


        ''' <summary>Dither-Schwellen fuer die 16-nach-8-Quantisierung: geordnete 8x8-Bayer-Matrix
        ''' (dieselbe rekursive Konstruktion wie in der Punktoperationskette, dort privat), als
        ''' Integer-Schwelle T skaliert, sodass out = (v*255 + T) \ 65535 mittelwerttreu
        ''' quantisiert. Positionsbasiert und damit deterministisch - derselbe Decode liefert
        ''' bitgleiche Ergebnisse, was der Vorschau-Cache voraussetzt.</summary>
        Private Shared ReadOnly DitherThresholds As Integer() = BuildDitherThresholds()

        Private Shared Function BuildDitherThresholds() As Integer()
            ' Rekursive Bayer-Konstruktion: M(2n) = [4M(n), 4M(n)+2; 4M(n)+3, 4M(n)+1]
            Dim m = New Integer() {0, 2, 3, 1}
            Dim size = 2
            While size < 8
                Dim n2 = size * 2
                Dim dst = New Integer(n2 * n2 - 1) {}
                For y = 0 To size - 1
                    For x = 0 To size - 1
                        Dim v = m(y * size + x) * 4
                        dst(y * n2 + x) = v
                        dst(y * n2 + (x + size)) = v + 2
                        dst((y + size) * n2 + x) = v + 3
                        dst((y + size) * n2 + (x + size)) = v + 1
                    Next
                Next
                m = dst
                size = n2
            End While
            Dim thresholds = New Integer(63) {}
            For i = 0 To 63
                ' T = ((2*m+1)/128) * 65535: Schwellenmitte je Zelle, Mittelwert exakt 0,5 LSB.
                thresholds(i) = ((2 * m(i) + 1) * 65535) \ 128
            Next
            Return thresholds
        End Function

        ''' <summary>Packt LibRaws 16-Bit-Ausgabe nach Bgra8888 und quantisiert dabei mit
        ''' Bayer-Dither statt durch Abschneiden.
        '''
        ''' DER REGELWEG jedes RAW-Decodes (DecodeOutputBits = 16). Die Diagnose ruft die Methode
        ''' zusaetzlich direkt auf, um die Quantisierung einzeln zu messen.
        '''
        ''' WARUM ueberhaupt: gemessen tragen dunkle Flaechen eines Konzertfotos nur 1-3 Byte
        ''' Kanalabstand. Beim direkten 8-Bit-Decode faellt dieser Rest beim Runden weg, BEVOR
        ''' irgendein Regler ihn anheben kann - eine Aufhellung der Tiefen zieht dann graue statt
        ''' farbiger Flaechen hoch. Mit Dither bleibt der Sub-Byte-Anteil als raeumliches Muster
        ''' erhalten; die ohnehin laufende Farbrauschreduzierung mittelt ihn wieder heraus.
        ''' Der Preis ist LibRaws doppelt so grosser Zwischenpuffer (45 MP: 270 statt 135 MB),
        ''' der unmittelbar nach dem Umpacken wieder freigegeben wird.
        '''
        ''' Zeilenweise aus dem nativen Puffer kopiert, damit kein zweiter Vollbild-Puffer
        ''' entsteht.</summary>

        ''' <summary>Adobes ACR-Standardtonkurve (dng_tone_curve_acr3_default aus dem oeffentlichen
        ''' DNG-SDK), auf 129 Stuetzstellen gerastert - linear ein, linear aus. Der Rasterfehler
        ''' gegen die 1025 Originalstuetzstellen liegt bei 0,107 von 255, also unter einem halben
        ''' Tonwert. Sie darf mitgeliefert werden; Adobes TABELLENdaten (HueSatMap, LookTable)
        ''' duerfen es NICHT.</summary>
        Private Shared ReadOnly Acr3Kurve As Double() = {
            0.000000, 0.006230, 0.014850, 0.026430, 0.040020, 0.054830, 0.070570, 0.087100,
            0.104330, 0.122180, 0.140610, 0.159560, 0.179010, 0.198930, 0.219290, 0.239630,
            0.259610, 0.279180, 0.298330, 0.317040, 0.335310, 0.353130, 0.370500, 0.387420,
            0.403890, 0.419930, 0.435540, 0.450730, 0.465510, 0.479890, 0.493870, 0.507470,
            0.520690, 0.533560, 0.546070, 0.558240, 0.570070, 0.581590, 0.592810, 0.603730,
            0.614360, 0.624720, 0.634820, 0.644660, 0.654260, 0.663620, 0.672750, 0.681650,
            0.690350, 0.698830, 0.707110, 0.715200, 0.723090, 0.730810, 0.738340, 0.745700,
            0.752890, 0.759920, 0.766790, 0.773500, 0.780060, 0.786470, 0.792740, 0.798870,
            0.804860, 0.810720, 0.816450, 0.822050, 0.827530, 0.832880, 0.838120, 0.843240,
            0.848250, 0.853150, 0.857940, 0.862620, 0.867200, 0.871680, 0.876060, 0.880340,
            0.884530, 0.888620, 0.892620, 0.896530, 0.900350, 0.904080, 0.907730, 0.911300,
            0.914780, 0.918180, 0.921500, 0.924750, 0.927920, 0.931010, 0.934030, 0.936980,
            0.939860, 0.942660, 0.945400, 0.948070, 0.950670, 0.953200, 0.955670, 0.958080,
            0.960420, 0.962710, 0.964930, 0.967090, 0.969190, 0.971230, 0.973220, 0.975150,
            0.977020, 0.978840, 0.980610, 0.982320, 0.983980, 0.985580, 0.987140, 0.988640,
            0.990090, 0.991500, 0.992850, 0.994160, 0.995420, 0.996640, 0.997800, 0.998920,
            1.000000
        }

        ''' <summary>Grundbelichtung in EV und Schwarzabzug der Belichtungsrampe.
        '''
        ''' Beides sind ECHTE Groessen aus Adobes Pipeline (BaselineExposure bzw. der Schwarzabzug
        ''' des DNG-SDK), keine freien Parameter. Gefittet gegen Referenz-Exporte OHNE Preset an
        ''' DREI Motiven mit angeglichener Geometrie; das Optimum liegt bei allen dreien auf
        ''' demselben Paar - also kamerafest, nicht motivabhaengig. Mittlerer |dRGB| zur Referenz:
        ''' Basis gegen Basis 13,1/15,0/25,1 (Auto-Aufhellung) auf 5,4/5,4/15,5; durch die
        ''' unveraenderte Preset-Kette 16,4/21,5/15,7 auf 11,8/12,7/13,7.
        '''
        ''' MESSHINWEIS: der Referenz-Export entzerrt standardmaessig mit dem Objektivprofil und skaliert
        ''' dabei um rund 2 %. Ohne Geometrie-Angleich misst ein Vergleich vor allem
        ''' Fehlregistrierung (WID_7643: 27,2 statt 15,9). FerrumPix baut die Objektivkorrektur
        ''' bewusst NICHT nach - siehe Audits/RAW_UND_FARBE.md.</summary>
        ''' <summary>FBDD-Stufe von LibRaw: 0 = aus, 1 = leicht, 2 = voll. Siehe Begruendung an der
        ''' Aufrufstelle - 2 ist beim Farbrauschen gemessen schlechter als 1.</summary>
        Private Const FbddRauschminderung As Integer = 1

        Private Const BaseExposureEv As Double = 0.5
        Private Const SchwarzAbzug As Double = 0.003
        ''' <summary>Tabelle LINEAR (0..65535) -> Belichtungsrampe + ACR3-Tonkurve, wieder linear
        ''' in 0..65535. Einmal gebaut statt pro Pixel gerechnet: die Kurve wird je Pixel ZWEIMAL
        ''' gebraucht (hellster und dunkelster Kanal).</summary>
        ''' <summary>Tontabellen je Grundbelichtung. Frueher gab es genau EINE, weil die
        ''' Grundbelichtung eine Konstante war; mit den Kamera-Referenzwerten gibt es je Kamera eine.
        ''' Der Aufbau kostet 65536 Schritte, in der Praxis liegen aber nur eine Handvoll Werte an -
        ''' deshalb ein kleiner Speicher statt Neuaufbau je Bild.</summary>
        Private Shared ReadOnly TonTabellen As New Dictionary(Of Integer, Integer())
        Private Shared ReadOnly TonTabellenLock As New Object()

        Friend Shared Function TonTabelleFuer(ev As Double) As Integer()
            Dim k = CInt(Math.Round(ev * 1000.0))
            SyncLock TonTabellenLock
                Dim vorhanden As Integer() = Nothing
                If TonTabellen.TryGetValue(k, vorhanden) Then Return vorhanden
                Dim neu2 = BuildToneTable(k / 1000.0)
                TonTabellen(k) = neu2
                Return neu2
            End SyncLock
        End Function

        ''' <summary>Tabelle LINEAR (0..65535) -> sRGB-kodiert (0..65535). Getrennt von der
        ''' Tonkurve, weil der mittlere Kanal ZWISCHEN den beiden anderen interpoliert wird - und
        ''' zwar linear, vor der Gamma-Kodierung.</summary>
        Private Shared ReadOnly GammaTabelle As Integer() = BuildGammaTable()

        Private Shared Function BuildToneTable(ev As Double) As Integer()
            Dim t(65535) As Integer
            Dim weiss = 1.0 / (2.0 ^ ev)
            Dim steigung = 1.0 / (weiss - SchwarzAbzug)
            ' Weicher Fuss wie im DNG-SDK: eine quadratische Anlaufstrecke um den Schwarzpunkt,
            ' sonst entstuende dort eine sichtbare Kante.
            Dim radius = Math.Min(0.5 * SchwarzAbzug, (1.0 / 16.0) / steigung)
            Dim qScale = If(radius > 0, steigung / (4.0 * radius), 0.0)
            For i = 0 To 65535
                Dim x = i / 65535.0
                Dim v As Double
                If x <= SchwarzAbzug - radius Then
                    v = 0.0
                ElseIf x >= SchwarzAbzug + radius Then
                    v = Math.Min((x - SchwarzAbzug) * steigung, 1.0)
                Else
                    v = qScale * (x - (SchwarzAbzug - radius)) ^ 2
                End If
                ' ACR3-Kurve mit linearer Interpolation zwischen den 129 Stuetzstellen.
                Dim pos = Math.Max(0.0, Math.Min(1.0, v)) * (Acr3Kurve.Length - 1)
                Dim idx = CInt(Math.Floor(pos))
                If idx > Acr3Kurve.Length - 2 Then idx = Acr3Kurve.Length - 2
                Dim f = pos - idx
                Dim yk = Acr3Kurve(idx) * (1.0 - f) + Acr3Kurve(idx + 1) * f
                t(i) = CInt(Math.Round(Math.Max(0.0, Math.Min(1.0, yk)) * 65535.0))
            Next
            Return t
        End Function

        Private Shared Function BuildGammaTable() As Integer()
            Dim t(65535) As Integer
            For i = 0 To 65535
                Dim v = i / 65535.0
                Dim sg = If(v <= 0.0031308, v * 12.92, 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055)
                t(i) = CInt(Math.Round(Math.Max(0.0, Math.Min(1.0, sg)) * 65535.0))
            Next
            Return t
        End Function

        ''' <summary>Dieselbe Umsetzung in einen VOLLBILDPUFFER, als Huelle um
        ''' <see cref="Convert16Rows"/>.
        '''
        ''' KEIN Aufrufer in der Anwendung - der Decode schreibt zeilenweise. Sie steht fuer die
        ''' beiden Diagnosepruefungen „RAW-Decode 16→8: Dither ist mittelwerttreu und faerbt Grau
        ''' nicht ein" und „Der Decode wendet Vignettierung und Farbquerfehler wirklich an", die die
        ''' Umsetzung an einem Puffer mit bekanntem Inhalt messen und sie ueber Reflexion aufrufen.
        ''' Ein Puffer ist dort die brauchbare Form; entscheidend ist, dass beide Wege denselben Kern
        ''' nehmen, sodass das Gemessene nicht vom Ausgelieferten abweichen kann. Wer diese
        ''' Pruefungen entfernt, entfernt auch diese Huelle.</summary>
        Private Shared Sub Convert16(data As IntPtr, width As Integer, height As Integer, pixels As Byte(),
                                     Optional lens As LensDataService.Korrektur = Nothing,
                                     Optional grundbelichtungEvWert As Double = BaseExposureEv)
            Dim rowBytes = width * 4
            Dim row(rowBytes - 1) As Byte
            Convert16Rows(data, width, height, row,
                          Sub(y) Array.Copy(row, 0, pixels, y * rowBytes, rowBytes),
                          lens, grundbelichtungEvWert)
        End Sub

        ''' <summary>16-Bit-LINEARE LibRaw-Ausgabe in 8-Bit-sRGB umsetzen: Belichtungsrampe,
        ''' ACR3-Tonkurve nach Adobes RGBTone-Regel, sRGB-Gamma, Bayer-Dither. Umgesetzt wird EINE
        ''' Zeile nach der anderen in <paramref name="rowBuffer"/>; wohin sie geht, entscheidet der
        ''' Aufrufer in <paramref name="onRow"/>.
        '''
        ''' RGBTone heisst: die Kurve laeuft auf dem HELLSTEN und dem DUNKELSTEN Kanal, der
        ''' mittlere wird zwischen den beiden Ergebnissen interpoliert. Kanalweise angewandt
        ''' verschoebe die Kurve den Farbton - genau der Fehler, der Lichter ausbleichen laesst.
        '''
        ''' Der Dither benutzt DIESELBE Schwelle fuer alle drei Kanaele eines Pixels: kanalweise
        ''' verschiedene Schwellen faerben neutrale Flaechen ein.
        '''
        ''' WARUM ZEILENWEISE: vorher entstand hier ein 8-Bit-Vollbildpuffer, der bei 45 Megapixeln
        ''' rund 180 MB gross ist, auf dem Haufen fuer grosse Objekte landet und danach noch einmal
        ''' vollstaendig in die Bitmap kopiert wurde. Eine Zeile ist ein paar Kilobyte, wird
        ''' wiederverwendet und geht direkt an ihr Ziel.</summary>
        Private Shared Sub Convert16Rows(data As IntPtr, width As Integer, height As Integer,
                                         rowBuffer As Byte(), onRow As Action(Of Integer),
                                         Optional lens As LensDataService.Korrektur = Nothing,
                                         Optional grundbelichtungEvWert As Double = BaseExposureEv)
            Dim thresholds = DitherThresholds
            Dim ton = TonTabelleFuer(grundbelichtungEvWert)
            Dim gamma = GammaTabelle
            Dim rowBytes = width * 6

            ' Farbquerfehler und Vignettierung werden BEIM UMSETZEN erledigt - ein eigener Durchgang
            ' ueber 20 MP waere ein zweites Mal Speicherbandbreite fuer nichts. Und beide gehoeren
            ' hierher, weil hier noch LINEARE 16-Bit-Werte anliegen: nach der Gamma-Kodierung zu
            ' interpolieren zieht Kanten schief, und eine Helligkeitskorrektur waere dort schlicht
            ' falsch gerechnet.
            '
            ' Die Verzeichnung sitzt BEWUSST NICHT hier: sie verschiebt Pixel um Dutzende Zeilen,
            ' waehrend der Zeilenring unten auf Bruchteile eines Pixels ausgelegt ist. Sie ist eine
            ' eigene Stufe hinter dem Decode.
            Dim korrigiertTca = lens IsNot Nothing AndAlso lens.HasChromaticAberration
            Dim korrigiertVignette = lens IsNot Nothing AndAlso lens.HasVignetting
            Dim korrigiert = korrigiertTca OrElse korrigiertVignette
            Dim cx = (width - 1) / 2.0, cy = (height - 1) / 2.0
            Dim normScale = If(lens IsNot Nothing, lens.NormScale, 0.0)
            Dim cornerScale = If(lens IsNot Nothing, lens.CornerScale, 1.0)

            ' Zeilenring: Gruen kommt aus der eigenen Zeile, Rot und Blau aus benachbarten. Die
            ' Verschiebung ist klein, deshalb reichen wenige Zeilen - und es bleibt bei EINER
            ' Kopie je Quellzeile statt einer je Zugriff.
            Const RingSize = 8
            Dim ring(RingSize - 1)() As Short
            Dim ringRow(RingSize - 1) As Integer
            For i = 0 To RingSize - 1
                ReDim ring(i)(width * 3 - 1) : ringRow(i) = -1
            Next
            Dim FetchRow = Function(zy As Integer) As Short()
                                Dim yy = Math.Min(Math.Max(zy, 0), height - 1)
                                Dim slot = yy Mod RingSize
                                If ringRow(slot) <> yy Then
                                    ' Versatz in Integer, siehe HeifDecodeService: IntPtr addiert
                                    ' nur Integer. Die Schranke des Aufrufers ist auf 6 Byte je
                                    ' Pixel bemessen, also auf genau diese Schrittweite.
                                    Marshal.Copy(data + yy * rowBytes, ring(slot), 0, width * 3)
                                    ringRow(slot) = yy
                                End If
                                Return ring(slot)
                            End Function

            For y = 0 To height - 1
                Dim rowShorts = FetchRow(y)
                Dim d = 0
                Dim ditherRow = (y And 7) << 3
                Dim dyPix = y - cy
                For x = 0 To width - 1
                    ' And &HFFFF hebt die Short-Werte vorzeichenfrei nach Integer (VB hat kein
                    ' UShort-Marshalling ueber Marshal.Copy).
                    Dim g = rowShorts(x * 3 + 1) And &HFFFF
                    Dim r As Integer, b As Integer
                    If korrigiert Then
                        Dim dxPix = x - cx
                        ' EIN Radius fuer beide Korrekturen - die Wurzel ist der teuerste Anteil
                        ' dieser Schleife und wird nicht zweimal gezogen.
                        Dim rPix = Math.Sqrt(dxPix * dxPix + dyPix * dyPix)
                        Dim rNorm = rPix * normScale

                        If korrigiertTca AndAlso rPix > 0.0 Then
                            ' ACHTUNG Konvention: der Faktor sagt, WIE WEIT AUSSEN der Kanal
                            ' abgetastet erscheint. Korrigiert wird mit dem KEHRWERT. Multiplizieren
                            ' statt dividieren verdoppelt den Farbsaum, statt ihn zu entfernen -
                            ' genau daran ist die erste Fassung gescheitert, und am echten Bild war
                            ' der Unterschied zu klein, um es zu merken.
                            Dim fr = LensDataService.ChromaticAberrationFactor(lens, rNorm, True)
                            Dim fb = LensDataService.ChromaticAberrationFactor(lens, rNorm, False)
                            r = AbtastenBilinear(FetchRow, width, height, cx + dxPix / fr, cy + dyPix / fr, 0)
                            b = AbtastenBilinear(FetchRow, width, height, cx + dxPix / fb, cy + dyPix / fb, 2)
                        Else
                            r = rowShorts(x * 3) And &HFFFF
                            b = rowShorts(x * 3 + 2) And &HFFFF
                        End If

                        If korrigiertVignette Then
                            ' Der gemessene Wert beschreibt den ABFALL, korrigiert wird durch
                            ' Teilen. Und er rechnet mit r = 1 in der ECKE, nicht an der langen
                            ' Kante wie der Farbquerfehler - daher die zweite Skala.
                            Dim abfall = LensDataService.VignettingFactor(lens, rNorm * cornerScale)
                            ' Sehr kleine Werte wuerden das Rauschen der Bildecke ins Unermessliche
                            ' heben; drei Blendenstufen sind die Grenze des Sinnvollen.
                            If abfall < 0.125 Then abfall = 0.125
                            r = Math.Min(65535, CInt(r / abfall))
                            g = Math.Min(65535, CInt(g / abfall))
                            b = Math.Min(65535, CInt(b / abfall))
                        End If
                    Else
                        r = rowShorts(x * 3) And &HFFFF
                        b = rowShorts(x * 3 + 2) And &HFFFF
                    End If
                    ' DIESELBE Schwelle fuer alle drei Kanaele eines Pixels (wie in der
                    ' Punktoperationskette): kanalweise verschiedene Schwellen faerben neutrale
                    ' Flaechen ein. (v*255 + T) \ 65535 liegt fuer T < 65535 immer in 0..255,
                    ' CByte kann nicht ueberlaufen.
                    Dim high = Math.Max(r, Math.Max(g, b))
                    Dim tief = Math.Min(r, Math.Min(g, b))
                    Dim tHoch = ton(high)
                    Dim tTief = ton(tief)
                    Dim rr As Integer, gg As Integer, bb As Integer
                    If high = tief Then
                        rr = tHoch : gg = tHoch : bb = tHoch
                    Else
                        Dim span = high - tief
                        Dim delta = tHoch - tTief
                        rr = tTief + CInt(CLng(delta) * (r - tief) \ span)
                        gg = tTief + CInt(CLng(delta) * (g - tief) \ span)
                        bb = tTief + CInt(CLng(delta) * (b - tief) \ span)
                    End If
                    Dim t = thresholds(ditherRow Or (x And 7))
                    rowBuffer(d) = CByte((gamma(bb) * 255 + t) \ 65535)
                    rowBuffer(d + 1) = CByte((gamma(gg) * 255 + t) \ 65535)
                    rowBuffer(d + 2) = CByte((gamma(rr) * 255 + t) \ 65535)
                    rowBuffer(d + 3) = 255
                    d += 4
                Next
                onRow(y)
            Next
        End Sub

        ''' <summary>Einen Kanal bilinear an einer beliebigen Stelle abtasten. Die Raender werden
        ''' geklemmt statt gespiegelt - beim Farbquerfehler geht es um Bruchteile eines Pixels, da
        ''' ist Klemmen unsichtbar und billiger.</summary>
        Private Shared Function AbtastenBilinear(holeZeile As Func(Of Integer, Short()),
                                                 width As Integer, height As Integer,
                                                 sx As Double, sy As Double, kanal As Integer) As Integer
            If sx < 0 Then sx = 0
            If sy < 0 Then sy = 0
            If sx > width - 1.001 Then sx = width - 1.001
            If sy > height - 1.001 Then sy = height - 1.001
            Dim x0 = CInt(Math.Floor(sx)), y0 = CInt(Math.Floor(sy))
            Dim fx = sx - x0, fy = sy - y0
            Dim x1 = Math.Min(x0 + 1, width - 1)
            Dim z0 = holeZeile(y0), z1 = holeZeile(y0 + 1)
            Dim o0 = x0 * 3 + kanal, o1 = x1 * 3 + kanal
            Dim top = (z0(o0) And &HFFFF) * (1.0 - fx) + (z0(o1) And &HFFFF) * fx
            Dim bottom = (z1(o0) And &HFFFF) * (1.0 - fx) + (z1(o1) And &HFFFF) * fx
            Dim v = top * (1.0 - fy) + bottom * fy
            Return CInt(Math.Min(Math.Max(v, 0.0), 65535.0))
        End Function

        ' Die frueher hier stehende SCHAETZUNG des Farbquerfehlers aus dem Bildinhalt ist
        ' entfernt. Sie fand auf echten Fotos nur ein Neuntel des Farbsaums, waehrend Messwerte
        ' aus der mitgelieferten Objektiv-Sammlung 30 bis 45 Prozent entfernen (gemessen
        ' 28.07.2026, siehe RAW_UND_FARBE.md). Zwei Erklaerungsversuche fuer ihr Versagen sind
        ' widerlegt und stehen dort; wer sie wiederbeleben will, faengt nicht bei null an.


        ''' <summary>Traegt die Datei ein bereits fertig gerendertes RGB-Bild statt Sensordaten?
        ''' Erkannt an PhotometricInterpretation = 2 (RGB). Ein echtes Sensor-RAW steht dort auf
        ''' 32803 (Farbfilter-Muster), ein lineares DNG auf 34892 - beide sind linear und gehoeren
        ''' durch unsere Basisstufe. Nur die RGB-Variante ist schon fertig.
        ''' Faellt das Lesen aus, gilt die Datei als normal - lieber die Basisstufe anwenden als
        ''' bei jedem unlesbaren Header darauf zu verzichten.</summary>
        Private Shared Function IsFinishedRgb(path As String) As Boolean
            Try
                If Not path.EndsWith(".dng", StringComparison.OrdinalIgnoreCase) Then Return False
                Dim verzeichnisse = MetadataExtractor.ImageMetadataReader.ReadMetadata(path)
                For Each d In verzeichnisse.OfType(Of MetadataExtractor.Formats.Exif.ExifDirectoryBase)()
                    ' Ausgeschrieben, weil MetadataExtractor das als Erweiterungsmethode anbietet
                    ' und diese Datei den Namensraum nicht importiert.
                    Dim value As Integer
                    If MetadataExtractor.DirectoryExtensions.TryGetInt32(
                           d, MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagPhotometricInterpretation, value) Then
                        ' Bei einer DNG-Huelle um ein RGB-Bild steht 2 im Hauptbild; ein Sensormuster
                        ' (32803) oder LinearRaw (34892) gaebe es dort gar nicht.
                        If value = 2 Then Return True
                    End If
                Next
                Return False
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Die Grundbelichtung fuer DIESE Datei. Ohne die Einstellung bleibt es beim
        ''' festen Wert; mit ihr entscheidet das Kameramodell aus den EXIF-Daten. Ein unbekanntes
        ''' Modell oder eine unlesbare Datei fuehrt IMMER auf den festen Wert zurueck - nie raten.</summary>
        Private Shared Function BaseExposureForFile(path As String) As Double
            Try
                If Not AppSettingsService.Load().UseCameraBaselineTable Then Return BaseExposureEv
                Dim verzeichnisse = MetadataExtractor.ImageMetadataReader.ReadMetadata(path)
                Dim ifd0 = verzeichnisse.OfType(Of MetadataExtractor.Formats.Exif.ExifIfd0Directory)().FirstOrDefault()
                If ifd0 Is Nothing Then Return BaseExposureEv
                Return CameraBaselineTable.BaseExposureFor(
                    ifd0.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagMake),
                    ifd0.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagModel),
                    BaseExposureEv)
            Catch
                Return BaseExposureEv
            End Try
        End Function

        Private Shared Function CheckedPixelCount(width As Integer, height As Integer) As Long
            If width <= 0 OrElse height <= 0 Then Return 0
            Dim pixelCount = CLng(width) * CLng(height)
            If pixelCount > Integer.MaxValue Then Return 0
            Return pixelCount
        End Function

        Private Shared Function StringToUtf8(value As String) As IntPtr
            Dim bytes = Text.Encoding.UTF8.GetBytes(value)
            Dim ptr = Marshal.AllocCoTaskMem(bytes.Length + 1)
            Marshal.Copy(bytes, 0, ptr, bytes.Length)
            Marshal.WriteByte(ptr, bytes.Length, 0)
            Return ptr
        End Function

    End Class

End Namespace
