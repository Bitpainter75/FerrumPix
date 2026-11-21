Imports System
Imports System.Runtime.InteropServices

Namespace Services

    ' Die Bindung an die geladene LibRaw.
    '
    ' Alle Einstiege der C-API stehen in einem Objekt LibRawBinding, nicht in einzelnen statischen
    ' Feldern. Die Namen, unter denen der Code sie ruft (_openFile, _unpack ...), sind
    ' Eigenschaften, die die Bindung der geladenen Bibliothek liefern (_primaryBinding). Der
    ' Pruefschalter LibRawHiddenForTest tauscht sie gegen eine leere Bindung: jeder Einstieg ist dann
    ' Nothing, und ein Weg, der doch in LibRaw ruft, wirft, statt still zu gelingen.
    Partial Public NotInheritable Class RawDecodeService

        ''' <summary>Die Einstiege der geladenen LibRaw. Pflicht sind die bis Close; die uebrigen
        ''' bleiben Nothing, wenn die Fassung sie nicht hat.</summary>
        Private NotInheritable Class LibRawBinding
            Public Handle As IntPtr
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
        End Class

        ''' <summary>Die mitgelieferte (oder, wo es keine gibt, die zuerst gefundene) Bibliothek.
        ''' Leer, solange EnsureLoaded nichts geladen hat.</summary>
        Private Shared _primaryBinding As New LibRawBinding()

        ''' <summary>Eine leere Bindung fuer den Pruefschalter <see cref="LibRawHiddenForTest"/>.</summary>
        Private Shared ReadOnly _hiddenBinding As New LibRawBinding()

        Private Shared ReadOnly Property ActiveBinding As LibRawBinding
            Get
                If LibRawHiddenForTest Then Return _hiddenBinding
                Return _primaryBinding
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
        Private Shared Function BindExports(handle As IntPtr, reentrant As Boolean) As LibRawBinding
            Dim b As New LibRawBinding With {.Handle = handle, .Reentrant = reentrant}
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
            Return b
        End Function

        Private Shared Function TryExport(Of T As Class)(handle As IntPtr, name As String) As T
            Dim address As IntPtr
            If Not NativeLibrary.TryGetExport(handle, name, address) Then Return Nothing
            Return Marshal.GetDelegateForFunctionPointer(Of T)(address)
        End Function

    End Class

End Namespace
