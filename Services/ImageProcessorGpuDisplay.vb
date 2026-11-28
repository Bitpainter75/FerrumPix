Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports SkiaSharp

Namespace Services

    ' DIE ANZEIGE IM REGLERZUG AUF DER KARTE DES FENSTERS.
    '
    ' Waehrend eines Zugs an einem Regler der Punktkette (Licht, Farbe, Kurve, Farbmischer ...)
    ' zeichnet das Fenster das Bild selbst: Ausgangsbild ist der Stand nach Geometrie und
    ' Farbrauschen, einmal je Zug auf die Karte gelegt. Je Schritt entstehen auf dem Prozessor nur
    ' Kette und Tabellen (DisplayFrame); Punktkette und raeumliche Stufen rechnet der Renderfaden
    ' mit dem Kontext des Fensters, ohne Zuruecklesen, ohne Szenenkopie, ohne Blit und ohne das
    ' Hochladen der Anzeige durch Avalonia.
    '
    ' DIE SZENE BLEIBT DIE WAHRHEIT. Nichts hier schreibt in die Szene des Editors, in den
    ' Zwischenspeicher der Basis, in eine Datei oder in den Export. Nach dem Loslassen rechnet die
    ' Vorschau wie bisher, und erst wenn ihr Bild steht, zeigt das Fenster wieder die Szene.
    '
    ' WAS DER WEG KANN, steht in DisplayFrameEligibility und BuildDisplayStages: die ganze
    ' Punktkette und die raeumlichen Stufen, deren Shader nichts zuruecklesen. Alles andere
    ' (Klarheit, Dunst, Korn, Maskenebenen, Objekte, Auswahl-Skopus, Lichtquellen) laesst den
    ' Zug auf dem bisherigen Weg. Jedes Feld des Rezepts ist in DisplayFrameRecipeFields
    ' eingeordnet; ein neues Feld ohne Einordnung meldet der Pruefstand.
    Partial Public Class ImageProcessor

        ''' <summary>Der Stand nach Geometrie und Farbrauschen fuer die Kartenanzeige, mit seinem
        ''' Namen. Gehoert dem Editor; freigegeben wird er ueber <see cref="RetireDisplayObject"/>,
        ''' weil der Renderfaden ihn gerade lesen kann.</summary>
        Friend NotInheritable Class DisplayBase
            Implements IDisposable

            Public Property Bitmap As SKBitmap
            ''' <summary>Quelle, Groesse, Stempel und Rezept bis zum Farbrauschen, wie der Name der
            ''' gemerkten Textur der Punktkette.</summary>
            Public Property Key As String = ""
            ''' <summary>Nur der Rezeptteil (ComputeColorNoiseCacheKey).</summary>
            Public Property RecipeKey As String = ""
            Public Property SourceIdentityValue As Long
            Public Property SourceStamp As Long
            ''' <summary>Massstab der Ausgabe fuer die Schaerfe (DisplayOutputScale der Quelle).</summary>
            Public Property SourceWidth As Integer
            Public Property SourceHeight As Integer

            Private _toneCellMeans As LocalToneCellMeans
            Private ReadOnly _toneCellMeansLock As New Object()

            ''' <summary>Die Farbmittel je Rasterfeld der oertlichen Basis, einmal je Ausgangsbild
            ''' gerechnet (siehe <see cref="LocalToneCellMeans"/>).</summary>
            Public Function ToneCellMeans() As LocalToneCellMeans
                SyncLock _toneCellMeansLock
                    If _toneCellMeans Is Nothing AndAlso Bitmap IsNot Nothing Then _toneCellMeans = BuildLocalToneCellMeans(Bitmap)
                    Return _toneCellMeans
                End SyncLock
            End Function

            Public Sub Dispose() Implements IDisposable.Dispose
                Bitmap?.Dispose()
                Bitmap = Nothing
            End Sub
        End Class

        ''' <summary>DAS TEURE AN DER OERTLICHEN BASIS, VOR DER KETTE: je Rasterfeld das Farbmittel des
        ''' Bilds samt Deckung, wie in BuildLocalToneGrid. Es haengt nur an den Bildpunkten des
        ''' Ausgangsbilds (Quelle, Geometrie, Farbrauschen), nicht an einem Regler der Kette; erst
        ''' danach schickt BuildLocalToneGrid jedes Feld durch die Stufen vor den Tonzonen
        ''' (Belichtung, Weissabgleich, RAW-Stufe ...). Im Zug bleibt das Ausgangsbild dasselbe, also
        ''' auch diese Mittel: gemessen kostete das Mitteln im Fenster 24,7 ms je Rahmen. Der Schluessel
        ''' ist damit das Ausgangsbild selbst; ein neues (andere Quelle, Geometrie, Farbrauschen)
        ''' rechnet sie neu. Das Raster daraus ist Wert fuer Wert dasselbe wie aus
        ''' BuildLocalToneGridFor (Pruefung "Kartenanzeige: oertliche Basis aus dem Zwischenstand").</summary>
        Friend NotInheritable Class LocalToneCellMeans
            Public R As Single()
            Public G As Single()
            Public B As Single()
            Public HasValue As Boolean()
            Public Coverage As Single()
            Public Width As Integer
            Public Height As Integer
            Public StepSize As Integer
            Public OriginX As Integer
            Public OriginY As Integer
            Public VisibleEdge As Integer
        End Class

        ''' <summary>Wie oft Farbmittel gerechnet wurden; fuer den Pruefstand.</summary>
        Friend Shared DisplayToneMeansBuilt As Integer

        ''' <summary>Das Mitteln aus BuildLocalToneGrid, ohne die Stufen der Kette. Muss Schritt fuer
        ''' Schritt so rechnen wie dort, damit das Raster dasselbe bleibt. Nothing ohne 8-Bit-Format.</summary>
        Friend Shared Function BuildLocalToneCellMeans(source As SKBitmap) As LocalToneCellMeans
            Dim srcBuf As Byte() = Nothing
            Dim length, stride, ri, gi, bi, ai As Integer
            If Not TryRentRgbaLikeBuffer(source, srcBuf, length, stride, ri, gi, bi, ai) Then Return Nothing
            Interlocked.Increment(DisplayToneMeansBuilt)
            Try
                Dim width = source.Width, height = source.Height
                Dim srcPremul = source.AlphaType = SKAlphaType.Premul
                Dim bounds = VisibleBounds(srcBuf, stride, width, height, ai)
                If bounds.Width <= 0 OrElse bounds.Height <= 0 Then bounds = New SKRectI(0, 0, width, height)
                Dim originX = bounds.Left, originY = bounds.Top
                Dim boundsRight = bounds.Right, boundsBottom = bounds.Bottom
                Dim visibleEdge = Math.Max(bounds.Width, bounds.Height)
                Dim stepSize = Math.Max(1, CInt(Math.Ceiling(visibleEdge / CDbl(LocalToneGridEdge))))
                Dim gw = (bounds.Width + stepSize - 1) \ stepSize
                Dim gh = (bounds.Height + stepSize - 1) \ stepSize
                Dim means As New LocalToneCellMeans With {
                    .R = New Single(gw * gh - 1) {}, .G = New Single(gw * gh - 1) {}, .B = New Single(gw * gh - 1) {},
                    .HasValue = New Boolean(gw * gh - 1) {}, .Coverage = New Single(gw * gh - 1) {},
                    .Width = gw, .Height = gh, .StepSize = stepSize, .OriginX = originX, .OriginY = originY,
                    .VisibleEdge = visibleEdge}
                Dim buffer = srcBuf
                PerformanceTraceService.Measure("Kartenanzeige: oertliche Basis, Raster mitteln", Sub()
                ForEachRow(gw, gh,
                    Sub(gy)
                        Dim yStart = originY + gy * stepSize
                        Dim yEnd = Math.Min(boundsBottom, yStart + stepSize)
                        For gx = 0 To gw - 1
                            Dim xStart = originX + gx * stepSize
                            Dim xEnd = Math.Min(boundsRight, xStart + stepSize)
                            Dim sumR = 0.0F, sumG = 0.0F, sumB = 0.0F
                            Dim totalAlpha = 0.0F
                            For y = yStart To yEnd - 1
                                Dim row = y * stride
                                For x = xStart To xEnd - 1
                                    Dim o = row + x * 4
                                    Dim a = buffer(o + ai)
                                    If a = 0 Then Continue For
                                    Dim weight = a / 255.0F
                                    If a = 255 OrElse Not srcPremul Then
                                        sumR += buffer(o + ri) / 255.0F * weight
                                        sumG += buffer(o + gi) / 255.0F * weight
                                        sumB += buffer(o + bi) / 255.0F * weight
                                    Else
                                        Dim inv = 1.0F / a
                                        sumR += Math.Min(1.0F, buffer(o + ri) * inv) * weight
                                        sumG += Math.Min(1.0F, buffer(o + gi) * inv) * weight
                                        sumB += Math.Min(1.0F, buffer(o + bi) * inv) * weight
                                    End If
                                    totalAlpha += weight
                                Next
                            Next
                            Dim i = gy * gw + gx
                            If totalAlpha > 0 Then
                                means.R(i) = sumR / totalAlpha
                                means.G(i) = sumG / totalAlpha
                                means.B(i) = sumB / totalAlpha
                                means.HasValue(i) = True
                            End If
                            means.Coverage(i) = totalAlpha / ((xEnd - xStart) * (yEnd - yStart))
                        Next
                    End Sub)
                End Sub)
                Return means
            Finally
                ReturnPooledBuffer(srcBuf)
            End Try
        End Function

        ''' <summary>Das Raster der oertlichen Basis aus den gemerkten Farbmitteln: je Feld die Stufen
        ''' vor den Tonzonen und der Grauwert, Radien wie in BuildLocalToneGrid.</summary>
        Friend Shared Function LocalToneGridFromMeans(means As LocalToneCellMeans, chain As PointOpChain,
                                                      referenceEdge As Double) As LocalToneGrid
            If means Is Nothing Then Return Nothing
            EnsureGammaTables()
            Dim toLinear = _srgbToLinearTable
            Dim toGamma = _linearToSrgbTable
            Dim gw = means.Width, gh = means.Height
            Dim gray = New Single(gw * gh - 1) {}
            PerformanceTraceService.Measure("Kartenanzeige: oertliche Basis aus dem Zwischenstand", Sub()
            ForEachRow(gw, gh,
                Sub(gy)
                    For gx = 0 To gw - 1
                        Dim i = gy * gw + gx
                        If Not means.HasValue(i) Then Continue For
                        Dim rr = means.R(i), gg = means.G(i), bb = means.B(i)
                        ApplyStagesBeforeToneZones(rr, gg, bb, chain, toLinear, toGamma)
                        gray(i) = LocalToneGray(rr, gg, bb, toLinear, toGamma)
                    Next
                End Sub)
            End Sub)
            Dim stepSize = means.StepSize
            Dim edge = If(referenceEdge > 0.0, referenceEdge, CDbl(means.VisibleEdge))
            Dim radius = LocalToneRadius * edge / stepSize
            Dim sigmaPixels = LocalToneDetailSigma * edge
            Dim gridVariance = Math.Max(0.0, (sigmaPixels * sigmaPixels - (CDbl(stepSize) * stepSize - 1.0) / 12.0) /
                                             (CDbl(stepSize) * stepSize))
            Dim smoothRadius = FractionalBoxRadiusForVariance(gridVariance / 3.0)
            Return New LocalToneGrid With {
                .Gray = gray, .Coverage = CType(means.Coverage.Clone(), Single()), .Width = gw, .Height = gh, .StepSize = stepSize,
                .OriginX = means.OriginX, .OriginY = means.OriginY, .Radius = radius, .SmoothRadius = smoothRadius}
        End Function

        ''' <summary>Ein Bild der Kartenanzeige, unveraenderlich nach dem Bau: die Kette, ihre
        ''' Tabellen als Bilder im Speicher und die raeumlichen Stufen als Fabriken. Gerechnet wird
        ''' es erst auf dem Renderfaden (<see cref="RenderDisplayFrame"/>).</summary>
        Friend NotInheritable Class DisplayFrame
            Implements IDisposable

            Public Property Base As DisplayBase
            ''' <summary>Nothing, wenn die Kette nichts tut; dann laeuft das Ausgangsbild unveraendert
            ''' in die Stufen, wie auf dem Prozessor.</summary>
            Public Property Chain As PointOpChain
            Public Property Table As SKImage
            Public Property Dither As SKImage
            Public Property CubeA As SKImage
            Public Property CubeB As SKImage
            Public Property Guide As SKImage
            Public Property LocalBaseImage As SKImage
            Public Property LocalBase As LocalToneBase
            ''' <summary>Raster der oertlichen Basis, das erst auf der Karte geglaettet wird.</summary>
            Public Property GpuGrid As LocalToneGrid
            Public Property Stages As New List(Of (Name As String, Gpu As Func(Of GRContext, SKImage, SKSurface)))()
            Public Property Width As Integer
            Public Property Height As Integer
            Public Property Premul As Boolean
            Public Property Sequence As Long

            Public Sub Dispose() Implements IDisposable.Dispose
                Table?.Dispose() : Table = Nothing
                Dither?.Dispose() : Dither = Nothing
                CubeA?.Dispose() : CubeA = Nothing
                CubeB?.Dispose() : CubeB = Nothing
                Guide?.Dispose() : Guide = Nothing
                LocalBaseImage?.Dispose() : LocalBaseImage = Nothing
            End Sub
        End Class

        ''' <summary>Was je Kontext auf der Karte liegt: die Textur des Ausgangsbilds und die
        ''' Flaechen des letzten Bilds. Nur auf dem Renderfaden angefasst. Freigegeben wird erst im
        ''' naechsten Render, solange derselbe Kontext gilt; Objekte eines verlorenen Kontexts werden
        ''' nur vergessen (Falle: Freigabe ohne gueltigen Kontext stuerzt in libGLdispatch ab).
        ''' Die statischen Ablagen des Bilddiensts (gemerkte Texturen, Rauschfelder) gelten nur fuer
        ''' dessen eigenen Kontext und werden hier nie benutzt.</summary>
        Friend NotInheritable Class DisplayResidency
            Private _context As GRContext
            Private _contextKnown As Boolean
            Private _baseKey As String
            Private _baseTexture As SKImage
            Private ReadOnly _retired As New List(Of IDisposable)()
            ''' <summary>Das zuletzt gerechnete Bild und der Rahmen dazu: Avalonia zeichnet dieselbe
            ''' Operation mehrmals (Ueberdeckung, Fensterwechsel), gerechnet wird sie nur einmal.</summary>
            Private _lastFrame As DisplayFrame
            Private _lastImage As SKImage
            Public Property Uploads As Integer

            ''' <summary>Vor jedem Render: ein anderer Kontext laesst alles Alte fallen, ohne es
            ''' freizugeben; sonst geht frei, was der vorige Render liegen liess.</summary>
            Public Sub BeginRender(gr As GRContext)
                If Not _contextKnown OrElse Not Object.ReferenceEquals(_context, gr) Then
                    _context = gr
                    _contextKnown = True
                    _baseKey = Nothing
                    _baseTexture = Nothing
                    _retired.Clear()
                    _lastFrame = Nothing
                    _lastImage = Nothing
                    Return
                End If
                ReleaseRetired()
            End Sub

            Private Sub ReleaseRetired()
                For Each item In _retired
                    Try
                        item.Dispose()
                    Catch ex As Exception
                        DiagnosticLogService.LogException("GpuDragDisplay", ex)
                    End Try
                Next
                _retired.Clear()
            End Sub

            Public Sub Retire(item As IDisposable)
                If item IsNot Nothing Then _retired.Add(item)
            End Sub

            ''' <summary>Das gemerkte Bild, wenn genau dieser Rahmen zuletzt gerechnet wurde.</summary>
            Public Function CachedImage(frame As DisplayFrame) As SKImage
                If _lastImage IsNot Nothing AndAlso Object.ReferenceEquals(_lastFrame, frame) Then Return _lastImage
                Return Nothing
            End Function

            ''' <summary>Uebernimmt das Ergebnis eines Rahmens. Das vorige geht erst im naechsten
            ''' Render frei; die Textur des Ausgangsbilds (Rahmen ohne Kette und Stufen) bleibt der
            ''' Ablage und wird hier nie freigegeben.</summary>
            Public Sub RememberImage(frame As DisplayFrame, image As SKImage)
                If _lastImage IsNot Nothing AndAlso Not Object.ReferenceEquals(_lastImage, image) AndAlso
                   Not Object.ReferenceEquals(_lastImage, _baseTexture) Then _retired.Add(_lastImage)
                _lastFrame = frame
                _lastImage = image
            End Sub

            ''' <summary>Das Ausgangsbild auf der Karte; hochgeladen nur, wenn sein Name wechselt.
            ''' Ohne Kontext (Pruefmodus) ein Bild im Speicher.</summary>
            Public Function BaseImageFor(gr As GRContext, base As DisplayBase) As SKImage
                If _baseTexture IsNot Nothing AndAlso String.Equals(_baseKey, base.Key, StringComparison.Ordinal) Then Return _baseTexture
                ' Die alte Textur geht frei; war sie zugleich das gemerkte Ergebnis, gilt das nicht mehr.
                If _baseTexture IsNot Nothing Then
                    If Object.ReferenceEquals(_lastImage, _baseTexture) Then
                        _lastImage = Nothing
                        _lastFrame = Nothing
                    End If
                    _retired.Add(_baseTexture)
                End If
                _baseTexture = Nothing
                Dim bitmap = base.Bitmap
                Dim made As SKImage
                If gr Is Nothing Then
                    made = SKImage.FromPixelCopy(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes)
                Else
                    made = PerformanceTraceService.Measure("Kartenanzeige: Basis hochladen",
                        Function()
                            Using raster = SKImage.FromPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes)
                                Return raster.ToTextureImage(gr)
                            End Using
                        End Function)
                End If
                If made Is Nothing Then Throw New InvalidOperationException("Ausgangsbild der Kartenanzeige nicht anlegbar.")
                _baseTexture = made
                _baseKey = base.Key
                Uploads += 1
                Return made
            End Function

            ''' <summary>Gibt alles frei; nur auf dem Renderfaden mit gueltigem Kontext.</summary>
            Public Sub ReleaseAll()
                If _baseTexture IsNot Nothing Then _retired.Add(_baseTexture)
                If _lastImage IsNot Nothing AndAlso Not Object.ReferenceEquals(_lastImage, _baseTexture) Then _retired.Add(_lastImage)
                _baseTexture = Nothing
                _baseKey = Nothing
                _lastImage = Nothing
                _lastFrame = Nothing
                ReleaseRetired()
            End Sub
        End Class

        ''' <summary>Schloss um jedes Rechnen eines Rahmens auf dem Renderfaden. Wer einen Rahmen
        ''' oder ein Ausgangsbild freigibt, nimmt es auch (<see cref="RetireDisplayObject"/>): der
        ''' Renderfaden koennte gerade daraus lesen.</summary>
        Friend Shared ReadOnly DisplayRenderLock As New Object()

        ''' <summary>Gibt einen Rahmen oder ein Ausgangsbild frei, sobald kein Render mehr daran
        ''' rechnet. Beides haelt nur Speicher des Prozessors, keine Textur.</summary>
        Friend Shared Sub RetireDisplayObject(item As IDisposable)
            If item Is Nothing Then Return
            SyncLock DisplayRenderLock
                item.Dispose()
            End SyncLock
        End Sub

        Private Shared _displayFrameSequence As Long

        ' ── Einordnung der Rezeptfelder ─────────────────────────────────────────

        ''' <summary>Klassen der Einordnung, siehe <see cref="DisplayFrameRecipeFields"/>.</summary>
        Friend Const DisplayFieldBase As String = "Basis"
        Friend Const DisplayFieldChain As String = "Kette"
        Friend Const DisplayFieldStage As String = "Stufe"
        Friend Const DisplayFieldRejected As String = "Abgelehnt"
        Friend Const DisplayFieldNoPixels As String = "Ohne Wirkung"
        Friend Const DisplayFieldSource As String = "Quelle"

        ''' <summary>JEDES FELD DES REZEPTS, eingeordnet fuer die Kartenanzeige:
        ''' Basis - steckt im Ausgangsbild und im Schluessel des Farbrauschens; aendert es sich im
        ''' Zug, faellt der Zug auf den bisherigen Weg zurueck. Kette - rechnet die Punktkette
        ''' (BuildPointOpChain), also auch auf der Karte. Stufe - liest BuildDisplayStages je
        ''' Schritt. Abgelehnt - ist es wirksam, bleibt der Zug auf dem bisherigen Weg
        ''' (DisplayFrameEligibility). Ohne Wirkung - aendert die Szene nicht.
        ''' Der Pruefstand haelt fest, dass kein Feld fehlt, dass jedes Kettenfeld ausserhalb und
        ''' jedes einfache Basisfeld innerhalb des Schluessels liegt.</summary>
        Friend Shared ReadOnly Property DisplayFrameRecipeFields As IReadOnlyDictionary(Of String, String)
            Get
                Return _displayFrameRecipeFields.Value
            End Get
        End Property

        Private Shared ReadOnly _displayFrameRecipeFields As New Lazy(Of Dictionary(Of String, String))(AddressOf BuildDisplayFrameRecipeFields)

        Private Shared Function BuildDisplayFrameRecipeFields() As Dictionary(Of String, String)
            Dim result As New Dictionary(Of String, String)(StringComparer.Ordinal)
            Dim add = Sub(kind As String, names As String)
                          For Each name In names.Split({","c, " "c, ControlChars.Lf, ControlChars.Cr}, StringSplitOptions.RemoveEmptyEntries)
                              result(name) = kind
                          Next
                      End Sub
            add(DisplayFieldChain,
                "Exposure,Brightness,Contrast,Saturation,Vibrance,Highlights,ToneRuleVersion,ShadowsLevel,Whites,Blacks," &
                "Temperature,Tint,WhiteBalanceModel,WhiteBalanceAnchorX,WhiteBalanceAnchorY,WhiteBalanceCamera," &
                "WhiteBalanceKelvin,WhiteBalanceKelvinTint," &
                "CalibrationRedHue,CalibrationRedSaturation,CalibrationGreenHue,CalibrationGreenSaturation," &
                "CalibrationBlueHue,CalibrationBlueSaturation,CalibrationShadowTint," &
                "ChannelMixerRedRed,ChannelMixerRedGreen,ChannelMixerRedBlue,ChannelMixerRedConstant," &
                "ChannelMixerGreenRed,ChannelMixerGreenGreen,ChannelMixerGreenBlue,ChannelMixerGreenConstant," &
                "ChannelMixerBlueRed,ChannelMixerBlueGreen,ChannelMixerBlueBlue,ChannelMixerBlueConstant," &
                "ChannelMixerMonochrome,ChannelMixerGrayRed,ChannelMixerGrayGreen,ChannelMixerGrayBlue,ChannelMixerGrayConstant," &
                "GradientMapShadowColor,GradientMapHighlightColor,GradientMapAmount,PosterizeLevels,ThresholdLevel," &
                "CurveRgbPoints,CurveRedPoints,CurveGreenPoints,CurveBluePoints,CurveLuminancePoints," &
                "RedHue,RedSaturation,RedLuminance,OrangeHue,OrangeSaturation,OrangeLuminance," &
                "YellowHue,YellowSaturation,YellowLuminance,GreenHue,GreenSaturation,GreenLuminance," &
                "AquaHue,AquaSaturation,AquaLuminance,BlueHue,BlueSaturation,BlueLuminance," &
                "PurpleHue,PurpleSaturation,PurpleLuminance,MagentaHue,MagentaSaturation,MagentaLuminance," &
                "PointColors," &
                "ColorGradeShadowHue,ColorGradeShadowSaturation,ColorGradeShadowLuminance," &
                "ColorGradeMidtoneHue,ColorGradeMidtoneSaturation,ColorGradeMidtoneLuminance," &
                "ColorGradeHighlightHue,ColorGradeHighlightSaturation,ColorGradeHighlightLuminance," &
                "ColorGradeGlobalHue,ColorGradeGlobalSaturation,ColorGradeGlobalLuminance,ColorGradeBalance,ColorGradeBlending," &
                "NegativeEnabled,NegativeMonochrome,NegativeBaseColor,NegativeDensityColor,NegativeGamma,NegativeModel,NegativeGrayBalance," &
                "FilterPreset,FilterStrength,LutPath,LutStrength")
            add(DisplayFieldStage,
                "Sharpness,SharpenRadius,SharpenDetail,SharpenMasking,SharpenMethod,SharpnessAtOutputScale," &
                "NoiseReduction,NoiseReductionMethod,NoiseReductionDetail,Blur,Glow,AddNoise," &
                "Vignette,VignetteTransition,VignetteRoundness,VignetteFeather,VignetteCenterX,VignetteCenterY,VignetteStyle," &
                "TiltShiftAmount,TiltShiftCenterX,TiltShiftCenterY,TiltShiftAngle,TiltShiftFocus,TiltShiftTransition," &
                "SourceWidthPixels,SourceHeightPixels")
            add(DisplayFieldRejected,
                "Clarity,Structure,Haze,HazeModel,FarbrauschGrob,ColorNoiseCoarseScale,ColorNoiseAdd,DustScratches," &
                "Grain,GrainSize,GrainFrequency,GrainColor,GrainModel,ExtensionSettings," &
                "LightSources,LightAmbient,LightDepthToken," &
                "Annotations,AnnotationGroups,Masks,MaskedAdjustmentLayers,GlobalAdjustmentsHidden," &
                "SelectionScopeEnabled,HasActiveSelection,ActiveSelectionIsMask,SelectionXPercent,SelectionYPercent," &
                "SelectionWidthPercent,SelectionHeightPercent,SelectionShapeMode,SelectionShapePointsX,SelectionShapePointsY," &
                "SelectionMaskLeft,SelectionMaskTop,SelectionMaskRight,SelectionMaskBottom,SelectionMaskPngBase64," &
                "SelectionMaskRaster,SelectionFeatherPixels,SelectionMaskSoftBaked")
            add(DisplayFieldNoPixels,
                "UnknownFields,LockResizeAspect,LockCanvasAspect,BaseLayerLockPosition,BaseLayerLockTransparency,RecipeCoordinateVersion")
            add(DisplayFieldBase,
                "GeometryOperations,ExposureInSource,SourceFollowsRawExposure,RampBlackShareInSource," &
                "ShadowSaturationInSource,KneeInSource,SourceFromRawStage,ColorNoiseReduction," &
                "DefringePurple,RotationDegrees,PerspectiveHorizontal,PerspectiveVertical," &
                "PerspectiveAspect,PerspectiveScale,PerspectiveCorner0X,PerspectiveCorner0Y,PerspectiveCorner1X," &
                "PerspectiveCorner1Y,PerspectiveCorner2X,PerspectiveCorner2Y,PerspectiveCorner3X,PerspectiveCorner3Y," &
                "ImageWarp,StraightenDegrees,StraightenExpandCanvas,StraightenAutoCrop,FlipHorizontal,FlipVertical," &
                "CropLeftPercent,CropTopPercent,CropRightPercent,CropBottomPercent,ResizeWidth,ResizeHeight," &
                "ResizeFitInsideBox,ResizeScalePercent,NoResizeUpscale,ResizeInterpolation," &
                "CanvasWidth,CanvasHeight,CanvasAnchor,CanvasBackgroundColor," &
                "RetouchSpots,RasterPaintStrokes,WorkingImageVersion,BakedOperations")
            ' Wirkt schon auf die Quelle (RAW-Stufe, Objektiv beim Entwickeln, Arbeitsbild, gebackene
            ' Vorgaenge): eine Aenderung liefert eine andere Quelle mit anderem Namen oder Stempel, und
            ' das Ausgangsbild passt nicht mehr (DisplayBaseMatches). WhiteBalanceMode ist nur die
            ' Beschriftung der Auswahl; die Werte stehen in den Kettenfeldern.
            add(DisplayFieldSource,
                "WhiteBalanceMode,LensDistortion,LensTca,LensVignetting,LensModel,LensDistortionAmount,LensTcaAmount," &
                "LensVignettingAmount,LensTcaRed,LensTcaBlue,RawHighlightRecovery,RawModelDenoise,RawModelDenoiseStrength," &
                "RawSensorEdgeCrop,RawCameraAspectCrop,UpscaleModel,WorkingImageHasTransparency,WorkingImageExposure," &
                "BakedOperationsApplied,PixelLayerHidden")
            add(DisplayFieldRejected, "BackgroundHidden")
            Return result
        End Function

        ' ── Eignung ──────────────────────────────────────────────────────────────

        ''' <summary>Kann die Kartenanzeige dieses Rezept zeigen? Prueft nur das Rezept; ob die
        ''' raeumlichen Stufen einen Shader haben, sagt erst <see cref="BuildDisplayStages"/>.
        ''' Der Grund ist fuer das Diagnoseprotokoll.</summary>
        Friend Shared Function DisplayFrameEligibility(adj As ImageAdjustments) As (Ok As Boolean, Reason As String)
            If adj Is Nothing Then Return (False, "kein Rezept")
            If adj.GlobalAdjustmentsHidden Then Return (False, "Anpassungen ausgeblendet")
            If adj.BackgroundHidden Then Return (False, "Hintergrund ausgeblendet")
            If adj.Annotations IsNot Nothing AndAlso adj.Annotations.Any(Function(a) a IsNot Nothing AndAlso a.IsVisible) Then
                Return (False, "Objekte")
            End If
            If adj.MaskedAdjustmentLayers IsNot Nothing AndAlso
               adj.MaskedAdjustmentLayers.Any(Function(l) l IsNot Nothing AndAlso adj.IsMaskedLayerRenderVisible(l)) Then
                Return (False, "Maskenebenen")
            End If
            If SelectionScopeIsEnabled(adj) Then Return (False, "Auswahl-Skopus")
            If HasActiveLightSources(adj) Then Return (False, "Lichtquellen")
            If Not String.IsNullOrEmpty(adj.ExtensionSettings) AndAlso
               (ExtensionRunOrder(adj, ExtensionStages.BeforeGrain).Any() OrElse
                ExtensionRunOrder(adj, ExtensionStages.AfterGrain).Any()) Then Return (False, "Erweiterungen")
            If adj.Clarity <> 0 Then Return (False, "Klarheit")
            If adj.[Structure] <> 0 Then Return (False, "Struktur")
            If adj.Haze <> 0 Then Return (False, "Dunst")
            If adj.NoiseReduction > 0 AndAlso
               (adj.NoiseReductionMethod = NoiseReductionMethod.Median OrElse adj.NoiseReductionMethod = NoiseReductionMethod.AdaptiveGuided) Then
                Return (False, "Rauschminderung mit Statistik oder Median")
            End If
            If adj.FarbrauschGrob > 0 Then Return (False, "Farbrauschen grob")
            If adj.ColorNoiseAdd > 0 Then Return (False, "Farbflecken")
            If adj.DustScratches <> 0 Then Return (False, "Staub und Kratzer")
            If adj.Grain > 0 Then Return (False, "Koernung")
            If adj.AddNoise > 0 Then Return (False, "Rauschen hinzufuegen")
            Return (True, "")
        End Function

        ''' <summary>Die raeumlichen Stufen nach der Kette, in der Reihenfolge von
        ''' ApplyDetailAdjustmentStagesCore, als Shader ohne Zuruecklesen. Nothing samt Grund, wenn
        ''' eine wirksame Stufe keinen solchen Shader hat.</summary>
        Friend Shared Function BuildDisplayStages(base As SKBitmap, adj As ImageAdjustments, outputScale As Double,
                                                  ByRef reason As String) As List(Of (Name As String, Gpu As Func(Of GRContext, SKImage, SKSurface)))
            reason = ""
            Dim stages As New List(Of (Name As String, Gpu As Func(Of GRContext, SKImage, SKSurface)))()
            Dim failed As String = Nothing
            Dim addStage = Sub(name As String, gpu As Func(Of GRContext, SKImage, SKSurface))
                               If failed IsNot Nothing Then Return
                               If gpu Is Nothing Then
                                   failed = name & " ohne Shader"
                               Else
                                   stages.Add((name, gpu))
                               End If
                           End Sub
            If String.Equals(If(adj.FilterPreset, "").Trim(), "weich", StringComparison.OrdinalIgnoreCase) Then
                addStage("Filter weich", SoftFocusOnGpu(Clamp(adj.FilterStrength / 100.0F, 0, 1)))
            End If
            If adj.NoiseReduction > 0 Then
                If adj.NoiseReductionMethod = NoiseReductionMethod.Guided Then
                    addStage("Rauschen (Fuehrung)", GuidedNoiseReductionOnGpu(adj.NoiseReduction / 100.0F, adj.NoiseReductionDetail / 100.0F,
                                                                             adaptive:=False, multiScale:=False))
                Else
                    addStage("Rauschen", NoiseReductionOnGpu(adj.NoiseReduction / 100.0F, adj.NoiseReductionDetail / 100.0F))
                End If
            End If
            If adj.Blur > 0 Then addStage("Weichzeichnen", ImageBlurOnGpu(base, adj.Blur / 100.0F))
            If adj.Glow <> 0 Then addStage("Gluehen", GlowOnGpu(adj.Glow / 100.0F))
            If adj.Sharpness > 0 Then
                Dim sharpAmount = adj.Sharpness / 100.0F *
                                  SharpnessDisplayFactor(outputScale, adj.SharpenMethod, adj.SharpenRadius / 100.0F)
                addStage("Schaerfe", SharpnessOnGpu(sharpAmount, adj.SharpenRadius / 100.0F, adj.SharpenDetail / 100.0F,
                                                    adj.SharpenMasking / 100.0F, adj.SharpenMethod))
            End If
            If adj.TiltShiftAmount > 0 Then
                addStage("Tilt-Shift", TiltShiftOnGpu(base, adj.TiltShiftAmount / 100.0F, adj.TiltShiftCenterX, adj.TiltShiftCenterY,
                                                      adj.TiltShiftAngle, adj.TiltShiftFocus, adj.TiltShiftTransition))
            End If
            If adj.Vignette <> 0 Then
                Dim vignetteAmount = adj.Vignette / 100.0F
                vignetteAmount *= If(vignetteAmount > 0, 0.59F, 0.85F)
                addStage("Vignette", VignetteOnGpu(base, vignetteAmount, adj.VignetteTransition, adj.VignetteRoundness, adj.VignetteFeather,
                                                   adj.VignetteCenterX, adj.VignetteCenterY, adj.VignetteStyle))
            End If
            If adj.AddNoise < 0 Then addStage("Rauschen hinzufuegen", NoiseReductionOnGpu(-adj.AddNoise / 100.0F))
            If failed IsNot Nothing Then
                reason = failed
                Return Nothing
            End If
            Return stages
        End Function

        ' ── Ausgangsbild und Rahmen ─────────────────────────────────────────────

        ''' <summary>Der Rezeptteil des Namens: alles bis zum Farbrauschen.</summary>
        Friend Shared Function DisplayBaseRecipeKey(adj As ImageAdjustments) As String
            Return ComputeColorNoiseCacheKey(adj)
        End Function

        ''' <summary>Passt das Ausgangsbild noch zu Quelle und Rezept? Aendert sich im Zug etwas vor
        ''' der Kette (Geometrie, Farbrauschen, Objektiv), ist es ein anderes.</summary>
        Friend Shared Function DisplayBaseMatches(base As DisplayBase, source As SKBitmap, adj As ImageAdjustments) As Boolean
            If base Is Nothing OrElse base.Bitmap Is Nothing OrElse source Is Nothing Then Return False
            If base.SourceIdentityValue <> SourceIdentity(source) Then Return False
            If base.SourceStamp <> Interlocked.Read(_baseSourceStamp) Then Return False
            Return String.Equals(base.RecipeKey, DisplayBaseRecipeKey(adj), StringComparison.Ordinal)
        End Function

        ''' <summary>Der Stand nach Farbsaum, Geometrie, Hintergrund und Farbrauschen, genau wie am
        ''' Anfang von ProcessBitmapBaseBeforeMaskedLayers und mit demselben Zwischenspeicher
        ''' (TakeColorNoiseState). Nothing, wenn das Rezept fuer die Kartenanzeige nicht taugt.</summary>
        Friend Shared Function PrepareDisplayBase(source As SKBitmap, adj As ImageAdjustments) As DisplayBase
            If source Is Nothing OrElse adj Is Nothing Then Return Nothing
            If Not DisplayFrameEligibility(adj).Ok Then Return Nothing
            Dim stampAtStart = Interlocked.Read(_baseSourceStamp)
            Dim recipeKey = DisplayBaseRecipeKey(adj)
            Dim processed As SKBitmap = source
            Dim owned = False
            Dim fromCache = False
            Dim useCache = ColorNoiseCacheApplies(adj)
            If useCache Then
                Dim hit = PerformanceTraceService.Measure("Pixel: Farbrauschen (Zwischenspeicher)",
                                                          Function() TakeColorNoiseState(source, stampAtStart, recipeKey))
                If hit IsNot Nothing Then
                    processed = hit
                    owned = True
                    fromCache = True
                End If
            End If
            If Not fromCache Then
                processed = ApplyDefringeStageOwned(processed, adj, owned)
                processed = ApplyGeometryPipelineOwned(processed, adj, owned)
                processed = ReplaceBitmapOwned(processed, RunStepWithDeep(processed, Function(b) ApplyDocumentBackground(b, adj)), owned)
                processed = ApplyColorNoiseStageCore(processed, adj, owned)
                If useCache Then RememberColorNoiseState(source, stampAtStart, recipeKey, processed)
            End If
            Dim bitmap = TakeOwnership(processed, owned)
            If bitmap Is Nothing Then Return Nothing
            If DeepOf(bitmap) IsNot Nothing OrElse
               (bitmap.ColorType <> SKColorType.Bgra8888 AndAlso bitmap.ColorType <> SKColorType.Rgba8888) OrElse
               bitmap.AlphaType = SKAlphaType.Unpremul Then
                DropDeep(bitmap)
                bitmap.Dispose()
                Return Nothing
            End If
            Return New DisplayBase With {
                .Bitmap = bitmap, .RecipeKey = recipeKey, .SourceStamp = stampAtStart,
                .SourceIdentityValue = SourceIdentity(source),
                .SourceWidth = source.Width, .SourceHeight = source.Height,
                .Key = $"{SourceIdentity(source)}|{bitmap.Width}x{bitmap.Height}|{stampAtStart}|{recipeKey}"}
        End Function

        ''' <summary>Baut einen Rahmen auf dem rufenden Faden: Kette, Tabellen, oertliche Basis und
        ''' die Stufen. Nothing samt Grund, wenn der Weg dieses Rezept nicht kann.</summary>
        ''' <param name="source">Die Quelle, aus der das Ausgangsbild entstand (fuer den Massstab der
        ''' Schaerfe, wie DisplayOutputScale in der Szene).</param>
        ''' <param name="smoothOnGpu">Darf das Raster der oertlichen Basis erst auf der Karte
        ''' geglaettet werden? Im Pruefmodus nicht: der Prozessor glaettet genauer.</param>
        Friend Shared Function BuildDisplayFrame(base As DisplayBase, source As SKBitmap, adj As ImageAdjustments,
                                                 smoothOnGpu As Boolean, ByRef reason As String) As DisplayFrame
            reason = ""
            If base Is Nothing OrElse base.Bitmap Is Nothing Then reason = "kein Ausgangsbild" : Return Nothing
            Dim check = DisplayFrameEligibility(adj)
            If Not check.Ok Then reason = check.Reason : Return Nothing
            If Not DisplayBaseMatches(base, source, adj) Then reason = "Ausgangsbild veraltet" : Return Nothing
            Dim bitmap = base.Bitmap
            Dim stageReason As String = Nothing
            Dim stages = BuildDisplayStages(bitmap, adj, DisplayOutputScale(source, adj), stageReason)
            If stages Is Nothing Then reason = stageReason : Return Nothing
            Dim chain = PerformanceTraceService.Measure("Kartenanzeige: Kette bauen", Function() BuildPointOpChain(adj, bitmap, Nothing))
            Dim frame As New DisplayFrame With {
                .Base = base, .Width = bitmap.Width, .Height = bitmap.Height,
                .Premul = bitmap.AlphaType = SKAlphaType.Premul, .Stages = stages,
                .Sequence = Interlocked.Increment(_displayFrameSequence)}
            If chain.IsIdentity Then Return frame
            Dim chainReason As String = Nothing
            If Not CanRunPointOpChainShader(bitmap, chain, chainReason) Then reason = "Kette: " & chainReason : Return Nothing
            Try
                frame.Chain = chain
                frame.Table = BuildShaderTables(chain)
                frame.Dither = BuildDitherImage()
                frame.CubeA = BuildCubeImage(chain, 0)
                frame.CubeB = BuildCubeImage(chain, 1)
                If chain.Hsl IsNot Nothing Then
                    Using image = SKImage.FromPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes)
                        frame.Guide = BuildGuideImage(image, chain)
                    End Using
                Else
                    frame.Guide = BuildGuideImage(Nothing, chain)
                End If
                ' Die oertliche Basis wie in TryRunPointOpChainOnGpu: aus dem Zwischenspeicher, sonst
                ' erst auf der Karte geglaettet, und nur wo die nicht kann, auf dem Prozessor.
                If chain.LocalToneZones IsNot Nothing Then
                    Dim edge = _localToneReferenceEdge
                    ' Die Farbmittel je Feld haengen nur am Ausgangsbild und bleiben im Zug liegen;
                    ' je Schritt laufen nur die Stufen der Kette ueber das kleine Raster.
                    Dim means = base.ToneCellMeans()
                    Dim grid = PerformanceTraceService.Measure("Pixel: Punktkette, oertliche Basis",
                        Function() If(means Is Nothing, BuildLocalToneGridFor(bitmap, chain, edge), LocalToneGridFromMeans(means, chain, edge)))
                    If grid IsNot Nothing Then
                        Dim localBase = LocalToneBaseFromGrid(grid, allowSmoothing:=False)
                        If localBase Is Nothing AndAlso smoothOnGpu AndAlso LocalBaseFitsGpu(grid) Then
                            frame.GpuGrid = grid
                            localBase = New LocalToneBase With {
                                .Width = grid.Width, .Height = grid.Height, .StepSize = grid.StepSize,
                                .OriginX = grid.OriginX, .OriginY = grid.OriginY}
                        ElseIf localBase Is Nothing Then
                            localBase = PerformanceTraceService.Measure("Pixel: Punktkette, oertliche Basis",
                                                                        Function() LocalToneBaseFromGrid(grid, allowSmoothing:=True))
                        End If
                        frame.LocalBase = localBase
                    End If
                End If
                If frame.GpuGrid Is Nothing Then frame.LocalBaseImage = BuildLocalBaseImage(frame.LocalBase)
                Return frame
            Catch
                frame.Dispose()
                Throw
            End Try
        End Function

        ''' <summary>Hochladen eines kleinen Bilds; ohne Kontext (Pruefmodus) das Bild selbst.</summary>
        Private Shared Function DisplayTexture(gr As GRContext, image As SKImage, residency As DisplayResidency) As SKImage
            If image Is Nothing Then Return Nothing
            If gr Is Nothing Then Return image
            Dim texture = image.ToTextureImage(gr)
            If texture Is Nothing Then Throw New InvalidOperationException("Tabelle der Kartenanzeige nicht anlegbar.")
            residency.Retire(texture)
            Return texture
        End Function

        ''' <summary>Rechnet einen Rahmen: Punktkette ueber dem Ausgangsbild, dann die Stufen.
        ''' Mit <paramref name="gr"/> auf der Karte (Renderfaden, ohne Warten und ohne Zuruecklesen),
        ''' ohne ueber Skia auf dem Prozessor (Pruefstand). Das Ergebnis gehoert der Ablage
        ''' (<paramref name="residency"/>) und lebt bis zum naechsten Render.</summary>
        Friend Shared Function RenderDisplayFrame(gr As GRContext, frame As DisplayFrame, residency As DisplayResidency) As SKImage
            Dim cached = residency.CachedImage(frame)
            If cached IsNot Nothing Then Return cached
            Dim baseImage = residency.BaseImageFor(gr, frame.Base)
            Dim current = baseImage
            If frame.Chain IsNot Nothing Then
                Dim smoothed As SKImage = Nothing
                Dim localBase = frame.LocalBase
                If frame.GpuGrid IsNot Nothing Then
                    smoothed = SmoothLocalToneBaseOnGpu(gr, frame.GpuGrid)
                    If smoothed Is Nothing Then
                        localBase = SmoothLocalToneGrid(frame.GpuGrid)
                        Dim fallbackImage = BuildLocalBaseImage(localBase)
                        residency.Retire(fallbackImage)
                        smoothed = DisplayTexture(gr, fallbackImage, residency)
                    Else
                        residency.Retire(smoothed)
                    End If
                Else
                    smoothed = DisplayTexture(gr, frame.LocalBaseImage, residency)
                End If
                Dim table = DisplayTexture(gr, frame.Table, residency)
                Dim dither = DisplayTexture(gr, frame.Dither, residency)
                Dim cubeA = DisplayTexture(gr, frame.CubeA, residency)
                Dim cubeB = DisplayTexture(gr, frame.CubeB, residency)
                Dim guide = DisplayTexture(gr, frame.Guide, residency)
                Dim surface = NewStageSurface(gr, frame.Width, frame.Height)
                If surface Is Nothing Then Throw New InvalidOperationException("Zielflaeche der Kartenanzeige nicht anlegbar.")
                residency.Retire(surface)
                Using shader = PointOpShader(PointOpEffect(), frame.Chain, current, table, dither, smoothed, cubeA, cubeB, guide,
                                             localBase, frame.Premul)
                    Using paint = New SKPaint With {.Shader = shader, .BlendMode = SKBlendMode.Src}
                        surface.Canvas.DrawRect(0, 0, frame.Width, frame.Height, paint)
                    End Using
                End Using
                current = surface.Snapshot()
            End If
            For Each stage In frame.Stages
                Dim nextSurface = stage.Gpu(gr, current)
                If nextSurface Is Nothing Then Throw New InvalidOperationException($"Stufe {stage.Name} der Kartenanzeige gescheitert.")
                residency.Retire(nextSurface)
                ' Das Zwischenbild davor geht im naechsten Render frei, das Ausgangsbild nie hier.
                If Not Object.ReferenceEquals(current, baseImage) Then residency.Retire(current)
                current = nextSurface.Snapshot()
            Next
            ' Das Ergebnis lebt, bis ein anderes es ersetzt (Avalonia zeichnet dieselbe Operation oft
            ' mehrmals). Ohne Kette und ohne Stufen ist es das Ausgangsbild selbst; das bleibt der Ablage.
            residency.RememberImage(frame, current)
            Return current
        End Function

        ''' <summary>Fuer den Pruefstand: ein Rahmen ueber Skia auf dem Prozessor, als Bitmap im
        ''' Format der Szene (Rgba8888, vormultipliziert).</summary>
        Friend Shared Function RenderDisplayFrameOnCpu(frame As DisplayFrame) As SKBitmap
            Dim residency As New DisplayResidency()
            residency.BeginRender(Nothing)
            Try
                Dim image = RenderDisplayFrame(Nothing, frame, residency)
                Dim result As New SKBitmap(New SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                If Not image.ReadPixels(result.Info, result.GetPixels(), result.RowBytes, 0, 0) Then
                    result.Dispose()
                    Throw New InvalidOperationException("Rahmen der Kartenanzeige nicht lesbar.")
                End If
                Return result
            Finally
                residency.ReleaseAll()
            End Try
        End Function

        ''' <summary>Die Selbstpruefung der Kartenanzeige im Kontext des Fensters, einmal je Sitzung
        ''' beim ersten Rahmen: die Pruefflaeche mit dem Rezept der Selbstpruefung des Bilddiensts
        ''' samt Schaerfe und Vignette, einmal zurueckgelesen und gegen denselben Rahmen ueber Skia
        ''' auf dem Prozessor verglichen. Liefert die groesste Abweichung in Tonstufen, oder -1,
        ''' wenn der Rahmen auf der Karte nicht entsteht.</summary>
        Friend Shared Function DisplaySelfCheck(gr As GRContext, ByRef detail As String) As Integer
            detail = ""
            Using src = BuildGpuCheckImage()
                Dim adj As New ImageAdjustments With {
                    .Exposure = 0.3F, .Contrast = 20, .Whites = 20, .Blacks = -10, .Temperature = -10,
                    .Saturation = 10, .Vibrance = 30, .RedHue = 15, .BlueSaturation = -20,
                    .CalibrationShadowTint = 10, .ChannelMixerRedGreen = 10, .Highlights = -40, .ShadowsLevel = 30,
                    .Sharpness = 40, .Vignette = 30}
                Dim base As New DisplayBase With {
                    .Bitmap = src.Copy(), .RecipeKey = "", .Key = "selbstpruefung|" & Guid.NewGuid().ToString("N")}
                Try
                    Dim stageReason As String = Nothing
                    Dim stages = BuildDisplayStages(base.Bitmap, adj, 1.0, stageReason)
                    If stages Is Nothing Then detail = stageReason : Return -1
                    Dim chain = BuildPointOpChain(adj, base.Bitmap, Nothing)
                    Dim frame As New DisplayFrame With {
                        .Base = base, .Width = src.Width, .Height = src.Height, .Premul = True, .Stages = stages, .Chain = chain,
                        .Table = BuildShaderTables(chain), .Dither = BuildDitherImage(),
                        .CubeA = BuildCubeImage(chain, 0), .CubeB = BuildCubeImage(chain, 1), .Guide = BuildGuideImage(Nothing, chain),
                        .LocalBase = BuildLocalToneBaseFor(base.Bitmap, chain)}
                    frame.LocalBaseImage = BuildLocalBaseImage(frame.LocalBase)
                    Using frame
                        Using expected = RenderDisplayFrameOnCpu(frame)
                            Dim residency As New DisplayResidency()
                            residency.BeginRender(gr)
                            Try
                                Dim image = RenderDisplayFrame(gr, frame, residency)
                                Using actual As New SKBitmap(expected.Info)
                                    If Not image.ReadPixels(actual.Info, actual.GetPixels(), actual.RowBytes, 0, 0) Then
                                        detail = "Zuruecklesen gescheitert"
                                        Return -1
                                    End If
                                    Dim a = expected.GetPixelSpan()
                                    Dim b = actual.GetPixelSpan()
                                    Dim worst = 0
                                    For i = 0 To a.Length - 1
                                        Dim d = Math.Abs(CInt(a(i)) - CInt(b(i)))
                                        If d > worst Then worst = d
                                    Next
                                    Return worst
                                End Using
                            Finally
                                residency.ReleaseAll()
                            End Try
                        End Using
                    End Using
                Finally
                    base.Dispose()
                End Try
            End Using
        End Function

        ''' <summary>Hoechste Abweichung der Selbstpruefung gegen den Prozessor, die der Weg noch
        ''' zulaesst: die Grenze des Plans fuer Kette mit Stufen.</summary>
        Friend Const DisplaySelfCheckTolerance As Integer = 4

    End Class

End Namespace
