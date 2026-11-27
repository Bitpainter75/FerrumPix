Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.Linq
Imports System.Net.WebSockets
Imports System.Numerics
Imports System.Reflection
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Headless
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports SkiaSharp

Namespace Services

    ' Die Bilder der Fenster: abholen, mit dem zuletzt gesendeten Stand vergleichen, die geaenderten
    ' Ausschnitte kodieren und schicken.
    '
    ' VERLUSTBEHAFTET IN DER BEWEGUNG, VERLUSTFREI IM STILLSTAND. Ein grosser Ausschnitt geht als
    ' JPEG hinaus, damit Reglerzuege fluessig bleiben. Steht das Fenster danach kurz still, folgt
    ' derselbe Bereich noch einmal als PNG. Was im Browser steht, ist dann bitgleich das, was die
    ' Anwendung gezeichnet hat - fuer eine Bildbearbeitung ist das der Punkt des Ganzen.
    Partial Public NotInheritable Class BrowserBridge

        ''' <summary>Der Takt: so lange liegt zwischen zwei gezeichneten Bildern mindestens, also
        ''' hoechstens rund sechzig Bilder je Sekunde. Gezeichnet und abgeholt wird im selben
        ''' Durchgang, siehe "Der Takt".</summary>
        Private Const FrameIntervalMilliseconds As Integer = 16

        ''' <summary>So lange muss ein Fenster stillstehen, bevor der verlustfreie Nachzug kommt.</summary>
        Private Const LosslessDelayMilliseconds As Long = 300

        ''' <summary>Bis zu dieser Flaeche geht ein Ausschnitt gleich verlustfrei hinaus: ein
        ''' blinkender Textzeiger oder ein Knopf unter der Maus ist als PNG kleiner als als JPEG
        ''' und braucht dann keinen Nachzug.</summary>
        Private Const DirectLosslessPixels As Integer = 96 * 96

        Private Const JpegQuality As Integer = 85

        Private Const HeaderLength As Integer = 22

        ''' <summary>Kantenlaenge der Kacheln, in denen Unterschiede gesucht werden.</summary>
        Private Const TileSize As Integer = 64

        ''' <summary>Mehr Rechtecke als das lohnen den Aufwand je Nachricht nicht mehr; dann geht
        ''' das eine Rechteck hinaus, das alle umschliesst.</summary>
        Private Const MaxRects As Integer = 24

        ''' <summary>Ein Ausschnitt wird in Streifen von etwa dieser Flaeche zerlegt, die
        ''' nebeneinander kodiert werden. Ein ganzes Fenster belegt sonst einen Kern fuer die volle
        ''' Dauer, waehrend die uebrigen warten.</summary>
        Private Const StripPixels As Integer = 256 * 1024

        Private NotInheritable Class BridgeWindow
            Public Id As Integer
            ''' <summary>Der Stand, den der Browser hat.</summary>
            Public Previous As Integer()
            Public PixelWidth As Integer
            Public PixelHeight As Integer
            ''' <summary>Das Bildobjekt der Plattform, das zuletzt verglichen wurde. Ist es noch
            ''' dasselbe, hat die Anwendung nichts neu gezeichnet.</summary>
            Public LastFrame As Object
            Public HasLossy As Boolean
            Public LossyLeft As Integer
            Public LossyTop As Integer
            Public LossyRight As Integer
            Public LossyBottom As Integer
            Public LastChange As Long
            Public ActivationOrder As Integer
            ''' <summary>Reihenfolge der Farbkanaele im Bild der Plattform. Sie haengt davon ab, wie
            ''' das Toolkit aufgesetzt wurde, und wird deshalb je Bild abgelesen statt angenommen.</summary>
            Public ColorType As SKColorType = SKColorType.Bgra8888
        End Class

        Private NotInheritable Class FrameJob
            Public WindowId As Integer
            Public X As Integer
            Public Y As Integer
            Public Width As Integer
            Public Height As Integer
            Public Pixels As Integer()
            Public Lossless As Boolean
            Public ColorType As SKColorType
        End Class

        Private Shared ReadOnly States As New Dictionary(Of Window, BridgeWindow)()
        Private Shared _nextWindowId As Integer
        Private Shared _activationCounter As Integer
        Private Shared _lastLayout As String
        Private Shared _sending As Integer
        Private Shared _scale As Double = 1.0
        Private Shared _lastFrameField As FieldInfo
        Private Shared _lastFrameFieldSearched As Boolean

        ' ------------------------------------------------------------------------------------
        ' Der Takt: zeichnen und abholen in EINEM Durchgang.
        '
        ' GEMESSEN (Anzeigefadenmessung, Release, Tab mit 5,4 Megapixeln): beim Ziehen eines
        ' Reglers zeichnete die Plattform rund 50 Bilder je Sekunde zu je 19 ms, der Faden war zu
        ' 90 bis 95 % belegt. Jede Mausbewegung stiess ein Bild an, dazu lief der eigene Takt der
        ' Plattform mit 60 Hz, und die Abholung kam alle 16 ms; Bilder dazwischen sah niemand. Im
        ' Leerlauf kosteten die Zeitgeber 10 bis 12 % eines Kerns: die Ereignisschleife des
        ' Toolkits wartet nicht, wenn ein Zeitgeber in weniger als einer Millisekunde faellig
        ' ist, sondern dreht leer (ManagedDispatcherImpl.RunLoop).
        '
        ' Deshalb gilt:
        '
        ' 1. DIE BRUECKE IST DER ZEICHENTAKT. Die Zeichenschleife des Toolkits bekommt statt des
        '    Takts der Plattform den der Bruecke (BridgeRenderTimer). Sie meldet sich damit, wenn
        '    es etwas zu zeichnen gibt, und gezeichnet wird nur noch in einem Durchgang der
        '    Bruecke, unmittelbar vor dem Abholen. Ein Bild ohne Abnehmer entsteht so nicht.
        '
        ' 2. HOECHSTENS EIN BILD JE TAKT. Ist seit dem letzten Bild ein Takt vergangen, wird sofort
        '    gezeichnet und abgeholt (nach einer Pause kommt die Bewegung ohne Wartezeit an, siehe
        '    RequestFrameNow); sonst genau einmal zum naechsten Takt.
        '
        ' 3. EIN ZEITGEBER, UND ER STEHT, WENN NICHTS ZU TUN IST: ohne verbundenen Browser, ohne
        '    Wunsch der Zeichenschleife und ohne faelligen verlustfreien Nachzug. Er laeuft auf
        '    einem Faden des Pools und stellt den Durchgang nur in die Schlange; die
        '    Ereignisschleife bekommt damit keinen Zeitgeber, vor dem sie leer drehen koennte.
        '
        ' Fehlt die Zeichenschleife des Toolkits in der erwarteten Form (neue Fassung), bleibt
        ' ihr eigener Takt, und die Bruecke laeuft wie ein gewoehnlicher Abholtakt alle 16 ms,
        ' solange ein Browser verbunden ist: langsamer, aber richtig.
        ' ------------------------------------------------------------------------------------

        ''' <summary>Der Zeichentakt der Bruecke an Stelle des Takts der Plattform. Die
        ''' Zeichenschleife setzt <c>Tick</c>, wenn sie etwas zu zeichnen hat, und nimmt ihn weg,
        ''' wenn nicht; getickt wird nur im Durchgang der Bruecke.
        '''
        ''' <para>Die Schnittstelle (<c>IRenderTimer</c>) fuehrt das Toolkit als PrivateApi: beim
        ''' Bauen sind ihre Glieder nicht sichtbar und sie laesst sich nicht umsetzen. Die Klasse
        ''' entsteht deshalb erst zur Laufzeit als Stellvertreter (<see cref="DispatchProxy"/>),
        ''' wie die Eingaben ueber Reflexion gehen.</para></summary>
        Friend Class BridgeRenderTimer
            Inherits DispatchProxy

            Private ReadOnly _clock As Stopwatch = Stopwatch.StartNew()
            Private _tick As Action(Of TimeSpan)

            ''' <summary>Der Takt der Plattform und sein Feld fuer den erzwungenen Takt
            ''' (<c>AvaloniaHeadlessPlatform.ForceRenderTimerTick</c>). Solange die Schleife wach
            ''' ist, zeigt es auf <see cref="Fire"/>: wer ein Bild erzwingt (die Hilfen der
            ''' Plattform fuer Tests tun das bei jeder Eingabe), bekommt es weiter.</summary>
            Public PlatformTimer As Object
            Public PlatformForceTick As FieldInfo

            ''' <summary>Ob die Zeichenschleife wach ist, also etwas zeichnen will.</summary>
            Public ReadOnly Property IsAwake As Boolean
                Get
                    Return Volatile.Read(_tick) IsNot Nothing
                End Get
            End Property

            Protected Overrides Function Invoke(targetMethod As MethodInfo, args As Object()) As Object
                Select Case targetMethod?.Name
                    Case "get_Tick"
                        Return Volatile.Read(_tick)
                    Case "set_Tick"
                        Dim value = TryCast(args(0), Action(Of TimeSpan))
                        Volatile.Write(_tick, value)
                        PlatformForceTick?.SetValue(PlatformTimer, If(value Is Nothing, Nothing, New Action(AddressOf Fire)))
                        ' Die Schleife ist aufgewacht: es gibt etwas zu zeichnen.
                        If value IsNot Nothing Then SchedulePass()
                        Return Nothing
                    Case "get_RunsInBackground"
                        Return False
                End Select
                Return Nothing
            End Function

            ''' <summary>Laesst die Zeichenschleife einmal laufen, wenn sie wach ist. Nur auf dem
            ''' Anzeigefaden.</summary>
            Public Sub Fire()
                Volatile.Read(_tick)?.Invoke(_clock.Elapsed)
            End Sub
        End Class

        Private Shared ReadOnly PassLock As New Object()
        Private Shared _passClock As System.Threading.Timer
        Private Shared _pumpActive As Boolean
        Private Shared _renderTimer As BridgeRenderTimer
        Private Shared _originalRenderTimer As Object
        Private Shared _renderLoop As Object
        Private Shared _renderLoopTimerField As FieldInfo
        Private Shared _renderTimerTick As PropertyInfo
        ''' <summary>Wann zuletzt ein Bild gezeichnet wurde (Stopwatch); Grundlage der Regel
        ''' "hoechstens ein Bild je Takt".</summary>
        Private Shared _lastFrameTicks As Long
        ''' <summary>Wann der Zeitgeber faellig ist (Stopwatch), 0 wenn er steht.</summary>
        Private Shared _clockDueTicks As Long
        Private Shared _passPosted As Integer
        ''' <summary>Ein Durchgang fiel aus, weil der vorige Schwung noch unterwegs war; ist er
        ''' hinaus, kommt der naechste gleich.</summary>
        Private Shared _passAfterSend As Integer
        Private Shared _passCount As Long

        Private Shared ReadOnly FrameIntervalTicks As Long = Stopwatch.Frequency * FrameIntervalMilliseconds \ 1000

        ''' <summary>Der lange Takt ohne Browser, solange die Zeichenschleife etwas zeichnen will.
        ''' Ganz anhalten geht dort nicht: das Toolkit gibt Freigaben erst mit dem naechsten
        ''' gezeichneten Bild weiter und rechnet dabei auch die Lage der Elemente fuer die
        ''' Trefferpruefung nach. Laeuft im Behaelter ohne offenen Tab etwa der Katalog durch,
        ''' blieben sonst Bilder im Speicher liegen. Zwei Bilder je Sekunde kosten fast nichts.</summary>
        Private Const DetachedIntervalMilliseconds As Integer = 500

        Private Shared ReadOnly DetachedIntervalTicks As Long = Stopwatch.Frequency * DetachedIntervalMilliseconds \ 1000

        ''' <summary>Wie viele Durchgaenge (zeichnen und abholen) bisher liefen. Fuer die Zeile im
        ''' Diagnoseprotokoll und den Pruefstand.</summary>
        Friend Shared ReadOnly Property PassCount As Long
            Get
                Return Interlocked.Read(_passCount)
            End Get
        End Property

        Private Shared Sub StartFramePump()
            If _pumpActive Then Return
            _pumpActive = True
            _passClock = New System.Threading.Timer(AddressOf OnPassClock, Nothing, Timeout.Infinite, Timeout.Infinite)
            InstallRenderTimer()
            SchedulePass()
        End Sub

        ''' <summary>Setzt den Zeichentakt der Bruecke in die Zeichenschleife des Toolkits. Die
        ''' Schleife haelt ihren Takt in einem internen Feld; ist sie gerade wach, wandert ihr
        ''' Auftrag mit.</summary>
        Private Shared Sub InstallRenderTimer()
            Try
                ' Die Schleife, an der der Compositor der Plattform haengt; beide sind intern.
                Dim timerType = GetType(AvaloniaLocator).Assembly.GetType("Avalonia.Rendering.IRenderTimer")
                Dim compositor = GetType(AvaloniaHeadlessPlatform).GetProperty("Compositor", BindingFlags.Static Or BindingFlags.NonPublic)?.GetValue(Nothing)
                Dim renderLoop = compositor?.GetType().GetProperty("Loop", BindingFlags.Instance Or BindingFlags.NonPublic Or BindingFlags.Public)?.GetValue(compositor)
                Dim field = renderLoop?.GetType().GetField("_timer", BindingFlags.Instance Or BindingFlags.NonPublic)
                Dim tick = timerType?.GetProperty("Tick")
                Dim original = field?.GetValue(renderLoop)
                If original Is Nothing OrElse tick Is Nothing OrElse Not timerType.IsAssignableFrom(field.FieldType) OrElse
                   TypeOf original Is BridgeRenderTimer Then
                    DiagnosticLogService.LogAlways("BrowserBridge.Frame", "Zeichenschleife nicht erreichbar, es bleibt beim Takt der Plattform")
                    Return
                End If
                Dim replacement = DirectCast(DispatchProxy.Create(timerType, GetType(BridgeRenderTimer)), BridgeRenderTimer)
                Dim running = tick.GetValue(original)
                If running IsNot Nothing Then tick.SetValue(original, Nothing)
                Dim forceTick = original.GetType().GetField("_forceTick", BindingFlags.Instance Or BindingFlags.NonPublic)
                If forceTick IsNot Nothing AndAlso forceTick.FieldType Is GetType(Action) Then
                    replacement.PlatformTimer = original
                    replacement.PlatformForceTick = forceTick
                End If
                field.SetValue(renderLoop, replacement)
                _renderLoop = renderLoop
                _renderLoopTimerField = field
                _renderTimerTick = tick
                _originalRenderTimer = original
                _renderTimer = replacement
                If running IsNot Nothing Then tick.SetValue(replacement, running)
            Catch ex As Exception When TypeOf ex Is FieldAccessException OrElse TypeOf ex Is ArgumentException OrElse
                                       TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TargetInvocationException
                DiagnosticLogService.LogException("BrowserBridge.Frame", If(ex.InnerException, ex))
                _renderTimer = Nothing
            End Try
        End Sub

        ''' <summary>Gibt der Zeichenschleife ihren eigenen Takt zurueck (Anhalten der Bruecke im
        ''' Pruefstand).</summary>
        Private Shared Sub RestoreRenderTimer()
            Dim replacement = _renderTimer
            _renderTimer = Nothing
            If replacement Is Nothing OrElse _renderLoopTimerField Is Nothing Then Return
            Dim running = _renderTimerTick.GetValue(replacement)
            replacement.PlatformForceTick?.SetValue(replacement.PlatformTimer, Nothing)
            replacement.PlatformForceTick = Nothing
            _renderLoopTimerField.SetValue(_renderLoop, _originalRenderTimer)
            If running IsNot Nothing Then _renderTimerTick.SetValue(_originalRenderTimer, running)
            _renderLoop = Nothing
            _renderLoopTimerField = Nothing
            _originalRenderTimer = Nothing
        End Sub

        ''' <summary>Sorgt fuer einen Durchgang: sofort, wenn seit dem letzten ein Takt vergangen
        ''' ist, sonst zum naechsten Takt. Von jedem Faden aus. Ohne Browser nur im langen Takt
        ''' (<see cref="DetachedIntervalMilliseconds"/>); beim Verbinden kommt der Durchgang sofort.</summary>
        Private Shared Sub SchedulePass()
            If Not _pumpActive Then Return
            If CurrentClient Is Nothing Then
                If _renderTimer IsNot Nothing AndAlso _renderTimer.IsAwake Then ArmPassClock(DetachedIntervalTicks)
                Return
            End If
            Dim wait = Interlocked.Read(_lastFrameTicks) + FrameIntervalTicks - Stopwatch.GetTimestamp()
            If wait <= 0 Then
                PostPass()
            Else
                ArmPassClock(wait)
            End If
        End Sub

        ''' <summary>Stellt den Zeitgeber, falls er nicht schon frueher faellig ist.</summary>
        Private Shared Sub ArmPassClock(waitTicks As Long)
            Dim due = Stopwatch.GetTimestamp() + waitTicks
            SyncLock PassLock
                If _passClock Is Nothing Then Return
                If _clockDueTicks <> 0 AndAlso _clockDueTicks <= due Then Return
                _clockDueTicks = due
                ' Aufgerundet: ein Durchgang vor dem Takt wuerde ein zweites Bild in denselben
                ' Takt legen.
                Dim milliseconds = CLng(Math.Ceiling(waitTicks * 1000.0 / Stopwatch.Frequency))
                _passClock.Change(Math.Max(1L, milliseconds), Timeout.Infinite)
            End SyncLock
        End Sub

        Private Shared Sub OnPassClock(state As Object)
            SyncLock PassLock
                _clockDueTicks = 0
            End SyncLock
            PostPass()
        End Sub

        ''' <summary>LOADED UND NICHT BACKGROUND: der Durchgang muss vor den Eingaben an die Reihe
        ''' kommen. Beim Ziehen eines Reglers steht staendig eine Mausbewegung in der Schlange; mit
        ''' niedrigerer Prioritaet kaeme er erst dran, wenn die Maus ruht. Und unter Anordnen und
        ''' Zeichnen der Oberflaeche: was eine Eingabe dort angestossen hat, laeuft vorher.</summary>
        Private Shared Sub PostPass()
            If Interlocked.Exchange(_passPosted, 1) <> 0 Then Return
            Dispatcher.UIThread.Post(AddressOf RunPass, DispatcherPriority.Loaded)
        End Sub

        ''' <summary>Ein Durchgang: zeichnen, was ansteht, abholen, und entscheiden, ob der
        ''' Zeitgeber weiterlaeuft.</summary>
        Private Shared Sub RunPass()
            Interlocked.Exchange(_passPosted, 0)
            If Not _pumpActive Then Return
            If CurrentClient Is Nothing Then
                ' Ohne Browser nur die Zeichenschleife, im langen Takt: niemand holt ab.
                If _renderTimer Is Nothing Then Return
                DrawFrame()
                ScheduleFollowingPass()
                Return
            End If
            ' Solange der letzte Schwung noch kodiert oder gesendet wird, wird nicht gezeichnet:
            ' das Bild haette keinen Abnehmer. Ist er hinaus, kommt der Durchgang (SendFramesAsync).
            ' Erst die Marke, dann die Frage: endet der Schwung dazwischen, sieht er die Marke.
            Interlocked.Exchange(_passAfterSend, 1)
            If Interlocked.CompareExchange(_sending, 0, 0) = 0 Then
                Interlocked.Exchange(_passAfterSend, 0)
            Else
                _skippedTicks += 1
                ' Eine wartende Mausbewegung bleibt gesperrt, bis ihr Vorgaenger abgeholt ist;
                ' der Anstoss hier laesst nur ihre Frist greifen.
                KickInput()
                Return
            End If
            DrawFrame()
            CollectAndSend()
            ScheduleFollowingPass()
        End Sub

        ''' <summary>Laesst die Zeichenschleife laufen. Der Takt zaehlt ab dem letzten BILD, nicht
        ''' ab dem letzten Durchgang: bleibt die Schleife nach einer Aenderung noch eine Weile wach
        ''' (das Toolkit tut das zehn Takte lang) oder laeuft eine Animation ohne neues Bild, darf
        ''' eine Bewegung danach trotzdem sofort ein Bild bekommen. Gemessen: mit dem Durchgang als
        ''' Mass kam das Bild einer einzelnen Bewegung nach einer Pause im Mittel 8 bis 10 ms
        ''' spaeter als vorher.</summary>
        Private Shared Sub DrawFrame()
            Dim started = Stopwatch.GetTimestamp()
            Interlocked.Increment(_passCount)
            Dim windows = VisibleWindows()
            Dim before = windows.Select(Function(w) CurrentFrameMarker(w)).ToList()
            Try
                If _renderTimer IsNot Nothing Then
                    _renderTimer.Fire()
                Else
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick()
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Frame", ex)
            End Try
            ' Ohne erreichbares Feld der Plattform gilt jeder Durchgang als Bild.
            Dim drew = windows.Count = 0
            For index = 0 To windows.Count - 1
                If before(index) Is Nothing OrElse CurrentFrameMarker(windows(index)) IsNot before(index) Then
                    drew = True
                    Exit For
                End If
            Next
            If drew Then Interlocked.Exchange(_lastFrameTicks, started)
        End Sub

        ''' <summary>Ob und wann der naechste Durchgang kommt, ohne dass jemand ihn anstoesst: im
        ''' naechsten Takt, solange die Zeichenschleife wach ist; zum verlustfreien Nachzug, wenn
        ''' einer aussteht; sonst gar nicht.</summary>
        Private Shared Sub ScheduleFollowingPass()
            If CurrentClient Is Nothing Then
                SchedulePass()
                Return
            End If
            If _renderTimer Is Nothing OrElse _renderTimer.IsAwake Then
                ArmPassClock(FrameIntervalTicks)
                Return
            End If
            Dim nextLossless = Long.MaxValue
            For Each state In States.Values
                If state.HasLossy Then nextLossless = Math.Min(nextLossless, state.LastChange + LosslessDelayMilliseconds)
            Next
            If nextLossless = Long.MaxValue Then Return
            Dim waitMilliseconds = Math.Max(FrameIntervalMilliseconds, nextLossless - Environment.TickCount64)
            ArmPassClock(waitMilliseconds * Stopwatch.Frequency \ 1000)
        End Sub

        ' ------------------------------------------------------------------------------------
        ' Die Glaettung beim Zeichnen von Bildern.
        '
        ' IM BROWSER ZEICHNET DER PROZESSOR, nicht die Grafikkarte, und dort kostet die Glaettung
        ' etwas ganz anderes. Gemessen an einem Fenster von 5,4 Megapixeln, je Bild der
        ' Oberflaeche: ein Bild in Anzeigegroesse 44 ms, gleich mit welcher Stufe. Ein GROESSERES
        ' Bild verkleinert: ohne oder mit einfacher Glaettung 52 bis 59 ms, mit mittlerer oder
        ' hoher rund 650 ms. Editor und Betrachter verlangen die hohe, und der Anzeigefaden stand
        ' damit je Bild 200 bis 300 ms: beim Malen kamen drei bis neun Bilder in der Sekunde an
        ' (Nutzerbefund, im Diagnoseprotokoll als "Anzeigefaden blockiert" ohne offenen Messpunkt).
        '
        ' Deshalb gilt im Browser hoechstens die einfache Stufe. Der Preis: stark verkleinerte
        ' Bilder sind etwas weniger sauber gezeichnet. Die Bildrechnung selbst, das Speichern und
        ' der Export sind nicht betroffen, es geht nur um das Zeichnen auf den Schirm.
        '
        ' Die Angabe steht in der Oberflaeche an den einzelnen Elementen. Beim Laden eines Elements
        ' wird sie begrenzt; ein Fenster bekommt die einfache Stufe selbst, damit auch alles ohne
        ' eigene Angabe sie erbt.
        ' ------------------------------------------------------------------------------------
        Private Shared _limitSmoothing As Boolean

        Private Shared Sub LimitImageSmoothing()
            If _limitSmoothing Then Return
            _limitSmoothing = True
            Control.LoadedEvent.AddClassHandler(Of Control)(Sub(control, e) ClampSmoothing(control, includeUnspecified:=False))
        End Sub

        Private Shared Sub ClampSmoothing(target As Visual, includeUnspecified As Boolean)
            Dim mode = Avalonia.Media.RenderOptions.GetBitmapInterpolationMode(target)
            If mode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality OrElse
               mode = Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality OrElse
               (includeUnspecified AndAlso mode = Avalonia.Media.Imaging.BitmapInterpolationMode.Unspecified) Then
                Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(target, Avalonia.Media.Imaging.BitmapInterpolationMode.LowQuality)
            End If
        End Sub

        ''' <summary>Nach einer Eingabe: zeichnen und abholen, ohne auf einen festen Takt zu warten.
        '''
        ''' Mit einem festen Takt vergingen bis zu 16 Millisekunden, bis die Anwendung zeichnet,
        ''' und mit getrennten Takten fuer Zeichnen und Abholen noch einmal so viel. Beim Malen ist
        ''' das die Zeit, um die der Pinselkreis der Maus nachlaeuft: er ist ein Element der Szene
        ''' und kommt nur ueber das Bild im Browser an. Nach einer Pause kommt der Durchgang
        ''' deshalb sofort; nur wenn das letzte Bild juenger als ein Takt ist, wartet er bis
        ''' dahin (SchedulePass).
        '''
        ''' Als eigene Aufgabe mit der Prioritaet der Abholung, nicht sofort: was die Eingabe an
        ''' Anordnung und Zeichnen angestossen hat, steht darueber und laeuft vorher.</summary>
        Private Shared Sub RequestFrameNow()
            SchedulePass()
        End Sub

        Private Shared Sub StopFramePump()
            If Not _pumpActive Then Return
            _pumpActive = False
            SyncLock PassLock
                _passClock?.Dispose()
                _passClock = Nothing
                _clockDueTicks = 0
            End SyncLock
            RestoreRenderTimer()
            States.Clear()
            _lastLayout = Nothing
            _pointerOwner = Nothing
            ClearInput()
        End Sub

        ''' <summary>Ein Browser hat sich verbunden: seine Flaeche und sein Pixelverhaeltnis
        ''' uebernehmen und alles neu schicken, er hat noch nichts.</summary>
        Private Shared Sub OnClientConnected(hello As JsonElement)
            For Each state In States.Values
                state.Previous = Nothing
                state.LastFrame = Nothing
                state.HasLossy = False
            Next
            _lastLayout = Nothing
            _lastCursor = Nothing
            ' Ein Druck aus dem abgeloesten Browser bekommt sein Loslassen nicht mehr, und dessen
            ' Dateiwaehler meldet kein Ende mehr.
            CancelPointer()
            FilePickerClosed()
            ApplyViewport(hello)
            ' Ohne Browser stand der Takt; was seitdem gezeichnet werden will, kommt jetzt.
            SchedulePass()
        End Sub

        ''' <summary>Das Hauptfenster fuellt den Tab, und gezeichnet wird in dessen Pixelverhaeltnis:
        ''' auf einem hochaufloesenden Schirm ist das Bild damit scharf statt hochgerechnet.</summary>
        Private Shared Sub ApplyViewport(message As JsonElement)
            Dim width = Math.Clamp(ReadNumber(message, "w", 1280), 320, 8192)
            Dim height = Math.Clamp(ReadNumber(message, "h", 800), 240, 8192)
            _scale = Math.Clamp(ReadNumber(message, "scale", 1), 0.5, 4)

            Dim windows = VisibleWindows()
            For Each win In windows
                InvokeHeadless(win, "SetRenderScaling", _scale)
            Next

            Dim main = windows.FirstOrDefault(Function(w) w.Owner Is Nothing)
            If main Is Nothing Then Return
            main.WindowState = Avalonia.Controls.WindowState.Normal
            main.Position = New PixelPoint(0, 0)
            main.Width = Math.Max(width, main.MinWidth)
            main.Height = Math.Max(height, main.MinHeight)
        End Sub

        Private Shared Function VisibleWindows() As List(Of Window)
            Dim source = WindowSource
            If source Is Nothing Then Return New List(Of Window)()
            Return source().Where(Function(w) w IsNot Nothing AndAlso w.IsVisible AndAlso w.PlatformImpl IsNot Nothing).ToList()
        End Function

        Private Shared Function StateOf(win As Window) As BridgeWindow
            Dim state As BridgeWindow = Nothing
            If States.TryGetValue(win, state) Then Return state

            _nextWindowId += 1
            _activationCounter += 1
            state = New BridgeWindow With {.Id = _nextWindowId, .ActivationOrder = _activationCounter}
            States(win) = state
            ' Welches Fenster oben liegt, weiss ohne Fensterverwaltung niemand ausser uns: gemerkt
            ' wird die Reihenfolge, in der sie zuletzt aktiviert wurden.
            AddHandler win.Activated,
                Sub(sender, e)
                    _activationCounter += 1
                    state.ActivationOrder = _activationCounter
                End Sub
            ' Ein geschlossenes Fenster aendert die Lage der Fenster im Browser, auch wenn darunter
            ' nichts neu gezeichnet wird.
            AddHandler win.Closed, Sub(sender, e) SchedulePass()
            If _scale <> 1.0 Then InvokeHeadless(win, "SetRenderScaling", _scale)
            ' Was im Fenster keine eigene Angabe traegt, erbt die des Fensters.
            If _limitSmoothing Then ClampSmoothing(win, includeUnspecified:=True)
            Return state
        End Function

        ''' <summary>Holt die Bilder aller Fenster ab und schickt die geaenderten Ausschnitte.
        ''' Solange der letzte Schwung noch kodiert oder gesendet wird, kommt kein neuer dazu
        ''' (RunPass laesst den Durchgang dann aus). Verglichen wird immer gegen den GESENDETEN
        ''' Stand, es geht also nichts verloren: was sich inzwischen geaendert hat, steckt im
        ''' naechsten Ausschnitt.</summary>
        Private Shared Sub CollectAndSend()
            Try
                Dim started = Stopwatch.GetTimestamp()
                Dim windows = VisibleWindows()
                For Each closed In States.Keys.Where(Function(w) Not windows.Contains(w)).ToList()
                    States.Remove(closed)
                Next

                Dim jobs As New List(Of FrameJob)()
                For Each win In windows
                    CollectFrame(win, StateOf(win), jobs)
                Next
                ' Was die letzte Mausbewegung bewirkt hat, ist jetzt abgeholt: die naechste darf.
                ReleaseInputGate()

                Dim layout = BuildLayout(windows)
                Dim layoutChanged = Not String.Equals(layout, _lastLayout, StringComparison.Ordinal)
                If Not layoutChanged AndAlso jobs.Count = 0 Then Return
                _lastLayout = layout

                Interlocked.Exchange(_sending, 1)
                Dim layoutToSend = If(layoutChanged, layout, Nothing)
                Dim collectTicks = Stopwatch.GetTimestamp() - started
                Dim ignored = Task.Run(Function() SendFramesAsync(layoutToSend, jobs, collectTicks))
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Frame", ex)
            End Try
        End Sub

        ''' <summary>Lage, Groesse und Reihenfolge aller Fenster in Bildpunkten. Der Browser legt
        ''' danach je Fenster ein Canvas an.</summary>
        Private Shared Function BuildLayout(windows As List(Of Window)) As String
            Dim text As New StringBuilder()
            text.Append("{""t"":""windows"",""scale"":").Append(_scale.ToString("R", CultureInfo.InvariantCulture)).Append(",""list"":[")
            Dim first = True
            For Each win In windows
                Dim state = StateOf(win)
                Dim size = PixelSize.FromSize(win.ClientSize, win.RenderScaling)
                If Not first Then text.Append(","c)
                first = False
                text.Append("{""id"":").Append(state.Id).
                     Append(",""x"":").Append(win.Position.X).
                     Append(",""y"":").Append(win.Position.Y).
                     Append(",""w"":").Append(size.Width).
                     Append(",""h"":").Append(size.Height).
                     Append(",""z"":").Append(state.ActivationOrder).
                     Append(",""s"":").Append(win.RenderScaling.ToString("R", CultureInfo.InvariantCulture)).
                     Append("}"c)
            Next
            text.Append("]}")
            Return text.ToString()
        End Function

        ''' <summary>Holt das zuletzt gezeichnete Bild eines Fensters und legt die geaenderten
        ''' Ausschnitte als Auftraege ab. Hat die Anwendung nichts neu gezeichnet, prueft es nur,
        ''' ob ein verlustfreier Nachzug faellig ist.</summary>
        Private Shared Sub CollectFrame(win As Window, state As BridgeWindow, jobs As List(Of FrameJob))
            Dim marker = CurrentFrameMarker(win)
            If marker IsNot Nothing AndAlso marker Is state.LastFrame Then
                CollectLossless(state, jobs)
                Return
            End If

            ' Das Bild der Plattform wird direkt gelesen, wenn es erreichbar ist. Der oeffentliche
            ' Weg legt erst eine Kopie des ganzen Bilds an, die hier gleich wieder verworfen wuerde.
            ' Gezeichnet wird auf demselben Faden, das Bild kann sich waehrenddessen nicht aendern.
            Dim direct = TryCast(marker, WriteableBitmap)
            Dim copy As WriteableBitmap = Nothing
            If direct Is Nothing Then
                copy = win.GetLastRenderedFrame()
                If copy Is Nothing Then Return
            End If
            Try
                Using locked = If(direct, copy).Lock()
                    state.ColorType = If(locked.Format = Avalonia.Platform.PixelFormat.Rgba8888,
                                         SKColorType.Rgba8888, SKColorType.Bgra8888)
                    Dim width = locked.Size.Width
                    Dim height = locked.Size.Height
                    If width <= 0 OrElse height <= 0 Then Return

                    ' Verglichen wird direkt auf dem Bild der Plattform, und in den gesendeten Stand
                    ' kommen nur die geaenderten Kacheln. Vorher wurde erst das ganze Bild in einen
                    ' Arbeitspuffer kopiert (21,6 MB bei 5,4 Megapixeln) und dann verglichen.
                    Dim sameSize = state.Previous IsNot Nothing AndAlso state.PixelWidth = width AndAlso state.PixelHeight = height
                    Dim rects As List(Of PixelRect)
                    If sameSize Then
                        rects = FindChangedRects(state.Previous, locked.Address, locked.RowBytes, width, height)
                    Else
                        Dim count = width * height
                        state.Previous = New Integer(count - 1) {}
                        If locked.RowBytes = width * 4 Then
                            Marshal.Copy(locked.Address, state.Previous, 0, count)
                        Else
                            For row = 0 To height - 1
                                Marshal.Copy(IntPtr.Add(locked.Address, row * locked.RowBytes), state.Previous, row * width, width)
                            Next
                        End If
                        rects = New List(Of PixelRect) From {New PixelRect(0, 0, width, height)}
                        state.HasLossy = False
                    End If

                    state.PixelWidth = width
                    state.PixelHeight = height
                    state.LastFrame = marker

                    If rects.Count = 0 Then
                        CollectLossless(state, jobs)
                        Return
                    End If

                    state.LastChange = Environment.TickCount64
                    For Each rect In rects
                        Dim lossless = rect.Width * rect.Height <= DirectLosslessPixels
                        AddJobs(state, rect, lossless, jobs)
                        If lossless Then Continue For
                        If state.HasLossy Then
                            state.LossyLeft = Math.Min(state.LossyLeft, rect.X)
                            state.LossyTop = Math.Min(state.LossyTop, rect.Y)
                            state.LossyRight = Math.Max(state.LossyRight, rect.Right - 1)
                            state.LossyBottom = Math.Max(state.LossyBottom, rect.Bottom - 1)
                        Else
                            state.LossyLeft = rect.X
                            state.LossyTop = rect.Y
                            state.LossyRight = rect.Right - 1
                            state.LossyBottom = rect.Bottom - 1
                            state.HasLossy = True
                        End If
                    Next
                End Using
            Finally
                copy?.Dispose()
            End Try
        End Sub

        ''' <summary>Der verlustfreie Nachzug: steht das Fenster still, geht der Bereich, der
        ''' zuletzt als JPEG hinausging, noch einmal als PNG.</summary>
        Private Shared Sub CollectLossless(state As BridgeWindow, jobs As List(Of FrameJob))
            If Not state.HasLossy OrElse state.Previous Is Nothing Then Return
            If Environment.TickCount64 - state.LastChange < LosslessDelayMilliseconds Then Return
            state.HasLossy = False
            Dim left = Math.Clamp(state.LossyLeft, 0, state.PixelWidth - 1)
            Dim top = Math.Clamp(state.LossyTop, 0, state.PixelHeight - 1)
            Dim right = Math.Clamp(state.LossyRight, left, state.PixelWidth - 1)
            Dim bottom = Math.Clamp(state.LossyBottom, top, state.PixelHeight - 1)
            AddJobs(state, New PixelRect(left, top, right - left + 1, bottom - top + 1), True, jobs)
        End Sub

        ''' <summary>Schneidet einen Ausschnitt aus dem gesendeten Stand und zerlegt ihn in
        ''' waagerechte Streifen, die nebeneinander kodiert werden koennen.</summary>
        Private Shared Sub AddJobs(state As BridgeWindow, rect As PixelRect, lossless As Boolean, jobs As List(Of FrameJob))
            Dim stripHeight = Math.Max(TileSize, StripPixels \ Math.Max(1, rect.Width))
            Dim top = rect.Y
            While top < rect.Bottom
                Dim height = Math.Min(stripHeight, rect.Bottom - top)
                ' Ein schmaler Rest am Ende geht mit dem letzten Streifen, statt eine eigene
                ' Nachricht zu werden.
                If rect.Bottom - (top + height) < TileSize \ 2 Then height = rect.Bottom - top
                Dim pixels = New Integer(rect.Width * height - 1) {}
                For row = 0 To height - 1
                    Array.Copy(state.Previous, (top + row) * state.PixelWidth + rect.X, pixels, row * rect.Width, rect.Width)
                Next
                jobs.Add(New FrameJob With {
                    .WindowId = state.Id, .X = rect.X, .Y = top, .Width = rect.Width, .Height = height,
                    .Pixels = pixels, .Lossless = lossless, .ColorType = state.ColorType})
                top += height
            End While
        End Sub

        ''' <summary>Die Rechtecke, in denen sich zwei gleich grosse Bilder unterscheiden.
        '''
        ''' Gesucht wird in Kacheln. Benachbarte geaenderte Kacheln einer Zeile werden zu einem
        ''' Lauf, gleiche Laeufe untereinander zu einem Rechteck. Zieht jemand einen Regler am
        ''' rechten Rand, aendern sich der Regler und das Bild in der Mitte: das sind dann zwei
        ''' Rechtecke und nicht eines, das auch alles dazwischen einschliesst.
        '''
        ''' <para><paramref name="previous"/> ist der gesendete Stand, <paramref name="frame"/> das
        ''' neue Bild der Plattform (32 Bit je Bildpunkt, <paramref name="rowBytes"/> je Zeile).
        ''' Verglichen wird direkt auf dem neuen Bild, Kachelzeile fuer Kachelzeile auf allen
        ''' Kernen, und in den gesendeten Stand kommen nur die geaenderten Kacheln: danach ist er
        ''' gleich dem neuen Bild. Die Schmutzrechtecke des Toolkits waeren genauer, sind aber
        ''' nicht erreichbar: es setzt sie am Ende desselben Zeichenaufrufs zurueck, in dem es sie
        ''' bildet.</para></summary>
        Friend Shared Function FindChangedRects(previous As Integer(), frame As IntPtr, rowBytes As Integer,
                                                width As Integer, height As Integer) As List(Of PixelRect)
            Dim columns = (width + TileSize - 1) \ TileSize
            Dim rows = (height + TileSize - 1) \ TileSize
            Dim dirtyTiles = New Boolean(rows * columns - 1) {}
            If rows > 1 Then
                Parallel.For(0, rows, Sub(tileRow) CompareTileRow(previous, frame, rowBytes, width, height, tileRow, columns, dirtyTiles))
            Else
                CompareTileRow(previous, frame, rowBytes, width, height, 0, columns, dirtyTiles)
            End If

            Dim result As New List(Of PixelRect)()
            ' Offene Rechtecke der vorigen Kachelzeile, nach ihrer ersten Spalte.
            Dim open As New Dictionary(Of Integer, PixelRect)()
            Dim dirty = New Boolean(columns - 1) {}

            For tileRow = 0 To rows - 1
                Array.Copy(dirtyTiles, tileRow * columns, dirty, 0, columns)
                Dim top = tileRow * TileSize
                Dim bottom = Math.Min(top + TileSize, height)

                Dim continued As New Dictionary(Of Integer, PixelRect)()
                Dim first = 0
                While first < columns
                    If Not dirty(first) Then
                        first += 1
                        Continue While
                    End If
                    Dim last = first
                    While last + 1 < columns AndAlso dirty(last + 1)
                        last += 1
                    End While
                    Dim left = first * TileSize
                    Dim runWidth = Math.Min((last + 1) * TileSize, width) - left
                    Dim above As PixelRect
                    If open.TryGetValue(first, above) AndAlso above.Width = runWidth Then
                        continued(first) = New PixelRect(left, above.Y, runWidth, bottom - above.Y)
                        open.Remove(first)
                    Else
                        continued(first) = New PixelRect(left, top, runWidth, bottom - top)
                    End If
                    first = last + 1
                End While
                result.AddRange(open.Values)
                open = continued
            Next
            result.AddRange(open.Values)

            If result.Count > MaxRects Then
                Dim union = result(0)
                For Each rect In result
                    union = union.Union(rect)
                Next
                result.Clear()
                result.Add(union)
            End If
            Return result
        End Function

        ''' <summary>Eine Kachelzeile: welche ihrer Kacheln sich geaendert haben, und deren neuer
        ''' Inhalt in den gesendeten Stand. Eine Zeile des neuen Bilds wird dafuer in einen kleinen
        ''' Puffer gelesen (sie passt in den schnellen Speicher des Kerns) und dort verglichen. Ist
        ''' eine Kachel einmal als geaendert erkannt, wird sie fuer den Rest ihrer Zeilen nur noch
        ''' kopiert; die Zeilen darueber waren gleich und brauchen nichts.</summary>
        Private Shared Sub CompareTileRow(previous As Integer(), frame As IntPtr, rowBytes As Integer, width As Integer, height As Integer,
                                          tileRow As Integer, columns As Integer, dirtyTiles As Boolean())
            Dim line = System.Buffers.ArrayPool(Of Integer).Shared.Rent(width)
            Try
                Dim first = tileRow * columns
                Dim top = tileRow * TileSize
                Dim bottom = Math.Min(top + TileSize, height)
                Dim remaining = columns
                For y = top To bottom - 1
                    Dim offset = y * width
                    Dim source = IntPtr.Add(frame, y * rowBytes)
                    If remaining = 0 Then
                        ' Jede Kachel dieser Zeile ist geaendert: die Zeile ganz uebernehmen.
                        Marshal.Copy(source, previous, offset, width)
                        Continue For
                    End If
                    Marshal.Copy(source, line, 0, width)
                    For column = 0 To columns - 1
                        Dim left = column * TileSize
                        Dim length = Math.Min(TileSize, width - left)
                        If Not dirtyTiles(first + column) Then
                            If Not SegmentDiffers(previous, offset + left, line, left, length) Then Continue For
                            dirtyTiles(first + column) = True
                            remaining -= 1
                        End If
                        Array.Copy(line, left, previous, offset + left, length)
                    Next
                Next
            Finally
                System.Buffers.ArrayPool(Of Integer).Shared.Return(line)
            End Try
        End Sub

        ''' <summary>Ob sich zwei Abschnitte gleicher Laenge unterscheiden. Verglichen wird in
        ''' Vektoren, also acht Bildpunkte auf einmal, wo der Prozessor das kann.</summary>
        Private Shared Function SegmentDiffers(previous As Integer(), previousStart As Integer,
                                               current As Integer(), currentStart As Integer, length As Integer) As Boolean
            Dim stride = System.Numerics.Vector(Of Integer).Count
            Dim index = 0
            While index + stride <= length
                If Not System.Numerics.Vector.EqualsAll(New System.Numerics.Vector(Of Integer)(previous, previousStart + index),
                                                        New System.Numerics.Vector(Of Integer)(current, currentStart + index)) Then Return True
                index += stride
            End While
            While index < length
                If previous(previousStart + index) <> current(currentStart + index) Then Return True
                index += 1
            End While
            Return False
        End Function

        ''' <summary>Das Bildobjekt, das die Plattform zuletzt gezeichnet hat. Sie legt je Bild ein
        ''' neues an; ist es noch dasselbe wie beim letzten Mal, muss nichts kopiert und nichts
        ''' verglichen werden. Das Feld ist intern. Findet es sich nicht mehr, wird jedes Mal
        ''' verglichen: langsamer, aber richtig.</summary>
        Private Shared Function CurrentFrameMarker(win As Window) As Object
            Dim impl = win.PlatformImpl
            If impl Is Nothing Then Return Nothing
            If Not _lastFrameFieldSearched Then
                _lastFrameFieldSearched = True
                _lastFrameField = impl.GetType().GetField("_lastRenderedFrame", BindingFlags.Instance Or BindingFlags.NonPublic)
            End If
            If _lastFrameField Is Nothing OrElse Not _lastFrameField.DeclaringType.IsInstanceOfType(impl) Then Return Nothing
            Return _lastFrameField.GetValue(impl)
        End Function

        Private Shared Async Function SendFramesAsync(layout As String, jobs As List(Of FrameJob), collectTicks As Long) As Task
            Try
                Dim started = Stopwatch.GetTimestamp()
                Dim messages = New Byte(jobs.Count - 1)() {}
                If jobs.Count = 1 Then
                    messages(0) = Encode(jobs(0))
                ElseIf jobs.Count > 1 Then
                    Parallel.For(0, jobs.Count, Sub(index) messages(index) = Encode(jobs(index)))
                End If
                Dim encoded = Stopwatch.GetTimestamp()

                If layout IsNot Nothing Then Await SendTextAsync(layout)
                Dim bytes As Long = 0
                For Each message In messages
                    bytes += message.Length
                    Await SendAsync(message, WebSocketMessageType.Binary)
                Next
                RecordTiming(collectTicks, encoded - started, Stopwatch.GetTimestamp() - encoded, jobs, bytes)
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Send", ex)
            Finally
                Interlocked.Exchange(_sending, 0)
                ' Ein Durchgang fiel aus, solange der Schwung unterwegs war: er kommt jetzt, nach
                ' der Regel des Takts.
                If Interlocked.Exchange(_passAfterSend, 0) <> 0 Then SchedulePass()
            End Try
        End Function

        ''' <summary>Eine Bildnachricht: 22 Bytes Kopf (Art, Fenster, Lage, Groesse, Format), danach
        ''' die kodierten Bilddaten.</summary>
        Private Shared Function Encode(job As FrameJob) As Byte()
            Dim handle = GCHandle.Alloc(job.Pixels, GCHandleType.Pinned)
            Try
                ' Das Bild der Plattform ist vormultipliziert. Fuer JPEG gilt es als deckend, PNG
                ' behaelt die Deckkraft, damit runde Fensterecken durchsichtig bleiben.
                Dim info As New SKImageInfo(job.Width, job.Height, job.ColorType,
                                            If(job.Lossless, SKAlphaType.Premul, SKAlphaType.Opaque))
                Using pixmap As New SKPixmap(info, handle.AddrOfPinnedObject(), job.Width * 4)
                    Using data = If(job.Lossless,
                                    PngEncoder.Encode(pixmap, PngPurpose.Stored),
                                    pixmap.Encode(New SKJpegEncoderOptions(JpegQuality)))
                        Dim image = data.ToArray()
                        Dim message = New Byte(HeaderLength + image.Length - 1) {}
                        message(0) = 1
                        WriteInt32(message, 1, job.WindowId)
                        WriteInt32(message, 5, job.X)
                        WriteInt32(message, 9, job.Y)
                        WriteInt32(message, 13, job.Width)
                        WriteInt32(message, 17, job.Height)
                        message(21) = If(job.Lossless, CByte(1), CByte(0))
                        Buffer.BlockCopy(image, 0, message, HeaderLength, image.Length)
                        Return message
                    End Using
                End Using
            Finally
                handle.Free()
            End Try
        End Function

        Private Shared Sub WriteInt32(target As Byte(), offset As Integer, value As Integer)
            Dim bytes = BitConverter.GetBytes(value)
            If Not BitConverter.IsLittleEndian Then Array.Reverse(bytes)
            Buffer.BlockCopy(bytes, 0, target, offset, 4)
        End Sub

        ' ------------------------------------------------------------------------------------
        ' Zeitmessung fuer das Diagnoseprotokoll (Bereich "BrowserBridge.Timing"). Sie sammelt
        ' ueber zwei Sekunden und schreibt dann eine Zeile: wie viele Schwuenge hinausgingen, wie
        ' lange Abholen und Vergleichen, Kodieren und Senden im Mittel dauerten, wie viel Flaeche
        ' und wie viele Bytes je Schwung, und wie oft die Abholung aussetzen musste, weil der
        ' vorige Schwung noch unterwegs war. Geschrieben wird nur bei eingeschaltetem Protokoll.
        ' ------------------------------------------------------------------------------------
        Private Shared ReadOnly TimingLock As New Object()
        Private Shared _timingStart As Long
        Private Shared _timingBatches As Integer
        Private Shared _timingCollect As Long
        Private Shared _timingEncode As Long
        Private Shared _timingSend As Long
        Private Shared _timingPixels As Long
        Private Shared _timingBytes As Long
        Private Shared _timingLossless As Integer
        Private Shared _timingPasses As Long
        Private Shared _skippedTicks As Integer

        Private Shared Sub RecordTiming(collectTicks As Long, encodeTicks As Long, sendTicks As Long,
                                        jobs As List(Of FrameJob), bytes As Long)
            Dim line As String = Nothing
            SyncLock TimingLock
                Dim now = Environment.TickCount64
                If _timingStart = 0 Then _timingStart = now
                _timingBatches += 1
                _timingCollect += collectTicks
                _timingEncode += encodeTicks
                _timingSend += sendTicks
                _timingBytes += bytes
                For Each job In jobs
                    _timingPixels += CLng(job.Width) * job.Height
                    If job.Lossless Then _timingLossless += 1
                Next
                If now - _timingStart < 2000 Then Return

                Dim toMilliseconds = 1000.0 / Stopwatch.Frequency / _timingBatches
                Dim seconds = (now - _timingStart) / 1000.0
                Dim passes = PassCount
                line = String.Format(CultureInfo.InvariantCulture,
                    "batches/s={0:F1} passes/s={8:F1} collect={1:F1}ms encode={2:F1}ms send={3:F1}ms megapixels/batch={4:F2} kB/batch={5:F0} lossless={6} skipped={7}",
                    _timingBatches / seconds, _timingCollect * toMilliseconds, _timingEncode * toMilliseconds,
                    _timingSend * toMilliseconds, _timingPixels / 1000000.0 / _timingBatches,
                    _timingBytes / 1024.0 / _timingBatches, _timingLossless, Interlocked.Exchange(_skippedTicks, 0),
                    (passes - _timingPasses) / seconds)
                _timingPasses = passes
                _timingStart = now
                _timingBatches = 0
                _timingCollect = 0
                _timingEncode = 0
                _timingSend = 0
                _timingPixels = 0
                _timingBytes = 0
                _timingLossless = 0
            End SyncLock
            DiagnosticLogService.LogAlways("BrowserBridge.Timing", line)
        End Sub
    End Class
End Namespace
