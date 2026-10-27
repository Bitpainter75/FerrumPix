Imports System
Imports System.Linq
Imports Avalonia.Threading
Imports SkiaSharp
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Verfluessigen: die fuenfte Art im Verzerren. Ein Pinsel schiebt, blaeht, zieht
    ''' zusammen, verdreht, glaettet oder holt zurueck, und zwar die KNOTEN eines feinen Rasters im
    ''' Verzerrraum. Uebernommen wird es wie Gitter, Linien und Verformen als Knotenfeld ins Rezept;
    ''' Masken und Objekte gehen damit von selbst mit, und beim naechsten Oeffnen steht es wieder da.
    '''
    ''' Eigene Scheibe, weil es eigenen Zustand hat (Knotenfeld, Pinsel, Zug), der sonst nirgends
    ''' gelesen wird. Was es mit den anderen Arten teilt (Vorschaukanal, Verzerrraum, Uebernahme),
    ''' bleibt in <c>EditorViewModelWarp.vb</c>.
    '''
    ''' DAS FELD IST VORWAERTS GEFUEHRT: Knoten i ruht bei (Spalte, Zeile) und steht bei
    ''' (_liquifyX(i), _liquifyY(i)), genau die Form des Gitterwerkzeugs. Ein Tupfer wiegt jeden
    ''' Knoten nach seiner JETZIGEN Lage, also nach dem, was man sieht. Zwei Zuege ueber dieselbe
    ''' Stelle verketten sich damit von selbst; ein Feld aus addierten Versaetzen am Ruheort taete
    ''' das nicht, und starke Schuebe verwischten die Form.</summary>
    Partial Public Class EditorViewModel

        ''' <summary>Feinheit des Knotenfelds, je Richtung. 48 wie bei den anderen Arten waeren bei
        ''' 6000 Bildpunkten Maschen von 125 Punkten, fuer eine Nase oder einen Mundwinkel zu grob.
        ''' Feiner kostet jede Ruecksuche und die Groesse des Rezepts: 129 mal 129 Knoten, auf vier
        ''' Nachkommastellen gerundet, sind rund 250 KB im Rezept.</summary>
        Friend Const LiquifySteps As Integer = 128

        ''' <summary>Gerundet wird beim Uebernehmen auf so viele Nachkommastellen (Prozent). Bei 6000
        ''' Bildpunkten ist das ein Hundertstel Punkt, sichtbar ist es nicht, halbiert aber das
        ''' Rezept.</summary>
        Private Const LiquifyDecimals As Integer = 4

        Private _liquifyX As Double() = Nothing
        Private _liquifyY As Double() = Nothing
        ''' Der beiseitegelegte BILD-Stand, solange ein Objekt markiert ist (siehe RefreshWarpSpace).
        Private _liquifyImageX As Double() = Nothing
        Private _liquifyImageY As Double() = Nothing

        Private _liquifyBrush As String = "Push"
        Private _liquifySize As Double = 150
        Private _liquifyStrength As Double = 50

        Private _liquifyStroking As Boolean
        Private _liquifyLastX As Double
        Private _liquifyLastY As Double
        Private _liquifyTimer As DispatcherTimer

        Public ReadOnly Property IsWarpLiquify As Boolean
            Get
                Return String.Equals(_warpMode, "Liquify", StringComparison.Ordinal)
            End Get
        End Property

        ' ── Pinsel ──────────────────────────────────────────────────────────────

        ''' <summary>"Push", "Bloat", "Pucker", "TwirlRight", "TwirlLeft", "Smooth" oder
        ''' "Reconstruct".</summary>
        Public Property LiquifyBrush As String
            Get
                Return _liquifyBrush
            End Get
            Set(value As String)
                Dim v = If(value, "").Trim()
                Select Case v
                    Case "Push", "Bloat", "Pucker", "TwirlRight", "TwirlLeft", "Smooth", "Reconstruct"
                    Case Else
                        v = "Push"
                End Select
                If String.Equals(_liquifyBrush, v, StringComparison.Ordinal) Then Return
                _liquifyBrush = v
                For Each n In {NameOf(LiquifyBrush), NameOf(IsLiquifyPush), NameOf(IsLiquifyBloat),
                               NameOf(IsLiquifyPucker), NameOf(IsLiquifyTwirlRight), NameOf(IsLiquifyTwirlLeft),
                               NameOf(IsLiquifySmooth), NameOf(IsLiquifyReconstruct)}
                    Me.RaisePropertyChanged(n)
                Next
            End Set
        End Property

        Public ReadOnly Property IsLiquifyPush As Boolean
            Get
                Return _liquifyBrush = "Push"
            End Get
        End Property

        Public ReadOnly Property IsLiquifyBloat As Boolean
            Get
                Return _liquifyBrush = "Bloat"
            End Get
        End Property

        Public ReadOnly Property IsLiquifyPucker As Boolean
            Get
                Return _liquifyBrush = "Pucker"
            End Get
        End Property

        Public ReadOnly Property IsLiquifyTwirlRight As Boolean
            Get
                Return _liquifyBrush = "TwirlRight"
            End Get
        End Property

        Public ReadOnly Property IsLiquifyTwirlLeft As Boolean
            Get
                Return _liquifyBrush = "TwirlLeft"
            End Get
        End Property

        Public ReadOnly Property IsLiquifySmooth As Boolean
            Get
                Return _liquifyBrush = "Smooth"
            End Get
        End Property

        Public ReadOnly Property IsLiquifyReconstruct As Boolean
            Get
                Return _liquifyBrush = "Reconstruct"
            End Get
        End Property

        ''' <summary>Durchmesser des Pinsels in Punkten des Anzeigebildes, wie BrushSize.</summary>
        Public Property LiquifySize As Double
            Get
                Return _liquifySize
            End Get
            Set(value As Double)
                Dim v = Math.Max(10.0, Math.Min(1000.0, value))
                If Math.Abs(_liquifySize - v) < 0.001 Then Return
                _liquifySize = v
                Me.RaisePropertyChanged(NameOf(LiquifySize))
            End Set
        End Property

        ''' <summary>Staerke 1 bis 100. Beim Schieben ist 100 "der Inhalt unter der Pinselmitte folgt
        ''' dem Zeiger ganz", bei den uebrigen Pinseln die Geschwindigkeit, mit der sie wirken,
        ''' solange die Taste gehalten wird.</summary>
        Public Property LiquifyStrength As Double
            Get
                Return _liquifyStrength
            End Get
            Set(value As Double)
                Dim v = Math.Max(1.0, Math.Min(100.0, value))
                If Math.Abs(_liquifyStrength - v) < 0.001 Then Return
                _liquifyStrength = v
                Me.RaisePropertyChanged(NameOf(LiquifyStrength))
            End Set
        End Property

        ' ── Das Knotenfeld ──────────────────────────────────────────────────────

        Private Sub PrepareLiquify()
            Dim count = (LiquifySteps + 1) * (LiquifySteps + 1)
            If _liquifyX IsNot Nothing AndAlso _liquifyX.Length = count AndAlso
               _liquifyY IsNot Nothing AndAlso _liquifyY.Length = count Then Return
            ResetLiquifyNodes()
        End Sub

        Private Sub ResetLiquifyNodes()
            Dim count = (LiquifySteps + 1) * (LiquifySteps + 1)
            ReDim _liquifyX(count - 1)
            ReDim _liquifyY(count - 1)
            For rowIdx = 0 To LiquifySteps
                For colIdx = 0 To LiquifySteps
                    Dim i = rowIdx * (LiquifySteps + 1) + colIdx
                    _liquifyX(i) = colIdx * 100.0 / LiquifySteps
                    _liquifyY(i) = rowIdx * 100.0 / LiquifySteps
                Next
            Next
        End Sub

        Public ReadOnly Property HasLiquifyChanges As Boolean
            Get
                If _liquifyX Is Nothing OrElse _liquifyY Is Nothing Then Return False
                For rowIdx = 0 To LiquifySteps
                    For colIdx = 0 To LiquifySteps
                        Dim i = rowIdx * (LiquifySteps + 1) + colIdx
                        If i >= _liquifyX.Length Then Return False
                        If Math.Abs(_liquifyX(i) - colIdx * 100.0 / LiquifySteps) > 0.0005 OrElse
                           Math.Abs(_liquifyY(i) - rowIdx * 100.0 / LiquifySteps) > 0.0005 Then Return True
                    Next
                Next
                Return False
            End Get
        End Property

        ''' <summary>Die Knoten in ANZEIGE-Prozent, in der Form von WarpGridValues: Spalten,
        ''' Zeilen, dann je Knoten x und y. Die Vorschau zeichnet daraus.</summary>
        Private Function LiquifyDisplayValues() As Double()
            PrepareLiquify()
            Dim arr(1 + _liquifyX.Length * 2) As Double
            arr(0) = LiquifySteps
            arr(1) = LiquifySteps
            For i = 0 To _liquifyX.Length - 1
                Dim anzeige = WarpSpaceToDisplay(_liquifyX(i), _liquifyY(i))
                If anzeige.HasValue Then
                    arr(2 + i * 2) = anzeige.Value.X
                    arr(3 + i * 2) = anzeige.Value.Y
                Else
                    arr(2 + i * 2) = Double.NaN
                    arr(3 + i * 2) = Double.NaN
                End If
            Next
            Return arr
        End Function

        ''' <summary>Wie viele Punkte des Anzeigebildes ein Prozent des Verzerrraums ist, je Achse.
        ''' Der Pinsel ist rund auf dem Schirm, der Verzerrraum ist es nicht: ein Prozent der Breite
        ''' ist bei einem Querformat mehr als ein Prozent der Hoehe.</summary>
        Private Function LiquifyPixelsPerPercent() As (X As Double, Y As Double)
            Dim size = GetAnnotationDisplayPixelSize()
            If size.Width <= 0 OrElse size.Height <= 0 Then Return (0, 0)
            If WarpsTheObject Then
                Dim r = GetSelectedAnnotationDisplayRectPercent()
                If r.Width <= 0 OrElse r.Height <= 0 Then Return (0, 0)
                Return (r.Width / 100.0 * size.Width / 100.0, r.Height / 100.0 * size.Height / 100.0)
            End If
            Return (size.Width / 100.0, size.Height / 100.0)
        End Function

        ' ── Der Tupfer ──────────────────────────────────────────────────────────

        ''' <summary>Ein Tupfer an der Stelle (cx, cy) im Verzerrraum. dx und dy ist die Bewegung
        ''' seit dem letzten Tupfer, ebenfalls im Verzerrraum; nur Schieben braucht sie.
        '''
        ''' Gerechnet wird in Punkten des Anzeigebildes, damit der Pinsel rund ist. Die Profile:
        ''' Schieben, Strudel, Glaetten und Zurueckholen mit einer Glocke (Sigma ein Drittel des
        ''' Radius); Aufblaehen und Zusammenziehen mit t(1 - t)^2, in der Mitte und am Rand null,
        ''' am staerksten bei einem Drittel. Mit einer Glocke bekaeme die Mitte beim Aufblaehen die
        ''' groesste Verschiebung, holte sich nach einigen Tupfern Inhalt vom aeusseren Ring und
        ''' wuerde hohl.</summary>
        Friend Sub ApplyLiquifyDab(cx As Double, cy As Double, dx As Double, dy As Double)
            PrepareLiquify()
            Dim scale = LiquifyPixelsPerPercent()
            If scale.X <= 0 OrElse scale.Y <= 0 Then Return
            Dim radius = _liquifySize / 2.0
            If radius <= 0 Then Return
            Dim k = _liquifyStrength / 100.0
            Dim centerX = cx * scale.X, centerY = cy * scale.Y
            Dim radiusSquared = radius * radius
            Dim twoSigmaSquared = 2.0 * (radius / 3.0) * (radius / 3.0)
            Dim n = LiquifySteps + 1

            ' Glaetten braucht die Nachbarn VOR dem Tupfer, sonst haengt das Ergebnis an der
            ' Reihenfolge, in der die Knoten durchlaufen werden.
            Dim oldX As Double() = Nothing, oldY As Double() = Nothing
            If _liquifyBrush = "Smooth" Then
                oldX = CType(_liquifyX.Clone(), Double())
                oldY = CType(_liquifyY.Clone(), Double())
            End If

            For rowIdx = 0 To LiquifySteps
                For colIdx = 0 To LiquifySteps
                    Dim i = rowIdx * n + colIdx
                    Dim px = _liquifyX(i) * scale.X, py = _liquifyY(i) * scale.Y
                    Dim ox = px - centerX, oy = py - centerY
                    Dim distanceSquared = ox * ox + oy * oy
                    If distanceSquared >= radiusSquared Then Continue For
                    Dim bell = Math.Exp(-distanceSquared / twoSigmaSquared)
                    Dim nx = px, ny = py
                    Select Case _liquifyBrush
                        Case "Push"
                            Dim w = k * bell
                            nx += dx * scale.X * w
                            ny += dy * scale.Y * w
                        Case "Bloat", "Pucker"
                            Dim distance = Math.Sqrt(distanceSquared)
                            If distance < 0.000001 Then Continue For
                            Dim t = distance / radius
                            ' Auf 1 bei t = 1/3 normiert: (1/3)(2/3)^2 = 4/27.
                            Dim profile = t * (1.0 - t) * (1.0 - t) * 27.0 / 4.0
                            Dim amount = radius * 0.03 * k * profile
                            If _liquifyBrush = "Pucker" Then amount = -amount
                            nx += ox / distance * amount
                            ny += oy / distance * amount
                        Case "TwirlRight", "TwirlLeft"
                            ' Im Bildraum zeigt y nach unten: ein positiver Winkel dreht im
                            ' Uhrzeigersinn.
                            Dim angle = 0.06 * k * bell
                            If _liquifyBrush = "TwirlLeft" Then angle = -angle
                            Dim c = Math.Cos(angle), s = Math.Sin(angle)
                            nx = centerX + ox * c - oy * s
                            ny = centerY + ox * s + oy * c
                        Case "Reconstruct"
                            Dim restX = colIdx * 100.0 / LiquifySteps * scale.X
                            Dim restY = rowIdx * 100.0 / LiquifySteps * scale.Y
                            Dim w = Math.Min(1.0, 0.15 * k * bell)
                            nx += (restX - px) * w
                            ny += (restY - py) * w
                        Case "Smooth"
                            ' Der Versatz des Knotens geht auf den Mittelwert der Versaetze seiner
                            ' vier Nachbarn zu. Am Rand zaehlen nur die vorhandenen.
                            Dim sumX = 0.0, sumY = 0.0, count = 0
                            For Each nb In {(colIdx - 1, rowIdx), (colIdx + 1, rowIdx), (colIdx, rowIdx - 1), (colIdx, rowIdx + 1)}
                                If nb.Item1 < 0 OrElse nb.Item1 > LiquifySteps OrElse nb.Item2 < 0 OrElse nb.Item2 > LiquifySteps Then Continue For
                                Dim j = nb.Item2 * n + nb.Item1
                                sumX += oldX(j) - nb.Item1 * 100.0 / LiquifySteps
                                sumY += oldY(j) - nb.Item2 * 100.0 / LiquifySteps
                                count += 1
                            Next
                            If count = 0 Then Continue For
                            Dim ownX = oldX(i) - colIdx * 100.0 / LiquifySteps
                            Dim ownY = oldY(i) - rowIdx * 100.0 / LiquifySteps
                            Dim w = Math.Min(1.0, 0.3 * k * bell)
                            nx += (sumX / count - ownX) * w * scale.X
                            ny += (sumY / count - ownY) * w * scale.Y
                    End Select
                    _liquifyX(i) = nx / scale.X
                    _liquifyY(i) = ny / scale.Y
                Next
            Next
            PinLiquifyEdges()
        End Sub

        ''' <summary>Randknoten bleiben auf ihrer Kante und laufen nur an ihr entlang. Sonst zoege
        ''' ein Zug nahe dem Rand das Bild von der Kante weg, und dort waere es danach durchsichtig.</summary>
        Private Sub PinLiquifyEdges()
            Dim n = LiquifySteps + 1
            For k = 0 To LiquifySteps
                _liquifyX(k) = Math.Max(0.0, Math.Min(100.0, _liquifyX(k)))
                _liquifyY(k) = 0
                Dim bottom = LiquifySteps * n + k
                _liquifyX(bottom) = Math.Max(0.0, Math.Min(100.0, _liquifyX(bottom)))
                _liquifyY(bottom) = 100
                Dim left = k * n
                _liquifyX(left) = 0
                _liquifyY(left) = Math.Max(0.0, Math.Min(100.0, _liquifyY(left)))
                Dim right = k * n + LiquifySteps
                _liquifyX(right) = 100
                _liquifyY(right) = Math.Max(0.0, Math.Min(100.0, _liquifyY(right)))
            Next
        End Sub

        ' ── Der Zug ─────────────────────────────────────────────────────────────

        ''' <summary>Ein Zug beginnt. Die Lage kommt in ANZEIGE-Prozent, wie bei den anderen Arten.
        ''' Schieben wirkt erst mit der Bewegung, die anderen Pinsel sofort und weiter, solange die
        ''' Taste gehalten wird.</summary>
        Public Function BeginLiquifyStroke(xPercent As Double, yPercent As Double) As Boolean
            If Not IsWarpLiquify OrElse Not HasDocument Then Return False
            Dim p = DisplayToWarpSpace(xPercent, yPercent)
            If Not p.HasValue Then Return False
            PrepareLiquify()
            PushUndo(CombineHistoryLabel("Verzerren", "Verflüssigen"))
            _liquifyStroking = True
            _liquifyLastX = p.Value.X
            _liquifyLastY = p.Value.Y
            If Not WarpsTheObject AndAlso _gridPreviewBase Is Nothing Then
                PrepareWarpPreview(LiquifySteps, LiquifySteps, AddressOf GridPointToDisplay)
            End If
            If _liquifyBrush <> "Push" Then
                ApplyLiquifyDab(_liquifyLastX, _liquifyLastY, 0, 0)
                AfterLiquifyChange()
                StartLiquifyTimer()
            End If
            Return True
        End Function

        ''' <summary>Der Zeiger bewegt sich. Weite Spruenge werden in Schritte von hoechstens einem
        ''' Viertel des Radius geteilt: ein schneller Zug soll dieselbe Spur hinterlassen wie ein
        ''' langsamer, und ein einzelner grosser Schub faltete das Raster.</summary>
        Public Sub UpdateLiquifyStroke(xPercent As Double, yPercent As Double)
            If Not _liquifyStroking Then Return
            Dim p = DisplayToWarpSpace(xPercent, yPercent)
            If Not p.HasValue Then Return
            Dim scale = LiquifyPixelsPerPercent()
            If scale.X <= 0 OrElse scale.Y <= 0 Then Return
            Dim tx = CDbl(p.Value.X), ty = CDbl(p.Value.Y)
            Dim moveX = (tx - _liquifyLastX) * scale.X, moveY = (ty - _liquifyLastY) * scale.Y
            Dim distance = Math.Sqrt(moveX * moveX + moveY * moveY)
            If distance < 0.5 Then Return
            Dim stepLength = Math.Max(1.0, _liquifySize / 8.0)
            Dim steps = Math.Max(1, CInt(Math.Ceiling(distance / stepLength)))
            Dim stepX = (tx - _liquifyLastX) / steps, stepY = (ty - _liquifyLastY) / steps
            For s = 1 To steps
                Dim fromX = _liquifyLastX + stepX * (s - 1), fromY = _liquifyLastY + stepY * (s - 1)
                If _liquifyBrush = "Push" Then
                    ApplyLiquifyDab(fromX, fromY, stepX, stepY)
                Else
                    ApplyLiquifyDab(fromX + stepX, fromY + stepY, 0, 0)
                End If
            Next
            _liquifyLastX = tx
            _liquifyLastY = ty
            AfterLiquifyChange()
        End Sub

        Public Sub EndLiquifyStroke()
            If Not _liquifyStroking Then Return
            _liquifyStroking = False
            StopLiquifyTimer()
            If WarpsTheObject Then
                WriteObjectLiquify()
                RefreshPreviewImmediately()
            End If
            ' Am Bild BLEIBT die Vorschau stehen, wie beim Gitter: sie zeigt, was "Anwenden"
            ' uebernaehme.
        End Sub

        Private Sub StartLiquifyTimer()
            If _liquifyTimer Is Nothing Then
                _liquifyTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(40)}
                AddHandler _liquifyTimer.Tick, Sub(s, e)
                                                   If Not _liquifyStroking OrElse _liquifyBrush = "Push" Then
                                                       StopLiquifyTimer()
                                                       Return
                                                   End If
                                                   ApplyLiquifyDab(_liquifyLastX, _liquifyLastY, 0, 0)
                                                   AfterLiquifyChange()
                                               End Sub
            End If
            _liquifyTimer.Start()
        End Sub

        Private Sub StopLiquifyTimer()
            _liquifyTimer?.Stop()
        End Sub

        Private Sub AfterLiquifyChange()
            Me.RaisePropertyChanged(NameOf(HasLiquifyChanges))
            If WarpsTheObject Then
                WriteObjectLiquify()
                SchedulePreviewUpdate()
            Else
                QueueWarpPreview(Sub() RefreshWarpPreview(LiquifySteps, LiquifySteps, LiquifyDisplayValues(), "Liquify"))
            End If
        End Sub

        ' ── Zuruecksetzen und Uebernehmen ───────────────────────────────────────

        Public Sub ResetLiquify()
            If Not HasLiquifyChanges Then Return
            CaptureUndoState("Verzerren")
            StopLiquifyTimer()
            _liquifyStroking = False
            DisposeGridPreview()
            ResetLiquifyNodes()
            Me.RaisePropertyChanged(NameOf(HasLiquifyChanges))
            If WarpsTheObject Then
                WriteObjectLiquify()
                RefreshPreviewImmediately()
            End If
        End Sub

        ''' <summary>Ins Rezept uebernehmen: derselbe Weg wie Gitter und Verformen, nur mit dem
        ''' feineren Raster dieser Art. Am Objekt gibt es nichts zu uebernehmen, dort steht die
        ''' Verzerrung schon laufend.</summary>
        Public Sub ApplyLiquifyWarp()
            If WarpsTheObject Then Return
            If Not HasLiquifyChanges Then Return
            ApplyNodeWarp(LiquifySteps, LiquifySteps,
                          CType(_liquifyX.Clone(), Double()), CType(_liquifyY.Clone(), Double()),
                          afterApply:=Sub()
                              DisposeGridPreview()
                              ResetLiquifyNodes()
                              Me.RaisePropertyChanged(NameOf(HasLiquifyChanges))
                          End Sub,
                          meshSteps:=LiquifySteps, decimals:=LiquifyDecimals)
        End Sub

        ' ── Am markierten Objekt ────────────────────────────────────────────────

        ''' <summary>Das Feld als eigene Verzerrung des Objekts, wie beim Gitter: im Verzerrraum
        ''' des Objekts sind die Zahlen schon Objektprozent.</summary>
        Private Sub WriteObjectLiquify()
            Dim a = CurrentObject()
            If a Is Nothing Then Return
            PrepareLiquify()
            If Not HasLiquifyChanges Then
                a.OwnWarp = Nothing
            Else
                Dim node(_liquifyX.Length * 2 - 1) As Double
                For i = 0 To _liquifyX.Length - 1
                    node(i * 2) = Math.Round(_liquifyX(i), LiquifyDecimals)
                    node(i * 2 + 1) = Math.Round(_liquifyY(i), LiquifyDecimals)
                Next
                a.OwnWarp = New ObjectWarp With {
                    .Kind = "Gitter", .Columns = LiquifySteps, .Rows = LiquifySteps, .Nodes = node}
            End If
            RaiseObjectWarpChanged()
        End Sub

        ''' <summary>Beim Wechsel ins Objekt: das Feld aus dessen eigener Verzerrung holen. Traegt
        ''' es schon eine andere (Gitter, Linien, Verformen), wird sie auf das feine Raster
        ''' ausgewertet, und der erste Zug setzt darauf auf, statt sie zu ersetzen.</summary>
        Private Sub LoadLiquifyFromObject()
            ResetLiquifyNodes()
            Dim v = CurrentObject()?.OwnWarp
            If v Is Nothing OrElse v.IsEmpty Then Return
            Dim mapping As Func(Of Double, Double, (X As Double, Y As Double))
            Select Case v.Kind
                Case "Gitter"
                    mapping = Function(px, py) ExistingWarp(v, px, py)
                Case "Linien"
                    mapping = Function(px, py)
                                  Dim p = ImageGeometryMapper.LinePoint(px, py, v.LineTarget, v.LineSource)
                                  Return (CDbl(p.X), CDbl(p.Y))
                              End Function
                Case Else
                    ' Die Perspektive bleibt, wie sie ist: das Feld beginnt gerade und ersetzt sie
                    ' beim ersten Zug, wie es Gitter und Linien auch tun.
                    Return
            End Select
            For i = 0 To _liquifyX.Length - 1
                Dim m = mapping(_liquifyX(i), _liquifyY(i))
                _liquifyX(i) = m.X
                _liquifyY(i) = m.Y
            Next
        End Sub

        ''' <summary>Den Bildstand beiseite legen, bevor der Raum zu einem Objekt wechselt.</summary>
        Private Sub StashImageLiquify()
            PrepareLiquify()
            _liquifyImageX = CType(_liquifyX.Clone(), Double())
            _liquifyImageY = CType(_liquifyY.Clone(), Double())
        End Sub

        ''' <summary>Den Bildstand zurueckholen, sobald wieder das Bild gemeint ist.</summary>
        Private Sub RestoreImageLiquify()
            Dim count = (LiquifySteps + 1) * (LiquifySteps + 1)
            If _liquifyImageX IsNot Nothing AndAlso _liquifyImageX.Length = count Then
                _liquifyX = CType(_liquifyImageX.Clone(), Double())
                _liquifyY = CType(_liquifyImageY.Clone(), Double())
            Else
                ResetLiquifyNodes()
            End If
        End Sub

    End Class

End Namespace
