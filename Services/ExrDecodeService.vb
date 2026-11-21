Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>
    ''' Liest OpenEXR (.exr) ueber OpenEXRCore, die C-Schnittstelle von OpenEXR (exr_*), dynamisch
    ''' geladen wie libheif und libjxl. Mitgeliefert als libOpenEXRCore (packaging/openexr, im
    ''' Flatpak als eigenes Modul unter /app/lib); ein Rueckfall auf eine OpenEXR des Systems ist
    ''' nicht vorgesehen, deren Dateiname wandert mit jeder Fassung.
    '''
    ''' EXR traegt lineares Licht mit Werten weit ueber 1. Das Ergebnis ist deshalb kein Bild,
    ''' sondern ein <see cref="LinearLightImage"/>, das wie ein entwickeltes RAW durch die
    ''' RAW-Grundstufe geht (RawDecodeServiceLinear.vb).
    '''
    ''' Die Regeln:
    ''' - Gelesen wird der Teil, der am ehesten ein Farbbild ist: zuerst einer mit R, G und B ohne
    '''   Ebenennamen, dann einer mit R, G und B einer Ebene (bevorzugt rgba, beauty, combined), dann
    '''   einer mit Y (dazu RY und BY: Helligkeit und Farbdifferenz). Tiefe Daten (deep) nicht.
    ''' - Y, RY und BY werden nach RGB gerechnet: R = Y (1 + RY), B = Y (1 + BY), G aus Y mit den
    '''   Helligkeitsanteilen der Primaerfarben; die halb aufgeloesten RY und BY bilinear gestreckt.
    '''   Y allein ist Grau.
    ''' - Das Attribut chromaticities des Teils wird angewandt (Bradford auf D65, nach
    '''   sRGB-Primaerfarben); ohne Attribut gilt lineares Rec.709, wie im Format vorgesehen.
    ''' - Alpha bleibt weg: die Grundstufe ist deckend, und EXR traegt die Farbe vormultipliziert,
    '''   das Bild steht also wie vor Schwarz.
    ''' - Grenzen vor jeder Zuweisung: Masse gegen <see cref="LinearLightImage.MaxPixels"/>, bevor ein
    '''   Feld entsteht; die Bibliothek bekommt dieselbe Grenze fuer Bild- und Kachelgroesse, sodass
    '''   ein Kopf, der mehr ansagt, gar nicht erst zu einem Decode fuehrt.
    ''' - Durch <see cref="DecodeGate"/>; die Bloecke laufen auf mehreren Faeden, jeder mit eigenem
    '''   Dekodierer (die Bibliothek erlaubt das Lesen verschiedener Bloecke zugleich).
    ''' </summary>
    Friend NotInheritable Class ExrDecodeService

        Private Sub New()
        End Sub

        ' Werte aus openexr_base.h und openexr_part.h (3.5), dort seit 3.0 unveraendert.
        Private Const ExrSuccess As Integer = 0
        Private Const StorageScanline As Integer = 0
        Private Const StorageTiled As Integer = 1
        Private Const PixelFloat As Integer = 2

        ' Groessen und Versaetze aus den Kopfdateien von OpenEXR 3.5.4 (64 Bit).
        Private Const InitializerSize As Integer = 112
        Private Const InitializerErrorHandler As Integer = 8
        Private Const InitializerMaxImageWidth As Integer = 72
        Private Const InitializerMaxImageHeight As Integer = 76
        Private Const InitializerMaxTileWidth As Integer = 80
        Private Const InitializerMaxTileHeight As Integer = 84
        Private Const InitializerZipLevel As Integer = 88
        Private Const InitializerDwaQuality As Integer = 92
        Private Const InitializerLossyHtj2kQuality As Integer = 100
        Private Const InitializerZstdLevel As Integer = 104
        Private Const ChunkInfoSize As Integer = 64
        Private Const ChunkStartX As Integer = 4
        Private Const ChunkStartY As Integer = 8
        Private Const PipelineSize As Integer = 528
        Private Const PipelineChannels As Integer = 8
        Private Const PipelineChannelCount As Integer = 16
        Private Const ChannelInfoSize As Integer = 48
        Private Const ChannelName As Integer = 0
        Private Const ChannelUserBytesPerElement As Integer = 28
        Private Const ChannelUserDataType As Integer = 30
        Private Const ChannelUserPixelStride As Integer = 32
        Private Const ChannelUserLineStride As Integer = 36
        Private Const ChannelDecodeTo As Integer = 40
        Private Const ChlistEntrySize As Integer = 32
        Private Const ChlistEntryName As Integer = 8
        Private Const ChlistEntryXSampling As Integer = 24
        Private Const ChlistEntryYSampling As Integer = 28

        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Sub ErrorHandlerFn(context As IntPtr, code As Integer, message As IntPtr)
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Sub GetLibraryVersionFn(ByRef major As Integer, ByRef minor As Integer, ByRef patch As Integer, ByRef extra As IntPtr)
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function ErrorCodeAsStringFn(code As Integer) As IntPtr
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function StartReadFn(ByRef context As IntPtr, filename As IntPtr, initializer As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function FinishFn(ByRef context As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function GetCountFn(context As IntPtr, ByRef count As Integer) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function GetIntOfPartFn(context As IntPtr, part As Integer, ByRef value As Integer) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function GetPointerOfPartFn(context As IntPtr, part As Integer, ByRef value As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function GetDataWindowFn(context As IntPtr, part As Integer, box As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function GetTileDescriptorFn(context As IntPtr, part As Integer, ByRef xSize As UInteger, ByRef ySize As UInteger,
                                                      ByRef levelMode As Integer, ByRef roundMode As Integer) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function GetTileCountsFn(context As IntPtr, part As Integer, levelX As Integer, levelY As Integer,
                                                  ByRef countX As Integer, ByRef countY As Integer) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function ReadScanlineChunkInfoFn(context As IntPtr, part As Integer, y As Integer, chunkInfo As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function ReadTileChunkInfoFn(context As IntPtr, part As Integer, tileX As Integer, tileY As Integer,
                                                      levelX As Integer, levelY As Integer, chunkInfo As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function DecodingWithChunkFn(context As IntPtr, part As Integer, chunkInfo As IntPtr, pipeline As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function DecodingFn(context As IntPtr, part As Integer, pipeline As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function DecodingDestroyFn(context As IntPtr, pipeline As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function GetChromaticitiesFn(context As IntPtr, part As Integer, name As IntPtr, chromaticities As IntPtr) As Integer

        Private NotInheritable Class ExrApi
            Public GetLibraryVersion As GetLibraryVersionFn
            Public ErrorCodeAsString As ErrorCodeAsStringFn
            Public StartRead As StartReadFn
            Public Finish As FinishFn
            Public GetCount As GetCountFn
            Public GetStorage As GetIntOfPartFn
            Public GetDataWindow As GetDataWindowFn
            Public GetChannels As GetPointerOfPartFn
            Public GetScanlinesPerChunk As GetIntOfPartFn
            Public GetTileDescriptor As GetTileDescriptorFn
            Public GetTileCounts As GetTileCountsFn
            Public ReadScanlineChunkInfo As ReadScanlineChunkInfoFn
            Public ReadTileChunkInfo As ReadTileChunkInfoFn
            Public DecodingInitialize As DecodingWithChunkFn
            Public DecodingUpdate As DecodingWithChunkFn
            Public DecodingChooseDefaultRoutines As DecodingFn
            Public DecodingRun As DecodingFn
            Public DecodingDestroy As DecodingDestroyFn
            Public GetChromaticities As GetChromaticitiesFn
        End Class

        Private Shared ReadOnly _initLock As New Object()
        Private Shared ReadOnly _nativeLock As New Object()
        Private Shared _initialized As Boolean
        Private Shared _api As ExrApi
        Private Shared _loadedLibrary As String
        Private Shared _version As String = ""
        ''' <summary>Die Meldungen der Bibliothek, still: ohne eigenen Empfaenger schriebe sie jede
        ''' auf die Konsole. Gehalten, solange die Bibliothek geladen ist.</summary>
        Private Shared ReadOnly _silentHandler As ErrorHandlerFn = AddressOf OnLibraryError
        <ThreadStatic> Private Shared _lastMessage As String

        Private Shared Sub OnLibraryError(context As IntPtr, code As Integer, message As IntPtr)
            Try
                _lastMessage = If(Marshal.PtrToStringUTF8(message), "")
            Catch
            End Try
        End Sub

        Public Shared Function IsSupportedExr(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Return String.Equals(IO.Path.GetExtension(path), ".exr", StringComparison.OrdinalIgnoreCase)
        End Function

        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                EnsureLoaded()
                Return _api IsNot Nothing
            End Get
        End Property

        ''' <summary>Dateiname der geladenen Bibliothek, fuer die Seite Technik und die Pruefung.</summary>
        Public Shared ReadOnly Property LoadedLibrary As String
            Get
                EnsureLoaded()
                Return _loadedLibrary
            End Get
        End Property

        ''' <summary>Fassung der geladenen Bibliothek ("3.5.4"), leer ohne.</summary>
        Public Shared ReadOnly Property Version As String
            Get
                EnsureLoaded()
                Return _version
            End Get
        End Property

        ' --- Bibliothek --------------------------------------------------------------------

        ''' <summary>Erst der blosse Name (Flatpak: /app/lib ueber den Suchpfad), dann neben der
        ''' Anwendung und unter runtimes/&lt;rid&gt;/native, wohin packaging/package.sh die
        ''' mitgelieferte legt. Welche zuerst probiert wird, entscheidet BundledLibraryOrder.</summary>
        Private Shared Iterator Function BundledCandidates(names As String()) As IEnumerable(Of String)
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
            For Each name In names
                Yield name
                Yield IO.Path.Combine(baseDir, name)
                If rid.Length > 0 Then Yield IO.Path.Combine(baseDir, "runtimes", rid, "native", name)
            Next
        End Function

        ''' <summary>Die Namen der mitgelieferten Datei je Plattform. Der eigene Bau setzt
        ''' OPENEXR_LIB_SUFFIX leer, deshalb ohne Fassung im Namen; die Zahl im Soname ist die der
        ''' Schnittstelle.</summary>
        Private Shared Function CandidateNames() As String()
            If OperatingSystem.IsWindows() Then Return {"libOpenEXRCore.dll"}
            If OperatingSystem.IsMacOS() Then Return {"libOpenEXRCore.dylib"}
            Return {"libOpenEXRCore.so.34"}
        End Function

        Private Shared Sub EnsureLoaded()
            SyncLock _initLock
                If _initialized Then Return
                _initialized = True
                For Each candidate In BundledLibraryOrder.Arrange(BundledCandidates(CandidateNames()))
                    Dim handle As IntPtr
                    If Not NativeLibrary.TryLoad(candidate, handle) Then Continue For
                    Dim api = Bind(handle)
                    If api Is Nothing Then
                        NativeLibrary.Free(handle)
                        Continue For
                    End If
                    _api = api
                    _loadedLibrary = IO.Path.GetFileName(candidate)
                    Try
                        Dim major, minor, patch As Integer
                        Dim extra As IntPtr
                        api.GetLibraryVersion(major, minor, patch, extra)
                        _version = $"{major}.{minor}.{patch}"
                    Catch
                    End Try
                    Return
                Next
                DiagnosticLogService.LogAlways("Exr", "OpenEXRCore nicht geladen - EXR bleibt zu. Versucht: " & String.Join(", ", CandidateNames()))
            End SyncLock
        End Sub

        Private Shared Function Bind(handle As IntPtr) As ExrApi
            Try
                Return New ExrApi With {
                    .GetLibraryVersion = GetExport(Of GetLibraryVersionFn)(handle, "exr_get_library_version"),
                    .ErrorCodeAsString = GetExport(Of ErrorCodeAsStringFn)(handle, "exr_get_error_code_as_string"),
                    .StartRead = GetExport(Of StartReadFn)(handle, "exr_start_read"),
                    .Finish = GetExport(Of FinishFn)(handle, "exr_finish"),
                    .GetCount = GetExport(Of GetCountFn)(handle, "exr_get_count"),
                    .GetStorage = GetExport(Of GetIntOfPartFn)(handle, "exr_get_storage"),
                    .GetDataWindow = GetExport(Of GetDataWindowFn)(handle, "exr_get_data_window"),
                    .GetChannels = GetExport(Of GetPointerOfPartFn)(handle, "exr_get_channels"),
                    .GetScanlinesPerChunk = GetExport(Of GetIntOfPartFn)(handle, "exr_get_scanlines_per_chunk"),
                    .GetTileDescriptor = GetExport(Of GetTileDescriptorFn)(handle, "exr_get_tile_descriptor"),
                    .GetTileCounts = GetExport(Of GetTileCountsFn)(handle, "exr_get_tile_counts"),
                    .ReadScanlineChunkInfo = GetExport(Of ReadScanlineChunkInfoFn)(handle, "exr_read_scanline_chunk_info"),
                    .ReadTileChunkInfo = GetExport(Of ReadTileChunkInfoFn)(handle, "exr_read_tile_chunk_info"),
                    .DecodingInitialize = GetExport(Of DecodingWithChunkFn)(handle, "exr_decoding_initialize"),
                    .DecodingUpdate = GetExport(Of DecodingWithChunkFn)(handle, "exr_decoding_update"),
                    .DecodingChooseDefaultRoutines = GetExport(Of DecodingFn)(handle, "exr_decoding_choose_default_routines"),
                    .DecodingRun = GetExport(Of DecodingFn)(handle, "exr_decoding_run"),
                    .DecodingDestroy = GetExport(Of DecodingDestroyFn)(handle, "exr_decoding_destroy"),
                    .GetChromaticities = GetExport(Of GetChromaticitiesFn)(handle, "exr_attr_get_chromaticities")
                }
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function GetExport(Of T)(handle As IntPtr, name As String) As T
            Return Marshal.GetDelegateForFunctionPointer(Of T)(NativeLibrary.GetExport(handle, name))
        End Function

        ' --- Lesen -------------------------------------------------------------------------

        ''' <summary>Ein Kanal eines Teils, wie die Kanalliste ihn nennt.</summary>
        Private Structure ChannelEntry
            Public Name As String
            Public XSampling As Integer
            Public YSampling As Integer
        End Structure

        ''' <summary>Was aus welchem Teil gelesen wird.</summary>
        Private NotInheritable Class PartChoice
            Public Part As Integer
            Public Storage As Integer
            Public MinX, MinY, Width, Height As Integer
            ''' <summary>"rgb", "y" oder "yc" (Y mit RY und BY).</summary>
            Public Kind As String
            ''' <summary>Die Kanalnamen in der Reihenfolge R, G, B beziehungsweise Y, RY, BY.</summary>
            Public Names As String()
            Public Score As Integer
        End Class

        ''' <summary>Die Masse des Teils, der gelesen wuerde, oder (0, 0). Oeffnet die Datei ueber die
        ''' Bibliothek, liest aber nur den Kopf; kein Decode, also nicht durch DecodeGate.</summary>
        Public Shared Function TryGetSize(path As String) As (Width As Integer, Height As Integer)
            If Not IsSupportedExr(path) OrElse Not IsAvailable Then Return (0, 0)
            Dim result As (Width As Integer, Height As Integer) = (0, 0)
            WithContext(path,
                Sub(context)
                    Dim choice = ChoosePart(context)
                    If choice IsNot Nothing Then result = (choice.Width, choice.Height)
                End Sub)
            Return result
        End Function

        ''' <summary>Das Bild als lineares Licht, oder Nothing. Der Aufrufer haelt
        ''' <see cref="DecodeGate"/>.</summary>
        Public Shared Function TryReadLinear(path As String) As LinearLightImage
            If Not IsSupportedExr(path) OrElse Not IsAvailable Then Return Nothing
            Dim image As LinearLightImage = Nothing
            WithContext(path,
                Sub(context)
                    Dim choice = ChoosePart(context)
                    If choice Is Nothing Then
                        DiagnosticLogService.LogAlways("Exr", $"kein lesbarer Teil mit R, G, B oder Y: {IO.Path.GetFileName(path)}")
                        Return
                    End If
                    image = ReadPart(context, choice, path)
                End Sub)
            Return image
        End Function

        ''' <summary>Oeffnet die Datei mit Grenzen und stiller Fehlerausgabe, ruft
        ''' <paramref name="work"/> und schliesst wieder.</summary>
        Private Shared Sub WithContext(path As String, work As Action(Of IntPtr))
            Dim api = _api
            If api Is Nothing Then Return
            Dim pathPtr = IntPtr.Zero
            Dim initializer = IntPtr.Zero
            Dim context = IntPtr.Zero
            Try
                pathPtr = Marshal.StringToCoTaskMemUTF8(path)
                initializer = Marshal.AllocHGlobal(InitializerSize)
                For i = 0 To InitializerSize - 1
                    Marshal.WriteByte(initializer, i, 0)
                Next
                ' Wie EXR_DEFAULT_CONTEXT_INITIALIZER, dazu der stille Empfaenger und die Grenzen.
                Marshal.WriteInt64(initializer, 0, InitializerSize)
                Marshal.WriteIntPtr(initializer, InitializerErrorHandler, Marshal.GetFunctionPointerForDelegate(_silentHandler))
                Marshal.WriteInt32(initializer, InitializerMaxImageWidth, 65535)
                Marshal.WriteInt32(initializer, InitializerMaxImageHeight, 65535)
                Marshal.WriteInt32(initializer, InitializerMaxTileWidth, 8192)
                Marshal.WriteInt32(initializer, InitializerMaxTileHeight, 8192)
                Marshal.WriteInt32(initializer, InitializerZipLevel, -2)
                Marshal.WriteInt32(initializer, InitializerDwaQuality, BitConverter.SingleToInt32Bits(-1.0F))
                Marshal.WriteInt32(initializer, InitializerLossyHtj2kQuality, 0)
                Marshal.WriteInt32(initializer, InitializerZstdLevel, -1)
                SyncLock _nativeLock
                    Dim rv = api.StartRead(context, pathPtr, initializer)
                    If rv <> ExrSuccess Then
                        DiagnosticLogService.LogAlways("Exr", $"nicht lesbar: {IO.Path.GetFileName(path)}: {ErrorText(api, rv)}")
                        Return
                    End If
                    work(context)
                End SyncLock
            Catch ex As Exception
                DiagnosticLogService.LogException("Exr.Read", ex)
            Finally
                If context <> IntPtr.Zero Then
                    Try
                        api.Finish(context)
                    Catch
                    End Try
                End If
                If initializer <> IntPtr.Zero Then Marshal.FreeHGlobal(initializer)
                If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
            End Try
        End Sub

        Private Shared Function ErrorText(api As ExrApi, code As Integer) As String
            Dim text = ""
            Try
                text = If(Marshal.PtrToStringUTF8(api.ErrorCodeAsString(code)), "")
            Catch
            End Try
            If Not String.IsNullOrEmpty(_lastMessage) Then text &= " (" & _lastMessage & ")"
            Return text
        End Function

        ''' <summary>Die Kanaele eines Teils.</summary>
        Private Shared Function ReadChannels(api As ExrApi, context As IntPtr, part As Integer) As List(Of ChannelEntry)
            Dim list As IntPtr
            If api.GetChannels(context, part, list) <> ExrSuccess OrElse list = IntPtr.Zero Then Return Nothing
            Dim count = Marshal.ReadInt32(list, 0)
            Dim entries = Marshal.ReadIntPtr(list, 8)
            If count <= 0 OrElse count > 4096 OrElse entries = IntPtr.Zero Then Return Nothing
            Dim result As New List(Of ChannelEntry)(count)
            For i = 0 To count - 1
                Dim entry = IntPtr.Add(entries, i * ChlistEntrySize)
                Dim namePtr = Marshal.ReadIntPtr(entry, ChlistEntryName)
                result.Add(New ChannelEntry With {
                    .Name = If(namePtr = IntPtr.Zero, "", If(Marshal.PtrToStringUTF8(namePtr), "")),
                    .XSampling = Marshal.ReadInt32(entry, ChlistEntryXSampling),
                    .YSampling = Marshal.ReadInt32(entry, ChlistEntryYSampling)
                })
            Next
            Return result
        End Function

        ''' <summary>Der Teil, der am ehesten ein Farbbild ist, oder Nothing.</summary>
        Private Shared Function ChoosePart(context As IntPtr) As PartChoice
            Dim api = _api
            Dim parts As Integer
            If api.GetCount(context, parts) <> ExrSuccess OrElse parts <= 0 Then Return Nothing
            Dim best As PartChoice = Nothing
            Dim box = Marshal.AllocHGlobal(16)
            Try
                For part = 0 To Math.Min(parts, 256) - 1
                    Dim storage As Integer
                    If api.GetStorage(context, part, storage) <> ExrSuccess Then Continue For
                    If storage <> StorageScanline AndAlso storage <> StorageTiled Then Continue For
                    If api.GetDataWindow(context, part, box) <> ExrSuccess Then Continue For
                    Dim minX = Marshal.ReadInt32(box, 0), minY = Marshal.ReadInt32(box, 4)
                    Dim maxX = Marshal.ReadInt32(box, 8), maxY = Marshal.ReadInt32(box, 12)
                    Dim width = CLng(maxX) - minX + 1
                    Dim height = CLng(maxY) - minY + 1
                    If Not LinearLightImage.SizeAllowed(width, height) Then Continue For
                    Dim channels = ReadChannels(api, context, part)
                    If channels Is Nothing Then Continue For
                    Dim candidate = ScoreChannels(channels)
                    If candidate Is Nothing Then Continue For
                    candidate.Part = part
                    candidate.Storage = storage
                    candidate.MinX = minX
                    candidate.MinY = minY
                    candidate.Width = CInt(width)
                    candidate.Height = CInt(height)
                    If best Is Nothing OrElse candidate.Score > best.Score Then best = candidate
                Next
            Finally
                Marshal.FreeHGlobal(box)
            End Try
            Return best
        End Function

        ''' <summary>Bewertet die Kanaele eines Teils: R, G, B ohne Ebene vor R, G, B einer Ebene
        ''' vor Y. Die Farbkanaele muessen voll aufgeloest sein, RY und BY duerfen es nicht sein.</summary>
        Private Shared Function ScoreChannels(channels As List(Of ChannelEntry)) As PartChoice
            Dim byName As New Dictionary(Of String, ChannelEntry)(StringComparer.Ordinal)
            For Each c In channels
                byName(c.Name) = c
            Next
            Dim Full = Function(name As String) byName.ContainsKey(name) AndAlso byName(name).XSampling = 1 AndAlso byName(name).YSampling = 1

            If Full("R") AndAlso Full("G") AndAlso Full("B") Then
                Return New PartChoice With {.Kind = "rgb", .Names = {"R", "G", "B"}, .Score = 400}
            End If
            ' Eine Ebene mit R, G und B, die bekannten Namen eines Farbbilds zuerst.
            Dim bestLayer As String = Nothing
            Dim bestRank = Integer.MaxValue
            For Each c In channels
                If Not c.Name.EndsWith(".R", StringComparison.Ordinal) Then Continue For
                Dim layer = c.Name.Substring(0, c.Name.Length - 2)
                If Not (Full(layer & ".R") AndAlso Full(layer & ".G") AndAlso Full(layer & ".B")) Then Continue For
                Dim lower = layer.ToLowerInvariant()
                Dim rank = If(lower = "rgba" OrElse lower = "rgb", 0,
                           If(lower = "beauty" OrElse lower = "combined" OrElse lower.EndsWith("beauty", StringComparison.Ordinal), 1, 2))
                If rank < bestRank Then
                    bestRank = rank
                    bestLayer = layer
                End If
            Next
            If bestLayer IsNot Nothing Then
                Return New PartChoice With {.Kind = "rgb", .Names = {bestLayer & ".R", bestLayer & ".G", bestLayer & ".B"}, .Score = 300 - bestRank}
            End If
            If Full("Y") Then
                If byName.ContainsKey("RY") AndAlso byName.ContainsKey("BY") AndAlso
                   byName("RY").XSampling >= 1 AndAlso byName("RY").YSampling >= 1 AndAlso
                   byName("BY").XSampling = byName("RY").XSampling AndAlso byName("BY").YSampling = byName("RY").YSampling AndAlso
                   byName("RY").XSampling <= 16 AndAlso byName("RY").YSampling <= 16 Then
                    Return New PartChoice With {.Kind = "yc", .Names = {"Y", "RY", "BY"}, .Score = 200}
                End If
                Return New PartChoice With {.Kind = "y", .Names = {"Y"}, .Score = 100}
            End If
            Return Nothing
        End Function

        ''' <summary>Wohin ein Kanal dekodiert wird: Anfang, Abstand zweier Punkte, einer Zeile,
        ''' und die Unterabtastung.</summary>
        Private NotInheritable Class Target
            Public Base As IntPtr
            Public PixelStride As Integer
            Public LineStride As Integer
            Public XSampling As Integer = 1
            Public YSampling As Integer = 1
        End Class

        Private Shared Function ReadPart(context As IntPtr, choice As PartChoice, path As String) As LinearLightImage
            Dim api = _api
            Dim width = choice.Width
            Dim height = choice.Height
            Dim pixelCount = CLng(width) * height
            Dim rgb(CInt(pixelCount * 3) - 1) As Single
            Dim rgbPin = GCHandle.Alloc(rgb, GCHandleType.Pinned)
            ' Fuer Y, RY und BY eigene Ebenen, RY und BY in ihrer Aufloesung.
            Dim planes As Single()() = Nothing
            Dim planePins As GCHandle() = Nothing
            Try
                Dim targets As New Dictionary(Of String, Target)(StringComparer.Ordinal)
                Dim channels = ReadChannels(api, context, choice.Part)
                If channels Is Nothing Then Return Nothing
                If choice.Kind = "rgb" Then
                    For i = 0 To 2
                        targets(choice.Names(i)) = New Target With {
                            .Base = IntPtr.Add(rgbPin.AddrOfPinnedObject(), i * 4), .PixelStride = 12, .LineStride = width * 12}
                    Next
                Else
                    ReDim planes(choice.Names.Length - 1)
                    ReDim planePins(choice.Names.Length - 1)
                    For i = 0 To choice.Names.Length - 1
                        Dim wanted = choice.Names(i)
                        Dim entry = channels.Find(Function(c) String.Equals(c.Name, wanted, StringComparison.Ordinal))
                        Dim xs = Math.Max(1, entry.XSampling), ys = Math.Max(1, entry.YSampling)
                        Dim planeWidth = (width + xs - 1) \ xs
                        Dim planeHeight = (height + ys - 1) \ ys
                        planes(i) = New Single(planeWidth * planeHeight - 1) {}
                        planePins(i) = GCHandle.Alloc(planes(i), GCHandleType.Pinned)
                        targets(choice.Names(i)) = New Target With {
                            .Base = planePins(i).AddrOfPinnedObject(), .PixelStride = 4, .LineStride = planeWidth * 4,
                            .XSampling = xs, .YSampling = ys}
                    Next
                End If

                ' Die Bloecke dieses Teils, als Liste von (x, y) fuer Kacheln bzw. Zeilenanfaengen.
                Dim blocks As New List(Of (X As Integer, Y As Integer))()
                Dim tileWidth As UInteger, tileHeight As UInteger
                If choice.Storage = StorageScanline Then
                    Dim lines As Integer
                    If api.GetScanlinesPerChunk(context, choice.Part, lines) <> ExrSuccess OrElse lines <= 0 Then Return Nothing
                    For y = choice.MinY To choice.MinY + height - 1 Step lines
                        blocks.Add((0, y))
                    Next
                Else
                    Dim levelMode, roundMode, countX, countY As Integer
                    If api.GetTileDescriptor(context, choice.Part, tileWidth, tileHeight, levelMode, roundMode) <> ExrSuccess Then Return Nothing
                    If api.GetTileCounts(context, choice.Part, 0, 0, countX, countY) <> ExrSuccess Then Return Nothing
                    If countX <= 0 OrElse countY <= 0 OrElse CLng(countX) * countY > 16L * 1024 * 1024 Then Return Nothing
                    For ty = 0 To countY - 1
                        For tx = 0 To countX - 1
                            blocks.Add((tx, ty))
                        Next
                    Next
                End If

                ' Auf mehreren Faeden, jeder mit eigenem Dekodierer und einem zusammenhaengenden
                ' Stueck der Liste.
                Dim workers = Math.Max(1, Math.Min(Environment.ProcessorCount, Math.Min(8, blocks.Count)))
                Dim failure As String = Nothing
                Parallel.For(0, workers,
                    Sub(worker)
                        Dim from = CInt(CLng(blocks.Count) * worker \ workers)
                        Dim [to] = CInt(CLng(blocks.Count) * (worker + 1) \ workers)
                        Dim message = DecodeBlocks(context, choice, blocks, from, [to], targets, tileWidth, tileHeight)
                        If message IsNot Nothing Then failure = message
                    End Sub)
                If failure IsNot Nothing Then
                    DiagnosticLogService.LogAlways("Exr", $"Bildpunkte nicht lesbar: {IO.Path.GetFileName(path)}: {failure}")
                    Return Nothing
                End If

                Dim result As New LinearLightImage With {.Width = width, .Height = height, .Rgb = rgb}
                Dim chroma = ReadChromaticities(context, choice.Part)
                If choice.Kind <> "rgb" Then
                    Dim weights = If(chroma Is Nothing, New Double() {0.2126, 0.7152, 0.0722},
                                     LinearLightImage.LuminanceWeights(chroma(0), chroma(1), chroma(2), chroma(3), chroma(4), chroma(5), chroma(6), chroma(7)))
                    FillFromLuminance(rgb, width, height, planes, targets, choice, weights)
                End If
                If chroma IsNot Nothing Then
                    result.ToSrgb = LinearLightImage.MatrixToSrgb(chroma(0), chroma(1), chroma(2), chroma(3), chroma(4), chroma(5), chroma(6), chroma(7))
                End If
                result.Description = $"Teil {choice.Part}, {String.Join(" ", choice.Names)}" &
                                     If(chroma Is Nothing, "", If(result.ToSrgb Is Nothing, ", chromaticities wie sRGB", ", chromaticities"))
                Return result
            Finally
                rgbPin.Free()
                If planePins IsNot Nothing Then
                    For Each pin In planePins
                        If pin.IsAllocated Then pin.Free()
                    Next
                End If
            End Try
        End Function

        ''' <summary>Dekodiert die Bloecke from bis to-1 mit einem eigenen Dekodierer. Nothing bei
        ''' Erfolg, sonst die Meldung.</summary>
        Private Shared Function DecodeBlocks(context As IntPtr, choice As PartChoice, blocks As List(Of (X As Integer, Y As Integer)),
                                             from As Integer, [to] As Integer, targets As Dictionary(Of String, Target),
                                             tileWidth As UInteger, tileHeight As UInteger) As String
            Dim api = _api
            Dim pipeline = Marshal.AllocHGlobal(PipelineSize)
            Dim chunk = Marshal.AllocHGlobal(ChunkInfoSize)
            Dim initialized = False
            Try
                For i = 0 To PipelineSize - 1
                    Marshal.WriteByte(pipeline, i, 0)
                Next
                Marshal.WriteInt64(pipeline, 0, PipelineSize)
                For index = from To [to] - 1
                    For i = 0 To ChunkInfoSize - 1
                        Marshal.WriteByte(chunk, i, 0)
                    Next
                    Dim rv As Integer
                    Dim block = blocks(index)
                    If choice.Storage = StorageScanline Then
                        rv = api.ReadScanlineChunkInfo(context, choice.Part, block.Y, chunk)
                    Else
                        rv = api.ReadTileChunkInfo(context, choice.Part, block.X, block.Y, 0, 0, chunk)
                    End If
                    If rv <> ExrSuccess Then Return ErrorText(api, rv)
                    ' Lage des Blocks im Bild, vom Rand des Datenfensters aus gezaehlt.
                    Dim x0, y0 As Integer
                    If choice.Storage = StorageScanline Then
                        x0 = 0
                        y0 = Marshal.ReadInt32(chunk, ChunkStartY) - choice.MinY
                    Else
                        x0 = CInt(block.X * CLng(tileWidth))
                        y0 = CInt(block.Y * CLng(tileHeight))
                    End If
                    If x0 < 0 OrElse y0 < 0 OrElse x0 >= choice.Width OrElse y0 >= choice.Height Then Return "Block ausserhalb des Datenfensters"
                    If Not initialized Then
                        rv = api.DecodingInitialize(context, choice.Part, chunk, pipeline)
                        If rv <> ExrSuccess Then Return ErrorText(api, rv)
                        initialized = True
                        PointChannels(pipeline, targets, x0, y0)
                        rv = api.DecodingChooseDefaultRoutines(context, choice.Part, pipeline)
                        If rv <> ExrSuccess Then Return ErrorText(api, rv)
                    Else
                        rv = api.DecodingUpdate(context, choice.Part, chunk, pipeline)
                        If rv <> ExrSuccess Then Return ErrorText(api, rv)
                        PointChannels(pipeline, targets, x0, y0)
                    End If
                    rv = api.DecodingRun(context, choice.Part, pipeline)
                    If rv <> ExrSuccess Then Return ErrorText(api, rv)
                Next
                Return Nothing
            Finally
                If initialized Then
                    Try
                        api.DecodingDestroy(context, pipeline)
                    Catch
                    End Try
                End If
                Marshal.FreeHGlobal(chunk)
                Marshal.FreeHGlobal(pipeline)
            End Try
        End Function

        ''' <summary>Richtet die Kanalzeiger des Dekodierers auf die Stelle des Blocks; Kanaele ohne
        ''' Ziel bleiben leer und werden uebersprungen.</summary>
        Private Shared Sub PointChannels(pipeline As IntPtr, targets As Dictionary(Of String, Target), x0 As Integer, y0 As Integer)
            Dim channels = Marshal.ReadIntPtr(pipeline, PipelineChannels)
            Dim count = CInt(Marshal.ReadInt16(pipeline, PipelineChannelCount))
            For c = 0 To count - 1
                Dim info = IntPtr.Add(channels, c * ChannelInfoSize)
                Dim namePtr = Marshal.ReadIntPtr(info, ChannelName)
                Dim name = If(namePtr = IntPtr.Zero, "", If(Marshal.PtrToStringUTF8(namePtr), ""))
                Dim target As Target = Nothing
                Marshal.WriteInt16(info, ChannelUserBytesPerElement, 4)
                Marshal.WriteInt16(info, ChannelUserDataType, CShort(PixelFloat))
                If targets.TryGetValue(name, target) Then
                    Marshal.WriteInt32(info, ChannelUserPixelStride, target.PixelStride)
                    Marshal.WriteInt32(info, ChannelUserLineStride, target.LineStride)
                    Dim offset = CLng(y0 \ target.YSampling) * target.LineStride + CLng(x0 \ target.XSampling) * target.PixelStride
                    Marshal.WriteIntPtr(info, ChannelDecodeTo, New IntPtr(target.Base.ToInt64() + offset))
                Else
                    Marshal.WriteInt32(info, ChannelUserPixelStride, 4)
                    Marshal.WriteInt32(info, ChannelUserLineStride, 0)
                    Marshal.WriteIntPtr(info, ChannelDecodeTo, IntPtr.Zero)
                End If
            Next
        End Sub

        ''' <summary>Das Attribut chromaticities des Teils als acht Zahlen, oder Nothing.</summary>
        Private Shared Function ReadChromaticities(context As IntPtr, part As Integer) As Double()
            Dim api = _api
            If api.GetChromaticities Is Nothing Then Return Nothing
            Dim name = Marshal.StringToCoTaskMemUTF8("chromaticities")
            Dim buffer = Marshal.AllocHGlobal(32)
            Try
                If api.GetChromaticities(context, part, name, buffer) <> ExrSuccess Then Return Nothing
                Dim values(7) As Double
                For i = 0 To 7
                    values(i) = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer, i * 4))
                    If Not Double.IsFinite(values(i)) Then Return Nothing
                Next
                Return values
            Finally
                Marshal.FreeHGlobal(buffer)
                Marshal.FreeCoTaskMem(name)
            End Try
        End Function

        ''' <summary>RGB aus Y allein (Grau) oder aus Y, RY und BY.</summary>
        Private Shared Sub FillFromLuminance(rgb As Single(), width As Integer, height As Integer, planes As Single()(),
                                             targets As Dictionary(Of String, Target), choice As PartChoice, weights As Double())
            Dim luminance = planes(0)
            If choice.Kind = "y" Then
                Parallel.For(0, height,
                    Sub(y)
                        For x = 0 To width - 1
                            Dim v = luminance(y * width + x)
                            Dim o = (y * width + x) * 3
                            rgb(o) = v : rgb(o + 1) = v : rgb(o + 2) = v
                        Next
                    End Sub)
                Return
            End If
            Dim ry = planes(1), by = planes(2)
            Dim chromaTarget = targets(choice.Names(1))
            Dim xs = chromaTarget.XSampling, ys = chromaTarget.YSampling
            Dim chromaWidth = (width + xs - 1) \ xs
            Dim chromaHeight = (height + ys - 1) \ ys
            Dim wr = weights(0), wg = Math.Max(0.0001, weights(1)), wb = weights(2)
            Parallel.For(0, height,
                Sub(y)
                    ' Die Stelle in der halb aufgeloesten Ebene, mittig gelegt, bilinear.
                    Dim fy = Math.Max(0.0, Math.Min(chromaHeight - 1.0, (y + 0.5) / ys - 0.5))
                    Dim cy0 = CInt(Math.Floor(fy)), cy1 = Math.Min(chromaHeight - 1, cy0 + 1)
                    Dim wy = fy - cy0
                    For x = 0 To width - 1
                        Dim fx = Math.Max(0.0, Math.Min(chromaWidth - 1.0, (x + 0.5) / xs - 0.5))
                        Dim cx0 = CInt(Math.Floor(fx)), cx1 = Math.Min(chromaWidth - 1, cx0 + 1)
                        Dim wx = fx - cx0
                        Dim Sample = Function(plane As Single()) As Double
                                         Dim top = plane(cy0 * chromaWidth + cx0) * (1.0 - wx) + plane(cy0 * chromaWidth + cx1) * wx
                                         Dim bottom = plane(cy1 * chromaWidth + cx0) * (1.0 - wx) + plane(cy1 * chromaWidth + cx1) * wx
                                         Return top * (1.0 - wy) + bottom * wy
                                     End Function
                        Dim yv = CDbl(luminance(y * width + x))
                        Dim r = (Sample(ry) + 1.0) * yv
                        Dim b = (Sample(by) + 1.0) * yv
                        Dim g = (yv - r * wr - b * wb) / wg
                        Dim o = (y * width + x) * 3
                        rgb(o) = CSng(r) : rgb(o + 1) = CSng(g) : rgb(o + 2) = CSng(b)
                    Next
                End Sub)
        End Sub

    End Class

End Namespace
