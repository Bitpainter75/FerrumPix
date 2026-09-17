Imports System
Imports System.IO
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Schreibt JPEG XL ueber libjxl: fertige Bilder aus der Pipeline und, als eigener Weg,
    ''' verlustfrei umgepackte JPEG.
    '''
    ''' Die Bibliothek laedt <see cref="JxlDecodeService"/>; dieser Dienst holt sich dort nur den
    ''' Zeiger und laedt seine eigenen Exporte dazu. Fehlt libjxl oder ein Pflichtexport, meldet
    ''' IsAvailable False, und JPEG XL taucht in keinem Formatdialog auf.
    '''
    ''' QUALITAET: dieselbe Zahl wie bei JPEG, 1 bis 100. Umgerechnet wird sie mit libjxls eigener
    ''' Abbildung (JxlEncoderDistanceFromQuality, ab 0.9), davor mit derselben Formel von Hand.
    ''' 100 heisst VERLUSTFREI: dort wird nicht nur die Distanz null gesetzt, sondern der echte
    ''' verlustfreie Modus, und das Bild bleibt in seinem Farbraum statt in XYB.
    '''
    ''' GESCHRIEBEN WIRD sRGB mit 8 Bit, wie auf allen anderen Wegen. Alpha nur, wenn das Bild
    ''' wirklich durchsichtige Stellen hat; ein voll deckendes Bild bekaeme sonst einen vierten
    ''' Kanal, der nur Platz kostet. EXIF und XMP gehen als Boxen "Exif" und "xml " VOR den
    ''' Bildstrom, ungepackt, damit auch Leser ohne Brotli sie finden.
    ''' </summary>
    Public NotInheritable Class JxlEncodeService

        Private Sub New()
        End Sub

        Private Delegate Function CreateFn(memoryManager As IntPtr) As IntPtr
        Private Delegate Sub DestroyFn(enc As IntPtr)
        Private Delegate Function SetRunnerFn(enc As IntPtr, runner As IntPtr, opaque As IntPtr) As Integer
        Private Delegate Function EncBoolFn(enc As IntPtr, value As Integer) As Integer
        Private Delegate Function EncFn(enc As IntPtr) As Integer
        Private Delegate Sub EncVoidFn(enc As IntPtr)
        Private Delegate Function AddBoxFn(enc As IntPtr, type As IntPtr, contents As IntPtr, size As UIntPtr, compress As Integer) As Integer
        Private Delegate Sub InitBasicInfoFn(info As IntPtr)
        Private Delegate Function SetStructFn(enc As IntPtr, data As IntPtr) As Integer
        Private Delegate Sub SetToSrgbFn(encoding As IntPtr, isGray As Integer)
        Private Delegate Function FrameSettingsCreateFn(enc As IntPtr, source As IntPtr) As IntPtr
        Private Delegate Function FrameBoolFn(settings As IntPtr, value As Integer) As Integer
        Private Delegate Function FrameDistanceFn(settings As IntPtr, distance As Single) As Integer
        Private Delegate Function DistanceFromQualityFn(quality As Single) As Single
        Private Delegate Function AddImageFrameFn(settings As IntPtr, ByRef format As JxlPixelFormat, buffer As IntPtr, size As UIntPtr) As Integer
        Private Delegate Function AddJpegFrameFn(settings As IntPtr, buffer As IntPtr, size As UIntPtr) As Integer
        Private Delegate Function ProcessOutputFn(enc As IntPtr, ByRef nextOut As IntPtr, ByRef availOut As UIntPtr) As Integer

        ''' <summary>Wie in JxlDecodeService: Kanalzahl, Datentyp, Byte-Reihenfolge, Ausrichtung.</summary>
        <StructLayout(LayoutKind.Sequential)>
        Private Structure JxlPixelFormat
            Public NumChannels As UInteger
            Public DataType As Integer
            Public Endianness As Integer
            Public Align As UIntPtr
        End Structure

        Private Const EncSuccess As Integer = 0
        Private Const EncNeedMoreOutput As Integer = 2
        Private Const TypeUint8 As Integer = 2

        ''' <summary>Versatz der Felder in JxlBasicInfo, siehe JxlDecodeService. Gesetzt werden nur
        ''' diese; alles andere bleibt, wie JxlEncoderInitBasicInfo es vorgibt.</summary>
        Private Const InfoXsize As Integer = 4
        Private Const InfoYsize As Integer = 8
        Private Const InfoBits As Integer = 12
        Private Const InfoExponentBits As Integer = 16
        Private Const InfoUsesOriginalProfile As Integer = 36
        Private Const InfoOrientation As Integer = 48
        Private Const InfoColorChannels As Integer = 52
        Private Const InfoExtraChannels As Integer = 56
        Private Const InfoAlphaBits As Integer = 60
        Private Const InfoAlphaExponentBits As Integer = 64
        Private Const StructBufferSize As Integer = 1024

        Private Const OutputChunkBytes As Integer = 1024 * 1024

        Private Shared ReadOnly _initLock As New Object()
        Private Shared _initialized As Boolean
        Private Shared _ready As Boolean

        Private Shared _create As CreateFn
        Private Shared _destroy As DestroyFn
        Private Shared _setRunner As SetRunnerFn
        Private Shared _useContainer As EncBoolFn
        Private Shared _useBoxes As EncFn
        Private Shared _addBox As AddBoxFn
        Private Shared _initBasicInfo As InitBasicInfoFn
        Private Shared _setBasicInfo As SetStructFn
        Private Shared _setColorEncoding As SetStructFn
        Private Shared _setToSrgb As SetToSrgbFn
        Private Shared _frameSettingsCreate As FrameSettingsCreateFn
        Private Shared _setLossless As FrameBoolFn
        Private Shared _setDistance As FrameDistanceFn
        Private Shared _addImageFrame As AddImageFrameFn
        Private Shared _closeInput As EncVoidFn
        Private Shared _processOutput As ProcessOutputFn
        ''' Optional: ohne sie gilt die Formel von Hand, und das Umpacken von JPEG fehlt.
        Private Shared _distanceFromQuality As DistanceFromQualityFn
        Private Shared _storeJpegMetadata As EncBoolFn
        Private Shared _addJpegFrame As AddJpegFrameFn

        ''' <summary>Kann JPEG XL geschrieben werden? Davon haengt ab, ob das Format in den Dialogen
        ''' ueberhaupt erscheint - ein Format, das beim Klick scheitert, waere schlechter als keins.</summary>
        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                EnsureLoaded()
                Return _ready
            End Get
        End Property

        ''' <summary>Kann ein JPEG verlustfrei umgepackt werden?</summary>
        Public Shared ReadOnly Property CanTranscodeJpeg As Boolean
            Get
                EnsureLoaded()
                Return _ready AndAlso _storeJpegMetadata IsNot Nothing AndAlso _addJpegFrame IsNot Nothing
            End Get
        End Property

        Private Shared Sub EnsureLoaded()
            SyncLock _initLock
                If _initialized Then Return
                _initialized = True
                Dim handle = JxlDecodeService.NativeHandle
                If handle = IntPtr.Zero Then Return
                Try
                    _create = GetExport(Of CreateFn)(handle, "JxlEncoderCreate")
                    _destroy = GetExport(Of DestroyFn)(handle, "JxlEncoderDestroy")
                    _setRunner = GetExport(Of SetRunnerFn)(handle, "JxlEncoderSetParallelRunner")
                    _useContainer = GetExport(Of EncBoolFn)(handle, "JxlEncoderUseContainer")
                    _useBoxes = GetExport(Of EncFn)(handle, "JxlEncoderUseBoxes")
                    _addBox = GetExport(Of AddBoxFn)(handle, "JxlEncoderAddBox")
                    _initBasicInfo = GetExport(Of InitBasicInfoFn)(handle, "JxlEncoderInitBasicInfo")
                    _setBasicInfo = GetExport(Of SetStructFn)(handle, "JxlEncoderSetBasicInfo")
                    _setColorEncoding = GetExport(Of SetStructFn)(handle, "JxlEncoderSetColorEncoding")
                    _setToSrgb = GetExport(Of SetToSrgbFn)(handle, "JxlColorEncodingSetToSRGB")
                    _frameSettingsCreate = GetExport(Of FrameSettingsCreateFn)(handle, "JxlEncoderFrameSettingsCreate")
                    _setLossless = GetExport(Of FrameBoolFn)(handle, "JxlEncoderSetFrameLossless")
                    _setDistance = GetExport(Of FrameDistanceFn)(handle, "JxlEncoderSetFrameDistance")
                    _addImageFrame = GetExport(Of AddImageFrameFn)(handle, "JxlEncoderAddImageFrame")
                    _closeInput = GetExport(Of EncVoidFn)(handle, "JxlEncoderCloseInput")
                    _processOutput = GetExport(Of ProcessOutputFn)(handle, "JxlEncoderProcessOutput")
                    _ready = True
                Catch
                    _ready = False
                    DiagnosticLogService.LogAlways("JXL", "libjxl ist geladen, aber der Encoder ist unvollstaendig - JPEG XL wird nicht angeboten")
                    Return
                End Try
                _distanceFromQuality = TryGetExport(Of DistanceFromQualityFn)(handle, "JxlEncoderDistanceFromQuality")
                _storeJpegMetadata = TryGetExport(Of EncBoolFn)(handle, "JxlEncoderStoreJPEGMetadata")
                _addJpegFrame = TryGetExport(Of AddJpegFrameFn)(handle, "JxlEncoderAddJPEGFrame")
            End SyncLock
        End Sub

        Private Shared Function GetExport(Of T)(handle As IntPtr, name As String) As T
            Return Marshal.GetDelegateForFunctionPointer(Of T)(NativeLibrary.GetExport(handle, name))
        End Function

        Private Shared Function TryGetExport(Of T As Class)(handle As IntPtr, name As String) As T
            Dim address As IntPtr
            If Not NativeLibrary.TryGetExport(handle, name, address) Then Return Nothing
            Return Marshal.GetDelegateForFunctionPointer(Of T)(address)
        End Function

        ''' <summary>Die JPEG-Qualitaet als Distanz, wie libjxl sie rechnet. Ab 0.9 fragt sie die
        ''' Bibliothek selbst, davor gilt dieselbe Formel hier. Unter 1 wird auf 1 gehoben.</summary>
        Friend Shared Function DistanceForQuality(quality As Integer) As Single
            Dim q = Math.Max(1, Math.Min(100, quality))
            If _distanceFromQuality IsNot Nothing Then Return _distanceFromQuality(CSng(q))
            If q >= 100 Then Return 0.0F
            If q >= 30 Then Return CSng(0.1 + (100 - q) * 0.09)
            Return CSng(53.0 / 3000.0 * q * q - 23.0 / 20.0 * q + 25.0)
        End Function

        ''' <summary>Schreibt das Bild als JPEG XL in den Strom. Wirft bei einem Fehler, damit
        ''' <see cref="ImageProcessor.WriteFileAtomic"/> die halbe Datei verwirft.
        '''
        ''' <paramref name="exifTiff"/> ist der nackte TIFF-Block, <paramref name="xmp"/> der
        ''' XMP-Text; beide duerfen Nothing sein.</summary>
        Public Shared Sub Encode(bitmap As SKBitmap, output As Stream, quality As Integer,
                                 Optional exifTiff As Byte() = Nothing, Optional xmp As Byte() = Nothing)
            If bitmap Is Nothing OrElse output Is Nothing Then Throw New ArgumentNullException(NameOf(bitmap))
            If Not IsAvailable Then Throw New InvalidOperationException("JPEG XL: kein Encoder geladen")

            Dim width = bitmap.Width
            Dim height = bitmap.Height
            If width <= 0 OrElse height <= 0 Then Throw New InvalidOperationException("JPEG XL: leeres Bild")
            If CLng(width) * CLng(height) * 4L > Integer.MaxValue Then Throw New InvalidOperationException("JPEG XL: Bild zu gross")

            Dim lossless = quality >= 100
            Dim pixels As IntPtr = IntPtr.Zero
            Dim info As IntPtr = IntPtr.Zero
            Dim color As IntPtr = IntPtr.Zero
            Dim enc As IntPtr = IntPtr.Zero
            Dim runner As IntPtr = IntPtr.Zero
            Try
                ' In unvormultipliziertes RGBA umrechnen lassen. Die Bitmaps der Pipeline tragen
                ' keinen Farbraum, es wird also nur die Form gewandelt, nicht die Farbe.
                pixels = Marshal.AllocHGlobal(width * height * 4)
                Dim rgbaInfo = New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul)
                Using pixmap = bitmap.PeekPixels()
                    If pixmap Is Nothing OrElse Not pixmap.ReadPixels(rgbaInfo, pixels, width * 4) Then
                        Throw New InvalidOperationException("JPEG XL: Bildpunkte nicht lesbar")
                    End If
                End Using
                Dim hasAlpha = HasTransparency(pixels, width, height)
                Dim channels = If(hasAlpha, 4, 3)
                If Not hasAlpha Then DropAlphaInPlace(pixels, width, height)

                enc = _create(IntPtr.Zero)
                If enc = IntPtr.Zero Then Throw New InvalidOperationException("JPEG XL: Encoder nicht angelegt")
                runner = JxlDecodeService.CreateRunner()
                If runner <> IntPtr.Zero Then _setRunner(enc, JxlDecodeService.RunnerFunction, runner)

                Dim withBoxes = (exifTiff IsNot Nothing AndAlso exifTiff.Length > 0) OrElse (xmp IsNot Nothing AndAlso xmp.Length > 0)
                If withBoxes Then
                    Check(_useContainer(enc, 1), "Behaelter")
                    Check(_useBoxes(enc), "Boxen")
                End If

                info = Marshal.AllocHGlobal(StructBufferSize)
                ZeroMemory(info, StructBufferSize)
                _initBasicInfo(info)
                Marshal.WriteInt32(info, InfoXsize, width)
                Marshal.WriteInt32(info, InfoYsize, height)
                Marshal.WriteInt32(info, InfoBits, 8)
                Marshal.WriteInt32(info, InfoExponentBits, 0)
                ' Verlustfrei muss im Farbraum der Bildpunkte gespeichert werden, sonst ist es nicht
                ' mehr bitgenau; verlustbehaftet ist XYB die bessere Wahl.
                Marshal.WriteInt32(info, InfoUsesOriginalProfile, If(lossless, 1, 0))
                Marshal.WriteInt32(info, InfoOrientation, 1)
                Marshal.WriteInt32(info, InfoColorChannels, 3)
                Marshal.WriteInt32(info, InfoExtraChannels, If(hasAlpha, 1, 0))
                Marshal.WriteInt32(info, InfoAlphaBits, If(hasAlpha, 8, 0))
                Marshal.WriteInt32(info, InfoAlphaExponentBits, 0)
                Check(_setBasicInfo(enc, info), "Kopfdaten")

                color = Marshal.AllocHGlobal(StructBufferSize)
                ZeroMemory(color, StructBufferSize)
                _setToSrgb(color, 0)
                Check(_setColorEncoding(enc, color), "Farbraum")

                If withBoxes Then
                    If exifTiff IsNot Nothing AndAlso exifTiff.Length > 0 Then
                        ' Vier Null-Byte vorweg: der Abstand bis zum TIFF-Kopf.
                        Dim content(exifTiff.Length + 4 - 1) As Byte
                        Buffer.BlockCopy(exifTiff, 0, content, 4, exifTiff.Length)
                        AddBox(enc, "Exif", content)
                    End If
                    If xmp IsNot Nothing AndAlso xmp.Length > 0 Then AddBox(enc, "xml ", xmp)
                End If

                Dim settings = _frameSettingsCreate(enc, IntPtr.Zero)
                If settings = IntPtr.Zero Then Throw New InvalidOperationException("JPEG XL: keine Bildeinstellungen")
                If lossless Then
                    Check(_setLossless(settings, 1), "verlustfrei")
                Else
                    Check(_setDistance(settings, Math.Max(0.01F, DistanceForQuality(quality))), "Qualitaet")
                End If

                Dim format = New JxlPixelFormat With {.NumChannels = CUInt(channels), .DataType = TypeUint8, .Endianness = 0, .Align = UIntPtr.Zero}
                Check(_addImageFrame(settings, format, pixels, CType(CULng(width) * CULng(height) * CULng(channels), UIntPtr)), "Bild")
                _closeInput(enc)
                WriteOutput(enc, output)
            Finally
                If enc <> IntPtr.Zero Then _destroy(enc)
                JxlDecodeService.DestroyRunner(runner)
                If pixels <> IntPtr.Zero Then Marshal.FreeHGlobal(pixels)
                If info <> IntPtr.Zero Then Marshal.FreeHGlobal(info)
                If color <> IntPtr.Zero Then Marshal.FreeHGlobal(color)
            End Try
        End Sub

        ''' <summary>Packt ein JPEG verlustfrei nach JPEG XL um. Die Datei wird rund ein Fuenftel
        ''' kleiner, und <see cref="JxlDecodeService.ReconstructJpeg"/> holt dasselbe JPEG Byte fuer
        ''' Byte zurueck. EXIF und XMP des JPEG nimmt libjxl selbst mit.
        '''
        ''' False, wenn libjxl das JPEG nicht umpacken kann (etwa CMYK oder ungewoehnliche
        ''' Kodierungen). Dann ist in den Strom nichts geschrieben, und der Aufrufer nimmt den
        ''' gewoehnlichen Weg ueber die Bildpunkte.</summary>
        Public Shared Function TryTranscodeJpeg(jpegBytes As Byte(), output As Stream) As Boolean
            If jpegBytes Is Nothing OrElse jpegBytes.Length < 4 OrElse output Is Nothing Then Return False
            If Not CanTranscodeJpeg Then Return False
            Dim enc As IntPtr = IntPtr.Zero
            Dim runner As IntPtr = IntPtr.Zero
            Dim pin As GCHandle
            Try
                enc = _create(IntPtr.Zero)
                If enc = IntPtr.Zero Then Return False
                runner = JxlDecodeService.CreateRunner()
                If runner <> IntPtr.Zero Then _setRunner(enc, JxlDecodeService.RunnerFunction, runner)
                If _useContainer(enc, 1) <> EncSuccess Then Return False
                If _storeJpegMetadata(enc, 1) <> EncSuccess Then Return False
                Dim settings = _frameSettingsCreate(enc, IntPtr.Zero)
                If settings = IntPtr.Zero Then Return False
                pin = GCHandle.Alloc(jpegBytes, GCHandleType.Pinned)
                If _addJpegFrame(settings, pin.AddrOfPinnedObject(), CType(CULng(jpegBytes.Length), UIntPtr)) <> EncSuccess Then Return False
                _closeInput(enc)
                ' Erst vollstaendig in den Speicher: scheitert die Ausgabe mittendrin, darf im Strom
                ' nichts stehen, sonst saehe der Rueckfall eine halbe Datei vor sich.
                Using buffer As New MemoryStream()
                    Try
                        WriteOutput(enc, buffer)
                    Catch
                        Return False
                    End Try
                    buffer.Position = 0
                    buffer.CopyTo(output)
                End Using
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("JXL.TryTranscodeJpeg", ex)
                Return False
            Finally
                If enc <> IntPtr.Zero Then _destroy(enc)
                JxlDecodeService.DestroyRunner(runner)
                If pin.IsAllocated Then pin.Free()
            End Try
        End Function

        Private Shared Sub AddBox(enc As IntPtr, type As String, content As Byte())
            Dim typeBytes = Text.Encoding.ASCII.GetBytes(type)
            Dim typePin = GCHandle.Alloc(typeBytes, GCHandleType.Pinned)
            Dim contentPin = GCHandle.Alloc(content, GCHandleType.Pinned)
            Try
                Check(_addBox(enc, typePin.AddrOfPinnedObject(), contentPin.AddrOfPinnedObject(),
                              CType(CULng(content.Length), UIntPtr), 0), $"Box {type.Trim()}")
            Finally
                typePin.Free()
                contentPin.Free()
            End Try
        End Sub

        Private Shared Sub WriteOutput(enc As IntPtr, output As Stream)
            Dim chunk = Marshal.AllocHGlobal(OutputChunkBytes)
            Try
                Dim managed(OutputChunkBytes - 1) As Byte
                Do
                    Dim nextOut = chunk
                    Dim avail = CType(CULng(OutputChunkBytes), UIntPtr)
                    Dim status = _processOutput(enc, nextOut, avail)
                    Dim written = OutputChunkBytes - CInt(avail.ToUInt64())
                    If written > 0 Then
                        Marshal.Copy(chunk, managed, 0, written)
                        output.Write(managed, 0, written)
                    End If
                    If status = EncSuccess Then Exit Do
                    If status <> EncNeedMoreOutput Then Throw New InvalidOperationException("JPEG XL: Kodieren fehlgeschlagen")
                Loop
            Finally
                Marshal.FreeHGlobal(chunk)
            End Try
        End Sub

        Private Shared Sub Check(status As Integer, what As String)
            If status <> EncSuccess Then Throw New InvalidOperationException($"JPEG XL: {what} abgelehnt")
        End Sub

        Private Shared Sub ZeroMemory(ptr As IntPtr, length As Integer)
            Marshal.Copy(New Byte(length - 1) {}, 0, ptr, length)
        End Sub

        ''' <summary>Hat das Bild eine Stelle, die nicht voll deckt? Zeilenweise, damit ein grosses
        ''' Bild nicht auf einmal in den verwalteten Speicher kopiert wird.</summary>
        Private Shared Function HasTransparency(pixels As IntPtr, width As Integer, height As Integer) As Boolean
            Dim rowBytes = width * 4
            Dim row(rowBytes - 1) As Byte
            For y = 0 To height - 1
                Marshal.Copy(pixels + y * rowBytes, row, 0, rowBytes)
                For x = 3 To rowBytes - 1 Step 4
                    If row(x) <> 255 Then Return True
                Next
            Next
            Return False
        End Function

        ''' <summary>RGBA zu RGB an Ort und Stelle. Das Ziel liegt nie hinter der Quelle, deshalb
        ''' ueberschreibt der Umbau nichts, was noch gelesen wird.</summary>
        Private Shared Sub DropAlphaInPlace(pixels As IntPtr, width As Integer, height As Integer)
            Dim sourceRow(width * 4 - 1) As Byte
            Dim targetRow(width * 3 - 1) As Byte
            For y = 0 To height - 1
                Marshal.Copy(pixels + y * width * 4, sourceRow, 0, sourceRow.Length)
                Dim t = 0
                For s = 0 To sourceRow.Length - 1 Step 4
                    targetRow(t) = sourceRow(s)
                    targetRow(t + 1) = sourceRow(s + 1)
                    targetRow(t + 2) = sourceRow(s + 2)
                    t += 3
                Next
                Marshal.Copy(targetRow, 0, pixels + y * width * 3, targetRow.Length)
            Next
        End Sub

    End Class

End Namespace
