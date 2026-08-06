Imports System
Imports System.Buffers
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.IO
Imports System.Linq
Imports System.Runtime.CompilerServices
Imports System.Threading
Imports System.Threading.Tasks
Imports SkiaSharp
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports System.Text.RegularExpressions
Imports System.Text.Json.Serialization
Imports System.Runtime.InteropServices
Imports QRCoder

' Die Datenmodelle der Objekte auf der Editor-Buehne: Pinselzuege, Retuschestellen, gemalte
' Striche, die Objektverzerrung, das Objekt selbst und seine Gruppe, dazu die Layout-Helfer.
' Sie lagen bis 2026-08-06 im Kopf von ImageProcessor.vb und sind reine Modelle - sie teilen
' keinen Zustand mit dem Prozessor und sind deshalb eine eigene Datei, keine Partial-Scheibe.
' Verwandte Modelle: ImageMaskModels.vb (Masken) und ImageAdjustmentsModels.vb (Rezept).
Namespace Services

    Public Structure StrokePoint
        Public ReadOnly X As Single
        Public ReadOnly Y As Single

        <JsonConstructor>
        Public Sub New(x As Single, y As Single)
            Me.X = x
            Me.Y = y
        End Sub
    End Structure

    ''' <summary>
    ''' Ein zusammenhängender Pinsel- oder Radiergummi-Zug in Bildpixeln.
    '''
    ''' Bewusst unveränderlich: ein einmal gezeichneter Zug ändert sich nie mehr. Dadurch dürfen
    ''' Undo-Schnappschüsse und ImageAnnotation.Clone dieselbe Instanz weiterreichen, statt die Punkte
    ''' zu kopieren - vorher lag jeder Strich als Zeichenkette im Text-Feld der Ebene und landete
    ''' vollständig in jedem nachfolgenden Undo-Eintrag.
    ''' </summary>
    Public NotInheritable Class BrushStroke
        Private ReadOnly _points As StrokePoint()

        ' Parametername und -typ müssen exakt zur Points-Eigenschaft passen, damit System.Text.Json den
        ' Zug konstruktorbasiert wiederherstellen kann (VB kann keine JsonConverter schreiben, siehe FpxService).
        <JsonConstructor>
        Public Sub New(points As IReadOnlyList(Of StrokePoint))
            _points = If(points, CType(Array.Empty(Of StrokePoint)(), IReadOnlyList(Of StrokePoint))).ToArray()
        End Sub

        Public ReadOnly Property Points As IReadOnlyList(Of StrokePoint)
            Get
                Return _points
            End Get
        End Property

        Public Function Scale(scaleX As Single, scaleY As Single) As BrushStroke
            Return New BrushStroke(_points.Select(Function(p) New StrokePoint(p.X * scaleX, p.Y * scaleY)).ToList())
        End Function
    End Class

    Public Class RetouchSpot
        Public Property XPixels As Single
        Public Property YPixels As Single
        Public Property RadiusPixels As Single
        Public Property StrengthPercent As Single = 100
        Public Property OpacityPercent As Single = 100
        Public Property FlowPercent As Single = 100
        Public Property Mode As String = "Blur"
        Public Property StrokeId As Integer = 0

        ''' Klonquelle in Bildpixeln: von hier wird die Textur kopiert. Ein negativer Wert bedeutet
        ''' "kein Quellpunkt gesetzt" - dann greift der Ringmittelwert-Rückfall in ApplyRetouch, der
        ''' die Umgebung des Ziels mittelt.
        Public Property SourceXPixels As Single = -1
        Public Property SourceYPixels As Single = -1

        Public ReadOnly Property HasCloneSource As Boolean
            Get
                Return SourceXPixels >= 0 AndAlso SourceYPixels >= 0
            End Get
        End Property

        Public Function Clone() As RetouchSpot
            Return New RetouchSpot With {
                .XPixels = XPixels, .YPixels = YPixels, .RadiusPixels = RadiusPixels,
                .StrengthPercent = StrengthPercent, .OpacityPercent = OpacityPercent,
                .FlowPercent = FlowPercent,
                .Mode = If(String.IsNullOrWhiteSpace(Mode), "Blur", Mode),
                .StrokeId = StrokeId,
                .SourceXPixels = SourceXPixels, .SourceYPixels = SourceYPixels
            }
        End Function
    End Class

    Public Class PixelPaintStroke
        Public Property Kind As String = "Brush"
        Public Property XPixels As Single = 0
        Public Property YPixels As Single = 0
        Public Property WidthPixels As Single = 1
        Public Property HeightPixels As Single = 1
        Public Property StrokeColor As String = "#FF000000"
        Public Property EraserFillColor As String = ""
        Public Property StrokeWidth As Single = 24
        Public Property Opacity As Single = 100
        Public Property BlendMode As String = "Normal"
        Public Property FlowPercent As Single = 100
        Public Property HardnessPercent As Single = 100
        Public Property BrushPreset As String = "soft"
        Public Property ShadowEnabled As Boolean = False
        Public Property ShadowOffsetXPercent As Single = 4
        Public Property ShadowOffsetYPercent As Single = 4
        Public Property ShadowBlur As Single = 6
        Public Property ShadowStrength As Single = 100
        Public Property ShadowColor As String = "#80000000"
        Public Property ShadowSizePercent As Single = 100
        Public Property GlowEnabled As Boolean = False
        Public Property GlowBlur As Single = 10
        Public Property GlowStrength As Single = 100
        Public Property GlowColor As String = "#FFFFFF00"
        Public Property Strokes As New List(Of BrushStroke)()

        Public Function Clone() As PixelPaintStroke
            Return New PixelPaintStroke With {
                .Kind = If(String.IsNullOrWhiteSpace(Kind), "Brush", Kind),
                .XPixels = XPixels,
                .YPixels = YPixels,
                .WidthPixels = WidthPixels,
                .HeightPixels = HeightPixels,
                .StrokeColor = If(StrokeColor, "#FF000000"),
                .EraserFillColor = If(EraserFillColor, ""),
                .StrokeWidth = StrokeWidth,
                .Opacity = Opacity,
                .BlendMode = If(String.IsNullOrWhiteSpace(BlendMode), "Normal", BlendMode),
                .FlowPercent = FlowPercent,
                .HardnessPercent = HardnessPercent,
                .BrushPreset = If(String.IsNullOrWhiteSpace(BrushPreset), "soft", BrushPreset),
                .ShadowEnabled = ShadowEnabled,
                .ShadowOffsetXPercent = ShadowOffsetXPercent,
                .ShadowOffsetYPercent = ShadowOffsetYPercent,
                .ShadowBlur = ShadowBlur,
                .ShadowStrength = ShadowStrength,
                .ShadowColor = If(ShadowColor, "#80000000"),
                .ShadowSizePercent = ShadowSizePercent,
                .GlowEnabled = GlowEnabled,
                .GlowBlur = GlowBlur,
                .GlowStrength = GlowStrength,
                .GlowColor = If(GlowColor, "#FFFFFF00"),
                .Strokes = New List(Of BrushStroke)(Strokes)
            }
        End Function

        Friend Function ToRenderAnnotation() As ImageAnnotation
            Return New ImageAnnotation With {
                .Kind = If(String.IsNullOrWhiteSpace(Kind), "Brush", Kind),
                .XPixels = XPixels,
                .YPixels = YPixels,
                .WidthPixels = WidthPixels,
                .HeightPixels = HeightPixels,
                .StrokeColor = If(StrokeColor, "#FF000000"),
                .EraserFillColor = If(EraserFillColor, ""),
                .StrokeWidth = StrokeWidth,
                .Opacity = Opacity,
                .BlendMode = If(String.IsNullOrWhiteSpace(BlendMode), "Normal", BlendMode),
                .FlowPercent = FlowPercent,
                .HardnessPercent = HardnessPercent,
                .BrushPreset = If(String.IsNullOrWhiteSpace(BrushPreset), "soft", BrushPreset),
                .ShadowEnabled = ShadowEnabled,
                .ShadowOffsetXPercent = ShadowOffsetXPercent,
                .ShadowOffsetYPercent = ShadowOffsetYPercent,
                .ShadowBlur = ShadowBlur,
                .ShadowStrength = ShadowStrength,
                .ShadowColor = If(ShadowColor, "#80000000"),
                .ShadowSizePercent = ShadowSizePercent,
                .GlowEnabled = GlowEnabled,
                .GlowBlur = GlowBlur,
                .GlowStrength = GlowStrength,
                .GlowColor = If(GlowColor, "#FFFFFF00"),
                .Strokes = New List(Of BrushStroke)(Strokes)
            }
        End Function
    End Class

    ''' <summary>Wie ein Objekt mitverzerrt wird. Alle Angaben in Prozent des BILDES, nicht des
    ''' Objekts: die Verzerrung wird ja am Bild eingestellt, und ein Objekt, das man danach
    ''' verschiebt, soll dorthin passen, wo es dann liegt.</summary>
    Public Class ObjectWarp
        ''' <summary>"Perspektive", "Gitter" oder "Linien". Leer heisst: keine.</summary>
        Public Property Kind As String = ""

        ''' <summary>Perspektive: die vier Ecken des Bildes nach der Verzerrung, in Bildprozent,
        ''' als x0,y0,x1,y1,x2,y2,x3,y3 (links oben, rechts oben, rechts unten, links unten).</summary>
        Public Property Corners As Double() = New Double() {}

        ''' <summary>Gitter: Spalten und Zeilen, dann je Knoten x,y in Bildprozent.</summary>
        Public Property Columns As Integer = 0
        Public Property Rows As Integer = 0
        Public Property Nodes As Double() = New Double() {}

        ''' <summary>Die zwölf Kontrollpunkte einer mit dem Verformen-Werkzeug erstellten
        ''' Hüllkurve. Das Raster bleibt die maßgebliche Renderdarstellung; diese Werte bewahren
        ''' zusätzlich die editierbaren Ecken und Bézier-Anfasser.</summary>
        Public Property EnvelopePoints As Double() = New Double() {}

        ''' <summary>Linien: je Linie QuelleAx, QuelleAy, QuelleBx, QuelleBy und dasselbe fuer das
        ''' Ziel, alles in Bildprozent.</summary>
        Public Property LineSource As Double() = New Double() {}
        Public Property LineTarget As Double() = New Double() {}

        Public ReadOnly Property IsEmpty As Boolean
            Get
                Select Case Kind
                    Case "Perspektive" : Return Corners Is Nothing OrElse Corners.Length <> 8
                    Case "Gitter" : Return Nodes Is Nothing OrElse Columns < 1 OrElse Rows < 1 OrElse
                                           Nodes.Length <> (Columns + 1) * (Rows + 1) * 2
                    Case "Linien" : Return LineSource Is Nothing OrElse LineTarget Is Nothing OrElse
                                           LineSource.Length < 4 OrElse LineSource.Length <> LineTarget.Length
                    Case Else : Return True
                End Select
            End Get
        End Property

        Public Function Clone() As ObjectWarp
            Return New ObjectWarp With {
                .Kind = Kind,
                .Corners = If(Corners Is Nothing, New Double() {}, CType(Corners.Clone(), Double())),
                .Columns = Columns, .Rows = Rows,
                .Nodes = If(Nodes Is Nothing, New Double() {}, CType(Nodes.Clone(), Double())),
                .EnvelopePoints = If(EnvelopePoints Is Nothing, New Double() {}, CType(EnvelopePoints.Clone(), Double())),
                .LineSource = If(LineSource Is Nothing, New Double() {}, CType(LineSource.Clone(), Double())),
                .LineTarget = If(LineTarget Is Nothing, New Double() {}, CType(LineTarget.Clone(), Double()))}
        End Function
    End Class

    Public Class ImageAnnotation
        Implements INotifyPropertyChanged

        Private _kind As String = "Text"
        Private _text As String = ""
        Private _imagePath As String = ""
        Private _sourceFileName As String = ""
        Private _xPixels As Single = 0
        Private _yPixels As Single = 0
        Private _widthPixels As Single = 480
        Private _heightPixels As Single = 180
        Private _fillColor As String = "#FFFFFFFF"
        Private _strokeColor As String = "#FF000000"
        Private _eraserFillColor As String = ""
        Private _strokeWidth As Single = 0
        Private _frameSizePercent As Single = 0
        Private _frameCornerRadiusPercent As Single = 0
        Private _frameEffect As String = "Einfach"
        Private _frameSymbol As String = ""
        Private _frameSymbolSpacingPercent As Single = 50
        Private _frameSymbolRotate As Boolean = False
        Private _fontSizePixels As Single = 48
        Private _fontFamily As String = "Arial"
        Private _opacity As Single = 100
        Private _blendMode As String = "Normal"
        ''' Gehoert die Kontur zum gemischten Teil des Objekts? True = wie bisher, das ganze Objekt geht
        ''' durch den Mischmodus. False = nur die Fuellung mischt sich mit dem Untergrund, die Kontur
        ''' liegt unveraendert (Normal) darueber - so bleibt z.B. ein Rahmen sichtbar, waehrend die
        ''' Flaeche multipliziert oder aufgehellt wird.
        Private _blendIncludesStroke As Boolean = True
        Private _flowPercent As Single = 100
        Private _rotationDegrees As Single = 0
        ' Spiegelung des Objekts um seine eigene Mitte (nicht um die Bildmitte): das Drehen-Werkzeug
        ' wirkt mit seinen vier Knöpfen auf das markierte Objekt, und Spiegeln können die Anfasser nicht.
        Private _flipHorizontal As Boolean = False
        Private _lockAspect As Boolean = True
        Private _flipVertical As Boolean = False
        Private _anchor As String = ""
        ' Wächst das Objekt mit dem Bild mit? Objekte leben im Pixelraum der QUELLE und werden auf die
        ' Ausgabemaße umgerechnet - beim Verkleinern schrumpft ein Wasserzeichen deshalb mit. False
        ' heißt: die Maße gelten für das AUSGEGEBENE Bild, das Wasserzeichen bleibt gleich groß.
        Private _scaleWithImage As Boolean = True
        Private _isVisible As Boolean = True
        Private _isLocked As Boolean = False
        ' Vom Nutzer im Ebenen-Panel vergebener Name. Leer = automatische Beschriftung aus Art/Text/Datei.
        Private _customName As String = ""
        ' Zugehörigkeit zu einer Objekt-Gruppe (leer = keine). Siehe GroupId.
        Private _groupId As String = ""
        ' Ebenenmaske des Objekts (leer = keine). Siehe MaskId.
        Private _maskId As String = ""
        ' Auf die Deckung der Ebene darunter beschränkt? Siehe ClipToLayerBelow.
        Private _clipToLayerBelow As Boolean = False
        ' Stützpunkte eines freien Pfades (leer = keiner). Siehe PathPoints.
        Private _pathPoints As String = ""
        Private _pathClosed As Boolean = False
        Private _id As String = ""
        ' Vorlagenname, aus dem ein Wasserzeichen entstanden ist. Leer = frei angelegt.
        Private _watermarkPresetName As String = ""
        ' Reiner UI-Zustand: gerade wird der Name inline bearbeitet (nicht persistiert, nicht geklont).
        Private _isRenaming As Boolean = False
        Private _hardnessPercent As Single = 100
        Private _brushPreset As String = "soft"
        Private _fillKind As String = "Solid"
        ' Text an Pfaden (nur Kind "Text"): "" = gerade, sonst "Arc"/"Circle"/"Wave". Der Pfad wird
        ' aus dem Objektrechteck abgeleitet - Selektion/Anfasser/Verschieben bleiben unveraendert.
        Private _textPathKind As String = ""
        Private _textPathBend As Single = 50
        Private _textPathStartOffset As Single
        Private _letterSpacingPercent As Single
        Private _bold As Boolean
        Private _italic As Boolean = False
        Private _fillColor2 As String = "#FFFFFFFF"
        Private _gradientAngleDegrees As Single = 0
        Private _gradientInverted As Boolean = False
        Private _shadowEnabled As Boolean = False
        Private _shadowOffsetXPercent As Single = 4
        Private _shadowOffsetYPercent As Single = 4
        Private _shadowBlur As Single = 6
        Private _shadowStrength As Single = 100
        Private _shadowColor As String = "#80000000"
        Private _shadowRounded As Boolean = False
        Private _shadowCornerRadiusPercent As Single = 20
        Private _shadowSizePercent As Single = 100
        Private _glowEnabled As Boolean = False
        Private _glowBlur As Single = 10
        Private _glowStrength As Single = 100
        Private _glowColor As String = "#FFFFFF00"

        ''' <summary>Eigene Pixel-Anpassungen dieses Objekts (Belichtung, Farbe, Details, Effekte, Filter …).
        ''' Nothing = keine. Ist ein Objekt markiert, bedienen die Regler der Werkzeuge Anpassen/Farbe/Details/
        ''' Effekte/Filter genau diesen Satz statt den des Bildes; ohne Markierung wirken sie wie immer aufs
        ''' ganze Bild. Enthält NUR Pixel-Anpassungen - Geometrie, Auswahl, Objekte bleiben leer (siehe
        ''' ImageAdjustments.PixelAdjustmentProperties).</summary>
        Public Property Adjustments As ImageAdjustments = Nothing

        ''' Nur bei Kind "Brush"/"Eraser" befüllt. Kein PropertyChanged: die Liste wächst ausschließlich
        ''' beim Malen, und die Vorschau wird dabei ohnehin explizit angestoßen.
        Public Property Strokes As New List(Of BrushStroke)()

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Public Property Kind As String
            Get
                Return _kind
            End Get
            Set(value As String)
                SetField(_kind, If(value, "Text"))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(LayerLabel)))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(IconSource)))
            End Set
        End Property

        Public Property Text As String
            Get
                Return _text
            End Get
            Set(value As String)
                SetField(_text, If(value, ""))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(LayerLabel)))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(IconSource)))
            End Set
        End Property

        Public Property ImagePath As String
            Get
                Return _imagePath
            End Get
            Set(value As String)
                SetField(_imagePath, If(value, ""))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(LayerLabel)))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(IconSource)))
            End Set
        End Property

        ''' <summary>Ursprünglicher Dateiname der eingefügten Bild-/SVG-Datei (nur für die Beschriftung).
        ''' Beim .fpx-Speichern wird ImagePath auf den bündel-internen Asset-Namen (assets/aN.ext)
        ''' umgeschrieben - ohne diesen Merker hießen alle Bild-Ebenen nach dem Wiederöffnen „Bild: a0.png".</summary>
        Public Property SourceFileName As String
            Get
                Return _sourceFileName
            End Get
            Set(value As String)
                SetField(_sourceFileName, If(value, ""))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(LayerLabel)))
            End Set
        End Property

        ''' <summary>Vom Nutzer vergebener Ebenenname; leer = automatische Beschriftung. Ändert LayerLabel.</summary>
        Public Property CustomName As String
            Get
                Return _customName
            End Get
            Set(value As String)
                SetField(_customName, If(value, ""))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(LayerLabel)))
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(EditableName)))
            End Set
        End Property

        ''' <summary>Stabile Kennung des Objekts. Nur intern gebraucht: eine Korrekturebene kann sich
        ''' über <see cref="MaskedAdjustmentLayer.StackAboveAnnotationId"/> darauf beziehen und damit
        ''' IN den Objektstapel einsortiert werden.</summary>
        Public Property Id As String
            Get
                If String.IsNullOrEmpty(_id) Then _id = Guid.NewGuid().ToString("N")
                Return _id
            End Get
            Set(value As String)
                _id = If(value, "")
            End Set
        End Property

        ''' <summary>Zugehörigkeit zu einer Objekt-Gruppe (leer = keine Gruppe). Die Gruppe selbst steht in
        ''' <see cref="ImageAdjustments.AnnotationGroups"/> und trägt Name, Sichtbarkeit und Klappzustand.
        ''' Mitglieder einer Gruppe liegen ZUSAMMENHÄNGEND in der Objektliste - die Gruppe ist damit auch
        ''' ein Z-Order-Block und lässt sich als Ganzes umsortieren.</summary>
        Public Property GroupId As String
            Get
                Return _groupId
            End Get
            Set(value As String)
                SetField(_groupId, If(value, ""))
            End Set
        End Property

        ''' <summary>Ebenenmaske DIESES Objekts: verweist auf eine <see cref="ImageMask"/> in
        ''' <see cref="ImageAdjustments.Masks"/>, genau wie eine Korrekturebene. Leer = keine Maske.
        '''
        ''' Sie begrenzt die Deckung des FERTIG gezeichneten Objekts (nach eigener Verzerrung und
        ''' eigenen Anpassungen) und wird dabei nicht gebacken - das Objekt bleibt unveraendert
        ''' aenderbar, und dieselbe Maske laesst sich mit dem Masken-Pinsel weiter bearbeiten.
        ''' Mehrere Ebenen duerfen sich eine Maske teilen; geloescht wird eine Maske erst, wenn
        ''' weder eine Korrekturebene noch ein Objekt mehr auf sie zeigt.</summary>
        Public Property MaskId As String
            Get
                Return _maskId
            End Get
            Set(value As String)
                SetField(_maskId, If(value, ""))
            End Set
        End Property

        ''' <summary>Freier Pfad: die Stützpunkte in PROZENT DES OBJEKTRECHTECKS, je Punkt sechs Zahlen
        ''' „ax,ay,einX,einY,ausX,ausY" (Stützpunkt, eingehender Griff, ausgehender Griff), Punkte durch
        ''' Semikolon getrennt. Ein ECKpunkt hat beide Griffe auf seinem Stützpunkt.
        '''
        ''' Prozent DES OBJEKTS und nicht des Bildes: damit machen Verschieben, Skalieren, Drehen,
        ''' Spiegeln und die eigene Verzerrung den Pfad ohne Zutun mit, genau wie bei jeder anderen
        ''' Form. Neu ist allein die Punktliste; am Objekt selbst ändert sich nichts.
        '''
        ''' Genutzt von der Objektart „Path" als Form UND von einem Textobjekt mit
        ''' <see cref="TextPathKind"/> = „Free" als Grundlinie.</summary>
        Public Property PathPoints As String
            Get
                Return _pathPoints
            End Get
            Set(value As String)
                SetField(_pathPoints, If(value, ""))
            End Set
        End Property

        ''' <summary>Ist der freie Pfad geschlossen? Offen wird er trotzdem gefüllt (Skia schließt zum
        ''' Füllen gedanklich) - der Unterschied liegt in der KONTUR, und die ist bei einem Pfad meist
        ''' das Sichtbare.</summary>
        Public Property PathClosed As Boolean
            Get
                Return _pathClosed
            End Get
            Set(value As Boolean)
                SetField(_pathClosed, value)
            End Set
        End Property

        ''' <summary>Schnittmaske: das Objekt erscheint nur dort, wo die Ebene DARUNTER deckt.
        '''
        ''' Basis ist das naechste sichtbare Objekt unter ihm, das nicht selbst beschraenkt ist -
        ''' mehrere beschraenkte Objekte uebereinander teilen sich also dieselbe Basis, wie in einer
        ''' Schnittmasken-Gruppe ueblich. Ohne Basis (das Objekt liegt ganz unten) bleibt der
        ''' Schalter wirkungslos, statt das Objekt verschwinden zu lassen.</summary>
        Public Property ClipToLayerBelow As Boolean
            Get
                Return _clipToLayerBelow
            End Get
            Set(value As Boolean)
                SetField(_clipToLayerBelow, value)
            End Set
        End Property

        ''' <summary>Name der Wasserzeichen-Vorlage, aus der dieses Objekt entstanden ist (leer = keine).
        ''' Damit füllt das Eigenschaften-Panel das Namensfeld wieder vor, wenn das Objekt erneut markiert
        ''' wird - erneutes Speichern überschreibt dann dieselbe Vorlage, statt eine zweite anzulegen.</summary>
        Public Property WatermarkPresetName As String
            Get
                Return _watermarkPresetName
            End Get
            Set(value As String)
                SetField(_watermarkPresetName, If(value, ""))
            End Set
        End Property

        ''' <summary>Reiner UI-Zustand: die Ebene wird im Panel gerade inline umbenannt (steuert die
        ''' Sichtbarkeit von Beschriftung vs. Eingabefeld). Wird nicht gespeichert oder geklont.</summary>
        Public Property IsRenaming As Boolean
            Get
                Return _isRenaming
            End Get
            Set(value As Boolean)
                SetField(_isRenaming, value)
            End Set
        End Property

        ''' <summary>Der bearbeitbare Rohname (= CustomName). BeginLayerRename füllt ihn beim Start mit der
        ''' aktuellen Beschriftung vor, damit das Eingabefeld sich wie ein normales Textfeld verhält. Leert
        ''' der Nutzer das Feld, fällt die Ebene auf die automatische Beschriftung zurück.</summary>
        Public Property EditableName As String
            Get
                Return _customName
            End Get
            Set(value As String)
                CustomName = If(value, "")
            End Set
        End Property

        Public ReadOnly Property LayerLabel As String
            Get
                If Not String.IsNullOrWhiteSpace(_customName) Then Return _customName
                ' Der TYP-Name wird uebersetzt, ein selbst gegebener Name nicht - der steht oben und
                ' kehrt vorher zurueck. So heisst eine Ebene in jeder Sprache nach ihrer Art, waehrend
                ' eine umbenannte Ebene ihren Namen behaelt.
                Dim baseLabel = LocalizationService.T(GermanKindLabel(_kind))
                Dim isSelectionKind = _kind IsNot Nothing AndAlso
                    (_kind.Equals("SelectionFill", StringComparison.OrdinalIgnoreCase) OrElse _kind.Equals("SelectionImage", StringComparison.OrdinalIgnoreCase))
                If isSelectionKind Then
                    Return If(String.IsNullOrWhiteSpace(_text), baseLabel, _text)
                End If
                Dim isTextual = _kind IsNot Nothing AndAlso
                    (_kind.Equals("Text", StringComparison.OrdinalIgnoreCase) OrElse _kind.Equals("Watermark", StringComparison.OrdinalIgnoreCase))
                If isTextual AndAlso Not String.IsNullOrWhiteSpace(_text) Then
                    Dim preview = _text.Trim()
                    If preview.Length > 18 Then preview = preview.Substring(0, 18) & "…"
                    Return $"{baseLabel}: {preview}"
                End If
                If _kind IsNot Nothing AndAlso _kind.Equals("Image", StringComparison.OrdinalIgnoreCase) AndAlso Not String.IsNullOrWhiteSpace(_imagePath) Then
                    Return $"{baseLabel}: {If(String.IsNullOrWhiteSpace(_sourceFileName), IO.Path.GetFileName(_imagePath), _sourceFileName)}"
                End If
                If _kind IsNot Nothing AndAlso _kind.Equals("Svg", StringComparison.OrdinalIgnoreCase) AndAlso Not String.IsNullOrWhiteSpace(_imagePath) Then
                    Return GetSvgLayerLabel(If(String.IsNullOrWhiteSpace(_sourceFileName), _imagePath, _sourceFileName))
                End If
                Return baseLabel
            End Get
        End Property

        Private Shared Function GetSvgLayerLabel(imagePath As String) As String
            Dim sourceName = FormatIconDisplayName(imagePath)
            Dim tag = LocalizationService.Tag(sourceName)
            Return If(String.IsNullOrWhiteSpace(tag), sourceName, tag)
        End Function

        Private Shared Function FormatIconDisplayName(assetPath As String) As String
            Dim fileName = IO.Path.GetFileNameWithoutExtension(assetPath)
            Dim m = Regex.Match(fileName, "^\d+_(?<rest>.+)$")
            Dim name = If(m.Success, m.Groups("rest").Value, fileName)
            Return name.Replace("_", " ").Replace("-", " ")
        End Function

        Public ReadOnly Property IconSource As String
            Get
                Const base As String = "avares://FerrumPix/Assets/Icons/"
                If _kind IsNot Nothing AndAlso _kind.Equals("Svg", StringComparison.OrdinalIgnoreCase) Then
                    Return If(String.IsNullOrWhiteSpace(_imagePath), base & "outline/rectangle.svg", _imagePath)
                End If
                If _kind IsNot Nothing AndAlso _kind.Equals("Symbol", StringComparison.OrdinalIgnoreCase) Then
                    Select Case _text
                        Case "♥" : Return base & "outline/heart.svg"
                        Case "✓" : Return base & "outline/check.svg"
                        Case Else : Return base & "outline/star.svg"
                    End Select
                End If
                Select Case If(_kind, "").Trim().ToLowerInvariant()
                    Case "text" : Return base & "outline/text-size.svg"
                    Case "watermark" : Return "avares://FerrumPix/Assets/Icons/outline/rubber-stamp.svg"
                    Case "image", "selectionimage" : Return base & "outline/photo.svg"
                    Case "qr", "qrcode", "qr-code" : Return base & "outline/qrcode.svg"
                    Case "rectangle", "rect", "selectionfill" : Return base & "outline/rectangle.svg"
                    Case "roundedrectangle", "rounded-rectangle" : Return base & "outline/square-rounded.svg"
                    Case "square" : Return base & "outline/square.svg"
                    Case "triangle" : Return base & "outline/triangle.svg"
                    Case "ellipse", "circle" : Return base & "outline/oval.svg"
                    Case "cone" : Return base & "outline/cone.svg"
                    Case "pyramid" : Return base & "outline/diamond.svg"
                    Case "trapezoid" : Return base & "outline/trapezoid.svg"
                    Case "diamond" : Return base & "outline/square-rotated.svg"
                    Case "polygon" : Return base & "outline/hexagon.svg"
                    Case "star" : Return base & "outline/star.svg"
                    Case "doublestar", "double-star" : Return base & "outline/eight-point-star.svg"
                    Case "spiral" : Return base & "outline/spiral.svg"
                    Case "droplet" : Return base & "outline/droplet-shape.svg"
                    Case "ellipsespeechbubble", "ellipse-speech-bubble" : Return base & "outline/ellipse-speech-bubble-shape.svg"
                    Case "rectspeechbubble", "rect-speech-bubble" : Return base & "outline/message.svg"
                    Case "speechbubble", "speech-bubble", "sprechblase", "bubble" : Return base & "outline/speech-bubble-shape.svg"
                    Case "heart" : Return base & "outline/heart.svg"
                    Case "cloud" : Return base & "outline/cloud.svg"
                    Case "path" : Return base & "outline/vector-bezier.svg"
                    Case "line" : Return base & "outline/line-shape.svg"
                    Case "arrow" : Return base & "outline/arrow-right.svg"
                    Case "frame" : Return base & "outline/frame.svg"
                    Case "brush" : Return base & "outline/brush.svg"
                    Case "eraser" : Return "avares://FerrumPix/Assets/Icons/outline/eraser.svg"
                    Case Else : Return base & "outline/rectangle.svg"
                End Select
            End Get
        End Property

        Friend Shared Function GermanKindLabel(kind As String) As String
            Select Case If(kind, "").Trim().ToLowerInvariant()
                Case "text" : Return "Text"
                Case "watermark" : Return "Wasserzeichen"
                Case "image" : Return "Bild"
                Case "rectangle", "rect" : Return "Rechteck"
                Case "roundedrectangle", "rounded-rectangle" : Return "Abgerundetes Rechteck"
                Case "selectionfill", "selectionimage" : Return "Auswahl"
                Case "ellipse", "circle" : Return "Ellipse"
                Case "square" : Return "Quadrat"
                Case "triangle" : Return "Dreieck"
                Case "cone" : Return "Kegel"
                Case "pyramid" : Return "Pyramide"
                Case "trapezoid" : Return "Trapez"
                Case "diamond" : Return "Raute"
                Case "polygon" : Return "Polygon"
                Case "star" : Return "Stern"
                Case "doublestar", "double-star" : Return "Doppelstern"
                Case "spiral" : Return "Spirale"
                Case "droplet" : Return "Tropfen"
                Case "ellipsespeechbubble", "ellipse-speech-bubble" : Return "Ellipse Sprechblase"
                Case "rectspeechbubble", "rect-speech-bubble" : Return "Rechteck Sprechblase"
                Case "speechbubble", "speech-bubble", "sprechblase", "bubble" : Return "Sprechblase"
                Case "heart" : Return "Herz"
                Case "cloud" : Return "Wolke"
                Case "frame" : Return "Rahmen"
                Case "path" : Return "Pfad"
                Case "line" : Return "Linie"
                Case "arrow" : Return "Pfeil"
                Case "brush" : Return "Pinsel"
                Case "eraser" : Return "Radiergummi"
                Case "symbol" : Return "Symbol"
                Case "qr", "qrcode", "qr-code" : Return "QR-Code"
                Case Else : Return If(String.IsNullOrWhiteSpace(kind), "Ebene", kind)
            End Select
        End Function

        Public Property XPixels As Single
            Get
                Return _xPixels
            End Get
            Set(value As Single)
                SetField(_xPixels, value)
            End Set
        End Property

        Public Property YPixels As Single
            Get
                Return _yPixels
            End Get
            Set(value As Single)
                SetField(_yPixels, value)
            End Set
        End Property

        Public Property WidthPixels As Single
            Get
                Return _widthPixels
            End Get
            Set(value As Single)
                SetField(_widthPixels, value)
            End Set
        End Property

        Public Property HeightPixels As Single
            Get
                Return _heightPixels
            End Get
            Set(value As Single)
                SetField(_heightPixels, value)
            End Set
        End Property

        Public Property FillColor As String
            Get
                Return _fillColor
            End Get
            Set(value As String)
                SetField(_fillColor, If(value, "#FFFFFFFF"))
            End Set
        End Property

        Public Property StrokeColor As String
            Get
                Return _strokeColor
            End Get
            Set(value As String)
                SetField(_strokeColor, If(value, "#FF000000"))
            End Set
        End Property

        ''' Farbe, in die der Radiergummi radiert. Leer = altes Verhalten: transparent ausstanzen.
        Public Property EraserFillColor As String
            Get
                Return _eraserFillColor
            End Get
            Set(value As String)
                SetField(_eraserFillColor, If(value, ""))
            End Set
        End Property

        Public Property StrokeWidth As Single
            Get
                Return _strokeWidth
            End Get
            Set(value As Single)
                SetField(_strokeWidth, value)
            End Set
        End Property

        ''' <summary>Staerke des Rahmens in Prozent der kuerzeren Bildkante (0 bis 25). Nur fuer
        ''' Objekte der Art "Frame". Der Rahmen ist ein Objekt wie Text und Form und kein Schritt
        ''' der Pixelkette mehr: nur so steht er in der Ebenenliste, laesst sich abschalten und
        ''' verschwindet nicht mit den ausgeblendeten Globalkorrekturen.</summary>
        Public Property FrameSizePercent As Single
            Get
                Return _frameSizePercent
            End Get
            Set(value As Single)
                SetField(_frameSizePercent, value)
            End Set
        End Property

        ''' <summary>Eckenrundung des Rahmens in Prozent (0 bis 100).</summary>
        Public Property FrameCornerRadiusPercent As Single
            Get
                Return _frameCornerRadiusPercent
            End Get
            Set(value As Single)
                SetField(_frameCornerRadiusPercent, value)
            End Set
        End Property

        ''' <summary>Art des Rahmens: "Einfach", "Doppelt", "Gestrichelt", "Punktiert", "Gezackt"
        ''' oder "Wellig".</summary>
        Public Property FrameEffect As String
            Get
                Return _frameEffect
            End Get
            Set(value As String)
                SetField(_frameEffect, If(value, "Einfach"))
            End Set
        End Property

        ''' <summary>Statt einer Linie ein SYMBOL entlang des Rahmens stempeln. Leer heisst: Linie
        ''' wie bisher. Der Wert ist eine der Formen, die es als Objekt auch gibt ("Star", "Heart",
        ''' …) - gezeichnet wird sie mit derselben Routine, es gibt also kein zweites Aussehen.
        '''
        ''' Die Rahmenart bleibt dabei der PFAD: "Einfach" reiht die Symbole gerade auf, "Wellig"
        ''' auf einer Welle, "Doppelt" in zwei Reihen.</summary>
        Public Property FrameSymbol As String
            Get
                Return _frameSymbol
            End Get
            Set(value As String)
                SetField(_frameSymbol, If(value, ""))
            End Set
        End Property

        ''' <summary>Abstand der Symbole in Prozent ihrer Groesse. 0 heisst: sie beruehren sich.</summary>
        Public Property FrameSymbolSpacingPercent As Single
            Get
                Return _frameSymbolSpacingPercent
            End Get
            Set(value As Single)
                SetField(_frameSymbolSpacingPercent, value)
            End Set
        End Property

        ''' <summary>Dreht jedes Symbol in die Laufrichtung des Rahmens. Aus heisst: alle stehen
        ''' aufrecht - bei Herzen und Sternen meist das Gewollte.</summary>
        Public Property FrameSymbolRotate As Boolean
            Get
                Return _frameSymbolRotate
            End Get
            Set(value As Boolean)
                SetField(_frameSymbolRotate, value)
            End Set
        End Property

        Public Property FontSizePixels As Single
            Get
                Return _fontSizePixels
            End Get
            Set(value As Single)
                SetField(_fontSizePixels, value)
            End Set
        End Property

        Public Property FontFamily As String
            Get
                Return _fontFamily
            End Get
            Set(value As String)
                SetField(_fontFamily, If(value, "Arial"))
            End Set
        End Property

        Public Property Opacity As Single
            Get
                Return _opacity
            End Get
            Set(value As Single)
                SetField(_opacity, value)
            End Set
        End Property

        Public Property BlendMode As String
            Get
                Return _blendMode
            End Get
            Set(value As String)
                SetField(_blendMode, If(String.IsNullOrWhiteSpace(value), "Normal", value))
            End Set
        End Property

        ''' <summary>Wird die Kontur mitgemischt? Siehe _blendIncludesStroke.</summary>
        ''' <summary>Die Verzerrung, die dieses Objekt mitmacht - oder Nothing.
        '''
        ''' Sie gehoert ins REZEPT und nicht in die Pixel. Beim Bild wird eine Gitter- oder
        ''' Linienverzerrung gebacken, weil dort jeder Bildpunkt einzeln wandert und sich das nicht
        ''' als Zahl aufheben laesst. Ein Objekt dagegen wird bei jedem Render neu gezeichnet - es
        ''' kann seine Verzerrung als Angabe behalten und bleibt damit aenderbar: Text laesst sich
        ''' weiter tippen, ein Bildobjekt weiter austauschen.</summary>
        Public Property Warp As ObjectWarp

        ''' <summary>Die EIGENE Verzerrung dieses Objekts - unabhaengig vom Bild.
        '''
        ''' Der Unterschied zu <see cref="Verzerrung"/> ist der Bezug: die dort ist in BILDprozent
        ''' und kommt vom Verzerren des Bildes, das Objekt macht sie nur mit. Diese hier ist in
        ''' Prozent des OBJEKTS und gehoert ihm allein - verschiebt man das Objekt, wandert sie mit,
        ''' statt sich zu aendern. Deshalb zwei Felder und nicht eines: sie beantworten verschiedene
        ''' Fragen und muessen verschieden mitwandern.</summary>
        Public Property OwnWarp As ObjectWarp

        Public Property BlendIncludesStroke As Boolean
            Get
                Return _blendIncludesStroke
            End Get
            Set(value As Boolean)
                SetField(_blendIncludesStroke, value)
            End Set
        End Property

        Public Property FlowPercent As Single
            Get
                Return _flowPercent
            End Get
            Set(value As Single)
                SetField(_flowPercent, value)
            End Set
        End Property

        Public Property RotationDegrees As Single
            Get
                Return _rotationDegrees
            End Get
            Set(value As Single)
                SetField(_rotationDegrees, value)
            End Set
        End Property

        Public Property FlipHorizontal As Boolean
            Get
                Return _flipHorizontal
            End Get
            Set(value As Boolean)
                SetField(_flipHorizontal, value)
            End Set
        End Property

        ''' Seitenverhältnis beim Grössenziehen beibehalten - relevant für Bild-Objekte und
        ''' Wasserzeichen-Bilder (wie "Seitenverhältnis beibehalten" bei Bildgrösse). Standard AN:
        ''' ein verzerrtes Foto ist praktisch nie gewollt; abschalten bleibt jederzeit möglich.
        Public Property LockAspect As Boolean
            Get
                Return _lockAspect
            End Get
            Set(value As Boolean)
                SetField(_lockAspect, value)
            End Set
        End Property

        Public Property FlipVertical As Boolean
            Get
                Return _flipVertical
            End Get
            Set(value As Boolean)
                SetField(_flipVertical, value)
            End Set
        End Property

        Public Property Anchor As String
            Get
                Return _anchor
            End Get
            Set(value As String)
                SetField(_anchor, If(value, ""))
            End Set
        End Property

        ''' <summary>Nur für verankerte Wasserzeichen ausgewertet: True (Vorgabe) rechnet Größe,
        ''' Schriftgröße und Randabstand wie bei jedem Objekt von der Quellauflösung auf die
        ''' Ausgabegröße um - beim Verkleinern schrumpft das Wasserzeichen mit. False lässt sie
        ''' unverändert, die Zahlen gelten dann für das fertige Bild.</summary>
        Public Property ScaleWithImage As Boolean
            Get
                Return _scaleWithImage
            End Get
            Set(value As Boolean)
                SetField(_scaleWithImage, value)
            End Set
        End Property

        ''' <summary>GESPERRT: keine geometrischen Änderungen mehr an diesem Objekt - kein Verschieben,
        ''' Skalieren, Drehen oder Spiegeln, weder per Maus noch über die Regler, und auch nicht als
        ''' Teil einer Gruppen-Transformation. Auswählen, Sichtbarkeit und Aussehen bleiben möglich;
        ''' der Renderer kennt die Sperre gar nicht.</summary>
        Public Property IsLocked As Boolean
            Get
                Return _isLocked
            End Get
            Set(value As Boolean)
                SetField(_isLocked, value)
            End Set
        End Property

        Public Property IsVisible As Boolean
            Get
                Return _isVisible
            End Get
            Set(value As Boolean)
                SetField(_isVisible, value)
            End Set
        End Property

        Public Property HardnessPercent As Single
            Get
                Return _hardnessPercent
            End Get
            Set(value As Single)
                SetField(_hardnessPercent, value)
            End Set
        End Property

        ' Pinsel-Variante nur für Kind="Brush": "soft" (weicher Rundpinsel, Standard) plus die
        ' texturierten Stufe-2-Presets "acrylic"/"sandpaper"/"pencil" (Korn-Textur) und "marker"
        ' (harte, halbtransparente Chisel-Kante). Siehe DrawBrushStroke. Radiergummi ignoriert das.
        Public Property BrushPreset As String
            Get
                Return _brushPreset
            End Get
            Set(value As String)
                SetField(_brushPreset, If(String.IsNullOrWhiteSpace(value), "soft", value.Trim().ToLowerInvariant()))
            End Set
        End Property

        ' "Solid", "LinearGradient" oder "RadialGradient" - nur für Kind="Rectangle"/"Ellipse" relevant,
        ' siehe DrawShape/CreateFillGradientShader in ApplyAnnotations.
        Public Property FillKind As String
            Get
                Return _fillKind
            End Get
            Set(value As String)
                SetField(_fillKind, If(String.IsNullOrWhiteSpace(value), "Solid", value))
            End Set
        End Property

        ''' <summary>Pfadform fuer Text: "" (gerade), "Arc", "Circle" oder "Wave" - siehe BuildTextPath.</summary>
        Public Property TextPathKind As String
            Get
                Return _textPathKind
            End Get
            Set(value As String)
                SetField(_textPathKind, If(value, ""))
            End Set
        End Property

        ''' <summary>Kruemmung -100..100: bei Bogen/Welle Staerke und Richtung der Biegung,
        ''' beim Kreis die Laufrichtung (negativ = innen/gegen den Uhrzeigersinn).</summary>
        Public Property TextPathBend As Single
            Get
                Return _textPathBend
            End Get
            Set(value As Single)
                SetField(_textPathBend, Math.Max(-100.0F, Math.Min(100.0F, value)))
            End Set
        End Property

        ''' <summary>Startversatz auf dem Pfad in Prozent (0-100): beim Kreis der Startwinkel,
        ''' bei Bogen/Welle die Verschiebung entlang des Pfades.</summary>
        Public Property TextPathStartOffset As Single
            Get
                Return _textPathStartOffset
            End Get
            Set(value As Single)
                SetField(_textPathStartOffset, Math.Max(0.0F, Math.Min(100.0F, value)))
            End Set
        End Property

        ''' <summary>Zeichenabstand in PROZENT DER SCHRIFTGROESSE (-20 bis 200). Prozent statt
        ''' Pixel, damit der Abstand beim Skalieren des Objekts mitwaechst - sonst risse der Text
        ''' bei grosser Schrift auseinander und klebte bei kleiner zusammen.
        ''' Achtung: bei einem Wert ungleich 0 werden die Zeichen EINZELN gesetzt; Kerning und
        ''' Ligaturen entfallen dann. Das ist bei Zeichenabstand ueblich und gewollt.</summary>
        Public Property LetterSpacingPercent As Single
            Get
                Return _letterSpacingPercent
            End Get
            Set(value As Single)
                SetField(_letterSpacingPercent, Math.Max(-20.0F, Math.Min(200.0F, value)))
            End Set
        End Property

        ''' <summary>Fetter Schriftschnitt. Wirkt nur, wenn die Familie einen hat - Skia stellt
        ''' keinen synthetischen her.</summary>
        Public Property Bold As Boolean
            Get
                Return _bold
            End Get
            Set(value As Boolean)
                SetField(_bold, value)
            End Set
        End Property

        ''' <summary>Kursiver Schriftschnitt. Gilt dieselbe Einschraenkung wie bei Bold.</summary>
        Public Property Italic As Boolean
            Get
                Return _italic
            End Get
            Set(value As Boolean)
                SetField(_italic, value)
            End Set
        End Property

        Public Property FillColor2 As String
            Get
                Return _fillColor2
            End Get
            Set(value As String)
                SetField(_fillColor2, If(value, "#FFFFFFFF"))
            End Set
        End Property

        Public Property GradientAngleDegrees As Single
            Get
                Return _gradientAngleDegrees
            End Get
            Set(value As Single)
                SetField(_gradientAngleDegrees, value)
            End Set
        End Property

        Public Property GradientInverted As Boolean
            Get
                Return _gradientInverted
            End Get
            Set(value As Boolean)
                SetField(_gradientInverted, value)
            End Set
        End Property

        Public Property ShadowEnabled As Boolean
            Get
                Return _shadowEnabled
            End Get
            Set(value As Boolean)
                SetField(_shadowEnabled, value)
            End Set
        End Property

        Public Property ShadowOffsetXPercent As Single
            Get
                Return _shadowOffsetXPercent
            End Get
            Set(value As Single)
                SetField(_shadowOffsetXPercent, value)
            End Set
        End Property

        Public Property ShadowOffsetYPercent As Single
            Get
                Return _shadowOffsetYPercent
            End Get
            Set(value As Single)
                SetField(_shadowOffsetYPercent, value)
            End Set
        End Property

        Public Property ShadowBlur As Single
            Get
                Return _shadowBlur
            End Get
            Set(value As Single)
                SetField(_shadowBlur, value)
            End Set
        End Property

        Public Property ShadowStrength As Single
            Get
                Return _shadowStrength
            End Get
            Set(value As Single)
                SetField(_shadowStrength, value)
            End Set
        End Property

        Public Property ShadowColor As String
            Get
                Return _shadowColor
            End Get
            Set(value As String)
                SetField(_shadowColor, If(value, "#80000000"))
            End Set
        End Property

        Public Property ShadowRounded As Boolean
            Get
                Return _shadowRounded
            End Get
            Set(value As Boolean)
                SetField(_shadowRounded, value)
            End Set
        End Property

        Public Property ShadowCornerRadiusPercent As Single
            Get
                Return _shadowCornerRadiusPercent
            End Get
            Set(value As Single)
                SetField(_shadowCornerRadiusPercent, value)
            End Set
        End Property

        ''' Größe des Schattens in Prozent der Objektgröße. 100 = genau Objektgröße, >100 lässt den
        ''' Schatten (um seine Mitte skaliert) über das Objekt hinauswachsen, <100 verkleinert ihn.
        Public Property ShadowSizePercent As Single
            Get
                Return _shadowSizePercent
            End Get
            Set(value As Single)
                SetField(_shadowSizePercent, value)
            End Set
        End Property

        Public Property GlowEnabled As Boolean
            Get
                Return _glowEnabled
            End Get
            Set(value As Boolean)
                SetField(_glowEnabled, value)
            End Set
        End Property

        Public Property GlowBlur As Single
            Get
                Return _glowBlur
            End Get
            Set(value As Single)
                SetField(_glowBlur, value)
            End Set
        End Property

        Public Property GlowStrength As Single
            Get
                Return _glowStrength
            End Get
            Set(value As Single)
                SetField(_glowStrength, value)
            End Set
        End Property

        Public Property GlowColor As String
            Get
                Return _glowColor
            End Get
            Set(value As String)
                SetField(_glowColor, If(value, "#FFFFFF00"))
            End Set
        End Property

        ''' Strokes wird flach kopiert: die Liste ist neu, die Striche darin werden geteilt. Das ist
        ''' zulässig, weil BrushStroke unveränderlich ist, und hält Undo-Schnappschüsse klein.
        Public Function Clone() As ImageAnnotation
            Return New ImageAnnotation With {
                .Kind = Kind,
                .Text = Text,
                .CustomName = CustomName,
                .Id = Id,
                .GroupId = GroupId,
                .MaskId = MaskId,
                .ClipToLayerBelow = ClipToLayerBelow,
                .PathPoints = PathPoints,
                .PathClosed = PathClosed,
                .WatermarkPresetName = WatermarkPresetName,
                .FrameSizePercent = FrameSizePercent,
                .FrameCornerRadiusPercent = FrameCornerRadiusPercent,
                .FrameEffect = FrameEffect,
                .FrameSymbol = FrameSymbol,
                .FrameSymbolSpacingPercent = FrameSymbolSpacingPercent,
                .FrameSymbolRotate = FrameSymbolRotate,
                .Warp = Warp?.Clone(),
                .OwnWarp = OwnWarp?.Clone(),
                .ImagePath = ImagePath,
                .SourceFileName = SourceFileName,
                .XPixels = XPixels,
                .YPixels = YPixels,
                .WidthPixels = WidthPixels,
                .HeightPixels = HeightPixels,
                .FillColor = FillColor,
                .StrokeColor = StrokeColor,
                .EraserFillColor = EraserFillColor,
                .StrokeWidth = StrokeWidth,
                .FontSizePixels = FontSizePixels,
                .FontFamily = FontFamily,
                .Opacity = Opacity,
                .BlendMode = BlendMode,
                .BlendIncludesStroke = BlendIncludesStroke,
                .FlowPercent = FlowPercent,
                .RotationDegrees = RotationDegrees,
                .FlipHorizontal = FlipHorizontal,
                .LockAspect = LockAspect,
                .FlipVertical = FlipVertical,
                .Adjustments = If(Adjustments Is Nothing, Nothing, Adjustments.Clone()),
                .Anchor = Anchor,
                .ScaleWithImage = ScaleWithImage,
                .IsVisible = IsVisible, .IsLocked = IsLocked,
                .HardnessPercent = HardnessPercent,
                .BrushPreset = BrushPreset,
                .FillKind = FillKind,
                .TextPathKind = TextPathKind,
                .TextPathBend = TextPathBend,
                .TextPathStartOffset = TextPathStartOffset,
                .LetterSpacingPercent = LetterSpacingPercent,
                .Bold = Bold,
                .Italic = Italic,
                .FillColor2 = FillColor2,
                .GradientAngleDegrees = GradientAngleDegrees,
                .GradientInverted = GradientInverted,
                .ShadowEnabled = ShadowEnabled,
                .ShadowOffsetXPercent = ShadowOffsetXPercent,
                .ShadowOffsetYPercent = ShadowOffsetYPercent,
                .ShadowBlur = ShadowBlur,
                .ShadowStrength = ShadowStrength,
                .ShadowColor = ShadowColor,
                .ShadowRounded = ShadowRounded,
                .ShadowCornerRadiusPercent = ShadowCornerRadiusPercent,
                .ShadowSizePercent = ShadowSizePercent,
                .GlowEnabled = GlowEnabled,
                .GlowBlur = GlowBlur,
                .GlowStrength = GlowStrength,
                .GlowColor = GlowColor,
                .Strokes = New List(Of BrushStroke)(Strokes)
            }
        End Function

        Private Sub SetField(Of T)(ByRef field As T, value As T, <CallerMemberName> Optional propertyName As String = Nothing)
            If EqualityComparer(Of T).Default.Equals(field, value) Then Return
            field = value
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
        End Sub
    End Class


    ''' <summary>Eine Gruppe von Objekt-Ebenen: gemeinsamer Name, gemeinsame Sichtbarkeit, gemeinsame
    ''' Auswahl. Die Mitglieder verweisen über <see cref="ImageAnnotation.GroupId"/> hierher und liegen
    ''' ZUSAMMENHÄNGEND in der Objektliste.
    '''
    ''' Die Gruppe ist BEWUSST kein eigener Renderschritt: Deckkraft und Mischmodus auf der Gruppenzeile
    ''' schreiben ihren Wert in jedes Mitglied, statt die Gruppe als Ganzes zu komponieren. Damit bleibt
    ''' der Renderer unberührt (keine zusätzliche bildgroße Ebene je Gruppe). Preis: bei überlappenden
    ''' Mitgliedern stapelt sich Deckkraft an den Überlappungen, anders als bei einer echten
    ''' Photoshop-Gruppe (Entscheidung).</summary>
    Public Class AnnotationGroup
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        ''' <summary>Sichtbarkeit der GRUPPE. Ein Mitglied wird gezeichnet, wenn sein eigenes IsVisible
        ''' UND die Gruppe sichtbar sind (siehe ImageAdjustments.IsAnnotationRenderVisible).</summary>
        Public Property IsVisible As Boolean = True
        ''' <summary>GESPERRT: sperrt die ganze Gruppe. Ein Mitglied ist geometrisch unantastbar, wenn
        ''' sein eigenes IsLocked ODER das der Gruppe gesetzt ist (siehe
        ''' ImageAdjustments.IsAnnotationGeometryLocked).</summary>
        Public Property IsLocked As Boolean = False
        ''' <summary>Reiner Panel-Zustand: Mitglieder eingeklappt. Persistiert, weil er sonst bei jedem
        ''' Öffnen verloren ginge; ohne Wirkung auf das Bild.</summary>
        Public Property IsCollapsed As Boolean = False

        Public Function Clone() As AnnotationGroup
            Return New AnnotationGroup With {
                .Id = Id, .Name = Name, .IsVisible = IsVisible, .IsLocked = IsLocked, .IsCollapsed = IsCollapsed
            }
        End Function
    End Class


    Friend Module AnnotationLayoutHelpers
        Friend Function NormalizeAnnotationAnchor(value As String) As String
            Select Case If(value, "").Trim()
                Case "TopLeft", "Top", "TopRight", "Left", "Center", "Right", "BottomLeft", "Bottom", "BottomRight"
                    Return value.Trim()
                Case Else
                    Return "BottomRight"
            End Select
        End Function

        Friend Function ComputeAnnotationRect(sourceWidth As Integer, sourceHeight As Integer, kind As String, annotation As ImageAnnotation) As SKRect
            Dim width = Math.Max(1.0F, annotation.WidthPixels)
            Dim height = Math.Max(1.0F, annotation.HeightPixels)
            Dim normalizedKind = If(kind, "").Trim().ToLowerInvariant()
            Dim x As Single
            Dim y As Single

            ' Der Rahmen umfasst IMMER das ganze Bild. Er traegt zwar Position und Groesse wie jedes
            ' Objekt, benutzt sie aber nicht: ein Rahmen, den man verschieben kann, ist keiner mehr,
            ' und nach einem Zuschnitt oder einer neuen Leinwandgroesse soll er ohne Zutun wieder an
            ' der Kante sitzen.
            If normalizedKind = "frame" Then
                Return New SKRect(0, 0, Math.Max(1, sourceWidth), Math.Max(1, sourceHeight))
            End If

            If normalizedKind = "watermark" Then
                Dim offsetX = annotation.XPixels
                Dim offsetY = annotation.YPixels
                Select Case NormalizeAnnotationAnchor(annotation.Anchor)
                    Case "TopLeft"
                        x = offsetX : y = offsetY
                    Case "Top"
                        x = (sourceWidth - width) / 2.0F + offsetX : y = offsetY
                    Case "TopRight"
                        x = sourceWidth - width - offsetX : y = offsetY
                    Case "Left"
                        x = offsetX : y = (sourceHeight - height) / 2.0F + offsetY
                    Case "Center"
                        x = (sourceWidth - width) / 2.0F + offsetX : y = (sourceHeight - height) / 2.0F + offsetY
                    Case "Right"
                        x = sourceWidth - width - offsetX : y = (sourceHeight - height) / 2.0F + offsetY
                    Case "BottomLeft"
                        x = offsetX : y = sourceHeight - height - offsetY
                    Case "Bottom"
                        x = (sourceWidth - width) / 2.0F + offsetX : y = sourceHeight - height - offsetY
                    Case Else
                        x = sourceWidth - width - offsetX : y = sourceHeight - height - offsetY
                End Select
            Else
                x = annotation.XPixels
                y = annotation.YPixels
            End If

            Return New SKRect(x, y, x + width, y + height)
        End Function

        ''' <summary>Gegenstück zu <see cref="ComputeAnnotationRect"/>: rechnet eine ABSOLUTE Lage
        ''' zurück in die Anker-Abstände, mit denen ein Wasserzeichen gespeichert wird. Beide
        ''' Richtungen gehören zusammen an EINE Stelle - eine gespiegelte Formel in der Oberfläche
        ''' liefe unweigerlich auseinander, und genau das ist bereits passiert: der Klickbereich lag
        ''' an den Abständen (also oben links), das Wasserzeichen selbst an seinem Anker.</summary>
        Friend Function ComputeAnnotationOffsets(sourceWidth As Double, sourceHeight As Double, anchor As String,
                                                 x As Double, y As Double, width As Double, height As Double) As (X As Double, Y As Double)
            Dim w = Math.Max(1.0, width)
            Dim h = Math.Max(1.0, height)
            Dim offsetX As Double
            Dim offsetY As Double

            Select Case NormalizeAnnotationAnchor(anchor)
                Case "TopLeft"
                    offsetX = x : offsetY = y
                Case "Top"
                    offsetX = x - (sourceWidth - w) / 2.0 : offsetY = y
                Case "TopRight"
                    offsetX = sourceWidth - w - x : offsetY = y
                Case "Left"
                    offsetX = x : offsetY = y - (sourceHeight - h) / 2.0
                Case "Center"
                    offsetX = x - (sourceWidth - w) / 2.0 : offsetY = y - (sourceHeight - h) / 2.0
                Case "Right"
                    offsetX = sourceWidth - w - x : offsetY = y - (sourceHeight - h) / 2.0
                Case "BottomLeft"
                    offsetX = x : offsetY = sourceHeight - h - y
                Case "Bottom"
                    offsetX = x - (sourceWidth - w) / 2.0 : offsetY = sourceHeight - h - y
                Case Else
                    offsetX = sourceWidth - w - x : offsetY = sourceHeight - h - y
            End Select

            Return (offsetX, offsetY)
        End Function
    End Module

End Namespace
