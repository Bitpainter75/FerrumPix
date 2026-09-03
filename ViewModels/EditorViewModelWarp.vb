Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Controls
Imports SkiaSharp
Imports ReactiveUI
Imports FerrumPix.Services
Imports FerrumPix.Models

Namespace ViewModels

    ''' <summary>Verzerren: Perspektive, Stuetzpunktraster, Linienverzerrung und das Verformen mit
    ''' kurvigen Raendern - fuer das BILD wie fuer ein markiertes Objekt, samt Live-Vorschau und dem
    ''' Raumwechsel zwischen beiden.
    '''
    ''' Dritte Scheibe der Dateiaufteilung (2026-08-04), Regeln wie in
    ''' <c>ViewModels/EditorViewModelMask.vb</c>: entlang des ZUSTANDS geschnitten, reiner
    ''' TEXTumzug, Kontrolle ueber die Zaehlung vor und nach dem Schnitt.
    '''
    ''' Der Vorschaukanal (<c>ToolPreviewImage</c>) liegt mit hier, obwohl ihn auch die
    ''' Tiefen-Unschaerfe benutzt: sein Zustand gehoert zu dieser Vorschau-Maschinerie, und beide
    ''' Werkzeuge koennen ohnehin nie gleichzeitig laufen.</summary>
    Partial Public Class EditorViewModel

        ' ── Verzerren ───────────────────────────────────────────────────────────
        '
        ' Vier Werte, alle -100..100 und bei 0 wirkungslos. Sie gehoeren zur GEOMETRIE: sie
        ' veraendern, wo ein Bildpunkt landet, nicht seine Farbe. Deshalb laufen sie ueber
        ' SetUndoableGeometryDouble und nicht ueber den gewoehnlichen Reglerweg.

        Private _perspectiveHorizontal As Double = 0
        Private _perspectiveVertical As Double = 0
        Private _perspectiveAspect As Double = 0
        Private _perspectiveScale As Double = 0

        Public Property PerspectiveHorizontal As Double
            Get
                Return _perspectiveHorizontal
            End Get
            Set(value As Double)
                SetUndoableDouble(_perspectiveHorizontal, Math.Max(-100, Math.Min(100, value)),
                                  NameOf(PerspectiveHorizontal))
                Me.RaisePropertyChanged(NameOf(HasPerspectiveWarning))
                Me.RaisePropertyChanged(NameOf(HasAnyImageWarp))
            End Set
        End Property

        Public Property PerspectiveVertical As Double
            Get
                Return _perspectiveVertical
            End Get
            Set(value As Double)
                SetUndoableDouble(_perspectiveVertical, Math.Max(-100, Math.Min(100, value)),
                                  NameOf(PerspectiveVertical))
                Me.RaisePropertyChanged(NameOf(HasPerspectiveWarning))
                Me.RaisePropertyChanged(NameOf(HasAnyImageWarp))
            End Set
        End Property

        Public Property PerspectiveAspect As Double
            Get
                Return _perspectiveAspect
            End Get
            Set(value As Double)
                SetUndoableDouble(_perspectiveAspect, Math.Max(-100, Math.Min(100, value)),
                                  NameOf(PerspectiveAspect))
                Me.RaisePropertyChanged(NameOf(HasPerspectiveWarning))
                Me.RaisePropertyChanged(NameOf(HasAnyImageWarp))
            End Set
        End Property

        Public Property PerspectiveScale As Double
            Get
                Return _perspectiveScale
            End Get
            Set(value As Double)
                SetUndoableDouble(_perspectiveScale, Math.Max(-100, Math.Min(100, value)),
                                  NameOf(PerspectiveScale))
                Me.RaisePropertyChanged(NameOf(HasPerspectiveWarning))
                Me.RaisePropertyChanged(NameOf(HasAnyImageWarp))
            End Set
        End Property

        ''' <summary>Der Hinweis erscheint nur, wenn er zutrifft: es wird gekippt UND es gibt
        ''' Objekte, die dabei nicht mitwandern. Ein Hinweis, der immer dasteht, wird nicht
        ''' gelesen. Die freien Eckversaetze kippen das Bild genauso wie die beiden Regler und
        ''' zaehlen deshalb mit; wer nur die Ecken zieht, bekam die Warnung sonst nie.</summary>
        Public ReadOnly Property HasPerspectiveWarning As Boolean
            Get
                Dim tilts = Math.Abs(_perspectiveHorizontal) >= 0.01 OrElse
                            Math.Abs(_perspectiveVertical) >= 0.01 OrElse
                            _perspectiveCorners.Any(Function(v) Math.Abs(v) > 0.0001)
                If Not tilts Then Return False
                Return _annotations IsNot Nothing AndAlso _annotations.Count > 0
            End Get
        End Property

        Private Sub ResetPerspectiveInternal()
            Dim hasCommitted = HasCommittedPerspective()
            If Not HasPerspectiveChanges AndAlso Not hasCommitted Then Return
            CaptureUndoState("Verzerren")
            ResetCommittedPerspective()
            _perspectiveHorizontal = 0
            _perspectiveVertical = 0
            _perspectiveAspect = 0
            _perspectiveScale = 0
            Array.Clear(_perspectiveCorners, 0, _perspectiveCorners.Length)
            For Each n In {NameOf(PerspectiveHorizontal), NameOf(PerspectiveVertical),
                           NameOf(PerspectiveAspect), NameOf(PerspectiveScale)}
                Me.RaisePropertyChanged(n)
            Next
            RaiseCornersChanged()
            ' Eine bestaetigte Perspektive steht im Rezept; sie herauszunehmen ist eine Aenderung
            ' am Dokument und muss wie jede andere zum Speichern auffordern.
            _hasChanges = True
            SchedulePreviewUpdate()
        End Sub

        ' ── Welche Verzerrung gerade bedient wird ───────────────────────────────
        '
        ' Beide Overlays gleichzeitig ueber dem Bild waren ein Fehler: die Eck-Anfasser und die
        ' Rasterpunkte liegen teils uebereinander, und man sieht zwei Werkzeuge, von denen man eines
        ' meint. Erst waehlen, dann erscheint genau dessen Overlay. Ohne Wahl liegt nichts ueber dem
        ' Bild - das ist auch der Zustand, in dem man Zoom und Verschieben ungestoert bedienen kann.

        Private _warpMode As String = ""

        ''' <summary>"" = keines, "Perspektive", "Gitter", "Linien" oder "Verformen".</summary>
        Public Property WarpMode As String
            Get
                Return _warpMode
            End Get
            Set(value As String)
                Dim v = If(value, "").Trim()
                If Not (v = "Perspektive" OrElse v = "Gitter" OrElse v = "Linien" OrElse
                        v = "Verformen") Then v = ""
                ' Nochmal auf denselben Knopf: wieder abwaehlen. So wird man das Overlay los, ohne
                ' das Werkzeug zu verlassen.
                If String.Equals(_warpMode, v, StringComparison.Ordinal) Then v = ""
                If String.Equals(_warpMode, v, StringComparison.Ordinal) Then Return
                _warpMode = v
                ' Ein laufender Zug gehoert zum abgewaehlten Werkzeug und endet hier.
                _perspectiveCornerDrag = -1
                _warpDragIndex = -1
                _linienDragIndex = -1
                _linienDragTeil = -1
                _envelopeDragIndex = -1
                DisposeGridPreview()
                DisposeLinePreview()
                For Each n In {NameOf(WarpMode), NameOf(IsWarpPerspective),
                               NameOf(IsWarpGrid), NameOf(IsWarpLines), NameOf(IsWarpEnvelope),
                               NameOf(PerspectiveCornerValues), NameOf(LineValues),
                               NameOf(WarpGridValues), NameOf(EnvelopeValues),
                               NameOf(EnvelopeMeshValues), NameOf(HasEnvelopeChanges)}
                    Me.RaisePropertyChanged(n)
                Next
                ' Ein Modus ohne Verzerren-Wahl verzerrt gar nichts - damit wechselt auch der Raum,
                ' in dem Gitter und Linien liegen.
                RaiseObjectWarpChanged()
                RefreshWarpSpace()
            End Set
        End Property

        Public ReadOnly Property IsWarpPerspective As Boolean
            Get
                Return String.Equals(_warpMode, "Perspektive", StringComparison.Ordinal)
            End Get
        End Property

        Public ReadOnly Property IsWarpLines As Boolean
            Get
                Return String.Equals(_warpMode, "Linien", StringComparison.Ordinal)
            End Get
        End Property

        Public ReadOnly Property IsWarpGrid As Boolean
            Get
                Return String.Equals(_warpMode, "Gitter", StringComparison.Ordinal)
            End Get
        End Property

        Public ReadOnly Property IsWarpEnvelope As Boolean
            Get
                Return String.Equals(_warpMode, "Verformen", StringComparison.Ordinal)
            End Get
        End Property

        ' ── Freie Ecken ─────────────────────────────────────────────────────────
        '
        ' Acht Versaetze in Prozent der Bildbreite bzw. -hoehe, im Uhrzeigersinn ab links oben. Sie
        ' kommen ZUSAETZLICH zu den vier Reglern: die Regler kippen symmetrisch um eine Achse (der
        ' Griff fuer stuerzende Linien), die Ecken erlauben jede Lage, die eine Homographie hergibt.
        '
        ' Wo die Ecken LIEGEN, rechnet nicht diese Klasse, sondern ImageGeometryMapper.
        ' VerzerrungsEcken - dieselbe Stelle, aus der auch die Matrix im Renderer entsteht. Eine
        ' zweite Rechnung hier wuerde frueher oder spaeter danebenliegen, und man saehe es daran,
        ' dass ein Anfasser neben der Bildecke sitzt, die er anfasst.

        Private ReadOnly _perspectiveCorners As Double() = New Double(7) {}
        Private _perspectiveCornerDrag As Integer = -1
        ''' Versatz der Ecke beim Beginn des Zuges - Bezug fuer die Achsentreue mit Umschalt.
        Private _perspectiveDragStartX As Double
        Private _perspectiveDragStartY As Double

        Public Property PerspectiveCorner0X As Double
            Get
                Return _perspectiveCorners(0)
            End Get
            Set(value As Double)
                SetCornerOffset(0, value)
            End Set
        End Property

        Public Property PerspectiveCorner0Y As Double
            Get
                Return _perspectiveCorners(1)
            End Get
            Set(value As Double)
                SetCornerOffset(1, value)
            End Set
        End Property

        Public Property PerspectiveCorner1X As Double
            Get
                Return _perspectiveCorners(2)
            End Get
            Set(value As Double)
                SetCornerOffset(2, value)
            End Set
        End Property

        Public Property PerspectiveCorner1Y As Double
            Get
                Return _perspectiveCorners(3)
            End Get
            Set(value As Double)
                SetCornerOffset(3, value)
            End Set
        End Property

        Public Property PerspectiveCorner2X As Double
            Get
                Return _perspectiveCorners(4)
            End Get
            Set(value As Double)
                SetCornerOffset(4, value)
            End Set
        End Property

        Public Property PerspectiveCorner2Y As Double
            Get
                Return _perspectiveCorners(5)
            End Get
            Set(value As Double)
                SetCornerOffset(5, value)
            End Set
        End Property

        Public Property PerspectiveCorner3X As Double
            Get
                Return _perspectiveCorners(6)
            End Get
            Set(value As Double)
                SetCornerOffset(6, value)
            End Set
        End Property

        Public Property PerspectiveCorner3Y As Double
            Get
                Return _perspectiveCorners(7)
            End Get
            Set(value As Double)
                SetCornerOffset(7, value)
            End Set
        End Property

        ''' <summary>Ein Eckenversatz. Der Bereich ist mit plus/minus 60 Prozent bewusst weiter als
        ''' der der Regler: eine Ecke soll sich auch weit ueber die Bildkante hinausziehen lassen,
        ''' sonst laesst sich eine schraeg fotografierte Flaeche nicht geradeziehen.</summary>
        Private Sub SetCornerOffset(index As Integer, value As Double)
            Dim v = Math.Max(-60.0, Math.Min(60.0, value))
            If Math.Abs(_perspectiveCorners(index) - v) < 0.0001 Then Return
            CaptureUndoState("Verzerren")
            _perspectiveCorners(index) = v
            RaiseCornersChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub RaiseCornersChanged()
            For Each n In {NameOf(PerspectiveCorner0X), NameOf(PerspectiveCorner0Y),
                           NameOf(PerspectiveCorner1X), NameOf(PerspectiveCorner1Y),
                           NameOf(PerspectiveCorner2X), NameOf(PerspectiveCorner2Y),
                           NameOf(PerspectiveCorner3X), NameOf(PerspectiveCorner3Y),
                           NameOf(PerspectiveCornerValues), NameOf(HasPerspectiveChanges),
                           NameOf(HasPerspectiveWarning), NameOf(HasAnyImageWarp)}
                Me.RaisePropertyChanged(n)
            Next
            RaiseResetButtonStateChanged()
        End Sub

        ''' <summary>Die vier Ecken des Verzerrungsvierecks in ANZEIGE-Prozent, im Uhrzeigersinn ab
        ''' links oben: [x0, y0, x1, y1, x2, y2, x3, y3]. Fuer das Overlay und die Trefferpruefung.
        '''
        ''' Gerechnet wird ueber die gemeinsame Eckenfunktion und dann durch die restliche
        ''' Geometriekette - so sitzt ein Anfasser auch bei Beschnitt, Drehung, Begradigung und
        ''' Skalierung genau auf seiner Ecke. Nothing, solange kein Bild offen ist.</summary>
        Public ReadOnly Property PerspectiveCornerValues As Double()
            Get
                Dim previousStep = WarpStepSize()
                If previousStep.Width <= 0 OrElse previousStep.Height <= 0 Then Return Nothing
                Dim displaySize = GetAnnotationDisplayPixelSize()
                If displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return Nothing

                Dim corners = ImageGeometryMapper.WarpCorners(previousStep.Width, previousStep.Height,
                                                                 _perspectiveHorizontal, _perspectiveVertical,
                                                                 CType(_perspectiveCorners.Clone(), Double()))
                ' Seitenverhaeltnis und Groesse wirken NACH der Kippung um die Bildmitte - dieselbe
                ' Reihenfolge wie in der Matrix, sonst wanderten die Anfasser bei "Groesse" nicht mit.
                Dim m = ImageGeometryMapper.WarpMatrix(previousStep.Width, previousStep.Height,
                                                              _perspectiveHorizontal, _perspectiveVertical,
                                                              _perspectiveAspect, _perspectiveScale,
                                                              CType(_perspectiveCorners.Clone(), Double()))
                Dim arr(7) As Double
                For i = 0 To 3
                    ' Die Ecke ist der Zielpunkt der Verzerrung. Um ihn in die Anzeige zu bringen,
                    ' wird der zugehoerige QUELLpunkt (die unverzerrte Bildecke) durch die volle
                    ' Kette geschickt - die traegt die Verzerrung seit dieser Aenderung selbst.
                    Dim fromX = If(i = 1 OrElse i = 2, previousStep.Width, 0.0)
                    Dim fromY = If(i = 2 OrElse i = 3, previousStep.Height, 0.0)
                    Dim mapped = If(m.IsIdentity, New SKPoint(CSng(fromX), CSng(fromY)),
                                                m.MapPoint(New SKPoint(CSng(fromX), CSng(fromY))))
                    arr(i * 2) = mapped.X / previousStep.Width * 100.0
                    arr(i * 2 + 1) = mapped.Y / previousStep.Height * 100.0
                Next
                Return arr
            End Get
        End Property

        ''' <summary>Die Groesse, auf der die Verzerrungsstufe rechnet: nach Beschnitt, Vierteldrehung
        ''' und Begradigung, VOR Skalierung und Leinwand. Genau die Stufe, an der ApplyPerspective im
        ''' Renderer sitzt.</summary>
        Private Function WarpStepSize() As (Width As Double, Height As Double)
            Dim baseWidth = GetBaseWidth(), baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return (0, 0)
            Dim adj = BuildAppliedGeometryAdjustments()
            adj.ResizeWidth = 0 : adj.ResizeHeight = 0
            adj.CanvasWidth = 0 : adj.CanvasHeight = 0
            Dim size = ImageProcessor.ComputeGeometryOutputSize(baseWidth, baseHeight, adj)
            Return (size.Width, size.Height)
        End Function

        Public ReadOnly Property HasPerspectiveChanges As Boolean
            Get
                Return Math.Abs(_perspectiveHorizontal) > 0.0001 OrElse Math.Abs(_perspectiveVertical) > 0.0001 OrElse
                       Math.Abs(_perspectiveAspect) > 0.0001 OrElse Math.Abs(_perspectiveScale) > 0.0001 OrElse
                       _perspectiveCorners.Any(Function(v) Math.Abs(v) > 0.0001)
            End Get
        End Property

        ''' <summary>Fasst die naechstgelegene Ecke an, sofern eine in Greifweite liegt. Die
        ''' Trefferpruefung laeuft im ANZEIGERAUM, damit die Greifweite die auf dem Bildschirm
        ''' ist.</summary>
        Public Function TryBeginPerspectiveCornerDrag(xPercent As Double, yPercent As Double,
                                                      slopXPercent As Double, slopYPercent As Double) As Boolean
            Dim corners = PerspectiveCornerValues
            If corners Is Nothing Then Return False
            Dim bestDistance = Double.MaxValue
            Dim bester = -1
            For i = 0 To 3
                Dim dx = (xPercent - corners(i * 2)) / Math.Max(0.0001, slopXPercent)
                Dim dy = (yPercent - corners(i * 2 + 1)) / Math.Max(0.0001, slopYPercent)
                Dim d = dx * dx + dy * dy
                If d <= 1.0 AndAlso d < bestDistance Then
                    bestDistance = d
                    bester = i
                End If
            Next
            If bester < 0 Then Return False
            PushUndo(CombineHistoryLabel("Verzerren", "Perspektive"))
            _perspectiveCornerDrag = bester
            _perspectiveDragStartX = _perspectiveCorners(bester * 2)
            _perspectiveDragStartY = _perspectiveCorners(bester * 2 + 1)
            Return True
        End Function

        ''' <summary>Zieht die gefasste Ecke. Der Zeiger gibt die ZIELlage vor; gespeichert wird der
        ''' Versatz gegenueber der Ausgangsecke, damit die Regler daneben ihre Bedeutung behalten.</summary>
        ''' <param name="axisLock">Umschalt: der Versatz bleibt auf einer Achse - dieselbe Zusage wie
        ''' am Gitterpunkt.</param>
        Public Sub UpdatePerspectiveCornerDrag(xPercent As Double, yPercent As Double,
                                               Optional axisLock As Boolean = False)
            If _perspectiveCornerDrag < 0 Then Return
            Dim previousStep = WarpStepSize()
            If previousStep.Width <= 0 OrElse previousStep.Height <= 0 Then Return

            ' Der Zielpunkt in der Stufengroesse. Er kommt aus dem ANZEIGE-Prozent, das dieselbe
            ' Bezugsgroesse hat wie die Ausgabe von PerspectiveCornerValues.
            Dim targetX = xPercent / 100.0 * previousStep.Width
            Dim targetY = yPercent / 100.0 * previousStep.Height

            ' Wo die Ecke OHNE ihren eigenen Versatz laege (Regler und die anderen drei Ecken
            ' bleiben stehen) - die Differenz dazu ist der neue Versatz.
            Dim ohne = CType(_perspectiveCorners.Clone(), Double())
            ohne(_perspectiveCornerDrag * 2) = 0
            ohne(_perspectiveCornerDrag * 2 + 1) = 0
            Dim basis = ImageGeometryMapper.WarpCorners(previousStep.Width, previousStep.Height,
                                                             _perspectiveHorizontal, _perspectiveVertical, ohne)
            Dim i2 = _perspectiveCornerDrag
            Dim neuX = (targetX - basis(i2).X) / previousStep.Width * 100.0
            Dim neuY = (targetY - basis(i2).Y) / previousStep.Height * 100.0

            ' ACHSENTREU gegen den Stand VOR dem Zug: die kleinere der beiden Bewegungen faellt weg.
            If axisLock Then
                If Math.Abs(neuX - _perspectiveDragStartX) >= Math.Abs(neuY - _perspectiveDragStartY) Then
                    neuY = _perspectiveDragStartY
                Else
                    neuX = _perspectiveDragStartX
                End If
            End If
            _perspectiveCorners(i2 * 2) = Math.Max(-60.0, Math.Min(60.0, neuX))
            _perspectiveCorners(i2 * 2 + 1) = Math.Max(-60.0, Math.Min(60.0, neuY))
            RaiseCornersChanged()
            SchedulePreviewUpdate()
        End Sub

        Public Sub EndPerspectiveCornerDrag()
            _perspectiveCornerDrag = -1
        End Sub

        ' ── Gitterverzerrung ────────────────────────────────────────────────────
        '
        ' Das Raster lebt im ANZEIGERAUM des Bildes - dort, wo man es zieht. Genau dort wird es auch
        ' ausgewertet: eine bestaetigte Verzerrung ist ein eigener Schritt der Geometriekette und
        ' laeuft HINTER Beschnitt, Drehung, Groesse und Leinwand. Ein 4x4-Raster fuellt damit immer
        ' das sichtbare Bild, jeder Knoten hat einen Anzeigeort, und ein Zug wirkt an der Achse, an
        ' der er gezogen wurde. Am markierten OBJEKT ist der Bezugsraum dessen eigenes Rechteck.
        '
        ' Das RASTER selbst ist Sitzungszustand: es sagt nur, wohin gezogen wurde. Beim Anwenden
        ' wird daraus ein Knotenfeld im REZEPT (BuildWarpMesh, siehe unten) - nichts wird in die
        ' Pixel gebacken, und deshalb laesst sich eine zweite Verzerrung auf eine erste setzen.

        Private _warpColumns As Integer = 4
        Private _warpRows As Integer = 4
        Private _warpX As Double() = Nothing
        Private _warpY As Double() = Nothing
        Private _warpDragIndex As Integer = -1
        ''' Wo der laufende Zug begann - Bezug fuer die Achsentreue mit Umschalt.
        Private _warpDragStartX As Double
        Private _warpDragStartY As Double

        Public ReadOnly Property WarpColumns As Integer
            Get
                Return _warpColumns
            End Get
        End Property

        Public ReadOnly Property WarpRows As Integer
            Get
                Return _warpRows
            End Get
        End Property

        ''' <summary>Die Rastergroesse. Ein groeberes Raster verzerrt weicher, ein feineres genauer;
        ''' das Umstellen verwirft ein begonnenes Ziehen, weil die Punkte sonst nicht mehr
        ''' zueinander passen.</summary>
        Public Property WarpGridSize As Integer
            Get
                Return _warpColumns
            End Get
            Set(value As Integer)
                Dim v = Math.Max(2, Math.Min(12, value))
                If v = _warpColumns AndAlso v = _warpRows Then Return
                CaptureUndoState("Gitter")
                DisposeGridPreview()
                _warpColumns = v
                _warpRows = v
                ResetGrid()
                ' Am OBJEKT muss das neutrale Raster auch dorthin: sonst stand das Overlay auf
                ' neutral, waehrend das Objekt weiter mit der alten Verzerrung gerendert wurde.
                If WarpsTheObject Then
                    WriteObjectGrid()
                    RefreshPreviewImmediately()
                End If
                Me.RaisePropertyChanged(NameOf(WarpGridSize))
                Me.RaisePropertyChanged(NameOf(WarpGridValues))
                Me.RaisePropertyChanged(NameOf(HasWarpGridChanges))
            End Set
        End Property

        ''' <summary>Das Raster fuer die Anzeige: [spalten, zeilen, x0, y0, ...] in ANZEIGE-Prozent.
        ''' Am BILD ist das die gespeicherte Lage selbst; am markierten OBJEKT wird jeder Punkt auf
        ''' dessen Rechteck umgerechnet. Punkte ohne Anzeigeort kommen als NaN heraus - die Anzeige
        ''' laesst sie dann aus.</summary>
        Public ReadOnly Property WarpGridValues As Double()
            Get
                PrepareGrid()
                Dim arr(1 + _warpX.Length * 2) As Double
                arr(0) = _warpColumns
                arr(1) = _warpRows
                For i = 0 To _warpX.Length - 1
                    Dim anzeige = GridPointToDisplay(_warpX(i), _warpY(i))
                    If anzeige.HasValue Then
                        arr(2 + i * 2) = anzeige.Value.X
                        arr(3 + i * 2) = anzeige.Value.Y
                    Else
                        arr(2 + i * 2) = Double.NaN
                        arr(3 + i * 2) = Double.NaN
                    End If
                Next
                Return arr
            End Get
        End Property

        Public ReadOnly Property HasWarpGridChanges As Boolean
            Get
                PrepareGrid()
                For zi = 0 To _warpRows
                    For si = 0 To _warpColumns
                        Dim i = zi * (_warpColumns + 1) + si
                        If Math.Abs(_warpX(i) - si * 100.0 / _warpColumns) > 0.01 OrElse
                           Math.Abs(_warpY(i) - zi * 100.0 / _warpRows) > 0.01 Then Return True
                    Next
                Next
                Return False
            End Get
        End Property

        Private Sub PrepareGrid()
            Dim count = (_warpColumns + 1) * (_warpRows + 1)
            If _warpX IsNot Nothing AndAlso _warpX.Length = count Then Return
            ResetGrid()
        End Sub

        Private Sub ResetGrid()
            Dim count = (_warpColumns + 1) * (_warpRows + 1)
            ReDim _warpX(count - 1)
            ReDim _warpY(count - 1)
            For rowIdx = 0 To _warpRows
                For colIdx = 0 To _warpColumns
                    Dim i = rowIdx * (_warpColumns + 1) + colIdx
                    _warpX(i) = colIdx * 100.0 / _warpColumns
                    _warpY(i) = rowIdx * 100.0 / _warpRows
                Next
            Next
            _warpDragIndex = -1
        End Sub

        ' ── Live-Vorschau der Gitterverzerrung ──────────────────────────────────
        '
        ' Waehrend des Ziehens wird eine KOPIE des Anzeigebildes verzerrt und ueber das Bild gelegt.
        ' Ohne sie zieht man Punkte und sieht bis zum Anwenden nur ein Gitter - man kann also nicht
        ' beurteilen, was man tut.
        '
        ' Die Kopie hat ANZEIGEgroesse, nicht Bildgroesse: sie ist nur zum Anschauen da, und ein
        ' 45-MP-Netz bei jeder Mausbewegung waere unbezahlbar. Beim Anwenden wird dann sauber auf
        ' dem vollen Arbeitsbild gerechnet.
        '
        ' Sie enthaelt die OBJEKTE: beim Anwenden gehen sie inzwischen mit, und dieselbe Verzerrung
        ' auf dem fertigen Anzeigebild zeigt deshalb genau das Ergebnis.

        Private _gridPreviewBase As SKBitmap
        Private _gridPreviewSourceX As Single()
        Private _gridPreviewSourceY As Single()
        Private _vorschauBild As Bitmap
        Private _vorschauQuelle As String = ""

        ''' <summary>Das Vorschaubild, das ueber dem Bild liegt, sonst Nothing.
        '''
        ''' EIN Kanal fuer beide Werkzeuge, die eine Live-Vorschau haben: Gitterverzerrung und
        ''' Tiefen-Unschaerfe. Sie koennen nie gleichzeitig laufen (verschiedene Werkzeuge), und zwei
        ''' Bilder uebereinander waeren nur eine Gelegenheit, dass eines haengen bleibt. Wer den Kanal
        ''' gerade haelt, steht in <see cref="_vorschauQuelle"/>: nur der eigene Halter raeumt ihn ab,
        ''' sonst loescht ein Werkzeug beim Verlassen die Vorschau des naechsten.</summary>
        Public Property ToolPreviewImage As Bitmap
            Get
                Return _vorschauBild
            End Get
            Private Set(value As Bitmap)
                If Object.ReferenceEquals(_vorschauBild, value) Then Return
                Dim alt = _vorschauBild
                _vorschauBild = value
                Me.RaisePropertyChanged(NameOf(ToolPreviewImage))
                Me.RaisePropertyChanged(NameOf(HasPreview))
                ' ERST melden, DANN freigeben: die Anzeige haelt sonst kurz ein totes Bitmap.
                alt?.Dispose()
            End Set
        End Property

        Public ReadOnly Property HasPreview As Boolean
            Get
                Return _vorschauBild IsNot Nothing
            End Get
        End Property

        ''' <summary>Auf Bgra8888/Premul bringen. ToAvaloniaBitmapFast ist eine reine Zeilenkopie
        ''' und braucht exakt dieses Format; der PNG-Decode liefert je nach Plattform auch anderes.
        ''' Dann wird einmal umgezeichnet und das Original freigegeben - einmal je Zuggeste, nicht
        ''' je Bewegung.</summary>
        Private Shared Function NormalizePreviewBase(bmp As SKBitmap) As SKBitmap
            If bmp Is Nothing Then Return Nothing
            If bmp.ColorType = SKColorType.Bgra8888 AndAlso bmp.AlphaType = SKAlphaType.Premul Then Return bmp
            Dim norm = New SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Using cv As New SKCanvas(norm)
                cv.Clear(SKColors.Transparent)
                cv.DrawBitmap(bmp, 0, 0)
            End Using
            bmp.Dispose()
            Return norm
        End Function

        ' ── Drosselung der Live-Vorschau ────────────────────────────────────────
        '
        ' Das Ziehen liefert mehr Bewegungsereignisse, als Zeichnen noetig ist. Je UI-Durchlauf
        ' wird hoechstens EINMAL gezeichnet, und zwar der zuletzt angeforderte Stand: die
        ' Hintergrund-Prioritaet laeuft erst, wenn die anstehenden Eingabe-Ereignisse verarbeitet
        ' sind, ein Schwall Bewegungen faellt also zu einem Bild zusammen. Ein gemeinsamer Kanal
        ' fuer Gitter, Verformen und Linien - sie koennen nie gleichzeitig ziehen, und der letzte
        ' Stand ist immer der richtige. Laeuft der aufgeschobene Zeichner NACH dem Verlassen des
        ' Werkzeugs, greift die Nothing-Pruefung der jeweiligen Grundlage.
        Private _warpPreviewQueued As Boolean
        Private _warpPreviewRender As Action

        Private Sub QueueWarpPreview(render As Action)
            _warpPreviewRender = render
            If _warpPreviewQueued Then Return
            _warpPreviewQueued = True
            Avalonia.Threading.Dispatcher.UIThread.Post(
                Sub()
                    _warpPreviewQueued = False
                    Dim r = _warpPreviewRender
                    _warpPreviewRender = Nothing
                    r?.Invoke()
                End Sub, Avalonia.Threading.DispatcherPriority.Background)
        End Sub

        ''' <summary>Die Grundlage der Vorschau anlegen: das Anzeigebild, dazu die Lage des
        ''' UNVERZERRTEN Rasters im Anzeigeraum. Am Bild ist das ein gleichmaessiges Raster; am
        ''' markierten Objekt liegt es auf dessen Rechteck und ist es nicht.</summary>
        Private Sub PrepareGridPreview()
            PrepareWarpPreview(_warpColumns, _warpRows, AddressOf GridPointToDisplay)
        End Sub

        ''' <summary>Dasselbe fuer ein beliebig feines Auswertungsraster - das Verformen-Werkzeug
        ''' fuehrt seinen Zustand in vier Randkurven und wertet sie erst hier auf Knoten aus.</summary>
        Private Sub PrepareWarpPreview(columns As Integer, rows As Integer,
                                       Optional sourcePointToDisplay As Func(Of Double, Double, SKPoint?) = Nothing)
            DisposeGridPreview()
            Dim size = GetAnnotationDisplayPixelSize()
            If size.Width <= 0 OrElse size.Height <= 0 Then Return
            Dim source = TryCast(DisplayImage, Bitmap)
            If source Is Nothing Then Return

            Try
                ' Das Anzeigebild als SKBitmap in Anzeigegroesse. Ueber den PNG-Umweg, weil eine
                ' Avalonia-Bitmap ihre Pixel nicht direkt herausgibt.
                Using strom = New IO.MemoryStream()
                    source.Save(strom, PngBitmapEncoderOptions.Default)
                    strom.Position = 0
                    Dim roh = NormalizePreviewBase(SKBitmap.Decode(strom))
                    If roh Is Nothing Then Return
                    _gridPreviewBase = roh
                End Using
            Catch
                DisposeGridPreview()
                Return
            End Try

            ' MIT OBJEKTEN. Frueher wurden sie fuer die Vorschau herausgerechnet, weil sie beim
            ' Anwenden nicht mitverzerrt wurden - jetzt gehen sie mit, und dieselbe Verzerrung auf
            ' dem Anzeigebild zeigt genau das Ergebnis.

            PrepareGrid()
            Dim n = (columns + 1) * (rows + 1)
            ReDim _gridPreviewSourceX(n - 1)
            ReDim _gridPreviewSourceY(n - 1)
            Dim bw = _gridPreviewBase.Width, bh = _gridPreviewBase.Height
            For rowIdx = 0 To rows
                For colIdx = 0 To columns
                    Dim i = rowIdx * (columns + 1) + colIdx
                    Dim x = colIdx * 100.0 / columns
                    Dim y = rowIdx * 100.0 / rows
                    Dim anzeige = If(sourcePointToDisplay Is Nothing,
                                     SourcePercentToDisplayPercent(x, y),
                                     sourcePointToDisplay(x, y))
                    If anzeige.HasValue Then
                        _gridPreviewSourceX(i) = CSng(anzeige.Value.X / 100.0 * bw)
                        _gridPreviewSourceY(i) = CSng(anzeige.Value.Y / 100.0 * bh)
                    Else
                        ' Weggeschnitten: dann taugt die Vorschau nicht, lieber gar keine.
                        DisposeGridPreview()
                        Return
                    End If
                Next
            Next
        End Sub

        Private Sub DisposeGridPreview()
            ' Gitter und Verformen teilen sich Grundlage und Vorschaukanal - sie koennen nie
            ' gleichzeitig laufen, und beide muessen hier abgeraeumt werden.
            If String.Equals(_vorschauQuelle, "Gitter", StringComparison.Ordinal) OrElse
               String.Equals(_vorschauQuelle, "Verformen", StringComparison.Ordinal) Then
                ToolPreviewImage = Nothing
                _vorschauQuelle = ""
            End If
            _gridPreviewBase?.Dispose()
            _gridPreviewBase = Nothing
            _gridPreviewSourceX = Nothing
            _gridPreviewSourceY = Nothing
        End Sub

        ''' <summary>Die Vorschau zum aktuellen Raster neu zeichnen - gedrosselt, siehe
        ''' <see cref="QueueWarpPreview"/>.</summary>
        Private Sub RefreshGridPreview()
            QueueWarpPreview(Sub() RefreshWarpPreview(_warpColumns, _warpRows, WarpGridValues, "Gitter"))
        End Sub

        ''' <summary>Der gemeinsame Weg fuer Gitter und Verformen: aus den Knoten in ANZEIGE-Prozent
        ''' wird die Kopie in Anzeigegroesse verzerrt und als Vorschau ueber das Bild gelegt.
        ''' <paramref name="holder"/> vermerkt, wer den Vorschaukanal haelt.</summary>
        Private Sub RefreshWarpPreview(columns As Integer, rows As Integer,
                                       values As Double(), holder As String)
            If _gridPreviewBase Is Nothing OrElse _gridPreviewSourceX Is Nothing Then Return
            If values Is Nothing OrElse values.Length < 4 Then Return
            Dim bw = _gridPreviewBase.Width, bh = _gridPreviewBase.Height
            Dim n = (columns + 1) * (rows + 1)
            If _gridPreviewSourceX.Length <> n Then Return
            Dim zx(n - 1) As Single
            Dim zy(n - 1) As Single
            For i = 0 To n - 1
                Dim x = values(2 + i * 2), y = values(3 + i * 2)
                If Double.IsNaN(x) OrElse Double.IsNaN(y) Then Return
                zx(i) = CSng(x / 100.0 * bw)
                zy(i) = CSng(y / 100.0 * bh)
            Next

            Dim warped = ImageGeometryMapper.WarpOverGrid(
                _gridPreviewBase, columns, rows, zx, zy,
                _gridPreviewSourceX, _gridPreviewSourceY)
            ' Gleiches Objekt zurueck heisst: unbewegt, also nichts zu zeigen.
            If warped Is Nothing OrElse Object.ReferenceEquals(warped, _gridPreviewBase) Then
                DisposeGridPreview()
                Return
            End If
            Using warped
                ' Direkte Zeilenkopie statt PNG-Rundlauf: der alte Weg kodierte und dekodierte je
                ' Mausbewegung das ganze Anzeigebild und gab sein SKImage nie frei.
                ToolPreviewImage = ImageOrientationService.ToAvaloniaBitmapFast(warped)
                _vorschauQuelle = holder
            End Using
        End Sub


        ' ── Linienverzerrung ────────────────────────────────────────────────────
        '
        ' Man legt eine Linie auf eine KANTE im Bild und zieht sie an ihren neuen Platz. Die Kante
        ' geht als Ganzes mit, und die Umgebung folgt weich. Das ist der Unterschied zum
        ' Stuetzpunktraster: dort muesste man einer Kante ein Dutzend Punkte einzeln nachfuehren.
        '
        ' Jede Linie liegt zweimal vor: als QUELLE dort, wo sie im Bild liegt, und als ZIEL dort,
        ' wohin sie gezogen wurde. Beide im VERZERRRAUM in Prozent, wie das Raster und aus demselben
        ' Grund: dort wird die Verzerrung ausgewertet, also gehoert sie auch dorthin.

        ''' <summary>Eine Linie der Verzerrung. Alle Werte in Verzerrraum-Prozent.</summary>
        Public Class WarpLine
            Public Property SourceAx As Double
            Public Property SourceAy As Double
            Public Property SourceBx As Double
            Public Property SourceBy As Double
            Public Property TargetAx As Double
            Public Property TargetAy As Double
            Public Property TargetBx As Double
            Public Property TargetBy As Double

            Public ReadOnly Property IsMoved As Boolean
                Get
                    Return Math.Abs(TargetAx - SourceAx) > 0.02 OrElse Math.Abs(TargetAy - SourceAy) > 0.02 OrElse
                           Math.Abs(TargetBx - SourceBx) > 0.02 OrElse Math.Abs(TargetBy - SourceBy) > 0.02
                End Get
            End Property

            Public Function Clone() As WarpLine
                Return New WarpLine With {
                    .SourceAx = SourceAx, .SourceAy = SourceAy, .SourceBx = SourceBx, .SourceBy = SourceBy,
                    .TargetAx = TargetAx, .TargetAy = TargetAy, .TargetBx = TargetBx, .TargetBy = TargetBy}
            End Function
        End Class

        ''' <summary>Der ARBEITSSTAND des Verzerren-Werkzeugs, wie ihn ein Undo-Eintrag mitnimmt:
        ''' Stützpunktraster samt seiner Größe, die Linien und die Verformen-Punkte.
        '''
        ''' Er steht bewusst NICHT im Rezept - eine noch nicht übernommene Verzerrung ist
        ''' Sitzungszustand. Genau daran war Strg+Z während einer offenen Verzerrung wirkungslos: der
        ''' Schnappschuss enthielt nur das Rezept, der Schritt wurde verbraucht, und die Griffe
        ''' blieben stehen, wo sie waren.</summary>
        Friend NotInheritable Class WarpSessionState
            Public Columns As Integer
            Public Rows As Integer
            Public GridX As Double()
            Public GridY As Double()
            Public Envelope As Double()
            Public Lines As List(Of WarpLine)
        End Class

        ''' <summary>Den offenen Arbeitsstand sichern - oder Nothing, wenn es keinen gibt. Die Felder
        ''' sind klein (ein 12er-Raster sind 338 Zahlen), das Sichern kostet also nichts.</summary>
        Private Function CaptureWarpSession() As WarpSessionState
            If _warpX Is Nothing AndAlso _envelope Is Nothing AndAlso _linien.Count = 0 Then Return Nothing
            Return New WarpSessionState With {
                .Columns = _warpColumns, .Rows = _warpRows,
                .GridX = If(_warpX Is Nothing, Nothing, CType(_warpX.Clone(), Double())),
                .GridY = If(_warpY Is Nothing, Nothing, CType(_warpY.Clone(), Double())),
                .Envelope = If(_envelope Is Nothing, Nothing, CType(_envelope.Clone(), Double())),
                .Lines = _linien.Select(Function(l) l.Clone()).ToList()}
        End Function

        ''' <summary>Den gesicherten Arbeitsstand zurückholen. Ein laufender Zug wird dabei
        ''' abgebrochen und die Vorschau verworfen - sie zeigte sonst den Stand von vorhin.</summary>
        Private Sub RestoreWarpSession(state As WarpSessionState)
            If state Is Nothing Then Return
            _warpColumns = state.Columns
            _warpRows = state.Rows
            _warpX = If(state.GridX Is Nothing, Nothing, CType(state.GridX.Clone(), Double()))
            _warpY = If(state.GridY Is Nothing, Nothing, CType(state.GridY.Clone(), Double()))
            _envelope = If(state.Envelope Is Nothing, Nothing, CType(state.Envelope.Clone(), Double()))
            _linien.Clear()
            If state.Lines IsNot Nothing Then _linien.AddRange(state.Lines.Select(Function(l) l.Clone()))
            _warpDragIndex = -1
            _linienDragIndex = -1
            _linienDragTeil = -1
            _envelopeDragIndex = -1
            DisposeGridPreview()
            DisposeLinePreview()
            RaiseLinesChanged()
            RaiseEnvelopeChanged()
            Me.RaisePropertyChanged(NameOf(WarpGridSize))
            Me.RaisePropertyChanged(NameOf(WarpGridValues))
            Me.RaisePropertyChanged(NameOf(HasWarpGridChanges))
        End Sub

        Private ReadOnly _linien As New List(Of WarpLine)()
        ''' <summary>Welche Linie gerade gezogen wird, und woran: 0 Anfang, 1 Ende, 2 die ganze
        ''' Linie. -1 heisst: kein Zug.</summary>
        Private _linienDragIndex As Integer = -1
        Private _linienDragTeil As Integer = -1
        Private _linienDragStartX As Double = 0
        Private _linienDragStartY As Double = 0
        Private _linienDragZielAx As Double = 0
        Private _linienDragZielAy As Double = 0
        Private _linienDragZielBx As Double = 0
        Private _linienDragZielBy As Double = 0

        Public ReadOnly Property HasLineChanges As Boolean
            Get
                Return _linien.Any(Function(l) l.IsMoved)
            End Get
        End Property

        Public ReadOnly Property LineCount As Integer
            Get
                Return _linien.Count
            End Get
        End Property

        ''' <summary>Alle Linien fuer die Anzeige, in ANZEIGE-Prozent: [Anzahl, dann je Linie
        ''' QuelleAx, QuelleAy, QuelleBx, QuelleBy, ZielAx, ZielAy, ZielBx, ZielBy].
        '''
        ''' Punkte ohne Anzeigeort (weggeschnitten, leere Begradigungsecke) kommen als NaN heraus
        ''' und werden nicht gezeichnet - genau wie beim Raster.</summary>
        Public ReadOnly Property LineValues As Double()
            Get
                Dim arr(_linien.Count * 8) As Double
                arr(0) = _linien.Count
                For i = 0 To _linien.Count - 1
                    Dim l = _linien(i)
                    Dim paare = {(l.SourceAx, l.SourceAy), (l.SourceBx, l.SourceBy),
                                 (l.TargetAx, l.TargetAy), (l.TargetBx, l.TargetBy)}
                    For j = 0 To 3
                        Dim anzeige = WarpSpaceToDisplay(paare(j).Item1, paare(j).Item2)
                        If anzeige.HasValue Then
                            arr(1 + i * 8 + j * 2) = anzeige.Value.X
                            arr(2 + i * 8 + j * 2) = anzeige.Value.Y
                        Else
                            arr(1 + i * 8 + j * 2) = Double.NaN
                            arr(2 + i * 8 + j * 2) = Double.NaN
                        End If
                    Next
                Next
                Return arr
            End Get
        End Property

        Private Sub RaiseLinesChanged()
            Me.RaisePropertyChanged(NameOf(LineValues))
            Me.RaisePropertyChanged(NameOf(HasLineChanges))
            Me.RaisePropertyChanged(NameOf(LineCount))
        End Sub

        ''' <summary>Eine neue Linie beginnen. Quelle und Ziel sind dabei gleich - die Linie tut noch
        ''' nichts, sie liegt erst einmal nur da. Erst das Ziehen an ihr verzerrt.</summary>
        Public Function BeginneNeueLinie(xPercent As Double, yPercent As Double) As Boolean
            Dim source = DisplayToWarpSpace(xPercent, yPercent)
            If Not source.HasValue Then Return False
            If _linien.Count >= MaxLines Then Return False
            CaptureUndoState("Linien")
            Dim l = New WarpLine With {
                .SourceAx = source.Value.X, .SourceAy = source.Value.Y,
                .SourceBx = source.Value.X, .SourceBy = source.Value.Y,
                .TargetAx = source.Value.X, .TargetAy = source.Value.Y,
                .TargetBx = source.Value.X, .TargetBy = source.Value.Y}
            _linien.Add(l)
            _linienDragIndex = _linien.Count - 1
            ' Beim Aufziehen wandert das ENDE, und zwar in Quelle und Ziel gleichzeitig: es wird ja
            ' gerade erst festgelegt, wo die Linie liegt.
            _linienDragTeil = 3
            RaiseLinesChanged()
            PrepareLinePreview()
            Return True
        End Function

        ''' <summary>Hoechstzahl der Linien. Jede kostet bei jedem Rasterknoten eine Auswertung -
        ''' mit einem Dutzend faengt die Vorschau an zu haengen, und mehr braucht man auch nicht.</summary>
        Public Const MaxLines As Integer = 12

        ''' <summary>Eine vorhandene Linie anfassen: an einem Ende oder in der Mitte. Getroffen wird
        ''' die ZIELlinie - die Quelle bleibt liegen, wo sie liegt.</summary>
        Public Function TryBeginLineDrag(xPercent As Double, yPercent As Double,
                                           slopXPercent As Double, slopYPercent As Double) As Boolean
            Dim bestDistance = Double.MaxValue
            Dim bestLine = -1, besterTeil = -1
            For i = 0 To _linien.Count - 1
                Dim l = _linien(i)
                Dim a = WarpSpaceToDisplay(l.TargetAx, l.TargetAy)
                Dim b = WarpSpaceToDisplay(l.TargetBx, l.TargetBy)
                If Not a.HasValue OrElse Not b.HasValue Then Continue For
                ' Erst die Enden: sie liegen auf der Linie, muessen also Vorrang haben.
                For part = 0 To 1
                    Dim p = If(part = 0, a.Value, b.Value)
                    Dim dx = (xPercent - p.X) / Math.Max(0.0001, slopXPercent)
                    Dim dy = (yPercent - p.Y) / Math.Max(0.0001, slopYPercent)
                    Dim d = dx * dx + dy * dy
                    If d <= 1.0 AndAlso d < bestDistance Then
                        bestDistance = d
                        bestLine = i
                        besterTeil = part
                    End If
                Next
            Next
            If bestLine < 0 Then
                ' Dann die Linien selbst, in ihrer Mitte gefasst.
                For i = 0 To _linien.Count - 1
                    Dim l = _linien(i)
                    Dim a = WarpSpaceToDisplay(l.TargetAx, l.TargetAy)
                    Dim b = WarpSpaceToDisplay(l.TargetBx, l.TargetBy)
                    If Not a.HasValue OrElse Not b.HasValue Then Continue For
                    Dim d = DistanceToSegment(xPercent, yPercent, a.Value.X, a.Value.Y, b.Value.X, b.Value.Y,
                                              slopXPercent, slopYPercent)
                    If d <= 1.0 AndAlso d < bestDistance Then
                        bestDistance = d
                        bestLine = i
                        besterTeil = 2
                    End If
                Next
            End If
            If bestLine < 0 Then Return False

            CaptureUndoState("Linien")
            _linienDragIndex = bestLine
            _linienDragTeil = besterTeil
            _linienDragStartX = xPercent
            _linienDragStartY = yPercent
            Dim gefasst = _linien(bestLine)
            _linienDragZielAx = gefasst.TargetAx
            _linienDragZielAy = gefasst.TargetAy
            _linienDragZielBx = gefasst.TargetBx
            _linienDragZielBy = gefasst.TargetBy
            PrepareLinePreview()
            Return True
        End Function

        ''' <summary>Abstand eines Punktes zu einer STRECKE, in Greifweiten gemessen. Nicht zur
        ''' unendlichen Geraden: sonst liesse sich eine kurze Linie weit ausserhalb ihrer Enden
        ''' anfassen.</summary>
        Private Shared Function DistanceToSegment(px As Double, py As Double,
                                                  ax As Double, ay As Double, bx As Double, by As Double,
                                                  slopX As Double, slopY As Double) As Double
            Dim dx = bx - ax, dy = by - ay
            Dim laenge2 = dx * dx + dy * dy
            Dim t = 0.0
            If laenge2 > 0.000001 Then
                t = ((px - ax) * dx + (py - ay) * dy) / laenge2
                t = Math.Max(0.0, Math.Min(1.0, t))
            End If
            Dim nx = ax + t * dx, ny = ay + t * dy
            Dim ex = (px - nx) / Math.Max(0.0001, slopX)
            Dim ey = (py - ny) / Math.Max(0.0001, slopY)
            Return ex * ex + ey * ey
        End Function

        Public Sub UpdateLineDrag(xPercent As Double, yPercent As Double)
            If _linienDragIndex < 0 OrElse _linienDragIndex >= _linien.Count Then Return
            Dim l = _linien(_linienDragIndex)
            ' GEKLEMMT WIRD NUR AM BILD. Am OBJEKT ist der Bezugsraum sein Rechteck, und dort soll
            ' sich ein Linienende ueber den Rahmen hinausziehen lassen - Gitter und Verformen halten
            ' es genauso (limit = Not WarpsTheObject). Vorher klemmte diese Stelle immer, und am
            ' Objekt liess sich keine Kante nach aussen ziehen.
            Dim clampToImage = Not WarpsTheObject
            Dim clamp = Function(value As Double) If(clampToImage, ClampPercent(value), value)

            If _linienDragTeil = 2 Then
                ' Die ganze Ziellinie mitnehmen: die Verschiebung wird im ANZEIGERAUM genommen und
                ' fuer beide Enden einzeln zurueckgerechnet. Ein Versatz in Quell-Prozent waere bei
                ' gedrehtem Bild eine andere Richtung als die, in die man zieht.
                Dim va = WarpSpaceToDisplay(_linienDragZielAx, _linienDragZielAy)
                Dim vb = WarpSpaceToDisplay(_linienDragZielBx, _linienDragZielBy)
                If Not va.HasValue OrElse Not vb.HasValue Then Return
                Dim dx = xPercent - _linienDragStartX
                Dim dy = yPercent - _linienDragStartY
                Dim na = DisplayToWarpSpace(va.Value.X + dx, va.Value.Y + dy)
                Dim nb = DisplayToWarpSpace(vb.Value.X + dx, vb.Value.Y + dy)
                If Not na.HasValue OrElse Not nb.HasValue Then Return
                l.TargetAx = clamp(na.Value.X) : l.TargetAy = clamp(na.Value.Y)
                l.TargetBx = clamp(nb.Value.X) : l.TargetBy = clamp(nb.Value.Y)
            Else
                Dim source = DisplayToWarpSpace(xPercent, yPercent)
                If Not source.HasValue Then Return
                Dim nx = clamp(source.Value.X), ny = clamp(source.Value.Y)
                Select Case _linienDragTeil
                    Case 0
                        l.TargetAx = nx : l.TargetAy = ny
                    Case 1
                        l.TargetBx = nx : l.TargetBy = ny
                    Case 3
                        ' Aufziehen: Quelle UND Ziel, die Linie wird ja gerade erst gelegt.
                        l.SourceBx = nx : l.SourceBy = ny
                        l.TargetBx = nx : l.TargetBy = ny
                End Select
            End If

            RaiseLinesChanged()
            If WarpsTheObject Then
                WriteObjectLines()
                SchedulePreviewUpdate()
            Else
                RefreshLinePreview()
            End If
        End Sub

        ''' <summary>Auf das Bild klemmen: 0 bis 100 Prozent.</summary>
        Private Shared Function ClampPercent(value As Double) As Double
            Return Math.Max(0.0, Math.Min(100.0, value))
        End Function

        Public Sub EndLineDrag()
            If _linienDragIndex < 0 Then Return
            ' Eine Linie, die beim Aufziehen zu kurz geblieben ist, war ein Klick und keine Linie.
            ' Sie stehen zu lassen hiesse, dass jeder Fehlklick einen Griff auf dem Bild hinterlaesst.
            If _linienDragTeil = 3 AndAlso _linienDragIndex < _linien.Count Then
                Dim l = _linien(_linienDragIndex)
                If Math.Abs(l.SourceBx - l.SourceAx) < 1.0 AndAlso Math.Abs(l.SourceBy - l.SourceAy) < 1.0 Then
                    _linien.RemoveAt(_linienDragIndex)
                    RaiseLinesChanged()
                End If
            End If
            _linienDragIndex = -1
            _linienDragTeil = -1
            If WarpsTheObject Then
                WriteObjectLines()
                RefreshPreviewImmediately()
            End If
        End Sub

        ''' <summary>Die zuletzt angelegte Linie wieder entfernen.</summary>
        Public Sub RemoveLastLine()
            If _linien.Count = 0 Then Return
            CaptureUndoState("Linien")
            _linien.RemoveAt(_linien.Count - 1)
            DisposeLinePreview()
            RaiseLinesChanged()
            ' Am OBJEKT muss die Verzerrung nachgezogen werden, genau wie beim Ziehen und beim
            ' Zuruecksetzen. Ohne das zeigte das Overlay eine Linie weniger, das Objekt wurde aber
            ' weiter mit der entfernten Linie gerendert - bis irgendein anderer Zug OwnWarp neu
            ' schrieb.
            If WarpsTheObject Then
                WriteObjectLines()
                RefreshPreviewImmediately()
            End If
        End Sub

        Public Sub ResetLines()
            If _linien.Count = 0 Then Return
            CaptureUndoState("Linien")
            _linien.Clear()
            _linienDragIndex = -1
            _linienDragTeil = -1
            DisposeLinePreview()
            RaiseLinesChanged()
            If WarpsTheObject Then
                WriteObjectLines()
                RefreshPreviewImmediately()
            End If
        End Sub

        ''' <summary>Die Linien als flache Felder in PIXELN des uebergebenen Bildes.</summary>
        Private Function LinesAsPixels(width As Integer, height As Integer,
                                        ByRef source As Double(), ByRef target As Double()) As Boolean
            Dim bewegte = _linien.Where(Function(l) l.IsMoved).ToList()
            If bewegte.Count = 0 Then Return False
            ReDim source(bewegte.Count * 4 - 1)
            ReDim target(bewegte.Count * 4 - 1)
            For i = 0 To bewegte.Count - 1
                Dim l = bewegte(i)
                source(i * 4) = l.SourceAx / 100.0 * width
                source(i * 4 + 1) = l.SourceAy / 100.0 * height
                source(i * 4 + 2) = l.SourceBx / 100.0 * width
                source(i * 4 + 3) = l.SourceBy / 100.0 * height
                target(i * 4) = l.TargetAx / 100.0 * width
                target(i * 4 + 1) = l.TargetAy / 100.0 * height
                target(i * 4 + 2) = l.TargetBx / 100.0 * width
                target(i * 4 + 3) = l.TargetBy / 100.0 * height
            Next
            Return True
        End Function


        ''' <summary>Traegt eine Verzerrung in ALLE Objekte ein, damit sie mitgehen.
        '''
        ''' Gespeichert wird immer als GITTER, egal welches Werkzeug sie erzeugt hat. Das ist die
        ''' Form, die alles ausdruecken kann, und sie laesst sich verketten: wird ein zweites Mal
        ''' verzerrt, wandern einfach die vorhandenen Stuetzpunkte durch das neue Feld. Mit drei
        ''' getrennten Arten muesste man fuer jede Paarung ueberlegen, was ihre Verkettung ist.
        '''
        ''' <paramref name="abbildung"/> nimmt einen Punkt in Bildprozent und gibt zurueck, wohin er
        ''' wandert - ebenfalls in Bildprozent.</summary>
        ''' <summary>Traegt die gerade bestaetigte Perspektive in das eigene Verzerrungsfeld jedes
        ''' Objekts ein - das Gegenstueck zum Gitterverzerren.
        '''
        ''' DER BEZUGSRAUM IST DER KNACKPUNKT. Bedient wird die Perspektive auf dem ANGEZEIGTEN
        ''' Bild, das Feld eines Objekts wird beim Rendern aber im QUELLRAUM des unbeschnittenen
        ''' Bildes ausgewertet (ImageGeometryMapper.MeshPoint mit den Quellmassen). Steht vor der
        ''' Perspektive schon ein Beschnitt, eine Vierteldrehung oder eine Groessenaenderung, sind
        ''' das zwei verschiedene Raeume - ein direkt uebernommenes Feld zoege die Objekte dann in
        ''' die falsche Richtung.
        '''
        ''' Deshalb wird die Abbildung eingerahmt: Quellpunkt in den Anzeigeraum, dort die
        ''' Perspektive, und wieder zurueck. Genommen wird dafuer die Kette, die auch die Objekte
        ''' selbst durchlaufen (ohne Verzerren und Perspektive, siehe GeometryForAnnotations),
        ''' sonst wuerde eine bereits bestaetigte Verzerrung doppelt einfliessen.</summary>
        Private Sub ApplyPerspectiveToObjects()
            If _annotations Is Nothing OrElse _annotations.Count = 0 Then Return
            Dim baseWidth = GetBaseWidth(), baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return
            Dim size = GetAnnotationDisplayPixelSize()
            If size.Width <= 0 OrElse size.Height <= 0 Then Return
            Dim corners = New Double() {_perspectiveCorners(0), _perspectiveCorners(1),
                                        _perspectiveCorners(2), _perspectiveCorners(3),
                                        _perspectiveCorners(4), _perspectiveCorners(5),
                                        _perspectiveCorners(6), _perspectiveCorners(7)}
            Dim matrix = ImageGeometryMapper.WarpMatrix(size.Width, size.Height,
                                                        _perspectiveHorizontal, _perspectiveVertical,
                                                        _perspectiveAspect, _perspectiveScale, corners)
            If matrix.IsIdentity Then Return

            Dim geometry = BuildAppliedGeometryAdjustments()
            geometry.SourceWidthPixels = baseWidth
            geometry.SourceHeightPixels = baseHeight
            Dim objectGeometry = ImageProcessor.GeometryForAnnotations(geometry)

            ApplyWarpToObjects(Function(px, py)
                                   Dim shown As SkiaSharp.SKPoint
                                   If Not ImageProcessor.TrySourcePointToGeometryOutput(px / 100.0 * baseWidth,
                                                                                        py / 100.0 * baseHeight,
                                                                                        baseWidth, baseHeight,
                                                                                        objectGeometry, shown) Then Return (px, py)
                                   Dim moved = matrix.MapPoint(shown)
                                   Dim back As SkiaSharp.SKPoint
                                   If Not ImageProcessor.TryGeometryOutputToSourcePoint(moved.X, moved.Y,
                                                                                        baseWidth, baseHeight,
                                                                                        objectGeometry, back) Then Return (px, py)
                                   Return (back.X / baseWidth * 100.0, back.Y / baseHeight * 100.0)
                               End Function)
        End Sub

        Private Sub ApplyWarpToObjects(abbildung As Func(Of Double, Double, (X As Double, Y As Double)))
            If _annotations Is Nothing OrElse _annotations.Count = 0 Then Return
            ' Bild und mitwandernde Objekte muessen auf derselben Feinheit ausgewertet werden.
            ' Ein groberes Objektmesh zeigte sonst im Export andere Knicke als das Bild darunter.
            Const Steps As Integer = 48
            For Each a In _annotations
                If a Is Nothing Then Continue For
                Dim alt = a.Warp
                Dim node((Steps + 1) * (Steps + 1) * 2 - 1) As Double
                For rowIdx = 0 To Steps
                    For colIdx = 0 To Steps
                        Dim i = (rowIdx * (Steps + 1) + colIdx) * 2
                        Dim px = colIdx / CDbl(Steps) * 100.0
                        Dim py = rowIdx / CDbl(Steps) * 100.0
                        ' ERST WAS SCHON STEHT, DANN DER NEUE ZUG - und zwar seit der geordneten
                        ' Geometriekette. Das Bild faltet seine Verzerrungen nicht mehr in EIN Feld
                        ' (so tat es ComposeImageWarp, und dort stand der neue Zug deshalb vorn);
                        ' es fuehrt jede bestaetigte Verzerrung als eigenen Schritt und wendet den
                        ' aelteren ZUERST an. Ein Objekt traegt beide in einem einzigen Feld und
                        ' muss sie deshalb in derselben Reihenfolge verketten.
                        '
                        ' Gemessen von "Objekt und Bild bleiben auch nach zwei projektiven
                        ' Schritten zusammen": in der anderen Reihenfolge liefen Objekt und Bild
                        ' nach Gitter plus Perspektive um 17 Punkte auseinander.
                        If alt IsNot Nothing AndAlso Not alt.IsEmpty Then
                            Dim v = ExistingWarp(alt, px, py)
                            px = v.X : py = v.Y
                        End If
                        Dim n = abbildung(px, py)
                        px = n.X : py = n.Y
                        node(i) = px
                        node(i + 1) = py
                    Next
                Next
                a.Warp = New ObjectWarp With {
                    .Kind = "Gitter", .Columns = Steps, .Rows = Steps, .Nodes = node}
            Next
        End Sub

        ''' <summary>Wohin ein Punkt durch eine BEREITS eingetragene Objektverzerrung wandert. Alles
        ''' in Bildprozent. Ueber dieselbe Stelle wie alles andere, aus dem Grund, der bei
        ''' <see cref="NodeMapping"/> steht.</summary>
        Private Shared Function ExistingWarp(v As ObjectWarp, px As Double, py As Double) As (X As Double, Y As Double)
            If v Is Nothing OrElse v.IsEmpty OrElse v.Kind <> "Gitter" Then Return (px, py)
            Dim p = ImageGeometryMapper.MeshPoint(v.Nodes, v.Columns, v.Rows, px, py, 100.0, 100.0)
            Return (CDbl(p.X), CDbl(p.Y))
        End Function

        ''' <summary>Die Abbildung des GITTERWERKZEUGS: das Raster liegt im Verzerrraum in Prozent,
        ''' ein Punkt wandert also zwischen seinen vier Stuetzpunkten.</summary>
        Private Function GridMapping() As Func(Of Double, Double, (X As Double, Y As Double))
            PrepareGrid()
            Return NodeMapping(_warpColumns, _warpRows,
                               CType(_warpX.Clone(), Double()), CType(_warpY.Clone(), Double()))
        End Function

        ''' <summary>Dieselbe Abbildung fuer ein beliebiges Knotenraster. Das Verformen-Werkzeug
        ''' wertet seine vier Randkurven auf so ein Raster aus und geht von dort denselben Weg.
        '''
        ''' Gerechnet wird ueber <see cref="ImageGeometryMapper.MeshPoint"/>, also mit DERSELBEN
        ''' Formel, mit der das Bild gezeichnet und mit der zurueckgerechnet wird. Hier stand die
        ''' Rechnung ein zweites Mal und dabei bilinear ueber die ganze Masche statt ueber deren zwei
        ''' Dreiecke - die Objekte wanderten damit ein Stueck anders als das Bild unter ihnen, und
        ''' zwar am meisten in den Maschenmitten. Eine Formel an zwei Stellen laeuft frueher oder
        ''' spaeter auseinander; hier hatte sie es schon.</summary>
        Private Shared Function NodeMapping(columns As Integer, rows As Integer,
                                            xs As Double(), ys As Double()) As Func(Of Double, Double, (X As Double, Y As Double))
            ' Einmal verschraenken statt bei jedem Punkt: [x0, y0, x1, y1, ...], die Form, in der das
            ' Rezept die Knoten ohnehin fuehrt.
            Dim count = Math.Min(If(xs Is Nothing, 0, xs.Length), If(ys Is Nothing, 0, ys.Length))
            Dim nodes(Math.Max(0, count * 2 - 1)) As Double
            For i = 0 To count - 1
                nodes(i * 2) = xs(i)
                nodes(i * 2 + 1) = ys(i)
            Next
            Return Function(px As Double, py As Double) As (X As Double, Y As Double)
                       ' Bezugsgroesse 100, damit Ein- und Ausgabe Prozent bleiben.
                       Dim p = ImageGeometryMapper.MeshPoint(nodes, columns, rows, px, py, 100.0, 100.0)
                       Return (CDbl(p.X), CDbl(p.Y))
                   End Function
        End Function

        ''' <summary>Die Abbildung des LINIENWERKZEUGS.</summary>
        Private Function LineMapping() As Func(Of Double, Double, (X As Double, Y As Double))
            Dim bewegte = _linien.Where(Function(l) l.IsMoved).ToList()
            Dim q(bewegte.Count * 4 - 1) As Double
            Dim z(bewegte.Count * 4 - 1) As Double
            For i = 0 To bewegte.Count - 1
                Dim l = bewegte(i)
                q(i * 4) = l.SourceAx : q(i * 4 + 1) = l.SourceAy
                q(i * 4 + 2) = l.SourceBx : q(i * 4 + 3) = l.SourceBy
                z(i * 4) = l.TargetAx : z(i * 4 + 1) = l.TargetAy
                z(i * 4 + 2) = l.TargetBx : z(i * 4 + 3) = l.TargetBy
            Next
            Return Function(px As Double, py As Double) As (X As Double, Y As Double)
                       ' Das Feld sagt, WOHER ein Punkt seine Farbe holt. Ein Objekt soll dorthin
                       ' WANDERN, also werden Quelle und Ziel getauscht.
                       Dim p = ImageGeometryMapper.LinePoint(px, py, z, q)
                       Return (CDbl(p.X), CDbl(p.Y))
                   End Function
        End Function

        ''' <summary>Die Linienverzerrung ins Rezept uebernehmen - derselbe Weg wie beim Raster und
        ''' beim Verformen: das Verschiebungsfeld wird auf einem regelmaessigen Raster ausgewertet,
        ''' und ab dort sind alle drei Arten dasselbe.</summary>
        Public Sub ApplyLineWarp()
            ' Gilt die Verzerrung einem Objekt, ist sie dort laengst eingetragen und bleibt eine
            ' Angabe - es gibt nichts zu uebernehmen.
            If WarpsTheObject Then Return
            If Not HasLineChanges Then Return

            Dim steps = MaxImageWarpSteps
            Dim mapping = LineMapping()
            Dim n = (steps + 1) * (steps + 1)
            Dim xs(n - 1) As Double
            Dim ys(n - 1) As Double
            For rowIdx = 0 To steps
                For colIdx = 0 To steps
                    Dim i = rowIdx * (steps + 1) + colIdx
                    Dim z = mapping(colIdx / CDbl(steps) * 100.0, rowIdx / CDbl(steps) * 100.0)
                    xs(i) = z.X
                    ys(i) = z.Y
                Next
            Next
            ApplyNodeWarp(steps, steps, xs, ys,
                          afterApply:=Sub()
                              DisposeLinePreview()
                              _linien.Clear()
                              RaiseLinesChanged()
                          End Sub)
        End Sub

        ' ── Live-Vorschau der Linienverzerrung ──────────────────────────────────
        '
        ' Derselbe Kanal wie bei Gitter und Tiefen-Unschaerfe: EIN Vorschaubild, mit Halter-Vermerk.
        ' Die Grundlage ist das fertige Anzeigebild MIT Objekten: sie gehen beim Anwenden mit, und
        ' die Vorschau soll zeigen, was danach dasteht.

        Private _linienVorschauBasis As SKBitmap

        Private Sub PrepareLinePreview()
            DisposeLinePreview()
            Dim source = TryCast(DisplayImage, Bitmap)
            If source Is Nothing Then Return
            Try
                Using strom = New IO.MemoryStream()
                    source.Save(strom, PngBitmapEncoderOptions.Default)
                    strom.Position = 0
                    _linienVorschauBasis = SKBitmap.Decode(strom)
                End Using
            Catch
                DisposeLinePreview()
                Return
            End Try
        End Sub


        Private Sub DisposeLinePreview()
            If String.Equals(_vorschauQuelle, "Linien", StringComparison.Ordinal) Then
                ToolPreviewImage = Nothing
                _vorschauQuelle = ""
            End If
            _linienVorschauBasis?.Dispose()
            _linienVorschauBasis = Nothing
        End Sub

        Private Sub RefreshLinePreview()
            If _linienVorschauBasis Is Nothing Then Return
            Dim bw = _linienVorschauBasis.Width, bh = _linienVorschauBasis.Height
            ' Die Vorschau rechnet im ANZEIGEraum, die Linien werden dafuer ueber ihre Anzeigelage
            ' genommen. Am Bild ist das ihre Lage selbst, am markierten Objekt dessen Rechteck.
            Dim values = LineValues
            If values Is Nothing OrElse values.Length < 9 Then Return
            Dim count = CInt(values(0))
            Dim qp As New List(Of Double)(), zp As New List(Of Double)()
            For i = 0 To count - 1
                Dim v(7) As Double
                Dim gilt = True
                For j = 0 To 7
                    v(j) = values(1 + i * 8 + j)
                    If Double.IsNaN(v(j)) Then gilt = False
                Next
                If Not gilt Then Continue For
                If Math.Abs(v(4) - v(0)) < 0.02 AndAlso Math.Abs(v(5) - v(1)) < 0.02 AndAlso
                   Math.Abs(v(6) - v(2)) < 0.02 AndAlso Math.Abs(v(7) - v(3)) < 0.02 Then Continue For
                qp.AddRange({v(0) / 100.0 * bw, v(1) / 100.0 * bh, v(2) / 100.0 * bw, v(3) / 100.0 * bh})
                zp.AddRange({v(4) / 100.0 * bw, v(5) / 100.0 * bh, v(6) / 100.0 * bw, v(7) / 100.0 * bh})
            Next
            If qp.Count = 0 Then
                DisposeLinePreview()
                Return
            End If

            Dim steps = ImageGeometryMapper.LineGridSteps
            Dim sourceX As Single() = Nothing, quellY As Single() = Nothing
            ImageGeometryMapper.LineField(bw, bh, steps, steps, qp.ToArray(), zp.ToArray(), sourceX, quellY)
            If sourceX Is Nothing Then Return
            Dim targetX(sourceX.Length - 1) As Single
            Dim targetY(quellY.Length - 1) As Single
            For rowIdx = 0 To steps
                For colIdx = 0 To steps
                    Dim i = rowIdx * (steps + 1) + colIdx
                    targetX(i) = CSng(colIdx / CDbl(steps) * bw)
                    targetY(i) = CSng(rowIdx / CDbl(steps) * bh)
                Next
            Next
            Dim warped = ImageGeometryMapper.WarpOverGrid(
                _linienVorschauBasis, steps, steps, targetX, targetY, sourceX, quellY)
            If warped Is Nothing OrElse Object.ReferenceEquals(warped, _linienVorschauBasis) Then
                DisposeLinePreview()
                Return
            End If
            Using warped
                ' Direkte Zeilenkopie statt PNG-Rundlauf, wie bei der Gittervorschau: der alte Weg
                ' kodierte und dekodierte je Mausbewegung das ganze Anzeigebild und gab sein SKImage
                ' nie frei.
                ToolPreviewImage = ImageOrientationService.ToAvaloniaBitmapFast(warped)
                _vorschauQuelle = "Linien"
            End Using
        End Sub


        ' ── Verformen: ein Viereck mit KURVIGEN Raendern ────────────────────────
        '
        ' Dasselbe Viereck wie bei der Perspektive, nur sind seine vier Raender Kurven statt
        ' Geraden: vier Ecken, dazu je Kante zwei Griffe. Das Innere folgt den Raendern als
        ' Coons-Flaeche - jeder Punkt liegt dort, wo die vier Randkurven ihn gemeinsam hinlegen.
        ' Damit biegt man eine Flaeche in einem Zug, wofuer man am Stuetzpunktraster ein Dutzend
        ' Punkte einzeln nachfuehren muesste.
        '
        ' Ein zweiter Renderweg entsteht dabei NICHT: die Abbildung wird auf den Knoten eines
        ' regelmaessigen Rasters ausgewertet und geht von dort denselben Weg wie Gitter- und
        ' Linienverzerrung (WarpOverGrid). Beim Objekt steht sie als Gitter in dessen eigener
        ' Verzerrung, beim Bild als Knotenfeld im Rezept. Neu ist allein der Bedienzustand.
        '
        ' Die zwoelf Punkte liegen im VERZERRRAUM in Prozent, wie Raster und Linien und aus
        ' demselben Grund: dort wird die Verformung ausgewertet. Ihre neutrale Lage sitzt deshalb
        ' immer auf den vollen 0 bis 100 - am Bild ist das der sichtbare Ausschnitt selbst.
        ' Reihenfolge: 0 bis 3 die Ecken links oben, rechts oben, rechts unten, links unten;
        ' danach je Kante zwei Griffe in Laufrichtung der Kante (oben, rechts, unten, links).

        ''' <summary>Feinheit des Auswertungsrasters. Achtundvierzig Felder je Richtung halten die
        ''' Bézier-Ränder auch im JPEG-/PNG-Export mit vielen Bildpunkten sichtbar glatt. Bei 24×24
        ''' waren die einzelnen linearen Mesh-Segmente an stark gebogenen Kanten noch klar zu sehen.
        ''' Die 2401 Knoten sind gegenüber dem Bildrender weiterhin klein.</summary>
        Private Const EnvelopeSteps As Integer = 48

        ''' <summary>Wie viele Hilfslinien das Overlay im Inneren zeigt. Nur zum Hinsehen - sie
        ''' sagen, wohin sich die Flaeche zwischen den Raendern legt.</summary>
        Private Const EnvelopeMeshSteps As Integer = 4

        Private _envelope As Double() = Nothing
        Private _envelopeDragIndex As Integer = -1
        ''' Wo der laufende Zug begann - Bezug fuer die Achsentreue mit Umschalt.
        Private _envelopeDragStartX As Double
        Private _envelopeDragStartY As Double

        ''' <summary>Das Rechteck, auf dem die Huellkurve aufsitzt - immer die vollen 0 bis 100.
        '''
        ''' Am OBJEKT ist es dessen eigenes Rechteck. Am BILD ist es der Anzeigeraum, und der IST
        ''' der sichtbare Bereich: die Verzerrung laeuft als eigener Rezeptschritt hinter Beschnitt,
        ''' Drehung, Groesse und Leinwand. Solange sie noch im Quellraum gefuehrt wurde, brauchte es
        ''' hier ein aus der Anzeige zurueckgerechnetes Rechteck, weil sonst nach einem Zuschnitt
        ''' kein einziger der zwoelf Anfasser einen Anzeigeort hatte.
        '''
        ''' Achsenparallel, und das ist kein Schoenheitsfehler, sondern Bedingung: nur dann ist die
        ''' neutrale Huellkurve die IDENTITAET, auch ausserhalb des Rechtecks (die Coons-Flaeche ist
        ''' dort linear und setzt sich glatt fort). Ein gedrehtes Viereck als Bezug wuerde das Bild
        ''' schon beim blossen Oeffnen des Werkzeugs verziehen.</summary>
        Private Shared Function CurrentEnvelopeRect() As (X As Double, Y As Double, Width As Double, Height As Double)
            Return (0.0, 0.0, 100.0, 100.0)
        End Function

        ''' <summary>Das unverformte Viereck: die vier Ecken auf dem Rechteck, die Griffe auf den
        ''' Dritteln ihrer Kante. Genau diese Lage ist die Identitaet.</summary>
        Private Shared Function NeutralEnvelope(rect As (X As Double, Y As Double, Width As Double, Height As Double)) As Double()
            Dim p(23) As Double
            ' Ausgeschriebene Namen, weil l/t/r/b weiter unten die Schleifenvariablen verdecken
            ' wuerden - VB meldet das als Fehler, und der Name waere ohnehin missverstaendlich.
            Dim left = rect.X, top = rect.Y
            Dim right = rect.X + rect.Width, bottom = rect.Y + rect.Height
            Dim corners = New Double() {left, top, right, top, right, bottom, left, bottom}
            Array.Copy(corners, p, 8)
            For edge = 0 To 3
                Dim a = edge, b = (edge + 1) Mod 4
                For k = 0 To 1
                    Dim t = (k + 1) / 3.0
                    p(8 + edge * 4 + k * 2) = corners(a * 2) + (corners(b * 2) - corners(a * 2)) * t
                    p(9 + edge * 4 + k * 2) = corners(a * 2 + 1) + (corners(b * 2 + 1) - corners(a * 2 + 1)) * t
                Next
            Next
            Return p
        End Function

        ''' <summary>Sorgt dafuer, dass zwoelf Punkte dastehen. Ein Nachfuehren des Bezugsrechtecks
        ''' braucht es nicht mehr: es sind immer die vollen 0 bis 100 des Verzerrraums, und ein
        ''' Zuschnitt des Bildes verschiebt den nicht (siehe <see cref="CurrentEnvelopeRect"/>).</summary>
        Private Sub PrepareEnvelope()
            If _envelope Is Nothing OrElse _envelope.Length <> 24 Then ResetEnvelopePoints()
        End Sub

        ''' <summary>Haelt einen Wert innerhalb des Bezugsrechtecks.</summary>
        Private Shared Function ClampToEnvelopeRect(value As Double, start As Double, length As Double) As Double
            If length <= 0 Then Return start
            Return Math.Max(start, Math.Min(start + length, value))
        End Function

        Private Sub ResetEnvelopePoints()
            _envelope = NeutralEnvelope(CurrentEnvelopeRect())
            _envelopeDragIndex = -1
        End Sub

        ''' <summary>Die beiden Griffe, die an einer Ecke haengen: der erste ihrer eigenen Kante und
        ''' der letzte der Kante davor.</summary>
        Private Shared Function EnvelopeCornerHandles(corner As Integer) As Integer()
            Return New Integer() {4 + corner * 2, 4 + ((corner + 3) Mod 4) * 2 + 1}
        End Function

        ''' <summary>Ein Punkt auf einer der vier Randkurven, als kubische Bezierkurve ueber Ecke,
        ''' ihren beiden Griffen und der naechsten Ecke.</summary>
        Private Shared Sub EnvelopeEdgePoint(p As Double(), edge As Integer, t As Double,
                                             ByRef x As Double, ByRef y As Double)
            Dim a = edge, b = (edge + 1) Mod 4
            Dim h0 = 4 + edge * 2, h1 = h0 + 1
            Dim s = 1.0 - t
            Dim w0 = s * s * s, w1 = 3 * s * s * t, w2 = 3 * s * t * t, w3 = t * t * t
            x = p(a * 2) * w0 + p(h0 * 2) * w1 + p(h1 * 2) * w2 + p(b * 2) * w3
            y = p(a * 2 + 1) * w0 + p(h0 * 2 + 1) * w1 + p(h1 * 2 + 1) * w2 + p(b * 2 + 1) * w3
        End Sub

        ''' <summary>Wohin der Punkt (u, v) des Rechtecks durch die Verformung wandert; u und v
        ''' laufen von 0 bis 1, heraus kommen Verzerrraum-Prozent.
        '''
        ''' Das ist die Coons-Flaeche: die Summe der beiden Randinterpolationen minus dem
        ''' bilinearen Anteil der Ecken, der darin doppelt steckt. Untere und linke Kante sind
        ''' entgegen ihrer Laufrichtung gespeichert und werden deshalb rueckwaerts abgetastet.
        ''' Fuer die neutrale Lage ergibt das exakt (100u, 100v).</summary>
        Private Shared Function EnvelopePoint(p As Double(), u As Double, v As Double) As (X As Double, Y As Double)
            Dim tx As Double, ty As Double, rx As Double, ry As Double
            Dim bx As Double, by As Double, lx As Double, ly As Double
            EnvelopeEdgePoint(p, 0, u, tx, ty)
            EnvelopeEdgePoint(p, 1, v, rx, ry)
            EnvelopeEdgePoint(p, 2, 1.0 - u, bx, by)
            EnvelopeEdgePoint(p, 3, 1.0 - v, lx, ly)
            Dim x = (1 - v) * tx + v * bx + (1 - u) * lx + u * rx -
                    ((1 - u) * (1 - v) * p(0) + u * (1 - v) * p(2) + u * v * p(4) + (1 - u) * v * p(6))
            Dim y = (1 - v) * ty + v * by + (1 - u) * ly + u * ry -
                    ((1 - u) * (1 - v) * p(1) + u * (1 - v) * p(3) + u * v * p(5) + (1 - u) * v * p(7))
            Return (x, y)
        End Function

        ''' <summary>Die Verformung auf den Knoten eines regelmaessigen Rasters, in
        ''' Verzerrraum-Prozent - die Form, in der sie der gemeinsame Renderweg erwartet.</summary>
        Private Sub EnvelopeNodes(steps As Integer, ByRef xs As Double(), ByRef ys As Double())
            PrepareEnvelope()
            Dim rect = CurrentEnvelopeRect()
            Dim n = (steps + 1) * (steps + 1)
            ReDim xs(n - 1)
            ReDim ys(n - 1)
            ' Das Knotenraster deckt den GANZEN Verzerrraum ab. Liegt die Huellkurve einmal nicht auf
            ' dessen Rand, laufen u und v fuer die aeusseren Knoten ueber 0 bis 1 hinaus; die
            ' Coons-Flaeche setzt sich dort glatt fort, statt an der Kante abzubrechen. Bei neutraler
            ' Lage ist diese Fortsetzung exakt die Identitaet - sonst verzoege schon das Oeffnen des
            ' Werkzeugs das Bild.
            For rowIdx = 0 To steps
                Dim spaceY = rowIdx / CDbl(steps) * 100.0
                Dim v = (spaceY - rect.Y) / rect.Height
                For colIdx = 0 To steps
                    Dim i = rowIdx * (steps + 1) + colIdx
                    Dim spaceX = colIdx / CDbl(steps) * 100.0
                    Dim u = (spaceX - rect.X) / rect.Width
                    Dim z = EnvelopePoint(_envelope, u, v)
                    xs(i) = z.X
                    ys(i) = z.Y
                Next
            Next
        End Sub

        ''' <summary>Dieselben Knoten in ANZEIGE-Prozent, im Format des Rasters:
        ''' [spalten, zeilen, x0, y0, ...]. Punkte ohne Anzeigeort kommen als NaN heraus.</summary>
        Private Function EnvelopeDisplayNodes(steps As Integer) As Double()
            PrepareEnvelope()
            Dim arr(1 + (steps + 1) * (steps + 1) * 2) As Double
            arr(0) = steps
            arr(1) = steps
            For rowIdx = 0 To steps
                For colIdx = 0 To steps
                    Dim i = rowIdx * (steps + 1) + colIdx
                    ' Nur der sichtbare Bezugsbereich wird in der Vorschau gebraucht. Das
                    ' gespeicherte Mesh rechnet EnvelopeNodes weiterhin ueber das ganze Original
                    ' fort; seine ausserhalb des Crops liegenden Knoten haben im Anzeigebild aber
                    ' keinen Ort und duerfen die Vorschau nicht mehr abbrechen.
                    Dim u = colIdx / CDbl(steps), v = rowIdx / CDbl(steps)
                    Dim target = EnvelopePoint(_envelope, u, v)
                    Dim display = WarpSpaceToDisplay(target.X, target.Y)
                    If display.HasValue Then
                        arr(2 + i * 2) = display.Value.X
                        arr(3 + i * 2) = display.Value.Y
                    Else
                        arr(2 + i * 2) = Double.NaN
                        arr(3 + i * 2) = Double.NaN
                    End If
                Next
            Next
            Return arr
        End Function

        ''' <summary>Die zwoelf Anfasser fuer das Overlay, in ANZEIGE-Prozent: erst die vier Ecken,
        ''' dann die acht Kantengriffe. Punkte ohne Anzeigeort (weggeschnitten, leere
        ''' Begradigungsecke) kommen als NaN heraus - Overlay und Trefferpruefung uebergehen sie
        ''' EINZELN, so wie beim Stuetzpunktraster.</summary>
        Public ReadOnly Property EnvelopeValues As Double()
            Get
                PrepareEnvelope()
                Dim arr(23) As Double
                For i = 0 To 11
                    Dim display = WarpSpaceToDisplay(_envelope(i * 2), _envelope(i * 2 + 1))
                    If display.HasValue Then
                        arr(i * 2) = display.Value.X
                        arr(i * 2 + 1) = display.Value.Y
                    Else
                        arr(i * 2) = Double.NaN
                        arr(i * 2 + 1) = Double.NaN
                    End If
                Next
                Return arr
            End Get
        End Property

        ''' <summary>Die Hilfslinien im Inneren, im Format des Rasters.</summary>
        Public ReadOnly Property EnvelopeMeshValues As Double()
            Get
                Return EnvelopeDisplayNodes(EnvelopeMeshSteps)
            End Get
        End Property

        Public ReadOnly Property HasEnvelopeChanges As Boolean
            Get
                PrepareEnvelope()
                Return EnvelopeDiffersFromNeutral()
            End Get
        End Property

        ''' <summary>Weicht die Huellkurve von ihrer neutralen Lage ab? Verglichen wird gegen das
        ''' Rechteck, auf dem sie AUFGESETZT wurde, nicht gegen den gerade sichtbaren Bereich - sonst
        ''' galte eine unangetastete Kurve nach einem Zuschnitt als verzogen.</summary>
        Private Function EnvelopeDiffersFromNeutral() As Boolean
            If _envelope Is Nothing OrElse _envelope.Length <> 24 Then Return False
            Dim neutral = NeutralEnvelope(CurrentEnvelopeRect())
            For i = 0 To 23
                If Math.Abs(_envelope(i) - neutral(i)) > 0.01 Then Return True
            Next
            Return False
        End Function

        Private Sub RaiseEnvelopeChanged()
            Me.RaisePropertyChanged(NameOf(EnvelopeValues))
            Me.RaisePropertyChanged(NameOf(EnvelopeMeshValues))
            Me.RaisePropertyChanged(NameOf(HasEnvelopeChanges))
        End Sub

        ''' <summary>Fasst den naechstgelegenen der zwoelf Anfasser an, sofern einer in Greifweite
        ''' liegt. Wie beim Raster im ANZEIGERAUM gemessen: greifbar ist, was man sieht.</summary>
        Public Function TryBeginEnvelopeDrag(xPercent As Double, yPercent As Double,
                                             slopXPercent As Double, slopYPercent As Double) As Boolean
            Dim values = EnvelopeValues
            If values Is Nothing Then Return False
            Dim bestDistance = Double.MaxValue
            Dim best = -1
            For i = 0 To 11
                Dim px = values(i * 2), py = values(i * 2 + 1)
                If Double.IsNaN(px) OrElse Double.IsNaN(py) Then Continue For
                Dim dx = (xPercent - px) / Math.Max(0.0001, slopXPercent)
                Dim dy = (yPercent - py) / Math.Max(0.0001, slopYPercent)
                Dim d = dx * dx + dy * dy
                If d <= 1.0 AndAlso d < bestDistance Then
                    bestDistance = d
                    best = i
                End If
            Next
            If best < 0 Then Return False
            PushUndo(CombineHistoryLabel("Verzerren", "Verformen"))
            _envelopeDragIndex = best
            PrepareEnvelope()
            _envelopeDragStartX = _envelope(best * 2)
            _envelopeDragStartY = _envelope(best * 2 + 1)
            If Not WarpsTheObject Then PrepareEnvelopePreview()
            Return True
        End Function

        ''' <param name="axisLock">Umschalt: der Griff bleibt auf der Achse, die ueberwiegt - dieselbe
        ''' Zusage wie am Rasterpunkt und an der Perspektivecke.</param>
        ''' <param name="detachHandles">Alt: eine ECKE geht allein, ihre beiden Kantengriffe bleiben
        ''' stehen. Sonst nimmt sie beide mit, damit die Kante nicht ausbeult - genau das will man
        ''' aber, wenn man eine Kante absichtlich schief ziehen moechte. An einem Kantengriff selbst
        ''' hat die Taste nichts zu tun: er nimmt ohnehin nichts mit.</param>
        Public Sub UpdateEnvelopeDrag(xPercent As Double, yPercent As Double,
                                      Optional axisLock As Boolean = False,
                                      Optional detachHandles As Boolean = False)
            If _envelopeDragIndex < 0 Then Return
            Dim target = DisplayToWarpSpace(xPercent, yPercent)
            ' Ohne gueltigen Punkt im Verzerrraum bleibt der Zug stehen, statt auf einen geratenen
            ' Wert zu springen.
            If Not target.HasValue Then Return
            PrepareEnvelope()
            ' Beim BILD bleiben die Anfasser im Bild: ein herausgezogener Griff laege neben der
            ' Bildflaeche und waere weder anzuzeigen noch je wieder zu fassen. Beim OBJEKT ist der
            ' Bezug das Objektrechteck, dort ist Hinauswandern erlaubt und gewollt - die Ebene
            ' waechst mit.
            Dim nx = CDbl(target.Value.X), ny = CDbl(target.Value.Y)
            ' ACHSENTREU gegen den Beginn des Zuges: die kleinere der beiden Bewegungen faellt weg.
            If axisLock Then
                If Math.Abs(nx - _envelopeDragStartX) >= Math.Abs(ny - _envelopeDragStartY) Then
                    ny = _envelopeDragStartY
                Else
                    nx = _envelopeDragStartX
                End If
            End If
            ' Geklemmt wird auf den sichtbaren Bereich - am Bild ist das der Verzerrraum selbst.
            Dim limit = Not WarpsTheObject
            Dim rect = CurrentEnvelopeRect()
            If limit Then
                nx = ClampToEnvelopeRect(nx, rect.X, rect.Width)
                ny = ClampToEnvelopeRect(ny, rect.Y, rect.Height)
            End If
            ' Eine Ecke nimmt ihre beiden Griffe mit. Sonst bliebe die Kante an ihren alten Griffen
            ' haengen und beulte aus, waehrend die Ecke davonlaeuft. Mit Alt bleibt genau das aus.
            If _envelopeDragIndex < 4 AndAlso Not detachHandles Then
                Dim dx = nx - _envelope(_envelopeDragIndex * 2)
                Dim dy = ny - _envelope(_envelopeDragIndex * 2 + 1)
                For Each h In EnvelopeCornerHandles(_envelopeDragIndex)
                    Dim hx = _envelope(h * 2) + dx
                    Dim hy = _envelope(h * 2 + 1) + dy
                    _envelope(h * 2) = If(limit, ClampToEnvelopeRect(hx, rect.X, rect.Width), hx)
                    _envelope(h * 2 + 1) = If(limit, ClampToEnvelopeRect(hy, rect.Y, rect.Height), hy)
                Next
            End If
            _envelope(_envelopeDragIndex * 2) = nx
            _envelope(_envelopeDragIndex * 2 + 1) = ny
            RaiseEnvelopeChanged()
            ' Gilt der Zug einem OBJEKT, gibt es keine Bildvorschau zu rechnen: die Verformung steht
            ' sofort am Objekt, und das Bild darunter bleibt unangetastet.
            If WarpsTheObject Then
                WriteObjectEnvelope()
                SchedulePreviewUpdate()
            Else
                RefreshEnvelopePreview()
            End If
        End Sub

        Public Sub EndEnvelopeDrag()
            Dim wasDragging = _envelopeDragIndex >= 0
            _envelopeDragIndex = -1
            If wasDragging AndAlso WarpsTheObject Then
                ' Loslassen heisst anwenden - wie bei den Eck-Anfassern.
                WriteObjectEnvelope()
                RefreshPreviewImmediately()
            End If
            ' Beim BILD bleibt die Vorschau stehen: sie zeigt den Stand, den "Anwenden" ins Rezept
            ' schreiben wuerde.
        End Sub

        Public Sub ResetEnvelope()
            If Not HasEnvelopeChanges Then Return
            CaptureUndoState("Verformen")
            DisposeGridPreview()
            ResetEnvelopePoints()
            RaiseEnvelopeChanged()
            ' Am Objekt IST die Verformung die Verzerrung selbst - ein gerades Viereck heisst also,
            ' dass auch am Objekt nichts mehr stehen darf.
            If WarpsTheObject Then
                WriteObjectEnvelope()
                RefreshPreviewImmediately()
            End If
        End Sub

        ''' <summary>Die Verformung anwenden - derselbe Weg wie beim Raster: sie wandert als
        ''' Knotenfeld ins Rezept, nicht in die Pixel.</summary>
        Public Sub ApplyEnvelopeWarp()
            ' Wie bei Raster und Linien: am Objekt gibt es nichts anzuwenden, dort steht die
            ' Verzerrung schon laufend im Objekt selbst.
            If WarpsTheObject Then Return
            If Not HasEnvelopeChanges Then Return
            Dim xs As Double() = Nothing, ys As Double() = Nothing
            EnvelopeNodes(EnvelopeSteps, xs, ys)
            ApplyNodeWarp(EnvelopeSteps, EnvelopeSteps, xs, ys,
                          afterApply:=Sub()
                              DisposeGridPreview()
                              ResetEnvelopePoints()
                              RaiseEnvelopeChanged()
                          End Sub)
        End Sub

        ''' <summary>Die Verformung als Gitter in die eigene Verzerrung des markierten Objekts
        ''' eintragen - dieselbe Form, die auch das Rasterwerkzeug dort hinterlaesst.</summary>
        Private Sub WriteObjectEnvelope()
            Dim a = CurrentObject()
            If a Is Nothing Then Return
            If Not HasEnvelopeChanges Then
                a.OwnWarp = Nothing
            Else
                Dim xs As Double() = Nothing, ys As Double() = Nothing
                EnvelopeNodes(EnvelopeSteps, xs, ys)
                Dim node(xs.Length * 2 - 1) As Double
                For i = 0 To xs.Length - 1
                    node(i * 2) = xs(i)
                    node(i * 2 + 1) = ys(i)
                Next
                a.OwnWarp = New ObjectWarp With {
                    .Kind = "Gitter", .Columns = EnvelopeSteps, .Rows = EnvelopeSteps, .Nodes = node,
                    .EnvelopePoints = CType(_envelope.Clone(), Double())}
            End If
            RaiseObjectWarpChanged()
        End Sub

        ' Vorschau: derselbe Kanal und dieselbe Grundlage wie beim Raster, nur mit dem feineren
        ' Auswertungsraster. Beide Werkzeuge koennen nie gleichzeitig laufen.

        Private Sub PrepareEnvelopePreview()
            PrepareEnvelope()
            Dim rect = CurrentEnvelopeRect()
            PrepareWarpPreview(EnvelopeSteps, EnvelopeSteps,
                Function(x As Double, y As Double) WarpSpaceToDisplay(
                    rect.X + x / 100.0 * rect.Width,
                    rect.Y + y / 100.0 * rect.Height))
        End Sub

        Private Sub RefreshEnvelopePreview()
            RefreshWarpPreview(EnvelopeSteps, EnvelopeSteps,
                               EnvelopeDisplayNodes(EnvelopeSteps), "Verformen")
        End Sub


        ' ── Ein Objekt fuer sich verzerren ──────────────────────────────────────
        '
        ' Ist im Verzerren-Werkzeug ein Objekt markiert, gehoeren die Anfasser IHM und nicht dem
        ' Bild. Die vier Ecken liegen dann auf dem Objektrechteck, und gespeichert werden sie in
        ' Prozent DES OBJEKTS - so wandert die Verzerrung mit, wenn man das Objekt verschiebt,
        ' statt sich zu aendern.

        Private _objectCornerDrag As Integer = -1

        ''' <summary>Gehoert das Verzerren gerade einem Objekt statt dem Bild?
        '''
        ''' Gilt fuer ALLE drei Arten, nicht nur fuer die Perspektive. Ein markiertes Objekt ist die
        ''' Ansage, woran gearbeitet wird - dass die Ecken es verzerrten, Gitter und Linien aber
        ''' weiter das Bild darunter, war genau die fehlende Entkopplung. Ohne markiertes Objekt
        ''' bleibt alles wie bisher: die Verzerrung trifft das Bild.</summary>
        Public ReadOnly Property WarpsTheObject As Boolean
            Get
                Return ShowWarpAdjustments AndAlso HasSelectedAnnotation AndAlso
                       Not String.IsNullOrEmpty(_warpMode)
            End Get
        End Property

        ''' <summary>Liegen die vier Eck-Anfasser auf dem Objekt? Nur in der Perspektive - Gitter und
        ''' Linien haben ihre eigenen Anfasser.</summary>
        Public ReadOnly Property ShowsObjectCorners As Boolean
            Get
                Return WarpsTheObject AndAlso IsWarpPerspective
            End Get
        End Property

        ''' <summary>Das Gegenstueck: die Verzerrung trifft das BILD und braucht deshalb ein
        ''' ausdrueckliches "Anwenden". Nur dann gibt es etwas zu uebernehmen.</summary>
        Public ReadOnly Property WarpsTheImage As Boolean
            Get
                Return Not WarpsTheObject
            End Get
        End Property

        ''' <summary>Steht eine Verzerrung offen, die noch übernommen werden muss?
        '''
        ''' Nur am BILD: dort ist der Zug eine schwebende Vorschau mit "Anwenden". Am OBJEKT wirkt
        ''' er sofort und ist eine Angabe wie jede andere - es gibt nichts zu bestätigen. Die
        ''' PERSPEKTIVE steht in Reglern, die man auch nach dem Wechsel noch sieht, und zählt
        ''' deshalb ebenfalls nicht dazu.</summary>
        Public ReadOnly Property HasOpenWarpTransaction As Boolean
            Get
                If Not WarpsTheImage Then Return False
                ' Enter und Esc dürfen nur den Zustand bedienen, den der Nutzer gerade sehen
                ' und beurteilen kann. Gitter, Linien und Hülle bleiben beim Umschalten bewusst
                ' stehen, damit man zu ihnen zurückkehren kann; sie dürfen deshalb nicht als
                ' unsichtbarer Nebenbestandteil einer Transaktion gelten.
                Select Case _warpMode
                    Case "Gitter"
                        Return HasWarpGridChanges
                    Case "Linien"
                        Return HasLineChanges
                    Case "Verformen"
                        Return HasEnvelopeChanges
                    Case Else
                        Return False
                End Select
            End Get
        End Property

        ''' <summary>Die offene Verzerrung übernehmen - der Eingabetaste.
        '''
        ''' Bis dahin ging das nur über den Knopf im Panel. Enter und Esc sind in jedem
        ''' Referenzprogramm der Abschluss einer solchen Transaktion, und wer die Hand an der Maus
        ''' hat, sucht sie zuerst.</summary>
        Public Sub ApplyOpenWarpTransaction()
            If Not HasOpenWarpTransaction Then Return
            Select Case _warpMode
                Case "Gitter"
                    ApplyWarpGrid()
                Case "Linien"
                    ApplyLineWarp()
                Case "Verformen"
                    ApplyEnvelopeWarp()
            End Select
        End Sub

        ''' <summary>Die offene Verzerrung verwerfen - der Escape-Taste. Zurück auf gerade, nichts
        ''' wird übernommen.</summary>
        Public Sub DiscardOpenWarpTransaction()
            If Not HasOpenWarpTransaction Then Return
            Select Case _warpMode
                Case "Gitter"
                    ResetWarpGrid()
                Case "Linien"
                    ResetLines()
                Case "Verformen"
                    ResetEnvelope()
            End Select
            StatusText = LocalizationService.T("Verzerrung verworfen")
        End Sub

        ''' <summary>Traegt der Auswahlrahmen seine Griffe? Beim Verzerren nicht: die Ecken der
        ''' Verzerrung liegen auf DEMSELBEN Rechteck. Zwei Werkzeuge an derselben Stelle heisst, dass
        ''' der Zug je nach getroffenem Pixel das eine oder das andere meint - und der Rahmen zeigt
        ''' Griffe, die dort gar nichts tun sollen. Der Rahmen selbst bleibt: man soll sehen, welches
        ''' Objekt gemeint ist.</summary>
        Public ReadOnly Property ShowsObjectFrameHandles As Boolean
            Get
                ' Der PFAD ist derselbe Fall: seine Stuetzpunkte liegen auf und in dem Rechteck, das
                ' der Rahmen zeigt. Blieben die Griffe stehen, kaeme man an die Punkte nicht mehr
                ' heran. Der Rahmen selbst bleibt - er sagt, welches Objekt gemeint ist.
                Return Not WarpsTheObject AndAlso Not CanEditPathNodes
            End Get
        End Property

        ''' <summary>Der Raum, in dem Gitter, Linien und Verformen gefuehrt werden, in
        ''' ANZEIGE-Prozent ausgedrueckt: ohne markiertes Objekt IST es der Anzeigeraum, mit
        ''' markiertem Objekt das Rechteck DES OBJEKTS. So liegt das Raster ueber dem, was es
        ''' verzerrt.
        '''
        ''' AM BILD IST DAS SEIT DER GEORDNETEN GEOMETRIEKETTE DER ANZEIGERAUM SELBST, nicht mehr
        ''' der Quellraum. Eine bestaetigte Verzerrung ist heute ein eigener Rezeptschritt und laeuft
        ''' HINTER Beschnitt, Drehung, Groesse und Leinwand; sie wird also genau in dem Raum
        ''' ausgewertet, in dem man sie zieht. Ein im Quellraum gefuehrtes Bedienraster passte dazu
        ''' nicht mehr - nach einer Vierteldrehung wirkte ein Zug an der falschen Achse, nach einem
        ''' Zuschnitt hatten Anfasser gar keinen Anzeigeort mehr (weder gezeichnet noch greifbar),
        ''' und die Live-Vorschau stieg bei einem einzigen solchen Punkt ganz aus.</summary>
        Private Function WarpSpaceToDisplay(xPercent As Double, yPercent As Double) As SKPoint?
            If Not WarpsTheObject Then Return New SKPoint(CSng(xPercent), CSng(yPercent))
            Dim r = GetSelectedAnnotationDisplayRectPercent()
            If r.Width <= 0 OrElse r.Height <= 0 Then Return Nothing
            Dim point = ObjectWarpPointThroughGeometry(xPercent, yPercent)
            Return New SKPoint(CSng(r.X + point.X / 100.0 * r.Width),
                               CSng(r.Y + point.Y / 100.0 * r.Height))
        End Function

        ''' <summary>Gegenrichtung zu <see cref="WarpSpaceToDisplay"/>.</summary>
        Private Function DisplayToWarpSpace(xPercent As Double, yPercent As Double) As SKPoint?
            If Not WarpsTheObject Then Return New SKPoint(CSng(xPercent), CSng(yPercent))
            Dim r = GetSelectedAnnotationDisplayRectPercent()
            If r.Width <= 0 OrElse r.Height <= 0 Then Return Nothing
            Dim point = DisplayPointThroughObjectWarpGeometry((xPercent - r.X) / r.Width * 100.0,
                                                               (yPercent - r.Y) / r.Height * 100.0)
            Return New SKPoint(CSng(point.X), CSng(point.Y))
        End Function

        ''' <summary>Ein Gitterpunkt in die Anzeige. Der Beschnitt braucht hier keine eigene
        ''' Umrechnung mehr: das Anzeigebild IST der beschnittene Ausschnitt.</summary>
        Private Function GridPointToDisplay(xPercent As Double, yPercent As Double) As SKPoint?
            Return WarpSpaceToDisplay(xPercent, yPercent)
        End Function

        ''' <summary>Gegenrichtung zu <see cref="GridPointToDisplay"/>.</summary>
        Private Function DisplayToGridPoint(xPercent As Double, yPercent As Double) As SKPoint?
            Return DisplayToWarpSpace(xPercent, yPercent)
        End Function

        ''' <summary>Die sichtbare Lage der Objektanfasser: die ENDGUELTIGE Drehung und Spiegelung
        ''' des Objekts, dieselbe, mit der der Renderer sein Verzerrungsfeld ueber die fertige
        ''' Ebene legt (ImageProcessor.TransformAnnotationForGeometry, ganz am Ende der Kette).
        '''
        ''' Beide Seiten muessen denselben EINEN Winkel nehmen. Hier stand frueher die eigene
        ''' Drehung mit den eigenen Spiegelungen und danach den Spiegelungen des Bildes, und die
        ''' Bildspiegelung kam obendrein aus den rohen Feldern statt aus der zusammengesetzten
        ''' Lage - eine geordnete Schrittfolge fuehrt ihre Spiegelungen aber in den Schritten.</summary>
        Private Function ObjectWarpPointThroughGeometry(xPercent As Double, yPercent As Double) As (X As Double, Y As Double)
            If CurrentObject() Is Nothing Then Return (xPercent, yPercent)
            Return TransformObjectWarpPoint(xPercent, yPercent, _annotationRotation,
                                            AnnotationDisplayFlipHorizontal, AnnotationDisplayFlipVertical)
        End Function

        Private Function DisplayPointThroughObjectWarpGeometry(xPercent As Double, yPercent As Double) As (X As Double, Y As Double)
            If CurrentObject() Is Nothing Then Return (xPercent, yPercent)
            Return InverseTransformObjectWarpPoint(xPercent, yPercent, _annotationRotation,
                                                   AnnotationDisplayFlipHorizontal, AnnotationDisplayFlipVertical)
        End Function

        Private Shared Function TransformObjectWarpPoint(x As Double, y As Double, rotationDegrees As Double,
                                                         flipH As Boolean, flipV As Boolean) As (X As Double, Y As Double)
            Dim radians = rotationDegrees * Math.PI / 180.0
            Dim dx = x - 50.0, dy = y - 50.0
            Dim transformedX = 50.0 + Math.Cos(radians) * dx - Math.Sin(radians) * dy
            Dim transformedY = 50.0 + Math.Sin(radians) * dx + Math.Cos(radians) * dy
            If flipH Then transformedX = 100.0 - transformedX
            If flipV Then transformedY = 100.0 - transformedY
            Return (transformedX, transformedY)
        End Function

        Private Shared Function InverseTransformObjectWarpPoint(x As Double, y As Double, rotationDegrees As Double,
                                                                flipH As Boolean, flipV As Boolean) As (X As Double, Y As Double)
            If flipH Then x = 100.0 - x
            If flipV Then y = 100.0 - y
            Return TransformObjectWarpPoint(x, y, -rotationDegrees, False, False)
        End Function

        Private Function CurrentObject() As ImageAnnotation
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return Nothing
            Return _annotations(_selectedAnnotationIndex)
        End Function

        ''' <summary>Die vier Ecken der eigenen Verzerrung, in Prozent des Objekts. Ohne eigene
        ''' Verzerrung ist es das unverzerrte Rechteck.</summary>
        Private Function ObjectCornersRaw() As Double()
            Dim a = CurrentObject()
            If a Is Nothing Then Return Nothing
            Dim v = a.OwnWarp
            If v IsNot Nothing AndAlso v.Kind = "Perspektive" AndAlso v.Corners IsNot Nothing AndAlso
               v.Corners.Length = 8 Then
                Return CType(v.Corners.Clone(), Double())
            End If
            Return New Double() {0, 0, 100, 0, 100, 100, 0, 100}
        End Function

        ''' <summary>Die vier Ecken in ANZEIGE-Prozent, fuer das Overlay.</summary>
        Public ReadOnly Property ObjectCornerValues As Double()
            Get
                If Not ShowsObjectCorners Then Return Nothing
                Dim roh = ObjectCornersRaw()
                If roh Is Nothing Then Return Nothing
                ' DASSELBE Rechteck wie der Auswahlrahmen: es beruecksichtigt den Anker eines
                ' Wasserzeichens, die gespeicherten Pixel tun das nicht. Vorher lagen die
                ' Verzerrungsecken bei einem verankerten Objekt neben dem Objekt.
                Dim r = GetSelectedAnnotationDisplayRectPercent()
                Dim x = r.X, y = r.Y
                Dim b = r.Width, h = r.Height
                If b <= 0 OrElse h <= 0 Then Return Nothing
                Dim arr(7) As Double
                For i = 0 To 3
                    arr(i * 2) = x + roh(i * 2) / 100.0 * b
                    arr(i * 2 + 1) = y + roh(i * 2 + 1) / 100.0 * h
                Next
                Return arr
            End Get
        End Property

        Public ReadOnly Property HasObjectWarp As Boolean
            Get
                Dim a = CurrentObject()
                Return a IsNot Nothing AndAlso a.OwnWarp IsNot Nothing AndAlso
                       Not a.OwnWarp.IsEmpty
            End Get
        End Property

        Public Function TryBeginObjectCornerDrag(xPercent As Double, yPercent As Double,
                                               slopXPercent As Double, slopYPercent As Double) As Boolean
            Dim values = ObjectCornerValues
            If values Is Nothing Then Return False
            Dim bestDistance = Double.MaxValue
            Dim bester = -1
            For i = 0 To 3
                Dim dx = (xPercent - values(i * 2)) / Math.Max(0.0001, slopXPercent)
                Dim dy = (yPercent - values(i * 2 + 1)) / Math.Max(0.0001, slopYPercent)
                Dim d = dx * dx + dy * dy
                If d <= 1.0 AndAlso d < bestDistance Then
                    bestDistance = d
                    bester = i
                End If
            Next
            If bester < 0 Then Return False
            CaptureUndoState("Objektverzerrung")
            _objectCornerDrag = bester
            Return True
        End Function

        Public Sub UpdateObjectCornerDrag(xPercent As Double, yPercent As Double)
            If _objectCornerDrag < 0 Then Return
            Dim a = CurrentObject()
            If a Is Nothing Then Return
            Dim r = GetSelectedAnnotationDisplayRectPercent()
            Dim b = r.Width, h = r.Height
            If b <= 0 OrElse h <= 0 Then Return
            Dim roh = ObjectCornersRaw()
            If roh Is Nothing Then Return
            ' Zurueck in Objektprozent. BEWUSST nicht geklemmt: eine Ecke ueber den Objektrand hinaus
            ' zu ziehen ist genau der Sinn der Sache - die Ebene waechst beim Zeichnen mit.
            roh(_objectCornerDrag * 2) = (xPercent - r.X) / b * 100.0
            roh(_objectCornerDrag * 2 + 1) = (yPercent - r.Y) / h * 100.0
            a.OwnWarp = New ObjectWarp With {.Kind = "Perspektive", .Corners = roh}
            RaiseObjectWarpChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Loslassen heisst hier ANWENDEN: die Verzerrung wird sofort gerechnet, statt auf
        ''' den Entprellzeitgeber der Vorschau zu warten.
        '''
        ''' Waehrend des Ziehens folgt nur das Anfasser-Viereck; das Objekt selbst haengt an der
        ''' Vorschau, und die startet ihren Zeitgeber bei JEDER Mausbewegung neu. Das Ergebnis kam
        ''' also erst eine Verzoegerung nach dem Loslassen - genau in dem Moment, in dem man
        ''' hinsieht, stand noch das alte Bild.</summary>
        Public Sub EndObjectCornerDrag()
            If _objectCornerDrag < 0 Then Return
            _objectCornerDrag = -1
            RefreshPreviewImmediately()
        End Sub

        ''' <summary>Die eigene Verzerrung des markierten Objekts wieder wegnehmen.</summary>
        Public Sub ResetObjectWarp()
            Dim a = CurrentObject()
            If a Is Nothing OrElse a.OwnWarp Is Nothing Then Return
            CaptureUndoState("Objektverzerrung")
            a.OwnWarp = Nothing
            RaiseObjectWarpChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub RaiseObjectWarpChanged()
            Me.RaisePropertyChanged(NameOf(ObjectCornerValues))
            Me.RaisePropertyChanged(NameOf(HasObjectWarp))
            Me.RaisePropertyChanged(NameOf(WarpsTheObject))
            Me.RaisePropertyChanged(NameOf(WarpsTheImage))
            Me.RaisePropertyChanged(NameOf(ShowsObjectCorners))
            Me.RaisePropertyChanged(NameOf(ShowsObjectFrameHandles))
        End Sub

        ' ── Gitter und Linien auf ein markiertes Objekt ─────────────────────────
        '
        ' Beide Werkzeuge fuehren ihren Zustand in dem Raum, in dem gerade verzerrt wird (siehe
        ' VerzerrRaumZuAnzeige). Mit markiertem Objekt sind die Zahlen also OBJEKTprozent - genau
        ' das, was die eigene Verzerrung des Objekts erwartet. Es wird darum nichts umgerechnet,
        ' sondern nur eingetragen.
        '
        ' Und es wird nichts gebacken: die Verzerrung bleibt eine Angabe am Objekt und damit
        ' aenderbar - ein verzerrter Text laesst sich weiter tippen. Ein "Anwenden" gibt es hier
        ' deshalb nicht; es steht nur, wenn das BILD gemeint ist.

        Private Sub WriteObjectGrid()
            Dim a = CurrentObject()
            If a Is Nothing Then Return
            PrepareGrid()
            Dim node(_warpX.Length * 2 - 1) As Double
            For i = 0 To _warpX.Length - 1
                node(i * 2) = _warpX(i)
                node(i * 2 + 1) = _warpY(i)
            Next
            If Not HasWarpGridChanges Then
                a.OwnWarp = Nothing
            Else
                a.OwnWarp = New ObjectWarp With {
                    .Kind = "Gitter", .Columns = _warpColumns, .Rows = _warpRows, .Nodes = node}
            End If
            RaiseObjectWarpChanged()
        End Sub

        Private Sub WriteObjectLines()
            Dim a = CurrentObject()
            If a Is Nothing Then Return
            Dim bewegte = _linien.Where(Function(l) l.IsMoved).ToList()
            If bewegte.Count = 0 Then
                a.OwnWarp = Nothing
                RaiseObjectWarpChanged()
                Return
            End If
            Dim q(bewegte.Count * 4 - 1) As Double
            Dim z(bewegte.Count * 4 - 1) As Double
            For i = 0 To bewegte.Count - 1
                Dim l = bewegte(i)
                q(i * 4) = l.SourceAx : q(i * 4 + 1) = l.SourceAy
                q(i * 4 + 2) = l.SourceBx : q(i * 4 + 3) = l.SourceBy
                z(i * 4) = l.TargetAx : z(i * 4 + 1) = l.TargetAy
                z(i * 4 + 2) = l.TargetBx : z(i * 4 + 3) = l.TargetBy
            Next
            a.OwnWarp = New ObjectWarp With {
                .Kind = "Linien", .LineSource = q, .LineTarget = z}
            RaiseObjectWarpChanged()
        End Sub

        ''' <summary>Welcher Raum gerade gilt: der Index des markierten Objekts, oder -1 fuer das
        ''' Bild.</summary>
        Private _warpSpaceObject As Integer = -1
        Private _gridImageX As Double() = Nothing
        Private _gridImageY As Double() = Nothing
        ''' Die Rastergroesse gehoert zum beiseitegelegten BILD-Stand dazu. Ohne sie ging er verloren,
        ''' sobald man die Groesse wechselte, waehrend ein Objekt markiert war: beim Abwaehlen passte
        ''' die Feldlaenge nicht mehr, und der Stand fiel kommentarlos auf neutral zurueck.
        Private _gridImageColumns As Integer = -1
        Private _gridImageRows As Integer = -1
        Private _envelopeImage As Double() = Nothing
        Private ReadOnly _linienBild As New List(Of WarpLine)()

        ''' <summary>Haelt Gitter und Linien in dem Raum, in dem gerade verzerrt wird.
        '''
        ''' Wechselt der Raum - ein Objekt wird markiert, ein anderes, oder gar keines mehr -, dann
        ''' bedeuten dieselben Zahlen etwas anderes. Der Bildstand wird deshalb beiseite gelegt und
        ''' kommt beim Abwaehlen zurueck; der Objektstand kommt aus dem Objekt selbst, sodass eine
        ''' zweite Verzerrung dort weitermacht, wo die erste aufgehoert hat.</summary>
        Private Sub RefreshWarpSpace()
            Dim jetzt = If(WarpsTheObject, _selectedAnnotationIndex, -1)
            If jetzt = _warpSpaceObject Then Return

            If _warpSpaceObject < 0 Then
                PrepareGrid()
                _gridImageX = CType(_warpX.Clone(), Double())
                _gridImageY = CType(_warpY.Clone(), Double())
                _gridImageColumns = _warpColumns
                _gridImageRows = _warpRows
                PrepareEnvelope()
                _envelopeImage = CType(_envelope.Clone(), Double())
                _linienBild.Clear()
                _linienBild.AddRange(_linien)
            End If

            _warpSpaceObject = jetzt
            _warpDragIndex = -1
            _linienDragIndex = -1
            _linienDragTeil = -1
            _envelopeDragIndex = -1
            DisposeGridPreview()
            DisposeLinePreview()
            _linien.Clear()

            If jetzt < 0 Then
                ' Die Rastergroesse kommt MIT zurueck: sie gehoert zum beiseitegelegten Stand. Wer
                ' sie am Objekt gewechselt hat, meinte das Objekt - der Bildstand darf daran nicht
                ' zerbrechen.
                Dim count = (_gridImageColumns + 1) * (_gridImageRows + 1)
                If _gridImageX IsNot Nothing AndAlso _gridImageColumns >= 2 AndAlso _gridImageX.Length = count Then
                    If _gridImageColumns <> _warpColumns OrElse _gridImageRows <> _warpRows Then
                        _warpColumns = _gridImageColumns
                        _warpRows = _gridImageRows
                        Me.RaisePropertyChanged(NameOf(WarpGridSize))
                    End If
                    _warpX = CType(_gridImageX.Clone(), Double())
                    _warpY = CType(_gridImageY.Clone(), Double())
                Else
                    ResetGrid()
                End If
                If _envelopeImage IsNot Nothing AndAlso _envelopeImage.Length = 24 Then
                    _envelope = CType(_envelopeImage.Clone(), Double())
                Else
                    ResetEnvelopePoints()
                End If
                _linien.AddRange(_linienBild)
            Else
                LoadWarpFromObject()
            End If

            RaiseLinesChanged()
            RaiseEnvelopeChanged()
            Me.RaisePropertyChanged(NameOf(WarpGridValues))
            Me.RaisePropertyChanged(NameOf(HasWarpGridChanges))
        End Sub

        ''' <summary>Gitter, Linien und die editierbare Hüllkurve aus der eigenen Verzerrung des
        ''' markierten Objekts holen.</summary>
        Private Sub LoadWarpFromObject()
            ResetGrid()
            ResetEnvelopePoints()
            Dim a = CurrentObject()
            Dim v = a?.OwnWarp
            If v Is Nothing OrElse v.IsEmpty Then Return

            ' Neue Dokumente bewahren die zwölf Originalpunkte. Für ältere Dokumente lässt sich die
            ' Hüllkurve aus den Knoten bei einem Drittel und zwei Dritteln der vier Ränder exakt
            ' zurückgewinnen (das Verformen-Raster besitzt 12 oder 24, also durch drei teilbare,
            ' Felder). So führt auch das erste Nachbearbeiten eines alten Objekts nicht zum Reset.
            If v.EnvelopePoints IsNot Nothing AndAlso v.EnvelopePoints.Length = 24 Then
                _envelope = CType(v.EnvelopePoints.Clone(), Double())
            Else
                Dim restored As Double() = Nothing
                If TryRestoreEnvelopeFromGrid(v, restored) Then _envelope = restored
            End If

            If v.Kind = "Gitter" AndAlso v.Columns = _warpColumns AndAlso v.Rows = _warpRows Then
                For i = 0 To _warpX.Length - 1
                    _warpX(i) = v.Nodes(i * 2)
                    _warpY(i) = v.Nodes(i * 2 + 1)
                Next
            ElseIf v.Kind = "Linien" Then
                For i = 0 To v.LineSource.Length \ 4 - 1
                    _linien.Add(New WarpLine With {
                        .SourceAx = v.LineSource(i * 4), .SourceAy = v.LineSource(i * 4 + 1),
                        .SourceBx = v.LineSource(i * 4 + 2), .SourceBy = v.LineSource(i * 4 + 3),
                        .TargetAx = v.LineTarget(i * 4), .TargetAy = v.LineTarget(i * 4 + 1),
                        .TargetBx = v.LineTarget(i * 4 + 2), .TargetBy = v.LineTarget(i * 4 + 3)})
                Next
            End If
        End Sub

        Private Shared Function TryRestoreEnvelopeFromGrid(warp As ObjectWarp,
                                                            ByRef points As Double()) As Boolean
            If warp Is Nothing OrElse warp.Kind <> "Gitter" OrElse warp.Columns < 3 OrElse warp.Rows < 3 OrElse
               warp.Columns Mod 3 <> 0 OrElse warp.Rows Mod 3 <> 0 OrElse
               warp.Nodes Is Nothing OrElse warp.Nodes.Length <> (warp.Columns + 1) * (warp.Rows + 1) * 2 Then Return False

            ' Der Weg gilt einem OBJEKT: dessen Bezug ist immer sein eigenes Rechteck, ein Zuschnitt
            ' des Bildes geht ihn nichts an.
            Dim p = NeutralEnvelope((0.0, 0.0, 100.0, 100.0))
            Dim thirdColumn = warp.Columns \ 3, thirdRow = warp.Rows \ 3
            RestoreEnvelopeEdgeFromGrid(warp, p, 0, 0, 0, thirdColumn, 0, thirdColumn * 2, 0, warp.Columns, 0)
            RestoreEnvelopeEdgeFromGrid(warp, p, 1, warp.Columns, 0, warp.Columns, thirdRow, warp.Columns, thirdRow * 2, warp.Columns, warp.Rows)
            RestoreEnvelopeEdgeFromGrid(warp, p, 2, warp.Columns, warp.Rows, thirdColumn * 2, warp.Rows, thirdColumn, warp.Rows, 0, warp.Rows)
            RestoreEnvelopeEdgeFromGrid(warp, p, 3, 0, warp.Rows, 0, thirdRow * 2, 0, thirdRow, 0, 0)
            points = p
            Return True
        End Function

        ''' <summary>Gewinnt die zwei kubischen Bézier-Griffe einer Kante aus ihren Proben bei
        ''' t=1/3 und t=2/3 zurück. Die Formel ist die Umkehrung der kubischen Bézier-Gleichung.</summary>
        Private Shared Sub RestoreEnvelopeEdgeFromGrid(warp As ObjectWarp, points As Double(), edge As Integer,
                                                        startColumn As Integer, startRow As Integer,
                                                        firstColumn As Integer, firstRow As Integer,
                                                        secondColumn As Integer, secondRow As Integer,
                                                        endColumn As Integer, endRow As Integer)
            Dim p0 = EnvelopeGridNode(warp, startColumn, startRow)
            Dim b1 = EnvelopeGridNode(warp, firstColumn, firstRow)
            Dim b2 = EnvelopeGridNode(warp, secondColumn, secondRow)
            Dim p3 = EnvelopeGridNode(warp, endColumn, endRow)
            points(edge * 2) = p0.X : points(edge * 2 + 1) = p0.Y
            Dim handle = 8 + edge * 4
            points(handle) = 3 * b1.X - 1.5 * b2.X - (5.0 / 6.0) * p0.X + (1.0 / 3.0) * p3.X
            points(handle + 1) = 3 * b1.Y - 1.5 * b2.Y - (5.0 / 6.0) * p0.Y + (1.0 / 3.0) * p3.Y
            points(handle + 2) = -1.5 * b1.X + 3 * b2.X + (1.0 / 3.0) * p0.X - (5.0 / 6.0) * p3.X
            points(handle + 3) = -1.5 * b1.Y + 3 * b2.Y + (1.0 / 3.0) * p0.Y - (5.0 / 6.0) * p3.Y
        End Sub

        Private Shared Function EnvelopeGridNode(warp As ObjectWarp, column As Integer, row As Integer) As (X As Double, Y As Double)
            Dim i = (row * (warp.Columns + 1) + column) * 2
            Return (warp.Nodes(i), warp.Nodes(i + 1))
        End Function

        ''' <summary>Fasst den naechstgelegenen Rasterpunkt an, sofern einer in Greifweite liegt.
        ''' Die Trefferpruefung laeuft im ANZEIGERAUM: greifbar ist, was man sieht, und der Abstand
        ''' soll der auf dem Bildschirm sein - im Quellraum waere er bei gedrehtem oder beschnittenem
        ''' Bild ein anderer.</summary>
        Public Function TryBeginWarpDrag(xPercent As Double, yPercent As Double,
                                         slopXPercent As Double, slopYPercent As Double) As Boolean
            PrepareGrid()
            Dim bestDistance = Double.MaxValue
            Dim bester = -1
            For i = 0 To _warpX.Length - 1
                Dim anzeige = WarpSpaceToDisplay(_warpX(i), _warpY(i))
                If Not anzeige.HasValue Then Continue For
                Dim dx = (xPercent - anzeige.Value.X) / Math.Max(0.0001, slopXPercent)
                Dim dy = (yPercent - anzeige.Value.Y) / Math.Max(0.0001, slopYPercent)
                Dim d = dx * dx + dy * dy
                If d <= 1.0 AndAlso d < bestDistance Then
                    bestDistance = d
                    bester = i
                End If
            Next
            If bester < 0 Then Return False
            PushUndo(CombineHistoryLabel("Verzerren", "Gitter"))
            _warpDragIndex = bester
            ' Der Ausgangspunkt fuer die Achsentreue: Umschalt haelt den Zug auf der Achse, die
            ' ueberwiegt, und dafuer braucht es die Stelle, an der er begann.
            _warpDragStartX = _warpX(bester)
            _warpDragStartY = _warpY(bester)
            PrepareGridPreview()
            Return True
        End Function

        ''' <param name="axisLock">Umschalt: der Punkt bleibt auf der Achse, die ueberwiegt. Ohne das
        ''' war ein waagerechter Zug ueber ein Dutzend Punkte nicht sauber hinzubekommen - genau
        ''' dafuer haelt man in jedem anderen Programm die Umschalttaste.</param>
        Public Sub UpdateWarpDrag(xPercent As Double, yPercent As Double,
                                  Optional axisLock As Boolean = False)
            If _warpDragIndex < 0 Then Return
            Dim target = DisplayToGridPoint(xPercent, yPercent)
            ' Ohne gueltigen Punkt im Verzerrraum bleibt der Zug einfach stehen, statt auf einen
            ' geratenen Wert zu springen.
            If Not target.HasValue Then Return
            ' Die Randpunkte duerfen NICHT ins Bild hinein oder aus ihm heraus wandern: sonst
            ' entstehen an der Bildkante durchsichtige Streifen oder es wird Bildinhalt
            ' abgeschnitten, ohne dass man es beim Ziehen sieht. Sie bleiben auf ihrer Kante und
            ' laufen nur DARAUF entlang.
            Dim column = _warpDragIndex Mod (_warpColumns + 1)
            Dim row = _warpDragIndex \ (_warpColumns + 1)
            Dim nx = Math.Max(0.0, Math.Min(100.0, CDbl(target.Value.X)))
            Dim ny = Math.Max(0.0, Math.Min(100.0, CDbl(target.Value.Y)))
            ' ACHSENTREU: die kleinere der beiden Bewegungen faellt weg. Verglichen wird gegen den
            ' Beginn des Zuges und nicht gegen den letzten Punkt - sonst waere die Achse bei jedem
            ' Mausereignis neu zu haben, und der Punkt wanderte doch in beide Richtungen.
            If axisLock Then
                If Math.Abs(nx - _warpDragStartX) >= Math.Abs(ny - _warpDragStartY) Then
                    ny = _warpDragStartY
                Else
                    nx = _warpDragStartX
                End If
            End If
            If column = 0 Then nx = 0
            If column = _warpColumns Then nx = 100
            If row = 0 Then ny = 0
            If row = _warpRows Then ny = 100
            _warpX(_warpDragIndex) = nx
            _warpY(_warpDragIndex) = ny
            Me.RaisePropertyChanged(NameOf(WarpGridValues))
            Me.RaisePropertyChanged(NameOf(HasWarpGridChanges))
            ' Gilt der Zug einem OBJEKT, gibt es keine Bildvorschau zu rechnen: die Verzerrung
            ' steht sofort am Objekt, und das Bild darunter bleibt unangetastet.
            If WarpsTheObject Then
                WriteObjectGrid()
                SchedulePreviewUpdate()
            Else
                RefreshGridPreview()
            End If
        End Sub

        Public Sub EndWarpDrag()
            Dim warZug = _warpDragIndex >= 0
            _warpDragIndex = -1
            If warZug AndAlso WarpsTheObject Then
                ' Loslassen heisst anwenden - wie bei den Eck-Anfassern.
                WriteObjectGrid()
                RefreshPreviewImmediately()
                Return
            End If
            ' Die Vorschau BLEIBT nach dem Loslassen stehen: sie zeigt den Stand, den "Anwenden"
            ' uebernehmen wuerde. Sie verschwindet erst beim Zuruecksetzen, beim Anwenden oder wenn das
            ' Werkzeug gewechselt wird - sonst saehe man sein Ergebnis nur waehrend des Ziehens.
        End Sub

        Public Sub ResetWarpGrid()
            If Not HasWarpGridChanges Then Return
            CaptureUndoState("Gitter")
            DisposeGridPreview()
            ResetGrid()
            Me.RaisePropertyChanged(NameOf(WarpGridValues))
            Me.RaisePropertyChanged(NameOf(HasWarpGridChanges))
            ' Am Objekt ist das Raster die Verzerrung selbst - ein gerades Raster heisst also, dass
            ' auch am Objekt nichts mehr stehen darf.
            If WarpsTheObject Then
                WriteObjectGrid()
                RefreshPreviewImmediately()
            End If
        End Sub

        ''' <summary>Die Verzerrung uebernehmen: sie wandert als Knotenfeld ins Rezept und laeuft ab
        ''' dann in der Geometriestufe mit - Masken gehen ueber dieselbe Stufe mit, und beim
        ''' naechsten Oeffnen steht sie wieder da.</summary>
        Public Sub ApplyWarpGrid()
            ' Siehe ApplyLineWarp: am Objekt gibt es nichts zu uebernehmen, dort steht die
            ' Verzerrung schon laufend im Objekt.
            If WarpsTheObject Then Return
            If Not HasWarpGridChanges Then Return
            PrepareGrid()
            ApplyNodeWarp(_warpColumns, _warpRows,
                          CType(_warpX.Clone(), Double()), CType(_warpY.Clone(), Double()),
                          afterApply:=Sub()
                              DisposeGridPreview()
                              ResetGrid()
                              Me.RaisePropertyChanged(NameOf(WarpGridValues))
                              Me.RaisePropertyChanged(NameOf(HasWarpGridChanges))
                          End Sub)
        End Sub

        ''' <summary>Ein Knotenraster ins REZEPT uebernehmen. Gitter, Linien und Verformen gehen hier
        ''' zusammen: sie unterscheiden sich nur darin, WORAUS die Knoten entstehen.
        ''' <paramref name="afterApply"/> raeumt danach den Stand des jeweiligen Werkzeugs auf.
        '''
        ''' Frueher wurde hier in die PIXEL gebacken. Das kostete die Bearbeitbarkeit und war bei RAW
        ''' und PSD sogar ganz verloren: neben diesen Dateien liegt nur das Rezept. Jetzt wird die
        ''' Verzerrung als Knotenraster ins Rezept geschrieben und laeuft bei jedem Render in der
        ''' Geometriestufe mit - beim naechsten Oeffnen also wieder.</summary>
        Private Sub ApplyNodeWarp(columns As Integer, rows As Integer,
                                  xs As Double(), ys As Double(),
                                  Optional afterApply As Action = Nothing)
            If Not HasDocument Then Return

            PushUndo(CombineHistoryLabel("Verzerren", "Verformen"))
            ' Die OBJEKTE gehen mit: sie bekommen die Verzerrung als eigene Angabe und bleiben damit
            ' aenderbar - Text laesst sich weiter tippen. Das Bild darunter braucht das nicht mehr,
            ' seine Verzerrung steht ab jetzt im Rezept.
            ' Das Bedienraster liegt auf dem bereits gerenderten Bild. Ein neuer Warp-Schritt
            ' kommt aber HINTER die vorhandene Geometriekette (Drehung, Crop, Groesse, ...)
            ' und muss deshalb genau in DIESEM Anzeigeraum abgelegt werden. Die alte Rueckrechnung
            ' in den Ursprungsraum liess einen Zug nach 90 Grad an der falschen Achse wirken - bei
            ' symmetrischen Punkten sogar scheinbar gar nicht.
            '
            ' Die eigenen Verzerrungsfelder der Objekte werden hingegen im Quellraum gespeichert.
            ' Fuer sie rahmt MapDisplayWarpToObjectSpace die sichtbare Abbildung mit dem passenden
            ' Hin- und Rueckweg ein, wie es die Perspektive bereits tut.
            Dim displayMapping = NodeMapping(columns, rows, xs, ys)
            ApplyWarpToObjects(MapDisplayWarpToObjectSpace(displayMapping))
            ' Nicht mit einer eventuell früheren Verzerrung zusammenfalten: ihre Reihenfolge
            ' gegenüber Crop, Drehung und Leinwand ist sichtbar. Jede bestätigte Verzerrung wird
            ' deshalb als eigener Pipeline-Schritt abgelegt. Ein noch aus einem ALTEN Rezept
            ' stammendes Feld (_imageWarp) bleibt dabei unangetastet: es gehoert an den Anfang der
            ' Kette, und es in den neuen Schritt zu falten wuerde es hinter Beschnitt und Drehung
            ' schieben - das Bild spraenge beim ersten neuen Zug.
            _geometryOperations.Add(New GeometryOperation With {
                .Kind = "warp",
                .Adjustments = New ImageAdjustments With {.ImageWarp = BuildWarpMesh(displayMapping)}})
            RaiseImageWarpChanged()
            afterApply?.Invoke()
            StatusText = LocalizationService.T("Verzerrung angewendet")
            _hasChanges = True
            RefreshPreviewImmediately()
        End Sub

        ' ── Die Verzerrung des BILDES als Rezeptwert ────────────────────────────
        '
        ' Ein Knotenraster in Prozent des unbeschnittenen Bildes, genau die Form, die auch ein Objekt
        ' traegt. Jede der drei Arten wird darauf abgebildet, und genau deshalb laesst sich eine
        ' zweite Verzerrung auf eine erste setzen: die vorhandenen Stuetzpunkte wandern einfach durch
        ' das neue Feld. Mit drei getrennten Arten muesste man fuer jede Paarung ueberlegen, was ihre
        ' Verkettung ist.

        ''' <summary>Traegt das Bild IRGENDEINE Verzerrung - Raster ODER Perspektive?
        '''
        ''' Der Knopf zum Zuruecknehmen haengt daran und nicht am Raster allein. Die Perspektive war
        ''' sonst nur ueber den kleinen Knopf in IHRER Gruppe erreichbar, und die ist ausgeblendet,
        ''' sobald ein anderer Modus gewaehlt ist: eine gekippte Perspektive liess sich damit nicht
        ''' mehr zuruecknehmen, ohne erst wieder in den Perspektive-Modus zu wechseln.
        '''
        ''' Ein oberes Feld gibt es nicht mehr: eine bestaetigte Verzerrung ist ein Schritt, eine
        ''' offene steht in den Perspektivreglern.</summary>
        Public ReadOnly Property HasAnyImageWarp As Boolean
            Get
                Return HasPerspectiveChanges OrElse HasCommittedImageWarp() OrElse HasCommittedPerspective()
            End Get
        End Property

        Private Sub RaiseImageWarpChanged()
            Me.RaisePropertyChanged(NameOf(HasAnyImageWarp))
        End Sub

        ''' <summary>Uebersetzt einen im sichtbaren Bild bedienten Warp in den Quellraum eines
        ''' Objekts. Der Bild-Warp selbst bleibt im sichtbaren Raum, weil sein Pipeline-Schritt
        ''' erst NACH den schon bestaetigten Geometrieschritten ausgefuehrt wird.</summary>
        Private Function MapDisplayWarpToObjectSpace(mapping As Func(Of Double, Double, (X As Double, Y As Double))) As Func(Of Double, Double, (X As Double, Y As Double))
            Dim baseWidth = GetBaseWidth(), baseHeight = GetBaseHeight()
            Dim displaySize = GetAnnotationDisplayPixelSize()
            If mapping Is Nothing OrElse baseWidth <= 0 OrElse baseHeight <= 0 OrElse
               displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return mapping

            Dim geometry = BuildAppliedGeometryAdjustments()
            geometry.SourceWidthPixels = baseWidth
            geometry.SourceHeightPixels = baseHeight
            ' Bestaetigte Bild-Warps und Perspektiven stehen bereits in OwnWarp der Objekte. Die
            ' Lagekette darf sie hier nicht noch einmal anwenden.
            Dim objectGeometry = ImageProcessor.GeometryForAnnotations(geometry)
            Return Function(px As Double, py As Double) As (X As Double, Y As Double)
                       Dim shown As SkiaSharp.SKPoint
                       If Not ImageProcessor.TrySourcePointToGeometryOutput(px / 100.0 * baseWidth,
                                                                            py / 100.0 * baseHeight,
                                                                            baseWidth, baseHeight,
                                                                            objectGeometry, shown) Then Return (px, py)
                       Dim moved = mapping(shown.X / displaySize.Width * 100.0,
                                           shown.Y / displaySize.Height * 100.0)
                       Dim back As SkiaSharp.SKPoint
                       If Not ImageProcessor.TryGeometryOutputToSourcePoint(moved.X / 100.0 * displaySize.Width,
                                                                            moved.Y / 100.0 * displaySize.Height,
                                                                            baseWidth, baseHeight,
                                                                            objectGeometry, back) Then Return (px, py)
                       Return (back.X / baseWidth * 100.0, back.Y / baseHeight * 100.0)
                   End Function
        End Function

        ''' <summary>Aus einer Abbildung das Knotenfeld eines Rezeptschrittes machen.
        '''
        ''' Das Bedienraster darf grob bleiben, das Rezept nicht: Gitter, Linien und Verformen
        ''' werden ausnahmslos in ein 48x48-Auswertungsmesh uebernommen. So sieht derselbe Zug in
        ''' Vorschau, nach erneutem Oeffnen und beim JPEG-/PNG-Export gleich aus.
        '''
        ''' VERKETTET WIRD HIER NICHTS MEHR. Jede bestaetigte Verzerrung ist ein eigener Schritt der
        ''' Geometriekette und wird in ihrer Reihenfolge ausgefuehrt; sie zusammenzufalten wuerde die
        ''' Reihenfolge gegenueber Beschnitt, Drehung und Leinwand einebnen, und die ist sichtbar.</summary>
        Private Shared Function BuildWarpMesh(mapping As Func(Of Double, Double, (X As Double, Y As Double))) As ObjectWarp
            Dim steps = MaxImageWarpSteps
            Dim node((steps + 1) * (steps + 1) * 2 - 1) As Double
            For rowIdx = 0 To steps
                For colIdx = 0 To steps
                    Dim i = (rowIdx * (steps + 1) + colIdx) * 2
                    Dim n = mapping(colIdx / CDbl(steps) * 100.0, rowIdx / CDbl(steps) * 100.0)
                    node(i) = n.X
                    node(i + 1) = n.Y
                Next
            Next
            Return New ObjectWarp With {
                .Kind = "Gitter", .Columns = steps, .Rows = steps, .Nodes = node}
        End Function

        ''' <summary>Obergrenze der Rasterfeinheit im Rezept. Die Linienverzerrung wertet ihr Feld
        ''' mit 48 Schritten aus; so fein gespeichert kostet jede Ruecksuche (Overlays, Pinsel,
        ''' Retusche) das Vierfache, ohne dass man den Unterschied saehe.</summary>
        ' Die Obergrenze ist zugleich die Exportauflösung des gespeicherten Meshes. Sie darf nicht
        ' unter EnvelopeSteps liegen, sonst wird eine glatte Verformung beim Speichern wieder auf
        ' ein gröberes Gitter heruntergerechnet.
        Private Const MaxImageWarpSteps As Integer = 48

        ''' <summary>Die Verzerrung des Bildes wieder wegnehmen - ALLE VIER Arten, also auch die
        ''' Perspektive. Sie stehen im Rezept, es gibt also nichts zurueckzurechnen; anders als
        ''' frueher, als Raster und Linien in den Pixeln standen.</summary>
        Public Sub ResetImageWarp()
            If Not HasAnyImageWarp Then Return
            PushUndo(ResetHistoryLabel("Verzerren"))
            ResetCommittedImageWarp()
            ResetCommittedPerspective()
            RaiseImageWarpChanged()
            ' Eine stehende Vorschau MUSS mit weg. Sie liegt ueber dem Bild und zeigte sonst den
            ' alten Stand weiter, waehrend das Bild darunter ausgeblendet ist - "zurueckgenommen"
            ' stand in der Fusszeile, zu sehen war die Verzerrung.
            DisposeGridPreview()
            DisposeLinePreview()
            ' Die Perspektive gehoert dazu: fuer den Nutzer ist sie eine der vier Arten in derselben
            ' Gruppe, und ihr eigener Knopf verschwindet mit ihrer Gruppe, sobald ein anderer Modus
            ' gewaehlt ist.
            If HasPerspectiveChanges Then
                _perspectiveHorizontal = 0
                _perspectiveVertical = 0
                _perspectiveAspect = 0
                _perspectiveScale = 0
                Array.Clear(_perspectiveCorners, 0, _perspectiveCorners.Length)
                For Each n In {NameOf(PerspectiveHorizontal), NameOf(PerspectiveVertical),
                               NameOf(PerspectiveAspect), NameOf(PerspectiveScale)}
                    Me.RaisePropertyChanged(n)
                Next
                RaiseCornersChanged()
            End If
            _hasChanges = True
            StatusText = LocalizationService.T("Verzerrung zurückgenommen")
            RefreshPreviewImmediately()
        End Sub
    End Class

End Namespace
