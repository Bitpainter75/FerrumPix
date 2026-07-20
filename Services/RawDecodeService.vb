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
                    _process = GetExport(Of IntFn)(handle, "libraw_dcraw_process")
                    _unpackThumb = GetExport(Of IntFn)(handle, "libraw_unpack_thumb")
                    _makeMemThumb = GetExport(Of MakeMemImageFn)(handle, "libraw_dcraw_make_mem_thumb")
                    _makeMemImage = GetExport(Of MakeMemImageFn)(handle, "libraw_dcraw_make_mem_image")
                    _clearMem = GetExport(Of PtrFn)(handle, "libraw_dcraw_clear_mem")
                    _close = GetExport(Of PtrFn)(handle, "libraw_close")
                    _library = handle
                Catch
                    ' Ein fehlender Export = Bibliothek unbrauchbar; alles auf Anfang.
                    _init = Nothing : _openFile = Nothing : _unpack = Nothing
                    _setOutputBps = Nothing : _setOutputColor = Nothing
                    _getCamMul = Nothing : _setUserMul = Nothing
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
                        If width <= 0 OrElse height <= 0 OrElse dataSize < width * height * 3 Then Return Nothing
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
            Using bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque))
                Dim pixels(width * height * 4 - 1) As Byte
                For i = 0 To width * height - 1
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
        Private Shared _cachedBitmap As SKBitmap

        ''' <summary>Voll aufgelöster, fertig entwickelter Decode (Besitz beim Aufrufer) oder Nothing.
        ''' Orientierung ist bereits angewandt (libraw dreht nach dem Kamera-Flip).</summary>
        Public Shared Function TryDecode(path As String) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            Try
                Dim writeTime = File.GetLastWriteTimeUtc(path)
                SyncLock _cacheLock
                    If _cachedBitmap IsNot Nothing AndAlso
                       String.Equals(_cachedPath, path, StringComparison.Ordinal) AndAlso
                       _cachedWriteTimeUtc = writeTime Then
                        Return _cachedBitmap.Copy()
                    End If
                End SyncLock

                ' Ohne reentrante libraw laufen die nativen Aufrufe nacheinander - die
                ' Thumbnail-Erzeugung ruft aus mehreren Threads hier herein (Parallel.For).
                Dim decoded As SKBitmap
                If _reentrant Then
                    decoded = DecodeCore(path)
                Else
                    SyncLock _nativeLock
                        decoded = DecodeCore(path)
                    End SyncLock
                End If
                If decoded Is Nothing Then Return Nothing

                SyncLock _cacheLock
                    _cachedBitmap?.Dispose()
                    _cachedBitmap = decoded
                    _cachedPath = path
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
            End SyncLock
        End Sub

        Private Shared Function DecodeCore(path As String) As SKBitmap
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

                _setOutputBps(handle, 8)
                _setOutputColor(handle, 1) ' sRGB
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
                If imageType <> 2 OrElse colors <> 3 OrElse bits <> 8 Then Return Nothing ' 2 = Bitmap
                If width <= 0 OrElse height <= 0 OrElse dataSize < width * height * 3 Then Return Nothing

                Dim rgb(dataSize - 1) As Byte
                Marshal.Copy(image + 16, rgb, 0, dataSize)

                Dim bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque))
                Try
                    Dim pixels(width * height * 4 - 1) As Byte
                    For i = 0 To width * height - 1
                        pixels(i * 4) = rgb(i * 3 + 2)      ' B
                        pixels(i * 4 + 1) = rgb(i * 3 + 1)  ' G
                        pixels(i * 4 + 2) = rgb(i * 3)      ' R
                        pixels(i * 4 + 3) = 255
                    Next
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

        Private Shared Function StringToUtf8(value As String) As IntPtr
            Dim bytes = Text.Encoding.UTF8.GetBytes(value)
            Dim ptr = Marshal.AllocCoTaskMem(bytes.Length + 1)
            Marshal.Copy(bytes, 0, ptr, bytes.Length)
            Marshal.WriteByte(ptr, bytes.Length, 0)
            Return ptr
        End Function

    End Class

End Namespace
