Imports System
Imports System.Threading.Tasks
Imports Avalonia.Threading

Namespace Models

    ''' <summary>
    ''' Entprellt schnelles Mausrad-Blättern im Filmstrip (viele Wheel-Events kurz hintereinander
    ''' würden sonst jedes für sich sofort ein Bild laden, wodurch das Blättern nach dem Aufhören zu
    ''' scrollen sichtbar weiterläuft). Viewer und Editor pflegten zuvor unabhängige, fast identische
    ''' Kopien dieser Logik. Was beim tatsächlichen Navigieren passiert (Speichern-Abfrage, Laden) und
    ''' ob der Index am Ende/Anfang umbricht, unterscheidet sich zwischen beiden und wird deshalb als
    ''' Parameter/Callback übergeben statt hier festgeschrieben zu sein. Jede Ansicht hält weiterhin
    ''' ihre eigene Instanz.
    ''' </summary>
    Public NotInheritable Class FilmstripNavigationDebouncer
        Private ReadOnly _wrapAround As Boolean
        Private ReadOnly _getCurrentIndex As Func(Of Integer)
        Private ReadOnly _getCount As Func(Of Integer)
        Private ReadOnly _commit As Func(Of Integer, Task)
        Private _pendingIndex As Integer = -1
        Private ReadOnly _timer As DispatcherTimer
        Private ReadOnly _debounceMs As Integer

        ' Ein einzelner, isolierter Schritt (z.B. ein Pfeiltasten-Druck oder ein einzelner
        ' Mausrad-Notch nach einer Pause) committet sofort statt die vollen debounceMs zu warten -
        ' das machte jede einzelne Navigation spürbar träge. Erst wenn innerhalb von debounceMs seit
        ' dem letzten Commit ein WEITERER Schritt eintrifft (schnelles Blättern/Scrollen), greift der
        ' bestehende Debounce-Timer und bündelt die restlichen Schritte zu einem einzigen Commit.
        Private _lastCommitAt As DateTime = DateTime.MinValue

        ' Hochauflösende Mäuse/Touchpads feuern pro physischer Scroll-Geste viele PointerWheelChanged-
        ' Events (oft 10-20) innerhalb weniger Millisekunden, jedes mit einem kleinen Delta.Y-Bruchteil
        ' statt der vollen "Rastung" (±1.0) einer klassischen Maus. Ohne Berücksichtigung der Delta-
        ' Magnitude (nur "ein Event = ein Schritt") akkumulierten sich diese Events lautlos zu einem
        ' großen Sprung, bevor der obige Commit-Timer feuert - _wheelAccumulator sammelt die
        ' tatsächliche Rohdelta-Summe und löst erst ab einer vollen Rastung (WheelNotchThreshold)
        ' einen einzelnen Schritt über das bestehende, unveränderte QueueDelta aus.
        Private Const WheelNotchThreshold As Double = 1.0
        Private _wheelAccumulator As Double = 0.0
        Private ReadOnly _wheelIdleTimer As DispatcherTimer

        ' Momentum-/Kinetic-Scrolling von Touchpads/manchen Mäusen feuert nach dem physischen
        ' Ende einer Scroll-Geste noch für einige hundert ms weitere, abklingende Wheel-Events mit
        ' abnehmendem Delta. Der Abstand zwischen zwei solchen Nachläufer-Events überschreitet dabei
        ' oft _debounceMs (90ms), wodurch der Sofort-Commit-Pfad unten (gedacht für einen bewusst
        ' isolierten Einzelschritt nach einer echten Pause) jeden Nachläufer fälschlich als neuen
        ' Schritt committet - das Blättern läuft dadurch spürbar nach. Ein Wheel-Notch darf daher nur
        ' sofort committen, wenn seit dem VORHERIGEN Wheel-Event (nicht nur seit dem letzten Commit)
        ' eine echte Pause vergangen ist. Tastatur-Navigation (QueueNext/QueuePrevious) ist davon
        ' nicht betroffen und bleibt weiterhin bei jedem Einzelschritt sofort reagierend.
        Private Const WheelFreshGestureThresholdMs As Double = 200
        Private _lastWheelEventAt As DateTime = DateTime.MinValue

        Public Sub New(wrapAround As Boolean, getCurrentIndex As Func(Of Integer), getCount As Func(Of Integer),
                       commit As Func(Of Integer, Task), Optional debounceMs As Integer = 90)
            _wrapAround = wrapAround
            _getCurrentIndex = getCurrentIndex
            _getCount = getCount
            _commit = commit
            _debounceMs = debounceMs
            _timer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(debounceMs)}
            AddHandler _timer.Tick, Sub()
                                        _timer.Stop()
                                        Dim idx = _pendingIndex
                                        _pendingIndex = -1
                                        If idx >= 0 Then
                                            _lastCommitAt = DateTime.UtcNow
                                            Dim ignored = _commit(idx)
                                        End If
                                    End Sub

            ' 300ms bewusst länger als der 90ms-Commit-Timer, damit sie sich nie überschneiden -
            ' verwirft einen liegengebliebenen Bruchteil-Rest, sobald 300ms lang kein Wheel-Event
            ' mehr kam, damit er nicht Minuten später mit einem einzelnen späteren Notch-Scroll zu
            ' einem ungewollten Extra-Schritt verschmilzt.
            _wheelIdleTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(300)}
            AddHandler _wheelIdleTimer.Tick, Sub()
                                                 _wheelIdleTimer.Stop()
                                                 _wheelAccumulator = 0.0
                                             End Sub
        End Sub

        Public Sub QueueNext()
            QueueDelta(1)
        End Sub

        Public Sub QueuePrevious()
            QueueDelta(-1)
        End Sub

        ''' Normalisiert Mausrad-Events anhand ihrer tatsächlichen Delta.Y-Magnitude statt sie 1:1
        ''' als einzelnen Navigationsschritt zu behandeln - siehe Kommentar an _wheelAccumulator.
        ''' Ruft für jede volle konsumierte Rastung das bestehende, unveränderte QueueDelta auf, kann
        ''' bei einer schnellen Geste also mehrfach hintereinander synchron aufgerufen werden.
        Public Sub QueueWheelDelta(deltaY As Double)
            If deltaY = 0 Then Return
            Dim isFreshGesture = (DateTime.UtcNow - _lastWheelEventAt).TotalMilliseconds >= WheelFreshGestureThresholdMs
            _lastWheelEventAt = DateTime.UtcNow
            _wheelAccumulator += deltaY
            _wheelIdleTimer.Stop()
            _wheelIdleTimer.Start()
            While Math.Abs(_wheelAccumulator) >= WheelNotchThreshold
                If _wheelAccumulator > 0 Then
                    _wheelAccumulator -= WheelNotchThreshold
                    QueueDelta(-1, allowImmediateCommit:=isFreshGesture)
                Else
                    _wheelAccumulator += WheelNotchThreshold
                    QueueDelta(1, allowImmediateCommit:=isFreshGesture)
                End If
            End While
        End Sub

        Private Sub QueueDelta(delta As Integer, Optional allowImmediateCommit As Boolean = True)
            Dim count = _getCount()
            If count = 0 Then Return
            Dim baseIndex = If(_pendingIndex >= 0, _pendingIndex, _getCurrentIndex())
            Dim idx As Integer
            If _wrapAround Then
                If count = 1 Then
                    idx = 0
                Else
                    Dim fromIndex = Math.Max(0, baseIndex)
                    idx = ((fromIndex + delta) Mod count + count) Mod count
                End If
            Else
                idx = baseIndex + delta
                If idx < 0 OrElse idx >= count Then Return
            End If
            _pendingIndex = idx

            ' Sofort committen, wenn gerade kein Debounce läuft UND der letzte Commit lange genug
            ' her ist - das deckt sowohl den allerersten Schritt als auch einen isolierten Schritt
            ' nach einer Pause ab. Läuft bereits ein Timer (schnelle Folge-Schritte einer laufenden
            ' Geste), bleibt es beim bisherigen Bündeln über den Debounce-Timer.
            If allowImmediateCommit AndAlso Not _timer.IsEnabled AndAlso (DateTime.UtcNow - _lastCommitAt).TotalMilliseconds >= _debounceMs Then
                Dim idxToCommit = _pendingIndex
                _pendingIndex = -1
                _lastCommitAt = DateTime.UtcNow
                Dim ignored = _commit(idxToCommit)
                Return
            End If

            _timer.Stop()
            _timer.Start()
        End Sub
    End Class

End Namespace
