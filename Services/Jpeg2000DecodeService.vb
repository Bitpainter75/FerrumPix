Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Liest JPEG 2000 ueber OpenJPEG (libopenjp2), dynamisch geladen wie libheif und libjxl:
    ''' den JP2-Behaelter (.jp2, .jpf), den nackten Codestrom (.j2k, .j2c, .jpc) und HTJ2K (.jph
    ''' im Behaelter, .jhc als Codestrom). Mitgeliefert als libopenjp2 (packaging/openjpeg), im
    ''' Flatpak aus der Laufzeit; ein Rueckfall auf eine OpenJPEG des Systems ist nicht vorgesehen.
    '''
    ''' JPEG 2000 ist KEIN lineares Licht: es geht wie TIFF und HEIF als fertiges Bild an die
    ''' Anzeigewege (ForeignImageDecoder), nicht durch die RAW-Grundstufe.
    '''
    ''' Die Regeln:
    ''' - Den Codec waehlt der Kopf, nicht die Endung (OpenJPEG erkennt ihn nicht selbst): der
    '''   Signaturkasten des JP2-Behaelters oder SOC und SIZ eines Codestroms. .jp2, .j2k und .jph
    '''   gelten nach der Endung als JPEG 2000; .j2c, .jpc, .jpf und .jhc nur, wenn der Kopf passt.
    ''' - Grenzen nach opj_read_header, vor dem Decode: Masse und Zahl der Komponenten.
    ''' - sYCC (auch ohne Angabe, wenn die Farbkomponenten unterabgetastet sind) wird hier nach RGB
    '''   gerechnet, CMYK ohne Profil; ein eingebettetes ICC-Profil nach sRGB.
    ''' - Mehr als 8 Bit ohne Alpha gibt ein tiefes Bild mit 16 Bit (TryDecodeDeep), wie TIFF mit
    '''   16 Bit; das 8-Bit-Bild ist genau das tiefe, gerundet.
    ''' - Fuer die Kachel verkleinert gelesen (opj_set_decoded_resolution_factor): je Stufe die
    '''   halbe Kante, ohne die feineren Stufen zu dekodieren.
    ''' - 32 Bit je Komponente liest OpenJPEG nicht (HTJ2K mit Gleitkomma); solche Dateien bleiben zu.
    ''' - Durch <see cref="DecodeGate"/>, die nativen Aufrufe unter einem eigenen Schloss; Kopf und
    '''   Masse liest der eigene Kopfleser ohne Bibliothek.
    ''' - Geschrieben wird nichts: Speichern geht ins Rezept neben der Datei.
    ''' </summary>
    Public NotInheritable Class Jpeg2000DecodeService

        Private Sub New()
        End Sub

        Private Const MaxPixels As Long = 200L * 1000L * 1000L
        Private Const MaxFileBytes As Long = 1024L * 1024L * 1024L

        Private Const CodecJ2k As Integer = 0
        Private Const CodecJp2 As Integer = 2
        Private Const ColorSpaceUnknown As Integer = -1
        Private Const ColorSpaceUnspecified As Integer = 0
        Private Const ColorSpaceSrgb As Integer = 1
        Private Const ColorSpaceGray As Integer = 2
        Private Const ColorSpaceSycc As Integer = 3
        Private Const ColorSpaceEycc As Integer = 4
        Private Const ColorSpaceCmyk As Integer = 5

        ' opj_image_t und opj_image_comp_t (OpenJPEG 2.5, 64 Bit).
        Private Const ImageNumComps As Integer = 16
        Private Const ImageColorSpace As Integer = 20
        Private Const ImageComps As Integer = 24
        Private Const ImageIccBuffer As Integer = 32
        Private Const ImageIccLength As Integer = 40
        Private Const CompSize As Integer = 64
        Private Const CompDx As Integer = 0
        Private Const CompDy As Integer = 4
        Private Const CompW As Integer = 8
        Private Const CompH As Integer = 12
        Private Const CompPrec As Integer = 24
        Private Const CompSgnd As Integer = 32
        Private Const CompData As Integer = 48
        Private Const CompAlpha As Integer = 56
        ''' <summary>opj_dparameters_t ist 8252 Byte gross; der Puffer hat Luft fuer eine Fassung,
        ''' die ihn wachsen laesst.</summary>
        Private Const ParametersBufferSize As Integer = 16384

        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function CreateDecompressFn(format As Integer) As IntPtr
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Sub SetDefaultParametersFn(parameters As IntPtr)
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function SetupDecoderFn(codec As IntPtr, parameters As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function SetThreadsFn(codec As IntPtr, threads As Integer) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function ReadHeaderFn(stream As IntPtr, codec As IntPtr, ByRef image As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function SetResolutionFactorFn(codec As IntPtr, factor As UInteger) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function SetDecodeAreaFn(codec As IntPtr, image As IntPtr, x0 As Integer, y0 As Integer, x1 As Integer, y1 As Integer) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function DecodeFn(codec As IntPtr, stream As IntPtr, image As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function EndDecompressFn(codec As IntPtr, stream As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Sub DestroyFn(handle As IntPtr)
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function StreamCreateFn(bufferSize As UIntPtr, isInput As Integer) As IntPtr
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Sub StreamSetFunctionFn(stream As IntPtr, fn As IntPtr)
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Sub StreamSetUserDataFn(stream As IntPtr, data As IntPtr, freeFn As IntPtr)
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Sub StreamSetUserDataLengthFn(stream As IntPtr, length As ULong)
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function SetHandlerFn(codec As IntPtr, handler As IntPtr, user As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function VersionFn() As IntPtr

        ' Die Rueckrufe des Speicherstroms und der Meldungen.
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function StreamReadFn(buffer As IntPtr, count As UIntPtr, user As IntPtr) As UIntPtr
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function StreamSkipFn(count As Long, user As IntPtr) As Long
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function StreamSeekFn(position As Long, user As IntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Sub MessageFn(message As IntPtr, user As IntPtr)

        Private NotInheritable Class OpjApi
            Public CreateDecompress As CreateDecompressFn
            Public SetDefaultParameters As SetDefaultParametersFn
            Public SetupDecoder As SetupDecoderFn
            Public SetThreads As SetThreadsFn
            Public ReadHeader As ReadHeaderFn
            Public SetResolutionFactor As SetResolutionFactorFn
            Public SetDecodeArea As SetDecodeAreaFn
            Public Decode As DecodeFn
            Public EndDecompress As EndDecompressFn
            Public ImageDestroy As DestroyFn
            Public DestroyCodec As DestroyFn
            Public StreamCreate As StreamCreateFn
            Public StreamDestroy As DestroyFn
            Public StreamSetRead As StreamSetFunctionFn
            Public StreamSetSkip As StreamSetFunctionFn
            Public StreamSetSeek As StreamSetFunctionFn
            Public StreamSetUserData As StreamSetUserDataFn
            Public StreamSetUserDataLength As StreamSetUserDataLengthFn
            Public SetErrorHandler As SetHandlerFn
            Public Version As VersionFn
        End Class

        Private Shared ReadOnly _initLock As New Object()
        Private Shared ReadOnly _nativeLock As New Object()
        Private Shared _initialized As Boolean
        Private Shared _api As OpjApi
        Private Shared _loadedLibrary As String
        Private Shared _version As String = ""

        ' Die Rueckrufe bleiben fuer die Lebensdauer des Prozesses gehalten.
        Private Shared ReadOnly _readCallback As StreamReadFn = AddressOf OnRead
        Private Shared ReadOnly _skipCallback As StreamSkipFn = AddressOf OnSkip
        Private Shared ReadOnly _seekCallback As StreamSeekFn = AddressOf OnSeek
        Private Shared ReadOnly _errorCallback As MessageFn = AddressOf OnError
        <ThreadStatic> Private Shared _lastError As String

        ''' <summary>Der Speicherstrom hinter den Rueckrufen.</summary>
        Private NotInheritable Class MemorySource
            Public Bytes As Byte()
            Public Position As Long
        End Class

        Private Shared Function SourceOf(user As IntPtr) As MemorySource
            Return TryCast(GCHandle.FromIntPtr(user).Target, MemorySource)
        End Function

        Private Shared Function OnRead(buffer As IntPtr, count As UIntPtr, user As IntPtr) As UIntPtr
            Dim source = SourceOf(user)
            Dim remaining = source.Bytes.LongLength - source.Position
            ' (OPJ_SIZE_T)-1 heisst fuer OpenJPEG: nichts mehr da.
            If remaining <= 0 Then Return New UIntPtr(UInt64.MaxValue)
            Dim n = CInt(Math.Min(remaining, Math.Min(CLng(count.ToUInt64()), Integer.MaxValue)))
            Marshal.Copy(source.Bytes, CInt(source.Position), buffer, n)
            source.Position += n
            Return New UIntPtr(CULng(n))
        End Function

        Private Shared Function OnSkip(count As Long, user As IntPtr) As Long
            Dim source = SourceOf(user)
            Dim target = source.Position + count
            If target < 0 Then target = 0
            If target > source.Bytes.LongLength Then target = source.Bytes.LongLength
            Dim skipped = target - source.Position
            source.Position = target
            Return skipped
        End Function

        Private Shared Function OnSeek(position As Long, user As IntPtr) As Integer
            Dim source = SourceOf(user)
            If position < 0 OrElse position > source.Bytes.LongLength Then Return 0
            source.Position = position
            Return 1
        End Function

        Private Shared Sub OnError(message As IntPtr, user As IntPtr)
            Try
                _lastError = If(Marshal.PtrToStringAnsi(message), "").Trim()
            Catch
            End Try
        End Sub

        ' --- Erkennen ----------------------------------------------------------------------

        ''' <summary>Die Endungen nach Art: die drei ueblichen gelten nach der Endung, die
        ''' uebrigen nur mit passendem Kopf.</summary>
        Private Shared Function ExtensionKind(path As String) As Integer
            If String.IsNullOrWhiteSpace(path) Then Return 0
            Select Case IO.Path.GetExtension(path).ToLowerInvariant()
                Case ".jp2", ".j2k", ".jph" : Return 1
                Case ".j2c", ".jpc", ".jpf", ".jhc" : Return 2
                Case Else : Return 0
            End Select
        End Function

        ''' <summary>Alle Endungen, unter denen JPEG 2000 in der Galerie erscheint.</summary>
        Public Shared ReadOnly Extensions As String() = {".jp2", ".j2k", ".jph", ".j2c", ".jpc", ".jpf", ".jhc"}

        Private Shared ReadOnly _headerCache As New ConcurrentDictionary(Of String, (Length As Long, Stamp As Long, Header As Jp2Header))(StringComparer.Ordinal)
        Private Const HeaderCacheLimit As Integer = 4096

        ''' <summary>Was der Kopf sagt, ohne die Bibliothek.</summary>
        Friend NotInheritable Class Jp2Header
            Public Codec As Integer
            Public Width As Integer
            Public Height As Integer
            Public Components As Integer
            ''' <summary>Bittiefe der ersten Komponente, 0 wenn unbekannt.</summary>
            Public Precision As Integer
        End Class

        ''' <summary>Ist die Datei JPEG 2000, das hier gelesen wird? Nach der Endung (.jp2, .j2k,
        ''' .jph) oder, bei .j2c, .jpc, .jpf und .jhc, nach dem Kopf. Haengt NICHT davon ab, ob die
        ''' Bibliothek da ist: die Frage gilt auch dem Speichern.</summary>
        Public Shared Function IsSupportedJpeg2000(path As String) As Boolean
            Select Case ExtensionKind(path)
                Case 1 : Return True
                Case 2 : Return ReadHeader(path) IsNot Nothing
                Case Else : Return False
            End Select
        End Function

        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                EnsureLoaded()
                Return _api IsNot Nothing
            End Get
        End Property

        Public Shared ReadOnly Property LoadedLibrary As String
            Get
                EnsureLoaded()
                Return _loadedLibrary
            End Get
        End Property

        ''' <summary>Fassung der geladenen Bibliothek ("2.5.4"), leer ohne.</summary>
        Public Shared ReadOnly Property Version As String
            Get
                EnsureLoaded()
                Return _version
            End Get
        End Property

        ''' <summary>Traegt diese Datei mehr als 8 Bit ohne Alpha, und ist die Bibliothek da? Dann
        ''' gibt es ein tiefes Bild (ImageProcessor.IsDeepFileSource). Gefragt wird der Kopf: im
        ''' Behaelter die Angabe des Kastens ihdr, im Codestrom die der ersten Komponente; Alpha
        ''' (vier Komponenten in RGB, zwei in Grau) schliesst aus.</summary>
        Public Shared Function IsDeepJpeg2000(path As String) As Boolean
            If Not IsSupportedJpeg2000(path) Then Return False
            Dim header = ReadHeader(path)
            If header Is Nothing OrElse header.Precision <= 8 Then Return False
            If header.Components = 2 OrElse header.Components >= 4 Then Return False
            Return IsAvailable
        End Function

        ''' <summary>Masse aus dem Kopf, (0, 0) ohne. Keine Bibliothek, kein Decode.</summary>
        Public Shared Function TryGetSize(path As String) As (Width As Integer, Height As Integer)
            If Not IsSupportedJpeg2000(path) Then Return (0, 0)
            Dim header = ReadHeader(path)
            If header Is Nothing Then Return (0, 0)
            Return (header.Width, header.Height)
        End Function

        Friend Shared Function ReadHeader(path As String) As Jp2Header
            If ExtensionKind(path) = 0 Then Return Nothing
            Try
                Dim info = New FileInfo(path)
                If Not info.Exists Then Return Nothing
                Dim stamp = info.LastWriteTimeUtc.Ticks
                Dim cached As (Length As Long, Stamp As Long, Header As Jp2Header) = Nothing
                If _headerCache.TryGetValue(path, cached) AndAlso cached.Length = info.Length AndAlso cached.Stamp = stamp Then Return cached.Header
                Dim header As Jp2Header
                Using stream = New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete, 4096)
                    header = ParseHeader(stream)
                End Using
                If _headerCache.Count >= HeaderCacheLimit Then _headerCache.Clear()
                _headerCache(path) = (info.Length, stamp, header)
                Return header
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared ReadOnly Jp2Signature As Byte() = {0, 0, 0, &HC, &H6A, &H50, &H20, &H20, &HD, &HA, &H87, &HA}

        ''' <summary>Der Kopf eines JP2-Behaelters (Kasten ihdr in jp2h) oder eines Codestroms
        ''' (Marker SIZ), oder Nothing. Jede Laenge wird gegen das Dateiende geprueft.</summary>
        Friend Shared Function ParseHeader(stream As Stream) As Jp2Header
            Dim length = stream.Length
            Dim head(11) As Byte
            If stream.Read(head, 0, 12) < 12 Then Return Nothing
            Dim isJp2 = True
            For i = 0 To 11
                If head(i) <> Jp2Signature(i) Then isJp2 = False
            Next
            If Not isJp2 Then
                If head(0) = &HFF AndAlso head(1) = &H4F AndAlso head(2) = &HFF AndAlso head(3) = &H51 Then
                    stream.Position = 4
                    Return ParseSiz(stream, CodecJ2k)
                End If
                Return Nothing
            End If
            ' Die Kaesten der obersten Ebene bis jp2h, darin ihdr.
            Dim position = 12L
            For guard = 0 To 255
                Dim box = ReadBox(stream, position, length)
                If box.Type Is Nothing Then Return Nothing
                If box.Type = "jp2h" Then
                    Dim inner = box.ContentStart
                    While inner < box.End
                        Dim child = ReadBox(stream, inner, box.End)
                        If child.Type Is Nothing Then Return Nothing
                        If child.Type = "ihdr" AndAlso child.End - child.ContentStart >= 14 Then
                            stream.Position = child.ContentStart
                            Dim ihdr(13) As Byte
                            If stream.Read(ihdr, 0, 14) < 14 Then Return Nothing
                            Dim header As New Jp2Header With {
                                .Codec = CodecJp2,
                                .Height = CInt(Math.Min(Integer.MaxValue, BigEndian32(ihdr, 0))),
                                .Width = CInt(Math.Min(Integer.MaxValue, BigEndian32(ihdr, 4))),
                                .Components = (CInt(ihdr(8)) << 8) Or ihdr(9),
                                .Precision = If(ihdr(10) = &HFF, 0, (ihdr(10) And &H7F) + 1)
                            }
                            If header.Width <= 0 OrElse header.Height <= 0 OrElse header.Components <= 0 Then Return Nothing
                            ' 255 heisst: die Komponenten haben verschiedene Tiefen (Kasten bpcc);
                            ' dann gilt die des Codestroms.
                            If header.Precision = 0 Then header.Precision = FindCodestreamPrecision(stream, length)
                            Return header
                        End If
                        inner = child.End
                    End While
                    Return Nothing
                End If
                position = box.End
                If position >= length Then Return Nothing
            Next
            Return Nothing
        End Function

        Private Structure Box
            Public Type As String
            Public ContentStart As Long
            Public [End] As Long
        End Structure

        Private Shared Function ReadBox(stream As Stream, position As Long, limit As Long) As Box
            Dim result As New Box()
            If position + 8 > limit Then Return result
            stream.Position = position
            Dim head(7) As Byte
            If stream.Read(head, 0, 8) < 8 Then Return result
            Dim size = BigEndian32(head, 0)
            Dim type = Text.Encoding.ASCII.GetString(head, 4, 4)
            Dim contentStart = position + 8
            If size = 1 Then
                Dim ext(7) As Byte
                If stream.Read(ext, 0, 8) < 8 Then Return result
                size = (BigEndian32(ext, 0) << 32) Or BigEndian32(ext, 4)
                contentStart = position + 16
            ElseIf size = 0 Then
                size = limit - position
            End If
            If size < contentStart - position OrElse position + size > limit Then Return result
            result.Type = type
            result.ContentStart = contentStart
            result.End = position + size
            Return result
        End Function

        ''' <summary>Die Tiefe der ersten Komponente aus dem SIZ des Codestroms im Kasten jp2c.</summary>
        Private Shared Function FindCodestreamPrecision(stream As Stream, length As Long) As Integer
            Dim position = 12L
            For guard = 0 To 255
                Dim box = ReadBox(stream, position, length)
                If box.Type Is Nothing Then Return 0
                If box.Type = "jp2c" Then
                    stream.Position = box.ContentStart
                    Dim soc(3) As Byte
                    If stream.Read(soc, 0, 4) < 4 OrElse soc(0) <> &HFF OrElse soc(1) <> &H4F OrElse soc(2) <> &HFF OrElse soc(3) <> &H51 Then Return 0
                    Dim siz = ParseSiz(stream, CodecJp2)
                    Return If(siz Is Nothing, 0, siz.Precision)
                End If
                position = box.End
            Next
            Return 0
        End Function

        ''' <summary>Der Marker SIZ, der Strom steht hinter seiner Kennung FF51.</summary>
        Private Shared Function ParseSiz(stream As Stream, codec As Integer) As Jp2Header
            Dim fixedPart(37) As Byte
            If stream.Read(fixedPart, 0, 38) < 38 Then Return Nothing
            ' Lsiz(2) Rsiz(2) Xsiz Ysiz XOsiz YOsiz XTsiz YTsiz XTOsiz YTOsiz (je 4) Csiz(2)
            Dim xsiz = BigEndian32(fixedPart, 4), ysiz = BigEndian32(fixedPart, 8)
            Dim xo = BigEndian32(fixedPart, 12), yo = BigEndian32(fixedPart, 16)
            Dim components = (CInt(fixedPart(36)) << 8) Or fixedPart(37)
            If xsiz <= xo OrElse ysiz <= yo OrElse components <= 0 Then Return Nothing
            Dim first = stream.ReadByte()
            If first < 0 Then Return Nothing
            Return New Jp2Header With {
                .Codec = codec,
                .Width = CInt(Math.Min(Integer.MaxValue, xsiz - xo)),
                .Height = CInt(Math.Min(Integer.MaxValue, ysiz - yo)),
                .Components = components,
                .Precision = (first And &H7F) + 1
            }
        End Function

        Private Shared Function BigEndian32(data As Byte(), offset As Integer) As Long
            Return (CLng(data(offset)) << 24) Or (CLng(data(offset + 1)) << 16) Or (CLng(data(offset + 2)) << 8) Or data(offset + 3)
        End Function

        ' --- Bibliothek --------------------------------------------------------------------

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

        ''' <summary>Die Namen der mitgelieferten Datei je Plattform; im Flatpak liegt dieselbe aus
        ''' der Laufzeit im Suchpfad.</summary>
        Private Shared Function CandidateNames() As String()
            If OperatingSystem.IsWindows() Then Return {"libopenjp2.dll"}
            If OperatingSystem.IsMacOS() Then Return {"libopenjp2.dylib"}
            Return {"libopenjp2.so.7"}
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
                        _version = If(Marshal.PtrToStringAnsi(api.Version()), "")
                    Catch
                    End Try
                    Return
                Next
                DiagnosticLogService.LogAlways("Jpeg2000", "OpenJPEG nicht geladen - JPEG 2000 bleibt zu. Versucht: " & String.Join(", ", CandidateNames()))
            End SyncLock
        End Sub

        Private Shared Function Bind(handle As IntPtr) As OpjApi
            Try
                Return New OpjApi With {
                    .CreateDecompress = GetExport(Of CreateDecompressFn)(handle, "opj_create_decompress"),
                    .SetDefaultParameters = GetExport(Of SetDefaultParametersFn)(handle, "opj_set_default_decoder_parameters"),
                    .SetupDecoder = GetExport(Of SetupDecoderFn)(handle, "opj_setup_decoder"),
                    .SetThreads = GetExport(Of SetThreadsFn)(handle, "opj_codec_set_threads"),
                    .ReadHeader = GetExport(Of ReadHeaderFn)(handle, "opj_read_header"),
                    .SetResolutionFactor = GetExport(Of SetResolutionFactorFn)(handle, "opj_set_decoded_resolution_factor"),
                    .SetDecodeArea = GetExport(Of SetDecodeAreaFn)(handle, "opj_set_decode_area"),
                    .Decode = GetExport(Of DecodeFn)(handle, "opj_decode"),
                    .EndDecompress = GetExport(Of EndDecompressFn)(handle, "opj_end_decompress"),
                    .ImageDestroy = GetExport(Of DestroyFn)(handle, "opj_image_destroy"),
                    .DestroyCodec = GetExport(Of DestroyFn)(handle, "opj_destroy_codec"),
                    .StreamCreate = GetExport(Of StreamCreateFn)(handle, "opj_stream_create"),
                    .StreamDestroy = GetExport(Of DestroyFn)(handle, "opj_stream_destroy"),
                    .StreamSetRead = GetExport(Of StreamSetFunctionFn)(handle, "opj_stream_set_read_function"),
                    .StreamSetSkip = GetExport(Of StreamSetFunctionFn)(handle, "opj_stream_set_skip_function"),
                    .StreamSetSeek = GetExport(Of StreamSetFunctionFn)(handle, "opj_stream_set_seek_function"),
                    .StreamSetUserData = GetExport(Of StreamSetUserDataFn)(handle, "opj_stream_set_user_data"),
                    .StreamSetUserDataLength = GetExport(Of StreamSetUserDataLengthFn)(handle, "opj_stream_set_user_data_length"),
                    .SetErrorHandler = GetExport(Of SetHandlerFn)(handle, "opj_set_error_handler"),
                    .Version = GetExport(Of VersionFn)(handle, "opj_version")
                }
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function GetExport(Of T)(handle As IntPtr, name As String) As T
            Return Marshal.GetDelegateForFunctionPointer(Of T)(NativeLibrary.GetExport(handle, name))
        End Function

        ' --- Decode ------------------------------------------------------------------------

        ''' <summary>Das Bild als Bgra8888 mit geradlinigem Alpha (deckend: Opaque), in sRGB, Besitz
        ''' beim Aufrufer; Nothing, wenn die Datei nicht hierher gehoert oder sich nicht lesen laesst.</summary>
        Public Shared Function TryDecode(path As String) As SKBitmap
            Return TryDecodeReduced(path, 0)
        End Function

        ''' <summary>Wie <see cref="TryDecode"/>, aber verkleinert gelesen: die kleinste Stufe, deren
        ''' kuerzere Kante noch mindestens <paramref name="minShortSide"/> Punkte hat (0 heisst volle
        ''' Groesse). Fuer die Kachel.</summary>
        Public Shared Function TryDecodeReduced(path As String, minShortSide As Integer) As SKBitmap
            If Not IsSupportedJpeg2000(path) OrElse Not IsAvailable Then Return Nothing
            Return DecodeGate.Run(Of SKBitmap)(
                Function()
                    Dim deep = DecodeCore(path, minShortSide)
                    If deep Is Nothing Then Return Nothing
                    Using deep
                        Return RoundDeepToBgra(deep)
                    End Using
                End Function)
        End Function

        ''' <summary>Das tiefe Bild (Rgba16161616, deckend, in sRGB), Nothing ohne mehr als 8 Bit oder
        ''' mit Alpha. Das 8-Bit-Bild aus <see cref="TryDecode"/> ist genau dieses, gerundet.</summary>
        Friend Shared Function TryDecodeDeep(path As String) As SKBitmap
            If Not IsDeepJpeg2000(path) Then Return Nothing
            Return DecodeGate.Run(Of SKBitmap)(
                Function()
                    Dim deep = DecodeCore(path, 0)
                    If deep IsNot Nothing AndAlso deep.AlphaType <> SKAlphaType.Opaque Then
                        deep.Dispose()
                        Return Nothing
                    End If
                    Return deep
                End Function)
        End Function

        ''' <summary>Als PNG-Strom fuer die Wege, die einen Strom erwarten (OpenSourceStream, Kachel).</summary>
        Public Shared Function ExtractPreview(path As String, Optional minShortSide As Integer = 0) As MemoryStream
            Using bmp = TryDecodeReduced(path, minShortSide)
                If bmp Is Nothing Then Return Nothing
                Return PngEncoder.EncodeToStream(bmp, PngPurpose.Transient)
            End Using
        End Function

        ''' <summary>Was aus der Bibliothek kommt, ehe es ein Bild wird.</summary>
        Private NotInheritable Class Components
            Public Width As Integer
            Public Height As Integer
            Public ColorSpace As Integer
            Public Count As Integer
            Public Data As Integer()()
            Public CompWidth As Integer()
            Public CompHeight As Integer()
            Public Precision As Integer()
            Public Signed As Boolean()
            Public Alpha As Boolean()
            Public Icc As Byte()
        End Class

        Private Shared Function DecodeCore(path As String, minShortSide As Integer) As SKBitmap
            Dim header = ReadHeader(path)
            If header Is Nothing OrElse _api Is Nothing Then Return Nothing
            If CLng(header.Width) * header.Height > MaxPixels Then Return Nothing
            Dim bytes As Byte()
            Try
                If New FileInfo(path).Length > MaxFileBytes Then Return Nothing
                bytes = File.ReadAllBytes(path)
            Catch
                Return Nothing
            End Try
            Dim parts As Components
            SyncLock _nativeLock
                parts = DecodeComponents(bytes, header, minShortSide, path)
            End SyncLock
            If parts Is Nothing Then Return Nothing
            Dim deep = BuildDeepBitmap(parts)
            If deep Is Nothing Then Return Nothing
            Dim profile As SKColorSpace = Nothing
            Try
                If parts.Icc IsNot Nothing AndAlso parts.ColorSpace <> ColorSpaceCmyk Then profile = SKColorSpace.CreateIcc(parts.Icc)
                Dim managed = ColorManagementService.ToSrgb(deep, profile)
                If Not Object.ReferenceEquals(managed, deep) Then deep.Dispose()
                Return managed
            Catch ex As Exception
                DiagnosticLogService.LogException("Jpeg2000.Color", ex)
                Return deep
            Finally
                profile?.Dispose()
            End Try
        End Function

        Private Shared Function DecodeComponents(bytes As Byte(), header As Jp2Header, minShortSide As Integer, path As String) As Components
            Dim api = _api
            Dim codec = api.CreateDecompress(header.Codec)
            If codec = IntPtr.Zero Then Return Nothing
            Dim parameters = Marshal.AllocHGlobal(ParametersBufferSize)
            Dim stream = IntPtr.Zero
            Dim image = IntPtr.Zero
            Dim source As New MemorySource With {.Bytes = bytes}
            Dim sourceHandle = GCHandle.Alloc(source)
            _lastError = Nothing
            Try
                For i = 0 To ParametersBufferSize - 1 Step 8
                    Marshal.WriteInt64(parameters, i, 0)
                Next
                api.SetErrorHandler(codec, Marshal.GetFunctionPointerForDelegate(_errorCallback), IntPtr.Zero)
                api.SetDefaultParameters(parameters)
                If api.SetupDecoder(codec, parameters) = 0 Then Return Fail(path, "Einrichten")
                api.SetThreads(codec, Math.Max(1, Math.Min(8, Environment.ProcessorCount)))

                stream = api.StreamCreate(New UIntPtr(1UL << 20), 1)
                If stream = IntPtr.Zero Then Return Nothing
                api.StreamSetRead(stream, Marshal.GetFunctionPointerForDelegate(_readCallback))
                api.StreamSetSkip(stream, Marshal.GetFunctionPointerForDelegate(_skipCallback))
                api.StreamSetSeek(stream, Marshal.GetFunctionPointerForDelegate(_seekCallback))
                api.StreamSetUserData(stream, GCHandle.ToIntPtr(sourceHandle), IntPtr.Zero)
                api.StreamSetUserDataLength(stream, CULng(bytes.LongLength))

                If api.ReadHeader(stream, codec, image) = 0 OrElse image = IntPtr.Zero Then Return Fail(path, "Kopf")
                ' Grenzen nach dem Kopf der Bibliothek, vor dem Decode.
                Dim count = Marshal.ReadInt32(image, ImageNumComps)
                Dim comps = Marshal.ReadIntPtr(image, ImageComps)
                If count <= 0 OrElse count > 16384 OrElse comps = IntPtr.Zero Then Return Nothing
                Dim fullWidth = Marshal.ReadInt32(comps, CompW)
                Dim fullHeight = Marshal.ReadInt32(comps, CompH)
                If fullWidth <= 0 OrElse fullHeight <= 0 OrElse CLng(fullWidth) * fullHeight > MaxPixels Then Return Nothing
                For c = 0 To Math.Min(count, 4) - 1
                    Dim prec = Marshal.ReadInt32(IntPtr.Add(comps, c * CompSize), CompPrec)
                    If prec < 1 OrElse prec > 31 Then Return Fail(path, $"{prec} Bit je Komponente")
                Next

                ' Verkleinert: die groesste Stufe, deren kuerzere Kante noch reicht. Lehnt die
                ' Bibliothek sie ab (weniger Aufloesungsstufen in der Datei), eine weniger.
                Dim factor = 0
                If minShortSide > 0 Then
                    Dim shortSide = Math.Min(fullWidth, fullHeight)
                    While factor < 10 AndAlso (shortSide >> (factor + 1)) >= minShortSide
                        factor += 1
                    End While
                End If
                While factor > 0 AndAlso api.SetResolutionFactor(codec, CUInt(factor)) = 0
                    factor -= 1
                End While
                If api.SetDecodeArea(codec, image, 0, 0, 0, 0) = 0 Then Return Fail(path, "Bereich")
                If api.Decode(codec, stream, image) = 0 Then Return Fail(path, "Bildpunkte")
                api.EndDecompress(codec, stream)

                ' Die Komponenten lesen; Masse nach dem Decode, also verkleinert.
                count = Marshal.ReadInt32(image, ImageNumComps)
                comps = Marshal.ReadIntPtr(image, ImageComps)
                Dim used = Math.Min(count, 4)
                Dim result As New Components With {
                    .ColorSpace = Marshal.ReadInt32(image, ImageColorSpace), .Count = used,
                    .Data = New Integer(used - 1)() {}, .CompWidth = New Integer(used - 1) {}, .CompHeight = New Integer(used - 1) {},
                    .Precision = New Integer(used - 1) {}, .Signed = New Boolean(used - 1) {}, .Alpha = New Boolean(used - 1) {}}
                For c = 0 To used - 1
                    Dim comp = IntPtr.Add(comps, c * CompSize)
                    Dim w = Marshal.ReadInt32(comp, CompW)
                    Dim h = Marshal.ReadInt32(comp, CompH)
                    Dim data = Marshal.ReadIntPtr(comp, CompData)
                    If w <= 0 OrElse h <= 0 OrElse data = IntPtr.Zero OrElse CLng(w) * h > MaxPixels Then Return Fail(path, "keine Bildpunkte")
                    result.CompWidth(c) = w
                    result.CompHeight(c) = h
                    result.Precision(c) = Marshal.ReadInt32(comp, CompPrec)
                    result.Signed(c) = Marshal.ReadInt32(comp, CompSgnd) <> 0
                    result.Alpha(c) = Marshal.ReadInt16(comp, CompAlpha) <> 0
                    result.Data(c) = New Integer(w * h - 1) {}
                    Marshal.Copy(data, result.Data(c), 0, w * h)
                Next
                result.Width = result.CompWidth(0)
                result.Height = result.CompHeight(0)
                Dim iccLength = Marshal.ReadInt32(image, ImageIccLength)
                Dim iccBuffer = Marshal.ReadIntPtr(image, ImageIccBuffer)
                If iccBuffer <> IntPtr.Zero AndAlso iccLength > 128 AndAlso iccLength < 16 * 1024 * 1024 Then
                    result.Icc = New Byte(iccLength - 1) {}
                    Marshal.Copy(iccBuffer, result.Icc, 0, iccLength)
                End If
                Return result
            Catch ex As Exception
                DiagnosticLogService.LogException("Jpeg2000.Decode", ex)
                Return Nothing
            Finally
                If stream <> IntPtr.Zero Then api.StreamDestroy(stream)
                api.DestroyCodec(codec)
                If image <> IntPtr.Zero Then api.ImageDestroy(image)
                sourceHandle.Free()
                Marshal.FreeHGlobal(parameters)
            End Try
        End Function

        Private Shared Function Fail(path As String, stage As String) As Components
            DiagnosticLogService.LogAlways("Jpeg2000", $"{stage} nicht lesbar: {IO.Path.GetFileName(path)}{If(String.IsNullOrEmpty(_lastError), "", ": " & _lastError)}")
            Return Nothing
        End Function

        ''' <summary>Die Komponenten als Rgba16161616, auf 16 Bit gestreckt. Graustufen auf alle
        ''' drei Kanaele, sYCC und CMYK nach RGB, unterabgetastete Komponenten auf die Groesse der
        ''' ersten gezogen (naechster Nachbar). Alpha aus der als Alpha gekennzeichneten Komponente,
        ''' ohne Kennzeichnung aus der vierten in RGB und der zweiten in Grau.</summary>
        Private Shared Function BuildDeepBitmap(parts As Components) As SKBitmap
            Dim width = parts.Width
            Dim height = parts.Height
            Dim count = parts.Count
            Dim space = parts.ColorSpace
            If space = ColorSpaceUnknown OrElse space = ColorSpaceUnspecified Then
                If count <= 2 Then
                    space = ColorSpaceGray
                ElseIf parts.CompWidth(1) < width OrElse parts.CompHeight(1) < height Then
                    ' Unterabgetastete Farbkomponenten ohne Angabe: wie OpenJPEGs eigenes Werkzeug sYCC.
                    space = ColorSpaceSycc
                Else
                    space = ColorSpaceSrgb
                End If
            End If
            If space = ColorSpaceGray AndAlso count >= 3 Then space = ColorSpaceSrgb
            Dim isGray = space = ColorSpaceGray OrElse count < 3
            Dim isCmyk = space = ColorSpaceCmyk AndAlso count >= 4
            Dim isYcc = (space = ColorSpaceSycc OrElse space = ColorSpaceEycc) AndAlso count >= 3
            Dim colorCount = If(isGray, 1, If(isCmyk, 4, 3))
            Dim alphaIndex = -1
            For c = 0 To count - 1
                If parts.Alpha(c) Then alphaIndex = c : Exit For
            Next
            If alphaIndex < 0 AndAlso count > colorCount Then alphaIndex = colorCount
            If alphaIndex >= 0 AndAlso alphaIndex < colorCount AndAlso Not isGray Then alphaIndex = -1

            Dim alphaType = If(alphaIndex >= 0, SKAlphaType.Unpremul, SKAlphaType.Opaque)
            Dim bitmap = ImageProcessor.NewDeepBitmap(width, height, alphaType)
            If bitmap Is Nothing Then Return Nothing
            ' Je Komponente: Versatz fuer Vorzeichen, Hoechstwert und die Spalte bzw. Zeile der
            ' Komponente zu einer Spalte bzw. Zeile des Bilds.
            Dim offsets(count - 1) As Long
            Dim maxima(count - 1) As Long
            For c = 0 To count - 1
                offsets(c) = If(parts.Signed(c), 1L << (parts.Precision(c) - 1), 0L)
                maxima(c) = (1L << parts.Precision(c)) - 1L
            Next
            Try
                Dim target = bitmap.GetPixels()
                Dim stride = bitmap.RowBytes
                Threading.Tasks.Parallel.For(0, height,
                    Sub(y)
                        Dim row(width * 4 - 1) As Short
                        ' Wert einer Komponente am Bildpunkt, ohne Vorzeichen, 0 bis 1.
                        Dim ValueAt = Function(c As Integer, x As Integer) As Double
                                          Dim cw = parts.CompWidth(c), ch = parts.CompHeight(c)
                                          Dim cx = If(cw = width, x, CInt(CLng(x) * cw \ width))
                                          Dim cy = If(ch = height, y, CInt(CLng(y) * ch \ height))
                                          Dim v = parts.Data(c)(cy * cw + cx) + offsets(c)
                                          If v < 0 Then v = 0
                                          If v > maxima(c) Then v = maxima(c)
                                          Return v / CDbl(maxima(c))
                                      End Function
                        For x = 0 To width - 1
                            Dim r, g, b As Double
                            If isGray Then
                                r = ValueAt(0, x) : g = r : b = r
                            ElseIf isYcc Then
                                ' sYCC nach RGB wie in OpenJPEGs Werkzeug: Cb und Cr um die Mitte.
                                Dim yy = ValueAt(0, x), cb = ValueAt(1, x) - 0.5, cr = ValueAt(2, x) - 0.5
                                r = yy + 1.402 * cr
                                g = yy - 0.344136 * cb - 0.714136 * cr
                                b = yy + 1.772 * cb
                            ElseIf isCmyk Then
                                Dim k = 1.0 - ValueAt(3, x)
                                r = (1.0 - ValueAt(0, x)) * k
                                g = (1.0 - ValueAt(1, x)) * k
                                b = (1.0 - ValueAt(2, x)) * k
                            Else
                                r = ValueAt(0, x) : g = ValueAt(1, x) : b = ValueAt(2, x)
                            End If
                            Dim o = x * 4
                            row(o) = ToShort(r)
                            row(o + 1) = ToShort(g)
                            row(o + 2) = ToShort(b)
                            row(o + 3) = If(alphaIndex >= 0, ToShort(ValueAt(alphaIndex, x)), -1S)
                        Next
                        Marshal.Copy(row, 0, New IntPtr(target.ToInt64() + CLng(y) * stride), width * 4)
                    End Sub)
                Return bitmap
            Catch ex As Exception
                DiagnosticLogService.LogException("Jpeg2000.Build", ex)
                bitmap.Dispose()
                Return Nothing
            End Try
        End Function

        Private Shared Function ToShort(value As Double) As Short
            Dim v = CInt(Math.Round(Math.Max(0.0, Math.Min(1.0, value)) * 65535.0))
            Return ImageProcessor.ToDeepShort(v)
        End Function

        ''' <summary>Das 8-Bit-Bild aus dem tiefen, je Kanal gerundet wie bei TIFF und HEIF.</summary>
        Private Shared Function RoundDeepToBgra(deep As SKBitmap) As SKBitmap
            Dim width = deep.Width
            Dim height = deep.Height
            Dim alphaType = If(deep.AlphaType = SKAlphaType.Opaque, SKAlphaType.Opaque, SKAlphaType.Unpremul)
            Dim bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, alphaType))
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

    End Class

End Namespace
