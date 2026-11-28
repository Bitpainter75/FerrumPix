Imports System
Imports System.Threading
Imports Avalonia
Imports Avalonia.Media
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports Avalonia.Rendering.SceneGraph
Imports Avalonia.Skia
Imports FerrumPix.Services
Imports SkiaSharp

Namespace Controls

    ''' <summary>Die Anzeige im Reglerzug auf der Grafikkarte des Fensters (siehe
    ''' ImageProcessorGpuDisplay.vb). Der Editor legt einen Rahmen fuer seine Anzeige-Bitmap ab
    ''' (<see cref="Show"/>); solange er liegt, zeichnet <see cref="ColorManagedBitmap"/> statt der
    ''' Bitmap den Rahmen, gerechnet auf dem Renderfaden mit dem Kontext des Fensters.
    '''
    ''' <para>WARUM UEBER DIE HUELLE UND NICHT UEBER EINE EIGENE BILDQUELLE: die Quelle des
    ''' Bild-Elements ist gebunden; sie fuer die Dauer des Zugs zu tauschen hiesse, die Bindung zu
    ''' ueberschreiben und danach wiederherzustellen. Ueber die Huelle bleiben Bindung, Layout,
    ''' Zoom und Vergleichslinie, wie sie sind; nur das Zeichnen nimmt einen anderen Weg.</para>
    '''
    ''' <para>RUECKFALL: ohne Kontext des Fensters (Software-Rendering, manche Flatpak- und
    ''' VM-Umgebungen), bei durchgefallener Selbstpruefung, bei einem Fehler im Rahmen und nach
    ''' drei langsamen Rahmen in Folge zeichnet die Huelle wieder die Bitmap und meldet das dem
    ''' Editor (<see cref="Failed"/>), der den Zug auf dem bisherigen Weg weiterrechnet. Die ersten
    ''' drei Faelle schalten den Weg fuer die Sitzung aus.</para></summary>
    Public NotInheritable Class GpuDragDisplay

        Private Sub New()
        End Sub

        Private Shared _target As Bitmap
        Private Shared _frame As ImageProcessor.DisplayFrame
        Private Shared ReadOnly _residency As New ImageProcessor.DisplayResidency()
        Private Shared _checkedContext As GRContext
        Private Shared _checkPassed As Boolean
        Private Shared _slowFrames As Integer
        Private Shared _framesDrawn As Integer
        Private Shared _markerArmed As Boolean
        Private Shared _failurePosted As Integer
        Private Shared _framesRenderedTotal As Long

        ''' <summary>Wie viele Rahmen bisher auf der Karte gezeichnet wurden. Der Editor merkt sich
        ''' den Stand beim Ablegen und erkennt so einen Rahmen, den nie jemand zeichnet (etwa weil
        ''' gerade ein anderes Bild angezeigt wird); dann rechnet er auf dem bisherigen Weg.</summary>
        Friend Shared ReadOnly Property FramesRenderedTotal As Long
            Get
                Return Interlocked.Read(_framesRenderedTotal)
            End Get
        End Property

        ''' <summary>Aus fuer diese Sitzung, mit Grund (fuer Einstellungen und Protokoll).</summary>
        Public Shared ReadOnly Property SwitchedOff As Boolean
            Get
                Return Not String.IsNullOrEmpty(Volatile.Read(_offReason))
            End Get
        End Property

        Private Shared _offReason As String = ""

        Public Shared ReadOnly Property OffReason As String
            Get
                Return If(Volatile.Read(_offReason), "")
            End Get
        End Property

        ''' <summary>True, wenn der Start eine liegengebliebene Marke fand: die letzte Sitzung endete
        ''' mitten in den ersten Bildern der Kartenanzeige.</summary>
        Public Shared Property SwitchedOffAfterCrash As Boolean

        ''' <summary>Gemeldet auf dem Anzeigefaden, wenn ein Rahmen nicht auf der Karte entstand und
        ''' die Bitmap gezeichnet wurde. Der Editor rechnet den Zug dann auf dem bisherigen Weg.</summary>
        Public Shared Event Failed As EventHandler

        ''' <summary>Laeuft der Weg auf dieser Plattform? Unter Windows (ANGLE) und macOS (Metal)
        ''' ist er am Geraet nicht geprueft und bleibt aus.</summary>
        Public Shared ReadOnly Property PlatformSupported As Boolean
            Get
                Return OperatingSystem.IsLinux()
            End Get
        End Property

        ''' <summary>Beim Start: liegt die Marke noch, bleibt der Weg fuer diese Sitzung aus und
        ''' die Marke liegen, bis der Schalter einmal aus und wieder an geht.</summary>
        Public Shared Sub StartFromSettings()
            Try
                If IO.File.Exists(AppFolders.GpuDragDisplayMarkerPath) Then
                    SwitchedOffAfterCrash = True
                    SwitchOff("die letzte Sitzung endete in den ersten Bildern der Kartenanzeige")
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("GpuDragDisplay", ex)
            End Try
        End Sub

        ''' <summary>Der Schalter wurde neu eingeschaltet: Marke weg, neuer Versuch.</summary>
        Public Shared Sub ResetAfterSwitch()
            Try
                IO.File.Delete(AppFolders.GpuDragDisplayMarkerPath)
            Catch
            End Try
            SwitchedOffAfterCrash = False
            Volatile.Write(_offReason, "")
            _checkedContext = Nothing
            _checkPassed = False
            _slowFrames = 0
            _framesDrawn = 0
            _markerArmed = False
        End Sub

        Private Shared Sub SwitchOff(reason As String)
            If SwitchedOff Then Return
            Volatile.Write(_offReason, reason)
            DiagnosticLogService.LogAlways("GpuDragDisplay", "aus fuer diese Sitzung: " & reason)
        End Sub

        ''' <summary>Legt den Rahmen fuer die Anzeige-Bitmap ab; der vorige geht frei, sobald kein
        ''' Render mehr daran rechnet. Nur auf dem Anzeigefaden.</summary>
        Friend Shared Sub Show(target As Bitmap, frame As ImageProcessor.DisplayFrame)
            Dim previous As ImageProcessor.DisplayFrame
            SyncLock ImageProcessor.DisplayRenderLock
                previous = _frame
                _target = target
                _frame = frame
                Interlocked.Exchange(_failurePosted, 0)
            End SyncLock
            If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, frame) Then ImageProcessor.RetireDisplayObject(previous)
        End Sub

        ''' <summary>Die Anzeige zeigt wieder die Bitmap. Nur auf dem Anzeigefaden.</summary>
        Friend Shared Sub Clear()
            Dim previous As ImageProcessor.DisplayFrame
            SyncLock ImageProcessor.DisplayRenderLock
                previous = _frame
                _frame = Nothing
                _target = Nothing
            End SyncLock
            If previous IsNot Nothing Then ImageProcessor.RetireDisplayObject(previous)
        End Sub

        ''' <summary>Liegt ein Rahmen fuer diese Bitmap?</summary>
        Friend Shared Function IsShowing(target As Bitmap) As Boolean
            SyncLock ImageProcessor.DisplayRenderLock
                Return _frame IsNot Nothing AndAlso Object.ReferenceEquals(_target, target)
            End SyncLock
        End Function

        ''' <summary>Fuer <see cref="ColorManagedBitmap"/>: eine Zeichenoperation, wenn fuer diese
        ''' Bitmap ein Rahmen liegt; sonst Nothing.</summary>
        Friend Shared Function TryCreateOperation(bitmap As Bitmap, sourceRect As Rect, destRect As Rect,
                                                  filter As SKColorFilter) As ICustomDrawOperation
            If bitmap Is Nothing OrElse SwitchedOff Then Return Nothing
            If Not IsShowing(bitmap) Then Return Nothing
            Return New FrameOperation(bitmap, sourceRect, destRect, filter)
        End Function

        Private Shared Sub ReportFailure(reason As String, switchOffForSession As Boolean)
            If switchOffForSession Then SwitchOff(reason) Else DiagnosticLogService.LogAlways("GpuDragDisplay", "Rueckfall: " & reason)
            If Interlocked.Exchange(_failurePosted, 1) = 1 Then Return
            Avalonia.Threading.Dispatcher.UIThread.Post(Sub() RaiseEvent Failed(Nothing, EventArgs.Empty))
        End Sub

        ''' <summary>Zeitwache: so viele Rahmen in Folge ueber der Grenze schalten den Weg ab.</summary>
        Private Const SlowFrameLimit As Integer = 3
        Private Const SlowFrameMilliseconds As Double = 25.0
        ''' <summary>Nach so vielen Rahmen ohne Absturz geht die Marke weg.</summary>
        Private Const FramesUntilMarkerCleared As Integer = 30

        Private Shared Sub ArmMarker()
            If _markerArmed Then Return
            _markerArmed = True
            Try
                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(AppFolders.GpuDragDisplayMarkerPath))
                IO.File.WriteAllText(AppFolders.GpuDragDisplayMarkerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            Catch
                ' Ohne Marke fehlt nur der Schutz beim naechsten Start.
            End Try
        End Sub

        Private Shared Sub DisarmMarker()
            Try
                IO.File.Delete(AppFolders.GpuDragDisplayMarkerPath)
            Catch
            End Try
        End Sub

        ''' <summary>Zeichnet den Rahmen auf dem Renderfaden. False, wenn er nicht auf der Karte
        ''' entstand; dann zeichnet der Aufrufer die Bitmap.</summary>
        Private Shared Function RenderOnGpu(feature As ISkiaSharpApiLeaseFeature, bitmap As Bitmap, sourceRect As Rect,
                                            destRect As Rect, filter As SKColorFilter) As Boolean
            SyncLock ImageProcessor.DisplayRenderLock
                Dim frame = _frame
                If frame Is Nothing OrElse Not Object.ReferenceEquals(_target, bitmap) OrElse SwitchedOff Then Return False
                Using lease = feature.Lease()
                    Dim gr = lease.GrContext
                    If gr Is Nothing Then
                        ReportFailure("das Fenster zeichnet ohne Grafikkarte", True)
                        Return False
                    End If
                    Try
                        ArmMarker()
                        _residency.BeginRender(gr)
                        If Not Object.ReferenceEquals(_checkedContext, gr) Then
                            _checkedContext = gr
                            Dim detail As String = Nothing
                            Dim worst = PerformanceTraceService.Measure("Kartenanzeige: Selbstpruefung",
                                                                        Function() ImageProcessor.DisplaySelfCheck(gr, detail))
                            _checkPassed = worst >= 0 AndAlso worst <= ImageProcessor.DisplaySelfCheckTolerance
                            DiagnosticLogService.LogAlways("GpuDragDisplay",
                                $"Selbstpruefung {If(_checkPassed, "bestanden", "nicht bestanden")}: hoechstens {worst} Tonstufe(n) {detail}")
                            If Not _checkPassed Then
                                ReportFailure($"Selbstpruefung nicht bestanden ({worst} Tonstufen {detail})", True)
                                Return False
                            End If
                        End If
                        Dim clock = Diagnostics.Stopwatch.StartNew()
                        Dim image = ImageProcessor.RenderDisplayFrame(gr, frame, _residency)
                        Dim canvas = lease.SkCanvas
                        ' Die Quelle steht in Punkten der Anzeige-Bitmap; der Rahmen kann kleiner sein
                        ' (verkleinerte Zugquelle), also anteilig umrechnen.
                        Dim scaleX = frame.Width / Math.Max(1.0, bitmap.Size.Width)
                        Dim scaleY = frame.Height / Math.Max(1.0, bitmap.Size.Height)
                        Dim src = New SKRect(CSng(sourceRect.Left * scaleX), CSng(sourceRect.Top * scaleY),
                                             CSng(sourceRect.Right * scaleX), CSng(sourceRect.Bottom * scaleY))
                        Dim dst = New SKRect(CSng(destRect.Left), CSng(destRect.Top), CSng(destRect.Right), CSng(destRect.Bottom))
                        ' Abtastung wie Avalonia bei HighQuality (ToSKSamplingOptions): beim Vergroessern
                        ' Mitchell, beim Verkleinern linear mit Mip-Stufen. Verglichen wird in
                        ' Geraetepunkten, wie Avalonia es mit seiner Transformation tut.
                        Dim mapped = canvas.TotalMatrix.MapRect(dst)
                        Dim upscaling = mapped.Width > src.Width OrElse mapped.Height > src.Height
                        Dim sampling = If(upscaling, New SKSamplingOptions(SKCubicResampler.Mitchell),
                                          New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                        Dim alpha = CByte(Math.Max(0, Math.Min(255, Math.Round(255.0 * lease.CurrentOpacity))))
                        Using paint = New SKPaint With {.ColorFilter = filter, .IsAntialias = True,
                                                        .Color = New SKColor(255, 255, 255, alpha)}
                            canvas.DrawImage(image, src, dst, sampling, paint)
                        End Using
                        Dim elapsed = clock.Elapsed.TotalMilliseconds
                        PerformanceTraceService.Record("Kartenanzeige: Zeichnen", elapsed)
                        _slowFrames = If(elapsed > SlowFrameMilliseconds, _slowFrames + 1, 0)
                        If _slowFrames >= SlowFrameLimit Then
                            ReportFailure($"{SlowFrameLimit} Rahmen in Folge ueber {SlowFrameMilliseconds} ms", True)
                        End If
                        _framesDrawn += 1
                        Interlocked.Increment(_framesRenderedTotal)
                        If _framesDrawn = FramesUntilMarkerCleared Then DisarmMarker()
                        Return True
                    Catch ex As Exception
                        DiagnosticLogService.LogException("GpuDragDisplay", ex)
                        ReportFailure("Fehler im Rahmen: " & ex.Message, True)
                        Return False
                    End Try
                End Using
            End SyncLock
        End Function

        ''' <summary>Die Zeichenoperation fuer einen Rahmen. Faellt der Rahmen aus, zeichnet sie die
        ''' Bitmap wie <see cref="ColorManagedBitmap"/>.</summary>
        Private NotInheritable Class FrameOperation
            Implements ICustomDrawOperation

            Private ReadOnly _bitmap As Bitmap
            Private ReadOnly _sourceRect As Rect
            Private ReadOnly _destRect As Rect
            Private ReadOnly _filter As SKColorFilter

            Public Sub New(bitmap As Bitmap, sourceRect As Rect, destRect As Rect, filter As SKColorFilter)
                _bitmap = bitmap
                _sourceRect = sourceRect
                _destRect = destRect
                _filter = filter
            End Sub

            Public ReadOnly Property Bounds As Rect Implements ICustomDrawOperation.Bounds
                Get
                    Return _destRect
                End Get
            End Property

            Public Function HitTest(p As Point) As Boolean Implements ICustomDrawOperation.HitTest
                Return _destRect.Contains(p)
            End Function

            ' Wie in ColorManagedBitmap: VB.NET uebernimmt den vorgegebenen Rumpf nicht.
            Public Function HitTest(geometry As Avalonia.Media.Geometry) As Avalonia.Media.IntersectionResult Implements ICustomDrawOperation.HitTest
                Return Avalonia.Media.IntersectionResult.Empty
            End Function

            ''' <summary>NIE GLEICH: jeder neue Rahmen soll gezeichnet werden.</summary>
            Public Overloads Function Equals(other As ICustomDrawOperation) As Boolean Implements IEquatable(Of ICustomDrawOperation).Equals
                Return False
            End Function

            Public Sub Render(context As ImmediateDrawingContext) Implements ICustomDrawOperation.Render
                Dim feature = TryCast(context.TryGetFeature(GetType(ISkiaSharpApiLeaseFeature)), ISkiaSharpApiLeaseFeature)
                If feature IsNot Nothing AndAlso RenderOnGpu(feature, _bitmap, _sourceRect, _destRect, _filter) Then Return
                If feature Is Nothing Then ReportFailure("keine Zeichenflaeche von Skia", True)
                ' Rueckfall: die Bitmap, mit dem Farbfilter wie in ColorManagedBitmap.
                If feature Is Nothing OrElse _filter Is Nothing Then
                    context.DrawBitmap(_bitmap, _sourceRect, _destRect)
                    Return
                End If
                Dim saved As Integer
                Using lease = feature.Lease()
                    Using paint = New SKPaint With {.ColorFilter = _filter}
                        saved = lease.SkCanvas.SaveLayer(New SKRect(CSng(_destRect.Left), CSng(_destRect.Top),
                                                                    CSng(_destRect.Right), CSng(_destRect.Bottom)), paint)
                    End Using
                End Using
                Try
                    context.DrawBitmap(_bitmap, _sourceRect, _destRect)
                Finally
                    Using lease = feature.Lease()
                        lease.SkCanvas.RestoreToCount(saved)
                    End Using
                End Try
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
            End Sub
        End Class
    End Class

End Namespace
