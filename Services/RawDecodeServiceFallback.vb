Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices

Namespace Services

    ' Die Bindung an eine LibRaw und der Rueckfall auf die LibRaw des Systems.
    '
    ' DIE BINDUNG. Alle Einstiege der C-API stehen in einem Objekt LibRawBinding, nicht mehr in
    ' einzelnen statischen Feldern. Die Namen, unter denen der Code sie ruft (_openFile, _unpack ...),
    ' sind Eigenschaften, die die Bindung des laufenden Fadens liefern: im Normalfall die der
    ' mitgelieferten Bibliothek (_primaryBinding), waehrend eines Rueckfalls die des Systems
    ' (_threadBinding, je Faden). So laeuft derselbe Code ohne Aenderung gegen beide.
    '
    ' DER RUECKFALL. Scheitert die mitgelieferte an einer Datei (Oeffnen, Entpacken, Entwickeln; die
    ' Vorschau und die Aufnahmedaten nur, wenn sie die Datei gar nicht oeffnet), wird die LibRaw des
    ' Systems einmal nachgeladen und dieselbe Datei damit versucht. Suchen, Laden, Merkliste je
    ' Datei und das gedeckelte Protokoll macht die gemeinsame Hilfsklasse SystemLibraryFallback
    ' (dieselbe wie fuer libheif und libjxl); hier steht nur, was LibRaw zusaetzlich braucht: das
    ' Binden der Einstiege (BindSystem), die Pruefung der Fassung und der Strukturen, und welcher
    ' leere Ausgang ein Scheitern ist. Den Rueckfall gibt es damit nur unter Linux ausserhalb des
    ' Flatpak, und nur, wenn die laufende die mitgelieferte ist.
    '
    ' Beide Bibliotheken liegen dann im Prozess nebeneinander; die des Systems in einem eigenen
    ' Namensraum des Laders (dlmopen), die mitgelieferte haelt alles aus ihren statischen Archiven
    ' verborgen (--exclude-libs,ALL), und jede wird nur ueber ihren eigenen Handle angesprochen.
    '
    ' Ausgeschlossen bleiben Fassungen vor 0.21 und jede Fassung, deren Strukturen die Laufzeitsuche
    ' nicht eindeutig findet: die Basis von libraw_output_params_t (FindParamsBase) beim Laden, und
    ' je Datei die festen Versaetze in libraw_image_sizes_t und libraw_iparams_t gegen die Getter
    ' derselben Fassung (SystemLayoutMatches).
    '
    ' DAS BILD KANN ANDERS AUSSEHEN. Pegel, Farbe und Grundbelichtung sind am mitgelieferten Snapshot
    ' gemessen, ebenso der Anker des RAW-Kerns. Der Rueckfall greift nur, wenn die mitgelieferte
    ' wirklich scheitert; ein Bild daraus ist besser als die eingebettete Vorschau, entspricht in
    ' seinen Kennzahlen aber nicht dem Anker.
    Partial Public NotInheritable Class RawDecodeService

        ''' <summary>Die Einstiege EINER geladenen LibRaw. Pflicht sind die bis Close; die uebrigen
        ''' bleiben Nothing, wenn die Fassung sie nicht hat.</summary>
        Private NotInheritable Class LibRawBinding
            Public Handle As IntPtr
            Public Name As String = ""
            Public Version As String = ""
            Public Reentrant As Boolean
            Public Init As InitFn
            Public OpenFile As OpenFileFn
            Public Unpack As IntFn
            Public SetOutputBps As SetIntFn
            Public SetOutputColor As SetIntFn
            Public GetCamMul As GetCamMulFn
            Public SetUserMul As SetUserMulFn
            Public SetNoAutoBright As SetIntFn
            Public SetGamma As SetGammaFn
            Public Process As IntFn
            Public UnpackThumb As IntFn
            Public MakeMemThumb As MakeMemImageFn
            Public MakeMemImage As MakeMemImageFn
            Public ClearMem As PtrFn
            Public Close As PtrFn
            Public OpenBuffer As OpenBufferFn
            Public GetPreMul As GetCamMulFn
            Public GetRgbCam As GetRgbCamFn
            Public GetIparams As GetStructFn
            Public GetImgOther As GetStructFn
            Public GetIwidth As IntFn
            Public GetIheight As IntFn
            Public GetRawWidth As IntFn
            Public GetRawHeight As IntFn
            Public GetColorMaximum As IntFn
            Public SetFbdd As SetIntFn
            Public SetDemosaic As SetIntFn
            Public SetHighlight As SetIntFn
            Public SetAdjustMaximumThr As SetFloatFn
            Public UnpackThumbEx As IndexedIntFn
            Public CameraList As CameraListFn
            Public CameraCount As CameraCountFn
            Public SetDataErrorHandler As SetDataErrorHandlerFn
            Public GetDecoderInfo As GetDecoderInfoFn
            Public SetProgressHandler As SetProgressHandlerFn
            Public StrError As StrErrorFn
        End Class

        Private Delegate Function StrErrorFn(code As Integer) As IntPtr
        Private Delegate Function VersionFn() As IntPtr
        Private Delegate Function VersionNumberFn() As Integer

        ''' <summary>Die mitgelieferte (oder, wo es keine gibt, die zuerst gefundene) Bibliothek.
        ''' Leer, solange EnsureLoaded nichts geladen hat.</summary>
        Private Shared _primaryBinding As New LibRawBinding()
        ''' <summary>Die Bindung eines laufenden Rueckfalls, nur auf dessen Faden.</summary>
        <ThreadStatic> Private Shared _threadBinding As LibRawBinding

        ''' <summary>Eine leere Bindung fuer den Pruefschalter <see cref="LibRawHiddenForTest"/>.</summary>
        Private Shared ReadOnly _hiddenBinding As New LibRawBinding()

        Private Shared ReadOnly Property ActiveBinding As LibRawBinding
            Get
                If LibRawHiddenForTest Then Return _hiddenBinding
                Return If(_threadBinding, _primaryBinding)
            End Get
        End Property

        Private Shared ReadOnly Property _reentrant As Boolean
            Get
                Return ActiveBinding.Reentrant
            End Get
        End Property
        Private Shared ReadOnly Property _init As InitFn
            Get
                Return ActiveBinding.Init
            End Get
        End Property
        Private Shared ReadOnly Property _openFile As OpenFileFn
            Get
                Return ActiveBinding.OpenFile
            End Get
        End Property
        ''' OPTIONAL: ohne ihn bleibt ein DNG mit JPEG-XL-Rohdaten bei der Vorschau (DngJxlRawService).
        Private Shared ReadOnly Property _openBuffer As OpenBufferFn
            Get
                Return ActiveBinding.OpenBuffer
            End Get
        End Property
        Private Shared ReadOnly Property _unpack As IntFn
            Get
                Return ActiveBinding.Unpack
            End Get
        End Property
        Private Shared ReadOnly Property _setOutputBps As SetIntFn
            Get
                Return ActiveBinding.SetOutputBps
            End Get
        End Property
        Private Shared ReadOnly Property _setOutputColor As SetIntFn
            Get
                Return ActiveBinding.SetOutputColor
            End Get
        End Property
        Private Shared ReadOnly Property _getCamMul As GetCamMulFn
            Get
                Return ActiveBinding.GetCamMul
            End Get
        End Property
        Private Shared ReadOnly Property _getPreMul As GetCamMulFn
            Get
                Return ActiveBinding.GetPreMul
            End Get
        End Property
        Private Shared ReadOnly Property _getRgbCam As GetRgbCamFn
            Get
                Return ActiveBinding.GetRgbCam
            End Get
        End Property
        Private Shared ReadOnly Property _getIparams As GetStructFn
            Get
                Return ActiveBinding.GetIparams
            End Get
        End Property
        Private Shared ReadOnly Property _getImgOther As GetStructFn
            Get
                Return ActiveBinding.GetImgOther
            End Get
        End Property
        Private Shared ReadOnly Property _getIwidth As IntFn
            Get
                Return ActiveBinding.GetIwidth
            End Get
        End Property
        Private Shared ReadOnly Property _getIheight As IntFn
            Get
                Return ActiveBinding.GetIheight
            End Get
        End Property
        Private Shared ReadOnly Property _getRawWidth As IntFn
            Get
                Return ActiveBinding.GetRawWidth
            End Get
        End Property
        Private Shared ReadOnly Property _getRawHeight As IntFn
            Get
                Return ActiveBinding.GetRawHeight
            End Get
        End Property
        ''' LibRaws Weisspunkt (color.maximum); nur fuer den gemessenen Schwarzpunkt, siehe
        ''' MeasureMaskedBlack. Optional wie die Getter darueber.
        Private Shared ReadOnly Property _getColorMaximum As IntFn
            Get
                Return ActiveBinding.GetColorMaximum
            End Get
        End Property
        Private Shared ReadOnly Property _setUserMul As SetUserMulFn
            Get
                Return ActiveBinding.SetUserMul
            End Get
        End Property
        Private Shared ReadOnly Property _setNoAutoBright As SetIntFn
            Get
                Return ActiveBinding.SetNoAutoBright
            End Get
        End Property
        Private Shared ReadOnly Property _setGamma As SetGammaFn
            Get
                Return ActiveBinding.SetGamma
            End Get
        End Property
        Private Shared ReadOnly Property _setFbdd As SetIntFn
            Get
                Return ActiveBinding.SetFbdd
            End Get
        End Property
        Private Shared ReadOnly Property _setDemosaic As SetIntFn
            Get
                Return ActiveBinding.SetDemosaic
            End Get
        End Property
        Private Shared ReadOnly Property _setHighlight As SetIntFn
            Get
                Return ActiveBinding.SetHighlight
            End Get
        End Property
        Private Shared ReadOnly Property _setAdjustMaximumThr As SetFloatFn
            Get
                Return ActiveBinding.SetAdjustMaximumThr
            End Get
        End Property
        Private Shared ReadOnly Property _process As IntFn
            Get
                Return ActiveBinding.Process
            End Get
        End Property
        Private Shared ReadOnly Property _unpackThumb As IntFn
            Get
                Return ActiveBinding.UnpackThumb
            End Get
        End Property
        Private Shared ReadOnly Property _unpackThumbEx As IndexedIntFn
            Get
                Return ActiveBinding.UnpackThumbEx
            End Get
        End Property
        Private Shared ReadOnly Property _makeMemThumb As MakeMemImageFn
            Get
                Return ActiveBinding.MakeMemThumb
            End Get
        End Property
        Private Shared ReadOnly Property _makeMemImage As MakeMemImageFn
            Get
                Return ActiveBinding.MakeMemImage
            End Get
        End Property
        Private Shared ReadOnly Property _clearMem As PtrFn
            Get
                Return ActiveBinding.ClearMem
            End Get
        End Property
        Private Shared ReadOnly Property _close As PtrFn
            Get
                Return ActiveBinding.Close
            End Get
        End Property
        Private Shared ReadOnly Property _cameraList As CameraListFn
            Get
                Return ActiveBinding.CameraList
            End Get
        End Property
        Private Shared ReadOnly Property _cameraCount As CameraCountFn
            Get
                Return ActiveBinding.CameraCount
            End Get
        End Property
        Private Shared ReadOnly Property _setDataErrorHandler As SetDataErrorHandlerFn
            Get
                Return ActiveBinding.SetDataErrorHandler
            End Get
        End Property
        Private Shared ReadOnly Property _getDecoderInfo As GetDecoderInfoFn
            Get
                Return ActiveBinding.GetDecoderInfo
            End Get
        End Property
        Private Shared ReadOnly Property _setProgressHandler As SetProgressHandlerFn
            Get
                Return ActiveBinding.SetProgressHandler
            End Get
        End Property

        ''' <summary>Bindet die Einstiege einer geladenen Bibliothek, oder Nothing, wenn ein
        ''' Pflichteinstieg fehlt (die Bibliothek ist dann unbrauchbar und bleibt dem Aufrufer zum
        ''' Freigeben). Die optionalen fehlen einzeln, ohne die Bibliothek abzulehnen.</summary>
        Private Shared Function BindExports(handle As IntPtr, name As String, reentrant As Boolean) As LibRawBinding
            Dim b As New LibRawBinding With {.Handle = handle, .Name = If(name, ""), .Reentrant = reentrant}
            Try
                b.Init = GetExport(Of InitFn)(handle, "libraw_init")
                b.OpenFile = GetExport(Of OpenFileFn)(handle, "libraw_open_file")
                b.Unpack = GetExport(Of IntFn)(handle, "libraw_unpack")
                b.SetOutputBps = GetExport(Of SetIntFn)(handle, "libraw_set_output_bps")
                b.SetOutputColor = GetExport(Of SetIntFn)(handle, "libraw_set_output_color")
                b.GetCamMul = GetExport(Of GetCamMulFn)(handle, "libraw_get_cam_mul")
                b.SetUserMul = GetExport(Of SetUserMulFn)(handle, "libraw_set_user_mul")
                b.SetNoAutoBright = GetExport(Of SetIntFn)(handle, "libraw_set_no_auto_bright")
                b.SetGamma = GetExport(Of SetGammaFn)(handle, "libraw_set_gamma")
                b.Process = GetExport(Of IntFn)(handle, "libraw_dcraw_process")
                b.UnpackThumb = GetExport(Of IntFn)(handle, "libraw_unpack_thumb")
                b.MakeMemThumb = GetExport(Of MakeMemImageFn)(handle, "libraw_dcraw_make_mem_thumb")
                b.MakeMemImage = GetExport(Of MakeMemImageFn)(handle, "libraw_dcraw_make_mem_image")
                b.ClearMem = GetExport(Of PtrFn)(handle, "libraw_dcraw_clear_mem")
                b.Close = GetExport(Of PtrFn)(handle, "libraw_close")
            Catch
                ' Ein fehlender Pflichteinstieg: die Bibliothek ist unbrauchbar.
                Return Nothing
            End Try
            ' OPTIONAL: eine aeltere libraw ohne diesen Export ist voll brauchbar, sie rauscht nur mehr.
            b.SetFbdd = TryExport(Of SetIntFn)(handle, "libraw_set_fbdd_noiserd")
            ' OPTIONAL: ohne sie wird ein Datenfehler beim Entpacken nicht erkannt (UnpackChecked).
            ' Beide zusammen oder keiner.
            b.SetDataErrorHandler = TryExport(Of SetDataErrorHandlerFn)(handle, "libraw_set_dataerror_handler")
            b.GetDecoderInfo = TryExport(Of GetDecoderInfoFn)(handle, "libraw_get_decoder_info")
            If b.SetDataErrorHandler Is Nothing OrElse b.GetDecoderInfo Is Nothing Then
                b.SetDataErrorHandler = Nothing
                b.GetDecoderInfo = Nothing
            ElseIf _dataErrorCallbackPointer = IntPtr.Zero Then
                _dataErrorCallbackPointer = Marshal.GetFunctionPointerForDelegate(_dataErrorCallback)
            End If
            ' OPTIONAL: ohne ihn nimmt die Kachel weiter die groesste Vorschau.
            b.UnpackThumbEx = TryExport(Of IndexedIntFn)(handle, "libraw_unpack_thumb_ex")
            b.OpenBuffer = TryExport(Of OpenBufferFn)(handle, "libraw_open_buffer")
            ' OPTIONAL: ohne ihn laeuft ein ueberholter Decode zu Ende (siehe RunWhileWanted).
            b.SetProgressHandler = TryExport(Of SetProgressHandlerFn)(handle, "libraw_set_progress_handler")
            If b.SetProgressHandler IsNot Nothing AndAlso _progressCallbackPointer = IntPtr.Zero Then
                _progressCallbackPointer = Marshal.GetFunctionPointerForDelegate(_progressCallback)
            End If
            b.SetDemosaic = TryExport(Of SetIntFn)(handle, "libraw_set_demosaic")
            b.SetHighlight = TryExport(Of SetIntFn)(handle, "libraw_set_highlight")
            b.SetAdjustMaximumThr = TryExport(Of SetFloatFn)(handle, "libraw_set_adjust_maximum_thr")
            ' OPTIONAL, beide zusammen oder keiner: sie tragen NUR den Aufnahme-Weissabgleich als
            ' Zahl (ReadCameraColorFacts); die Rechnung braucht pre_mul UND rgb_cam.
            b.GetPreMul = TryExport(Of GetCamMulFn)(handle, "libraw_get_pre_mul")
            b.GetRgbCam = TryExport(Of GetRgbCamFn)(handle, "libraw_get_rgb_cam")
            If b.GetPreMul Is Nothing OrElse b.GetRgbCam Is Nothing Then
                b.GetPreMul = Nothing
                b.GetRgbCam = Nothing
            End If
            ' OPTIONAL als Gruppe: die AUFNAHMEDATEN, die LibRaw beim Oeffnen selbst liest
            ' (ReadFileMetadata). Fehlt einer, fehlen alle.
            b.GetIparams = TryExport(Of GetStructFn)(handle, "libraw_get_iparams")
            b.GetImgOther = TryExport(Of GetStructFn)(handle, "libraw_get_imgother")
            b.GetIwidth = TryExport(Of IntFn)(handle, "libraw_get_iwidth")
            b.GetIheight = TryExport(Of IntFn)(handle, "libraw_get_iheight")
            b.GetRawWidth = TryExport(Of IntFn)(handle, "libraw_get_raw_width")
            b.GetRawHeight = TryExport(Of IntFn)(handle, "libraw_get_raw_height")
            b.GetColorMaximum = TryExport(Of IntFn)(handle, "libraw_get_color_maximum")
            If b.GetIparams Is Nothing OrElse b.GetImgOther Is Nothing OrElse b.GetIwidth Is Nothing OrElse
               b.GetIheight Is Nothing OrElse b.GetRawWidth Is Nothing OrElse b.GetRawHeight Is Nothing OrElse
               b.GetColorMaximum Is Nothing Then
                b.GetIparams = Nothing : b.GetImgOther = Nothing
                b.GetIwidth = Nothing : b.GetIheight = Nothing
                b.GetRawWidth = Nothing : b.GetRawHeight = Nothing : b.GetColorMaximum = Nothing
            End If
            ' OPTIONAL: die Modellliste beantwortet nur die Frage nach der Ursache eines kaputt
            ' aussehenden Bildes (CameraIsKnown).
            b.CameraList = TryExport(Of CameraListFn)(handle, "libraw_cameraList")
            b.CameraCount = TryExport(Of CameraCountFn)(handle, "libraw_cameraCount")
            If b.CameraList Is Nothing OrElse b.CameraCount Is Nothing Then
                b.CameraList = Nothing
                b.CameraCount = Nothing
            End If
            b.StrError = TryExport(Of StrErrorFn)(handle, "libraw_strerror")
            Dim version = TryExport(Of VersionFn)(handle, "libraw_version")
            If version IsNot Nothing Then
                Try
                    b.Version = If(Marshal.PtrToStringAnsi(version()), "")
                Catch
                    b.Version = ""
                End Try
            End If
            Return b
        End Function

        Private Shared Function TryExport(Of T As Class)(handle As IntPtr, name As String) As T
            Dim address As IntPtr
            If Not NativeLibrary.TryGetExport(handle, name, address) Then Return Nothing
            Return Marshal.GetDelegateForFunctionPointer(Of T)(address)
        End Function


        ' ── Die LibRaw des Systems ─────────────────────────────────────────────────────────────

        ''' <summary>Der Rueckfall auf die LibRaw des Systems, ueber die gemeinsame Hilfsklasse mit
        ''' libheif und libjxl. Die Seite Technik liest daraus Stand und Fassung; der Pruefstand
        ''' setzt dort <see cref="SystemLibraryFallback.SimulatedBundledFailurePath"/>.
        '''
        ''' Gesucht werden die fadensicheren Dateien zuerst, wie beim ersten Laden. Den Soname 20
        ''' (0.20, Ubuntu 22.04) fuehrt die Liste nicht; ein nacktes libraw_r.so kann trotzdem eine
        ''' alte Fassung sein, deshalb prueft BindSystem die Fassung selbst.
        '''
        ''' Das Lesen laedt zuerst die mitgelieferte (EnsureLoaded): erst danach weiss die
        ''' Hilfsklasse, welche Datei laeuft, und die Seite Technik zeigt den richtigen Stand, auch
        ''' wenn noch keine RAW geoeffnet wurde.</summary>
        Friend Shared ReadOnly Property SystemFallback As SystemLibraryFallback
            Get
                EnsureLoaded()
                Return _systemFallback
            End Get
        End Property

        Private Shared ReadOnly _systemFallback As New SystemLibraryFallback("LibRaw",
            {"libraw_r.so.27", "libraw_r.so.26", "libraw_r.so.25", "libraw_r.so.24", "libraw_r.so.23", "libraw_r.so.22",
             "libraw.so.27", "libraw.so.26", "libraw.so.25", "libraw.so.24", "libraw.so.23", "libraw.so.22",
             "libraw_r.so", "libraw.so"})

        Private Shared _systemBinding As LibRawBinding

        ''' <summary>Aelteste Fassung, die als Rueckfall genommen wird: 0.21 (libraw_versionNumber,
        ''' Haupt mal 65536 plus Neben mal 256 plus Fehlerstand). 0.20 faellt darunter.</summary>
        Private Const MinimumSystemVersionNumber As Integer = &H1500

        ''' <summary>Traegt nach dem ersten Laden Pfad und Fassung der laufenden Bibliothek in die
        ''' Hilfsklasse ein; erst damit weiss sie, ob die laufende die mitgelieferte ist.</summary>
        Private Shared Sub RegisterPrimaryForFallback()
            _systemFallback.PrimaryPath = PrimaryLibraryPath()
            _systemFallback.PrimaryVersion = If(_primaryBinding.Version, "")
        End Sub

        ''' <summary>Bindet die nachgeladene System-LibRaw (Aufruf aus
        ''' <see cref="SystemLibraryFallback.EnsureLoaded"/>). Nothing samt Grund, wenn sie nicht
        ''' taugt: ein Pflichteinstieg fehlt, sie ist aelter als 0.21, oder die Basis ihrer
        ''' Ausgabeparameter liegt nicht eindeutig (die Wege mit festem Versatz schrieben sonst ins
        ''' Leere). Die festen Versaetze in libraw_image_sizes_t und libraw_iparams_t prueft je Datei
        ''' <see cref="SystemLayoutMatches"/>.</summary>
        Private Shared Function BindSystem(handle As IntPtr, path As String, reason As Text.StringBuilder) As String
            Dim fileName = IO.Path.GetFileName(path)
            Dim reentrant = fileName.Contains("_r.")
            Dim binding = BindExports(handle, path, reentrant)
            If binding Is Nothing Then
                reason.Append("ein Pflichteinstieg fehlt")
                Return Nothing
            End If
            Dim numberFn = TryExport(Of VersionNumberFn)(handle, "libraw_versionNumber")
            Dim versionNumber = If(numberFn Is Nothing, 0, numberFn())
            If versionNumber < MinimumSystemVersionNumber Then
                reason.Append($"Fassung {binding.Version} ist aelter als 0.21")
                Return Nothing
            End If
            Dim previous = _threadBinding
            _threadBinding = binding
            Try
                Dim probe = binding.Init(0UI)
                If probe = IntPtr.Zero Then
                    reason.Append("libraw_init lieferte nichts")
                    Return Nothing
                End If
                Try
                    If FindParamsBase(probe) < 0 Then
                        reason.Append($"Fassung {binding.Version}: die Ausgabeparameter liegen nicht eindeutig")
                        Return Nothing
                    End If
                Finally
                    binding.Close(probe)
                End Try
            Finally
                _threadBinding = previous
            End Try
            _systemBinding = binding
            Return If(binding.Version.Length > 0, binding.Version, SystemLibraryFallback.VersionFromFileName(path))
        End Function

        ''' <summary>Der volle Pfad der geladenen Bibliothek: steht im Namen nur der Dateiname (die
        ''' mitgelieferte, siehe EnsureLoaded), einer der Orte unter dem Verzeichnis der Anwendung.</summary>
        Private Shared Function PrimaryLibraryPath() As String
            Dim name = If(_loadedLibrary, "")
            If name.Length = 0 OrElse Path.IsPathRooted(name) Then Return name
            For Each candidate In {Path.Combine(AppContext.BaseDirectory, name),
                                   Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeIdentifierForBundle(), "native", name)}
                If File.Exists(candidate) Then Return candidate
            Next
            Return name
        End Function

        Private Shared Function RuntimeIdentifierForBundle() As String
            Dim arch = If(RuntimeInformation.ProcessArchitecture = Architecture.Arm64, "arm64", "x64")
            If OperatingSystem.IsWindows() Then Return $"win-{arch}"
            If OperatingSystem.IsMacOS() Then Return $"osx-{arch}"
            Return $"linux-{arch}"
        End Function

        ''' <summary>Prueft fuer EINE Datei, dass die festen Versaetze in libraw_image_sizes_t und
        ''' libraw_iparams_t in der Fassung der Bindung dort liegen, wo der Code sie liest: Masse
        ''' gegen die Getter derselben Fassung, Marke als druckbarer Text, Kanalzahl 1 bis 4. Laeuft
        ''' mit der Bindung des Fadens.</summary>
        Private Shared Function SystemLayoutMatches(path As String) As Boolean
            If _getIparams Is Nothing OrElse _getRawWidth Is Nothing OrElse _getIwidth Is Nothing Then Return False
            Dim handle = _init(0UI)
            If handle = IntPtr.Zero Then Return False
            Dim pathPtr As IntPtr = IntPtr.Zero
            Try
                pathPtr = StringToUtf8(path)
                If _openFile(handle, pathPtr) <> 0 Then Return False
                Dim rawWidth = _getRawWidth(handle), rawHeight = _getRawHeight(handle)
                If rawWidth <= 0 OrElse rawHeight <= 0 Then Return False
                If (Marshal.ReadInt16(handle, SizesRawWidthOffset) And &HFFFF) <> rawWidth OrElse
                   (Marshal.ReadInt16(handle, SizesRawHeightOffset) And &HFFFF) <> rawHeight Then Return False
                Dim iparams = _getIparams(handle)
                If iparams = IntPtr.Zero Then Return False
                If Not IsPlausibleText(FixedText(iparams, IparamsMakeOffset)) Then Return False
                Dim colors = Marshal.ReadInt32(iparams, IparamsColorsOffset)
                Return colors >= 1 AndAlso colors <= 4
            Catch
                Return False
            Finally
                _close.Invoke(handle)
                If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
            End Try
        End Function

        ''' <summary>Was die mitgelieferte zu einer Datei sagt, fuer das Protokoll: der Fehler von
        ''' open_file, oder dass sie oeffnet und erst danach scheitert. Kind ist die Fehlerart, nach
        ''' der das Protokoll deckelt.</summary>
        Private Shared Function PrimaryFailure(path As String) As (Opens As Boolean, Kind As String, Message As String)
            Dim b = _primaryBinding
            If b.Init Is Nothing Then Return (False, "keine Bibliothek", "keine Bibliothek")
            Dim handle = b.Init(0UI)
            If handle = IntPtr.Zero Then Return (False, "init", "libraw_init lieferte nichts")
            Dim pathPtr As IntPtr = IntPtr.Zero
            Try
                pathPtr = StringToUtf8(path)
                Dim rc = b.OpenFile(handle, pathPtr)
                If rc = 0 Then Return (True, "Entwicklung", "oeffnet, Entpacken oder Entwickeln scheitert")
                Dim text = If(b.StrError IsNot Nothing, Marshal.PtrToStringAnsi(b.StrError(rc)), Nothing)
                Return (False, $"open_file {rc}", $"open_file {rc}" & If(String.IsNullOrEmpty(text), "", $" ({text})"))
            Catch ex As Exception
                Return (False, ex.GetType().Name, ex.Message)
            Finally
                b.Close(handle)
                If pathPtr <> IntPtr.Zero Then Marshal.FreeCoTaskMem(pathPtr)
            End Try
        End Function

        ' ── Der Rueckfall je Datei ─────────────────────────────────────────────────────────────

        ''' <summary>Die Wege, fuer die getrennt gemerkt wird, wer gelesen hat: eine Datei kann in
        ''' der Vorschau aufgehen und erst beim Entwickeln scheitern.</summary>
        Private Enum FallbackStage
            Develop
            Thumbnail
            Metadata
        End Enum

        ''' <summary>Fuehrt <paramref name="work"/> mit der mitgelieferten Bibliothek aus und, wenn
        ''' sie an der Datei scheitert, ein zweites Mal mit der des Systems
        ''' (<see cref="SystemLibraryFallback.Run"/>: Merkliste, Protokoll, einmaliges Nachladen).
        '''
        ''' WANN ES ALS SCHEITERN GILT. Beim Entwickeln jedes leere Ergebnis, das kein Abbruch ist.
        ''' Bei Vorschau und Aufnahmedaten nur, wenn die mitgelieferte die Datei gar nicht oeffnet:
        ''' Nothing heisst dort auch "die Datei traegt keine", und dafuer die System-LibRaw zu fragen,
        ''' kostete bei jeder solchen Datei einen zweiten Lauf.
        '''
        ''' Beide Versuche laufen nacheinander auf dem Faden des Aufrufers, innerhalb der Schleuse,
        ''' die er schon haelt; die Versuche mit der System-LibRaw zusaetzlich unter
        ''' <see cref="_fallbackLock"/>, auch fuer eine Fassung ohne Faeden.</summary>
        Private Shared Function WithSystemFallback(Of T As Class)(path As String, stage As FallbackStage, work As Func(Of T)) As T
            If _threadBinding IsNot Nothing Then Return work()
            ' Lineares Licht liest nicht LibRaw; eine LibRaw des Systems kennt das Format ebenso wenig.
            If IsLinearLightSource(path) Then Return work()
            Return SystemFallback.Run(Of T)(path, stage.ToString(),
                Function(attempt)
                    Dim result = work()
                    If result IsNot Nothing OrElse IsAbandoned() Then Return result
                    Dim failure = PrimaryFailure(path)
                    If stage = FallbackStage.Develop OrElse Not failure.Opens Then attempt.Fail(failure.Kind, failure.Message)
                    Return result
                End Function,
                AddressOf BindSystem,
                Function(attempt)
                    Dim result = RunWithSystem(path, work)
                    If result Is Nothing Then attempt.Fail("System", "die System-LibRaw liest die Datei ebenfalls nicht")
                    Return result
                End Function)
        End Function

        Private Shared ReadOnly _fallbackLock As New Object()

        Private Shared Function RunWithSystem(Of T As Class)(path As String, work As Func(Of T)) As T
            Dim system = _systemBinding
            If system Is Nothing Then Return Nothing
            SyncLock _fallbackLock
                _threadBinding = system
                Try
                    If Not SystemLayoutMatches(path) Then Return Nothing
                    Return work()
                Finally
                    _threadBinding = Nothing
                End Try
            End SyncLock
        End Function

    End Class

End Namespace
