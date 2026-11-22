Imports System
Imports System.IO
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Liest HEIC/HEIF und AVIF über libheif - SkiaSharp kann beides nicht. Die mitgelieferte
    ''' libheif bringt für HEIC libde265 und für AVIF dav1d mit (packaging/libheif/dav1d.sh), für
    ''' Bilder, die eine HEIF als JPEG trägt (die Vorschaubilder von Fujifilm), ihren eingebauten
    ''' JPEG-Dekoder gegen eine eigene libjpeg (packaging/libheif/jpeg.sh); fehlt
    ''' einer Bibliothek der Dekoder, scheitert heif_decode_image, und die Datei bleibt leer. Damit lassen sich iPhone-Fotos direkt ansehen und bearbeiten,
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
    ''' WOHER DIE BIBLIOTHEK KOMMT, hängt von der Plattform ab:
    '''
    '''   Linux    AppImage, portables ZIP (und damit AUR), deb und rpm bringen eine eigene
    '''            libheif.so.1 mit libde265, dav1d und libjpeg darin mit (packaging/libheif/build-linux.sh); sie geht
    '''            der des Systems vor (siehe BundledLibraryOrder), die nur noch Rueckfall ist. Der
    '''            Flatpak nimmt libheif aus seiner Laufzeit.
    '''   macOS    zuerst ImageIO (siehe oben). Als Rueckfall dahinter bringt das Mac-Paket eine
    '''            libheif.dylib im Zuschnitt der Windows-Datei mit, unter runtimes/osx-*/native
    '''            (packaging/libheif/build-macos.sh). Sie geht einer libheif aus Homebrew vor.
    '''   Windows  MITGELIEFERT, seit dem 26. August 2026. Vorher lag dort nichts bei, mit dem
    '''            Codec als Begründung: HEIC ist in aller Regel HEVC-kodiert, HEVC ist
    '''            patentbelastet, und diese Entscheidung sollte die Distribution treffen.
    '''            Nur gibt es unter Windows keine, die sie treffen könnte - und dasselbe Paket
    '''            bringt mit libmpv längst einen HEVC-Dekoder für Video mit. Die Zurückhaltung
    '''            bei Standbildern stand damit neben einer Datei, die für Video dasselbe tut.
    '''            Übrig blieb ein Anwender, der sich eine libheif.dll aus dem Netz suchen musste
    '''            und mit der üblichen Einzeldatei scheiterte, weil ihr Dekoder und Laufzeit
    '''            fehlen. Gebaut wird sie deshalb selbst, als EINE Datei ohne Beigaben und ohne
    '''            Encoder: packaging/libheif/build-windows.sh.
    '''
    ''' Lizenzseitig ist alles unkritisch: libheif und libde265 stehen unter der LGPL-3 und werden
    ''' unverändert dynamisch geladen - dieselbe Konstruktion wie bei LibRaw und libmpv, und die
    ''' mitgelieferte Datei lässt sich schlicht überschreiben. Version, Prüfsummen und Bauoptionen
    ''' stehen in licenses/HERKUNFT.txt neben der DLL.
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
        ''' Die eingebetteten Vorschaubilder. heif_item_id ist uint32, deshalb UInteger.
        Private Delegate Function GetThumbnailIdsFn(handle As IntPtr, ids As UInteger(), count As Integer) As Integer
        Private Delegate Function GetThumbnailFn(handle As IntPtr, id As UInteger, ByRef thumbnail As IntPtr) As HeifError
        ''' Die Transformationen eines Eintrags (irot, imir, clap) in der Reihenfolge der Datei, fuer
        ''' das Vorschaubild. heif_property_id ist wie heif_item_id ein uint32; die Aufzaehlungen
        ''' fuer Art und Spiegelrichtung sind je vier Byte. Mit outList = Nothing liefert die
        ''' Abfrage nur die Anzahl.
        Private Delegate Function GetItemIdFn(handle As IntPtr) As UInteger
        Private Delegate Function GetTransformPropertiesFn(ctx As IntPtr, itemId As UInteger, outList As UInteger(), count As Integer) As Integer
        Private Delegate Function GetPropertyIntFn(ctx As IntPtr, itemId As UInteger, propertyId As UInteger) As Integer
        ''' Die Faeden INNERHALB eines Decodes (siehe <see cref="NewDecodingContext"/> und
        ''' <see cref="DecodeImageThreaded"/>). Die Rasterabfrage schreibt in eine Struktur, die der
        ''' Aufrufer anlegt (<see cref="CodecThreadsFor"/>).
        Private Delegate Sub SetMaxDecodingThreadsFn(ctx As IntPtr, maxThreads As Integer)
        Private Delegate Function DecodingOptionsAllocFn() As IntPtr
        Private Delegate Sub DecodingOptionsFreeFn(options As IntPtr)
        Private Delegate Function GetImageTilingFn(handle As IntPtr, processTransformations As Integer, tiling As IntPtr) As HeifError

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
        ''' heif_chroma_interleaved_RRGGBBAA_LE: je Kanal zwei Byte, kleines Ende zuerst, die Werte
        ''' in der Tiefe der Datei (bei 10 Bit also 0 bis 1023), nicht auf 16 Bit gestreckt.
        Private Const ChromaInterleavedRrggbbaaLe As Integer = 15

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
        ''' Der Handle der geladenen libheif (der Pruefstand liest ihn per Reflexion).
        Private Shared _library As IntPtr
        Private Shared _loadedLibrary As String

        ''' <summary>Die Einstiege der geladenen libheif (<see cref="_api"/>). Jeder Weg unten
        ''' bekommt sie als Parameter, statt einzelne geteilte Felder zu lesen.</summary>
        Private NotInheritable Class LibheifApi
            Public Library As IntPtr
            Public ContextAlloc As ContextAllocFn
            Public ContextFree As ContextFreeFn
            Public ReadFromFile As ReadFromFileFn
            Public GetPrimaryHandle As GetPrimaryHandleFn
            Public DecodeImage As DecodeImageFn
            Public GetPlane As GetPlaneFn
            Public ImageGetWidth As GetIntFn
            Public ImageGetHeight As GetIntFn
            Public HandleGetWidth As HandleIntFn
            Public HandleGetHeight As HandleIntFn
            Public HandleRelease As ReleaseFn
            Public ImageRelease As ReleaseFn
            ''' Die beiden fuer das Farbprofil sind OPTIONAL: fehlen sie in einer aelteren libheif,
            ''' bleibt HEIF vollstaendig nutzbar und nur das Farbmanagement entfaellt. Sie duerfen
            ''' deshalb nicht im selben Block geladen werden wie die Pflichtexporte, wo ein Fehlgriff
            ''' die ganze Bibliothek verwirft.
            Public ProfileSize As GetProfileSizeFn
            Public RawProfile As GetRawProfileFn
            Public NclxProfile As GetNclxProfileFn
            Public FreeNclx As FreeNclxProfileFn
            ''' Ebenfalls OPTIONAL: ohne sie gibt es keine schnelle Kachel, sonst aendert sich nichts.
            Public ThumbnailCount As HandleIntFn
            Public ThumbnailIds As GetThumbnailIdsFn
            Public Thumbnail As GetThumbnailFn
            ''' Ebenfalls OPTIONAL (aeltere libheif kennen sie nicht): die Transformationen eines Eintrags. Ohne sie
            ''' kommt das Vorschaubild so, wie libheif es liefert, auch wenn es die Drehung des
            ''' Hauptbilds nicht traegt.
            Public ItemId As GetItemIdFn
            Public TransformProperties As GetTransformPropertiesFn
            Public PropertyType As GetPropertyIntFn
            Public RotationCcw As GetPropertyIntFn
            Public MirrorDirection As GetPropertyIntFn
            ''' Ebenfalls OPTIONAL, fuer Bilder mit mehr als 8 Bit: Tiefe der Aufnahme, ob sie Alpha
            ''' traegt, und die Tiefe des dekodierten Bilds. Fehlen sie, wird jedes Bild mit 8 Bit
            ''' gelesen wie bisher.
            Public LumaBits As HandleIntFn
            Public HasAlpha As HandleIntFn
            Public BitsRange As GetIntFn
            ''' Ebenfalls OPTIONAL, fuer die Faeden eines Decodes: ohne die erste dekodiert libheif
            ''' die Kacheln eines Rasters mit seiner Vorgabe (vier Faeden), ohne die drei anderen
            ''' laeuft der Codec mit seiner eigenen Vorgabe. Die Bildpunkte sind dieselben.
            Public SetMaxDecodingThreads As SetMaxDecodingThreadsFn
            Public DecodingOptionsAlloc As DecodingOptionsAllocFn
            Public DecodingOptionsFree As DecodingOptionsFreeFn
            Public ImageTiling As GetImageTilingFn
        End Class

        Private Shared _api As LibheifApi

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

        ''' <summary>Erst der blosse Name, dann die beiden Orte, an denen eine mitgelieferte
        ''' Bibliothek liegen kann: neben der Anwendung und unter runtimes/&lt;rid&gt;/native, wohin
        ''' packaging/package.sh sie legt.
        '''
        ''' DER BLOSSE NAME REICHT NICHT, und das hat gekostet: die Windows-Suche kennt das
        ''' Verzeichnis der Anwendung, aber NICHT den runtimes-Unterordner. Die mitgelieferte
        ''' libheif.dll lag dort und wurde nie gefunden; erst von Hand ins Hauptverzeichnis kopiert
        ''' lief sie (Befund vom 2026-08-26). RawDecodeService und MpvInterop bauen dieselbe Liste,
        ''' nur diese Stelle tat es nicht.
        '''
        ''' Der Name ZUERST: eine vom System gepflegte Bibliothek hat Vorrang vor der
        ''' mitgelieferten - unter Linux ist sie die einzige, und dort soll die Paketverwaltung
        ''' bestimmen, welche Fassung läuft.</summary>
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
                Yield name
                ' Unter macOS zusaetzlich die beiden Homebrew-Verzeichnisse mit vollem Pfad. Eine
                ' aus dem Finder gestartete .app erbt keinen Homebrew-Pfad, und dyld sucht einen
                ' Namen ohne Schraegstrich nur in $HOME/lib, /usr/local/lib und /usr/lib - auf Apple
                ' Silicon liegt Homebrew aber unter /opt/homebrew/lib. Dieselbe Stelle gibt es bei
                ' libmpv und libraw; hier fehlte sie.
                If OperatingSystem.IsMacOS() Then
                    Yield IO.Path.Combine("/opt/homebrew/lib", name)
                    Yield IO.Path.Combine("/usr/local/lib", name)
                End If
                Yield IO.Path.Combine(baseDir, name)
                If rid.Length > 0 Then Yield IO.Path.Combine(baseDir, "runtimes", rid, "native", name)
            Next
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
                    ' Dev-Paket. Mitgeliefert ist sie in der AppImage und im portablen ZIP, mit
                    ' libde265 in derselben Datei (packaging/libheif/build-linux.sh).
                    candidates = {"libheif.so.1", "libheif.so"}
                End If

                Dim handle As IntPtr
                ' Der Mutationstest zu dieser Stelle: nimmt man BundledCandidates wieder heraus,
                ' bleibt die Prüfung "Native Bibliotheken werden dort gesucht, wo das Paket sie
                ' hinlegt" rot - sie ruft diese Liste per Reflexion ab. Die mitgelieferte kommt vor
                ' der des Systems (BundledLibraryOrder): ueberall dieselbe Fassung, Ubuntu 22.04
                ' fuehrt nur libheif 1.12, und nicht ueberall ist sie samt libde265 installiert.
                For Each candidate In BundledLibraryOrder.Arrange(BundledCandidates(candidates))
                    If NativeLibrary.TryLoad(candidate, handle) Then
                        _loadedLibrary = IO.Path.GetFileName(candidate)
                        Exit For
                    End If
                    handle = IntPtr.Zero
                Next
                If handle = IntPtr.Zero Then
                    ' EINE ZEILE INS PROTOKOLL, und sie ist mehr wert als sie aussieht: bis hierher
                    ' schwieg dieser Weg. Wer eine libheif danebenlegte, die wegen einer fehlenden
                    ' Begleitbibliothek nicht lud, sah genau dasselbe wie jemand ohne Bibliothek -
                    ' nämlich nichts, und hatte keine Möglichkeit, den Unterschied zu erkennen.
                    DiagnosticLogService.LogAlways("HEIF",
                        "kein HEIC-Dekoder geladen - vergeblich versucht: " & String.Join(", ", candidates) &
                        ". Unter Windows liegt libheif bei; unter Linux kommt sie aus der Paketverwaltung.")
                    Return
                End If

                Dim api = Bind(handle)
                If api Is Nothing Then
                    ' Ein fehlender Export = Bibliothek unbrauchbar; alles auf Anfang.
                    NativeLibrary.Free(handle)
                    _library = IntPtr.Zero
                    _loadedLibrary = Nothing
                    Return
                End If
                _api = api
                _library = handle
            End SyncLock
        End Sub

        ''' <summary>Die Einstiege der geladenen libheif, oder Nothing, wenn ein Pflichtexport
        ''' fehlt.</summary>
        Private Shared Function Bind(handle As IntPtr) As LibheifApi
            Dim api As New LibheifApi With {.Library = handle}
            Try
                api.ContextAlloc = GetExport(Of ContextAllocFn)(handle, "heif_context_alloc")
                api.ContextFree = GetExport(Of ContextFreeFn)(handle, "heif_context_free")
                api.ReadFromFile = GetExport(Of ReadFromFileFn)(handle, "heif_context_read_from_file")
                api.GetPrimaryHandle = GetExport(Of GetPrimaryHandleFn)(handle, "heif_context_get_primary_image_handle")
                api.DecodeImage = GetExport(Of DecodeImageFn)(handle, "heif_decode_image")
                api.GetPlane = GetExport(Of GetPlaneFn)(handle, "heif_image_get_plane_readonly")
                api.ImageGetWidth = GetExport(Of GetIntFn)(handle, "heif_image_get_width")
                api.ImageGetHeight = GetExport(Of GetIntFn)(handle, "heif_image_get_height")
                api.HandleGetWidth = GetExport(Of HandleIntFn)(handle, "heif_image_handle_get_width")
                api.HandleGetHeight = GetExport(Of HandleIntFn)(handle, "heif_image_handle_get_height")
                api.HandleRelease = GetExport(Of ReleaseFn)(handle, "heif_image_handle_release")
                api.ImageRelease = GetExport(Of ReleaseFn)(handle, "heif_image_release")
            Catch
                Return Nothing
            End Try
            ' Aeltere libheif ohne diese Exporte: HEIF bleibt nutzbar, nur unverwaltet.
            api.ProfileSize = TryGetExport(Of GetProfileSizeFn)(handle, "heif_image_handle_get_raw_color_profile_size")
            api.RawProfile = TryGetExport(Of GetRawProfileFn)(handle, "heif_image_handle_get_raw_color_profile")
            If api.ProfileSize Is Nothing OrElse api.RawProfile Is Nothing Then
                api.ProfileSize = Nothing
                api.RawProfile = Nothing
                DiagnosticLogService.LogAlways("HEIF",
                    "libheif kennt die Profilabfrage nicht - HEIC wird ohne Farbmanagement gelesen")
            End If
            ' Ebenfalls optional: ohne sie bleibt nur der Weg ueber ein ICC-Profil.
            api.NclxProfile = TryGetExport(Of GetNclxProfileFn)(handle, "heif_image_handle_get_nclx_color_profile")
            api.FreeNclx = TryGetExport(Of FreeNclxProfileFn)(handle, "heif_nclx_color_profile_free")
            If api.NclxProfile Is Nothing OrElse api.FreeNclx Is Nothing Then
                api.NclxProfile = Nothing
                api.FreeNclx = Nothing
            End If
            ' Optional: ohne sie wird jede Kachel aus dem vollen Bild gerechnet.
            api.ThumbnailCount = TryGetExport(Of HandleIntFn)(handle, "heif_image_handle_get_number_of_thumbnails")
            api.ThumbnailIds = TryGetExport(Of GetThumbnailIdsFn)(handle, "heif_image_handle_get_list_of_thumbnail_IDs")
            api.Thumbnail = TryGetExport(Of GetThumbnailFn)(handle, "heif_image_handle_get_thumbnail")
            If api.ThumbnailCount Is Nothing OrElse api.ThumbnailIds Is Nothing OrElse api.Thumbnail Is Nothing Then
                api.ThumbnailCount = Nothing
                api.ThumbnailIds = Nothing
                api.Thumbnail = Nothing
            End If
            ' Optional: ohne sie bleibt ein Vorschaubild, das die Drehung des Hauptbilds nicht
            ' traegt, liegen, wie es gespeichert ist.
            api.ItemId = TryGetExport(Of GetItemIdFn)(handle, "heif_image_handle_get_item_id")
            api.TransformProperties = TryGetExport(Of GetTransformPropertiesFn)(handle, "heif_item_get_transformation_properties")
            api.PropertyType = TryGetExport(Of GetPropertyIntFn)(handle, "heif_item_get_property_type")
            api.RotationCcw = TryGetExport(Of GetPropertyIntFn)(handle, "heif_item_get_property_transform_rotation_ccw")
            api.MirrorDirection = TryGetExport(Of GetPropertyIntFn)(handle, "heif_item_get_property_transform_mirror")
            If api.ItemId Is Nothing OrElse api.TransformProperties Is Nothing OrElse api.PropertyType Is Nothing OrElse
               api.RotationCcw Is Nothing OrElse api.MirrorDirection Is Nothing Then
                api.ItemId = Nothing
                api.TransformProperties = Nothing
                api.PropertyType = Nothing
                api.RotationCcw = Nothing
                api.MirrorDirection = Nothing
            End If
            ' Optional: ohne sie kommt jedes Bild mit 8 Bit, wie vorher.
            api.LumaBits = TryGetExport(Of HandleIntFn)(handle, "heif_image_handle_get_luma_bits_per_pixel")
            api.HasAlpha = TryGetExport(Of HandleIntFn)(handle, "heif_image_handle_has_alpha_channel")
            api.BitsRange = TryGetExport(Of GetIntFn)(handle, "heif_image_get_bits_per_pixel_range")
            If api.LumaBits Is Nothing OrElse api.HasAlpha Is Nothing OrElse api.BitsRange Is Nothing Then
                api.LumaBits = Nothing
                api.HasAlpha = Nothing
                api.BitsRange = Nothing
            End If
            ' Optional: fehlen sie, bleibt an ihrer Stelle die Vorgabe der Bibliothek.
            api.SetMaxDecodingThreads = TryGetExport(Of SetMaxDecodingThreadsFn)(handle, "heif_context_set_max_decoding_threads")
            api.DecodingOptionsAlloc = TryGetExport(Of DecodingOptionsAllocFn)(handle, "heif_decoding_options_alloc")
            api.DecodingOptionsFree = TryGetExport(Of DecodingOptionsFreeFn)(handle, "heif_decoding_options_free")
            api.ImageTiling = TryGetExport(Of GetImageTilingFn)(handle, "heif_image_handle_get_image_tiling")
            If api.DecodingOptionsAlloc Is Nothing OrElse api.DecodingOptionsFree Is Nothing OrElse api.ImageTiling Is Nothing Then
                api.DecodingOptionsAlloc = Nothing
                api.DecodingOptionsFree = Nothing
                api.ImageTiling = Nothing
            End If
            Return api
        End Function

        Private Shared Function GetExport(Of T)(handle As IntPtr, name As String) As T
            Return Marshal.GetDelegateForFunctionPointer(Of T)(NativeLibrary.GetExport(handle, name))
        End Function

        ''' <summary>Ein optionaler Export als Delegat, oder Nothing, wenn die Bibliothek ihn nicht
        ''' kennt.</summary>
        Private Shared Function TryGetExport(Of T As Class)(handle As IntPtr, name As String) As T
            Dim address As IntPtr
            If Not NativeLibrary.TryGetExport(handle, name, address) OrElse address = IntPtr.Zero Then Return Nothing
            Return Marshal.GetDelegateForFunctionPointer(Of T)(address)
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
                Return DecodeCore(_api, path)
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
                    ' sRGB an und saehe zu blass aus (siehe ColorManagementService). Sanft wie
                    ' der libheif-Weg (DecodeHandle), damit dieselbe Datei auf beiden Wegen
                    ' gleich ankommt.
                    Dim managed = ColorManagementService.ToSrgbSoftGamut(bitmap, codec.Info.ColorSpace)
                    If Not Object.ReferenceEquals(managed, bitmap) Then bitmap.Dispose()
                    Return managed
                End Using
            End Using
        End Function

        ''' <summary>Ein neuer Kontext, dessen Raster (die Kacheln eines Telefonfotos) auf einem Faden
        ''' je Kern dekodiert wird statt auf den vier der Vorgabe. Gemessen (24 Kerne, 12 MP mit 48
        ''' Kacheln): 185 auf 137 ms und 127 auf 82 ms, Bildpunkte Byte fuer Byte gleich.</summary>
        Private Shared Function NewDecodingContext(api As LibheifApi) As IntPtr
            Dim ctx = api.ContextAlloc()
            If ctx <> IntPtr.Zero AndAlso api.SetMaxDecodingThreads IsNot Nothing Then
                api.SetMaxDecodingThreads(ctx, DecodingThreads())
            End If
            Return ctx
        End Function

        ''' <summary>Faeden fuer EINEN Decode: einer je Kern. Die Regel "nur ein Decode gleichzeitig"
        ''' bleibt davon unberuehrt (DecodeGate und das Schloss oben); die Faeden arbeiten alle an
        ''' demselben Bild.</summary>
        Private Shared Function DecodingThreads() As Integer
            Return Math.Max(1, Environment.ProcessorCount)
        End Function

        ''' Ab hier lohnen Faeden im Codec: darunter kostet das Anlegen mehr, als sie bringen.
        Private Const CodecThreadsMinPixels As Long = 1000000L

        ''' <summary>Faeden fuer den Codec dieses Bilds, oder 0 fuer die Vorgabe des Codecs (HEVC:
        ''' einer, AV1: alle Kerne).
        '''
        ''' NUR FUER EIN BILD AUS EINEM STUECK. Ein Raster bekommt schon einen Faden je Kachel, und
        ''' libheif legt fuer JEDE Kachel einen eigenen Dekoder mit eigenen Faeden an; mit beidem
        ''' zusammen wurde ein 12-MP-Foto mit 48 Kacheln langsamer (135 auf 156 ms). Ein Bild aus
        ''' einem Stueck dagegen hatte vorher nur einen Faden: 51 MP 802 auf 382 ms, 1,7 MP 28 auf
        ''' 14 ms. Kennt die libheif die Abfrage des Rasters nicht, bleibt die Vorgabe.</summary>
        Private Shared Function CodecThreadsFor(api As LibheifApi, handle As IntPtr) As Integer
            If api.ImageTiling Is Nothing Then Return 0
            Dim pixels = CLng(api.HandleGetWidth(handle)) * CLng(api.HandleGetHeight(handle))
            If pixels < CodecThreadsMinPixels Then Return 0
            ' heif_image_tiling legt der Aufrufer an; der Puffer ist reichlich groesser als die
            ' Struktur (72 Byte in 1.23.7), falls eine spaetere Fassung hinten anhaengt.
            Dim tiling = Marshal.AllocHGlobal(TilingBufferBytes)
            Try
                For i = 0 To TilingBufferBytes - 1 Step 4
                    Marshal.WriteInt32(tiling, i, 0)
                Next
                If api.ImageTiling(handle, 0, tiling).Code <> 0 Then Return 0
                Dim columns = Marshal.ReadInt32(tiling, TilingColumnsOffset)
                Dim rows = Marshal.ReadInt32(tiling, TilingRowsOffset)
                If columns <> 1 OrElse rows <> 1 Then Return 0
                Return DecodingThreads()
            Finally
                Marshal.FreeHGlobal(tiling)
            End Try
        End Function

        ''' heif_image_tiling: int version, dann uint32 num_columns und num_rows.
        Private Const TilingBufferBytes As Integer = 512
        Private Const TilingColumnsOffset As Integer = 4
        Private Const TilingRowsOffset As Integer = 8

        ''' Lage von num_codec_threads in heif_decoding_options: ein int ab Fassung 8 der Struktur
        ''' (libheif 1.21.0 brachte ignore_sequence_editlist, output_image_nclx_profile,
        ''' num_library_threads und num_codec_threads zusammen). Belegt mit offsetof am Header der
        ''' mitgelieferten 1.23.7 unter 64 Bit; bis dorthin sind alle Felder Zeiger, Bytes, int und
        ''' eine Struktur aus Byte und drei Aufzaehlungen, die Lage ist also unter Linux, Windows
        ''' und macOS dieselbe. Spaetere Fassungen haengen nur hinten an.
        Private Const OptionsCodecThreadsOffset As Integer = 108
        Private Const OptionsCodecThreadsVersion As Byte = 8

        ''' <summary>heif_decode_image mit Optionen aus heif_decoding_options_alloc statt ohne: die
        ''' Vorgaben der Bibliothek, nur mit Faeden fuer den Codec (<see cref="CodecThreadsFor"/>),
        ''' wenn die Struktur das Feld kennt. libheif fuellt dieselben Vorgaben selbst ein, wenn
        ''' keine Optionen kommen; die Bildpunkte sind also dieselben. Fehlen die Einstiege, geht es
        ''' ohne Optionen wie bisher.</summary>
        Private Shared Function DecodeImageThreaded(api As LibheifApi, handle As IntPtr, ByRef img As IntPtr,
                                                    colorspace As Integer, chroma As Integer) As HeifError
            Dim codecThreads = If(api.DecodingOptionsAlloc Is Nothing OrElse IntPtr.Size <> 8, 0, CodecThreadsFor(api, handle))
            If codecThreads <= 0 Then Return api.DecodeImage(handle, img, colorspace, chroma, IntPtr.Zero)
            Dim options = api.DecodingOptionsAlloc()
            Try
                If options <> IntPtr.Zero AndAlso Marshal.ReadByte(options) >= OptionsCodecThreadsVersion Then
                    Marshal.WriteInt32(options, OptionsCodecThreadsOffset, codecThreads)
                End If
                Return api.DecodeImage(handle, img, colorspace, chroma, options)
            Finally
                If options <> IntPtr.Zero Then api.DecodingOptionsFree(options)
            End Try
        End Function

        Private Shared Function DecodeCore(api As LibheifApi, path As String) As SKBitmap
            Dim ctx As IntPtr = IntPtr.Zero
            Dim handle As IntPtr = IntPtr.Zero
            Dim pathPtr As IntPtr = IntPtr.Zero
            Try
                ctx = NewDecodingContext(api)
                If ctx = IntPtr.Zero Then Return Nothing

                pathPtr = StringToUtf8(path)
                If api.ReadFromFile(ctx, pathPtr, IntPtr.Zero).Code <> 0 Then Return Nothing
                If api.GetPrimaryHandle(ctx, handle).Code <> 0 OrElse handle = IntPtr.Zero Then Return Nothing
                Return DecodeHandle(api, handle, handle)
            Catch
                Return Nothing
            Finally
                If handle <> IntPtr.Zero Then api.HandleRelease(handle)
                If ctx <> IntPtr.Zero Then api.ContextFree(ctx)
                If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
            End Try
        End Function

        ''' <summary>Das eingebettete Vorschaubild der Hauptaufnahme (Bgra8888, Besitz beim
        ''' Aufrufer), oder Nothing, wenn die Datei keines traegt oder libheif die Abfrage nicht
        ''' kennt. Fuer die schnelle Kachel: ein Telefon-HEIC von 12 MP kostete voll dekodiert 130 bis
        ''' 460 ms, sein Vorschaubild (gesehen: 240x320 und 384x512) einige Millisekunden. Es ist
        ''' KLEINER als eine Kachel und deshalb nur ein Platzhalter, bis das scharfe Bild da ist
        ''' (siehe ImageItem). Mit dem Farbprofil der Hauptaufnahme, wenn es kein eigenes traegt.
        '''
        ''' Gedreht wie das Hauptbild, aber NICHT von selbst: libheif legt auf ein Vorschaubild nur
        ''' dessen EIGENE Transformationen. Telefone geben ihm die Drehung des Hauptbilds mit,
        ''' Fujifilm nicht: dort kommt das Hauptbild nach irot 270 hochkant, die drei Vorschaubilder
        ''' (1600x1200, 640x480, 160x120) quer. Traegt das Vorschaubild keine eigene, legt
        ''' <see cref="ApplyPrimaryTransforms"/> die des Hauptbilds darauf.</summary>
        Public Shared Function TryDecodeEmbeddedThumbnail(path As String) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not IsAvailable Then Return Nothing
            Return DecodeGate.Run(Function()
                                      SyncLock _nativeLock
                                          If Not LibheifReady OrElse _api.ThumbnailCount Is Nothing Then Return Nothing
                                          Return DecodeThumbnailCore(_api, path)
                                      End SyncLock
                                  End Function)
        End Function

        Private Shared Function DecodeThumbnailCore(api As LibheifApi, path As String) As SKBitmap
            If api.ThumbnailCount Is Nothing Then Return Nothing
            Dim ctx As IntPtr = IntPtr.Zero
            Dim handle As IntPtr = IntPtr.Zero
            Dim thumb As IntPtr = IntPtr.Zero
            Dim pathPtr As IntPtr = IntPtr.Zero
            Try
                ctx = NewDecodingContext(api)
                If ctx = IntPtr.Zero Then Return Nothing
                pathPtr = StringToUtf8(path)
                If api.ReadFromFile(ctx, pathPtr, IntPtr.Zero).Code <> 0 Then Return Nothing
                If api.GetPrimaryHandle(ctx, handle).Code <> 0 OrElse handle = IntPtr.Zero Then Return Nothing
                Dim count = api.ThumbnailCount(handle)
                If count <= 0 Then Return Nothing
                Dim ids(count - 1) As UInteger
                If api.ThumbnailIds(handle, ids, count) <= 0 Then Return Nothing
                If api.Thumbnail(handle, ids(0), thumb).Code <> 0 OrElse thumb = IntPtr.Zero Then Return Nothing
                ' Ein Vorschaubild ohne eigenes Profil teilt das der Hauptaufnahme.
                Dim hasOwnProfile = (api.ProfileSize IsNot Nothing AndAlso api.ProfileSize(thumb) <> IntPtr.Zero)
                Dim decoded = DecodeHandle(api, thumb, If(hasOwnProfile, thumb, handle))
                If decoded Is Nothing Then Return Nothing
                Return ApplyPrimaryTransforms(api, ctx, handle, thumb, decoded)
            Catch
                Return Nothing
            Finally
                If thumb <> IntPtr.Zero Then api.HandleRelease(thumb)
                If handle <> IntPtr.Zero Then api.HandleRelease(handle)
                If ctx <> IntPtr.Zero Then api.ContextFree(ctx)
                If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
            End Try
        End Function

        ' heif_item_property_type als fourcc: 'irot' und 'imir'.
        Private Const PropertyTypeRotation As Integer = &H69726F74
        Private Const PropertyTypeMirror As Integer = &H696D6972
        ' heif_transform_mirror_direction: 0 kehrt oben und unten um, 1 links und rechts.
        Private Const MirrorTopBottom As Integer = 0
        Private Const MirrorLeftRight As Integer = 1

        ''' <summary>Legt die Drehung und Spiegelung des Hauptbilds (irot, imir, in der Reihenfolge
        ''' der Datei) auf ein Vorschaubild, das selbst keine traegt. libheif wendet beim Dekodieren
        ''' nur die Transformationen des dekodierten Eintrags an, und manche Kameras geben sie dem
        ''' Vorschaubild nicht mit. Gibt <paramref name="decoded"/> unveraendert zurueck, wenn nichts
        ''' zu tun ist, sonst ein neues Bild (das alte ist dann freigegeben).
        '''
        ''' Zwei Faelle bleiben unangetastet:
        '''   - Das Vorschaubild traegt eigene Transformationen: die hat libheif schon angewandt.
        '''   - Bei einer Vierteldrehung liegt es schon so wie das angezeigte Hauptbild (hochkant zu
        '''     hochkant oder quer zu quer): dann sind die Pixel selbst gedreht gespeichert, und eine
        '''     zweite Drehung stellte es quer.
        ''' Der Beschnitt (clap) bleibt aussen vor: er steht in Bildpunkten des Hauptbilds, und fuer
        ''' einen Platzhalter zaehlt nur die Lage.</summary>
        Private Shared Function ApplyPrimaryTransforms(api As LibheifApi, ctx As IntPtr, primary As IntPtr,
                                                       thumb As IntPtr, decoded As SKBitmap) As SKBitmap
            Dim steps = PrimaryTransformSteps(api, ctx, primary, thumb, decoded)
            If steps Is Nothing Then Return decoded
            Dim current = decoded
            For Each [step] In steps
                Dim nextBitmap As SKBitmap
                If [step].IsRotation Then
                    ' irot zaehlt gegen den Uhrzeigersinn, ApplyQuarterRotation mit ihm.
                    nextBitmap = ImageOrientationService.ApplyQuarterRotation(current, 360 - [step].Value)
                Else
                    nextBitmap = ImageOrientationService.ApplyOrientation(current,
                        If([step].Value = MirrorLeftRight, SKEncodedOrigin.TopRight, SKEncodedOrigin.BottomLeft))
                End If
                If Not Object.ReferenceEquals(nextBitmap, current) Then
                    current.Dispose()
                    current = nextBitmap
                End If
            Next
            Return current
        End Function

        ''' <summary>Die Schritte fuer <see cref="ApplyPrimaryTransforms"/>, oder Nothing, wenn das
        ''' Vorschaubild bleibt, wie es ist. Nur Abfragen an libheif, kein Bild wird angefasst.</summary>
        Private Shared Function PrimaryTransformSteps(api As LibheifApi, ctx As IntPtr, primary As IntPtr,
                                                      thumb As IntPtr, decoded As SKBitmap) As List(Of (IsRotation As Boolean, Value As Integer))
            If api.TransformProperties Is Nothing Then Return Nothing
            Try
                Dim thumbId = api.ItemId(thumb)
                If api.TransformProperties(ctx, thumbId, Nothing, 0) > 0 Then Return Nothing
                Dim primaryId = api.ItemId(primary)
                Dim count = api.TransformProperties(ctx, primaryId, Nothing, 0)
                If count <= 0 Then Return Nothing
                Dim ids(count - 1) As UInteger
                count = Math.Min(count, api.TransformProperties(ctx, primaryId, ids, count))

                ' Erst sammeln, dann pruefen: die Pruefung braucht die Summe der Drehungen.
                Dim steps As New List(Of (IsRotation As Boolean, Value As Integer))
                Dim quarterTurns = 0
                For i = 0 To count - 1
                    Select Case api.PropertyType(ctx, primaryId, ids(i))
                        Case PropertyTypeRotation
                            Dim ccw = ImageOrientationService.NormalizeQuarterTurn(api.RotationCcw(ctx, primaryId, ids(i)))
                            If ccw <> 0 Then
                                steps.Add((True, ccw))
                                If ccw <> 180 Then quarterTurns += 1
                            End If
                        Case PropertyTypeMirror
                            Dim direction = api.MirrorDirection(ctx, primaryId, ids(i))
                            If direction = MirrorTopBottom OrElse direction = MirrorLeftRight Then steps.Add((False, direction))
                    End Select
                Next
                If steps.Count = 0 Then Return Nothing

                If quarterTurns Mod 2 = 1 Then
                    Dim shownWidth = api.HandleGetWidth(primary)
                    Dim shownHeight = api.HandleGetHeight(primary)
                    If shownWidth <> shownHeight AndAlso decoded.Width <> decoded.Height AndAlso
                       (shownHeight > shownWidth) = (decoded.Height > decoded.Width) Then Return Nothing
                End If
                Return steps
            Catch ex As Exception
                DiagnosticLogService.LogException("HEIF.ThumbnailTransform", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Traegt die Aufnahme hinter diesem Handle mehr als 8 Bit, und kennt die
        ''' Bibliothek die Abfragen dafuer?</summary>
        Private Shared Function IsHighBitDepth(api As LibheifApi, handle As IntPtr) As Boolean
            If handle = IntPtr.Zero OrElse api.LumaBits Is Nothing OrElse api.BitsRange Is Nothing Then Return False
            Return api.LumaBits(handle) > 8
        End Function

        ''' <summary>Traegt diese Datei mehr als 8 Bit in einer Form, die als tiefe Quelle taugt
        ''' (ohne Alpha, wie beim TIFF mit 16 Bit)? Danach richtet sich, ob die Dialoge 16 Bit
        ''' anbieten und ob die Kette ein tiefes Bild bekommt (ImageProcessor.IsDeepFileSource).
        '''
        ''' Nur ein Blick in die Kopfdaten, deshalb wie <see cref="TryGetSize"/> NICHT durch die
        ''' Decode-Schleuse. Auf macOS liefert ImageIO das 8-Bit-Bild; ein tiefes aus libheif
        ''' daneben waere nicht sicher dasselbe Bild, dort gibt es deshalb keine tiefe Quelle.</summary>
        Public Shared Function IsDeepHeif(path As String) As Boolean
            If Not IsSupportedHeif(path) OrElse Not File.Exists(path) Then Return False
            If MacImageIoService.IsAvailable Then Return False
            SyncLock _nativeLock
                If Not LibheifReady OrElse _api.HasAlpha Is Nothing Then Return False
                Return IsDeepCore(_api, path)
            End SyncLock
        End Function

        ''' <summary>Der Blick in die Kopfdaten fuer <see cref="IsDeepHeif"/>.</summary>
        Private Shared Function IsDeepCore(api As LibheifApi, path As String) As Boolean
            If api.HasAlpha Is Nothing Then Return False
            Dim ctx As IntPtr = IntPtr.Zero
            Dim handle As IntPtr = IntPtr.Zero
            Dim pathPtr As IntPtr = IntPtr.Zero
            Try
                ctx = api.ContextAlloc()
                If ctx = IntPtr.Zero Then Return False
                pathPtr = StringToUtf8(path)
                If api.ReadFromFile(ctx, pathPtr, IntPtr.Zero).Code <> 0 Then Return False
                If api.GetPrimaryHandle(ctx, handle).Code <> 0 OrElse handle = IntPtr.Zero Then Return False
                Return IsHighBitDepth(api, handle) AndAlso api.HasAlpha(handle) = 0
            Catch
                Return False
            Finally
                If handle <> IntPtr.Zero Then api.HandleRelease(handle)
                If ctx <> IntPtr.Zero Then api.ContextFree(ctx)
                If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
            End Try
        End Function

        ''' <summary>Die Hauptaufnahme mit 16 Bit je Kanal (Rgba16161616, deckend, sRGB ohne
        ''' Farbraum, Besitz beim Aufrufer) oder Nothing, wenn die Datei keine tiefe Quelle ist
        ''' (<see cref="IsDeepHeif"/>). DAS TIEFE GEGENSTUECK ZU <see cref="TryDecode"/>: dasselbe
        ''' Bild, dieselbe Drehung, dieselbe Wandlung des Profils; das 8-Bit-Bild einer solchen
        ''' Datei ist genau dieses Bild, gerundet.</summary>
        Friend Shared Function TryDecodeDeep(path As String) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not IsDeepHeif(path) Then Return Nothing
            Return DecodeGate.Run(Function()
                                      SyncLock _nativeLock
                                          If Not LibheifReady Then Return Nothing
                                          Return DecodeDeepCore(_api, path)
                                      End SyncLock
                                  End Function)
        End Function

        Private Shared Function DecodeDeepCore(api As LibheifApi, path As String) As SKBitmap
            If api.HasAlpha Is Nothing Then Return Nothing
            Dim ctx As IntPtr = IntPtr.Zero
            Dim handle As IntPtr = IntPtr.Zero
            Dim pathPtr As IntPtr = IntPtr.Zero
            Try
                ctx = NewDecodingContext(api)
                If ctx = IntPtr.Zero Then Return Nothing
                pathPtr = StringToUtf8(path)
                If api.ReadFromFile(ctx, pathPtr, IntPtr.Zero).Code <> 0 Then Return Nothing
                If api.GetPrimaryHandle(ctx, handle).Code <> 0 OrElse handle = IntPtr.Zero Then Return Nothing
                If Not IsHighBitDepth(api, handle) OrElse api.HasAlpha(handle) <> 0 Then Return Nothing
                Return DecodeDeepHandle(api, handle, handle, True)
            Catch ex As Exception
                DiagnosticLogService.LogException("HEIF.TryDecodeDeep", ex)
                Return Nothing
            Finally
                If handle <> IntPtr.Zero Then api.HandleRelease(handle)
                If ctx <> IntPtr.Zero Then api.ContextFree(ctx)
                If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
            End Try
        End Function

        ''' <summary>Ein Bild mit mehr als 8 Bit aus einem libheif-Handle als Rgba16161616 in sRGB
        ''' (ohne Farbraum, Besitz beim Aufrufer), oder Nothing.
        '''
        ''' libheif liefert die Werte in der Tiefe der Datei (bei 10 Bit 0 bis 1023); sie werden
        ''' gerundet auf 0 bis 65535 gestreckt, sodass Schwarz und Weiss genau auf den Enden liegen.
        ''' Das Profil wird danach in 16 Bit gewandelt, sanft an den Raendern von sRGB
        ''' (<see cref="ColorManagementService.ToSrgbSoftGamut"/>).</summary>
        ''' <param name="opaque">Deckend anlegen (die tiefe Quelle der Kette); sonst
        ''' unvormultipliziert mit dem Alpha der Datei.</param>
        Private Shared Function DecodeDeepHandle(api As LibheifApi, handle As IntPtr, profileHandle As IntPtr, opaque As Boolean) As SKBitmap
            Dim img As IntPtr = IntPtr.Zero
            Try
                If DecodeImageThreaded(api, handle, img, ColorspaceRgb, ChromaInterleavedRrggbbaaLe).Code <> 0 OrElse img = IntPtr.Zero Then Return Nothing

                Dim width = api.ImageGetWidth(img, ChannelInterleaved)
                Dim height = api.ImageGetHeight(img, ChannelInterleaved)
                If width <= 0 OrElse height <= 0 Then Return Nothing
                If CLng(width) * CLng(height) > Integer.MaxValue \ 8 Then Return Nothing
                Dim bits = api.BitsRange(img, ChannelInterleaved)
                If bits <= 8 OrElse bits > 16 Then Return Nothing
                Dim maxValue = (1 << bits) - 1

                Dim stride = 0
                Dim plane = api.GetPlane(img, ChannelInterleaved, stride)
                If plane = IntPtr.Zero OrElse stride < width * 8 Then Return Nothing

                Dim bitmap = ImageProcessor.NewDeepBitmap(width, height, If(opaque, SKAlphaType.Opaque, SKAlphaType.Unpremul))
                If bitmap Is Nothing Then Return Nothing
                Try
                    Dim target = bitmap.GetPixels()
                    Dim targetStride = bitmap.RowBytes
                    Threading.Tasks.Parallel.For(0, height,
                        Sub(y)
                            ' Short statt UShort: Marshal.Copy kennt kein UShort-Feld. Gelesen
                            ' wird ueber And &HFFFF, geschrieben ueber ToDeepShort.
                            Dim row(width * 4 - 1) As Short
                            Marshal.Copy(New IntPtr(plane.ToInt64() + CLng(y) * stride), row, 0, width * 4)
                            For i = 0 To row.Length - 1
                                If opaque AndAlso (i And 3) = 3 Then
                                    row(i) = -1S
                                    Continue For
                                End If
                                Dim value = Math.Min(CInt(row(i)) And &HFFFF, maxValue)
                                row(i) = ImageProcessor.ToDeepShort(CInt((CLng(value) * 65535L + (maxValue \ 2)) \ maxValue))
                            Next
                            Marshal.Copy(row, 0, New IntPtr(target.ToInt64() + CLng(y) * targetStride), width * 4)
                        End Sub)

                    Using profile = ReadColorProfile(api, profileHandle)
                        Dim managed = ColorManagementService.ToSrgbSoftGamut(bitmap, profile)
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
                If img <> IntPtr.Zero Then api.ImageRelease(img)
            End Try
        End Function

        ''' <summary>Das 8-Bit-Bild (Bgra8888, unvormultipliziert, wie der 8-Bit-Weg) aus dem
        ''' tiefen, je Kanal gerundet: 65535 wird 255, 128 wird 0, 129 wird 1 - dieselbe Rundung
        ''' wie beim TIFF mit 16 Bit. Die Quelle bleibt beim Aufrufer.</summary>
        Private Shared Function RoundDeepToBgra(deep As SKBitmap) As SKBitmap
            Dim width = deep.Width
            Dim height = deep.Height
            Dim bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul))
            Try
                Dim source = deep.GetPixels()
                Dim target = bitmap.GetPixels()
                If source = IntPtr.Zero OrElse target = IntPtr.Zero Then
                    bitmap.Dispose()
                    Return Nothing
                End If
                Dim sourceStride = deep.RowBytes
                Dim targetStride = bitmap.RowBytes
                Threading.Tasks.Parallel.For(0, height,
                    Sub(y)
                        Dim deepRow(width * 4 - 1) As Short
                        Dim row(width * 4 - 1) As Byte
                        Marshal.Copy(New IntPtr(source.ToInt64() + CLng(y) * sourceStride), deepRow, 0, width * 4)
                        For x = 0 To width - 1
                            Dim o = x * 4
                            row(o) = CByte(((deepRow(o + 2) And &HFFFF) + 128) \ 257)
                            row(o + 1) = CByte(((deepRow(o + 1) And &HFFFF) + 128) \ 257)
                            row(o + 2) = CByte(((deepRow(o) And &HFFFF) + 128) \ 257)
                            row(o + 3) = CByte(((deepRow(o + 3) And &HFFFF) + 128) \ 257)
                        Next
                        Marshal.Copy(row, 0, New IntPtr(target.ToInt64() + CLng(y) * targetStride), width * 4)
                    End Sub)
                Return bitmap
            Catch
                bitmap.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Ein Bild aus einem libheif-Handle als Bgra8888 in sRGB. Das Farbprofil kommt
        ''' von <paramref name="profileHandle"/> (beim Vorschaubild ggf. das der Hauptaufnahme).
        '''
        ''' Mit mehr als 8 Bit geht es ueber das tiefe Bild (<see cref="DecodeDeepHandle"/>) und wird
        ''' erst am Ende gerundet: die Wandlung des Profils rechnet so auf den Werten der Datei und
        ''' nicht auf schon gerundeten. Scheitert das, bleibt der 8-Bit-Weg darunter. Ein Bild mit
        ''' 8 Bit nimmt ihn unveraendert, ohne Profil oder mit sRGB also Byte fuer Byte wie bisher.</summary>
        Private Shared Function DecodeHandle(api As LibheifApi, handle As IntPtr, profileHandle As IntPtr) As SKBitmap
            If IsHighBitDepth(api, handle) Then
                Dim hasAlpha = api.HasAlpha IsNot Nothing AndAlso api.HasAlpha(handle) <> 0
                Using deep = DecodeDeepHandle(api, handle, profileHandle, Not hasAlpha)
                    If deep IsNot Nothing Then
                        Dim rounded = RoundDeepToBgra(deep)
                        If rounded IsNot Nothing Then Return rounded
                    End If
                End Using
            End If
            Dim img As IntPtr = IntPtr.Zero
            Try
                ' IMMER mit Alphakanal dekodieren: das kostet bei undurchsichtigen Bildern nur den
                ' vierten Kanal, erspart aber einen zweiten Zweig - und HEIC kann Transparenz.
                If DecodeImageThreaded(api, handle, img, ColorspaceRgb, ChromaInterleavedRgba).Code <> 0 OrElse img = IntPtr.Zero Then Return Nothing

                Dim width = api.ImageGetWidth(img, ChannelInterleaved)
                Dim height = api.ImageGetHeight(img, ChannelInterleaved)
                If width <= 0 OrElse height <= 0 Then Return Nothing
                If CLng(width) * CLng(height) > Integer.MaxValue \ 4 Then Return Nothing

                Dim stride = 0
                Dim plane = api.GetPlane(img, ChannelInterleaved, stride)
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
                    ' Ordner voller HEIC erzeugt eines je Datei. Gewandelt wird sanft an den
                    ' Raendern von sRGB, wie im tiefen Weg (DecodeDeepHandle): ein HEIC in
                    ' Display P3 kommt mit 8 und mit 10 Bit gleich an.
                    Using profile = ReadColorProfile(api, profileHandle)
                        Dim managed = ColorManagementService.ToSrgbSoftGamut(bitmap, profile)
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
                ' Nur das eigene Bild: Handle und Kontext gehoeren dem Aufrufer.
                If img <> IntPtr.Zero Then api.ImageRelease(img)
            End Try
        End Function

        ''' <summary>Der Farbraum der Aufnahme, oder Nothing.
        '''
        ''' Zwei Wege, in dieser Reihenfolge: ein eingebettetes ICC-Profil, sonst die nclx-Angabe.
        ''' Das ICC hat Vorrang, weil es die genauere Auskunft ist - nclx nennt nur Kennzahlen aus
        ''' einer Tabelle, ein Profil beschreibt die Kurve selbst.</summary>
        Private Shared Function ReadColorProfile(api As LibheifApi, handle As IntPtr) As SKColorSpace
            Dim icc = ReadIccProfile(api, handle)
            If icc IsNot Nothing Then Return icc
            Return ReadNclxProfile(api, handle)
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
        Private Shared Function ReadNclxProfile(api As LibheifApi, handle As IntPtr) As SKColorSpace
            If handle = IntPtr.Zero OrElse api.NclxProfile Is Nothing Then Return Nothing
            Dim raw As IntPtr = IntPtr.Zero
            Try
                If api.NclxProfile(handle, raw).Code <> 0 OrElse raw = IntPtr.Zero Then Return Nothing
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
                If raw <> IntPtr.Zero Then api.FreeNclx?.Invoke(raw)
            End Try
        End Function

        ''' <summary>Das eingebettete ICC-Profil der Aufnahme, oder Nothing.</summary>
        Private Shared Function ReadIccProfile(api As LibheifApi, handle As IntPtr) As SKColorSpace
            If handle = IntPtr.Zero OrElse api.ProfileSize Is Nothing OrElse api.RawProfile Is Nothing Then Return Nothing
            Dim buffer As IntPtr = IntPtr.Zero
            Try
                Dim size = api.ProfileSize(handle).ToInt64()
                ' Null heisst schlicht "kein ICC an dieser Datei". Die obere Schranke faengt einen
                ' unsinnigen Wert ab, bevor daraus eine Speicheranforderung wird.
                If size <= 0 OrElse size > 16 * 1024 * 1024 Then Return Nothing

                Dim length = CInt(size)
                buffer = Marshal.AllocCoTaskMem(length)
                If api.RawProfile(handle, buffer).Code <> 0 Then Return Nothing

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

                Using bmp = DecodeCore(_api, path)
                    Return PngEncoder.EncodeToStream(bmp, PngPurpose.Transient)
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
                Return SizeCore(_api, path)
            End SyncLock
        End Function

        Private Shared Function SizeCore(api As LibheifApi, path As String) As (Width As Integer, Height As Integer)
            Dim ctx As IntPtr = IntPtr.Zero
            Dim handle As IntPtr = IntPtr.Zero
            Dim pathPtr As IntPtr = IntPtr.Zero
            Try
                ctx = api.ContextAlloc()
                If ctx = IntPtr.Zero Then Return (0, 0)
                pathPtr = StringToUtf8(path)
                If api.ReadFromFile(ctx, pathPtr, IntPtr.Zero).Code <> 0 Then Return (0, 0)
                If api.GetPrimaryHandle(ctx, handle).Code <> 0 OrElse handle = IntPtr.Zero Then Return (0, 0)
                Return (api.HandleGetWidth(handle), api.HandleGetHeight(handle))
            Catch
                Return (0, 0)
            Finally
                If handle <> IntPtr.Zero Then api.HandleRelease(handle)
                If ctx <> IntPtr.Zero Then api.ContextFree(ctx)
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
