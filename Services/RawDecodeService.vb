Imports System
Imports System.IO
Imports System.Runtime.InteropServices
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
    ''' Verfügbarkeit sauber prüfbar. Entwickelt wird mit Kamera-Weißabgleich (cam_mul als
    ''' user_mul - libraw hat keinen C-API-Setter für use_camera_wb), sRGB, 8 Bit.
    ''' LibRaws automatische Aufhellung bleibt an (Histogramm-Stretch bis 1 % Clipping) - sie ist
    ''' das, was eine Entwicklung ohne weiteres Zutun brauchbar aussehen laesst.
    ''' Ein 16-Bit-Zweig mit Dither-Quantisierung ist gebaut und STILLGELEGT (siehe
    ''' DecodeOutputBits) - er bringt gemessen zu wenig fuer den doppelten Zwischenpuffer.
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
        Private Shared Iterator Function MitgelieferteKandidaten(namen As String()) As IEnumerable(Of String)
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
                    candidates = {"libraw.dylib", "libraw.23.dylib", "libraw.22.dylib"}
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

                ' DANN die mitgelieferte: Windows und die portablen Pakete haben keine System-
                ' bibliothek. Sie liegt neben der Anwendung bzw. unter runtimes/<rid>/native -
                ' ein nackter Name findet sie dort NICHT, es braucht den vollen Pfad.
                If handle = IntPtr.Zero Then
                    For Each pfad In MitgelieferteKandidaten(candidates)
                        If NativeLibrary.TryLoad(pfad, handle) Then
                            geladen = Path.GetFileName(pfad)
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
                    _library = handle
                Catch
                    ' Ein fehlender Export = Bibliothek unbrauchbar; alles auf Anfang.
                    _init = Nothing : _openFile = Nothing : _unpack = Nothing
                    _setOutputBps = Nothing : _setOutputColor = Nothing
                    _getCamMul = Nothing : _setUserMul = Nothing
                    _setNoAutoBright = Nothing : _setGamma = Nothing : _setFbdd = Nothing
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
        Private Shared _cachedBitmap As SKBitmap

        ''' <summary>Voll aufgelöster, fertig entwickelter Decode (Besitz beim Aufrufer) oder Nothing.
        ''' Orientierung ist bereits angewandt (libraw dreht nach dem Kamera-Flip).</summary>
        Public Shared Function TryDecode(path As String) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            Try
                Dim writeTime = File.GetLastWriteTimeUtc(path)
                Dim grundEv = GrundbelichtungFuerDatei(path)
                SyncLock _cacheLock
                    If _cachedBitmap IsNot Nothing AndAlso
                       String.Equals(_cachedPath, path, StringComparison.Ordinal) AndAlso
                       _cachedWriteTimeUtc = writeTime AndAlso
                       _cachedGrundbelichtung = grundEv Then
                        Return _cachedBitmap.Copy()
                    End If
                End SyncLock

                ' Ohne reentrante libraw laufen die nativen Aufrufe nacheinander - die
                ' Thumbnail-Erzeugung ruft aus mehreren Threads hier herein (Parallel.For).
                Dim decoded As SKBitmap
                If _reentrant Then
                    decoded = DecodeCore(path, grundEv)
                Else
                    SyncLock _nativeLock
                        decoded = DecodeCore(path, grundEv)
                    End SyncLock
                End If
                If decoded Is Nothing Then Return Nothing

                SyncLock _cacheLock
                    _cachedBitmap?.Dispose()
                    _cachedBitmap = decoded
                    _cachedPath = path
                    _cachedGrundbelichtung = grundEv
                    _cachedWriteTimeUtc = writeTime
                    Return _cachedBitmap.Copy()
                End SyncLock
            Catch
                Return Nothing
            End Try
        End Function

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

        Private Shared Function DecodeCore(path As String, grundEv As Double) As SKBitmap
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

                _setOutputBps(handle, DecodeOutputBits)
                _setOutputColor(handle, 1) ' sRGB
                ' LINEAR und OHNE Auto-Aufhellung dekodieren - die Tonabbildung macht Convert16.
                ' LibRaws Auto-Aufhellung ist ein Histogramm-Stretch: gemessen faellt sie an einem
                ' dunklen Motiv zu HELL und an einem hellen zu DUNKEL aus (P50 0,140 gegen Lightrooms
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
                Dim bereitsGerendert = IstFertigGerendertesRgb(path)
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
                If pixelCount <= 0 OrElse pixelCount > Integer.MaxValue \ 4 Then Return Nothing
                If dataSize < pixelCount * 3L * (bits \ 8) Then Return Nothing

                Dim bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque))
                Try
                    Dim pixels(CInt(pixelCount * 4L - 1)) As Byte
                    If bits = 16 Then
                        ' Farbquerfehler: STILLGELEGT, nicht entfernt. Die Mechanik ist belegt
                        ' (synthetisches Bild mit bekanntem Fehler: 78 % entfernt, siehe Pruefstand),
                        ' aber die SCHAETZUNG traegt auf echten Bildinhalten nicht - am Referenzfoto
                        ' bleiben 89 % des Farbsaums stehen. Zwei Vermutungen sind widerlegt
                        ' (Gamma-Skala statt linear, Schwelle auf die staerksten Kanten). Solange sie
                        ' nur ein Neuntel entfernt, kostet sie Decode-Zeit fuer nichts Sichtbares.
                        ' Umschalten ist genau diese eine Konstante; die Pruefung ruft Convert16
                        ' direkt und haelt den Zweig lauffaehig.
                        Dim v As (Rot As Double, Blau As Double) = (1.0, 1.0)
                        If FarbquerfehlerAktiv Then v = SchaetzeFarbquerfehler(image + 16, width, height)
                        Dim ecke = Math.Sqrt(CDbl(width) * width + CDbl(height) * height) / 2.0
                        Dim vRot = If(Math.Abs(v.Rot - 1.0) * ecke >= FarbquerfehlerSchwelle, v.Rot, 1.0)
                        Dim vBlau = If(Math.Abs(v.Blau - 1.0) * ecke >= FarbquerfehlerSchwelle, v.Blau, 1.0)
                        If vRot <> 1.0 OrElse vBlau <> 1.0 Then
                            DiagnosticLogService.LogAlways("Raw.Farbquerfehler",
                                $"Rot {(v.Rot - 1.0) * ecke:F2} px, Blau {(v.Blau - 1.0) * ecke:F2} px an der Ecke")
                        End If
                        Convert16(image + 16, width, height, pixels, vRot, vBlau, grundEv)
                    Else
                        Dim rgb(dataSize - 1) As Byte
                        Marshal.Copy(image + 16, rgb, 0, dataSize)
                        For i = 0 To CInt(pixelCount - 1)
                            pixels(i * 4) = rgb(i * 3 + 2)      ' B
                            pixels(i * 4 + 1) = rgb(i * 3 + 1)  ' G
                            pixels(i * 4 + 2) = rgb(i * 3)      ' R
                            pixels(i * 4 + 3) = 255
                        Next
                    End If
                    Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length)
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
        Private Shared ReadOnly DitherSchwellen As Integer() = BaueDitherSchwellen()

        Private Shared Function BaueDitherSchwellen() As Integer()
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
            Dim schwellen = New Integer(63) {}
            For i = 0 To 63
                ' T = ((2*m+1)/128) * 65535: Schwellenmitte je Zelle, Mittelwert exakt 0,5 LSB.
                schwellen(i) = ((2 * m(i) + 1) * 65535) \ 128
            Next
            Return schwellen
        End Function

        ''' <summary>Packt LibRaws 16-Bit-Ausgabe nach Bgra8888 und quantisiert dabei mit
        ''' Bayer-Dither statt durch Abschneiden.
        '''
        ''' DERZEIT NICHT AKTIV (DecodeOutputBits = 8), aber lauffaehig gehalten - die Diagnose
        ''' ruft die Methode direkt auf.
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
        ''' des DNG-SDK), keine freien Parameter. Gefittet gegen Lightroom-Exporte OHNE Preset an
        ''' DREI Motiven mit angeglichener Geometrie; das Optimum liegt bei allen dreien auf
        ''' demselben Paar - also kamerafest, nicht motivabhaengig. Mittlerer |dRGB| zu Lightroom:
        ''' Basis gegen Basis 13,1/15,0/25,1 (Auto-Aufhellung) auf 5,4/5,4/15,5; durch die
        ''' unveraenderte Preset-Kette 16,4/21,5/15,7 auf 11,8/12,7/13,7.
        '''
        ''' MESSHINWEIS: Lightroom entzerrt standardmaessig mit dem Objektivprofil und skaliert
        ''' dabei um rund 2 %. Ohne Geometrie-Angleich misst ein Vergleich vor allem
        ''' Fehlregistrierung (WID_7643: 27,2 statt 15,9). FerrumPix baut die Objektivkorrektur
        ''' bewusst NICHT nach - siehe Audits/RAW_UND_FARBE.md.</summary>
        ''' <summary>FBDD-Stufe von LibRaw: 0 = aus, 1 = leicht, 2 = voll. Siehe Begruendung an der
        ''' Aufrufstelle - 2 ist beim Farbrauschen gemessen schlechter als 1.</summary>
        Private Const FbddRauschminderung As Integer = 1

        Private Const GrundbelichtungEv As Double = 0.5
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
                Dim neu2 = BaueTonTabelle(k / 1000.0)
                TonTabellen(k) = neu2
                Return neu2
            End SyncLock
        End Function

        ''' <summary>Tabelle LINEAR (0..65535) -> sRGB-kodiert (0..65535). Getrennt von der
        ''' Tonkurve, weil der mittlere Kanal ZWISCHEN den beiden anderen interpoliert wird - und
        ''' zwar linear, vor der Gamma-Kodierung.</summary>
        Private Shared ReadOnly GammaTabelle As Integer() = BaueGammaTabelle()

        Private Shared Function BaueTonTabelle(ev As Double) As Integer()
            Dim t(65535) As Integer
            Dim weiss = 1.0 / (2.0 ^ ev)
            Dim steigung = 1.0 / (weiss - SchwarzAbzug)
            ' Weicher Fuss wie im DNG-SDK: eine quadratische Anlaufstrecke um den Schwarzpunkt,
            ' sonst entstuende dort eine sichtbare Kante.
            Dim radius = Math.Min(0.5 * SchwarzAbzug, (1.0 / 16.0) / steigung)
            Dim qskala = If(radius > 0, steigung / (4.0 * radius), 0.0)
            For i = 0 To 65535
                Dim x = i / 65535.0
                Dim v As Double
                If x <= SchwarzAbzug - radius Then
                    v = 0.0
                ElseIf x >= SchwarzAbzug + radius Then
                    v = Math.Min((x - SchwarzAbzug) * steigung, 1.0)
                Else
                    v = qskala * (x - (SchwarzAbzug - radius)) ^ 2
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

        Private Shared Function BaueGammaTabelle() As Integer()
            Dim t(65535) As Integer
            For i = 0 To 65535
                Dim v = i / 65535.0
                Dim sg = If(v <= 0.0031308, v * 12.92, 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055)
                t(i) = CInt(Math.Round(Math.Max(0.0, Math.Min(1.0, sg)) * 65535.0))
            Next
            Return t
        End Function

        ''' <summary>16-Bit-LINEARE LibRaw-Ausgabe in 8-Bit-sRGB umsetzen: Belichtungsrampe,
        ''' ACR3-Tonkurve nach Adobes RGBTone-Regel, sRGB-Gamma, Bayer-Dither.
        '''
        ''' RGBTone heisst: die Kurve laeuft auf dem HELLSTEN und dem DUNKELSTEN Kanal, der
        ''' mittlere wird zwischen den beiden Ergebnissen interpoliert. Kanalweise angewandt
        ''' verschoebe die Kurve den Farbton - genau der Fehler, der Lichter ausbleichen laesst.
        '''
        ''' Der Dither benutzt DIESELBE Schwelle fuer alle drei Kanaele eines Pixels: kanalweise
        ''' verschiedene Schwellen faerben neutrale Flaechen ein.</summary>
        Private Shared Sub Convert16(data As IntPtr, width As Integer, height As Integer, pixels As Byte(),
                                     Optional vRot As Double = 1.0, Optional vBlau As Double = 1.0,
                                     Optional grundbelichtungEvWert As Double = GrundbelichtungEv)
            Dim schwellen = DitherSchwellen
            Dim ton = TonTabelleFuer(grundbelichtungEvWert)
            Dim gamma = GammaTabelle
            Dim rowBytes = width * 6

            ' Farbquerfehler: Rot und Blau sitzen radial verschoben. Korrigiert wird beim Umsetzen -
            ' ein eigener Durchgang ueber 20 MP waere ein zweites Mal Speicherbandbreite fuer nichts.
            ' Ein Feature, das an dieser Stelle sitzt, rechnet ausserdem im LINEAREN 16-Bit-Raum;
            ' nach der Gamma-Kodierung zu interpolieren zieht Kanten schief.
            Dim korrigiert = Math.Abs(vRot - 1.0) > 0.0000001 OrElse Math.Abs(vBlau - 1.0) > 0.0000001
            Dim cx = (width - 1) / 2.0, cy = (height - 1) / 2.0
            ' ACHTUNG Konvention: vRot/vBlau sind der GEMESSENE Fehler - der Faktor, mit dem der
            ' Kanal abgetastet erscheint. Korrigiert wird mit dem KEHRWERT. Multiplizieren statt
            ' dividieren verdoppelt den Farbsaum, statt ihn zu entfernen; genau daran ist die erste
            ' Fassung gescheitert, und am echten Bild war der Unterschied zu klein, um es zu merken.
            Dim kRot = 1.0 / vRot, kBlau = 1.0 / vBlau
            ' Die Abbildung ist SEPARIERBAR (x haengt nur von x ab), also einmal vorrechnen.
            Dim x0R(width - 1), x0B(width - 1) As Integer
            Dim fxR(width - 1), fxB(width - 1) As Double
            If korrigiert Then
                For x = 0 To width - 1
                    Dim sxr = Math.Min(Math.Max(cx + (x - cx) * kRot, 0.0), width - 1.001)
                    x0R(x) = CInt(Math.Floor(sxr)) : fxR(x) = sxr - x0R(x)
                    Dim sxb = Math.Min(Math.Max(cx + (x - cx) * kBlau, 0.0), width - 1.001)
                    x0B(x) = CInt(Math.Floor(sxb)) : fxB(x) = sxb - x0B(x)
                Next
            End If

            ' Zeilenring: Gruen kommt aus der eigenen Zeile, Rot und Blau aus benachbarten. Die
            ' Verschiebung ist klein, deshalb reichen wenige Zeilen - und es bleibt bei EINER
            ' Kopie je Quellzeile statt einer je Zugriff.
            Const RingGroesse = 8
            Dim ring(RingGroesse - 1)() As Short
            Dim ringZeile(RingGroesse - 1) As Integer
            For i = 0 To RingGroesse - 1
                ReDim ring(i)(width * 3 - 1) : ringZeile(i) = -1
            Next
            Dim HoleZeile = Function(zy As Integer) As Short()
                                Dim yy = Math.Min(Math.Max(zy, 0), height - 1)
                                Dim slot = yy Mod RingGroesse
                                If ringZeile(slot) <> yy Then
                                    Marshal.Copy(data + CLng(yy) * rowBytes, ring(slot), 0, width * 3)
                                    ringZeile(slot) = yy
                                End If
                                Return ring(slot)
                            End Function

            For y = 0 To height - 1
                Dim rowShorts = HoleZeile(y)
                Dim zR0 As Short() = Nothing, zR1 As Short() = Nothing
                Dim zB0 As Short() = Nothing, zB1 As Short() = Nothing
                Dim fyR = 0.0, fyB = 0.0
                If korrigiert Then
                    Dim syr = Math.Min(Math.Max(cy + (y - cy) * kRot, 0.0), height - 1.001)
                    Dim y0r = CInt(Math.Floor(syr)) : fyR = syr - y0r
                    zR0 = HoleZeile(y0r) : zR1 = HoleZeile(y0r + 1)
                    Dim syb = Math.Min(Math.Max(cy + (y - cy) * kBlau, 0.0), height - 1.001)
                    Dim y0b = CInt(Math.Floor(syb)) : fyB = syb - y0b
                    zB0 = HoleZeile(y0b) : zB1 = HoleZeile(y0b + 1)
                End If
                Dim d = y * width * 4
                Dim ditherRow = (y And 7) << 3
                For x = 0 To width - 1
                    ' And &HFFFF hebt die Short-Werte vorzeichenfrei nach Integer (VB hat kein
                    ' UShort-Marshalling ueber Marshal.Copy).
                    Dim g = rowShorts(x * 3 + 1) And &HFFFF
                    Dim r As Integer, b As Integer
                    If korrigiert Then
                        r = BilinearKanal(zR0, zR1, fyR, x0R(x), fxR(x), 0, width)
                        b = BilinearKanal(zB0, zB1, fyB, x0B(x), fxB(x), 2, width)
                    Else
                        r = rowShorts(x * 3) And &HFFFF
                        b = rowShorts(x * 3 + 2) And &HFFFF
                    End If
                    ' DIESELBE Schwelle fuer alle drei Kanaele eines Pixels (wie in der
                    ' Punktoperationskette): kanalweise verschiedene Schwellen faerben neutrale
                    ' Flaechen ein. (v*255 + T) \ 65535 liegt fuer T < 65535 immer in 0..255,
                    ' CByte kann nicht ueberlaufen.
                    Dim hoch = Math.Max(r, Math.Max(g, b))
                    Dim tief = Math.Min(r, Math.Min(g, b))
                    Dim tHoch = ton(hoch)
                    Dim tTief = ton(tief)
                    Dim rr As Integer, gg As Integer, bb As Integer
                    If hoch = tief Then
                        rr = tHoch : gg = tHoch : bb = tHoch
                    Else
                        Dim spanne = hoch - tief
                        Dim delta = tHoch - tTief
                        rr = tTief + CInt(CLng(delta) * (r - tief) \ spanne)
                        gg = tTief + CInt(CLng(delta) * (g - tief) \ spanne)
                        bb = tTief + CInt(CLng(delta) * (b - tief) \ spanne)
                    End If
                    Dim t = schwellen(ditherRow Or (x And 7))
                    pixels(d) = CByte((gamma(bb) * 255 + t) \ 65535)
                    pixels(d + 1) = CByte((gamma(gg) * 255 + t) \ 65535)
                    pixels(d + 2) = CByte((gamma(rr) * 255 + t) \ 65535)
                    pixels(d + 3) = 255
                    d += 4
                Next
            Next
        End Sub

        ''' <summary>Ein Kanal bilinear aus zwei Quellzeilen. Die Randspalte wird geklemmt statt
        ''' gespiegelt - beim Farbquerfehler geht es um Bruchteile eines Pixels, da ist Klemmen
        ''' unsichtbar und billiger.</summary>
        Private Shared Function BilinearKanal(z0 As Short(), z1 As Short(), fy As Double,
                                              x0 As Integer, fx As Double, kanal As Integer,
                                              width As Integer) As Integer
            Dim x1 = Math.Min(x0 + 1, width - 1)
            Dim o0 = x0 * 3 + kanal, o1 = x1 * 3 + kanal
            Dim oben = (z0(o0) And &HFFFF) * (1.0 - fx) + (z0(o1) And &HFFFF) * fx
            Dim unten = (z1(o0) And &HFFFF) * (1.0 - fx) + (z1(o1) And &HFFFF) * fx
            Dim v = oben * (1.0 - fy) + unten * fy
            Return CInt(Math.Min(Math.Max(v, 0.0), 65535.0))
        End Function

        ''' <summary>Unterhalb dieser Verschiebung an der Bildecke (in Pixeln) wird NICHT korrigiert:
        ''' das Umsampeln kostet Zeit und weicht Kanten minimal auf, ein Zehntelpixel Farbsaum sieht
        ''' dagegen niemand. Gute Objektive liegen darunter.</summary>
        Private Const FarbquerfehlerSchwelle As Double = 0.15

        ''' <summary>Solange die Schaetzung auf echten Fotos nur ein Neuntel des Farbsaums findet,
        ''' bleibt die Korrektur aus. Siehe Begruendung an der Aufrufstelle und in
        ''' Audits/OFFENE_PUNKTE.md.</summary>
        Private Const FarbquerfehlerAktiv As Boolean = False

        ''' <summary>Lateraler Farbquerfehler: Rot und Blau sind gegenueber Gruen radial verschoben,
        ''' und zwar LINEAR mit dem Abstand zur Bildmitte. Geschaetzt wird der Skalierungsfaktor je
        ''' Kanal aus dem Bild selbst — keine Objektivdatenbank, kein Modellabgleich, funktioniert
        ''' also auch mit Objektiven, die nirgends verzeichnet sind.
        '''
        ''' Verfahren: an radialen Kanten die Verschiebung nach Lucas-Kanade in EINER Richtung. Mit
        ''' d(r) = (v-1)*r folgt v-1 = Summe(gu*r*diff) / Summe(gu^2*r^2), gewichtet also von selbst
        ''' mit der Kantenstaerke — eine Schwelle braucht es nicht. Der Hochpass (Wert minus lokaler
        ''' Mittelwert) entfernt den Helligkeitsunterschied zwischen den Kanaelen; ohne ihn misst man
        ''' Farbe statt Versatz.</summary>
        Private Shared Function SchaetzeFarbquerfehler(data As IntPtr, width As Integer, height As Integer) _
                As (Rot As Double, Blau As Double)
            Const Rand = 3
            If width < 64 OrElse height < 64 Then Return (1.0, 1.0)
            ' Schrittweite nach Bildgroesse: ein 20-MP-RAW braucht keine 20 Mio. Stichproben, ein
            ' kleines Bild darf aber nicht unter die Mindestzahl fallen.
            Dim Schritt = Math.Max(2, Math.Min(8, width \ 400))

            Dim cx = (width - 1) / 2.0, cy = (height - 1) / 2.0
            Dim halbW = Math.Max(1.0, cx), halbH = Math.Max(1.0, cy)
            Dim rowBytes = width * 6
            ' Geschaetzt wird in der GAMMA-Skala, nicht linear. Linear haengt die Kantenamplitude
            ' an der Helligkeit: helle Kanten dominieren die Gewichtung, und der eine globale
            ' Amplitudenausgleich zwischen den Kanaelen passt dann nur fuer eine Helligkeitsklasse.
            ' Am echten Foto hat das die Schaetzung um den Faktor zehn zu klein gemacht (11 % des
            ' Fehlers entfernt statt 78 % im synthetischen Test) - am synthetischen Bild mit EINEM
            ' Helligkeitspaar faellt genau das nicht auf.
            Dim gam = GammaTabelle
            ' Fuenf Zeilen im Ring: der 5x5-Mittelwert braucht y-2..y+2.
            Dim ring(4)() As Short
            For i = 0 To 4 : ReDim ring(i)(width * 3 - 1) : Next
            Dim ringZeile(4) As Integer
            For i = 0 To 4 : ringZeile(i) = -1 : Next

            Dim HoleZeile = Function(y As Integer) As Short()
                                Dim slot = ((y Mod 5) + 5) Mod 5
                                If ringZeile(slot) <> y Then
                                    Marshal.Copy(data + CLng(y) * rowBytes, ring(slot), 0, width * 3)
                                    ringZeile(slot) = y
                                End If
                                Return ring(slot)
                            End Function

            ' Gesammelte Stichproben: Hochpasswerte, radiale Ableitung von Gruen, Radius.
            Dim kap = ((height - 2 * Rand) \ Schritt + 1) * ((width - 2 * Rand) \ Schritt + 1)
            Dim hg(kap - 1), hr(kap - 1), hb(kap - 1), gu(kap - 1), rad(kap - 1) As Double
            Dim n = 0

            For y = Rand To height - Rand - 1 Step Schritt
                Dim zm2 = HoleZeile(y - 2), zm1 = HoleZeile(y - 1), z0 = HoleZeile(y)
                Dim zp1 = HoleZeile(y + 1), zp2 = HoleZeile(y + 2)
                Dim dy = y - cy
                For x = Rand To width - Rand - 1 Step Schritt
                    Dim dx = x - cx
                    Dim rn = Math.Sqrt((dx / halbW) ^ 2 + (dy / halbH) ^ 2)
                    If rn < 0.3 Then Continue For      ' in der Mitte ist der Effekt per Definition 0
                    Dim r = Math.Sqrt(dx * dx + dy * dy)
                    Dim ux = dx / r, uy = dy / r

                    ' 5x5-Mittelwert je Kanal (dieselben 25 Bildpunkte fuer alle drei).
                    Dim sR = 0.0, sG = 0.0, sB = 0.0
                    For Each z In {zm2, zm1, z0, zp1, zp2}
                        For k = -2 To 2
                            Dim o = (x + k) * 3
                            sR += gam(z(o) And &HFFFF) : sG += gam(z(o + 1) And &HFFFF) : sB += gam(z(o + 2) And &HFFFF)
                        Next
                    Next
                    Dim o0 = x * 3
                    hr(n) = gam(z0(o0) And &HFFFF) - sR / 25.0
                    hg(n) = gam(z0(o0 + 1) And &HFFFF) - sG / 25.0
                    hb(n) = gam(z0(o0 + 2) And &HFFFF) - sB / 25.0
                    ' Ableitung von Gruen entlang der radialen Richtung. Der Hochpass aendert sie
                    ' kaum (der geglaettete Anteil variiert langsam), deshalb direkt am Rohwert.
                    Dim gx = (gam(z0((x + 1) * 3 + 1) And &HFFFF) - gam(z0((x - 1) * 3 + 1) And &HFFFF)) / 2.0
                    Dim gy = (gam(zp1(o0 + 1) And &HFFFF) - gam(zm1(o0 + 1) And &HFFFF)) / 2.0
                    gu(n) = gx * ux + gy * uy
                    rad(n) = r
                    n += 1
                Next
            Next
            If n < 1000 Then Return (1.0, 1.0)

            ' Kantenamplitude angleichen, sonst misst man den Helligkeitsunterschied der Kanaele.
            Dim eG = 0.0, eR = 0.0, eB = 0.0
            For i = 0 To n - 1
                eG += hg(i) * hg(i) : eR += hr(i) * hr(i) : eB += hb(i) * hb(i)
            Next
            Dim sr2 = If(eR > 0, Math.Sqrt(eG / eR), 1.0)
            Dim sb2 = If(eB > 0, Math.Sqrt(eG / eB), 1.0)

            ' NUR die staerksten radialen Kanten auswerten. An einer farbigen Flaeche ist die
            ' Differenz zwischen den Kanaelen Farbe, nicht Versatz - und sie haengt mit der
            ' Kantenrichtung zusammen, mittelt sich also NICHT heraus, sondern zieht die Schaetzung
            ' systematisch gegen null. Mit allen Punkten wurden am echten Foto nur 10 % des
            ' Farbsaums entfernt, mit dieser Schwelle deutlich mehr; am synthetischen Graubild
            ' faellt der Unterschied nicht auf, weil es dort keine Farbe gibt.
            Dim sortiert(n - 1) As Double
            For i = 0 To n - 1 : sortiert(i) = Math.Abs(gu(i)) : Next
            Array.Sort(sortiert)
            Dim schwelle = sortiert(CInt(Math.Floor((n - 1) * 0.9)))

            Dim numR = 0.0, numB = 0.0, den = 0.0
            For i = 0 To n - 1
                If Math.Abs(gu(i)) < schwelle Then Continue For
                Dim w = gu(i) * rad(i)
                numR += w * (hr(i) * sr2 - hg(i))
                numB += w * (hb(i) * sb2 - hg(i))
                den += w * w
            Next
            If den <= 0 Then Return (1.0, 1.0)
            Return (1.0 + numR / den, 1.0 + numB / den)
        End Function

        ''' <summary>Traegt die Datei ein bereits fertig gerendertes RGB-Bild statt Sensordaten?
        ''' Erkannt an PhotometricInterpretation = 2 (RGB). Ein echtes Sensor-RAW steht dort auf
        ''' 32803 (Farbfilter-Muster), ein lineares DNG auf 34892 - beide sind linear und gehoeren
        ''' durch unsere Basisstufe. Nur die RGB-Variante ist schon fertig.
        ''' Faellt das Lesen aus, gilt die Datei als normal - lieber die Basisstufe anwenden als
        ''' bei jedem unlesbaren Header darauf zu verzichten.</summary>
        Private Shared Function IstFertigGerendertesRgb(path As String) As Boolean
            Try
                If Not path.EndsWith(".dng", StringComparison.OrdinalIgnoreCase) Then Return False
                Dim verzeichnisse = MetadataExtractor.ImageMetadataReader.ReadMetadata(path)
                For Each d In verzeichnisse.OfType(Of MetadataExtractor.Formats.Exif.ExifDirectoryBase)()
                    ' Ausgeschrieben, weil MetadataExtractor das als Erweiterungsmethode anbietet
                    ' und diese Datei den Namensraum nicht importiert.
                    Dim wert As Integer
                    If MetadataExtractor.DirectoryExtensions.TryGetInt32(
                           d, MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagPhotometricInterpretation, wert) Then
                        ' Bei einer DNG-Huelle um ein RGB-Bild steht 2 im Hauptbild; ein Sensormuster
                        ' (32803) oder LinearRaw (34892) gaebe es dort gar nicht.
                        If wert = 2 Then Return True
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
        Private Shared Function GrundbelichtungFuerDatei(path As String) As Double
            Try
                If Not AppSettingsService.Load().UseCameraBaselineTable Then Return GrundbelichtungEv
                Dim verzeichnisse = MetadataExtractor.ImageMetadataReader.ReadMetadata(path)
                Dim ifd0 = verzeichnisse.OfType(Of MetadataExtractor.Formats.Exif.ExifIfd0Directory)().FirstOrDefault()
                If ifd0 Is Nothing Then Return GrundbelichtungEv
                Return CameraBaselineTable.GrundbelichtungFuer(
                    ifd0.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagMake),
                    ifd0.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagModel),
                    GrundbelichtungEv)
            Catch
                Return GrundbelichtungEv
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
