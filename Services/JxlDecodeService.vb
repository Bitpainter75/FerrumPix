Imports System
Imports System.IO
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Liest JPEG XL (.jxl) ueber libjxl. SkiaSharp bringt dafuer keinen Dekoder mit.
    '''
    ''' Aufgebaut wie <see cref="HeifDecodeService"/>: die Bibliothek wird dynamisch ueber
    ''' NativeLibrary.Load und Delegaten geladen (der DllImport-Resolver der Assembly ist schon
    ''' belegt), und fehlt sie, meldet IsAvailable False und die Dateien bleiben zu. Kein Absturz.
    '''
    ''' WOHER DIE BIBLIOTHEK KOMMT:
    '''
    '''   Linux    aus der Paketverwaltung; die Flatpak-Laufzeit bringt sie selbst mit.
    '''   macOS    aus Homebrew.
    '''   Windows  MITGELIEFERT, als EINE jxl.dll mit Faden-Bibliothek, Farbmanagement, Brotli und
    '''            Highway darin, gebaut mit packaging/libjxl/build-windows.sh. Lizenzseitig
    '''            unkritisch: BSD-3, MIT und Apache-2.0, dazu libjxls lizenzfreie Patentzusage.
    '''
    ''' WELCHE FASSUNG: ab 0.7. Mit 0.9 fiel bei den beiden Profilabfragen der ungenutzte Parameter
    ''' fuer das Pixelformat weg; welche Form gilt, sagt JxlDecoderVersion. Aeltere Fassungen gibt
    ''' es in den Distributionen nicht mehr.
    '''
    ''' WAS GELESEN WIRD: das erste Bild, als 8 Bit RGBA. Bei einer Animation ist das das erste
    ''' Einzelbild, bei 16 Bit oder HDR schneidet libjxl auf 8 Bit ab, weil die Pipeline 8 Bit
    ''' rechnet. Die Drehung aus dem Codestream legt libjxl selbst auf (JxlDecoderSetKeepOrientation
    ''' bleibt auf seiner Vorgabe), es braucht also KEINE zweite Orientierungskorrektur.
    '''
    ''' NUR LESEN: ein Encoder ist hier nicht angebunden. SaveImage weist ein .jxl-Ziel ab.
    ''' </summary>
    Public NotInheritable Class JxlDecodeService

        Private Sub New()
        End Sub

        ' Native Bindung

        Private Delegate Function VersionFn() As UInteger
        Private Delegate Function CreateFn(memoryManager As IntPtr) As IntPtr
        Private Delegate Sub DestroyFn(dec As IntPtr)
        Private Delegate Function SubscribeEventsFn(dec As IntPtr, events As Integer) As Integer
        Private Delegate Function SetInputFn(dec As IntPtr, data As IntPtr, size As UIntPtr) As Integer
        Private Delegate Sub CloseInputFn(dec As IntPtr)
        Private Delegate Function ProcessInputFn(dec As IntPtr) As Integer
        Private Delegate Function GetBasicInfoFn(dec As IntPtr, info As IntPtr) As Integer
        Private Delegate Function OutBufferSizeFn(dec As IntPtr, ByRef format As JxlPixelFormat, ByRef size As UIntPtr) As Integer
        Private Delegate Function SetOutBufferFn(dec As IntPtr, ByRef format As JxlPixelFormat, buffer As IntPtr, size As UIntPtr) As Integer
        Private Delegate Function SetBoolFn(dec As IntPtr, value As Integer) As Integer
        Private Delegate Function SetParallelRunnerFn(dec As IntPtr, runner As IntPtr, opaque As IntPtr) As Integer
        ''' Ab 0.9: (dec, target, size) und (dec, target, buffer, size).
        Private Delegate Function IccSizeFn(dec As IntPtr, target As Integer, ByRef size As UIntPtr) As Integer
        Private Delegate Function IccFn(dec As IntPtr, target As Integer, buffer As IntPtr, size As UIntPtr) As Integer
        ''' Vor 0.9: dazwischen ein Zeiger auf ein Pixelformat, den die Bibliothek nicht benutzt.
        Private Delegate Function IccSizeLegacyFn(dec As IntPtr, format As IntPtr, target As Integer, ByRef size As UIntPtr) As Integer
        Private Delegate Function IccLegacyFn(dec As IntPtr, format As IntPtr, target As Integer, buffer As IntPtr, size As UIntPtr) As Integer
        Private Delegate Function RunnerCreateFn(memoryManager As IntPtr, workers As UIntPtr) As IntPtr
        Private Delegate Sub RunnerDestroyFn(runner As IntPtr)
        Private Delegate Function DefaultWorkersFn() As UIntPtr

        ''' <summary>JxlPixelFormat: Kanalzahl, Datentyp, Byte-Reihenfolge, Zeilenausrichtung. Die
        ''' beiden Aufzaehlungen sind in C je vier Byte, align ist size_t; das Polster davor legt
        ''' .NET genauso an wie der C-Uebersetzer.</summary>
        <StructLayout(LayoutKind.Sequential)>
        Private Structure JxlPixelFormat
            Public NumChannels As UInteger
            Public DataType As Integer
            Public Endianness As Integer
            Public Align As UIntPtr
        End Structure

        Private Const StatusSuccess As Integer = 0
        Private Const StatusError As Integer = 1
        Private Const StatusNeedMoreInput As Integer = 2
        Private Const StatusNeedImageOutBuffer As Integer = 5
        Private Const StatusBasicInfo As Integer = &H40
        Private Const StatusColorEncoding As Integer = &H100
        Private Const StatusFullImage As Integer = &H1000

        Private Const TypeUint8 As Integer = 2
        Private Const NativeEndian As Integer = 0
        Private Const ProfileTargetData As Integer = 1

        ''' <summary>Versatz der Felder in JxlBasicInfo. Die Struktur ist lang und traegt am Ende
        ''' ein Polster fuer spaetere Felder; gebraucht werden nur diese drei, deshalb wird sie als
        ''' Speicherblock gelesen statt vollstaendig nachgebildet. Vor xsize steht have_container,
        ''' zwischen ysize und orientation neun Felder zu je vier Byte.</summary>
        Private Const BasicInfoXsizeOffset As Integer = 4
        Private Const BasicInfoYsizeOffset As Integer = 8
        Private Const BasicInfoOrientationOffset As Integer = 48
        ''' Reichlich bemessen: die Struktur ist in 0.12 rund 200 Byte gross.
        Private Const BasicInfoBufferSize As Integer = 1024

        ''' <summary>Wie viel fuer die Masse zuerst gelesen wird. Die Kopfdaten stehen am Anfang,
        ''' im Behaelterformat koennen EXIF- oder XMP-Boxen davor liegen; reicht es nicht, wird die
        ''' ganze Datei gereicht.</summary>
        Private Const HeaderProbeBytes As Integer = 64 * 1024

        Private Shared ReadOnly _initLock As New Object()
        ''' Siehe HeifDecodeService: dieses Schloss haelt nur die nativen Aufrufe dieses Dienstes
        ''' auseinander. Dass in der ganzen Anwendung nur ein Decode gleichzeitig laeuft, sichert
        ''' <see cref="DecodeGate"/>, und zwar immer in der Reihenfolge erst Schleuse, dann Schloss.
        Private Shared ReadOnly _nativeLock As New Object()

        Private Shared _initialized As Boolean
        Private Shared _library As IntPtr
        Private Shared _threadsLibrary As IntPtr
        Private Shared _loadedLibrary As String
        Private Shared _version As UInteger

        Private Shared _create As CreateFn
        Private Shared _destroy As DestroyFn
        Private Shared _subscribeEvents As SubscribeEventsFn
        Private Shared _setInput As SetInputFn
        Private Shared _processInput As ProcessInputFn
        Private Shared _getBasicInfo As GetBasicInfoFn
        Private Shared _outBufferSize As OutBufferSizeFn
        Private Shared _setOutBuffer As SetOutBufferFn
        ''' Die folgenden sind OPTIONAL. Ohne sie wird trotzdem gelesen: ohne CloseInput meldet ein
        ''' abgeschnittenes Bild "mehr Eingabe noetig" statt eines Fehlers, ohne Profilabfrage
        ''' bleibt das Bild unverwaltet, ohne die Faeden laeuft der Decode auf einem Kern.
        Private Shared _closeInput As CloseInputFn
        Private Shared _setUnpremultiply As SetBoolFn
        Private Shared _setParallelRunner As SetParallelRunnerFn
        Private Shared _iccSize As IccSizeFn
        Private Shared _icc As IccFn
        Private Shared _iccSizeLegacy As IccSizeLegacyFn
        Private Shared _iccLegacy As IccLegacyFn
        Private Shared _runnerCreate As RunnerCreateFn
        Private Shared _runnerDestroy As RunnerDestroyFn
        Private Shared _defaultWorkers As DefaultWorkersFn
        ''' Die Funktion JxlThreadParallelRunner selbst: sie wird nicht aus .NET gerufen, sondern
        ''' als Zeiger an den Dekoder gereicht, der sie aufruft.
        Private Shared _runnerFunction As IntPtr

        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                EnsureLoaded()
                Return _library <> IntPtr.Zero
            End Get
        End Property

        ''' <summary>Welche Bibliothek geladen wurde, samt Fassung. Fuer Diagnose und Feldberichte.</summary>
        Public Shared ReadOnly Property LoadedLibraryName As String
            Get
                EnsureLoaded()
                If _library = IntPtr.Zero Then Return ""
                Return LoadedLibraryNameUnlocked()
            End Get
        End Property

        Public Shared Function IsSupportedJxl(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Return String.Equals(IO.Path.GetExtension(filePath), ".jxl", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Die Dateinamen, unter denen libjxl zu finden ist, und je Name der passende
        ''' Name der Faden-Bibliothek. Die beiden muessen zur selben Fassung gehoeren.
        '''
        ''' Unter Linux die Sonamen von neu nach alt. Das nackte libjxl.so gibt es nur mit dem
        ''' Entwicklerpaket und steht deshalb am Ende.</summary>
        Private Shared Function LibraryNames() As (Main As String, Threads As String)()
            If OperatingSystem.IsWindows() Then
                Return {("jxl.dll", "jxl_threads.dll"), ("libjxl.dll", "libjxl_threads.dll")}
            End If
            If OperatingSystem.IsMacOS() Then
                Return {("libjxl.dylib", "libjxl_threads.dylib")}
            End If
            Dim names As New List(Of (Main As String, Threads As String))()
            For minor = 14 To 7 Step -1
                names.Add(($"libjxl.so.0.{minor}", $"libjxl_threads.so.0.{minor}"))
            Next
            names.Add(("libjxl.so", "libjxl_threads.so"))
            Return names.ToArray()
        End Function

        ''' <summary>Erst der blosse Name, dann Homebrew unter macOS, dann neben der Anwendung und
        ''' unter runtimes/&lt;rid&gt;/native. Dieselbe Reihenfolge wie bei libheif, aus denselben
        ''' Gruenden (siehe HeifDecodeService.BundledCandidates).</summary>
        Private Shared Iterator Function CandidatePaths(name As String) As IEnumerable(Of String)
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

            Yield name
            If OperatingSystem.IsMacOS() Then
                Yield IO.Path.Combine("/opt/homebrew/lib", name)
                Yield IO.Path.Combine("/usr/local/lib", name)
            End If
            Yield IO.Path.Combine(baseDir, name)
            If rid.Length > 0 Then Yield IO.Path.Combine(baseDir, "runtimes", rid, "native", name)
        End Function

        Private Shared Sub EnsureLoaded()
            SyncLock _initLock
                If _initialized Then Return
                _initialized = True

                Dim handle As IntPtr = IntPtr.Zero
                Dim threadsName As String = Nothing
                Dim loadedPath As String = Nothing
                For Each pair In LibraryNames()
                    For Each candidate In CandidatePaths(pair.Main)
                        If NativeLibrary.TryLoad(candidate, handle) Then
                            loadedPath = candidate
                            threadsName = pair.Threads
                            Exit For
                        End If
                        handle = IntPtr.Zero
                    Next
                    If handle <> IntPtr.Zero Then Exit For
                Next
                If handle = IntPtr.Zero Then
                    DiagnosticLogService.LogAlways("JXL",
                        "kein JPEG-XL-Dekoder geladen. Unter Windows liegt jxl.dll bei; unter Linux kommt libjxl aus der Paketverwaltung.")
                    Return
                End If

                Try
                    Dim version = GetExport(Of VersionFn)(handle, "JxlDecoderVersion")()
                    If version < 7000UI Then
                        DiagnosticLogService.LogAlways("JXL", $"libjxl {version} ist aelter als 0.7 und wird nicht benutzt")
                        NativeLibrary.Free(handle)
                        Return
                    End If
                    _create = GetExport(Of CreateFn)(handle, "JxlDecoderCreate")
                    _destroy = GetExport(Of DestroyFn)(handle, "JxlDecoderDestroy")
                    _subscribeEvents = GetExport(Of SubscribeEventsFn)(handle, "JxlDecoderSubscribeEvents")
                    _setInput = GetExport(Of SetInputFn)(handle, "JxlDecoderSetInput")
                    _processInput = GetExport(Of ProcessInputFn)(handle, "JxlDecoderProcessInput")
                    _getBasicInfo = GetExport(Of GetBasicInfoFn)(handle, "JxlDecoderGetBasicInfo")
                    _outBufferSize = GetExport(Of OutBufferSizeFn)(handle, "JxlDecoderImageOutBufferSize")
                    _setOutBuffer = GetExport(Of SetOutBufferFn)(handle, "JxlDecoderSetImageOutBuffer")
                    _version = version
                    _library = handle
                    _loadedLibrary = IO.Path.GetFileName(loadedPath)
                Catch
                    _create = Nothing : _destroy = Nothing : _subscribeEvents = Nothing
                    _setInput = Nothing : _processInput = Nothing : _getBasicInfo = Nothing
                    _outBufferSize = Nothing : _setOutBuffer = Nothing
                    NativeLibrary.Free(handle)
                    DiagnosticLogService.LogAlways("JXL", $"{IO.Path.GetFileName(loadedPath)} fehlt ein Pflichtexport und wird nicht benutzt")
                    Return
                End Try

                _closeInput = TryGetExport(Of CloseInputFn)(handle, "JxlDecoderCloseInput")
                _setUnpremultiply = TryGetExport(Of SetBoolFn)(handle, "JxlDecoderSetUnpremultiplyAlpha")
                _setParallelRunner = TryGetExport(Of SetParallelRunnerFn)(handle, "JxlDecoderSetParallelRunner")
                If _version >= 9000UI Then
                    _iccSize = TryGetExport(Of IccSizeFn)(handle, "JxlDecoderGetICCProfileSize")
                    _icc = TryGetExport(Of IccFn)(handle, "JxlDecoderGetColorAsICCProfile")
                Else
                    _iccSizeLegacy = TryGetExport(Of IccSizeLegacyFn)(handle, "JxlDecoderGetICCProfileSize")
                    _iccLegacy = TryGetExport(Of IccLegacyFn)(handle, "JxlDecoderGetColorAsICCProfile")
                End If

                ' Die Faden-Funktionen zuerst in der Hauptbibliothek: die unter Windows mitgelieferte
                ' jxl.dll traegt sie selbst (packaging/libjxl/build-windows.sh). Sonst liegt die
                ' Faden-Bibliothek daneben. War die Hauptbibliothek mit vollem Pfad geladen, wird
                ' die zweite im selben Ordner gesucht, sonst unter ihrem Namen.
                Dim threadsHandle As IntPtr
                Dim threadsCandidate = If(IO.Path.IsPathRooted(loadedPath),
                                          IO.Path.Combine(IO.Path.GetDirectoryName(loadedPath), threadsName),
                                          threadsName)
                Dim ownRunner As IntPtr
                If NativeLibrary.TryGetExport(handle, "JxlThreadParallelRunner", ownRunner) Then
                    threadsHandle = handle
                ElseIf Not NativeLibrary.TryLoad(threadsCandidate, threadsHandle) Then
                    threadsHandle = IntPtr.Zero
                End If
                If threadsHandle <> IntPtr.Zero Then
                    _runnerCreate = TryGetExport(Of RunnerCreateFn)(threadsHandle, "JxlThreadParallelRunnerCreate")
                    _runnerDestroy = TryGetExport(Of RunnerDestroyFn)(threadsHandle, "JxlThreadParallelRunnerDestroy")
                    _defaultWorkers = TryGetExport(Of DefaultWorkersFn)(threadsHandle, "JxlThreadParallelRunnerDefaultNumWorkerThreads")
                    Dim runnerFunction As IntPtr
                    If NativeLibrary.TryGetExport(threadsHandle, "JxlThreadParallelRunner", runnerFunction) Then _runnerFunction = runnerFunction
                    _threadsLibrary = threadsHandle
                End If

                DiagnosticLogService.LogAlways("JXL", $"geladen: {LoadedLibraryNameUnlocked()}" &
                    If(RunnerReady, "", ", ohne Faden-Bibliothek (Decode auf einem Kern)"))
            End SyncLock
        End Sub

        Private Shared Function LoadedLibraryNameUnlocked() As String
            Return $"{_loadedLibrary} {_version \ 1000000UI}.{(_version \ 1000UI) Mod 1000UI}.{_version Mod 1000UI}"
        End Function

        Private Shared ReadOnly Property RunnerReady As Boolean
            Get
                Return _setParallelRunner IsNot Nothing AndAlso _runnerCreate IsNot Nothing AndAlso
                       _runnerDestroy IsNot Nothing AndAlso _defaultWorkers IsNot Nothing AndAlso
                       _runnerFunction <> IntPtr.Zero
            End Get
        End Property

        Private Shared Function GetExport(Of T)(handle As IntPtr, name As String) As T
            Return Marshal.GetDelegateForFunctionPointer(Of T)(NativeLibrary.GetExport(handle, name))
        End Function

        Private Shared Function TryGetExport(Of T As Class)(handle As IntPtr, name As String) As T
            Dim address As IntPtr
            If Not NativeLibrary.TryGetExport(handle, name, address) Then Return Nothing
            Return Marshal.GetDelegateForFunctionPointer(Of T)(address)
        End Function

        ''' <summary>Dekodiert das erste Bild als Bgra8888 (Besitz beim Aufrufer) oder Nothing.
        ''' Gedreht ist es schon, siehe Klassenkommentar.</summary>
        Public Shared Function TryDecode(path As String) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            Return DecodeGate.Run(Function()
                                      SyncLock _nativeLock
                                          Return DecodeCore(path)
                                      End SyncLock
                                  End Function)
        End Function

        ''' <summary>Die Datei als PNG-Strom, die Form, die OpenSourceStream und die Miniaturen
        ''' erwarten (gleiches Muster wie HEIF, PSD und TIFF).</summary>
        Public Shared Function ExtractPreview(path As String) As MemoryStream
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            Return DecodeGate.Run(Function()
                                      SyncLock _nativeLock
                                          Using bmp = DecodeCore(path)
                                              Return EncodePng(bmp)
                                          End Using
                                      End SyncLock
                                  End Function)
        End Function

        Private Shared Function EncodePng(bmp As SKBitmap) As MemoryStream
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
        End Function

        Private Shared Function DecodeCore(path As String) As SKBitmap
            Dim bytes As Byte()
            Try
                bytes = File.ReadAllBytes(path)
            Catch
                Return Nothing
            End Try
            If bytes.Length = 0 Then Return Nothing

            Dim dec As IntPtr = IntPtr.Zero
            Dim runner As IntPtr = IntPtr.Zero
            Dim info As IntPtr = IntPtr.Zero
            Dim pin As GCHandle
            Dim bitmap As SKBitmap = Nothing
            Dim profile As SKColorSpace = Nothing
            Dim complete = False
            Try
                dec = _create(IntPtr.Zero)
                If dec = IntPtr.Zero Then Return Nothing

                If RunnerReady Then
                    runner = _runnerCreate(IntPtr.Zero, _defaultWorkers())
                    If runner <> IntPtr.Zero Then _setParallelRunner(dec, _runnerFunction, runner)
                End If
                If _subscribeEvents(dec, StatusBasicInfo Or StatusColorEncoding Or StatusFullImage) <> StatusSuccess Then Return Nothing
                ' Skia erwartet unvormultipliziertes Alpha; ein JPEG XL darf vormultipliziert
                ' gespeichert sein.
                _setUnpremultiply?.Invoke(dec, 1)

                pin = GCHandle.Alloc(bytes, GCHandleType.Pinned)
                If _setInput(dec, pin.AddrOfPinnedObject(), CType(bytes.Length, UIntPtr)) <> StatusSuccess Then Return Nothing
                _closeInput?.Invoke(dec)

                info = Marshal.AllocHGlobal(BasicInfoBufferSize)
                Dim format = New JxlPixelFormat With {.NumChannels = 4, .DataType = TypeUint8, .Endianness = NativeEndian, .Align = UIntPtr.Zero}
                Dim width = 0
                Dim height = 0

                Do
                    Dim status = _processInput(dec)
                    Select Case status
                        Case StatusBasicInfo
                            If _getBasicInfo(dec, info) <> StatusSuccess Then Return Nothing
                            Dim size = OrientedSize(info)
                            width = size.Width
                            height = size.Height
                            If width <= 0 OrElse height <= 0 Then Return Nothing
                            If CLng(width) * CLng(height) > Integer.MaxValue \ 4 Then Return Nothing

                        Case StatusColorEncoding
                            profile = ReadColorProfile(dec)

                        Case StatusNeedImageOutBuffer
                            If width <= 0 OrElse height <= 0 Then Return Nothing
                            Dim needed As UIntPtr
                            If _outBufferSize(dec, format, needed) <> StatusSuccess Then Return Nothing
                            ' Stimmt die Groesse nicht mit den Massen ueberein, ist die Annahme ueber
                            ' die Drehung falsch. Dann lieber nichts als ein verschobenes Bild.
                            If needed.ToUInt64() <> CULng(width) * CULng(height) * 4UL Then Return Nothing
                            bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul))
                            If bitmap.GetPixels() = IntPtr.Zero OrElse bitmap.RowBytes <> width * 4 Then Return Nothing
                            If _setOutBuffer(dec, format, bitmap.GetPixels(), needed) <> StatusSuccess Then Return Nothing

                        Case StatusFullImage
                            ' Das erste vollstaendige Bild. Bei einer Animation folgten weitere
                            ' Einzelbilder; gebraucht wird nur dieses.
                            complete = True
                            Exit Do

                        Case StatusSuccess
                            Exit Do

                        Case StatusError, StatusNeedMoreInput
                            Return Nothing

                        Case Else
                            ' Ereignisse, die nicht bestellt wurden, kommen nicht; alles andere
                            ' waere ein Zustand, den dieser Weg nicht kennt.
                            Return Nothing
                    End Select
                Loop

                If Not complete OrElse bitmap Is Nothing Then Return Nothing

                ' Die Pipeline arbeitet in Bgra8888. Die Kopie ist zugleich der Punkt, an dem das
                ' Farbprofil angewandt wird: danach ist es weg (siehe ColorManagementService).
                Dim bgra = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul))
                Try
                    Dim rowBytes = width * 4
                    Dim row(rowBytes - 1) As Byte
                    Dim source = bitmap.GetPixels()
                    Dim target = bgra.GetPixels()
                    Dim targetStride = bgra.RowBytes
                    For y = 0 To height - 1
                        Marshal.Copy(source + y * rowBytes, row, 0, rowBytes)
                        For x = 0 To rowBytes - 4 Step 4
                            Dim r = row(x)
                            row(x) = row(x + 2)
                            row(x + 2) = r
                        Next
                        Marshal.Copy(row, 0, target + y * targetStride, rowBytes)
                    Next
                    Dim managed = ColorManagementService.ToSrgb(bgra, profile)
                    If Not Object.ReferenceEquals(managed, bgra) Then bgra.Dispose()
                    Return managed
                Catch
                    bgra.Dispose()
                    Return Nothing
                End Try
            Catch ex As Exception
                DiagnosticLogService.LogException("JXL.DecodeCore", ex)
                Return Nothing
            Finally
                ' Der Dekoder zuerst: er haelt Zeiger auf den Ausgabepuffer und die Eingabe.
                If dec <> IntPtr.Zero Then _destroy(dec)
                If runner <> IntPtr.Zero Then _runnerDestroy(runner)
                If info <> IntPtr.Zero Then Marshal.FreeHGlobal(info)
                If pin.IsAllocated Then pin.Free()
                bitmap?.Dispose()
                profile?.Dispose()
            End Try
        End Function

        ''' <summary>Breite und Hoehe, wie sie nach dem Aufbringen der Drehung herauskommen. xsize
        ''' und ysize stehen VOR der Drehung; die Werte 5 bis 8 vertauschen die Achsen.</summary>
        Private Shared Function OrientedSize(info As IntPtr) As (Width As Integer, Height As Integer)
            Dim xsize = Marshal.ReadInt32(info, BasicInfoXsizeOffset)
            Dim ysize = Marshal.ReadInt32(info, BasicInfoYsizeOffset)
            Dim orientation = Marshal.ReadInt32(info, BasicInfoOrientationOffset)
            If orientation >= 5 AndAlso orientation <= 8 Then Return (ysize, xsize)
            Return (xsize, ysize)
        End Function

        ''' <summary>Der Farbraum der ausgegebenen Pixel, oder Nothing.
        '''
        ''' Gefragt wird nach dem Profil der DATEN, nicht dem der Aufnahme. Ein verlustbehaftetes
        ''' JPEG XL ist intern in XYB gespeichert, und was libjxl daraus ohne eigenes
        ''' Farbmanagement ausgibt, muss nicht der Farbraum der Aufnahme sein. Das Profil der Daten
        ''' beschreibt genau die Zahlen, die im Puffer stehen.
        '''
        ''' Kann Skia das Profil nicht lesen (etwa bei PQ oder HLG), bleibt das Bild unverwaltet.</summary>
        Private Shared Function ReadColorProfile(dec As IntPtr) As SKColorSpace
            Dim buffer As IntPtr = IntPtr.Zero
            Try
                Dim size As UIntPtr
                Dim status As Integer
                If _iccSize IsNot Nothing AndAlso _icc IsNot Nothing Then
                    status = _iccSize(dec, ProfileTargetData, size)
                ElseIf _iccSizeLegacy IsNot Nothing AndAlso _iccLegacy IsNot Nothing Then
                    status = _iccSizeLegacy(dec, IntPtr.Zero, ProfileTargetData, size)
                Else
                    Return Nothing
                End If
                If status <> StatusSuccess Then Return Nothing
                Dim length = size.ToUInt64()
                If length = 0UL OrElse length > 16UL * 1024UL * 1024UL Then Return Nothing

                buffer = Marshal.AllocHGlobal(CInt(length))
                If _icc IsNot Nothing Then
                    status = _icc(dec, ProfileTargetData, buffer, size)
                Else
                    status = _iccLegacy(dec, IntPtr.Zero, ProfileTargetData, buffer, size)
                End If
                If status <> StatusSuccess Then Return Nothing

                Dim bytes(CInt(length) - 1) As Byte
                Marshal.Copy(buffer, bytes, 0, bytes.Length)
                Return SKColorSpace.CreateIcc(bytes)
            Catch ex As Exception
                DiagnosticLogService.LogException("JXL.ReadColorProfile", ex)
                Return Nothing
            Finally
                If buffer <> IntPtr.Zero Then Marshal.FreeHGlobal(buffer)
            End Try
        End Function

        ''' <summary>Masse ohne Decode, aus den Kopfdaten und schon gedreht.
        '''
        ''' Wie bei HEIF bewusst NICHT durch <see cref="DecodeGate"/>: ein Blick in die Kopfdaten,
        ''' den auch die Oberflaeche stellt, soll nicht hinter einer RAW-Entwicklung warten.</summary>
        Public Shared Function TryGetSize(path As String) As (Width As Integer, Height As Integer)
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return (0, 0)
            SyncLock _nativeLock
                Try
                    Dim length = New FileInfo(path).Length
                    If length <= 0 Then Return (0, 0)
                    Dim probe = ReadHead(path, CInt(Math.Min(length, HeaderProbeBytes)))
                    Dim size = ReadBasicSize(probe, isComplete:=probe.Length >= length)
                    If size.Width > 0 OrElse probe.Length >= length Then Return size
                    ' Die Kopfdaten lagen weiter hinten, etwa hinter einer grossen EXIF-Box.
                    If length > Integer.MaxValue Then Return (0, 0)
                    Return ReadBasicSize(File.ReadAllBytes(path), isComplete:=True)
                Catch
                    Return (0, 0)
                End Try
            End SyncLock
        End Function

        Private Shared Function ReadHead(path As String, count As Integer) As Byte()
            Dim buffer(count - 1) As Byte
            Using stream = File.OpenRead(path)
                Dim read = 0
                While read < count
                    Dim n = stream.Read(buffer, read, count - read)
                    If n <= 0 Then Exit While
                    read += n
                End While
                If read < count Then Array.Resize(buffer, read)
            End Using
            Return buffer
        End Function

        ''' <summary>Liest nur die Kopfdaten. Bei unvollstaendiger Eingabe darf CloseInput NICHT
        ''' gerufen werden: dann meldete libjxl einen Fehler statt "mehr Eingabe noetig".</summary>
        Private Shared Function ReadBasicSize(bytes As Byte(), isComplete As Boolean) As (Width As Integer, Height As Integer)
            If bytes Is Nothing OrElse bytes.Length = 0 Then Return (0, 0)
            Dim dec As IntPtr = IntPtr.Zero
            Dim info As IntPtr = IntPtr.Zero
            Dim pin As GCHandle
            Try
                dec = _create(IntPtr.Zero)
                If dec = IntPtr.Zero Then Return (0, 0)
                If _subscribeEvents(dec, StatusBasicInfo) <> StatusSuccess Then Return (0, 0)
                pin = GCHandle.Alloc(bytes, GCHandleType.Pinned)
                If _setInput(dec, pin.AddrOfPinnedObject(), CType(bytes.Length, UIntPtr)) <> StatusSuccess Then Return (0, 0)
                If isComplete Then _closeInput?.Invoke(dec)
                If _processInput(dec) <> StatusBasicInfo Then Return (0, 0)
                info = Marshal.AllocHGlobal(BasicInfoBufferSize)
                If _getBasicInfo(dec, info) <> StatusSuccess Then Return (0, 0)
                Return OrientedSize(info)
            Catch
                Return (0, 0)
            Finally
                If dec <> IntPtr.Zero Then _destroy(dec)
                If info <> IntPtr.Zero Then Marshal.FreeHGlobal(info)
                If pin.IsAllocated Then pin.Free()
            End Try
        End Function

    End Class

End Namespace
