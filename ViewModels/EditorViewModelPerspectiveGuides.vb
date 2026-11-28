Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI
Imports SkiaSharp

Namespace ViewModels

    ''' <summary>Die gefuehrte Perspektive: zwei bis vier Linien an Kanten, die im Ergebnis senkrecht
    ''' oder waagerecht stehen sollen, und das Bild wird so entzerrt, dass sie es tun.
    '''
    ''' <para>Eigener Zustand: die Linien, die gefasste Linie, der Umschalter. Das ERGEBNIS steht
    ''' nicht hier, sondern in den acht Eckenversaetzen der Perspektive
    ''' (<see cref="PerspectiveGuideSolver"/> rechnet es, siehe dort, warum das verlustfrei geht).
    ''' Rezept, Renderer, Maskenweg, Anwenden und Objekte kennen die Linien deshalb nicht; neu ist nur,
    ''' woher die Ecken kommen. Mit EINER Linie steht das Ergebnis stattdessen im Regler Ausrichten,
    ''' wie bei "Linie ziehen" im Panel Drehen (siehe <see cref="SolvePerspectiveGuides"/>).</para>
    '''
    ''' <para>Die Linien liegen im Raum der Stufe VOR der offenen Perspektive und werden durch sie auf
    ''' das entzerrte Bild gezeichnet. Nach dem Loesen stehen sie also sichtbar senkrecht und
    ''' waagerecht, und man sieht sofort, ob es stimmt. Hin und zurueck geht es ueber dieselbe Matrix
    ''' wie bei <see cref="PerspectiveCornerValues"/> und dem Eckenzug.</para>
    '''
    ''' <para>Kein eigener Verzerrmodus: "Linien" ist schon die Linienverzerrung, eine zweite Sache
    ''' mit fast demselben Namen waere eine Falle. Der Umschalter sitzt in der Gruppe
    ''' Perspektive.</para></summary>
    Partial Public Class EditorViewModel

        Private ReadOnly _perspectiveGuides As New List(Of PerspectiveGuideLine)()
        Private _isPerspectiveGuideMode As Boolean
        ''' <summary>Die zuletzt gefasste oder gesetzte Linie, -1 fuer keine. ENTF nimmt sie weg.</summary>
        Private _perspectiveGuideSelected As Integer = -1
        ''' <summary>Der laufende Zug: welche Linie, und woran (0 Anfang, 1 Ende, 2 die ganze Linie,
        ''' 3 eine neue Linie, die gerade aufgezogen wird). -1 heisst: kein Zug.</summary>
        Private _perspectiveGuideDragIndex As Integer = -1
        Private _perspectiveGuideDragPart As Integer = -1
        Private _perspectiveGuideDragStartX As Double
        Private _perspectiveGuideDragStartY As Double
        Private _perspectiveGuideDragOrigin As PerspectiveGuideLine
        ''' <summary>Bildschirmpunkte je Anzeige-Prozent beim Zugbeginn, fuer die Mindestlaenge.</summary>
        Private _perspectiveGuideDragScreenWidth As Double
        Private _perspectiveGuideDragScreenHeight As Double
        ''' <summary>Der Rueckgaengig-Schritt des laufenden Zugs. Abgelegt wird er erst beim Loslassen
        ''' und nur, wenn der Zug etwas geaendert hat: ein Klick, der keine Linie ergibt, hinterlaesst
        ''' sonst einen Schritt, der nichts zuruecknimmt.</summary>
        Private _perspectiveGuideDragUndo As UndoEntry
        ''' <summary>Die Ausrichtung (<c>StraightenDegrees</c>) vor der ersten Linie. Mit EINER Linie
        ''' dreht die Ausrichtung; sind alle Linien wieder weg, kehrt sie hierher zurueck, so wie die
        ''' Ecken auf 0 gehen. Nothing, solange keine Linie liegt.</summary>
        Private _perspectiveGuideStraightenBase As Double?

        ''' <summary>Kuerzer als so viele Bildschirmpunkte ist ein Zug ein Klick und keine Linie -
        ''' dieselbe Schwelle wie beim Ausrichten mit einer Linie.</summary>
        Private Const MinPerspectiveGuideScreenLength As Double = 8.0

        ''' <summary>Wie weit ein Endpunkt ueber die Stufe hinaus liegen darf, in Prozent je Seite.</summary>
        Private Const PerspectiveGuideOutsidePercent As Double = 50.0

        ''' <summary>Der Umschalter "Perspektivlinien" der Gruppe Perspektive. Ist er an, liegen statt
        ''' der Eckanfasser die Linien ueber dem Bild. Ausschalten nimmt die Linien NICHT weg: das
        ''' Ergebnis steht in den Ecken, und die Linien kommen beim Wiedereinschalten zurueck, solange
        ''' niemand von Hand an Ecke oder Kipp-Regler gegriffen hat.</summary>
        Public Property IsPerspectiveGuideMode As Boolean
            Get
                Return _isPerspectiveGuideMode
            End Get
            Set(value As Boolean)
                If _isPerspectiveGuideMode = value Then Return
                CancelPerspectiveGuideDrag()
                Me.RaiseAndSetIfChanged(_isPerspectiveGuideMode, value)
                RaisePerspectiveGuidesChanged()
            End Set
        End Property

        ''' <summary>Liegen gerade die Linien ueber dem Bild und nehmen sie die Zuege an? Nur im
        ''' Werkzeug Verzerren, Modus Perspektive, mit eingeschaltetem Umschalter und am BILD: mit
        ''' markiertem Objekt gehoeren die Ecken dem Objekt, und dessen Verzerrung kennt keine
        ''' Linien.</summary>
        Public ReadOnly Property IsPerspectiveGuideActive As Boolean
            Get
                Return _isPerspectiveGuideMode AndAlso ShowWarpAdjustments AndAlso IsWarpPerspective AndAlso
                       Not WarpsTheObject
            End Get
        End Property

        Public ReadOnly Property PerspectiveGuideCount As Integer
            Get
                Return _perspectiveGuides.Count
            End Get
        End Property

        Public ReadOnly Property HasPerspectiveGuides As Boolean
            Get
                Return _perspectiveGuides.Count > 0
            End Get
        End Property

        ''' <summary>Die Linien fuer das Overlay, in ANZEIGE-Prozent: [Anzahl, gefasste Linie (-1 fuer
        ''' keine), dann je Linie x0, y0, x1, y1 und 1 fuer senkrecht, 0 fuer waagerecht].
        ''' Ein Endpunkt ohne Anzeigeort kommt als NaN und wird nicht gezeichnet.</summary>
        Public ReadOnly Property PerspectiveGuideValues As Double()
            Get
                Dim stepSize = WarpStepSize()
                Dim arr(1 + _perspectiveGuides.Count * 5) As Double
                arr(0) = _perspectiveGuides.Count
                arr(1) = If(_perspectiveGuideDragIndex >= 0, _perspectiveGuideDragIndex, _perspectiveGuideSelected)
                For i = 0 To _perspectiveGuides.Count - 1
                    Dim line = _perspectiveGuides(i)
                    Dim a = GuideStageToDisplay(line.X0, line.Y0)
                    Dim b = GuideStageToDisplay(line.X1, line.Y1)
                    Dim baseIndex = 2 + i * 5
                    arr(baseIndex) = If(a.HasValue, a.Value.X, Double.NaN)
                    arr(baseIndex + 1) = If(a.HasValue, a.Value.Y, Double.NaN)
                    arr(baseIndex + 2) = If(b.HasValue, b.Value.X, Double.NaN)
                    arr(baseIndex + 3) = If(b.HasValue, b.Value.Y, Double.NaN)
                    arr(baseIndex + 4) = If(line.IsVertical(stepSize.Width, stepSize.Height), 1.0, 0.0)
                Next
                Return arr
            End Get
        End Property

        ''' <summary>Die Linien selbst, in Prozent der Stufe vor der offenen Perspektive. Eine Kopie:
        ''' wer sie aendern will, geht ueber die Zuege oder <see cref="AddPerspectiveGuide"/>.</summary>
        Public ReadOnly Property PerspectiveGuideLines As IReadOnlyList(Of PerspectiveGuideLine)
            Get
                Return _perspectiveGuides.Select(Function(l) l.Clone()).ToList()
            End Get
        End Property

        Private Sub RaisePerspectiveGuidesChanged()
            Me.RaisePropertyChanged(NameOf(PerspectiveGuideValues))
            Me.RaisePropertyChanged(NameOf(PerspectiveGuideCount))
            Me.RaisePropertyChanged(NameOf(HasPerspectiveGuides))
            Me.RaisePropertyChanged(NameOf(IsPerspectiveGuideActive))
            ' Die Eckanfasser verschwinden, solange die Linien liegen.
            Me.RaisePropertyChanged(NameOf(PerspectiveCornerValues))
        End Sub

        Private Shared Function PerspectiveGuideHistoryLabel() As String
            Return LocalizationService.T("Verzerren") & ": " & LocalizationService.T("Geführte Perspektive")
        End Function

        ' ── Abbildung zwischen Anzeige und Stufe ────────────────────────────────

        ''' <summary>Die offene Perspektive als Matrix, mit Seitenverhaeltnis und Groesse - dieselbe,
        ''' die der Renderer nimmt und aus der <see cref="PerspectiveCornerValues"/> rechnet.</summary>
        Private Function CurrentPerspectiveMatrix(stepWidth As Double, stepHeight As Double) As SKMatrix
            Return ImageGeometryMapper.WarpMatrix(stepWidth, stepHeight,
                                                  _perspectiveHorizontal, _perspectiveVertical,
                                                  _perspectiveAspect, _perspectiveScale,
                                                  CType(_perspectiveCorners.Clone(), Double()))
        End Function

        ''' <summary>Ein Punkt der Stufe (Prozent, vor der Perspektive) an seiner Stelle in der
        ''' Anzeige (Prozent). Anzeige-Prozent und Stufen-Prozent haben dieselbe Bezugsgroesse.</summary>
        Private Function GuideStageToDisplay(xPercent As Double, yPercent As Double) As SKPoint?
            Dim stepSize = WarpStepSize()
            If stepSize.Width <= 0 OrElse stepSize.Height <= 0 Then Return Nothing
            Dim m = CurrentPerspectiveMatrix(stepSize.Width, stepSize.Height)
            Dim p = New SKPoint(CSng(xPercent / 100.0 * stepSize.Width), CSng(yPercent / 100.0 * stepSize.Height))
            If Not m.IsIdentity Then p = m.MapPoint(p)
            If Single.IsNaN(p.X) OrElse Single.IsNaN(p.Y) OrElse Single.IsInfinity(p.X) OrElse Single.IsInfinity(p.Y) Then Return Nothing
            Return New SKPoint(CSng(p.X / stepSize.Width * 100.0), CSng(p.Y / stepSize.Height * 100.0))
        End Function

        ''' <summary>Gegenrichtung zu <see cref="GuideStageToDisplay"/>: ueber die Umkehrung derselben
        ''' Matrix, also samt der Streckung um die Mitte.</summary>
        Private Function GuideDisplayToStage(xPercent As Double, yPercent As Double) As SKPoint?
            Dim stepSize = WarpStepSize()
            If stepSize.Width <= 0 OrElse stepSize.Height <= 0 Then Return Nothing
            Dim m = CurrentPerspectiveMatrix(stepSize.Width, stepSize.Height)
            Dim p = New SKPoint(CSng(xPercent / 100.0 * stepSize.Width), CSng(yPercent / 100.0 * stepSize.Height))
            If Not m.IsIdentity Then
                Dim inverse As SKMatrix
                If Not m.TryInvert(inverse) Then Return Nothing
                p = inverse.MapPoint(p)
            End If
            If Single.IsNaN(p.X) OrElse Single.IsNaN(p.Y) OrElse Single.IsInfinity(p.X) OrElse Single.IsInfinity(p.Y) Then Return Nothing
            Return New SKPoint(CSng(ClampGuidePercent(p.X / stepSize.Width * 100.0)),
                               CSng(ClampGuidePercent(p.Y / stepSize.Height * 100.0)))
        End Function

        Private Shared Function ClampGuidePercent(value As Double) As Double
            Return Math.Max(-PerspectiveGuideOutsidePercent, Math.Min(100.0 + PerspectiveGuideOutsidePercent, value))
        End Function

        ' ── Setzen, Fassen, Ziehen ──────────────────────────────────────────────

        ''' <summary>Ein Druck auf das Bild: fasst einen Endpunkt in Greifweite, sonst die Linie selbst
        ''' in ihrer Mitte, sonst beginnt er eine neue Linie. Alles im ANZEIGERAUM, damit die
        ''' Greifweite die auf dem Bildschirm ist. <paramref name="screenWidth"/> und
        ''' <paramref name="screenHeight"/> sind die Masse des angezeigten Bildes in Bildschirmpunkten,
        ''' fuer die Mindestlaenge einer neuen Linie.</summary>
        Public Function TryBeginPerspectiveGuideDrag(xPercent As Double, yPercent As Double,
                                                     slopXPercent As Double, slopYPercent As Double,
                                                     screenWidth As Double, screenHeight As Double) As Boolean
            If Not IsPerspectiveGuideActive Then Return False
            CancelPerspectiveGuideDrag()
            Dim values = PerspectiveGuideValues
            Dim bestDistance = Double.MaxValue
            Dim bestLine = -1, bestPart = -1
            ' Erst die Enden: sie liegen auf der Linie und haben deshalb Vorrang.
            For i = 0 To _perspectiveGuides.Count - 1
                For part = 0 To 1
                    Dim px = values(2 + i * 5 + part * 2), py = values(3 + i * 5 + part * 2)
                    If Double.IsNaN(px) OrElse Double.IsNaN(py) Then Continue For
                    Dim dx = (xPercent - px) / Math.Max(0.0001, slopXPercent)
                    Dim dy = (yPercent - py) / Math.Max(0.0001, slopYPercent)
                    Dim d = dx * dx + dy * dy
                    If d <= 1.0 AndAlso d < bestDistance Then
                        bestDistance = d
                        bestLine = i
                        bestPart = part
                    End If
                Next
            Next
            If bestLine < 0 Then
                For i = 0 To _perspectiveGuides.Count - 1
                    Dim ax = values(2 + i * 5), ay = values(3 + i * 5)
                    Dim bx = values(4 + i * 5), by = values(5 + i * 5)
                    If Double.IsNaN(ax) OrElse Double.IsNaN(ay) OrElse Double.IsNaN(bx) OrElse Double.IsNaN(by) Then Continue For
                    Dim d = DistanceToSegment(xPercent, yPercent, ax, ay, bx, by, slopXPercent, slopYPercent)
                    If d <= 1.0 AndAlso d < bestDistance Then
                        bestDistance = d
                        bestLine = i
                        bestPart = 2
                    End If
                Next
            End If

            _perspectiveGuideDragScreenWidth = screenWidth
            _perspectiveGuideDragScreenHeight = screenHeight
            _perspectiveGuideDragStartX = xPercent
            _perspectiveGuideDragStartY = yPercent

            If bestLine >= 0 Then
                _perspectiveGuideDragUndo = PrepareUndo(PerspectiveGuideHistoryLabel())
                _perspectiveGuideDragIndex = bestLine
                _perspectiveGuideDragPart = bestPart
                _perspectiveGuideDragOrigin = _perspectiveGuides(bestLine).Clone()
                _perspectiveGuideSelected = bestLine
                RaisePerspectiveGuidesChanged()
                Return True
            End If

            ' Eine neue Linie nur auf dem Bild selbst: ausserhalb liegt keine Kante.
            If xPercent < 0 OrElse xPercent > 100 OrElse yPercent < 0 OrElse yPercent > 100 Then Return False
            If _perspectiveGuides.Count >= PerspectiveGuideSolver.MaxLines Then
                StatusText = LocalizationService.T("Höchstens vier Perspektivlinien")
                Return False
            End If
            Dim start = GuideDisplayToStage(xPercent, yPercent)
            If Not start.HasValue Then Return False
            _perspectiveGuideDragUndo = PrepareUndo(PerspectiveGuideHistoryLabel())
            _perspectiveGuides.Add(New PerspectiveGuideLine(start.Value.X, start.Value.Y, start.Value.X, start.Value.Y))
            _perspectiveGuideDragIndex = _perspectiveGuides.Count - 1
            _perspectiveGuideDragPart = 3
            _perspectiveGuideDragOrigin = _perspectiveGuides(_perspectiveGuideDragIndex).Clone()
            _perspectiveGuideSelected = _perspectiveGuideDragIndex
            RaisePerspectiveGuidesChanged()
            Return True
        End Function

        ''' <summary>Zieht die gefasste Linie. Gerechnet wird NICHT waehrend des Zugs: die Abbildung,
        ''' durch die die Linie gezeichnet wird, haenge sonst am Endpunkt, den man gerade zieht, und
        ''' der Inhalt liefe unter dem Zeiger weg. Geloest wird beim Loslassen - dasselbe Muster wie
        ''' "Automatisch fuellen" am Eckenzug.</summary>
        Public Sub UpdatePerspectiveGuideDrag(xPercent As Double, yPercent As Double)
            If _perspectiveGuideDragIndex < 0 OrElse _perspectiveGuideDragIndex >= _perspectiveGuides.Count Then Return
            Dim line = _perspectiveGuides(_perspectiveGuideDragIndex)
            Dim origin = _perspectiveGuideDragOrigin
            Select Case _perspectiveGuideDragPart
                Case 0
                    Dim p = GuideDisplayToStage(xPercent, yPercent)
                    If Not p.HasValue Then Return
                    line.X0 = p.Value.X : line.Y0 = p.Value.Y
                Case 1, 3
                    Dim p = GuideDisplayToStage(xPercent, yPercent)
                    If Not p.HasValue Then Return
                    line.X1 = p.Value.X : line.Y1 = p.Value.Y
                Case 2
                    ' Die ganze Linie: die Verschiebung gilt im ANZEIGERAUM und wird fuer beide Enden
                    ' einzeln zurueckgerechnet. Ein Versatz in Stufen-Prozent waere auf entzerrtem
                    ' Bild eine andere Richtung als die, in die man zieht.
                    Dim a = GuideStageToDisplay(origin.X0, origin.Y0)
                    Dim b = GuideStageToDisplay(origin.X1, origin.Y1)
                    If Not a.HasValue OrElse Not b.HasValue Then Return
                    Dim dx = xPercent - _perspectiveGuideDragStartX
                    Dim dy = yPercent - _perspectiveGuideDragStartY
                    Dim na = GuideDisplayToStage(a.Value.X + dx, a.Value.Y + dy)
                    Dim nb = GuideDisplayToStage(b.Value.X + dx, b.Value.Y + dy)
                    If Not na.HasValue OrElse Not nb.HasValue Then Return
                    line.X0 = na.Value.X : line.Y0 = na.Value.Y
                    line.X1 = nb.Value.X : line.Y1 = nb.Value.Y
                Case Else
                    Return
            End Select
            RaisePerspectiveGuidesChanged()
        End Sub

        ''' <summary>Ende des Zugs: eine zu kurz gebliebene neue Linie war ein Klick und faellt weg,
        ''' ohne einen Schritt zu hinterlassen. Sonst wird der Schritt abgelegt und geloest.</summary>
        Public Sub EndPerspectiveGuideDrag()
            If _perspectiveGuideDragIndex < 0 Then Return
            Dim index = _perspectiveGuideDragIndex
            Dim part = _perspectiveGuideDragPart
            Dim undo = _perspectiveGuideDragUndo
            Dim origin = _perspectiveGuideDragOrigin
            _perspectiveGuideDragIndex = -1
            _perspectiveGuideDragPart = -1
            _perspectiveGuideDragUndo = Nothing
            _perspectiveGuideDragOrigin = Nothing
            If index >= _perspectiveGuides.Count Then
                RaisePerspectiveGuidesChanged()
                Return
            End If
            Dim line = _perspectiveGuides(index)
            If part = 3 AndAlso Not IsLongEnoughOnScreen(line) Then
                _perspectiveGuides.RemoveAt(index)
                _perspectiveGuideSelected = -1
                RaisePerspectiveGuidesChanged()
                Return
            End If
            Dim moved = part = 3 OrElse
                        Math.Abs(line.X0 - origin.X0) > 0.000001 OrElse Math.Abs(line.Y0 - origin.Y0) > 0.000001 OrElse
                        Math.Abs(line.X1 - origin.X1) > 0.000001 OrElse Math.Abs(line.Y1 - origin.Y1) > 0.000001
            If Not moved Then
                RaisePerspectiveGuidesChanged()
                Return
            End If
            CommitPreparedUndo(undo)
            SolvePerspectiveGuides()
        End Sub

        ''' <summary>Bricht einen laufenden Zug ab: die Linie kommt auf ihren Stand vom Zugbeginn
        ''' zurueck, eine gerade aufgezogene faellt weg, und es bleibt kein Schritt.</summary>
        Private Sub CancelPerspectiveGuideDrag()
            If _perspectiveGuideDragIndex < 0 Then Return
            Dim index = _perspectiveGuideDragIndex
            If index < _perspectiveGuides.Count Then
                If _perspectiveGuideDragPart = 3 Then
                    _perspectiveGuides.RemoveAt(index)
                    _perspectiveGuideSelected = -1
                ElseIf _perspectiveGuideDragOrigin IsNot Nothing Then
                    _perspectiveGuides(index) = _perspectiveGuideDragOrigin
                End If
            End If
            _perspectiveGuideDragIndex = -1
            _perspectiveGuideDragPart = -1
            _perspectiveGuideDragUndo = Nothing
            _perspectiveGuideDragOrigin = Nothing
            RaisePerspectiveGuidesChanged()
        End Sub

        ''' <summary>Ist die Linie auf dem Schirm mindestens <see cref="MinPerspectiveGuideScreenLength"/>
        ''' Bildschirmpunkte lang? Ohne Bildschirmmasse (headless) zaehlt ein Prozent der Stufe.</summary>
        Private Function IsLongEnoughOnScreen(line As PerspectiveGuideLine) As Boolean
            Dim a = GuideStageToDisplay(line.X0, line.Y0)
            Dim b = GuideStageToDisplay(line.X1, line.Y1)
            If Not a.HasValue OrElse Not b.HasValue Then Return False
            Dim dx = (b.Value.X - a.Value.X) / 100.0
            Dim dy = (b.Value.Y - a.Value.Y) / 100.0
            If _perspectiveGuideDragScreenWidth > 0 AndAlso _perspectiveGuideDragScreenHeight > 0 Then
                dx *= _perspectiveGuideDragScreenWidth
                dy *= _perspectiveGuideDragScreenHeight
                Return Math.Sqrt(dx * dx + dy * dy) >= MinPerspectiveGuideScreenLength
            End If
            Return Math.Sqrt(dx * dx + dy * dy) >= 0.01
        End Function

        ''' <summary>Legt eine Linie von (x0, y0) nach (x1, y1) in ANZEIGE-Prozent an und loest - ein
        ''' eigener Rueckgaengig-Schritt. Derselbe Weg wie ein Zug auf dem Bild, nur ohne Zeiger.
        ''' False, wenn schon vier Linien liegen oder ein Punkt keinen Platz in der Stufe hat.</summary>
        Public Function AddPerspectiveGuide(startXPercent As Double, startYPercent As Double,
                                            endXPercent As Double, endYPercent As Double) As Boolean
            CancelPerspectiveGuideDrag()
            If _perspectiveGuides.Count >= PerspectiveGuideSolver.MaxLines Then
                StatusText = LocalizationService.T("Höchstens vier Perspektivlinien")
                Return False
            End If
            Dim a = GuideDisplayToStage(startXPercent, startYPercent)
            Dim b = GuideDisplayToStage(endXPercent, endYPercent)
            If Not a.HasValue OrElse Not b.HasValue Then Return False
            PushUndo(PerspectiveGuideHistoryLabel())
            _perspectiveGuides.Add(New PerspectiveGuideLine(a.Value.X, a.Value.Y, b.Value.X, b.Value.Y))
            _perspectiveGuideSelected = _perspectiveGuides.Count - 1
            SolvePerspectiveGuides()
            Return True
        End Function

        ''' <summary>ENTF: die gefasste oder zuletzt gesetzte Linie entfernen.</summary>
        Public Sub DeleteSelectedPerspectiveGuide()
            If _perspectiveGuides.Count = 0 Then Return
            Dim index = If(_perspectiveGuideSelected >= 0 AndAlso _perspectiveGuideSelected < _perspectiveGuides.Count,
                           _perspectiveGuideSelected, _perspectiveGuides.Count - 1)
            RemovePerspectiveGuideAt(index)
        End Sub

        ''' <summary>Der Knopf "Letzte Linie entfernen".</summary>
        Public Sub RemoveLastPerspectiveGuide()
            If _perspectiveGuides.Count = 0 Then Return
            RemovePerspectiveGuideAt(_perspectiveGuides.Count - 1)
        End Sub

        Private Sub RemovePerspectiveGuideAt(index As Integer)
            CancelPerspectiveGuideDrag()
            If index < 0 OrElse index >= _perspectiveGuides.Count Then Return
            PushUndo(PerspectiveGuideHistoryLabel())
            _perspectiveGuides.RemoveAt(index)
            _perspectiveGuideSelected = -1
            SolvePerspectiveGuides()
        End Sub

        ''' <summary>Der Knopf "Alle Linien entfernen": ohne Linien stehen die Ecken wieder bei 0.</summary>
        Public Sub ResetPerspectiveGuides()
            CancelPerspectiveGuideDrag()
            If _perspectiveGuides.Count = 0 Then Return
            PushUndo(PerspectiveGuideHistoryLabel())
            _perspectiveGuides.Clear()
            _perspectiveGuideSelected = -1
            SolvePerspectiveGuides()
        End Sub

        ''' <summary>Rechnet die Linien in die Geometrie.
        '''
        ''' <para>AB ZWEI LINIEN in die Ecken: die beiden Kipp-Regler gehen auf 0, die Ecken auf die
        ''' Loesung - ohne die Begrenzung von <see cref="SetCornerOffset"/>, die Rechnung begrenzt
        ''' selbst. Seitenverhaeltnis und Groesse bleiben: sie wirken NACH der Kippung als Streckung um
        ''' die Mitte und lassen senkrechte Linien senkrecht. Ergibt sich keine gueltige Entzerrung,
        ''' bleibt der letzte Stand, und die Fusszeile sagt warum.</para>
        '''
        ''' <para>MIT EINER LINIE in die Ausrichtung, wie "Linie ziehen" im Panel Drehen: Ecken und
        ''' Kipp-Regler auf 0, <c>StraightenDegrees</c> nach derselben Regel
        ''' (<see cref="StraightenFromSinglePerspectiveGuide"/>). Eine Linie legt nur einen Winkel fest,
        ''' und genau dafuer gibt es den Regler; in den Ecken stuende dieselbe Drehung als Homographie,
        ''' die niemand als Drehung wiedererkennt.</para>
        '''
        ''' <para>OHNE LINIEN stehen die Ecken bei 0 und die Ausrichtung wieder dort, wo sie vor der
        ''' ersten Linie stand.</para></summary>
        Private Sub SolvePerspectiveGuides()
            Dim stepSize = WarpStepSize()
            If stepSize.Width <= 0 OrElse stepSize.Height <= 0 Then
                RaisePerspectiveGuidesChanged()
                Return
            End If
            ' Der Stand vor der ersten Linie: hierher kehrt die Ausrichtung zurueck, wenn alle weg sind.
            If _perspectiveGuides.Count > 0 AndAlso Not _perspectiveGuideStraightenBase.HasValue Then
                _perspectiveGuideStraightenBase = _straightenDegrees
            End If
            If _perspectiveGuides.Count = 0 Then
                WriteGuideCorners(New Double(7) {})
                If _perspectiveGuideStraightenBase.HasValue Then
                    SetPerspectiveGuideStraighten(_perspectiveGuideStraightenBase.Value)
                    _perspectiveGuideStraightenBase = Nothing
                End If
            ElseIf _perspectiveGuides.Count = 1 Then
                ' Erst die Ecken weg (die Linie liegt VOR der Perspektive, ihre Lage aendert sich
                ' dabei nicht), dann die Ausrichtung - sie misst die Linie in der Stufe.
                WriteGuideCorners(New Double(7) {})
                StraightenFromSinglePerspectiveGuide()
            Else
                ' Ab zwei Linien loest die Perspektive auf dem Bild, wie es die Ausrichtung einer
                ' ersten Linie hinterlassen hat. Die Linien liegen in genau dieser Stufe; die Drehung
                ' zaehlt also nicht doppelt, und die Rechnung ist gegen eine Drehung um die Mitte
                ' unempfindlich (sie macht die Richtungen zu den Fluchtpunkten zu den Achsen, egal wie
                ' sie vorher lagen). Die Ausrichtung zurueckzunehmen hiesse dagegen, dass das Bild beim
                ' Setzen der zweiten Linie einmal zurueck und wieder hin dreht.
                Dim solution = PerspectiveGuideSolver.Solve(_perspectiveGuides, stepSize.Width, stepSize.Height)
                If solution.IsValid Then
                    WriteGuideCorners(solution.CornerOffset)
                    If solution.IsLimited Then
                        StatusText = LocalizationService.T("Die Perspektivlinien verlangen zu viel, die Entzerrung wurde begrenzt")
                    End If
                ElseIf solution.Failure = PerspectiveGuideFailure.Contradictory Then
                    StatusText = LocalizationService.T("Die Perspektivlinien widersprechen sich")
                Else
                    StatusText = LocalizationService.T("Aus diesen Perspektivlinien ergibt sich keine gültige Entzerrung")
                End If
            End If
            RaisePerspectiveGuidesChanged()
        End Sub

        ''' <summary>Kipp-Regler auf 0, die Ecken auf <paramref name="offsets"/>.</summary>
        Private Sub WriteGuideCorners(offsets As Double())
            _perspectiveVertical = 0
            _perspectiveHorizontal = 0
            For i = 0 To 7
                _perspectiveCorners(i) = offsets(i)
            Next
            Me.RaisePropertyChanged(NameOf(PerspectiveVertical))
            Me.RaisePropertyChanged(NameOf(PerspectiveHorizontal))
            ApplyPerspectiveAutoFill()
            RaiseCornersChanged()
            RaisePerspectiveStateChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Mit EINER Linie: die Ausrichtung so stellen, dass sie waagerecht steht, oder
        ''' senkrecht, wenn sie steiler als 45 Grad liegt. Dieselbe Regel wie
        ''' <see cref="ApplyStraightenFromLine"/>: neuer Wert = alter Wert minus Schraegstand, auf
        ''' Hundertstelgrad, unter 0,05 Grad nichts.
        '''
        ''' <para>Gemessen wird in Bildpunkten der Stufe, also vor Seitenverhaeltnis und Groesse der
        ''' offenen Perspektive: die strecken nur entlang der Achsen und lassen eine waagerechte Linie
        ''' waagerecht. Ohne beides ist das genau der Winkel auf dem Schirm.</para>
        '''
        ''' <para>Die Linie wandert mit der Stufe (<see cref="RemapPerspectiveGuidesAround"/>) und
        ''' bleibt am Motiv. Danach wird nachgemessen: steht hinter der Ausrichtung noch ein
        ''' bestaetigter projektiver Schritt, ist eine Drehung davor auf dem Schirm keine reine
        ''' Drehung, und ein zweiter Schritt holt den Rest. In der ueblichen Kette geht schon der erste
        ''' auf den Hundertstelgrad genau auf.</para></summary>
        Private Sub StraightenFromSinglePerspectiveGuide()
            Dim lastTilt = Double.MaxValue
            For iteration = 1 To 4
                Dim stepSize = WarpStepSize()
                If stepSize.Width <= 0 OrElse stepSize.Height <= 0 OrElse _perspectiveGuides.Count <> 1 Then Return
                Dim line = _perspectiveGuides(0)
                Dim dx = (line.X1 - line.X0) / 100.0 * stepSize.Width
                Dim dy = (line.Y1 - line.Y0) / 100.0 * stepSize.Height
                If Math.Sqrt(dx * dx + dy * dy) < 0.000001 Then Return
                Dim angle = Math.Atan2(dy, dx) * 180.0 / Math.PI
                Dim tilt = angle - Math.Round(angle / 90.0) * 90.0
                ' Die Schwelle von "Linie ziehen" beim ersten Mal; das Nachmessen geht feiner.
                If Math.Abs(tilt) < If(iteration = 1, 0.05, 0.005) Then Return
                ' Wird der Rest nicht kleiner, hilft auch ein weiterer Schritt nicht.
                If Math.Abs(tilt) >= lastTilt Then Return
                lastTilt = Math.Abs(tilt)
                Dim target = _straightenDegrees - tilt
                target = ((target + 180.0) Mod 360.0 + 360.0) Mod 360.0 - 180.0
                target = Math.Round(target, 2)
                If Math.Abs(target - _straightenDegrees) < 0.0001 Then Return
                SetPerspectiveGuideStraighten(target)
            Next
        End Sub

        ''' <summary>Setzt die Ausrichtung fuer die Perspektivlinien und uebernimmt sie gleich in den
        ''' Drehschritt, an der Stelle des vertretenen (wie "Anwenden" im Panel Drehen). Ohne eigenen
        ''' Rueckgaengig-Schritt: der Zug an der Linie hat den Stand davor schon gesichert. Die Linien
        ''' wandern mit der Stufe.
        '''
        ''' <para>UEBERNOMMEN, nicht offen gelassen: eine offene Drehung gehoert dem Werkzeug
        ''' Transformieren und wuerde beim Verlassen DORT verworfen; hier im Werkzeug Verzerren gibt es
        ''' dafuer keinen Knopf. So steht der Winkel wie nach "Linie ziehen" und "Anwenden" im Rezept,
        ''' im Regler "Ausrichten" und in der Geometrie-Uebertragung beim Einfuegen.</para></summary>
        Private Sub SetPerspectiveGuideStraighten(value As Double)
            Dim clamped = Math.Round(Math.Max(-180, Math.Min(180, value)), 2)
            If Math.Abs(_straightenDegrees - clamped) < 0.0001 Then Return
            RemapPerspectiveGuidesAround(
                Sub()
                    _straightenDegrees = clamped
                    CommitOpenTransform()
                End Sub)
            RaiseRotateFieldsChanged()
            _hasChanges = True
            RaiseResetButtonStateChanged()
            RaiseDisplayImageGeometryProperties()
            ApplyPerspectiveAutoFill()
            RaiseCornersChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Die Kette, deren Ausgabe die Stufe der Perspektivlinien ist: die angezeigte
        ''' Geometrie ohne die offene Perspektive (sie haengt als letzter Schritt ihrer Art an, siehe
        ''' <see cref="AppendOpenGeometryOperationsWithoutTransform"/>).</summary>
        Private Function PerspectiveGuideStageAdjustments() As ImageAdjustments
            Dim adj = BuildAppliedGeometryAdjustments()
            If HasPerspectiveChanges AndAlso adj.GeometryOperations IsNot Nothing Then
                For index = adj.GeometryOperations.Count - 1 To 0 Step -1
                    If String.Equals(adj.GeometryOperations(index)?.Kind, "perspective", StringComparison.OrdinalIgnoreCase) Then
                        adj.GeometryOperations.RemoveAt(index)
                        Exit For
                    End If
                Next
            End If
            Return adj
        End Function

        ''' <summary>Fuehrt <paramref name="change"/> aus und rechnet die Linien in die neue Stufe um:
        ''' vorher jeden Endpunkt durch die alte Kette ins Quellbild, danach durch die neue zurueck. So
        ''' liegt eine Linie nach einer Drehung oder einem neuen Zuschnitt der Begradigung sichtbar am
        ''' selben Motiv, und eine zweite Linie kommt in derselben Stufe dazu. Ein Punkt ohne Ort
        ''' behaelt seinen alten Wert.</summary>
        Private Sub RemapPerspectiveGuidesAround(change As Action)
            Dim baseWidth = GetBaseWidth(), baseHeight = GetBaseHeight()
            If _perspectiveGuides.Count = 0 OrElse baseWidth <= 0 OrElse baseHeight <= 0 Then
                change()
                Return
            End If
            Dim before = PerspectiveGuideStageAdjustments()
            Dim beforeSize = ImageProcessor.ComputeGeometryOutputSize(baseWidth, baseHeight, before)
            Dim sources As New List(Of SKPoint?)()
            For Each line In _perspectiveGuides
                For Each p In {(line.X0, line.Y0), (line.X1, line.Y1)}
                    Dim source As SKPoint
                    If beforeSize.Width > 0 AndAlso beforeSize.Height > 0 AndAlso
                       ImageProcessor.TryGeometryOutputToSourcePoint(p.Item1 / 100.0 * beforeSize.Width, p.Item2 / 100.0 * beforeSize.Height,
                                                                     baseWidth, baseHeight, before, source, allowOutside:=True) Then
                        sources.Add(source)
                    Else
                        sources.Add(Nothing)
                    End If
                Next
            Next
            change()
            Dim after = PerspectiveGuideStageAdjustments()
            Dim afterSize = ImageProcessor.ComputeGeometryOutputSize(baseWidth, baseHeight, after)
            If afterSize.Width <= 0 OrElse afterSize.Height <= 0 Then Return
            For i = 0 To _perspectiveGuides.Count - 1
                Dim line = _perspectiveGuides(i)
                For part = 0 To 1
                    Dim source = sources(i * 2 + part)
                    If Not source.HasValue Then Continue For
                    Dim output As SKPoint
                    If Not ImageProcessor.TrySourcePointToGeometryOutputUnclipped(source.Value.X, source.Value.Y,
                                                                                  baseWidth, baseHeight, after, output) Then Continue For
                    Dim x = ClampGuidePercent(output.X / afterSize.Width * 100.0)
                    Dim y = ClampGuidePercent(output.Y / afterSize.Height * 100.0)
                    If part = 0 Then
                        line.X0 = x : line.Y0 = y
                    Else
                        line.X1 = x : line.Y1 = y
                    End If
                Next
            Next
        End Sub

        ''' <summary>Ein Griff von Hand an Ecke oder Kipp-Regler nimmt die Linien weg; die Ecken
        ''' bleiben, wie sie sind, und lassen sich nachziehen. Sonst gaebe es zwei Wahrheiten fuer
        ''' dieselben acht Zahlen, und die naechste Linie wuerfe den Griff von Hand still weg.
        ''' Gerufen NACH dem Sichern des Rueckgaengig-Schritts, damit STRG+Z die Linien mit
        ''' zurueckholt.</summary>
        Private Sub DropPerspectiveGuidesForManualGrip()
            If _perspectiveGuides.Count = 0 Then Return
            CancelPerspectiveGuideDrag()
            _perspectiveGuides.Clear()
            _perspectiveGuideSelected = -1
            _perspectiveGuideStraightenBase = Nothing
            RaisePerspectiveGuidesChanged()
            StatusText = LocalizationService.T("Perspektivlinien entfernt, die Ecken bleiben und lassen sich nachziehen")
        End Sub

        ''' <summary>Die Linien still wegnehmen, ohne eigenen Schritt: Zuruecksetzen, Anwenden,
        ''' Werkzeugwechsel und ein neues Bild. Gibt zurueck, ob welche da waren.</summary>
        Private Function ClearPerspectiveGuides() As Boolean
            Dim had = _perspectiveGuides.Count > 0
            _perspectiveGuideDragIndex = -1
            _perspectiveGuideDragPart = -1
            _perspectiveGuideDragUndo = Nothing
            _perspectiveGuideDragOrigin = Nothing
            _perspectiveGuides.Clear()
            _perspectiveGuideSelected = -1
            _perspectiveGuideStraightenBase = Nothing
            RaisePerspectiveGuidesChanged()
            Return had
        End Function

        ''' <summary>Fuer den Rueckgaengig-Schritt: eine Kopie der Linien, oder Nothing ohne Linien.</summary>
        Private Function CapturePerspectiveGuides() As List(Of PerspectiveGuideLine)
            If _perspectiveGuides.Count = 0 Then Return Nothing
            Return _perspectiveGuides.Select(Function(l) l.Clone()).ToList()
        End Function

        ''' <summary>Holt die Linien eines Rueckgaengig-Schritts zurueck; Nothing heisst: damals lagen
        ''' keine. Ein laufender Zug wird abgebrochen.</summary>
        Private Sub RestorePerspectiveGuides(lines As List(Of PerspectiveGuideLine), straightenBase As Double?)
            _perspectiveGuideDragIndex = -1
            _perspectiveGuideDragPart = -1
            _perspectiveGuideDragUndo = Nothing
            _perspectiveGuideDragOrigin = Nothing
            _perspectiveGuides.Clear()
            If lines IsNot Nothing Then _perspectiveGuides.AddRange(lines.Select(Function(l) l.Clone()))
            _perspectiveGuideStraightenBase = If(_perspectiveGuides.Count > 0, straightenBase, Nothing)
            If _perspectiveGuideSelected >= _perspectiveGuides.Count Then _perspectiveGuideSelected = -1
            RaisePerspectiveGuidesChanged()
        End Sub

        ' ── Befehle fuer das Panel ──────────────────────────────────────────────

        Private _removeLastPerspectiveGuideCommand As ICommand
        Private _resetPerspectiveGuidesCommand As ICommand

        Public ReadOnly Property RemoveLastPerspectiveGuideCommand As ICommand
            Get
                If _removeLastPerspectiveGuideCommand Is Nothing Then
                    _removeLastPerspectiveGuideCommand = ReactiveCommand.Create(Sub() RemoveLastPerspectiveGuide())
                End If
                Return _removeLastPerspectiveGuideCommand
            End Get
        End Property

        Public ReadOnly Property ResetPerspectiveGuidesCommand As ICommand
            Get
                If _resetPerspectiveGuidesCommand Is Nothing Then
                    _resetPerspectiveGuidesCommand = ReactiveCommand.Create(Sub() ResetPerspectiveGuides())
                End If
                Return _resetPerspectiveGuidesCommand
            End Get
        End Property

    End Class

End Namespace
