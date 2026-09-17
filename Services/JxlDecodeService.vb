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
    ''' GESCHRIEBEN wird in <see cref="JxlEncodeService"/>. Der teilt sich mit diesem Dienst die
    ''' geladene Bibliothek und die Faden-Funktionen (die Friend-Einstiege unten) und laedt nur seine
    ''' eigenen Exporte dazu. Hier stehen ausserdem die beiden Lesewege, die das Schreiben braucht:
    ''' die Metadaten-Boxen (EXIF, XMP) und das Zurueckholen eines verlustfrei umgepackten JPEG.
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
        ''' Boxen und JPEG-Rekonstruktion. Der Boxtyp ist char[4] und kommt ueber einen Zeiger auf
        ''' vier Byte heraus; die Puffer werden wie die Eingabe als Zeiger mit Laenge gesetzt und
        ''' melden beim Freigeben, wie viel davon UNBENUTZT blieb.
        Private Delegate Function GetBoxTypeFn(dec As IntPtr, type As IntPtr, decompressed As Integer) As Integer
        Private Delegate Function ReleaseBufferFn(dec As IntPtr) As UIntPtr

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
        Private Const StatusJpegNeedMoreOutput As Integer = 6
        Private Const StatusBoxNeedMoreOutput As Integer = 7
        Private Const StatusJpegReconstruction As Integer = &H2000
        Private Const StatusBox As Integer = &H4000

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
        ''' Fuer die Frage, ob eine vorhandene Datei ersetzt werden darf: Bittiefe, Gleitkomma-Bits
        ''' und ob eine Animation vorliegt.
        Private Const BasicInfoBitsOffset As Integer = 12
        Private Const BasicInfoExponentBitsOffset As Integer = 16
        Private Const BasicInfoHaveAnimationOffset As Integer = 44
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
        ''' Ebenfalls optional, gebraucht nur fuer Metadaten und JPEG-Rekonstruktion.
        Private Shared _setDecompressBoxes As SetBoolFn
        Private Shared _getBoxType As GetBoxTypeFn
        Private Shared _setBoxBuffer As SetInputFn
        Private Shared _releaseBoxBuffer As ReleaseBufferFn
        Private Shared _setJpegBuffer As SetInputFn
        Private Shared _releaseJpegBuffer As ReleaseBufferFn

        ''' <summary>Die geladene libjxl fuer <see cref="JxlEncodeService"/>, oder IntPtr.Zero. Der
        ''' Encoder steckt in derselben Bibliothek; eine zweite Ladelogik wuerde nur eine zweite
        ''' Kandidatenliste bedeuten, die auseinanderlaeuft.</summary>
        Friend Shared ReadOnly Property NativeHandle As IntPtr
            Get
                EnsureLoaded()
                Return _library
            End Get
        End Property

        ''' <summary>Fassung der geladenen Bibliothek als major*1000000 + minor*1000 + patch.</summary>
        Friend Shared ReadOnly Property NativeVersion As UInteger
            Get
                EnsureLoaded()
                Return _version
            End Get
        End Property

        ''' <summary>Ein Faden-Verteiler fuer Dekoder oder Encoder, oder IntPtr.Zero, wenn die
        ''' Faden-Funktionen fehlen. Freigeben mit <see cref="DestroyRunner"/>.</summary>
        Friend Shared Function CreateRunner() As IntPtr
            EnsureLoaded()
            If Not RunnerReady Then Return IntPtr.Zero
            Return _runnerCreate(IntPtr.Zero, _defaultWorkers())
        End Function

        Friend Shared Sub DestroyRunner(runner As IntPtr)
            If runner <> IntPtr.Zero AndAlso _runnerDestroy IsNot Nothing Then _runnerDestroy(runner)
        End Sub

        ''' <summary>Der Zeiger auf JxlThreadParallelRunner, der zusammen mit dem Verteiler gereicht wird.</summary>
        Friend Shared ReadOnly Property RunnerFunction As IntPtr
            Get
                EnsureLoaded()
                Return _runnerFunction
            End Get
        End Property

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
                _setDecompressBoxes = TryGetExport(Of SetBoolFn)(handle, "JxlDecoderSetDecompressBoxes")
                _getBoxType = TryGetExport(Of GetBoxTypeFn)(handle, "JxlDecoderGetBoxType")
                _setBoxBuffer = TryGetExport(Of SetInputFn)(handle, "JxlDecoderSetBoxBuffer")
                _releaseBoxBuffer = TryGetExport(Of ReleaseBufferFn)(handle, "JxlDecoderReleaseBoxBuffer")
                _setJpegBuffer = TryGetExport(Of SetInputFn)(handle, "JxlDecoderSetJPEGBuffer")
                _releaseJpegBuffer = TryGetExport(Of ReleaseBufferFn)(handle, "JxlDecoderReleaseJPEGBuffer")
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
            Dim fields = TryReadBasicFields(path)
            If fields Is Nothing Then Return (0, 0)
            Return (fields.Width, fields.Height)
        End Function

        ''' <summary>Darf eine VORHANDENE JPEG-XL-Datei durch ein neues Bild ersetzt werden?
        '''
        ''' Dieselbe Frage wie bei TIFF (TiffWriterService.CanReplaceExisting): geschrieben wird mit
        ''' 8 Bit und einem Einzelbild. Traegt die vorhandene Datei mehr, also hoehere Bittiefe,
        ''' Gleitkomma (HDR) oder eine Animation, ginge das still verloren. Was sich nicht lesen
        ''' laesst, gilt als nicht ersetzbar.</summary>
        Public Shared Function CanReplaceExisting(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return True
            Dim fields = TryReadBasicFields(path)
            If fields Is Nothing Then Return False
            Return fields.BitsPerSample <= 8 AndAlso fields.ExponentBits = 0 AndAlso Not fields.HasAnimation
        End Function

        ''' <summary>Was die Kopfdaten ueber eine Datei sagen.</summary>
        Private NotInheritable Class BasicFields
            Public Width As Integer
            Public Height As Integer
            Public BitsPerSample As Integer
            Public ExponentBits As Integer
            Public HasAnimation As Boolean
        End Class

        Private Shared Function TryReadBasicFields(path As String) As BasicFields
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            SyncLock _nativeLock
                Try
                    Dim length = New FileInfo(path).Length
                    If length <= 0 Then Return Nothing
                    Dim probe = ReadHead(path, CInt(Math.Min(length, HeaderProbeBytes)))
                    Dim fields = ReadBasicFields(probe, isComplete:=probe.Length >= length)
                    If fields IsNot Nothing OrElse probe.Length >= length Then Return fields
                    ' Die Kopfdaten lagen weiter hinten, etwa hinter einer grossen EXIF-Box.
                    If length > Integer.MaxValue Then Return Nothing
                    Return ReadBasicFields(File.ReadAllBytes(path), isComplete:=True)
                Catch
                    Return Nothing
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
        Private Shared Function ReadBasicFields(bytes As Byte(), isComplete As Boolean) As BasicFields
            If bytes Is Nothing OrElse bytes.Length = 0 Then Return Nothing
            Dim dec As IntPtr = IntPtr.Zero
            Dim info As IntPtr = IntPtr.Zero
            Dim pin As GCHandle
            Try
                dec = _create(IntPtr.Zero)
                If dec = IntPtr.Zero Then Return Nothing
                If _subscribeEvents(dec, StatusBasicInfo) <> StatusSuccess Then Return Nothing
                pin = GCHandle.Alloc(bytes, GCHandleType.Pinned)
                If _setInput(dec, pin.AddrOfPinnedObject(), CType(bytes.Length, UIntPtr)) <> StatusSuccess Then Return Nothing
                If isComplete Then _closeInput?.Invoke(dec)
                If _processInput(dec) <> StatusBasicInfo Then Return Nothing
                info = Marshal.AllocHGlobal(BasicInfoBufferSize)
                If _getBasicInfo(dec, info) <> StatusSuccess Then Return Nothing
                Dim size = OrientedSize(info)
                If size.Width <= 0 OrElse size.Height <= 0 Then Return Nothing
                Return New BasicFields With {
                    .Width = size.Width,
                    .Height = size.Height,
                    .BitsPerSample = Marshal.ReadInt32(info, BasicInfoBitsOffset),
                    .ExponentBits = Marshal.ReadInt32(info, BasicInfoExponentBitsOffset),
                    .HasAnimation = Marshal.ReadInt32(info, BasicInfoHaveAnimationOffset) <> 0}
            Catch
                Return Nothing
            Finally
                If dec <> IntPtr.Zero Then _destroy(dec)
                If info <> IntPtr.Zero Then Marshal.FreeHGlobal(info)
                If pin.IsAllocated Then pin.Free()
            End Try
        End Function

        ''' <summary>EXIF und XMP aus den Boxen einer JPEG-XL-Datei. EXIF als nackter TIFF-Block (ohne
        ''' den Versatz von vier Byte, mit dem die Box beginnt), XMP als Text in UTF-8. Fehlt etwas,
        ''' ist das Feld Nothing.
        '''
        ''' Die Boxen koennen mit Brotli gepackt sein ("brob"). JxlDecoderSetDecompressBoxes packt sie
        ''' aus, und nach dem Typ wird in der AUSGEPACKTEN Form gefragt.
        '''
        ''' Kein DecodeGate: gelesen werden nur Boxen, keine Bildpunkte. Das Schloss dieses Dienstes
        ''' haelt die nativen Aufrufe auseinander.</summary>
        Public Shared Function ReadMetadataBoxes(path As String) As (Exif As Byte(), Xmp As Byte())
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return (Nothing, Nothing)
            If _getBoxType Is Nothing OrElse _setBoxBuffer Is Nothing OrElse _releaseBoxBuffer Is Nothing Then Return (Nothing, Nothing)
            Dim bytes As Byte()
            Try
                bytes = File.ReadAllBytes(path)
            Catch
                Return (Nothing, Nothing)
            End Try
            SyncLock _nativeLock
                Dim exif As Byte() = Nothing
                Dim xmp As Byte() = Nothing
                Dim dec As IntPtr = IntPtr.Zero
                Dim typeBuffer As IntPtr = IntPtr.Zero
                Dim pin As GCHandle
                Dim collector As New NativeGrowBuffer()
                Try
                    dec = _create(IntPtr.Zero)
                    If dec = IntPtr.Zero Then Return (Nothing, Nothing)
                    If _subscribeEvents(dec, StatusBox) <> StatusSuccess Then Return (Nothing, Nothing)
                    _setDecompressBoxes?.Invoke(dec, 1)
                    pin = GCHandle.Alloc(bytes, GCHandleType.Pinned)
                    If _setInput(dec, pin.AddrOfPinnedObject(), CType(bytes.Length, UIntPtr)) <> StatusSuccess Then Return (Nothing, Nothing)
                    _closeInput?.Invoke(dec)
                    typeBuffer = Marshal.AllocHGlobal(4)

                    Dim currentType As String = Nothing
                    Dim finishBox = Sub()
                                        If currentType Is Nothing Then Return
                                        collector.Release(_releaseBoxBuffer(dec))
                                        Dim content = collector.ToArray()
                                        If currentType = "Exif" Then
                                            exif = TiffFromExifBox(content)
                                        Else
                                            xmp = content
                                        End If
                                        currentType = Nothing
                                    End Sub

                    Do
                        Dim status = _processInput(dec)
                        Select Case status
                            Case StatusBox
                                finishBox()
                                If _getBoxType(dec, typeBuffer, 1) <> StatusSuccess Then Exit Do
                                Dim typeBytes(3) As Byte
                                Marshal.Copy(typeBuffer, typeBytes, 0, 4)
                                Dim type = Text.Encoding.ASCII.GetString(typeBytes)
                                If (type = "Exif" AndAlso exif Is Nothing) OrElse (type = "xml " AndAlso xmp Is Nothing) Then
                                    collector.Reset(64 * 1024)
                                    If _setBoxBuffer(dec, collector.FreeStart, collector.FreeLength) <> StatusSuccess Then Exit Do
                                    currentType = type
                                End If
                            Case StatusBoxNeedMoreOutput
                                If currentType Is Nothing Then Exit Do
                                collector.Release(_releaseBoxBuffer(dec))
                                If Not collector.Grow(MaxMetadataBoxBytes) Then
                                    currentType = Nothing
                                    Exit Do
                                End If
                                If _setBoxBuffer(dec, collector.FreeStart, collector.FreeLength) <> StatusSuccess Then Exit Do
                            Case StatusSuccess
                                finishBox()
                                Exit Do
                            Case Else
                                Exit Do
                        End Select
                    Loop
                    Return (exif, xmp)
                Catch ex As Exception
                    DiagnosticLogService.LogException("JXL.ReadMetadataBoxes", ex)
                    Return (exif, xmp)
                Finally
                    If dec <> IntPtr.Zero Then _destroy(dec)
                    If typeBuffer <> IntPtr.Zero Then Marshal.FreeHGlobal(typeBuffer)
                    If pin.IsAllocated Then pin.Free()
                    collector.Dispose()
                End Try
            End SyncLock
        End Function

        ''' <summary>Eine EXIF-Box beginnt mit vier Byte, Big Endian: dem Abstand vom Ende dieser vier
        ''' Byte bis zum TIFF-Kopf. Meist null.</summary>
        Private Shared Function TiffFromExifBox(content As Byte()) As Byte()
            If content Is Nothing OrElse content.Length < 12 Then Return Nothing
            Dim offset = (CLng(content(0)) << 24) Or (CLng(content(1)) << 16) Or (CLng(content(2)) << 8) Or CLng(content(3))
            Dim start = 4L + offset
            If start < 4 OrElse start >= content.Length - 8 Then Return Nothing
            Dim tiff(CInt(content.Length - start) - 1) As Byte
            Buffer.BlockCopy(content, CInt(start), tiff, 0, tiff.Length)
            Return tiff
        End Function

        ''' <summary>Obergrenze fuer eine EXIF- oder XMP-Box. Echte Bloecke haben Kilobyte; eine
        ''' Box von vielen Megabyte ist kaputt oder boeswillig und wird nicht eingelesen.</summary>
        Private Const MaxMetadataBoxBytes As Integer = 16 * 1024 * 1024

        ''' <summary>Die Metadaten einer JPEG-XL-Datei in der Form, die MetadataExtractor sonst selbst
        ''' liefert. Der kennt JPEG XL nicht (2.9.3), wohl aber einen nackten TIFF-Block und XMP.
        ''' Damit sehen Infopanel, Katalog und die Uebernahme beim Speichern dieselben Verzeichnisse
        ''' wie bei jedem anderen Format.</summary>
        Public Shared Function ReadMetadataDirectories(path As String) As IReadOnlyList(Of MetadataExtractor.Directory)
            Dim result As New List(Of MetadataExtractor.Directory)()
            Dim boxes = ReadMetadataBoxes(path)
            Try
                If boxes.Exif IsNot Nothing Then
                    result.AddRange(New MetadataExtractor.Formats.Exif.ExifReader().Extract(
                        New MetadataExtractor.IO.ByteArrayReader(boxes.Exif), 0))
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("JXL.ReadMetadataDirectories.Exif", ex)
            End Try
            Try
                If boxes.Xmp IsNot Nothing Then
                    result.Add(New MetadataExtractor.Formats.Xmp.XmpReader().Extract(boxes.Xmp))
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("JXL.ReadMetadataDirectories.Xmp", ex)
            End Try
            Return result
        End Function

        ''' <summary>Das JPEG zurueck, aus dem diese Datei verlustfrei umgepackt wurde, Byte fuer Byte.
        ''' Nothing, wenn die Datei kein umgepacktes JPEG ist (dann fehlt die Rekonstruktionsbox und
        ''' das Ereignis kommt nicht).
        '''
        ''' Durch <see cref="DecodeGate"/>, weil libjxl dafuer den Bildstrom vollstaendig liest.</summary>
        Public Shared Function ReconstructJpeg(path As String) As Byte()
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            If _setJpegBuffer Is Nothing OrElse _releaseJpegBuffer Is Nothing Then Return Nothing
            Dim bytes As Byte()
            Try
                bytes = File.ReadAllBytes(path)
            Catch
                Return Nothing
            End Try
            Return DecodeGate.Run(Function()
                                      SyncLock _nativeLock
                                          Return ReconstructJpegCore(bytes)
                                      End SyncLock
                                  End Function)
        End Function

        Private Shared Function ReconstructJpegCore(bytes As Byte()) As Byte()
            Dim dec As IntPtr = IntPtr.Zero
            Dim pin As GCHandle
            Dim collector As New NativeGrowBuffer()
            Try
                dec = _create(IntPtr.Zero)
                If dec = IntPtr.Zero Then Return Nothing
                If _subscribeEvents(dec, StatusJpegReconstruction Or StatusFullImage) <> StatusSuccess Then Return Nothing
                pin = GCHandle.Alloc(bytes, GCHandleType.Pinned)
                If _setInput(dec, pin.AddrOfPinnedObject(), CType(bytes.Length, UIntPtr)) <> StatusSuccess Then Return Nothing
                _closeInput?.Invoke(dec)

                Dim reconstructing = False
                Do
                    Select Case _processInput(dec)
                        Case StatusJpegReconstruction
                            reconstructing = True
                            ' Ein umgepacktes JPEG ist etwas groesser als die JPEG-XL-Datei.
                            collector.Reset(bytes.Length + bytes.Length \ 2 + 64 * 1024)
                            If _setJpegBuffer(dec, collector.FreeStart, collector.FreeLength) <> StatusSuccess Then Return Nothing
                        Case StatusJpegNeedMoreOutput
                            collector.Release(_releaseJpegBuffer(dec))
                            If Not collector.Grow(Integer.MaxValue \ 2) Then Return Nothing
                            If _setJpegBuffer(dec, collector.FreeStart, collector.FreeLength) <> StatusSuccess Then Return Nothing
                        Case StatusFullImage, StatusSuccess
                            If Not reconstructing Then Return Nothing
                            collector.Release(_releaseJpegBuffer(dec))
                            Return collector.ToArray()
                        Case Else
                            ' Auch NEED_IMAGE_OUT_BUFFER landet hier: dann ist es kein umgepacktes
                            ' JPEG, und Bildpunkte waren nicht bestellt.
                            Return Nothing
                    End Select
                Loop
            Catch ex As Exception
                DiagnosticLogService.LogException("JXL.ReconstructJpeg", ex)
                Return Nothing
            Finally
                If dec <> IntPtr.Zero Then _destroy(dec)
                If pin.IsAllocated Then pin.Free()
                collector.Dispose()
            End Try
        End Function

        ''' <summary>Ein nativer Puffer, den libjxl stueckweise fuellt. Beim Freigeben meldet die
        ''' Bibliothek, wie viel vom zuletzt gereichten Bereich UNBENUTZT blieb; daraus ergibt sich,
        ''' wie weit der Puffer voll ist. Reicht er nicht, wird er verdoppelt und der Rest ab dem
        ''' Fuellstand neu gereicht.</summary>
        Private NotInheritable Class NativeGrowBuffer
            Implements IDisposable

            Private _base As IntPtr = IntPtr.Zero
            Private _capacity As Integer
            Private _filled As Integer

            Public ReadOnly Property FreeStart As IntPtr
                Get
                    Return _base + _filled
                End Get
            End Property

            Public ReadOnly Property FreeLength As UIntPtr
                Get
                    Return CType(CULng(_capacity - _filled), UIntPtr)
                End Get
            End Property

            Public Sub Reset(capacity As Integer)
                Dispose()
                _capacity = Math.Max(4096, capacity)
                _base = Marshal.AllocHGlobal(_capacity)
                _filled = 0
            End Sub

            ''' <summary>Uebernimmt die Rueckmeldung der Freigabe: so viele Byte blieben leer.</summary>
            Public Sub Release(unused As UIntPtr)
                Dim remaining = CLng(unused.ToUInt64())
                _filled = CInt(Math.Max(0L, Math.Min(_capacity, _capacity - remaining)))
            End Sub

            Public Function Grow(limit As Integer) As Boolean
                If _capacity >= limit Then Return False
                Dim newCapacity = CInt(Math.Min(CLng(limit), CLng(_capacity) * 2L))
                Dim newBase = Marshal.AllocHGlobal(newCapacity)
                Dim chunk(_filled - 1) As Byte
                If _filled > 0 Then
                    Marshal.Copy(_base, chunk, 0, _filled)
                    Marshal.Copy(chunk, 0, newBase, _filled)
                End If
                Marshal.FreeHGlobal(_base)
                _base = newBase
                _capacity = newCapacity
                Return True
            End Function

            Public Function ToArray() As Byte()
                Dim result(_filled - 1) As Byte
                If _filled > 0 Then Marshal.Copy(_base, result, 0, _filled)
                Return result
            End Function

            Public Sub Dispose() Implements IDisposable.Dispose
                If _base <> IntPtr.Zero Then Marshal.FreeHGlobal(_base)
                _base = IntPtr.Zero
                _capacity = 0
                _filled = 0
            End Sub
        End Class

    End Class

End Namespace
