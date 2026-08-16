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

    ''' <summary>Auswahl, Masken und Korrekturebenen des Editors - der Zustand rund um
    ''' <c>_selectionMask</c>, <c>_imageMasks</c> und die Maskenbestandteile, samt Auswahlwerkzeug,
    ''' Maskenpinsel, Verlaufsmasken und rotem Overlay.
    '''
    ''' Erste Scheibe der Dateiaufteilung (2026-08-04). Geschnitten ist entlang des ZUSTANDS, nicht
    ''' entlang der Funktion: ein Schnitt nach Features ergaebe Dateien, die alle dieselben Felder
    ''' anfassen, und gewonnen waere nur Scrollweg. Die Regeln, die dieser Bereich einhalten muss,
    ''' stehen in <c>Audits/MASKEN_EBENEN_AUSWAHL.md</c>.
    '''
    ''' Der Umzug war ein reiner TEXTumzug - keine Zeile wurde dabei geaendert. Wer hier aufraeumt,
    ''' tut das als eigenen Schritt, damit ein Fehler nicht zwischen tausend verschobenen Zeilen
    ''' verschwindet.</summary>
    Partial Public Class EditorViewModel

        ' Auswahlrechteck des Auswahlwerkzeugs - Prozent-vom-Bild wie beim Crop, aber als
        ' eigenständiges Rechteck (X/Y/Breite/Höhe) statt Rand-Abstände. HasActiveSelection steuert
        ' Sichtbarkeit des Overlays und Aktivierung von "Kopieren"/"Füllen" in der UI.
        Public Property HasActiveSelection As Boolean
            Get
                Return _hasActiveSelection
            End Get
            Set(value As Boolean)
                Dim before = _hasActiveSelection
                Me.RaiseAndSetIfChanged(_hasActiveSelection, value)
                ' Was an einer Markierung haengt, muss mitgemeldet werden. "Objekt entfernen" liest
                ' diesen Wert und blieb sonst dauerhaft grau: der Knopf fragte einmal beim Aufbau und
                ' erfuhr nie, dass inzwischen etwas markiert ist.
                If before <> _hasActiveSelection Then
                    RaiseCurrentTargetChanged()
                    Me.RaisePropertyChanged(NameOf(CanRemoveObject))
                    ' Und das Ablegen der Auswahlform, aus demselben Grund.
                    Me.RaisePropertyChanged(NameOf(CanCopySelectionShape))
                    ' Umkehren und Verwerfen haengen ebenfalls daran. Ohne diese Meldung blieben sie
                    ' nach dem ersten Pinselstrich grau, bis zufaellig ein Verlaufs-Ereignis feuerte,
                    ' und nach dem Aufheben blieben sie bedienbar.
                    RaiseMaskActionStateChanged()
                End If
            End Set
        End Property

        Public Property SelectionXPercent As Double
            Get
                Return _selectionXPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_selectionXPercent, value)
            End Set
        End Property

        Public Property SelectionYPercent As Double
            Get
                Return _selectionYPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_selectionYPercent, value)
            End Set
        End Property

        Public Property SelectionWidthPercent As Double
            Get
                Return _selectionWidthPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_selectionWidthPercent, value)
            End Set
        End Property

        Public Property SelectionHeightPercent As Double
            Get
                Return _selectionHeightPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_selectionHeightPercent, value)
            End Set
        End Property

        ''' Aktueller Auswahlmodus: "Move", "Rectangle", "Ellipse", "Lasso" oder "MagicWand". Steuert, wie
        ''' EditorView den Zeiger interpretiert (Auswahl verschieben, Rechteck aufziehen, Freihand zeichnen, klicken) und
        ''' welche Zusatzregler (Toleranz) sichtbar sind.
        Public Property SelectionMode As String
            Get
                Return _selectionMode
            End Get
            Set(value As String)
                Dim v = If(String.IsNullOrWhiteSpace(value), "Move", value)
                If _selectionMode = v Then Return
                InvalidatePendingRangeMask()
                Me.RaiseAndSetIfChanged(_selectionMode, v)
                Me.RaisePropertyChanged(NameOf(ShowMagicWandControls))
                Me.RaisePropertyChanged(NameOf(ShowMaskBrushControls))
                Me.RaisePropertyChanged(NameOf(IsMoveSelectionMode))
                Me.RaisePropertyChanged(NameOf(IsRectangleSelectionMode))
                Me.RaisePropertyChanged(NameOf(IsEllipseSelectionMode))
                Me.RaisePropertyChanged(NameOf(IsLassoSelectionMode))
                Me.RaisePropertyChanged(NameOf(IsMagicWandSelectionMode))
                Me.RaisePropertyChanged(NameOf(IsColorRangeSelectionMode))
                Me.RaisePropertyChanged(NameOf(IsLuminanceRangeSelectionMode))
                Me.RaisePropertyChanged(NameOf(IsBrushSelectionMode))
                Me.RaisePropertyChanged(NameOf(IsSubjectSelectionMode))
                ' Beim Wechsel in den Masken-Pinsel sinnvoll auf "Hinzufügen" vorbelegen (der erste Strich
                ' ohne aktive Auswahl läuft über ApplySelectionCandidate ohnehin als "New"). Das Overlay
                ' (rot vs. Ameisen) steuert NICHT mehr der Modus, sondern die Art der Auswahl
                ' (ActiveSelectionIsMask) - so bleibt eine Masken-Ebene auch außerhalb des Pinsels rot.
                If v = "Brush" AndAlso _selectionCombineMode = "New" Then SelectionCombineMode = "Add"
                If v = "LuminanceRange" Then
                    Dim ignored = RedrawLuminanceRangeMask(isMask:=False, captureUndo:=True)
                End If
            End Set
        End Property

        Public Sub SetSelectionMode(mode As String)
            SelectionMode = mode
        End Sub

        Public Property SelectionCombineMode As String
            Get
                Return _selectionCombineMode
            End Get
            Set(value As String)
                Dim v = NormalizeSelectionCombineMode(value)
                If _selectionCombineMode = v Then Return
                Me.RaiseAndSetIfChanged(_selectionCombineMode, v)
                Me.RaisePropertyChanged(NameOf(IsSelectionCombineNew))
                Me.RaisePropertyChanged(NameOf(IsSelectionCombineAdd))
                Me.RaisePropertyChanged(NameOf(IsSelectionCombineSubtract))
                Me.RaisePropertyChanged(NameOf(IsSelectionCombineIntersect))
            End Set
        End Property

        ''' <summary>Die Modusreihe des Masken- und des Auswahl-Werkzeugs.
        '''
        ''' „NEU" IM MASKEN-WERKZEUG LOEST DIE AKTUELLE MASKE AB. Der Knopf heisst „Neue Maske
        ''' beginnen", war aber wirkungslos, solange eine vorhandene Maske bearbeitet wurde: jeder
        ''' Strich schrieb weiter in genau diese Maske zurueck (_editingLayerMaskId) und jeder
        ''' Verlaufszug hing sich an sie an (_workingMaskId). Aus einer bestehenden Maske heraus liess
        ''' sich damit keine neue anfangen. Nach dem Abloesen legt der naechste Strich beziehungsweise
        ''' der naechste Zug eine neue Maske samt eigener Ebene an.
        '''
        ''' Im AUSWAHL-Werkzeug bleibt „Neu" was es war - dort ist es die Verknuepfung des naechsten
        ''' Zugs, und eine laufende Auswahl darf ein Moduswechsel nicht wegraeumen.</summary>
        Public Sub SetSelectionCombineMode(mode As String)
            Dim v = NormalizeSelectionCombineMode(mode)
            If v = "New" AndAlso _currentTool = EditorTool.Mask Then DeselectMaskTarget()
            SelectionCombineMode = v
        End Sub

        Public ReadOnly Property IsSelectionCombineNew As Boolean
            Get
                Return _selectionCombineMode = "New"
            End Get
        End Property

        Public ReadOnly Property IsSelectionCombineAdd As Boolean
            Get
                Return _selectionCombineMode = "Add"
            End Get
        End Property

        Public ReadOnly Property IsSelectionCombineSubtract As Boolean
            Get
                Return _selectionCombineMode = "Subtract"
            End Get
        End Property

        Public ReadOnly Property IsSelectionCombineIntersect As Boolean
            Get
                Return _selectionCombineMode = "Intersect"
            End Get
        End Property

        Public ReadOnly Property IsMoveSelectionMode As Boolean
            Get
                Return _selectionMode = "Move"
            End Get
        End Property

        Public ReadOnly Property IsRectangleSelectionMode As Boolean
            Get
                Return _selectionMode = "Rectangle"
            End Get
        End Property
        Public ReadOnly Property IsEllipseSelectionMode As Boolean
            Get
                Return _selectionMode = "Ellipse"
            End Get
        End Property
        Public ReadOnly Property IsLassoSelectionMode As Boolean
            Get
                Return _selectionMode = "Lasso"
            End Get
        End Property
        Public ReadOnly Property IsMagicWandSelectionMode As Boolean
            Get
                Return _selectionMode = "MagicWand"
            End Get
        End Property

        Public ReadOnly Property IsColorRangeSelectionMode As Boolean
            Get
                Return _selectionMode = "ColorRange"
            End Get
        End Property

        Public ReadOnly Property IsLuminanceRangeSelectionMode As Boolean
            Get
                Return _selectionMode = "LuminanceRange"
            End Get
        End Property

        Public ReadOnly Property IsBrushSelectionMode As Boolean
            Get
                Return _selectionMode = "Brush"
            End Get
        End Property

        ''' <summary>Objektauswahl als AUSWAHL statt als Maskenebene. Dasselbe Werkzeug wie im
        ''' Maskenpanel, nur ist das Ergebnis hier eine gewoehnliche Auswahl, die sich addieren,
        ''' abziehen und mit dem Pinsel nacharbeiten laesst.</summary>
        Public ReadOnly Property IsSubjectSelectionMode As Boolean
            Get
                Return _selectionMode = "Subject"
            End Get
        End Property

        Public ReadOnly Property ShowMagicWandControls As Boolean
            Get
                Return _selectionMode = "MagicWand"
            End Get
        End Property

        ''' <summary>True im Masken-Pinsel-Modus: blendet Pinselgröße ein und schaltet die Anzeige auf
        ''' das rote Overlay statt Laufameisen.</summary>
        Public ReadOnly Property ShowMaskBrushControls As Boolean
            Get
                Return _selectionMode = "Brush"
            End Get
        End Property

        Public ReadOnly Property SelectionShapeMode As String
            Get
                Return _selectionShapeMode
            End Get
        End Property

        Public ReadOnly Property SelectionShapePointsX As Double()
            Get
                Return _selectionShapePointsX
            End Get
        End Property

        Public ReadOnly Property SelectionShapePointsY As Double()
            Get
                Return _selectionShapePointsY
            End Get
        End Property

        Public ReadOnly Property SelectionMaskPreviewImage As Bitmap
            Get
                Return _selectionMaskPreviewImage
            End Get
        End Property

        Public ReadOnly Property HasSelectionMask As Boolean
            Get
                Return _selectionMask IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property SelectionMaskEdgePointsX As Double()
            Get
                Return _selectionMaskEdgePointsX
            End Get
        End Property

        Public ReadOnly Property SelectionMaskEdgePointsY As Double()
            Get
                Return _selectionMaskEdgePointsY
            End Get
        End Property

        Public Function IsPointInsideSelectionPercent(xPercent As Double, yPercent As Double) As Boolean
            If Not _hasActiveSelection Then Return False
            Dim selectionSize = GetAnnotationDisplayPixelSize()
            Dim bw = selectionSize.Width
            Dim bh = selectionSize.Height
            If bw <= 0 OrElse bh <= 0 Then Return False

            Dim imageX = CInt(Math.Round(bw * xPercent / 100.0))
            Dim imageY = CInt(Math.Round(bh * yPercent / 100.0))
            imageX = Math.Max(0, Math.Min(bw - 1, imageX))
            imageY = Math.Max(0, Math.Min(bh - 1, imageY))

            If _selectionMask IsNot Nothing Then
                Dim localX = imageX - _selectionMaskRect.Left
                Dim localY = imageY - _selectionMaskRect.Top
                If localX < 0 OrElse localY < 0 OrElse localX >= _selectionMask.Width OrElse localY >= _selectionMask.Height Then Return False
                If _selectionMaskBytes Is Nothing OrElse _selectionMaskBytesStride <= 0 Then Return False
                Return _selectionMaskBytes(localY * _selectionMaskBytesStride + localX) > 0
            End If

            Dim rectPx = SelectionRectPixels()
            Return imageX >= rectPx.Left AndAlso imageX < rectPx.Right AndAlso imageY >= rectPx.Top AndAlso imageY < rectPx.Bottom
        End Function

        ' ── Maskeneigenschaften: EIN Block fuer jede Maske ──────────────────────
        '
        ' Sie lagen verstreut: die weiche Kante allein im Pinsel-Zweig (also unsichtbar, sobald man
        ' mit Verlauf oder Objektauswahl arbeitete), die Dichte gar nicht - was ihr am naechsten kam,
        ' war die Deckkraft der Ebene, und die gibt es an einer Ebenenmaske am OBJEKT nicht. Deshalb
        ' hier eine Gruppe, die IMMER dieselbe ist, egal woher die Maske kommt.
        '
        ' Das ZIEL ist dasselbe wie bei der Bestandteilliste: CurrentMaskForComponents, also die
        ' Maske, an der gerade gearbeitet wird. Zwei Wege zur "aktuellen Maske" liefen unweigerlich
        ' auseinander.

        ''' <summary>Zeigt der Block ueberhaupt etwas? Sobald es eine Maske gibt oder eine Auswahl
        ''' laeuft - sonst beschriebe er nichts.</summary>
        Public ReadOnly Property ShowMaskProperties As Boolean
            Get
                Return _hasActiveSelection OrElse CurrentMaskForComponents() IsNot Nothing
            End Get
        End Property

        ''' <summary>Die Dichte braucht eine gespeicherte Maske. Eine frei gezogene Auswahl ist noch
        ''' keine: sie wird erst beim Anlegen einer Ebene zu einer, und bis dahin gaebe es nichts,
        ''' worauf der Regler schreiben koennte.</summary>
        Public ReadOnly Property ShowMaskDensity As Boolean
            Get
                Return CurrentMaskForComponents() IsNot Nothing
            End Get
        End Property

        ''' <summary>Die weiche Kante ist eine STRECKE in Bildpunkten und meint den Rand einer
        ''' gemalten Form. Ein VERLAUF hat keinen solchen Rand - bei ihm macht der Uebergang die
        ''' Weichheit, und der steht als eigener Regler in seinem Zweig. Ein Regler, der dort nichts
        ''' bewirkt, waere schlimmer als keiner.</summary>
        Public ReadOnly Property ShowMaskFeather As Boolean
            Get
                If _hasActiveSelection Then Return True
                Dim m = CurrentMaskForComponents()
                Return m IsNot Nothing AndAlso Not m.IsGradient
            End Get
        End Property

        ''' <summary>Wie stark die Maske ueberhaupt deckt, in Prozent. Sie liegt an der MASKE (siehe
        ''' ImageMask.Density) und wirkt damit auch dort, wo es keine Ebene mit Deckkraft gibt.</summary>
        Public Property MaskDensity As Double
            Get
                Dim m = CurrentMaskForComponents()
                Return If(m Is Nothing, 100.0, m.Density)
            End Get
            Set(value As Double)
                Dim m = CurrentMaskForComponents()
                If m Is Nothing Then Return
                Dim clamped = Math.Max(0, Math.Min(100, value))
                If Math.Abs(m.Density - clamped) < 0.0001 Then Return
                CaptureUndoState("MaskDensity")
                m.Density = clamped
                Me.RaisePropertyChanged(NameOf(MaskDensity))
                SchedulePreviewUpdate()
            End Set
        End Property

        ''' Farbtoleranz des Zauberstabs in Prozent (0..100).
        Public Property SelectionTolerance As Double
            Get
                Return _selectionTolerance
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_selectionTolerance, Math.Max(0, Math.Min(100, value)))
            End Set
        End Property

        ''' <summary>Weiche Kante der Auswahl in Bildpixeln. Wirkt auf Anpassungen innerhalb der Auswahl,
        ''' auf „Kopieren" und auf „Auswahl füllen" - die gespeicherte Maske bleibt pixelgenau, weich wird
        ''' erst das Ergebnis. Darum lässt sich der Wert jederzeit nachträglich ändern.</summary>
        Public Property SelectionFeather As Double
            Get
                Return _selectionFeather
            End Get
            Set(value As Double)
                Dim clamped = Math.Max(0, Math.Min(200, value))
                If Math.Abs(_selectionFeather - clamped) < 0.0001 Then Return
                CaptureUndoState("SelectionFeather")
                Me.RaiseAndSetIfChanged(_selectionFeather, clamped)
                If IsSelectionAdjustModeActive() Then
                    Dim layer = _maskedAdjustmentLayers.FirstOrDefault(Function(l) l IsNot Nothing AndAlso l.Id = _selectionAdjustLayerId)
                    Dim mask = If(layer Is Nothing, Nothing,
                        _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso m.Id = layer.MaskId))
                    If mask IsNot Nothing Then mask.FeatherPixels = CSng(clamped)
                End If
                ' Beim Bearbeiten einer EBENENMASKE gehoert die weiche Kante an genau diese Maske.
                ' Im Masken-Werkzeug laeuft kein Anpassungs-Modus, der Zweig darueber greift dort also
                ' nie - der Regler blieb ohne Wirkung auf die Ebene und war beim naechsten Oeffnen
                ' wieder auf dem alten Wert. Dieselbe Falle wie beim Umkehren.
                If _editingLayerMaskId <> "" Then
                    Dim editedMask = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                                    String.Equals(m.Id, _editingLayerMaskId, StringComparison.Ordinal))
                    If editedMask IsNot Nothing Then editedMask.FeatherPixels = CSng(clamped)
                ElseIf Not _hasActiveSelection Then
                    ' Weder Anpassungs-Modus noch geoeffnete Ebenenmaske noch laufende Auswahl: dann
                    ' meint der Regler die Maske, an der gearbeitet wird - dieselbe, die auch die
                    ' Bestandteilliste und die Dichte bedienen. Ohne diesen Zweig stand die weiche
                    ' Kante im Eigenschaften-Block, ohne irgendwo anzukommen.
                    Dim current = CurrentMaskForComponents()
                    If current IsNot Nothing Then current.FeatherPixels = CSng(clamped)
                End If
                SchedulePreviewUpdate()
            End Set
        End Property

        Private Sub SetSelectionMaskData(mask As SKBitmap, rectPx As SKRectI)
            If _selectionMask IsNot Nothing AndAlso Not Object.ReferenceEquals(_selectionMask, mask) Then _selectionMask.Dispose()
            _selectionMask = mask
            _selectionMaskRect = rectPx
            _selectionMaskBytesStride = If(mask Is Nothing, 0, mask.RowBytes)
            _selectionMaskBytes = If(mask Is Nothing, Nothing, New Byte(_selectionMaskBytesStride * mask.Height - 1) {})
            If mask IsNot Nothing Then Marshal.Copy(mask.GetPixels(), _selectionMaskBytes, 0, _selectionMaskBytes.Length)
            _selectionMaskBase64 = EncodeMaskBitmapToBase64(mask)
            RefreshSelectionMaskEdgePoints()
            Me.RaisePropertyChanged(NameOf(HasSelectionMask))
            ' DER Engpass: hier laeuft JEDE Aenderung der committeten Auswahlmaske durch - Zauberstab,
            ' Pinselstrich, Umkehren, Objektauswahl, Wiederherstellen. Die Ameisen entstehen aus den
            ' Maskenraendern, die eine Zeile darueber neu gerechnet werden; das rote Overlay dagegen
            ' ist ein eigenes Bild und blieb auf dem alten Stand, wenn ein Aufrufer es vergass.
            ' Dreimal ist genau das passiert, jedesmal an einer anderen Stelle. Deshalb steht es
            ' jetzt HIER und nicht bei den Aufrufern: wer die Maske aendert, kann das Overlay nicht
            ' mehr vergessen.
            If _activeSelectionIsMask Then PublishMaskBrushOverlay()
        End Sub

        Private Sub ClearSelectionMask()
            _selectionMask?.Dispose()
            _selectionMask = Nothing
            _selectionMaskRect = SKRectI.Empty
            _selectionMaskBytes = Nothing
            _selectionMaskBytesStride = 0
            _selectionMaskBase64 = ""
            ' Zurück auf harte Kante: der Masken-Pinsel-Commit setzt das Flag danach wieder, wenn nötig.
            _selectionMaskSoftBaked = False
            _selectionMaskEdgePointsX = Nothing
            _selectionMaskEdgePointsY = Nothing
            Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsX))
            Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsY))
            SetSelectionMaskPreviewImage(Nothing)
            Me.RaisePropertyChanged(NameOf(HasSelectionMask))
        End Sub

        Private Sub SetSelectionMaskPreviewImage(image As Bitmap)
            Dim oldImage = _selectionMaskPreviewImage
            _selectionMaskPreviewImage = image
            If oldImage IsNot Nothing AndAlso Not Object.ReferenceEquals(oldImage, image) Then oldImage.Dispose()
            Me.RaisePropertyChanged(NameOf(SelectionMaskPreviewImage))
        End Sub

        Private Sub RefreshSelectionMaskEdgePoints()
            SetSelectionMaskPreviewImage(Nothing)
            If _selectionMask Is Nothing OrElse _selectionMaskBytes Is Nothing OrElse _selectionMask.Width <= 0 OrElse _selectionMask.Height <= 0 Then
                _selectionMaskEdgePointsX = Nothing
                _selectionMaskEdgePointsY = Nothing
                Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsX))
                Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsY))
                Return
            End If

            ' Randpixel sampeln (die MASKE bleibt davon unberührt - das hier ist nur die Ameisenlinie).
            ' Früher landete zunächst jeder einzelne Randpixel in zwei Double-Listen und wurde erst
            ' anschließend ausgedünnt. Bei großen/zerklüfteten Masken kostete allein diese Anzeige
            ' mehrere zig MB. Deterministisches Reservoir-Sampling hält Speicher UND Durchlaufzahl
            ' konstant begrenzt und verteilt die Anzeigepunkte gleichmäßig über den gesamten Rand.
            Dim w = _selectionMask.Width, h = _selectionMask.Height
            Dim stride = _selectionMaskBytesStride
            Dim bytes = _selectionMaskBytes
            Const MaxEdgePoints As Integer = 4000
            Dim edgeXs As New List(Of Double)(MaxEdgePoints)
            Dim edgeYs As New List(Of Double)(MaxEdgePoints)
            Dim edgeCount As Integer = 0
            Dim sampler As New Random(872341)

            For y = 0 To h - 1
                Dim row = y * stride
                Dim up = row - stride
                Dim down = row + stride
                For x = 0 To w - 1
                    If bytes(row + x) = 0 Then Continue For
                    Dim isEdge = x = 0 OrElse y = 0 OrElse x = w - 1 OrElse y = h - 1 OrElse
                                 bytes(row + x - 1) = 0 OrElse
                                 bytes(row + x + 1) = 0 OrElse
                                 bytes(up + x) = 0 OrElse
                                 bytes(down + x) = 0
                    If Not isEdge Then Continue For
                    edgeCount += 1
                    If edgeCount <= MaxEdgePoints Then
                        edgeXs.Add((x + 0.5) * 100.0 / w)
                        edgeYs.Add((y + 0.5) * 100.0 / h)
                    Else
                        Dim replaceIndex = sampler.Next(edgeCount)
                        If replaceIndex < MaxEdgePoints Then
                            edgeXs(replaceIndex) = (x + 0.5) * 100.0 / w
                            edgeYs(replaceIndex) = (y + 0.5) * 100.0 / h
                        End If
                    End If
                Next
            Next

            _selectionMaskEdgePointsX = edgeXs.ToArray()
            _selectionMaskEdgePointsY = edgeYs.ToArray()
            Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsX))
            Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsY))
        End Sub

        Private Sub SetSelectionShape(mode As String, xsPercent As Double(), ysPercent As Double())
            _selectionShapeMode = If(String.IsNullOrWhiteSpace(mode), "Rectangle", mode)
            _selectionShapePointsX = xsPercent
            _selectionShapePointsY = ysPercent
            Me.RaisePropertyChanged(NameOf(SelectionShapeMode))
            Me.RaisePropertyChanged(NameOf(SelectionShapePointsX))
            Me.RaisePropertyChanged(NameOf(SelectionShapePointsY))
        End Sub

        Private Shared Function NormalizeSelectionCombineMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Add", "Subtract", "Intersect"
                    Return value.Trim()
                Case Else
                    Return "New"
            End Select
        End Function

        ' Auswahlrechteck aus Display-Prozentwerten in Pixel des gerenderten Bildraums umrechnen
        ' (für Maskenerzeugung/-extraktion). ProcessBitmap/Zauberstab/Kopieren arbeiten nach der
        ' Bildgeometrie, also in genau diesem Raum.
        Private Function SelectionRectPixels() As SKRectI
            Dim selectionSize = GetAnnotationDisplayPixelSize()
            Dim bw = selectionSize.Width, bh = selectionSize.Height
            Dim left = CInt(Math.Round(bw * _selectionXPercent / 100.0))
            Dim top = CInt(Math.Round(bh * _selectionYPercent / 100.0))
            Dim right = CInt(Math.Round(bw * (_selectionXPercent + _selectionWidthPercent) / 100.0))
            Dim bottom = CInt(Math.Round(bh * (_selectionYPercent + _selectionHeightPercent) / 100.0))
            Return New SKRectI(Math.Max(0, left), Math.Max(0, top), Math.Min(bw, right), Math.Min(bh, bottom))
        End Function

        ''' <summary>Übernimmt das Auswahlrechteck aus Bildpixeln - OHNE Mindestgröße. Das Rechteck ist der
        ''' Bezugsrahmen der Maske: Overlay-Ränder, Ausschneiden und Füllen rechnen alle relativ dazu. Eine
        ''' Untergrenze (früher 0,5 % der Bildbreite = 30 px bei 6000 px) hätte bei kleinen Zauberstab-/
        ''' Lasso-Auswahlen das Rechteck größer gemacht als die Maske - die Ameisenlinie säße dann daneben
        ''' und ein kopierter Ausschnitt käme verzerrt heraus. Die Aufrufer verwerfen leere Rechtecke selbst.</summary>
        Private Sub SetSelectionBoundsFromPixels(rectPx As SKRectI)
            Dim selectionSize = GetAnnotationDisplayPixelSize()
            Dim bw = selectionSize.Width, bh = selectionSize.Height
            If bw <= 0 OrElse bh <= 0 Then Return
            _selectionXPercent = Math.Max(0, rectPx.Left * 100.0 / bw)
            _selectionYPercent = Math.Max(0, rectPx.Top * 100.0 / bh)
            _selectionWidthPercent = rectPx.Width * 100.0 / bw
            _selectionHeightPercent = rectPx.Height * 100.0 / bh
            Me.RaisePropertyChanged(NameOf(SelectionXPercent))
            Me.RaisePropertyChanged(NameOf(SelectionYPercent))
            Me.RaisePropertyChanged(NameOf(SelectionWidthPercent))
            Me.RaisePropertyChanged(NameOf(SelectionHeightPercent))
        End Sub

        Private Sub SetSelectionBoundsFromPercent(xPercent As Double, yPercent As Double, widthPercent As Double, heightPercent As Double)
            _selectionXPercent = Math.Max(0, xPercent)
            _selectionYPercent = Math.Max(0, yPercent)
            _selectionWidthPercent = Math.Max(0.5, widthPercent)
            _selectionHeightPercent = Math.Max(0.5, heightPercent)
            Me.RaisePropertyChanged(NameOf(SelectionXPercent))
            Me.RaisePropertyChanged(NameOf(SelectionYPercent))
            Me.RaisePropertyChanged(NameOf(SelectionWidthPercent))
            Me.RaisePropertyChanged(NameOf(SelectionHeightPercent))
        End Sub

        ''' Wird von EditorView beim Loslassen der Maus nach dem Aufziehen eines Auswahlrechtecks aufgerufen.
        Public Sub SetSelectionRect(xPercent As Double, yPercent As Double, widthPercent As Double, heightPercent As Double,
                                    Optional captureUndo As Boolean = True)
            InvalidateSelectionLayerLink()   ' neue Auswahl → eigene Ebene (siehe PromoteActiveSelectionToLayer)
            Dim selectionSize = GetAnnotationDisplayPixelSize()
            Dim bw = selectionSize.Width, bh = selectionSize.Height
            If bw <= 0 OrElse bh <= 0 Then Return
            Dim rectPx = New SKRectI(
                Math.Max(0, CInt(Math.Round(bw * xPercent / 100.0))),
                Math.Max(0, CInt(Math.Round(bh * yPercent / 100.0))),
                Math.Min(bw, CInt(Math.Round(bw * (xPercent + widthPercent) / 100.0))),
                Math.Min(bh, CInt(Math.Round(bh * (yPercent + heightPercent) / 100.0))))
            If rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return
            If captureUndo Then PushUndo()

            If _selectionCombineMode = "New" OrElse Not _hasActiveSelection Then
                _editingLayerMaskId = ""   ' neue Auswahl ersetzt eine evtl. geladene Ebenen-Maske
                ClearSelectionMask()
                SetSelectionBoundsFromPixels(rectPx)
                SetSelectionShape("Rectangle", Nothing, Nothing)
                HasActiveSelection = True
                ' Ein gezogenes Rechteck ist eine AUSWAHL, keine Maske. Ohne dieses Zurücksetzen behielt
                ' es nach einer Masken-Bearbeitung die Art "Maske" und wurde als Masken-Ebene promotet.
                SetActiveSelectionIsMask(False)
                Return
            End If

            Using candidate = CreateSolidMask(rectPx.Width, rectPx.Height)
                ApplySelectionCandidate(candidate, rectPx, "Rectangle", Nothing, Nothing)
            End Using
        End Sub

        ''' Ellipse-Auswahl: Rechteck wie beim Rechteck-Modus, zusätzlich eine eingepasste Ellipsen-Maske.
        Public Sub SetSelectionEllipse(xPercent As Double, yPercent As Double, widthPercent As Double, heightPercent As Double,
                                       Optional captureUndo As Boolean = True)
            InvalidateSelectionLayerLink()   ' neue Auswahl → eigene Ebene (siehe PromoteActiveSelectionToLayer)
            Dim selectionSize = GetAnnotationDisplayPixelSize()
            Dim bw = selectionSize.Width, bh = selectionSize.Height
            If bw <= 0 OrElse bh <= 0 Then Return
            Dim rawLeft = CInt(Math.Round(bw * xPercent / 100.0))
            Dim rawTop = CInt(Math.Round(bh * yPercent / 100.0))
            Dim rawRight = CInt(Math.Round(bw * (xPercent + widthPercent) / 100.0))
            Dim rawBottom = CInt(Math.Round(bh * (yPercent + heightPercent) / 100.0))
            Dim rectPx = New SKRectI(
                Math.Max(0, rawLeft),
                Math.Max(0, rawTop),
                Math.Min(bw, rawRight),
                Math.Min(bh, rawBottom))
            If rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return
            If captureUndo Then PushUndo()
            Dim localOval = New SKRect(rawLeft - rectPx.Left,
                                       rawTop - rectPx.Top,
                                       rawRight - rectPx.Left,
                                       rawBottom - rectPx.Top)
            Using mask = ImageProcessor.BuildEllipseMask(rectPx.Width, rectPx.Height, localOval)
                If mask IsNot Nothing Then ApplySelectionCandidate(mask, rectPx, "Ellipse", Nothing, Nothing)
            End Using
        End Sub

        ''' Lasso-Auswahl aus Freihand-Punkten (Prozentkoordinaten). Bounding-Box wird zum Auswahlrechteck,
        ''' das Polygon zur Maske.
        Public Sub SetSelectionLasso(xsPercent As Double(), ysPercent As Double(), Optional captureUndo As Boolean = True)
            If xsPercent Is Nothing OrElse ysPercent Is Nothing OrElse xsPercent.Length < 3 OrElse xsPercent.Length <> ysPercent.Length Then Return
            Dim minX = xsPercent.Min(), maxX = xsPercent.Max()
            Dim minY = ysPercent.Min(), maxY = ysPercent.Max()
            If (maxX - minX) < 0.5 OrElse (maxY - minY) < 0.5 Then Return
            Dim selectionSize = GetAnnotationDisplayPixelSize()
            Dim bw = selectionSize.Width, bh = selectionSize.Height
            If bw <= 0 OrElse bh <= 0 Then Return
            Dim rectPx = New SKRectI(
                Math.Max(0, CInt(Math.Round(bw * minX / 100.0))),
                Math.Max(0, CInt(Math.Round(bh * minY / 100.0))),
                Math.Min(bw, CInt(Math.Round(bw * maxX / 100.0))),
                Math.Min(bh, CInt(Math.Round(bh * maxY / 100.0))))
            If rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return
            Dim localX(xsPercent.Length - 1) As Single
            Dim localY(ysPercent.Length - 1) As Single
            For i = 0 To xsPercent.Length - 1
                localX(i) = CSng(bw * xsPercent(i) / 100.0 - rectPx.Left)
                localY(i) = CSng(bh * ysPercent(i) / 100.0 - rectPx.Top)
            Next
            If captureUndo Then PushUndo()
            Using mask = ImageProcessor.BuildPolygonMask(localX, localY, rectPx.Width, rectPx.Height)
                If mask IsNot Nothing Then ApplySelectionCandidate(mask, rectPx, "Lasso", xsPercent.ToArray(), ysPercent.ToArray())
            End Using
        End Sub

        ' ── Masken-Pinsel ────────────────────────────────────────────────────────
        ' Malt eine weiche Maske ins Auswahl-System (rotes Quick-Mask-Overlay statt Laufameisen). Radius
        ' und Randweichheit liegen im DISPLAY-Bildraum (= Maskenraum), genau wie BrushSize/SelectionFeather.
        Private Const MaskBrushOverlayMaxEdge As Integer = 1600

        Private Function MaskBrushRadiusDisplay() As Double
            Return Math.Max(0.5, BrushSize / 2.0)
        End Function

        Private Function MaskBrushDisplayPoints(xsPercent As Double(), ysPercent As Double(), bw As Integer, bh As Integer) As List(Of SKPoint)
            Dim pts As New List(Of SKPoint)(xsPercent.Length)
            For i = 0 To xsPercent.Length - 1
                pts.Add(New SKPoint(CSng(bw * xsPercent(i) / 100.0), CSng(bh * ysPercent(i) / 100.0)))
            Next
            Return pts
        End Function

        ''' <summary>Die rot eingefaerbte COMMITTETE Maske in Overlay-Aufloesung, waehrend ein Strich
        ''' laeuft. Sie aendert sich innerhalb eines Strichs nicht - vorher wurde sie trotzdem bei
        ''' JEDER Mausbewegung neu skaliert (hochwertige Abtastung ueber die ganze Flaeche), und das
        ''' war der Hauptposten der CPU-Last beim Malen. Jetzt einmal je Strich, danach nur noch
        ''' kopieren und den Strich daraufzeichnen.
        ''' Lebensdauer bewusst NUR der Strich: solange er laeuft, kann die committete Maske sich
        ''' nicht aendern, und die Frage nach einer Ungueltigkeitsregel stellt sich gar nicht.</summary>
        Private _maskOverlayBasis As SKBitmap
        Private _maskOverlayBasisBreite As Integer
        Private _maskOverlayBasisHoehe As Integer

        Private Sub DiscardMaskOverlayBase()
            _maskOverlayBasis?.Dispose()
            _maskOverlayBasis = Nothing
            _maskOverlayBasisBreite = 0
            _maskOverlayBasisHoehe = 0
        End Sub

        ''' <summary>Baut das rote Quick-Mask-Overlay: die aktuelle Auswahlmaske rot getönt (Alpha ∝ Deckung)
        ''' auf eine gedeckelte Overlay-Auflösung heruntergerechnet, plus optional den laufenden Strich als
        ''' Live-Vorschau (Subtrahieren stanzt via DstOut). Rückgabe deckt das ganze Anzeigebild ab und wird
        ''' vom View auf das Bildrechteck gestreckt. Nothing, wenn nichts zu zeigen ist.</summary>
        Private Function BuildSelectionRedOverlayBitmap(livePts As List(Of SKPoint), eraseMode As Boolean) As Bitmap
            Dim selectionSize = GetAnnotationDisplayPixelSize()
            Dim bw = selectionSize.Width, bh = selectionSize.Height
            If bw <= 0 OrElse bh <= 0 Then Return Nothing
            If _selectionMask Is Nothing AndAlso (livePts Is Nothing OrElse livePts.Count = 0) Then Return Nothing

            Dim ovScale = Math.Min(1.0, MaskBrushOverlayMaxEdge / CDbl(Math.Max(bw, bh)))
            Dim ow = Math.Max(1, CInt(Math.Round(bw * ovScale)))
            Dim oh = Math.Max(1, CInt(Math.Round(bh * ovScale)))
            Dim redColor = New SKColor(255, 0, 0, 128)

            ' Trägt die Ebene dieser Auswahl eine Füllung, zeigt das rote Overlay die tatsächliche
            ' DECKUNG (Maske × Füll-Luminanz) statt nur der Maskenform - so sieht man den Deckungsverlauf,
            ' mit dem die Anpassung abgestuft wird.
            Dim fillLayer = ActiveFillLayerForSelection()
            Dim maskForOverlay = _selectionMask
            Dim ownsMaskForOverlay = False
            If _selectionMask IsNot Nothing AndAlso fillLayer IsNot Nothing Then
                Dim graded = ImageProcessor.RenderMaskFilledWithGradient(_selectionMask, fillLayer.FillColor,
                    fillLayer.FillKind, fillLayer.FillColor2, CSng(fillLayer.FillAngle), fillLayer.FillInverted)
                If graded IsNot Nothing Then
                    maskForOverlay = graded
                    ownsMaskForOverlay = True
                End If
            End If

            ' Waehrend eines Strichs die eingefaerbte Maske einmal bauen und danach nur kopieren.
            Dim liveStroke = livePts IsNot Nothing AndAlso livePts.Count > 0
            If Not liveStroke Then DiscardMaskOverlayBase()
            Dim basisTaugt = liveStroke AndAlso _maskOverlayBasis IsNot Nothing AndAlso
                             _maskOverlayBasisBreite = ow AndAlso _maskOverlayBasisHoehe = oh

            Try
            Using overlay = New SKBitmap(ow, oh, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(overlay)
                    canvas.Clear(SKColors.Transparent)
                    If basisTaugt Then
                        ' Fertige Basis uebernehmen - das teure Neuskalieren der Maske entfaellt.
                        canvas.DrawBitmap(_maskOverlayBasis, 0, 0)
                    End If
                    ' Committete Maske rot einfärben: erst Rot in der Maskenregion auslegen, dann per DstIn
                    ' auf die Maskendeckung beschränken. Rein alphabasiert - unabhängig davon, wie eine
                    ' Alpha8-Quelle beim direkten Zeichnen eingefärbt würde.
                    If maskForOverlay IsNot Nothing AndAlso Not basisTaugt Then
                        Dim src = New SKRect(0, 0, maskForOverlay.Width, maskForOverlay.Height)
                        Dim dst = New SKRect(CSng(_selectionMaskRect.Left * ovScale), CSng(_selectionMaskRect.Top * ovScale),
                                             CSng(_selectionMaskRect.Right * ovScale), CSng(_selectionMaskRect.Bottom * ovScale))
                        Using redPaint = New SKPaint With {.Color = redColor, .Style = SKPaintStyle.Fill}
                            canvas.DrawRect(dst, redPaint)
                        End Using
                        ' KEINE Kantenglättung: das rote Rechteck oben wird ohne AA gerastert. Zeichnete man
                        ' die DstIn-Maske MIT AA, deckten beide nicht exakt dieselben Pixel ab - am Rand des
                        ' Maskenrechtecks blieb eine dünne Reihe voll roter Pixel stehen ("ganz feiner roter
                        ' Rahmen"). Die weiche Kante der Maske kommt ohnehin aus
                        ' ihren Alpha-Werten (plus SamplingHigh), nicht aus der Kantenglättung des Rechtecks.
                        Using maskPaint = New SKPaint With {.BlendMode = SKBlendMode.DstIn, .IsAntialias = False}
                            ImageProcessor.DrawBitmapSampled(canvas, maskForOverlay, src, dst, ImageProcessor.SamplingHigh, maskPaint)
                        End Using
                    End If
                    ' Basis fuer die naechsten Bewegungen desselben Strichs festhalten - VOR dem
                    ' Zeichnen des Strichs, sonst waere er beim naechsten Mal doppelt drin.
                    If liveStroke AndAlso Not basisTaugt Then
                        DiscardMaskOverlayBase()
                        _maskOverlayBasis = overlay.Copy()
                        _maskOverlayBasisBreite = ow
                        _maskOverlayBasisHoehe = oh
                    End If
                    ' Laufender Strich (Live): auf Overlay-Auflösung skaliert.
                    If liveStroke Then
                        Dim scaled As New List(Of SKPoint)(livePts.Count)
                        For Each p In livePts
                            scaled.Add(New SKPoint(CSng(p.X * ovScale), CSng(p.Y * ovScale)))
                        Next
                        Dim r = CSng(Math.Max(0.5, MaskBrushRadiusDisplay() * ovScale))
                        Dim soft = CSng(MaskBrushStrokeSoftness() * ovScale)
                        ImageProcessor.DrawSoftMaskStroke(canvas, scaled, r, soft, redColor, eraseMode)
                    End If
                End Using
                Return ImageProcessor.ToAvaloniaBitmap(overlay)
            End Using
            Finally
                If ownsMaskForOverlay Then maskForOverlay.Dispose()
            End Try
        End Function

        ''' <summary>Die Korrekturebene dieser Auswahl, sofern sie eine deklarative Füllung trägt - sonst
        ''' Nothing. Quelle für die Deckungs-Darstellung im roten Overlay.</summary>
        Private Function ActiveFillLayerForSelection() As MaskedAdjustmentLayer
            Dim l As MaskedAdjustmentLayer = Nothing
            If _selectionPromotedLayerId <> "" Then
                l = _maskedAdjustmentLayers.FirstOrDefault(Function(x) x IsNot Nothing AndAlso x.Id = _selectionPromotedLayerId)
            End If
            If l Is Nothing Then l = LayerForEditedMask()
            If l IsNot Nothing AndAlso l.HasFill() Then Return l
            Return Nothing
        End Function

        ''' Rotes Overlay aus der committeten Maske neu bauen und veröffentlichen (nach jedem Strich-Commit).
        Private Sub PublishSelectionRedOverlay()
            If _isMaskGrayscaleView Then
                SetSelectionMaskPreviewImage(BuildMaskGrayscaleBitmap())
                Return
            End If
            SetSelectionMaskPreviewImage(BuildSelectionRedOverlayBitmap(Nothing, False))
        End Sub

        Private _isMaskGrayscaleView As Boolean

        ''' <summary>NUR DIE MASKE ZEIGEN, als Graustufenbild: weiss deckt, schwarz laesst frei.
        '''
        ''' Der Handgriff, der bisher ganz fehlte. Am roten Schleier ist nicht zu sehen, ob eine
        ''' Stelle zu 90 oder zu 100 Prozent gedeckt ist - im Graustufenblick schon, und genau daran
        ''' entscheidet sich, ob eine Maske sitzt. Ein reiner ANZEIGEzustand: er wird nirgends
        ''' gespeichert und aendert an der Maske nichts.</summary>
        Public Property IsMaskGrayscaleView As Boolean
            Get
                Return _isMaskGrayscaleView
            End Get
            Set(value As Boolean)
                If _isMaskGrayscaleView = value Then Return
                _isMaskGrayscaleView = value
                Me.RaisePropertyChanged(NameOf(IsMaskGrayscaleView))
                ' Beim Verlauf haengt das rote Overlay an einem anderen Weg als bei einer gemalten
                ' Maske - beide muessen den Blick anbieten, sonst ist er je nach Herkunft der Maske
                ' da oder nicht.
                Dim gradient = CurrentMaskForComponents()
                If gradient IsNot Nothing AndAlso gradient.IsGradient Then
                    PublishGradientOverlay(gradient)
                Else
                    PublishSelectionRedOverlay()
                End If
            End Set
        End Property

        ''' <summary>Die Maske als Graustufenbild ueber dem ganzen Bild: schwarzer Grund, die Deckung
        ''' in Weiss darauf. Deckend, damit vom Foto nichts durchscheint - genau das ist der Zweck.
        '''
        ''' Gebaut wird es aus derselben Arbeitskopie wie das rote Overlay (_selectionMask im
        ''' Anzeigeraum). Ein zweiter Weg zur Maskenform liefe unweigerlich auseinander.</summary>
        Private Function BuildMaskGrayscaleBitmap() As Bitmap
            Dim selectionSize = GetAnnotationDisplayPixelSize()
            Dim bw = selectionSize.Width, bh = selectionSize.Height
            If bw <= 0 OrElse bh <= 0 Then Return Nothing

            Dim ovScale = Math.Min(1.0, MaskBrushOverlayMaxEdge / CDbl(Math.Max(bw, bh)))
            Dim ow = Math.Max(1, CInt(Math.Round(bw * ovScale)))
            Dim oh = Math.Max(1, CInt(Math.Round(bh * ovScale)))

            ' Woher die Form kommt: eine gemalte Maske liegt schon als Arbeitskopie im Anzeigeraum
            ' (_selectionMask, mit allem was gerade gemalt wurde). Ein VERLAUF hat keine solche
            ' Kopie - sein rotes Overlay entsteht aus der Geometrie. Fuer ihn wird dieselbe Form
            ' geholt, ueber die auch eine Ebenenmaske zum Bearbeiten in den Anzeigeraum kommt.
            Dim shape = _selectionMask
            Dim shapeRect = _selectionMaskRect
            Dim ownsShape = False
            If shape Is Nothing Then
                Dim current = CurrentMaskForComponents()
                If current IsNot Nothing Then
                    Dim rect As SKRectI
                    shape = ImageProcessor.BuildSelectionMaskFromLayerMask(current, BuildAdjustmentsFromFields(), rect)
                    shapeRect = rect
                    ownsShape = shape IsNot Nothing
                End If
            End If

            Try
            Using overlay = New SKBitmap(ow, oh, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(overlay)
                    ' Schwarz ueberall: was die Maske nicht deckt, ist frei - und frei heisst hier
                    ' schwarz, nicht durchsichtig.
                    canvas.Clear(SKColors.Black)
                    If shape IsNot Nothing Then
                        Dim src = New SKRect(0, 0, shape.Width, shape.Height)
                        Dim dst = New SKRect(CSng(shapeRect.Left * ovScale), CSng(shapeRect.Top * ovScale),
                                             CSng(shapeRect.Right * ovScale), CSng(shapeRect.Bottom * ovScale))
                        ' Das Weiss entsteht in einer eigenen Schicht: DstIn wirkt auf die ganze
                        ' Schicht, und ohne sie fraesse es den schwarzen Grund gleich mit weg.
                        canvas.SaveLayer()
                        Using whitePaint = New SKPaint With {.Color = SKColors.White, .Style = SKPaintStyle.Fill}
                            canvas.DrawRect(dst, whitePaint)
                        End Using
                        Using maskPaint = New SKPaint With {.BlendMode = SKBlendMode.DstIn, .IsAntialias = False}
                            ImageProcessor.DrawBitmapSampled(canvas, shape, src, dst, ImageProcessor.SamplingHigh, maskPaint)
                        End Using
                        canvas.Restore()
                    End If
                End Using
                Return ImageProcessor.ToAvaloniaBitmap(overlay)
            End Using
            Finally
                If ownsShape Then shape.Dispose()
            End Try
        End Function

        ''' <summary>Art der aktiven Auswahl: True = MASKE (rotes Overlay), False = AUSWAHL (Laufameisen).
        ''' Von der View gelesen, um die richtige Darstellung zu wählen - unabhängig vom Werkzeug.</summary>
        Public ReadOnly Property ActiveSelectionIsMask As Boolean
            Get
                Return _activeSelectionIsMask
            End Get
        End Property

        ''' <summary>Setzt die Art der aktiven Auswahl und baut das rote Overlay bzw. wechselt auf
        ''' Laufameisen. Das SelectionMaskPreviewImage-PropertyChanged löst danach das Layout/Overlay aus.</summary>
        Private Sub SetActiveSelectionIsMask(value As Boolean)
            If _activeSelectionIsMask = value Then Return
            _activeSelectionIsMask = value
            Me.RaisePropertyChanged(NameOf(ActiveSelectionIsMask))
            Me.RaisePropertyChanged(NameOf(CanConvertKind))
            Me.RaisePropertyChanged(NameOf(ConvertKindText))
            Me.RaisePropertyChanged(NameOf(ConvertKindHint))
            RaiseCurrentTargetChanged()
            If value Then PublishSelectionRedOverlay() Else SetSelectionMaskPreviewImage(Nothing)
        End Sub

        ''' Live-Vorschau während des Strichs: committete Maske + laufender Strich in Rot.
        Public Sub RefreshMaskBrushLivePreview(xsPercent As Double(), ysPercent As Double())
            If xsPercent Is Nothing OrElse ysPercent Is Nothing OrElse xsPercent.Length = 0 OrElse xsPercent.Length <> ysPercent.Length Then
                PublishMaskBrushOverlay()
                Return
            End If
            Dim selectionSize = GetAnnotationDisplayPixelSize()
            Dim bw = selectionSize.Width, bh = selectionSize.Height
            If bw <= 0 OrElse bh <= 0 Then Return
            ' Derselbe Stempel wird auch vom Auswahlpinsel benutzt. Nur eine bereits geöffnete
            ' Ebenenmaske bzw. das Maskenwerkzeug ist eine Maske; eine freie Auswahl darf durch die
            ' Live-Vorschau nicht zur Maske werden, sonst bleiben nach dem Loslassen das rote
            ' Overlay und die Masken-Semantik statt der Laufameisen übrig.
            Dim paintsMask = CurrentTool <> EditorTool.Selection OrElse _activeSelectionIsMask
            If Not paintsMask Then
                SetActiveSelectionIsMask(False)
                SetSelectionMaskPreviewImage(Nothing)
                Return
            End If
            SetActiveSelectionIsMask(True)   ' laufender Masken-Strich = rotes Overlay
            ' Beim Nachbessern eines VERLAUFS gibt es keine aktive Auswahl - der Abzieh-Modus muss
            ' trotzdem als Radiergummi angezeigt werden, sonst sieht der Strich aus, als füge er hinzu.
            Dim gradient = SelectedGradientMask
            Dim eraseMode = (_hasActiveSelection OrElse gradient IsNot Nothing) AndAlso
                            _selectionCombineMode = "Subtract"
            Dim pts = MaskBrushDisplayPoints(xsPercent, ysPercent, bw, bh)
            ' Der Verlauf zeichnet sein Overlay selbst (Geometrie + Pinselkorrektur); der Strich kommt
            ' obendrauf. Ueber BuildSelectionRedOverlayBitmap ginge nur der Strich allein durch.
            PublishMaskBrushOverlay(pts, eraseMode)
        End Sub

        ''' <summary>DIE Stelle, an der das rote Overlay des Maskenpinsels entsteht - egal ob das
        ''' Ziel ein Verlauf oder eine gemalte Maske ist.
        '''
        ''' <paramref name="nurWennSichtbar"/> fuer Aufrufer, die NICHT aus dem Pinselweg kommen
        ''' (Undo/Redo): die View zeigt das rote Overlay nur bei einer Masken-Auswahl oder einem
        ''' markierten Verlauf. Ohne diese Bremse baute jedes Rueckgaengig im AUSWAHL-Werkzeug ein
        ''' bis zu 1600 px grosses Bitmap, das niemand zu sehen bekommt.
        '''
        ''' Vorher stand diese Fallunterscheidung dreimal da (Commit, Abbruch, Live-Vorschau), und
        ''' dreimal ist derselbe Fehler passiert: der Auswahl-Weg wurde gerufen, obwohl das Ziel ein
        ''' Verlauf war - mangels aktiver Auswahl LOESCHTE das Overlay, statt es zu zeichnen. Wer
        ''' hier einen vierten Aufrufer anlegt, ruft diese Methode, nicht die Zweige darunter.</summary>
        Private Sub PublishMaskBrushOverlay(Optional livePts As List(Of SKPoint) = Nothing,
                                            Optional eraseMode As Boolean = False,
                                            Optional nurWennSichtbar As Boolean = False)
            Dim gradient = SelectedGradientMask
            If nurWennSichtbar AndAlso gradient Is Nothing AndAlso Not _activeSelectionIsMask Then Return
            If gradient IsNot Nothing Then
                PublishGradientOverlay(gradient, livePts, eraseMode)
                Return
            End If
            If livePts IsNot Nothing AndAlso livePts.Count > 0 Then
                SetSelectionMaskPreviewImage(BuildSelectionRedOverlayBitmap(livePts, eraseMode))
                Return
            End If
            PublishSelectionRedOverlay()
        End Sub

        ''' Live-Vorschau verwerfen (Strich abgebrochen): zurück auf die committete Maske.
        Public Sub CancelMaskBrushStroke()
            PublishMaskBrushOverlay()
        End Sub

        ''' <summary>Commit eines Masken-Pinselstrichs: aus den Strichpunkten (Display-Prozent) einen weichen
        ''' Alpha8-Stempel bauen und über ApplySelectionCandidate mit dem aktuellen Kombiniermodus verrechnen
        ''' (erster Strich ohne aktive Auswahl = "New"). Danach ist die Maske weich-gebacken.</summary>
        Public Sub CommitMaskBrushStroke(xsPercent As Double(), ysPercent As Double())
            If xsPercent Is Nothing OrElse ysPercent Is Nothing OrElse xsPercent.Length = 0 OrElse xsPercent.Length <> ysPercent.Length Then
                PublishMaskBrushOverlay()
                Return
            End If
            Dim selectionSize = GetAnnotationDisplayPixelSize()
            Dim bw = selectionSize.Width, bh = selectionSize.Height
            If bw <= 0 OrElse bh <= 0 Then Return
            Dim radius = CSng(MaskBrushRadiusDisplay())
            Dim softness = MaskBrushStrokeSoftness()
            Dim pts = MaskBrushDisplayPoints(xsPercent, ysPercent, bw, bh)

            Dim margin = CInt(Math.Ceiling(radius + softness + 2))
            Dim minX = Integer.MaxValue, minY = Integer.MaxValue, maxX = Integer.MinValue, maxY = Integer.MinValue
            For Each p In pts
                minX = Math.Min(minX, CInt(Math.Floor(p.X))) : maxX = Math.Max(maxX, CInt(Math.Ceiling(p.X)))
                minY = Math.Min(minY, CInt(Math.Floor(p.Y))) : maxY = Math.Max(maxY, CInt(Math.Ceiling(p.Y)))
            Next
            Dim rectPx = New SKRectI(Math.Max(0, minX - margin), Math.Max(0, minY - margin),
                                     Math.Min(bw, maxX + margin), Math.Min(bh, maxY + margin))
            If rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then
                PublishMaskBrushOverlay()
                Return
            End If

            ' EIN Ablauf fuer beide Ziele: Rueckgaengig-Punkt, Stempel, Overlay und Vorschau standen
            ' vorher zweimal da - einmal fuer den Verlauf, einmal fuer die gemalte Maske. Zwei Kopien
            ' derselben Kette sind genau die Bauform, in der hier schon dreimal derselbe Fehler
            ' entstanden ist. Der Unterschied ist jetzt eine einzige Verzweigung: WOHIN der Strich
            ' geht. Alles davor und danach ist gemeinsam.
            PushUndo()
            Using stamp = ImageProcessor.BuildSoftBrushStampMask(pts, radius, softness, rectPx)
                If stamp IsNot Nothing Then
                    Dim gradient = SelectedGradientMask
                    ' MEHRTEILIGE Maske: der Strich geht DIREKT in den ANGEFASSTEN Bestandteil (die
                    ' Engine entscheidet, ob das ein gemaltes Raster oder die Pinselkorrektur eines
                    ' Verlaufs ist). Zwei Gruende, warum das vor den beiden anderen Wegen steht:
                    '
                    ' Ueber die AUSWAHL zu gehen ginge schief, seit sie die SUMME aller Bestandteile
                    ' traegt - das Zurueckschreiben legte die Summe in den ersten Bestandteil, und
                    ' die uebrigen kaemen beim Rendern ein zweites Mal obendrauf.
                    '
                    ' Und der erste Bestandteil ist nicht mehr das automatische Ziel: wer in der
                    ' Liste einen Bestandteil anklickt, meint ihn. Vorher landete jeder Strich in
                    ' Bestandteil eins, und ein Strich in einem Bereich, den ein SPAETERER
                    ' Bestandteil deckt, aenderte an der Summe dort nichts - er wurde ueberschrieben.
                    Dim componentMask = If(EditedLayerMask(), gradient)
                    If componentMask IsNot Nothing AndAlso componentMask.ComponentCount > 1 Then
                        Dim stroke = BuildSourceMaskFromStamp(stamp, rectPx, componentMask.Name)
                        If stroke IsNot Nothing Then
                            ' Der ECHTE Verknuepfungsmodus, nicht nur "abziehen ja/nein": das
                            ' SCHNEIDEN aus der Modusreihe fiel sonst still auf Hinzufuegen
                            ' zurueck, und die Flaeche wuchs, statt geschnitten zu werden.
                            ImageProcessor.ApplyMaskBrushStrokeToComponent(componentMask, ActiveMaskComponentIndex, stroke,
                                                                           _selectionCombineMode = "Subtract",
                                                                           _selectionCombineMode)
                            ' Nur eine Ebenenmaske haengt an der Auswahl - ein markierter Verlauf hat
                            ' keine, sein Overlay kommt am Ende ueber PublishMaskBrushOverlay.
                            If _editingLayerMaskId <> "" Then ReloadEditedLayerMaskIntoSelection()
                        End If
                    ElseIf gradient IsNot Nothing Then
                        ' Ein Verlauf wird gerechnet, nicht gemalt - er darf nie in die Auswahlmaske
                        ' geladen werden (das machte aus zwei Punkten ein PNG und naehme ihm die
                        ' Aenderbarkeit). Der Strich geht in den Quellraum und von dort in seine
                        ' Pinselkorrektur; WELCHES Ziel gemeint ist, entscheidet die Engine.
                        Dim stroke = BuildSourceMaskFromStamp(stamp, rectPx, gradient.Name)
                        If stroke IsNot Nothing Then
                            ImageProcessor.ApplyMaskBrushStroke(gradient, stroke, _selectionCombineMode = "Subtract")
                        End If
                    Else
                        ' WOHIN der Strich geht, haengt am WERKZEUG: im Maskenwerkzeug entsteht eine
                        ' Maske (rot), im Auswahlwerkzeug eine gewoehnliche Auswahl (Laufameisen) -
                        ' derselbe Pinsel, aber das Ergebnis passt zu dem, womit man arbeitet.
                        ' Liegt bereits eine Maske als aktive Auswahl, bleibt es eine Maske: sonst
                        ' wuerde ein nachbessernder Strich sie in eine Auswahl verwandeln.
                        Dim asMask = CurrentTool <> EditorTool.Selection OrElse ActiveSelectionIsMask
                        ApplySelectionCandidate(stamp, rectPx, "MagicWand", Nothing, Nothing, isMask:=asMask)
                        ' Freistehende Auswahl: weiche Kante ist in die Alpha-Werte gebacken. Ebenen-Maske:
                        ' harte Form (Feather kommt beim Rendern über mask.FeatherPixels) - nicht baken.
                        _selectionMaskSoftBaked = (_editingLayerMaskId = "")
                        ' Beim Bearbeiten einer Ebenen-Maske jeden Strich in die Ebene zurueckschreiben,
                        ' damit die Anpassung der Ebene der neuen Maskenform sofort folgt.
                        If _editingLayerMaskId <> "" Then WriteSelectionMaskBackToLayer()
                    End If
                End If
            End Using
            PublishMaskBrushOverlay()
            TraceLayerInventory("Pinselstrich")
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Projiziert einen Pinselstempel aus dem ANZEIGE-Raum in den Quellraum - über
        ''' denselben Weg wie eine committete Auswahl, damit Zuschnitt, Drehung und Begradigung
        ''' identisch behandelt werden. Dafür bekommt eine Kopie des Rezepts den Stempel als
        ''' Auswahlmaske untergeschoben; das echte Rezept bleibt unberührt (eine Verlaufskorrektur
        ''' darf die laufende Auswahl des Nutzers nicht anfassen).</summary>
        Private Function BuildSourceMaskFromStamp(stamp As SKBitmap, rectPx As SKRectI, name As String) As ImageMask
            If stamp Is Nothing OrElse rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return Nothing
            Try
                Using image = SKImage.FromBitmap(stamp)
                    Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                        Dim adj = BuildAdjustmentsFromFields()
                        adj.SelectionMaskPngBase64 = Convert.ToBase64String(data.ToArray())
                        adj.SelectionMaskLeft = rectPx.Left
                        adj.SelectionMaskTop = rectPx.Top
                        adj.SelectionFeatherPixels = 0
                        ' Nur den Stempelbereich abtasten statt das ganze Bild - bei 20 MP sind das
                        ' gemessen 2,3 Sekunden Unterschied JE STRICH.
                        Return ImageProcessor.CreateSourceMaskFromSelection(adj, name, rectPx)
                    End Using
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' Strich-Randweichheit: freistehend aus dem "Weiche Kante"-Regler (in die Maske gebacken); beim
        ''' Bearbeiten einer Ebenen-Maske 0 (harte Form, Feather wirkt beim Rendern über die Ebene).
        ''' Beim Nachbessern eines VERLAUFS gilt der Regler-Wert: dort gibt es keine Ebenen-Maske im
        ''' Sinne von _editingLayerMaskId (LoadLayerMaskIntoSelection steigt für Verläufe früh aus),
        ''' und die Korrekturraster tragen ihre weiche Kante selbst - der Verlauf hat keinen
        ''' nachgeschalteten Weichzeichner, der sie liefern könnte.
        Private Function MaskBrushStrokeSoftness() As Single
            If _editingLayerMaskId <> "" Then Return 0.0F
            Return CSng(Math.Max(0.0, _selectionFeather))
        End Function

        ' ============================ VERLAUFSMASKEN ============================
        ' Ein Verlauf wird NICHT gemalt, sondern gerechnet: gespeichert sind nur zwei Punkte, ein
        ' Achsenverhältnis und die Weichheit (siehe ImageMask.Kind). Deshalb bleibt er beliebig oft
        ' änderbar, kostet kein PNG im Projekt und ist bei 45 MP genauso schnell wie bei 2 MP.
        ' Die Punkte liegen im QUELLRAUM (Prozent), nicht im Anzeigeraum - sonst wanderte der Verlauf,
        ' sobald später zugeschnitten oder gedreht wird.

        Private _gradientDragMaskId As String = ""
        Private _gradientDragActive As Boolean = False
        Private _gradientSlopX As Double = 0.0
        Private _gradientSlopY As Double = 0.0
        ''' <summary>Rechteck der Pinselkorrektur bei Beginn des Verschiebens. Die Korrektur muss
        ''' MITWANDERN, sonst bleibt ein weggepinselter Bereich stehen, waehrend der Verlauf
        ''' darunter wegzieht. Absolut gemerkt statt schrittweise verschoben: jeder Zug rechnet
        ''' vom Startpunkt aus, ein Aufaddieren wuerde sich ueber die Bewegung aufschaukeln.</summary>
        Private _gradientMoveBrush As SKRectI = SKRectI.Empty

        ''' <summary>Rechnet einen Punkt der ANZEIGE (Prozent) in Quellraum-Prozent um. Nothing, wenn
        ''' der Punkt neben dem Bildinhalt liegt (Canvas-Rand, leere Begradigungs-Ecke).</summary>
        Private Function DisplayPercentToSourcePercent(xPercent As Double, yPercent As Double) As SKPoint?
            Dim baseWidth = GetBaseWidth(), baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return Nothing
            Dim displaySize = GetAnnotationDisplayPixelSize()
            If displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return Nothing
            Dim adj = BuildAppliedGeometryAdjustments()
            Dim source As SKPoint
            If Not ImageProcessor.TryGeometryOutputToSourcePoint(
                displaySize.Width * xPercent / 100.0, displaySize.Height * yPercent / 100.0,
                baseWidth, baseHeight, adj, source) Then Return Nothing
            Return New SKPoint(CSng(source.X / baseWidth * 100.0), CSng(source.Y / baseHeight * 100.0))
        End Function

        ''' <summary>Wie DisplayPercentToSourcePercent, aber oeffentlich - fuer die Hilfslinien der
        ''' Lineale, die in der View liegen, ihre Lage aber im Quellraum fuehren muessen.</summary>
        Public Function DisplayToSourcePercent(xPercent As Double, yPercent As Double) As SKPoint?
            Return DisplayPercentToSourcePercent(xPercent, yPercent)
        End Function

        ''' <summary>Gegenrichtung: Quellraum-Prozent zurück in Anzeige-Prozent, für die Griffe.</summary>
        Public Function SourcePercentToDisplayPercent(xPercent As Double, yPercent As Double) As SKPoint?
            Dim baseWidth = GetBaseWidth(), baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return Nothing
            Dim displaySize = GetAnnotationDisplayPixelSize()
            If displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return Nothing
            Dim adj = BuildAppliedGeometryAdjustments()
            Dim target As SKPoint
            ' Die RECHTE und UNTERE Kante liegen bei 100 Prozent auf einem Pixel, das nicht mehr zum
            ' Bild gehoert - die Kette rechnet mit halboffenen Bereichen und weist den Punkt ab. Ein
            ' Bruchteil eines Pixels nach innen, dann bezeichnet "100 Prozent" die Kante selbst.
            ' Ohne das fehlten dem Stuetzpunktraster die letzte Spalte und die letzte Zeile, und eine
            ' Hilfslinie ganz am rechten Rand verschwand.
            Dim sourceX = Math.Min(baseWidth * xPercent / 100.0, baseWidth - 0.002)
            Dim sourceY = Math.Min(baseHeight * yPercent / 100.0, baseHeight - 0.002)
            If Not ImageProcessor.TrySourcePointToGeometryOutput(
                sourceX, sourceY, baseWidth, baseHeight, adj, target) Then Return Nothing
            Return New SKPoint(CSng(target.X / displaySize.Width * 100.0), CSng(target.Y / displaySize.Height * 100.0))
        End Function

        ''' <summary>Beginnt einen neuen Verlauf am Druckpunkt. Legt Maske UND Korrekturebene sofort an,
        ''' damit der Zug schon live zu sehen ist; ein Zug ohne Weg wird in EndGradientMaskDrag wieder
        ''' verworfen.</summary>
        Public Sub BeginGradientMaskDrag(xPercent As Double, yPercent As Double)
            If Not IsMaskLinearMode AndAlso Not IsMaskRadialMode Then Return
            Dim baseWidth = GetBaseWidth(), baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return
            Dim start = DisplayPercentToSourcePercent(xPercent, yPercent)
            If Not start.HasValue Then Return

            ' HINZUFÜGEN, ABZIEHEN oder SCHNEIDEN heisst: dieser Verlauf gehoert IN die Maske, an der
            ' gerade gearbeitet wird - nicht in eine neue daneben. Nur "Neue Auswahl" legt eine neue
            ' Maske samt Ebene an. Vorher gab es diese Wahl bei Verlaeufen gar nicht, und jeder Zug
            ' erzeugte eine weitere Ebene.
            If Not IsSelectionCombineNew Then
                Dim target = CurrentMaskForComponents()
                If target IsNot Nothing Then
                    BeginGradientComponentDrag(target, start.Value)
                    Return
                End If
            End If

            PushUndo()
            ' Eine laufende Pixelauswahl hat mit dem Verlauf nichts zu tun und würde sonst als
            ' zweite, konkurrierende Maske weiterleben.
            If _hasActiveSelection Then ClearSelection(captureUndo:=False)
            Dim kind = If(IsMaskRadialMode, "Radial", "Linear")
            Dim mask As New ImageMask With {
                .Name = LocalizationService.T(If(kind = "Radial", "Radialer Verlauf", "Linearer Verlauf")) & " " & (_imageMasks.Count + 1).ToString(),
                .Kind = kind,
                .SourceWidthPixels = baseWidth,
                .SourceHeightPixels = baseHeight,
                .GradientStartXPercent = start.Value.X,
                .GradientStartYPercent = start.Value.Y,
                .GradientEndXPercent = start.Value.X,
                .GradientEndYPercent = start.Value.Y,
                .GradientRadiusRatio = 1.0,
                .GradientFeatherPercent = _gradientFeatherPercent,
                .Inverted = _gradientInverted
            }
            _imageMasks.Add(mask)
            Dim layer As New MaskedAdjustmentLayer With {
                .Name = mask.Name,
                .MaskId = mask.Id,
                .Adjustments = New ImageAdjustments(),
                .IsMaskLayer = True
            }
            PlaceNewCorrectionLayerInBaseImage(layer)
            _maskedAdjustmentLayers.Add(layer)
            _selectedMaskedAdjustmentLayerId = layer.Id
            _gradientDragMaskId = mask.Id
            _workingMaskId = mask.Id
            _activeMaskComponentIndex = -1
            _gradientDragActive = True
            _gradientHandle = -1
            RaiseMaskComponentsChanged()
        End Sub

        ''' <summary>Die Maske, in die ein weiterer Bestandteil gehoert: die des markierten Verlaufs,
        ''' die einer markierten Korrekturebene oder die gerade bearbeitete Ebenenmaske. Nothing =
        ''' es gibt keine, dann entsteht wie bisher eine neue.</summary>
        Private Function CurrentMaskForComponents() As ImageMask
            ' 1) Ausdruecklich gemerkt: waehrend und nach einem Bestandteil-Zug. Steht ganz vorn,
            '    damit der zweite Zug dieselbe Maske trifft wie der erste.
            If _workingMaskId <> "" Then
                Dim working = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                             String.Equals(m.Id, _workingMaskId, StringComparison.Ordinal))
                If working IsNot Nothing Then Return working
                _workingMaskId = ""
            End If
            ' 2) Die Ebenenmaske, die gerade bearbeitet wird (rotes Overlay).
            If _editingLayerMaskId <> "" Then
                Dim edited = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                            String.Equals(m.Id, _editingLayerMaskId, StringComparison.Ordinal))
                If edited IsNot Nothing Then Return edited
            End If
            ' 3) Die Maske der markierten Korrekturebene - GLEICH WELCHER ART. SelectedGradientMask
            '    taugt hier nicht: sie verlangt einen Verlauf als ersten Bestandteil.
            Dim layer = _maskedAdjustmentLayers.FirstOrDefault(Function(l) l IsNot Nothing AndAlso
                                                                   l.Id = _selectedMaskedAdjustmentLayerId)
            If layer Is Nothing OrElse String.IsNullOrEmpty(layer.MaskId) Then Return Nothing
            Return _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                  String.Equals(m.Id, layer.MaskId, StringComparison.Ordinal))
        End Function

        ''' <summary>Haengt einen Verlauf als weiteren Bestandteil an eine vorhandene Maske und macht
        ''' ihn zum aktiven - der Zug bedient ab jetzt IHN.</summary>
        Private Sub BeginGradientComponentDrag(target As ImageMask, start As SKPoint)
            PushUndo()
            ' Eine laufende PIXELAUSWAHL gehoert nicht dazu; sonst laege eine zweite Maske an. Die
            ' bearbeitete EBENENMASKE ist aber keine solche Auswahl, sondern genau die Maske, an die
            ' angehaengt wird - sie wegzuraeumen loeschte das rote Overlay und die Bindung daran.
            If _hasActiveSelection AndAlso _editingLayerMaskId = "" Then ClearSelection(captureUndo:=False)
            _workingMaskId = target.Id
            Dim component As New MaskComponent With {
                .Mode = SelectionCombineMode,
                .Kind = If(IsMaskRadialMode, "Radial", "Linear"),
                .GradientStartXPercent = start.X,
                .GradientStartYPercent = start.Y,
                .GradientEndXPercent = start.X,
                .GradientEndYPercent = start.Y,
                .GradientRadiusRatio = 1.0,
                .GradientFeatherPercent = _gradientFeatherPercent,
                .Inverted = _gradientInverted}
            target.AddComponent(component)
            ' Der neue Bestandteil ist der letzte, und der letzte ist der aktive.
            _activeMaskComponentIndex = -1
            ' Die Ebene dieser Maske markieren, damit Regler und Overlay auf sie zeigen.
            Dim owner = _maskedAdjustmentLayers.FirstOrDefault(Function(l) l IsNot Nothing AndAlso
                                                                   String.Equals(l.MaskId, target.Id, StringComparison.Ordinal))
            If owner IsNot Nothing Then _selectedMaskedAdjustmentLayerId = owner.Id
            _gradientDragMaskId = target.Id
            _gradientDragActive = True
            _gradientHandle = -1
            RaiseGradientPropertiesChanged()
            RaiseMaskComponentsChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Zieht den Endpunkt mit. Mit gedrückter Umschalttaste rastet die Achse auf
        ''' 15-Grad-Schritte - so bekommt man einen exakt waagerechten Horizontverlauf.</summary>
        Public Sub UpdateGradientMaskDrag(xPercent As Double, yPercent As Double, rasten As Boolean)
            If Not _gradientDragActive Then Return
            Dim mask = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso m.Id = _gradientDragMaskId)
            If mask Is Nothing Then Return
            Dim ende = DisplayPercentToSourcePercent(xPercent, yPercent)
            If Not ende.HasValue Then Return
            ' Der Zug spannt den AKTIVEN Bestandteil auf. Beim Anhaengen an eine vorhandene Maske ist
            ' das der eben erzeugte - ohne das zoege man am ERSTEN Verlauf, und der neue bliebe ein
            ' Punkt.
            Dim component = ActiveMaskComponent()
            If component Is Nothing Then Return
            Dim ex As Double = ende.Value.X, ey As Double = ende.Value.Y
            If rasten Then
                ' Rasten muss in PIXELN rechnen, nicht in Prozent: bei 3:2 wäre ein "45-Grad"-Zug
                ' in Prozent in Wahrheit 34 Grad.
                Dim dx = (ex - component.GradientStartXPercent) / 100.0 * mask.SourceWidthPixels
                Dim dy = (ey - component.GradientStartYPercent) / 100.0 * mask.SourceHeightPixels
                Dim laenge = Math.Sqrt(dx * dx + dy * dy)
                If laenge > 0.0001 Then
                    Dim angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 12.0)) * (Math.PI / 12.0)
                    ex = component.GradientStartXPercent + Math.Cos(angle) * laenge / mask.SourceWidthPixels * 100.0
                    ey = component.GradientStartYPercent + Math.Sin(angle) * laenge / mask.SourceHeightPixels * 100.0
                End If
            End If
            component.GradientEndXPercent = ex
            component.GradientEndYPercent = ey
            CommitActiveMaskComponent(component)
            PublishGradientOverlay(mask)
            RaiseGradientPropertiesChanged()
        End Sub

        ''' <summary>Schliesst den Zug ab. Ein blosser Klick (kein Weg) lässt keine leere Ebene zurück.</summary>
        Public Sub EndGradientMaskDrag()
            If Not _gradientDragActive Then Return
            _gradientDragActive = False
            Dim mask = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso m.Id = _gradientDragMaskId)
            _gradientDragMaskId = ""
            If mask Is Nothing Then Return
            Dim component = ActiveMaskComponent()
            If component Is Nothing Then Return
            Dim dx = (component.GradientEndXPercent - component.GradientStartXPercent) / 100.0 * mask.SourceWidthPixels
            Dim dy = (component.GradientEndYPercent - component.GradientStartYPercent) / 100.0 * mask.SourceHeightPixels
            If Math.Sqrt(dx * dx + dy * dy) < 4.0 Then
                ' Ein blosser Klick laesst nichts Leeres zurueck. War der Verlauf ein WEITERER
                ' Bestandteil, faellt nur er weg - die Maske darunter hat mit dem Fehlgriff nichts zu
                ' tun, und sie samt Ebene zu entfernen waere ein Datenverlust.
                If mask.ComponentCount > 1 Then
                    mask.RemoveComponentAt(ActiveMaskComponentIndex)
                    _activeMaskComponentIndex = -1
                    ' Das rote Overlay nachziehen: waehrend des Zugs wurde der eben entfernte Verlauf
                    ' laufend mitgezeichnet und bliebe sonst darin stehen, obwohl es ihn nicht mehr
                    ' gibt. Die andere Seite raeumt es in RemoveGradientMaskAndLayer selbst ab.
                    PublishGradientOverlay(mask)
                Else
                    RemoveGradientMaskAndLayer(mask)
                End If
                RebuildLayerRows()
                RaiseGradientPropertiesChanged()
                Return
            End If
            RebuildLayerRows()
            AddHistoryEntry(If(mask.IsRadialGradient, "Radialer Verlauf", "Linearer Verlauf"))
            SchedulePreviewUpdate()
            RaiseGradientPropertiesChanged()
        End Sub

        Private Sub RemoveGradientMaskAndLayer(mask As ImageMask)
            If mask Is Nothing Then Return
            Dim ebenen = _maskedAdjustmentLayers.Where(Function(l) l IsNot Nothing AndAlso l.MaskId = mask.Id).ToList()
            For Each l In ebenen
                _maskedAdjustmentLayers.Remove(l)
                If _selectedMaskedAdjustmentLayerId = l.Id Then _selectedMaskedAdjustmentLayerId = ""
            Next
            RemoveMaskIfUnreferenced(mask.Id)
            SetSelectionMaskPreviewImage(Nothing)
        End Sub

        ' ── Ablaufspur der Masken (In-App-Diagnoselog) ──────────────────────────
        '
        ' Masken sind Daten OHNE sichtbare Kennung: sieht eine Ebene falsch aus, ist von aussen nicht
        ' zu erkennen, WELCHE Maske sie gerade benutzt und wer zuletzt hineingeschrieben hat. Diese
        ' Zeilen beantworten genau das - eingeschaltet ueber "Diagnose-Log" in den Einstellungen,
        ' ausgeschaltet kosten sie nichts (die teuren Angaben entstehen erst hinter der Abfrage).

        Private Shared Function IsMaskTraceEnabled() As Boolean
            Try
                Return AppSettingsService.Load().EnableDiagnosticLogging
            Catch
                Return False
            End Try
        End Function

        Private Shared Sub TraceMask(message As Func(Of String))
            If Not IsMaskTraceEnabled() Then Return
            Try
                DiagnosticLogService.LogAlways("Maske", message())
            Catch
            End Try
        End Sub

        ''' <summary>Kurzform einer Maske fuer die Ablaufspur: Kennung, Form und Umkehrung. Die FORM
        ''' ist ein Fingerabdruck ueber die Pixel - zwei Ebenen mit derselben Form, aber verschiedenen
        ''' Kennungen sind Kopien; dieselbe Kennung heisst geteilt.</summary>
        Private Function MaskTrace(maskId As String) As String
            If String.IsNullOrEmpty(maskId) Then Return "Maske=-"
            Dim mask = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                      String.Equals(m.Id, maskId, StringComparison.Ordinal))
            If mask Is Nothing Then Return $"Maske={Kurz(maskId)}(FEHLT)"
            Return $"Maske={Kurz(maskId)} Form={FormKurz(mask.PngBase64)} Rand={mask.Left},{mask.Top},{mask.Right},{mask.Bottom}" &
                   $" Umgekehrt={mask.Inverted} Teile={mask.ComponentCount}"
        End Function

        Private Shared Function Kurz(id As String) As String
            If String.IsNullOrEmpty(id) Then Return "-"
            Return If(id.Length <= 8, id, id.Substring(0, 8))
        End Function

        ''' <summary>Fingerabdruck der Maskenpixel: Laenge plus Kurzhash. Gleicher Wert = gleiche Form.</summary>
        Private Shared Function FormKurz(pngBase64 As String) As String
            If String.IsNullOrEmpty(pngBase64) Then Return "leer"
            Try
                Using sha = System.Security.Cryptography.SHA1.Create()
                    Dim hash = sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(pngBase64))
                    Return $"{pngBase64.Length}/{BitConverter.ToString(hash).Replace("-", "").Substring(0, 8).ToLowerInvariant()}"
                End Using
            Catch
                Return pngBase64.Length.ToString()
            End Try
        End Function

        ''' <summary>Der ganze Bestand in einer Zeile je Ebene: welche Ebene benutzt welche Maske und
        ''' welche Form hat die. Nach jedem Eingriff aufgerufen - daran ist im Log sofort zu sehen, ob
        ''' zwei Ebenen dieselbe Kennung tragen (geteilt) oder ob eine Form ueberschrieben wurde.</summary>
        Private Sub TraceLayerInventory(occasion As String)
            If Not IsMaskTraceEnabled() Then Return
            Try
                Dim layerLines = _maskedAdjustmentLayers.Where(Function(l) l IsNot Nothing).
                    Select(Function(l, i) $"  [{i}] Ebene={Kurz(l.Id)} ""{l.Name}"" {MaskTrace(l.MaskId)}").ToList()
                Dim annotationLines = _annotations.Where(Function(a) a IsNot Nothing AndAlso Not String.IsNullOrEmpty(a.MaskId)).
                    Select(Function(a) $"  [Objekt] {Kurz(a.Id)} {MaskTrace(a.MaskId)}").ToList()
                DiagnosticLogService.LogAlways("Maske",
                    $"Bestand nach {occasion}: {_maskedAdjustmentLayers.Count} Korrekturebenen, {_imageMasks.Count} Masken," &
                    $" bearbeitet={Kurz(_editingLayerMaskId)} Arbeitsmaske={Kurz(_workingMaskId)}" &
                    $" markiert={Kurz(_selectedMaskedAdjustmentLayerId)} promotet={Kurz(_selectionPromotedLayerId)}" &
                    Environment.NewLine & String.Join(Environment.NewLine, layerLines.Concat(annotationLines)))
            Catch
            End Try
        End Sub

        ''' <summary>Zeigt noch irgendetwas auf diese Maske? Korrekturebenen UND Objekte zaehlen: seit
        ''' ein Objekt eine eigene Ebenenmaske tragen darf, reicht der Blick auf die Korrekturebenen
        ''' nicht mehr. Wer nur die eine Liste prueft, loescht die Maske unter dem Objekt weg - das
        ''' Objekt behielte eine Kennung ohne Daten, und seine Maske waere lautlos wirkungslos.</summary>
        Private Function IsMaskReferenced(maskId As String) As Boolean
            If String.IsNullOrEmpty(maskId) Then Return False
            If _maskedAdjustmentLayers.Any(Function(l) l IsNot Nothing AndAlso
                                               String.Equals(l.MaskId, maskId, StringComparison.Ordinal)) Then Return True
            ' Auch eine GRUPPE kann eine Maske halten - ohne diese Zeile raeumte das Aufraeumen
            ' eine Maske weg, auf die die Gruppe noch zeigt.
            If _annotationGroups.Any(Function(g) g IsNot Nothing AndAlso
                                         String.Equals(g.MaskId, maskId, StringComparison.Ordinal)) Then Return True
            Return _annotations.Any(Function(a) a IsNot Nothing AndAlso
                                        String.Equals(a.MaskId, maskId, StringComparison.Ordinal))
        End Function

        ''' <summary>Gibt der KOPIE eines Objekts eine eigene Ebenenmaske.
        '''
        ''' Wer die Maske der Kopie nachmalt, meint die Kopie und nicht zugleich das Original. Ohne
        ''' Maske bleibt alles, wie es ist.</summary>
        Private Sub GiveCopyItsOwnMask(copy As ImageAnnotation)
            If copy Is Nothing Then Return
            copy.MaskId = CloneMaskForCopy(copy.MaskId)
        End Sub

        ''' <summary>Dasselbe fuer die KOPIE einer Korrekturebene. „Duplizieren" heisst eine
        ''' UNABHAENGIGE zweite Ebene: wer danach ihre Maske umkehrt, nachmalt oder verschiebt, meint
        ''' die Kopie und nicht zugleich das Original. Zwei Ebenen teilen sich an KEINER Stelle eine
        ''' Maske. Vorher trug die Kopie dieselbe MaskId, und ein Umkehren an der Kopie kehrte die
        ''' Ursprungsmaske mit um.</summary>
        Private Sub GiveCopyItsOwnMask(copy As MaskedAdjustmentLayer)
            If copy Is Nothing Then Return
            copy.MaskId = CloneMaskForCopy(copy.MaskId)
        End Sub

        ''' <summary>Legt eine eigenstaendige Abschrift der Maske an und liefert deren Kennung. Leere
        ''' Kennung zurueck, wenn es die Maske nicht (mehr) gibt - eine Kennung ohne Daten waere eine
        ''' lautlos wirkungslose Maske.</summary>
        Private Function CloneMaskForCopy(maskId As String) As String
            If String.IsNullOrEmpty(maskId) Then Return ""
            Dim original = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                          String.Equals(m.Id, maskId, StringComparison.Ordinal))
            If original Is Nothing Then
                TraceMask(Function() $"Abschrift nicht möglich: Maske {Kurz(maskId)} steht nicht mehr in der Liste")
                Return ""
            End If
            Dim clone = original.Clone()
            clone.Id = Guid.NewGuid().ToString("N")
            _imageMasks.Add(clone)
            TraceMask(Function() $"Abschrift der Maske: {Kurz(maskId)} -> {Kurz(clone.Id)} Form={FormKurz(clone.PngBase64)}")
            Return clone.Id
        End Function

        ''' <summary>Die EINE Stelle, die eine Maske wegraeumt: nur, wenn nichts mehr auf sie zeigt.</summary>
        Private Sub RemoveMaskIfUnreferenced(maskId As String)
            If String.IsNullOrEmpty(maskId) OrElse IsMaskReferenced(maskId) Then Return
            _imageMasks.RemoveAll(Function(m) m IsNot Nothing AndAlso String.Equals(m.Id, maskId, StringComparison.Ordinal))
            If String.Equals(_editingLayerMaskId, maskId, StringComparison.Ordinal) Then _editingLayerMaskId = ""
            If String.Equals(_workingMaskId, maskId, StringComparison.Ordinal) Then _workingMaskId = ""
        End Sub

        ' --- Regler des Masken-Werkzeugs -------------------------------------------------
        ' Die Felder halten die VOREINSTELLUNG für den nächsten Verlauf. Ist gerade ein Verlauf
        ' markiert, schreiben die Setter zusätzlich direkt in ihn - genau das macht ihn nachträglich
        ' änderbar, ohne ihn neu ziehen zu müssen.

        Private _gradientFeatherPercent As Double = 50.0
        Private _gradientInverted As Boolean = False
        Private _gradientRadiusRatio As Double = 1.0

        ' ── Der BESTANDTEIL, an dem gerade gearbeitet wird ───────────────────────
        '
        ' Eine Maske kann aus mehreren Bestandteilen bestehen (Verlauf plus Verlauf plus gemalt, je
        ' hinzugefuegt, abgezogen oder geschnitten). Griffe und Regler bedienen immer GENAU EINEN
        ' davon. Standard ist der ZULETZT hinzugefuegte: wer einen zweiten Verlauf aufzieht, meint
        ' ihn und nicht den ersten.
        '
        ' Gelesen wird eine ABSCHRIFT, zurueckgegeben wird ueber CommitActiveMaskComponent. Das ist
        ' Absicht: der erste Bestandteil liegt in den Feldern der Maske selbst, die weiteren in ihrer
        ' Liste - eine Referenz gaebe es also nur fuer einen Teil der Faelle, und genau daran wuerde
        ' spaeter jemand haengenbleiben.

        Private _activeMaskComponentIndex As Integer = -1

        ''' <summary>Die Maske, an der gerade gearbeitet wird. AUSDRÜCKLICH gemerkt und nicht aus
        ''' <see cref="SelectedGradientMask"/> abgeleitet: die verlangt, dass der ERSTE Bestandteil
        ''' ein Verlauf ist. Hängt man einen Verlauf an eine GEMALTE Maske (der Normalfall bei einer
        ''' Ebenenmaske), lieferte sie Nothing - der Zug fand seinen Bestandteil nicht, es passierte
        ''' nichts, und der nächste Klick legte eine neue Ebene an.</summary>
        Private _workingMaskId As String = ""

        ''' <summary>Stelle des bedienten Bestandteils in der Maske. -1 bzw. zu gross heisst: der
        ''' letzte.</summary>
        Public Property ActiveMaskComponentIndex As Integer
            Get
                Dim m = CurrentMaskForComponents()
                If m Is Nothing Then Return 0
                Dim count = m.ComponentCount
                If count = 0 Then Return 0
                If _activeMaskComponentIndex < 0 OrElse _activeMaskComponentIndex >= count Then Return count - 1
                Return _activeMaskComponentIndex
            End Get
            Set(value As Integer)
                If _activeMaskComponentIndex = value Then Return
                _activeMaskComponentIndex = value
                RaiseGradientPropertiesChanged()
                RaiseMaskComponentsChanged()
            End Set
        End Property

        ''' <summary>Die Bestandteile der bearbeiteten Maske als Zeilen für das Panel.</summary>
        Public ReadOnly Property MaskComponentRows As New ObservableCollection(Of MaskComponentRow)()

        ''' <summary>Lohnt die Liste überhaupt? Bei genau einem Bestandteil sagt sie nichts, was das
        ''' Panel nicht schon zeigt - dann bleibt sie weg.</summary>
        Public ReadOnly Property HasMaskComponents As Boolean
            Get
                Return MaskComponentRows.Count > 1
            End Get
        End Property

        Private Sub RaiseMaskComponentsChanged()
            MaskComponentRows.Clear()
            Dim m = CurrentMaskForComponents()
            If m IsNot Nothing Then
                Dim components = m.GetComponents()
                Dim active = ActiveMaskComponentIndex
                For i = 0 To components.Count - 1
                    MaskComponentRows.Add(New MaskComponentRow(i, components(i), i = active, components.Count))
                Next
            End If
            Me.RaisePropertyChanged(NameOf(MaskComponentRows))
            Me.RaisePropertyChanged(NameOf(HasMaskComponents))
            Me.RaisePropertyChanged(NameOf(ActiveMaskComponentIndex))
            RaiseMaskPropertiesChanged()
        End Sub

        ''' <summary>Die Maskeneigenschaften haengen an derselben Maske wie die Bestandteilliste -
        ''' wechselt die, wechseln beide. Deshalb an EINER Stelle gemeldet: eine zweite Liste von
        ''' Anstoss-Stellen hat schon einmal genau die eine vergessen, auf die es ankam.</summary>
        Private Sub RaiseMaskPropertiesChanged()
            Me.RaisePropertyChanged(NameOf(ShowMaskProperties))
            Me.RaisePropertyChanged(NameOf(ShowMaskDensity))
            Me.RaisePropertyChanged(NameOf(ShowMaskFeather))
            Me.RaisePropertyChanged(NameOf(MaskDensity))
            Me.RaisePropertyChanged(NameOf(IsMaskDisabled))
            Me.RaisePropertyChanged(NameOf(CanCopyMask))
            Me.RaisePropertyChanged(NameOf(CanPasteMask))
        End Sub

        ' ── Umkehren und Verwerfen fuer JEDE Masken-Art ─────────────────────────
        '
        ' Die Modusreihe (Neu, Plus, Minus, Umkehren, Verwerfen) steht in allen Arten des
        ' Masken-Werkzeugs. Was ein VERLAUF dabei umkehrt und verwirft, ist aber ein anderes Ding als
        ' bei einer gemalten Maske: er hat keine aktive Auswahl, sondern einen Bestandteil und eine
        ' Ebene. Die beiden Befehle entscheiden das deshalb selbst, statt es der Oberflaeche mit zwei
        ' Knopfsaetzen aufzubuerden.

        Public ReadOnly Property CanInvertMask As Boolean
            Get
                Return _hasActiveSelection OrElse ActiveMaskComponent() IsNot Nothing
            End Get
        End Property

        Public Sub InvertCurrentMask()
            If ActiveMaskComponent() IsNot Nothing AndAlso Not _hasActiveSelection Then
                GradientInverted = Not GradientInverted
                Return
            End If
            InvertSelection()
        End Sub

        Public ReadOnly Property CanDiscardMask As Boolean
            Get
                Return _hasActiveSelection OrElse CurrentMaskForComponents() IsNot Nothing
            End Get
        End Property

        ''' <summary>Verwerfen heisst bei einer aktiven Auswahl: Auswahl weg. Bei einem markierten
        ''' Verlauf gibt es keine Auswahl - dort ist die MASKE samt ihrer Ebene gemeint.
        '''
        ''' Zeigt ein OBJEKT auf diese Maske, muss es seine Kennung ebenfalls abgeben. Sonst raeumt
        ''' RemoveGradientMaskAndLayer nur die Korrekturebenen ab, die Maske ueberlebt unter dem
        ''' Objekt weiter, der Vermerk auf die bearbeitete Maske bleibt stehen, sichtbar passiert
        ''' nichts - und PushUndo hinterlaesst einen leeren Schritt. Verworfen wird die Maske also
        ''' wirklich; danach greift RemoveMaskIfUnreferenced.</summary>
        Public Sub DiscardCurrentMask()
            If _hasActiveSelection Then
                ClearSelection()
                Return
            End If
            Dim mask = CurrentMaskForComponents()
            If mask Is Nothing Then Return
            PushUndo()
            _workingMaskId = ""
            Dim owners = _annotations.Where(Function(a) a IsNot Nothing AndAlso
                                                String.Equals(a.MaskId, mask.Id, StringComparison.Ordinal)).ToList()
            For Each owner In owners
                owner.MaskId = ""
            Next
            RemoveGradientMaskAndLayer(mask)
            _activeMaskComponentIndex = -1
            RaiseGradientPropertiesChanged()
            If owners.Count > 0 Then RaiseAnnotationMaskStateChanged()
            PublishMaskBrushOverlay()
            RebuildLayerRows()
            _hasChanges = True
            SchedulePreviewUpdate()
        End Sub

        Private Sub RaiseMaskActionStateChanged()
            Me.RaisePropertyChanged(NameOf(CanInvertMask))
            Me.RaisePropertyChanged(NameOf(CanDiscardMask))
            ' Der Eigenschaften-Block haengt an derselben Frage "gibt es hier eine Maske" - eine
            ' laufende Auswahl bringt ihn ebenso auf wie eine markierte Ebene.
            RaiseMaskPropertiesChanged()
        End Sub

        ''' <summary>Einen Bestandteil zum bedienten machen.</summary>
        Public Sub SelectMaskComponent(index As Integer)
            ActiveMaskComponentIndex = index
        End Sub

        ''' <summary>Einen Bestandteil entfernen. War es der letzte, bleibt die Maske leer stehen -
        ''' entfernt wird sie über ihre Ebene, nicht hier.</summary>
        Public Sub RemoveMaskComponent(index As Integer)
            Dim m = CurrentMaskForComponents()
            If m Is Nothing Then Return
            If index < 0 OrElse index >= m.ComponentCount Then Return
            PushUndo()
            m.RemoveComponentAt(index)
            _activeMaskComponentIndex = -1
            RaiseGradientPropertiesChanged()
            RaiseMaskComponentsChanged()
            ' Wird die Maske gerade als Ebenenmaske BEARBEITET, traegt _selectionMask noch die
            ' alte Summe samt des entfernten Bestandteils - der naechste Pinselstrich schriebe
            ' sie ueber WriteSelectionMaskBackToLayer als gemalte Pixel zurueck. Neu laden stellt
            ' den kompletten Anzeigezustand her (Auswahl, Rot, Bestandteilliste), auch fuer
            ' gemalt-erste Masken, deren Rot nicht aus dem Verlaufs-Overlay kommt.
            If _editingLayerMaskId <> "" AndAlso String.Equals(_editingLayerMaskId, m.Id, StringComparison.Ordinal) Then
                ReloadEditedLayerMaskIntoSelection()
            Else
                PublishGradientOverlay(m)
            End If
            _hasChanges = True
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Das Auge eines Bestandteils: er bleibt erhalten und zaehlt nur nicht mehr mit.
        '''
        ''' Derselbe Weg wie beim Entfernen, nur ohne Verlust - inklusive des Neuladens der
        ''' bearbeiteten Ebenenmaske: ohne das traegt die Arbeitskopie weiter die alte Summe, und der
        ''' naechste Pinselstrich schriebe den ausgeschalteten Bestandteil als gemalte Pixel
        ''' zurueck.</summary>
        Public Sub ToggleMaskComponentVisible(index As Integer)
            Dim m = CurrentMaskForComponents()
            If m Is Nothing Then Return
            If index < 0 OrElse index >= m.ComponentCount Then Return
            PushUndo()
            If index = 0 Then
                m.PrimaryVisible = Not m.PrimaryVisible
            Else
                Dim extra = m.ExtraComponents(index - 1)
                If extra Is Nothing Then Return
                extra.IsVisible = Not extra.IsVisible
            End If
            AfterMaskComponentChange(m)
        End Sub

        ''' <summary>Der gemeinsame Abschluss jeder Änderung an der Bestandteilliste: Liste neu
        ''' aufbauen, Overlay nachziehen, Vorschau anstoßen.
        '''
        ''' Das NEULADEN einer bearbeiteten Ebenenmaske gehört dazu: ohne das trägt die Arbeitskopie
        ''' weiter die alte Summe, und der nächste Pinselstrich schriebe den alten Stand zurück.</summary>
        Private Sub AfterMaskComponentChange(m As ImageMask)
            RaiseGradientPropertiesChanged()
            RaiseMaskComponentsChanged()
            If _editingLayerMaskId <> "" AndAlso String.Equals(_editingLayerMaskId, m.Id, StringComparison.Ordinal) Then
                ReloadEditedLayerMaskIntoSelection()
            Else
                PublishGradientOverlay(m)
            End If
            _hasChanges = True
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Verschiebt einen Bestandteil in der Reihe - <paramref name="delta"/> ist -1 für
        ''' nach oben und +1 für nach unten. Die Reihenfolge ist keine Anzeigefrage: der ERSTE
        ''' sichtbare Bestandteil setzt das Ergebnis, jeder weitere wird mit seinem Modus darauf
        ''' verrechnet. Wer einen abgezogenen Bestandteil nach oben holt, ändert damit das Bild.
        '''
        ''' Der Bestandteil, der auf Platz eins landet, verliert seinen Modus - dort gibt es nichts,
        ''' worauf man rechnen könnte. Der bisher erste bekommt beim Abrutschen „Hinzufügen", weil er
        ''' bis dahin gar keinen trug.
        '''
        ''' Der ANGEFASSTE Bestandteil wandert mit: sonst zeigten die Regler nach dem Verschieben auf
        ''' einen anderen als den, den man gerade in der Hand hatte.</summary>
        Public Sub MoveMaskComponent(index As Integer, delta As Integer)
            Dim m = CurrentMaskForComponents()
            If m Is Nothing Then Return
            Dim components = m.GetComponents()
            Dim target = index + delta
            If index < 0 OrElse index >= components.Count Then Return
            If target < 0 OrElse target >= components.Count Then Return
            PushUndo()
            Dim moved = components(index)
            components.RemoveAt(index)
            components.Insert(target, moved)
            m.SetPrimaryFromComponent(components(0))
            m.ExtraComponents = components.Skip(1).ToList()
            If _activeMaskComponentIndex = index Then
                _activeMaskComponentIndex = target
            ElseIf _activeMaskComponentIndex = target Then
                _activeMaskComponentIndex = index
            End If
            TraceMask(Function() $"Bestandteil {index} nach {target} verschoben: {MaskTrace(m.Id)}")
            AfterMaskComponentChange(m)
        End Sub

        ''' <summary>Schaltet den Verknüpfungsmodus eines Bestandteils weiter: hinzufügen, abziehen,
        ''' schneiden, und wieder von vorn. Der ERSTE Bestandteil hat keinen - er setzt das Ergebnis,
        ''' und ein Modus stünde dort ohne Gegenüber.</summary>
        Public Sub CycleMaskComponentMode(index As Integer)
            Dim m = CurrentMaskForComponents()
            If m Is Nothing OrElse index <= 0 Then Return
            If m.ExtraComponents Is Nothing OrElse index - 1 >= m.ExtraComponents.Count Then Return
            Dim c = m.ExtraComponents(index - 1)
            If c Is Nothing Then Return
            PushUndo()
            Select Case If(c.Mode, "").Trim().ToLowerInvariant()
                Case "subtract" : c.Mode = "Intersect"
                Case "intersect" : c.Mode = "Add"
                Case Else : c.Mode = "Subtract"
            End Select
            TraceMask(Function() $"Bestandteil {index} steht jetzt auf {c.Mode}: {MaskTrace(m.Id)}")
            AfterMaskComponentChange(m)
        End Sub

        ''' <summary>Maske vorübergehend AUS. Sie bleibt vollstaendig erhalten und deckt solange
        ''' ueberall - so sieht man, was ohne sie geschaehe.</summary>
        Public Property IsMaskDisabled As Boolean
            Get
                Dim m = CurrentMaskForComponents()
                Return m IsNot Nothing AndAlso m.IsDisabled
            End Get
            Set(value As Boolean)
                Dim m = CurrentMaskForComponents()
                If m Is Nothing OrElse m.IsDisabled = value Then Return
                CaptureUndoState("IsMaskDisabled")
                m.IsDisabled = value
                Me.RaisePropertyChanged(NameOf(IsMaskDisabled))
                _hasChanges = True
                SchedulePreviewUpdate()
            End Set
        End Property

        Private Function ActiveMaskComponent() As MaskComponent
            Dim m = CurrentMaskForComponents()
            If m Is Nothing Then Return Nothing
            Dim components = m.GetComponents()
            Dim index = ActiveMaskComponentIndex
            If index < 0 OrElse index >= components.Count Then Return Nothing
            Return components(index)
        End Function

        Private Sub CommitActiveMaskComponent(c As MaskComponent)
            Dim m = CurrentMaskForComponents()
            If m Is Nothing OrElse c Is Nothing Then Return
            Dim index = ActiveMaskComponentIndex
            If index = 0 Then
                m.SetPrimaryFromComponent(c)
            ElseIf m.ExtraComponents IsNot Nothing AndAlso index - 1 < m.ExtraComponents.Count Then
                m.ExtraComponents(index - 1) = c
            End If
        End Sub

        Public Property GradientFeatherPercent As Double
            Get
                Dim c = ActiveMaskComponent()
                Return If(c IsNot Nothing, c.GradientFeatherPercent, _gradientFeatherPercent)
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                _gradientFeatherPercent = v
                Dim c = ActiveMaskComponent()
                If c IsNot Nothing AndAlso Math.Abs(c.GradientFeatherPercent - v) > 0.0001 Then
                    c.GradientFeatherPercent = v
                    CommitActiveMaskComponent(c)
                    ' Veroeffentlicht wird die Maske des AKTIVEN Bestandteils. SelectedGradientMask
                    ' verlangt einen Verlauf als ERSTEN Bestandteil und liefert bei einem Verlauf an
                    ' einer gemalten Ebenenmaske - dem Normalfall - Nothing; das rote Overlay wurde
                    ' dann beim Drehen am Regler geloescht statt neu gezeichnet.
                    PublishGradientOverlay(CurrentMaskForComponents())
                    SchedulePreviewUpdate()
                End If
                Me.RaisePropertyChanged(NameOf(GradientFeatherPercent))
            End Set
        End Property

        ''' <summary>Stauchung der zweiten Halbachse beim radialen Verlauf: 1 = Kreis, kleiner = flach.</summary>
        Public Property GradientRadiusRatio As Double
            Get
                Dim c = ActiveMaskComponent()
                Return If(c IsNot Nothing, c.GradientRadiusRatio, _gradientRadiusRatio)
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.05, Math.Min(4.0, value))
                _gradientRadiusRatio = v
                Dim c = ActiveMaskComponent()
                If c IsNot Nothing AndAlso Math.Abs(c.GradientRadiusRatio - v) > 0.0001 Then
                    c.GradientRadiusRatio = v
                    CommitActiveMaskComponent(c)
                    ' Maske des AKTIVEN Bestandteils, siehe GradientFeatherPercent.
                    PublishGradientOverlay(CurrentMaskForComponents())
                    SchedulePreviewUpdate()
                End If
                Me.RaisePropertyChanged(NameOf(GradientRadiusRatio))
            End Set
        End Property

        Public Property GradientInverted As Boolean
            Get
                Dim c = ActiveMaskComponent()
                Return If(c IsNot Nothing, c.Inverted, _gradientInverted)
            End Get
            Set(value As Boolean)
                _gradientInverted = value
                Dim c = ActiveMaskComponent()
                If c IsNot Nothing AndAlso c.Inverted <> value Then
                    c.Inverted = value
                    CommitActiveMaskComponent(c)
                    ' Maske des AKTIVEN Bestandteils, siehe GradientFeatherPercent.
                    PublishGradientOverlay(CurrentMaskForComponents())
                    SchedulePreviewUpdate()
                End If
                Me.RaisePropertyChanged(NameOf(GradientInverted))
            End Set
        End Property

        ''' <summary>Winkel der Verlaufsachse in Grad, gerechnet in PIXELN (nicht in Prozent, sonst
        ''' verzerrt das Seitenverhältnis die Anzeige). Schreibbar: der Verlauf dreht sich dann um
        ''' seinen Startpunkt, die Länge bleibt.
        ''' Bezug ist die Maske des AKTIVEN Bestandteils, nicht SelectedGradientMask: die verlangt
        ''' einen Verlauf als ERSTEN Bestandteil, und bei einem Verlauf an einer gemalten Ebenenmaske
        ''' zeigte das Feld deshalb 0 und der Setter tat nichts.</summary>
        Public Property GradientAngleDegrees As Double
            Get
                Dim m = CurrentMaskForComponents()
                Dim c = ActiveMaskComponent()
                If m Is Nothing OrElse c Is Nothing OrElse Not c.IsGradient Then Return 0
                Dim dx = (c.GradientEndXPercent - c.GradientStartXPercent) / 100.0 * m.SourceWidthPixels
                Dim dy = (c.GradientEndYPercent - c.GradientStartYPercent) / 100.0 * m.SourceHeightPixels
                Dim grad = Math.Atan2(dy, dx) * 180.0 / Math.PI
                If grad < 0 Then grad += 360.0
                Return Math.Round(grad)
            End Get
            Set(value As Double)
                Dim m = CurrentMaskForComponents()
                Dim c = ActiveMaskComponent()
                If m Is Nothing OrElse c Is Nothing OrElse Not c.IsGradient Then Return
                If m.SourceWidthPixels <= 0 OrElse m.SourceHeightPixels <= 0 Then Return
                Dim dx = (c.GradientEndXPercent - c.GradientStartXPercent) / 100.0 * m.SourceWidthPixels
                Dim dy = (c.GradientEndYPercent - c.GradientStartYPercent) / 100.0 * m.SourceHeightPixels
                Dim laenge = Math.Sqrt(dx * dx + dy * dy)
                If laenge < 0.0001 Then Return
                Dim rad = value * Math.PI / 180.0
                c.GradientEndXPercent = c.GradientStartXPercent + Math.Cos(rad) * laenge / m.SourceWidthPixels * 100.0
                c.GradientEndYPercent = c.GradientStartYPercent + Math.Sin(rad) * laenge / m.SourceHeightPixels * 100.0
                CommitActiveMaskComponent(c)
                PublishGradientOverlay(m)
                SchedulePreviewUpdate()
                Me.RaisePropertyChanged(NameOf(GradientAngleDegrees))
            End Set
        End Property

        Private Sub RaiseGradientPropertiesChanged()
            Me.RaisePropertyChanged(NameOf(SelectedGradientMask))
            Me.RaisePropertyChanged(NameOf(HasSelectedGradientMask))
            Me.RaisePropertyChanged(NameOf(ShowGradientControls))
            Me.RaisePropertyChanged(NameOf(IsRefiningGradientMask))
            Me.RaisePropertyChanged(NameOf(ShowRadialRatioControl))
            Me.RaisePropertyChanged(NameOf(GradientFeatherPercent))
            Me.RaisePropertyChanged(NameOf(GradientRadiusRatio))
            Me.RaisePropertyChanged(NameOf(GradientInverted))
            Me.RaisePropertyChanged(NameOf(GradientAngleDegrees))
            Me.RaisePropertyChanged(NameOf(GradientGeometry))
            RaiseMaskComponentsChanged()
            RaiseMaskActionStateChanged()
        End Sub

        ''' <summary>Liegt gerade ein VERLAUF zum Anfassen vor? Gefragt wird der aktive Bestandteil
        ''' und nicht die Maske: seit eine Maske aus mehreren Bestandteilen bestehen kann, ist ein
        ''' Verlauf an einer gemalten Ebenenmaske der Normalfall - und für den ist die Antwort ja.</summary>
        Public ReadOnly Property HasSelectedGradientMask As Boolean
            Get
                Dim c = ActiveMaskComponent()
                Return c IsNot Nothing AndAlso c.IsGradient
            End Get
        End Property

        Public ReadOnly Property ShowRadialRatioControl As Boolean
            Get
                Dim c = ActiveMaskComponent()
                If c IsNot Nothing Then Return c.IsRadialGradient
                Return IsMaskRadialMode
            End Get
        End Property

        ''' <summary>Die Achse des markierten Verlaufs in ANZEIGE-Prozent - alles, was das Overlay für
        ''' seine Griffe braucht. Nothing, wenn gerade kein Verlauf markiert ist.</summary>
        Public ReadOnly Property GradientGeometry As Double()
            Get
                Dim c = ActiveMaskComponent()
                If c Is Nothing OrElse Not c.IsGradient Then Return Nothing
                Dim a = SourcePercentToDisplayPercent(c.GradientStartXPercent, c.GradientStartYPercent)
                Dim b = SourcePercentToDisplayPercent(c.GradientEndXPercent, c.GradientEndYPercent)
                If Not a.HasValue OrElse Not b.HasValue Then Return Nothing
                Return New Double() {a.Value.X, a.Value.Y, b.Value.X, b.Value.Y,
                                     c.GradientRadiusRatio, c.GradientFeatherPercent,
                                     If(c.IsRadialGradient, 1.0, 0.0), If(c.Inverted, 1.0, 0.0)}
            End Get
        End Property

        ''' <summary>Zeichnet die Deckung des Verlaufs als rotes Overlay - dasselbe Bild, das auch der
        ''' Masken-Pinsel benutzt. Über Skia-Verläufe statt pro Pixel: die Vorschau ist ein Bruchteil
        ''' der Bildgrösse, ein eigener Rechenweg wäre nur eine zweite Fehlerquelle.</summary>
        Private Sub PublishGradientOverlay(mask As ImageMask,
                                           Optional livePts As List(Of SKPoint) = Nothing,
                                           Optional eraseMode As Boolean = False)
            ' Im Graustufenblick zeigt AUCH ein Verlauf seine Deckung als Grauwerte - waere er davon
            ' ausgenommen, haenge der Blick daran, woher die Maske stammt. Waehrend eines Zuges
            ' bleibt es beim roten Overlay: dort geht es um die Griffe, nicht um die Deckung.
            If _isMaskGrayscaleView AndAlso (livePts Is Nothing OrElse livePts.Count = 0) Then
                SetSelectionMaskPreviewImage(BuildMaskGrayscaleBitmap())
                Return
            End If
            SetSelectionMaskPreviewImage(BuildGradientRedOverlayBitmap(mask, livePts, eraseMode))
        End Sub

        ''' <summary>Ein einmal projiziertes Raster samt der Quell-Lage, zu der es gehört. Bewegt sich
        ''' NUR die Lage, wird beim Abtasten verschoben statt neu projiziert.</summary>
        Private Class ProjectedMaskRaster
            Public Property Data As Byte()
            Public Property SourceLeft As Integer
            Public Property SourceTop As Integer
        End Class

        ''' Zwischenspeicher der projizierten Masken-Raster (Pinselkorrekturen und gemalte
        ''' Bestandteile im roten Overlay). Sitzungszustand, gehoert NICHT ins Rezept.
        Private ReadOnly _projectedMaskRasters As New Dictionary(Of String, ProjectedMaskRaster)()
        Private _projectedMaskScope As String = ""
        Private Const ProjectedMaskRasterSlots As Integer = 16

        ''' <summary>Der Zwischenspeicher gilt für EINE Overlay-Größe und EINE Geometrie. Ändert sich
        ''' eines von beidem, sind alle Einträge wertlos - dann lieber leeren als je Eintrag prüfen.</summary>
        Private Sub EnsureProjectedMaskScope(scope As String)
            If String.Equals(scope, _projectedMaskScope, StringComparison.Ordinal) Then Return
            _projectedMaskScope = scope
            _projectedMaskRasters.Clear()
        End Sub

        Private Function ProjectedMaskScopeKey(adj As ImageAdjustments, ow As Integer, oh As Integer,
                                               bw As Integer, bh As Integer) As String
            Return String.Join("|", ow, oh, bw, bh,
                               adj.RotationDegrees, adj.StraightenDegrees,
                               adj.FlipHorizontal, adj.FlipVertical,
                               adj.CropLeftPercent, adj.CropTopPercent,
                               adj.CropRightPercent, adj.CropBottomPercent)
        End Function

        ''' <summary>Projiziert ein Raster aus dem QUELLRAUM auf die Overlay-Größe, mit
        ''' Zwischenspeicher.
        '''
        ''' ZWISCHENSPEICHER, kein Luxus: die Projektion läuft über die GANZE Anzeigefläche mit einer
        ''' Matrixrechnung je Pixel - gemessen 454 ms bei 3 MP, und sie liefe mehrfach pro
        ''' Mausbewegung. Während eines Strichs oder eines Reglerzuges ändert sich das committete
        ''' Raster aber gar nicht.
        '''
        ''' Die LAGE steht bewusst NICHT im Schlüssel, nur die Größe: beim Verschieben der Maske
        ''' ändert sich allein der Ursprung, und dann wäre jeder Zwischenschritt ein Fehlschlag -
        ''' 218 ms je Mausbewegung. Ein reiner Versatz lässt sich im Anzeigeraum nachziehen (eine
        ''' affine Abbildung macht aus einer Verschiebung wieder eine Verschiebung), deshalb liefert
        ''' die Funktion den Versatz zurück und der Aufrufer tastet verschoben ab.</summary>
        Private Function ProjectMaskRasterCached(mask As ImageMask, pngBase64 As String,
                                                 srcLeft As Integer, srcTop As Integer,
                                                 srcRight As Integer, srcBottom As Integer,
                                                 adj As ImageAdjustments,
                                                 ow As Integer, oh As Integer, bw As Integer, bh As Integer,
                                                 ByRef offsetX As Integer, ByRef offsetY As Integer) As Byte()
            offsetX = 0 : offsetY = 0
            If mask Is Nothing OrElse String.IsNullOrWhiteSpace(pngBase64) Then Return Nothing
            If srcRight <= srcLeft OrElse srcBottom <= srcTop Then Return Nothing
            If mask.SourceWidthPixels <= 0 OrElse mask.SourceHeightPixels <= 0 Then Return Nothing

            Dim key = String.Join("|", ImageProcessor.MaskRasterFingerprint(pngBase64),
                                  srcRight - srcLeft, srcBottom - srcTop)
            Dim entry As ProjectedMaskRaster = Nothing
            If _projectedMaskRasters.TryGetValue(key, entry) AndAlso entry IsNot Nothing Then
                If entry.SourceLeft <> srcLeft OrElse entry.SourceTop <> srcTop Then
                    Dim before = SourcePercentToDisplayPercent(entry.SourceLeft * 100.0 / mask.SourceWidthPixels,
                                                               entry.SourceTop * 100.0 / mask.SourceHeightPixels)
                    Dim after = SourcePercentToDisplayPercent(srcLeft * 100.0 / mask.SourceWidthPixels,
                                                              srcTop * 100.0 / mask.SourceHeightPixels)
                    If before.HasValue AndAlso after.HasValue Then
                        offsetX = CInt(Math.Round((after.Value.X - before.Value.X) / 100.0 * ow))
                        offsetY = CInt(Math.Round((after.Value.Y - before.Value.Y) / 100.0 * oh))
                    End If
                End If
                Return entry.Data
            End If

            Dim data = ProjectSourceRasterToDisplay(mask, pngBase64, srcLeft, srcTop, srcRight, srcBottom,
                                                    adj, ow, oh, bw, bh)
            If data Is Nothing Then Return Nothing
            If _projectedMaskRasters.Count >= ProjectedMaskRasterSlots Then _projectedMaskRasters.Clear()
            _projectedMaskRasters(key) = New ProjectedMaskRaster With {
                .Data = data, .SourceLeft = srcLeft, .SourceTop = srcTop}
            Return data
        End Function

        ''' <summary>Rechnet die PINSELKORREKTUREN einer Maske ins rote Overlay ein. Ohne das zeigt
        ''' das Overlay nur die Geometrie - man malt und sieht nichts, obwohl das Bild sich ändert.
        '''
        ''' JEDER Bestandteil, nicht nur der erste: eine Korrektur am zweiten Verlauf wirkte im Bild,
        ''' blieb im Overlay aber unsichtbar. Die Korrekturen aller Träger werden dafür zu je einem
        ''' Hinzu- und Weg-Raster zusammengefasst (Maximum je Bildpunkt).
        '''
        ''' Die Korrekturraster liegen im QUELLRAUM, das Overlay im Anzeigeraum; die Projektion
        ''' übernimmt <see cref="ProjectMaskRasterCached"/>.</summary>
        Private Sub ApplyBrushCorrectionToOverlay(overlay As SKBitmap, mask As ImageMask,
                                                  displayWidth As Integer, displayHeight As Integer)
            If overlay Is Nothing OrElse mask Is Nothing Then Return
            If displayWidth <= 0 OrElse displayHeight <= 0 Then Return
            Dim carriers = mask.GetComponents().Where(Function(c) c IsNot Nothing AndAlso c.HasBrushCorrection).ToList()
            If carriers.Count = 0 Then Return
            Dim adj = BuildAdjustmentsFromFields()
            EnsureProjectedMaskScope(ProjectedMaskScopeKey(adj, overlay.Width, overlay.Height, displayWidth, displayHeight))

            Dim pixelCount = overlay.Width * overlay.Height
            Dim added As Byte() = Nothing, removed As Byte() = Nothing
            For Each c In carriers
                Dim dx As Integer, dy As Integer
                Dim addRaster = ProjectMaskRasterCached(mask, c.BrushAddPngBase64, c.BrushLeft, c.BrushTop,
                                                        c.BrushRight, c.BrushBottom, adj,
                                                        overlay.Width, overlay.Height, displayWidth, displayHeight, dx, dy)
                If addRaster IsNot Nothing Then
                    If added Is Nothing Then added = New Byte(pixelCount - 1) {}
                    AccumulateShiftedMaximum(added, addRaster, overlay.Width, overlay.Height, dx, dy)
                End If
                Dim removeRaster = ProjectMaskRasterCached(mask, c.BrushSubtractPngBase64, c.BrushLeft, c.BrushTop,
                                                           c.BrushRight, c.BrushBottom, adj,
                                                           overlay.Width, overlay.Height, displayWidth, displayHeight, dx, dy)
                If removeRaster IsNot Nothing Then
                    If removed Is Nothing Then removed = New Byte(pixelCount - 1) {}
                    AccumulateShiftedMaximum(removed, removeRaster, overlay.Width, overlay.Height, dx, dy)
                End If
            Next
            If added Is Nothing AndAlso removed Is Nothing Then Return

            Dim stride = overlay.RowBytes
            Dim buffer = New Byte(stride * overlay.Height - 1) {}
            Runtime.InteropServices.Marshal.Copy(overlay.GetPixels(), buffer, 0, buffer.Length)
            ' Das Overlay ist premultipliziertes BGRA in reinem Rot - Deckung steht im Alphakanal,
            ' und Rot muss mitgezogen werden, sonst leuchtet ein aufgehellter Bereich falsch.
            For y = 0 To overlay.Height - 1
                Dim row = y * stride, iRow = y * overlay.Width
                For x = 0 To overlay.Width - 1
                    Dim a = CInt(buffer(row + x * 4 + 3))
                    Dim i = iRow + x
                    If added IsNot Nothing Then a += CInt(added(i)) * 128 \ 255
                    If removed IsNot Nothing Then a -= CInt(removed(i)) * 128 \ 255
                    a = Math.Max(0, Math.Min(128, a))
                    Dim o = row + x * 4
                    buffer(o) = 0
                    buffer(o + 1) = 0
                    buffer(o + 2) = CByte(a)   ' premultipliziertes Rot = Alpha
                    buffer(o + 3) = CByte(a)
                Next
            Next
            Runtime.InteropServices.Marshal.Copy(buffer, 0, overlay.GetPixels(), buffer.Length)
        End Sub

        ''' <summary>Legt ein verschoben abgetastetes Raster mit dem MAXIMUM auf ein Sammelraster -
        ''' zweimal dieselbe Stelle sieht aus wie einmal, dieselbe Regel wie beim Zusammensetzen der
        ''' Bestandteile.</summary>
        Private Shared Sub AccumulateShiftedMaximum(target As Byte(), source As Byte(),
                                                    width As Integer, height As Integer,
                                                    offsetX As Integer, offsetY As Integer)
            If target Is Nothing OrElse source Is Nothing Then Return
            For y = 0 To height - 1
                Dim qy = y - offsetY
                If qy < 0 OrElse qy >= height Then Continue For
                Dim zRow = y * width, qRow = qy * width
                For x = 0 To width - 1
                    Dim qx = x - offsetX
                    If qx < 0 OrElse qx >= width Then Continue For
                    If source(qRow + qx) > target(zRow + x) Then target(zRow + x) = source(qRow + qx)
                Next
            Next
        End Sub

        ''' <summary>Ein Raster aus dem Quellraum auf die Overlay-Größe bringen. Nothing, wenn es
        ''' leer ist oder sich nicht projizieren lässt.
        '''
        ''' Statt die Projektion hier ein zweites Mal zu schreiben, wird das Raster kurz als GEMALTE
        ''' Maske verpackt und durch <see cref="ImageProcessor.BuildSelectionMaskFromLayerMask"/>
        ''' geschickt - denselben Weg, über den eine Ebenenmaske zum Bearbeiten in den Anzeigeraum
        ''' kommt. Zuschnitt, Drehung und Begradigung stimmen damit automatisch.</summary>
        Private Function ProjectSourceRasterToDisplay(mask As ImageMask, pngBase64 As String,
                                                      srcLeft As Integer, srcTop As Integer,
                                                      srcRight As Integer, srcBottom As Integer,
                                                      adj As ImageAdjustments,
                                                      overlayWidth As Integer, overlayHeight As Integer,
                                                      displayWidth As Integer, displayHeight As Integer) As Byte()
            If String.IsNullOrWhiteSpace(pngBase64) Then Return Nothing
            Dim helperMask = New ImageMask With {
                .SourceWidthPixels = mask.SourceWidthPixels, .SourceHeightPixels = mask.SourceHeightPixels,
                .Left = srcLeft, .Top = srcTop,
                .Right = srcRight, .Bottom = srcBottom,
                .PngBase64 = pngBase64
            }
            Dim rectPx As SKRectI
            Using imDisplay = ImageProcessor.BuildSelectionMaskFromLayerMask(helperMask, adj, rectPx)
                If imDisplay Is Nothing OrElse rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return Nothing
                Dim result = New Byte(overlayWidth * overlayHeight - 1) {}
                Dim sx = displayWidth / CDbl(overlayWidth)
                Dim sy = displayHeight / CDbl(overlayHeight)
                Dim stride = imDisplay.RowBytes
                Dim source = New Byte(stride * imDisplay.Height - 1) {}
                Runtime.InteropServices.Marshal.Copy(imDisplay.GetPixels(), source, 0, source.Length)
                For y = 0 To overlayHeight - 1
                    Dim dy = CInt(Math.Floor((y + 0.5) * sy)) - rectPx.Top
                    If dy < 0 OrElse dy >= imDisplay.Height Then Continue For
                    Dim zRow = y * overlayWidth, qRow = dy * stride
                    For x = 0 To overlayWidth - 1
                        Dim dx = CInt(Math.Floor((x + 0.5) * sx)) - rectPx.Left
                        If dx < 0 OrElse dx >= imDisplay.Width Then Continue For
                        result(zRow + x) = source(qRow + dx)
                    Next
                Next
                Return result
            End Using
        End Function

        ''' <summary>Die Deckung EINES Verlaufs-Bestandteils als Byte je Overlay-Punkt (0 bis 255).
        ''' Dieselbe Rechnung, die der Renderer je Bildpunkt macht - hier einmal als Farbverlauf mit
        ''' Matrix.
        '''
        ''' Geliefert wird die reine Deckung und nicht schon das fertige Rot: der Modus des
        ''' Bestandteils (hinzufügen, abziehen, schneiden) wird erst beim Zusammensetzen angewendet,
        ''' und zwar mit denselben Regeln wie im Renderweg. Über Mischmodi des Canvas ging das nicht
        ''' mehr, seit auch GEMALTE Bestandteile in derselben Reihe stehen.</summary>
        Private Function BuildGradientComponentCoverage(component As MaskComponent,
                                                        ow As Integer, oh As Integer) As Byte()
            If component Is Nothing OrElse Not component.IsGradient Then Return Nothing
            Dim a = SourcePercentToDisplayPercent(component.GradientStartXPercent, component.GradientStartYPercent)
            Dim b = SourcePercentToDisplayPercent(component.GradientEndXPercent, component.GradientEndYPercent)
            If Not a.HasValue OrElse Not b.HasValue Then Return Nothing
            Dim p0 = New SKPoint(CSng(a.Value.X / 100.0 * ow), CSng(a.Value.Y / 100.0 * oh))
            Dim p1 = New SKPoint(CSng(b.Value.X / 100.0 * ow), CSng(b.Value.Y / 100.0 * oh))
            Dim dx = p1.X - p0.X, dy = p1.Y - p0.Y
            Dim radius = CSng(Math.Sqrt(dx * dx + dy * dy))
            If radius < 0.5 Then Return Nothing

            ' Smoothstep in fünf Stützstellen nachbilden - eine reine Zweipunkt-Rampe zeigt an ihren
            ' Enden dieselben Kanten, die der Renderer bewusst vermeidet.
            Dim steps = New Single() {0.0F, 0.25F, 0.5F, 0.75F, 1.0F}
            Dim colors(steps.Length - 1) As SKColor
            For i = 0 To steps.Length - 1
                Dim t = steps(i)
                Dim s = t * t * (3.0 - 2.0 * t)
                colors(i) = New SKColor(255, 255, 255, CByte(Math.Round((1.0 - s) * 255.0)))
            Next
            If component.Inverted Then Array.Reverse(colors)

            Using surface = New SKBitmap(ow, oh, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(surface)
                    canvas.Clear(SKColors.Transparent)
                    Using paint = New SKPaint With {.Style = SKPaintStyle.Fill, .IsAntialias = True}
                        If component.IsRadialGradient Then
                            Dim inner = CSng(Math.Max(0.0, Math.Min(0.98, 1.0 - component.GradientFeatherPercent / 100.0)))
                            Dim pos(steps.Length - 1) As Single
                            For i = 0 To steps.Length - 1
                                pos(i) = inner + (1.0F - inner) * steps(i)
                            Next
                            ' Die Ellipse entsteht durch Drehen und Stauchen des Kreises - genau die
                            ' Umrechnung, die der Renderer pro Pixel macht, hier einmal als Matrix.
                            Dim angle = CSng(Math.Atan2(dy, dx) * 180.0 / Math.PI)
                            Dim m = SKMatrix.CreateScale(1.0F, CSng(Math.Max(0.05, component.GradientRadiusRatio)), p0.X, p0.Y)
                            m = m.PostConcat(SKMatrix.CreateRotationDegrees(angle, p0.X, p0.Y))
                            paint.Shader = SKShader.CreateRadialGradient(p0, radius, colors, pos, SKShaderTileMode.Clamp, m)
                        Else
                            Dim width = CSng(Math.Max(0.02, Math.Min(1.0, component.GradientFeatherPercent / 100.0)))
                            Dim pos(steps.Length - 1) As Single
                            For i = 0 To steps.Length - 1
                                pos(i) = 0.5F + (steps(i) - 0.5F) * width
                            Next
                            paint.Shader = SKShader.CreateLinearGradient(p0, p1, colors, pos, SKShaderTileMode.Clamp)
                        End If
                        canvas.DrawRect(New SKRect(0, 0, ow, oh), paint)
                        paint.Shader?.Dispose()
                    End Using
                End Using
                Dim stride = surface.RowBytes
                Dim buffer = New Byte(stride * oh - 1) {}
                Runtime.InteropServices.Marshal.Copy(surface.GetPixels(), buffer, 0, buffer.Length)
                Dim result = New Byte(ow * oh - 1) {}
                For y = 0 To oh - 1
                    Dim row = y * stride, targetRow = y * ow
                    For x = 0 To ow - 1
                        result(targetRow + x) = buffer(row + x * 4 + 3)
                    Next
                Next
                Return result
            End Using
        End Function

        ''' <summary>Verrechnet einen Bestandteil auf das bisherige Ergebnis - dieselben drei Regeln
        ''' wie im Renderweg: Hinzufügen nimmt das Maximum, Abziehen die Differenz, Schneiden das
        ''' Minimum.</summary>
        Private Shared Sub CombineCoverageInto(target As Byte(), source As Byte(), mode As String)
            If target Is Nothing OrElse source Is Nothing OrElse target.Length <> source.Length Then Return
            Dim normalized = If(mode, "").Trim().ToLowerInvariant()
            For i = 0 To target.Length - 1
                Dim a = CInt(target(i)), b = CInt(source(i))
                Dim v As Integer
                Select Case normalized
                    Case "subtract"
                        v = a - b
                    Case "intersect"
                        v = Math.Min(a, b)
                    Case Else
                        v = Math.Max(a, b)
                End Select
                If v < 0 Then v = 0
                If v > 255 Then v = 255
                target(i) = CByte(v)
            Next
        End Sub

        Private Function BuildGradientRedOverlayBitmap(mask As ImageMask,
                                                       Optional livePts As List(Of SKPoint) = Nothing,
                                                       Optional eraseMode As Boolean = False) As Bitmap
            If mask Is Nothing Then Return Nothing
            Dim displaySize = GetAnnotationDisplayPixelSize()
            Dim bw = displaySize.Width, bh = displaySize.Height
            If bw <= 0 OrElse bh <= 0 Then Return Nothing

            Dim ovScale = Math.Min(1.0, MaskBrushOverlayMaxEdge / CDbl(Math.Max(bw, bh)))
            Dim ow = Math.Max(1, CInt(Math.Round(bw * ovScale)))
            Dim oh = Math.Max(1, CInt(Math.Round(bh * ovScale)))

            ' ALLE Bestandteile in ihrer Reihenfolge, gemalte wie gerechnete. Vorher las diese Stelle
            ' die Verlaufsfelder der MASKE - also den ersten Bestandteil -, und ein zweiter, per Plus
            ' angehaengter Verlauf blieb ohne Rot (Nutzerbefund 2026-08-04). Danach zeichnete sie zwar
            ' alle VERLAEUFE, aber keinen gemalten Anteil: an einer Maske aus gemalt plus Verlauf
            ' verschwand der gemalte Teil, solange man an einem Regler drehte.
            '
            ' Zusammengesetzt wird wie im Renderweg: der erste SICHTBARE Bestandteil setzt das
            ' Ergebnis, jeder weitere kommt mit seinem Modus hinzu.
            Dim components = mask.GetComponents().Where(Function(c) c IsNot Nothing AndAlso c.IsVisible).ToList()
            If components.Count = 0 Then Return Nothing

            Dim adj = BuildAdjustmentsFromFields()
            EnsureProjectedMaskScope(ProjectedMaskScopeKey(adj, ow, oh, bw, bh))
            Dim coverage As Byte() = Nothing
            For Each component In components
                Dim part As Byte() = Nothing
                If component.IsGradient Then
                    part = BuildGradientComponentCoverage(component, ow, oh)
                ElseIf Not String.IsNullOrWhiteSpace(component.PngBase64) Then
                    Dim dx As Integer, dy As Integer
                    Dim projected = ProjectMaskRasterCached(mask, component.PngBase64,
                                                            component.Left, component.Top,
                                                            component.Right, component.Bottom,
                                                            adj, ow, oh, bw, bh, dx, dy)
                    If projected IsNot Nothing Then
                        part = New Byte(ow * oh - 1) {}
                        AccumulateShiftedMaximum(part, projected, ow, oh, dx, dy)
                        If component.Inverted Then
                            For i = 0 To part.Length - 1
                                part(i) = CByte(255 - CInt(part(i)))
                            Next
                        End If
                    End If
                End If
                If part Is Nothing Then Continue For
                If coverage Is Nothing Then
                    coverage = part
                Else
                    CombineCoverageInto(coverage, part, component.Mode)
                End If
            Next
            If coverage Is Nothing Then Return Nothing
            ' Umkehrung des Ergebnisses, wie im Renderweg nach allen Bestandteilen.
            If mask.InvertResult Then
                For i = 0 To coverage.Length - 1
                    coverage(i) = CByte(255 - CInt(coverage(i)))
                Next
            End If

            Using overlay = New SKBitmap(ow, oh, SKColorType.Bgra8888, SKAlphaType.Premul)
                Dim stride = overlay.RowBytes
                Dim puffer = New Byte(stride * oh - 1) {}
                ' Premultipliziertes BGRA in reinem Rot: Deckung steht im Alphakanal, und Rot muss
                ' denselben Wert tragen. Halbe Staerke, damit das Bild darunter sichtbar bleibt.
                For y = 0 To oh - 1
                    Dim row = y * stride, qRow = y * ow
                    For x = 0 To ow - 1
                        Dim a = CByte(CInt(coverage(qRow + x)) * 128 \ 255)
                        Dim o = row + x * 4
                        puffer(o + 2) = a
                        puffer(o + 3) = a
                    Next
                Next
                Runtime.InteropServices.Marshal.Copy(puffer, 0, overlay.GetPixels(), puffer.Length)
                ApplyBrushCorrectionToOverlay(overlay, mask, bw, bh)
                ' Laufender Strich obendrauf - sonst verschwaende das Verlaufs-Overlay waehrend des
                ' Ziehens und kaeme erst beim Loslassen zurueck.
                If livePts IsNot Nothing AndAlso livePts.Count > 0 Then
                    Using canvas = New SKCanvas(overlay)
                        Dim scaled As New List(Of SKPoint)(livePts.Count)
                        For Each p In livePts
                            scaled.Add(New SKPoint(CSng(p.X * ovScale), CSng(p.Y * ovScale)))
                        Next
                        ImageProcessor.DrawSoftMaskStroke(canvas, scaled,
                                                          CSng(Math.Max(0.5, MaskBrushRadiusDisplay() * ovScale)),
                                                          CSng(Math.Max(0.0, _selectionFeather) * ovScale),
                                                          New SKColor(255, 0, 0, 128), eraseMode)
                    End Using
                End If
                Return ImageProcessor.ToAvaloniaBitmap(overlay)
            End Using
        End Function

        ' --- Griffe eines bestehenden Verlaufs -------------------------------------------
        ' 0 = Startpunkt, 1 = Endpunkt, 2 = ganzer Verlauf verschieben. Ohne Griffe müsste man
        ' jeden Verlauf neu ziehen, statt ihn zu korrigieren - das ist der eigentliche Gewinn
        ' gegenüber einer eingebrannten Maske.
        Private _gradientHandle As Integer = -1
        Private _gradientMoveRefX As Double
        Private _gradientMoveRefY As Double
        Private _gradientMoveStart As SKPoint
        Private _gradientMoveEnd As SKPoint

        ''' <summary>Prüft, ob der Druckpunkt (Anzeige-Prozent) einen Griff des markierten Verlaufs
        ''' trifft, und beginnt dann dessen Zug. False = kein Griff, der Aufrufer zieht einen neuen
        ''' Verlauf auf.</summary>
        Public Function TryBeginGradientHandleDrag(xPercent As Double, yPercent As Double,
                                                   slopXPercent As Double, slopYPercent As Double) As Boolean
            ' NICHT ueber SelectedGradientMask: die verlangt einen Verlauf als ERSTEN Bestandteil.
            ' Ein Verlauf, der an eine gemalte Ebenenmaske angehaengt wurde, faende seine Griffe
            ' sonst nie - und weil der Griff-Versuch scheitert, zoege jeder Klick darauf einen
            ' weiteren Verlauf auf.
            Dim mask = CurrentMaskForComponents()
            If mask Is Nothing Then Return False
            Dim component = ActiveMaskComponent()
            If component Is Nothing OrElse Not component.IsGradient Then Return False
            Dim geo = GradientGeometry
            If geo Is Nothing Then Return False
            Dim handle = -1
            If Math.Abs(xPercent - geo(2)) <= slopXPercent AndAlso Math.Abs(yPercent - geo(3)) <= slopYPercent Then
                handle = 1
            ElseIf Math.Abs(xPercent - geo(0)) <= slopXPercent AndAlso Math.Abs(yPercent - geo(1)) <= slopYPercent Then
                ' Der Startpunkt gewinnt NICHT gegen den Endpunkt: bei einem ganz kurzen Verlauf
                ' liegen beide fast aufeinander, und der Endpunkt ist der, den man dann meint.
                handle = 0
            ElseIf IsOnSqueezeHandle(geo, xPercent, yPercent, slopXPercent, slopYPercent) Then
                ' VOR der inneren Ellipse pruefen: der Griff liegt auf der aeusseren, und bei
                ' schmalem Uebergang liegen beide dicht beieinander - dann meint man den Griff.
                handle = 4
            ElseIf IsOnInnerEllipse(geo, xPercent, yPercent, slopXPercent, slopYPercent) Then
                handle = 3
            ElseIf IsOnTransitionLine(geo, xPercent, yPercent, slopXPercent, slopYPercent) Then
                ' VOR der Achse pruefen: die Striche liegen auf ihr, sonst gewaenne immer das
                ' Verschieben des ganzen Verlaufs und der Uebergang waere nie greifbar.
                handle = 3
            ElseIf IsOnGradientAxis(geo, xPercent, yPercent, slopXPercent, slopYPercent) Then
                handle = 2
            ElseIf IsInGradientArea(geo, xPercent, yPercent, slopXPercent, slopYPercent) Then
                ' Innerhalb der Maske ziehen VERSCHIEBT sie. Vorher legte jeder Zug daneben einen
                ' neuen Verlauf an - der bestehende war dann nur noch ueber die Achse zu fassen,
                ' einen Strich von wenigen Pixeln Breite.
                handle = 2
            End If
            If handle < 0 Then Return False
            PushUndo()
            _gradientDragMaskId = mask.Id
            _gradientDragActive = True
            _gradientHandle = handle
            _gradientMoveRefX = xPercent
            _gradientMoveRefY = yPercent
            ' Die Greiftoleranz ist zugleich der MASSSTAB der Ellipsenrechnung - das Ziehen muss
            ' denselben benutzen wie der Treffertest, sonst springt der Wert beim Anfassen.
            _gradientSlopX = slopXPercent
            _gradientSlopY = slopYPercent
            ' Die Startwerte kommen aus dem AKTIVEN Bestandteil, nicht aus der Maske: sonst zoege man
            ' am zweiten Verlauf und rechnete gegen die Lage des ersten.
            _gradientMoveBrush = New SKRectI(component.BrushLeft, component.BrushTop,
                                             component.BrushRight, component.BrushBottom)
            _gradientMoveStart = New SKPoint(CSng(component.GradientStartXPercent), CSng(component.GradientStartYPercent))
            _gradientMoveEnd = New SKPoint(CSng(component.GradientEndXPercent), CSng(component.GradientEndYPercent))
            Return True
        End Function




        ''' <summary>Liegt der Punkt INNERHALB der Maske? Beim radialen Verlauf heisst das: in der
        ''' aeusseren Ellipse. Beim linearen gibt es keine geschlossene Flaeche - er reicht ueber
        ''' das ganze Bild -, deshalb gilt dort das BAND zwischen den beiden Uebergangsstrichen:
        ''' das ist der Bereich, den das Overlay als "der Verlauf" zeigt. Wer weiter draussen zieht,
        ''' meint einen neuen Verlauf, nicht diesen.</summary>
        Private Shared Function IsInGradientArea(geo As Double(), xPercent As Double, yPercent As Double,
                                                     slopXPercent As Double, slopYPercent As Double) As Boolean
            If geo Is Nothing OrElse geo.Length < 7 Then Return False
            If geo(6) > 0.5 Then
                Dim laenge As Double
                Dim r = EllipsenRadiusNormiert(geo, xPercent, yPercent, slopXPercent, slopYPercent, laenge)
                Return r >= 0.0 AndAlso r <= 1.0
            End If
            Dim ax = geo(0), ay = geo(1), bx = geo(2), by = geo(3)
            Dim dx = bx - ax, dy = by - ay
            Dim len2 = dx * dx + dy * dy
            If len2 < 0.000001 Then Return False
            Dim mx = (ax + bx) / 2.0, my = (ay + by) / 2.0
            Dim t = ((xPercent - mx) * dx + (yPercent - my) * dy) / len2
            Return Math.Abs(t) <= Math.Max(0.02, Math.Min(100.0, geo(5)) / 200.0)
        End Function
        ''' <summary>Normierter Ellipsenradius des Punktes: 1 = auf der Aussenkante, 0 = im
        ''' Mittelpunkt. Gerechnet wird in SLOP-EINHEITEN (beide Achsen durch ihre eigene Toleranz
        ''' geteilt) - Prozent auf einer 3:2-Kante sind in X und Y verschieden lang, und eine
        ''' Ellipsenrechnung in solchen Koordinaten waere schief. Eine Laengeneinheit entspricht
        ''' danach genau der Greiftoleranz.</summary>
        Private Shared Function EllipsenRadiusNormiert(geo As Double(), xPercent As Double, yPercent As Double,
                                                       slopXPercent As Double, slopYPercent As Double,
                                                       ByRef laengeSkaliert As Double) As Double
            laengeSkaliert = 0.0
            If slopXPercent <= 0.0 OrElse slopYPercent <= 0.0 Then Return -1.0
            Dim ax = geo(0) / slopXPercent, ay = geo(1) / slopYPercent
            Dim bx = geo(2) / slopXPercent, by = geo(3) / slopYPercent
            Dim px = xPercent / slopXPercent, py = yPercent / slopYPercent
            Dim dx = bx - ax, dy = by - ay
            Dim laenge = Math.Sqrt(dx * dx + dy * dy)
            If laenge < 0.0001 Then Return -1.0
            laengeSkaliert = laenge
            Dim ex = dx / laenge, ey = dy / laenge
            Dim vx = px - ax, vy = py - ay
            Dim u = vx * ex + vy * ey
            Dim v = -vx * ey + vy * ex
            Dim halbNeben = Math.Max(0.05, geo(4)) * laenge
            Return Math.Sqrt((u / laenge) * (u / laenge) + (v / halbNeben) * (v / halbNeben))
        End Function

        ''' <summary>Der Griff fuer die STAUCHUNG: er sitzt auf der Ellipse quer zur Achse, dort wo
        ''' die zweite Halbachse endet. Rueckgabe in Prozentkoordinaten, Nothing wenn es ihn nicht
        ''' gibt (kein radialer Verlauf oder Achse zu kurz).
        '''
        ''' Gerechnet wird im NORMIERTEN Raum: Prozent-x und Prozent-y beziehen sich auf
        ''' verschiedene Pixelmasse, ein "senkrecht" in Prozent waere im Bild schief. Denselben
        ''' Massstab benutzen Trefferpruefung und Zeichnung, sonst springt der Griff beim
        ''' Anfassen.</summary>
        Friend Shared Function SqueezeHandle(geo As Double(), slopXPercent As Double, slopYPercent As Double) _
                                               As Double()
            If geo Is Nothing OrElse geo.Length < 7 OrElse geo(6) <= 0.5 Then Return Nothing
            If slopXPercent <= 0.0 OrElse slopYPercent <= 0.0 Then Return Nothing
            Dim ax = geo(0) / slopXPercent, ay = geo(1) / slopYPercent
            Dim bx = geo(2) / slopXPercent, by = geo(3) / slopYPercent
            Dim dx = bx - ax, dy = by - ay
            Dim laenge = Math.Sqrt(dx * dx + dy * dy)
            If laenge < 0.0001 Then Return Nothing
            Dim ex = dx / laenge, ey = dy / laenge
            ' Senkrecht zur Achse, Laenge gleich der zweiten Halbachse.
            Dim halbNeben = Math.Max(0.05, geo(4)) * laenge
            Dim px = ax - ey * halbNeben
            Dim py = ay + ex * halbNeben
            Return New Double() {px * slopXPercent, py * slopYPercent}
        End Function

        Private Shared Function IsOnSqueezeHandle(geo As Double(), xPercent As Double, yPercent As Double,
                                                      slopXPercent As Double, slopYPercent As Double) As Boolean
            Dim g = SqueezeHandle(geo, slopXPercent, slopYPercent)
            If g Is Nothing Then Return False
            Return Math.Abs(xPercent - g(0)) <= slopXPercent AndAlso Math.Abs(yPercent - g(1)) <= slopYPercent
        End Function

        ''' <summary>Die Stauchung, die sich aus einer Zeigerposition ergibt: der Abstand QUER zur
        ''' Achse, gemessen in Vielfachen der Achsenlaenge. Geklemmt auf denselben Bereich wie der
        ''' Regler - sonst liesse sich mit der Maus etwas einstellen, das der Regler nicht anzeigen
        ''' kann.</summary>
        Friend Shared Function SqueezeFromPointer(geo As Double(), xPercent As Double, yPercent As Double,
                                                  slopXPercent As Double, slopYPercent As Double) As Double
            If geo Is Nothing OrElse geo.Length < 7 Then Return -1.0
            If slopXPercent <= 0.0 OrElse slopYPercent <= 0.0 Then Return -1.0
            Dim ax = geo(0) / slopXPercent, ay = geo(1) / slopYPercent
            Dim bx = geo(2) / slopXPercent, by = geo(3) / slopYPercent
            Dim dx = bx - ax, dy = by - ay
            Dim laenge = Math.Sqrt(dx * dx + dy * dy)
            If laenge < 0.0001 Then Return -1.0
            Dim ex = dx / laenge, ey = dy / laenge
            Dim vx = xPercent / slopXPercent - ax, vy = yPercent / slopYPercent - ay
            Dim quer = Math.Abs(-vx * ey + vy * ex)
            Return Math.Max(0.05, Math.Min(4.0, quer / laenge))
        End Function

        ''' <summary>Liegt der Punkt auf der INNEREN Ellipse des radialen Verlaufs - der Grenze, ab
        ''' der die Deckung abfaellt? Sie sitzt beim normierten Radius (1 - Uebergang/100), genau
        ''' dort, wo das Overlay sie zeichnet. Unter 0,02 zeichnet das Overlay sie nicht mehr, dann
        ''' gibt es auch nichts zu greifen.</summary>
        Private Shared Function IsOnInnerEllipse(geo As Double(), xPercent As Double, yPercent As Double,
                                                     slopXPercent As Double, slopYPercent As Double) As Boolean
            If geo Is Nothing OrElse geo.Length < 7 OrElse geo(6) <= 0.5 Then Return False
            Dim inner = 1.0 - Math.Max(0.0, Math.Min(100.0, geo(5))) / 100.0
            If inner <= 0.02 Then Return False
            Dim laenge As Double
            Dim r = EllipsenRadiusNormiert(geo, xPercent, yPercent, slopXPercent, slopYPercent, laenge)
            If r < 0.0 Then Return False
            Return Math.Abs(r - inner) * laenge <= 1.0
        End Function
        ''' <summary>Liegt der Punkt auf einem der beiden Uebergangsstriche? Die sitzen auf der Achse
        ''' bei plusminus (Achsenlaenge mal Uebergang/200) um die Mitte - genau dort, wo sie das
        ''' Overlay zeichnet. NUR beim linearen Verlauf: der radiale zeigt statt der Striche eine
        ''' innere Ellipse, die eine eigene Trefferpruefung braeuchte.</summary>
        Private Shared Function IsOnTransitionLine(geo As Double(), xPercent As Double, yPercent As Double,
                                                       slopXPercent As Double, slopYPercent As Double) As Boolean
            If geo Is Nothing OrElse geo.Length < 7 OrElse geo(6) > 0.5 Then Return False
            Dim ax = geo(0), ay = geo(1), bx = geo(2), by = geo(3)
            Dim dx = bx - ax, dy = by - ay
            If dx * dx + dy * dy < 0.000001 Then Return False
            Dim mx = (ax + bx) / 2.0, my = (ay + by) / 2.0
            Dim share = Math.Max(0.0, Math.Min(1.0, geo(5) / 100.0)) / 2.0
            For Each vorzeichen In New Double() {-1.0, 1.0}
                Dim px = mx + dx * share * vorzeichen
                Dim py = my + dy * share * vorzeichen
                If Math.Abs(xPercent - px) <= slopXPercent AndAlso Math.Abs(yPercent - py) <= slopYPercent Then Return True
            Next
            Return False
        End Function
        ''' <summary>Liegt der Punkt auf der Verbindungslinie der beiden Griffe? Dann fasst man den
        ''' Verlauf als Ganzes an.</summary>
        Private Shared Function IsOnGradientAxis(geo As Double(), xPercent As Double, yPercent As Double,
                                                    slopXPercent As Double, slopYPercent As Double) As Boolean
            Dim ax = geo(0), ay = geo(1), bx = geo(2), by = geo(3)
            Dim dx = bx - ax, dy = by - ay
            Dim len2 = dx * dx + dy * dy
            If len2 < 0.000001 Then Return False
            Dim t = ((xPercent - ax) * dx + (yPercent - ay) * dy) / len2
            If t < 0.0 OrElse t > 1.0 Then Return False
            Dim px = ax + dx * t, py = ay + dy * t
            ' Die Toleranz ist in X und Y verschieden gross (Prozent auf verschiedenen Kanten),
            ' deshalb wird der Abstand vor dem Vergleich auf sie normiert.
            Dim nx = (xPercent - px) / Math.Max(0.0001, slopXPercent)
            Dim ny = (yPercent - py) / Math.Max(0.0001, slopYPercent)
            Return nx * nx + ny * ny <= 1.0
        End Function

        ''' <summary>Zieht den angefassten Griff. Ohne Griff (frisch aufgezogener Verlauf) wandert der
        ''' Endpunkt - das ist derselbe Weg wie beim Aufziehen.</summary>
        Public Sub UpdateGradientHandleDrag(xPercent As Double, yPercent As Double, rasten As Boolean)
            If Not _gradientDragActive Then Return
            If _gradientHandle < 0 Then
                UpdateGradientMaskDrag(xPercent, yPercent, rasten)
                Return
            End If
            Dim mask = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso m.Id = _gradientDragMaskId)
            If mask Is Nothing Then Return
            ' Der Zug bedient den AKTIVEN Bestandteil, nicht pauschal den ersten - sonst zoege
            ' man am zweiten Verlauf und der erste bewegte sich.
            Dim component = ActiveMaskComponent()
            If component Is Nothing Then Return
            If _gradientHandle = 4 Then
                ' Stauchung: der Abstand des Zeigers QUER zur Achse ist die zweite Halbachse.
                Dim geoS = GradientGeometry
                If geoS Is Nothing Then Return
                Dim neu = SqueezeFromPointer(geoS, xPercent, yPercent, _gradientSlopX, _gradientSlopY)
                If neu < 0.0 Then Return
                If rasten Then neu = Math.Round(neu * 20.0) / 20.0
                component.GradientRadiusRatio = neu
                _gradientRadiusRatio = neu
                Me.RaisePropertyChanged(NameOf(GradientRadiusRatio))
            ElseIf _gradientHandle = 3 AndAlso component.IsRadialGradient Then
                ' Innere Ellipse: der normierte Radius des Zeigers IST die innere Grenze. Umkehrung
                ' der Zeichnung (innen = 1 - Uebergang/100).
                Dim geoR = GradientGeometry
                If geoR Is Nothing Then Return
                Dim laengeR As Double
                Dim rR = EllipsenRadiusNormiert(geoR, xPercent, yPercent, _gradientSlopX, _gradientSlopY, laengeR)
                If rR < 0.0 Then Return
                component.GradientFeatherPercent = Math.Max(2.0, Math.Min(100.0, (1.0 - Math.Max(0.02, Math.Min(0.98, rR))) * 100.0))
                _gradientFeatherPercent = component.GradientFeatherPercent
                Me.RaisePropertyChanged(NameOf(GradientFeatherPercent))
            ElseIf _gradientHandle = 3 Then
                ' Uebergangsstrich: der Abstand des Zeigers von der Achsenmitte ENTLANG der Achse
                ' bestimmt die Breite. Umkehrung der Zeichnung (Strich bei Anteil Uebergang/200):
                ' Uebergang = Anteil mal 200. Verhaeltnisse entlang einer Geraden bleiben bei
                ' Drehung/Zuschnitt erhalten, deshalb darf das im ANZEIGERAUM gerechnet werden.
                Dim geo = GradientGeometry
                If geo Is Nothing Then Return
                Dim dx = geo(2) - geo(0), dy = geo(3) - geo(1)
                Dim len2 = dx * dx + dy * dy
                If len2 < 0.000001 Then Return
                Dim mx = (geo(0) + geo(2)) / 2.0, my = (geo(1) + geo(3)) / 2.0
                Dim t = ((xPercent - mx) * dx + (yPercent - my) * dy) / len2
                ' Untergrenze 2 statt 0: bei 0 faellt die Rampe auf eine harte Kante zusammen, und
                ' die beiden Striche lägen aufeinander - der Uebergang waere nicht mehr zu fassen.
                component.GradientFeatherPercent = Math.Max(2.0, Math.Min(100.0, Math.Abs(t) * 200.0))
                _gradientFeatherPercent = component.GradientFeatherPercent
                Me.RaisePropertyChanged(NameOf(GradientFeatherPercent))
            ElseIf _gradientHandle = 2 Then
                ' Ganzer Verlauf: der Versatz wird im ANZEIGERAUM gemessen und für beide Punkte
                ' einzeln in den Quellraum gerechnet - sonst liefe er bei gedrehtem Bild schief.
                Dim aNeu = DisplayOffsetToSource(_gradientMoveStart, xPercent - _gradientMoveRefX, yPercent - _gradientMoveRefY)
                Dim bNeu = DisplayOffsetToSource(_gradientMoveEnd, xPercent - _gradientMoveRefX, yPercent - _gradientMoveRefY)
                If Not aNeu.HasValue OrElse Not bNeu.HasValue Then Return
                component.GradientStartXPercent = aNeu.Value.X
                component.GradientStartYPercent = aNeu.Value.Y
                component.GradientEndXPercent = bNeu.Value.X
                component.GradientEndYPercent = bNeu.Value.Y
                ' Pinselkorrektur mitnehmen: derselbe Versatz in QUELLPIXELN. Ein Verschieben ist eine
                ' reine Verschiebung - das achsenparallele Rechteck bleibt achsenparallel, es muss also
                ' nichts umgerechnet werden ausser dem Ursprung.
                If _gradientMoveBrush.Right > _gradientMoveBrush.Left AndAlso component.HasBrushCorrection Then
                    Dim dxPx = CInt(Math.Round((aNeu.Value.X - _gradientMoveStart.X) / 100.0 * mask.SourceWidthPixels))
                    Dim dyPx = CInt(Math.Round((aNeu.Value.Y - _gradientMoveStart.Y) / 100.0 * mask.SourceHeightPixels))
                    component.BrushLeft = _gradientMoveBrush.Left + dxPx
                    component.BrushTop = _gradientMoveBrush.Top + dyPx
                    component.BrushRight = _gradientMoveBrush.Right + dxPx
                    component.BrushBottom = _gradientMoveBrush.Bottom + dyPx
                End If
            Else
                Dim source = DisplayPercentToSourcePercent(xPercent, yPercent)
                If Not source.HasValue Then Return
                If _gradientHandle = 0 Then
                    component.GradientStartXPercent = source.Value.X
                    component.GradientStartYPercent = source.Value.Y
                Else
                    component.GradientEndXPercent = source.Value.X
                    component.GradientEndYPercent = source.Value.Y
                End If
            End If
            CommitActiveMaskComponent(component)
            PublishGradientOverlay(mask)
            ' Beim Korrigieren traegt die Ebene schon eine Anpassung - ohne Neurechnung saehe man
            ' nur das rote Overlay wandern, nicht die Wirkung. Der Aufruf ist entprellt.
            SchedulePreviewUpdate()
            RaiseGradientPropertiesChanged()
        End Sub

        ''' <summary>Verschiebt einen Quellraum-Punkt um einen Versatz, der in ANZEIGE-Prozent
        ''' angegeben ist. Nothing, wenn das Ziel neben dem Bildinhalt landet.</summary>
        Private Function DisplayOffsetToSource(quellPunkt As SKPoint, dxPercent As Double, dyPercent As Double) As SKPoint?
            Dim anzeige = SourcePercentToDisplayPercent(quellPunkt.X, quellPunkt.Y)
            If Not anzeige.HasValue Then Return Nothing
            Return DisplayPercentToSourcePercent(anzeige.Value.X + dxPercent, anzeige.Value.Y + dyPercent)
        End Function

        ''' <summary>Beendet einen Griff-Zug. Anders als beim Aufziehen bleibt der Verlauf hier immer
        ''' erhalten - er bestand ja schon.</summary>
        Public Sub EndGradientHandleDrag()
            If Not _gradientDragActive Then Return
            If _gradientHandle < 0 Then
                EndGradientMaskDrag()
                Return
            End If
            _gradientDragActive = False
            _gradientHandle = -1
            _gradientDragMaskId = ""
            AddHistoryEntry("Verlauf geändert")
            SchedulePreviewUpdate()
            RaiseGradientPropertiesChanged()
        End Sub

        ''' <summary>Lädt die Maske einer Anpassungsebene in die editierbare Auswahlmaske (Anzeigeraum),
        ''' schaltet in den Masken-Pinsel und zeigt sie als rotes Overlay. Ab jetzt bearbeitet der Pinsel
        ''' die harte Form dieser Ebenen-Maske; die "Weiche Kante" steuert mask.FeatherPixels.</summary>
        Private Sub LoadLayerMaskIntoSelection(layer As MaskedAdjustmentLayer)
            If layer Is Nothing Then
                _editingLayerMaskId = ""
                InvalidateSelectionLayerLink()
                Return
            End If
            LoadMaskIntoSelection(layer.MaskId, layer.IsMaskLayer)
        End Sub

        ''' <summary>Derselbe Weg fuer die Ebenenmaske eines OBJEKTS: dort gibt es keine
        ''' Korrekturebene, nur die Maskenkennung. <paramref name="showAsMask"/> entscheidet ueber
        ''' rotes Overlay (Maske) oder Laufameisen (Auswahl).</summary>
        Private Sub LoadMaskIntoSelection(maskId As String, showAsMask As Boolean)
            TraceMask(Function() $"Maske wird zum Bearbeiten geöffnet: {MaskTrace(maskId)}" &
                                 $" (vorher bearbeitet={Kurz(_editingLayerMaskId)})")
            _editingLayerMaskId = ""
            ' Ein bewusster Wechsel auf eine andere Maske hebt auch das Merken der zuletzt
            ' bearbeiteten auf - sonst haengte ein Verlaufszug weiter an der vorigen.
            _workingMaskId = ""
            _activeMaskComponentIndex = -1
            InvalidateSelectionLayerLink()
            If String.IsNullOrEmpty(maskId) Then
                RaiseMaskComponentsChanged()
                Return
            End If
            Dim mask = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso m.Id = maskId)
            If mask Is Nothing Then
                RaiseMaskComponentsChanged()
                Return
            End If
            ' Bereichsmasken bleiben intern ein Alpha-Raster, tragen aber ihre Ursprungswerte.
            ' Beim erneuten Öffnen stehen damit wieder dieselben Regler bereit statt nur der
            ' Pinsel; die folgende Standardstrecke lädt zusätzlich die aktuelle Form als Overlay.
            If String.Equals(mask.RangeKind, "Color", StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(mask.RangeKind, "Luminance", StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(mask.RangeKind, "Depth", StringComparison.OrdinalIgnoreCase) Then
                _editingLayerMaskId = mask.Id
                ' NUR DIE REGLER DER TATSAECHLICHEN ART. Die Maske traegt EINEN Uebergangswert und
                ' EIN Wertepaar - sie in alle drei Werkzeuge zu schreiben hiess: eine Farbmaske mit
                ' Uebergang 80 (Bereich 1..100) landete auch im Luminanz- und im Tiefenuebergang,
                ' deren Regler bei 50 enden, und der dort zuvor gewaehlte Wert war weg.
                If String.Equals(mask.RangeKind, "Color", StringComparison.OrdinalIgnoreCase) Then
                    _colorRangeTolerance = Math.Max(0.0, Math.Min(100.0, mask.RangeTolerance))
                    _colorRangeFeather = Math.Max(1.0, Math.Min(100.0, mask.RangeFeather))
                    _colorRangeContiguous = mask.RangeContiguous
                ElseIf String.Equals(mask.RangeKind, "Luminance", StringComparison.OrdinalIgnoreCase) Then
                    _luminanceRangeFrom = Math.Max(0.0, Math.Min(100.0, mask.RangeFrom))
                    _luminanceRangeTo = Math.Max(0.0, Math.Min(100.0, mask.RangeTo))
                    _luminanceRangeFeather = Math.Max(0.0, Math.Min(50.0, mask.RangeFeather))
                Else
                    _depthFrom = Math.Max(0.0, Math.Min(100.0, mask.RangeFrom))
                    _depthTo = Math.Max(0.0, Math.Min(100.0, mask.RangeTo))
                    _depthFeather = Math.Max(0.0, Math.Min(50.0, mask.RangeFeather))
                End If
                For Each prop In {NameOf(ColorRangeTolerance), NameOf(ColorRangeFeather), NameOf(ColorRangeContiguous),
                                  NameOf(LuminanceRangeFrom), NameOf(LuminanceRangeTo), NameOf(LuminanceRangeFeather),
                                  NameOf(DepthFrom), NameOf(DepthTo), NameOf(DepthFeather)}
                    Me.RaisePropertyChanged(prop)
                Next
                MaskMode = If(String.Equals(mask.RangeKind, "Color", StringComparison.OrdinalIgnoreCase), "Farbe",
                              If(String.Equals(mask.RangeKind, "Depth", StringComparison.OrdinalIgnoreCase), "Tiefe", "Luminanz"))
            End If
            ' Ein Verlauf ist gerechnet, nicht gemalt: er wird NICHT in die Auswahlmaske geladen (das
            ' machte aus zwei Punkten ein PNG und nahm ihm die Aenderbarkeit). Eine noch laufende
            ' Pixelauswahl muss trotzdem weg, sonst laegen zwei Masken gleichzeitig auf dem Bild.
            If mask.IsGradient Then
                If _hasActiveSelection Then ClearSelection(captureUndo:=False)
                ' Sie ist trotzdem die BEARBEITETE Maske. Vorher stieg der Weg hier ohne alles aus:
                ' ohne Vermerk, ohne Rot, ohne Bestandteile - beim zweiten Oeffnen derselben
                ' Ebenenmaske sah man deshalb gar nichts mehr. Woran das Rot haengt, ist bei einer
                ' verlaufsbasierten Maske die Deckung des Verlaufs und nicht die Auswahlmaske.
                _editingLayerMaskId = mask.Id
                SetActiveSelectionIsMask(showAsMask)
                RaiseGradientPropertiesChanged()
                If showAsMask Then PublishGradientOverlay(mask)
                Return
            End If
            Dim adj = BuildAdjustmentsFromFields()
            Dim rectPx As SKRectI
            Dim bmp = ImageProcessor.BuildSelectionMaskFromLayerMask(mask, adj, rectPx)
            If bmp Is Nothing OrElse rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return
            ClearSelectionMask()
            SetSelectionBoundsFromPixels(rectPx)
            SetSelectionShape("MagicWand", Nothing, Nothing)
            SetSelectionMaskData(bmp, rectPx)
            _selectionMaskSoftBaked = False
            _selectionFeather = Math.Max(0, Math.Min(200, mask.FeatherPixels))
            Me.RaisePropertyChanged(NameOf(SelectionFeather))
            HasActiveSelection = True
            _editingLayerMaskId = mask.Id
            ' Overlay nach EBENEN-ART: Masken-Ebene → rotes Overlay, Auswahl-Ebene → Laufameisen. Kein
            ' Werkzeugwechsel; die Maske bleibt mit dem Masken-Pinsel editierbar (Auswahl-Werkzeug + Pinsel).
            SetActiveSelectionIsMask(showAsMask)
            ' SetActiveSelectionIsMask baut das Overlay NUR bei einem Artwechsel neu. Beim Wechsel von einer
            ' Masken-Ebene zur nächsten (beide True) bliebe sonst das ROT DER VORIGEN Maske stehen - es sähe
            ' aus, als färbe die neue Maske fremde Bereiche/Ebenen rot. Deshalb hier immer neu aufbauen.
            If showAsMask Then PublishSelectionRedOverlay()
            ' Und die Liste der Bestandteile gehoert ZU DIESER Maske. Sie wurde bisher nur von den
            ' Verlaufs- und Bestandteilwegen neu gebaut; wer eine Ebenenmaske einfach wieder oeffnete,
            ' sah die Liste des letzten Aufbaus - oder gar keine.
            RaiseMaskComponentsChanged()
        End Sub

        ''' <summary>Die gerade bearbeitete Ebenenmaske - oder Nothing.</summary>
        Private Function EditedLayerMask() As ImageMask
            If _editingLayerMaskId = "" Then Return Nothing
            Return _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                  String.Equals(m.Id, _editingLayerMaskId, StringComparison.Ordinal))
        End Function

        ''' <summary>Die bearbeitete Ebenenmaske neu in die Auswahl holen, damit das rote Overlay
        ''' nach einem Strich wieder die SUMME aller Bestandteile zeigt. Der angefasste Bestandteil
        ''' bleibt dabei angefasst - sonst spraenge die Liste bei jedem Strich auf den letzten
        ''' zurueck.</summary>
        Private Sub ReloadEditedLayerMaskIntoSelection()
            Dim id = _editingLayerMaskId
            If id = "" Then Return
            Dim active = _activeMaskComponentIndex
            LoadMaskIntoSelection(id, showAsMask:=True)
            _activeMaskComponentIndex = active
            RaiseMaskComponentsChanged()
        End Sub

        ''' <summary>Verschiebt eine MEHRTEILIGE bearbeitete Ebenenmaske als Ganzes um den zurückgelegten
        ''' Weg eines Maskenzuges - der Weg für den Fall, in dem <see cref="WriteSelectionMaskBackToLayer"/>
        ''' ablehnt. Verschoben wird nicht die Summe, sondern jeder Bestandteil, über denselben Baustein
        ''' wie beim Verschieben eines Objekts (Verlaufspunkte, gemalte Raster, Pinselkorrekturen).
        '''
        ''' Der Weg kommt aus dem ANZEIGE-Raum und muss in den Quellraum. Statt Anfang und Ende
        ''' einzeln abzubilden - beide können neben dem Bild liegen, sobald man die Maske hinausschiebt -
        ''' wird die Abbildung EINMAL in der Bildmitte differenziert und auf den Weg angewendet. Für
        ''' Zuschnitt, Drehung, Spiegelung, Begradigung und Größenänderung ist das exakt; bei
        ''' Perspektive und Verzerrung ist es eine Näherung, wie überall sonst an dieser Naht.</summary>
        Private Sub MoveEditedMaskComponents(displayDeltaX As Double, displayDeltaY As Double)
            Dim mask = EditedLayerMask()
            If mask Is Nothing OrElse mask.ComponentCount <= 1 Then Return
            If mask.SourceWidthPixels <= 0 OrElse mask.SourceHeightPixels <= 0 Then Return
            Dim size = GetAnnotationDisplayPixelSize()
            If size.Width <= 0 OrElse size.Height <= 0 Then Return
            If Math.Abs(displayDeltaX) < 0.5 AndAlso Math.Abs(displayDeltaY) < 0.5 Then Return

            ' Ein Schritt von einem Prozent der Anzeige in jede Richtung: gross genug, um nicht in der
            ' Rundung unterzugehen, klein genug, um lokal zu bleiben.
            Dim center = DisplayPercentToSourcePercent(50.0, 50.0)
            Dim stepRight = DisplayPercentToSourcePercent(51.0, 50.0)
            Dim stepDown = DisplayPercentToSourcePercent(50.0, 51.0)
            If Not center.HasValue OrElse Not stepRight.HasValue OrElse Not stepDown.HasValue Then Return

            Dim pixelsPerPercentX = size.Width / 100.0, pixelsPerPercentY = size.Height / 100.0
            Dim sw = mask.SourceWidthPixels / 100.0, sh = mask.SourceHeightPixels / 100.0
            ' Ableitung der Quellkoordinate nach der Anzeigekoordinate, in Bildpunkten je Bildpunkt.
            Dim sourceXperX = (stepRight.Value.X - center.Value.X) * sw / pixelsPerPercentX
            Dim sourceYperX = (stepRight.Value.Y - center.Value.Y) * sh / pixelsPerPercentX
            Dim sourceXperY = (stepDown.Value.X - center.Value.X) * sw / pixelsPerPercentY
            Dim sourceYperY = (stepDown.Value.Y - center.Value.Y) * sh / pixelsPerPercentY

            Dim offsetX = sourceXperX * displayDeltaX + sourceXperY * displayDeltaY
            Dim offsetY = sourceYperX * displayDeltaX + sourceYperY * displayDeltaY
            If Math.Abs(offsetX) < 0.5 AndAlso Math.Abs(offsetY) < 0.5 Then Return

            ImageProcessor.TransformMaskRegion(mask, 1.0, 1.0, 0.0, 0.0, offsetX, offsetY)
            TraceMask(Function() $"Mehrteilige Maske verschoben um {offsetX:F1}/{offsetY:F1} Quellpunkte: {MaskTrace(mask.Id)}")
            ' Die Arbeitskopie stammt aus der Maske - ohne Neuladen zeigte das Overlay den Weg, den
            ' der Zeiger genommen hat, und die Daten den, den die Maske genommen hat.
            ReloadEditedLayerMaskIntoSelection()
        End Sub

        ''' <summary>Schreibt die aktuelle _selectionMask (harte Form) in die bearbeitete Ebenen-Maske
        ''' zurück (Anzeige- → Quellraum via CreateSourceMaskFromSelection). FeatherPixels bleibt unberührt
        ''' (die "Weiche Kante" pflegt sie über die bestehende Brücke). Danach folgt die Anpassung der
        ''' Ebene der neuen Maske, weil der Render adj.Masks je Frame neu liest.
        '''
        ''' NUR FUER EINTEILIGE MASKEN. Seit die Auswahl die SUMME aller Bestandteile traegt, waere
        ''' das Zurueckschreiben bei einer mehrteiligen Maske ein Abflachen: die Summe landete im
        ''' ersten Bestandteil, und die uebrigen kaemen beim Rendern ein zweites Mal obendrauf - ein
        ''' hinzugefuegter Verlauf addierte sich nach dem Umkehren wieder auf, ein abgezogener
        ''' Bestandteil mit Zwischenwerten wurde doppelt abgezogen, und ein spaeter geloeschter
        ''' Bestandteil liesse seinen Geist im Primaerraster zurueck. Wer eine mehrteilige Maske
        ''' aendern will, geht deshalb an den BESTANDTEIL (ApplyMaskBrushStrokeToComponent) oder ans
        ''' Ergebnis (ImageMask.InvertResult).</summary>
        ''' <returns>False, wenn nichts geschrieben wurde - der Aufrufer muss dann seinen eigenen Weg gehen.</returns>
        Private Function WriteSelectionMaskBackToLayer() As Boolean
            If _editingLayerMaskId = "" Then Return False
            Dim mask = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso m.Id = _editingLayerMaskId)
            If mask Is Nothing OrElse _selectionMask Is Nothing Then Return False
            If mask.ComponentCount > 1 Then
                TraceMask(Function() $"Zurueckschreiben abgelehnt, die Maske hat {mask.ComponentCount} Bestandteile: {MaskTrace(mask.Id)}")
                Return False
            End If
            Dim rebuilt = ImageProcessor.CreateSourceMaskFromSelection(BuildAdjustmentsFromFields(), mask.Name)
            If rebuilt Is Nothing Then Return False
            ' WER schreibt WOHIN: die haeufigste Ursache fuer "die falsche Ebene hat sich geaendert".
            TraceMask(Function() $"Auswahl wird in die bearbeitete Maske zurückgeschrieben: {MaskTrace(mask.Id)}" &
                                 $" -> Form={FormKurz(rebuilt.PngBase64)}")
            mask.SourceWidthPixels = rebuilt.SourceWidthPixels
            mask.SourceHeightPixels = rebuilt.SourceHeightPixels
            mask.Left = rebuilt.Left
            mask.Top = rebuilt.Top
            mask.Right = rebuilt.Right
            mask.Bottom = rebuilt.Bottom
            mask.PngBase64 = rebuilt.PngBase64
            mask.Inverted = False
            ' Die Auswahl zeigt das FERTIGE Ergebnis, eine Umkehrung darin ist also schon in diesen
            ' Bildpunkten. Bliebe der Schalter stehen, kehrte er sie ein zweites Mal um.
            mask.InvertResult = False
            Return True
        End Function
        ''' <summary>Objektstapel in ANZEIGE-Reihenfolge fürs Ebenen-Panel: _annotations umgekehrt (vorderste
        ''' Ebene zuerst/oben). Wird von RebuildLayerRows synchron gehalten.</summary>
        Public ReadOnly Property LayerRows As ObservableCollection(Of LayerPanelRow)
            Get
                Return _layerRows
            End Get
        End Property

        ''' <summary>Die markierte Ebene als Objekt (statt Index) - so bleibt die Markierung beim Umkehren/
        ''' Neuaufbau der Anzeigeliste erhalten. Setzen übersetzt zurück auf SelectedAnnotationIndex.</summary>
        Public Property SelectedLayer As ImageAnnotation
            Get
                If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return Nothing
                Return _annotations(_selectedAnnotationIndex)
            End Get
            Set(value As ImageAnnotation)
                ' Während RebuildLayerRows die Liste leert/neu füllt, meldet die ListBox kurz SelectedItem=Nothing.
                ' Ohne diese Sperre würde das die Selektion bei jeder Stapeländerung fälschlich aufheben.
                If _suppressLayerRowSelectionSync Then Return
                If value Is Nothing Then
                    SelectedAnnotationIndex = -1
                Else
                    SelectedAnnotationIndex = _annotations.IndexOf(value)
                End If
            End Set
        End Property

        ''' <summary>Setzt die View kurz vor einem RECHTSklick auf eine bereits markierte Zeile: die
        ''' ListBox setzt ihr SelectedItem auch bei der rechten Maustaste, und ohne diese Ausnahme
        ''' zerstörte das die Mehrfachauswahl, bevor das Kontextmenü aufgeht. Wird beim nächsten
        ''' Zeilenwechsel verbraucht.</summary>
        Public Property PreserveMultiSelectionOnNextRowChange As Boolean
            Get
                Return _preserveMultiSelectionOnNextRowChange
            End Get
            Set(value As Boolean)
                _preserveMultiSelectionOnNextRowChange = value
            End Set
        End Property
        Private _preserveMultiSelectionOnNextRowChange As Boolean

        ''' <summary>Auswahl des gemeinsamen Panel-Stapels. Objektzeilen übersetzen weiter auf den
        ''' bestehenden SelectedAnnotationIndex; Korrekturzeilen bleiben ein eigenes Renderziel.</summary>
        Public Property SelectedLayerRow As LayerPanelRow
            Get
                Return _selectedLayerRow
            End Get
            Set(value As LayerPanelRow)
                If _suppressLayerRowSelectionSync OrElse Object.ReferenceEquals(value, _selectedLayerRow) Then Return
                ' Der Zeilenwechsel stellt das Reglerziel um, und das baut die Objektliste neu auf -
                ' waehrend die Liste noch mitten in ihrer Auswahlaenderung steht. Deshalb hier NICHT
                ' aufbauen, sondern einmal danach (siehe ResumeLayerRowRebuild). Nebenbei bleiben so
                ' die Zeilen gueltig, mit denen dieser Setter arbeitet - die uebergebene Zeile war
                ' nach einem Aufbau mittendrin ein Ueberbleibsel, das es in der Liste nicht mehr gab.
                SuspendLayerRowRebuild()
                Try
                Dim annotationIndex = If(value?.Annotation Is Nothing, -1, _annotations.IndexOf(value.Annotation))
                Dim adjustmentId = If(value?.AdjustmentLayer Is Nothing, "", value.AdjustmentLayer.Id)
                ' Das Merk-Flag gilt fuer GENAU DIESEN Wechsel: hier lesen und sofort abraeumen.
                ' Vorher wurde es erst am Ende des Setters zurueckgesetzt - der Gruppenzweig steigt
                ' aber mit Return aus und erreichte die Stelle nie. Danach galt es fuer den NAECHSTEN
                ' Klick weiter, und eine Ebene ausserhalb der Gruppe kam zur alten Auswahl HINZU,
                ' statt sie zu ersetzen.
                Dim mengeBehalten = _preserveMultiSelectionOnNextRowChange
                _preserveMultiSelectionOnNextRowChange = False
                If IsSelectionAdjustModeActive() AndAlso adjustmentId <> _selectionAdjustLayerId Then
                    ' Zielwechsel immer aus einem kanonischen globalen Reglerstand beginnen. Ein
                    ' Objekt darf nicht versehentlich die gerade sichtbaren Werte der lokalen
                    ' Korrektur als seine Bildwerte parken.
                    CommitSelectionAdjustModeToModel()
                End If
                If value IsNot Nothing AndAlso value.IsGroupHeader Then
                    ' Kopfzeile einer Gruppe: alle Mitglieder markieren, Anker ist das oberste.
                    ' MIT UNTERGRUPPEN: die Kopfzeile meint die Gruppe als Ganzes, und dazu gehoert
                    ' alles, was in ihr liegt - auch das, was in einer Gruppe darin liegt.
                    Dim members = AnnotationsInGroupTree(value.Group.Id)
                    Dim layerMembers = _maskedAdjustmentLayers.Where(Function(l) l IsNot Nothing AndAlso
                                                                     IsGroupInside(l.GroupId, value.Group.Id)).ToList()
                    _extraSelectedAdjustmentLayers.Clear()
                    _selectedMaskedAdjustmentLayerId = ""
                    If members.Count = 0 Then
                        SelectedAnnotationIndex = -1
                    Else
                        ' AUSDRUECKLICH statt ueber SelectAnnotationWithGroup: das nimmt die Gruppe
                        ' des ANKERS, und der letzte Eintrag des Baums kann zu einer UNTERgruppe
                        ' gehoeren - die Auswahl waere dann auf diese eingedampft, obwohl die
                        ' Kopfzeile der aeusseren angeklickt wurde.
                        Dim memberIndices = IndicesOfAnnotations(members)
                        SelectedAnnotationIndex = _annotations.IndexOf(members(members.Count - 1))
                        AddExtraSelectedAnnotationsByIndex(memberIndices)
                    End If
                    ' Korrekturebenen der Gruppe gehören zur Auswahl dazu - eine Gruppe wird als GANZES
                    ' markiert, auch wenn Objekte und Korrekturen darin gemischt sind.
                    If layerMembers.Count > 0 Then
                        _selectedMaskedAdjustmentLayerId = layerMembers(layerMembers.Count - 1).Id
                        For Each l In layerMembers
                            If l.Id <> _selectedMaskedAdjustmentLayerId Then _extraSelectedAdjustmentLayers.Add(l.Id)
                        Next
                    End If
                    RaiseMultiSelectionChanged()
                    _selectedLayerRow = value
                    RaiseLayerPanelSelectionChanged()
                    If _hasActiveSelection Then ClearSelection(captureUndo:=False)
                    RefreshSelectionAdjustMode()
                    Return
                ElseIf annotationIndex >= 0 AndAlso annotationIndex < _annotations.Count Then
                    ' Eine MITGLIEDS-Zeile meint bewusst das einzelne Objekt (die Gruppe wählt man über
                    ' ihre Kopfzeile oder durch Anklicken auf der Leinwand).
                    ' Gehört die Zeile bereits zur MEHRFACHauswahl, bleibt die Menge bestehen und nur der
                    ' Anker wandert: sonst zerstörte schon der Rechtsklick auf ein markiertes Objekt die
                    ' Auswahl, bevor das Kontextmenü überhaupt aufgeht (die ListBox setzt ihr SelectedItem
                    ' auch bei der rechten Maustaste).
                    ' Die Menge bleibt NUR beim Rechtsklick erhalten (Kontextmenü). Ein normaler
                    ' Klick auf eine Zeile grenzt bewusst auf dieses eine Objekt ein - sonst käme man
                    ' in einer Gruppe nie an ein einzelnes Mitglied.
                    Dim keepSet = mengeBehalten AndAlso
                                  IsAnnotationSelected(_annotations(annotationIndex)) AndAlso HasMultiAnnotationSelection
                    ' Beim Ankerwechsel MUSS der bisherige Anker in die Zusatzliste - er ist Teil der
                    ' Auswahl und stünde sonst als einziger nicht mehr darin (nach
                    ' Umschalt+Klick verlor die zuerst markierte Ebene beim Rechtsklick ihre Markierung).
                    ' Gemerkt wird über den INDEX, nicht über das Objekt: das Setzen des Ankers kann
                    ' die Objektliste durch Klone ersetzen (siehe AddExtraSelectedAnnotationsByIndex).
                    Dim keptIndices = If(keepSet, IndicesOfAnnotations(SelectedExtraAnnotations()), New List(Of Integer)())
                    If keepSet AndAlso _selectedAnnotationIndex >= 0 AndAlso _selectedAnnotationIndex < _annotations.Count AndAlso
                       _selectedAnnotationIndex <> annotationIndex AndAlso Not keptIndices.Contains(_selectedAnnotationIndex) Then
                        keptIndices.Add(_selectedAnnotationIndex)
                    End If
                    _selectedMaskedAdjustmentLayerId = ""
                    ' Auch die ZUSATZliste der Korrekturebenen raeumen: sonst blieben Mitglieder einer
                    ' zuvor markierten Gruppe weiter markiert, waehrend nur noch das Objekt gemeint ist.
                    ' Die fuehrende Ebene oben zu loeschen genuegt nicht - genau daran blieben nach dem
                    ' Klick auf ein Textobjekt zwei Verlaufsebenen orange stehen.
                    If _extraSelectedAdjustmentLayers.Count > 0 Then
                        _extraSelectedAdjustmentLayers.Clear()
                        RaiseMultiSelectionChanged()
                    End If
                    SelectedAnnotationIndex = annotationIndex
                    If keepSet Then
                        AddExtraSelectedAnnotationsByIndex(keptIndices)
                        RaiseMultiSelectionChanged()
                    End If
                    _selectedLayerRow = _layerRows.FirstOrDefault(Function(r) Object.ReferenceEquals(r.Annotation, _annotations(annotationIndex)))
                ElseIf Not String.IsNullOrWhiteSpace(adjustmentId) Then
                    ' Dieselbe Regel für Korrekturebenen: eine bereits mitmarkierte Zeile behält die Menge -
                    ' inklusive der bisherigen Hauptebene, die dabei in die Zusatzliste rückt.
                    Dim keepLayerSet = mengeBehalten AndAlso
                                       value?.AdjustmentLayer IsNot Nothing AndAlso
                                       IsAdjustmentLayerSelected(value.AdjustmentLayer) AndAlso
                                       SelectedAdjustmentLayers.Count > 1
                    Dim selectionSetCleared = False
                    If keepLayerSet Then
                        Dim previousPrimary = _maskedAdjustmentLayers.FirstOrDefault(Function(l) l IsNot Nothing AndAlso l.Id = _selectedMaskedAdjustmentLayerId)
                        ' Ueber die ID pruefen, nicht ueber die Referenz: ApplyAdjustments ersetzt die
                        ' Ebenenliste durch Klone, ein Contains ueber Objekte findet den Vorgaenger
                        ' danach nicht mehr und er landete doppelt in der Liste.
                        If previousPrimary IsNot Nothing AndAlso previousPrimary.Id <> adjustmentId AndAlso
                           Not _extraSelectedAdjustmentLayers.Contains(previousPrimary.Id) Then
                            _extraSelectedAdjustmentLayers.Add(previousPrimary.Id)
                        End If
                        _extraSelectedAdjustmentLayers.Remove(adjustmentId)
                    Else
                        selectionSetCleared = _extraSelectedAdjustmentLayers.Count > 0
                        _extraSelectedAdjustmentLayers.Clear()
                    End If
                    _selectedMaskedAdjustmentLayerId = adjustmentId
                    ' Spiegelfall zum Objekt-Zweig: eine Korrekturebene zu waehlen raeumt die
                    ' MEHRFACHauswahl der Objekte, sonst blieben dort Zeilen markiert stehen.
                    If _extraSelectedAnnotations.Count > 0 Then
                        _extraSelectedAnnotations.Clear()
                        selectionSetCleared = True
                    End If
                    ' Das Raeumen MUSS gemeldet werden: nur RaiseMultiSelectionChanged zieht die
                    ' orange Kennzeichnung der Zeilen nach. Ohne die Meldung blieben die vorher mit
                    ' Strg markierten Masken- und Auswahlebenen markiert stehen, und ein Klick auf
                    ' eine bisher unmarkierte Zeile sah aus, als KAEME sie zur Menge hinzu - dabei
                    ' war die Auswahl innen laengst auf diese eine Ebene eingedampft. Gemeldet wird
                    ' erst NACH dem Setzen der fuehrenden Ebene, sonst rechnet die Auffrischung noch
                    ' mit der alten.
                    If selectionSetCleared Then RaiseMultiSelectionChanged()
                    SelectedAnnotationIndex = -1
                    _selectedLayerRow = _layerRows.FirstOrDefault(Function(r) r.AdjustmentLayer IsNot Nothing AndAlso r.AdjustmentLayer.Id = adjustmentId)
                Else
                    _selectedMaskedAdjustmentLayerId = ""
                    SelectedAnnotationIndex = -1
                    _selectedLayerRow = Nothing
                End If
                ' Kein scharfgestellter Platzierungstyp mehr: SelectedAnnotationIndex = -1 raeumt ihn
                ' NICHT ab (das passiert nur beim Markieren eines Objekts). Wer vom Text-Werkzeug auf
                ' eine Masken- oder Auswahlebene klickt, behielt sonst den Akzentrahmen auf "Text" -
                ' und der naechste Klick ins Bild haette dort ein Textobjekt gesetzt.
                PendingInsertKind = ""
                RaiseLayerPanelSelectionChanged()
                ' Nur die Auswahl/Maske DER gewählten Ebene aktiv: vorige Selektion im Bild verwerfen und
                ' die dieser Ebene laden (Auswahl-Ebene -> Ameisen, Masken-Ebene -> rot). Objekt/Nichts
                ' gewählt -> gar keine Bild-Auswahl. Funktioniert in JEDEM Werkzeug (nicht nur Anpassen).
                If Not String.IsNullOrWhiteSpace(adjustmentId) Then
                    Dim picked = _maskedAdjustmentLayers.FirstOrDefault(Function(l) l IsNot Nothing AndAlso l.Id = adjustmentId)
                    If picked IsNot Nothing Then
                        TraceMask(Function() $"Zeile im Ebenenpanel gewählt: Ebene={Kurz(picked.Id)} ""{picked.Name}"" ({MaskTrace(picked.MaskId)})")
                        ApplyAdjustmentLayerPresentation(picked)
                        TraceLayerInventory("Auswahl der Panel-Zeile")
                    End If
                Else
                    ' Ein OBJEKT mit Ebenenmaske im MASKEN-Werkzeug: wer es dort anklickt, will an
                    ' seine Maske heran - sie wird also wieder geoeffnet statt abgeraeumt. Ohne das
                    ' war die Maske nach einem Klick neben das Bild nur noch ueber das Maskensymbol
                    ' der Zeile zu erreichen; ein Klick auf die Zeile selbst - der naheliegende Weg -
                    ' loeschte das rote Overlay, statt es zu zeigen.
                    Dim maskedObject = If(annotationIndex >= 0 AndAlso annotationIndex < _annotations.Count,
                                          _annotations(annotationIndex), Nothing)
                    Dim objectMaskId = If(maskedObject Is Nothing, "", maskedObject.MaskId)
                    If _currentTool = EditorTool.Mask AndAlso Not String.IsNullOrEmpty(objectMaskId) Then
                        LoadMaskIntoSelection(objectMaskId, showAsMask:=True)
                        ' Eine gemalte Maske wird mit dem PINSEL nachgebessert - im Verlaufsmodus
                        ' zoege der erste Zug einen neuen Verlauf auf.
                        Dim objectMask = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso m.Id = objectMaskId)
                        If objectMask Is Nothing OrElse Not objectMask.IsGradient Then MaskMode = "Brush"
                    Else
                        ' Kein Ebenen-Ziel mehr (Objekt ohne Maske oder nichts gewaehlt). Eine laufende
                        ' Pixelauswahl raeumt ClearSelection ab - ein VERLAUF hat aber gar keine aktive
                        ' Auswahl, sein rotes Overlay haengt allein an der markierten Ebene. Ohne die
                        ' zwei Zeilen bleibt es stehen, und die Maske sieht aus, als waere sie noch
                        ' gewaehlt.
                        If _hasActiveSelection Then ClearSelection(captureUndo:=False)
                        RaiseGradientPropertiesChanged()
                        PublishMaskBrushOverlay()
                    End If
                End If
                RefreshSelectionAdjustMode()
                Finally
                    ResumeLayerRowRebuild(deferToDispatcher:=True)
                End Try
            End Set
        End Property

        ''' <summary>Der Anzeigezustand EINER Korrekturebene: ihre Maske in die Auswahl laden, das
        ''' zustaendige Werkzeug samt Maskenmodus setzen und rotes Overlay bzw. Laufameisen zeigen.
        '''
        ''' Eine Korrektur lebt von ihrer Maske - wer sie im Panel anklickt, will an genau die heran.
        ''' Eine Verlaufsmaske hat weder Ameisen noch malbare Form, fuer sie ist das MASKEN-Werkzeug
        ''' zustaendig (Griffe und Regler). Eine MASKEN-Ebene (gemalt oder aus der Objektauswahl)
        ''' gehoert ebenfalls dorthin: sie zeigt rotes Overlay und wird mit dem Maskenpinsel
        ''' bearbeitet. Nur eine echte AUSWAHL-Ebene fuehrt ins Auswahl-Werkzeug. Bei Mehrfachauswahl
        ''' bleibt das Werkzeug, wie es ist - dort geht es um die Menge, nicht um eine Maske.
        '''
        ''' Steht als EIGENE Methode da, weil derselbe Zustand auch OHNE Zeilenwechsel gebraucht
        ''' wird - siehe <see cref="ReapplySelectedLayerPresentation"/>.</summary>
        Private Sub ApplyAdjustmentLayerPresentation(picked As MaskedAdjustmentLayer)
            If picked Is Nothing Then Return
            LoadLayerMaskIntoSelection(picked)
            Dim isGradient = _imageMasks.Any(Function(m) m IsNot Nothing AndAlso m.Id = picked.MaskId AndAlso m.IsGradient)
            Dim isMaskLayer = isGradient OrElse picked.IsMaskLayer
            Dim layerTool = If(isMaskLayer, EditorTool.Mask, EditorTool.Selection)
            If SelectedAdjustmentLayers.Count <= 1 AndAlso _currentTool <> layerTool Then
                CurrentTool = layerTool
            End If
            ' Eine gemalte Maskenebene wird mit dem PINSEL bearbeitet - der Verlaufsmodus
            ' zoege beim ersten Zug einen neuen Verlauf auf, statt sie nachzubessern.
            If isMaskLayer AndAlso Not isGradient Then MaskMode = "Brush"
            If isGradient Then
                Dim gradient = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso m.Id = picked.MaskId)
                MaskMode = If(gradient IsNot Nothing AndAlso gradient.IsRadialGradient, "Radial", "Linear")
                PublishGradientOverlay(gradient)
                RaiseGradientPropertiesChanged()
            End If
        End Sub

        ''' <summary>Dieselbe Ebene noch einmal anwaehlen: den Anzeigezustand neu herstellen.
        '''
        ''' Ein Klick auf die BEREITS markierte Zeile meldet der Liste keinen Wechsel, der Setter
        ''' oben laeuft also nie - und genau dann ist das rote Overlay oft mit Absicht ausgeblendet:
        ''' nach dem ersten Reglerdreh in einem Anpassungswerkzeug und in jedem Werkzeug, das es
        ''' verdeckt. Es gab bis dahin keine Geste, die es zurueckholt; ein Umweg ueber eine andere
        ''' Zeile und zurueck war die einzige Moeglichkeit.
        '''
        ''' Der angefasste BESTANDTEIL bleibt dabei angefasst - das Laden setzt ihn sonst auf den
        ''' zuletzt hinzugefuegten zurueck, und die Regler sprangen bei einem Klick auf eine Zeile,
        ''' die schon markiert ist, auf einen anderen Verlauf.</summary>
        Public Sub ReapplySelectedLayerPresentation()
            Dim picked = _maskedAdjustmentLayers.FirstOrDefault(Function(l) l IsNot Nothing AndAlso
                                                                    l.Id = _selectedMaskedAdjustmentLayerId)
            If picked Is Nothing Then Return
            Dim activeComponent = _activeMaskComponentIndex
            Dim previousMaskId = _editingLayerMaskId
            ApplyAdjustmentLayerPresentation(picked)
            If activeComponent >= 0 AndAlso String.Equals(previousMaskId, _editingLayerMaskId, StringComparison.Ordinal) Then
                _activeMaskComponentIndex = activeComponent
                RaiseMaskComponentsChanged()
            End If
            RefreshSelectionAdjustMode()
        End Sub

        ''' <summary>Einfacher Klick auf die bereits FUEHRENDE Zeile: die Mehrfachauswahl auf genau
        ''' diese Zeile eindampfen.
        '''
        ''' Der Setter oben macht das bei jedem Zeilenwechsel - nur meldet die Liste bei einem Klick
        ''' auf ihre eigene Auswahl keinen Wechsel, der Setter laeuft also nie. Damit gab es fuer
        ''' mehrere mit Strg markierte Masken- oder Auswahlebenen keine Geste, die wieder auf eine
        ''' einzelne zurueckfuehrt: der Klick auf eine der markierten Zeilen tat nichts, und die
        ''' Auswahl liess sich nur noch Zeile fuer Zeile mit Strg abraeumen.</summary>
        Public Sub CollapseSelectionToSelectedRow()
            Dim changed = False
            If _extraSelectedAdjustmentLayers.Count > 0 Then
                _extraSelectedAdjustmentLayers.Clear()
                changed = True
            End If
            If _extraSelectedAnnotations.Count > 0 Then
                _extraSelectedAnnotations.Clear()
                changed = True
            End If
            If changed Then RaiseMultiSelectionChanged()
        End Sub

        Public ReadOnly Property HasSelectedPanelLayer As Boolean
            Get
                Return _selectedLayerRow IsNot Nothing
            End Get
        End Property

        ''' <summary>Gibt es etwas, das Esc zuerst abwaehlen soll, bevor es den Editor verlaesst?
        '''
        ''' Bewusst NICHT nur die Panel-Zeile: eine gerade gezogene Verlaufsmaske setzt die
        ''' Maskenebene, aber keine Zeile - Esc landete deshalb sofort beim Verlassen samt
        ''' Speicherabfrage, obwohl sichtbar etwas markiert war.</summary>
        Public ReadOnly Property HasDeselectableTarget As Boolean
            Get
                Return _selectedLayerRow IsNot Nothing OrElse
                       Not String.IsNullOrWhiteSpace(_selectedMaskedAdjustmentLayerId) OrElse
                       _selectedAnnotationIndex >= 0 OrElse
                       Not String.IsNullOrEmpty(_pendingInsertKind)
            End Get
        End Property

        ''' <summary>Alles abwaehlen, was Esc abwaehlen soll: Panel-Zeile, Maskenebene, Objekt und
        ''' einen vorgemerkten Platzierungstyp. Die Regler zielen danach wieder aufs ganze Bild.</summary>
        Public Sub DeselectCurrentTarget()
            If Not HasDeselectableTarget Then Return
            PendingInsertKind = ""
            SelectGlobalAdjustmentsTarget()
            Me.RaisePropertyChanged(NameOf(HasDeselectableTarget))
        End Sub

        ''' <summary>Alles abwaehlen, was im Masken-Werkzeug markiert sein kann - fuer den Klick
        ''' NEBEN das Bild.
        '''
        ''' <see cref="DeselectCurrentTarget"/> allein reicht dort nicht: es nimmt die Ebene aus der
        ''' Markierung, laesst die bearbeitete Ebenenmaske samt rotem Overlay aber stehen. Sichtbar
        ''' blieb dann eine rot gefaerbte Maske ohne Ziel - genau das, was ein Klick ins Leere
        ''' aufloesen soll. Eine noch nicht uebernommene Auswahl faellt hier bewusst MIT: im
        ''' Masken-Werkzeug ist sie die Maske, die man gerade sieht.</summary>
        Public Sub DeselectMaskTarget()
            Dim hadTarget = _editingLayerMaskId <> "" OrElse _hasActiveSelection OrElse
                            _workingMaskId <> "" OrElse HasDeselectableTarget
            If Not hadTarget Then Return
            _editingLayerMaskId = ""
            _workingMaskId = ""
            _activeMaskComponentIndex = -1
            _gradientDragMaskId = ""
            _gradientDragActive = False
            _gradientHandle = -1
            If _hasActiveSelection Then ClearSelection(captureUndo:=False)
            SetActiveSelectionIsMask(False)
            DeselectCurrentTarget()
            RaiseMaskComponentsChanged()
            RaiseGradientPropertiesChanged()
            PublishMaskBrushOverlay()
        End Sub

        Public ReadOnly Property HasSelectedAdjustmentLayer As Boolean
            Get
                Return _selectedLayerRow?.AdjustmentLayer IsNot Nothing
            End Get
        End Property

        ''' <summary>Ist gerade die Arbeitskopie einer Ebenenmaske geöffnet? Der Wert trennt sie
        ''' von einer freien, noch nicht übernommenen Auswahl: ein Klick in den leeren Bereich des
        ''' Ebenenpanels darf letztere nicht wegwerfen, muss eine geöffnete Ebenenmaske samt ihrem
        ''' Overlay aber vollständig schließen.</summary>
        Public ReadOnly Property IsEditingLayerMask As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(_editingLayerMaskId)
            End Get
        End Property

        ''' <summary>True, wenn die globalen Regler das Bild und nicht eine Objekt- oder lokale
        ''' Korrekturebene bedienen. Die globale Anpassungszeile ist kein ListBox-Eintrag, daher
        ''' wird ihr aktiver Zustand separat vom Ebenenpanel dargestellt.</summary>
        Public ReadOnly Property IsGlobalAdjustmentsSelected As Boolean
            Get
                Return Not HasSelectedPanelLayer AndAlso Not HasSelectedAnnotation
            End Get
        End Property

        Public Property SelectedLayerOpacity As Double
            Get
                ' EINE GRUPPENZEILE meint die GRUPPE, nicht ihre Mitglieder. Vorher setzte der Regler
                ' dort die Deckkraft jedes einzelnen Mitglieds - an den Überlappungen sah man dann
                ' zwei Stufen statt einer gleichmäßigen Fläche, und "die Gruppe weich einblenden" ging
                ' gar nicht.
                If ShowGroupProperties Then Return GroupOpacity
                If _selectedLayerRow?.AdjustmentLayer IsNot Nothing Then
                    Return Math.Round(Math.Max(0, Math.Min(1, _selectedLayerRow.AdjustmentLayer.Opacity)) * 100.0, 2)
                End If
                Return AnnotationOpacity
            End Get
            Set(value As Double)
                Dim clamped = Math.Max(0, Math.Min(100, value))
                If ShowGroupProperties Then
                    GroupOpacity = clamped
                    Me.RaisePropertyChanged(NameOf(SelectedLayerOpacity))
                    Return
                End If
                If _selectedLayerRow?.AdjustmentLayer IsNot Nothing Then
                    Dim nextOpacity = CSng(clamped / 100.0)
                    If Math.Abs(_selectedLayerRow.AdjustmentLayer.Opacity - nextOpacity) < 0.0001F Then Return
                    CaptureUndoState("AdjustmentLayerOpacity")
                    _selectedLayerRow.AdjustmentLayer.Opacity = nextOpacity
                    _selectedLayerRow.Refresh()
                    _hasChanges = True
                    RaiseResetButtonStateChanged()
                    SchedulePreviewUpdate()
                Else
                    AnnotationOpacity = clamped
                End If
                Me.RaisePropertyChanged(NameOf(SelectedLayerOpacity))
            End Set
        End Property

        ''' <summary>Die Mischmethode der markierten Zeile - dieselbe Weiche wie bei der Deckkraft:
        ''' eine GRUPPENzeile meint die Gruppe, eine Objektzeile das Objekt.</summary>
        Public Property SelectedLayerBlendModeOption As AnnotationBlendModeOption
            Get
                If ShowGroupProperties Then Return SelectedGroupBlendModeOption
                Return SelectedAnnotationBlendModeOption
            End Get
            Set(value As AnnotationBlendModeOption)
                If value Is Nothing Then Return
                If ShowGroupProperties Then
                    GroupBlendMode = value.Key
                Else
                    SelectedAnnotationBlendModeOption = value
                End If
                Me.RaisePropertyChanged(NameOf(SelectedLayerBlendModeOption))
            End Set
        End Property

        ''' <summary>Ist die Mischmethode der Kopfleiste überhaupt einstellbar? Bei einem markierten
        ''' Objekt oder einer markierten Gruppe ja, sonst nicht.</summary>
        Public ReadOnly Property CanEditSelectedLayerBlendMode As Boolean
            Get
                Return HasSelectedAnnotation OrElse ShowGroupProperties
            End Get
        End Property

        ''' <summary>Waehrend eines Massenwechsels an der Objektliste NICHT je Objekt neu aufbauen.
        '''
        ''' Die Zeilen haengen ueber `CollectionChanged` am Objektstapel, und das ist richtig so -
        ''' aber `ApplyAdjustments` leert die Liste und fuellt sie Objekt fuer Objekt wieder auf.
        ''' Gemessen am 2026-08-04 an einem Dokument mit 32 Objekten: 33 vollstaendige Neuaufbauten
        ''' je Aufruf, zweimal je Ebenenwechsel, zusammen 1022 Aufbauten und 7,5 Sekunden Rechenzeit
        ''' in zehn Sekunden. Mit der Klammer bleibt genau EINER am Ende.</summary>
        Private _layerRowsSuspendDepth As Integer
        Private _layerRowsRebuildPending As Boolean

        Private Sub SuspendLayerRowRebuild()
            _layerRowsSuspendDepth += 1
        End Sub

        ''' <param name="deferToDispatcher">Fuer die Klammer um die AUSWAHLAENDERUNG der Liste. Dort
        ''' darf der Zeilenstapel nicht angefasst werden: Avalonia verwirft eine Sammlung, die sich
        ''' mitten in seiner Auswahlaenderung aendert ("Source collection was modified during
        ''' selection update"). Die Ausnahme flog beim Leeren, also ganz am Anfang - die alten Zeilen
        ''' waren weg, die neuen kamen nie an, und das Ebenenpanel blieb leer, bis irgendetwas einen
        ''' sauberen Aufbau anstiess. Der Aufbau kommt deshalb erst, wenn die Liste mit sich fertig
        ''' ist.</param>
        Private Sub ResumeLayerRowRebuild(Optional deferToDispatcher As Boolean = False)
            If _layerRowsSuspendDepth > 0 Then _layerRowsSuspendDepth -= 1
            If _layerRowsSuspendDepth > 0 OrElse Not _layerRowsRebuildPending Then Return
            _layerRowsRebuildPending = False
            If Not deferToDispatcher Then
                RebuildLayerRows()
                Return
            End If
            ' Mehrere Zeilenwechsel kurz hintereinander brauchen trotzdem nur EINEN Aufbau.
            If _layerRowsRebuildPosted Then Return
            _layerRowsRebuildPosted = True
            Dispatcher.UIThread.Post(Sub()
                                         _layerRowsRebuildPosted = False
                                         RebuildLayerRows()
                                     End Sub, DispatcherPriority.Background)
        End Sub
        Private _layerRowsRebuildPosted As Boolean

        Private Sub RebuildLayerRows()
            ' Waehrend einer Klammer nur vormerken - der Aufbau kommt einmal am Ende.
            If _layerRowsSuspendDepth > 0 Then
                _layerRowsRebuildPending = True
                Return
            End If
            ' Wie oft und wie teuer - der Neuaufbau verwirft ALLE Zeilen und legt sie neu an, und die
            ' Liste baut daraufhin jeden Eintrag samt Miniatur neu auf. Das taucht in keinem
            ' Render-Protokoll auf, kostet im Oberflaechen-Thread aber genauso.
            Dim rebuildWatch = Diagnostics.Stopwatch.StartNew()
            Dim selectedAnnotation = SelectedLayer
            Dim selectedAdjustmentId = _selectedMaskedAdjustmentLayerId
            ' War eine GRUPPEN-Kopfzeile markiert, muss sie es danach wieder sein: die Zeilen sind neue
            ' Objekte, und ohne diese Merkung fiel die Auswahl auf ein Mitglied zurück - sichtbar daran,
            ' dass der Menüpunkt nach dem Sperren plötzlich wieder "Ebene ..." hieß.
            Dim selectedGroupId = If(_selectedLayerRow IsNot Nothing AndAlso _selectedLayerRow.IsGroupHeader AndAlso
                                     _selectedLayerRow.Group IsNot Nothing, _selectedLayerRow.Group.Id, "")
            Try
            _suppressLayerRowSelectionSync = True
            Try
                _layerRows.Clear()
                ' Von vorn (oberste Ebene) nach hinten. Trifft der Durchlauf auf ein Gruppenmitglied,
                ' kommt zuerst die Kopfzeile der Gruppe und danach - eingerückt - ihre Mitglieder.
                ' Mitglieder liegen zusammenhängend (siehe GroupSelectedAnnotations), der Block ist also
                ' genau hier zu Ende; eine eingeklappte Gruppe zeigt nur ihre Kopfzeile.
                Dim emittedGroups As New HashSet(Of String)(StringComparer.Ordinal)

                ' Die Gruppenkette einer Kennung, von aussen nach innen - dieselbe Reihenfolge, in der
                ' die Kopfzeilen erscheinen muessen. Ein Ring wird abgefangen.
                Dim chainOf = Function(groupId As String) As List(Of AnnotationGroup)
                                Dim result As New List(Of AnnotationGroup)()
                                Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
                                Dim current = FindAnnotationGroup(groupId)
                                While current IsNot Nothing AndAlso seen.Add(current.Id)
                                    result.Insert(0, current)
                                    current = FindAnnotationGroup(current.ParentGroupId)
                                End While
                                Return result
                            End Function

                ' Zugeklappt ist eine Zeile, sobald IRGENDEINE Gruppe ueber ihr zugeklappt ist -
                ' nicht nur die unmittelbare.
                Dim isHidden = Function(chain As List(Of AnnotationGroup), bisIndex As Integer) As Boolean
                                    For k = 0 To Math.Min(bisIndex, chain.Count - 1)
                                        If chain(k).IsCollapsed Then Return True
                                    Next
                                    Return False
                                End Function

                ' Die noch fehlenden Kopfzeilen der Kette ausgeben, von aussen nach innen und je mit
                ' ihrer Tiefe. Eine Untergruppe erscheint damit eingerueckt unter ihrer Elterngruppe.
                Dim emitHeaders = Sub(chain As List(Of AnnotationGroup))
                                     For k = 0 To chain.Count - 1
                                         If isHidden(chain, k - 1) Then Exit For
                                         If emittedGroups.Add(chain(k).Id) Then _layerRows.Add(New LayerPanelRow(chain(k), k))
                                     Next
                                 End Sub

                For i = _annotations.Count - 1 To 0 Step -1
                    Dim a = _annotations(i)
                    ' Korrekturebenen, die ÜBER diesem Objekt einsortiert sind, stehen im Panel direkt
                    ' darüber - sie wirken auf alles darunter (Basis und die Objekte bis hierher).
                    ' Gehört das Objekt zu einer Gruppe, erscheinen sie nur über dem OBERSTEN Mitglied,
                    ' also über der ganzen Gruppe: eine Zeile mitten im Gruppenblock sähe aus, als läge
                    ' die Korrektur „in" der Gruppe - das gibt es nicht.
                    Dim aGroup = If(a Is Nothing, Nothing, FindAnnotationGroup(a.GroupId))
                    ' Liegt das Objekt in einer Gruppe, steht auch seine Korrektur EINGERÜCKT im Block
                    ' (und verschwindet mit ihm, wenn die Gruppe zugeklappt ist) - sie gehört ja oft
                    ' genau zu diesem einen Objekt.
                    For k = _maskedAdjustmentLayers.Count - 1 To 0 Step -1
                        Dim stacked = _maskedAdjustmentLayers(k)
                        If stacked Is Nothing Then Continue For
                        If Not String.Equals(stacked.StackAboveAnnotationId, a.Id, StringComparison.Ordinal) Then Continue For
                        ' Gehört die Korrektur zur selben Gruppe wie ihr Anker, steht sie EINGERÜCKT im
                        ' Block (und verschwindet mit ihm beim Zuklappen). Sonst liegt sie darüber -
                        ' also VOR der Kopfzeile, ausserhalb des Blocks.
                        Dim imBlock = aGroup IsNot Nothing AndAlso
                                      String.Equals(stacked.GroupId, aGroup.Id, StringComparison.Ordinal)
                        If imBlock Then
                            Dim aChain = chainOf(aGroup.Id)
                            If isHidden(aChain, aChain.Count - 1) Then Continue For
                            emitHeaders(aChain)
                            _layerRows.Add(New LayerPanelRow(stacked, aGroup, aChain.Count))
                        Else
                            _layerRows.Add(New LayerPanelRow(stacked))
                        End If
                    Next
                    Dim grp = If(a Is Nothing, Nothing, FindAnnotationGroup(a.GroupId))
                    If grp Is Nothing Then
                        _layerRows.Add(New LayerPanelRow(a))
                        Continue For
                    End If
                    Dim chain = chainOf(grp.Id)
                    emitHeaders(chain)
                    If Not isHidden(chain, chain.Count - 1) Then _layerRows.Add(New LayerPanelRow(a, grp, chain.Count))
                Next
                For i = _maskedAdjustmentLayers.Count - 1 To 0 Step -1
                    Dim l = _maskedAdjustmentLayers(i)
                    If l IsNot Nothing AndAlso Not String.IsNullOrEmpty(l.StackAboveAnnotationId) Then Continue For
                    Dim lgrp = If(l Is Nothing, Nothing, FindAnnotationGroup(l.GroupId))
                    If lgrp Is Nothing Then
                        _layerRows.Add(New LayerPanelRow(l))
                        Continue For
                    End If
                    Dim lchain = chainOf(lgrp.Id)
                    emitHeaders(lchain)
                    If Not isHidden(lchain, lchain.Count - 1) Then _layerRows.Add(New LayerPanelRow(l, lgrp, lchain.Count))
                Next
                If Not String.IsNullOrEmpty(selectedGroupId) Then
                    _selectedLayerRow = _layerRows.FirstOrDefault(Function(r) r.IsGroupHeader AndAlso
                                                                  r.Group IsNot Nothing AndAlso
                                                                  String.Equals(r.Group.Id, selectedGroupId, StringComparison.Ordinal))
                End If
                If _selectedLayerRow Is Nothing OrElse String.IsNullOrEmpty(selectedGroupId) Then
                    _selectedLayerRow = _layerRows.FirstOrDefault(Function(r)
                        Return (selectedAnnotation IsNot Nothing AndAlso Object.ReferenceEquals(r.Annotation, selectedAnnotation)) OrElse
                               (Not String.IsNullOrWhiteSpace(selectedAdjustmentId) AndAlso
                                r.AdjustmentLayer IsNot Nothing AndAlso r.AdjustmentLayer.Id = selectedAdjustmentId)
                    End Function)
                End If
                If _selectedLayerRow Is Nothing AndAlso selectedAnnotation Is Nothing Then _selectedMaskedAdjustmentLayerId = ""
            Finally
                _suppressLayerRowSelectionSync = False
            End Try
            Me.RaisePropertyChanged(NameOf(SelectedLayer))
            RaiseLayerPanelSelectionChanged()
            RefreshLayerRowSelectionMarks()
            RefreshLayerRowThumbnails()
            rebuildWatch.Stop()
            DiagnosticLogService.LogAlways("Editor.LayerRows",
                                           $"neu aufgebaut zeilen={_layerRows.Count} ms={rebuildWatch.ElapsedMilliseconds}")
            ' Ein Abbruch mittendrin laesst den Zeilenstapel GELEERT zurueck, und weil die Ausnahme
            ' unterwegs verschluckt wird, stuerzt nichts ab: das Ebenenpanel steht einfach leer da.
            ' Genau so trat der Fehler oben auf - ohne diese Zeile sucht man ihn wieder blind.
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.LayerRowsAbbruch", ex)
                ' SELBSTHEILUNG: laeuft ein kuenftiger Weg doch wieder in die Avalonia-Sperre
                ' (InvalidOperationException waehrend einer Auswahlaenderung), bleibt das Panel
                ' nicht leer stehen - ein sauberer Neuaufbau kommt, sobald die Liste mit ihrer
                ' Auswahlaenderung fertig ist.
                If TypeOf ex Is InvalidOperationException AndAlso Not _layerRowsRebuildPosted Then
                    _layerRowsRebuildPosted = True
                    Dispatcher.UIThread.Post(Sub()
                                                 _layerRowsRebuildPosted = False
                                                 RebuildLayerRows()
                                             End Sub, DispatcherPriority.Background)
                End If
                Throw
            End Try
        End Sub

        ''' <summary>Die Miniatur EINER Zeile nachziehen - gerufen beim Abwaehlen des Objekts.
        '''
        ''' Waehrend der Arbeit an einer Ebene bleibt ihre Miniatur stehen: man sieht sie dabei
        ''' ohnehin nicht an, und bei jeder Aenderung neu zu zeichnen hiesse, eine Pinselebene mit
        ''' allen ihren Strichen je Strich einmal mehr zu zeichnen (Nutzerentscheidung
        ''' 2026-08-04).</summary>
        Private Sub RefreshThumbnailForAnnotationIndex(index As Integer)
            If index < 0 OrElse index >= _annotations.Count Then Return
            If Not AppSettingsService.Load().EditorLayerThumbnails Then Return
            Dim a = _annotations(index)
            Dim row = _layerRows.FirstOrDefault(Function(r) r IsNot Nothing AndAlso
                                                    Object.ReferenceEquals(r.Annotation, a))
            If row Is Nothing Then Return
            ' Eine Textminiatur ist in einem 26-Pixel-Kasten nicht lesbar. Die Zeile zeigt bewusst
            ' das Text-Werkzeug-Symbol; zudem sparen wir das Rasterisieren bei jedem Abwählen.
            If String.Equals(a.Kind, "Text", StringComparison.OrdinalIgnoreCase) Then
                row.Thumbnail = Nothing
                Return
            End If
            Dim key = "obj:" & a.Id & ":" & AnnotationThumbnailFingerprint(a)
            Dim size = GetCurrentScenePixelSize()
            If size.Width <= 0 OrElse size.Height <= 0 Then Return
            row.Thumbnail = GetOrBuildThumbnail(key,
                Function() ImageProcessor.BuildAnnotationThumbnail(a, size.Width, size.Height, LayerThumbnailBoxSize))
        End Sub

        ''' <summary>Die Miniaturen aller Zeilen nachziehen: Inhalt der Ebene und, wenn sie eine
        ''' Maske traegt, die Maske daneben.
        '''
        ''' Gezeichnet wird nur, was sich geaendert hat. `RebuildLayerRows` laeuft bei jeder
        ''' Kleinigkeit, und ein Objekt bei jedem Durchlauf neu zu zeichnen waere bei einem Stapel
        ''' aus dreissig Ebenen jedes Mal dreissig Renderdurchlaeufe. Der Schluessel ist deshalb ein
        ''' Fingerabdruck aus dem, was man SIEHT; bleibt er gleich, wird die vorhandene Miniatur
        ''' weitergereicht.</summary>
        Private Sub RefreshLayerRowThumbnails()
            ' Abgeschaltet heisst: gar nichts zeichnen und alles freigeben. Die Zeile faellt dann von
            ' selbst auf das Typsymbol des Werkzeugs zurueck (die Ansicht entscheidet an
            ' HasThumbnail), und ein Bild-Objekt wird fuer das Panel nicht mehr aus seiner Datei
            ' gelesen - genau der Grund, warum es den Schalter gibt.
            If Not AppSettingsService.Load().EditorLayerThumbnails Then
                For Each row In _layerRows
                    If row Is Nothing Then Continue For
                    row.Thumbnail = Nothing
                    row.MaskThumbnail = Nothing
                Next
                DisposeLayerThumbnails()
                Return
            End If
            Dim size = GetCurrentScenePixelSize()
            Dim alive As New HashSet(Of String)(StringComparer.Ordinal)
            ' Gezaehlt wird, was WIRKLICH gezeichnet wurde. Der Aufbau des Panels laeuft bei jeder
            ' Kleinigkeit, und eine Miniatur aus einer Bilddatei kostet auch verkleinert noch
            ' Millisekunden - ohne diese Zeile im Diagnoselog raet man, woher die Last kommt.
            _thumbnailsBuilt = 0
            Dim watch = Diagnostics.Stopwatch.StartNew()
            For Each row In _layerRows
                If row Is Nothing Then Continue For
                ' Eine Gruppen-Kopfzeile behaelt ihr Ordnersymbol: sie ist kein Inhalt, sondern eine
                ' Klammer um mehrere.
                If row.IsGroupHeader Then Continue For

                Dim maskId = ""
                If row.Annotation IsNot Nothing Then
                    maskId = row.Annotation.MaskId
                    ' Textobjekte werden im Ebenenpanel durch ihr eindeutiges Werkzeugsymbol
                    ' dargestellt. Die Vorschau wäre bei 26 Pixeln nur ein Strich und verursacht
                    ' bei jeder Textänderung unnötige Zeichenarbeit.
                    If String.Equals(row.Annotation.Kind, "Text", StringComparison.OrdinalIgnoreCase) Then
                        row.Thumbnail = Nothing
                    Else
                        Dim key = "obj:" & row.Annotation.Id & ":" & AnnotationThumbnailFingerprint(row.Annotation)
                        alive.Add(key)
                        row.Thumbnail = GetOrBuildThumbnail(key,
                            Function() ImageProcessor.BuildAnnotationThumbnail(row.Annotation, size.Width, size.Height,
                                                                              LayerThumbnailBoxSize))
                    End If
                ElseIf row.AdjustmentLayer IsNot Nothing Then
                    maskId = row.AdjustmentLayer.MaskId
                    ' Eine Korrekturebene HAT keinen eigenen Inhalt - ihre Maske ist ihr Inhalt. Sie
                    ' steht deshalb allein in der ersten Miniatur, statt zweimal nebeneinander.
                    row.Thumbnail = Nothing
                End If

                If String.IsNullOrEmpty(maskId) Then
                    row.MaskThumbnail = Nothing
                    Continue For
                End If
                Dim mask = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                          String.Equals(m.Id, maskId, StringComparison.Ordinal))
                If mask Is Nothing Then
                    row.MaskThumbnail = Nothing
                    Continue For
                End If
                Dim maskKey = "mask:" & maskId & ":" & ImageProcessor.MaskFingerprint(mask)
                alive.Add(maskKey)
                Dim thumb = GetOrBuildThumbnail(maskKey,
                    Function() ImageProcessor.BuildMaskThumbnail(mask, LayerThumbnailBoxSize))
                If row.AdjustmentLayer IsNot Nothing Then
                    row.Thumbnail = thumb
                    row.MaskThumbnail = Nothing
                Else
                    row.MaskThumbnail = thumb
                End If
            Next
            ' Was keine Zeile mehr braucht, wird freigegeben - sonst waechst der Speicher mit jeder
            ' geloeschten Ebene weiter.
            Dim stale = _layerThumbnails.Keys.Where(Function(k) Not alive.Contains(k)).ToList()
            For Each k In stale
                _layerThumbnails(k)?.Dispose()
                _layerThumbnails.Remove(k)
            Next
            watch.Stop()
            ' Nur melden, wenn wirklich gezeichnet wurde - sonst stuende bei jedem Aufbau eine Zeile
            ' im Protokoll, die nichts sagt.
            If _thumbnailsBuilt > 0 Then
                DiagnosticLogService.LogAlways("Editor.LayerThumbnails",
                                               $"gezeichnet={_thumbnailsBuilt} zeilen={_layerRows.Count} " &
                                               $"gespeichert={_layerThumbnails.Count} ms={watch.ElapsedMilliseconds}")
            End If
        End Sub

        ''' <summary>Die Miniaturen nach einer Aenderung der Einstellung neu bewerten.
        '''
        ''' BEWUSST nicht an `RefreshLayoutBindings` gehaengt: das ist der Weg, den auch die
        ''' Rueckkehr aus den Einstellungen geht, und dort etwas am geoeffneten Dokument anzufassen
        ''' waere ein schlechter Tausch fuer den Sofort-Effekt eines Schalters. Die Einstellung wird
        ''' ohnehin bei JEDEM Aufbau des Panels gelesen, sie wirkt also beim naechsten Anlass.</summary>
        Public Sub RefreshLayerRowThumbnailsFromSettings()
            RefreshLayerRowThumbnails()
        End Sub

        Private Sub DisposeLayerThumbnails()
            For Each bmp In _layerThumbnails.Values
                bmp?.Dispose()
            Next
            _layerThumbnails.Clear()
        End Sub

        ''' <summary>Kantenlaenge, in der eine Miniatur GERECHNET wird - vier Punkte kleiner als ihr
        ''' Kasten im Panel, damit der Rahmen sichtbar bleibt. Waechst der Kasten im XAML, waechst
        ''' dieser Wert mit: sonst wird ein zu kleines Bild hochgezogen und sieht weich aus.</summary>
        Private Const LayerThumbnailBoxSize As Integer = 32
        Private ReadOnly _layerThumbnails As New Dictionary(Of String, Bitmap)(StringComparer.Ordinal)

        ''' <summary>Zaehlt die Miniaturen, die seit dem letzten Zuruecksetzen wirklich GEZEICHNET
        ''' wurden - der Rest kam aus dem Zwischenspeicher.</summary>
        Private _thumbnailsBuilt As Integer

        Private Function GetOrBuildThumbnail(key As String, build As Func(Of SKBitmap)) As Bitmap
            Dim cached As Bitmap = Nothing
            If _layerThumbnails.TryGetValue(key, cached) Then Return cached
            _thumbnailsBuilt += 1
            Dim sk = build()
            If sk Is Nothing Then Return Nothing
            Try
                Dim bmp = ImageProcessor.ToAvaloniaBitmap(sk)
                If bmp IsNot Nothing Then _layerThumbnails(key) = bmp
                Return bmp
            Finally
                sk.Dispose()
            End Try
        End Function

        ''' <summary>Die gelesenen Eigenschaften, beim ersten Gebrauch bestimmt. NICHT als
        ''' Feldinitialisierer: der liefe vor der Ausnahmeliste, die er braucht, und ein Fehler in
        ''' einem statischen Initialisierer legt die ganze Klasse lahm - der Editor liess sich dann
        ''' gar nicht mehr bauen.</summary>
        Private Shared _thumbnailKeyProperties As System.Reflection.PropertyInfo() = Nothing

        Private Shared Function ThumbnailKeyProperties() As System.Reflection.PropertyInfo()
            If _thumbnailKeyProperties IsNot Nothing Then Return _thumbnailKeyProperties
            Dim ausgenommen As New HashSet(Of String)(StringComparer.Ordinal) From {
                "XPixels", "YPixels", "RotationDegrees", "Anchor", "ScaleWithImage",
                "Id", "GroupId", "CustomName", "SourceFileName", "WatermarkPresetName",
                "IsLocked", "IsVisible", "IsRenaming", "EditableName",
                "BlendMode", "BlendIncludesStroke", "Opacity",
                "MaskId", "ClipToLayerBelow", "Adjustments", "IconSource"}
            _thumbnailKeyProperties =
                GetType(ImageAnnotation).GetProperties(System.Reflection.BindingFlags.Public Or
                                                       System.Reflection.BindingFlags.Instance).
                    Where(Function(p) p.CanRead AndAlso p.GetIndexParameters().Length = 0).
                    Where(Function(p) Not ausgenommen.Contains(p.Name)).
                    OrderBy(Function(p) p.Name, StringComparer.Ordinal).ToArray()
            Return _thumbnailKeyProperties
        End Function

        ''' <summary>Woran die Miniatur eines Objekts erkennt, dass sie nicht mehr stimmt.
        '''
        ''' Gelesen werden ALLE Eigenschaften des Objekts ueber eine AUSNAHME-Liste - ein neues
        ''' Rezeptfeld ist damit von selbst dabei, bis es hier bewusst ausgenommen wird. Eine
        ''' handgepflegte Positivliste stand hier zuerst und war der Fehler: sie kannte Text und
        ''' Farbe, aber weder Textpfad noch Schatten noch Schein, und wer den Text auf eine Kurve
        ''' legte, sah in der Zeile weiter das Bild von vorhin.
        '''
        ''' BILLIG muss sie sein, denn beim Aufbau des Panels wird sie fuer JEDE Zeile gerechnet.
        ''' Der Aussehens-Schluessel des Objekt-Bitmap-Speichers taugt dafuer NICHT: er serialisiert
        ''' das ganze Objekt samt Anpassungssatz und allen Pinselstrichen und kostete an einer Ebene
        ''' mit 200 Strichen 0,77 ms - bei einem Stapel eine spuerbare Pause bei jedem
        ''' Ebenenwechsel. Sammlungen und lange Texte gehen deshalb nur mit ihrer GROESSE ein.
        '''
        ''' Drei bewusste Auslassungen:
        ''' - Lage und Drehung: die Miniatur zeigt den Inhalt, nicht die Stelle im Bild. Stuenden
        '''   sie drin, waere beim Verschieben bei jeder Mausbewegung neu zu zeichnen.
        ''' - Der Anpassungssatz des Objekts: die Miniatur zeichnet ueber `DrawAnnotationOnCanvas`
        '''   und zeigt ihn ohnehin nicht.
        ''' - Die Maskenkennung: die Maske steht als eigene Miniatur daneben.</summary>
        Private Shared Function AnnotationThumbnailFingerprint(a As ImageAnnotation) As String
            If a Is Nothing Then Return ""
            Dim sb As New Text.StringBuilder(256)
            For Each p In ThumbnailKeyProperties()
                Dim v As Object = Nothing
                Try
                    v = p.GetValue(a)
                Catch
                    Continue For
                End Try
                sb.Append(p.Name).Append("="c)
                If v Is Nothing Then
                    sb.Append("-"c)
                ElseIf TypeOf v Is String Then
                    ' Lange Texte nur mit Laenge und Streuwert: ein Pfad mit hundert Punkten steht
                    ' als Zeichenkette im Objekt und blaehte sonst jede Kennung auf.
                    Dim s = DirectCast(v, String)
                    If s.Length > 48 Then sb.Append(s.Length).Append("#"c).Append(s.GetHashCode()) Else sb.Append(s)
                ElseIf TypeOf v Is System.Collections.ICollection Then
                    sb.Append(DirectCast(v, System.Collections.ICollection).Count)
                ElseIf TypeOf v Is ObjectWarp Then
                    Dim w = DirectCast(v, ObjectWarp)
                    sb.Append(If(w Is Nothing OrElse w.IsEmpty, "-", w.Kind & ":" & w.GetHashCode()))
                Else
                    sb.Append(Convert.ToString(v, Globalization.CultureInfo.InvariantCulture))
                End If
                sb.Append("|"c)
            Next
            Return sb.ToString()
        End Function

        Private Sub RaiseLayerPanelSelectionChanged()
            ' Die EINE Stelle, an der das ViewModel eine Auswahlaenderung der Ebenenliste anstossen
            ' kann - und deshalb die richtige fuer die Aufschub-Klammer. Grund (an Avalonia 12.1.1
            ' dekompiliert): endet ein Auswahl-Commit mit GELEERTER Auswahl, erhoeht der
            ' LostSelection-Zweig von SelectionModel.CommitOperation den UpdateCount ohne Ruecknahme.
            ' Der ganze Rest des Commits - Bindungs-Rueckweg in den SelectedLayerRow-Setter,
            ' SelectionChanged - laeuft dann als "laufende Auswahlaenderung", und jede Aenderung an
            ' der gebundenen Zeilenliste wirft "Source collection was modified during selection
            ' update" (das leere Ebenenpanel aus dem Fehlerprotokoll). Mit der Klammer hier wird ein
            ' dabei angestossener Neuaufbau aufgeschoben, egal aus welchem der rund vierzig Aufrufer
            ' der Anstoss kam - einzeln zu klammern waere die Sorte Vollstaendigkeit, die beim
            ' naechsten Aufrufer wieder fehlt. Nach einem Avalonia-Update neu pruefen, ob das
            ' Fenster noch existiert.
            SuspendLayerRowRebuild()
            Try
                Me.RaisePropertyChanged(NameOf(IsMaskToolbarAccented))
                Me.RaisePropertyChanged(NameOf(HasDeselectableTarget))
                Me.RaisePropertyChanged(NameOf(SelectedLayerRow))
                Me.RaisePropertyChanged(NameOf(HasSelectedPanelLayer))
                Me.RaisePropertyChanged(NameOf(HasSelectedAdjustmentLayer))
                RaiseCurrentTargetChanged()
                Me.RaisePropertyChanged(NameOf(SelectedLayerOpacity))
                Me.RaisePropertyChanged(NameOf(IsGlobalAdjustmentsSelected))
                ' Die Gruppenregler haengen an der markierten KOPFZEILE - ohne diese Meldung zeigten
                ' sie nach einem Zeilenwechsel weiter die Werte der vorigen Gruppe.
                Me.RaisePropertyChanged(NameOf(ShowGroupProperties))
                Me.RaisePropertyChanged(NameOf(IsSelectedGroupRenderStep))
                Me.RaisePropertyChanged(NameOf(GroupOpacity))
                Me.RaisePropertyChanged(NameOf(GroupBlendMode))
                Me.RaisePropertyChanged(NameOf(SelectedGroupBlendModeOption))
                Me.RaisePropertyChanged(NameOf(SelectedLayerBlendModeOption))
                Me.RaisePropertyChanged(NameOf(CanEditSelectedLayerBlendMode))
                ' Der Masken-Knopf der Fußzeile haengt an der markierten Ebene - ohne das bliebe er
                ' nach einem Wechsel stehen, wie er beim vorigen war.
                RaiseAnnotationMaskStateChanged()
                RaiseLayerFooterStateChanged()
            Finally
                ResumeLayerRowRebuild(deferToDispatcher:=True)
            End Try
        End Sub

        ''' <summary>Wählt die feste globale Einstellungsebene im Ebenenpanel als Reglerziel.
        ''' Eine lokale Korrektur bleibt dabei im Dokument erhalten, wird aber nicht länger als
        ''' aktives Ziel für Anpassen/Farbe/Details/Filter verwendet.</summary>
        Public Sub SelectGlobalAdjustmentsTarget()
            CommitSelectionAdjustModeToModel()
            CommitObjectAdjustModeToModel()
            ' Eine bereits zu einer Ebene gemachte Auswahl ist nur noch eine Kopie - die Ebene
            ' traegt sie. Sie stehen zu lassen, waehrend das Reglerziel aufs ganze Bild zurueckgeht,
            ' hinterlaesst ein rotes Overlay bzw. Laufameisen ohne Ziel: sichtbar, aber ohne
            ' Bedeutung, und weder mit Esc noch mit einem Klick ins Leere loszuwerden.
            '
            ' Eine noch NICHT uebernommene Auswahl bleibt dagegen stehen - die ist Arbeit, die
            ' jemand gerade macht, und darf nicht bei einem Klick daneben verschwinden.
            If _hasActiveSelection AndAlso Not String.IsNullOrEmpty(_selectionPromotedLayerId) Then
                ClearSelection(captureUndo:=False)
            End If
            _selectedMaskedAdjustmentLayerId = ""
            ' Auch die ZUSATZLISTE der Mehrfachauswahl leeren - und die Aenderung melden. Ohne
            ' beides blieben die per Strg dazugenommenen Ebenen unsichtbar markiert stehen
            ' (IsMultiLayerSelection weiter True), und "Markierte Ebenen loeschen" traf Ebenen,
            ' die niemand mehr markiert sah. Der SelectedAnnotationIndex-Setter meldet selbst
            ' nichts, wenn der Index schon -1 ist - deshalb hier ausdruecklich.
            _extraSelectedAdjustmentLayers.Clear()
            _selectedLayerRow = Nothing
            SelectedAnnotationIndex = -1
            RaiseLayerPanelSelectionChanged()
            RaiseMultiSelectionChanged()
            RefreshSelectionAdjustMode()
        End Sub
        ' ── Ebenenmaske und Schnittmaske am OBJEKT ──────────────────────────────
        '
        ' Die Maske eines Objekts ist dieselbe ImageMask wie die einer Korrekturebene und liegt in
        ' derselben Liste. Damit gelten fuer sie ohne Zutun: Masken-Pinsel, rotes Overlay, weiche
        ' Kante, Umkehren, Speichern im Rezept und Undo. Neu ist allein, WER auf sie zeigt.

        ''' <summary>Das EINE Objekt, dem eine Ebenenmaske gilt - oder Nothing.
        '''
        ''' Eine Mehrfachauswahl bleibt aussen vor: eine gemeinsame Maske ueber mehrere Objekte waere
        ''' etwas anderes als je eine eigene, und welches von beidem gemeint ist, sagt keine der
        ''' Gesten.
        '''
        ''' Gezaehlt werden Objekte UND Korrekturebenen (<see cref="IsMultiLayerSelection"/>), nicht
        ''' nur die Objekte: ein Objekt zusammen mit einer markierten Korrektur sind zwei Ebenen, und
        ''' der Masken-Knopf der Fusszeile stand dabei weiter da, obwohl er nur auf eine von beiden
        ''' wirkt. Eine markierte Gruppe faellt damit ebenfalls heraus.</summary>
        Private Function MaskTargetAnnotation() As ImageAnnotation
            If IsMultiLayerSelection Then Return Nothing
            Return CurrentObject()
        End Function

        ''' <summary>Die GRUPPE, der eine Maske gilt: die markierte Kopfzeile. Eine Gruppenmaske
        ''' liegt auf der Gruppenebene und deckt damit alles ab, was die Gruppe zeichnet - anders
        ''' als eine Maske an jedem Mitglied, die an den Überlappungen sichtbar würde.</summary>
        Private Function MaskTargetGroup() As AnnotationGroup
            Return SelectedGroupForProperties()
        End Function

        Public ReadOnly Property CanAddAnnotationMask As Boolean
            Get
                Dim g = MaskTargetGroup()
                If g IsNot Nothing Then Return String.IsNullOrEmpty(g.MaskId)
                Dim a = MaskTargetAnnotation()
                Return a IsNot Nothing AndAlso String.IsNullOrEmpty(a.MaskId)
            End Get
        End Property

        Public ReadOnly Property SelectedAnnotationHasMask As Boolean
            Get
                Dim g = MaskTargetGroup()
                If g IsNot Nothing Then Return Not String.IsNullOrEmpty(g.MaskId)
                Dim a = MaskTargetAnnotation()
                Return a IsNot Nothing AndAlso Not String.IsNullOrEmpty(a.MaskId)
            End Get
        End Property

        ''' <summary>Eine Schnittmaske braucht eine Basis: das naechste sichtbare Objekt darunter,
        ''' das nicht selbst beschraenkt ist. Ganz unten im Stapel gibt es keine.</summary>
        Public ReadOnly Property CanClipSelectedAnnotation As Boolean
            Get
                ' Eine KORREKTUREBENE kann es auch, aber nur mit Anker: ohne ihn liegt sie im
                ' Basisbild, unter allen Objekten, und es gibt keine Ebene darunter (siehe
                ' MaskedAdjustmentLayer.ClipToLayerBelow).
                Dim adjustmentLayer = PasteTargetLayer()
                If adjustmentLayer IsNot Nothing Then
                    If adjustmentLayer.ClipToLayerBelow Then Return True
                    Return Not String.IsNullOrEmpty(adjustmentLayer.StackAboveAnnotationId)
                End If
                Dim a = MaskTargetAnnotation()
                If a Is Nothing Then Return False
                If a.ClipToLayerBelow Then Return True
                Dim index = _annotations.IndexOf(a)
                If index <= 0 Then Return False
                For i = index - 1 To 0 Step -1
                    Dim candidate = _annotations(i)
                    If candidate Is Nothing OrElse Not IsAnnotationRenderVisibleLive(candidate) Then Continue For
                    If candidate.ClipToLayerBelow Then Continue For
                    Return True
                Next
                Return False
            End Get
        End Property

        ''' <summary>Der Masken-Knopf in der Fußzeile des Ebenen-Panels tut ZWEI Dinge, je nachdem was
        ''' die markierte Ebene schon hat: anlegen oder bearbeiten. Zwei Knöpfe nebeneinander, von
        ''' denen immer einer grau ist, sagen dasselbe und kosten Platz.</summary>
        Public ReadOnly Property CanUseAnnotationMaskButton As Boolean
            Get
                Return CanAddAnnotationMask OrElse SelectedAnnotationHasMask
            End Get
        End Property

        Public ReadOnly Property AnnotationMaskButtonHint As String
            Get
                Return If(SelectedAnnotationHasMask,
                          LocalizationService.T("Ebenenmaske bearbeiten"),
                          LocalizationService.T("Ebenenmaske hinzufügen"))
            End Get
        End Property

        ''' <summary>Der eine Weg hinter Knopf und Maskensymbol: hat die Ebene noch keine Maske, wird
        ''' sie angelegt UND gleich zum Bearbeiten geöffnet - wer sie anlegt, will an sie heran.</summary>
        Public Sub UseAnnotationMask()
            ' DIE GRUPPE ZUERST. Eine markierte Gruppenzeile setzt ganz nebenbei auch die fuehrende
            ' KORREKTUREBENE der Gruppe (siehe den Kopfzeilen-Zweig von SelectedLayerRow); ohne diese
            ' Abzweigung oeffnete der Knopf deren Maske statt der Maske der Gruppe.
            If MaskTargetGroup() IsNot Nothing Then
                If CanAddAnnotationMask Then AddMaskToSelectedAnnotation()
                EditSelectedAnnotationMask()
                Return
            End If
            ' Der Maskenknopf im Ebenenpanel gilt auch fuer Korrektur- und Maskenebenen.
            ' Bei einer bereits ausgewaehlten solchen Zeile lief er vorher ausschliesslich ueber
            ' MaskTargetAnnotation (also ein Bildobjekt) und kehrte still zurueck. Dadurch konnte
            ' man die Maske nur beim zufaelligen Wechsel VON einer anderen Zeile oeffnen; danach
            ' sah der Knopf aus, als lasse sich eine bestehende Maske nicht mehr bearbeiten.
            Dim adjustmentLayer = PasteTargetLayer()
            If adjustmentLayer IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(adjustmentLayer.MaskId) Then
                ApplyAdjustmentLayerPresentation(adjustmentLayer)
                Return
            End If
            If CanAddAnnotationMask Then
                AddMaskToSelectedAnnotation()
                EditSelectedAnnotationMask()
                Return
            End If
            EditSelectedAnnotationMask()
        End Sub

        Public ReadOnly Property SelectedAnnotationIsClipped As Boolean
            Get
                Dim adjustmentLayer = PasteTargetLayer()
                If adjustmentLayer IsNot Nothing Then Return adjustmentLayer.ClipToLayerBelow
                Dim a = MaskTargetAnnotation()
                Return a IsNot Nothing AndAlso a.ClipToLayerBelow
            End Get
        End Property

        ''' <summary>Legt eine Ebenenmaske am markierten Objekt an. Eine LAUFENDE Auswahl ist die
        ''' Ansage, welcher Teil sichtbar bleiben soll; ohne Auswahl deckt die Maske erst einmal
        ''' alles, und der Masken-Pinsel nimmt danach weg.</summary>
        ' ── Masken übertragen ───────────────────────────────────────────────────
        '
        ' Eine Maske ist Arbeit: aufgezogen, nachgemalt, an der Kante gefeilt. Sie ein zweites Mal
        ' zu bauen, weil dieselbe Form auch auf einer anderen Ebene gebraucht wird, war bisher der
        ' einzige Weg. Kopiert wird immer eine ABSCHRIFT mit eigener Kennung - zwei Ebenen teilen
        ' sich nie eine Maske, sonst zoege das Nachmalen an der einen die andere mit.

        ''' Die abgelegte Abschrift. Sitzungszustand, gehoert NICHT ins Rezept.
        Private _copiedMask As ImageMask

        Public ReadOnly Property CanCopyMask As Boolean
            Get
                Return CurrentMaskForComponents() IsNot Nothing
            End Get
        End Property

        ''' <summary>Zielt das Einfuegen ueberhaupt irgendwohin? Ein markiertes Objekt geht vor: wer
        ''' eines angeklickt hat, meint es. Sonst die markierte Masken- oder Auswahlebene.</summary>
        Public ReadOnly Property CanPasteMask As Boolean
            Get
                If _copiedMask Is Nothing Then Return False
                Return MaskTargetAnnotation() IsNot Nothing OrElse PasteTargetLayer() IsNot Nothing
            End Get
        End Property

        Private Function PasteTargetLayer() As MaskedAdjustmentLayer
            If String.IsNullOrEmpty(_selectedMaskedAdjustmentLayerId) Then Return Nothing
            Return _maskedAdjustmentLayers.FirstOrDefault(Function(l) l IsNot Nothing AndAlso
                                                              l.Id = _selectedMaskedAdjustmentLayerId)
        End Function

        Public Sub CopyCurrentMask()
            Dim m = CurrentMaskForComponents()
            If m Is Nothing Then Return
            _copiedMask = m.Clone()
            Me.RaisePropertyChanged(NameOf(CanPasteMask))
            StatusText = LocalizationService.T("Maske kopiert")
        End Sub

        ''' <summary>Setzt die abgelegte Maske auf das markierte Objekt oder die markierte Ebene.
        '''
        ''' Als eigene Maske mit eigener Kennung, und die bisherige des Ziels wird ERSETZT. Ein
        ''' Zusammenfuehren waere eine zweite Bedeutung fuer denselben Handgriff; wer verbinden will,
        ''' haengt einen Bestandteil an.</summary>
        Public Sub PasteMaskToTarget()
            If _copiedMask Is Nothing Then Return
            Dim annotation = MaskTargetAnnotation()
            Dim layer = If(annotation Is Nothing, PasteTargetLayer(), Nothing)
            If annotation Is Nothing AndAlso layer Is Nothing Then Return
            PushUndo()
            Dim copy = _copiedMask.Clone()
            copy.Id = Guid.NewGuid().ToString("N")
            _imageMasks.Add(copy)
            If annotation IsNot Nothing Then
                annotation.MaskId = copy.Id
                RaiseAnnotationMaskStateChanged()
                RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(annotation))
            Else
                layer.MaskId = copy.Id
                LoadLayerMaskIntoSelection(layer)
            End If
            _hasChanges = True
            RaiseMaskComponentsChanged()
            RebuildLayerRows()
            AddHistoryEntry(LocalizationService.T("Maske eingefügt"))
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Die Maske als AUSWAHL laden: dieselbe Form, aber sie begrenzt statt abzustufen.
        '''
        ''' Der Standardweg, um eine einmal gebaute Form weiterzuverwenden - fuellen, kopieren, eine
        ''' zweite Ebene daraus machen. Die Maske selbst bleibt, wo sie ist.</summary>
        Public Sub LoadCurrentMaskAsSelection()
            Dim m = CurrentMaskForComponents()
            If m Is Nothing Then Return
            PushUndo()
            LoadMaskIntoSelection(m.Id, showAsMask:=False)
            ' Die geladene Auswahl haengt an keiner Ebene mehr - sonst schriebe der naechste Strich
            ' in die Maske zurueck, aus der sie nur ABGELEITET ist.
            _editingLayerMaskId = ""
            SetActiveSelectionIsMask(False)
            StatusText = LocalizationService.T("Maske als Auswahl geladen")
            RaiseMaskComponentsChanged()
        End Sub

        Public Sub AddMaskToSelectedAnnotation()
            ' DIE GRUPPE GEHT VOR: ist ihre Kopfzeile markiert, ist sie gemeint und nicht eines
            ' ihrer Mitglieder.
            Dim targetGroup = MaskTargetGroup()
            If targetGroup IsNot Nothing Then
                AddMaskToSelectedGroup(targetGroup)
                Return
            End If
            Dim a = MaskTargetAnnotation()
            If a Is Nothing OrElse Not String.IsNullOrEmpty(a.MaskId) Then Return
            CommitObjectAdjustModeToModel()
            PushUndo()
            Dim adj = BuildAdjustmentsFromFields()
            Dim maskName = LocalizationService.T("Ebenenmaske")
            Dim mask As ImageMask = Nothing
            If _hasActiveSelection Then mask = ImageProcessor.CreateSourceMaskFromSelection(adj, maskName)
            ' Ohne Auswahl deckt die Maske alles, was das OBJEKT ausmacht - und nur dessen Bereich.
            ' Vorher war es das ganze Bild: ein Alpha8-Raster in voller Quellgroesse (bei 45 MP also
            ' 45 MB fuer "alles sichtbar"), und das rote Overlay lag als gleichmaessige Flaeche ueber
            ' dem ganzen Foto, statt zu zeigen, worum es geht. Die Deckung selbst aendert sich dabei
            ' nicht: ausserhalb seines Bereichs gibt es vom Objekt nichts zu decken.
            If mask Is Nothing Then mask = CreateObjectCoverageMask(a, maskName)
            ' Rueckfall, falls sich der Bereich nicht bestimmen laesst: lieber die grosse Maske als
            ' gar keine.
            If mask Is Nothing Then mask = ImageProcessor.CreateFullCoverageMask(adj, maskName)
            If mask Is Nothing Then Return
            _imageMasks.Add(mask)
            a.MaskId = mask.Id
            If _hasActiveSelection Then ClearSelection(captureUndo:=False)
            _hasChanges = True
            RaiseAnnotationMaskStateChanged()
            RebuildLayerRows()
            AddHistoryEntry(LocalizationService.T("Ebenenmaske hinzugefügt"))
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(a))
        End Sub

        ''' <summary>Eine Deckung, die genau den Bereich des Objekts umfasst - der Ausgangspunkt einer
        ''' neuen Ebenenmaske ohne Auswahl.
        '''
        ''' Bezug ist das Rechteck, das das Objekt auf der Szene beruehrt (`ComputeAnnotationDirtyRect`),
        ''' nicht seine Kastenmasse: bei einem gedrehten Objekt ragen die Ecken aus dem unrotierten
        ''' Kasten heraus, und genau dort haette die Maske es beschnitten. Schatten und Schein sind
        ''' darin enthalten, und das ist richtig so - sie gehoeren zum Objekt.
        '''
        ''' Gebaut wird sie ueber denselben Weg wie eine Auswahlmaske, nur mit dem Objektrechteck
        ''' anstelle der Auswahl: auf einer KOPIE der Anpassungen, damit eine laufende Auswahl
        ''' unberuehrt bleibt. Damit liegt sie automatisch im Quellraum und traegt dieselben
        ''' Koordinaten wie jede andere Maske.</summary>
        ''' <summary>Legt die Maske der GRUPPE an. Eine laufende Auswahl ist die Ansage, welcher Teil
        ''' der Gruppe sichtbar bleiben soll; ohne Auswahl deckt sie erst einmal alles, was die
        ''' Gruppe zeichnet, und der Maskenpinsel nimmt danach weg.
        '''
        ''' Der Bereich kommt aus der VEREINIGUNG der Mitglieder - dieselbe Überlegung wie am Objekt,
        ''' wo die Maske nur dessen Bereich deckt statt des ganzen Bildes: ein bildgroßes Raster
        ''' kostet bei 45 Megapixeln 45 MB für „alles sichtbar", und das rote Overlay läge als
        ''' gleichmäßige Fläche über dem ganzen Foto.</summary>
        Private Sub AddMaskToSelectedGroup(group As AnnotationGroup)
            If group Is Nothing OrElse Not String.IsNullOrEmpty(group.MaskId) Then Return
            CommitObjectAdjustModeToModel()
            PushUndo()
            Dim adj = BuildAdjustmentsFromFields()
            Dim maskName = LocalizationService.T("Gruppenmaske")
            Dim mask As ImageMask = Nothing
            If _hasActiveSelection Then mask = ImageProcessor.CreateSourceMaskFromSelection(adj, maskName)
            If mask Is Nothing Then mask = CreateGroupCoverageMask(group, maskName)
            If mask Is Nothing Then mask = ImageProcessor.CreateFullCoverageMask(adj, maskName)
            If mask Is Nothing Then Return
            _imageMasks.Add(mask)
            group.MaskId = mask.Id
            If _hasActiveSelection Then ClearSelection(captureUndo:=False)
            _hasChanges = True
            RaiseAnnotationMaskStateChanged()
            RebuildLayerRows()
            AddHistoryEntry(LocalizationService.T("Gruppenmaske hinzugefügt"))
            RequestOverlayStateNotify()
            RefreshPreviewImmediately()
        End Sub

        Private Sub RemoveMaskFromSelectedGroup(group As AnnotationGroup)
            If group Is Nothing OrElse String.IsNullOrEmpty(group.MaskId) Then Return
            PushUndo()
            Dim maskId = group.MaskId
            group.MaskId = ""
            ' Wurde genau diese Maske gerade bearbeitet, muss das rote Overlay mit weg - sonst bleibt
            ' es ohne Ziel stehen.
            If String.Equals(_editingLayerMaskId, maskId, StringComparison.Ordinal) Then
                _editingLayerMaskId = ""
                If _hasActiveSelection Then ClearSelection(captureUndo:=False)
                PublishMaskBrushOverlay()
            End If
            RemoveMaskIfUnreferenced(maskId)
            _hasChanges = True
            RaiseAnnotationMaskStateChanged()
            RebuildLayerRows()
            AddHistoryEntry(LocalizationService.T("Gruppenmaske entfernt"))
            RequestOverlayStateNotify()
            RefreshPreviewImmediately()
        End Sub

        ''' <summary>Der Bereich, den eine Gruppe im Szenenraum einnimmt: die Vereinigung der
        ''' Rechtecke ihrer sichtbaren Mitglieder.
        '''
        ''' MITGLIEDER SIND AUCH DIE DER UNTERGRUPPEN (<c>AnnotationsInGroupTree</c>). Mit den
        ''' unmittelbaren allein blieb die Vereinigung bei einer Gruppe, die nur eine Untergruppe
        ''' enthaelt, LEER - die Maske fiel dann auf das ganze Bild zurueck, und bei gemischten
        ''' Gruppen fehlte der Teil in den Untergruppen (Befund 2026-08-09).</summary>
        Private Function CreateGroupCoverageMask(group As AnnotationGroup, maskName As String) As ImageMask
            If group Is Nothing Then Return Nothing
            Dim sceneSize = GetCurrentScenePixelSize()
            If sceneSize.Width <= 0 OrElse sceneSize.Height <= 0 Then Return Nothing
            Dim union = SKRectI.Empty
            For Each member In AnnotationsInGroupTree(group.Id)
                If member Is Nothing OrElse Not IsAnnotationRenderVisibleLive(member) Then Continue For
                Dim rect = ComputeSceneDirtyRectFor(member)
                If rect.Width <= 0 OrElse rect.Height <= 0 Then Continue For
                union = If(union.IsEmpty, rect, SKRectI.Union(union, rect))
            Next
            If union.IsEmpty OrElse union.Width <= 0 OrElse union.Height <= 0 Then Return Nothing
            Dim adj = BuildAdjustmentsFromFields()
            adj.SelectionMaskPngBase64 = ""
            adj.SelectionXPercent = union.Left / CDbl(sceneSize.Width) * 100.0
            adj.SelectionYPercent = union.Top / CDbl(sceneSize.Height) * 100.0
            adj.SelectionWidthPercent = union.Width / CDbl(sceneSize.Width) * 100.0
            adj.SelectionHeightPercent = union.Height / CDbl(sceneSize.Height) * 100.0
            adj.SelectionShapeMode = "Rectangle"
            adj.HasActiveSelection = True
            Return ImageProcessor.CreateSourceMaskFromSelection(adj, maskName)
        End Function

        Private Function CreateObjectCoverageMask(annotation As ImageAnnotation, maskName As String) As ImageMask
            If annotation Is Nothing Then Return Nothing
            Dim sceneSize = GetCurrentScenePixelSize()
            If sceneSize.Width <= 0 OrElse sceneSize.Height <= 0 Then Return Nothing
            Dim rect = ComputeSceneDirtyRectFor(annotation)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing
            Dim adj = BuildAdjustmentsFromFields()
            adj.SelectionMaskPngBase64 = ""
            adj.SelectionXPercent = rect.Left / CDbl(sceneSize.Width) * 100.0
            adj.SelectionYPercent = rect.Top / CDbl(sceneSize.Height) * 100.0
            adj.SelectionWidthPercent = rect.Width / CDbl(sceneSize.Width) * 100.0
            adj.SelectionHeightPercent = rect.Height / CDbl(sceneSize.Height) * 100.0
            adj.SelectionFeatherPixels = 0
            Return ImageProcessor.CreateSourceMaskFromSelection(adj, maskName)
        End Function

        ''' <summary>Bringt die Ebenenmaske des Objekts in den Masken-Pinsel: rotes Overlay, harte
        ''' Form malbar, weiche Kante zur Renderzeit. Derselbe Weg wie bei einer Maskenebene.</summary>
        Public Sub EditSelectedAnnotationMask()
            Dim targetGroup = MaskTargetGroup()
            Dim maskId As String
            If targetGroup IsNot Nothing Then
                If String.IsNullOrEmpty(targetGroup.MaskId) Then Return
                maskId = targetGroup.MaskId
            Else
                Dim a = MaskTargetAnnotation()
                If a Is Nothing OrElse String.IsNullOrEmpty(a.MaskId) Then Return
                maskId = a.MaskId
            End If
            ' ERST das Werkzeug, DANN die Maske laden. Andersherum wurde das rote Overlay
            ' veroeffentlicht, waehrend die Ansicht noch im vorigen Werkzeug stand - dort gehoert
            ' kein Auswahl-Overlay hin, sie versteckte es also sofort wieder, und der Werkzeugwechsel
            ' danach brachte nur noch ein Bild mit, das niemand mehr neu veroeffentlichte. Genau
            ' dieses Bild sah der Nutzer nicht: Maskenwerkzeug da, Overlay weg.
            ' Die Kennung wird vorher gemerkt, weil der Wechsel die Objektmarkierung veraendern darf.
            If _currentTool <> EditorTool.Mask Then CurrentTool = EditorTool.Mask
            ' Der Werkzeugwechsel startet auf VERSCHIEBEN - im Verlaufsmodus zoege der erste Zug
            ' einen neuen Verlauf auf, statt die Maske nachzubessern.
            MaskMode = "Brush"
            LoadMaskIntoSelection(maskId, showAsMask:=True)
        End Sub

        Public Sub RemoveSelectedAnnotationMask()
            Dim targetGroup = MaskTargetGroup()
            If targetGroup IsNot Nothing Then
                RemoveMaskFromSelectedGroup(targetGroup)
                Return
            End If
            Dim a = MaskTargetAnnotation()
            If a Is Nothing OrElse String.IsNullOrEmpty(a.MaskId) Then Return
            PushUndo()
            Dim maskId = a.MaskId
            a.MaskId = ""
            ' Wurde genau diese Maske gerade bearbeitet, muss das rote Overlay mit weg - sonst bleibt
            ' es ohne Ziel stehen (dieselbe Fehlerklasse wie beim Loeschen einer Verlaufsebene).
            If String.Equals(_editingLayerMaskId, maskId, StringComparison.Ordinal) Then
                _editingLayerMaskId = ""
                If _hasActiveSelection Then ClearSelection(captureUndo:=False)
                PublishMaskBrushOverlay()
            End If
            RemoveMaskIfUnreferenced(maskId)
            _hasChanges = True
            RaiseAnnotationMaskStateChanged()
            RebuildLayerRows()
            AddHistoryEntry(LocalizationService.T("Ebenenmaske entfernt"))
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(a))
        End Sub

        Public Sub ToggleClipSelectedAnnotation()
            ' Die markierte KORREKTUREBENE geht vor: sie ist es, die man gerade in der Hand hat.
            Dim adjustmentLayer = PasteTargetLayer()
            If adjustmentLayer IsNot Nothing Then
                If Not adjustmentLayer.ClipToLayerBelow AndAlso Not CanClipSelectedAnnotation Then Return
                PushUndo()
                adjustmentLayer.ClipToLayerBelow = Not adjustmentLayer.ClipToLayerBelow
                _hasChanges = True
                RaiseAnnotationMaskStateChanged()
                RebuildLayerRows()
                AddHistoryEntry(If(adjustmentLayer.ClipToLayerBelow,
                                   LocalizationService.T("Auf Ebene darunter beschränkt"),
                                   LocalizationService.T("Beschränkung aufgehoben")))
                SchedulePreviewUpdate()
                Return
            End If
            Dim a = MaskTargetAnnotation()
            If a Is Nothing Then Return
            If Not a.ClipToLayerBelow AndAlso Not CanClipSelectedAnnotation Then Return
            PushUndo()
            a.ClipToLayerBelow = Not a.ClipToLayerBelow
            _hasChanges = True
            RaiseAnnotationMaskStateChanged()
            RebuildLayerRows()
            AddHistoryEntry(If(a.ClipToLayerBelow,
                               LocalizationService.T("Auf Ebene darunter beschränkt"),
                               LocalizationService.T("Beschränkung aufgehoben")))
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(a))
        End Sub

        Private Sub RaiseAnnotationMaskStateChanged()
            Me.RaisePropertyChanged(NameOf(CanAddAnnotationMask))
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationHasMask))
            Me.RaisePropertyChanged(NameOf(CanClipSelectedAnnotation))
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationIsClipped))
            Me.RaisePropertyChanged(NameOf(CanUseAnnotationMaskButton))
            Me.RaisePropertyChanged(NameOf(AnnotationMaskButtonHint))
        End Sub

        ''' <summary>Erzeugt aus der aktiven Auswahl SOFORT eine Masken- oder Auswahlebene (leere
        ''' Anpassung), statt erst bei der ersten Regleränderung. Nutzt dieselbe Masken-Erzeugung und
        ''' Dedup wie der automatische Weg, also legt eine spätere Anpassung KEINE zweite Ebene an.
        '''
        ''' GEHOERT DIE AUSWAHL SCHON ZU EINER EBENE, legt ein weiterer Druck eine unabhaengige
        ''' Kopie derselben Form an. So kann man dieselbe Auswahl direkt fuer mehrere, getrennt
        ''' bearbeitbare Korrekturebenen verwenden.</summary>
        Public Sub CreateAdjustmentLayerFromSelection()
            TraceMask(Function() $"„Neue Masken-/Auswahlebene"" gedrückt: Auswahl aktiv={_hasActiveSelection}" &
                                 $" bearbeitet={Kurz(_editingLayerMaskId)} markiert={Kurz(_selectedMaskedAdjustmentLayerId)}" &
                                 $" promotet={Kurz(_selectionPromotedLayerId)}")
            If Not _hasActiveSelection Then Return
            PushUndo()
            Dim countBefore = _maskedAdjustmentLayers.Count
            Dim layer = PromoteActiveSelectionToLayer()
            If layer Is Nothing Then Return
            ' PromoteActiveSelectionToLayer findet bei einer offenen Ebenenmaske absichtlich deren
            ' vorhandene Ebene. Der ausdrueckliche Neu-Befehl bedeutet dort jedoch: eine zweite,
            ' unabhaengige Ebene mit derselben Maskenform.
            If _maskedAdjustmentLayers.Count = countBefore Then
                Dim name = If(layer.IsMaskLayer, LocalizationService.T("Maskenebene"), LocalizationService.T("Auswahlebene")) &
                           " " & (_maskedAdjustmentLayers.Count + 1).ToString()
                Dim copy = DuplicateAdjustmentLayer(layer, name)
                If copy Is Nothing Then Return
                TraceMask(Function() $"Kopie angelegt: aus Ebene={Kurz(layer.Id)} ({MaskTrace(layer.MaskId)})" &
                                     $" wurde Ebene={Kurz(copy.Id)} ({MaskTrace(copy.MaskId)})")
                layer = copy
                LoadMaskIntoSelection(copy.MaskId, copy.IsMaskLayer)
            End If
            _selectedMaskedAdjustmentLayerId = layer.Id
            RebuildLayerRows()
            _hasChanges = True
            TraceLayerInventory("„Neue Masken-/Auswahlebene""")
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Löst die Verknüpfung "diese Auswahl gehört bereits zu Ebene X". MUSS bei jeder
        ''' Auswahl-/Maskenänderung laufen, damit eine NEUE Auswahl eine eigene Ebene bekommt.</summary>
        Private Sub InvalidateSelectionLayerLink()
            _selectionPromotedLayerId = ""
        End Sub

        ''' <summary>Die Ebene, deren Maske gerade als Auswahl bearbeitet wird - oder Nothing.
        ''' Entschieden wird über die im Panel GEWÄHLTE Ebene; ein blindes LastOrDefault über die
        ''' MaskId träfe die zuletzt angelegte Ebene und schriebe Füllung/Maske auf die FALSCHE.</summary>
        Private Function LayerForEditedMask() As MaskedAdjustmentLayer
            If _editingLayerMaskId = "" Then Return Nothing
            Dim picked = _maskedAdjustmentLayers.FirstOrDefault(Function(l) l IsNot Nothing AndAlso
                            l.Id = _selectedMaskedAdjustmentLayerId AndAlso l.MaskId = _editingLayerMaskId)
            If picked IsNot Nothing Then Return picked
            Return _maskedAdjustmentLayers.LastOrDefault(Function(l) l IsNot Nothing AndAlso l.MaskId = _editingLayerMaskId)
        End Function

        ''' <summary>Erzeugt (oder findet) die dauerhafte Ebene der aktiven Auswahl und gibt sie
        ''' zurück - OHNE Undo, Neuaufbau und Vorschau (der Aufrufer steuert das). Dedup gegen den
        ''' automatischen Weg, also legt eine spätere Anpassung KEINE zweite Ebene an. Setzt
        ''' _editingLayerMaskId, damit Füllung und Anpassung anschließend auf DIESER Ebene landen.</summary>
        Private Function PromoteActiveSelectionToLayer() As MaskedAdjustmentLayer
            If Not _hasActiveSelection Then Return Nothing
            ' 1. Bewusst im Panel gewählte Ebene (deren Maske gerade bearbeitet wird).
            Dim edited = LayerForEditedMask()
            If edited IsNot Nothing Then
                TraceMask(Function() $"Auswahl gehört zur bearbeiteten Ebene={Kurz(edited.Id)} ({MaskTrace(edited.MaskId)})")
                Return edited
            End If
            ' 2. Diese (unveränderte) Auswahl wurde bereits promotet → GARANTIERT dieselbe Ebene weiterbenutzen,
            '    damit erneutes Füllen die vorhandene Füllung ersetzt statt eine zweite Ebene anzulegen.
            If _selectionPromotedLayerId <> "" Then
                Dim linked = _maskedAdjustmentLayers.FirstOrDefault(Function(l) l IsNot Nothing AndAlso l.Id = _selectionPromotedLayerId)
                If linked IsNot Nothing Then
                    TraceMask(Function() $"Auswahl gehört zur bereits erzeugten Ebene={Kurz(linked.Id)} ({MaskTrace(linked.MaskId)})")
                    Return linked
                End If
            End If
            Dim snapshot = BuildAdjustmentsFromFields()
            Dim mask = ImageProcessor.CreateSourceMaskFromSelection(snapshot,
                LocalizationService.T("Auswahlmaske") & " " & (_imageMasks.Count + 1).ToString())
            If mask Is Nothing Then Return Nothing
            Dim existingMask = _imageMasks.LastOrDefault(Function(m) m IsNot Nothing AndAlso
                m.SourceWidthPixels = mask.SourceWidthPixels AndAlso m.SourceHeightPixels = mask.SourceHeightPixels AndAlso
                m.Left = mask.Left AndAlso m.Top = mask.Top AndAlso m.Right = mask.Right AndAlso m.Bottom = mask.Bottom AndAlso
                m.PngBase64 = mask.PngBase64)
            Dim layer As MaskedAdjustmentLayer = Nothing
            If existingMask IsNot Nothing Then
                layer = _maskedAdjustmentLayers.LastOrDefault(Function(l) l IsNot Nothing AndAlso l.MaskId = existingMask.Id)
                Dim found = layer
                TraceMask(Function() $"Auswahl ist inhaltsgleich mit {MaskTrace(existingMask.Id)}; " &
                                     If(found Is Nothing, "keine Ebene darauf - es entsteht eine eigene Maske",
                                        $"weiterbenutzt wird Ebene={Kurz(found.Id)}"))
            End If
            If layer Is Nothing Then
                ' EIGENE Maske, auch wenn eine inhaltsgleiche schon da ist. Die Deduplizierung soll
                ' verhindern, dass dieselbe Auswahl ZWEI Ebenen bekommt - das erledigt der Zweig
                ' darueber, der die vorhandene Ebene weiterbenutzt. Hier gibt es KEINE solche Ebene,
                ' die gefundene Maske gehoert also etwas anderem (zum Beispiel einem Objekt). Sie
                ' mitzubenutzen band zwei Dinge an ein Raster: wer die Maske der einen Seite
                ' nachmalte oder umkehrte, aenderte die andere mit.
                _imageMasks.Add(mask)
                ApplyPendingRangeMetadata(mask)
                layer = New MaskedAdjustmentLayer With {
                    .Name = If(_activeSelectionIsMask, LocalizationService.T("Maskenebene"), LocalizationService.T("Auswahlebene")) & " " & (_maskedAdjustmentLayers.Count + 1).ToString(),
                    .MaskId = mask.Id,
                    .Adjustments = New ImageAdjustments(),
                    .IsMaskLayer = _activeSelectionIsMask
                }
                PlaceNewCorrectionLayerInBaseImage(layer)
                _maskedAdjustmentLayers.Add(layer)
            End If
            ' KEINE Dauerbindung von _editingLayerMaskId mehr setzen: die verhinderte zwar Dubletten beim
            ' späteren Anpassen, band aber die Auswahl PERMANENT an diese Ebene - legte man danach eine neue
            ' (Masken-)Ebene an und füllte sie, landete die Füllung wieder auf DIESER Ebene
            '. Die Dubletten-Vermeidung übernimmt die Masken-Deduplizierung oben und in
            ' RefreshSelectionAdjustMode (gleiche Auswahl → gleiche SourceSpace-Maske → gleiche Ebene).
            ' _editingLayerMaskId wird NUR noch von LoadLayerMaskIntoSelection gesetzt (bewusstes Auswählen
            ' einer Ebene im Panel), sodass Füllung/Anpassung dann gezielt DIESE Ebene treffen.
            _selectedMaskedAdjustmentLayerId = layer.Id
            _selectionPromotedLayerId = layer.Id
            TraceMask(Function() $"Neue Ebene aus der Auswahl: Ebene={Kurz(layer.Id)} ({MaskTrace(layer.MaskId)})")
            Return layer
        End Function

        ''' <summary>Liefert die Korrekturebene, auf der das Füll-Werkzeug arbeiten soll: die gerade im Panel
        ''' bearbeitete Ebene, sonst promotet es die aktive Auswahl zu einer neuen Ebene.</summary>
        Private Function EnsureCorrectionLayerForActiveSelection() As MaskedAdjustmentLayer
            Return PromoteActiveSelectionToLayer()
        End Function
    End Class

End Namespace
