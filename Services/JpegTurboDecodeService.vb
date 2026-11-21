Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Liest die JPEG-Dateien, die SkiaSharp nicht kann: 12 Bit (SOF1, "extended", auch
    ''' progressiv oder arithmetisch kodiert) und verlustfreies JPEG (SOF3, 2 bis 16 Bit). Die
    ''' libjpeg-turbo in Skia ist nur fuer 8 Bit uebersetzt; fuer solche Dateien gibt SKCodec.Create
    ''' Nothing zurueck, und die Flaeche bliebe ueberall leer. Vorkommen tun sie in der Medizin
    ''' (DICOM), in der Wissenschaft, bei Scannern und in den Vorschauen mancher Kameras.
    '''
    ''' NUR EIN RUECKFALL: jedes gewoehnliche JPEG geht weiter ueber Skia. Welche Datei hierher
    ''' gehoert, entscheidet der Kopf (<see cref="IsTurboJpeg"/>): ein SOF-Marker fuer verlustfreies
    ''' JPEG (SOF3, 7, 11, 15) oder eine Tiefe ungleich 8. Genau diese Dateien lehnt Skia ab
    ''' (gemessen: 8 Bit baseline, progressiv und arithmetisch liest es, 12 Bit und verlustfrei in
    ''' jeder Tiefe nicht).
    '''
    ''' Gelesen wird ueber die TurboJPEG-Schnittstelle 3 von libjpeg-turbo, dynamisch geladen wie
    ''' libheif und libjxl. Mitgeliefert ist sie als ferrumpix-turbojpeg (packaging/libjpeg-turbo),
    ''' umbenannt, damit sie sich mit keiner libturbojpeg des Systems verwechseln laesst; die des
    ''' Systems ist der Rueckfall (BundledLibraryOrder) und muss mindestens Fassung 3.1 sein
    ''' (tj3GetICCProfile; erst ab 3.1 liest tj3Decompress8 bis 16 verlustfreies JPEG in jeder Tiefe
    ''' von 2 bis 16 Bit). Der Flatpak nimmt libturbojpeg.so.0 aus der Laufzeit 26.08 (3.2.0).
    '''
    ''' Was herauskommt, gleicht den anderen eigenen Lesern (ForeignImageDecoder): Bgra8888 mit
    ''' geradlinigem Alpha, Drehung aus dem EXIF schon angewandt, Farbe schon nach sRGB gewandelt.
    ''' Traegt die Datei mehr als 8 Bit und kein Alpha, gibt es ein tiefes Bild mit 16 Bit
    ''' (<see cref="TryDecodeDeep"/>), wie bei TIFF mit 16 Bit und HEIF mit 10 und 12 Bit; das
    ''' 8-Bit-Bild ist dann genau das tiefe, gerundet.
    '''
    ''' Vier Komponenten: mit dem Adobe-Marker (APP14) ist es CMYK in Adobes umgekehrter Form (so
    ''' schreiben es Photoshop und libjpeg), ohne ihn RGB mit Alpha (so schreiben es
    ''' wissenschaftliche Werkzeuge, etwa imagecodecs).
    '''
    ''' GESCHRIEBEN wird nichts: ein solches JPEG wird nie ueberschrieben, Skia machte daraus ein
    ''' 8-Bit-JPEG. Speichern geht ins Rezept daneben (RawSidecarService.SavesRecipeBeside), und
    ''' SaveImage weist jedes Ziel ab, das eine solche Datei ist.
    ''' </summary>
    Public NotInheritable Class JpegTurboDecodeService

        Private Sub New()
        End Sub

        ''' <summary>Obergrenze gegen Speicherausfaelle: der Weg haelt je Bildpunkt die Werte der
        ''' Datei, das tiefe Bild (8 Byte) und das 8-Bit-Bild zugleich.</summary>
        Private Const MaxPixels As Long = 200L * 1000L * 1000L
        ''' <summary>Groesste Datei, die gelesen wird. Ein verlustfreies 16-Bit-Bild mit 200 MP hat
        ''' unkomprimiert 1,2 GB; mehr als die Haelfte davon bleibt nach der Kompression selten.</summary>
        Private Const MaxFileBytes As Long = 1024L * 1024L * 1024L

        ' Die Werte aus turbojpeg.h (3.2), alle seit 3.0 beziehungsweise 3.1 unveraendert.
        Private Const TjInitDecompress As Integer = 1
        Private Const TjParamJpegWidth As Integer = 5
        Private Const TjParamJpegHeight As Integer = 6
        Private Const TjParamPrecision As Integer = 7
        Private Const TjParamColorspace As Integer = 8
        Private Const TjParamSaveMarkers As Integer = 25
        Private Const TjPfRgb As Integer = 0
        Private Const TjPfGray As Integer = 6
        Private Const TjPfCmyk As Integer = 11
        Private Const TjCsGray As Integer = 2
        Private Const TjCsCmyk As Integer = 3
        Private Const TjCsYcck As Integer = 4

        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function InitFn(initType As Integer) As IntPtr
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Sub DestroyFn(handle As IntPtr)
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function SetFn(handle As IntPtr, param As Integer, value As Integer) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function GetFn(handle As IntPtr, param As Integer) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function DecompressHeaderFn(handle As IntPtr, jpegBuf As IntPtr, jpegSize As UIntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function DecompressFn(handle As IntPtr, jpegBuf As IntPtr, jpegSize As UIntPtr,
                                               dstBuf As IntPtr, pitch As Integer, pixelFormat As Integer) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function GetIccProfileFn(handle As IntPtr, ByRef iccBuf As IntPtr, ByRef iccSize As UIntPtr) As Integer
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Sub FreeFn(buffer As IntPtr)
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Private Delegate Function GetErrorStrFn(handle As IntPtr) As IntPtr

        Private NotInheritable Class TurboApi
            Public Init As InitFn
            Public Destroy As DestroyFn
            Public SetParam As SetFn
            Public GetParam As GetFn
            Public DecompressHeader As DecompressHeaderFn
            Public Decompress8 As DecompressFn
            Public Decompress12 As DecompressFn
            Public Decompress16 As DecompressFn
            Public GetIccProfile As GetIccProfileFn
            Public FreeBuffer As FreeFn
            Public GetErrorStr As GetErrorStrFn
        End Class

        Private Shared ReadOnly _initLock As New Object()
        Private Shared ReadOnly _nativeLock As New Object()
        Private Shared _initialized As Boolean
        Private Shared _api As TurboApi
        Private Shared _library As IntPtr
        Private Shared _loadedLibrary As String

        ''' <summary>Was der Kopf einer JPEG-Datei sagt, ohne die Bilddaten zu lesen.</summary>
        Friend NotInheritable Class JpegHeader
            Public Property Width As Integer
            Public Property Height As Integer
            Public Property Precision As Integer
            Public Property Components As Integer
            ''' <summary>Nummer des SOF-Markers (0 bis 15 ohne 4, 8 und 12).</summary>
            Public Property FrameType As Integer
            Public Property HasAdobeMarker As Boolean
            ''' <summary>EXIF-Ausrichtung 1 bis 8, ohne Angabe 1.</summary>
            Public Property Orientation As Integer = 1

            Public ReadOnly Property IsLossless As Boolean
                Get
                    Return FrameType = 3 OrElse FrameType = 7 OrElse FrameType = 11 OrElse FrameType = 15
                End Get
            End Property

            ''' <summary>Liest Skia das nicht? Verlustfrei in jeder Tiefe und jede Tiefe ausser 8.</summary>
            Public ReadOnly Property NeedsTurbo As Boolean
                Get
                    Return IsLossless OrElse Precision <> 8
                End Get
            End Property

            ''' <summary>Vier Komponenten ohne Adobe-Marker: RGB mit Alpha.</summary>
            Public ReadOnly Property HasAlpha As Boolean
                Get
                    Return Components = 4 AndAlso Not HasAdobeMarker
                End Get
            End Property

            Public ReadOnly Property SwapsAxes As Boolean
                Get
                    Return Orientation >= 5 AndAlso Orientation <= 8
                End Get
            End Property
        End Class

        ''' <summary>Je Datei der zuletzt gelesene Kopf, mit Groesse und Zeitstempel als Schluessel.
        ''' Die Frage <see cref="IsTurboJpeg"/> stellen Kachel, Betrachter und Editor fuer jedes JPEG;
        ''' der Kopf soll dafuer nur einmal gelesen werden.</summary>
        Private Shared ReadOnly _headerCache As New ConcurrentDictionary(Of String, (Length As Long, Stamp As Long, Header As JpegHeader))(StringComparer.Ordinal)
        Private Const HeaderCacheLimit As Integer = 4096

        ''' <summary>Die Endungen, unter denen ein JPEG liegt (wie MediaFileTypes).</summary>
        Public Shared Function IsJpegExtension(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Select Case IO.Path.GetExtension(path).ToLowerInvariant()
                Case ".jpg", ".jpeg", ".jpe", ".jfif", ".jps"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        ''' <summary>Ein JPEG, das Skia nicht liest und das hierher gehoert: verlustfrei oder mit
        ''' einer Tiefe ungleich 8. Liest nur den Kopf bis zum SOF-Marker. Haengt NICHT davon ab, ob
        ''' die Bibliothek da ist: die Frage gilt auch dem Speichern, und ein solches JPEG darf nie
        ''' ueberschrieben werden, auch wenn es sich gerade nicht lesen laesst.</summary>
        Public Shared Function IsTurboJpeg(path As String) As Boolean
            Dim header = ReadHeader(path)
            Return header IsNot Nothing AndAlso header.NeedsTurbo
        End Function

        ''' <summary>Traegt diese Datei mehr als 8 Bit in einer Form, die sich tief lesen laesst
        ''' (ohne Alpha, wie bei TIFF), und ist die Bibliothek da?</summary>
        Public Shared Function IsDeepJpeg(path As String) As Boolean
            Dim header = ReadHeader(path)
            If header Is Nothing OrElse Not header.NeedsTurbo Then Return False
            Return header.Precision > 8 AndAlso Not header.HasAlpha AndAlso IsAvailable
        End Function

        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                EnsureLoaded()
                Return _api IsNot Nothing
            End Get
        End Property

        ''' <summary>Dateiname der geladenen Bibliothek, fuer Meldungen und die Pruefung.</summary>
        Public Shared ReadOnly Property LoadedLibrary As String
            Get
                EnsureLoaded()
                Return _loadedLibrary
            End Get
        End Property

        ''' <summary>Masse aus dem Kopf, gedreht wie der Decode. (0, 0) fuer eine Datei, die nicht
        ''' hierher gehoert. Kein Decode und keine Bibliothek noetig, also auch nicht durch
        ''' <see cref="DecodeGate"/>.</summary>
        Public Shared Function TryGetSize(path As String) As (Width As Integer, Height As Integer)
            Dim header = ReadHeader(path)
            If header Is Nothing OrElse Not header.NeedsTurbo Then Return (0, 0)
            If header.SwapsAxes Then Return (header.Height, header.Width)
            Return (header.Width, header.Height)
        End Function

        ''' <summary>Masse aus dem Kopf, wie sie gespeichert sind (ohne die Drehung aus dem EXIF),
        ''' so wie SKCodec sie fuer jedes andere JPEG meldet. (0, 0) fuer eine Datei, die nicht
        ''' hierher gehoert.</summary>
        Public Shared Function TryGetEncodedSize(path As String) As (Width As Integer, Height As Integer)
            Dim header = ReadHeader(path)
            If header Is Nothing OrElse Not header.NeedsTurbo Then Return (0, 0)
            Return (header.Width, header.Height)
        End Function

        ''' <summary>Das Bild als Bgra8888 mit geradlinigem Alpha (deckend: Opaque), gedreht und in
        ''' sRGB, Besitz beim Aufrufer; Nothing, wenn die Datei nicht hierher gehoert oder sich nicht
        ''' lesen laesst. Keine zweite Drehung nachschalten.</summary>
        Public Shared Function TryDecode(path As String) As SKBitmap
            If Not IsTurboJpeg(path) OrElse Not IsAvailable Then Return Nothing
            Return DecodeGate.Run(Of SKBitmap)(
                Function()
                    Dim deep = DecodeCore(path)
                    If deep Is Nothing Then Return Nothing
                    Using deep
                        Return RoundDeepToBgra(deep)
                    End Using
                End Function)
        End Function

        ''' <summary>Das tiefe Bild mit 16 Bit je Kanal (Rgba16161616, deckend, gedreht und in sRGB),
        ''' Besitz beim Aufrufer; Nothing ohne mehr als 8 Bit oder mit Alpha. DAS TIEFE GEGENSTUECK
        ''' ZU <see cref="TryDecode"/>: dieselben Bildpunkte, nur ohne den Schritt auf 8 Bit
        ''' (ImageProcessor.DeepMatchesShallow prueft das vor dem Schreiben).</summary>
        Friend Shared Function TryDecodeDeep(path As String) As SKBitmap
            If Not IsDeepJpeg(path) Then Return Nothing
            Return DecodeGate.Run(Of SKBitmap)(Function() DecodeCore(path))
        End Function

        ''' <summary>Die Datei als PNG-Strom, fuer die Wege, die einen Strom erwarten
        ''' (OpenSourceStream, Kachel), gleiches Muster wie TIFF.</summary>
        Public Shared Function ExtractPreview(path As String) As MemoryStream
            Using bmp = TryDecode(path)
                If bmp Is Nothing Then Return Nothing
                Return PngEncoder.EncodeToStream(bmp, PngPurpose.Transient)
            End Using
        End Function

        ' --- Kopf --------------------------------------------------------------------------

        ''' <summary>Der Kopf einer JPEG-Datei, oder Nothing, wenn es keines ist oder er sich nicht
        ''' lesen laesst. Aus dem Zwischenspeicher, solange Groesse und Zeitstempel gleich sind.</summary>
        Friend Shared Function ReadHeader(path As String) As JpegHeader
            If Not IsJpegExtension(path) Then Return Nothing
            Try
                Dim info = New FileInfo(path)
                If Not info.Exists Then Return Nothing
                Dim stamp = info.LastWriteTimeUtc.Ticks
                Dim cached As (Length As Long, Stamp As Long, Header As JpegHeader) = Nothing
                If _headerCache.TryGetValue(path, cached) AndAlso cached.Length = info.Length AndAlso cached.Stamp = stamp Then
                    Return cached.Header
                End If
                Dim header As JpegHeader
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

        ''' <summary>Geht die Marker bis zum ersten SOF durch und springt ueber alles andere. Merkt
        ''' sich unterwegs den Adobe-Marker (APP14) und die EXIF-Ausrichtung (APP1). Jeder Abstand
        ''' wird gegen das Dateiende geprueft; eine beschaedigte Datei gibt Nothing.</summary>
        Friend Shared Function ParseHeader(stream As Stream) As JpegHeader
            If stream Is Nothing OrElse Not stream.CanSeek Then Return Nothing
            Dim soi(1) As Byte
            If stream.Read(soi, 0, 2) <> 2 OrElse soi(0) <> &HFF OrElse soi(1) <> &HD8 Then Return Nothing
            Dim result As New JpegHeader()
            Dim length = stream.Length
            For guard = 0 To 1023
                ' Fuellbytes &HFF vor dem Marker sind erlaubt.
                Dim b = stream.ReadByte()
                If b < 0 Then Return Nothing
                If b <> &HFF Then Return Nothing
                Dim marker = stream.ReadByte()
                While marker = &HFF
                    marker = stream.ReadByte()
                End While
                If marker < 0 Then Return Nothing
                ' Marker ohne Laenge: TEM und RST.
                If marker = &H1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) Then Continue For
                If marker = &HD9 OrElse marker = &HDA Then Return Nothing
                Dim hi = stream.ReadByte()
                Dim lo = stream.ReadByte()
                If hi < 0 OrElse lo < 0 Then Return Nothing
                Dim segmentLength = (hi << 8) Or lo
                If segmentLength < 2 Then Return Nothing
                Dim payloadStart = stream.Position
                If payloadStart + segmentLength - 2 > length Then Return Nothing
                Dim isSof = marker >= &HC0 AndAlso marker <= &HCF AndAlso marker <> &HC4 AndAlso marker <> &HC8 AndAlso marker <> &HCC
                If isSof Then
                    If segmentLength < 8 Then Return Nothing
                    Dim sof(5) As Byte
                    If stream.Read(sof, 0, 6) <> 6 Then Return Nothing
                    result.FrameType = marker - &HC0
                    result.Precision = sof(0)
                    result.Height = (CInt(sof(1)) << 8) Or sof(2)
                    result.Width = (CInt(sof(3)) << 8) Or sof(4)
                    result.Components = sof(5)
                    If result.Width <= 0 OrElse result.Height <= 0 OrElse result.Components <= 0 Then Return Nothing
                    Return result
                End If
                If marker = &HEE AndAlso segmentLength >= 7 Then
                    Dim adobe(4) As Byte
                    If stream.Read(adobe, 0, 5) = 5 AndAlso adobe(0) = AscW("A"c) AndAlso adobe(1) = AscW("d"c) AndAlso
                       adobe(2) = AscW("o"c) AndAlso adobe(3) = AscW("b"c) AndAlso adobe(4) = AscW("e"c) Then
                        result.HasAdobeMarker = True
                    End If
                ElseIf marker = &HE1 AndAlso segmentLength > 16 Then
                    Dim payload(segmentLength - 3) As Byte
                    If stream.Read(payload, 0, payload.Length) = payload.Length Then
                        Dim orientation = ExifOrientation(payload)
                        If orientation >= 1 AndAlso orientation <= 8 Then result.Orientation = orientation
                    End If
                End If
                stream.Seek(payloadStart + segmentLength - 2, SeekOrigin.Begin)
            Next
            Return Nothing
        End Function

        ''' <summary>Die Ausrichtung aus einem APP1-Block "Exif\0\0" (Tag 0x0112 in IFD0), oder 0.</summary>
        Private Shared Function ExifOrientation(payload As Byte()) As Integer
            If payload.Length < 14 OrElse payload(0) <> AscW("E"c) OrElse payload(1) <> AscW("x"c) OrElse
               payload(2) <> AscW("i"c) OrElse payload(3) <> AscW("f"c) OrElse payload(4) <> 0 OrElse payload(5) <> 0 Then Return 0
            Const tiff As Integer = 6
            Dim little As Boolean
            If payload(tiff) = &H49 AndAlso payload(tiff + 1) = &H49 Then
                little = True
            ElseIf payload(tiff) = &H4D AndAlso payload(tiff + 1) = &H4D Then
                little = False
            Else
                Return 0
            End If
            Dim u16 = Function(offset As Integer) As Integer
                          If offset < 0 OrElse offset + 2 > payload.Length Then Return -1
                          Return If(little, CInt(payload(offset)) Or (CInt(payload(offset + 1)) << 8),
                                            (CInt(payload(offset)) << 8) Or CInt(payload(offset + 1)))
                      End Function
            Dim u32 = Function(offset As Integer) As Long
                          If offset < 0 OrElse offset + 4 > payload.Length Then Return -1
                          If little Then
                              Return CLng(payload(offset)) Or (CLng(payload(offset + 1)) << 8) Or
                                     (CLng(payload(offset + 2)) << 16) Or (CLng(payload(offset + 3)) << 24)
                          End If
                          Return (CLng(payload(offset)) << 24) Or (CLng(payload(offset + 1)) << 16) Or
                                 (CLng(payload(offset + 2)) << 8) Or CLng(payload(offset + 3))
                      End Function
            Dim ifd = u32(tiff + 4)
            If ifd < 8 OrElse tiff + ifd + 2 > payload.Length Then Return 0
            Dim start = tiff + CInt(ifd)
            Dim count = u16(start)
            If count <= 0 Then Return 0
            For i = 0 To Math.Min(count, 512) - 1
                Dim entry = start + 2 + i * 12
                If entry + 12 > payload.Length Then Return 0
                If u16(entry) = &H112 Then Return u16(entry + 8)
            Next
            Return 0
        End Function

        ' --- Bibliothek --------------------------------------------------------------------

        ''' <summary>Erst der blosse Name, dann die Orte, an denen eine mitgelieferte Bibliothek
        ''' liegen kann: neben der Anwendung und unter runtimes/&lt;rid&gt;/native (wohin
        ''' packaging/package.sh sie legt), unter macOS dazu die beiden Homebrew-Verzeichnisse.
        ''' Dieselbe Liste wie bei libheif; welche zuerst probiert wird, entscheidet danach
        ''' BundledLibraryOrder.</summary>
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
                If OperatingSystem.IsMacOS() Then
                    Yield IO.Path.Combine("/opt/homebrew/lib", name)
                    Yield IO.Path.Combine("/usr/local/lib", name)
                End If
                Yield IO.Path.Combine(baseDir, name)
                If rid.Length > 0 Then Yield IO.Path.Combine(baseDir, "runtimes", rid, "native", name)
            Next
        End Function

        ''' <summary>Die Namen, unter denen gesucht wird: zuerst die mitgelieferte, dann die des
        ''' Systems (Linux libturbojpeg.so.0, Homebrew libturbojpeg.0.dylib, unter Windows etwa aus
        ''' vcpkg turbojpeg.dll).</summary>
        Private Shared Function CandidateNames() As String()
            If OperatingSystem.IsWindows() Then Return {"ferrumpix-turbojpeg.dll", "turbojpeg.dll"}
            If OperatingSystem.IsMacOS() Then Return {"libferrumpix-turbojpeg.dylib", "libturbojpeg.0.dylib", "libturbojpeg.dylib"}
            Return {"libferrumpix-turbojpeg.so.0", "libturbojpeg.so.0"}
        End Function

        Private Shared Sub EnsureLoaded()
            SyncLock _initLock
                If _initialized Then Return
                _initialized = True
                Dim names = CandidateNames()
                Dim tried As New List(Of String)()
                For Each candidate In BundledLibraryOrder.Arrange(BundledCandidates(names))
                    Dim handle As IntPtr
                    If Not NativeLibrary.TryLoad(candidate, handle) Then Continue For
                    ' Eine TurboJPEG vor 3.1 (Ubuntu 24.04 fuehrt 2.1) laedt, kennt aber tj3GetICCProfile
                    ' nicht: dann weiter zur naechsten, statt still mit einer zu alten zu laufen.
                    Dim api = Bind(handle)
                    If api Is Nothing Then
                        tried.Add(IO.Path.GetFileName(candidate) & " (vor 3.1)")
                        NativeLibrary.Free(handle)
                        Continue For
                    End If
                    _api = api
                    _library = handle
                    _loadedLibrary = IO.Path.GetFileName(candidate)
                    Return
                Next
                DiagnosticLogService.LogAlways("JpegTurbo",
                    "keine TurboJPEG ab 3.1 geladen - JPEG mit 12 Bit und verlustfreies JPEG bleiben zu. Versucht: " &
                    String.Join(", ", names) & If(tried.Count > 0, "; zu alt: " & String.Join(", ", tried), ""))
            End SyncLock
        End Sub

        Private Shared Function Bind(handle As IntPtr) As TurboApi
            Try
                Return New TurboApi With {
                    .Init = GetExport(Of InitFn)(handle, "tj3Init"),
                    .Destroy = GetExport(Of DestroyFn)(handle, "tj3Destroy"),
                    .SetParam = GetExport(Of SetFn)(handle, "tj3Set"),
                    .GetParam = GetExport(Of GetFn)(handle, "tj3Get"),
                    .DecompressHeader = GetExport(Of DecompressHeaderFn)(handle, "tj3DecompressHeader"),
                    .Decompress8 = GetExport(Of DecompressFn)(handle, "tj3Decompress8"),
                    .Decompress12 = GetExport(Of DecompressFn)(handle, "tj3Decompress12"),
                    .Decompress16 = GetExport(Of DecompressFn)(handle, "tj3Decompress16"),
                    .GetIccProfile = GetExport(Of GetIccProfileFn)(handle, "tj3GetICCProfile"),
                    .FreeBuffer = GetExport(Of FreeFn)(handle, "tj3Free"),
                    .GetErrorStr = GetExport(Of GetErrorStrFn)(handle, "tj3GetErrorStr")
                }
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function GetExport(Of T)(handle As IntPtr, name As String) As T
            Return Marshal.GetDelegateForFunctionPointer(Of T)(NativeLibrary.GetExport(handle, name))
        End Function

        ' --- Decode ------------------------------------------------------------------------

        ''' <summary>Das Bild in 16 Bit je Kanal, gedreht und in sRGB: Rgba16161616, deckend oder
        ''' mit geradlinigem Alpha. Grundlage beider Wege, damit das 8-Bit-Bild genau das tiefe ist.
        ''' Laeuft unter <see cref="DecodeGate"/> (der Aufrufer nimmt es).</summary>
        Private Shared Function DecodeCore(path As String) As SKBitmap
            Dim header = ReadHeader(path)
            If header Is Nothing OrElse Not header.NeedsTurbo OrElse _api Is Nothing Then Return Nothing
            If CLng(header.Width) * header.Height > MaxPixels Then Return Nothing
            Dim jpeg As Byte()
            Try
                Dim info = New FileInfo(path)
                If info.Length > MaxFileBytes Then Return Nothing
                jpeg = File.ReadAllBytes(path)
            Catch
                Return Nothing
            End Try

            Dim width As Integer
            Dim height As Integer
            Dim channels As Integer
            Dim precision As Integer
            Dim kind As Integer
            Dim icc As Byte() = Nothing
            Dim samples As UShort() = Nothing
            SyncLock _nativeLock
                Dim api = _api
                Dim handle = api.Init(TjInitDecompress)
                If handle = IntPtr.Zero Then Return Nothing
                Dim pin = GCHandle.Alloc(jpeg, GCHandleType.Pinned)
                Try
                    Dim source = pin.AddrOfPinnedObject()
                    Dim size = New UIntPtr(CULng(jpeg.LongLength))
                    ' 2: alle Marker sichern, sonst findet tj3GetICCProfile kein Profil.
                    api.SetParam(handle, TjParamSaveMarkers, 2)
                    If api.DecompressHeader(handle, source, size) <> 0 Then
                        LogError(api, handle, path, "Kopf")
                        Return Nothing
                    End If
                    width = api.GetParam(handle, TjParamJpegWidth)
                    height = api.GetParam(handle, TjParamJpegHeight)
                    precision = api.GetParam(handle, TjParamPrecision)
                    Dim colorspace = api.GetParam(handle, TjParamColorspace)
                    If width <> header.Width OrElse height <> header.Height OrElse precision < 2 OrElse precision > 16 Then Return Nothing

                    Dim pixelFormat As Integer
                    If colorspace = TjCsGray Then
                        pixelFormat = TjPfGray : channels = 1 : kind = KindGray
                    ElseIf colorspace = TjCsCmyk OrElse colorspace = TjCsYcck Then
                        pixelFormat = TjPfCmyk : channels = 4
                        kind = If(header.HasAdobeMarker, KindCmyk, KindRgba)
                    Else
                        pixelFormat = TjPfRgb : channels = 3 : kind = KindRgb
                    End If

                    Dim iccBuffer As IntPtr
                    Dim iccSize As UIntPtr
                    If api.GetIccProfile(handle, iccBuffer, iccSize) = 0 AndAlso iccBuffer <> IntPtr.Zero Then
                        Try
                            Dim n = CLng(iccSize.ToUInt64())
                            If n > 0 AndAlso n < 16L * 1024 * 1024 Then
                                icc = New Byte(CInt(n) - 1) {}
                                Marshal.Copy(iccBuffer, icc, 0, CInt(n))
                            End If
                        Finally
                            api.FreeBuffer(iccBuffer)
                        End Try
                    End If

                    Dim count = CLng(width) * height * channels
                    If count > Integer.MaxValue Then Return Nothing
                    samples = New UShort(CInt(count) - 1) {}
                    If precision <= 8 Then
                        Dim bytes = New Byte(CInt(count) - 1) {}
                        If Not RunDecompress(api.Decompress8, api, handle, source, size, bytes, pixelFormat, path) Then Return Nothing
                        For i = 0 To bytes.Length - 1
                            samples(i) = bytes(i)
                        Next
                    Else
                        ' 9 bis 12 Bit ueber tj3Decompress12 (short), 13 bis 16 ueber tj3Decompress16
                        ' (unsigned short); beide schreiben zwei Byte je Wert in derselben Breite.
                        Dim fn = If(precision <= 12, api.Decompress12, api.Decompress16)
                        If Not RunDecompress(fn, api, handle, source, size, samples, pixelFormat, path) Then Return Nothing
                    End If
                Finally
                    pin.Free()
                    api.Destroy(handle)
                End Try
            End SyncLock

            Dim deep = BuildDeepBitmap(samples, width, height, channels, precision, kind, header.Orientation)
            If deep Is Nothing Then Return Nothing
            Dim profile As SKColorSpace = Nothing
            Try
                ' Ein CMYK-Profil versteht Skia nicht (CreateIcc gibt Nothing); die Werte sind
                ' dann wie ueblich ohne Profil nach RGB gerechnet.
                If icc IsNot Nothing AndAlso kind <> KindCmyk Then profile = SKColorSpace.CreateIcc(icc)
                If profile Is Nothing AndAlso kind <> KindCmyk Then
                    ' Ohne eingebettetes Profil fragt die Farbverwaltung das EXIF (Adobe RGB), wie
                    ' bei jedem anderen JPEG.
                    Using data = SKData.CreateCopy(jpeg)
                        profile = ColorManagementService.EffectiveProfile(Nothing, data)
                    End Using
                End If
                Dim managed = ColorManagementService.ToSrgb(deep, profile)
                If Not Object.ReferenceEquals(managed, deep) Then deep.Dispose()
                Return managed
            Catch ex As Exception
                DiagnosticLogService.LogException("JpegTurbo.Color", ex)
                Return deep
            End Try
        End Function

        Private Const KindGray As Integer = 0
        Private Const KindRgb As Integer = 1
        Private Const KindRgba As Integer = 2
        Private Const KindCmyk As Integer = 3

        Private Shared Function RunDecompress(Of TBuffer)(fn As DecompressFn, api As TurboApi, handle As IntPtr,
                                                          source As IntPtr, size As UIntPtr, target As TBuffer(),
                                                          pixelFormat As Integer, path As String) As Boolean
            Dim pin = GCHandle.Alloc(target, GCHandleType.Pinned)
            Try
                If fn(handle, source, size, pin.AddrOfPinnedObject(), 0, pixelFormat) <> 0 Then
                    LogError(api, handle, path, "Bildpunkte")
                    Return False
                End If
                Return True
            Finally
                pin.Free()
            End Try
        End Function

        Private Shared Sub LogError(api As TurboApi, handle As IntPtr, path As String, stage As String)
            Dim text = ""
            Try
                text = If(Marshal.PtrToStringAnsi(api.GetErrorStr(handle)), "")
            Catch
            End Try
            DiagnosticLogService.LogAlways("JpegTurbo", $"{stage} nicht lesbar: {IO.Path.GetFileName(path)}: {text}")
        End Sub

        ''' <summary>Die Werte der Datei als Rgba16161616, auf 16 Bit gestreckt (Wert mal 65535
        ''' durch den Hoechstwert der Tiefe, gerundet) und dabei gedreht. Graustufen gehen auf alle
        ''' drei Kanaele, CMYK in Adobes umgekehrter Form wird nach RGB gerechnet (R = C' mal K'),
        ''' ohne Profil.</summary>
        Private Shared Function BuildDeepBitmap(samples As UShort(), width As Integer, height As Integer,
                                                channels As Integer, precision As Integer, kind As Integer,
                                                orientation As Integer) As SKBitmap
            Dim swap = orientation >= 5 AndAlso orientation <= 8
            Dim outWidth = If(swap, height, width)
            Dim outHeight = If(swap, width, height)
            Dim alphaType = If(kind = KindRgba, SKAlphaType.Unpremul, SKAlphaType.Opaque)
            Dim bitmap = ImageProcessor.NewDeepBitmap(outWidth, outHeight, alphaType)
            If bitmap Is Nothing Then Return Nothing
            Dim maxValue = (1 << precision) - 1
            ' Strecken als Tabelle: hoechstens 65536 Eintraege, und jede Stufe rechnet nur einmal.
            Dim scale(maxValue) As Short
            For v = 0 To maxValue
                scale(v) = ImageProcessor.ToDeepShort(CInt((CLng(v) * 65535L + (maxValue \ 2)) \ maxValue))
            Next
            Try
                Dim target = bitmap.GetPixels()
                Dim stride = bitmap.RowBytes
                Threading.Tasks.Parallel.For(0, outHeight,
                    Sub(y)
                        Dim row(outWidth * 4 - 1) As Short
                        For x = 0 To outWidth - 1
                            Dim sx As Integer
                            Dim sy As Integer
                            Select Case orientation
                                Case 2 : sx = width - 1 - x : sy = y
                                Case 3 : sx = width - 1 - x : sy = height - 1 - y
                                Case 4 : sx = x : sy = height - 1 - y
                                Case 5 : sx = y : sy = x
                                Case 6 : sx = y : sy = height - 1 - x
                                Case 7 : sx = width - 1 - y : sy = height - 1 - x
                                Case 8 : sx = width - 1 - y : sy = x
                                Case Else : sx = x : sy = y
                            End Select
                            Dim index = (sy * width + sx) * channels
                            Dim o = x * 4
                            Select Case kind
                                Case KindGray
                                    Dim g = scale(Math.Min(CInt(samples(index)), maxValue))
                                    row(o) = g : row(o + 1) = g : row(o + 2) = g : row(o + 3) = -1S
                                Case KindCmyk
                                    Dim k = Math.Min(CInt(samples(index + 3)), maxValue)
                                    For c = 0 To 2
                                        Dim v = Math.Min(CInt(samples(index + c)), maxValue)
                                        row(o + c) = scale(CInt((CLng(v) * k + (maxValue \ 2)) \ maxValue))
                                    Next
                                    row(o + 3) = -1S
                                Case Else
                                    row(o) = scale(Math.Min(CInt(samples(index)), maxValue))
                                    row(o + 1) = scale(Math.Min(CInt(samples(index + 1)), maxValue))
                                    row(o + 2) = scale(Math.Min(CInt(samples(index + 2)), maxValue))
                                    row(o + 3) = If(kind = KindRgba, scale(Math.Min(CInt(samples(index + 3)), maxValue)), -1S)
                            End Select
                        Next
                        Marshal.Copy(row, 0, New IntPtr(target.ToInt64() + CLng(y) * stride), outWidth * 4)
                    End Sub)
                Return bitmap
            Catch ex As Exception
                DiagnosticLogService.LogException("JpegTurbo.Build", ex)
                bitmap.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Das 8-Bit-Bild (Bgra8888, geradliniges Alpha, deckend bleibt Opaque) aus dem
        ''' tiefen, je Kanal gerundet: 65535 wird 255, 128 wird 0, 129 wird 1, dieselbe Rundung wie
        ''' bei TIFF und HEIF. Die Quelle bleibt beim Aufrufer.</summary>
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
