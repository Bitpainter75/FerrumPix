Imports System
Imports System.IO
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Liest HEIC/HEIF (und AVIF, sofern die Bibliothek den Codec mitbringt) über libheif -
    ''' SkiaSharp kann beides nicht. Damit lassen sich iPhone-Fotos direkt ansehen und bearbeiten,
    ''' statt sie vorher umwandeln zu müssen; geschrieben wird HEIC bewusst NICHT (siehe unten).
    '''
    ''' Geladen wird dynamisch über NativeLibrary.Load + Delegates, genau wie bei LibRaw: der
    ''' DllImport-Resolver der Assembly ist bereits belegt (nur EINER erlaubt), und so bleibt die
    ''' Verfügbarkeit sauber prüfbar. Fehlt libheif, meldet IsAvailable False und ALLES läuft wie
    ''' bisher - HEIC-Dateien erscheinen dann einfach nicht als Bild, kein Absturz.
    '''
    ''' NUR die Bibliothek des SYSTEMS, bewusst ohne mitgelieferten Rückfall (anders als LibRaw):
    ''' HEIC-Dateien sind in aller Regel HEVC-kodiert, und für HEVC bestehen Patentansprüche.
    ''' Die Entscheidung, einen HEVC-Dekoder auszuliefern, trifft damit die Distribution
    ''' (Arch/Debian/Fedora liefern libheif+libde265 in ihren Paketquellen), nicht FerrumPix.
    ''' Lizenzseitig ist alles unkritisch: libheif und libde265 stehen unter der LGPL-3 und werden
    ''' unverändert dynamisch geladen - dieselbe Konstruktion wie bei LibRaw und libmpv.
    '''
    ''' NUR LESEN: einen Encoder gibt es hier nicht. Bearbeitete HEIC-Dateien werden wie PSD als
    ''' neue Datei in einem schreibbaren Format gespeichert (ImageProcessor.CanEncodeToTargetExtension
    ''' weist ein HEIC-Ziel ab), damit keine Datei still unter falscher Endung landet.
    ''' </summary>
    Public NotInheritable Class HeifDecodeService

        Private Sub New()
        End Sub

        ' ── Native Bindung ───────────────────────────────────────────────────────

        Private Delegate Function ContextAllocFn() As IntPtr
        Private Delegate Sub ContextFreeFn(ctx As IntPtr)
        Private Delegate Function ReadFromFileFn(ctx As IntPtr, filename As IntPtr, options As IntPtr) As HeifError
        Private Delegate Function GetPrimaryHandleFn(ctx As IntPtr, ByRef handle As IntPtr) As HeifError
        Private Delegate Function DecodeImageFn(handle As IntPtr, ByRef img As IntPtr, colorspace As Integer, chroma As Integer, options As IntPtr) As HeifError
        Private Delegate Function GetPlaneFn(img As IntPtr, channel As Integer, ByRef stride As Integer) As IntPtr
        Private Delegate Function GetIntFn(obj As IntPtr, channel As Integer) As Integer
        Private Delegate Function HandleIntFn(handle As IntPtr) As Integer
        Private Delegate Sub ReleaseFn(obj As IntPtr)

        ''' <summary>libheifs Fehlerstruktur: code, subcode, message. Wird BY VALUE zurückgegeben,
        ''' deshalb als Struct und nicht als Zeiger.</summary>
        <StructLayout(LayoutKind.Sequential)>
        Private Structure HeifError
            Public Code As Integer
            Public Subcode As Integer
            Public Message As IntPtr
        End Structure

        ' heif_colorspace_RGB = 1; heif_chroma_interleaved_RGBA = 11; heif_channel_interleaved = 10.
        Private Const ColorspaceRgb As Integer = 1
        Private Const ChromaInterleavedRgba As Integer = 11
        Private Const ChannelInterleaved As Integer = 10

        Private Shared ReadOnly _initLock As New Object()
        ''' libheif ist nicht garantiert reentrant über einen Kontext hinweg; jeder Aufruf legt
        ''' zwar seinen eigenen Kontext an, die Thumbnail-Erzeugung ruft aber aus mehreren Threads
        ''' herein. Wie bei der nicht-reentranten LibRaw wird deshalb serialisiert - lieber
        ''' langsamere Thumbnails als sporadische Abstürze.
        Private Shared ReadOnly _nativeLock As New Object()

        Private Shared _initialized As Boolean
        Private Shared _library As IntPtr
        Private Shared _loadedLibrary As String

        Private Shared _contextAlloc As ContextAllocFn
        Private Shared _contextFree As ContextFreeFn
        Private Shared _readFromFile As ReadFromFileFn
        Private Shared _getPrimaryHandle As GetPrimaryHandleFn
        Private Shared _decodeImage As DecodeImageFn
        Private Shared _getPlane As GetPlaneFn
        Private Shared _imageGetWidth As GetIntFn
        Private Shared _imageGetHeight As GetIntFn
        Private Shared _handleGetWidth As HandleIntFn
        Private Shared _handleGetHeight As HandleIntFn
        Private Shared _handleRelease As ReleaseFn
        Private Shared _imageRelease As ReleaseFn

        ''' <summary>True, wenn libheif geladen werden konnte (Ergebnis wird gecacht).</summary>
        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                EnsureLoaded()
                Return _library <> IntPtr.Zero
            End Get
        End Property

        ''' <summary>Welche libheif-Variante geladen wurde - für die Diagnose und Feldberichte.</summary>
        Public Shared ReadOnly Property LoadedLibraryName As String
            Get
                EnsureLoaded()
                Return If(_loadedLibrary, "")
            End Get
        End Property

        Public Shared Function IsSupportedHeif(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Select Case IO.Path.GetExtension(filePath).ToLowerInvariant()
                Case ".heic", ".heif", ".hif", ".avif"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        Private Shared Sub EnsureLoaded()
            SyncLock _initLock
                If _initialized Then Return
                _initialized = True

                Dim candidates As String()
                If OperatingSystem.IsWindows() Then
                    candidates = {"libheif.dll", "heif.dll"}
                ElseIf OperatingSystem.IsMacOS() Then
                    candidates = {"libheif.dylib", "libheif.1.dylib"}
                Else
                    ' Sonamen der verbreiteten Versionen; das nackte libheif.so existiert nur mit
                    ' Dev-Paket. Kein mitgelieferter Rückfall - siehe Klassenkommentar (HEVC).
                    candidates = {"libheif.so.1", "libheif.so"}
                End If

                Dim handle As IntPtr
                For Each candidate In candidates
                    If NativeLibrary.TryLoad(candidate, handle) Then
                        _loadedLibrary = candidate
                        Exit For
                    End If
                    handle = IntPtr.Zero
                Next
                If handle = IntPtr.Zero Then Return

                Try
                    _contextAlloc = GetExport(Of ContextAllocFn)(handle, "heif_context_alloc")
                    _contextFree = GetExport(Of ContextFreeFn)(handle, "heif_context_free")
                    _readFromFile = GetExport(Of ReadFromFileFn)(handle, "heif_context_read_from_file")
                    _getPrimaryHandle = GetExport(Of GetPrimaryHandleFn)(handle, "heif_context_get_primary_image_handle")
                    _decodeImage = GetExport(Of DecodeImageFn)(handle, "heif_decode_image")
                    _getPlane = GetExport(Of GetPlaneFn)(handle, "heif_image_get_plane_readonly")
                    _imageGetWidth = GetExport(Of GetIntFn)(handle, "heif_image_get_width")
                    _imageGetHeight = GetExport(Of GetIntFn)(handle, "heif_image_get_height")
                    _handleGetWidth = GetExport(Of HandleIntFn)(handle, "heif_image_handle_get_width")
                    _handleGetHeight = GetExport(Of HandleIntFn)(handle, "heif_image_handle_get_height")
                    _handleRelease = GetExport(Of ReleaseFn)(handle, "heif_image_handle_release")
                    _imageRelease = GetExport(Of ReleaseFn)(handle, "heif_image_release")
                    _library = handle
                Catch
                    ' Ein fehlender Export = Bibliothek unbrauchbar; alles auf Anfang.
                    _contextAlloc = Nothing : _contextFree = Nothing : _readFromFile = Nothing
                    _getPrimaryHandle = Nothing : _decodeImage = Nothing : _getPlane = Nothing
                    _imageGetWidth = Nothing : _imageGetHeight = Nothing
                    _handleGetWidth = Nothing : _handleGetHeight = Nothing
                    _handleRelease = Nothing : _imageRelease = Nothing
                    NativeLibrary.Free(handle)
                    _library = IntPtr.Zero
                    _loadedLibrary = Nothing
                End Try
            End SyncLock
        End Sub

        Private Shared Function GetExport(Of T)(handle As IntPtr, name As String) As T
            Return Marshal.GetDelegateForFunctionPointer(Of T)(NativeLibrary.GetExport(handle, name))
        End Function

        ''' <summary>Dekodiert die Hauptaufnahme der Datei als Bgra8888 (Besitz beim Aufrufer) oder
        ''' Nothing. Drehung/Spiegelung aus dem Container wendet libheif selbst an (die
        ''' Standard-Dekodieroptionen lassen Transformationen nicht aus) - es braucht also KEINE
        ''' zusätzliche Orientierungskorrektur, anders als beim eingebetteten RAW-Vorschaubild.</summary>
        Public Shared Function TryDecode(path As String) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            SyncLock _nativeLock
                Return DecodeCore(path)
            End SyncLock
        End Function

        Private Shared Function DecodeCore(path As String) As SKBitmap
            Dim ctx As IntPtr = IntPtr.Zero
            Dim handle As IntPtr = IntPtr.Zero
            Dim img As IntPtr = IntPtr.Zero
            Dim pathPtr As IntPtr = IntPtr.Zero
            Try
                ctx = _contextAlloc()
                If ctx = IntPtr.Zero Then Return Nothing

                pathPtr = StringToUtf8(path)
                If _readFromFile(ctx, pathPtr, IntPtr.Zero).Code <> 0 Then Return Nothing
                If _getPrimaryHandle(ctx, handle).Code <> 0 OrElse handle = IntPtr.Zero Then Return Nothing

                ' IMMER mit Alphakanal dekodieren: das kostet bei undurchsichtigen Bildern nur den
                ' vierten Kanal, erspart aber einen zweiten Zweig - und HEIC kann Transparenz.
                If _decodeImage(handle, img, ColorspaceRgb, ChromaInterleavedRgba, IntPtr.Zero).Code <> 0 OrElse img = IntPtr.Zero Then Return Nothing

                Dim width = _imageGetWidth(img, ChannelInterleaved)
                Dim height = _imageGetHeight(img, ChannelInterleaved)
                If width <= 0 OrElse height <= 0 Then Return Nothing
                If CLng(width) * CLng(height) > Integer.MaxValue \ 4 Then Return Nothing

                Dim stride = 0
                Dim plane = _getPlane(img, ChannelInterleaved, stride)
                If plane = IntPtr.Zero OrElse stride < width * 4 Then Return Nothing

                ' libheif liefert R,G,B,A - Skia erwartet bei Bgra8888 B,G,R,A. Zeilenweise
                ' umsortieren; der native Puffer hat eine eigene Schrittweite (Zeilenpolster).
                Dim bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul))
                Try
                    Dim rowBytes = width * 4
                    Dim row(rowBytes - 1) As Byte
                    Dim target = bitmap.GetPixels()
                    Dim targetStride = bitmap.RowBytes
                    ' Der Versatz wird bewusst in Integer gerechnet und NICHT in Long: IntPtr
                    ' addiert nur Integer, ein Long wuerde ohnehin wieder verengt - das frueher
                    ' hier stehende CLng sah nach 64-Bit-Sicherheit aus, ohne welche zu geben.
                    ' So wirft eine Ueberschreitung sichtbar, statt still umzulaufen; verhindert
                    ' wird sie durch die Schranke weiter oben (Integer.MaxValue \ 4).
                    For y = 0 To height - 1
                        Marshal.Copy(plane + y * stride, row, 0, rowBytes)
                        For x = 0 To rowBytes - 4 Step 4
                            Dim r = row(x)
                            row(x) = row(x + 2)
                            row(x + 2) = r
                        Next
                        Marshal.Copy(row, 0, target + y * targetStride, rowBytes)
                    Next
                    Return bitmap
                Catch
                    bitmap.Dispose()
                    Return Nothing
                End Try
            Catch
                Return Nothing
            Finally
                If img <> IntPtr.Zero Then _imageRelease(img)
                If handle <> IntPtr.Zero Then _handleRelease(handle)
                If ctx <> IntPtr.Zero Then _contextFree(ctx)
                If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
            End Try
        End Function

        ''' <summary>Die Datei als PNG-Strom - das ist die Form, die OpenSourceStream und die
        ''' Thumbnail-Erzeugung erwarten (gleiches Muster wie PSD/ICO).</summary>
        Public Shared Function ExtractPreview(path As String) As MemoryStream
            Using bmp = TryDecode(path)
                If bmp Is Nothing Then Return Nothing
                Using image = SKImage.FromBitmap(bmp)
                    If image Is Nothing Then Return Nothing
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

        ''' <summary>Maße ohne vollständiges Dekodieren - die Kopfdaten reichen dafür.</summary>
        Public Shared Function TryGetSize(path As String) As (Width As Integer, Height As Integer)
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return (0, 0)
            SyncLock _nativeLock
                Dim ctx As IntPtr = IntPtr.Zero
                Dim handle As IntPtr = IntPtr.Zero
                Dim pathPtr As IntPtr = IntPtr.Zero
                Try
                    ctx = _contextAlloc()
                    If ctx = IntPtr.Zero Then Return (0, 0)
                    pathPtr = StringToUtf8(path)
                    If _readFromFile(ctx, pathPtr, IntPtr.Zero).Code <> 0 Then Return (0, 0)
                    If _getPrimaryHandle(ctx, handle).Code <> 0 OrElse handle = IntPtr.Zero Then Return (0, 0)
                    Return (_handleGetWidth(handle), _handleGetHeight(handle))
                Catch
                    Return (0, 0)
                Finally
                    If handle <> IntPtr.Zero Then _handleRelease(handle)
                    If ctx <> IntPtr.Zero Then _contextFree(ctx)
                    If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
                End Try
            End SyncLock
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
