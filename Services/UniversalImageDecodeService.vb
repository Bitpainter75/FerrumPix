Imports System
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports NetVips

Namespace Services

    ''' <summary>
    ''' Einheitlicher Fallback-Decoder fuer Rasterformate, die Avalonia/Skia nicht selbst lesen.
    ''' Aufrufer muessen weder libvips-Loader noch deren optionale Codec-Abhaengigkeiten kennen:
    ''' ein erfolgreicher Aufruf liefert immer einen orientierten PNG-Strom, andernfalls Nothing.
    '''
    ''' RAW, PSD/PSB und FPX bleiben bewusst ausserhalb dieses Moduls. Sie besitzen in FerrumPix
    ''' eigene Semantik (Entwicklung/Sidecar, Ebenen/Komposit beziehungsweise eigener Container)
    ''' und sind deshalb keine austauschbaren allgemeinen Rasterformate.
    ''' </summary>
    Public NotInheritable Class UniversalImageDecodeService

        Private Shared ReadOnly SupportedExtensions As String() = {
            ".heic", ".heif", ".hif", ".avif",
            ".tif", ".tiff",
            ".jp2", ".j2k", ".jxl", ".mpo",
            ".svg"
        }

        Private Shared ReadOnly MacImageIoExtensions As String() = {
            ".heic", ".heif", ".hif", ".avif",
            ".jp2", ".j2k", ".jxl", ".mpo"
        }

        Private Shared ReadOnly MacHeifExtensions As String() = {
            ".heic", ".heif", ".hif", ".avif"
        }

        Private Sub New()
        End Sub

        Public Shared Function IsSupported(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Return SupportedExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase)
        End Function

        ''' <summary>True, wenn mindestens einer der internen Decoder geladen werden kann.</summary>
        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                If OperatingSystem.IsMacOS() Then Return True
                Try
                    Return NetVips.NetVips.AtLeastLibvips(8, 18)
                Catch
                    Return False
                End Try
            End Get
        End Property

        ''' <summary>Dekodiert die erste Seite/das Primaerbild und korrigiert EXIF-Orientierung.
        ''' maxDimension begrenzt nur bei Bedarf die laengste Kante; 0 behaelt die Originalgroesse.</summary>
        Public Shared Function ExtractPreview(filePath As String, Optional maxDimension As Integer = 0) As MemoryStream
            If Not IsSupported(filePath) OrElse Not File.Exists(filePath) Then Return Nothing

            ' Apples ImageIO ist auf macOS der verlässlichste eingebaute HEIF-Decoder. Die
            ' vorgefertigte libvips-Laufzeit kennt zwar den HEIF-Loader, dessen Codec-Plug-in
            ' lehnt jedoch manche gültigen iPhone-Dateien ab. Für alle anderen Plattformen
            ' und Formate bleibt libvips der gebündelte, einheitliche Decoder.
            If OperatingSystem.IsMacOS() AndAlso IsMacImageIoSupported(filePath) Then
                Dim imageIoPreview = ExtractWithMacImageIo(filePath, maxDimension)
                If imageIoPreview IsNot Nothing Then Return imageIoPreview
                ' Der gebündelte HEIF-Loader kann genau die iPhone-Dateien, die ImageIO ablehnt,
                ' sehr lange blockieren. Im Immich-Viewer verhindert das außerdem den vorhandenen
                ' Server-Preview-Fallback. Für HEIF deshalb sofort "nicht lokal dekodierbar"
                ' melden; andere ImageIO-Formate dürfen weiterhin libvips versuchen.
                If MacHeifExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase) Then
                    Return Nothing
                End If
            End If

            Try
                If Not NetVips.NetVips.AtLeastLibvips(8, 18) Then Return Nothing
                Using source = Image.NewFromFile(filePath, access:=Enums.Access.Sequential, failOn:=Enums.FailOn.Error)
                    Using oriented = source.Autorot()
                        Dim output As Image = oriented
                        Dim resized As Image = Nothing
                        Try
                            If maxDimension > 0 Then
                                Dim longest = Math.Max(oriented.Width, oriented.Height)
                                If longest > maxDimension Then
                                    resized = oriented.Resize(CDbl(maxDimension) / longest)
                                    output = resized
                                End If
                            End If

                            Dim bytes = output.WriteToBuffer(".png")
                            If bytes Is Nothing OrElse bytes.Length = 0 Then Return Nothing
                            Return New MemoryStream(bytes, writable:=False)
                        Finally
                            resized?.Dispose()
                        End Try
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("UniversalImageDecode", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Liest die orientierten Abmessungen möglichst nur aus dem Header.</summary>
        Public Shared Function TryGetSize(filePath As String) As (Width As Integer, Height As Integer)
            If Not IsSupported(filePath) OrElse Not File.Exists(filePath) Then Return (0, 0)
            If OperatingSystem.IsMacOS() AndAlso IsMacImageIoSupported(filePath) Then
                Dim imageIoSize = TryGetSizeWithMacImageIo(filePath)
                If imageIoSize.Width > 0 AndAlso imageIoSize.Height > 0 Then Return imageIoSize
            End If
            Try
                If NetVips.NetVips.AtLeastLibvips(8, 18) Then
                    Using source = Image.NewFromFile(filePath, access:=Enums.Access.Sequential, failOn:=Enums.FailOn.Error)
                        Using oriented = source.Autorot()
                            Return (oriented.Width, oriented.Height)
                        End Using
                    End Using
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("UniversalImageDecode.Size", ex)
            End Try

            Using preview = ExtractPreview(filePath)
                If preview Is Nothing Then Return (0, 0)
                Try
                    Using data = SkiaSharp.SKData.CreateCopy(preview.ToArray())
                        Using codec = SkiaSharp.SKCodec.Create(data)
                            If codec IsNot Nothing Then Return (codec.Info.Width, codec.Info.Height)
                        End Using
                    End Using
                Catch
                End Try
            End Using
            Return (0, 0)
        End Function

        Private Shared Function TryGetSizeWithMacImageIo(filePath As String) As (Width As Integer, Height As Integer)
            Dim url As IntPtr = IntPtr.Zero
            Dim source As IntPtr = IntPtr.Zero
            Dim properties As IntPtr = IntPtr.Zero
            Try
                Dim pathBytes = Text.Encoding.UTF8.GetBytes(filePath)
                url = CFURLCreateFromFileSystemRepresentation(
                    IntPtr.Zero, pathBytes, CType(pathBytes.Length, UIntPtr), False)
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
            Catch ex As Exception
                DiagnosticLogService.LogException("UniversalImageDecode.ImageIOSize", ex)
                Return (0, 0)
            Finally
                Release(properties)
                Release(source)
                Release(url)
            End Try
        End Function

        Private Shared Function ReadDictionaryInteger(dictionary As IntPtr, key As IntPtr) As Integer
            If dictionary = IntPtr.Zero OrElse key = IntPtr.Zero Then Return 0
            Dim number = CFDictionaryGetValue(dictionary, key)
            If number = IntPtr.Zero Then Return 0
            Dim value As Integer
            Return If(CFNumberGetValue(number, CFNumberSInt32Type, value) <> 0, value, 0)
        End Function

        Private Shared Function IsMacImageIoSupported(filePath As String) As Boolean
            Return MacImageIoExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase)
        End Function

        Private Shared Function ExtractWithMacImageIo(filePath As String, maxDimension As Integer) As MemoryStream
            Dim url As IntPtr = IntPtr.Zero
            Dim source As IntPtr = IntPtr.Zero
            Dim options As IntPtr = IntPtr.Zero
            Dim maxPixelNumber As IntPtr = IntPtr.Zero
            Dim image As IntPtr = IntPtr.Zero
            Dim data As IntPtr = IntPtr.Zero
            Dim pngType As IntPtr = IntPtr.Zero
            Dim destination As IntPtr = IntPtr.Zero

            Try
                Dim pathBytes = Text.Encoding.UTF8.GetBytes(filePath)
                url = CFURLCreateFromFileSystemRepresentation(
                    IntPtr.Zero, pathBytes, CType(pathBytes.Length, UIntPtr), False)
                If url = IntPtr.Zero Then Return Nothing

                source = CGImageSourceCreateWithURL(url, IntPtr.Zero)
                If source = IntPtr.Zero Then Return Nothing

                Dim maximum = If(maxDimension > 0, maxDimension, Integer.MaxValue)
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
            Catch ex As Exception
                DiagnosticLogService.LogException("UniversalImageDecode.ImageIO", ex)
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
