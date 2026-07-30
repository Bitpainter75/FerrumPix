Imports System
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Reads HEIC/HEIF and AVIF without teaching callers which platform decoder is available.
    ''' macOS uses the built-in ImageIO frameworks. Other platforms retain the optional system
    ''' libheif loader, so FerrumPix does not distribute an HEVC decoder or change codec policy.
    '''
    ''' The non-macOS backend loads libheif dynamically through NativeLibrary and delegates. The
    ''' assembly already has a DllImport resolver, and only one can be registered, while explicit
    ''' loading also keeps decoder availability testable and optional.
    '''
    ''' Only the system libheif is considered; there is deliberately no bundled fallback. HEIF
    ''' images commonly use HEVC, so the decision to provide an HEVC decoder remains with the
    ''' operating system or distribution rather than FerrumPix. The library is used unmodified
    ''' and dynamically, following the same replaceable-system-library model as LibRaw and libmpv.
    '''
    ''' This module is intentionally read-only. Edited HEIF files are saved to an encodable format
    ''' instead of writing different data under the original extension. ImageProcessor rejects a
    ''' HEIF target so an edited image can never be silently written with mismatched file contents.
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

        ''' <summary>True when the platform ImageIO or optional libheif decoder is available.</summary>
        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                If OperatingSystem.IsMacOS() Then
                    Return CoreFoundationHandle <> IntPtr.Zero AndAlso ImageIoHandle <> IntPtr.Zero
                End If
                EnsureLoaded()
                Return _library <> IntPtr.Zero
            End Get
        End Property

        ''' <summary>The active platform decoder name, for diagnostics and field reports.</summary>
        Public Shared ReadOnly Property LoadedLibraryName As String
            Get
                If OperatingSystem.IsMacOS() AndAlso IsAvailable Then Return "ImageIO"
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

        ''' <summary>Decodes the oriented primary image as caller-owned Bgra8888 pixels.
        ''' ImageIO applies the container transform through kCGImageSourceCreateThumbnailWithTransform;
        ''' libheif applies the container transformations through its default decoding options.
        ''' Callers must therefore not apply an additional orientation correction.</summary>
        Public Shared Function TryDecode(path As String) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            If OperatingSystem.IsMacOS() Then
                Using preview = ExtractWithMacImageIo(path)
                    Return If(preview IsNot Nothing, SKBitmap.Decode(preview), Nothing)
                End Using
            End If
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
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            If OperatingSystem.IsMacOS() Then Return ExtractWithMacImageIo(path)

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
            If OperatingSystem.IsMacOS() Then Return TryGetSizeWithMacImageIo(path)

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

        Private Shared Function TryGetSizeWithMacImageIo(path As String) As (Width As Integer, Height As Integer)
            Dim url As IntPtr = IntPtr.Zero
            Dim source As IntPtr = IntPtr.Zero
            Dim properties As IntPtr = IntPtr.Zero
            Try
                url = CreateFileUrl(path)
                If url = IntPtr.Zero Then Return (0, 0)
                source = CGImageSourceCreateWithURL(url, IntPtr.Zero)
                If source = IntPtr.Zero Then Return (0, 0)
                properties = CGImageSourceCopyPropertiesAtIndex(source, UIntPtr.Zero, IntPtr.Zero)
                If properties = IntPtr.Zero Then Return (0, 0)

                Dim width = ReadDictionaryInteger(
                    properties, ReadFrameworkConstant(ImageIoHandle, "kCGImagePropertyPixelWidth"))
                Dim height = ReadDictionaryInteger(
                    properties, ReadFrameworkConstant(ImageIoHandle, "kCGImagePropertyPixelHeight"))
                Dim orientation = ReadDictionaryInteger(
                    properties, ReadFrameworkConstant(ImageIoHandle, "kCGImagePropertyOrientation"))
                If orientation >= 5 AndAlso orientation <= 8 Then
                    Dim swap = width
                    width = height
                    height = swap
                End If
                Return (width, height)
            Catch
                Return (0, 0)
            Finally
                Release(properties)
                Release(source)
                Release(url)
            End Try
        End Function

        Private Shared Function ExtractWithMacImageIo(path As String) As MemoryStream
            Dim url As IntPtr = IntPtr.Zero
            Dim source As IntPtr = IntPtr.Zero
            Dim options As IntPtr = IntPtr.Zero
            Dim maxPixelNumber As IntPtr = IntPtr.Zero
            Dim image As IntPtr = IntPtr.Zero
            Dim data As IntPtr = IntPtr.Zero
            Dim pngType As IntPtr = IntPtr.Zero
            Dim destination As IntPtr = IntPtr.Zero

            Try
                url = CreateFileUrl(path)
                If url = IntPtr.Zero Then Return Nothing
                source = CGImageSourceCreateWithURL(url, IntPtr.Zero)
                If source = IntPtr.Zero Then Return Nothing

                Dim maximum = Integer.MaxValue
                maxPixelNumber = CFNumberCreate(IntPtr.Zero, CFNumberSInt32Type, maximum)
                If maxPixelNumber = IntPtr.Zero Then Return Nothing

                Dim keys = {
                    ReadFrameworkConstant(ImageIoHandle, "kCGImageSourceCreateThumbnailFromImageAlways"),
                    ReadFrameworkConstant(ImageIoHandle, "kCGImageSourceCreateThumbnailWithTransform"),
                    ReadFrameworkConstant(ImageIoHandle, "kCGImageSourceThumbnailMaxPixelSize")
                }
                Dim values = {
                    ReadFrameworkConstant(CoreFoundationHandle, "kCFBooleanTrue"),
                    ReadFrameworkConstant(CoreFoundationHandle, "kCFBooleanTrue"),
                    maxPixelNumber
                }
                If keys.Any(Function(value) value = IntPtr.Zero) OrElse
                   values.Any(Function(value) value = IntPtr.Zero) Then Return Nothing

                options = CFDictionaryCreate(
                    IntPtr.Zero, keys, values, CType(keys.Length, UIntPtr), IntPtr.Zero, IntPtr.Zero)
                If options = IntPtr.Zero Then Return Nothing
                image = CGImageSourceCreateThumbnailAtIndex(source, UIntPtr.Zero, options)
                If image = IntPtr.Zero Then Return Nothing

                data = CFDataCreateMutable(IntPtr.Zero, UIntPtr.Zero)
                pngType = CFStringCreateWithCString(IntPtr.Zero, "public.png", CFStringEncodingUtf8)
                If data = IntPtr.Zero OrElse pngType = IntPtr.Zero Then Return Nothing
                destination = CGImageDestinationCreateWithData(data, pngType, CType(1, UIntPtr), IntPtr.Zero)
                If destination = IntPtr.Zero Then Return Nothing

                CGImageDestinationAddImage(destination, image, IntPtr.Zero)
                If CGImageDestinationFinalize(destination) = 0 Then Return Nothing

                Dim length = CFDataGetLength(data)
                Dim bytesPointer = CFDataGetBytePtr(data)
                If length <= 0 OrElse bytesPointer = IntPtr.Zero OrElse length > Integer.MaxValue Then Return Nothing

                Dim bytes(CInt(length) - 1) As Byte
                Marshal.Copy(bytesPointer, bytes, 0, bytes.Length)
                Return New MemoryStream(bytes, writable:=False)
            Catch
                Return Nothing
            Finally
                Release(destination)
                Release(pngType)
                Release(data)
                Release(image)
                Release(options)
                Release(maxPixelNumber)
                Release(source)
                Release(url)
            End Try
        End Function

        Private Shared Function CreateFileUrl(path As String) As IntPtr
            Dim pathBytes = Text.Encoding.UTF8.GetBytes(path)
            Return CFURLCreateFromFileSystemRepresentation(
                IntPtr.Zero, pathBytes, CType(pathBytes.Length, UIntPtr), False)
        End Function

        Private Shared Function ReadDictionaryInteger(dictionary As IntPtr, key As IntPtr) As Integer
            If dictionary = IntPtr.Zero OrElse key = IntPtr.Zero Then Return 0
            Dim number = CFDictionaryGetValue(dictionary, key)
            If number = IntPtr.Zero Then Return 0
            Dim value As Integer
            Return If(CFNumberGetValue(number, CFNumberSInt32Type, value) <> 0, value, 0)
        End Function

        Private Shared Function ReadFrameworkConstant(handle As IntPtr, name As String) As IntPtr
            If handle = IntPtr.Zero Then Return IntPtr.Zero
            Dim symbol As IntPtr
            If Not NativeLibrary.TryGetExport(handle, name, symbol) Then Return IntPtr.Zero
            Return Marshal.ReadIntPtr(symbol)
        End Function

        Private Shared Function LoadFrameworkIfAvailable(path As String) As IntPtr
            If Not OperatingSystem.IsMacOS() Then Return IntPtr.Zero
            Try
                Return NativeLibrary.Load(path)
            Catch
                Return IntPtr.Zero
            End Try
        End Function

        Private Shared Sub Release(value As IntPtr)
            If value <> IntPtr.Zero Then CFRelease(value)
        End Sub

        Private Shared Function StringToUtf8(value As String) As IntPtr
            Dim bytes = Text.Encoding.UTF8.GetBytes(value)
            Dim ptr = Marshal.AllocCoTaskMem(bytes.Length + 1)
            Marshal.Copy(bytes, 0, ptr, bytes.Length)
            Marshal.WriteByte(ptr, bytes.Length, 0)
            Return ptr
        End Function

        Private Const CoreFoundationFramework As String =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"
        Private Const ImageIoFramework As String =
            "/System/Library/Frameworks/ImageIO.framework/ImageIO"
        Private Shared ReadOnly CoreFoundationHandle As IntPtr =
            LoadFrameworkIfAvailable(CoreFoundationFramework)
        Private Shared ReadOnly ImageIoHandle As IntPtr =
            LoadFrameworkIfAvailable(ImageIoFramework)
        Private Const CFNumberSInt32Type As Integer = 3
        Private Const CFStringEncodingUtf8 As UInteger = &H8000100UI

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFURLCreateFromFileSystemRepresentation(
            allocator As IntPtr,
            buffer As Byte(),
            bufferLength As UIntPtr,
            <MarshalAs(UnmanagedType.I1)> isDirectory As Boolean) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFNumberCreate(
            allocator As IntPtr,
            numberType As Integer,
            ByRef value As Integer) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFDictionaryCreate(
            allocator As IntPtr,
            keys As IntPtr(),
            values As IntPtr(),
            count As UIntPtr,
            keyCallbacks As IntPtr,
            valueCallbacks As IntPtr) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFDictionaryGetValue(dictionary As IntPtr, key As IntPtr) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFNumberGetValue(
            number As IntPtr,
            numberType As Integer,
            ByRef value As Integer) As Integer
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFDataCreateMutable(
            allocator As IntPtr,
            capacity As UIntPtr) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFStringCreateWithCString(
            allocator As IntPtr,
            value As String,
            encoding As UInteger) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFDataGetLength(data As IntPtr) As Long
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFDataGetBytePtr(data As IntPtr) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Sub CFRelease(value As IntPtr)
        End Sub

        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGImageSourceCreateWithURL(url As IntPtr, options As IntPtr) As IntPtr
        End Function

        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGImageSourceCopyPropertiesAtIndex(
            source As IntPtr,
            index As UIntPtr,
            options As IntPtr) As IntPtr
        End Function

        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGImageSourceCreateThumbnailAtIndex(
            source As IntPtr,
            index As UIntPtr,
            options As IntPtr) As IntPtr
        End Function

        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGImageDestinationCreateWithData(
            data As IntPtr,
            type As IntPtr,
            count As UIntPtr,
            options As IntPtr) As IntPtr
        End Function

        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Sub CGImageDestinationAddImage(
            destination As IntPtr,
            image As IntPtr,
            properties As IntPtr)
        End Sub

        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGImageDestinationFinalize(destination As IntPtr) As Integer
        End Function

    End Class

End Namespace
