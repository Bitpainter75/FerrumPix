Imports System
Imports System.Threading
Imports System.Threading.Tasks
Imports FerrumPix.Controls
Imports FerrumPix.Services
Imports SkiaSharp

Namespace ViewModels

    ''' <summary>Die Anzeige im Reglerzug auf der Karte des Fensters (Controls.GpuDragDisplay,
    ''' ImageProcessorGpuDisplay.vb): waehrend eines Zugs an Licht oder Farbe baut jeder Schritt nur
    ''' einen Rahmen, die Szene bleibt stehen. Nach dem Loslassen rechnet die Vorschau wie bisher;
    ''' erst wenn ihr Bild steht, zeigt das Fenster wieder die Szene. Histogramm, Kurvenuntergrund
    ''' und Pipette sehen bis dahin den Stand vor dem Zug.</summary>
    Partial Public Class EditorViewModel

        ''' <summary>Laeuft der Zug gerade ueber die Karte des Fensters?</summary>
        Private _gpuDragDisplayActive As Boolean
        ''' <summary>Das Ausgangsbild des Zugs (Stand nach dem Farbrauschen).</summary>
        Private _gpuDragBase As ImageProcessor.DisplayBase
        Private _gpuDragFailureHooked As Boolean
        ''' <summary>Wann der letzte Rahmen abgelegt wurde und wie viele Rahmen die Karte bis dahin
        ''' gezeichnet hatte: wird ein Rahmen nie gezeichnet, faellt der Zug zurueck.</summary>
        Private _gpuDragShownTicks As Long
        Private _gpuDragRenderedAtShow As Long
        Private _gpuDragFramesShown As Integer

        ''' <summary>Laenger als das wartet ein abgelegter Rahmen nicht auf sein Zeichnen.</summary>
        Private Const GpuDragUndrawnLimitMs As Double = 300.0

        ''' <summary>Gerufen aus <see cref="BeginSliderPreviewDrag"/>. Nur fuer Regler, die je
        ''' Bildpunkt rechnen; massstabsabhaengige Regler bleiben beim bisherigen Weg.</summary>
        Private Sub BeginGpuDragDisplay(scaleSensitive As Boolean)
            If scaleSensitive OrElse _gpuDragDisplayActive Then Return
            If Not GpuDragDisplayAllowedNow() Then Return
            If Not _gpuDragFailureHooked Then
                _gpuDragFailureHooked = True
                AddHandler GpuDragDisplay.Failed, AddressOf OnGpuDragDisplayFailed
            End If
            _gpuDragDisplayActive = True
            _gpuDragFramesShown = 0
        End Sub

        ''' <summary>Darf die Karte jetzt zeichnen? Alles, was die Szene nach dem Blit noch
        ''' veraendert (Anschlagswarnung, Kompositor-Objekte, Werkzeugvorschau, Retusche-Bruecke,
        ''' Zoom-Detail), laesst den Zug auf dem bisherigen Weg.</summary>
        Private Function GpuDragDisplayAllowedNow() As Boolean
            If Not GpuDragDisplay.PlatformSupported OrElse GpuDragDisplay.SwitchedOff Then Return False
            If Not AppSettingsService.Load().GpuDragDisplay Then Return False
            If _sceneDisplay Is Nothing OrElse Not Object.ReferenceEquals(_previewImage, _sceneDisplay) Then Return False
            If ShowClippingWarning OrElse HasRetouchLivePatch OrElse HasPreview OrElse _zoomDetailWanted Then Return False
            If _annotationPlacementEditActive OrElse IsObjectAdjustModeActive() OrElse HasCompositorObjects() Then Return False
            Return True
        End Function

        ''' <summary>Gerufen aus <see cref="EndSliderPreviewDrag"/>: kein neuer Rahmen mehr, der
        ''' letzte bleibt stehen, bis die Szene des Nachzugs gelandet ist.</summary>
        Private Sub EndGpuDragDisplay()
            _gpuDragDisplayActive = False
        End Sub

        ''' <summary>Eine neue Szene ist in der Anzeige: ab jetzt zeigt das Fenster wieder sie.
        ''' Gerufen am Ende von SetSceneBitmapCore, nach dem Blit.</summary>
        Private Sub ClearGpuDragDisplayForScene()
            GpuDragDisplay.Clear()
            If Not _gpuDragDisplayActive Then ReleaseGpuDragBase()
        End Sub

        Private Sub ReleaseGpuDragBase()
            Dim base = _gpuDragBase
            _gpuDragBase = Nothing
            ImageProcessor.RetireDisplayObject(base)
        End Sub

        ''' <summary>Zurueck auf den bisherigen Weg, mitten im Zug. Der zuletzt abgelegte Rahmen
        ''' bleibt stehen, bis die naechste Szene landet; die Aenderung bleibt angemeldet.</summary>
        Private Sub StopGpuDragDisplay(reason As String)
            If Not _gpuDragDisplayActive Then Return
            _gpuDragDisplayActive = False
            DiagnosticLogService.LogAlways("GpuDragDisplay", "Zug auf dem bisherigen Weg: " & reason)
            _previewPending = True
        End Sub

        Private Sub OnGpuDragDisplayFailed(sender As Object, e As EventArgs)
            ' Der Rahmen ist nicht auf der Karte entstanden; in der Anzeige steht die alte Szene.
            If GpuDragDisplay.IsShowing(_sceneDisplay) Then GpuDragDisplay.Clear()
            Dim wasActive = _gpuDragDisplayActive
            StopGpuDragDisplay("Rahmen auf der Karte gescheitert: " & GpuDragDisplay.OffReason)
            If wasActive OrElse _sliderDragActive Then
                MarkPreviewPending()
                RestartPreviewTimer(1.0)
            End If
        End Sub

        ''' <summary>Der Ersatz fuer einen Vorschaulauf waehrend eines Zugs auf der Karte. False,
        ''' wenn der Zug (nicht mehr) ueber die Karte laeuft; dann rechnet der Aufrufer wie bisher.
        ''' True, wenn dieser Lauf erledigt ist, auch wenn er auf den bisherigen Weg zurueckfiel:
        ''' die Kette der Laeufe zieht die Aenderung dann nach (RegisterPreviewRenderEnd).</summary>
        Private Async Function TryRenderGpuDragFrameAsync(previewSource As SKBitmap) As Task(Of Boolean)
            If Not _gpuDragDisplayActive Then Return False
            If Not _sliderDragActive OrElse previewSource Is Nothing OrElse Not GpuDragDisplayAllowedNow() Then
                StopGpuDragDisplay("Zustand des Editors erlaubt die Karte nicht mehr")
                Return False
            End If
            If _gpuDragFramesShown > 0 AndAlso GpuDragDisplay.FramesRenderedTotal = _gpuDragRenderedAtShow AndAlso
               (Diagnostics.Stopwatch.GetTimestamp() - _gpuDragShownTicks) * 1000.0 / Diagnostics.Stopwatch.Frequency > GpuDragUndrawnLimitMs Then
                GpuDragDisplay.Clear()
                StopGpuDragDisplay("Rahmen wurde nicht gezeichnet")
                Return False
            End If

            RegisterPreviewRenderStart()
            Dim requestId = _previewRequestId
            Dim changeIdAtStart = Volatile.Read(_previewChangeId)
            Dim adj = GetSceneAdjustments()
            Dim currentBase = _gpuDragBase
            Dim firstFrame = _gpuDragFramesShown = 0
            Dim clock = Diagnostics.Stopwatch.StartNew()
            Try
                Dim built = Await Task.Run(Function() BuildGpuDragFrame(currentBase, previewSource, adj, firstFrame))
                If built.Frame Is Nothing Then
                    If built.Base IsNot Nothing AndAlso Not Object.ReferenceEquals(built.Base, currentBase) Then ImageProcessor.RetireDisplayObject(built.Base)
                    StopGpuDragDisplay(built.Reason)
                    Return True
                End If
                If requestId <> _previewRequestId OrElse Not _gpuDragDisplayActive OrElse _sceneDisplay Is Nothing Then
                    ImageProcessor.RetireDisplayObject(built.Frame)
                    If Not Object.ReferenceEquals(built.Base, currentBase) Then ImageProcessor.RetireDisplayObject(built.Base)
                    Return True
                End If
                GpuDragDisplay.Show(_sceneDisplay, built.Frame)
                If Not Object.ReferenceEquals(built.Base, _gpuDragBase) Then
                    ' Das alte Ausgangsbild haengt an keinem abgelegten Rahmen mehr.
                    Dim previousBase = _gpuDragBase
                    _gpuDragBase = built.Base
                    ImageProcessor.RetireDisplayObject(previousBase)
                End If
                _gpuDragFramesShown += 1
                _gpuDragShownTicks = Diagnostics.Stopwatch.GetTimestamp()
                _gpuDragRenderedAtShow = GpuDragDisplay.FramesRenderedTotal
                RaiseEvent SceneInvalidated(Me, EventArgs.Empty)
                If Volatile.Read(_previewChangeId) = changeIdAtStart Then _previewPending = False
                PerformanceTraceService.Record("Kartenanzeige: Rahmen bis Ablage", clock.Elapsed.TotalMilliseconds)
                If _previewPendingSinceTicks <> 0 Then
                    ' Gezeichnet wird mit dem naechsten Bild des Fensters; bis dahin vergeht hoechstens
                    ' ein Bildwechsel, der hier nicht mitzaehlt.
                    PerformanceTraceService.Record("Regler bis Bild",
                        (Diagnostics.Stopwatch.GetTimestamp() - _previewPendingSinceTicks) * 1000.0 / Diagnostics.Stopwatch.Frequency)
                    _previewPendingSinceTicks = If(_previewPending, Diagnostics.Stopwatch.GetTimestamp() - clock.ElapsedTicks, 0L)
                End If
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("GpuDragDisplay", ex)
                StopGpuDragDisplay("Fehler beim Bau des Rahmens: " & ex.Message)
                Return True
            Finally
                RegisterPreviewRenderEnd()
                ' Nach einem Rueckfall ohne laufenden Zug zieht RegisterPreviewRenderEnd nicht nach.
                If Not _gpuDragDisplayActive AndAlso _previewPending AndAlso Not _sliderDragActive Then RestartPreviewTimer(1.0)
            End Try
        End Function

        ''' <summary>Im Hintergrund: Ausgangsbild pruefen oder neu bauen, dann den Rahmen. Ein neues
        ''' Ausgangsbild gibt es nur beim ersten Rahmen oder wenn die Quelle wechselt (verkleinerte
        ''' Zugquelle fertig, Nachrechnen in voller Groesse); aendert sich mitten im Zug das Rezept
        ''' vor der Kette, faellt der Zug zurueck.</summary>
        Private Shared Function BuildGpuDragFrame(currentBase As ImageProcessor.DisplayBase, source As SKBitmap,
                                                  adj As ImageAdjustments, firstFrame As Boolean) _
                                                  As (Frame As ImageProcessor.DisplayFrame, Base As ImageProcessor.DisplayBase, Reason As String)
            Dim base = currentBase
            If Not ImageProcessor.DisplayBaseMatches(base, source, adj) Then
                Dim recipeChanged = base IsNot Nothing AndAlso Not firstFrame AndAlso
                                    Not String.Equals(base.RecipeKey, ImageProcessor.DisplayBaseRecipeKey(adj), StringComparison.Ordinal)
                If recipeChanged Then Return (Nothing, Nothing, "Rezept vor der Kette geaendert")
                base = PerformanceTraceService.Measure("Kartenanzeige: Basis vorbereiten",
                                                       Function() ImageProcessor.PrepareDisplayBase(source, adj))
                If base Is Nothing Then Return (Nothing, Nothing, "Ausgangsbild nicht anlegbar oder Rezept ungeeignet: " &
                                                ImageProcessor.DisplayFrameEligibility(adj).Reason)
            End If
            Dim reason As String = Nothing
            Dim frame = PerformanceTraceService.Measure("Kartenanzeige: Rahmen bauen",
                                                        Function() ImageProcessor.BuildDisplayFrame(base, source, adj, True, reason))
            Return (frame, base, reason)
        End Function

    End Class

End Namespace
