Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Services
Imports ReactiveUI
Imports SkiaSharp

Namespace ViewModels

    Partial Public Class EditorViewModel

        ''' <summary>Gespeicherte Anpassungen VORAB ZEIGEN, ohne sie anzuwenden: als kleines Bild je
        ''' Anpassung (Fenster "Anpassungen verwalten") und im grossen Bild, solange der Zeiger auf
        ''' einer Anpassung steht (dort und in der Auswahlliste des Bereichs).
        '''
        ''' EIN EIGENER VORSCHAUZUSTAND, DER NIE INS REZEPT SCHREIBT. Die Regler, die Historie und
        ''' der Zustand "geaendert" bleiben, wie sie sind: die Anpassung wird erst dort aufgelegt,
        ''' wo das Rezept fuer die ANZEIGE entsteht (<see cref="GetCurrentAdjustments"/> mit
        ''' forPreview, <see cref="ApplyPresetHoverTo"/>). Speichern, Rueckgaengig und jeder
        ''' Schnappschuss lesen das kanonische Rezept und sehen sie nicht. Endet die Vorschau, wird
        ''' nur neu gerendert.
        '''
        ''' DIESELBE RECHNUNG WIE DAS ANWENDEN: <see cref="ApplyPresetLook"/> legt die Anpassung
        ''' auf ein Rezept, fuer das Anwenden (<see cref="ApplyAdjustmentPreset"/>), fuer die
        ''' Vorschau im grossen Bild und fuer die kleinen Bilder.
        '''
        ''' DIE KLEINEN BILDER rechnen wie die Kacheln der Filter (EditorViewModelFilterPreview.vb)
        ''' und auf DEREN kleiner Quelle: erst wenn das grosse Bild steht, im Hintergrund, eine
        ''' nach der anderen, abbrechbar. Beide teilen sich das Tor _filterPreviewRenderGate, es
        ''' laeuft also nie ein Durchgang der einen neben dem der anderen, und die Datei wird dafuer
        ''' kein zweites Mal dekodiert.</summary>
        Private _presetHoverPreset As AdjustmentPresetFile
        Private _presetHoverLook As ImageAdjustments
        ''' Die Anpassung unter dem Zeiger, die nach der Wartezeit gezeigt wird.
        Private _presetHoverPending As AdjustmentPresetFile
        Private _presetHoverTimer As DispatcherTimer
        ''' Kurz gesetzt, wenn das Rezept OHNE die Vorschau gebraucht wird (kleine Bilder).
        Private _presetHoverSuspended As Boolean

        ''' Wartezeit, bevor eine ueberfahrene Anpassung im grossen Bild erscheint: wer nur ueber
        ''' die Liste faehrt, soll nichts rechnen lassen.
        Friend Const PresetHoverDelayMs As Integer = 150
        ''' Laengste Kante eines kleinen Bildes: doppelt so gross wie am Schirm.
        Private Const PresetThumbnailMaxDimension As Integer = 160
        ''' So lange warten die kleinen Bilder hoechstens auf das grosse.
        Private Const PresetThumbnailWaitMs As Integer = 8000

        ''' <summary>Aus dem Konstruktor: ein Bildwechsel beendet die Vorschau, sie gehoerte dem
        ''' vorigen Bild.</summary>
        Private Sub InitializeAdjustmentPresetPreviews()
            AddHandler Me.PropertyChanged,
                Sub(sender, e)
                    If String.Equals(e.PropertyName, NameOf(CurrentImagePath), StringComparison.Ordinal) Then DropAdjustmentPresetHover()
                End Sub
        End Sub

        ''' <summary>Legt eine gespeicherte Anpassung auf ein Rezept: ihre Eintraege ersetzen, alles
        ''' andere bleibt (AdjustmentEntries.ApplyPreset). Der Graufarbausgleich der Anpassung
        ''' gehoert ihrem Quellbild und kommt nicht mit. Der EINE Schritt fuer Anwenden, Vorschau
        ''' und kleine Bilder.</summary>
        Friend Shared Sub ApplyPresetLook(target As ImageAdjustments, look As ImageAdjustments, fields As IEnumerable(Of String))
            If target Is Nothing OrElse look Is Nothing Then Return
            AdjustmentEntries.ApplyPreset(target, look, fields)
            target.ForgetTransferredNegativeGrayBalance(look)
        End Sub

        ''' <summary>Steht gerade eine Anpassung nur zur Ansicht im grossen Bild?</summary>
        Public ReadOnly Property IsAdjustmentPresetHoverActive As Boolean
            Get
                Return _presetHoverLook IsNot Nothing
            End Get
        End Property

        ''' <summary>Die Anpassung, die gerade nur zur Ansicht im grossen Bild steht, sonst
        ''' Nothing.</summary>
        Public ReadOnly Property AdjustmentPresetHoverPreset As AdjustmentPresetFile
            Get
                Return If(_presetHoverLook Is Nothing, Nothing, _presetHoverPreset)
            End Get
        End Property

        ''' <summary>Der Zeiger steht auf einer Anpassung: nach einer kurzen Wartezeit zeigt das
        ''' grosse Bild sie vorab. Nothing beendet die Vorschau
        ''' (<see cref="EndAdjustmentPresetHover"/>). Am Rezept aendert sich nichts.</summary>
        Public Sub BeginAdjustmentPresetHover(preset As AdjustmentPresetFile)
            If preset Is Nothing OrElse Not HasDocument Then
                EndAdjustmentPresetHover()
                Return
            End If
            ' Schon gezeigt: nur ein vorgemerkter Wechsel auf eine andere entfaellt.
            If _presetHoverLook IsNot Nothing AndAlso _presetHoverPreset Is preset Then
                _presetHoverPending = Nothing
                _presetHoverTimer?.Stop()
                Return
            End If
            _presetHoverPending = preset
            If _presetHoverTimer Is Nothing Then
                _presetHoverTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(PresetHoverDelayMs)}
                AddHandler _presetHoverTimer.Tick, Sub() OnPresetHoverTimer()
            End If
            _presetHoverTimer.Stop()
            _presetHoverTimer.Start()
        End Sub

        Private Sub OnPresetHoverTimer()
            _presetHoverTimer.Stop()
            Dim preset = _presetHoverPending
            _presetHoverPending = Nothing
            If preset Is Nothing OrElse Not HasDocument Then Return
            Dim look = AdjustmentEntries.LoadPresetLook(preset)
            If look Is Nothing Then
                ' Unlesbar: lieber das Bild, wie es ist, als eine halbe Vorschau.
                EndAdjustmentPresetHover()
                Return
            End If
            _presetHoverPreset = preset
            _presetHoverLook = look
            Me.RaisePropertyChanged(NameOf(IsAdjustmentPresetHoverActive))
            SchedulePresetHoverRender()
        End Sub

        ''' <summary>Der Zeiger hat die Anpassung verlassen, die Liste oder das Fenster ist zu: das
        ''' grosse Bild zeigt wieder das Rezept. Eine nur vorgemerkte Vorschau entfaellt, ohne dass
        ''' gerechnet wurde.</summary>
        Public Sub EndAdjustmentPresetHover()
            _presetHoverTimer?.Stop()
            _presetHoverPending = Nothing
            If _presetHoverLook Is Nothing Then Return
            _presetHoverPreset = Nothing
            _presetHoverLook = Nothing
            Me.RaisePropertyChanged(NameOf(IsAdjustmentPresetHoverActive))
            SchedulePresetHoverRender()
        End Sub

        ''' <summary>Vergisst die Vorschau, ohne neu zu rendern: wer das ruft, rendert ohnehin
        ''' (Anwenden, Bildwechsel).</summary>
        Private Sub DropAdjustmentPresetHover()
            _presetHoverTimer?.Stop()
            _presetHoverPending = Nothing
            If _presetHoverLook Is Nothing Then Return
            _presetHoverPreset = Nothing
            _presetHoverLook = Nothing
            Me.RaisePropertyChanged(NameOf(IsAdjustmentPresetHoverActive))
        End Sub

        ''' <summary>Rendert das grosse Bild neu, OHNE das Dokument als geaendert zu zaehlen und
        ''' ohne die Statuszeile anzufassen. Wie ScheduleToolPreviewUpdate: nur die Anzeige.</summary>
        Private Sub SchedulePresetHoverRender()
            If Not HasDocument Then Return
            MarkPreviewPending()
            _fullRenderPending = True
            RestartPreviewTimer(PreviewDebounceMs)
        End Sub

        ''' <summary>Aus <see cref="GetCurrentAdjustments"/> fuer die Anzeige: legt die Anpassung
        ''' unter dem Zeiger auf das Rezept, das gleich gerendert wird. Das Rezept ist dort ein
        ''' frisch gebauter Satz, der Editor selbst bleibt unberuehrt.</summary>
        Private Sub ApplyPresetHoverTo(adj As ImageAdjustments)
            If _presetHoverLook Is Nothing OrElse _presetHoverSuspended OrElse adj Is Nothing Then Return
            ApplyPresetLook(adj, _presetHoverLook, _presetHoverPreset.Fields)
        End Sub

        ''' <summary>Das Rezept fuer die Anzeige OHNE die Vorschau unter dem Zeiger: darauf rechnen
        ''' die kleinen Bilder, sonst zeigte jedes die gerade ueberfahrene Anpassung mit.</summary>
        Private Function PreviewAdjustmentsWithoutPresetHover() As ImageAdjustments
            Dim before = _presetHoverSuspended
            _presetHoverSuspended = True
            Try
                Return GetCurrentAdjustments(forPreview:=True)
            Finally
                _presetHoverSuspended = before
            End Try
        End Function

        ''' <summary>Das Rezept, das die Anzeige nach dem Anwenden von <paramref name="preset"/>
        ''' haette, ausgehend von <paramref name="current"/>. Rechnet nur. Nothing, wenn die
        ''' Anpassung unlesbar ist.</summary>
        Private Shared Function PresetPreviewAdjustments(current As ImageAdjustments, preset As AdjustmentPresetFile) As ImageAdjustments
            Dim look = AdjustmentEntries.LoadPresetLook(preset)
            If look Is Nothing OrElse current Is Nothing Then Return Nothing
            Dim adj = current.Clone()
            ApplyPresetLook(adj, look, preset.Fields)
            Return adj
        End Function

        ''' <summary>Rechnet je Anpassung ein kleines Bild des geoeffneten Fotos mit dieser
        ''' Anpassung und gibt es auf dem Anzeigefaden an <paramref name="deliver"/>. Wartet, bis
        ''' das grosse Bild steht, rechnet im Hintergrund eine nach der anderen und hoert auf,
        ''' sobald <paramref name="token"/> abbricht oder ein anderes Bild offen ist. Wer ein Bild
        ''' bekommt, gibt es selbst wieder frei.</summary>
        Public Async Function RenderAdjustmentPresetThumbnailsAsync(presets As IReadOnlyList(Of AdjustmentPresetFile),
                                                                    deliver As Action(Of AdjustmentPresetFile, Bitmap),
                                                                    token As CancellationToken) As Task
            If presets Is Nothing OrElse presets.Count = 0 OrElse deliver Is Nothing OrElse Not HasDocument Then Return
            Dim imagePath = _currentImagePath
            Try
                Await Task.Delay(FilterPreviewDelayMs, token)
                ' Das grosse Bild hat Vorrang.
                Dim waited = 0
                While waited < PresetThumbnailWaitMs AndAlso (_previewPending OrElse Volatile.Read(_activePreviewRenders) <> 0)
                    Await Task.Delay(100, token)
                    waited += 100
                End While
            Catch ex As OperationCanceledException
                Return
            End Try

            ' Die Rezepte entstehen auf dem Anzeigefaden, aus dem Zustand des Editors.
            Dim jobs As List(Of (Preset As AdjustmentPresetFile, Adjustments As ImageAdjustments)) = Nothing
            Dim sourcePath As String = Nothing
            Dim usesUnbaked As Boolean
            Dim sourceKey As String = Nothing
            Await Dispatcher.UIThread.InvokeAsync(
                Sub()
                    If token.IsCancellationRequested OrElse Not HasDocument OrElse
                       Not String.Equals(_currentImagePath, imagePath, StringComparison.Ordinal) Then Return
                    Dim current = PreviewAdjustmentsWithoutPresetHover()
                    Dim built As New List(Of (Preset As AdjustmentPresetFile, Adjustments As ImageAdjustments))()
                    For Each preset In presets
                        Dim adj = PresetPreviewAdjustments(current, preset)
                        If adj IsNot Nothing Then built.Add((preset, adj))
                    Next
                    sourcePath = RenderSourcePath
                    usesUnbaked = _pixelLayerHidden
                    sourceKey = FilterPreviewSourceKeyNow()
                    jobs = built
                End Sub)
            If jobs Is Nothing OrElse jobs.Count = 0 OrElse String.IsNullOrEmpty(sourcePath) Then Return

            Try
                Await _filterPreviewRenderGate.WaitAsync(token)
            Catch ex As OperationCanceledException
                Return
            End Try
            Try
                Await Task.Run(
                    Sub()
                        Dim source = EnsureFilterPreviewSource(sourcePath, usesUnbaked, sourceKey)
                        If source Is Nothing Then Return
                        For Each job In jobs
                            If token.IsCancellationRequested Then Return
                            Dim rendered As SKBitmap = Nothing
                            Dim small As SKBitmap = Nothing
                            Try
                                rendered = ImageProcessor.RenderPreviewSkBitmap(source, job.Adjustments)
                                If rendered Is Nothing Then Continue For
                                small = ShrinkForPresetThumbnail(rendered)
                                Dim image = ImageProcessor.ToAvaloniaBitmap(If(small, rendered))
                                Dim preset = job.Preset
                                Dispatcher.UIThread.Post(
                                    Sub()
                                        If token.IsCancellationRequested Then
                                            image.Dispose()
                                            Return
                                        End If
                                        deliver(preset, image)
                                    End Sub)
                            Finally
                                small?.Dispose()
                                rendered?.Dispose()
                            End Try
                        Next
                    End Sub, token)
            Catch ex As OperationCanceledException
                ' Die Liste hat gewechselt oder das Fenster ist zu.
            Catch ex As Exception
                DiagnosticLogService.LogException("EditorViewModel.RenderAdjustmentPresetThumbnails", ex)
            Finally
                _filterPreviewRenderGate.Release()
            End Try
        End Function

        ''' <summary>Verkleinert auf <see cref="PresetThumbnailMaxDimension"/>; Nothing, wenn das
        ''' Bild schon so klein ist.</summary>
        Private Shared Function ShrinkForPresetThumbnail(rendered As SKBitmap) As SKBitmap
            Dim width = rendered.Width
            Dim height = rendered.Height
            Dim longest = Math.Max(width, height)
            If longest <= PresetThumbnailMaxDimension Then Return Nothing
            Dim scale = PresetThumbnailMaxDimension / CDbl(longest)
            Dim info As New SKImageInfo(Math.Max(1, CInt(Math.Round(width * scale))), Math.Max(1, CInt(Math.Round(height * scale))),
                                        rendered.ColorType, rendered.AlphaType)
            Return rendered.Resize(info, ImageProcessor.SamplingMedium)
        End Function

    End Class

End Namespace
