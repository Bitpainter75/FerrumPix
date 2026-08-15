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
    ''' Auf macOS bringt das Betriebssystem den Dekoder selbst mit, dort ist HEIC das Standardformat
    ''' der Kamera und ein extra installiertes libheif die Ausnahme. Diesen Weg kapselt
    ''' MacImageIoService; findet er sich, wird er zuerst gefragt. Die Klasse hier fragt dafür nur
    ''' MacImageIoService.IsAvailable und kennt weder das Betriebssystem noch dessen Schnittstellen.
    ''' Meldet der Dienst False - auf Linux und Windows immer -, bleibt es beim libheif-Weg unten,
    ''' und für diese beiden Systeme ändert sich damit nichts.
    '''
    ''' Die Weiche steht JE DATEI und nicht je System: "die Frameworks sind geladen" heißt nicht
    ''' "diese Datei ist lesbar". Ein älteres macOS liest HEIC, aber kein AVIF, und liefert dafür
    ''' schlicht nichts zurück. Kommt vom Weg des Systems nichts, wird deshalb libheif gefragt,
    ''' sofern es da ist - sonst bliebe eine .avif leer, obwohl ein funktionierender Dekoder
    ''' installiert ist. Der Rückfall steht einmal je Programmlauf im Diagnoselog.
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
    ''' unverändert dynamisch geladen - dieselbe Konstruktion wie bei LibRaw und libmpv. Auch der
    ''' macOS-Weg ändert daran nichts: ImageIO gehört zum Betriebssystem und wird nicht mitgeliefert.
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
        ''' size_t, deshalb IntPtr und nicht Integer: auf 64 Bit ist der Rueckgabewert acht Byte
        ''' breit, und ein Integer laese die obere Haelfte des Registers stehen.
        Private Delegate Function GetProfileSizeFn(handle As IntPtr) As IntPtr
        Private Delegate Function GetRawProfileFn(handle As IntPtr, outData As IntPtr) As HeifError
        Private Delegate Function GetNclxProfileFn(handle As IntPtr, ByRef outProfile As IntPtr) As HeifError
        Private Delegate Sub FreeNclxProfileFn(profile As IntPtr)

        ''' <summary>Die Farbangabe eines HEIF als ZAHLEN statt als Profildatei.
        '''
        ''' Die drei Kennungen sind in C Aufzaehlungen, also je vier Byte; das Byte davor und das
        ''' danach werden von .NET genauso aufgefuellt wie vom C-Uebersetzer. Die Gleitkommafelder
        ''' ab Fassung 2 stehen dahinter und werden nicht gebraucht - sie muessen deshalb auch nicht
        ''' aufgefuehrt werden, gelesen wird ja nur der Anfang.</summary>
        <StructLayout(LayoutKind.Sequential)>
        Private Structure HeifNclx
            Public Version As Byte
            Public ColorPrimaries As Integer
            Public TransferCharacteristics As Integer
            Public MatrixCoefficients As Integer
            Public FullRangeFlag As Byte
        End Structure

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
        ''' langsamere Thumbnails als sporadische Abstürze. Der Weg über das Betriebssystem
        ''' liegt bewusst unter demselben Schloss: ImageIO wäre zwar threadsicher, aber in der
        ''' ganzen Anwendung soll immer nur EIN Decode gleichzeitig laufen.
        '''
        ''' Dieses Schloss deckt aber nur DIESEN Dienst. Die Regel gilt für die ganze Anwendung,
        ''' und dafür ist <see cref="DecodeGate"/> zuständig - die teuren Wege hier laufen deshalb
        ''' durch die Schleuse und erst darunter durch dieses Schloss. Die Reihenfolge ist immer
        ''' dieselbe (erst Schleuse, dann Schloss), sonst könnten sich zwei Fäden verhaken. Steht
        ''' ein Aufrufer schon in der Schleuse, ist der zweite Eintritt unschädlich: sie ist ein
        ''' Sperrblock und lässt denselben Faden durch.
        Private Shared ReadOnly _nativeLock As New Object()

        ''' <summary>Wurde der Rückfall vom Weg des Systems auf libheif schon gemeldet? Einmal je
        ''' Programmlauf genügt - bei einem Ordner voller AVIF stünde dieselbe Zeile sonst
        ''' hundertfach im Log und deckte alles andere zu.</summary>
        Private Shared _fallbackLogged As Integer

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
        ''' Die beiden fuer das Farbprofil sind OPTIONAL: fehlen sie in einer aelteren libheif,
        ''' bleibt HEIF vollstaendig nutzbar und nur das Farbmanagement entfaellt. Sie duerfen
        ''' deshalb nicht im selben Block geladen werden wie die Pflichtexporte, wo ein Fehlgriff
        ''' die ganze Bibliothek verwirft.
        Private Shared _profileSize As GetProfileSizeFn
        Private Shared _rawProfile As GetRawProfileFn
        Private Shared _nclxProfile As GetNclxProfileFn
        Private Shared _freeNclx As FreeNclxProfileFn

        Private Shared Sub NoteFallbackToLibheif(what As String)
            If Threading.Interlocked.Exchange(_fallbackLogged, 1) = 0 Then
                DiagnosticLogService.LogAlways("HEIF",
                    $"{MacImageIoService.DecoderName} liefert für {what} nichts - es wird libheif genommen")
            End If
        End Sub

        ''' <summary>Steht der libheif-Weg wirklich bereit? Auf macOS meldet IsAvailable schon True,
        ''' wenn nur die Frameworks des Systems geladen sind - dann sind die Zeiger hier unten leer,
        ''' und ein Aufruf liefe ins Nichts.</summary>
        Private Shared ReadOnly Property LibheifReady As Boolean
            Get
                EnsureLoaded()
                Return _library <> IntPtr.Zero
            End Get
        End Property

        ''' <summary>True, wenn irgendein Dekoder bereitsteht: der des Betriebssystems oder libheif
        ''' (Ergebnis wird gecacht). Die Reihenfolge ist Absicht - liegt auf einem Mac zusätzlich
        ''' ein libheif aus Homebrew, bleibt trotzdem der Weg des Systems der erste, und fehlen
        ''' dort die Frameworks, greift libheif als Rückfall.</summary>
        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                If MacImageIoService.IsAvailable Then Return True
                EnsureLoaded()
                Return _library <> IntPtr.Zero
            End Get
        End Property

        ''' <summary>Welcher Dekoder geladen wurde - für die Diagnose und Feldberichte.</summary>
        Public Shared ReadOnly Property LoadedLibraryName As String
            Get
                If MacImageIoService.IsAvailable Then Return MacImageIoService.DecoderName
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
                    Try
                        _profileSize = GetExport(Of GetProfileSizeFn)(handle, "heif_image_handle_get_raw_color_profile_size")
                        _rawProfile = GetExport(Of GetRawProfileFn)(handle, "heif_image_handle_get_raw_color_profile")
                    Catch
                        ' Aeltere libheif ohne diese Exporte: HEIF bleibt nutzbar, nur unverwaltet.
                        _profileSize = Nothing
                        _rawProfile = Nothing
                        DiagnosticLogService.LogAlways("HEIF",
                            "libheif kennt die Profilabfrage nicht - HEIC wird ohne Farbmanagement gelesen")
                    End Try
                    Try
                        _nclxProfile = GetExport(Of GetNclxProfileFn)(handle, "heif_image_handle_get_nclx_color_profile")
                        _freeNclx = GetExport(Of FreeNclxProfileFn)(handle, "heif_nclx_color_profile_free")
                    Catch
                        ' Ebenfalls optional: ohne sie bleibt nur der Weg ueber ein ICC-Profil.
                        _nclxProfile = Nothing
                        _freeNclx = Nothing
                    End Try
                Catch
                    ' Ein fehlender Export = Bibliothek unbrauchbar; alles auf Anfang.
                    _contextAlloc = Nothing : _contextFree = Nothing : _readFromFile = Nothing
                    _getPrimaryHandle = Nothing : _decodeImage = Nothing : _getPlane = Nothing
                    _imageGetWidth = Nothing : _imageGetHeight = Nothing
                    _handleGetWidth = Nothing : _handleGetHeight = Nothing
                    _handleRelease = Nothing : _imageRelease = Nothing
                    _profileSize = Nothing : _rawProfile = Nothing
                    _nclxProfile = Nothing : _freeNclx = Nothing
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
        ''' Nothing. Drehung/Spiegelung aus dem Container wenden beide Wege selbst an (libheif über
        ''' seine Standard-Dekodieroptionen, ImageIO über kCGImageSourceCreateThumbnailWithTransform)
        ''' - es braucht also KEINE zusätzliche Orientierungskorrektur, anders als beim eingebetteten
        ''' RAW-Vorschaubild.</summary>
        Public Shared Function TryDecode(path As String) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            Return DecodeGate.Run(Function() DecodeWithFallback(path))
        End Function

        ''' <summary>Erst der Weg des Systems, bei leerem Ergebnis libheif.</summary>
        Private Shared Function DecodeWithFallback(path As String) As SKBitmap
            SyncLock _nativeLock
                If MacImageIoService.IsAvailable Then
                    Using preview = MacImageIoService.TryExtractPng(path)
                        If preview IsNot Nothing Then
                            Dim bitmap = DecodeToBgra(preview)
                            If bitmap IsNot Nothing Then Return bitmap
                        End If
                    End Using
                    If Not LibheifReady Then Return Nothing
                    NoteFallbackToLibheif("das Bild")
                End If
                If Not LibheifReady Then Return Nothing
                Return DecodeCore(path)
            End SyncLock
        End Function

        ''' <summary>Einen PNG-Strom in genau das Format bringen, das der libheif-Weg liefert.
        ''' Ohne die feste SKImageInfo entschiede Skia selbst, und die Zusage "Bgra8888" oben
        ''' gälte je nach Plattform - ein Fehler, den man erst an vertauschten Farben sähe.</summary>
        Private Shared Function DecodeToBgra(stream As Stream) As SKBitmap
            Using data = SKData.Create(stream)
                If data Is Nothing Then Return Nothing
                Using codec = SKCodec.Create(data)
                    If codec Is Nothing Then Return Nothing
                    Dim info = New SKImageInfo(codec.Info.Width, codec.Info.Height,
                                               SKColorType.Bgra8888, SKAlphaType.Unpremul)
                    If info.Width <= 0 OrElse info.Height <= 0 Then Return Nothing
                    Dim bitmap = New SKBitmap(info)
                    Dim result = codec.GetPixels(info, bitmap.GetPixels())
                    If result <> SKCodecResult.Success AndAlso result <> SKCodecResult.IncompleteInput Then
                        bitmap.Dispose()
                        Return Nothing
                    End If
                    ' HEIC vom Telefon traegt haeufig Display P3. Ohne diese Wandlung kaeme es als
                    ' sRGB an und saehe zu blass aus (siehe ColorManagementService).
                    Dim managed = ColorManagementService.ToSrgb(bitmap, codec.Info.ColorSpace)
                    If Not Object.ReferenceEquals(managed, bitmap) Then bitmap.Dispose()
                    Return managed
                End Using
            End Using
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

                    ' Farbmanagement: eine HEIC vom Telefon traegt haeufig ein weiteres Profil als
                    ' sRGB. Ohne die Wandlung kaemen die Zahlen als sRGB an und das Bild saehe zu
                    ' blass aus (siehe ColorManagementService). Das Profil wird danach freigegeben:
                    ' es ist ein eigens erzeugtes natives Objekt, und ein Kachellauf ueber einen
                    ' Ordner voller HEIC erzeugt eines je Datei.
                    Using profile = ReadColorProfile(handle)
                        Dim managed = ColorManagementService.ToSrgb(bitmap, profile)
                        If Not Object.ReferenceEquals(managed, bitmap) Then bitmap.Dispose()
                        Return managed
                    End Using
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

        ''' <summary>Der Farbraum der Aufnahme, oder Nothing.
        '''
        ''' Zwei Wege, in dieser Reihenfolge: ein eingebettetes ICC-Profil, sonst die nclx-Angabe.
        ''' Das ICC hat Vorrang, weil es die genauere Auskunft ist - nclx nennt nur Kennzahlen aus
        ''' einer Tabelle, ein Profil beschreibt die Kurve selbst.</summary>
        Private Shared Function ReadColorProfile(handle As IntPtr) As SKColorSpace
            Dim icc = ReadIccProfile(handle)
            If icc IsNot Nothing Then Return icc
            Return ReadNclxProfile(handle)
        End Function

        ''' <summary>Die nclx-Angabe als Farbraum, oder Nothing.
        '''
        ''' Ein HEIF kann seinen Farbraum als Zahlenschluessel angeben statt als Profildatei, und
        ''' Apple tut genau das fuer Display P3. Die Schluessel stammen aus H.273; abgebildet werden
        ''' die Faelle, die in Fotos vorkommen. Alles andere liefert Nothing und die Datei bleibt
        ''' unverwaltet - lieber unverwaltet als nach einer geratenen Kurve verbogen.
        '''
        ''' sRGB (Primaerfarben 1 mit Kurve 13) kommt ebenfalls als Nothing zurueck: da ist nichts
        ''' zu wandeln, und der Aufrufer spart sich die Kopie.</summary>
        Private Shared Function ReadNclxProfile(handle As IntPtr) As SKColorSpace
            If handle = IntPtr.Zero OrElse _nclxProfile Is Nothing Then Return Nothing
            Dim raw As IntPtr = IntPtr.Zero
            Try
                If _nclxProfile(handle, raw).Code <> 0 OrElse raw = IntPtr.Zero Then Return Nothing
                Dim nclx = Marshal.PtrToStructure(Of HeifNclx)(raw)

                ' Grobe Plausibilitaet: die Schluessel von H.273 liegen in diesem Bereich. Kommt
                ' etwas anderes an, stimmt die Annahme ueber den Aufbau der Struktur nicht - dann
                ' lieber gar nichts tun als nach Zufallszahlen zu rechnen.
                If nclx.ColorPrimaries < 0 OrElse nclx.ColorPrimaries > 22 Then Return Nothing
                If nclx.TransferCharacteristics < 0 OrElse nclx.TransferCharacteristics > 18 Then Return Nothing

                Dim primaries As SKColorSpaceXyz
                Select Case nclx.ColorPrimaries
                    Case 1, 2, 0 : primaries = SKColorSpaceXyz.Srgb   ' 2 und 0 heissen "unbekannt"
                    Case 12 : primaries = SKColorSpaceXyz.DisplayP3
                    Case 9 : primaries = SKColorSpaceXyz.Rec2020
                    Case Else : Return Nothing
                End Select

                Dim transfer As SKColorSpaceTransferFn
                Select Case nclx.TransferCharacteristics
                    Case 13, 1, 6, 14, 15, 2, 0 : transfer = SKColorSpaceTransferFn.Srgb
                    Case 8 : transfer = SKColorSpaceTransferFn.Linear
                    Case Else : Return Nothing
                End Select

                ' Ist beides sRGB, gibt es nichts zu tun.
                If nclx.ColorPrimaries <= 2 AndAlso nclx.TransferCharacteristics <= 15 AndAlso
                   nclx.TransferCharacteristics <> 8 Then Return Nothing

                Dim space = SKColorSpace.CreateRgb(transfer, primaries)
                If space Is Nothing Then Return Nothing
                DiagnosticLogService.LogAlways("HEIF",
                    $"nclx gelesen: Primaerfarben {nclx.ColorPrimaries}, Kurve {nclx.TransferCharacteristics}" &
                    $" - {ColorManagementService.DescribeProfile(space)}")
                Return space
            Catch ex As Exception
                DiagnosticLogService.LogException("HEIF.ReadNclxProfile", ex)
                Return Nothing
            Finally
                If raw <> IntPtr.Zero Then _freeNclx?.Invoke(raw)
            End Try
        End Function

        ''' <summary>Das eingebettete ICC-Profil der Aufnahme, oder Nothing.</summary>
        Private Shared Function ReadIccProfile(handle As IntPtr) As SKColorSpace
            If handle = IntPtr.Zero OrElse _profileSize Is Nothing OrElse _rawProfile Is Nothing Then Return Nothing
            Dim buffer As IntPtr = IntPtr.Zero
            Try
                Dim size = _profileSize(handle).ToInt64()
                ' Null heisst schlicht "kein ICC an dieser Datei". Die obere Schranke faengt einen
                ' unsinnigen Wert ab, bevor daraus eine Speicheranforderung wird.
                If size <= 0 OrElse size > 16 * 1024 * 1024 Then Return Nothing

                Dim length = CInt(size)
                buffer = Marshal.AllocCoTaskMem(length)
                If _rawProfile(handle, buffer).Code <> 0 Then Return Nothing

                Dim bytes(length - 1) As Byte
                Marshal.Copy(buffer, bytes, 0, length)
                Dim profile = SKColorSpace.CreateIcc(bytes)
                If profile Is Nothing Then Return Nothing
                DiagnosticLogService.LogAlways("HEIF", $"ICC-Profil gelesen: {ColorManagementService.DescribeProfile(profile)}")
                Return profile
            Catch ex As Exception
                DiagnosticLogService.LogException("HEIF.ReadIccProfile", ex)
                Return Nothing
            Finally
                If buffer <> IntPtr.Zero Then Marshal.FreeCoTaskMem(buffer)
            End Try
        End Function

        ''' <summary>Die Datei als PNG-Strom - das ist die Form, die OpenSourceStream und die
        ''' Thumbnail-Erzeugung erwarten (gleiches Muster wie PSD/ICO).</summary>
        Public Shared Function ExtractPreview(path As String) As MemoryStream
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            Return DecodeGate.Run(Function() ExtractPreviewWithFallback(path))
        End Function

        Private Shared Function ExtractPreviewWithFallback(path As String) As MemoryStream
            SyncLock _nativeLock
                ' Der Weg des Systems liefert den PNG-Strom direkt, ohne den Umweg über ein SKBitmap.
                If MacImageIoService.IsAvailable Then
                    Dim direct = MacImageIoService.TryExtractPng(path)
                    If direct IsNot Nothing Then Return direct
                    If Not LibheifReady Then Return Nothing
                    NoteFallbackToLibheif("die Vorschau")
                End If
                If Not LibheifReady Then Return Nothing

                Using bmp = DecodeCore(path)
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
            End SyncLock
        End Function

        ''' <summary>Maße ohne vollständiges Dekodieren - die Kopfdaten reichen dafür.
        '''
        ''' Bewusst NICHT durch <see cref="DecodeGate"/>: das hier ist kein teurer Bildweg, sondern
        ''' ein Blick in den Container, und die Frage nach den Maßen wird auch aus der Oberfläche
        ''' gestellt. Sie hinter einem laufenden RAW-Decode warten zu lassen, kostete Sekunden für
        ''' eine Auskunft von Millisekunden. Gegen gleichzeitige native Aufrufe schützt das Schloss
        ''' dieses Dienstes.</summary>
        Public Shared Function TryGetSize(path As String) As (Width As Integer, Height As Integer)
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return (0, 0)
            SyncLock _nativeLock
                If MacImageIoService.IsAvailable Then
                    Dim size = MacImageIoService.TryGetSize(path)
                    If size.Width > 0 AndAlso size.Height > 0 Then Return size
                    If Not LibheifReady Then Return (0, 0)
                    NoteFallbackToLibheif("die Maße")
                End If
                If Not LibheifReady Then Return (0, 0)

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
