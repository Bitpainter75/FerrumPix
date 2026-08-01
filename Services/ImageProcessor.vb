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

Namespace Services

    Public Enum ResizeInterpolationMode
        Nearest
        Bilinear
        Bicubic
    End Enum

    Public Enum NoiseReductionMethod
        Gaussian
        Median
    End Enum

    ''' <summary>Wie eine dunkelnde Vignette auf die Pixel wirkt (angelehnt an Adobes
    ''' PostCropVignetteStyle). ColorPriority = 0 ist bewusst der erste Wert: es ist das bisherige
    ''' Verhalten (multiplikatives Abdunkeln, Farbton bleibt), damit ein fehlendes Feld in alten
    ''' .fpx-Projekten und der Enum-Standard exakt das ergeben, was vorher gerechnet wurde.</summary>
    Public Enum VignetteStyle
        ColorPriority
        HighlightPriority
        PaintOverlay
    End Enum

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

    ''' <summary>Persistente, wiederverwendbare Alpha-Maske im ungedrehten Quellbildraum.
    ''' Eine aktive Auswahl ist nur UI-Zustand; lokale Korrekturen verweisen über MaskId auf diese Daten.</summary>
    Public Class ImageMask
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = LocalizationService.T("Auswahlmaske")
        Public Property SourceWidthPixels As Integer
        Public Property SourceHeightPixels As Integer
        Public Property Left As Integer
        Public Property Top As Integer
        Public Property Right As Integer
        Public Property Bottom As Integer
        Public Property PngBase64 As String = ""
        Public Property FeatherPixels As Single
        Public Property Inverted As Boolean

        ''' <summary>Art der Maske. Leer = GEMALTE Maske, deren Alphawerte in PngBase64 liegen
        ''' (Rechteck, Ellipse, Lasso, Zauberstab, Masken-Pinsel). "Linear" und "Radial" =
        ''' VERLAUF, der NICHT gebacken wird, sondern bei jedem Render aus seiner Geometrie
        ''' entsteht. Das ist der ganze Unterschied und der Grund fuer den eigenen Typ: ein
        ''' gebackener Verlauf liesse sich hinterher weder drehen noch in der Weichheit aendern,
        ''' ohne die Maske neu zu malen. UMGEKEHRT (aussen statt innen, unten statt oben) ist
        ''' kein eigener Typ, sondern das vorhandene <see cref="Inverted"/>.</summary>
        Public Property Kind As String = ""

        ''' <summary>Geometrie des Verlaufs, in Prozent der QUELLBILD-Maße (wie alle
        ''' Geometrie-Angaben im Rezept).
        ''' LINEAR: Start = volle Deckung, Ende = keine. Der ABSTAND der beiden ist die Weichheit,
        ''' ihre Richtung der Winkel; ausserhalb gilt der jeweilige Endwert, der Verlauf reicht
        ''' also immer ueber das ganze Bild.
        ''' RADIAL: Start = MITTELPUNKT, Ende = ein Punkt auf dem Rand. Der Abstand ist damit der
        ''' Radius und die Richtung die Drehung der Ellipse.</summary>
        Public Property GradientStartXPercent As Double
        Public Property GradientStartYPercent As Double
        Public Property GradientEndXPercent As Double
        Public Property GradientEndYPercent As Double

        ''' <summary>NUR RADIAL: Verhaeltnis der zweiten zur ersten Halbachse. 1 = Kreis, kleiner =
        ''' quer gestaucht, groesser = laengs gestreckt. Damit laesst sich die Flaeche an ein
        ''' Gesicht oder einen Himmelsausschnitt anpassen, ohne einen zweiten Griff zu brauchen.</summary>
        Public Property GradientRadiusRatio As Double = 1.0

        ''' <summary>NUR RADIAL: wie viel des Radius der weiche Uebergang einnimmt, in Prozent.
        ''' 0 = harte Kante, 100 = vom Mittelpunkt an abfallend. Beim linearen Verlauf braucht es
        ''' das nicht - dort IST der Abstand der beiden Punkte die Weichheit.</summary>
        Public Property GradientFeatherPercent As Double = 50.0

        ''' <summary>PINSEL-KORREKTUR eines Verlaufs: zwei Alpha8-Raster im QUELLRAUM, die nach dem
        ''' gerechneten Verlauf verrechnet werden (<c>Deckung = Verlauf + Hinzu - Weg</c>, geklemmt).
        ''' Damit laesst sich ein Verlauf mit dem Maskenpinsel nachbessern - Dach aus dem
        ''' Himmelsverlauf herausnehmen, eine Ecke dazunehmen -, OHNE dass der Verlauf seine
        ''' Aenderbarkeit verliert: seine Geometrie bleibt Geometrie, die Korrektur liegt daneben.
        ''' Deshalb zwei getrennte Raster statt eines vorzeichenbehafteten: beide Richtungen bleiben
        ''' unabhaengig voneinander uebermalbar, und ein leeres Raster kostet nichts.
        '''
        ''' Beide teilen sich EIN Rechteck (die Vereinigung des Bemalten) - getrennte Rechtecke
        ''' waeren zwei weitere Feldergruppen fuer einen Fall, den es praktisch nicht gibt.
        ''' Bei einer GEMALTEN Maske bleiben diese Felder leer; dort verrechnet der Pinsel direkt
        ''' die Maske selbst (siehe WriteSelectionMaskBackToLayer im EditorViewModel).</summary>
        Public Property BrushAddPngBase64 As String = ""
        Public Property BrushSubtractPngBase64 As String = ""
        Public Property BrushLeft As Integer
        Public Property BrushTop As Integer
        Public Property BrushRight As Integer
        Public Property BrushBottom As Integer

        ''' <summary>True, wenn ein Verlauf eine Pinselkorrektur traegt.</summary>
        Public ReadOnly Property HasBrushCorrection As Boolean
            Get
                Return BrushRight > BrushLeft AndAlso BrushBottom > BrushTop AndAlso
                       (Not String.IsNullOrWhiteSpace(BrushAddPngBase64) OrElse
                        Not String.IsNullOrWhiteSpace(BrushSubtractPngBase64))
            End Get
        End Property

        ''' <summary>True fuer beide Verlaufsarten - sie teilen sich Speicherung, Renderweg und
        ''' Cache-Schluessel.</summary>
        Public ReadOnly Property IsGradient As Boolean
            Get
                Return IsLinearGradient OrElse IsRadialGradient
            End Get
        End Property

        Public ReadOnly Property IsLinearGradient As Boolean
            Get
                Return String.Equals(Kind, "Linear", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsRadialGradient As Boolean
            Get
                Return String.Equals(Kind, "Radial", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public Function Clone() As ImageMask
            Return New ImageMask With {
                .Id = Id, .Name = Name,
                .SourceWidthPixels = SourceWidthPixels, .SourceHeightPixels = SourceHeightPixels,
                .Left = Left, .Top = Top, .Right = Right, .Bottom = Bottom,
                .PngBase64 = PngBase64, .FeatherPixels = FeatherPixels, .Inverted = Inverted,
                .Kind = Kind,
                .GradientStartXPercent = GradientStartXPercent, .GradientStartYPercent = GradientStartYPercent,
                .GradientEndXPercent = GradientEndXPercent, .GradientEndYPercent = GradientEndYPercent,
                .GradientRadiusRatio = GradientRadiusRatio, .GradientFeatherPercent = GradientFeatherPercent,
                .BrushAddPngBase64 = BrushAddPngBase64, .BrushSubtractPngBase64 = BrushSubtractPngBase64,
                .BrushLeft = BrushLeft, .BrushTop = BrushTop, .BrushRight = BrushRight, .BrushBottom = BrushBottom
            }
        End Function
    End Class

    ''' <summary>Nicht-destruktive Pixelkorrektur, die über MaskId auf eine ImageMask begrenzt wird.</summary>
    Public Class MaskedAdjustmentLayer
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = LocalizationService.T("Lokale Korrektur")
        Public Property MaskId As String = ""
        Public Property IsVisible As Boolean = True
        ''' <summary>GESPERRT - siehe ImageAnnotation.IsLocked. Bei einer Korrekturebene heißt das:
        ''' ihre Maske wandert bei Gruppen-Transformationen nicht mit.</summary>
        Public Property IsLocked As Boolean = False
        Public Property Opacity As Single = 1.0F
        Public Property Adjustments As ImageAdjustments = New ImageAdjustments()
        ''' <summary>True = diese Ebene wurde aus einem XMP-Preset importiert (lokale Korrektur).
        ''' Beim Anwenden eines ANDEREN Presets werden diese entfernt, damit sich Presets nicht
        ''' aufeinander stapeln - manuell erstellte Ebenen (False) bleiben davon unberührt.</summary>
        Public Property FromPreset As Boolean = False
        ''' <summary>Thematische Art der Ebene: False = AUSWAHL-Ebene (aus Rechteck/Ellipse/Lasso/
        ''' Zauberstab, im Editor als Laufameisen), True = MASKEN-Ebene (aus Masken-Pinsel/Verlauf, im
        ''' Editor als rotes Overlay). Steuert Symbol/Name im Ebenen-Panel und die Overlay-Darstellung.</summary>
        Public Property IsMaskLayer As Boolean = False

        ''' <summary>Zugehörigkeit zu einer Gruppe (leer = keine). Dieselben Gruppen wie bei den
        ''' Objekt-Ebenen (<see cref="AnnotationGroup"/>), aber eine Gruppe fasst entweder Objekte ODER
        ''' Korrekturebenen zusammen: beide sind getrennte Rendergruppen (lokale Korrekturen laufen VOR
        ''' den Objekt-Overlays), eine gemischte Gruppe würde eine Reihenfolge vortäuschen, die es im
        ''' Renderer nicht gibt.</summary>
        Public Property GroupId As String = ""

        ''' <summary>Leer = die Korrektur liegt im BASISBILD (schnell, im Basis-Cache) und wirkt damit
        ''' unter allen Objekten - das ist der Normalfall. Steht hier die Id eines Objekts, ist die
        ''' Korrektur ÜBER dieses Objekt einsortiert und wirkt auf alles, was darunter liegt: Basis
        ''' UND die bis dahin gezeichneten Objekte. Solche Ebenen fallen aus dem Basis-Cache heraus,
        ''' sie müssen bei jedem Render auf das Komposit angewendet werden.</summary>
        Public Property StackAboveAnnotationId As String = ""

        ''' <summary>DEKLARATIVE Füllung (kein PNG/Objekt): leer = keine Füllung, sonst "Solid"/
        ''' "LinearGradient"/"RadialGradient". Bei einer AUSWAHL-Ebene wird die Füllung SICHTBAR in die
        ''' Auswahl komponiert (Farbe/Verlauf); bei einer MASKEN-Ebene stuft die LUMINANZ der Füllung die
        ''' Maske ab und bestimmt so, wie stark die Anpassung der Ebene je Bereich wirkt. Der Render zeichnet
        ''' beides selbst - so bleibt die Füllung nachträglich änderbar (Winkel/Farben) statt eingebrannt.</summary>
        Public Property FillKind As String = ""
        Public Property FillColor As String = "#FFFFFFFF"
        Public Property FillColor2 As String = "#FF000000"
        Public Property FillAngle As Double = 0
        Public Property FillInverted As Boolean = False

        Public Function Clone() As MaskedAdjustmentLayer
            Return New MaskedAdjustmentLayer With {
                .Id = Id, .Name = Name, .MaskId = MaskId,
                .IsVisible = IsVisible, .IsLocked = IsLocked, .Opacity = Opacity, .FromPreset = FromPreset, .IsMaskLayer = IsMaskLayer,
                .GroupId = GroupId,
                .StackAboveAnnotationId = StackAboveAnnotationId,
                .FillKind = FillKind, .FillColor = FillColor, .FillColor2 = FillColor2,
                .FillAngle = FillAngle, .FillInverted = FillInverted,
                .Adjustments = If(Adjustments Is Nothing, New ImageAdjustments(), Adjustments.Clone())
            }
        End Function

        ''' <summary>True, wenn diese Ebene eine deklarative Füllung trägt.</summary>
        Public Function HasFill() As Boolean
            Return Not String.IsNullOrWhiteSpace(FillKind)
        End Function
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

    Public Class ImageAdjustments

        ''' <summary>Die eingebauten Filter, in der Reihenfolge, in der sie im Editor stehen. "Keine" ist
        ''' der neutrale Eintrag. Einzige Quelle der Namen: sie sind gleichzeitig die Schlüssel, auf die
        ''' ImageProcessor.ApplyFilterPreset schaltet, und werden im Editor UND in der Stapelverarbeitung
        ''' der Galerie angeboten.</summary>
        Public Shared ReadOnly FilterPresetNames As String() = {
            "Keine", "S/W", "Warm", "Kühl", "Fade", "Kontrast", "Sepia", "Matt", "Cross", "Dramatisch",
            "Weich", "Noir", "Duoton", "Polaroid", "VHS", "Alt"
        }

        ''' <summary>Die Stärke, mit der ein frisch gewählter Filter startet. S/W und Sepia sind Umwandlungen -
        ''' halb angewendet ergeben sie nur ein blasses Bild, sie starten deshalb voll. Alle übrigen sind
        ''' Looks, die bei voller Stärke überzeichnen, und starten auf der Hälfte.</summary>
        Public Shared Function DefaultFilterStrength(preset As String) As Single
            Dim isConversion = String.Equals(preset, "S/W", StringComparison.OrdinalIgnoreCase) OrElse
                               String.Equals(preset, "Sepia", StringComparison.OrdinalIgnoreCase)
            Return If(isConversion, 100.0F, 50.0F)
        End Function

        Public Property SourceWidthPixels As Integer = 0
        Public Property SourceHeightPixels As Integer = 0
        Public Property RecipeCoordinateVersion As Integer = 2
        Public Property Exposure As Single = 0
        Public Property Brightness As Single = 0
        Public Property Contrast As Single = 0
        Public Property Saturation As Single = 0
        Public Property Highlights As Single = 0
        Public Property ShadowsLevel As Single = 0
        Public Property Whites As Single = 0
        Public Property Blacks As Single = 0
        Public Property Temperature As Single = 0
        Public Property Tint As Single = 0
        Public Property Sharpness As Single = 0
        ''' <summary>Radius der Unschärfemaske, 0-100. 0 = die bisherige feste 3×3-Maske (Radius ~1);
        ''' höher = größerer Wirkradius, schärft gröbere Strukturen. Bei 0 UND SharpenDetail 0 rechnet
        ''' ApplySharpness bitgenau wie zuvor.</summary>
        Public Property SharpenRadius As Single = 0
        ''' <summary>Detailanhebung der Unschärfemaske, 0-100. 0 = neutral; höher = die feinen
        ''' Hochfrequenzanteile werden stärker herausgearbeitet.</summary>
        Public Property SharpenDetail As Single = 0
        ''' <summary>Kantenmaskierung der Schärfung, 0-100 (Adobes „Maskieren"). 0 = die ganze Fläche wird
        ''' geschärft (bisheriges Verhalten); höher = die Schärfung zieht sich auf die Kanten zurück, glatte
        ''' Flächen wie Himmel und Haut bleiben ruhig. Bei 0 rechnet ApplySharpness bitgenau wie zuvor.</summary>
        Public Property SharpenMasking As Single = 0
        Public Property NoiseReduction As Single = 0
        ''' <summary>Kantenerhalt der (gaußschen) Rauschreduzierung, 0-100. 0 = reines Weichzeichnen wie
        ''' bisher; höher = an kontrastreichen Kanten wird das Original zurückgemischt, Details bleiben
        ''' stehen. Wirkt nur bei aktiver NoiseReduction.</summary>
        Public Property NoiseReductionDetail As Single = 0
        Public Property NoiseReductionMethod As NoiseReductionMethod = NoiseReductionMethod.Gaussian
        ''' Farb-Rauschreduzierung 0-100: glaettet NUR die Farbanteile (Chroma), die Helligkeit
        ''' bleibt unangetastet - Details bleiben stehen, Farbflecken verschwinden. Gerade bei der
        ''' echten RAW-Entwicklung sichtbar, wo die Kamera-Vorschau schon entrauscht war.
        Public Property ColorNoiseReduction As Single = 0

        ''' <summary>Mehrskalige Farbrauschminderung: 0 bis 100. Die dritte Stufe neben Rauschen und
        ''' Farbrauschen - sie holt das GROBFLECKIGE Farbrauschen, an das ein einstufiger Filter nur
        ''' mit einem Radius herankaeme, der alles andere gleich mitfrisst.</summary>
        Public Property FarbrauschGrob As Single = 0

        ''' <summary>Wie grosse Flecken noch erfasst werden: 0 bis 100.</summary>
        Public Property ColorNoiseCoarseScale As Single = 50
        ''' <summary>Farbrauschen HINZUFUEGEN 0-100 - die Plus-Seite desselben Reglers im Panel
        ''' („Farbrauschen" ist bipolar wie „Rauschen": minus entfernt, plus faerbt ein). Getrennt
        ''' gespeichert, damit <see cref="ColorNoiseReduction"/> weiter genau das bleibt, was
        ''' unter crs:ColorNoiseReduction abgelegt wird (0-100 Entfernung) - eine gemeinsame
        ''' Skala mit Vorzeichen haette den Preset-Import zweideutig gemacht.</summary>
        Public Property ColorNoiseAdd As Single = 0
        Public Property DustScratches As Single = 0
        Public Property Haze As Single = 0
        Public Property AddNoise As Single = 0
        Public Property [Structure] As Single = 0
        Public Property Glow As Single = 0
        Public Property Vibrance As Single = 0

        ''' KAMERAKALIBRIERUNG (Panel "Kalibrierung", crs:RedHue/RedSaturation/...).
        ''' Dreht und saettigt die drei PRIMAERFARBEN und ist damit das, was vielen Presets ihren
        ''' charakteristischen Farbstich gibt - ohne sie kam ein Preset strukturell unvollstaendig an.
        ''' Alle Werte -100..100. Naeherung: Farbton = Drehung der Primaerfarbe um die Grauachse
        ''' (bis +/-30 Grad), Saettigung = Abstand von der Grauachse. Adobes exakte Rechnung sitzt
        ''' im Kameraprofil und ist nicht oeffentlich; diese Form ist verbreitet und reproduzierbar.
        Public Property CalibrationRedHue As Single = 0
        Public Property CalibrationRedSaturation As Single = 0
        Public Property CalibrationGreenHue As Single = 0
        Public Property CalibrationGreenSaturation As Single = 0
        Public Property CalibrationBlueHue As Single = 0
        Public Property CalibrationBlueSaturation As Single = 0

        ''' Gruen-/Magenta-Verschiebung, die nur die TIEFEN faerbt (crs:ShadowTint).
        Public Property CalibrationShadowTint As Single = 0
        ' ── Objektivkorrektur, uebersteuert je Bild ─────────────────────────
        '
        ' Nothing heisst "wie in den Einstellungen vorgegeben". Bewusst dreiwertig und nicht einfach
        ' Boolean: waere die Vorgabe hier hineinkopiert, wuerde ein spaeter umgestellter Schalter
        ' alle bereits gespeicherten Rezepte nicht mehr erreichen - und man saehe der Datei nicht an,
        ' ob AUS eine Entscheidung war oder nur der damalige Standard.
        Public Property LensDistortion As Boolean? = Nothing
        Public Property LensTca As Boolean? = Nothing
        Public Property LensVignetting As Boolean? = Nothing

        ' Staerke je Korrektur in Prozent, 100 = wie kalibriert. Sie ist noetig, weil die Messwerte
        ' fuer ein OBJEKTIVMODELL gelten, nicht fuer das einzelne Exemplar - und weil sie an einer
        ' anderen Kamera aufgenommen wurden. Am eigenen Referenzfoto lag der Rotkanal bei doppelter
        ' Staerke naeher am Ziel als bei einfacher, der Blaukanal dagegen schlechter; ein fester
        ' Faktor kann das nicht abbilden.
        ''' <summary>Von Hand gewaehltes Objektiv, wenn die Aufnahmedaten KEINES nennen. Dann gibt
        ''' es keinen Objektivnamen, an dem sich eine dauerhafte Zuordnung festmachen liesse - also
        ''' gehoert die Wahl ins Rezept dieses einen Bildes. Nennt das EXIF ein Objektiv, bleibt es
        ''' bei der Zuordnung ueber den Namen, die dann fuer den ganzen Bestand gilt.</summary>
        Public Property LensModel As String = ""

        Public Property LensDistortionAmount As Single = 100
        Public Property LensTcaAmount As Single = 100
        Public Property LensVignettingAmount As Single = 100

        Public Property Vignette As Single = 0
        Public Property VignetteTransition As Single = 55
        Public Property VignetteRoundness As Single = 0
        Public Property VignetteFeather As Single = 70
        Public Property VignetteCenterX As Single = 50
        Public Property VignetteCenterY As Single = 50
        ''' <summary>Stil, mit dem eine dunkelnde Vignette wirkt. Standard = ColorPriority = bisheriges
        ''' Verhalten (siehe <see cref="Services.VignetteStyle"/>).</summary>
        Public Property VignetteStyle As VignetteStyle = VignetteStyle.ColorPriority
        Public Property Grain As Single = 0
        ''' <summary>Körnungsgröße, 0-100. 0 = feinstes Korn (1 px, bisheriges Verhalten); höher =
        ''' gröberes Korn (das Rauschen wird zellenweise über größere Blöcke konstant gehalten).
        ''' Bei 0 UND GrainFrequency 0 rechnet ApplyGrain bitgenau wie zuvor.</summary>
        Public Property GrainSize As Single = 0
        ''' <summary>Körnungsfrequenz/Unregelmäßigkeit, 0-100. 0 = gleichmäßiges Korn; höher = eine
        ''' feine zweite Lage wird eingemischt, das Korn wirkt unruhiger.</summary>
        Public Property GrainFrequency As Single = 0
        Public Property Clarity As Single = 0

        ''' <summary>Gescanntes Filmnegativ in ein Positiv umkehren.</summary>
        Public Property NegativeEnabled As Boolean = False
        ''' <summary>Schwarzweiß-Negativ: ein gemeinsamer Basiswert für alle drei Kanäle. Die kanalweise
        ''' Normalisierung würde bei einem Graustufenscan sonst das Kanalrauschen zu einem Farbstich
        ''' aufziehen - es gibt hier keine Maske, die sie herausrechnen müsste.</summary>
        Public Property NegativeMonochrome As Boolean = False
        ''' <summary>Farbe des unbelichteten Filmträgers ("orange Maske") = die hellste Stelle des Scans.
        ''' Leer: wird beim Verarbeiten aus dem Bild geschätzt (siehe AnalyzeFilmNegative).</summary>
        Public Property NegativeBaseColor As String = ""
        ''' <summary>Dichteste (= dunkelste) Stelle des Negativs, entspricht dem hellsten Motivdetail.
        ''' Leer: wird geschätzt.</summary>
        Public Property NegativeDensityColor As String = ""
        ''' <summary>Gradation der Dichtekurve, -100..100 (0 = neutral), wirkt als Gamma 2^(v/100).</summary>
        Public Property NegativeGamma As Single = 0

        Public Property CurveRgbPoints As String = "0,0;255,255"
        Public Property CurveRedPoints As String = "0,0;255,255"
        Public Property CurveGreenPoints As String = "0,0;255,255"
        Public Property CurveBluePoints As String = "0,0;255,255"
        Public Property CurveLuminancePoints As String = "0,0;255,255"
        Public Property RedHue As Single = 0
        Public Property RedSaturation As Single = 0
        Public Property RedLuminance As Single = 0
        Public Property OrangeHue As Single = 0
        Public Property OrangeSaturation As Single = 0
        Public Property OrangeLuminance As Single = 0
        Public Property YellowHue As Single = 0
        Public Property YellowSaturation As Single = 0
        Public Property YellowLuminance As Single = 0
        Public Property GreenHue As Single = 0
        Public Property GreenSaturation As Single = 0
        Public Property GreenLuminance As Single = 0
        Public Property AquaHue As Single = 0
        Public Property AquaSaturation As Single = 0
        Public Property AquaLuminance As Single = 0
        Public Property BlueHue As Single = 0
        Public Property BlueSaturation As Single = 0
        Public Property BlueLuminance As Single = 0
        Public Property PurpleHue As Single = 0
        Public Property PurpleSaturation As Single = 0
        Public Property PurpleLuminance As Single = 0
        Public Property MagentaHue As Single = 0
        Public Property MagentaSaturation As Single = 0
        Public Property MagentaLuminance As Single = 0
        ''' <summary>Farbgradierung: vier Zonen (Schatten/Mitten/Lichter/Global) mit je Farbton (0-360),
        ''' Sättigung (0-100) und Luminanz (±100). Die Schatten- und Lichter-Felder hießen bis
        ''' SplitToning*: Split-Toning ist die Zweizonen-Variante desselben Werkzeugs, und Adobe hat es
        ''' ab den neueren Prozessversionen genauso in die Farbgradierung überführt (crs:SplitToning* wird beim Import
        ''' weiterhin gelesen, siehe XmpPresetService).</summary>
        Public Property ColorGradeShadowHue As Single = 0
        Public Property ColorGradeShadowSaturation As Single = 0
        Public Property ColorGradeShadowLuminance As Single = 0
        Public Property ColorGradeMidtoneHue As Single = 0
        Public Property ColorGradeMidtoneSaturation As Single = 0
        Public Property ColorGradeMidtoneLuminance As Single = 0
        Public Property ColorGradeHighlightHue As Single = 0
        Public Property ColorGradeHighlightSaturation As Single = 0
        Public Property ColorGradeHighlightLuminance As Single = 0
        Public Property ColorGradeGlobalHue As Single = 0
        Public Property ColorGradeGlobalSaturation As Single = 0
        Public Property ColorGradeGlobalLuminance As Single = 0
        ''' <summary>Verschiebt die Grenze zwischen Schatten- und Lichterzone (±100).</summary>
        Public Property ColorGradeBalance As Single = 0
        ''' <summary>Wie weich die Zonen ineinander übergehen (0-100, 50 = neutral). Wirkt als Exponent
        ''' auf die Zonengewichte: kleiner = die Tönungen bleiben stärker in ihrer Zone, größer = sie
        ''' greifen weiter ineinander. Bei 50 rechnet die Kette exakt wie das frühere Split-Toning.</summary>
        Public Property ColorGradeBlending As Single = 50
        Public Property RotationDegrees As Integer = 0
        ' ── Verzerren (perspektivisch) ──────────────────────────────────────
        '
        ' Alle vier laufen -100..100 und sind bei 0 wirkungslos. Sie gehoeren zur GEOMETRIE, nicht
        ' zu den Reglern: sie veraendern, wo ein Bildpunkt landet, und stehen deshalb unten auch in
        ' der Liste der strukturellen Felder.
        Public Property PerspectiveHorizontal As Single = 0
        Public Property PerspectiveVertical As Single = 0
        Public Property PerspectiveAspect As Single = 0
        Public Property PerspectiveScale As Single = 0

        ' Freie Ecken. Acht Werte in PROZENT der Bildbreite bzw. -hoehe, im Uhrzeigersinn ab links
        ' oben, bei 0 wirkungslos. Sie kommen ZUSAETZLICH zu den vier Reglern: die Regler kippen um
        ' die beiden Achsen (symmetrisch, das braucht man fuer stuerzende Linien), die Ecken
        ' erlauben jede Lage, die eine Homographie hergibt.
        '
        ' Warum beides und nicht nur die Ecken: die vier Regler sind in bestehenden Rezepten
        ' gespeichert und behalten hier ihre Bedeutung unveraendert. Und sie sind der bequemere
        ' Griff fuer den haeufigsten Fall - eine Kante geradeziehen, ohne vier Ecken einzeln zu
        ' treffen. Zwei Wege zur selben Matrix, aber nicht zwei Kopien derselben Formel: die Regler
        ' verschieben die Ecken, danach rechnet EINE Stelle weiter.
        Public Property PerspectiveCorner0X As Single = 0
        Public Property PerspectiveCorner0Y As Single = 0
        Public Property PerspectiveCorner1X As Single = 0
        Public Property PerspectiveCorner1Y As Single = 0
        Public Property PerspectiveCorner2X As Single = 0
        Public Property PerspectiveCorner2Y As Single = 0
        Public Property PerspectiveCorner3X As Single = 0
        Public Property PerspectiveCorner3Y As Single = 0

        Public Property StraightenDegrees As Single = 0
        Public Property StraightenExpandCanvas As Boolean = False
        Public Property FlipHorizontal As Boolean = False
        Public Property FlipVertical As Boolean = False
        Public Property CropLeftPercent As Single = 0
        Public Property CropTopPercent As Single = 0
        Public Property CropRightPercent As Single = 0
        Public Property CropBottomPercent As Single = 0
        Public Property ResizeWidth As Integer = 0
        Public Property ResizeHeight As Integer = 0
        Public Property LockResizeAspect As Boolean = True

        ''' <summary>Zielmasse als KASTEN lesen statt als exakte Groesse (Stapel/Export): das Bild
        ''' wird verhaeltniswahrend eingepasst, ein einzelner Wert begrenzt die laengste Kante.
        ''' Der EDITOR setzt das bewusst NICHT: dort haengen beide Felder am Arbeitsmass VOR dem
        ''' Ausrichten - mit Einpassen kaeme bei erweiterter Leinwand etwas anderes heraus, als
        ''' das Panel anzeigt (4000x3000 + 5 Grad ergab 1908x1500 statt
        ''' 2000x1500). Ausserdem ist "laengste Kante" beim Einzelbild ueberraschend.</summary>
        Public Property ResizeFitInsideBox As Boolean = False

        ''' <summary>Prozentuale Zielgroesse (&gt;0) statt fester Masse. Wird auf dem TATSAECHLICH
        ''' dekodierten Bild gerechnet - die Masse vorab aus der Datei zu schaetzen ging fuer
        ''' RAW/PSD/.fpx schief (SKCodec kennt sie nicht) und bei EXIF-gedrehten JPEGs lag es um
        ''' die Drehung daneben.</summary>
        Public Property ResizeScalePercent As Double = 0

        ''' <summary>Kein Hochskalieren: ist das Bild bereits kleiner als die Zielmasse, bleibt es
        ''' unveraendert (wie "Don't Enlarge" in Export-Dialogen bzw. das Suffix "&gt;" bei
        ''' ImageMagick). Verhindert vorgetaeuschte Aufloesung in gemischten Stapeln.</summary>
        Public Property NoResizeUpscale As Boolean = False
        Public Property ResizeInterpolation As ResizeInterpolationMode = ResizeInterpolationMode.Bilinear
        Public Property CanvasWidth As Integer = 0
        Public Property CanvasHeight As Integer = 0
        Public Property LockCanvasAspect As Boolean = True
        Public Property CanvasAnchor As String = "Center"
        ''' <summary>Die Hintergrundfarbe des DOKUMENTS, als "#AARRGGBB". Voellig durchsichtig ist
        ''' der Ausgangszustand und aendert nichts.
        '''
        ''' Der Name stammt aus der Zeit, als sie nur die erweiterte Leinwand fuellte. Sie fuellt
        ''' jetzt ALLES, was sonst durchsichtig bliebe: erweiterte Leinwand, leere Ecken von
        ''' Begradigen und Verzerren, weggeradierte Stellen. Deshalb sitzt sie am ENDE der
        ''' Geometriekette - jede Stufe, die Loecher hinterlaesst, waere sonst eine eigene
        ''' Einstellung, und man muesste an jede einzeln denken. Der Feldname bleibt, damit
        ''' gespeicherte Rezepte weiter gelesen werden.</summary>
        Public Property CanvasBackgroundColor As String = "#00000000"
        Public Property FilterPreset As String = "Keine"
        Public Property FilterStrength As Single = 100
        Public Property LutPath As String = ""
        Public Property LutStrength As Single = 100
        Public Property RetouchSpots As New System.Collections.Generic.List(Of RetouchSpot)()
        Public Property Annotations As New System.Collections.Generic.List(Of ImageAnnotation)()
        ''' <summary>Objekt-Gruppen (siehe <see cref="AnnotationGroup"/>). Verwaist eine Gruppe (kein
        ''' Mitglied mehr), wird sie beim nächsten Aufräumen entfernt.</summary>
        Public Property AnnotationGroups As New System.Collections.Generic.List(Of AnnotationGroup)()
        Public Property RasterPaintStrokes As New System.Collections.Generic.List(Of PixelPaintStroke)()
        ''' <summary>Masken werden einmal gespeichert und können von mehreren lokalen Korrekturen benutzt werden.</summary>
        Public Property Masks As New System.Collections.Generic.List(Of ImageMask)()
        Public Property MaskedAdjustmentLayers As New System.Collections.Generic.List(Of MaskedAdjustmentLayer)()
        ''' <summary>Versionszähler des ARBEITSBILDS: geht in den Base-Cache-Key
        ''' ein und verwirft Pipeline-Caches nach jedem eingebackenen Commit. Kein Bestandteil des
        ''' Rezepts im inhaltlichen Sinn (reiner Cache-Stempel), schadet aber serialisiert nicht.</summary>
        Public Property WorkingImageVersion As Long = 0
        ''' <summary>True, wenn der Radierer (oder transparentes Rastern) Alpha-Löcher ins
        ''' Arbeitsbild gestanzt hat - im .fpx-Rezept persistiert, damit Schachbrett und
        ''' Transparenz-Verhalten das Wiederöffnen überleben.</summary>
        Public Property WorkingImageHasTransparency As Boolean = False
        ''' <summary>Persistenter Render-Skopus der gespeicherten Auswahlmaske. Im Gegensatz zu
        ''' HasActiveSelection ist dies Bildrezept und kein transient markierter UI-Zustand.</summary>
        Public Property SelectionScopeEnabled As Boolean = False
        ''' <summary>True nur solange die Auswahl im Editor aktiv bearbeitet wird. FPX speichert
        ''' diesen transienten UI-Zustand bewusst als False.</summary>
        Public Property HasActiveSelection As Boolean = False

        ''' <summary>Art der aktiven Auswahl: True = MASKE (rotes Overlay), False = AUSWAHL
        ''' (Laufameisen). Gehoert in den gespeicherten Zustand, weil das Wiederherstellen sie sonst
        ''' raten muesste - und pauschal auf "Auswahl" zurueckfiel. Nach einem Rueckgaengig zeigte
        ''' eine Maske dann Laufameisen statt des roten Overlays.</summary>
        Public Property ActiveSelectionIsMask As Boolean = False
        Public Property SelectionXPercent As Double = 0
        Public Property SelectionYPercent As Double = 0
        Public Property SelectionWidthPercent As Double = 0
        Public Property SelectionHeightPercent As Double = 0
        Public Property SelectionShapeMode As String = "Rectangle"
        Public Property SelectionShapePointsX As Double() = Nothing
        Public Property SelectionShapePointsY As Double() = Nothing
        Public Property SelectionMaskLeft As Integer = 0
        Public Property SelectionMaskTop As Integer = 0
        Public Property SelectionMaskRight As Integer = 0
        Public Property SelectionMaskBottom As Integer = 0
        Public Property SelectionMaskPngBase64 As String = ""

        ''' <summary>Weiche Kante der Auswahl in BILDpixeln. Die gespeicherte Maske bleibt hart und
        ''' pixelgenau - weich wird die Kante erst bei der Verwendung (Anpassungs-Skopus, Kopieren, Füllen).
        ''' So lässt sich der Wert jederzeit ändern, ohne die Auswahl neu zu ziehen.</summary>
        Public Property SelectionFeatherPixels As Single = 0

        ''' <summary>True = die Auswahlmaske wurde mit dem Masken-Pinsel gemalt und trägt ihre weiche
        ''' Kante bereits IN den Maskenwerten (weiche Alpha8-Stempel). Dann darf der lazy globale
        ''' Feather (SelectionFeatherPixels) NICHT erneut weichzeichnen, sonst wäre die Kante doppelt
        ''' weich. Für harte geometrische Auswahlen (Rechteck/Ellipse/Lasso/Zauberstab) bleibt es False,
        ''' dort wirkt der globale Feather wie bisher.</summary>
        Public Property SelectionMaskSoftBaked As Boolean = False

        ''' <summary>True = die gemeinsame globale Anpassungsebene (Anpassen, Farbe, Details,
        ''' Effekte und Filter) wird beim Rendern übersprungen. Geometrie, lokale maskierte
        ''' Korrekturen, Retusche und Objekte bleiben davon unberührt.</summary>
        Public Property GlobalAdjustmentsHidden As Boolean = False

        ''' <summary>True = die Hintergrund-Ebene (das Basisbild) wird beim Zusammensetzen ausgeblendet; es
        ''' bleiben nur die Objekt-Ebenen auf transparentem Grund. Strukturell, keine Pixel-Anpassung, und
        ''' gehört NICHT zu den Eigenschaften, die ein einzelnes Objekt mitträgt (siehe StructuralPropertyNames).</summary>
        Public Property BackgroundHidden As Boolean = False

        ''' <summary>True = die eingebackene Pixel-Ebene wird beim Zusammensetzen uebersprungen, der Render
        ''' laeuft also auf dem UNGEBACKENEN Basisbild. Das ist die Ebene "Retusche und Pinsel" im
        ''' Ebenen-Panel: Retusche, Striche und gerasterte Ebenen liegen alle in EINEM Arbeitsbild
        ''' (siehe WorkingImageService) und lassen sich deshalb nur gemeinsam ausblenden. Strukturell,
        ''' keine Pixel-Anpassung (siehe StructuralPropertyNames).</summary>
        Public Property PixelLayerHidden As Boolean = False

        Public Shared Function IsIdentityCurve(pointsCsv As String) As Boolean
            Return String.IsNullOrWhiteSpace(pointsCsv) OrElse String.Equals(pointsCsv.Trim(), "0,0;255,255", StringComparison.Ordinal)
        End Function

        Public Function HasHslChanges() As Boolean
            Return RedHue <> 0 OrElse RedSaturation <> 0 OrElse RedLuminance <> 0 OrElse
                   OrangeHue <> 0 OrElse OrangeSaturation <> 0 OrElse OrangeLuminance <> 0 OrElse
                   YellowHue <> 0 OrElse YellowSaturation <> 0 OrElse YellowLuminance <> 0 OrElse
                   GreenHue <> 0 OrElse GreenSaturation <> 0 OrElse GreenLuminance <> 0 OrElse
                   AquaHue <> 0 OrElse AquaSaturation <> 0 OrElse AquaLuminance <> 0 OrElse
                   BlueHue <> 0 OrElse BlueSaturation <> 0 OrElse BlueLuminance <> 0 OrElse
                   PurpleHue <> 0 OrElse PurpleSaturation <> 0 OrElse PurpleLuminance <> 0 OrElse
                   MagentaHue <> 0 OrElse MagentaSaturation <> 0 OrElse MagentaLuminance <> 0
        End Function

        ''' <summary>Felder, die die STRUKTUR beschreiben: Geometrie, Objekte, Retusche, Auswahl, Quellmaße.
        ''' Alles andere sind Pixel-Anpassungen (Belichtung, Farbe, Details, Effekte, Filter, Kurven, HSL …)
        ''' - und genau die können auch auf ein einzelnes OBJEKT wirken statt aufs Bild (siehe
        ''' <see cref="ImageAnnotation.Adjustments"/>).
        '''
        ''' „Rahmen" steht bewusst hier: er zieht seinen Rand an den BILDkanten. Ein Rahmen um ein Objekt
        ''' wäre etwas anderes und gibt es noch nicht - er bliebe sonst als Rahmen ums ganze Bild stehen,
        ''' während man ein Objekt bearbeitet.</summary>
        Private Shared ReadOnly StructuralPropertyNames As New HashSet(Of String)(StringComparer.Ordinal) From {
            "SourceWidthPixels", "SourceHeightPixels", "RecipeCoordinateVersion",
            "WorkingImageVersion", "WorkingImageHasTransparency",
            "RotationDegrees", "StraightenDegrees", "StraightenExpandCanvas", "FlipHorizontal", "FlipVertical",
            "PerspectiveHorizontal", "PerspectiveVertical", "PerspectiveAspect", "PerspectiveScale",
            "PerspectiveCorner0X", "PerspectiveCorner0Y", "PerspectiveCorner1X", "PerspectiveCorner1Y",
            "PerspectiveCorner2X", "PerspectiveCorner2Y", "PerspectiveCorner3X", "PerspectiveCorner3Y",
            "CropLeftPercent", "CropTopPercent", "CropRightPercent", "CropBottomPercent",
            "ResizeWidth", "ResizeHeight", "LockResizeAspect", "ResizeFitInsideBox", "ResizeScalePercent", "NoResizeUpscale", "ResizeInterpolation",
            "CanvasWidth", "CanvasHeight", "LockCanvasAspect", "CanvasAnchor", "CanvasBackgroundColor",
            "RetouchSpots", "Annotations", "AnnotationGroups", "RasterPaintStrokes", "Masks", "MaskedAdjustmentLayers",
            "SelectionScopeEnabled", "HasActiveSelection", "SelectionXPercent", "SelectionYPercent", "SelectionWidthPercent",
            "SelectionHeightPercent", "SelectionShapeMode", "SelectionShapePointsX", "SelectionShapePointsY",
            "SelectionMaskLeft", "SelectionMaskTop", "SelectionMaskRight", "SelectionMaskBottom",
            "SelectionMaskPngBase64", "SelectionFeatherPixels", "SelectionMaskSoftBaked", "GlobalAdjustmentsHidden", "BackgroundHidden", "PixelLayerHidden"
        }

        Private Shared _pixelProperties As Reflection.PropertyInfo() = Nothing
        Private Shared ReadOnly _pixelPropertiesLock As New Object()

        ''' <summary>Alle Pixel-Anpassungen, per Reflexion aus der Klasse selbst gewonnen: eine neue
        ''' Einstellung ist damit automatisch dabei und kann nicht vergessen werden.</summary>
        Public Shared Function PixelAdjustmentProperties() As Reflection.PropertyInfo()
            SyncLock _pixelPropertiesLock
                If _pixelProperties Is Nothing Then
                    _pixelProperties = GetType(ImageAdjustments).
                        GetProperties(Reflection.BindingFlags.Public Or Reflection.BindingFlags.Instance).
                        Where(Function(p) p.CanRead AndAlso p.CanWrite AndAlso Not StructuralPropertyNames.Contains(p.Name)).
                        ToArray()
                End If
                Return _pixelProperties
            End SyncLock
        End Function

        ''' <summary>Übernimmt alle Pixel-Anpassungen aus <paramref name="other"/>; Struktur bleibt unberührt.</summary>
        Public Sub CopyPixelAdjustmentsFrom(other As ImageAdjustments)
            If other Is Nothing Then Return
            For Each p In PixelAdjustmentProperties()
                p.SetValue(Me, p.GetValue(other))
            Next
        End Sub

        ''' <summary>Legt die Pixel-Anpassungen aus <paramref name="look"/> UEBER die eigenen -
        ''' aber nur die, die vom neutralen Standard abweichen. Alles, was der Look nicht anfasst,
        ''' bleibt so, wie es hier steht.
        '''
        ''' Gebraucht von den Stapelfunktionen: liegt neben einer RAW-Datei ein .fpxmp-Rezept, ist
        ''' DAS die Grundlage, und der Stapelschritt (ein Filter, ein Preset) kommt oben drauf.
        ''' Wuerde stattdessen der ganze Look kopiert, loeschte er mit seinen Nullwerten die
        ''' Bearbeitung des Nutzers - eine entwickelte RAW kaeme flach aus dem Stapel zurueck.
        '''
        ''' Die Kehrseite, bewusst in Kauf genommen: ein Look, der einen Regler ABSICHTLICH auf
        ''' null stellt, setzt sich gegen ein Rezept mit Wert dort nicht durch. Ein Look ist eine
        ''' Zutat, kein Zuruecksetzen - dafuer gibt es den Zuruecksetzen-Pfeil.</summary>
        Public Sub MergeNonDefaultPixelAdjustmentsFrom(look As ImageAdjustments)
            If look Is Nothing Then Return
            Dim neutral = New ImageAdjustments()
            For Each p In PixelAdjustmentProperties()
                Dim value = p.GetValue(look)
                Dim standard = p.GetValue(neutral)
                If Not Equals(value, standard) Then p.SetValue(Me, value)
            Next
        End Sub

        ''' <summary>Nur die Pixel-Anpassungen als eigenes Objekt - das ist der Satz, den ein Objekt mitträgt.</summary>
        Public Function ExtractPixelAdjustments() As ImageAdjustments
            Dim result = New ImageAdjustments()
            result.CopyPixelAdjustmentsFrom(Me)
            Return result
        End Function

        ''' <summary>True, sobald mindestens eine Korrekturebene IN den Objektstapel einsortiert ist.
        ''' Solche Ebenen wirken auf das Komposit und lassen sich deshalb nicht aus dem Basis-Cache
        ''' bedienen - der Editor muss dann voll rendern statt nur eine Region zu flicken.</summary>
        Public Function HasStackedCorrectionLayers() As Boolean
            If MaskedAdjustmentLayers Is Nothing Then Return False
            For Each l In MaskedAdjustmentLayers
                If l IsNot Nothing AndAlso Not String.IsNullOrEmpty(l.StackAboveAnnotationId) Then Return True
            Next
            Return False
        End Function

        ''' <summary>Findet die Gruppe eines Objekts (Nothing, wenn es keiner angehört).</summary>
        Public Function FindAnnotationGroup(annotation As ImageAnnotation) As AnnotationGroup
            If annotation Is Nothing OrElse String.IsNullOrEmpty(annotation.GroupId) Then Return Nothing
            If AnnotationGroups Is Nothing Then Return Nothing
            For Each g In AnnotationGroups
                If g IsNot Nothing AndAlso String.Equals(g.Id, annotation.GroupId, StringComparison.Ordinal) Then Return g
            Next
            Return Nothing
        End Function

        ''' <summary>DER Chokepoint für „wird dieses Objekt gezeichnet?": eigenes IsVisible UND die
        ''' Sichtbarkeit seiner Gruppe. Jede Renderstelle, die bisher `annotation.IsVisible` gelesen hat,
        ''' muss hierüber gehen - sonst blendet der Gruppenschalter im Panel nichts aus.</summary>
        Public Function IsAnnotationRenderVisible(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing OrElse Not annotation.IsVisible Then Return False
            Dim group = FindAnnotationGroup(annotation)
            Return group Is Nothing OrElse group.IsVisible
        End Function

        ''' <summary>Wie IsAnnotationRenderVisible, aber für lokale Korrekturebenen: eigene Sichtbarkeit
        ''' UND die ihrer Gruppe.</summary>
        ''' <summary>Ist dieses Objekt geometrisch gesperrt - eigenes Schloss oder das seiner Gruppe?
        ''' Der Renderer fragt das nie; es entscheidet allein, was der Editor zulässt.</summary>
        Public Function IsAnnotationGeometryLocked(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing Then Return False
            If annotation.IsLocked Then Return True
            If String.IsNullOrEmpty(annotation.GroupId) OrElse AnnotationGroups Is Nothing Then Return False
            For Each g In AnnotationGroups
                If g IsNot Nothing AndAlso String.Equals(g.Id, annotation.GroupId, StringComparison.Ordinal) Then Return g.IsLocked
            Next
            Return False
        End Function

        Public Function IsMaskedLayerRenderVisible(layer As MaskedAdjustmentLayer) As Boolean
            If layer Is Nothing OrElse Not layer.IsVisible Then Return False
            If String.IsNullOrEmpty(layer.GroupId) Then Return True
            If AnnotationGroups Is Nothing Then Return True
            For Each g In AnnotationGroups
                If g IsNot Nothing AndAlso String.Equals(g.Id, layer.GroupId, StringComparison.Ordinal) Then Return g.IsVisible
            Next
            Return True
        End Function

        ''' <summary>True, sobald irgendeine Pixel-Anpassung von der Voreinstellung abweicht. Nur dann muss
        ''' ein Objekt überhaupt über die (teure) eigene Ebene gerendert werden.</summary>
        Public Function HasPixelAdjustments() As Boolean
            Dim neutral = New ImageAdjustments()
            For Each p In PixelAdjustmentProperties()
                If Not Object.Equals(p.GetValue(Me), p.GetValue(neutral)) Then Return True
            Next
            Return False
        End Function

        Public Function Clone() As ImageAdjustments
            Dim result = New ImageAdjustments With {
                .Exposure = Exposure,
                .SourceWidthPixels = SourceWidthPixels,
                .SourceHeightPixels = SourceHeightPixels,
                .RecipeCoordinateVersion = RecipeCoordinateVersion,
                .WorkingImageVersion = WorkingImageVersion,
                .WorkingImageHasTransparency = WorkingImageHasTransparency,
                .Brightness = Brightness,
                .Contrast = Contrast,
                .Saturation = Saturation,
                .Vibrance = Vibrance,
                .CalibrationRedHue = CalibrationRedHue,
                .CalibrationRedSaturation = CalibrationRedSaturation,
                .CalibrationGreenHue = CalibrationGreenHue,
                .CalibrationGreenSaturation = CalibrationGreenSaturation,
                .CalibrationBlueHue = CalibrationBlueHue,
                .CalibrationBlueSaturation = CalibrationBlueSaturation,
                .CalibrationShadowTint = CalibrationShadowTint,
                .Highlights = Highlights,
                .ShadowsLevel = ShadowsLevel,
                .Whites = Whites,
                .Blacks = Blacks,
                .Temperature = Temperature,
                .Tint = Tint,
                .Sharpness = Sharpness,
                .NoiseReduction = NoiseReduction,
                .NoiseReductionMethod = NoiseReductionMethod,
                .ColorNoiseReduction = ColorNoiseReduction,
                .FarbrauschGrob = FarbrauschGrob,
                .ColorNoiseCoarseScale = ColorNoiseCoarseScale,
                .ColorNoiseAdd = ColorNoiseAdd,
                .DustScratches = DustScratches,
                .Haze = Haze,
                .AddNoise = AddNoise,
                .Structure = [Structure],
                .Glow = Glow,
                .Vignette = Vignette,
                .VignetteTransition = VignetteTransition,
                .VignetteRoundness = VignetteRoundness,
                .VignetteFeather = VignetteFeather,
                .VignetteCenterX = VignetteCenterX,
                .VignetteCenterY = VignetteCenterY,
                .Grain = Grain,
                .Clarity = Clarity,
                .NegativeEnabled = NegativeEnabled,
                .NegativeMonochrome = NegativeMonochrome,
                .NegativeBaseColor = NegativeBaseColor,
                .NegativeDensityColor = NegativeDensityColor,
                .NegativeGamma = NegativeGamma,
                .CurveRgbPoints = CurveRgbPoints,
                .CurveRedPoints = CurveRedPoints,
                .CurveGreenPoints = CurveGreenPoints,
                .CurveBluePoints = CurveBluePoints,
                .CurveLuminancePoints = CurveLuminancePoints,
                .RedHue = RedHue,
                .RedSaturation = RedSaturation,
                .RedLuminance = RedLuminance,
                .OrangeHue = OrangeHue,
                .OrangeSaturation = OrangeSaturation,
                .OrangeLuminance = OrangeLuminance,
                .YellowHue = YellowHue,
                .YellowSaturation = YellowSaturation,
                .YellowLuminance = YellowLuminance,
                .GreenHue = GreenHue,
                .GreenSaturation = GreenSaturation,
                .GreenLuminance = GreenLuminance,
                .AquaHue = AquaHue,
                .AquaSaturation = AquaSaturation,
                .AquaLuminance = AquaLuminance,
                .BlueHue = BlueHue,
                .BlueSaturation = BlueSaturation,
                .BlueLuminance = BlueLuminance,
                .PurpleHue = PurpleHue,
                .PurpleSaturation = PurpleSaturation,
                .PurpleLuminance = PurpleLuminance,
                .MagentaHue = MagentaHue,
                .MagentaSaturation = MagentaSaturation,
                .MagentaLuminance = MagentaLuminance,
                .ColorGradeShadowHue = ColorGradeShadowHue,
                .ColorGradeShadowSaturation = ColorGradeShadowSaturation,
                .ColorGradeShadowLuminance = ColorGradeShadowLuminance,
                .ColorGradeMidtoneHue = ColorGradeMidtoneHue,
                .ColorGradeMidtoneSaturation = ColorGradeMidtoneSaturation,
                .ColorGradeMidtoneLuminance = ColorGradeMidtoneLuminance,
                .ColorGradeHighlightHue = ColorGradeHighlightHue,
                .ColorGradeHighlightSaturation = ColorGradeHighlightSaturation,
                .ColorGradeHighlightLuminance = ColorGradeHighlightLuminance,
                .ColorGradeGlobalHue = ColorGradeGlobalHue,
                .ColorGradeGlobalSaturation = ColorGradeGlobalSaturation,
                .ColorGradeGlobalLuminance = ColorGradeGlobalLuminance,
                .ColorGradeBalance = ColorGradeBalance,
                .ColorGradeBlending = ColorGradeBlending,
                .RotationDegrees = RotationDegrees,
                .PerspectiveHorizontal = PerspectiveHorizontal,
                .PerspectiveVertical = PerspectiveVertical,
                .PerspectiveAspect = PerspectiveAspect,
                .PerspectiveScale = PerspectiveScale,
                .PerspectiveCorner0X = PerspectiveCorner0X, .PerspectiveCorner0Y = PerspectiveCorner0Y,
                .PerspectiveCorner1X = PerspectiveCorner1X, .PerspectiveCorner1Y = PerspectiveCorner1Y,
                .PerspectiveCorner2X = PerspectiveCorner2X, .PerspectiveCorner2Y = PerspectiveCorner2Y,
                .PerspectiveCorner3X = PerspectiveCorner3X, .PerspectiveCorner3Y = PerspectiveCorner3Y,
                .StraightenDegrees = StraightenDegrees,
                .StraightenExpandCanvas = StraightenExpandCanvas,
                .FlipHorizontal = FlipHorizontal,
                .FlipVertical = FlipVertical,
                .CropLeftPercent = CropLeftPercent,
                .CropTopPercent = CropTopPercent,
                .CropRightPercent = CropRightPercent,
                .CropBottomPercent = CropBottomPercent,
                .ResizeWidth = ResizeWidth,
                .ResizeHeight = ResizeHeight,
                .LockResizeAspect = LockResizeAspect,
                .ResizeFitInsideBox = ResizeFitInsideBox,
                .ResizeScalePercent = ResizeScalePercent,
                .NoResizeUpscale = NoResizeUpscale,
                .ResizeInterpolation = ResizeInterpolation,
                .CanvasWidth = CanvasWidth,
                .CanvasHeight = CanvasHeight,
                .LockCanvasAspect = LockCanvasAspect,
                .CanvasAnchor = CanvasAnchor,
                .CanvasBackgroundColor = CanvasBackgroundColor,
                .FilterPreset = FilterPreset,
                .FilterStrength = FilterStrength,
                .LutPath = LutPath,
                .LutStrength = LutStrength,
                .RetouchSpots = RetouchSpots.Select(Function(s) s.Clone()).ToList(),
                .Annotations = Annotations.Select(Function(a) a.Clone()).ToList(),
                .AnnotationGroups = If(AnnotationGroups, New System.Collections.Generic.List(Of AnnotationGroup)()).
                    Where(Function(g) g IsNot Nothing).Select(Function(g) g.Clone()).ToList(),
                .RasterPaintStrokes = RasterPaintStrokes.Select(Function(s) s.Clone()).ToList(),
                .Masks = If(Masks, New List(Of ImageMask)()).Where(Function(m) m IsNot Nothing).Select(Function(m) m.Clone()).ToList(),
                .MaskedAdjustmentLayers = If(MaskedAdjustmentLayers, New List(Of MaskedAdjustmentLayer)()).Where(Function(l) l IsNot Nothing).Select(Function(l) l.Clone()).ToList(),
                .SelectionScopeEnabled = SelectionScopeEnabled,
                .HasActiveSelection = HasActiveSelection,
                .ActiveSelectionIsMask = ActiveSelectionIsMask,
                .SelectionXPercent = SelectionXPercent,
                .SelectionYPercent = SelectionYPercent,
                .SelectionWidthPercent = SelectionWidthPercent,
                .SelectionHeightPercent = SelectionHeightPercent,
                .SelectionShapeMode = SelectionShapeMode,
                .SelectionShapePointsX = If(SelectionShapePointsX Is Nothing, Nothing, SelectionShapePointsX.ToArray()),
                .SelectionShapePointsY = If(SelectionShapePointsY Is Nothing, Nothing, SelectionShapePointsY.ToArray()),
                .SelectionMaskLeft = SelectionMaskLeft,
                .SelectionMaskTop = SelectionMaskTop,
                .SelectionMaskRight = SelectionMaskRight,
                .SelectionMaskBottom = SelectionMaskBottom,
                .SelectionMaskPngBase64 = SelectionMaskPngBase64,
                .SelectionFeatherPixels = SelectionFeatherPixels,
                .SelectionMaskSoftBaked = SelectionMaskSoftBaked,
                .GlobalAdjustmentsHidden = GlobalAdjustmentsHidden,
                .BackgroundHidden = BackgroundHidden,
                .PixelLayerHidden = PixelLayerHidden
            }
            ' Die explizite Liste oben hält die strukturellen/deep-copy-Felder lesbar. Pixelwerte werden
            ' zusätzlich zentral kopiert, damit ein neu ergänzter Regler nicht in Undo/FPX/Layern fehlt.
            result.CopyPixelAdjustmentsFrom(Me)
            Return result
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

    ' Partial: die Gleitkomma-Tonwertkette liegt in ImageProcessorPointOps.vb.
    Partial Public Class ImageProcessor

        Private Const FastPngCompressionQuality As Integer = 60

        ''' SKPaint trug bis SkiaSharp 2 die Schrift selbst. Sein interner Ersatz-SKFont hat
        ''' LinearMetrics=True - ein frisch erzeugter SKFont dagegen False, was Textbreiten und das
        ''' Rendering messbar verändert (geprüft: identische Bytes erst mit LinearMetrics=True).
        Private Shared Function CreateFont(fontFamily As String, fontSize As Single,
                                           Optional bold As Boolean = False, Optional italic As Boolean = False) As SKFont
            Return New SKFont(GetTypeface(fontFamily, bold, italic), fontSize) With {.LinearMetrics = True}
        End Function

        ''' SkiaSharp hat SKFilterQuality zugunsten von SKSamplingOptions abgekündigt. Diese Werte sind
        ''' exakt die, auf die SkiaSharp die alten Stufen intern abbildet (siehe SkiaExtensions.ToSamplingOptions):
        ''' High = kubisch (Mitchell), Medium = linear mit Mipmaps.
        ''' Friend: auch PrintService skaliert Bilder auf die Druckseite und braucht dieselbe Abtastung.
        Friend Shared ReadOnly SamplingHigh As New SKSamplingOptions(SKCubicResampler.Mitchell)

        ''' Die mittlere Stufe derselben Abbildung. Friend, weil die Gesichtsausschnitte der
        ''' Infoleiste und der Personenverwaltung sie brauchen: ein Feld von 88 Punkten rechtfertigt
        ''' die kubische Abtastung nicht, und ohne ausdrueckliche Angabe faellt es auf Nearest.
        Friend Shared ReadOnly SamplingMedium As New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)

        ''' Zeichnet eine Bitmap mit ausdrücklicher Abtastung. SKCanvas.DrawBitmap kennt keine
        ''' SKSamplingOptions-Überladung, DrawImage schon - ohne sie fiele die Skalierung auf
        ''' Nearest zurück, weil SKSamplingOptions.Default nicht filtert.
        Friend Shared Sub DrawBitmapSampled(canvas As SKCanvas, bitmap As SKBitmap, source As SKRect, dest As SKRect,
                                             sampling As SKSamplingOptions, paint As SKPaint)
            Using image = SKImage.FromBitmap(bitmap)
                canvas.DrawImage(image, source, dest, sampling, paint)
            End Using
        End Sub

        ''' Friend wie die Rechteck-Fassung darueber: das Vergleichsmodell zeichnet sein Gesicht
        ''' ueber eine gesetzte Matrix und braucht deshalb genau diese Ueberladung.
        Friend Shared Sub DrawBitmapSampled(canvas As SKCanvas, bitmap As SKBitmap, x As Single, y As Single,
                                             sampling As SKSamplingOptions, paint As SKPaint)
            Using image = SKImage.FromBitmap(bitmap)
                canvas.DrawImage(image, x, y, sampling, paint)
            End Using
        End Sub


        ' Cache des zuletzt berechneten Bildes VOR dem Einzeichnen der Objekte (Annotations).
        ' Beim Live-Verschieben/Bearbeiten eines Objekts ändert sich nur dieser letzte Schritt,
        ' daher muss die teure Pipeline (Belichtung, Kurven, Filter, Schärfen, ...) nicht jedes
        ' Mal neu durchlaufen werden - nur bei der Vorschau (previewSource), nicht beim finalen
        ' Export/Speichern, das immer frisch vom Originalbild rechnet.
        Private Shared ReadOnly _baseCacheLock As New Object()
        Private Shared _baseCacheKey As String = Nothing
        Private Shared _baseCacheSourceRef As SKBitmap = Nothing
        Private Shared _baseCacheBitmap As SKBitmap = Nothing

        ''' <summary>Warum ein Objekt-Region-Render kein Patch liefern konnte. Busy darf kurz
        ''' wiederholt werden; Stale braucht zwingend einen neuen Vollrender, weil nur dieser den
        ''' Basis-Cache mit den aktuellen Bildanpassungen aufbauen kann.</summary>
        Public Enum AnnotationPatchCacheState
            Unknown = 0
            Current = 1
            Busy = 2
            Stale = 3
        End Enum

        ' Zweiter Cache neben dem Base-Cache: das Basisbild MIT allen Raster-Strichen (Pinsel/Radierer),
        ' in Preview-Auflösung. Damit muss beim Malen nur das NEUE Strichsegment nachgezeichnet werden,
        ' statt alle Striche pro Maus-Batch neu zu rendern. Der Zustand + die Zeichenlogik liegen in
        ' RasterCompositeCache; hier werden die öffentlichen Einstiege unter _baseCacheLock gehalten und
        ' das gültige Base-Bitmap hereingereicht (siehe TryRenderRasterPaintIncrementalPatch).

        ' Ersetzt SKBitmap.Decode(path) an den Stellen, die das tatsächlich bearbeitete Foto laden
        ' (nicht Icons/Sticker-Assets oder reine Pixel-Statistik) - korrigiert die EXIF-Orientierung
        ' einmalig an der Quelle, damit die gesamte Anpassungs-/Export-Pipeline darauf aufbaut.
        ''' Liefert den zu dekodierenden Bild-Stream für einen Pfad: bei RAW die eingebettete
        ''' JPEG-Vorschau (RawPreviewService), bei PSD/PSB das zusammengesetzte Gesamtbild, sonst
        ''' die Datei direkt.
        '''
        ''' ACHTUNG - das ist NICHT mehr der einzige RAW-Weg: DecodeOriented versucht ZUERST die
        ''' echte RAW-Entwicklung über das System-libraw (volles Demosaic mit Kamera-Weißabgleich,
        ''' und landet erst dann hier. Diese Funktion ist also der
        ''' RÜCKFALL, wenn libraw fehlt oder die Datei nicht entwickelt werden kann.
        ''' Der Satz "Bearbeitung wirkt bei RAW nur auf die eingebettete Vorschau" stimmt seit der
        ''' libraw-Anbindung nur noch für diesen Rückfall.
        '''
        ''' Unverändert gilt: die RAW-Datei wird nie als Schreibziel berührt - Speichern schreibt
        ''' immer in eine neue Zieldatei, Reglerstände gehen in das .fpxmp-Sidecar.
        Private Shared Function OpenSourceStream(path As String) As Stream
            If RawPreviewService.IsSupportedRaw(path) Then Return RawPreviewService.ExtractPreview(path)
            ' ICO ist ein Container, den SkiaSharp nicht kennt - hier als PNG hereingereicht.
            If IcoPreviewService.IsSupportedIco(path) Then Return IcoPreviewService.ExtractPreview(path)
            ' PSD/PSB nur-lesend: das zusammengesetzte Gesamtbild als PNG (siehe PsdPreviewService).
            If PsdPreviewService.IsSupportedPsd(path) Then Return PsdPreviewService.ExtractPreview(path)
            ' HEIC/HEIF/AVIF kann SkiaSharp nicht - libheif liefert es als PNG herein (nur lesend,
            ' siehe HeifDecodeService). Fehlt die Bibliothek, faellt es auf den normalen Datei-
            ' Strom zurueck; der Decode scheitert dann wie bisher sichtbar statt still falsch.
            If HeifDecodeService.IsSupportedHeif(path) AndAlso HeifDecodeService.IsAvailable Then
                Dim heif = HeifDecodeService.ExtractPreview(path)
                If heif IsNot Nothing Then Return heif
            End If
            ' TIFF kann SkiaSharp ebenfalls nicht - LibTiff.NET liefert es als PNG herein.
            If TiffPreviewService.IsSupportedTiff(path) Then
                Dim tiff = TiffPreviewService.ExtractPreview(path)
                If tiff IsNot Nothing Then Return tiff
            End If
            Return File.OpenRead(path)
        End Function

        ''' <summary>Der Weg zum fertigen Bild für Ausgabewege (Drucken, PDF): wie DecodeOriented,
        ''' aber .fpx-Projekte werden aus Basisbild + Rezept gerendert statt als ZIP an den Codec
        ''' gereicht - dort kam bisher Nothing zurück, was in einer leeren Seite endete. Der Aufrufer
        ''' übernimmt das SKBitmap.</summary>
        Friend Shared Function DecodeForOutput(path As String, Optional developRaw As Boolean = True) As SKBitmap
            If FpxService.IsFpx(path) Then Return RenderFpxFullResolution(path)
            Return DecodeOriented(path, developRaw)
        End Function

        ''' <summary>Wie <see cref="DecodeForOutput"/>, wendet aber zusätzlich ein danebenliegendes
        ''' Rezept an: Druck und Collage zeigen damit die BEARBEITETE Fassung - also das Bild, das
        ''' der Nutzer im Editor gespeichert hat, statt der rohen Quelle daneben.
        '''
        ''' Bewusst NICHT in DecodeForOutput selbst: die automatische Bildverbesserung misst dort
        ''' die unbearbeitete Quelle und dürfte das Rezept nicht schon eingerechnet bekommen.</summary>
        ''' <param name="developRaw">Nur wirksam, solange KEIN Rezept vorliegt. Mit Rezept wird immer
        ''' entwickelt - die eingebettete Vorschau wäre dort schlicht das falsche Bild.</param>
        Friend Shared Function DecodeDevelopedForOutput(path As String, Optional developRaw As Boolean = True) As SKBitmap
            If FpxService.IsFpx(path) Then Return RenderFpxFullResolution(path)

            Dim rezept As ImageAdjustments = Nothing
            If RawSidecarService.IsSidecarFormat(path) AndAlso RawSidecarService.Exists(path) Then
                rezept = RawSidecarService.TryRead(path)
            End If

            Dim decoded = DecodeOriented(path, developRaw OrElse rezept IsNot Nothing)
            If decoded Is Nothing OrElse rezept Is Nothing Then Return decoded
            Using decoded
                Return ProcessBitmap(decoded, rezept)
            End Using
        End Function

        ''' <summary>Friend statt Private, damit PrintService dieselbe Dekodier-Route benutzt -
        ''' sie ist die einzige, die RAW/ICO/WebP und die EXIF-Orientierung korrekt behandelt.</summary>
        ''' <param name="developRaw">False = eine RAW-Quelle NICHT entwickeln, sondern ihre
        ''' eingebettete JPEG-Vorschau nehmen. Fuer Stapellaeufe, in denen Geschwindigkeit vor
        ''' Aufloesung geht (Einstellung "RAWs ohne Rezept im Stapel entwickeln"). Auf alles
        ''' andere hat der Schalter keine Wirkung.</param>
        ''' <param name="lensChoice">Welche Objektivkorrekturen fuer DIESES Bild gelten. Nothing =
        ''' wie in den Einstellungen vorgegeben. Sie gehoert hierher und nicht in die Reglerkette:
        ''' sie veraendert den DECODE, nicht die Nachbearbeitung.</param>
        Friend Shared Function DecodeOriented(path As String, Optional developRaw As Boolean = True,
                                              Optional lensChoice As LensDataService.Wahl = Nothing) As SKBitmap
            ' Echte RAW-Entwicklung, wenn das System-libraw da ist: voll aufgelöstes Demosaic mit
            ' Kamera-Weißabgleich statt der eingebetteten JPEG-Vorschau. Liefert der Decode nichts
            ' (defekte Datei, exotisches Format), greift darunter der bisherige Vorschau-Weg.
            If developRaw AndAlso RawPreviewService.IsSupportedRaw(path) AndAlso RawDecodeService.IsAvailable Then
                Dim developed = RawDecodeService.TryDecode(path, lensChoice)
                If developed IsNot Nothing Then Return developed
            ElseIf Not RawPreviewService.IsSupportedRaw(path) Then
                ' Anderes Hauptbild -> der ~180-MB-Entwicklungs-Cache ist stale und kann weg.
                RawDecodeService.ClearCache()
            End If
            ' SKCodec.Create(Stream) übernimmt den Stream, und manche Codecs (insbesondere WebP) schließen
            ' ihn dabei sofort. Ein späteres stream.Seek für den Fallback-Decode wirft dann
            ' ObjectDisposedException - WebP-Quellen ließen sich deshalb weder öffnen noch konvertieren.
            ' Den Inhalt daher einmal in SKData puffern und alle Decode-Pfade daraus bedienen.
            Dim data As SKData
            Using stream = OpenSourceStream(path)
                If stream Is Nothing Then Return Nothing
                data = SKData.Create(stream)
            End Using
            If data Is Nothing Then Return Nothing

            Using data
                Using codec = SKCodec.Create(data)
                    If codec Is Nothing OrElse codec.EncodedOrigin = SKEncodedOrigin.TopLeft Then
                        Return SKBitmap.Decode(data)
                    End If

                    Dim info = codec.Info
                    Dim decodeInfo = New SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Dim original = New SKBitmap(decodeInfo)
                    Dim result = codec.GetPixels(decodeInfo, original.GetPixels())
                    If result <> SKCodecResult.Success AndAlso result <> SKCodecResult.IncompleteInput Then
                        original.Dispose()
                        Return SKBitmap.Decode(data)
                    End If

                    Dim corrected = ImageOrientationService.ApplyOrientation(original, codec.EncodedOrigin)
                    If Not Object.ReferenceEquals(corrected, original) Then original.Dispose()
                    Return corrected
                End Using
            End Using
        End Function

        ''' TEMPORÄR ( Vorher/Nachher dunkler): protokolliert den Farbraum des rohen
        ''' Datei-Decodes und ob eine Farbkonvertierung nach sRGB den Mittelpixel ändert. So lässt sich
        ''' hart bestätigen, ob die Skia-Pipeline (farbraumlose Zwischen-Bitmaps) gegenüber dem
        ''' Avalonia-Decoder (New Bitmap) einen Helligkeits-/Farbversatz erzeugt. Nach der Auswertung
        ''' wieder entfernen.
        Public Shared Sub LogDecodeColorDiagnostics(path As String)
            Try
                If String.IsNullOrWhiteSpace(path) Then Return
                Dim data As SKData
                Using stream = OpenSourceStream(path)
                    If stream Is Nothing Then Return
                    data = SKData.Create(stream)
                End Using
                If data Is Nothing Then Return
                Using data
                    Using raw = SKBitmap.Decode(data)
                        If raw Is Nothing Then Return
                        Dim cs = raw.ColorSpace
                        Dim csDesc = If(cs Is Nothing, "null", If(cs.IsSrgb, "sRGB", "non-sRGB"))
                        Dim cx = raw.Width \ 2
                        Dim cy = raw.Height \ 2
                        Dim pRaw = raw.GetPixel(cx, cy)

                        ' Ziel farbraumlos (wie der Pipeline-Start via New SKBitmap ohne ColorSpace):
                        Dim pNull As SKColor = pRaw
                        Using drawnNull = New SKBitmap(raw.Width, raw.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                            Using canvas = New SKCanvas(drawnNull)
                                canvas.Clear(SKColors.Transparent)
                                canvas.DrawBitmap(raw, 0, 0)
                            End Using
                            pNull = drawnNull.GetPixel(cx, cy)
                        End Using

                        ' Ziel explizit sRGB (farbverwaltet):
                        Dim pSrgb As SKColor = pRaw
                        Dim srgbInfo = New SKImageInfo(raw.Width, raw.Height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb())
                        Using drawnSrgb = New SKBitmap(srgbInfo)
                            Using canvas = New SKCanvas(drawnSrgb)
                                canvas.Clear(SKColors.Transparent)
                                canvas.DrawBitmap(raw, 0, 0)
                            End Using
                            pSrgb = drawnSrgb.GetPixel(cx, cy)
                        End Using

                        DiagnosticLogService.LogAlways("Editor.DecodeColorCheck",
                            $"file={IO.Path.GetFileName(path)} colorType={raw.ColorType} colorSpace={csDesc} " &
                            $"rawCenter=#{pRaw.Red:X2}{pRaw.Green:X2}{pRaw.Blue:X2} " &
                            $"nullTarget=#{pNull.Red:X2}{pNull.Green:X2}{pNull.Blue:X2} " &
                            $"srgbTarget=#{pSrgb.Red:X2}{pSrgb.Green:X2}{pSrgb.Blue:X2} " &
                            $"nullVsSrgbDiffers={pNull <> pSrgb}")
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.DecodeColorCheck", ex)
            End Try
        End Sub

        Public Shared Function ApplyAdjustments(sourcePath As String, adj As ImageAdjustments) As Bitmap
            Using original = DecodeOriented(sourcePath, True, LensChoiceFrom(adj))
                If original Is Nothing Then Return Nothing

                Using processed = ProcessBitmap(original, adj)
                    Return ToAvaloniaBitmap(processed)
                End Using
            End Using
        End Function

        ''' <summary>Obergrenze für das Anzeigebild (composite.png) in einem .fpx-Bündel. Es ist nur
        ''' die schnelle Vorschau für Galerie und Betrachter; die volle Auflösung entsteht beim
        ''' Öffnen wieder aus Basisbild + Rezept. Ohne Deckel wäre jedes Bündel doppelt so groß wie
        ''' nötig.</summary>
        Public Const FpxCompositeMaxDimension As Integer = 2560

        ''' <summary>Kodiert ein Bitmap als PNG in einen Speicherstrom, bei Bedarf auf
        ''' <paramref name="maxDimension"/> verkleinert. Der Aufrufer übernimmt den Strom.</summary>
        Friend Shared Function EncodePngStream(bitmap As SKBitmap, Optional maxDimension As Integer = 0) As MemoryStream
            If bitmap Is Nothing Then Return Nothing
            Dim scaled As SKBitmap = Nothing
            Dim source = bitmap
            Try
                If maxDimension > 0 AndAlso (bitmap.Width > maxDimension OrElse bitmap.Height > maxDimension) Then
                    Dim ratio = Math.Min(maxDimension / CDbl(bitmap.Width), maxDimension / CDbl(bitmap.Height))
                    Dim w = Math.Max(1, CInt(Math.Round(bitmap.Width * ratio)))
                    Dim h = Math.Max(1, CInt(Math.Round(bitmap.Height * ratio)))
                    scaled = bitmap.Resize(New SKImageInfo(w, h), SamplingHigh)
                    If scaled IsNot Nothing Then source = scaled
                End If

                Dim stream = New MemoryStream()
                Using image = SKImage.FromBitmap(source)
                    Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                        If data Is Nothing Then
                            stream.Dispose()
                            Return Nothing
                        End If
                        data.SaveTo(stream)
                    End Using
                End Using
                stream.Position = 0
                Return stream
            Finally
                scaled?.Dispose()
            End Try
        End Function

        Public Shared Function RenderPngStream(sourcePath As String, adj As ImageAdjustments) As MemoryStream
            Dim decodeMs As Long = 0
            Dim processMs As Long = 0
            Dim encodeMs As Long = 0
            Return RenderPngStream(sourcePath, adj, 0, decodeMs, processMs, encodeMs)
        End Function

        Public Shared Function RenderPngStream(sourcePath As String, adj As ImageAdjustments,
                                               maxDimension As Integer,
                                               ByRef decodeMs As Long, ByRef processMs As Long, ByRef encodeMs As Long) As MemoryStream
            Dim sw = Diagnostics.Stopwatch.StartNew()
            Using original = DecodeOriented(sourcePath)
                decodeMs = sw.ElapsedMilliseconds
                If original Is Nothing Then Return Nothing

                Dim workingSource = CreatePreviewWorkingBitmap(original, maxDimension)
                If workingSource Is Nothing Then Return Nothing

                Try
                    sw.Restart()
                    Using processed = ProcessBitmap(workingSource, adj)
                        processMs = sw.ElapsedMilliseconds
                        Return EncodePngStream(processed, encodeMs)
                    End Using
                Finally
                    If Not Object.ReferenceEquals(workingSource, original) Then workingSource.Dispose()
                End Try
            End Using
        End Function

        Public Shared Function RenderPngStream(source As SKBitmap, adj As ImageAdjustments,
                                               ByRef processMs As Long, ByRef encodeMs As Long) As MemoryStream
            If source Is Nothing Then Return Nothing
            Dim sw = Diagnostics.Stopwatch.StartNew()
            Using processed = ProcessBitmap(source, adj)
                processMs = sw.ElapsedMilliseconds
                Return EncodePngStream(processed, encodeMs)
            End Using
        End Function

        Private Shared Function EncodePngStream(bitmap As SKBitmap, ByRef encodeMs As Long) As MemoryStream
            Dim sw = Diagnostics.Stopwatch.StartNew()
            Using image = SKImage.FromBitmap(bitmap)
                ' PNG bleibt verlustfrei; niedrigerer Quality-Wert reduziert hier die Encoder-Arbeit.
                Using data = image.Encode(SKEncodedImageFormat.Png, FastPngCompressionQuality)
                    Dim ms As New MemoryStream()
                    data.SaveTo(ms)
                    ms.Position = 0
                    encodeMs = sw.ElapsedMilliseconds
                    Return ms
                End Using
            End Using
        End Function

        ''' <summary>Die Objektiv-Wahl aus einem Rezept. Eigene Stelle, damit jeder Weg, der aus
        ''' einem Rezept dekodiert, dieselbe Umsetzung benutzt.</summary>
        Friend Shared Function LensChoiceFrom(adj As ImageAdjustments) As LensDataService.Wahl
            If adj Is Nothing Then Return Nothing
            Return New LensDataService.Wahl With {
                .Distortion = adj.LensDistortion,
                .ChromaticAberration = adj.LensTca,
                .Vignetting = adj.LensVignetting,
                .DistortionStrength = adj.LensDistortionAmount / 100.0,
                .ChromaticAberrationStrength = adj.LensTcaAmount / 100.0,
                .VignettingStrength = adj.LensVignettingAmount / 100.0,
                .LensModel = adj.LensModel}
        End Function

        Public Shared Function ApplyAdjustments(source As SKBitmap, adj As ImageAdjustments) As Bitmap
            Return ApplyAdjustments(source, adj, 0)
        End Function

        Public Shared Function ApplyAdjustments(source As SKBitmap, adj As ImageAdjustments, maxDimension As Integer) As Bitmap
            If source Is Nothing Then Return Nothing

            Dim workingSource = CreatePreviewWorkingBitmap(source, maxDimension)
            If workingSource Is Nothing Then Return Nothing

            If Not Object.ReferenceEquals(workingSource, source) Then
                Try
                    Using processed = ProcessBitmap(workingSource, adj)
                        Return ToAvaloniaBitmap(processed)
                    End Using
                Finally
                    workingSource.Dispose()
                End Try
            End If

            SyncLock _baseCacheLock
                Dim baseBitmap = GetOrComputeBaseLocked(workingSource, adj)
                Dim annotated = ApplyAnnotations(baseBitmap, adj)
                Try
                    Return ToAvaloniaBitmap(annotated)
                Finally
                    If Not Object.ReferenceEquals(annotated, baseBitmap) Then annotated.Dispose()
                End Try
            End SyncLock
        End Function

        Public Shared Function RenderPreviewSkBitmap(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing Then Return Nothing
            Return ProcessBitmap(source, adj)
        End Function

        ''' <summary>STUFE 5: Klon der warmen Basis (Pixel-Pipeline INKL. committeter Retusche, OHNE
        ''' Objekte) - das ist exakt das "Zielbild" der Retusche-Live-Puffer. Spart den vollen
        ''' Pipeline-Render, wenn der Cache zur aktuellen Einstellung passt; sonst Nothing.</summary>
        Public Shared Function TryCloneBaseCachedBitmap(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing Then Return Nothing
            If Not Monitor.TryEnter(_baseCacheLock, 12) Then Return Nothing
            Try
                Dim key = ComputeBaseKey(adj)
                If Not Object.ReferenceEquals(_baseCacheSourceRef, source) OrElse
                   Not String.Equals(_baseCacheKey, key, StringComparison.Ordinal) OrElse
                   _baseCacheBitmap Is Nothing Then
                    Return Nothing
                End If
                Return CloneBitmap(_baseCacheBitmap)
            Finally
                Monitor.Exit(_baseCacheLock)
            End Try
        End Function

        ''' <summary>STUFE 2: Szenen-Vollrender als SKBitmap UEBER den Base-Cache (GetOrComputeBaseLocked) -
        ''' im Gegensatz zu RenderPreviewSkBitmap/ProcessBitmap, die den Cache UMGEHEN. Ohne das Waermen
        ''' schlagen ALLE nachfolgenden Region-Renders (TryRenderAnnotationsPatchSkOnCachedBase) dauerhaft
        ''' mit cacheMissOrBusy fehl (Log-Befund). Liefert immer ein eigenes Bitmap
        ''' (Aufrufer disposed).</summary>
        Public Shared Function RenderSceneSkCached(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing Then Return Nothing
            SyncLock _baseCacheLock
                Dim baseBitmap = GetOrComputeBaseLocked(source, adj)
                Dim annotated = ApplyAnnotations(baseBitmap, adj)
                ' Ohne sichtbare Annotationen liefert ApplyAnnotations die gecachte Basis selbst zurueck -
                ' dann klonen, sonst wuerde der Aufrufer das Cache-Bitmap disposen. WICHTIG: als Rgba8888
                ' (CloneBitmapForAnnotationComposite), damit die Szene IMMER dasselbe Pixelformat hat wie
                ' der ApplyAnnotations-Ausgang - der Display-Blit kopiert rohe Bytes und wuerde bei
                ' gemischten Formaten Rot/Blau vertauschen.
                If Object.ReferenceEquals(annotated, baseBitmap) Then Return CloneBitmapForAnnotationComposite(baseBitmap)
                Return annotated
            End SyncLock
        End Function

        Public Shared Function CloneForEditing(source As SKBitmap) As SKBitmap
            If source Is Nothing Then Return Nothing
            Return CloneBitmap(source)
        End Function

        ''' <summary>Schneidet ein Rechteck aus <paramref name="source"/> als Avalonia-Bitmap aus.
        ''' <paramref name="rotationDegrees"/> (0/90/180/270) dreht den AUSGESCHNITTENEN Inhalt zusätzlich -
        ''' nötig für das Retusche-Live-Overlay: dessen Bitmap liegt im ungedrehten Arbeitsbild, das Overlay
        ''' aber über dem per Rezept gedrehten Anzeigebild.</summary>
        Public Shared Function RenderBitmapPatch(source As SKBitmap, rect As SKRectI, Optional rotationDegrees As Integer = 0) As Bitmap
            If source Is Nothing Then Return Nothing

            Dim clipped = New SKRectI(Math.Max(0, rect.Left),
                                      Math.Max(0, rect.Top),
                                      Math.Min(source.Width, rect.Right),
                                      Math.Min(source.Height, rect.Bottom))
            If clipped.Width <= 0 OrElse clipped.Height <= 0 Then Return Nothing

            Using patch = New SKBitmap(clipped.Width, clipped.Height, source.ColorType, source.AlphaType)
                Using canvas = New SKCanvas(patch)
                    canvas.Clear(SKColors.Transparent)
                    canvas.DrawBitmap(source,
                                      New SKRect(clipped.Left, clipped.Top, clipped.Right, clipped.Bottom),
                                      New SKRect(0, 0, clipped.Width, clipped.Height))
                End Using
                Dim q = (((rotationDegrees \ 90) Mod 4) + 4) Mod 4
                If q = 0 Then Return ToAvaloniaBitmap(patch)
                Using rotated = RotateBitmapQuarter(patch, q)
                    Return ToAvaloniaBitmap(If(rotated, patch))
                End Using
            End Using
        End Function

        Public Shared Function RenderChangedBitmapPatch(source As SKBitmap,
                                                        baseline As SKBitmap,
                                                        rect As SKRectI,
                                                        Optional tolerance As Integer = 1) As Bitmap
            If source Is Nothing Then Return Nothing

            Dim clipped = New SKRectI(Math.Max(0, rect.Left),
                                      Math.Max(0, rect.Top),
                                      Math.Min(source.Width, rect.Right),
                                      Math.Min(source.Height, rect.Bottom))
            If clipped.Width <= 0 OrElse clipped.Height <= 0 Then Return Nothing

            Using patch = New SKBitmap(clipped.Width, clipped.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(patch)
                    canvas.Clear(SKColors.Transparent)
                    canvas.DrawBitmap(source,
                                      New SKRect(clipped.Left, clipped.Top, clipped.Right, clipped.Bottom),
                                      New SKRect(0, 0, clipped.Width, clipped.Height))
                End Using

                If baseline IsNot Nothing AndAlso baseline.Width = source.Width AndAlso baseline.Height = source.Height Then
                    Using baselinePatch = New SKBitmap(clipped.Width, clipped.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                        Using canvas = New SKCanvas(baselinePatch)
                            canvas.Clear(SKColors.Transparent)
                            canvas.DrawBitmap(baseline,
                                              New SKRect(clipped.Left, clipped.Top, clipped.Right, clipped.Bottom),
                                              New SKRect(0, 0, clipped.Width, clipped.Height))
                        End Using

                        Dim tol = Math.Max(0, tolerance)
                        Dim rowBytes = patch.RowBytes
                        Dim activeBytes = clipped.Width * 4
                        Dim patchRow = ArrayPool(Of Byte).Shared.Rent(rowBytes)
                        Dim baselineRow = ArrayPool(Of Byte).Shared.Rent(rowBytes)
                        Try
                            ' Nur je eine Zeile statt zwei kompletter Patch-Kopien puffern. Dieser
                            ' Pfad laeuft waehrend Verwischen und Stempeln bis zu etwa 40-mal/s und
                            ' das Patch-Rechteck waechst ueber den ganzen Zug; flaechenbreite Byte-
                            ' Arrays verursachten deshalb vermeidbare LOH-/GC-Spitzen.
                            Dim patchPixels = patch.GetPixels()
                            Dim baselinePixels = baselinePatch.GetPixels()
                            For y = 0 To clipped.Height - 1
                                Dim nativeOffset = y * rowBytes
                                Marshal.Copy(IntPtr.Add(patchPixels, nativeOffset), patchRow, 0, rowBytes)
                                Marshal.Copy(IntPtr.Add(baselinePixels, nativeOffset), baselineRow, 0, rowBytes)
                                For offset = 0 To activeBytes - 4 Step 4
                                    Dim delta = Math.Abs(CInt(patchRow(offset)) - CInt(baselineRow(offset))) +
                                                Math.Abs(CInt(patchRow(offset + 1)) - CInt(baselineRow(offset + 1))) +
                                                Math.Abs(CInt(patchRow(offset + 2)) - CInt(baselineRow(offset + 2))) +
                                                Math.Abs(CInt(patchRow(offset + 3)) - CInt(baselineRow(offset + 3)))
                                    If delta <= tol Then
                                        patchRow(offset) = 0
                                        patchRow(offset + 1) = 0
                                        patchRow(offset + 2) = 0
                                        patchRow(offset + 3) = 0
                                    End If
                                Next
                                Marshal.Copy(patchRow, 0, IntPtr.Add(patchPixels, nativeOffset), rowBytes)
                            Next
                        Finally
                            ArrayPool(Of Byte).Shared.Return(patchRow)
                            ArrayPool(Of Byte).Shared.Return(baselineRow)
                        End Try
                    End Using
                End If

                Return ToAvaloniaBitmap(patch)
            End Using
        End Function

        ''' <summary>90°-Schritt-Drehung eines SKBitmap im Uhrzeigersinn (gleiche Transform wie
        ''' ApplyGeometryTransforms). q: 1=90°, 2=180°, 3=270°. Nothing bei Fehler.</summary>
        Friend Shared Function RotateBitmapQuarter(src As SKBitmap, q As Integer) As SKBitmap
            Dim n = ((q Mod 4) + 4) Mod 4
            If n = 0 OrElse src Is Nothing Then Return Nothing
            Dim swap = (n = 1 OrElse n = 3)
            Dim rw = If(swap, src.Height, src.Width)
            Dim rh = If(swap, src.Width, src.Height)
            Dim rotated As SKBitmap = Nothing
            Try
                rotated = New SKBitmap(New SKImageInfo(rw, rh, src.ColorType, src.AlphaType))
                Using canvas = New SKCanvas(rotated)
                    canvas.Clear(SKColors.Transparent)
                    Select Case n
                        Case 1
                            canvas.Translate(rw, 0) : canvas.RotateDegrees(90)
                        Case 2
                            canvas.Translate(rw, rh) : canvas.RotateDegrees(180)
                        Case 3
                            canvas.Translate(0, rh) : canvas.RotateDegrees(270)
                    End Select
                    canvas.DrawBitmap(src, 0, 0)
                End Using
                Return rotated
            Catch
                rotated?.Dispose()
                Return Nothing
            End Try
        End Function

        Public Shared Function RenderRetouchMaskPatch(spots As IEnumerable(Of RetouchSpot),
                                                      rect As SKRectI,
                                                      bitmapWidth As Integer,
                                                      bitmapHeight As Integer,
                                                      sourceWidthPixels As Integer,
                                                      sourceHeightPixels As Integer,
                                                      Optional rotationDegrees As Integer = 0) As Bitmap
            If spots Is Nothing OrElse bitmapWidth <= 0 OrElse bitmapHeight <= 0 Then Return Nothing

            Dim clipped = New SKRectI(Math.Max(0, rect.Left),
                                      Math.Max(0, rect.Top),
                                      Math.Min(bitmapWidth, rect.Right),
                                      Math.Min(bitmapHeight, rect.Bottom))
            If clipped.Width <= 0 OrElse clipped.Height <= 0 Then Return Nothing

            Dim scaleX As Single = 1.0F
            Dim scaleY As Single = 1.0F
            If sourceWidthPixels > 0 AndAlso sourceHeightPixels > 0 Then
                scaleX = bitmapWidth / CSng(sourceWidthPixels)
                scaleY = bitmapHeight / CSng(sourceHeightPixels)
            End If
            Dim radiusScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))

            Using patch = New SKBitmap(clipped.Width, clipped.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(patch)
                    canvas.Clear(SKColors.Transparent)
                    canvas.Translate(-clipped.Left, -clipped.Top)
                    Using paint = New SKPaint With {
                        .Color = New SKColor(255, 136, 0, 96),
                        .Style = SKPaintStyle.Fill,
                        .IsAntialias = True
                    }
                        For Each spot In spots
                            If spot Is Nothing Then Continue For
                            Dim cx = Clamp(spot.XPixels * scaleX, 0, bitmapWidth)
                            Dim cy = Clamp(spot.YPixels * scaleY, 0, bitmapHeight)
                            Dim radius = Math.Max(1.0F, spot.RadiusPixels * radiusScale)
                            canvas.DrawCircle(cx, cy, radius, paint)
                        Next
                    End Using
                End Using
                Dim q = (((rotationDegrees \ 90) Mod 4) + 4) Mod 4
                If q = 0 Then Return ToAvaloniaBitmap(patch)
                Using rotated = RotateBitmapQuarter(patch, q)
                    Return ToAvaloniaBitmap(If(rotated, patch))
                End Using
            End Using
        End Function

        ''' Schneller, UI-Thread-tauglicher Pfad, der NUR die Annotationen auf ein bereits
        ''' gecachtes Base-Bitmap neu komposiert (kein Neuberechnen der teuren Anpassungs-Pipeline).
        ''' Wird genutzt, um beim (De-)Selektieren eines Text-/Wasserzeichen-Objekts das Ausblenden
        ''' im gebackenen Vorschaubild synchron mit dem Anzeigen des Live-Overlays zu koppeln, statt
        ''' auf einen asynchronen Task.Run-Render zu warten (siehe EditorViewModel.TryRenderAnnotationOverlaySync).
        ''' Nutzt Monitor.TryEnter mit kurzem Timeout statt eines blockierenden SyncLock, damit der
        ''' UI-Thread nie hängt, falls ein Hintergrund-Task den Cache gerade neu berechnet - in dem
        ''' Fall (oder bei kaltem Cache) liefert die Funktion Nothing, der Aufrufer fällt dann auf
        ''' den bestehenden asynchronen Renderpfad zurück.
        Public Shared Function TryRenderAnnotationsOnCachedBase(source As SKBitmap, adj As ImageAdjustments) As Bitmap
            If source Is Nothing Then Return Nothing

            If Not Monitor.TryEnter(_baseCacheLock, 12) Then Return Nothing
            Try
                Dim key = ComputeBaseKey(adj)
                If Not Object.ReferenceEquals(_baseCacheSourceRef, source) OrElse
                   Not String.Equals(_baseCacheKey, key, StringComparison.Ordinal) OrElse
                   _baseCacheBitmap Is Nothing Then
                    Return Nothing
                End If

                Dim annotated = ApplyAnnotations(_baseCacheBitmap, adj)
                Try
                    Return ToAvaloniaBitmap(annotated)
                Finally
                    If Not Object.ReferenceEquals(annotated, _baseCacheBitmap) Then annotated.Dispose()
                End Try
            Finally
                Monitor.Exit(_baseCacheLock)
            End Try
        End Function

        ''' <summary>
        ''' Rendert nur einen Bildausschnitt aus dem gecachten Basisbild plus aktueller Annotationen.
        ''' Der Pfad vermeidet beim Verschieben/Ändern von Objekten den teuren Vollbild-Composite.
        ''' </summary>
        Public Shared Function TryRenderAnnotationsPatchOnCachedBase(source As SKBitmap, adj As ImageAdjustments, dirtyRect As SKRectI) As Bitmap
            Dim clampedRect As SKRectI
            Dim patch = TryRenderAnnotationsPatchSkOnCachedBase(source, adj, dirtyRect, clampedRect)
            If patch Is Nothing Then Return Nothing
            Using patch
                Return ToAvaloniaBitmap(patch)
            End Using
        End Function

        ''' <summary>SK-Kern des Region-Renderers (Basis + Striche + Objekte im Dirty-Rect). Liefert das
        ''' Patch-SKBitmap (Aufrufer disposed) und per clampedRect die tatsächlich gerenderte Region -
        ''' der Szenen-Renderer zeichnet das Patch dort in die persistente Szene. Nothing bei kaltem/
        ''' gesperrtem Base-Cache.</summary>
        Public Shared Function TryRenderAnnotationsPatchSkOnCachedBase(source As SKBitmap, adj As ImageAdjustments, dirtyRect As SKRectI,
                                                                       ByRef clampedRect As SKRectI) As SKBitmap
            Dim ignored = AnnotationPatchCacheState.Unknown
            Dim ignoredDrawn = 0
            Return TryRenderAnnotationsPatchSkOnCachedBase(source, adj, dirtyRect, clampedRect, ignored, ignoredDrawn)
        End Function

        Public Shared Function TryRenderAnnotationsPatchSkOnCachedBase(source As SKBitmap, adj As ImageAdjustments, dirtyRect As SKRectI,
                                                                       ByRef clampedRect As SKRectI,
                                                                       ByRef cacheState As AnnotationPatchCacheState) As SKBitmap
            Dim ignoredDrawn = 0
            Return TryRenderAnnotationsPatchSkOnCachedBase(source, adj, dirtyRect, clampedRect, cacheState, ignoredDrawn)
        End Function

        ''' <param name="drawnObjects">Messpunkt fuer den Kompositor-Umbau: wie viele Objekte dieser
        ''' Patch WIRKLICH gezeichnet hat (nach Sichtbarkeits- und Clip-Pruefung).</param>
        Public Shared Function TryRenderAnnotationsPatchSkOnCachedBase(source As SKBitmap, adj As ImageAdjustments, dirtyRect As SKRectI,
                                                                       ByRef clampedRect As SKRectI,
                                                                       ByRef cacheState As AnnotationPatchCacheState,
                                                                       ByRef drawnObjects As Integer) As SKBitmap
            clampedRect = SKRectI.Empty
            cacheState = AnnotationPatchCacheState.Unknown
            drawnObjects = 0
            If source Is Nothing OrElse dirtyRect.IsEmpty Then
                cacheState = AnnotationPatchCacheState.Stale
                Return Nothing
            End If

            If Not Monitor.TryEnter(_baseCacheLock, 12) Then
                cacheState = AnnotationPatchCacheState.Busy
                Return Nothing
            End If
            Try
                Dim key = ComputeBaseKey(adj)
                If Not Object.ReferenceEquals(_baseCacheSourceRef, source) OrElse
                   Not String.Equals(_baseCacheKey, key, StringComparison.Ordinal) OrElse
                   _baseCacheBitmap Is Nothing Then
                    cacheState = AnnotationPatchCacheState.Stale
                    Return Nothing
                End If
                cacheState = AnnotationPatchCacheState.Current

                Dim rect = ClampRectToBitmap(dirtyRect, _baseCacheBitmap.Width, _baseCacheBitmap.Height)
                If rect.IsEmpty OrElse rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing

                Dim patch = New SKBitmap(rect.Width, rect.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(patch)
                    canvas.Clear(SKColors.Transparent)
                    ' ARBEITSBILD-Umbau (Stufe D): Pinsel-/Radiererstriche sind ins Arbeitsbild
                    ' eingebacken und stecken damit bereits im Base-Cache-Bitmap - der Patch
                    ' schneidet nur noch Basis-Slice + Z-Order-Objekte (der RasterCompositeCache
                    ' und sein Strich-Stamp sind entfallen).
                    If adj Is Nothing OrElse Not adj.BackgroundHidden Then
                        canvas.DrawBitmap(_baseCacheBitmap,
                                          New SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                                          New SKRect(0, 0, rect.Width, rect.Height))
                    End If
                    If adj IsNot Nothing AndAlso adj.Annotations IsNot Nothing AndAlso adj.Annotations.Count > 0 Then
                        drawnObjects = DrawAnnotationsOnCanvas(canvas, adj, _baseCacheBitmap.Width, _baseCacheBitmap.Height,
                                                               rect.Left, rect.Top, rect.Width, rect.Height, adj.Annotations)
                    End If
                End Using
                clampedRect = rect
                Return patch
            Finally
                Monitor.Exit(_baseCacheLock)
            End Try
        End Function

        ''' <summary>Dirty-Rect eines Objekts im ZIEL-Koordinatenraum (sourceWidth/Height, z.B. die
        ''' gedeckelte Preview). Die Annotation ist in BASIS-Bildpixeln gespeichert; baseWidth/baseHeight
        ''' geben diesen Basisraum an, damit hier dieselbe Skalierung greift wie beim Zeichnen
        ''' (DrawAnnotationsOnCanvas via ScaleAnnotationForSource). 0/0 oder gleiche Masse = keine
        ''' Skalierung (historisches Verhalten, als preview==base galt).</summary>
        Public Shared Function ComputeAnnotationDirtyRect(sourceWidth As Integer, sourceHeight As Integer, annotation As ImageAnnotation,
                                                          Optional baseWidth As Integer = 0, Optional baseHeight As Integer = 0) As SKRectI
            If annotation Is Nothing OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return SKRectI.Empty
            If baseWidth > 0 AndAlso baseHeight > 0 AndAlso (baseWidth <> sourceWidth OrElse baseHeight <> sourceHeight) Then
                annotation = ScaleAnnotationForSource(annotation, sourceWidth / CSng(baseWidth), sourceHeight / CSng(baseHeight))
            End If
            Return ComputeAnnotationDirtyRectCore(sourceWidth, sourceHeight, annotation)
        End Function

        Public Shared Function ComputeAnnotationDirtyRect(sourceWidth As Integer, sourceHeight As Integer, annotation As ImageAnnotation,
                                                          adj As ImageAdjustments) As SKRectI
            If annotation Is Nothing OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return SKRectI.Empty
            annotation = TransformAnnotationForGeometry(annotation, adj, sourceWidth, sourceHeight)
            Return ComputeAnnotationDirtyRectCore(sourceWidth, sourceHeight, annotation)
        End Function

        ''' <summary>
        ''' Wo liegt eine MASKE in der Szene? Gerechnet wird über dieselbe Geometriekette wie bei
        ''' Objekten - dafür wandert das Maskenrechteck als Pseudo-Objekt hindurch, statt die Kette
        ''' (Crop, Vierteldrehung, Spiegelung, Skalierung) ein zweites Mal nachzubilden. Eine zweite
        ''' Formel wäre genau die Sorte Abweichung, die im Koordinaten-Audit steht.
        '''
        ''' Gebraucht wird das, um zu entscheiden, ob eine im Objektstapel hängende Korrektur eine
        ''' bestimmte Region überhaupt berührt (sonst darf der schnelle Region-Patch laufen).
        ''' Die weiche Kante wird großzügig mitgerechnet.
        ''' </summary>
        Public Shared Function ComputeMaskDirtyRect(sourceWidth As Integer, sourceHeight As Integer,
                                                    mask As ImageMask, adj As ImageAdjustments) As SKRectI
            If mask Is Nothing OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return SKRectI.Empty
            Dim width = mask.Right - mask.Left
            Dim height = mask.Bottom - mask.Top
            If width <= 0 OrElse height <= 0 Then Return SKRectI.Empty
            Dim border = Math.Max(0.0F, mask.FeatherPixels) + 2.0F
            Dim pseudo As New ImageAnnotation With {
                .Kind = "Rectangle",
                .XPixels = mask.Left - border,
                .YPixels = mask.Top - border,
                .WidthPixels = width + 2.0F * border,
                .HeightPixels = height + 2.0F * border
            }
            Return ComputeAnnotationDirtyRect(sourceWidth, sourceHeight, pseudo, adj)
        End Function

        Private Shared Function ComputeAnnotationDirtyRectCore(sourceWidth As Integer, sourceHeight As Integer, annotation As ImageAnnotation) As SKRectI
            If annotation Is Nothing OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return SKRectI.Empty
            Dim kind = If(annotation.Kind, "Text").Trim().ToLowerInvariant()
            If IsPaintKind(kind) Then
                Dim bounds As SKRect? = Nothing
                If annotation.Strokes IsNot Nothing Then
                    For Each stroke In annotation.Strokes
                        If stroke Is Nothing OrElse stroke.Points Is Nothing Then Continue For
                        For Each pt In stroke.Points
                            Dim pRect = New SKRect(CSng(pt.X), CSng(pt.Y), CSng(pt.X), CSng(pt.Y))
                            If bounds.HasValue Then
                                Dim b = bounds.Value
                                b.Union(pRect)
                                bounds = b
                            Else
                                bounds = pRect
                            End If
                        Next
                    Next
                End If
                If Not bounds.HasValue Then Return SKRectI.Empty
                ' Muss die RENDER-Reichweite von DrawBrushStrokeWithEffects spiegeln:
                ' dort pad = strokeWidth + |shadowDx| + |shadowDy| + 3*max(shadowSigma, glowSigma) + 4.
                ' Der alte Pauschalwert Max(4, strokeWidth*2) war kleiner als die Glow-Reichweite
                ' (glowSigma bis 0,8*strokeWidth => 3 Sigma = 2,4*strokeWidth ZUSAETZLICH zum Strich) -
                ' beim Patch-Render blieben abgeschnittene/veraltete Glow-Saeume am Patchrand stehen.
                ' Formen/Text rechnen ihre Effektraender unten laengst ein, nur Paint-Kinds nicht.
                Dim paintObjSize = Math.Max(1.0F, annotation.StrokeWidth)
                Dim paintShadowDx = If(annotation.ShadowEnabled, Clamp(annotation.ShadowOffsetXPercent, -100, 100) / 100.0F * paintObjSize, 0.0F)
                Dim paintShadowDy = If(annotation.ShadowEnabled, Clamp(annotation.ShadowOffsetYPercent, -100, 100) / 100.0F * paintObjSize, 0.0F)
                Dim paintShadowSigma = If(annotation.ShadowEnabled, Clamp(annotation.ShadowBlur, 0, 100) / 100.0F * paintObjSize * ShadowBlurSigmaFactor, 0.0F)
                Dim paintGlowSigma = If(annotation.GlowEnabled, Clamp(annotation.GlowBlur, 0, 100) / 100.0F * paintObjSize * 0.8F, 0.0F)
                Dim paintPad = Math.Max(Math.Max(4.0F, annotation.StrokeWidth * 2.0F),
                                        paintObjSize + Math.Abs(paintShadowDx) + Math.Abs(paintShadowDy) +
                                        Math.Max(paintShadowSigma, paintGlowSigma) * 3.0F + 4.0F)
                Return ClampRectToBitmap(InflateToRectI(bounds.Value, paintPad), sourceWidth, sourceHeight)
            End If

            Dim rect = ComputeAnnotationRect(sourceWidth, sourceHeight, kind, annotation)
            rect = RotationBounds(rect, annotation.RotationDegrees)

            Dim extent = Math.Max(rect.Width, rect.Height)
            Dim effectPad = Math.Max(8.0F, annotation.StrokeWidth * 3.0F)
            ' Text an Pfad: die Glyphen stehen SENKRECHT zum Pfad und ragen bis zu einer
            ' Schrifthoehe ueber das Layout-Rechteck hinaus (Baseline liegt AUF dem Pfad).
            If Not String.IsNullOrWhiteSpace(annotation.TextPathKind) Then
                ' EFFEKTIVE Groesse: der Kreis-Fit kann die Schrift ueber FontSizePixels hinaus wachsen lassen.
                effectPad = Math.Max(effectPad, annotation.FontSizePixels * ComputeTextPathFitRatio(annotation) * 1.2F)
            End If
            If annotation.ShadowEnabled Then
                Dim objSize = Math.Max(1.0F, Math.Min(rect.Width, rect.Height))
                Dim shadowBlurPx = objSize * Clamp(annotation.ShadowBlur, 0, 100) / 100.0F * ShadowBlurSigmaFactor
                Dim shadowOffset = Math.Max(Math.Abs(objSize * annotation.ShadowOffsetXPercent / 100.0F),
                                            Math.Abs(objSize * annotation.ShadowOffsetYPercent / 100.0F))
                Dim shadowGrow = Math.Max(0.0F, Clamp(annotation.ShadowSizePercent, 10, 400) / 100.0F - 1.0F) * objSize * 0.5F
                effectPad = Math.Max(effectPad, shadowBlurPx * 3.0F + shadowOffset + shadowGrow + 4.0F)
            End If
            If annotation.GlowEnabled Then
                Dim objSize = Math.Max(1.0F, Math.Min(rect.Width, rect.Height))
                Dim glowReach = objSize * Clamp(annotation.GlowBlur, 0, 100) / 100.0F * 1.5F
                Dim glowDilate = Math.Max(0, CInt(Math.Round(glowReach * 0.5F)))
                Dim glowSigma = Math.Max(0.1F, glowReach * 0.17F)
                effectPad = Math.Max(effectPad, glowDilate + 3.0F * glowSigma + 4.0F)
            End If
            If HasObjectAdjustments(annotation) OrElse Not IsNormalAnnotationBlendMode(annotation.BlendMode) Then
                effectPad = Math.Max(effectPad, 24.0F)
            End If

            Return ClampRectToBitmap(InflateToRectI(rect, effectPad), sourceWidth, sourceHeight)
        End Function

        Public Shared Function ComputePixelPaintDirtyRect(sourceWidth As Integer,
                                                          sourceHeight As Integer,
                                                          paintStroke As PixelPaintStroke,
                                                          Optional lastStroke As BrushStroke = Nothing) As SKRectI
            If paintStroke Is Nothing OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return SKRectI.Empty
            Dim renderStroke = paintStroke.ToRenderAnnotation()
            If lastStroke IsNot Nothing Then renderStroke.Strokes = New List(Of BrushStroke) From {lastStroke}
            Return ComputeAnnotationDirtyRect(sourceWidth, sourceHeight, renderStroke)
        End Function

        ''' <summary>Rechnet ein Rect von einem Koordinatenraum in einen anderen um (z.B. Basis-Bildpixel ->
        ''' gedeckelte Preview). Konservativ gerundet (Floor/Ceiling), damit der Zielbereich nie kleiner wird
        ''' als der Quellbereich - ein zu kleines Dirty-Rect liesse Randpixel veraltet stehen.</summary>
        Public Shared Function ScaleRectBetweenSpaces(rect As SKRectI,
                                                      fromWidth As Integer, fromHeight As Integer,
                                                      toWidth As Integer, toHeight As Integer) As SKRectI
            If rect.IsEmpty OrElse fromWidth <= 0 OrElse fromHeight <= 0 OrElse toWidth <= 0 OrElse toHeight <= 0 Then
                Return SKRectI.Empty
            End If
            If fromWidth = toWidth AndAlso fromHeight = toHeight Then Return rect
            Dim sx = toWidth / CDbl(fromWidth)
            Dim sy = toHeight / CDbl(fromHeight)
            Return New SKRectI(CInt(Math.Floor(rect.Left * sx)),
                               CInt(Math.Floor(rect.Top * sy)),
                               CInt(Math.Ceiling(rect.Right * sx)),
                               CInt(Math.Ceiling(rect.Bottom * sy)))
        End Function

        Public Shared Function UnionRects(a As SKRectI, b As SKRectI) As SKRectI
            If a.IsEmpty Then Return b
            If b.IsEmpty Then Return a
            Return New SKRectI(Math.Min(a.Left, b.Left),
                               Math.Min(a.Top, b.Top),
                               Math.Max(a.Right, b.Right),
                               Math.Max(a.Bottom, b.Bottom))
        End Function

        Private Shared Function InflateToRectI(rect As SKRect, padding As Single) As SKRectI
            Return New SKRectI(CInt(Math.Floor(rect.Left - padding)),
                               CInt(Math.Floor(rect.Top - padding)),
                               CInt(Math.Ceiling(rect.Right + padding)),
                               CInt(Math.Ceiling(rect.Bottom + padding)))
        End Function

        Friend Shared Function ClampRectToBitmap(rect As SKRectI, width As Integer, height As Integer) As SKRectI
            If width <= 0 OrElse height <= 0 OrElse rect.IsEmpty Then Return SKRectI.Empty
            Dim left = Math.Max(0, Math.Min(width, rect.Left))
            Dim top = Math.Max(0, Math.Min(height, rect.Top))
            Dim right = Math.Max(left, Math.Min(width, rect.Right))
            Dim bottom = Math.Max(top, Math.Min(height, rect.Bottom))
            If right <= left OrElse bottom <= top Then Return SKRectI.Empty
            Return New SKRectI(left, top, right, bottom)
        End Function

        Private Shared Function RotationBounds(rect As SKRect, degrees As Single) As SKRect
            If Math.Abs(degrees) < 0.01F Then Return rect
            Dim radians = degrees * Math.PI / 180.0
            Dim cos = Math.Cos(radians)
            Dim sin = Math.Sin(radians)
            Dim cx = rect.MidX
            Dim cy = rect.MidY
            Dim xs = New Single() {rect.Left, rect.Right, rect.Right, rect.Left}
            Dim ys = New Single() {rect.Top, rect.Top, rect.Bottom, rect.Bottom}
            Dim minX As Single = Single.MaxValue
            Dim minY As Single = Single.MaxValue
            Dim maxX As Single = Single.MinValue
            Dim maxY As Single = Single.MinValue
            For i = 0 To 3
                Dim dx = xs(i) - cx
                Dim dy = ys(i) - cy
                Dim x = CSng(cx + dx * cos - dy * sin)
                Dim y = CSng(cy + dx * sin + dy * cos)
                minX = Math.Min(minX, x)
                minY = Math.Min(minY, y)
                maxX = Math.Max(maxX, x)
                maxY = Math.Max(maxY, y)
            Next
            Return New SKRect(minX, minY, maxX, maxY)
        End Function

        ' Obergrenze für die interne Renderauflösung des Auswahl-Overlays. Das Bitmap wird ohnehin
        ' per Stretch="Fill" auf die Bildschirmgröße skaliert, daher genügt eine gedeckelte Auflösung.
        ' Wichtig für die Performance: RenderAnnotationOverlay wird bei JEDER Schatten-/Glow-Slider-
        ' Änderung neu aufgerufen (siehe EditorViewModel.SyncSelectedAnnotation) und das Bitmap wächst
        ' um den (bei großem Blur erheblichen) Effekt-Rand - ohne Deckelung würden mehrere hundert MB
        ' pro Slider-Tick auf dem UI-Thread alloziert.
        Private Const MaxOverlayRenderDim As Single = 720.0F

        ''' Ergebnis von RenderAnnotationOverlay: das Bitmap UND die Lage des Objekts darin (Bitmap-Pixel).
        ''' Die View braucht beides: sie legt das Bitmap per Stretch="Fill" und negativen Margins über die
        ''' Objekt-Border. Die Effekt-Ränder sind in Bitmap-Pixeln bemessen, die Border in Display-Pixeln -
        ''' die View darf die Padding-Formel deshalb nicht nachbauen, sondern rechnet dieses Rechteck um
        ''' (siehe EditorView.ComputeSelectedOverlayImageMargin).
        Public NotInheritable Class AnnotationOverlayRender
            Public Property Image As Bitmap
            Public Property BitmapWidth As Double
            Public Property BitmapHeight As Double
            Public Property ObjectX As Double
            Public Property ObjectY As Double
            Public Property ObjectWidth As Double
            Public Property ObjectHeight As Double
        End Class

        ''' <summary>Ergebnis des Skia-Kerns: die Bitmap (Eigentum geht an den Aufrufer) und die Lage
        ''' des Objekts darin, in Bitmap-Pixeln. Grundlage fuer das Auswahl-Overlay (Avalonia-Fassung
        ''' unten) UND fuer den Objekt-Bitmap-Cache des Kompositor-Umbaus - beide muessen dieselbe
        ''' Zeichnung sehen, eine zweite Formel liefe auseinander.</summary>
        Public NotInheritable Class AnnotationOverlaySkRender
            Public Property Bitmap As SKBitmap
            Public Property ObjectX As Integer
            Public Property ObjectY As Integer
            Public Property ObjectWidth As Integer
            Public Property ObjectHeight As Integer
        End Class

        ''' <summary>Zeichnet das selektierte Objekt so, wie es im gebackenen Bild aussieht - Silhouette
        ''' mit Schatten und Glühen, darüber das Objekt selbst. Die View legt das Bitmap deckungsgleich
        ''' über die Objekt-Border (siehe AnnotationOverlayRender).</summary>
        Public Shared Function RenderAnnotationOverlay(annotation As ImageAnnotation, pixelWidth As Integer, pixelHeight As Integer) As AnnotationOverlayRender
            Dim sk = RenderAnnotationOverlaySk(annotation, pixelWidth, pixelHeight)
            If sk Is Nothing Then Return Nothing
            Using sk.Bitmap
                Return New AnnotationOverlayRender With {
                    .Image = ToAvaloniaBitmap(sk.Bitmap),
                    .BitmapWidth = sk.Bitmap.Width,
                    .BitmapHeight = sk.Bitmap.Height,
                    .ObjectX = sk.ObjectX,
                    .ObjectY = sk.ObjectY,
                    .ObjectWidth = sk.ObjectWidth,
                    .ObjectHeight = sk.ObjectHeight
                }
            End Using
        End Function

        ''' <summary>Der Skia-Kern: Objekt inkl. Effekten und eigenen Pixel-Anpassungen, Drehung bewusst
        ''' NICHT eingerechnet (die legt der Anzeiger als Transformation darueber), Lage nicht enthalten.
        ''' Genau die Eigenschaften, die den Objekt-Bitmap-Cache tragen.
        '''
        ''' <paramref name="maxRenderDimension"/>: Deckel der internen Aufloesung. Der GHOST nimmt die
        ''' 720 (er wird ohnehin auf die Bildschirmgroesse gestreckt und bei jedem Effektregler-Tick
        ''' neu gerendert); der Objekt-Bitmap-Cache rendert UNGEDECKELT in Szenenaufloesung - das
        ''' komponierte Bild muss pixelgleich zur gebackenen Qualitaet sein.</summary>
        Public Shared Function RenderAnnotationOverlaySk(annotation As ImageAnnotation, pixelWidth As Integer, pixelHeight As Integer,
                                                         Optional maxRenderDimension As Single = MaxOverlayRenderDim) As AnnotationOverlaySkRender
            If annotation Is Nothing Then Return Nothing

            Dim renderAnnotation = annotation.Clone()
            renderAnnotation.RotationDegrees = 0

            ' Interne Objektauflösung (gedeckelt, Seitenverhältnis erhalten). Die Ränder sind damit in
            ' Bitmap-Pixeln bemessen, nicht in Display-Pixeln; die View rechnet sie über das zurückgegebene
            ' Objekt-Rechteck um, statt die Formel nachzubauen.
            Dim requestedLongest = CSng(Math.Max(1, Math.Max(pixelWidth, pixelHeight)))
            Dim cap = If(maxRenderDimension > 0, maxRenderDimension, MaxOverlayRenderDim)
            Dim renderScale = If(requestedLongest > cap, cap / requestedLongest, 1.0F)
            Dim objW = Math.Max(1, CInt(Math.Round(Math.Max(1, pixelWidth) * renderScale)))
            Dim objH = Math.Max(1, CInt(Math.Round(Math.Max(1, pixelHeight) * renderScale)))

            ' Schriftgrad und Konturbreite stehen in Bildpixeln und müssen mit dem Objekt-Rechteck
            ' schrumpfen - am Klon selbst, nicht nur in lokalen Variablen: DrawAnnotationShape und
            ' DrawAnnotationEffects lesen für Text und Kontur direkt aus der Annotation weiter. Beim
            ' Backen erledigt ScaleAnnotationForSource dasselbe.
            renderAnnotation.FontSizePixels *= renderScale
            renderAnnotation.StrokeWidth *= renderScale

            Dim objSize = CSng(Math.Max(1, Math.Min(objW, objH)))
            ' Schattengröße >100% lässt den (um seine Mitte skalierten) Schatten übers Objekt hinauswachsen;
            ' der Wachstumsrand wird über die größere Objektkante bemessen, damit auf keiner Achse abgeschnitten wird.
            Dim shadowGrow = If(renderAnnotation.ShadowEnabled, Math.Max(objW, objH) * Math.Max(0.0F, Clamp(renderAnnotation.ShadowSizePercent, 10, 400) / 100.0F - 1.0F) * 0.5F, 0.0F)
            ' Faktor 1.7 deckt die Glow-Reichweite (Dilate + Blur, siehe DrawAnnotationEffects: ~1.5x objSize
            ' bei Maximalwert) mit etwas Reserve ab.
            Dim glowPad = If(renderAnnotation.GlowEnabled, objSize * Clamp(renderAnnotation.GlowBlur, 0, 100) / 100.0F * 1.7F, 0.0F)
            Dim shadowPad = If(renderAnnotation.ShadowEnabled, objSize * Clamp(renderAnnotation.ShadowBlur, 0, 100) / 100.0F * ShadowBlurSigmaFactor * 3.0F + shadowGrow, 0.0F)
            Dim offsetX = If(renderAnnotation.ShadowEnabled, objSize * renderAnnotation.ShadowOffsetXPercent / 100.0F, 0.0F)
            Dim offsetY = If(renderAnnotation.ShadowEnabled, objSize * renderAnnotation.ShadowOffsetYPercent / 100.0F, 0.0F)
            Dim effectPad = Math.Max(glowPad, shadowPad)
            If Not String.IsNullOrWhiteSpace(renderAnnotation.TextPathKind) Then
                effectPad = Math.Max(effectPad, renderAnnotation.FontSizePixels * ComputeTextPathFitRatio(renderAnnotation) * 1.2F)
            End If
            ' Auf ganze Pixel aufrunden, damit das Objekt verlustfrei im Bitmap-Raster liegt: die View
            ' skaliert genau dieses Rechteck auf die Border, jeder Bruchteil würde das Objekt verzerren.
            Dim leftPad = CInt(Math.Ceiling(4.0F + effectPad + Math.Max(0.0F, -offsetX)))
            Dim rightPad = CInt(Math.Ceiling(4.0F + effectPad + Math.Max(0.0F, offsetX)))
            Dim topPad = CInt(Math.Ceiling(4.0F + effectPad + Math.Max(0.0F, -offsetY)))
            Dim bottomPad = CInt(Math.Ceiling(4.0F + effectPad + Math.Max(0.0F, offsetY)))

            ' Das Bitmap um die Effekt-Ränder VERGRÖSSERN (nicht das Objekt hineinschrumpfen): so wird
            ' der Schatten/Glow nie an der Bitmap-Kante abgeschnitten - im Gegensatz zum gebackenen Bild,
            ' das ins ganze Foto ausbluten kann.
            ' Damit entfällt auch der frühere "Reset auf 2px"-Notausgang, der bei flachen/breiten Objekten
            ' (Effekt-Rand > Objektgröße) die Auswahl-Vorschau komplett von der gebackenen Ansicht abweichen ließ.
            Dim width = objW + leftPad + rightPad
            Dim height = objH + topPad + bottomPad

            Dim rect = SKRect.Create(leftPad, topPad, objW, objH)
            Dim kind = If(renderAnnotation.Kind, "Text").Trim().ToLowerInvariant()
            Dim x = rect.Left
            Dim y = rect.Top
            Dim maxWidth = rect.Width
            Dim fontSize = Math.Max(8.0F, renderAnnotation.FontSizePixels)
            Dim alphaFactor = Clamp(renderAnnotation.Opacity, 0, 100) / 100.0F
            Dim fill = ApplyAlpha(ParseColor(renderAnnotation.FillColor, SKColors.White), alphaFactor)
            Dim stroke = ApplyAlpha(ParseColor(renderAnnotation.StrokeColor, SKColors.Black), alphaFactor)
            Dim strokeWidth = Math.Max(1.0F, renderAnnotation.StrokeWidth)

            ' KEIN Using: das Bitmap gehoert ab hier dem Aufrufer (Overlay-Fassung oder Cache).
            Dim bitmap = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Using canvas = New SKCanvas(bitmap)
                canvas.Clear(SKColors.Transparent)
                ' Die DREHUNG legt die View über eine RenderTransform auf das Overlay (oben deshalb auf 0
                ' gesetzt) - die SPIEGELUNG aber nicht. Ohne sie zeigte das Overlay ein markiertes Objekt
                ' ungespiegelt an, und Spiegeln sah aus, als täte es gar nichts: das gebackene Bild, in
                ' dem die Spiegelung längst drin war, blendet das markierte Objekt ja aus.
                If renderAnnotation.FlipHorizontal OrElse renderAnnotation.FlipVertical Then
                    canvas.Translate(rect.MidX, rect.MidY)
                    canvas.Scale(If(renderAnnotation.FlipHorizontal, -1.0F, 1.0F),
                                 If(renderAnnotation.FlipVertical, -1.0F, 1.0F))
                    canvas.Translate(-rect.MidX, -rect.MidY)
                End If
                If renderAnnotation.ShadowEnabled OrElse renderAnnotation.GlowEnabled Then
                    DrawAnnotationEffects(canvas, kind, renderAnnotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor, width, height)
                End If
                DrawAnnotationShape(canvas, kind, renderAnnotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor)
            End Using

            If HasObjectAdjustments(renderAnnotation) Then
                Dim objectAdj = renderAnnotation.Adjustments.ExtractPixelAdjustments()
                objectAdj.SourceWidthPixels = width
                objectAdj.SourceHeightPixels = height
                Dim processed = ProcessBitmapBase(bitmap, objectAdj)
                If Not Object.ReferenceEquals(processed, bitmap) Then bitmap.Dispose()
                bitmap = processed
            End If

            Return New AnnotationOverlaySkRender With {
                .Bitmap = bitmap,
                .ObjectX = leftPad,
                .ObjectY = topPad,
                .ObjectWidth = objW,
                .ObjectHeight = objH
            }
        End Function

        ''' DrawWrappedText setzt die Grundlinie der ersten Zeile auf rect.Top + fontSize, die Glyphen-
        ''' Oberkante liegt also um (fontSize - Ascent) unter der Oberkante des Objekt-Rechtecks. Avalonia
        ''' setzt die Glyphen-Oberkante einer TextBox dagegen direkt auf deren Oberkante. Der Rückgabewert
        ''' ist der Versatz, um den die Live-TextBox nach unten geschoben werden muss, damit ihre Glyphen
        ''' dort landen, wo das gebackene Bild sie zeichnet.
        ''' <summary>Zeilenabstand des gebackenen Textes, aus den Metriken der Schrift statt aus einem
        ''' festen Faktor. Genau diesen Abstand benutzt auch Avalonia, wenn an der Live-Textbox kein
        ''' LineHeight gesetzt ist - beides liest dieselben hhea-Werte über SkiaSharp. Ein eigener Faktor
        ''' hier (früher 1.22) ließ mehrzeiligen Text im Editor enger stehen als im Ergebnis; ihn über
        ''' TextBox.LineHeight nachzuziehen verschob dafür die erste Zeile nach unten, weil Avalonia die
        ''' zusätzliche Durchschusshöhe über der Grundlinie verteilt.</summary>
        Private Shared Function GetLineHeight(metrics As SKFontMetrics) As Single
            Return -metrics.Ascent + metrics.Descent + metrics.Leading
        End Function

        ''' <summary>Zeilenabstand in Pixeln für einen Schriftschnitt - für die Größenschätzung des
        ''' Textrechtecks im EditorViewModel.</summary>
        Public Shared Function GetBakedTextLineHeight(fontFamily As String, fontSize As Single) As Double
            If fontSize <= 0 Then Return 0
            Using font = CreateFont(fontFamily, fontSize)
                Return GetLineHeight(font.Metrics)
            End Using
        End Function

        Public Shared Function GetBakedTextTopOffset(fontFamily As String, fontSize As Single) As Double
            If fontSize <= 0 Then Return 0
            Using font = CreateFont(fontFamily, fontSize)
                Return Math.Max(0.0F, fontSize + font.Metrics.Ascent)
            End Using
        End Function

        Public Shared Function TryGetSvgAspectRatio(iconPath As String) As Double
            If String.IsNullOrWhiteSpace(iconPath) Then Return 1.0

            Dim shape = GetShapePath(iconPath)
            If shape Is Nothing OrElse shape.Bounds.Width <= 0 OrElse shape.Bounds.Height <= 0 Then Return 1.0
            Return Math.Max(0.01, shape.Bounds.Width / shape.Bounds.Height)
        End Function

        Public Shared Function ApplyGeometryAdjustments(sourcePath As String, adj As ImageAdjustments) As Bitmap
            Using original = DecodeOriented(sourcePath)
                If original Is Nothing Then Return Nothing

                Dim processed As SKBitmap = CloneBitmap(original)
                processed = ReplaceBitmap(processed, ApplyCrop(processed, adj))
                processed = ReplaceBitmap(processed, ApplyGeometryTransforms(processed, adj))
                processed = ReplaceBitmap(processed, ApplyStraighten(processed, adj))
            processed = ReplaceBitmap(processed, ApplyPerspective(processed, adj))
                processed = ReplaceBitmap(processed, ApplyResize(processed, adj))
                processed = ReplaceBitmap(processed, ApplyCanvasResize(processed, adj))
                processed = ReplaceBitmap(processed, ApplyDocumentBackground(processed, adj))

                Using processedBitmap = processed
                    Return ToAvaloniaBitmap(processedBitmap)
                End Using
            End Using
        End Function

        Public Shared Function ApplyGeometryAdjustments(source As SKBitmap, adj As ImageAdjustments) As Bitmap
            Return ApplyGeometryAdjustments(source, adj, 0)
        End Function

        Public Shared Function ApplyGeometryAdjustments(source As SKBitmap, adj As ImageAdjustments, maxDimension As Integer) As Bitmap
            If source Is Nothing Then Return Nothing

            Dim workingSource = CreatePreviewWorkingBitmap(source, maxDimension)
            If workingSource Is Nothing Then Return Nothing

            Dim processed As SKBitmap = CloneBitmap(workingSource)
            If Not Object.ReferenceEquals(workingSource, source) Then workingSource.Dispose()
            processed = ReplaceBitmap(processed, ApplyCrop(processed, adj))
            processed = ReplaceBitmap(processed, ApplyGeometryTransforms(processed, adj))
            processed = ReplaceBitmap(processed, ApplyStraighten(processed, adj))
            processed = ReplaceBitmap(processed, ApplyPerspective(processed, adj))
            processed = ReplaceBitmap(processed, ApplyResize(processed, adj))
            processed = ReplaceBitmap(processed, ApplyCanvasResize(processed, adj))
            processed = ReplaceBitmap(processed, ApplyDocumentBackground(processed, adj))

            Using processedBitmap = processed
                Return ToAvaloniaBitmap(processedBitmap)
            End Using
        End Function

        ''' <summary>Geometrie-only-Render als SKBitmap (Vorher-Seite des Zoom-Details): dieselben
        ''' Schritte wie ApplyGeometryAdjustments, aber ohne Avalonia-Konvertierung - der Aufrufer
        ''' extrahiert daraus Viewport-Regionen. Der Aufrufer übernimmt den Besitz.</summary>
        Public Shared Function ApplyGeometryAdjustmentsSk(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing Then Return Nothing

            Dim processed As SKBitmap = CloneBitmap(source)
            processed = ReplaceBitmap(processed, ApplyCrop(processed, adj))
            processed = ReplaceBitmap(processed, ApplyGeometryTransforms(processed, adj))
            processed = ReplaceBitmap(processed, ApplyStraighten(processed, adj))
            processed = ReplaceBitmap(processed, ApplyPerspective(processed, adj))
            processed = ReplaceBitmap(processed, ApplyResize(processed, adj))
            processed = ReplaceBitmap(processed, ApplyCanvasResize(processed, adj))
            processed = ReplaceBitmap(processed, ApplyDocumentBackground(processed, adj))
            Return processed
        End Function

        Public Shared Function BuildHistogramImage(sourcePath As String, width As Integer, height As Integer) As Bitmap
            Try
                Using original = DecodeHistogramSource(sourcePath)
                    If original Is Nothing Then Return Nothing
                    Using histogram = RenderHistogram(original, width, height)
                        Return ToAvaloniaBitmap(histogram)
                    End Using
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Histogramme einer FPX beschreiben das gespeicherte Ergebnis und werden deshalb
        ''' direkt aus composite.png gebildet. Die normale Decode-Pipeline kann den FPX-ZIP-Container
        ''' selbst nicht dekodieren und lieferte hier bisher ein leeres Histogramm.</summary>
        Private Shared Function DecodeHistogramSource(sourcePath As String) As SKBitmap
            If FpxService.IsFpx(sourcePath) Then
                Using composite = FpxService.ExtractComposite(sourcePath)
                    If composite Is Nothing Then Return Nothing
                    Return SKBitmap.Decode(composite)
                End Using
            End If
            Return DecodeOriented(sourcePath)
        End Function

        Public Shared Function BuildHistogramImage(source As SKBitmap, width As Integer, height As Integer) As Bitmap
            If source Is Nothing Then Return Nothing
            Using histogram = RenderHistogram(source, width, height)
                Return ToAvaloniaBitmap(histogram)
            End Using
        End Function

        ''' <summary>Voll aufgelöster Decode für das ARBEITSBILD: öffentlicher
        ''' Zugang zum universellen Decode-Chokepoint (RAW/ICO-Sonderfälle + EXIF-Orientierung).
        ''' Der Aufrufer übernimmt den Besitz des Bitmaps.</summary>
        Public Shared Function DecodeWorkingImage(path As String,
                                                  Optional lensChoice As LensDataService.Wahl = Nothing) As SKBitmap
            Try
                Return DecodeOriented(path, True, lensChoice)
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>ORIENTIERTE Bildmaße (wie DecodeOriented sie liefert) ohne Voll-Decode - zum
        ''' Abgleich, ob ein retouch.png wirklich das voll aufgelöste Arbeitsbild ist. (0,0) wenn
        ''' nicht bestimmbar; der Aufrufer behandelt das als „Prüfung nicht möglich".</summary>
        Public Shared Function GetOrientedImageSize(path As String) As (Width As Integer, Height As Integer)
            Try
                ' RAW mit warmem Entwicklungs-Cache: dessen Maße sind die des echten Decodes.
                ' Kalter Cache -> weiter unten die eingebettete Vorschau (kein Demosaic nur für
                ' eine Größenabfrage; die Maße stimmen bei modernen Kameras überein).
                If RawPreviewService.IsSupportedRaw(path) AndAlso RawDecodeService.IsAvailable Then
                    Dim cached = RawDecodeService.TryGetCachedSize(path)
                    If cached.Width > 0 Then Return cached
                End If
                Dim data As SKData
                If FpxService.IsFpx(path) Then
                    ' Eine FPX ist ein ZIP-Container und kann nicht selbst an SKCodec gehen. Fuer
                    ' Viewer/Infopanel sind die gespeicherten Composite-Masse die richtige schnelle
                    ' Header-Antwort. Ohne diesen Sonderweg fiel der asynchrone Viewer auf seine
                    ' zuvor angezeigten Bildmasse zurueck.
                    Using stream = FpxService.ExtractComposite(path)
                        If stream Is Nothing Then Return (0, 0)
                        data = SKData.Create(stream)
                    End Using
                Else
                    Using stream = OpenSourceStream(path)
                        If stream Is Nothing Then Return (0, 0)
                        data = SKData.Create(stream)
                    End Using
                End If
                If data Is Nothing Then Return (0, 0)
                Using data
                    Using codec = SKCodec.Create(data)
                        If codec Is Nothing Then Return (0, 0)
                        Dim info = codec.Info
                        Select Case codec.EncodedOrigin
                            Case SKEncodedOrigin.LeftTop, SKEncodedOrigin.RightTop, SKEncodedOrigin.RightBottom, SKEncodedOrigin.LeftBottom
                                ' 90°/270°-Orientierungen tauschen Breite und Höhe.
                                Return (info.Height, info.Width)
                            Case Else
                                Return (info.Width, info.Height)
                        End Select
                    End Using
                End Using
            Catch
                Return (0, 0)
            End Try
        End Function

        Public Shared Function LoadPreviewSource(imagePath As String, maxDimension As Integer) As SKBitmap
            Try
                Using original = DecodeOriented(imagePath)
                    If original Is Nothing Then Return Nothing
                    Dim working = CreatePreviewWorkingBitmap(original, maxDimension)
                    If working Is Nothing Then Return Nothing
                    If Object.ReferenceEquals(working, original) Then Return CloneBitmap(original)
                    Return working
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        Public Shared Function CreatePreviewWorkingBitmap(source As SKBitmap, maxDimension As Integer) As SKBitmap
            If source Is Nothing Then Return Nothing

            Dim limit = If(maxDimension > 0, Math.Max(256, maxDimension), Integer.MaxValue)
            Dim longest = Math.Max(source.Width, source.Height)
            If longest <= limit Then Return source

            Dim scale = limit / CDbl(longest)
            Dim width = Math.Max(1, CInt(Math.Round(source.Width * scale)))
            Dim height = Math.Max(1, CInt(Math.Round(source.Height * scale)))
            Dim result = New SKBitmap(width, height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                Using paint = New SKPaint With {.IsAntialias = True}
                    DrawBitmapSampled(canvas, source, New SKRect(0, 0, source.Width, source.Height), New SKRect(0, 0, width, height), SamplingHigh, paint)
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Bildabmessungen aus den Kopfdaten, ohne vollstaendiges Dekodieren. (0,0), wenn
        ''' das Format unbekannt ist - Aufrufer MUESSEN das abfangen, statt mit 0 weiterzurechnen.</summary>
        Public Shared Function GetImageSize(imagePath As String) As (Width As Integer, Height As Integer)
            ' HEIC/HEIF/AVIF kennt SKCodec nicht - dort antwortet libheif aus dem Container.
            If HeifDecodeService.IsSupportedHeif(imagePath) AndAlso HeifDecodeService.IsAvailable Then
                Dim heifSize = HeifDecodeService.TryGetSize(imagePath)
                If heifSize.Width > 0 AndAlso heifSize.Height > 0 Then Return heifSize
            End If
            If TiffPreviewService.IsSupportedTiff(imagePath) Then
                Dim tiffSize = TiffPreviewService.TryGetSize(imagePath)
                If tiffSize.Width > 0 AndAlso tiffSize.Height > 0 Then Return tiffSize
            End If
            Try
                Using codec = SKCodec.Create(imagePath)
                    If codec IsNot Nothing Then
                        Return (codec.Info.Width, codec.Info.Height)
                    End If
                End Using
            Catch
            End Try
            Return (0, 0)
        End Function


        ''' <summary>Grundbreite von Fuß und Schulter der Tonwertkurve, in Anteilen des Tonwertumfangs.</summary>
        Private Const ToneShoulderBase As Single = 0.12F
        Private Const ToneShoulderMax As Single = 0.4F


        Private Shared Function ToneTransfer(x As Single, exposureGain As Single, contrast As Single, brightness As Single) As Single
            ' Belichtung in LINEARLICHT: frueher x*exposureGain im Gamma-Raum - das
            ' staucht die Lichter brutal (gemessen: +50 zog 64->218). Belichtung ist physikalisch ein
            ' linearer Faktor: sRGB dekodieren, multiplizieren, wieder kodieren. Ergebnis darf >1 sein
            ' (Ueberstrahlung), die weiche Schulter (SoftShoulder/rolloff) faengt es danach ab.
            ' Kontrast/Helligkeit bleiben bewusst im Gamma-Raum um 0.5 - dort ist ihre Schulter definiert.
            Dim y As Single
            If exposureGain = 1.0F Then
                y = x
            Else
                y = LinearToSrgb(SrgbToLinear(x) * exposureGain)
            End If
            y = (y - 0.5F) * contrast + 0.5F
            Return y + brightness
        End Function

        ''' <summary>sRGB-Gamma -> Linearlicht (Standard-sRGB-EOTF). Nur fuer die Belichtung; der Rest
        ''' der Kette rechnet weiter im Gamma-Raum.</summary>
        Private Shared Function SrgbToLinear(c As Single) As Single
            If c <= 0.0F Then Return 0.0F
            If c <= 0.04045F Then Return c / 12.92F
            Return CSng(Math.Pow((c + 0.055) / 1.055, 2.4))
        End Function

        ''' <summary>Linearlicht -> sRGB-Gamma. Werte >1 bleiben >1 (Ueberstrahlung), damit die
        ''' Schulter-Logik sie wie bisher abrollen kann.</summary>
        Private Shared Function LinearToSrgb(c As Single) As Single
            If c <= 0.0F Then Return 0.0F
            If c <= 0.0031308F Then Return c * 12.92F
            Return CSng(1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055)
        End Function

        ''' Identisch im mittleren Bereich, exponentiell auslaufend zu Schwarz und Weiß - erreicht die
        ''' Enden nie ganz, sodass in den Lichtern und Tiefen Zeichnung bleibt statt einer Fläche.
        Private Shared Function SoftShoulder(y As Single, toe As Single, shoulder As Single) As Single
            Dim knee = 1.0F - shoulder
            If y > knee Then Return CSng(1.0 - shoulder * Math.Exp(-(y - knee) / shoulder))
            If y < toe Then Return CSng(toe * Math.Exp((y - toe) / toe))
            Return y
        End Function

        Private Shared Function ProcessBitmap(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim processed = ProcessBitmapBase(source, adj)
            Return ReplaceBitmap(processed, ApplyAnnotations(processed, WithAnnotationSourceSpace(adj, source)))
        End Function

        ''' <summary>Stapel- und Export-Aufrufer bauen ihre Anpassungen frisch zusammen und kennen die
        ''' Maße der Quelle nicht - SourceWidthPixels bleibt dort 0. Ohne Bezugsgröße rechnet
        ''' TransformAnnotationForGeometry die Objekte gar nicht um: ein Wasserzeichen behielt seine
        ''' Pixelmaße, obwohl das Bild verkleinert wurde, und wirkte auf dem kleinen Bild riesig.
        ''' Bezug ist hier das dekodierte Bild selbst - genau der Raum, in dem die Pipeline gleich
        ''' arbeitet; eine aus der Datei geschätzte Größe läge bei RAW/PSD/.fpx daneben.</summary>
        Private Shared Function WithAnnotationSourceSpace(adj As ImageAdjustments, source As SKBitmap) As ImageAdjustments
            If adj Is Nothing OrElse source Is Nothing Then Return adj
            If adj.SourceWidthPixels > 0 AndAlso adj.SourceHeightPixels > 0 Then Return adj
            If adj.Annotations Is Nothing OrElse adj.Annotations.Count = 0 Then Return adj

            Dim ergaenzt = adj.Clone()
            ergaenzt.SourceWidthPixels = source.Width
            ergaenzt.SourceHeightPixels = source.Height
            Return ergaenzt
        End Function

        ' Alle Pipeline-Schritte AUSSER dem Einzeichnen der Objekte (Annotations). Wird von
        ' ProcessBitmap sowie vom Basis-Cache in ApplyAdjustments(source As SKBitmap, ...) genutzt.
        ' ARBEITSBILD (Stufe E): Retusche ist KEIN Pipeline-Schritt mehr - sie steckt bereits im
        ' Eingangsbild (Arbeitsbild); die Pipeline beginnt direkt mit der Geometrie.
        ''' <summary>Schaltet zwischen der alten Stufenkette und der verschmolzenen
        ''' Gleitkomma-Kette um. Waehrend der Umstellung laufen beide nebeneinander, damit
        ''' der Aequivalenztest der Diagnose sie vergleichen kann.</summary>
        Private Shared Function ProcessBitmapBase(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            ' Copy-on-write: die Kette startet auf der FREMDEN Quelle und kopiert erst, wenn eine Stufe
            ' wirklich ein neues Bild liefert. Siehe ReplaceBitmapOwned.
            Dim processed As SKBitmap = source
            Dim owned = False

            processed = ReplaceBitmapOwned(processed, ApplyCrop(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyGeometryTransforms(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyStraighten(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyPerspective(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyResize(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyCanvasResize(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyDocumentBackground(processed, adj), owned)

            ' Eine Auswahl wird im bereits gerenderten Display-Raum angelegt. Deshalb muss auch der
            ' unveraenderte Vergleichsstand fuer selektive Farb-/Detailanpassungen NACH der Geometrie
            ' aufgenommen werden. `source` ist hier noch SourceSpace und passt nach Rotation/Flip weder
            ' in den Abmessungen noch in der Pixelanordnung zur sichtbaren Auswahl.
            If adj IsNot Nothing AndAlso Not adj.GlobalAdjustmentsHidden Then
                Dim selectionBaseline As SKBitmap = Nothing
                If SelectionScopeIsEnabled(adj) Then selectionBaseline = CloneBitmap(processed)

                processed = ApplyPixelAdjustmentStagesCore(processed, adj, owned)

                ' Auswahl-Skopus: Anpassungen nur INNERHALB der aktiven Auswahl wirken lassen. Maske,
                ' Vergleichsstand und angepasstes Bild liegen hier gemeinsam im gerenderten Display-Raum.
                If selectionBaseline IsNot Nothing Then
                    Dim scopeMask = BuildSelectionScopeMask(adj, processed.Width, processed.Height)
                    If scopeMask IsNot Nothing Then
                        Using scopeMask
                            processed = ReplaceBitmapOwned(processed, CompositeSelectionScoped(selectionBaseline, processed, scopeMask), owned)
                        End Using
                    Else
                        ' Eine aktive, aber unlesbare/leer gewordene Maske darf niemals still auf eine
                        ' globale Anpassung zurueckfallen. Im Fehlerfall bleibt das Bild unveraendert.
                        processed = ReplaceBitmapOwned(processed, CloneBitmap(selectionBaseline), owned)
                    End If
                    selectionBaseline.Dispose()
                End If
            End If

            processed = ApplyMaskedAdjustmentLayersCore(processed, adj, source.Width, source.Height, Nothing, owned)
            Return TakeOwnership(processed, owned)
        End Function

        ''' <summary>Die wiederverwendbare Pixelkette ohne Geometrie, Objekte und Auswahl-Compositing.
        ''' Sie dient sowohl der globalen Bearbeitung als auch jeder lokalen Einstellungsebene.</summary>
        Private Shared Function ApplyPixelAdjustmentStages(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim owned = False
            Return TakeOwnership(ApplyPixelAdjustmentStagesCore(source, adj, owned), owned)
        End Function

        ''' <summary>Der Kern der Pixelkette. <paramref name="owned"/> wandert durch: beim Eintritt
        ''' sagt es, ob <paramref name="source"/> uns gehört, beim Austritt, ob das Ergebnis uns
        ''' gehört. Nur so kann die Kette ohne Eingangskopie beginnen - siehe
        ''' <see cref="ReplaceBitmapOwned"/>.</summary>
        Private Shared Function ApplyPixelAdjustmentStagesCore(source As SKBitmap, adj As ImageAdjustments,
                                                               ByRef owned As Boolean) As SKBitmap
            Dim processed = source

            ' Alle Farb-Punktoperationen laufen in EINER verschmolzenen Gleitkomma-Stufe
            ' (ImageProcessorPointOps.vb): Filmnegativ, Farbmatrix, Tonwertkurve, Lichter/Tiefen/
            ' Weiß/Schwarz, RGB- und Kanalkurven, Luminanzkurve, HSL-Bänder, Split-Toning,
            ' Preset-Matrix und Cube-LUT. Vorher waren das acht aufeinanderfolgende Stufen mit je
            ' einem eigenen 8-Bit-Zwischenbild - die gestapelten Rundungen waren die Streifenbildung
            ' in Himmel und Hauttönen. Jetzt wird EINMAL am Ende quantisiert (mit Dither).
            ' Die Umkehr steckt dabei ganz vorn in der Kette: Belichtung, Weißabgleich, Kurven und
            ' Filter sollen auf dem fertigen Positiv arbeiten - auf dem Negativ wären sie
            ' seitenverkehrt (Aufhellen würde abdunkeln).
            processed = ReplaceBitmapOwned(processed, ApplyPointOpChain(processed, adj), owned)

            ' "weich" steht im selben Select Case wie die 15 Farbpresets, ist aber als einziges KEINE
            ' Punktoperation, sondern eine echte räumliche Unschärfe. BuildFilterPresetMatrix liefert
            ' dafür bewusst Nothing; die Stufe läuft hier getrennt.
            If String.Equals(If(adj.FilterPreset, "").Trim(), "weich", StringComparison.OrdinalIgnoreCase) Then
                processed = ReplaceBitmapOwned(processed, ApplySoftFocusBlur(processed, Clamp(adj.FilterStrength / 100.0F, 0, 1)), owned)
            End If

            If adj.Clarity <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyClarity(processed, adj.Clarity / 100.0F), owned)
            End If
            If adj.[Structure] <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyStructure(processed, adj.[Structure] / 100.0F), owned)
            End If
            If adj.Haze <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyHaze(processed, adj.Haze / 100.0F), owned)
            End If

            If adj.NoiseReduction > 0 Then
                If adj.NoiseReductionMethod = NoiseReductionMethod.Median Then
                    processed = ReplaceBitmapOwned(processed, ApplyMedianBlur(processed, adj.NoiseReduction / 100.0F), owned)
                Else
                    processed = ReplaceBitmapOwned(processed, ApplyNoiseReduction(processed, adj.NoiseReduction / 100.0F, adj.NoiseReductionDetail / 100.0F), owned)
                End If
            End If
            ' Die beiden Seiten desselben Panel-Reglers: Minus glaettet die Farbanteile, Plus faerbt
            ' sie ein. Getrennte Felder, weil nur die Reduzierung eine Entsprechung in den Presets hat
            ' (crs:ColorNoiseReduction) - siehe ImageAdjustments.ColorNoiseAdd.
            If adj.ColorNoiseReduction > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyColorNoiseReduction(processed, adj.ColorNoiseReduction / 100.0F), owned)
            End If
            ' NACH der einstufigen Glaettung: die nimmt das feine Farbkorn, diese Stufe die groben
            ' Flecken. Umgekehrt muesste die grobe Stufe erst durch das feine Korn hindurch.
            If adj.FarbrauschGrob > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyMultiScaleDenoise(
                    processed, adj.FarbrauschGrob / 100.0F, adj.ColorNoiseCoarseScale / 100.0F), owned)
            End If
            If adj.ColorNoiseAdd > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyColorNoiseAdd(processed, adj.ColorNoiseAdd / 100.0F), owned)
            End If
            If adj.DustScratches <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyDustScratches(processed, adj.DustScratches / 100.0F), owned)
            End If
            If adj.Glow <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyImageGlow(processed, adj.Glow / 100.0F), owned)
            End If

            If adj.Sharpness > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplySharpness(processed, adj.Sharpness / 100.0F, adj.SharpenRadius / 100.0F, adj.SharpenDetail / 100.0F, adj.SharpenMasking / 100.0F), owned)
            End If

            If adj.Vignette <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyVignette(processed, adj.Vignette / 100.0F, adj.VignetteTransition, adj.VignetteRoundness, adj.VignetteFeather, adj.VignetteCenterX, adj.VignetteCenterY, adj.VignetteStyle), owned)
            End If

            If adj.Grain > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyGrain(processed, adj.Grain / 100.0F, adj.GrainSize / 100.0F, adj.GrainFrequency / 100.0F), owned)
            End If
            If adj.AddNoise > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyAddNoise(processed, adj.AddNoise / 100.0F), owned)
            ElseIf adj.AddNoise < 0 Then
                ' Negative Haelfte = Rauschen REDUZIEREN (gleichmaessiges Weichzeichnen wie der
                ' Gaussian-Modus der Rauschreduzierung) - ein Regler, beide Richtungen.
                processed = ReplaceBitmapOwned(processed, ApplyNoiseReduction(processed, -adj.AddNoise / 100.0F), owned)
            End If

            ' Der Rahmen war bis hierher die letzte Stufe der Pixelkette. Er ist jetzt ein OBJEKT
            ' und wird mit den anderen Objekten gezeichnet - deshalb steht hier nichts mehr.

            Return processed
        End Function


        ''' <summary>Mehrskaliges Entrauschen der FARBE: nimmt grobfleckiges Farbrauschen weg, ohne
        ''' weichzuzeichnen.
        '''
        ''' Der Unterschied zu allem, was wir sonst haben, ist die SKALA. Ein Weichzeichner - egal ob
        ''' Gauss, Median oder kantenerhaltend - arbeitet auf einer Groesse. Grobfleckiges Rauschen
        ''' aus hochgezogenen Aufnahmen ist aber zehn bis zwanzig Bildpunkte gross; um daran zu
        ''' kommen, braucht so ein Filter einen Radius, der alles andere gleich mitfrisst. Genau
        ''' deshalb sah unser Farbrausch-Regler bei solchen Bildern schwach aus - er arbeitet auf der
        ''' falschen Skala.
        '''
        ''' Hier wird das Bild stattdessen in mehrere Groessenstufen ZERLEGT. Ein 20-Punkte-Fleck
        ''' liegt auf einer groben Stufe und ist DORT ein kleiner Ausschlag - ihn wegzunehmen kostet
        ''' fast nichts. Die Kanten des Motivs sind auf jeder Stufe grosse Ausschlaege und bleiben.
        '''
        ''' Geschrumpft wird mit einer Kennlinie, die grosse Ausschlaege fast unangetastet laesst
        ''' (y = x - T hoch 2 / x oberhalb der Schwelle, null darunter). Ein glattes Abziehen der
        ''' Schwelle von allem wuerde auch die Kanten um denselben Betrag abschwaechen - das ist der
        ''' Unterschied zwischen "entrauscht" und "matt".
        '''
        ''' Angefasst werden NUR die Farbkanaele. Das Auge loest Farbdetails ohnehin kaum auf, und
        ''' die Struktur eines Bildes steckt in der Helligkeit - die bleibt hier unberuehrt. Fuer
        ''' Helligkeitsrauschen gibt es die beiden bestehenden Regler.</summary>
        ''' <param name="strength">0 bis 1.</param>
        ''' <param name="coarse">0 bis 1: wie grosse Flecken noch erfasst werden.</param>
        Public Shared Function ApplyMultiScaleDenoise(source As SKBitmap, strength As Single,
                                                          coarse As Single) As SKBitmap
            If source Is Nothing OrElse strength <= 0.001F Then Return CloneBitmap(source)
            Dim w = source.Width, h = source.Height
            If w < 8 OrElse h < 8 Then Return CloneBitmap(source)

            ' Wie viele Stufen. Jede verdoppelt die erfasste Fleckengroesse; sechs Stufen reichen bis
            ' etwa 64 Bildpunkte, und groeber als das ist kein Rauschen mehr, sondern Motiv.
            Dim steps = Math.Max(3, Math.Min(7, CInt(Math.Round(3.0 + coarse * 4.0))))
            Dim n = w * h

            ' Gelesen wird DIREKT aus der Quelle. Der frühere Arbeitsklon war eine reine Vollkopie,
            ' aus der nur gelesen wurde - bei 45 MP allein rund 145 ms.
            Dim srcBuffer As Byte() = Nothing
            Dim srcStride, ri, gi, bi, ai As Integer
            Dim hasBuffer = TryBorrowRgbaLikeBuffer(source, srcBuffer, srcStride, ri, gi, bi, ai)
            Dim target = New SKBitmap(w, h, source.ColorType, source.AlphaType)

            ' ZWEI ANNAHMEN, die der Pufferweg still voraussetzte - jetzt geprueft, statt geglaubt.
            '
            ' 1. GLEICHE ZEILENLAENGE. Geschrieben wird in einen Puffer in der Groesse der QUELLE,
            '    indiziert mit deren Stride, und am Stueck in das FRISCH angelegte Ziel kopiert. Wäre
            '    die Quelle gepolstert, liefe die Kopie ueber das Ziel hinaus. Fuer alles, was Skia
            '    hier selbst anlegt, sind beide gleich - aber die Invariante steht nirgends.
            '
            ' 2. PREMULTIPLIZIERT. Ent- und Rueckrechnung bilden nach, was GetPixel/SetPixel auf
            '    einem Premul-Bitmap tun. Auf einem UNPREMUL-Bitmap tun die beiden gar nichts, und
            '    der Pufferweg lieferte an teiltransparenten Stellen ein anderes Bild als der
            '    Rueckfallzweig darunter. Bei Opaque ist Alpha 255 und beide Rechnungen sind die
            '    Identitaet, das ist also unbedenklich.
            '
            ' Trifft eine der beiden nicht zu, geht es ueber GetPixel/SetPixel weiter - langsam, aber
            ' richtig. Das ist derselbe Rueckfall, den exotische Farbtypen ohnehin nehmen.
            If hasBuffer AndAlso (srcStride <> target.RowBytes OrElse
                                  source.AlphaType = SKAlphaType.Unpremul) Then
                hasBuffer = False
                srcBuffer = Nothing
            End If

            Dim work As SKBitmap = If(hasBuffer, Nothing, CloneBitmap(source))
            Try
                ' In Helligkeit und zwei Farbdifferenzen zerlegen. Nicht wegen der Norm, sondern weil
                ' sich nur so unterschiedlich hart schrumpfen laesst.
                Dim y(n - 1) As Single, cb(n - 1) As Single, cr(n - 1) As Single
                Dim alpha(n - 1) As Byte
                If hasBuffer Then
                    ' Über den Byte-Puffer statt über GetPixel: derselbe Zeilenblock kostete mit
                    ' GetPixel/SetPixel gemessen rund 4 s je Vorschaubild, über den Puffer und
                    ' zeilenparallel rund 7 ms. Entpremultipliziert wird mit derselben Tabelle, die
                    ' GetPixel benutzt, damit das Bild an weichen Kanten bitgleich bleibt.
                    ForEachRow(w, h,
                        Sub(row)
                            Dim ro = row * srcStride
                            Dim rk = row * w
                            For i = 0 To w - 1
                                Dim o = ro + i * 4
                                Dim k = rk + i
                                Dim a = srcBuffer(o + ai)
                                Dim pr = UnpremultiplyByte(srcBuffer(o + ri), a)
                                Dim pg = UnpremultiplyByte(srcBuffer(o + gi), a)
                                Dim pb = UnpremultiplyByte(srcBuffer(o + bi), a)
                                y(k) = 0.299F * pr + 0.587F * pg + 0.114F * pb
                                cb(k) = pb - y(k)
                                cr(k) = pr - y(k)
                                alpha(k) = a
                            Next
                        End Sub)
                Else
                    ' Rückfall für exotische Farbtypen, die der Puffer nicht abdeckt.
                    For j = 0 To h - 1
                        For i = 0 To w - 1
                            Dim p = work.GetPixel(i, j)
                            Dim k = j * w + i
                            y(k) = 0.299F * p.Red + 0.587F * p.Green + 0.114F * p.Blue
                            cb(k) = p.Blue - y(k)
                            cr(k) = p.Red - y(k)
                            alpha(k) = p.Alpha
                        Next
                    Next
                End If

                ' Die Schwellen. Die Farbkanaele bekommen ein Vielfaches - dort darf es haerter zur
                ' Sache gehen, ohne dass man es sieht.
                ' NUR die Farbkanaele. Die Helligkeit bleibt unberuehrt, und das ist eine bewusste
                ' Grenze: eine Schrumpfung ueber viele Stufen nimmt auch die Auslaeufer einer Kante
                ' mit, und die tragen deren Kontrast. Gemessen fiel eine harte Kante dadurch von 120
                ' auf 43 Stufen - am sauberen Bild genauso wie am verrauschten, es lag also nicht am
                ' Rauschen, sondern am Verfahren. Fuer die Helligkeit gibt es die beiden bestehenden
                ' Regler; hier geht es um das grobfleckige FARBrauschen, an das die nicht herankommen.
                ' Kennlinie, gemessen an Flecken der Hoehe plus/minus 26. Vorher lief die Schwelle
                ' linear von 4 auf 38, und der Regler war damit bei Stellung 25 fertig: von 25 bis
                ' 100 aenderte sich NICHTS mehr, und fast 60 Prozent der Wirkung lagen schon im
                ' ersten Schritt. Drei Viertel des Weges waren wirkungslos.
                '
                ' Der Grund ist das Verfahren, nicht die Zahl: geschrumpft wird auf mehreren Stufen
                ' nacheinander, die Ausschlaege je Stufe sind viel kleiner als der Fleck im Bild.
                ' Der nutzbare Schwellenbereich liegt deshalb bei etwa 0 bis 13 und nicht bei 38.
                '
                ' Jetzt 1 bis 20 mit einem Exponenten von 1,5: der untere Bereich wird gedehnt, wo
                ' die Wirkung sitzt, und ueber der Saettigung bleibt Luft fuer starkes Rauschen.
                ' Die Kennlinie steht als Info-Zeile im Pruefstand - sie zeigt sofort, wenn eine
                ' Aenderung den Bereich wieder zusammenschiebt.
                Dim thresholdC = 1.0F + 19.0F * CSng(Math.Pow(strength, 1.5))
                ShrinkSteps(cb, w, h, steps, thresholdC, 1.0F)
                ShrinkSteps(cr, w, h, steps, thresholdC, 1.0F)

                If hasBuffer Then
                    Dim dstBuffer = New Byte(srcBuffer.Length - 1) {}
                    ForEachRow(w, h,
                        Sub(row)
                            Dim ro = row * srcStride
                            Dim rk = row * w
                            For i = 0 To w - 1
                                Dim o = ro + i * 4
                                Dim k = rk + i
                                Dim r = y(k) + cr(k)
                                Dim b = y(k) + cb(k)
                                Dim g = (y(k) - 0.299F * r - 0.114F * b) / 0.587F
                                Dim a = alpha(k)
                                ' Premultiplizieren wie SetPixel, sonst stimmen weiche Kanten nicht.
                                dstBuffer(o + ri) = PremultiplyByte(KlemmeByte(r), a)
                                dstBuffer(o + gi) = PremultiplyByte(KlemmeByte(g), a)
                                dstBuffer(o + bi) = PremultiplyByte(KlemmeByte(b), a)
                                dstBuffer(o + ai) = a
                            Next
                        End Sub)
                    Marshal.Copy(dstBuffer, 0, target.GetPixels(), dstBuffer.Length)
                Else
                    For j = 0 To h - 1
                        For i = 0 To w - 1
                            Dim k = j * w + i
                            Dim r = y(k) + cr(k)
                            Dim b = y(k) + cb(k)
                            Dim g = (y(k) - 0.299F * r - 0.114F * b) / 0.587F
                            target.SetPixel(i, j, New SKColor(KlemmeByte(r), KlemmeByte(g), KlemmeByte(b), alpha(k)))
                        Next
                    Next
                End If
                Return target
            Catch
                target.Dispose()
                Return CloneBitmap(source)
            Finally
                If work IsNot Nothing Then work.Dispose()
            End Try
        End Function

        ''' <summary>Zerlegt einen Kanal in Groessenstufen, schrumpft jede und setzt wieder zusammen.
        ''' Der Kanal wird an Ort und Stelle geaendert.
        '''
        ''' <paramref name="growth"/> sagt, wie die Schwelle von Stufe zu Stufe waechst. Groeber
        ''' heisst mehr Flaeche und damit weniger Zufall - dort steht ein Ausschlag eher fuer etwas
        ''' Echtes und die Schwelle darf mitwachsen, sonst frisst man auf den groben Stufen den
        ''' Farbverlauf des Motivs mit.</summary>
        Private Shared Sub ShrinkSteps(channel As Single(), w As Integer, h As Integer,
                                           steps As Integer, threshold As Single, growth As Single)
            Dim n = w * h
            Dim baseline(n - 1) As Single
            Array.Copy(channel, baseline, n)
            Dim result(n - 1) As Single
            Dim smoothed(n - 1) As Single
            Dim s = threshold

            ' "Step" ist in VB ein Schlüsselwort (For ... Step) und taugt nicht als Name.
            For level = 1 To steps
                Dim radius = CInt(Math.Pow(2, level - 1))
                Array.Copy(baseline, smoothed, n)
                BoxBlur(smoothed, w, h, radius)
                ' Was die Glaettung wegnimmt, ist der Anteil DIESER Stufe.
                ' Elementweise und damit zeilenunabhängig - siehe Hinweis in BoxBlur.
                Dim sStep = s
                ForEachRow(w, h,
                    Sub(j)
                        Dim row = j * w
                        For i = 0 To w - 1
                            Dim k = row + i
                            result(k) += Shrink(baseline(k) - smoothed(k), sStep)
                        Next
                    End Sub)
                Array.Copy(smoothed, baseline, n)
                s *= growth
            Next

            ' Der Rest ist der grobe Bildaufbau - der bleibt unangetastet.
            ForEachRow(w, h,
                Sub(j)
                    Dim row = j * w
                    For i = 0 To w - 1
                        Dim k = row + i
                        channel(k) = result(k) + baseline(k)
                    Next
                End Sub)
        End Sub

        ''' <summary>Die Schrumpfkennlinie. Unterhalb der Schwelle null, darueber wird nur ein mit
        ''' dem Betrag FALLENDER Anteil abgezogen - ein grosser Ausschlag bleibt damit praktisch
        ''' erhalten. Ein glattes Abziehen der Schwelle wuerde jede Kante um denselben Betrag
        ''' abschwaechen.</summary>
        Private Shared Function Shrink(x As Single, schwelle As Single) As Single
            Dim a = Math.Abs(x)
            If a <= schwelle Then Return 0.0F
            ' Deutlich ueber der Schwelle: UNANGETASTET lassen. Die weiche Kennlinie zieht auch
            ' grossen Ausschlaegen noch etwas ab, und ueber fuenf Stufen summiert sich das - eine
            ' harte Kante fiel so von 120 auf 41, also mehr als um die Haelfte. Was so weit ueber
            ' der Schwelle liegt, ist kein Rauschen mehr, sondern das Motiv.
            ' Der Uebergangsbereich ist BEWUSST schmal. Er wird ueber alle Stufen aufsummiert: an
            ' einer harten Kante liegt der Ausschlag auf den mittleren Stufen genau in diesem Band,
            ' und ein breiter Bereich hat die Kante von 120 auf 43 Stufen fallen lassen - am
            ' sauberen Bild genauso wie am verrauschten. Das war kein Entrauschen mehr.
            If a >= schwelle * 1.5F Then Return x
            Dim soft = CSng(x - schwelle * schwelle / x)
            Dim t = (a - schwelle) / (schwelle * 0.5F)
            Return soft * (1.0F - t) + x * t
        End Function

        ''' <summary>Kastenunschaerfe, zweimal getrennt - waagerecht, dann senkrecht. Ueber laufende
        ''' Summen, damit die Kosten NICHT mit dem Radius wachsen: auf den groben Stufen ist der
        ''' Radius 32, und eine gewoehnliche Faltung waere dort unbezahlbar.</summary>
        Private Shared Sub BoxBlur(field As Single(), w As Integer, h As Integer, radius As Integer)
            If radius < 1 Then Return
            Dim intermediate(w * h - 1) As Single
            Dim window = radius * 2 + 1

            ' Beide Durchgänge laufen zeilen- bzw. spaltenWEISE und jeder schreibt nur in seine eigene
            ' Zeile bzw. Spalte - die laufende Summe ist je Durchlauf lokal. Damit ist das Ergebnis
            ' unabhängig von der Thread-Aufteilung bitgleich zum seriellen Lauf.
            ForEachRow(w, h,
                Sub(j)
                    Dim row = j * w
                    Dim sum As Single = 0
                    For i = -radius To radius
                        sum += field(row + Math.Max(0, Math.Min(w - 1, i)))
                    Next
                    For i = 0 To w - 1
                        intermediate(row + i) = sum / window
                        Dim leaving = Math.Max(0, Math.Min(w - 1, i - radius))
                        Dim entering = Math.Max(0, Math.Min(w - 1, i + radius + 1))
                        sum += field(row + entering) - field(row + leaving)
                    Next
                End Sub)

            ' Der senkrechte Durchgang läuft über SPALTEN. ForEachRow iteriert über den zweiten
            ' Parameter, deshalb stehen h und w hier vertauscht - die Lastschwelle bleibt dieselbe.
            ForEachRow(h, w,
                Sub(i)
                    Dim sum As Single = 0
                    For j = -radius To radius
                        sum += intermediate(Math.Max(0, Math.Min(h - 1, j)) * w + i)
                    Next
                    For j = 0 To h - 1
                        field(j * w + i) = sum / window
                        Dim leaving = Math.Max(0, Math.Min(h - 1, j - radius))
                        Dim entering = Math.Max(0, Math.Min(h - 1, j + radius + 1))
                        sum += intermediate(entering * w + i) - intermediate(leaving * w + i)
                    Next
                End Sub)
        End Sub

        Private Shared Function KlemmeByte(v As Single) As Byte
            If Single.IsNaN(v) Then Return 0
            Return CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(v)))))
        End Function

        ''' <summary>Wendet lokale Korrekturen in Ebenenreihenfolge an. Eine fehlende oder beschädigte
        ''' Maske bewirkt absichtlich gar nichts; sie darf nie zu einer globalen Korrektur werden.</summary>
        ''' <param name="onlyStackedAboveId">Nothing = nur die Korrekturen des BASISBILDS (ohne
        ''' Einsortierung in den Objektstapel). Sonst genau die Korrekturen, die über dem Objekt mit
        ''' dieser Id liegen - sie wirken damit auf Basis UND die bereits gezeichneten Objekte.</param>
        Private Shared Function ApplyMaskedAdjustmentLayers(source As SKBitmap, adj As ImageAdjustments,
                                                             pipelineInputWidth As Integer,
                                                             pipelineInputHeight As Integer,
                                                             Optional onlyStackedAboveId As String = Nothing) As SKBitmap
            Dim owned = False
            Return TakeOwnership(ApplyMaskedAdjustmentLayersCore(source, adj, pipelineInputWidth,
                                                                 pipelineInputHeight, onlyStackedAboveId, owned), owned)
        End Function

        ''' <summary>Der Kern der Korrekturebenen. <paramref name="owned"/> wandert wie in
        ''' <see cref="ApplyPixelAdjustmentStagesCore"/> durch: ohne sichtbare Korrektur - der Regelfall -
        ''' fällt hier gar keine Kopie mehr an.</summary>
        Private Shared Function ApplyMaskedAdjustmentLayersCore(source As SKBitmap, adj As ImageAdjustments,
                                                                pipelineInputWidth As Integer,
                                                                pipelineInputHeight As Integer,
                                                                onlyStackedAboveId As String,
                                                                ByRef owned As Boolean) As SKBitmap
            Dim processed = source
            If adj Is Nothing OrElse adj.MaskedAdjustmentLayers Is Nothing OrElse adj.Masks Is Nothing Then Return processed

            Dim masksById = adj.Masks.Where(Function(m) m IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(m.Id)).
                                     GroupBy(Function(m) m.Id, StringComparer.Ordinal).
                                     ToDictionary(Function(g) g.Key, Function(g) g.First(), StringComparer.Ordinal)
            ' Ebenen mit DERSELBEN Anpassung wirken GEMEINSAM, nicht nacheinander. Sonst trifft die
            ' Anpassung jeden Pixel, den zwei Masken gemeinsam abdecken, zweimal - wer eine Gruppe
            ' oder eine Mehrfachauswahl von Maskenebenen anpasst, bekommt in der Ueberschneidung die
            ' doppelte Wirkung. Zusammengefasst wird ueber den Fingerabdruck der Pixelwerte: das ist
            ' genau das, was eine gemeinsame Anpassung hinterlaesst, und es bleibt nach dem Abwaehlen
            ' und nach dem Neuladen gleich (eine Mehrfachauswahl selbst ist fluechtig und stuende
            ' beim naechsten Oeffnen nicht mehr zur Verfuegung).
            ' Bei DISJUNKTEN Masken aendert das Zusammenfassen nichts - jeder Pixel wird ohnehin nur
            ' einmal getroffen -, es greift also genau dort, wo sich etwas ueberschneidet.
            ' Zusammengefasst wird nur INNERHALB EINER GRUPPE. Gleiche Werte allein reichen NICHT:
            ' mehrere Korrekturen duerfen dieselbe Maske mit verschiedenen Deckkraeften tragen und
            ' sollen sich dann ausdruecklich aufaddieren (eigene Pruefung). Die Gruppe ist die
            ' ausdrueckliche Aussage "das gehoert zusammen" - und sie ueberlebt das Abwaehlen und
            ' das Neuladen, anders als eine Mehrfachauswahl.
            Dim gemeinsam = adj.MaskedAdjustmentLayers.
                Where(Function(l) l IsNot Nothing AndAlso Not String.IsNullOrEmpty(l.GroupId) AndAlso
                                  l.Adjustments IsNot Nothing AndAlso l.Adjustments.HasPixelAdjustments()).
                GroupBy(Function(l) l.GroupId & "|" & PixelAdjustmentsFingerprint(l.Adjustments), StringComparer.Ordinal).
                Where(Function(g) g.Count() > 1).
                ToDictionary(Function(g) g.Key, Function(g) g.ToList(), StringComparer.Ordinal)
            Dim erledigt As New HashSet(Of String)(StringComparer.Ordinal)

            For Each layer In adj.MaskedAdjustmentLayers
                If Not adj.IsMaskedLayerRenderVisible(layer) Then Continue For
                Dim stackedAbove = If(layer.StackAboveAnnotationId, "")
                If onlyStackedAboveId Is Nothing Then
                    If stackedAbove.Length > 0 Then Continue For          ' liegt im Objektstapel
                ElseIf Not String.Equals(stackedAbove, onlyStackedAboveId, StringComparison.Ordinal) Then
                    Continue For
                End If
                Dim hasAdj = layer.Adjustments IsNot Nothing AndAlso layer.Adjustments.HasPixelAdjustments()
                Dim hasFill = layer.HasFill()
                ' Auch Ebenen OHNE Pixel-Anpassung verarbeiten, wenn sie eine deklarative Füllung tragen
                ' (sichtbare Auswahl-Füllung bzw. Masken-Abstufung).
                If Not hasAdj AndAlso Not hasFill Then Continue For
                Dim maskData As ImageMask = Nothing
                If Not masksById.TryGetValue(If(layer.MaskId, ""), maskData) Then Continue For

                ' Rolle der Füllung nach Zweck, nicht nur nach Ebenenart: Eine Füllung, die MIT einer
                ' Anpassung auf derselben Ebene liegt, existiert, um diese Anpassung ABZUSTUFEN (Luminanz →
                ' Maskenstärke) - egal ob Masken- oder Auswahl-Ebene. Eine Füllung OHNE Anpassung ist reine
                ' sichtbare Farb-/Verlaufsfläche. (Geometrische Auswahlen kommen als IsMaskLayer=False; ohne
                ' diese hasAdj-Weiche stufte eine Verlaufsfüllung die Anpassung dort nie ab.)
                Dim modulateFill = hasFill AndAlso (layer.IsMaskLayer OrElse hasAdj)
                Using mask = BuildPersistentMaskForOutput(maskData, adj, pipelineInputWidth, pipelineInputHeight,
                                                          processed.Width, processed.Height, layer.Opacity,
                                                          If(modulateFill, layer, Nothing))
                    If mask Is Nothing Then Continue For
                    If hasAdj Then
                        ' Teilt diese Ebene ihre Anpassung mit anderen, wird EINMAL ueber die
                        ' Vereinigung aller ihrer Masken angewendet - an der Stelle der ERSTEN
                        ' Ebene der Gruppe, damit die Reihenfolge gegenueber anderen Korrekturen
                        ' erhalten bleibt.
                        Dim key = If(layer.GroupId, "") & "|" & PixelAdjustmentsFingerprint(layer.Adjustments)
                        Dim geschwister As List(Of MaskedAdjustmentLayer) = Nothing
                        If gemeinsam.TryGetValue(key, geschwister) Then
                            If erledigt.Contains(key) Then Continue For
                            erledigt.Add(key)
                        End If
                        Dim effectMask = mask
                        Dim eigene As SKBitmap = Nothing
                        Try
                            If geschwister IsNot Nothing Then
                                eigene = MergeEffectMasks(geschwister, layer, mask, adj, masksById,
                                                             pipelineInputWidth, pipelineInputHeight,
                                                             processed.Width, processed.Height, onlyStackedAboveId)
                                If eigene IsNot Nothing Then effectMask = eigene
                            End If
                            Using adjusted = ApplyPixelAdjustmentStages(processed, layer.Adjustments.ExtractPixelAdjustments())
                                processed = ReplaceBitmapOwned(processed, CompositeSelectionScoped(processed, adjusted, effectMask), owned)
                            End Using
                        Finally
                            eigene?.Dispose()
                        End Try
                    End If
                    ' Sichtbare Füllung nur, wenn die Füllung NICHT bereits eine Anpassung abstuft.
                    If hasFill AndAlso Not layer.IsMaskLayer AndAlso Not hasAdj Then
                        Dim filled = CompositeVisibleFill(processed, mask, layer)
                        If filled IsNot Nothing Then processed = ReplaceBitmapOwned(processed, filled, owned)
                    End If
                End Using
            Next
            Return processed
        End Function

        ''' <summary>Komponiert die deklarative Füllung einer AUSWAHL-Ebene SICHTBAR in ihre Auswahlregion:
        ''' Vollfarbe/Verlauf/radial über die Bounding-Box der Maske, Deckung = Maskenwert × Eigen-Alpha der
        ''' Füllung. So erscheint die Füllung als Farbfläche/Verlauf in der Auswahl - ohne PNG-Objekt.</summary>
        Private Shared Function CompositeVisibleFill(processed As SKBitmap, mask As SKBitmap, layer As MaskedAdjustmentLayer) As SKBitmap
            If processed Is Nothing OrElse mask Is Nothing OrElse layer Is Nothing Then Return Nothing
            Dim w = processed.Width, h = processed.Height
            If mask.Width <> w OrElse mask.Height <> h Then Return Nothing

            ' Bounding-Box der Maske, damit der Verlauf die Auswahl umspannt (nicht das ganze Bild).
            Dim mStride = mask.RowBytes
            Dim mBuf = New Byte(mStride * h - 1) {}
            Marshal.Copy(mask.GetPixels(), mBuf, 0, mBuf.Length)
            Dim minX = w, minY = h, maxX = -1, maxY = -1
            For y = 0 To h - 1
                Dim mRow = y * mStride
                For x = 0 To w - 1
                    If mBuf(mRow + x) > 0 Then
                        If x < minX Then minX = x
                        If x > maxX Then maxX = x
                        If y < minY Then minY = y
                        If y > maxY Then maxY = y
                    End If
                Next
            Next
            If maxX < minX OrElse maxY < minY Then Return Nothing

            Dim col = ParseColor(layer.FillColor, SKColors.White)
            Using fill = New SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(fill)
                    canvas.Clear(SKColors.Transparent)
                    Dim rect = New SKRect(minX, minY, maxX + 1, maxY + 1)
                    Dim nk = If(layer.FillKind, "Solid").Trim().ToLowerInvariant()
                    If nk = "lineargradient" OrElse nk = "radialgradient" Then
                        Dim col2 = ParseColor(layer.FillColor2, col)
                        Using shader = CreateFillGradientShader(rect, nk, col, col2, CSng(layer.FillAngle), layer.FillInverted)
                            Using paint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                                canvas.DrawRect(rect, paint)
                            End Using
                        End Using
                    Else
                        Using paint = New SKPaint With {.Color = col, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                            canvas.DrawRect(rect, paint)
                        End Using
                    End If
                End Using

                ' Deckung = Maskenwert einmultiplizieren (premultipliziert: alle Kanäle skalieren).
                Dim fStride = fill.RowBytes
                Dim fBuf = New Byte(fStride * h - 1) {}
                Marshal.Copy(fill.GetPixels(), fBuf, 0, fBuf.Length)
                For y = 0 To h - 1
                    Dim fRow = y * fStride, mRow = y * mStride
                    For x = 0 To w - 1
                        Dim m = CInt(mBuf(mRow + x))
                        If m < 255 Then
                            Dim o = fRow + x * 4
                            fBuf(o) = CByte(CInt(fBuf(o)) * m \ 255)
                            fBuf(o + 1) = CByte(CInt(fBuf(o + 1)) * m \ 255)
                            fBuf(o + 2) = CByte(CInt(fBuf(o + 2)) * m \ 255)
                            fBuf(o + 3) = CByte(CInt(fBuf(o + 3)) * m \ 255)
                        End If
                    Next
                Next
                Marshal.Copy(fBuf, 0, fill.GetPixels(), fBuf.Length)

                Dim result = CloneBitmap(processed)
                Using canvas = New SKCanvas(result)
                    canvas.DrawBitmap(fill, 0, 0)   ' SrcOver: Füllung über das Bild
                End Using
                Return result
            End Using
        End Function

        ''' <summary>Projiziert eine SourceSpace-Maske durch exakt dieselbe Geometriekette wie das Bild:
        ''' Preview-Skalierung, Crop, Quarter-Turn/Flip, Begradigung, Resize und Canvas-Offset.</summary>
        ''' <summary>Fingerabdruck NUR der Pixelwerte einer Anpassung. Zwei Ebenen, die gemeinsam
        ''' angepasst wurden, tragen danach denselben - und genau daran werden sie beim Rendern
        ''' wieder zusammengefuehrt.</summary>
        Private Shared Function PixelAdjustmentsFingerprint(adjustments As ImageAdjustments) As String
            If adjustments Is Nothing Then Return ""
            Try
                Return System.Text.Json.JsonSerializer.Serialize(adjustments.ExtractPixelAdjustments())
            Catch
                ' Nicht serialisierbar: dann lieber NICHT zusammenfassen (jede Ebene bekommt einen
                ' eigenen Schluessel) - das ist das bisherige Verhalten und nie schlechter als falsch.
                Return Guid.NewGuid().ToString("N")
            End Try
        End Function

        ''' <summary>Die Wirkmasken aller Geschwister mit derselben Anpassung zu EINER vereinigen
        ''' (je Pixel das Maximum). Liefert Nothing, wenn es nichts zu vereinigen gibt - dann bleibt
        ''' die Maske der ersten Ebene unveraendert in Gebrauch.
        '''
        ''' Maximum, nicht Addition: die Deckung eines Pixels soll die STAERKSTE der beteiligten
        ''' Masken sein. Zwei Masken mit je 60 % ergeben 60 %, nicht 120 % - sonst waere die
        ''' Ueberschneidung wieder ein Sonderfall, nur ein leiserer.</summary>
        Private Shared Function MergeEffectMasks(geschwister As List(Of MaskedAdjustmentLayer),
                                                    ersteEbene As MaskedAdjustmentLayer,
                                                    ersteMaske As SKBitmap,
                                                    adj As ImageAdjustments,
                                                    masksById As Dictionary(Of String, ImageMask),
                                                    pipelineInputWidth As Integer, pipelineInputHeight As Integer,
                                                    zielBreite As Integer, zielHoehe As Integer,
                                                    onlyStackedAboveId As String) As SKBitmap
            Dim result As SKBitmap = Nothing
            For Each g In geschwister
                If g Is Nothing OrElse ReferenceEquals(g, ersteEbene) Then Continue For
                If Not adj.IsMaskedLayerRenderVisible(g) Then Continue For
                ' Dieselbe Skopus-Weiche wie in der Hauptschleife: eine Ebene aus dem Objektstapel
                ' darf den Basisdurchlauf nicht mitfaerben und umgekehrt.
                Dim stacked = If(g.StackAboveAnnotationId, "")
                If onlyStackedAboveId Is Nothing Then
                    If stacked.Length > 0 Then Continue For
                ElseIf Not String.Equals(stacked, onlyStackedAboveId, StringComparison.Ordinal) Then
                    Continue For
                End If
                Dim md As ImageMask = Nothing
                If Not masksById.TryGetValue(If(g.MaskId, ""), md) Then Continue For
                Dim modFill = g.HasFill() AndAlso (g.IsMaskLayer OrElse
                                                   (g.Adjustments IsNot Nothing AndAlso g.Adjustments.HasPixelAdjustments()))
                Using m = BuildPersistentMaskForOutput(md, adj, pipelineInputWidth, pipelineInputHeight,
                                                       zielBreite, zielHoehe, g.Opacity, If(modFill, g, Nothing))
                    If m Is Nothing Then Continue For
                    If m.Width <> ersteMaske.Width OrElse m.Height <> ersteMaske.Height Then Continue For
                    If result Is Nothing Then result = CloneBitmap(ersteMaske)
                    MaskMaximum(result, m)
                End Using
            Next
            Return result
        End Function

        ''' <summary>Je Pixel das Maximum aus zwei gleich grossen Alpha8-Masken, in die erste.</summary>
        Private Shared Sub MaskMaximum(target As SKBitmap, source As SKBitmap)
            If target Is Nothing OrElse source Is Nothing Then Return
            If target.Width <> source.Width OrElse target.Height <> source.Height Then Return
            If target.GetPixels() = IntPtr.Zero OrElse source.GetPixels() = IntPtr.Zero Then Return
            Dim zStride As Integer, qStride As Integer
            Dim zb = ReadMaskBytes(target, zStride)
            Dim qb = ReadMaskBytes(source, qStride)
            Dim width = Math.Min(zStride, qStride)
            For y = 0 To target.Height - 1
                Dim zo = y * zStride, qo = y * qStride
                For x = 0 To width - 1
                    If qb(qo + x) > zb(zo + x) Then zb(zo + x) = qb(qo + x)
                Next
            Next
            Marshal.Copy(zb, 0, target.GetPixels(), zb.Length)
        End Sub

        Private Shared Function BuildPersistentMaskForOutput(maskData As ImageMask, geometry As ImageAdjustments,
                                                              pipelineInputWidth As Integer, pipelineInputHeight As Integer,
                                                              targetW As Integer, targetH As Integer,
                                                              layerOpacity As Single,
                                                              Optional fillLayer As MaskedAdjustmentLayer = Nothing) As SKBitmap
            If maskData Is Nothing OrElse pipelineInputWidth <= 0 OrElse pipelineInputHeight <= 0 OrElse
               targetW <= 0 OrElse targetH <= 0 OrElse
               maskData.SourceWidthPixels <= 0 OrElse maskData.SourceHeightPixels <= 0 Then Return Nothing
            ' Ein VERLAUF traegt weder PNG noch Bounding-Box - er wird gleich gerechnet.
            If Not maskData.IsGradient AndAlso
               (maskData.Right <= maskData.Left OrElse maskData.Bottom <= maskData.Top OrElse
                String.IsNullOrWhiteSpace(maskData.PngBase64)) Then Return Nothing
            Try
                ' Beim Verlauf gibt es nichts zu dekodieren; die Deckung entsteht pro Pixel aus der
                ' Projektion auf die Verlaufsachse (siehe unten). Das spart bei 45 MP rund 45 MB
                ' Zwischenpuffer und haelt den Verlauf ausserdem verlustfrei aenderbar.
                Dim raw = If(maskData.IsGradient, Nothing, Convert.FromBase64String(maskData.PngBase64))
                ' Pinselkorrektur eines Verlaufs: die beiden Raster einmal auspacken, danach kostet
                ' der Zugriff je Pixel nur noch einen Indexzugriff.
                Dim korrHinzu As Byte() = Nothing, korrWeg As Byte() = Nothing
                Dim corrWidth = 0, korrHoehe = 0
                If maskData.IsGradient AndAlso maskData.HasBrushCorrection Then
                    corrWidth = maskData.BrushRight - maskData.BrushLeft
                    korrHoehe = maskData.BrushBottom - maskData.BrushTop
                    korrHinzu = DecodeAlphaRaster(maskData.BrushAddPngBase64, corrWidth, korrHoehe)
                    korrWeg = DecodeAlphaRaster(maskData.BrushSubtractPngBase64, corrWidth, korrHoehe)
                    If korrHinzu Is Nothing AndAlso korrWeg Is Nothing Then corrWidth = 0
                End If
                Using decoded = If(raw Is Nothing, Nothing, SKBitmap.Decode(raw))
                    If Not maskData.IsGradient AndAlso (decoded Is Nothing OrElse decoded.ColorType <> SKColorType.Alpha8) Then Return Nothing
                    Dim dStride = If(decoded Is Nothing, 0, decoded.RowBytes)
                    Dim dBuf As Byte() = Nothing
                    If decoded IsNot Nothing Then
                        dBuf = New Byte(dStride * decoded.Height - 1) {}
                        Marshal.Copy(decoded.GetPixels(), dBuf, 0, dBuf.Length)
                    End If

                    ' Verlaufsachse EINMAL vorbereiten: Start- und Endpunkt in Quellpixeln, dazu
                    ' der Kehrwert des Achsenquadrats fuer die Projektion je Pixel.
                    Dim gx0, gy0, gAchseX, gAchseY, gInvLen2 As Double
                    Dim gRadius, gEx, gEy, gRatio, gInner As Double
                    If maskData.IsGradient Then
                        gx0 = maskData.GradientStartXPercent / 100.0 * maskData.SourceWidthPixels
                        gy0 = maskData.GradientStartYPercent / 100.0 * maskData.SourceHeightPixels
                        gAchseX = maskData.GradientEndXPercent / 100.0 * maskData.SourceWidthPixels - gx0
                        gAchseY = maskData.GradientEndYPercent / 100.0 * maskData.SourceHeightPixels - gy0
                        Dim len2 = gAchseX * gAchseX + gAchseY * gAchseY
                        ' Beide Punkte aufeinander = keine Achse bzw. kein Radius: dann waere die
                        ' Maske ueberall halb gedeckt statt eines Verlaufs. Lieber gar keine Maske.
                        If len2 < 0.000001 Then Return Nothing
                        gInvLen2 = 1.0 / len2
                        If maskData.IsLinearGradient Then
                            ' Der Regler "Weiche Kante" bekommt beim linearen Verlauf eine
                            ' Bedeutung, statt wirkungslos zu bleiben: er staucht den Uebergang um
                            ' die MITTE der Achse zusammen. 100 % = voller Weg zwischen den beiden
                            ' Punkten (Standard), 0 % = harte Kante genau in der Mitte. Ein Weichzeichner
                            ' waere hier der falsche Weg - die Rampe ist schon glatt, und Blur kostet
                            ' bei 45 MP echte Zeit, ohne etwas zu aendern.
                            gInner = Math.Max(0.02, Math.Min(1.0, maskData.GradientFeatherPercent / 100.0))
                        End If
                        If maskData.IsRadialGradient Then
                            ' Radial: die Achse ist die erste Halbachse. Ihre Richtung ist zugleich
                            ' die Drehung der Ellipse, deshalb wird jeder Punkt in dieses gedrehte
                            ' System gerechnet und die zweite Achse ueber das Verhaeltnis skaliert.
                            gRadius = Math.Sqrt(len2)
                            gEx = gAchseX / gRadius
                            gEy = gAchseY / gRadius
                            gRatio = Math.Max(0.05, maskData.GradientRadiusRatio)
                            ' Innerer Anteil mit voller Deckung; der Rest ist der weiche Uebergang.
                            gInner = Math.Max(0.0, Math.Min(1.0, 1.0 - maskData.GradientFeatherPercent / 100.0))
                        End If
                    End If

                    ' MASKEN-Ebene mit deklarativer Füllung: die LUMINANZ der Füllung stuft die Maskenform ab
                    ' (Schwarz→0, Weiß→voll, Verlauf→Rampe), bevor sie durch die Geometrie läuft. So bestimmt
                    ' die Füllung, WIE STARK die Anpassung je Bereich wirkt - ohne die Maskenform zu verlieren.
                    ' decoded ist bei einem Verlauf Nothing - eine Fuellung hat dort nichts zu
                    ' stufen, die Rampe IST schon die Abstufung.
                    If decoded IsNot Nothing AndAlso fillLayer IsNot Nothing AndAlso fillLayer.HasFill() Then
                        Dim lum = ComputeFillLuminance(decoded.Width, decoded.Height, fillLayer.FillKind,
                            fillLayer.FillColor, fillLayer.FillColor2, CSng(fillLayer.FillAngle), fillLayer.FillInverted)
                        If lum IsNot Nothing Then
                            For y = 0 To decoded.Height - 1
                                Dim dRow = y * dStride, lRow = y * decoded.Width
                                For x = 0 To decoded.Width - 1
                                    dBuf(dRow + x) = CByte(CInt(dBuf(dRow + x)) * CInt(lum(lRow + x)) \ 255)
                                Next
                            Next
                        End If
                    End If

                    ' Zunächst in die tatsächliche Pipeline-Eingangsgröße (Vollbild oder Preview)
                    ' rasterisieren. Dadurch benutzt die anschließende Geometrie exakt dieselben
                    ' Pixelrundungen wie das Bild und braucht keine zweite, leicht abweichende Matrix.
                    Dim inputMask = New SKBitmap(pipelineInputWidth, pipelineInputHeight, SKColorType.Alpha8, SKAlphaType.Premul)
                    Dim iStride = inputMask.RowBytes
                    Dim iBuf = New Byte(iStride * pipelineInputHeight - 1) {}
                    For y = 0 To pipelineInputHeight - 1
                        Dim sySource = CInt(Math.Floor((y + 0.5) * maskData.SourceHeightPixels / pipelineInputHeight))
                        Dim sy = sySource - maskData.Top
                        Dim iRow = y * iStride
                        For x = 0 To pipelineInputWidth - 1
                            Dim sx = CInt(Math.Floor((x + 0.5) * maskData.SourceWidthPixels / pipelineInputWidth)) - maskData.Left
                            Dim alpha = 0
                            If maskData.IsRadialGradient Then
                                ' Abstand im gedrehten Ellipsensystem: 0 = Mittelpunkt, 1 = Rand.
                                Dim dx = sx + maskData.Left - gx0
                                Dim dy = sySource - gy0
                                Dim laengs = (dx * gEx + dy * gEy) / gRadius
                                Dim quer = (-dx * gEy + dy * gEx) / (gRadius * gRatio)
                                Dim d = Math.Sqrt(laengs * laengs + quer * quer)
                                If d <= gInner Then
                                    alpha = 255
                                ElseIf d >= 1.0 Then
                                    alpha = 0
                                Else
                                    Dim t2 = (d - gInner) / Math.Max(0.000001, 1.0 - gInner)
                                    Dim s2 = t2 * t2 * (3.0 - 2.0 * t2)
                                    alpha = CInt(Math.Round((1.0 - s2) * 255.0))
                                End If
                            ElseIf maskData.IsGradient Then
                                ' Projektion des Pixels auf die Verlaufsachse: t = 0 am Startpunkt
                                ' (volle Deckung), t = 1 am Endpunkt (keine). Ausserhalb wird
                                ' geklemmt, der Verlauf gilt also fuer das GANZE Bild.
                                Dim t = ((sx + maskData.Left - gx0) * gAchseX + (sySource - gy0) * gAchseY) * gInvLen2
                                ' Weichheit um die Mitte: t=0,5 bleibt der Wendepunkt, gInnen ist die
                                ' Breite des Uebergangs (siehe oben).
                                t = 0.5 + (t - 0.5) / gInner
                                If t <= 0.0 Then
                                    alpha = 255
                                ElseIf t >= 1.0 Then
                                    alpha = 0
                                Else
                                    ' Smoothstep statt linear: eine lineare Rampe zeigt an ihren
                                    ' beiden Enden eine sichtbare Kante (Mach-Band), gerade in
                                    ' Himmelsflaechen. Der weiche Ein- und Ausstieg ist das, was
                                    ' einen Verlaufsfilter unauffaellig macht.
                                    Dim s = t * t * (3.0 - 2.0 * t)
                                    alpha = CInt(Math.Round((1.0 - s) * 255.0))
                                End If
                            ElseIf sx >= 0 AndAlso sy >= 0 AndAlso sx < decoded.Width AndAlso sy < decoded.Height Then
                                alpha = dBuf(sy * dStride + sx)
                            End If
                            ' Pinselkorrektur NACH dem Verlauf und VOR dem Umkehren: "Umkehren"
                            ' soll das fertige Ergebnis spiegeln, nicht nur den Verlaufsanteil -
                            ' sonst kaeme ein weggepinselter Bereich beim Umkehren zurueck.
                            If corrWidth > 0 Then
                                Dim kx = sx + maskData.Left - maskData.BrushLeft
                                Dim ky = sySource - maskData.BrushTop
                                If kx >= 0 AndAlso ky >= 0 AndAlso kx < corrWidth AndAlso ky < korrHoehe Then
                                    Dim ki = ky * corrWidth + kx
                                    If korrHinzu IsNot Nothing Then alpha += korrHinzu(ki)
                                    If korrWeg IsNot Nothing Then alpha -= korrWeg(ki)
                                    If alpha < 0 Then
                                        alpha = 0
                                    ElseIf alpha > 255 Then
                                        alpha = 255
                                    End If
                                End If
                            End If
                            If maskData.Inverted Then alpha = 255 - alpha
                            iBuf(iRow + x) = CByte(alpha)
                        Next
                    Next
                    Marshal.Copy(iBuf, 0, inputMask.GetPixels(), iBuf.Length)

                    ' Verlaeufe sind bereits glatt - ihr Weichheits-Regler sitzt in der Geometrie
                    ' (GradientFeatherPercent), nicht in einem nachgeschalteten Weichzeichner.
                    If maskData.FeatherPixels > 0.05F AndAlso Not maskData.IsGradient Then
                        Dim initialScale = (pipelineInputWidth / CSng(maskData.SourceWidthPixels) +
                                            pipelineInputHeight / CSng(maskData.SourceHeightPixels)) / 2.0F
                        Dim blurred = BlurAlphaMask(inputMask, maskData.FeatherPixels * initialScale)
                        If blurred IsNot Nothing Then inputMask = ReplaceBitmap(inputMask, blurred)
                    End If

                    Dim maskPixels = New SKBitmap(pipelineInputWidth, pipelineInputHeight, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Dim pStride = maskPixels.RowBytes
                    Dim pBuf = New Byte(pStride * pipelineInputHeight - 1) {}
                    Dim alphaBuf = New Byte(inputMask.RowBytes * inputMask.Height - 1) {}
                    Marshal.Copy(inputMask.GetPixels(), alphaBuf, 0, alphaBuf.Length)
                    For y = 0 To pipelineInputHeight - 1
                        Dim aRow = y * inputMask.RowBytes, pRow = y * pStride
                        For x = 0 To pipelineInputWidth - 1
                            Dim a = alphaBuf(aRow + x), o = pRow + x * 4
                            pBuf(o) = a : pBuf(o + 1) = a : pBuf(o + 2) = a : pBuf(o + 3) = a
                        Next
                    Next
                    Marshal.Copy(pBuf, 0, maskPixels.GetPixels(), pBuf.Length)
                    inputMask.Dispose()

                    Dim maskGeometry = New ImageAdjustments With {
                        .CropLeftPercent = geometry.CropLeftPercent, .CropTopPercent = geometry.CropTopPercent,
                        .CropRightPercent = geometry.CropRightPercent, .CropBottomPercent = geometry.CropBottomPercent,
                        .RotationDegrees = geometry.RotationDegrees,
                        .FlipHorizontal = geometry.FlipHorizontal, .FlipVertical = geometry.FlipVertical,
                        .StraightenDegrees = geometry.StraightenDegrees,
                        .StraightenExpandCanvas = geometry.StraightenExpandCanvas,
                        .PerspectiveHorizontal = geometry.PerspectiveHorizontal,
                        .PerspectiveVertical = geometry.PerspectiveVertical,
                        .PerspectiveAspect = geometry.PerspectiveAspect,
                        .PerspectiveScale = geometry.PerspectiveScale,
                        .PerspectiveCorner0X = geometry.PerspectiveCorner0X, .PerspectiveCorner0Y = geometry.PerspectiveCorner0Y,
                        .PerspectiveCorner1X = geometry.PerspectiveCorner1X, .PerspectiveCorner1Y = geometry.PerspectiveCorner1Y,
                        .PerspectiveCorner2X = geometry.PerspectiveCorner2X, .PerspectiveCorner2Y = geometry.PerspectiveCorner2Y,
                        .PerspectiveCorner3X = geometry.PerspectiveCorner3X, .PerspectiveCorner3Y = geometry.PerspectiveCorner3Y,
                        .ResizeWidth = geometry.ResizeWidth, .ResizeHeight = geometry.ResizeHeight,
                        .ResizeInterpolation = geometry.ResizeInterpolation,
                        .CanvasWidth = geometry.CanvasWidth, .CanvasHeight = geometry.CanvasHeight,
                        .CanvasAnchor = geometry.CanvasAnchor,
                        .CanvasBackgroundColor = "#00000000"
                    }
                    maskPixels = ReplaceBitmap(maskPixels, ApplyCrop(maskPixels, maskGeometry))
                    maskPixels = ReplaceBitmap(maskPixels, ApplyGeometryTransforms(maskPixels, maskGeometry))
                    maskPixels = ReplaceBitmap(maskPixels, ApplyStraighten(maskPixels, maskGeometry))
                    maskPixels = ReplaceBitmap(maskPixels, ApplyPerspective(maskPixels, maskGeometry))
                    maskPixels = ReplaceBitmap(maskPixels, ApplyResize(maskPixels, maskGeometry))
                    maskPixels = ReplaceBitmap(maskPixels, ApplyCanvasResize(maskPixels, maskGeometry))

                    ' Derselbe Pfad sollte dieselbe Größe liefern. Der Fallback schützt dennoch vor
                    ' alten/inkonsistenten Rezeptmaßen, ohne die Korrektur global werden zu lassen.
                    If maskPixels.Width <> targetW OrElse maskPixels.Height <> targetH Then
                        Dim scaled = New SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul)
                        Using canvas = New SKCanvas(scaled)
                            canvas.Clear(SKColors.Transparent)
                            Using paint = New SKPaint With {.IsAntialias = True}
                                DrawBitmapSampled(canvas, maskPixels, New SKRect(0, 0, maskPixels.Width, maskPixels.Height),
                                                  New SKRect(0, 0, targetW, targetH),
                                                  New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint)
                            End Using
                        End Using
                        maskPixels = ReplaceBitmap(maskPixels, scaled)
                    End If

                    Dim transformed As Byte() = Nothing, transformedStride As Integer = 0
                    If Not TryBorrowBgraBuffer(maskPixels, transformed, transformedStride) Then
                        maskPixels.Dispose()
                        Return Nothing
                    End If
                    Dim opacity = Clamp(layerOpacity, 0, 1)
                    Dim result = New SKBitmap(targetW, targetH, SKColorType.Alpha8, SKAlphaType.Premul)
                    Dim rStride = result.RowBytes
                    Dim rBuf = New Byte(rStride * targetH - 1) {}
                    For y = 0 To targetH - 1
                        Dim pRow = y * transformedStride, rRow = y * rStride
                        For x = 0 To targetW - 1
                            rBuf(rRow + x) = CByte(Math.Round(transformed(pRow + x * 4 + 3) * opacity))
                        Next
                    Next
                    Marshal.Copy(rBuf, 0, result.GetPixels(), rBuf.Length)
                    maskPixels.Dispose()
                    Return result
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Exakte Ausgabemaße der Geometriekette ohne Pixelanpassungen/Objekte.</summary>
        Public Shared Function ComputeGeometryOutputSize(sourceWidth As Integer, sourceHeight As Integer,
                                                         adj As ImageAdjustments) As SKSizeI
            If sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return New SKSizeI(0, 0)
            Dim crop = ComputeGeometryCropRect(sourceWidth, sourceHeight, adj)
            Dim w = crop.Width, h = crop.Height
            Dim q = ImageGeometryMapper.NormalizeQuarterTurn(adj.RotationDegrees)
            If q = 90 OrElse q = 270 Then
                Dim swap = w : w = h : h = swap
            End If
            If Math.Abs(adj.StraightenDegrees) >= 0.01F AndAlso adj.StraightenExpandCanvas Then
                Dim radians = Math.Abs(adj.StraightenDegrees) * Math.PI / 180.0
                Dim oldW = w, oldH = h
                w = Math.Max(1, CInt(Math.Ceiling(oldW * Math.Cos(radians) + oldH * Math.Sin(radians))))
                h = Math.Max(1, CInt(Math.Ceiling(oldW * Math.Sin(radians) + oldH * Math.Cos(radians))))
            End If
            Dim resizeW = adj.ResizeWidth, resizeH = adj.ResizeHeight
            If resizeW > 0 OrElse resizeH > 0 Then
                If resizeW <= 0 Then resizeW = CInt(Math.Round(w * (resizeH / CDbl(h))))
                If resizeH <= 0 Then resizeH = CInt(Math.Round(h * (resizeW / CDbl(w))))
                w = Math.Max(1, resizeW) : h = Math.Max(1, resizeH)
            End If
            If adj.CanvasWidth > 0 Then w = adj.CanvasWidth
            If adj.CanvasHeight > 0 Then h = adj.CanvasHeight
            Return New SKSizeI(Math.Max(1, w), Math.Max(1, h))
        End Function

        Friend Shared Function ComputeGeometryCropRect(sourceWidth As Integer, sourceHeight As Integer,
                                                        adj As ImageAdjustments) As SKRectI
            Dim left = Math.Max(0, Math.Min(CInt(Math.Round(sourceWidth * Clamp(adj.CropLeftPercent, 0, 100) / 100.0F)), sourceWidth - 1))
            Dim top = Math.Max(0, Math.Min(CInt(Math.Round(sourceHeight * Clamp(adj.CropTopPercent, 0, 100) / 100.0F)), sourceHeight - 1))
            Dim right = Math.Max(left + 1, Math.Min(sourceWidth - CInt(Math.Round(sourceWidth * Clamp(adj.CropRightPercent, 0, 100) / 100.0F)), sourceWidth))
            Dim bottom = Math.Max(top + 1, Math.Min(sourceHeight - CInt(Math.Round(sourceHeight * Clamp(adj.CropBottomPercent, 0, 100) / 100.0F)), sourceHeight))
            Return New SKRectI(left, top, right, bottom)
        End Function

        ''' <summary>Bildet einen Punkt des unbeschnittenen SourceSpace durch dieselbe Geometriekette
        ''' wie der Renderer ab. False bedeutet: Der Punkt wurde vom Crop entfernt.
        ''' Public, weil der Editor dieselbe VOLLSTÄNDIGE Abbildung braucht für
        ''' Pinsel-/Retusche-Overlays - die Mapper-Kurzform (nur Drehung/Flip) saß nach
        ''' angewendetem Crop/Resize/Canvas daneben.</summary>
        Public Shared Function TrySourcePointToGeometryOutput(sourceX As Double, sourceY As Double,
                                                              sourceWidth As Integer, sourceHeight As Integer,
                                                              adj As ImageAdjustments, ByRef output As SKPoint) As Boolean
            Dim crop = ComputeGeometryCropRect(sourceWidth, sourceHeight, adj)
            If sourceX < crop.Left OrElse sourceY < crop.Top OrElse sourceX >= crop.Right OrElse sourceY >= crop.Bottom Then Return False
            Dim x = sourceX - crop.Left, y = sourceY - crop.Top
            Dim w As Double = crop.Width, h As Double = crop.Height

            Dim p = ImageGeometryMapper.SourcePointToDisplay(x, y, w, h, adj.RotationDegrees,
                                                              adj.FlipHorizontal, adj.FlipVertical)
            x = p.X : y = p.Y
            Dim q = ImageGeometryMapper.NormalizeQuarterTurn(adj.RotationDegrees)
            If q = 90 OrElse q = 270 Then
                Dim swap = w : w = h : h = swap
            End If

            If Math.Abs(adj.StraightenDegrees) >= 0.01F Then
                Dim radians = adj.StraightenDegrees * Math.PI / 180.0
                Dim absRadians = Math.Abs(adj.StraightenDegrees) * Math.PI / 180.0
                Dim outW = w, outH = h, scale = 1.0
                If adj.StraightenExpandCanvas Then
                    outW = Math.Max(1, Math.Ceiling(w * Math.Cos(absRadians) + h * Math.Sin(absRadians)))
                    outH = Math.Max(1, Math.Ceiling(w * Math.Sin(absRadians) + h * Math.Cos(absRadians)))
                Else
                    scale = Math.Max(w / (w * Math.Cos(absRadians) + h * Math.Sin(absRadians)),
                                     h / (w * Math.Sin(absRadians) + h * Math.Cos(absRadians)))
                    scale = Math.Max(1.0, scale)
                End If
                Dim dx = x - w / 2.0, dy = y - h / 2.0
                Dim cosA = Math.Cos(radians), sinA = Math.Sin(radians)
                x = outW / 2.0 + scale * (cosA * dx - sinA * dy)
                y = outH / 2.0 + scale * (sinA * dx + cosA * dy)
                w = outW : h = outH
            End If

            ' --- Verzerren --- (dieselbe Stelle wie im Bildweg: nach der Begradigung, vor dem
            ' Skalieren; die Stufe laesst die Masse unveraendert, deshalb aendert sich hier nur der
            ' Punkt und nicht w/h)
            Dim warp = ImageGeometryMapper.WarpMatrix(w, h,
                                 adj.PerspectiveHorizontal, adj.PerspectiveVertical,
                                 adj.PerspectiveAspect, adj.PerspectiveScale,
                                 ImageGeometryMapper.CornerOffset(adj))
            If Not warp.IsIdentity Then
                Dim v = warp.MapPoint(New SKPoint(CSng(x), CSng(y)))
                x = v.X : y = v.Y
                ' Was aus dem Rahmen kippt, wird von der Stufe abgeschnitten - fuer so einen Punkt
                ' gibt es im Ausgabebild keine Stelle. Ohne diese Pruefung meldete der Hinweg einen
                ' Punkt ausserhalb des Bildes als gueltig, und der Rueckweg wiese ihn ab.
                If Not TryClampToRange(x, w) OrElse Not TryClampToRange(y, h) Then Return False
            End If

            Dim resizeW = adj.ResizeWidth, resizeH = adj.ResizeHeight
            If resizeW > 0 OrElse resizeH > 0 Then
                If resizeW <= 0 Then resizeW = CInt(Math.Round(w * (resizeH / h)))
                If resizeH <= 0 Then resizeH = CInt(Math.Round(h * (resizeW / w)))
                resizeW = Math.Max(1, resizeW) : resizeH = Math.Max(1, resizeH)
                x *= resizeW / w : y *= resizeH / h
                w = resizeW : h = resizeH
            End If

            Dim canvasW = If(adj.CanvasWidth > 0, adj.CanvasWidth, CInt(Math.Round(w)))
            Dim canvasH = If(adj.CanvasHeight > 0, adj.CanvasHeight, CInt(Math.Round(h)))
            If canvasW <> CInt(Math.Round(w)) OrElse canvasH <> CInt(Math.Round(h)) Then
                Dim offsetX As Double, offsetY As Double
                Select Case If(adj.CanvasAnchor, "Center").Trim().ToLowerInvariant()
                    Case "top-left", "left-top" : offsetX = 0 : offsetY = 0
                    Case "top", "top-center" : offsetX = (canvasW - w) / 2.0 : offsetY = 0
                    Case "top-right", "right-top" : offsetX = canvasW - w : offsetY = 0
                    Case "left", "middle-left" : offsetX = 0 : offsetY = (canvasH - h) / 2.0
                    Case "right", "middle-right" : offsetX = canvasW - w : offsetY = (canvasH - h) / 2.0
                    Case "bottom-left", "left-bottom" : offsetX = 0 : offsetY = canvasH - h
                    Case "bottom", "bottom-center" : offsetX = (canvasW - w) / 2.0 : offsetY = canvasH - h
                    Case "bottom-right", "right-bottom" : offsetX = canvasW - w : offsetY = canvasH - h
                    Case Else : offsetX = (canvasW - w) / 2.0 : offsetY = (canvasH - h) / 2.0
                End Select
                x += offsetX : y += offsetY
            End If
            output = New SKPoint(CSng(x), CSng(y))
            Return True
        End Function

        ''' <summary>EXAKTE Inverse von <see cref="TrySourcePointToGeometryOutput"/>:
        ''' bildet einen Punkt des AUSGABE-Raums (Anzeigebild nach Crop/Vierteldrehung/Begradigung/
        ''' Resize/Canvas) zurück auf den unbeschnittenen SourceSpace. Jede Stufe wird in umgekehrter
        ''' Reihenfolge mit den IDENTISCHEN Maß-Formeln (inkl. Rundungen) abgelöst, damit Hin- und
        ''' Rückweg dieselben Zwischengrößen sehen. False bedeutet: der Punkt liegt außerhalb des
        ''' Bildinhalts (Canvas-Rand, leere Begradigungs-Ecke) oder des Crop-Ausschnitts - dort gibt
        ''' es keinen Source-Pixel, ein Backen (Pinsel/Retusche) muss den Punkt überspringen.
        ''' Randtoleranz: bis 0,5 px außerhalb wird auf die Kante geklemmt (Rundungsrauschen der
        ''' Anzeige), erst darüber hinaus ist der Punkt wirklich "neben dem Bild".</summary>
        Public Shared Function TryGeometryOutputToSourcePoint(outputX As Double, outputY As Double,
                                                              sourceWidth As Integer, sourceHeight As Integer,
                                                              adj As ImageAdjustments, ByRef source As SKPoint) As Boolean
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse adj Is Nothing Then Return False
            Dim crop = ComputeGeometryCropRect(sourceWidth, sourceHeight, adj)
            Dim w As Double = crop.Width, h As Double = crop.Height

            ' Maße der Kette VORWÄRTS nachvollziehen (gleiche Formeln wie oben), um sie rückwärts
            ' Stufe für Stufe abzulösen.
            Dim q = ImageGeometryMapper.NormalizeQuarterTurn(adj.RotationDegrees)
            Dim rotW = If(q = 90 OrElse q = 270, h, w)
            Dim rotH = If(q = 90 OrElse q = 270, w, h)

            Dim hasStraighten = Math.Abs(adj.StraightenDegrees) >= 0.01F
            Dim outW = rotW, outH = rotH, scale = 1.0
            If hasStraighten Then
                Dim absRadians = Math.Abs(adj.StraightenDegrees) * Math.PI / 180.0
                If adj.StraightenExpandCanvas Then
                    outW = Math.Max(1, Math.Ceiling(rotW * Math.Cos(absRadians) + rotH * Math.Sin(absRadians)))
                    outH = Math.Max(1, Math.Ceiling(rotW * Math.Sin(absRadians) + rotH * Math.Cos(absRadians)))
                Else
                    scale = Math.Max(rotW / (rotW * Math.Cos(absRadians) + rotH * Math.Sin(absRadians)),
                                     rotH / (rotW * Math.Sin(absRadians) + rotH * Math.Cos(absRadians)))
                    scale = Math.Max(1.0, scale)
                End If
            End If

            Dim resizeW = adj.ResizeWidth, resizeH = adj.ResizeHeight
            If resizeW > 0 OrElse resizeH > 0 Then
                If resizeW <= 0 Then resizeW = CInt(Math.Round(outW * (resizeH / outH)))
                If resizeH <= 0 Then resizeH = CInt(Math.Round(outH * (resizeW / outW)))
                resizeW = Math.Max(1, resizeW) : resizeH = Math.Max(1, resizeH)
            Else
                resizeW = 0 : resizeH = 0
            End If
            Dim afterResizeW = If(resizeW > 0, CDbl(resizeW), outW)
            Dim afterResizeH = If(resizeH > 0, CDbl(resizeH), outH)

            Dim x = outputX, y = outputY

            ' --- Canvas-Offset zurück ---
            Dim canvasW = If(adj.CanvasWidth > 0, adj.CanvasWidth, CInt(Math.Round(afterResizeW)))
            Dim canvasH = If(adj.CanvasHeight > 0, adj.CanvasHeight, CInt(Math.Round(afterResizeH)))
            If canvasW <> CInt(Math.Round(afterResizeW)) OrElse canvasH <> CInt(Math.Round(afterResizeH)) Then
                Dim offsetX As Double, offsetY As Double
                Select Case If(adj.CanvasAnchor, "Center").Trim().ToLowerInvariant()
                    Case "top-left", "left-top" : offsetX = 0 : offsetY = 0
                    Case "top", "top-center" : offsetX = (canvasW - afterResizeW) / 2.0 : offsetY = 0
                    Case "top-right", "right-top" : offsetX = canvasW - afterResizeW : offsetY = 0
                    Case "left", "middle-left" : offsetX = 0 : offsetY = (canvasH - afterResizeH) / 2.0
                    Case "right", "middle-right" : offsetX = canvasW - afterResizeW : offsetY = (canvasH - afterResizeH) / 2.0
                    Case "bottom-left", "left-bottom" : offsetX = 0 : offsetY = canvasH - afterResizeH
                    Case "bottom", "bottom-center" : offsetX = (canvasW - afterResizeW) / 2.0 : offsetY = canvasH - afterResizeH
                    Case "bottom-right", "right-bottom" : offsetX = canvasW - afterResizeW : offsetY = canvasH - afterResizeH
                    Case Else : offsetX = (canvasW - afterResizeW) / 2.0 : offsetY = (canvasH - afterResizeH) / 2.0
                End Select
                x -= offsetX : y -= offsetY
            End If
            If Not TryClampToRange(x, afterResizeW) OrElse Not TryClampToRange(y, afterResizeH) Then Return False

            ' --- Resize zurück ---
            If resizeW > 0 Then
                x *= outW / afterResizeW
                y *= outH / afterResizeH
            End If

            ' --- Verzerren zurück --- (Gegenstueck zur Vorwaertsstufe oben; die Umkehrmatrix
            ' existiert immer, solange die Homographie nicht entartet ist)
            ' outW/outH, NICHT rotW/rotH: an dieser Stelle liegt der Punkt im Raum NACH der
            ' Begradigung, und genau darauf rechnet die Vorwaertsstufe.
            Dim warp = ImageGeometryMapper.WarpMatrix(outW, outH,
                                 adj.PerspectiveHorizontal, adj.PerspectiveVertical,
                                 adj.PerspectiveAspect, adj.PerspectiveScale,
                                 ImageGeometryMapper.CornerOffset(adj))
            If Not warp.IsIdentity Then
                Dim umkehr As SKMatrix = Nothing
                If Not warp.TryInvert(umkehr) Then Return False
                Dim v = umkehr.MapPoint(New SKPoint(CSng(x), CSng(y)))
                x = v.X : y = v.Y
            End If

            ' --- Begradigung zurück (invers: erst ent-drehen, dann ent-skalieren) ---
            If hasStraighten Then
                Dim radians = adj.StraightenDegrees * Math.PI / 180.0
                Dim cosA = Math.Cos(radians), sinA = Math.Sin(radians)
                Dim dx = x - outW / 2.0, dy = y - outH / 2.0
                Dim ux = (cosA * dx + sinA * dy) / scale
                Dim uy = (-sinA * dx + cosA * dy) / scale
                x = rotW / 2.0 + ux
                y = rotH / 2.0 + uy
                If Not TryClampToRange(x, rotW) OrElse Not TryClampToRange(y, rotH) Then Return False
            End If

            ' --- Vierteldrehung/Flip zurück (auf den beschnittenen Ausschnitt bezogen) ---
            Dim p = ImageGeometryMapper.DisplayPointToSource(x, y, w, h, adj.RotationDegrees,
                                                             adj.FlipHorizontal, adj.FlipVertical)
            Dim sx As Double = p.X, sy As Double = p.Y
            If Not TryClampToRange(sx, w) OrElse Not TryClampToRange(sy, h) Then Return False

            source = New SKPoint(CSng(sx + crop.Left), CSng(sy + crop.Top))
            Return True
        End Function

        ''' Klemmt einen Wert in [0, size): bis 0,5 px außerhalb ist Rundungsrauschen und wird auf
        ''' die Kante gelegt, darüber hinaus liegt der Punkt echt außerhalb (False).
        Private Shared Function TryClampToRange(ByRef value As Double, size As Double) As Boolean
            If value < -0.5 OrElse value > size + 0.5 Then Return False
            If value < 0 Then value = 0
            If value >= size Then value = Math.Max(0.0, size - 0.001)
            Return True
        End Function

        ''' <summary>Friert die momentan aktive OutputSpace-Auswahl als persistente SourceSpace-Maske ein.</summary>
        ''' <summary>Packt ein Alpha8-PNG in einen dichten Bytepuffer der erwarteten Groesse aus.
        ''' Nothing bei leerem String, unlesbaren Daten, falschem Farbtyp oder abweichender Groesse -
        ''' ein stiller Rueckfall auf halbe Deckung waere hier schlimmer als gar keine Korrektur.</summary>
        Private Shared Function DecodeAlphaRaster(base64 As String, width As Integer, height As Integer) As Byte()
            If String.IsNullOrWhiteSpace(base64) OrElse width <= 0 OrElse height <= 0 Then Return Nothing
            Try
                Using bmp = SKBitmap.Decode(Convert.FromBase64String(base64))
                    If bmp Is Nothing OrElse bmp.ColorType <> SKColorType.Alpha8 Then Return Nothing
                    If bmp.Width <> width OrElse bmp.Height <> height Then Return Nothing
                    Dim stride = bmp.RowBytes
                    Dim source = New Byte(stride * height - 1) {}
                    Marshal.Copy(bmp.GetPixels(), source, 0, source.Length)
                    If stride = width Then Return source
                    Dim dense = New Byte(width * height - 1) {}
                    For y = 0 To height - 1
                        Buffer.BlockCopy(source, y * stride, dense, y * width, width)
                    Next
                    Return dense
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function EncodeAlphaRaster(puffer As Byte(), width As Integer, height As Integer) As String
            If puffer Is Nothing OrElse width <= 0 OrElse height <= 0 Then Return ""
            Dim empty = True
            For i = 0 To puffer.Length - 1
                If puffer(i) <> 0 Then
                    empty = False
                    Exit For
                End If
            Next
            If empty Then Return ""
            Using bmp = New SKBitmap(width, height, SKColorType.Alpha8, SKAlphaType.Premul)
                Dim stride = bmp.RowBytes
                Dim target = New Byte(stride * height - 1) {}
                For y = 0 To height - 1
                    Buffer.BlockCopy(puffer, y * width, target, y * stride, width)
                Next
                Marshal.Copy(target, 0, bmp.GetPixels(), target.Length)
                Using image = SKImage.FromBitmap(bmp)
                    Using data = image.Encode(SKEncodedImageFormat.Png, FastPngCompressionQuality)
                        Return Convert.ToBase64String(data.ToArray())
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Verrechnet einen Pinselstrich (als Quellraum-Maske, wie ihn
        ''' CreateSourceMaskFromSelection liefert) in die Pinselkorrektur eines VERLAUFS.
        '''
        ''' <paramref name="abziehen"/> steuert, in welches der beiden Raster der Strich geht. Beide
        ''' werden mit MAXIMUM verrechnet, nicht addiert: zweimal ueber dieselbe Stelle zu streichen
        ''' soll sie nicht "doppelt" wegnehmen (das gaebe sichtbare Stufen an Ueberlappungen), sondern
        ''' dasselbe Ergebnis liefern wie einmal. Ein Strich in die eine Richtung LOESCHT ausserdem
        ''' die Gegenrichtung an denselben Stellen - sonst liesse sich ein versehentliches Abziehen
        ''' nie wieder zurueckholen, weil beide Raster gegeneinander stehen blieben.
        '''
        ''' Gerechnet wird nur auf der VEREINIGUNG der bemalten Rechtecke, nicht auf dem ganzen Bild:
        ''' bei 50 MP waeren das sonst zwei Puffer von je 50 MB pro Strich.</summary>
        ''' <summary>EINE Stelle, an der ein Maskenpinsel-Strich sein Ziel kennt. Die drei Fälle -
        ''' gemalte Maske, Verlauf mit Pinselkorrektur, kein Ziel - standen vorher als If-Zweige im
        ''' EditorViewModel verstreut, und genau dort ist derselbe Fehler dreimal passiert (Commit,
        ''' Abbruch und Live-Vorschau riefen den Auswahl-Weg, obwohl das Ziel ein Verlauf war).
        '''
        ''' Hier unten statt im ViewModel, weil das ViewModel headless nicht instanziierbar ist (sein
        ''' einziger Konstruktor verlangt das MainWindowViewModel) - alle Prüfungen dort können nur
        ''' Quelltext abgleichen. In der Engine sind es echte Verhaltensprüfungen.
        '''
        ''' <paramref name="strich"/> ist der Strich als Quellraum-Maske, wie ihn
        ''' CreateSourceMaskFromSelection liefert.
        '''
        ''' STAND: die Anwendung ruft das bisher NUR fuer Verlaufsmasken. Striche auf eine gemalte
        ''' Ebenen-Maske laufen weiterhin ueber die Auswahl (ApplySelectionCandidate ->
        ''' WriteSelectionMaskBackToLayer), weil deren Raster zugleich die Quelle des roten Overlays
        ''' ist. MergePaintedMaskStroke ist der vorbereitete Weg dorthin und wird heute nur von der
        ''' Diagnose gefahren - siehe Audits/OFFENE_PUNKTE.md.</summary>
        Public Shared Function ApplyMaskBrushStroke(target As ImageMask, stroke As ImageMask,
                                                    subtract As Boolean) As Boolean
            If target Is Nothing OrElse stroke Is Nothing Then Return False
            If target.IsGradient Then Return MergeGradientBrushCorrection(target, stroke, subtract)
            Return MergePaintedMaskStroke(target, stroke, subtract)
        End Function

        ''' <summary>Strich in eine GEMALTE Maske einrechnen: hinzufügen nimmt das Maximum, abziehen
        ''' zieht ab. Das Rechteck wächst beim Hinzufügen mit; beim Abziehen bleibt es stehen, statt
        ''' es teuer neu zu vermessen - ein zu großes Rechteck mit Nullen kostet nur etwas Speicher,
        ''' ein zu kleines würde Maskenteile abschneiden.</summary>
        Public Shared Function MergePaintedMaskStroke(mask As ImageMask, stroke As ImageMask,
                                                      subtract As Boolean) As Boolean
            If mask Is Nothing OrElse stroke Is Nothing OrElse mask.IsGradient Then Return False
            If stroke.Right <= stroke.Left OrElse stroke.Bottom <= stroke.Top Then Return False
            Dim sWidth = stroke.Right - stroke.Left, sHeight = stroke.Bottom - stroke.Top
            Dim sPuffer = DecodeAlphaRaster(stroke.PngBase64, sWidth, sHeight)
            If sPuffer Is Nothing Then Return False

            Dim oldWidth = mask.Right - mask.Left, oldHeight = mask.Bottom - mask.Top
            Dim altPuffer As Byte() = Nothing
            If oldWidth > 0 AndAlso oldHeight > 0 Then altPuffer = DecodeAlphaRaster(mask.PngBase64, oldWidth, oldHeight)

            ' Ohne bisherige Maske ist ein ABZIEHEN gegenstandslos - sonst entstünde aus dem ersten
            ' Radiergummi-Strich eine leere Maske, die als "es gibt eine Maske" gilt.
            If altPuffer Is Nothing Then
                If subtract Then Return False
                mask.SourceWidthPixels = stroke.SourceWidthPixels
                mask.SourceHeightPixels = stroke.SourceHeightPixels
                mask.Left = stroke.Left : mask.Top = stroke.Top
                mask.Right = stroke.Right : mask.Bottom = stroke.Bottom
                mask.PngBase64 = stroke.PngBase64
                Return True
            End If

            Dim left = If(subtract, mask.Left, Math.Min(mask.Left, stroke.Left))
            Dim top = If(subtract, mask.Top, Math.Min(mask.Top, stroke.Top))
            Dim right = If(subtract, mask.Right, Math.Max(mask.Right, stroke.Right))
            Dim bottom = If(subtract, mask.Bottom, Math.Max(mask.Bottom, stroke.Bottom))
            Dim width = right - left, height = bottom - top
            If width <= 0 OrElse height <= 0 Then Return False

            Dim target = New Byte(width * height - 1) {}
            Dim dx0 = mask.Left - left, dy0 = mask.Top - top
            For y = 0 To oldHeight - 1
                Buffer.BlockCopy(altPuffer, y * oldWidth, target, (y + dy0) * width + dx0, oldWidth)
            Next

            Dim sx0 = stroke.Left - left, sy0 = stroke.Top - top
            For y = 0 To sHeight - 1
                Dim zy = y + sy0
                If zy < 0 OrElse zy >= height Then Continue For
                Dim sRow = y * sWidth, zRow = zy * width
                For x = 0 To sWidth - 1
                    Dim zx = x + sx0
                    If zx < 0 OrElse zx >= width Then Continue For
                    Dim v = sPuffer(sRow + x)
                    If v = 0 Then Continue For
                    Dim i = zRow + zx
                    If subtract Then
                        target(i) = CByte(Math.Max(0, CInt(target(i)) - CInt(v)))
                    ElseIf v > target(i) Then
                        target(i) = v
                    End If
                Next
            Next

            Dim kodiert = EncodeAlphaRaster(target, width, height)
            If String.IsNullOrEmpty(kodiert) Then
                ' Alles weggeradiert: die Maske ist leer, nicht "unverändert".
                mask.PngBase64 = ""
                mask.Left = 0 : mask.Top = 0 : mask.Right = 0 : mask.Bottom = 0
                Return True
            End If
            mask.Left = left : mask.Top = top : mask.Right = right : mask.Bottom = bottom
            mask.PngBase64 = kodiert
            Return True
        End Function

        Public Shared Function MergeGradientBrushCorrection(mask As ImageMask, stroke As ImageMask,
                                                            subtract As Boolean) As Boolean
            If mask Is Nothing OrElse stroke Is Nothing OrElse Not mask.IsGradient Then Return False
            If stroke.Right <= stroke.Left OrElse stroke.Bottom <= stroke.Top Then Return False
            Dim sWidth = stroke.Right - stroke.Left, sHeight = stroke.Bottom - stroke.Top
            Dim sPuffer = DecodeAlphaRaster(stroke.PngBase64, sWidth, sHeight)
            If sPuffer Is Nothing Then Return False

            Dim oldWidth = mask.BrushRight - mask.BrushLeft, oldHeight = mask.BrushBottom - mask.BrushTop
            Dim altHinzu As Byte() = Nothing, altWeg As Byte() = Nothing
            If mask.HasBrushCorrection Then
                altHinzu = DecodeAlphaRaster(mask.BrushAddPngBase64, oldWidth, oldHeight)
                altWeg = DecodeAlphaRaster(mask.BrushSubtractPngBase64, oldWidth, oldHeight)
            End If
            Dim hasOld = altHinzu IsNot Nothing OrElse altWeg IsNot Nothing

            Dim left = If(hasOld, Math.Min(mask.BrushLeft, stroke.Left), stroke.Left)
            Dim top = If(hasOld, Math.Min(mask.BrushTop, stroke.Top), stroke.Top)
            Dim right = If(hasOld, Math.Max(mask.BrushRight, stroke.Right), stroke.Right)
            Dim bottom = If(hasOld, Math.Max(mask.BrushBottom, stroke.Bottom), stroke.Bottom)
            Dim width = right - left, height = bottom - top
            If width <= 0 OrElse height <= 0 Then Return False

            Dim hinzu = New Byte(width * height - 1) {}
            Dim weg = New Byte(width * height - 1) {}
            If hasOld Then
                Dim dx = mask.BrushLeft - left, dy = mask.BrushTop - top
                For y = 0 To oldHeight - 1
                    If altHinzu IsNot Nothing Then Buffer.BlockCopy(altHinzu, y * oldWidth, hinzu, (y + dy) * width + dx, oldWidth)
                    If altWeg IsNot Nothing Then Buffer.BlockCopy(altWeg, y * oldWidth, weg, (y + dy) * width + dx, oldWidth)
                Next
            End If

            Dim targetTo = If(subtract, weg, hinzu)
            Dim gegen = If(subtract, hinzu, weg)
            Dim sx0 = stroke.Left - left, sy0 = stroke.Top - top
            For y = 0 To sHeight - 1
                Dim sRow = y * sWidth, zRow = (y + sy0) * width + sx0
                For x = 0 To sWidth - 1
                    Dim v = sPuffer(sRow + x)
                    If v = 0 Then Continue For
                    Dim i = zRow + x
                    If v > targetTo(i) Then targetTo(i) = v
                    ' Gegenrichtung an dieser Stelle zuruecknehmen (siehe Zusammenfassung).
                    Dim remainder = CInt(gegen(i)) - CInt(v)
                    gegen(i) = CByte(Math.Max(0, remainder))
                Next
            Next

            mask.BrushLeft = left
            mask.BrushTop = top
            mask.BrushRight = right
            mask.BrushBottom = bottom
            mask.BrushAddPngBase64 = EncodeAlphaRaster(hinzu, width, height)
            mask.BrushSubtractPngBase64 = EncodeAlphaRaster(weg, width, height)
            If String.IsNullOrEmpty(mask.BrushAddPngBase64) AndAlso String.IsNullOrEmpty(mask.BrushSubtractPngBase64) Then
                mask.BrushLeft = 0 : mask.BrushTop = 0 : mask.BrushRight = 0 : mask.BrushBottom = 0
            End If
            Return True
        End Function

        ''' <paramref name="displayBounds"/>: wenn gesetzt, wird NUR der Quellbereich abgetastet, der
        ''' auf dieses Anzeige-Rechteck abbilden kann - fuer einen Pinselstempel ist das ein Fleck
        ''' statt des ganzen Bildes. Gemessen kostete der volle Durchlauf bei 20 MP 2,3 Sekunden JE
        ''' STRICH; mit Grenze ist er vernachlaessigbar. Ohne den Parameter bleibt alles wie bisher.
        Public Shared Function CreateSourceMaskFromSelection(adj As ImageAdjustments,
                                                             Optional name As String = "Auswahlmaske",
                                                             Optional displayBounds As SKRectI? = Nothing) As ImageMask
            If adj Is Nothing OrElse adj.SourceWidthPixels <= 0 OrElse adj.SourceHeightPixels <= 0 Then Return Nothing
            Dim displaySize = ComputeGeometryOutputSize(adj.SourceWidthPixels, adj.SourceHeightPixels, adj)
            Dim decoded As SKBitmap = Nothing
            Try
                If Not String.IsNullOrWhiteSpace(adj.SelectionMaskPngBase64) Then
                    decoded = SKBitmap.Decode(Convert.FromBase64String(adj.SelectionMaskPngBase64))
                    If decoded Is Nothing OrElse decoded.ColorType <> SKColorType.Alpha8 Then Return Nothing
                End If
                Dim dStride = If(decoded Is Nothing, 0, decoded.RowBytes)
                Dim dBuf As Byte() = Nothing
                If decoded IsNot Nothing Then
                    dBuf = New Byte(dStride * decoded.Height - 1) {}
                    Marshal.Copy(decoded.GetPixels(), dBuf, 0, dBuf.Length)
                End If
                Dim sourceW = adj.SourceWidthPixels, sourceH = adj.SourceHeightPixels
                Dim full = New Byte(sourceW * sourceH - 1) {}
                Dim left = sourceW, top = sourceH, right = 0, bottom = 0
                ' Abtastgrenzen: die vier Ecken des Anzeige-Rechtecks in den Quellraum zuruecklegen und
                ' deren Huelle nehmen. Die Abbildung ist affin (Drehung/Begradigung/Spiegelung/Zuschnitt),
                ' die Ecken spannen sie also auf; ein paar Pixel Rand fangen Rundung ab.
                Dim vonY = 0, bisY = sourceH - 1, vonX = 0, bisX = sourceW - 1
                If displayBounds.HasValue Then
                    Dim db = displayBounds.Value
                    Dim minSx = Double.MaxValue, minSy = Double.MaxValue
                    Dim maxSx = Double.MinValue, maxSy = Double.MinValue
                    Dim corners = {(CDbl(db.Left), CDbl(db.Top)), (CDbl(db.Right), CDbl(db.Top)),
                                 (CDbl(db.Left), CDbl(db.Bottom)), (CDbl(db.Right), CDbl(db.Bottom))}
                    Dim alleGetroffen = True
                    For Each corner In corners
                        Dim sp As SKPoint
                        If Not TryGeometryOutputToSourcePoint(corner.Item1, corner.Item2, sourceW, sourceH, adj, sp) Then
                            alleGetroffen = False
                            Exit For
                        End If
                        minSx = Math.Min(minSx, sp.X) : maxSx = Math.Max(maxSx, sp.X)
                        minSy = Math.Min(minSy, sp.Y) : maxSy = Math.Max(maxSy, sp.Y)
                    Next
                    ' Faellt eine Ecke aus dem Bild, wird nicht begrenzt - lieber langsam als
                    ' abgeschnitten.
                    If alleGetroffen Then
                        vonX = Math.Max(0, CInt(Math.Floor(minSx)) - 2)
                        vonY = Math.Max(0, CInt(Math.Floor(minSy)) - 2)
                        bisX = Math.Min(sourceW - 1, CInt(Math.Ceiling(maxSx)) + 2)
                        bisY = Math.Min(sourceH - 1, CInt(Math.Ceiling(maxSy)) + 2)
                    End If
                End If
                For sy = vonY To bisY
                    For sx = vonX To bisX
                        Dim dp As SKPoint
                        If Not TrySourcePointToGeometryOutput(sx + 0.5, sy + 0.5, sourceW, sourceH, adj, dp) Then Continue For
                        Dim dx = CInt(Math.Floor(dp.X)), dy = CInt(Math.Floor(dp.Y))
                        If dx < 0 OrElse dy < 0 OrElse dx >= displaySize.Width OrElse dy >= displaySize.Height Then Continue For
                        Dim alpha As Byte
                        If decoded IsNot Nothing Then
                            Dim lx = dx - adj.SelectionMaskLeft, ly = dy - adj.SelectionMaskTop
                            If lx < 0 OrElse ly < 0 OrElse lx >= decoded.Width OrElse ly >= decoded.Height Then Continue For
                            alpha = dBuf(ly * dStride + lx)
                        Else
                            Dim inside = dx >= displaySize.Width * adj.SelectionXPercent / 100.0 AndAlso
                                         dy >= displaySize.Height * adj.SelectionYPercent / 100.0 AndAlso
                                         dx < displaySize.Width * (adj.SelectionXPercent + adj.SelectionWidthPercent) / 100.0 AndAlso
                                         dy < displaySize.Height * (adj.SelectionYPercent + adj.SelectionHeightPercent) / 100.0
                            If Not inside Then Continue For
                            alpha = 255
                        End If
                        full(sy * sourceW + sx) = alpha
                        If alpha > 0 Then
                            left = Math.Min(left, sx) : top = Math.Min(top, sy)
                            right = Math.Max(right, sx + 1) : bottom = Math.Max(bottom, sy + 1)
                        End If
                    Next
                Next
                If right <= left OrElse bottom <= top Then Return Nothing

                Using cropped = New SKBitmap(right - left, bottom - top, SKColorType.Alpha8, SKAlphaType.Premul)
                    Dim cStride = cropped.RowBytes
                    Dim cBuf = New Byte(cStride * cropped.Height - 1) {}
                    For y = 0 To cropped.Height - 1
                        Buffer.BlockCopy(full, (top + y) * sourceW + left, cBuf, y * cStride, cropped.Width)
                    Next
                    Marshal.Copy(cBuf, 0, cropped.GetPixels(), cBuf.Length)
                    Using image = SKImage.FromBitmap(cropped)
                        Using data = image.Encode(SKEncodedImageFormat.Png, FastPngCompressionQuality)
                            Return New ImageMask With {
                                .Name = If(String.IsNullOrWhiteSpace(name), LocalizationService.T("Auswahlmaske"), name),
                                .SourceWidthPixels = sourceW, .SourceHeightPixels = sourceH,
                                .Left = left, .Top = top, .Right = right, .Bottom = bottom,
                                .PngBase64 = Convert.ToBase64String(data.ToArray()),
                                .FeatherPixels = adj.SelectionFeatherPixels
                            }
                        End Using
                    End Using
                End Using
            Catch
                Return Nothing
            Finally
                decoded?.Dispose()
            End Try
        End Function

        ''' <summary>Inverse zu CreateSourceMaskFromSelection: projiziert eine Ebenen-Maske (ImageMask,
        ''' Quellraum) in den ANZEIGE-Bildraum und liefert eine Alpha8-Maske + Rechteck für die editierbare
        ''' Auswahlmaske (_selectionMask). Damit lässt sich eine Ebenen-Maske im Masken-Pinsel wieder als
        ''' rotes Overlay anzeigen und per +/- ändern. Inverted wird beim Sampeln aufgelöst (die editierbare
        ''' Kopie ist danach nicht invertiert); die weiche Kante (FeatherPixels) bleibt Sache der Ebene und
        ''' wird hier NICHT eingerechnet - der Pinsel bearbeitet die harte Form, der Feather wirkt beim
        ''' Rendern (BuildPersistentMaskForOutput).</summary>
        Public Shared Function BuildSelectionMaskFromLayerMask(mask As ImageMask, adj As ImageAdjustments,
                                                               ByRef rectPx As SKRectI) As SKBitmap
            rectPx = SKRectI.Empty
            If mask Is Nothing OrElse adj Is Nothing OrElse adj.SourceWidthPixels <= 0 OrElse adj.SourceHeightPixels <= 0 Then Return Nothing
            Dim displaySize = ComputeGeometryOutputSize(adj.SourceWidthPixels, adj.SourceHeightPixels, adj)
            If displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return Nothing

            Dim decoded As SKBitmap = Nothing
            Try
                If String.IsNullOrWhiteSpace(mask.PngBase64) Then Return Nothing
                decoded = SKBitmap.Decode(Convert.FromBase64String(mask.PngBase64))
                If decoded Is Nothing OrElse decoded.ColorType <> SKColorType.Alpha8 Then Return Nothing
                Dim mStride = decoded.RowBytes
                Dim mBuf = New Byte(mStride * decoded.Height - 1) {}
                Marshal.Copy(decoded.GetPixels(), mBuf, 0, mBuf.Length)

                Dim dw = displaySize.Width, dh = displaySize.Height
                Dim sourceW = adj.SourceWidthPixels, sourceH = adj.SourceHeightPixels
                Dim full = New Byte(dw * dh - 1) {}
                Dim left = dw, top = dh, right = 0, bottom = 0
                For dy = 0 To dh - 1
                    For dx = 0 To dw - 1
                        Dim sp As SKPoint
                        If Not TryGeometryOutputToSourcePoint(dx + 0.5, dy + 0.5, sourceW, sourceH, adj, sp) Then Continue For
                        Dim mx = CInt(Math.Floor(sp.X)) - mask.Left
                        Dim my = CInt(Math.Floor(sp.Y)) - mask.Top
                        Dim alpha As Byte = 0
                        If mx >= 0 AndAlso my >= 0 AndAlso mx < decoded.Width AndAlso my < decoded.Height Then
                            alpha = mBuf(my * mStride + mx)
                        End If
                        If mask.Inverted Then alpha = CByte(255 - alpha)
                        If alpha = 0 Then Continue For
                        full(dy * dw + dx) = alpha
                        left = Math.Min(left, dx) : top = Math.Min(top, dy)
                        right = Math.Max(right, dx + 1) : bottom = Math.Max(bottom, dy + 1)
                    Next
                Next
                If right <= left OrElse bottom <= top Then Return Nothing

                Dim result = New SKBitmap(right - left, bottom - top, SKColorType.Alpha8, SKAlphaType.Premul)
                Dim cStride = result.RowBytes
                Dim cBuf = New Byte(cStride * result.Height - 1) {}
                For y = 0 To result.Height - 1
                    Buffer.BlockCopy(full, (top + y) * dw + left, cBuf, y * cStride, result.Width)
                Next
                Marshal.Copy(cBuf, 0, result.GetPixels(), cBuf.Length)
                rectPx = New SKRectI(left, top, right, bottom)
                Return result
            Catch
                Return Nothing
            Finally
                decoded?.Dispose()
            End Try
        End Function

        ''' <summary>Baut eine ImageMask (Quellraum) aus einem vollbildgroßen Alpha-Puffer: schneidet auf
        ''' die Bounding-Box der gesetzten Pixel zu und kodiert Alpha8-PNG. Nothing, wenn leer.</summary>
        Private Shared Function EncodeSourceMaskFromAlpha(full As Byte(), sourceW As Integer, sourceH As Integer, name As String) As ImageMask
            Dim left = sourceW, top = sourceH, right = 0, bottom = 0
            For sy = 0 To sourceH - 1
                Dim row = sy * sourceW
                For sx = 0 To sourceW - 1
                    If full(row + sx) > 0 Then
                        If sx < left Then left = sx
                        If sy < top Then top = sy
                        If sx + 1 > right Then right = sx + 1
                        If sy + 1 > bottom Then bottom = sy + 1
                    End If
                Next
            Next
            If right <= left OrElse bottom <= top Then Return Nothing
            Using cropped = New SKBitmap(right - left, bottom - top, SKColorType.Alpha8, SKAlphaType.Premul)
                Dim cStride = cropped.RowBytes
                Dim cBuf = New Byte(cStride * cropped.Height - 1) {}
                For y = 0 To cropped.Height - 1
                    Buffer.BlockCopy(full, (top + y) * sourceW + left, cBuf, y * cStride, cropped.Width)
                Next
                Marshal.Copy(cBuf, 0, cropped.GetPixels(), cBuf.Length)
                Using image = SKImage.FromBitmap(cropped)
                    Using data = image.Encode(SKEncodedImageFormat.Png, FastPngCompressionQuality)
                        Return New ImageMask With {
                            .Name = If(String.IsNullOrWhiteSpace(name), LocalizationService.T("Auswahlmaske"), name),
                            .SourceWidthPixels = sourceW, .SourceHeightPixels = sourceH,
                            .Left = left, .Top = top, .Right = right, .Bottom = bottom,
                            .PngBase64 = Convert.ToBase64String(data.ToArray()),
                            .FeatherPixels = 0
                        }
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Rastert eine LINEARE Verlaufsmaske (crs "Mask/Gradient") in den Quellraum.
        ''' Zero/Full sind Bruchkoordinaten des Bildes (0..1, dürfen außerhalb liegen). Die Maske ist 0 an
        ''' der Zero-Linie und rampt entlang der Senkrechten linear auf maskValue an der Full-Linie (danach
        ''' konstant).</summary>
        Public Shared Function BuildLinearGradientMask(sourceW As Integer, sourceH As Integer,
                                                       zeroX As Double, zeroY As Double, fullX As Double, fullY As Double,
                                                       maskValue As Double, name As String) As ImageMask
            If sourceW <= 0 OrElse sourceH <= 0 Then Return Nothing
            Dim dirX = fullX - zeroX, dirY = fullY - zeroY
            Dim len2 = dirX * dirX + dirY * dirY
            If len2 <= 0.0000001 Then Return Nothing
            Dim mv = Math.Max(0.0, Math.Min(1.0, maskValue))
            Dim full = New Byte(sourceW * sourceH - 1) {}
            For sy = 0 To sourceH - 1
                Dim fy = (sy + 0.5) / sourceH
                Dim row = sy * sourceW
                For sx = 0 To sourceW - 1
                    Dim fx = (sx + 0.5) / sourceW
                    Dim t = ((fx - zeroX) * dirX + (fy - zeroY) * dirY) / len2
                    If t <= 0.0 Then Continue For
                    If t > 1.0 Then t = 1.0
                    full(row + sx) = CByte(Math.Round(t * mv * 255.0))
                Next
            Next
            Return EncodeSourceMaskFromAlpha(full, sourceW, sourceH, name)
        End Function

        ''' <summary>Rastert eine RADIALE Verlaufsmaske (crs "Mask/CircularGradient") in den
        ''' Quellraum. Top/Left/Bottom/Right in Bruchkoordinaten; angleDeg Grad; feather 0..100 (Anteil des
        ''' Radius mit weichem Auslauf); Flipped=True → Wirkung INNEN, sonst AUSSEN (der uebliche Standard).
        ''' Roundness/Midpoint werden in v1 vereinfacht (reine Ellipse, Halbwert = 1-feather).</summary>
        Public Shared Function BuildRadialGradientMask(sourceW As Integer, sourceH As Integer,
                                                       top As Double, left As Double, bottom As Double, right As Double,
                                                       angleDeg As Double, feather As Double, flipped As Boolean,
                                                       maskValue As Double, name As String) As ImageMask
            If sourceW <= 0 OrElse sourceH <= 0 Then Return Nothing
            Dim cx = (left + right) / 2.0, cy = (top + bottom) / 2.0
            Dim rx = Math.Max(0.0001, (right - left) / 2.0)
            Dim ry = Math.Max(0.0001, (bottom - top) / 2.0)
            Dim ang = -angleDeg * Math.PI / 180.0
            Dim cosA = Math.Cos(ang), sinA = Math.Sin(ang)
            Dim mv = Math.Max(0.0, Math.Min(1.0, maskValue))
            Dim inner = 1.0 - Math.Max(0.0, Math.Min(1.0, feather / 100.0))
            Dim full = New Byte(sourceW * sourceH - 1) {}
            For sy = 0 To sourceH - 1
                Dim fy = (sy + 0.5) / sourceH
                Dim row = sy * sourceW
                For sx = 0 To sourceW - 1
                    Dim fx = (sx + 0.5) / sourceW
                    Dim ddx = fx - cx, ddy = fy - cy
                    Dim ex = (ddx * cosA - ddy * sinA) / rx
                    Dim ey = (ddx * sinA + ddy * cosA) / ry
                    Dim d = Math.Sqrt(ex * ex + ey * ey)
                    Dim cover As Double
                    If d <= inner Then
                        cover = 1.0
                    ElseIf d >= 1.0 Then
                        cover = 0.0
                    Else
                        Dim tt = (1.0 - d) / Math.Max(0.0001, 1.0 - inner)
                        cover = tt * tt * (3.0 - 2.0 * tt)
                    End If
                    Dim m = If(flipped, cover, 1.0 - cover)
                    If m <= 0.0 Then Continue For
                    full(row + sx) = CByte(Math.Round(Math.Min(1.0, m) * mv * 255.0))
                Next
            Next
            Return EncodeSourceMaskFromAlpha(full, sourceW, sourceH, name)
        End Function

        ''' <summary>Baut aus der aktiven Auswahl eine Alpha8-Maske in der Größe des verarbeiteten Bildes
        ''' (<paramref name="targetW"/>×<paramref name="targetH"/>). Unregelmäßige Auswahlen kommen aus der
        ''' gespeicherten Alpha8-Maske (Display-Pixel, per Nearest-Sampling auf die Zielgröße gebracht),
        ''' Rechtecke direkt aus den Prozentwerten. Liefert Nothing, wenn keine nutzbare Auswahl vorliegt.</summary>
        ''' <summary>Weiche Kante: zeichnet die Alpha8-Maske mit Weichzeichner in eine neue Maske gleicher
        ''' Größe. Skias Sigma entspricht etwa dem halben Radius. Nothing, wenn nichts zu tun ist.</summary>
        Private Shared Function BlurAlphaMask(mask As SKBitmap, radiusPixels As Single) As SKBitmap
            If mask Is Nothing OrElse radiusPixels <= 0.05F Then Return Nothing
            Dim sigma = Math.Max(0.1F, radiusPixels * 0.5F)
            Dim result = New SKBitmap(mask.Width, mask.Height, SKColorType.Alpha8, SKAlphaType.Premul)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                Using paint = New SKPaint()
                    paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma)
                    paint.Color = SKColors.White
                    canvas.DrawBitmap(mask, 0, 0, paint)
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Liefert eine weichgezeichnete Kopie der Maske - um den Radius nach AUSSEN erweitert,
        ''' damit die Kante symmetrisch ausläuft und nicht am Maskenrand abgeschnitten wird (ein Lasso berührt
        ''' seinen Rahmen per Definition). <paramref name="expandedRect"/> ist das dazugehörige, ebenfalls
        ''' erweiterte Bildrechteck. Nothing, wenn keine weiche Kante gewünscht ist.</summary>
        Public Shared Function BuildFeatheredMask(mask As SKBitmap, rect As SKRectI, radiusPixels As Single,
                                                  ByRef expandedRect As SKRectI) As SKBitmap
            expandedRect = rect
            If mask Is Nothing OrElse radiusPixels <= 0.05F Then Return Nothing

            Dim pad = Math.Max(1, CInt(Math.Ceiling(radiusPixels * 2.0F)))
            Dim padded = New SKBitmap(mask.Width + 2 * pad, mask.Height + 2 * pad, SKColorType.Alpha8, SKAlphaType.Premul)
            Using canvas = New SKCanvas(padded)
                canvas.Clear(SKColors.Transparent)
                canvas.DrawBitmap(mask, pad, pad)
            End Using

            Dim blurred = BlurAlphaMask(padded, radiusPixels)
            padded.Dispose()
            If blurred Is Nothing Then Return Nothing

            expandedRect = New SKRectI(rect.Left - pad, rect.Top - pad, rect.Right + pad, rect.Bottom + pad)
            Return blurred
        End Function

        ''' <summary>Die Skopus-Maske in Zielgröße, mit weicher Kante falls eingestellt. Hier darf die Kante
        ''' frei nach außen auslaufen: die Maske hat bereits die volle Bildgröße, es wird nichts abgeschnitten.
        ''' Der Radius wird auf die Zielgröße mitskaliert - sonst wäre die Kante in der (kleineren) Vorschau
        ''' breiter als im gespeicherten Bild.</summary>
        Private Shared Function BuildSelectionScopeMask(adj As ImageAdjustments, targetW As Integer, targetH As Integer) As SKBitmap
            Dim mask = BuildSelectionScopeMaskCore(adj, targetW, targetH)
            ' Masken-Pinsel trägt seine weiche Kante bereits in den Alpha-Werten (siehe
            ' SelectionMaskSoftBaked) - dann NICHT erneut weichzeichnen, sonst doppelt weiche Kante.
            If mask Is Nothing OrElse adj.SelectionMaskSoftBaked OrElse adj.SelectionFeatherPixels <= 0.05F Then Return mask

            Dim referenceSize = ImageGeometryMapper.DisplaySize(adj.SourceWidthPixels, adj.SourceHeightPixels, adj.RotationDegrees)
            Dim scaleX = If(referenceSize.Width > 0, targetW / CSng(referenceSize.Width), 1.0F)
            Dim scaleY = If(referenceSize.Height > 0, targetH / CSng(referenceSize.Height), 1.0F)
            Dim scale = (scaleX + scaleY) / 2.0F
            Dim blurred = BlurAlphaMask(mask, adj.SelectionFeatherPixels * scale)
            If blurred Is Nothing Then Return mask
            mask.Dispose()
            Return blurred
        End Function

        Private Shared Function BuildSelectionScopeMaskCore(adj As ImageAdjustments, targetW As Integer, targetH As Integer) As SKBitmap
            If targetW <= 0 OrElse targetH <= 0 Then Return Nothing
            ' Maskenpixel und Masken-Rechteck werden vom Editor im sichtbaren Display-Raum gespeichert.
            ' Bei 90/270 Grad ist dessen Breite die Source-Hoehe; eine Skalierung ueber SourceWidth
            ' verschob bzw. leerte Lasso-/Zauberstabmasken in der Vorschau.
            Dim referenceSize = ImageGeometryMapper.DisplaySize(adj.SourceWidthPixels, adj.SourceHeightPixels, adj.RotationDegrees)
            Dim scaleX = If(referenceSize.Width > 0, targetW / CDbl(referenceSize.Width), 1.0)
            Dim scaleY = If(referenceSize.Height > 0, targetH / CDbl(referenceSize.Height), 1.0)

            If Not String.IsNullOrEmpty(adj.SelectionMaskPngBase64) Then
                Dim boundsW = adj.SelectionMaskRight - adj.SelectionMaskLeft
                Dim boundsH = adj.SelectionMaskBottom - adj.SelectionMaskTop
                If boundsW <= 0 OrElse boundsH <= 0 Then Return Nothing
                Try
                    Dim raw = Convert.FromBase64String(adj.SelectionMaskPngBase64)
                    Using decoded = SKBitmap.Decode(raw)
                        ' Empirisch: SKBitmap.Decode dieser PNGs liefert Alpha8 (1 Byte/Pixel).
                        If decoded Is Nothing OrElse decoded.ColorType <> SKColorType.Alpha8 Then Return Nothing
                        Dim dStride = decoded.RowBytes
                        Dim dBuf = New Byte(dStride * decoded.Height - 1) {}
                        Marshal.Copy(decoded.GetPixels(), dBuf, 0, dBuf.Length)
                        Dim dW = decoded.Width, dH = decoded.Height

                        Dim mask = New SKBitmap(targetW, targetH, SKColorType.Alpha8, SKAlphaType.Premul)
                        Dim mStride = mask.RowBytes
                        Dim mBuf = New Byte(mStride * targetH - 1) {}
                        For ty = 0 To targetH - 1
                            Dim baseY = If(scaleY > 0, CInt(ty / scaleY), ty)
                            Dim ly = baseY - adj.SelectionMaskTop
                            If ly < 0 OrElse ly >= dH Then Continue For
                            Dim mRow = ty * mStride, dRow = ly * dStride
                            For tx = 0 To targetW - 1
                                Dim baseX = If(scaleX > 0, CInt(tx / scaleX), tx)
                                Dim lx = baseX - adj.SelectionMaskLeft
                                If lx < 0 OrElse lx >= dW Then Continue For
                                mBuf(mRow + tx) = dBuf(dRow + lx)
                            Next
                        Next
                        Marshal.Copy(mBuf, 0, mask.GetPixels(), mBuf.Length)
                        Return mask
                    End Using
                Catch
                    Return Nothing
                End Try
            End If

            ' Rechteckdaten können entweder für den expliziten Legacy-Skopus ODER zum Einfrieren der
            ' momentan aktiven UI-Auswahl angefordert werden.
            If SelectionScopeIsEnabled(adj) OrElse adj.HasActiveSelection Then
                Dim left = Clamp3(CInt(Math.Round(targetW * adj.SelectionXPercent / 100.0)), 0, targetW)
                Dim top = Clamp3(CInt(Math.Round(targetH * adj.SelectionYPercent / 100.0)), 0, targetH)
                Dim right = Clamp3(CInt(Math.Round(targetW * (adj.SelectionXPercent + adj.SelectionWidthPercent) / 100.0)), 0, targetW)
                Dim bottom = Clamp3(CInt(Math.Round(targetH * (adj.SelectionYPercent + adj.SelectionHeightPercent) / 100.0)), 0, targetH)
                If right <= left OrElse bottom <= top Then Return Nothing
                Dim mask = New SKBitmap(targetW, targetH, SKColorType.Alpha8, SKAlphaType.Premul)
                Dim mStride = mask.RowBytes
                Dim mBuf = New Byte(mStride * targetH - 1) {}
                For y = top To bottom - 1
                    Dim mRow = y * mStride
                    For x = left To right - 1
                        mBuf(mRow + x) = 255
                    Next
                Next
                Marshal.Copy(mBuf, 0, mask.GetPixels(), mBuf.Length)
                Return mask
            End If

            Return Nothing
        End Function

        ''' <summary>Nur der explizite Rezeptzustand begrenzt globale Anpassungen. HasActiveSelection
        ''' steuert ausschließlich die Editor-Überlagerung und darf das Rendering nie verändern.</summary>
        Private Shared Function SelectionScopeIsEnabled(adj As ImageAdjustments) As Boolean
            Return adj IsNot Nothing AndAlso adj.SelectionScopeEnabled
        End Function

        Private Shared Function Clamp3(value As Integer, lo As Integer, hi As Integer) As Integer
            Return Math.Max(lo, Math.Min(hi, value))
        End Function

        ''' <summary>Mischt <paramref name="adjusted"/> (angepasst) über <paramref name="baseline"/> (unverändert)
        ''' anhand der Alpha8-<paramref name="mask"/>: out = baseline·(255−m) + adjusted·m. Alle drei müssen
        ''' dieselben Maße haben. Fällt bei ungeeignetem Farbformat auf eine Kopie der BASELINE zurück.</summary>
        Private Shared Function CompositeSelectionScoped(baseline As SKBitmap, adjusted As SKBitmap, mask As SKBitmap) As SKBitmap
            Dim w = adjusted.Width, h = adjusted.Height
            Dim aStride As Integer, bStride As Integer
            Dim aBuf As Byte() = Nothing, bBuf As Byte() = Nothing
            If Not TryBorrowBgraBuffer(adjusted, aBuf, aStride) OrElse Not TryBorrowBgraBuffer(baseline, bBuf, bStride) Then
                ' Fallback-Richtung BASELINE, nicht adjusted: eine Kopie von
                ' adjusted machte die auswahl-skopierte Anpassung still GLOBAL - exakt der Bruch
                ' der Garantie aus ProcessBitmapBase ("darf niemals still auf eine globale
                ' Anpassung zurueckfallen"; der Masken-Decode-Fehlerfall dort nimmt ebenfalls die
                ' Baseline). Loggen statt schweigen - stiller Farbtyp-Rueckfall ist eine bekannte
                ' Fallenklasse dieses Projekts.
                DiagnosticLogService.LogAlways("ImageProcessor.SelectionScope",
                    $"composite skipped: non-BGRA pipeline bitmap adjusted={adjusted.ColorType} baseline={baseline.ColorType} - returning baseline")
                Return CloneBitmap(baseline)
            End If
            Dim mStride = mask.RowBytes
            Dim mBuf = New Byte(mStride * mask.Height - 1) {}
            Marshal.Copy(mask.GetPixels(), mBuf, 0, mBuf.Length)

            Dim outBuf = New Byte(aBuf.Length - 1) {}
            Dim maskW = mask.Width, maskH = mask.Height
            ForEachRow(w, h, Sub(y)
                                 Dim aRow = y * aStride, bRow = y * bStride, mRow = y * mStride
                                 For x = 0 To w - 1
                                     Dim ao = aRow + x * 4, bo = bRow + x * 4
                                     Dim m = If(x < maskW AndAlso y < maskH, CInt(mBuf(mRow + x)), 0)
                                     If m = 0 Then
                                         outBuf(ao) = bBuf(bo) : outBuf(ao + 1) = bBuf(bo + 1) : outBuf(ao + 2) = bBuf(bo + 2) : outBuf(ao + 3) = bBuf(bo + 3)
                                     ElseIf m >= 255 Then
                                         outBuf(ao) = aBuf(ao) : outBuf(ao + 1) = aBuf(ao + 1) : outBuf(ao + 2) = aBuf(ao + 2) : outBuf(ao + 3) = aBuf(ao + 3)
                                     Else
                                         Dim inv = 255 - m
                                         outBuf(ao) = CByte((CInt(aBuf(ao)) * m + CInt(bBuf(bo)) * inv) \ 255)
                                         outBuf(ao + 1) = CByte((CInt(aBuf(ao + 1)) * m + CInt(bBuf(bo + 1)) * inv) \ 255)
                                         outBuf(ao + 2) = CByte((CInt(aBuf(ao + 2)) * m + CInt(bBuf(bo + 2)) * inv) \ 255)
                                         outBuf(ao + 3) = CByte((CInt(aBuf(ao + 3)) * m + CInt(bBuf(bo + 3)) * inv) \ 255)
                                     End If
                                 Next
                             End Sub)

            Dim result = New SKBitmap(w, h, adjusted.ColorType, adjusted.AlphaType)
            CommitBgraBuffer(result, outBuf)
            Return result
        End Function

        ''' <summary>Gibt das gecachte Basis-Bitmap frei. Muss aufgerufen werden, sobald die zugehörige
        ''' Quelle verschwindet (Bildwechsel, Editor verlassen) - der Cache ist statisch und hielte sonst
        ''' ein Bitmap in Vorschauauflösung sowie eine Referenz auf das bereits disposte Quell-SKBitmap
        ''' bis zum Programmende fest.</summary>
        Public Shared Sub ClearBaseCache()
            SyncLock _baseCacheLock
                _baseCacheBitmap?.Dispose()
                _baseCacheBitmap = Nothing
                _baseCacheKey = Nothing
                _baseCacheSourceRef = Nothing
            End SyncLock
        End Sub

        ' Liefert die gecachte Basis (Bild vor den Objekten) wenn sich seit dem letzten Aufruf nur
        ' die Objekte geändert haben, sonst wird die Pipeline neu berechnet und der Cache erneuert.
        Private Shared Function GetOrComputeBaseLocked(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim key = ComputeBaseKey(adj)
            If Object.ReferenceEquals(_baseCacheSourceRef, source) AndAlso
               String.Equals(_baseCacheKey, key, StringComparison.Ordinal) AndAlso
               _baseCacheBitmap IsNot Nothing Then
                Return _baseCacheBitmap
            End If

            Dim computed = ProcessBitmapBase(source, adj)
            _baseCacheBitmap?.Dispose()
            _baseCacheBitmap = computed
            _baseCacheKey = key
            _baseCacheSourceRef = source
            Return computed
        End Function

        ' Signatur aller Anpassungen AUSSER Annotations - solange sie sich nicht ändert, kann die
        ' gecachte Basis wiederverwendet werden. Friend: der Editor nutzt sie auch als
        ' Gültigkeitsstempel der vorgewärmten Retusche-Live-Puffer.
        ''' <summary>Kulturunabhaengige Textform eines Schluesselbestandteils. String.Join ruft sonst
        ''' das implizite ToString auf, und das formatiert Single/Double nach der aktuellen Kultur -
        ''' "0,5" hier und "0.5" dort. Der Schluessel ist zwar nur sitzungsintern, aber ein
        ''' Kulturwechsel zur Laufzeit (Spracheinstellung) wuerde den Cache stillschweigend
        ''' entwerten oder - schlimmer - zwei verschiedene Einstellungen gleich benennen.</summary>
        Private Shared Function KeyPart(value As Object) As String
            If value Is Nothing Then Return ""
            Dim f = TryCast(value, IFormattable)
            If f IsNot Nothing Then Return f.ToString(Nothing, Globalization.CultureInfo.InvariantCulture)
            Return value.ToString()
        End Function

        Friend Shared Function ComputeBaseKey(adj As ImageAdjustments) As String
            ' HasActiveSelection und ihre editierbare Display-Maske sind reine UI-Zustände. Seit
            ' Auswahlkorrekturen als persistente MaskedAdjustmentLayers gespeichert werden, wirken
            ' sie nur noch dann direkt auf die globale Pixelpipeline, wenn der explizite Legacy-
            ' SelectionScopeEnabled gesetzt ist. Die UI-Auswahl trotzdem in den Base-Key aufzunehmen
            ' machte den Cache unmittelbar nach jedem Zauberstab-/Lasso-Klick scheinbar veraltet,
            ' obwohl sich kein Bildpixel geändert hatte. Ein danach platziertes Objekt konnte deshalb
            ' nie als Region gerendert werden und war nur im Selektions-Ghost zu sehen.
            Dim selectionScopeKey = If(adj.SelectionScopeEnabled,
                String.Join(",", New Object() {
                    adj.SelectionXPercent, adj.SelectionYPercent,
                    adj.SelectionWidthPercent, adj.SelectionHeightPercent, adj.SelectionShapeMode,
                    adj.SelectionMaskLeft, adj.SelectionMaskTop, adj.SelectionMaskRight, adj.SelectionMaskBottom,
                    SelectionMaskFingerprint(adj.SelectionMaskPngBase64), adj.SelectionFeatherPixels
                }.Select(AddressOf KeyPart)), "")
            ' JEDES Feld, das ProcessBitmapBase liest, MUSS hier stehen: die
            ' Untergruppen-Regler SharpenRadius/SharpenDetail, NoiseReductionDetail, GrainSize/
            ' GrainFrequency und VignetteStyle fehlten - wer NUR so einen Regler bewegte, bekam
            ' das gecachte alte Bild zurueck ("Regler macht nix"), und Patch-Renderer
            ' komponierten auf veralteter Basis. Das gilt auch fuer APP-WEITE Render-Schalter,
            ' nicht nur adj-Felder - wer einen einfuehrt, muss ihn hier eintragen.
            Return String.Join("|", New Object() {
                adj.Exposure, adj.Brightness, adj.Contrast, adj.Saturation, adj.Highlights, adj.ShadowsLevel,
                adj.Whites, adj.Blacks, adj.Temperature, adj.Tint, adj.Sharpness, adj.SharpenRadius, adj.SharpenDetail,
                adj.SharpenMasking,
                adj.NoiseReduction, adj.NoiseReductionMethod, adj.NoiseReductionDetail, adj.ColorNoiseReduction,
                adj.FarbrauschGrob, adj.ColorNoiseCoarseScale,
                adj.ColorNoiseAdd,
                adj.DustScratches, adj.Haze, adj.AddNoise, adj.[Structure], adj.Glow,
                adj.PerspectiveHorizontal, adj.PerspectiveVertical, adj.PerspectiveAspect, adj.PerspectiveScale,
                adj.PerspectiveCorner0X, adj.PerspectiveCorner0Y, adj.PerspectiveCorner1X, adj.PerspectiveCorner1Y,
                adj.PerspectiveCorner2X, adj.PerspectiveCorner2Y, adj.PerspectiveCorner3X, adj.PerspectiveCorner3Y,
adj.CalibrationRedHue, adj.CalibrationRedSaturation,
                adj.CalibrationGreenHue, adj.CalibrationGreenSaturation,
                adj.CalibrationBlueHue, adj.CalibrationBlueSaturation, adj.CalibrationShadowTint,
                                adj.Vibrance, adj.Vignette, adj.VignetteTransition, adj.VignetteRoundness, adj.VignetteFeather,
                adj.VignetteCenterX, adj.VignetteCenterY, adj.VignetteStyle,
                adj.Grain, adj.GrainSize, adj.GrainFrequency, adj.Clarity,
                adj.NegativeEnabled, adj.NegativeMonochrome, adj.NegativeBaseColor, adj.NegativeDensityColor, adj.NegativeGamma,
                adj.CurveRgbPoints, adj.CurveRedPoints, adj.CurveGreenPoints, adj.CurveBluePoints, adj.CurveLuminancePoints,
                adj.RedHue, adj.RedSaturation, adj.RedLuminance, adj.OrangeHue, adj.OrangeSaturation, adj.OrangeLuminance,
                adj.YellowHue, adj.YellowSaturation, adj.YellowLuminance, adj.GreenHue, adj.GreenSaturation, adj.GreenLuminance,
                adj.AquaHue, adj.AquaSaturation, adj.AquaLuminance, adj.BlueHue, adj.BlueSaturation, adj.BlueLuminance,
                adj.PurpleHue, adj.PurpleSaturation, adj.PurpleLuminance, adj.MagentaHue, adj.MagentaSaturation, adj.MagentaLuminance,
                adj.ColorGradeShadowHue, adj.ColorGradeShadowSaturation, adj.ColorGradeShadowLuminance,
                adj.ColorGradeMidtoneHue, adj.ColorGradeMidtoneSaturation, adj.ColorGradeMidtoneLuminance,
                adj.ColorGradeHighlightHue, adj.ColorGradeHighlightSaturation, adj.ColorGradeHighlightLuminance,
                adj.ColorGradeGlobalHue, adj.ColorGradeGlobalSaturation, adj.ColorGradeGlobalLuminance,
                adj.ColorGradeBalance, adj.ColorGradeBlending,
                adj.RotationDegrees, adj.StraightenDegrees, adj.StraightenExpandCanvas, adj.FlipHorizontal, adj.FlipVertical,
                adj.CropLeftPercent, adj.CropTopPercent, adj.CropRightPercent, adj.CropBottomPercent,
                adj.ResizeWidth, adj.ResizeHeight, adj.LockResizeAspect, adj.ResizeFitInsideBox, adj.ResizeScalePercent, adj.NoResizeUpscale, adj.ResizeInterpolation,
                adj.CanvasWidth, adj.CanvasHeight, adj.LockCanvasAspect, adj.CanvasAnchor, adj.CanvasBackgroundColor,
                adj.FilterPreset, adj.FilterStrength, adj.LutPath, adj.LutStrength,
                adj.SelectionScopeEnabled, selectionScopeKey,
                adj.GlobalAdjustmentsHidden,
                PersistentMasksFingerprint(adj),
                adj.WorkingImageVersion
            }.Select(AddressOf KeyPart))
        End Function

        ' Verlaufsmasken tragen ihre Geometrie statt eines PNG - sie MUSS in den Schluessel,
        ' sonst bliebe die Vorschau beim Ziehen der Griffe stehen (der Cache gaebe die alte
        ' Basis zurueck, und das Werkzeug "macht nichts").
        ''' Die Pinselkorrektur eines Verlaufs gehoert MIT in den Fingerabdruck - ohne sie bliebe die
        ''' Vorschau beim Malen stehen, weil der Cache den unkorrigierten Verlauf zurueckgaebe.
        Private Shared Function PersistentMasksFingerprint(adj As ImageAdjustments) As String
            Dim masks = If(adj.Masks, New List(Of ImageMask)()).
                Where(Function(m) m IsNot Nothing).
                Select(Function(m) String.Join(":", m.Id, m.SourceWidthPixels, m.SourceHeightPixels,
                                               m.Left, m.Top, m.Right, m.Bottom, m.FeatherPixels,
                                               m.Inverted, SelectionMaskFingerprint(m.PngBase64),
                                               m.Kind, KeyPart(m.GradientStartXPercent), KeyPart(m.GradientStartYPercent),
                                               KeyPart(m.GradientEndXPercent), KeyPart(m.GradientEndYPercent),
                                               KeyPart(m.GradientRadiusRatio), KeyPart(m.GradientFeatherPercent),
                                               m.BrushLeft, m.BrushTop, m.BrushRight, m.BrushBottom,
                                               SelectionMaskFingerprint(m.BrushAddPngBase64),
                                               SelectionMaskFingerprint(m.BrushSubtractPngBase64)))
            ' Ebenen IM OBJEKTSTAPEL gehören NICHT in den Basis-Schlüssel: die Basis-Stufe überspringt
            ' sie (ApplyMaskedAdjustmentLayers ohne onlyStackedAboveId), sie wirken erst im
            ' Objektdurchlauf. Stünden sie hier, würde jede Änderung an ihnen den Basis-Cache
            ' wegwerfen - und der Zug, der sie für die Live-Darstellung kurz weglässt, würde ihn
            ' gleich zweimal neu aufbauen.
            Dim layers = If(adj.MaskedAdjustmentLayers, New List(Of MaskedAdjustmentLayer)()).
                Where(Function(l) l IsNot Nothing AndAlso String.IsNullOrEmpty(l.StackAboveAnnotationId)).
                Select(Function(l)
                           Dim values = If(l.Adjustments, New ImageAdjustments())
                           Dim pixelValues = ImageAdjustments.PixelAdjustmentProperties().
                               Select(Function(p) KeyPart(p.GetValue(values)))
                           ' IsMaskLayer und die DEKLARATIVE Füllung MÜSSEN mit in den Schlüssel: beide
                           ' liest ApplyMaskedAdjustmentLayers (Art der Füll-Wirkung bzw. Farbe/Verlauf).
                           ' Ohne sie lieferte der Basis-Cache nach einem erneuten Füllen dasselbe Bild
                           ' zurück - die ERSTE Füllung blieb sichtbar und liess sich nie ersetzen
                           ' (exakt die im Kopf von ComputeBaseKey beschriebene
                           ' Fehlerklasse "Regler macht nix").
                           ' Die Sichtbarkeit der GRUPPE gehört mit hinein: liegt die Korrektur in einer
                           ' Gruppe, entscheidet deren Auge mit darüber, ob sie gerendert wird
                           ' (IsMaskedLayerRenderVisible). Ohne sie war der Schlüssel vor und nach dem
                           ' Umschalten identisch - der Vollrender bekam die gecachte Basis zurück und
                           ' die Korrektur blieb sichtbar.
                           Dim groupVisible = adj.IsMaskedLayerRenderVisible(l)
                           Return String.Join(":", l.Id, l.MaskId, l.IsVisible, groupVisible, l.Opacity, l.GroupId,
                                              l.StackAboveAnnotationId,
                                              l.IsMaskLayer, l.FillKind, l.FillColor, l.FillColor2,
                                              KeyPart(l.FillAngle), l.FillInverted,
                                              String.Join(",", pixelValues))
                       End Function)
            Return String.Join(";", masks) & "|" & String.Join(";", layers)
        End Function

        ''' <summary>Stabiler Fingerabdruck der Auswahlmaske für den Basis-Cache-Schlüssel.
        '''
        ''' Vorher stand hier nur die LÄNGE der Base64-Zeichenkette. Zwei verschiedene Masken mit
        ''' gleicher Bounding-Box und gleicher Länge bekamen damit denselben Cache-Schlüssel - die
        ''' zweite Vorschau lief dann mit dem selektiv gerechneten Ergebnis der ERSTEN Maske. Das ist
        ''' ein echter Bildfehler, kein Performance-Thema.
        '''
        ''' Und es war nicht selten: an 63 lasso-artigen Masken mit identischer Bounding-Box gemessen
        ''' teilten sich 90,5 % ihre Base64-Länge mit einer anderen Maske - PNG-Kompression
        ''' quantisiert die Längen stark (drei verschiedene Masken lagen auf exakt 1092 Zeichen).
        '''
        ''' Gemerkt wird der letzte Wert: ComputeBaseKey läuft bei jedem Vorschaubild, die Maske
        ''' ändert sich beim Ziehen an einem Regler aber nicht. Ohne das Merken würde bei jedem Frame
        ''' über eine womöglich megabytegroße Zeichenkette gehasht.</summary>
        Private Shared _maskFingerprintSource As String
        Private Shared _maskFingerprintValue As String
        Private Shared ReadOnly _maskFingerprintLock As New Object()

        Private Shared Function SelectionMaskFingerprint(maskBase64 As String) As String
            If String.IsNullOrEmpty(maskBase64) Then Return "0"
            SyncLock _maskFingerprintLock
                ' Referenzgleichheit zuerst: waehrend eines Reglerzugs ist es dieselbe Instanz.
                If _maskFingerprintSource IsNot Nothing AndAlso
                   (Object.ReferenceEquals(_maskFingerprintSource, maskBase64) OrElse
                    String.Equals(_maskFingerprintSource, maskBase64, StringComparison.Ordinal)) Then
                    Return _maskFingerprintValue
                End If

                Dim hash As String
                Using sha = Security.Cryptography.SHA256.Create()
                    hash = Convert.ToHexString(sha.ComputeHash(Text.Encoding.ASCII.GetBytes(maskBase64)))
                End Using
                _maskFingerprintSource = maskBase64
                _maskFingerprintValue = hash
                Return hash
            End SyncLock
        End Function

        Private Shared Function ReplaceBitmap(oldBitmap As SKBitmap, newBitmap As SKBitmap) As SKBitmap
            If newBitmap Is Nothing OrElse Object.ReferenceEquals(oldBitmap, newBitmap) Then Return oldBitmap
            oldBitmap.Dispose()
            Return newBitmap
        End Function

        ''' <summary>Wie <see cref="ReplaceBitmap"/>, aber für Ketten, die MIT DER FREMDEN QUELLE
        ''' beginnen dürfen: <paramref name="owned"/> sagt, ob das aktuelle Bild uns gehört und beim
        ''' Ersetzen entsorgt werden darf.
        '''
        ''' WARUM: Die Pixelkette begann an drei Stellen mit einem unbedingten Klon, damit jede Stufe
        ''' ihren Vorgänger entsorgen darf. Eine neutrale Stufe gibt ihre Quelle aber unverändert
        ''' zurück - bei einem Bild ohne gesetzte Regler wurde also dreimal das ganze Bild kopiert,
        ''' ohne dass sich ein Pixel ändert. Gemessen an 45 MP waren das 445 ms Grundlast, also genau
        ''' drei Vollkopien zu je rund 145 ms. Jetzt wird erst kopiert, wenn eine Stufe wirklich
        ''' etwas liefert; bleibt am Ende die Quelle stehen, klont der Aufrufer EINMAL.</summary>
        Private Shared Function ReplaceBitmapOwned(oldBitmap As SKBitmap, newBitmap As SKBitmap,
                                                   ByRef owned As Boolean) As SKBitmap
            If newBitmap Is Nothing OrElse Object.ReferenceEquals(oldBitmap, newBitmap) Then Return oldBitmap
            If owned Then oldBitmap.Dispose()
            owned = True
            Return newBitmap
        End Function

        ''' <summary>Schlusspunkt einer <see cref="ReplaceBitmapOwned"/>-Kette: sorgt dafür, dass der
        ''' Aufrufer IMMER ein eigenes Bitmap bekommt. Hat keine Stufe etwas geändert, steht hier noch
        ''' die fremde Quelle und wird einmal kopiert.</summary>
        Private Shared Function TakeOwnership(bitmap As SKBitmap, owned As Boolean) As SKBitmap
            If owned OrElse bitmap Is Nothing Then Return bitmap
            Return CloneBitmap(bitmap)
        End Function

        ''' Kopiert den rohen Pixelspeicher eines Bgra8888-Bitmaps in ein verwaltetes Byte-Array, damit
        ''' Pixel-Loops per Array-Index statt über SkiaSharps P/Invoke-lastiges GetPixel/SetPixel
        ''' laufen können (bei mehreren Millionen Pixeln ein erheblicher Geschwindigkeitsunterschied).
        ''' Liefert False bei jedem anderen Farbformat - die Pipeline erzeugt Bitmaps praktisch immer
        ''' als Bgra8888 (siehe DecodeOriented), Aufrufer müssen für diesen seltenen Fall aber weiterhin
        ''' auf GetPixel/SetPixel zurückfallen können.
        Private Shared Function TryBorrowBgraBuffer(bmp As SKBitmap, ByRef buffer As Byte(), ByRef stride As Integer) As Boolean
            buffer = Nothing
            stride = 0
            If bmp Is Nothing OrElse bmp.ColorType <> SKColorType.Bgra8888 Then Return False
            stride = bmp.RowBytes
            Dim length = stride * bmp.Height
            If length <= 0 Then Return False
            buffer = New Byte(length - 1) {}
            Marshal.Copy(bmp.GetPixels(), buffer, 0, length)
            Return True
        End Function

        Private Shared Sub CommitBgraBuffer(bmp As SKBitmap, buffer As Byte())
            Marshal.Copy(buffer, 0, bmp.GetPixels(), buffer.Length)
        End Sub

        ''' <summary>Die Entpremultiplikation, die <c>SKBitmap.GetPixel</c> auf einem Premul-Bitmap
        ''' vornimmt - als Tabelle über alle 256 mal 256 Kombinationen aus Kanalwert und Alpha.
        '''
        ''' WARUM EINE TABELLE UND KEINE FORMEL: Wer eine Pixelschleife von GetPixel/SetPixel auf einen
        ''' Byte-Puffer umstellt, muss diese Umrechnung selbst machen - und sie muss BITGLEICH sein,
        ''' sonst ändert der Umbau das Bild an teiltransparenten Stellen. Gemessen über alle 65536
        ''' Kombinationen trifft keine der naheliegenden Formeln: das übliche <c>v * 255 \ a</c> weicht
        ''' in 15760 Fällen um eine Stufe ab, eine Nachbildung der Reziprok-Tabelle in fast allen. Die
        ''' Tabelle wird deshalb aus Skia SELBST erhoben statt nachgerechnet. Das bleibt auch dann
        ''' richtig, wenn ein SkiaSharp-Wechsel die Rundung ändert.
        '''
        ''' Kosten: einmalig rund 20 ms beim ERSTEN Bedarf (Lazy), danach ein Tabellenzugriff je Pixel.
        ''' Die Gegenrechnung: GetPixel/SetPixel kosteten in der mehrskaligen Entrauschung allein für
        ''' Ein- und Ausgang rund 4 s je Vorschaubild.</summary>
        Private Shared ReadOnly _unpremultiplyTable As New Lazy(Of Byte())(
            AddressOf BuildUnpremultiplyTable, Threading.LazyThreadSafetyMode.ExecutionAndPublication)

        Private Shared Function BuildUnpremultiplyTable() As Byte()
            Dim table = New Byte(256 * 256 - 1) {}
            ' Zeile = Alpha, Spalte = premultiplizierter Kanalwert. Auch die Fälle mit Wert über Alpha
            ' werden erhoben: sie kommen in sauber premultiplizierten Bildern nicht vor, können aber
            ' aus Rundung entstehen, und was Skia dort liefert, ist die maßgebliche Antwort.
            Using probe = New SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul)
                Dim stride = probe.RowBytes
                Dim raw = New Byte(stride * 256 - 1) {}
                For a = 0 To 255
                    For v = 0 To 255
                        Dim o = a * stride + v * 4
                        raw(o) = CByte(v) : raw(o + 1) = CByte(v) : raw(o + 2) = CByte(v) : raw(o + 3) = CByte(a)
                    Next
                Next
                Marshal.Copy(raw, 0, probe.GetPixels(), raw.Length)
                For a = 0 To 255
                    For v = 0 To 255
                        table(a * 256 + v) = probe.GetPixel(v, a).Red
                    Next
                Next
            End Using
            Return table
        End Function

        ''' <summary>Premultiplizierten Kanalwert zu dem Wert machen, den <c>GetPixel</c> melden würde.</summary>
        Private Shared Function UnpremultiplyByte(value As Byte, alpha As Byte) As Byte
            Return _unpremultiplyTable.Value(CInt(alpha) * 256 + CInt(value))
        End Function

        ''' <summary>Die Premultiplikation, die <c>SKBitmap.SetPixel</c> vornimmt: Skias
        ''' <c>SkMulDiv255Round</c>. Über alle 65536 Kombinationen gegen SetPixel geprüft, keine
        ''' Abweichung - hier ist die Formel exakt und eine Tabelle unnötig. Ein einfaches
        ''' <c>v * a \ 255</c> wäre es NICHT (31770 Abweichungen um je eine Stufe).
        ''' Überlaufsicher: der Zwischenwert bleibt unter 65536.</summary>
        Private Shared Function PremultiplyByte(value As Byte, alpha As Byte) As Byte
            Dim product = CInt(value) * CInt(alpha) + 128
            Return CByte((product + (product >> 8)) >> 8)
        End Function

        ' Ab dieser Pixelzahl lohnt der Thread-Overhead von Parallel.For. Darunter (Miniaturen,
        ' entartete Größen) bleibt es seriell.
        Private Const ParallelPixelThreshold As Integer = 65536

        ''' <summary>Führt eine zeilenweise Bildoperation über y = 0..height-1 aus - parallel, sobald sich
        ''' der Thread-Overhead lohnt, sonst seriell. Voraussetzung: Die Zeilen sind unabhängig, jeder
        ''' Aufruf schreibt nur in seine eigene Zeile und liest höchstens aus unveränderten Quellpuffern.
        ''' Dann ist das Ergebnis unabhängig von der Thread-Aufteilung bitgleich zum seriellen Lauf.</summary>
        Private Shared Sub ForEachRow(width As Integer, height As Integer, body As Action(Of Integer))
            If height <= 0 Then Return
            If CLng(width) * height < ParallelPixelThreshold Then
                For y As Integer = 0 To height - 1
                    body(y)
                Next
            Else
                Parallel.For(0, height, body)
            End If
        End Sub

        ''' <summary>Identitätstabelle (Index -> Index) für den Alpha-Kanal von SKColorFilter.CreateTable.
        ''' Seit SkiaSharp 3.119 wirft die Überladung eine ArgumentNullException, wenn die Alpha-Tabelle
        ''' Nothing ist - früher stand Nothing für "Alpha unverändert lassen". Diese Tabelle stellt genau
        ''' dieses Verhalten wieder her und wird von Skia beim Bau des nativen Filters kopiert, ist also
        ''' gefahrlos gemeinsam nutzbar.</summary>
        Private Shared ReadOnly IdentityByteTable As Byte() = BuildIdentityByteTable()

        Private Shared Function BuildIdentityByteTable() As Byte()
            Dim table = New Byte(255) {}
            For i As Integer = 0 To 255
                table(i) = CByte(i)
            Next
            Return table
        End Function

        Private Shared Function CloneBitmap(source As SKBitmap) As SKBitmap
            Dim clone = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(clone)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return clone
        End Function

        ''' <summary>Kopiert ein Bitmap in einen anderen Farbtyp (premultipliziert). Wird gebraucht, wo
        ''' Stufen mit festem Farbtyp auf das Objekt-Komposit treffen - ohne die Umwandlung fallen sie
        ''' still durch (bekannte Fallenklasse dieses Projekts).</summary>
        Private Shared Function ConvertBitmapToColorType(source As SKBitmap, colorType As SKColorType) As SKBitmap
            If source Is Nothing Then Return Nothing
            If source.ColorType = colorType Then Return CloneBitmap(source)
            Dim clone = New SKBitmap(source.Width, source.Height, colorType, SKAlphaType.Premul)
            Using canvas = New SKCanvas(clone)
                canvas.Clear(SKColors.Transparent)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return clone
        End Function

        Private Shared Function CloneBitmapForAnnotationComposite(source As SKBitmap) As SKBitmap
            Dim clone = New SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
            Using canvas = New SKCanvas(clone)
                canvas.Clear(SKColors.Transparent)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return clone
        End Function

        Private Shared Function ApplyCrop(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim leftPct = Clamp(adj.CropLeftPercent, 0, 100) / 100.0F
            Dim topPct = Clamp(adj.CropTopPercent, 0, 100) / 100.0F
            Dim rightPct = Clamp(adj.CropRightPercent, 0, 100) / 100.0F
            Dim bottomPct = Clamp(adj.CropBottomPercent, 0, 100) / 100.0F

            If leftPct = 0 AndAlso topPct = 0 AndAlso rightPct = 0 AndAlso bottomPct = 0 Then Return source

            ' Der Beschnitt kommt pixelgenau aus dem Editor, wird aber prozentual transportiert (die
            ' Vorschau ist kleiner als das Original). Deshalb hier hart in gültige Pixelgrenzen zwingen,
            ' statt bei zu engem Ausschnitt den Beschnitt stillschweigend fallen zu lassen: mindestens
            ' ein Pixel bleibt stehen, die linke/obere Kante gewinnt.
            Dim left = Math.Max(0, Math.Min(CInt(Math.Round(source.Width * leftPct)), source.Width - 1))
            Dim top = Math.Max(0, Math.Min(CInt(Math.Round(source.Height * topPct)), source.Height - 1))
            Dim right = Math.Max(left + 1, Math.Min(source.Width - CInt(Math.Round(source.Width * rightPct)), source.Width))
            Dim bottom = Math.Max(top + 1, Math.Min(source.Height - CInt(Math.Round(source.Height * bottomPct)), source.Height))

            Dim cropWidth = right - left
            Dim cropHeight = bottom - top
            If cropWidth = source.Width AndAlso cropHeight = source.Height Then Return source
            Dim result = New SKBitmap(cropWidth, cropHeight, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                Dim srcRect = New SKRect(left, top, right, bottom)
                Dim dstRect = New SKRect(0, 0, cropWidth, cropHeight)
                canvas.DrawBitmap(source, srcRect, dstRect)
            End Using
            Return result
        End Function

        ' Weiche Kante der Retusche-Scheibe: bis 55% des Radius voll deckend, danach linear auslaufend.
        ' Entspricht der früheren Formel edge = (radius - d) / (radius * 0.45).
        Private Shared ReadOnly RetouchFeatherStops As Single() = {0.0F, 0.55F, 1.0F}
        ''' <summary>Radius der Patches, mit denen die Reparatur fuellt - MIT DER AUFLOESUNG
        ''' SKALIERT, nicht mehr fest. Er war fest 6 und damit auf allem oberhalb einer Web-Groesse
        ''' zu klein: ein Patch muss ungefaehr EINE STRUKTURPERIODE ueberspannen, sonst kann die
        ''' Quellensuche gar nicht unterscheiden, ob ein Treffer phasenrichtig sitzt, und ein
        ''' Fenstergitter zerfaellt zu Matsch.
        '''
        ''' Der Teiler kommt aus einem Kontrollversuch mit DEMSELBEN Motiv in zwei Aufloesungen
        ''' (echtes 50-MP-Foto einer Hochhausfassade, einmal nativ 5784x8660, einmal auf 800x1198
        ''' verkleinert), jeweils gegen das unbeschaedigte Original gemessen: nativ liegt das
        ''' Optimum bei Radius 40 (Fehler 15,7, Kontrast 16,0 gegen 16,7 im Original; Radius 24
        ''' liefert nur 11,8 Kontrast, also sichtbar Matsch), verkleinert bei Radius 6 (Fehler
        ''' 14,3; Radius 40 gibt dort 19,7). Optimum-Verhaeltnis 6,7 bei Aufloesungsverhaeltnis 7,2
        ''' - also linear. 8660/40 und 1198/6 ergeben 217 und 200; 210 liegt dazwischen.
        '''
        ''' Grenzen: unter 4 traegt ein Patch keine Struktur mehr. Die Obergrenze 40 ist NICHT nur
        ''' der gemessene Bestwert, sie haelt auch HealingSearchMargin bei 188 - mit 64 waere der
        ''' Suchrand 212, und das ist gemessen KEIN freier Zugewinn: am 50-MP-Foto verschlechterte
        ''' der groessere Rand den Kontrast im reparierten Bereich von 16,0 auf 17,8 (er findet
        ''' weiter entfernte, schlechter passende Quellen), waehrend er am 800-px-Bild half. Wer die
        ''' Obergrenze anhebt, muss den Suchrand getrennt davon festhalten und beides neu messen.
        ''' Laufzeit ist ueber den ganzen Bereich unauffaellig (3,0 s an 50 MP): groessere Patches
        ''' heissen weniger Kopien und weniger Suchen.
        '''
        ''' Gerechnet wird auf dem ARBEITSBILD, nicht auf der Datei: grosse Regionen werden vorher
        ''' auf HealingMaxNativeExtent normalisiert, und dort sind die Strukturen entsprechend
        ''' kleiner. Ein fester Wert waere dort wieder zu gross.
        '''
        ''' VORSICHT bei Gegenbeispielen aus SYNTHETISCHEN Testbildern: eine kuenstliche Fassade mit
        ''' 76 px Fensterabstand in einem 5,6-MP-Bild braucht Radius 64 und widerspricht der Regel -
        ''' sie hat aber ein Strukturmass, das in echten Fotos so nicht vorkommt. Der eigentliche
        ''' Vorhersagewert ist die Strukturperiode; die Aufloesung ist ihr praktischer Stellvertreter
        ''' und fuer dasselbe Motiv exakt richtig. Wer es besser will, misst die Periode aus der
        ''' Umgebung der Reparaturstelle (Autokorrelation) statt zu schaetzen.</summary>
        Private Const HealingPatchRadiusDivisor As Integer = 210
        Private Const HealingPatchRadiusMin As Integer = 4
        Private Const HealingPatchRadiusMax As Integer = 40
        Private Const HealingSearchBaseMargin As Integer = 140
        ''' <summary>Kontextrand fuer die Quellensuche. Rechnet mit dem GROESSTEN moeglichen Patch,
        ''' damit der Rand nie zu knapp wird - er kostet nur etwas mehr kopierten Kontext.</summary>
        Private Const HealingSearchMargin As Integer = HealingSearchBaseMargin + HealingPatchRadiusMax + 8
        Private Const HealingMaxNativeExtent As Integer = 1200

        ''' <summary>Patch-Radius zur Bildkante, in der gerechnet wird.</summary>
        Private Shared Function HealingPatchRadiusFor(resolutionLongEdge As Integer) As Integer
            Return CInt(Clamp(CSng(resolutionLongEdge) / HealingPatchRadiusDivisor,
                              HealingPatchRadiusMin, HealingPatchRadiusMax))
        End Function

        ''' <summary>Die durchgereichte Bildkante, oder - wenn keine gesetzt ist - die des Bildes
        ''' selbst. NICHT die des Arbeitsausschnitts: der ist Region plus Suchrand und waere auf
        ''' einem 50-MP-Foto nur rund 1100 px breit, der Radius damit um das Achtfache zu klein.</summary>
        Private Shared Function EffectiveHealingLongEdge(result As SKBitmap, resolutionLongEdge As Integer) As Integer
            If resolutionLongEdge > 0 Then Return resolutionLongEdge
            If result Is Nothing Then Return 0
            Return Math.Max(result.Width, result.Height)
        End Function

        ''' ARBEITSBILD-Umbau Stufe E: Das Rezept-Replay der Retusche ist entfernt.
        ''' Retusche wird beim Commit REGIONAL in Vollauflösung ins Arbeitsbild eingebacken
        ''' (EditorViewModel.CommitRetouchStroke -> WorkingImageService.CommitRegion ->
        ''' ApplyRetouchSpotsInPlace). Damit entfielen: ApplyRetouch, der Retusche-Stufen-Cache
        ''' (Primary/Secondary/Seed-Slots), ComputeRetouchSpotsKey, das Praefix-Anhaengen und der
        ''' .fpx-Seed. Erhalten bleiben die Zeichen-Engines DrawRetouchSpot/DrawHealingRegion und
        ''' die InPlace-Anwendungen darunter (Commit + Live-Vorschau).

        Public Shared Sub ApplyRetouchSpotInPlace(target As SKBitmap, sampleSource As SKBitmap, spot As RetouchSpot,
                                                  sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If target Is Nothing OrElse sampleSource Is Nothing OrElse spot Is Nothing Then Return
            Using canvas = New SKCanvas(target)
                If IsHealingSpot(spot) Then
                    DrawHealingRegion(target, canvas, target, {spot}, sourceWidthPixels, sourceHeightPixels)
                Else
                    DrawRetouchSpot(target, sampleSource, canvas, spot, sourceWidthPixels, sourceHeightPixels)
                End If
            End Using
        End Sub

        Public Shared Sub ApplyRetouchSpotsInPlace(target As SKBitmap, sampleSource As SKBitmap, spots As IReadOnlyList(Of RetouchSpot),
                                                   sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If target Is Nothing OrElse sampleSource Is Nothing OrElse spots Is Nothing OrElse spots.Count = 0 Then Return
            Using canvas = New SKCanvas(target)
                Dim pendingHeal As New List(Of RetouchSpot)()
                Dim pendingHealStrokeId As Integer? = Nothing
                For Each spot In spots
                    If spot Is Nothing Then Continue For
                    If IsHealingSpot(spot) Then
                        If pendingHeal.Count > 0 AndAlso pendingHealStrokeId.HasValue AndAlso spot.StrokeId <> pendingHealStrokeId.Value Then
                            DrawHealingRegion(target, canvas, target, pendingHeal, sourceWidthPixels, sourceHeightPixels)
                            pendingHeal.Clear()
                        End If
                        pendingHeal.Add(spot)
                        pendingHealStrokeId = spot.StrokeId
                        Continue For
                    End If

                    If pendingHeal.Count > 0 Then
                        DrawHealingRegion(target, canvas, target, pendingHeal, sourceWidthPixels, sourceHeightPixels)
                        pendingHeal.Clear()
                        pendingHealStrokeId = Nothing
                    End If
                    DrawRetouchSpot(target, sampleSource, canvas, spot, sourceWidthPixels, sourceHeightPixels)
                Next
                If pendingHeal.Count > 0 Then
                    DrawHealingRegion(target, canvas, target, pendingHeal, sourceWidthPixels, sourceHeightPixels)
                End If
            End Using
        End Sub

        Private Shared Sub DrawRetouchSpot(result As SKBitmap, source As SKBitmap, canvas As SKCanvas,
                                           spot As RetouchSpot, sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If result Is Nothing OrElse source Is Nothing OrElse canvas Is Nothing OrElse spot Is Nothing Then Return

            Dim scaleX As Single = 1.0F
            Dim scaleY As Single = 1.0F
            If sourceWidthPixels > 0 AndAlso sourceHeightPixels > 0 AndAlso source.Width > 0 AndAlso source.Height > 0 Then
                scaleX = source.Width / CSng(sourceWidthPixels)
                scaleY = source.Height / CSng(sourceHeightPixels)
            End If
            Dim radiusScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))
            Dim cx = Clamp(spot.XPixels * scaleX, 0, source.Width)
            Dim cy = Clamp(spot.YPixels * scaleY, 0, source.Height)
            Dim radius = Clamp(spot.RadiusPixels * radiusScale, 1, Math.Max(source.Width, source.Height))
            Dim flow = Clamp(spot.FlowPercent, 0, 100) / 100.0F
            Dim opacity = Clamp(spot.OpacityPercent, 0, 100) / 100.0F
            Dim strength = Clamp(spot.StrengthPercent, 0, 100) / 100.0F
            Dim alphaFactor = flow * opacity * strength

            If spot.HasCloneSource Then
                Dim sx = Clamp(spot.SourceXPixels * scaleX, 0, source.Width)
                Dim sy = Clamp(spot.SourceYPixels * scaleY, 0, source.Height)
                ' Der Stempel soll denselben bereits bearbeiteten Stand sehen wie Reparatur und
                ' Verwischen. Sonst kopiert er nach nachfolgenden Retuschen wieder Textur aus einem
                ' älteren, retuschefreien Zwischenstand zurück.
                DrawCloneStamp(canvas, result, cx, cy, sx, sy, radius, alphaFactor)
            Else
                ' Verwischen soll auf dem bereits retuschierten Ergebnis aufbauen, damit nach einer
                ' Reparatur nicht wieder Textur aus dem Ursprungsbild "hineingewischt" wird.
                ' BEFUND: KEINE Umgebungsfarb-Scheibe mehr darueber - beim Ziehen ueberlappen
                ' dutzende Spots, und die 28-%-Scheiben konvergierten gegen eine flache Fremdfarbe
                ' (brauner Schmier). Die Scheibe war Fleckentferner-Logik, kein Verwischen.
                DrawBlurSpot(result, canvas, cx, cy, radius, alphaFactor)
            End If
        End Sub

        Private Shared Function IsHealingSpot(spot As RetouchSpot) As Boolean
            Return spot IsNot Nothing AndAlso Not spot.HasCloneSource AndAlso
                   String.Equals(spot.Mode, "Heal", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' Kopiert eine weich auslaufende Scheibe von (sx, sy) nach (cx, cy). Der Bitmap-Shader wird
        ''' um den Versatz verschoben, ein Radial-Verlauf liefert per DstIn die Kantenmaske.
        Private Shared Sub DrawCloneStamp(canvas As SKCanvas, source As SKBitmap,
                                          cx As Single, cy As Single, sx As Single, sy As Single, radius As Single, flow As Single)
            Dim offset = SKMatrix.CreateTranslation(cx - sx, cy - sy)
            Using bitmapShader = SKShader.CreateBitmap(source, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, offset)
                Using mask = SKShader.CreateRadialGradient(New SKPoint(cx, cy), radius,
                                                           {SKColors.White.WithAlpha(CByte(255 * flow)), SKColors.White.WithAlpha(CByte(255 * flow)), SKColors.Transparent},
                                                           RetouchFeatherStops, SKShaderTileMode.Clamp)
                    Using masked = SKShader.CreateCompose(bitmapShader, mask, SKBlendMode.DstIn)
                        Using paint = New SKPaint With {.Shader = masked, .IsAntialias = True}
                            canvas.DrawCircle(cx, cy, radius, paint)
                        End Using
                    End Using
                End Using
            End Using
        End Sub

        Private Shared Sub DrawSoftDisc(canvas As SKCanvas, cx As Single, cy As Single, radius As Single, fill As SKColor, flow As Single)
            Dim alphaFill = fill.WithAlpha(CByte(fill.Alpha * flow))
            Using shader = SKShader.CreateRadialGradient(New SKPoint(cx, cy), radius,
                                                         {alphaFill, alphaFill, fill.WithAlpha(0)},
                                                         RetouchFeatherStops, SKShaderTileMode.Clamp)
                Using paint = New SKPaint With {.Shader = shader, .IsAntialias = True}
                    canvas.DrawCircle(cx, cy, radius, paint)
                End Using
            End Using
        End Sub

        Private Shared Sub DrawBlurSpot(result As SKBitmap, canvas As SKCanvas,
                                        cx As Single, cy As Single, radius As Single, flow As Single)
            If result Is Nothing OrElse canvas Is Nothing Then Return
            flow = Clamp(flow, 0.0F, 1.0F)
            If radius <= 0.5F OrElse flow <= 0.001F Then Return

            ' BEFUND: sigma gedeckelt - 0,45r machte das Kreisinnere bei grossen Radien
            ' strukturlos, und ueberlappende Zug-Spots verstaerkten das zu Brei.
            Dim sigma = Clamp(radius * 0.22F, 1.25F, 24.0F)
            Dim pad = CInt(Math.Ceiling(radius + sigma * 3.0F + 2.0F))
            Dim left = Math.Max(0, CInt(Math.Floor(cx - pad)))
            Dim top = Math.Max(0, CInt(Math.Floor(cy - pad)))
            Dim right = Math.Min(result.Width, CInt(Math.Ceiling(cx + pad)))
            Dim bottom = Math.Min(result.Height, CInt(Math.Ceiling(cy + pad)))
            Dim width = right - left
            Dim height = bottom - top
            If width <= 0 OrElse height <= 0 Then Return

            ' Die Region direkt aus dem Arbeitsbild lesen. Eine zusaetzliche Patch-Bitmap samt
            ' SKCanvas-Kopie pro Spot verdoppelte zuvor den nativen Speicherverkehr, obwohl der
            ' Box-Blur die Pixel danach ohnehin in verwaltete Puffer uebernahm.
            Using blurred = FastBoxBlurRegion(result, left, top, width, height,
                                              Math.Max(1, CInt(Math.Round(sigma * 1.35F))))
                Dim bounds = New SKRect(left, top, right, bottom)
                canvas.SaveLayer(bounds, Nothing)
                canvas.DrawBitmap(blurred, left, top)
                Using mask = SKShader.CreateRadialGradient(New SKPoint(cx, cy), radius,
                                                           {SKColors.White.WithAlpha(CByte(255 * flow)),
                                                            SKColors.White.WithAlpha(CByte(255 * flow)),
                                                            SKColors.Transparent},
                                                           RetouchFeatherStops, SKShaderTileMode.Clamp)
                    Using maskPaint = New SKPaint With {.Shader = mask, .IsAntialias = True, .BlendMode = SKBlendMode.DstIn}
                        ' BEFUND (der 4x-Schmier): DstIn wirkt nur, wo auch GEZEICHNET
                        ' wird. DrawCircle liess den Layer AUSSERHALB des Kreises unangetastet -
                        ' die Blur-Kopie des gesamten Pads (2,35r je Seite) wurde beim Restore
                        ' voll einkomposittiert. Das volle Rechteck zeichnen: der Radial-Verlauf
                        ' ist ausserhalb r transparent und nullt den Layer dort.
                        canvas.DrawRect(bounds, maskPaint)
                    End Using
                End Using
                canvas.Restore()
            End Using
        End Sub

        Private Shared Function FastBoxBlurRegion(source As SKBitmap,
                                                  left As Integer, top As Integer,
                                                  width As Integer, height As Integer,
                                                  radius As Integer) As SKBitmap
            If source Is Nothing Then Return Nothing
            If width <= 0 OrElse height <= 0 Then Return Nothing
            radius = Math.Max(1, Math.Min(radius, Math.Max(width, height)))
            Dim count = width * height
            If count <= 0 OrElse count > Integer.MaxValue \ 4 Then Return Nothing
            Dim byteCount = count * 4
            Dim rowByteCount = width * 4
            Dim src = ArrayPool(Of Byte).Shared.Rent(byteCount)
            Dim tmp = ArrayPool(Of Byte).Shared.Rent(byteCount)
            Dim output = ArrayPool(Of Byte).Shared.Rent(byteCount)
            Dim result As SKBitmap = Nothing

            ' Drei gepoolte Byte-Puffer ersetzen zwoelf Integer-Arrays. Der Algorithmus bleibt
            ' derselbe zweistufige Box-Blur inklusive seiner Randbehandlung; pro Patch-Pixel
            ' sinkt der verwaltete Arbeitsbereich damit von 48 auf 12 Byte und wird zwischen
            ' den Spots wiederverwendet, statt den GC bei jedem Mauspunkt zu belasten.
            Dim rawSupported = source.BytesPerPixel = 4 AndAlso source.GetPixels() <> IntPtr.Zero
            Try
                If rawSupported Then
                    Dim basePtr = source.GetPixels()
                    Dim srcStride = source.RowBytes
                    For y = 0 To height - 1
                        Marshal.Copy(IntPtr.Add(basePtr, (top + y) * srcStride + left * 4),
                                     src, y * rowByteCount, rowByteCount)
                    Next
                Else
                    For y = 0 To height - 1
                        Dim row = y * rowByteCount
                        For x = 0 To width - 1
                            Dim c = source.GetPixel(left + x, top + y)
                            Dim o = row + x * 4
                            src(o) = c.Red
                            src(o + 1) = c.Green
                            src(o + 2) = c.Blue
                            src(o + 3) = c.Alpha
                        Next
                    Next
                End If

                For y = 0 To height - 1
                    Dim row = y * rowByteCount
                    Dim sum0 As Integer = 0, sum1 As Integer = 0, sum2 As Integer = 0, sum3 As Integer = 0
                    Dim samples As Integer = 0
                    For x = 0 To Math.Min(width - 1, radius)
                        Dim o = row + x * 4
                        sum0 += src(o) : sum1 += src(o + 1) : sum2 += src(o + 2) : sum3 += src(o + 3)
                        samples += 1
                    Next
                    For x = 0 To width - 1
                        Dim o = x * 4
                        If x - radius - 1 >= 0 Then
                            Dim removeOffset = row + (x - radius - 1) * 4
                            sum0 -= src(removeOffset) : sum1 -= src(removeOffset + 1)
                            sum2 -= src(removeOffset + 2) : sum3 -= src(removeOffset + 3)
                            samples -= 1
                        End If
                        If x + radius < width AndAlso x > 0 Then
                            Dim addOffset = row + (x + radius) * 4
                            sum0 += src(addOffset) : sum1 += src(addOffset + 1)
                            sum2 += src(addOffset + 2) : sum3 += src(addOffset + 3)
                            samples += 1
                        End If
                        Dim outOffset = row + o
                        tmp(outOffset) = CByte(sum0 \ samples)
                        tmp(outOffset + 1) = CByte(sum1 \ samples)
                        tmp(outOffset + 2) = CByte(sum2 \ samples)
                        tmp(outOffset + 3) = CByte(sum3 \ samples)
                    Next
                Next

                For x = 0 To width - 1
                    Dim columnOffset = x * 4
                    Dim sum0 As Integer = 0, sum1 As Integer = 0, sum2 As Integer = 0, sum3 As Integer = 0
                    Dim samples As Integer = 0
                    For y = 0 To Math.Min(height - 1, radius)
                        Dim o = y * rowByteCount + columnOffset
                        sum0 += tmp(o) : sum1 += tmp(o + 1) : sum2 += tmp(o + 2) : sum3 += tmp(o + 3)
                        samples += 1
                    Next
                    For y = 0 To height - 1
                        If y - radius - 1 >= 0 Then
                            Dim removeOffset = (y - radius - 1) * rowByteCount + columnOffset
                            sum0 -= tmp(removeOffset) : sum1 -= tmp(removeOffset + 1)
                            sum2 -= tmp(removeOffset + 2) : sum3 -= tmp(removeOffset + 3)
                            samples -= 1
                        End If
                        If y + radius < height AndAlso y > 0 Then
                            Dim addOffset = (y + radius) * rowByteCount + columnOffset
                            sum0 += tmp(addOffset) : sum1 += tmp(addOffset + 1)
                            sum2 += tmp(addOffset + 2) : sum3 += tmp(addOffset + 3)
                            samples += 1
                        End If
                        Dim outOffset = y * rowByteCount + columnOffset
                        output(outOffset) = CByte(sum0 \ samples)
                        output(outOffset + 1) = CByte(sum1 \ samples)
                        output(outOffset + 2) = CByte(sum2 \ samples)
                        output(outOffset + 3) = CByte(sum3 \ samples)
                    Next
                Next

                result = New SKBitmap(width, height, source.ColorType, source.AlphaType)
                Dim resultRaw = result.BytesPerPixel = 4 AndAlso result.GetPixels() <> IntPtr.Zero
                If rawSupported AndAlso resultRaw Then
                    Dim basePtr = result.GetPixels()
                    Dim dstStride = result.RowBytes
                    For y = 0 To height - 1
                        Marshal.Copy(output, y * rowByteCount,
                                     IntPtr.Add(basePtr, y * dstStride), rowByteCount)
                    Next
                Else
                    For y = 0 To height - 1
                        Dim row = y * rowByteCount
                        For x = 0 To width - 1
                            Dim o = row + x * 4
                            result.SetPixel(x, y, New SKColor(output(o), output(o + 1), output(o + 2), output(o + 3)))
                        Next
                    Next
                End If

                Return result
            Catch
                result?.Dispose()
                Throw
            Finally
                ArrayPool(Of Byte).Shared.Return(src)
                ArrayPool(Of Byte).Shared.Return(tmp)
                ArrayPool(Of Byte).Shared.Return(output)
            End Try
        End Function

        Private Shared Sub DrawHealingRegion(result As SKBitmap, canvas As SKCanvas, source As SKBitmap,
                                             spots As IReadOnlyList(Of RetouchSpot),
                                             sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If result Is Nothing OrElse source Is Nothing OrElse spots Is Nothing OrElse spots.Count = 0 Then Return

            Dim scaleX As Single = 1.0F
            Dim scaleY As Single = 1.0F
            If sourceWidthPixels > 0 AndAlso sourceHeightPixels > 0 AndAlso source.Width > 0 AndAlso source.Height > 0 Then
                scaleX = source.Width / CSng(sourceWidthPixels)
                scaleY = source.Height / CSng(sourceHeightPixels)
            End If
            Dim radiusScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))

            Dim scaled As New List(Of (X As Single, Y As Single, Radius As Single, Flow As Single, EffectCeiling As Single))()
            Dim left = source.Width
            Dim top = source.Height
            Dim right = 0
            Dim bottom = 0
            Dim maxRadius As Single = 1.0F
            For Each spot In spots
                If spot Is Nothing Then Continue For
                Dim radius = Clamp(spot.RadiusPixels * radiusScale + 2.0F, 1, Math.Max(source.Width, source.Height))
                Dim cx = Clamp(spot.XPixels * scaleX, 0, source.Width)
                Dim cy = Clamp(spot.YPixels * scaleY, 0, source.Height)
                Dim flow = Clamp(spot.FlowPercent, 0, 100) / 100.0F
                Dim effectCeiling = Clamp(spot.OpacityPercent, 0, 100) / 100.0F *
                                    Clamp(spot.StrengthPercent, 0, 100) / 100.0F
                If flow <= 0.001F OrElse effectCeiling <= 0.001F Then Continue For
                scaled.Add((cx, cy, radius, flow, effectCeiling))
                maxRadius = Math.Max(maxRadius, radius)
                left = Math.Min(left, CInt(Math.Floor(cx - radius - 2)))
                top = Math.Min(top, CInt(Math.Floor(cy - radius - 2)))
                right = Math.Max(right, CInt(Math.Ceiling(cx + radius + 2)))
                bottom = Math.Max(bottom, CInt(Math.Ceiling(cy + radius + 2)))
            Next
            If scaled.Count = 0 Then Return

            left = Math.Max(0, left)
            top = Math.Max(0, top)
            right = Math.Min(source.Width, right)
            bottom = Math.Min(source.Height, bottom)
            Dim width = right - left
            Dim height = bottom - top
            If width <= 0 OrElse height <= 0 Then Return

            ' Zwei Masken mit getrennten Aufgaben:
            ' - defectMask beschreibt die komplette Pinselgeometrie. Sie verhindert, dass die
            '   Quellensuche noch Pixel aus dem zu reparierenden Defekt verwendet.
            ' - blendMask beschreibt die sichtbare Wirkung. Fluss darf sich entlang des Zugs
            '   aufbauen, Deckkraft und Staerke deckeln danach aber den gesamten Zug.
            Using defectMask = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using blendMask = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using defectCanvas = New SKCanvas(defectMask)
                        Using blendCanvas = New SKCanvas(blendMask)
                            defectCanvas.Clear(SKColors.Transparent)
                            blendCanvas.Clear(SKColors.Transparent)
                            defectCanvas.Translate(-left, -top)
                            blendCanvas.Translate(-left, -top)
                            For Each s In scaled
                                Using defectShader = SKShader.CreateRadialGradient(New SKPoint(s.X, s.Y), s.Radius,
                                                                                   {SKColors.White, SKColors.White, SKColors.Transparent},
                                                                                   RetouchFeatherStops, SKShaderTileMode.Clamp)
                                    Using defectPaint = New SKPaint With {.Shader = defectShader, .IsAntialias = True, .BlendMode = SKBlendMode.SrcOver}
                                        defectCanvas.DrawCircle(s.X, s.Y, s.Radius, defectPaint)
                                    End Using
                                End Using
                                Using blendShader = SKShader.CreateRadialGradient(New SKPoint(s.X, s.Y), s.Radius,
                                                                                  {SKColors.White.WithAlpha(CByte(255 * s.Flow)),
                                                                                   SKColors.White.WithAlpha(CByte(255 * s.Flow)),
                                                                                   SKColors.Transparent},
                                                                                  RetouchFeatherStops, SKShaderTileMode.Clamp)
                                    Using blendPaint = New SKPaint With {.Shader = blendShader, .IsAntialias = True, .BlendMode = SKBlendMode.SrcOver}
                                        blendCanvas.DrawCircle(s.X, s.Y, s.Radius, blendPaint)
                                    End Using
                                End Using
                            Next
                            blendCanvas.ResetMatrix()
                            Dim effectCeiling = scaled(0).EffectCeiling
                            Using ceilingPaint = New SKPaint With {
                                .Color = SKColors.White.WithAlpha(CByte(255 * effectCeiling)),
                                .BlendMode = SKBlendMode.DstIn,
                                .IsAntialias = False
                            }
                                blendCanvas.DrawRect(New SKRect(0, 0, width, height), ceilingPaint)
                            End Using
                        End Using
                    End Using

                    If DrawInpaintedHealingRegion(result, defectMask, blendMask, left, top) Then
                        Return
                    End If

                    Dim targetAverage = AverageRegionSurroundingColor(source, defectMask, left, top, maxRadius)
                    If Not targetAverage.HasValue Then
                        For Each s In scaled
                            Dim visibleFlow = s.Flow * s.EffectCeiling
                            Dim fill = AverageSurroundingColor(source, s.X, s.Y, s.Radius)
                            If fill.HasValue Then DrawSoftDisc(canvas, s.X, s.Y, s.Radius, fill.Value, visibleFlow)
                        Next
                        Return
                    End If

                    Dim sample = FindHealingRegionPatch(source, defectMask, left, top, width, height, maxRadius, targetAverage.Value)
                    If Not sample.Found Then
                        For Each s In scaled
                            DrawSoftDisc(canvas, s.X, s.Y, s.Radius, targetAverage.Value, s.Flow * s.EffectCeiling)
                        Next
                        Return
                    End If

                    DrawAdjustedHealingRegion(result, source, blendMask, left, top, sample.Left, sample.Top,
                                              targetAverage.Value, sample.Average)
                End Using
            End Using
        End Sub

        ''' <paramref name="resolutionLongEdge"/>: laengste Kante des BILDES in der Aufloesung, in
        ''' der hier gerechnet wird - daran haengt der Patch-Radius. 0 heisst "aus result ableiten"
        ''' und gilt fuer den nativen Weg, wo result das ganze Bild ist. Der skalierte Weg reicht den
        ''' heruntergerechneten Wert durch, weil sein result nur ein verkleinerter Ausschnitt ist.
        Private Shared Function DrawInpaintedHealingRegion(result As SKBitmap, defectMask As SKBitmap, blendMask As SKBitmap,
                                                           targetLeft As Integer, targetTop As Integer,
                                                           Optional resolutionLongEdge As Integer = 0) As Boolean
            If result Is Nothing OrElse defectMask Is Nothing OrElse blendMask Is Nothing OrElse
               defectMask.Width <= 0 OrElse defectMask.Height <= 0 OrElse
               defectMask.Width <> blendMask.Width OrElse defectMask.Height <> blendMask.Height Then Return False

            If Math.Max(defectMask.Width, defectMask.Height) > HealingMaxNativeExtent Then
                Return DrawScaledInpaintedHealingRegion(result, defectMask, blendMask, targetLeft, targetTop,
                                                       EffectiveHealingLongEdge(result, resolutionLongEdge))
            End If

            Dim width = defectMask.Width
            Dim height = defectMask.Height
            Dim count = width * height
            Dim maskAlpha(count - 1) As Byte
            Dim blendAlpha(count - 1) As Byte
            Dim filled(count - 1) As Boolean
            Dim queued(count - 1) As Boolean
            Dim maskedCount = 0

            For maskY = 0 To height - 1
                Dim y = targetTop + maskY
                For mx = 0 To width - 1
                    Dim index = maskY * width + mx
                    Dim alpha = defectMask.GetPixel(mx, maskY).Alpha
                    maskAlpha(index) = NormalizeHealingMaskAlpha(alpha)
                    blendAlpha(index) = blendMask.GetPixel(mx, maskY).Alpha
                    Dim isMasked = alpha > 8 AndAlso y >= 0 AndAlso y < result.Height AndAlso
                                   targetLeft + mx >= 0 AndAlso targetLeft + mx < result.Width
                    filled(index) = Not isMasked
                    If isMasked Then maskedCount += 1
                Next
            Next

            If maskedCount = 0 Then Return False

            ' Nur Zielregion plus Suchrand kopieren. Die fruehere CloneBitmap(result)-Kopie
            ' allokierte bei 49 MP rund 196 MB pro Zug, obwohl nur ein kleiner Fleck veraendert wird.
            Dim workMargin = HealingSearchMargin
            Dim workLeft = Math.Max(0, targetLeft - workMargin)
            Dim workTop = Math.Max(0, targetTop - workMargin)
            Dim workRight = Math.Min(result.Width, targetLeft + width + workMargin)
            Dim workBottom = Math.Min(result.Height, targetTop + height + workMargin)
            Dim workWidth = workRight - workLeft
            Dim workHeight = workBottom - workTop
            If workWidth <= 0 OrElse workHeight <= 0 Then Return False

            Using work = New SKBitmap(workWidth, workHeight, result.ColorType, result.AlphaType)
                Using workCanvas = New SKCanvas(work)
                    workCanvas.DrawBitmap(result,
                                          New SKRect(workLeft, workTop, workRight, workBottom),
                                          New SKRect(0, 0, workWidth, workHeight))
                End Using
                Dim localTargetLeft = targetLeft - workLeft
                Dim localTargetTop = targetTop - workTop

                If DrawPatchBasedInpaintedHealingRegion(EffectiveHealingLongEdge(result, resolutionLongEdge),
                                                       result, work, maskAlpha, blendAlpha,
                                                         localTargetLeft, localTargetTop, width, height,
                                                         workLeft, workTop) Then
                    Return True
                End If

                Dim queue As New Queue(Of Integer)()
                For maskY = 0 To height - 1
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        If filled(index) OrElse Not HasFilledNeighbor(filled, width, height, mx, maskY) Then Continue For
                        queue.Enqueue(index)
                        queued(index) = True
                    Next
                Next

                Dim repairedCount = 0
                While queue.Count > 0
                    Dim index = queue.Dequeue()
                    queued(index) = False
                    If filled(index) Then Continue While

                    Dim mx = index Mod width
                    Dim maskY = index \ width
                    Dim x = localTargetLeft + mx
                    Dim y = localTargetTop + maskY
                    Dim average = AverageUnmaskedRays(work, maskAlpha, localTargetLeft, localTargetTop, width, height, mx, maskY)
                    If Not average.HasValue Then
                        average = AverageFilledNeighborhood(work, filled, localTargetLeft, localTargetTop, width, height, mx, maskY)
                    End If
                    If Not average.HasValue Then Continue While

                    work.SetPixel(x, y, average.Value)
                    filled(index) = True
                    repairedCount += 1

                    For oy = -1 To 1
                        For ox = -1 To 1
                            If ox = 0 AndAlso oy = 0 Then Continue For
                            Dim nx = mx + ox
                            Dim ny = maskY + oy
                            If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then Continue For
                            Dim ni = ny * width + nx
                            If filled(ni) OrElse queued(ni) OrElse maskAlpha(ni) <= 8 Then Continue For
                            queue.Enqueue(ni)
                            queued(ni) = True
                        Next
                    Next
                End While

                If repairedCount = 0 Then Return False
                If ShouldSmoothInpaintedRegion(work, maskAlpha, localTargetLeft, localTargetTop, width, height) Then
                    SmoothInpaintedRegion(work, maskAlpha, localTargetLeft, localTargetTop, width, height)
                End If

                For maskY = 0 To height - 1
                    Dim workY = localTargetTop + maskY
                    Dim resultY = workTop + workY
                    If resultY < 0 OrElse resultY >= result.Height Then Continue For
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        Dim alpha = blendAlpha(index)
                        If alpha <= 8 OrElse Not filled(index) Then Continue For
                        Dim workX = localTargetLeft + mx
                        Dim resultX = workLeft + workX
                        If resultX < 0 OrElse resultX >= result.Width Then Continue For

                        Dim localAlpha = Clamp(alpha / 255.0F, 0.0F, 1.0F)
                        If localAlpha <= 0.001F Then Continue For
                        Dim target = result.GetPixel(resultX, resultY)
                        Dim repaired = work.GetPixel(workX, workY)
                        result.SetPixel(resultX, resultY, New SKColor(
                            BlendByte(target.Red, repaired.Red, localAlpha),
                            BlendByte(target.Green, repaired.Green, localAlpha),
                            BlendByte(target.Blue, repaired.Blue, localAlpha),
                            BlendByte(target.Alpha, repaired.Alpha, localAlpha)))
                    Next
                Next
            End Using

            Return True
        End Function

        ''' <summary>Aufloesungsnormalisierter Heal-Pfad fuer sehr grosse Masken. Feste 13x13-Patches,
        ''' 160 Durchlaeufe und das Suchbudget reichen bei einem 1200-px-Ziel gut aus, bei einer
        ''' mehrere tausend Pixel langen 49-MP-Maske faellt die Mitte dagegen in den weichen Fallback.
        ''' Die Struktur wird deshalb in einem begrenzten Kontext rekonstruiert und anschliessend mit
        ''' der unveraenderten Vollaufloesungs-Deckungsmaske zurueckkomponiert.</summary>
        Private Shared Function DrawScaledInpaintedHealingRegion(result As SKBitmap,
                                                                 defectMask As SKBitmap,
                                                                 blendMask As SKBitmap,
                                                                 targetLeft As Integer,
                                                                 targetTop As Integer,
                                                                 resolutionLongEdge As Integer) As Boolean
            Dim scale = HealingMaxNativeExtent / CDbl(Math.Max(defectMask.Width, defectMask.Height))
            If scale <= 0.0 OrElse scale >= 1.0 Then Return False

            ' Der Suchrand soll auch NACH dem Downscale dieselben 154 Arbeits-Pixel behalten.
            Dim contextMargin = Math.Max(HealingSearchMargin,
                                         CInt(Math.Ceiling(HealingSearchMargin / scale)))
            Dim contextLeft = Math.Max(0, targetLeft - contextMargin)
            Dim contextTop = Math.Max(0, targetTop - contextMargin)
            Dim contextRight = Math.Min(result.Width, targetLeft + defectMask.Width + contextMargin)
            Dim contextBottom = Math.Min(result.Height, targetTop + defectMask.Height + contextMargin)
            Dim contextWidth = contextRight - contextLeft
            Dim contextHeight = contextBottom - contextTop
            If contextWidth <= 0 OrElse contextHeight <= 0 Then Return False

            Dim scaledContextWidth = Math.Max(1, CInt(Math.Round(contextWidth * scale)))
            Dim scaledContextHeight = Math.Max(1, CInt(Math.Round(contextHeight * scale)))
            Dim scaleX = scaledContextWidth / CDbl(contextWidth)
            Dim scaleY = scaledContextHeight / CDbl(contextHeight)
            Dim scaledTargetLeft = CInt(Math.Round((targetLeft - contextLeft) * scaleX))
            Dim scaledTargetTop = CInt(Math.Round((targetTop - contextTop) * scaleY))
            Dim scaledMaskWidth = Math.Max(1, CInt(Math.Round(defectMask.Width * scaleX)))
            Dim scaledMaskHeight = Math.Max(1, CInt(Math.Round(defectMask.Height * scaleY)))

            Using scaledContext = New SKBitmap(scaledContextWidth, scaledContextHeight,
                                               result.ColorType, result.AlphaType)
                Using scaledCanvas = New SKCanvas(scaledContext)
                    DrawBitmapSampled(scaledCanvas, result,
                                      New SKRect(contextLeft, contextTop, contextRight, contextBottom),
                                      New SKRect(0, 0, scaledContextWidth, scaledContextHeight),
                                      SamplingHigh, Nothing)
                End Using

                Using scaledDefect = New SKBitmap(scaledMaskWidth, scaledMaskHeight,
                                                  SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using maskCanvas = New SKCanvas(scaledDefect)
                        maskCanvas.Clear(SKColors.Transparent)
                        DrawBitmapSampled(maskCanvas, defectMask,
                                          New SKRect(0, 0, defectMask.Width, defectMask.Height),
                                          New SKRect(0, 0, scaledMaskWidth, scaledMaskHeight),
                                          SamplingHigh, Nothing)
                    End Using

                    ' Innerhalb des kleinen Arbeitsbilds voll rekonstruieren. Die sichtbare
                    ' Staerke/Deckkraft wird erst beim Rueckweg durch blendMask angewendet.
                    Using scaledFullBlend = New SKBitmap(scaledMaskWidth, scaledMaskHeight,
                                                         SKColorType.Bgra8888, SKAlphaType.Premul)
                        Using fullBlendCanvas = New SKCanvas(scaledFullBlend)
                            fullBlendCanvas.Clear(SKColors.White)
                        End Using
                        If Not DrawInpaintedHealingRegion(scaledContext, scaledDefect, scaledFullBlend,
                                                         scaledTargetLeft, scaledTargetTop,
                                                         Math.Max(1, CInt(resolutionLongEdge * scale))) Then Return False

                        Using resultCanvas = New SKCanvas(result)
                            Dim bounds = New SKRect(targetLeft, targetTop,
                                                    targetLeft + defectMask.Width,
                                                    targetTop + defectMask.Height)
                            resultCanvas.SaveLayer(bounds, Nothing)
                            DrawBitmapSampled(resultCanvas, scaledContext,
                                              New SKRect(scaledTargetLeft, scaledTargetTop,
                                                         scaledTargetLeft + scaledMaskWidth,
                                                         scaledTargetTop + scaledMaskHeight),
                                              bounds, SamplingHigh, Nothing)
                            Using maskPaint = New SKPaint With {
                                .BlendMode = SKBlendMode.DstIn,
                                .IsAntialias = False
                            }
                                resultCanvas.DrawBitmap(blendMask, targetLeft, targetTop, maskPaint)
                            End Using
                            resultCanvas.Restore()
                        End Using
                    End Using
                End Using
            End Using

            Return True
        End Function

        Private Shared Function DrawPatchBasedInpaintedHealingRegion(resolutionLongEdge As Integer,
                                                                     result As SKBitmap, work As SKBitmap,
                                                                     maskAlpha As Byte(), blendAlpha As Byte(),
                                                                     targetLeft As Integer, targetTop As Integer,
                                                                     width As Integer, height As Integer,
                                                                     resultOriginX As Integer, resultOriginY As Integer) As Boolean
            If result Is Nothing OrElse work Is Nothing OrElse maskAlpha Is Nothing OrElse blendAlpha Is Nothing OrElse
               maskAlpha.Length <> blendAlpha.Length OrElse width <= 0 OrElse height <= 0 Then Return False

            Dim known(width * height - 1) As Boolean
            Dim remaining = 0
            For i = 0 To known.Length - 1
                known(i) = maskAlpha(i) <= 8
                If Not known(i) Then remaining += 1
            Next
            If remaining = 0 Then Return False

            Dim repairExtent = Math.Max(width, height)
            ' BEFUND: groessere Patches (Radius 6) tragen mehr Struktur pro Kopie und brauchen
            ' weniger Suchen - bezahlbar, seit die Kandidaten vorberechnet sind.
            Dim patchRadius = HealingPatchRadiusFor(resolutionLongEdge)
            Dim maxPasses = Math.Min(repairExtent + patchRadius * 2, 160)
            Dim maxPatchCopiesPerPass = If(repairExtent > 360, 72, If(repairExtent > 240, 96, If(repairExtent > 120, 96, 64)))
            Dim repaired = 0

            ' QUALITAET: Pixelpuffer ueber den gesamten Suchbereich (Zielrechteck +
            ' maximale Suchreichweite) - damit wird HealingPatchScore rein managed und die Suche kann
            ' sich DICHTE leisten (kleinere Schrittweite, mehr Kandidaten, Verfeinerung immer), statt
            ' angrenzende Struktur wegen GetPixel-Kosten grob zu ueberspringen. CopyHealingPatch
            ' spiegelt seine Schreibzugriffe in den Puffer, damit Scores im selben Pass frisch bleiben.
            Dim searchMargin = HealingSearchBaseMargin + patchRadius + 8
            Dim pixels = RegionPixelBuffer.FromRegion(work, targetLeft - searchMargin, targetTop - searchMargin,
                                                      targetLeft + width - 1 + searchMargin, targetTop + height - 1 + searchMargin)

            ' BEFUND: gueltige Quell-Patches EINMAL vorberechnen (Praefixsumme + Bucket-Grid);
            ' die Suche pro Randpixel zieht daraus nur noch die naechsten echten Kandidaten.
            Dim candidates = New HealSourceCandidates(maskAlpha, targetLeft, targetTop, width, height,
                                                      patchRadius, searchMargin, work.Width, work.Height)
            Dim candidateScratch As New List(Of (X As Integer, Y As Integer))(256)
            If candidates.Count = 0 Then Return False

            ' BUDGET: Die Patch-Suche ist fuer FLECKEN gebaut. Ein langer Pinselzug
            ' erzeugt eine Riesen-Region, in der fast alle Kandidaten verworfen werden (Umgebung
            ' selbst maskiert) - ungedeckelt wurden daraus Milliarden Array-Reads (Log: 64 s fuer
            ' EINEN Zug). Das Budget zaehlt geprüfte Kandidaten ueber die GESAMTE Region; ist es
            ' erschoepft, fuellt der bestehende Fallback (FillRemainingInpaintedPixels + Rand-
            ' Blending) den Rest - sichtbar weicher, aber in Sekundenbruchteilen statt Minuten.
            Dim searchBudget As Long = 3_000_000

            For pass = 0 To maxPasses - 1
                Dim boundary As New List(Of Integer)()
                For maskY = 0 To height - 1
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        If known(index) Then Continue For
                        If HasKnownNeighbor(known, width, height, mx, maskY) Then boundary.Add(index)
                    Next
                Next

                If boundary.Count = 0 Then Exit For
                OrderHealingBoundaryByKnownNeighbors(boundary, known, width, height)
                Dim changedThisPass = 0
                Dim patchCopiesThisPass = 0
                For Each index In boundary
                    If known(index) Then Continue For
                    If searchBudget <= 0 Then Exit For
                    Dim mx = index Mod width
                    Dim maskY = index \ width
                    Dim sourcePatch = FindBestHealingSourcePatch(work, maskAlpha, known, targetLeft, targetTop,
                                                                 width, height, mx, maskY, patchRadius, pixels,
                                                                 candidates, candidateScratch, searchBudget)
                    If Not sourcePatch.Found Then Continue For

                    changedThisPass += CopyHealingPatch(work, maskAlpha, known, targetLeft, targetTop,
                                                        width, height, mx, maskY, sourcePatch.X, sourcePatch.Y, patchRadius, pixels)
                    patchCopiesThisPass += 1
                    If patchCopiesThisPass >= maxPatchCopiesPerPass Then Exit For
                Next

                If searchBudget <= 0 Then Exit For
                If changedThisPass = 0 Then Exit For
                repaired += changedThisPass
                remaining -= changedThisPass
                If remaining <= 0 Then Exit For
            Next

            If remaining > 0 Then
                Dim filledByFallback = FillRemainingInpaintedPixels(work, maskAlpha, known, targetLeft, targetTop, width, height)
                repaired += filledByFallback
                remaining -= filledByFallback
            End If

            If repaired = 0 Then Return False
            BlendInpaintedBoundary(work, maskAlpha, targetLeft, targetTop, width, height)
            For maskY = 0 To height - 1
                Dim y = targetTop + maskY
                If y < 0 OrElse y >= work.Height Then Continue For
                For mx = 0 To width - 1
                    Dim index = maskY * width + mx
                    If blendAlpha(index) <= 8 OrElse Not known(index) Then Continue For
                    Dim workX = targetLeft + mx
                    Dim resultX = resultOriginX + workX
                    Dim resultY = resultOriginY + y
                    If resultX < 0 OrElse resultX >= result.Width OrElse
                       resultY < 0 OrElse resultY >= result.Height Then Continue For

                    Dim localAlpha = Clamp(blendAlpha(index) / 255.0F, 0.0F, 1.0F)
                    Dim target = result.GetPixel(resultX, resultY)
                    Dim repairedColor = work.GetPixel(workX, y)
                    result.SetPixel(resultX, resultY, New SKColor(
                        BlendByte(target.Red, repairedColor.Red, localAlpha),
                        BlendByte(target.Green, repairedColor.Green, localAlpha),
                        BlendByte(target.Blue, repairedColor.Blue, localAlpha),
                        BlendByte(target.Alpha, repairedColor.Alpha, localAlpha)))
                Next
            Next

            Return True
        End Function

        Private Shared Function FillRemainingInpaintedPixels(work As SKBitmap, maskAlpha As Byte(), known As Boolean(),
                                                             targetLeft As Integer, targetTop As Integer,
                                                             width As Integer, height As Integer) As Integer
            If work Is Nothing OrElse maskAlpha Is Nothing OrElse known Is Nothing Then Return 0

            Dim queued(width * height - 1) As Boolean
            Dim queue As New Queue(Of Integer)()
            For maskY = 0 To height - 1
                For mx = 0 To width - 1
                    Dim index = maskY * width + mx
                    If known(index) OrElse maskAlpha(index) <= 8 Then Continue For
                    If Not HasKnownNeighbor(known, width, height, mx, maskY) Then Continue For
                    queue.Enqueue(index)
                    queued(index) = True
                Next
            Next

            Dim repaired = 0
            While queue.Count > 0
                Dim index = queue.Dequeue()
                queued(index) = False
                If known(index) OrElse maskAlpha(index) <= 8 Then Continue While

                Dim mx = index Mod width
                Dim maskY = index \ width
                Dim x = targetLeft + mx
                Dim y = targetTop + maskY
                If x < 0 OrElse y < 0 OrElse x >= work.Width OrElse y >= work.Height Then
                    known(index) = True
                    Continue While
                End If

                Dim average = AverageFilledNeighborhood(work, known, targetLeft, targetTop, width, height, mx, maskY)
                If Not average.HasValue Then average = AverageUnmaskedRays(work, maskAlpha, targetLeft, targetTop, width, height, mx, maskY)
                If Not average.HasValue Then Continue While

                work.SetPixel(x, y, average.Value)
                known(index) = True
                repaired += 1

                For oy = -1 To 1
                    For ox = -1 To 1
                        If ox = 0 AndAlso oy = 0 Then Continue For
                        Dim nx = mx + ox
                        Dim ny = maskY + oy
                        If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then Continue For
                        Dim ni = ny * width + nx
                        If known(ni) OrElse queued(ni) OrElse maskAlpha(ni) <= 8 Then Continue For
                        queue.Enqueue(ni)
                        queued(ni) = True
                    Next
                Next
            End While

            Return repaired
        End Function

        Private Shared Sub BlendInpaintedBoundary(work As SKBitmap, maskAlpha As Byte(),
                                                  targetLeft As Integer, targetTop As Integer,
                                                  width As Integer, height As Integer)
            If work Is Nothing OrElse maskAlpha Is Nothing Then Return

            Dim nextColors(width * height - 1) As SKColor
            Dim hasNext(width * height - 1) As Boolean
            For maskY = 0 To height - 1
                Dim y = targetTop + maskY
                If y < 0 OrElse y >= work.Height Then Continue For
                For mx = 0 To width - 1
                    Dim index = maskY * width + mx
                    If maskAlpha(index) <= 8 OrElse Not IsMaskBoundary(maskAlpha, width, height, mx, maskY) Then Continue For
                    Dim x = targetLeft + mx
                    If x < 0 OrElse x >= work.Width Then Continue For

                    Dim avg = AverageBoundaryBlendColor(work, maskAlpha, targetLeft, targetTop, width, height, mx, maskY)
                    If avg.HasValue Then
                        nextColors(index) = avg.Value
                        hasNext(index) = True
                    End If
                Next
            Next

            For maskY = 0 To height - 1
                Dim y = targetTop + maskY
                If y < 0 OrElse y >= work.Height Then Continue For
                For mx = 0 To width - 1
                    Dim index = maskY * width + mx
                    If Not hasNext(index) Then Continue For
                    Dim x = targetLeft + mx
                    If x < 0 OrElse x >= work.Width Then Continue For
                    Dim current = work.GetPixel(x, y)
                    Dim blended = nextColors(index)
                    work.SetPixel(x, y, New SKColor(
                        BlendByte(current.Red, blended.Red, 0.45F),
                        BlendByte(current.Green, blended.Green, 0.45F),
                        BlendByte(current.Blue, blended.Blue, 0.45F),
                        BlendByte(current.Alpha, blended.Alpha, 0.45F)))
                Next
            Next
        End Sub

        Private Shared Function IsMaskBoundary(maskAlpha As Byte(), width As Integer, height As Integer,
                                               mx As Integer, my As Integer) As Boolean
            For oy = -1 To 1
                For ox = -1 To 1
                    If ox = 0 AndAlso oy = 0 Then Continue For
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then Return True
                    If maskAlpha(ny * width + nx) <= 8 Then Return True
                Next
            Next
            Return False
        End Function

        Private Shared Function AverageBoundaryBlendColor(work As SKBitmap, maskAlpha As Byte(),
                                                          targetLeft As Integer, targetTop As Integer,
                                                          width As Integer, height As Integer,
                                                          mx As Integer, my As Integer) As SKColor?
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim weightSum = 0
            For oy = -2 To 2
                For ox = -2 To 2
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    Dim x = targetLeft + nx
                    Dim y = targetTop + ny
                    If x < 0 OrElse y < 0 OrElse x >= work.Width OrElse y >= work.Height Then Continue For

                    Dim weight = If(Math.Abs(ox) <= 1 AndAlso Math.Abs(oy) <= 1, 3, 1)
                    If nx >= 0 AndAlso ny >= 0 AndAlso nx < width AndAlso ny < height AndAlso
                       maskAlpha(ny * width + nx) > 8 Then
                        weight += 1
                    End If

                    Dim c = work.GetPixel(x, y)
                    sr += CInt(c.Red) * weight
                    sg += CInt(c.Green) * weight
                    sb += CInt(c.Blue) * weight
                    sa += CInt(c.Alpha) * weight
                    weightSum += weight
                Next
            Next
            If weightSum = 0 Then Return Nothing
            Return New SKColor(CByte(sr \ weightSum), CByte(sg \ weightSum),
                               CByte(sb \ weightSum), CByte(sa \ weightSum))
        End Function

        ''' <summary>Reihenfolge der Fuellfront: die Pixel mit den meisten bekannten Nachbarn zuerst.
        '''
        ''' GEPRUEFT UND VERWORFEN: ein zusaetzlicher STRUKTUR-Term nach Art der Isophoten-Prioritaet
        ''' (Helligkeitsgradient ueber die bekannten Nachbarn, Anteil entlang der Frontnormalen,
        ''' multiplikativ auf die Nachbarzahl). Er sollte den reproduzierten Fehler an einer Grenze
        ''' zwischen zwei Bereichen beheben (Horizont Himmel/Wiese: die Linie bricht ein, Himmel
        ''' blutet nach unten). Gemessen brachte er dort nur 22,6 -> 19,7 px Abweichung bei
        ''' unveraendertem Maximum, also keine Loesung, und am 50-MP-Foto zog er den Kontrast im
        ''' reparierten Bereich von 16,0 auf 17,5 weg vom Original (16,7). Auch eine strikte
        ''' Prioritaetsreihenfolge (Kopierbudget je Durchgang von 96 auf 6) blieb bei 18,9 px.
        ''' Der Grund liegt tiefer: eine Reihenfolge nuetzt wenig, solange die Patch-BEWERTUNG nur
        ''' den bekannten Teil des Zielfensters sieht - am oberen Rand ist der bekannt Teil reiner
        ''' Himmel, also gewinnt ein reiner Himmel-Patch und wird nach unten kopiert.</summary>
        Private Shared Sub OrderHealingBoundaryByKnownNeighbors(boundary As List(Of Integer), known As Boolean(),
                                                                width As Integer, height As Integer)
            If boundary Is Nothing OrElse boundary.Count < 2 Then Return
            boundary.Sort(Function(a, b)
                              Dim ax = a Mod width
                              Dim ay = a \ width
                              Dim bx = b Mod width
                              Dim by = b \ width
                              Return CountKnownNeighbors(known, width, height, bx, by).CompareTo(
                                     CountKnownNeighbors(known, width, height, ax, ay))
                          End Function)
        End Sub

        Private Shared Function CountKnownNeighbors(known As Boolean(), width As Integer, height As Integer,
                                                    mx As Integer, my As Integer) As Integer
            Dim count = 0
            For oy = -1 To 1
                For ox = -1 To 1
                    If ox = 0 AndAlso oy = 0 Then Continue For
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then
                        count += 1
                    ElseIf known(ny * width + nx) Then
                        count += 1
                    End If
                Next
            Next
            Return count
        End Function

        ''' <summary>BEFUND: Vorberechnete Quell-Kandidaten fuer die Heal-Patch-Suche. Die alte
        ''' Blindsuche prüfte pro Randpixel ein Raster um den ZIELPUNKT - mitten in einem breiten Zug
        ''' ist dort alles selbst maskiert, fast alle Kandidaten wurden verworfen (Budget verbrannt)
        ''' und die Zugmitte fiel auf den strukturlosen Mittelwert-Fallback zurück. Hier wird EINMAL
        ''' pro Region über eine 2D-Präfixsumme der Maske ein Gitter aller Positionen bestimmt, deren
        ''' kompletter Patch unmaskiert im Bild liegt, räumlich in 32-px-Buckets abgelegt. Die Suche
        ''' zieht dann pro Randpixel nur noch die NÄCHSTEN echten Kandidaten - ohne harten
        ''' Radius-Deckel, die Mitte bekommt Struktur von den Rändern.</summary>
        Private NotInheritable Class HealSourceCandidates
            Private Const BucketSize As Integer = 32
            Private ReadOnly _buckets As Dictionary(Of Long, List(Of (X As Integer, Y As Integer)))
            Private ReadOnly _integral As Integer()   ' Präfixsumme "maskiert" über das Suchfenster
            Private ReadOnly _winLeft As Integer
            Private ReadOnly _winTop As Integer
            Private ReadOnly _winWidth As Integer
            Private ReadOnly _winHeight As Integer
            Public ReadOnly Count As Integer

            Public Sub New(maskAlpha As Byte(), targetLeft As Integer, targetTop As Integer,
                           width As Integer, height As Integer, patchRadius As Integer,
                           margin As Integer, bitmapWidth As Integer, bitmapHeight As Integer)
                _winLeft = Math.Max(0, targetLeft - margin)
                _winTop = Math.Max(0, targetTop - margin)
                Dim winRight = Math.Min(bitmapWidth - 1, targetLeft + width - 1 + margin)
                Dim winBottom = Math.Min(bitmapHeight - 1, targetTop + height - 1 + margin)
                _winWidth = Math.Max(0, winRight - _winLeft + 1)
                _winHeight = Math.Max(0, winBottom - _winTop + 1)
                _buckets = New Dictionary(Of Long, List(Of (X As Integer, Y As Integer)))()
                If _winWidth <= 0 OrElse _winHeight <= 0 Then
                    _integral = Array.Empty(Of Integer)()
                    Return
                End If

                ' Präfixsumme: integral(y,x) = Anzahl maskierter Pixel im Rechteck [0..x)x[0..y).
                _integral = New Integer((_winWidth + 1) * (_winHeight + 1) - 1) {}
                Dim stride = _winWidth + 1
                For y = 0 To _winHeight - 1
                    Dim rowSum = 0
                    Dim absY = _winTop + y
                    Dim maskRow = (absY - targetTop) * width
                    For x = 0 To _winWidth - 1
                        Dim absX = _winLeft + x
                        Dim masked = 0
                        If absX >= targetLeft AndAlso absY >= targetTop AndAlso
                           absX < targetLeft + width AndAlso absY < targetTop + height AndAlso
                           maskAlpha(maskRow + (absX - targetLeft)) > 8 Then
                            masked = 1
                        End If
                        rowSum += masked
                        _integral((y + 1) * stride + (x + 1)) = _integral(y * stride + (x + 1)) + rowSum
                    Next
                Next

                ' Kandidaten-Gitter (Schritt 3): kompletter Patch im Bild UND unmaskiert.
                Dim total = 0
                For y = patchRadius To _winHeight - 1 - patchRadius Step 3
                    For x = patchRadius To _winWidth - 1 - patchRadius Step 3
                        Dim absX = _winLeft + x
                        Dim absY = _winTop + y
                        If absX < patchRadius OrElse absY < patchRadius OrElse
                           absX >= bitmapWidth - patchRadius OrElse absY >= bitmapHeight - patchRadius Then Continue For
                        If Not IsPatchClear(absX, absY, patchRadius) Then Continue For
                        Dim key = BucketKey(absX, absY)
                        Dim list As List(Of (X As Integer, Y As Integer)) = Nothing
                        If Not _buckets.TryGetValue(key, list) Then
                            list = New List(Of (X As Integer, Y As Integer))()
                            _buckets(key) = list
                        End If
                        list.Add((absX, absY))
                        total += 1
                    Next
                Next
                Count = total
            End Sub

            Private Shared Function BucketKey(x As Integer, y As Integer) As Long
                Return (CLng(y \ BucketSize) << 24) Or CLng(x \ BucketSize)
            End Function

            ''' Patch um (x,y) komplett unmaskiert? O(1) über die Präfixsumme (Bereiche ausserhalb
            ''' des Fensters gelten als unmaskiert - dort liegt keine Maske).
            Public Function IsPatchClear(x As Integer, y As Integer, patchRadius As Integer) As Boolean
                If _winWidth <= 0 Then Return True
                Dim x0 = Math.Max(0, x - patchRadius - _winLeft)
                Dim y0 = Math.Max(0, y - patchRadius - _winTop)
                Dim x1 = Math.Min(_winWidth - 1, x + patchRadius - _winLeft)
                Dim y1 = Math.Min(_winHeight - 1, y + patchRadius - _winTop)
                If x1 < x0 OrElse y1 < y0 Then Return True
                Dim stride = _winWidth + 1
                Dim masked = _integral((y1 + 1) * stride + (x1 + 1)) -
                             _integral(y0 * stride + (x1 + 1)) -
                             _integral((y1 + 1) * stride + x0) +
                             _integral(y0 * stride + x0)
                Return masked = 0
            End Function

            ''' Sammelt bis zu maxCount Kandidaten in ringförmig wachsenden Bucket-Schalen um das
            ''' Ziel - grob nach Nähe geordnet; die Feinordnung erledigt der Distanz-Malus im Score.
            Public Sub CollectNearest(targetX As Integer, targetY As Integer, maxCount As Integer,
                                      results As List(Of (X As Integer, Y As Integer)))
                results.Clear()
                If _buckets.Count = 0 Then Return
                Dim centerBx = targetX \ BucketSize
                Dim centerBy = targetY \ BucketSize
                Dim maxRing = Math.Max(_winWidth, _winHeight) \ BucketSize + 2
                For ring = 0 To maxRing
                    For by = centerBy - ring To centerBy + ring
                        For bx = centerBx - ring To centerBx + ring
                            If bx < 0 OrElse by < 0 Then Continue For
                            ' Nur die Schale, nicht das Innere (das lieferten schon kleinere Ringe).
                            If ring > 0 AndAlso Math.Abs(bx - centerBx) <> ring AndAlso Math.Abs(by - centerBy) <> ring Then Continue For
                            Dim list As List(Of (X As Integer, Y As Integer)) = Nothing
                            If Not _buckets.TryGetValue((CLng(by) << 24) Or CLng(bx), list) Then Continue For
                            results.AddRange(list)
                        Next
                    Next
                    If results.Count >= maxCount Then Exit For
                Next
                If results.Count > maxCount Then results.RemoveRange(maxCount, results.Count - maxCount)
            End Sub
        End Class

        ''' QUALITAET: Suche über vorberechnete Kandidaten (HealSourceCandidates) -
        ''' das Budget fliesst komplett in echte Struktur-Vergleiche, und ohne harten Radius-Deckel
        ''' erreicht auch die Mitte breiter Züge die Struktur der Ränder. Der Distanz-Malus im Score
        ''' lässt bei gleicher Ähnlichkeit weiterhin die NÄCHSTE Struktur gewinnen.
        Private Shared Function FindBestHealingSourcePatch(work As SKBitmap, maskAlpha As Byte(), known As Boolean(),
                                                           targetLeft As Integer, targetTop As Integer,
                                                           width As Integer, height As Integer,
                                                           mx As Integer, my As Integer,
                                                           patchRadius As Integer,
                                                           pixels As RegionPixelBuffer,
                                                           candidates As HealSourceCandidates,
                                                           scratch As List(Of (X As Integer, Y As Integer)),
                                                           ByRef searchBudget As Long) As (X As Integer, Y As Integer, Found As Boolean)
            Dim targetX = targetLeft + mx
            Dim targetY = targetTop + my
            Dim extent = Math.Max(width, height)
            Dim maxCandidates = If(extent > 360, 140, 220)

            candidates.CollectNearest(targetX, targetY, maxCandidates, scratch)

            Dim bestX = 0
            Dim bestY = 0
            Dim bestScore = Double.MaxValue
            Dim found = False
            For Each candidate In scratch
                If Math.Abs(candidate.X - targetX) <= patchRadius AndAlso Math.Abs(candidate.Y - targetY) <= patchRadius Then Continue For
                searchBudget -= 1
                If searchBudget <= 0 Then Exit For
                Dim score = HealingPatchScore(work, maskAlpha, known, targetLeft, targetTop,
                                              width, height, mx, my, candidate.X, candidate.Y, patchRadius, pixels)
                If score < bestScore Then
                    bestScore = score
                    bestX = candidate.X
                    bestY = candidate.Y
                    found = True
                End If
            Next

            If found AndAlso searchBudget > 0 Then
                ' Pixelgenaue Verfeinerung um den besten Treffer (das Kandidaten-Gitter hat Schritt 3).
                For sy = Math.Max(patchRadius, bestY - 2) To Math.Min(work.Height - patchRadius - 1, bestY + 2)
                    For sx = Math.Max(patchRadius, bestX - 2) To Math.Min(work.Width - patchRadius - 1, bestX + 2)
                        If sx = bestX AndAlso sy = bestY Then Continue For
                        If Math.Abs(sx - targetX) <= patchRadius AndAlso Math.Abs(sy - targetY) <= patchRadius Then Continue For
                        If Not candidates.IsPatchClear(sx, sy, patchRadius) Then Continue For
                        searchBudget -= 1
                        Dim score = HealingPatchScore(work, maskAlpha, known, targetLeft, targetTop,
                                                      width, height, mx, my, sx, sy, patchRadius, pixels)
                        If score < bestScore Then
                            bestScore = score
                            bestX = sx
                            bestY = sy
                        End If
                    Next
                Next
            End If

            Return (bestX, bestY, found)
        End Function

        Private Shared Function HealingPatchScore(work As SKBitmap, maskAlpha As Byte(), known As Boolean(),
                                                  targetLeft As Integer, targetTop As Integer,
                                                  width As Integer, height As Integer,
                                                  mx As Integer, my As Integer,
                                                  sx As Integer, sy As Integer,
                                                  patchRadius As Integer,
                                                  pixels As RegionPixelBuffer) As Double
            Dim score = 0.0
            Dim count = 0
            Dim targetX = targetLeft + mx
            Dim targetY = targetTop + my

            For oy = -patchRadius To patchRadius
                Dim oySq = oy * oy
                Dim ty = targetY + oy
                Dim py = sy + oy
                If ty < 0 OrElse ty >= work.Height OrElse py < 0 OrElse py >= work.Height Then Continue For
                For ox = -patchRadius To patchRadius
                    If ox * ox + oySq > patchRadius * patchRadius Then Continue For
                    Dim tx = targetX + ox
                    Dim px = sx + ox
                    If tx < 0 OrElse tx >= work.Width OrElse px < 0 OrElse px >= work.Width Then Continue For

                    Dim lx = mx + ox
                    Dim ly = my + oy
                    Dim targetKnown = True
                    If lx >= 0 AndAlso ly >= 0 AndAlso lx < width AndAlso ly < height Then
                        targetKnown = known(ly * width + lx)
                    End If
                    If Not targetKnown Then Continue For

                    Dim distance = Math.Max(Math.Abs(ox), Math.Abs(oy))
                    Dim weight = If(distance <= 1, 5.0, If(distance <= 3, 2.0, 1.0))
                    Dim targetColor = If(pixels IsNot Nothing AndAlso pixels.Contains(tx, ty), pixels.GetColor(tx, ty), work.GetPixel(tx, ty))
                    Dim patchColor = If(pixels IsNot Nothing AndAlso pixels.Contains(px, py), pixels.GetColor(px, py), work.GetPixel(px, py))
                    score += ColorDistanceSquared(targetColor, patchColor) * weight
                    count += CInt(weight)
                Next
            Next

            If count < Math.Max(8, patchRadius * patchRadius \ 2) Then Return Double.MaxValue
            Dim dx = sx - targetX
            Dim dy = sy - targetY
            Dim distancePenalty = Math.Sqrt(dx * dx + dy * dy) * 1.8
            Return score / count + distancePenalty
        End Function

        Private Shared Function CopyHealingPatch(work As SKBitmap, maskAlpha As Byte(), known As Boolean(),
                                                 targetLeft As Integer, targetTop As Integer,
                                                 width As Integer, height As Integer,
                                                 mx As Integer, my As Integer,
                                                 sx As Integer, sy As Integer,
                                                 patchRadius As Integer,
                                                 Optional pixels As RegionPixelBuffer = Nothing) As Integer
            Dim copied = 0
            Dim targetX = targetLeft + mx
            Dim targetY = targetTop + my
            ' UEBERLAPPUNG: bereits gefuellte Zielpixel werden mit einem STETIGEN Gewicht
            ' ueberblendet, das von der Patch-Mitte (voll) zum Patch-Rand (null) ausläuft.
            ' Ungefuellte bekommen weiterhin die volle Kopie (kein Durchbluten des Defekts).
            '
            ' BEFUND, der dazu gefuehrt hat: vorher blieben gefuellte Pixel INNERHALB von
            ' Radius-1,5 unangetastet und nur der aeussere Ring wurde hart 50/50 gemischt - also
            ' genau umgekehrt zur Verlaesslichkeit (die Patch-Mitte traegt die beste Struktur, der
            ' Rand die schlechteste) und mit ZWEI Stufen im Verlauf statt einer Rampe. Die Naehte
            ' daraus sammeln sich dort, wo zuletzt gefuellt wird: auf der MITTELLINIE des Zuges.
            ' Gemessen an einem 120x2000-Zug ueber Wolkentextur lag die Hochfrequenz dort bei 2,23
            ' gegen 1,27 in der Nachbarschaft - sichtbar als unsaubere Bahn in der Zugmitte.
            Dim featherRadius = Math.Max(1.0F, CSng(patchRadius))

            For oy = -patchRadius To patchRadius
                Dim oySq = oy * oy
                Dim y = targetY + oy
                Dim py = sy + oy
                If y < 0 OrElse y >= work.Height OrElse py < 0 OrElse py >= work.Height Then Continue For
                For ox = -patchRadius To patchRadius
                    Dim distSq = ox * ox + oySq
                    If distSq > patchRadius * patchRadius Then Continue For
                    Dim lx = mx + ox
                    Dim ly = my + oy
                    If lx < 0 OrElse ly < 0 OrElse lx >= width OrElse ly >= height Then Continue For
                    Dim index = ly * width + lx
                    If maskAlpha(index) <= 8 Then Continue For

                    Dim x = targetX + ox
                    Dim px = sx + ox
                    If x < 0 OrElse x >= work.Width OrElse px < 0 OrElse px >= work.Width Then Continue For

                    If known(index) Then
                        Dim w = 1.0F - CSng(Math.Sqrt(distSq)) / featherRadius
                        If w > 0.0F Then
                            ' Smoothstep: am Patch-Rand laeuft auch die ABLEITUNG auf null aus,
                            ' sonst bleibt dort eine sichtbare Knickkante stehen.
                            w = w * w * (3.0F - 2.0F * w)
                            Dim existing = work.GetPixel(x, y)
                            Dim incoming = work.GetPixel(px, py)
                            Dim blended = New SKColor(
                                BlendByte(existing.Red, incoming.Red, w),
                                BlendByte(existing.Green, incoming.Green, w),
                                BlendByte(existing.Blue, incoming.Blue, w),
                                existing.Alpha)
                            work.SetPixel(x, y, blended)
                            If pixels IsNot Nothing AndAlso pixels.Contains(x, y) Then pixels.SetColor(x, y, blended)
                        End If
                        Continue For
                    End If

                    Dim sourceColor = work.GetPixel(px, py)
                    work.SetPixel(x, y, sourceColor)
                    ' Puffer synchron halten - Scores im selben Pass sehen sonst den alten (defekten)
                    ' Inhalt unter frisch kopierten Pixeln.
                    If pixels IsNot Nothing AndAlso pixels.Contains(x, y) Then pixels.SetColor(x, y, sourceColor)
                    known(index) = True
                    copied += 1
                Next
            Next

            Return copied
        End Function

        Private Shared Function IsOriginalKnownPatch(maskAlpha As Byte(),
                                                     targetLeft As Integer, targetTop As Integer,
                                                     width As Integer, height As Integer,
                                                     sx As Integer, sy As Integer,
                                                     patchRadius As Integer) As Boolean
            For oy = -patchRadius To patchRadius
                Dim y = sy + oy
                Dim oySq = oy * oy
                For ox = -patchRadius To patchRadius
                    If ox * ox + oySq > patchRadius * patchRadius Then Continue For
                    Dim x = sx + ox
                    Dim mx = x - targetLeft
                    Dim my = y - targetTop
                    If mx >= 0 AndAlso my >= 0 AndAlso mx < width AndAlso my < height AndAlso
                       maskAlpha(my * width + mx) > 8 Then Return False
                Next
            Next
            Return True
        End Function

        Private Shared Function HasKnownNeighbor(known As Boolean(), width As Integer, height As Integer,
                                                 mx As Integer, my As Integer) As Boolean
            For oy = -1 To 1
                For ox = -1 To 1
                    If ox = 0 AndAlso oy = 0 Then Continue For
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then Return True
                    If known(ny * width + nx) Then Return True
                Next
            Next
            Return False
        End Function

        Private Shared Function ShouldSmoothInpaintedRegion(work As SKBitmap, maskAlpha As Byte(),
                                                            targetLeft As Integer, targetTop As Integer,
                                                            width As Integer, height As Integer) As Boolean
            Dim stepSize = Math.Max(1, Math.Max(width, height) \ 28)
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0
            Dim sr2 As Long = 0, sg2 As Long = 0, sb2 As Long = 0
            Dim count = 0

            For maskY = 0 To height - 1 Step stepSize
                Dim y = targetTop + maskY
                If y < 0 OrElse y >= work.Height Then Continue For
                For mx = 0 To width - 1 Step stepSize
                    If maskAlpha(maskY * width + mx) < 255 Then Continue For
                    Dim x = targetLeft + mx
                    If x < 0 OrElse x >= work.Width Then Continue For
                    Dim c = work.GetPixel(x, y)
                    sr += c.Red : sg += c.Green : sb += c.Blue
                    sr2 += CInt(c.Red) * CInt(c.Red)
                    sg2 += CInt(c.Green) * CInt(c.Green)
                    sb2 += CInt(c.Blue) * CInt(c.Blue)
                    count += 1
                Next
            Next

            If count < 12 Then Return False
            Dim ar = CDbl(sr) / count
            Dim ag = CDbl(sg) / count
            Dim ab = CDbl(sb) / count
            Dim variance = Math.Max(0.0, CDbl(sr2) / count - ar * ar) +
                           Math.Max(0.0, CDbl(sg2) / count - ag * ag) +
                           Math.Max(0.0, CDbl(sb2) / count - ab * ab)
            Return variance < 95.0
        End Function

        Private Shared Sub SmoothInpaintedRegion(work As SKBitmap, maskAlpha As Byte(),
                                                 targetLeft As Integer, targetTop As Integer,
                                                 width As Integer, height As Integer)
            If work Is Nothing OrElse maskAlpha Is Nothing OrElse width <= 0 OrElse height <= 0 Then Return

            Dim iterations = If(Math.Max(width, height) > 72, 4, 3)
            For iteration = 1 To iterations
                Dim nextColors(width * height - 1) As SKColor
                Dim hasNext(width * height - 1) As Boolean

                For maskY = 0 To height - 1
                    Dim y = targetTop + maskY
                    If y < 0 OrElse y >= work.Height Then Continue For
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        If maskAlpha(index) < 255 Then Continue For
                        Dim x = targetLeft + mx
                        If x < 0 OrElse x >= work.Width Then Continue For

                        Dim smoothed = AverageRepairNeighborhood(work, maskAlpha, targetLeft, targetTop, width, height, mx, maskY)
                        If smoothed.HasValue Then
                            nextColors(index) = smoothed.Value
                            hasNext(index) = True
                        End If
                    Next
                Next

                For maskY = 0 To height - 1
                    Dim y = targetTop + maskY
                    If y < 0 OrElse y >= work.Height Then Continue For
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        If Not hasNext(index) Then Continue For
                        Dim x = targetLeft + mx
                        If x < 0 OrElse x >= work.Width Then Continue For
                        work.SetPixel(x, y, nextColors(index))
                    Next
                Next
            Next
        End Sub

        Private Shared Function AverageRepairNeighborhood(work As SKBitmap, maskAlpha As Byte(),
                                                          targetLeft As Integer, targetTop As Integer,
                                                          width As Integer, height As Integer,
                                                          mx As Integer, my As Integer) As SKColor?
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim weightSum = 0

            For oy = -2 To 2
                For ox = -2 To 2
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    Dim x = targetLeft + nx
                    Dim y = targetTop + ny
                    If x < 0 OrElse y < 0 OrElse x >= work.Width OrElse y >= work.Height Then Continue For

                    Dim distance = Math.Max(Math.Abs(ox), Math.Abs(oy))
                    Dim weight = If(distance = 0, 8, If(distance = 1, 4, 1))
                    If nx >= 0 AndAlso ny >= 0 AndAlso nx < width AndAlso ny < height Then
                        Dim alpha = maskAlpha(ny * width + nx)
                        If alpha <= 8 Then
                            weight = 1
                        ElseIf alpha < 255 Then
                            weight = 2
                        End If
                    End If

                    Dim c = work.GetPixel(x, y)
                    sr += CInt(c.Red) * weight
                    sg += CInt(c.Green) * weight
                    sb += CInt(c.Blue) * weight
                    sa += CInt(c.Alpha) * weight
                    weightSum += weight
                Next
            Next

            If weightSum = 0 Then Return Nothing
            Return New SKColor(CByte(sr \ weightSum), CByte(sg \ weightSum),
                               CByte(sb \ weightSum), CByte(sa \ weightSum))
        End Function

        Private Shared Function NormalizeHealingMaskAlpha(alpha As Byte) As Byte
            If alpha <= 8 Then Return 0
            If alpha >= 24 Then Return 255
            Return CByte(Math.Min(255, CInt(alpha) * 11))
        End Function

        Private Shared Function HasFilledNeighbor(filled As Boolean(), width As Integer, height As Integer,
                                                  mx As Integer, my As Integer) As Boolean
            For oy = -1 To 1
                For ox = -1 To 1
                    If ox = 0 AndAlso oy = 0 Then Continue For
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then Return True
                    If filled(ny * width + nx) Then Return True
                Next
            Next
            Return False
        End Function

        Private Shared Function AverageFilledNeighborhood(work As SKBitmap, filled As Boolean(),
                                                          targetLeft As Integer, targetTop As Integer,
                                                          width As Integer, height As Integer,
                                                          mx As Integer, my As Integer) As SKColor?
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim weightSum = 0

            For oy = -2 To 2
                For ox = -2 To 2
                    If ox = 0 AndAlso oy = 0 Then Continue For
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    Dim x = targetLeft + nx
                    Dim y = targetTop + ny
                    If x < 0 OrElse y < 0 OrElse x >= work.Width OrElse y >= work.Height Then Continue For
                    If nx >= 0 AndAlso ny >= 0 AndAlso nx < width AndAlso ny < height AndAlso
                       Not filled(ny * width + nx) Then Continue For

                    Dim distance = Math.Max(Math.Abs(ox), Math.Abs(oy))
                    Dim weight = If(distance <= 1, 4, 1)
                    Dim c = work.GetPixel(x, y)
                    sr += CInt(c.Red) * weight
                    sg += CInt(c.Green) * weight
                    sb += CInt(c.Blue) * weight
                    sa += CInt(c.Alpha) * weight
                    weightSum += weight
                Next
            Next

            If weightSum = 0 Then Return Nothing
            Return New SKColor(CByte(sr \ weightSum), CByte(sg \ weightSum),
                               CByte(sb \ weightSum), CByte(sa \ weightSum))
        End Function

        Private Shared Function AverageUnmaskedRays(work As SKBitmap, maskAlpha As Byte(),
                                                    targetLeft As Integer, targetTop As Integer,
                                                    width As Integer, height As Integer,
                                                    mx As Integer, my As Integer) As SKColor?
            Dim directions = {
                (X:=0, Y:=-1, Weight:=7),
                (X:=-1, Y:=0, Weight:=5),
                (X:=1, Y:=0, Weight:=5),
                (X:=0, Y:=1, Weight:=4),
                (X:=-1, Y:=-1, Weight:=3),
                (X:=1, Y:=-1, Weight:=3),
                (X:=-1, Y:=1, Weight:=2),
                (X:=1, Y:=1, Weight:=2)
            }
            Dim samples As New List(Of (Color As SKColor, Weight As Integer))()
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim weightSum = 0
            Dim maxDistance = Math.Max(width, height)

            For Each direction In directions
                For distance = 1 To maxDistance
                    Dim nx = mx + direction.X * distance
                    Dim ny = my + direction.Y * distance
                    Dim x = targetLeft + nx
                    Dim y = targetTop + ny
                    If x < 0 OrElse y < 0 OrElse x >= work.Width OrElse y >= work.Height Then Exit For

                    If nx >= 0 AndAlso ny >= 0 AndAlso nx < width AndAlso ny < height AndAlso
                       maskAlpha(ny * width + nx) > 8 Then Continue For

                    Dim weight = Math.Max(1, (direction.Weight * 256) \ (distance * distance))
                    Dim c = work.GetPixel(x, y)
                    samples.Add((c, weight))
                    Exit For
                Next
            Next

            If samples.Count = 0 Then Return Nothing
            Dim median = MedianSampleColor(samples)
            Dim accepted = 0
            For Each sample In samples
                If samples.Count > 3 AndAlso ColorDistanceSquared(sample.Color, median) > 62 * 62 Then Continue For
                sr += CInt(sample.Color.Red) * sample.Weight
                sg += CInt(sample.Color.Green) * sample.Weight
                sb += CInt(sample.Color.Blue) * sample.Weight
                sa += CInt(sample.Color.Alpha) * sample.Weight
                weightSum += sample.Weight
                accepted += 1
            Next

            If accepted = 0 Then
                For Each sample In samples
                    sr += CInt(sample.Color.Red) * sample.Weight
                    sg += CInt(sample.Color.Green) * sample.Weight
                    sb += CInt(sample.Color.Blue) * sample.Weight
                    sa += CInt(sample.Color.Alpha) * sample.Weight
                    weightSum += sample.Weight
                Next
            End If

            If weightSum = 0 Then Return Nothing
            Return New SKColor(CByte(sr \ weightSum), CByte(sg \ weightSum),
                               CByte(sb \ weightSum), CByte(sa \ weightSum))
        End Function

        Private Shared Function MedianSampleColor(samples As List(Of (Color As SKColor, Weight As Integer))) As SKColor
            Dim reds As New List(Of Integer)(samples.Count)
            Dim greens As New List(Of Integer)(samples.Count)
            Dim blues As New List(Of Integer)(samples.Count)
            Dim alphas As New List(Of Integer)(samples.Count)
            For Each sample In samples
                reds.Add(sample.Color.Red)
                greens.Add(sample.Color.Green)
                blues.Add(sample.Color.Blue)
                alphas.Add(sample.Color.Alpha)
            Next
            reds.Sort()
            greens.Sort()
            blues.Sort()
            alphas.Sort()
            Dim mid = samples.Count \ 2
            Return New SKColor(CByte(reds(mid)), CByte(greens(mid)), CByte(blues(mid)), CByte(alphas(mid)))
        End Function

        Private Shared Function FindHealingRegionPatch(source As SKBitmap, mask As SKBitmap,
                                                       targetLeft As Integer, targetTop As Integer,
                                                       width As Integer, height As Integer,
                                                       radius As Single,
                                                       targetAverage As SKColor) As (Left As Integer, Top As Integer, Average As SKColor, Found As Boolean)
            Dim cx = targetLeft + width / 2.0F
            Dim cy = targetTop + height / 2.0F
            Dim reach = Math.Max(width, height) / 2.0F + radius
            Dim distances = {Math.Max(reach * 1.12F, radius * 1.65F),
                             Math.Max(reach * 1.38F, radius * 2.05F),
                             Math.Max(reach * 1.68F, radius * 2.55F)}
            Dim bestLeft = 0
            Dim bestTop = 0
            Dim bestAverage = SKColors.Transparent
            Dim bestScore = Double.MaxValue
            Dim found = False
            Dim targetStats = SampleRingPatchStats(source, cx, cy, Math.Max(width, height) * 0.55F, Math.Max(width, height) * 0.95F)

            Dim avoid = New SKRectI(Math.Max(0, targetLeft - CInt(Math.Ceiling(radius))),
                                    Math.Max(0, targetTop - CInt(Math.Ceiling(radius))),
                                    Math.Min(source.Width, targetLeft + width + CInt(Math.Ceiling(radius))),
                                    Math.Min(source.Height, targetTop + height + CInt(Math.Ceiling(radius))))

            For Each distance In distances
                ' 24 Winkel pro Ring - der Teiler unten. "0 To 31" wiederholte die Winkel von
                ' i=0..7 exakt (i Mod 24) und rechnete pro Ring 8 von 32 Kandidaten samt
                ' Statistik/Randbewertung doppelt: ~33 % verschenkte Suchzeit ohne jede Wirkung
                ' aufs Ergebnis, ein Duplikat gewinnt nie gegen sich selbst.
                For i = 0 To 23
                    Dim angle = (Math.PI * 2.0 * i) / 24.0
                    Dim sampleCenterX = cx + CSng(Math.Cos(angle) * distance)
                    Dim sampleCenterY = cy + CSng(Math.Sin(angle) * distance)
                    Dim sampleLeft = CInt(Math.Round(sampleCenterX - width / 2.0F))
                    Dim sampleTop = CInt(Math.Round(sampleCenterY - height / 2.0F))
                    Dim sampleRect = New SKRectI(sampleLeft, sampleTop, sampleLeft + width, sampleTop + height)
                    If sampleRect.Left < 0 OrElse sampleRect.Top < 0 OrElse
                       sampleRect.Right >= source.Width OrElse sampleRect.Bottom >= source.Height Then Continue For
                    If RectsIntersect(avoid, sampleRect) Then Continue For

                    Dim stats = SampleMaskedSourceStats(source, mask, sampleLeft, sampleTop)
                    If stats.Count <= 0 Then Continue For
                    Dim boundaryScore = RegionBoundaryScore(source, targetLeft, targetTop, sampleLeft, sampleTop, width, height)
                    Dim colorDistance = ColorDistanceSquared(stats.Average, targetAverage)
                    Dim varianceDelta = If(targetStats.Count > 0, Math.Abs(stats.Variance - targetStats.Variance), 0.0)
                    Dim outlierPenalty = MaskedPatchOutlierPenalty(source, mask, sampleLeft, sampleTop, targetAverage)
                    Dim textureBonus = If(targetStats.Count > 0 AndAlso targetStats.Variance > 120.0,
                                          Math.Min(stats.Variance, targetStats.Variance) * 0.22,
                                          0.0)
                    Dim score = boundaryScore * 1.7 + colorDistance * 0.5 + varianceDelta * 0.035 + outlierPenalty - textureBonus
                    If score < bestScore Then
                        bestScore = score
                        bestLeft = sampleLeft
                        bestTop = sampleTop
                        bestAverage = stats.Average
                        found = True
                    End If
                Next
            Next

            Return (bestLeft, bestTop, bestAverage, found)
        End Function

        Private Shared Function RegionBoundaryScore(source As SKBitmap, targetLeft As Integer, targetTop As Integer,
                                                    sampleLeft As Integer, sampleTop As Integer,
                                                    width As Integer, height As Integer) As Double
            Dim stepSize = Math.Max(2, CInt(Math.Round(Math.Max(width, height) / 12.0)))
            Dim score = 0.0
            Dim count = 0
            For x = 0 To width - 1 Step stepSize
                AddBoundaryPairScore(source, targetLeft + x, targetTop - 1, sampleLeft + x, sampleTop - 1, score, count)
                AddBoundaryPairScore(source, targetLeft + x, targetTop + height, sampleLeft + x, sampleTop + height, score, count)
            Next
            For y = 0 To height - 1 Step stepSize
                AddBoundaryPairScore(source, targetLeft - 1, targetTop + y, sampleLeft - 1, sampleTop + y, score, count)
                AddBoundaryPairScore(source, targetLeft + width, targetTop + y, sampleLeft + width, sampleTop + y, score, count)
            Next
            If count = 0 Then Return Double.MaxValue
            Return score / count
        End Function

        Private Shared Function RectsIntersect(a As SKRectI, b As SKRectI) As Boolean
            Return a.Left < b.Right AndAlso b.Left < a.Right AndAlso
                   a.Top < b.Bottom AndAlso b.Top < a.Bottom
        End Function

        Private Shared Sub AddBoundaryPairScore(source As SKBitmap, tx As Integer, ty As Integer,
                                                sx As Integer, sy As Integer,
                                                ByRef score As Double, ByRef count As Integer)
            If tx < 0 OrElse ty < 0 OrElse tx >= source.Width OrElse ty >= source.Height OrElse
               sx < 0 OrElse sy < 0 OrElse sx >= source.Width OrElse sy >= source.Height Then Return
            score += ColorDistanceSquared(source.GetPixel(tx, ty), source.GetPixel(sx, sy))
            count += 1
        End Sub

        Private Shared Sub DrawAdjustedHealingRegion(result As SKBitmap, source As SKBitmap, mask As SKBitmap,
                                                     targetLeft As Integer, targetTop As Integer,
                                                     sampleLeft As Integer, sampleTop As Integer,
                                                     targetAverage As SKColor, sourceAverage As SKColor)
            Dim dr = Math.Max(-56, Math.Min(56, CInt(targetAverage.Red) - CInt(sourceAverage.Red)))
            Dim dg = Math.Max(-56, Math.Min(56, CInt(targetAverage.Green) - CInt(sourceAverage.Green)))
            Dim db = Math.Max(-56, Math.Min(56, CInt(targetAverage.Blue) - CInt(sourceAverage.Blue)))

            For maskY = 0 To mask.Height - 1
                Dim y = targetTop + maskY
                Dim sy = sampleTop + maskY
                If y < 0 OrElse y >= result.Height OrElse sy < 0 OrElse sy >= source.Height Then Continue For
                For mx = 0 To mask.Width - 1
                    Dim maskAlpha = mask.GetPixel(mx, maskY).Alpha
                    If maskAlpha = 0 Then Continue For

                    Dim x = targetLeft + mx
                    Dim sx = sampleLeft + mx
                    If x < 0 OrElse x >= result.Width OrElse sx < 0 OrElse sx >= source.Width Then Continue For

                    Dim localAlpha = maskAlpha / 255.0F
                    Dim sample = source.GetPixel(sx, sy)
                    sample = SuppressHealingOutlier(sample, sourceAverage, targetAverage)
                    Dim target = result.GetPixel(x, y)
                    If ColorDistanceSquared(target, targetAverage) > 90 * 90 Then
                        localAlpha = 1.0F
                    End If

                    result.SetPixel(x, y, New SKColor(
                        BlendByte(target.Red, ClampByte(CInt(sample.Red) + dr), localAlpha),
                        BlendByte(target.Green, ClampByte(CInt(sample.Green) + dg), localAlpha),
                        BlendByte(target.Blue, ClampByte(CInt(sample.Blue) + db), localAlpha),
                        BlendByte(target.Alpha, sample.Alpha, localAlpha)))
                Next
            Next
        End Sub

        Private Shared Function SampleMaskedSourceStats(source As SKBitmap, mask As SKBitmap,
                                                        sampleLeft As Integer, sampleTop As Integer) As (Average As SKColor, Variance As Double, Count As Integer)
            Dim stepSize = Math.Max(1, CInt(Math.Round(Math.Max(mask.Width, mask.Height) / 18.0)))
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0
            Dim sr2 As Long = 0, sg2 As Long = 0, sb2 As Long = 0
            Dim count = 0
            For maskY = 0 To mask.Height - 1 Step stepSize
                Dim sy = sampleTop + maskY
                If sy < 0 OrElse sy >= source.Height Then Continue For
                For mx = 0 To mask.Width - 1 Step stepSize
                    If mask.GetPixel(mx, maskY).Alpha < 24 Then Continue For
                    Dim sx = sampleLeft + mx
                    If sx < 0 OrElse sx >= source.Width Then Continue For
                    Dim c = source.GetPixel(sx, sy)
                    sr += c.Red : sg += c.Green : sb += c.Blue
                    sr2 += CInt(c.Red) * CInt(c.Red)
                    sg2 += CInt(c.Green) * CInt(c.Green)
                    sb2 += CInt(c.Blue) * CInt(c.Blue)
                    count += 1
                Next
            Next
            If count = 0 Then Return (SKColors.Transparent, Double.MaxValue, 0)
            Dim ar = CDbl(sr) / count
            Dim ag = CDbl(sg) / count
            Dim ab = CDbl(sb) / count
            Dim variance = Math.Max(0.0, (CDbl(sr2) / count - ar * ar) +
                                     (CDbl(sg2) / count - ag * ag) +
                                     (CDbl(sb2) / count - ab * ab))
            Return (New SKColor(CByte(Math.Round(ar)), CByte(Math.Round(ag)), CByte(Math.Round(ab))), variance, count)
        End Function

        Private Shared Function MaskedPatchOutlierPenalty(source As SKBitmap, mask As SKBitmap,
                                                          sampleLeft As Integer, sampleTop As Integer,
                                                          targetAverage As SKColor) As Double
            Dim stepSize = Math.Max(1, CInt(Math.Round(Math.Max(mask.Width, mask.Height) / 16.0)))
            Dim outliers = 0
            Dim count = 0
            For maskY = 0 To mask.Height - 1 Step stepSize
                Dim sy = sampleTop + maskY
                If sy < 0 OrElse sy >= source.Height Then Continue For
                For mx = 0 To mask.Width - 1 Step stepSize
                    If mask.GetPixel(mx, maskY).Alpha < 24 Then Continue For
                    Dim sx = sampleLeft + mx
                    If sx < 0 OrElse sx >= source.Width Then Continue For
                    count += 1
                    If ColorDistanceSquared(source.GetPixel(sx, sy), targetAverage) > 92 * 92 Then outliers += 1
                Next
            Next
            If count = 0 Then Return 1000000.0
            Dim ratio = CDbl(outliers) / count
            Return ratio * ratio * 220000.0
        End Function

        ''' PERF: Masken-Alphas und Quell-Region einmal puffern, Abtastung mit
        ''' Schrittweite - der alte Doppel-Scan (pro Region-Pixel eine GetPixel-Nachbarschaftssuche
        ''' auf der Maske) kostete bei grossen Heal-Flaechen zweistellige Millionen Interop-Calls.
        Private Shared Function AverageRegionSurroundingColor(source As SKBitmap, mask As SKBitmap,
                                                              left As Integer, top As Integer,
                                                              radius As Single) As SKColor?
            Dim reach = Math.Max(3, CInt(Math.Ceiling(radius * 1.5F)))
            Dim minX = Math.Max(0, left - reach)
            Dim minY = Math.Max(0, top - reach)
            Dim maxX = Math.Min(source.Width - 1, left + mask.Width + reach)
            Dim maxY = Math.Min(source.Height - 1, top + mask.Height + reach)
            If maxX < minX OrElse maxY < minY Then Return Nothing

            Dim maskBuffer = RegionPixelBuffer.FromRegion(mask, 0, 0, mask.Width - 1, mask.Height - 1)
            Dim sourceBuffer = RegionPixelBuffer.FromRegion(source, minX, minY, maxX, maxY)
            Dim stride = Math.Max(1, CInt(Math.Ceiling(Math.Max(maxX - minX + 1, maxY - minY + 1) / 128.0)))
            Dim neighborStep = Math.Max(1, reach \ 3)

            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim count = 0
            For y = minY To maxY Step stride
                For x = minX To maxX Step stride
                    Dim mx = x - left
                    Dim my = y - top
                    Dim insideMaskArea = mx >= 0 AndAlso my >= 0 AndAlso mx < mask.Width AndAlso my < mask.Height
                    If insideMaskArea AndAlso MaskAlphaAt(maskBuffer, mask, mx, my) > 0 Then Continue For

                    Dim nearMask = False
                    For oy = -reach To reach Step neighborStep
                        If nearMask Then Exit For
                        For ox = -reach To reach Step neighborStep
                            Dim nx = mx + ox
                            Dim ny = my + oy
                            If nx >= 0 AndAlso ny >= 0 AndAlso nx < mask.Width AndAlso ny < mask.Height AndAlso
                               MaskAlphaAt(maskBuffer, mask, nx, ny) > 32 Then
                                nearMask = True
                                Exit For
                            End If
                        Next
                    Next
                    If Not nearMask Then Continue For

                    Dim c = If(sourceBuffer IsNot Nothing, sourceBuffer.GetColor(x, y), source.GetPixel(x, y))
                    sr += c.Red : sg += c.Green : sb += c.Blue : sa += c.Alpha
                    count += 1
                Next
            Next
            If count = 0 Then Return Nothing
            Return New SKColor(CByte(sr \ count), CByte(sg \ count), CByte(sb \ count), CByte(sa \ count))
        End Function

        Private Shared Function MaskAlphaAt(buffer As RegionPixelBuffer, mask As SKBitmap, x As Integer, y As Integer) As Byte
            If buffer IsNot Nothing Then Return buffer.GetAlpha(x, y)
            Return mask.GetPixel(x, y).Alpha
        End Function

        Private Shared Function SuppressHealingOutlier(sample As SKColor, sourceAverage As SKColor, targetAverage As SKColor) As SKColor
            If ColorDistanceSquared(sample, targetAverage) <= 92 * 92 Then Return sample

            Dim repaired = New SKColor(
                BlendByte(sample.Red, sourceAverage.Red, 0.78F),
                BlendByte(sample.Green, sourceAverage.Green, 0.78F),
                BlendByte(sample.Blue, sourceAverage.Blue, 0.78F),
                sample.Alpha)
            If ColorDistanceSquared(repaired, targetAverage) <= ColorDistanceSquared(sample, targetAverage) Then Return repaired
            Return New SKColor(sourceAverage.Red, sourceAverage.Green, sourceAverage.Blue, sample.Alpha)
        End Function

        Private Shared Function SampleRingPatchStats(source As SKBitmap, cx As Single, cy As Single,
                                                     innerRadius As Single, outerRadius As Single) As (Average As SKColor, Variance As Double, Count As Integer)
            Dim stepSize = Math.Max(1, CInt(Math.Round((outerRadius - innerRadius) / 1.5F)))
            Dim innerSq = innerRadius * innerRadius
            Dim outerSq = outerRadius * outerRadius
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0
            Dim sr2 As Long = 0, sg2 As Long = 0, sb2 As Long = 0
            Dim count = 0

            For y = Math.Max(0, CInt(Math.Floor(cy - outerRadius))) To Math.Min(source.Height - 1, CInt(Math.Ceiling(cy + outerRadius))) Step stepSize
                Dim dy = CSng(y) - cy
                For x = Math.Max(0, CInt(Math.Floor(cx - outerRadius))) To Math.Min(source.Width - 1, CInt(Math.Ceiling(cx + outerRadius))) Step stepSize
                    Dim dx = CSng(x) - cx
                    Dim dSq = dx * dx + dy * dy
                    If dSq < innerSq OrElse dSq > outerSq Then Continue For
                    Dim c = source.GetPixel(x, y)
                    sr += c.Red : sg += c.Green : sb += c.Blue
                    sr2 += CInt(c.Red) * CInt(c.Red)
                    sg2 += CInt(c.Green) * CInt(c.Green)
                    sb2 += CInt(c.Blue) * CInt(c.Blue)
                    count += 1
                Next
            Next

            If count = 0 Then Return (SKColors.Transparent, Double.MaxValue, 0)
            Dim ar = CDbl(sr) / count
            Dim ag = CDbl(sg) / count
            Dim ab = CDbl(sb) / count
            Dim variance = Math.Max(0.0, (CDbl(sr2) / count - ar * ar) +
                                     (CDbl(sg2) / count - ag * ag) +
                                     (CDbl(sb2) / count - ab * ab))
            Return (New SKColor(CByte(Math.Round(ar)), CByte(Math.Round(ag)), CByte(Math.Round(ab))), variance, count)
        End Function

        Private Shared Function ColorDistanceSquared(a As SKColor, b As SKColor) As Double
            Dim dr = CInt(a.Red) - CInt(b.Red)
            Dim dg = CInt(a.Green) - CInt(b.Green)
            Dim db = CInt(a.Blue) - CInt(b.Blue)
            Return dr * dr + dg * dg + db * db
        End Function

        Private Shared Function MedianByte(values As List(Of Byte)) As Byte
            If values Is Nothing OrElse values.Count = 0 Then Return 0
            values.Sort()
            Return values(values.Count \ 2)
        End Function

        ''' <summary>Kompakter, rein lesender Regionen-Pixelpuffer: kopiert den
        ''' benoetigten Ausschnitt EINMAL zeilenweise in ein Byte-Array und liefert Farben rein
        ''' managed. SKBitmap.GetPixel ist ein Interop-Call pro Pixel - grosse Ring-/Regionsscans
        ''' (Verwischen, Heal-Umgebung, Patch-Suche) wurden damit minutenlang. Nur Bgra8888/
        ''' Rgba8888 (Pipeline-Standard); andere Formate -> FromRegion liefert Nothing, der
        ''' Aufrufer faellt auf GetPixel zurueck.</summary>
        Private NotInheritable Class RegionPixelBuffer
            Public ReadOnly Left As Integer
            Public ReadOnly Top As Integer
            Public ReadOnly Width As Integer
            Public ReadOnly Height As Integer
            Private ReadOnly _bytes As Byte()
            Private ReadOnly _rIdx As Integer
            Private ReadOnly _gIdx As Integer
            Private ReadOnly _bIdx As Integer

            Private Sub New(left As Integer, top As Integer, width As Integer, height As Integer,
                            bytes As Byte(), rIdx As Integer, gIdx As Integer, bIdx As Integer)
                Me.Left = left
                Me.Top = top
                Me.Width = width
                Me.Height = height
                _bytes = bytes
                _rIdx = rIdx
                _gIdx = gIdx
                _bIdx = bIdx
            End Sub

            ''' <summary>x0..x1/y0..y1 einschliesslich, werden aufs Bitmap geklemmt.</summary>
            Public Shared Function FromRegion(bmp As SKBitmap, x0 As Integer, y0 As Integer,
                                              x1 As Integer, y1 As Integer) As RegionPixelBuffer
                If bmp Is Nothing Then Return Nothing
                Dim rIdx, gIdx, bIdx As Integer
                Select Case bmp.ColorType
                    Case SKColorType.Bgra8888 : bIdx = 0 : gIdx = 1 : rIdx = 2
                    Case SKColorType.Rgba8888 : rIdx = 0 : gIdx = 1 : bIdx = 2
                    Case Else
                        Return Nothing
                End Select
                x0 = Math.Max(0, x0) : y0 = Math.Max(0, y0)
                x1 = Math.Min(bmp.Width - 1, x1) : y1 = Math.Min(bmp.Height - 1, y1)
                If x1 < x0 OrElse y1 < y0 Then Return Nothing
                Dim width = x1 - x0 + 1
                Dim height = y1 - y0 + 1
                Dim bytes(width * height * 4 - 1) As Byte
                Dim basePtr = bmp.GetPixels()
                If basePtr = IntPtr.Zero Then Return Nothing
                Dim srcStride = bmp.RowBytes
                For row = 0 To height - 1
                    Runtime.InteropServices.Marshal.Copy(IntPtr.Add(basePtr, (y0 + row) * srcStride + x0 * 4),
                                                         bytes, row * width * 4, width * 4)
                Next
                Return New RegionPixelBuffer(x0, y0, width, height, bytes, rIdx, gIdx, bIdx)
            End Function

            ''' <summary>Bitmap-Koordinaten; entpremultipliziert bei Alpha &lt; 255 (GetPixel-Verhalten).</summary>
            Public Function GetColor(x As Integer, y As Integer) As SKColor
                Dim idx = ((y - Top) * Width + (x - Left)) * 4
                Dim a = _bytes(idx + 3)
                Dim r = CInt(_bytes(idx + _rIdx))
                Dim g = CInt(_bytes(idx + _gIdx))
                Dim b = CInt(_bytes(idx + _bIdx))
                If a > 0 AndAlso a < 255 Then
                    r = Math.Min(255, r * 255 \ a)
                    g = Math.Min(255, g * 255 \ a)
                    b = Math.Min(255, b * 255 \ a)
                End If
                Return New SKColor(CByte(r), CByte(g), CByte(b), a)
            End Function

            Public Function GetAlpha(x As Integer, y As Integer) As Byte
                Return _bytes(((y - Top) * Width + (x - Left)) * 4 + 3)
            End Function

            Public Function Contains(x As Integer, y As Integer) As Boolean
                Return x >= Left AndAlso y >= Top AndAlso x < Left + Width AndAlso y < Top + Height
            End Function

            ''' <summary>Spiegel-Schreibzugriff (Heal-Patch-Kopien): haelt den Puffer synchron zum
            ''' Bitmap, damit Scores innerhalb eines Passes frisch kopierte Pixel sehen. Schreibt
            ''' premultipliziert (Puffer-Layout).</summary>
            Public Sub SetColor(x As Integer, y As Integer, color As SKColor)
                Dim idx = ((y - Top) * Width + (x - Left)) * 4
                Dim a = CInt(color.Alpha)
                Dim r = CInt(color.Red)
                Dim g = CInt(color.Green)
                Dim b = CInt(color.Blue)
                If a < 255 Then
                    r = r * a \ 255
                    g = g * a \ 255
                    b = b * a \ 255
                End If
                _bytes(idx + _rIdx) = CByte(r)
                _bytes(idx + _gIdx) = CByte(g)
                _bytes(idx + _bIdx) = CByte(b)
                _bytes(idx + 3) = CByte(a)
            End Sub
        End Class

        ''' Mittelt den Ring zwischen dem 1,25- und dem 2-fachen Radius um das Ziel - der Rückfall,
        ''' wenn keine Klonquelle gesetzt wurde. Liefert Nothing, wenn der Ring komplett außerhalb
        ''' des Bildes liegt.
        ''' PERF: Ringabtastung mit Regionen-Puffer + Schrittweite statt GetPixel
        ''' ueber JEDES Pixel - bei grossen Verwisch-Radien wurden aus einem Zug sonst Milliarden
        ''' Interop-Calls (Minuten CPU). ~10k Samples liefern denselben Mittelwert.
        Private Shared Function AverageSurroundingColor(source As SKBitmap, cx As Single, cy As Single, radius As Single,
                                                        Optional innerFactor As Single = 1.25F,
                                                        Optional outerFactor As Single = 2.0F) As SKColor?
            Dim inner = radius * innerFactor
            Dim outer = radius * outerFactor
            Dim innerSq = inner * inner
            Dim outerSq = outer * outer

            Dim icx = CInt(Math.Round(cx))
            Dim icy = CInt(Math.Round(cy))
            Dim reach = CInt(Math.Ceiling(outer))
            Dim stride = Math.Max(1, CInt(Math.Ceiling(reach / 56.0)))

            Dim y0 = Math.Max(0, icy - reach)
            Dim y1 = Math.Min(source.Height - 1, icy + reach)
            Dim x0 = Math.Max(0, icx - reach)
            Dim x1 = Math.Min(source.Width - 1, icx + reach)
            If y1 < y0 OrElse x1 < x0 Then Return Nothing
            Dim buffer = RegionPixelBuffer.FromRegion(source, x0, y0, x1, y1)

            Dim samples As New List(Of SKColor)()
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim count As Integer = 0
            For yy = y0 To y1 Step stride
                Dim dy = CSng(yy - icy)
                Dim dySq = dy * dy
                For xx = x0 To x1 Step stride
                    Dim dx = CSng(xx - icx)
                    Dim dSq = dx * dx + dySq
                    If dSq >= innerSq AndAlso dSq <= outerSq Then
                        Dim c = If(buffer IsNot Nothing, buffer.GetColor(xx, yy), source.GetPixel(xx, yy))
                        samples.Add(c)
                        sr += c.Red : sg += c.Green : sb += c.Blue : sa += c.Alpha
                        count += 1
                    End If
                Next
            Next
            If count = 0 Then Return Nothing

            If samples.Count >= 12 Then
                Dim reds As New List(Of Byte)(samples.Count)
                Dim greens As New List(Of Byte)(samples.Count)
                Dim blues As New List(Of Byte)(samples.Count)
                For Each sample In samples
                    reds.Add(sample.Red)
                    greens.Add(sample.Green)
                    blues.Add(sample.Blue)
                Next

                Dim median = New SKColor(MedianByte(reds), MedianByte(greens), MedianByte(blues), CByte(sa \ count))
                Dim filteredR As Long = 0, filteredG As Long = 0, filteredB As Long = 0, filteredA As Long = 0
                Dim filteredCount = 0
                For Each sample In samples
                    If ColorDistanceSquared(sample, median) > 54 * 54 Then Continue For
                    filteredR += sample.Red
                    filteredG += sample.Green
                    filteredB += sample.Blue
                    filteredA += sample.Alpha
                    filteredCount += 1
                Next

                If filteredCount >= Math.Max(6, samples.Count \ 5) Then
                    Return New SKColor(CByte(filteredR \ filteredCount), CByte(filteredG \ filteredCount),
                                       CByte(filteredB \ filteredCount), CByte(filteredA \ filteredCount))
                End If
            End If

            Return New SKColor(CByte(sr \ count), CByte(sg \ count), CByte(sb \ count), CByte(sa \ count))
        End Function

        Private Shared Function ClampByte(value As Integer) As Byte
            If value <= 0 Then Return 0
            If value >= 255 Then Return 255
            Return CByte(value)
        End Function

        Private Shared Function BlendByte(dst As Byte, src As Byte, alpha As Single) As Byte
            Return ClampByte(CInt(Math.Round(dst + (CInt(src) - CInt(dst)) * alpha)))
        End Function

        Private Shared Function ApplyResize(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim targetWidth = adj.ResizeWidth
            Dim targetHeight = adj.ResizeHeight

            ' Prozentuale Zielgroesse auf dem TATSAECHLICHEN Bild - siehe ResizeScalePercent.
            If adj.ResizeScalePercent > 0 AndAlso source.Width > 0 AndAlso source.Height > 0 Then
                targetWidth = Math.Max(1, CInt(Math.Round(source.Width * adj.ResizeScalePercent / 100.0)))
                targetHeight = Math.Max(1, CInt(Math.Round(source.Height * adj.ResizeScalePercent / 100.0)))
            End If

            If targetWidth <= 0 AndAlso targetHeight <= 0 Then Return source

            ' KASTEN-Modus (nur Stapel/Export, siehe ResizeFitInsideBox): das Bild wird NICHT
            ' verzerrt, die Zielwerte sind eine Schranke statt eines exakten Masses:
            '   * nur EIN Wert  -> er begrenzt die LAENGSTE Kante, unabhaengig von der Ausrichtung
            '     (ein gemischter Stapel kommt so einheitlich heraus statt in zwei Groessen);
            '   * BEIDE Werte   -> das Bild wird in diesen Kasten EINGEPASST (kleinerer Faktor).
            ' Ohne den Modus gelten die Werte exakt (Editor: die zweite Kante haengt dort ohnehin
            ' am Seitenverhaeltnis, und der Nutzer sieht die Zahlen im Panel).
            If adj.ResizeFitInsideBox AndAlso adj.LockResizeAspect AndAlso source.Width > 0 AndAlso source.Height > 0 Then
                If targetWidth <= 0 Xor targetHeight <= 0 Then
                    Dim laengste = Math.Max(targetWidth, targetHeight)
                    If source.Width >= source.Height Then
                        targetWidth = laengste
                        targetHeight = 0
                    Else
                        targetHeight = laengste
                        targetWidth = 0
                    End If
                Else
                    Dim factor = Math.Min(targetWidth / CDbl(source.Width), targetHeight / CDbl(source.Height))
                    targetWidth = Math.Max(1, CInt(Math.Round(source.Width * factor)))
                    targetHeight = Math.Max(1, CInt(Math.Round(source.Height * factor)))
                End If
            End If

            If targetWidth <= 0 Then targetWidth = CInt(Math.Round(source.Width * (targetHeight / CDbl(source.Height))))
            If targetHeight <= 0 Then targetHeight = CInt(Math.Round(source.Height * (targetWidth / CDbl(source.Width))))

            ' "Nicht vergroessern": ein Bild, das schon kleiner ist als das Ziel, bleibt wie es ist.
            ' Mit gehaltenem Seitenverhaeltnis wird EINHEITLICH herunterskaliert (ein gemeinsamer
            ' Faktor), sonst wuerde die Deckelung je Achse das Bild doch wieder verzerren.
            If adj.NoResizeUpscale AndAlso source.Width > 0 AndAlso source.Height > 0 Then
                If adj.LockResizeAspect Then   ' einheitlicher Faktor, sonst verzerrt die Deckelung
                    Dim cap = Math.Min(1.0, Math.Min(targetWidth / CDbl(source.Width), targetHeight / CDbl(source.Height)))
                    targetWidth = Math.Max(1, CInt(Math.Round(source.Width * cap)))
                    targetHeight = Math.Max(1, CInt(Math.Round(source.Height * cap)))
                Else
                    targetWidth = Math.Min(targetWidth, source.Width)
                    targetHeight = Math.Min(targetHeight, source.Height)
                End If
            End If

            targetWidth = Math.Max(1, targetWidth)
            targetHeight = Math.Max(1, targetHeight)
            If targetWidth = source.Width AndAlso targetHeight = source.Height Then Return source

            Dim result = New SKBitmap(targetWidth, targetHeight, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                Using paint = New SKPaint With {.IsAntialias = True}
                    DrawBitmapSampled(canvas, source, New SKRect(0, 0, source.Width, source.Height), New SKRect(0, 0, targetWidth, targetHeight),
                                      ToSampling(adj.ResizeInterpolation), paint)
                End Using
            End Using
            Return result
        End Function

        Private Shared Function ToSampling(mode As ResizeInterpolationMode) As SKSamplingOptions
            Select Case mode
                Case ResizeInterpolationMode.Nearest
                    Return New SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
                Case ResizeInterpolationMode.Bilinear
                    Return New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)
                Case Else
                    Return SamplingHigh
            End Select
        End Function

        ''' <summary>Legt das Bild auf die Hintergrundfarbe des Dokuments. Ohne gesetzte Farbe kommt
        ''' dasselbe Objekt unveraendert zurueck - kein Umkopieren fuer nichts.
        '''
        ''' Am Ende der Kette, damit sie ALLES fuellt, was vorher durchsichtig geblieben ist:
        ''' erweiterte Leinwand, leere Ecken von Begradigen und Verzerren, weggeradierte Stellen.</summary>
        Private Shared Function ApplyDocumentBackground(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing OrElse adj Is Nothing Then Return source
            If String.IsNullOrWhiteSpace(adj.CanvasBackgroundColor) Then Return source
            Dim color As SKColor
            If Not SKColor.TryParse(adj.CanvasBackgroundColor, color) Then Return source
            ' Voellig durchsichtig heisst: keine Farbe gewuenscht.
            If color.Alpha = 0 Then Return source

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(color)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return result
        End Function

        Private Shared Function ApplyCanvasResize(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim targetWidth = If(adj.CanvasWidth > 0, adj.CanvasWidth, source.Width)
            Dim targetHeight = If(adj.CanvasHeight > 0, adj.CanvasHeight, source.Height)
            If targetWidth = source.Width AndAlso targetHeight = source.Height Then Return source
            If targetWidth <= 0 OrElse targetHeight <= 0 Then Return source

            Dim offsetX As Single = 0
            Dim offsetY As Single = 0
            Dim anchor = If(adj.CanvasAnchor, "Center").Trim().ToLowerInvariant()

            Select Case anchor
                Case "top-left", "left-top" : offsetX = 0 : offsetY = 0
                Case "top", "top-center" : offsetX = (targetWidth - source.Width) / 2.0F : offsetY = 0
                Case "top-right", "right-top" : offsetX = targetWidth - source.Width : offsetY = 0
                Case "left", "middle-left" : offsetX = 0 : offsetY = (targetHeight - source.Height) / 2.0F
                Case "right", "middle-right" : offsetX = targetWidth - source.Width : offsetY = (targetHeight - source.Height) / 2.0F
                Case "bottom-left", "left-bottom" : offsetX = 0 : offsetY = targetHeight - source.Height
                Case "bottom", "bottom-center" : offsetX = (targetWidth - source.Width) / 2.0F : offsetY = targetHeight - source.Height
                Case "bottom-right", "right-bottom" : offsetX = targetWidth - source.Width : offsetY = targetHeight - source.Height
                Case Else
                    offsetX = (targetWidth - source.Width) / 2.0F
                    offsetY = (targetHeight - source.Height) / 2.0F
            End Select

            Dim result = New SKBitmap(targetWidth, targetHeight, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                ' Durchsichtig lassen: gefuellt wird EINMAL am Ende der Kette
                ' (ApplyDocumentBackground), sonst faerbte diese Stufe ihr Loch selbst und die
                ' anderen Loecher blieben durchsichtig.
                canvas.Clear(SKColors.Transparent)
                canvas.DrawBitmap(source, offsetX, offsetY)
            End Using
            Return result
        End Function

        Private Shared Function IsPaintKind(kind As String) As Boolean
            Dim normalized = If(kind, "").Trim().ToLowerInvariant()
            Return normalized = "brush" OrElse normalized = "eraser"
        End Function

        Private Shared Function ScaleAnnotationForSource(annotation As ImageAnnotation, scaleX As Single, scaleY As Single) As ImageAnnotation
            If annotation Is Nothing Then Return Nothing
            If Math.Abs(scaleX - 1.0F) < 0.0001F AndAlso Math.Abs(scaleY - 1.0F) < 0.0001F Then Return annotation

            Dim scaled = annotation.Clone()
            Dim uniformScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))
            scaled.XPixels *= scaleX
            scaled.YPixels *= scaleY
            scaled.WidthPixels *= scaleX
            scaled.HeightPixels *= scaleY
            scaled.FontSizePixels *= uniformScale
            scaled.StrokeWidth *= uniformScale
            If IsPaintKind(scaled.Kind) Then
                scaled.Strokes = scaled.Strokes.Select(Function(s) s.Scale(scaleX, scaleY)).ToList()
            End If
            Return scaled
        End Function

        Friend Shared Function TransformAnnotationForGeometry(annotation As ImageAnnotation, adj As ImageAdjustments,
                                                              outputWidth As Integer, outputHeight As Integer) As ImageAnnotation
            If annotation Is Nothing Then Return Nothing
            If adj Is Nothing OrElse adj.SourceWidthPixels <= 0 OrElse adj.SourceHeightPixels <= 0 Then Return annotation

            Dim rotation = ImageGeometryMapper.NormalizeQuarterTurn(adj.RotationDegrees)
            Dim q = rotation \ 90
            Dim preWidth = If(rotation = 90 OrElse rotation = 270, outputHeight, outputWidth)
            Dim preHeight = If(rotation = 90 OrElse rotation = 270, outputWidth, outputHeight)
            If preWidth <= 0 OrElse preHeight <= 0 Then Return annotation

            ' Annotationen leben im Pixelraum des unbeschnittenen Basisbilds. Nach ApplyCrop beginnt
            ' der Renderraum jedoch an der linken/oberen Crop-Kante. Nur auf die Restgroesse zu
            ' skalieren (das fruehere Verhalten) verschob deshalb jedes Objekt: x=500 wurde bei einem
            ' 10-%-Crop zu 450 statt zu 400. Erst den Crop-Ursprung abziehen, danach eine eventuelle
            ' Preview-/Resize-Skalierung auf die beschnittene Groesse anwenden.
            Dim crop = ComputeGeometryCropRect(adj.SourceWidthPixels, adj.SourceHeightPixels, adj)
            Dim croppedAnnotation = annotation.Clone()
            Dim kind = If(croppedAnnotation.Kind, "").Trim().ToLowerInvariant()
            Dim isAnchoredWatermark = kind = "watermark" AndAlso Not String.IsNullOrWhiteSpace(croppedAnnotation.Anchor)
            If Not isAnchoredWatermark Then
                croppedAnnotation.XPixels -= crop.Left
                croppedAnnotation.YPixels -= crop.Top
            End If
            If IsPaintKind(kind) AndAlso croppedAnnotation.Strokes IsNot Nothing AndAlso
               (crop.Left <> 0 OrElse crop.Top <> 0) Then
                croppedAnnotation.Strokes = croppedAnnotation.Strokes.Where(Function(stroke) stroke IsNot Nothing).Select(
                    Function(stroke) New BrushStroke(stroke.Points.Select(
                        Function(p) New StrokePoint(p.X - crop.Left, p.Y - crop.Top)).ToList())).ToList()
            End If

            ' „Größe behalten": ein verankertes Wasserzeichen, das NICHT mitwachsen soll, wird gar
            ' nicht erst umgerechnet - seine Zahlen gelten dann direkt im Ausgabebild. Die Verankerung
            ' rechnet ComputeAnnotationRect ohnehin gegen die Ausgabemaße, die Lage stimmt also weiter.
            Dim renderAnnotation As ImageAnnotation
            If isAnchoredWatermark AndAlso Not croppedAnnotation.ScaleWithImage Then
                renderAnnotation = croppedAnnotation
            Else
                renderAnnotation = ScaleAnnotationForSource(croppedAnnotation,
                                                            preWidth / CSng(crop.Width),
                                                            preHeight / CSng(crop.Height))
            End If
            If renderAnnotation Is Nothing Then Return Nothing
            If q = 0 AndAlso Not adj.FlipHorizontal AndAlso Not adj.FlipVertical Then Return renderAnnotation

            Dim transformed = renderAnnotation.Clone()
            Dim objectGeometry = ImageGeometryMapper.SourceObjectToDisplay(
                New SKRect(transformed.XPixels, transformed.YPixels,
                           transformed.XPixels + transformed.WidthPixels,
                           transformed.YPixels + transformed.HeightPixels),
                preWidth, preHeight, outputWidth, outputHeight,
                rotation, adj.FlipHorizontal, adj.FlipVertical,
                transformed.RotationDegrees)
            transformed.XPixels = objectGeometry.Rect.Left
            transformed.YPixels = objectGeometry.Rect.Top
            transformed.WidthPixels = objectGeometry.Rect.Width
            transformed.HeightPixels = objectGeometry.Rect.Height
            transformed.RotationDegrees = objectGeometry.RotationDegrees
            If adj.FlipHorizontal Then transformed.FlipHorizontal = Not transformed.FlipHorizontal
            If adj.FlipVertical Then transformed.FlipVertical = Not transformed.FlipVertical

            If IsPaintKind(transformed.Kind) AndAlso transformed.Strokes IsNot Nothing Then
                transformed.Strokes = transformed.Strokes.Select(
                    Function(stroke) TransformStrokeForGeometry(stroke, preWidth, preHeight, rotation, adj.FlipHorizontal, adj.FlipVertical)).
                    Where(Function(stroke) stroke IsNot Nothing).
                    ToList()
            End If
            Return transformed
        End Function

        ''' <summary>Striche in die Ausgabegeometrie. Ueber die gemeinsame Matrix des Mappers statt
        ''' ueber eine eigene Fallunterscheidung - die war die letzte Sorte, die ihre eigene Kopie
        ''' der Dreh- und Spiegelregeln fuehrte. Zwei Kopien derselben Formel laufen frueher oder
        ''' spaeter auseinander; hier ist es nur deshalb nie passiert, weil beide gleichzeitig
        ''' geschrieben wurden.</summary>
        Private Shared Function TransformStrokeForGeometry(stroke As BrushStroke,
                                                           preWidth As Integer, preHeight As Integer,
                                                           rotationDegrees As Integer,
                                                           flipH As Boolean, flipV As Boolean) As BrushStroke
            If stroke Is Nothing OrElse stroke.Points Is Nothing Then Return Nothing
            Dim m = ImageGeometryMapper.SourceToDisplayMatrix(preWidth, preHeight, rotationDegrees, flipH, flipV)
            Dim points As New List(Of StrokePoint)(stroke.Points.Count)
            For Each p In stroke.Points
                Dim mapped = m.MapPoint(New SKPoint(CSng(p.X), CSng(p.Y)))
                points.Add(New StrokePoint(mapped.X, mapped.Y))
            Next
            Return New BrushStroke(points)
        End Function

        ''' <summary>Trägt das Objekt eigene Pixel-Anpassungen? Nur dann lohnt die eigene Ebene.</summary>
        Private Shared Function HasObjectAdjustments(annotation As ImageAnnotation) As Boolean
            Return annotation IsNot Nothing AndAlso annotation.Adjustments IsNot Nothing AndAlso annotation.Adjustments.HasPixelAdjustments()
        End Function

        ''' <summary>Zeichnet ein Objekt (samt Drehung, Spiegelung, Schatten/Glühen) auf die übergebene
        ''' Leinwand. Ausgelagert, weil dieselbe Zeichnung entweder direkt aufs Bild geht oder - wenn das
        ''' Objekt eigene Anpassungen trägt - zuerst auf eine eigene transparente Ebene.</summary>
        Private Shared Sub DrawAnnotationOnCanvas(canvas As SKCanvas, kind As String, renderAnnotation As ImageAnnotation,
                                                  rect As SKRect, sourceWidth As Integer, sourceHeight As Integer)
            Dim x = rect.Left
            Dim y = rect.Top
            Dim maxWidth = rect.Width
            Dim fontSize = Math.Max(8.0F, renderAnnotation.FontSizePixels)
            Dim alphaFactor = Clamp(renderAnnotation.Opacity, 0, 100) / 100.0F
            Dim fill = ApplyAlpha(ParseColor(renderAnnotation.FillColor, SKColors.White), alphaFactor)
            Dim stroke = ApplyAlpha(ParseColor(renderAnnotation.StrokeColor, SKColors.Black), alphaFactor)
            Dim strokeWidth = Math.Max(1.0F, renderAnnotation.StrokeWidth)

            canvas.Save()
            If Math.Abs(renderAnnotation.RotationDegrees) > 0.01F Then
                canvas.RotateDegrees(renderAnnotation.RotationDegrees, rect.MidX, rect.MidY)
            End If
            ' Spiegeln um die eigene Mitte - NACH der Drehung, damit „gedreht und gespiegelt" das Objekt
            ' nicht zusätzlich verschiebt. Schatten/Glühen und die Füllung folgen mit, weil alles Weitere
            ' auf derselben Leinwand-Transformation zeichnet.
            If renderAnnotation.FlipHorizontal OrElse renderAnnotation.FlipVertical Then
                canvas.Translate(rect.MidX, rect.MidY)
                canvas.Scale(If(renderAnnotation.FlipHorizontal, -1.0F, 1.0F),
                             If(renderAnnotation.FlipVertical, -1.0F, 1.0F))
                canvas.Translate(-rect.MidX, -rect.MidY)
            End If

            If renderAnnotation.ShadowEnabled OrElse renderAnnotation.GlowEnabled Then
                DrawAnnotationEffects(canvas, kind, renderAnnotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor, sourceWidth, sourceHeight)
            End If
            DrawAnnotationShape(canvas, kind, renderAnnotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor)
            canvas.Restore()
        End Sub

        Private Shared Function ApplyAnnotations(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            ' ARBEITSBILD-Umbau (Stufe D): Pinsel-/Radiererstriche sind ins Arbeitsbild eingebacken
            ' und laufen nicht mehr hier durch - hier rendern nur noch die Z-Order-Objekte.
            ' Ohne Objekte UND mit sichtbarem Hintergrund gibt es nichts zu tun. Ist der Hintergrund
            ' ausgeblendet, muss aber selbst ohne Objekte ein transparentes Bild herauskommen.
            Dim hasObjects = adj.Annotations IsNot Nothing AndAlso adj.Annotations.Count > 0
            If Not adj.BackgroundHidden AndAlso Not hasObjects Then Return source

            Dim result As SKBitmap
            If adj.BackgroundHidden Then
                ' Hintergrund-Ebene aus: die Objekte schweben auf transparentem Grund (durchsichtiges PNG,
                ' im Editor der Schachbrett-Hintergrund). Die teure Basis-Pipeline lief zwar, wird hier aber
                ' verworfen - das ist der Preis fürs saubere Ein-/Ausschalten über einen einzigen Schalter.
                result = New SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using clearCanvas = New SKCanvas(result)
                    clearCanvas.Clear(SKColors.Transparent)
                End Using
            Else
                result = CloneBitmapForAnnotationComposite(source)
            End If

            If Not hasObjects Then Return result

            ' Korrekturebenen, die IN den Objektstapel einsortiert sind, wirken auf alles, was unter
            ' ihnen liegt. Dafür werden die Objekte der Reihe nach gezeichnet und nach jedem Objekt die
            ' dort eingehängten Korrekturen auf das bisherige Komposit angewendet. Ohne solche Ebenen
            ' (Normalfall) bleibt es bei EINEM Aufruf über alle Objekte - kein Mehraufwand.
            Dim stacked = If(adj.MaskedAdjustmentLayers, New System.Collections.Generic.List(Of MaskedAdjustmentLayer)()).
                Where(Function(l) l IsNot Nothing AndAlso Not String.IsNullOrEmpty(l.StackAboveAnnotationId)).ToList()
            If stacked.Count = 0 Then
                Using canvas = New SKCanvas(result)
                    DrawAnnotationsOnCanvas(canvas, adj, source.Width, source.Height, 0, 0, source.Width, source.Height, adj.Annotations)
                End Using
                Return result
            End If

            ' FARBTYP: das Objekt-Komposit ist Rgba8888, die Masken-Komposition rechnet aber in
            ' Bgra8888 (TryBorrowBgraBuffer) und fiele sonst STILL auf die Baseline zurück - die
            ' Korrektur wäre wirkungslos.
            '
            ' Umgewandelt wird EINMAL für den ganzen Durchlauf statt zweimal je Korrektur: vorher
            ' kostete jede eingehängte Korrektur drei Vollkopien des Bildes (hin, Klon in
            ' ApplyMaskedAdjustmentLayers, zurück) - bei 24 MP rund 300 MB Speicherverkehr pro
            ' Korrektur und Frame. Jetzt bleibt es bei einer Umwandlung am Anfang, einer am Ende und
            ' dem unvermeidbaren Klon je Korrektur.
            Dim targetColorType = result.ColorType
            If targetColorType <> SKColorType.Bgra8888 Then
                result = ReplaceBitmap(result, ConvertBitmapToColorType(result, SKColorType.Bgra8888))
            End If

            For Each annotation In adj.Annotations
                Using canvas = New SKCanvas(result)
                    DrawAnnotationsOnCanvas(canvas, adj, source.Width, source.Height, 0, 0, source.Width, source.Height,
                                            New System.Collections.Generic.List(Of ImageAnnotation) From {annotation})
                End Using
                If stacked.Any(Function(l) String.Equals(l.StackAboveAnnotationId, annotation.Id, StringComparison.Ordinal)) Then
                    ' KEIN Using um das Ergebnis: es WIRD das neue Komposit (ReplaceBitmap gibt das
                    ' alte frei). Ein Using würde genau das Bitmap freigeben, mit dem weitergezeichnet
                    ' wird.
                    Dim corrected = ApplyMaskedAdjustmentLayers(result, adj, source.Width, source.Height, annotation.Id)
                    If corrected IsNot Nothing Then result = ReplaceBitmap(result, corrected)
                End If
            Next

            If result.ColorType <> targetColorType Then
                result = ReplaceBitmap(result, ConvertBitmapToColorType(result, targetColorType))
            End If
            Return result
        End Function

        ''' <summary>Zeichnet die Objekte in Z-Reihenfolge und gibt zurueck, WIE VIELE wirklich
        ''' gezeichnet wurden (sichtbar, mit Geometrie, nicht vom Clip verworfen). Die Zahl ist der
        ''' Messpunkt fuer den Kompositor-Umbau (OFFENE_PUNKTE Abschnitt 2, Stufe 1): sie zeigt je
        ''' Patch, wie viel Objektarbeit der Region-Weg heute leistet. Aufrufer duerfen den
        ''' Rueckgabewert ignorieren.</summary>
        Friend Shared Function DrawAnnotationsOnCanvas(canvas As SKCanvas, adj As ImageAdjustments,
                                                   sourceWidth As Integer, sourceHeight As Integer,
                                                   offsetX As Integer, offsetY As Integer,
                                                   layerWidth As Integer, layerHeight As Integer,
                                                   Optional renderAnnotations As IReadOnlyList(Of ImageAnnotation) = Nothing) As Integer
            If canvas Is Nothing OrElse adj Is Nothing Then Return 0
            Dim annotations = If(renderAnnotations, adj.Annotations)
            If annotations Is Nothing OrElse annotations.Count = 0 Then Return 0
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse layerWidth <= 0 OrElse layerHeight <= 0 Then Return 0

            Dim drawn = 0
            For Each annotation In annotations
                ' Sichtbarkeit IMMER über den Chokepoint: er verundet das eigene IsVisible mit dem
                ' Schalter der Gruppe, zu der das Objekt gehört.
                If Not adj.IsAnnotationRenderVisible(annotation) Then Continue For
                Dim renderAnnotation = TransformAnnotationForGeometry(annotation, adj, sourceWidth, sourceHeight)
                If renderAnnotation Is Nothing Then Continue For
                ' AUSSERHALB DES CLIPS GAR NICHT ERST ZEICHNEN. Ein Region-Patch ist nur wenige hundert
                ' Pixel groß, zeichnete bisher aber JEDES Objekt des Dokuments - bei 36 eingefügten
                ' Bildern hieß das 36 Decodes und Skalierungen für einen 400x400-Fleck (gemessen 550 ms
                ' statt ~20 ms; der Zug wirkte dadurch zäh und die Ghost-Übergabe kam nicht durch,
                '). QuickReject prüft gegen den aktuellen Clip des Canvas und ist
                ' selbst praktisch kostenlos.
                Dim eigenRect = ComputeAnnotationDirtyRectCore(sourceWidth, sourceHeight, renderAnnotation)
                If Not eigenRect.IsEmpty Then
                    Dim clipTest = New SKRect(eigenRect.Left - offsetX, eigenRect.Top - offsetY,
                                              eigenRect.Right - offsetX, eigenRect.Bottom - offsetY)
                    If canvas.QuickReject(clipTest) Then Continue For
                End If
                drawn += 1
                Dim kind = If(renderAnnotation.Kind, "Text").Trim().ToLowerInvariant()

                If IsPaintKind(kind) Then
                    Dim alphaFactor = Clamp(renderAnnotation.Opacity, 0, 100) / 100.0F
                    Dim stroke = ApplyAlpha(ParseColor(renderAnnotation.StrokeColor, SKColors.Black), alphaFactor)
                    Dim strokeWidth = Math.Max(1.0F, Clamp(renderAnnotation.StrokeWidth, 1, Math.Max(sourceWidth, sourceHeight)))
                    Dim isEraser = kind = "eraser"
                    Dim eraserFill As SKColor? = Nothing
                    If isEraser AndAlso Not String.IsNullOrWhiteSpace(renderAnnotation.EraserFillColor) Then
                        eraserFill = ApplyAlpha(ParseColor(renderAnnotation.EraserFillColor, SKColors.Transparent), alphaFactor)
                    End If
                    ' Mischmodus auch für Pinselstriche (nicht Radiergummi - der entfernt Pixel und ignoriert
                    ' den Modus): erst auf eine eigene transparente Ebene malen, dann mit dem Blend-Modus
                    ' einkomponieren - wie bei Formen/Text. Bei "Normal" direkt zeichnen (kein Extra-Speicher).
                    Dim useBrushBlendLayer = (Not isEraser) AndAlso Not IsNormalAnnotationBlendMode(renderAnnotation.BlendMode)
                    If useBrushBlendLayer Then
                        Using brushLayer = New SKBitmap(layerWidth, layerHeight, SKColorType.Rgba8888, SKAlphaType.Premul)
                            Using brushLayerCanvas = New SKCanvas(brushLayer)
                                brushLayerCanvas.Clear(SKColors.Transparent)
                                brushLayerCanvas.Translate(-offsetX, -offsetY)
                                If renderAnnotation.ShadowEnabled OrElse renderAnnotation.GlowEnabled Then
                                    DrawBrushStrokeWithEffects(brushLayerCanvas, renderAnnotation, sourceWidth, sourceHeight, stroke, strokeWidth)
                                Else
                                    DrawBrushStroke(brushLayerCanvas, renderAnnotation.Strokes, sourceWidth, sourceHeight, stroke, strokeWidth,
                                                    renderAnnotation.HardnessPercent, renderAnnotation.FlowPercent, renderAnnotation.BrushPreset, False, Nothing)
                                End If
                            End Using
                            DrawAnnotationLayer(canvas, brushLayer, renderAnnotation.BlendMode)
                        End Using
                    Else
                        canvas.Save()
                        canvas.Translate(-offsetX, -offsetY)
                        If (Not isEraser) AndAlso (renderAnnotation.ShadowEnabled OrElse renderAnnotation.GlowEnabled) Then
                            DrawBrushStrokeWithEffects(canvas, renderAnnotation, sourceWidth, sourceHeight, stroke, strokeWidth)
                        Else
                            DrawBrushStroke(canvas, renderAnnotation.Strokes, sourceWidth, sourceHeight, stroke, strokeWidth,
                                            renderAnnotation.HardnessPercent, renderAnnotation.FlowPercent, renderAnnotation.BrushPreset, isEraser, eraserFill)
                        End If
                        canvas.Restore()
                    End If
                    Continue For
                End If

                Dim rect = ComputeAnnotationRect(sourceWidth, sourceHeight, kind, renderAnnotation)

                ' Objekt MIT eigenen Anpassungen: erst allein auf eine transparente Ebene zeichnen, dann
                ' die Pixel-Pipeline darauf laufen lassen (Belichtung, Farbe, Filter … treffen so nur das
                ' Objekt), dann an Ort und Stelle in der Z-Reihenfolge einkomponieren. Ohne eigene
                ' Anpassungen wird wie bisher direkt gezeichnet - kein zusätzlicher Speicher, keine Zeit.
                If SplitsStrokeFromBlend(renderAnnotation, kind) Then
                    ' Kontur NICHT mitmischen: die Fuellung geht durch den Mischmodus, die Kontur wird
                    ' anschliessend normal darueber gelegt. Gezeichnet wird dafuer zweimal mit je einer
                    ' durchsichtig gesetzten Farbe - so bleibt jede der Zeichenroutinen unberuehrt.
                    DrawAnnotationViaLayer(canvas, annotation, AnnotationFillOnly(renderAnnotation), kind, rect,
                                           sourceWidth, sourceHeight, layerWidth, layerHeight, offsetX, offsetY,
                                           renderAnnotation.BlendMode)
                    DrawAnnotationViaLayer(canvas, annotation, AnnotationStrokeOnly(renderAnnotation), kind, rect,
                                           sourceWidth, sourceHeight, layerWidth, layerHeight, offsetX, offsetY,
                                           "Normal")
                ElseIf HasObjectAdjustments(annotation) OrElse Not IsNormalAnnotationBlendMode(renderAnnotation.BlendMode) OrElse
                       HasWarp(annotation) Then
                    DrawAnnotationViaLayer(canvas, annotation, renderAnnotation, kind, rect,
                                           sourceWidth, sourceHeight, layerWidth, layerHeight, offsetX, offsetY,
                                           renderAnnotation.BlendMode)
                Else
                    canvas.Save()
                    canvas.Translate(-offsetX, -offsetY)
                    DrawAnnotationOnCanvas(canvas, kind, renderAnnotation, rect, sourceWidth, sourceHeight)
                    canvas.Restore()
                End If
            Next
            Return drawn
        End Function

        ''' <summary>Zeichnet ein Objekt auf eine eigene transparente Ebene, laesst - falls vorhanden - die
        ''' Pixel-Anpassungen des Objekts darauf laufen und komponiert sie mit dem angegebenen Mischmodus
        ''' ein.</summary>

        ''' <summary>Verzerrt die fertig gezeichnete Ebene eines Objekts, bevor sie ins Bild kommt.
        '''
        ''' Der Weg ist derselbe wie beim Bild: fuer jeden Knoten eines Rasters wird die QUELLposition
        ''' bestimmt und das Ganze als Dreiecksnetz gezeichnet. Neu ist nur, dass das Raster hier ueber
        ''' der EBENE liegt, die Verzerrung aber am BILD eingestellt wurde - jeder Knoten muss also
        ''' erst in Bildkoordinaten gebracht, dort verschoben und wieder zurueckgerechnet werden.
        '''
        ''' Die Ebene WAECHST dabei: eine Verzerrung schiebt Bildpunkte nach aussen, und was ueber den
        ''' bisherigen Rand hinausgeht, waere sonst abgeschnitten. <paramref name="offsetX"/> und
        ''' <paramref name="offsetY"/> werden entsprechend nachgefuehrt.</summary>
        Friend Shared Function WarpObjectLayer(layer As SKBitmap, v As ObjectWarp,
                                                   imageWidth As Integer, imageHeight As Integer,
                                                   ByRef offsetX As Integer, ByRef offsetY As Integer) As SKBitmap
            If layer Is Nothing OrElse v Is Nothing OrElse v.IsEmpty Then Return Nothing
            If imageWidth <= 0 OrElse imageHeight <= 0 Then Return Nothing

            Const Steps As Integer = 24
            Dim node = (Steps + 1) * (Steps + 1)

            ' Wie weit sich ein Punkt der Ebene verschiebt, in BILDkoordinaten. Erst einmal fuer die
            ' Ecken und den Rand, um zu wissen, wie weit die Ebene wachsen muss.
            Dim targetX(node - 1) As Single, targetY(node - 1) As Single
            Dim sourceX(node - 1) As Single, quellY(node - 1) As Single
            Dim minX = Double.MaxValue, minY = Double.MaxValue
            Dim maxX = Double.MinValue, maxY = Double.MinValue

            For rowIdx = 0 To Steps
                For colIdx = 0 To Steps
                    Dim i = rowIdx * (Steps + 1) + colIdx
                    ' Der Knoten in Ebenenkoordinaten, dann in Bildkoordinaten.
                    Dim ex = colIdx / CDbl(Steps) * layer.Width
                    Dim ey = rowIdx / CDbl(Steps) * layer.Height
                    Dim bx = offsetX + ex, by = offsetY + ey
                    Dim z = MovePoint(v, bx, by, imageWidth, imageHeight)
                    targetX(i) = CSng(z.X)
                    targetY(i) = CSng(z.Y)
                    sourceX(i) = CSng(ex)
                    quellY(i) = CSng(ey)
                    If z.X < minX Then minX = z.X
                    If z.X > maxX Then maxX = z.X
                    If z.Y < minY Then minY = z.Y
                    If z.Y > maxY Then maxY = z.Y
                Next
            Next
            If maxX <= minX OrElse maxY <= minY Then Return Nothing

            ' Ein Rand von einem Bildpunkt, damit die aeusserste Reihe nicht auf der Kante liegt.
            Dim neuX = CInt(Math.Floor(minX)) - 1
            Dim neuY = CInt(Math.Floor(minY)) - 1
            Dim neuB = CInt(Math.Ceiling(maxX)) - neuX + 2
            Dim neuH = CInt(Math.Ceiling(maxY)) - neuY + 2
            ' Eine Verzerrung, die eine Ebene um das Zwanzigfache aufblaeht, ist keine Verzerrung
            ' mehr, sondern ein Rechenfehler - dann lieber unveraendert lassen.
            If neuB <= 0 OrElse neuH <= 0 Then Return Nothing
            If neuB > layer.Width * 20 + 64 OrElse neuH > layer.Height * 20 + 64 Then Return Nothing

            For i = 0 To node - 1
                targetX(i) = CSng(targetX(i) - neuX)
                targetY(i) = CSng(targetY(i) - neuY)
            Next

            Dim result = ImageGeometryMapper.WarpOverGridTo(layer, neuB, neuH, Steps, Steps,
                                                                     targetX, targetY, sourceX, quellY)
            If result Is Nothing Then Return Nothing
            offsetX = neuX
            offsetY = neuY
            Return result
        End Function

        ''' <summary>Wohin ein Bildpunkt durch die Verzerrung wandert. Alle drei Arten an einer
        ''' Stelle, damit die Zuordnung Punkt zu Ziel nur einmal existiert.</summary>
        Private Shared Function MovePoint(v As ObjectWarp, bx As Double, by As Double,
                                                imageWidth As Integer, imageHeight As Integer) As SKPoint
            Select Case v.Kind
                Case "Perspektive"
                    ' Bilineare Abbildung des Einheitsquadrats auf das verzerrte Viereck. Genau genug
                    ' fuer ein Objekt und ohne die Sonderfaelle einer projektiven Matrix.
                    Dim u = bx / imageWidth, w = by / imageHeight
                    Dim e = v.Corners
                    Dim top = (e(0) + (e(2) - e(0)) * u, e(1) + (e(3) - e(1)) * u)
                    Dim bottom = (e(6) + (e(4) - e(6)) * u, e(7) + (e(5) - e(7)) * u)
                    Dim px = top.Item1 + (bottom.Item1 - top.Item1) * w
                    Dim py = top.Item2 + (bottom.Item2 - top.Item2) * w
                    Return New SKPoint(CSng(px / 100.0 * imageWidth), CSng(py / 100.0 * imageHeight))

                Case "Gitter"
                    ' Zwischen den vier umgebenden Stuetzpunkten interpolieren.
                    '
                    ' NICHT auf 0..1 klemmen. Der Bezugsrahmen ist bei der objekteigenen Verzerrung
                    ' das OBJEKTRECHTECK, und ein Objekt zeichnet regelmaessig darueber hinaus - ein
                    ' Text auf Pfad legt seine Grundlinie auf einen Kreis, die Zeichen stehen
                    ' senkrecht darauf. Eine Klemmung faltet alles ausserhalb auf die Rechteckkante,
                    ' und genau das war zu sehen: gemessen an einem Kreispfad-Text blieben von der
                    ' Tinte 25 Prozent uebrig, der Rest lag ausserhalb und wurde auf den Rand
                    ' gestaucht - bei einer Verzerrung, die gar nichts verschiebt.
                    '
                    ' Ausserhalb wird stattdessen ueber die Randzelle FORTGESETZT: der Zellenindex
                    ' bleibt begrenzt (die Zeilen darunter), der Anteil darin darf negativ oder
                    ' groesser eins werden. Fuer ein Gitter auf seinem Raster ist das exakt die
                    ' Identitaet, ein unverzerrtes Objekt kommt also unveraendert durch.
                    Dim u = bx / imageWidth * v.Columns
                    Dim w = by / imageHeight * v.Rows
                    Dim s0 = Math.Max(0, Math.Min(v.Columns - 1, CInt(Math.Floor(u))))
                    Dim z0 = Math.Max(0, Math.Min(v.Rows - 1, CInt(Math.Floor(w))))
                    Dim tu = u - s0, tw = w - z0
                    Dim K = Function(colIdx As Integer, rowIdx As Integer) As (X As Double, Y As Double)
                                Dim i = (rowIdx * (v.Columns + 1) + colIdx) * 2
                                Return (v.Nodes(i), v.Nodes(i + 1))
                            End Function
                    Dim a = K(s0, z0), b = K(s0 + 1, z0), c = K(s0, z0 + 1), d = K(s0 + 1, z0 + 1)
                    Dim topPt = (a.X + (b.X - a.X) * tu, a.Y + (b.Y - a.Y) * tu)
                    Dim bottomPt = (c.X + (d.X - c.X) * tu, c.Y + (d.Y - c.Y) * tu)
                    Dim px = topPt.Item1 + (bottomPt.Item1 - topPt.Item1) * tw
                    Dim py = topPt.Item2 + (bottomPt.Item2 - topPt.Item2) * tw
                    Return New SKPoint(CSng(px / 100.0 * imageWidth), CSng(py / 100.0 * imageHeight))

                Case "Linien"
                    Dim qp(v.LineSource.Length - 1) As Double
                    Dim zp(v.LineTarget.Length - 1) As Double
                    For i = 0 To qp.Length - 1
                        Dim istX = (i Mod 2) = 0
                        qp(i) = v.LineSource(i) / 100.0 * If(istX, imageWidth, imageHeight)
                        zp(i) = v.LineTarget(i) / 100.0 * If(istX, imageWidth, imageHeight)
                    Next
                    ' Das Feld sagt, WOHER ein Zielpunkt seine Farbe holt. Fuer ein Objekt brauchen
                    ' wir die Gegenrichtung - wohin ein Punkt wandert -, also werden Quelle und Ziel
                    ' vertauscht.
                    Return ImageGeometryMapper.LinePoint(bx, by, zp, qp)
            End Select
            Return New SKPoint(CSng(bx), CSng(by))
        End Function

        ''' <summary>Traegt dieses Objekt eine Verzerrung - eine eigene oder die des Bildes?
        '''
        ''' Entscheidet mit darueber, ob ueber eine eigene Ebene gezeichnet wird. Ohne diese Frage
        ''' nahm der direkte Weg jedes Objekt ohne Anpassungen und ohne Mischmodus - also die
        ''' allermeisten -, und der zeichnet unverzerrt: die Verzerrung stand im Objekt, kam im Bild
        ''' aber nie an.</summary>
        ''' Friend statt Private: der Kompositor-Grenzschnitt (OverlaySceneRenderer) braucht dieselbe
        ''' Frage - ein verzerrtes Objekt bleibt im gebackenen Block, weil der Objekt-Bitmap-Cache
        ''' die Verzerrung nicht zeichnet.
        Friend Shared Function HasWarp(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing Then Return False
            If annotation.OwnWarp IsNot Nothing AndAlso Not annotation.OwnWarp.IsEmpty Then Return True
            Return annotation.Warp IsNot Nothing AndAlso Not annotation.Warp.IsEmpty
        End Function

        Private Shared Sub DrawAnnotationViaLayer(canvas As SKCanvas, annotation As ImageAnnotation,
                                                  renderAnnotation As ImageAnnotation, kind As String, rect As SKRect,
                                                  sourceWidth As Integer, sourceHeight As Integer,
                                                  layerWidth As Integer, layerHeight As Integer,
                                                  offsetX As Integer, offsetY As Integer,
                                                  blendModeName As String)
            Using layer = New SKBitmap(layerWidth, layerHeight, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using layerCanvas = New SKCanvas(layer)
                    layerCanvas.Clear(SKColors.Transparent)
                    layerCanvas.Translate(-offsetX, -offsetY)
                    DrawAnnotationOnCanvas(layerCanvas, kind, renderAnnotation, rect, sourceWidth, sourceHeight)
                End Using

                ' VERZERREN, bevor die Objektanpassungen greifen: die Verzerrung ist Geometrie und
                ' gehoert vor die Farbe, so wie beim Bild auch. Die Ebene kann dabei wachsen, deshalb
                ' wandert der Versatz mit.
                Dim drawn = layer
                Dim zwischen As SKBitmap = Nothing
                Dim eigene As SKBitmap = Nothing
                Dim vx = offsetX, vy = offsetY

                ' ERST die eigene Verzerrung des Objekts, DANN die des Bildes. Die eigene beschreibt
                ' seine Form, die des Bildes, wo es im Bild liegt - in dieser Reihenfolge gelesen
                ' ergibt beides zusammen genau das, was man auf dem Schirm erwartet.
                If annotation IsNot Nothing AndAlso annotation.OwnWarp IsNot Nothing AndAlso
                   Not annotation.OwnWarp.IsEmpty Then
                    ' Der Bezug ist das OBJEKTRECHTECK, nicht die Ebene: die eigene Verzerrung steht in
                    ' Prozent DES OBJEKTS, die Ebene ist aber so gross wie die gerenderte Flaeche. Mit
                    ' der Ebene als Bezug las eine kleine Verzerrung sich als eine ueber das ganze
                    ' Bild - der Text landete weit neben seinem Rahmen.
                    Dim rx = CInt(Math.Floor(rect.Left)), ry = CInt(Math.Floor(rect.Top))
                    Dim rw = Math.Max(1, CInt(Math.Round(rect.Width)))
                    Dim rh = Math.Max(1, CInt(Math.Round(rect.Height)))
                    Dim ox = offsetX - rx, oy = offsetY - ry
                    zwischen = WarpObjectLayer(layer, annotation.OwnWarp, rw, rh, ox, oy)
                    If zwischen IsNot Nothing Then
                        drawn = zwischen
                        ' Der zurueckgegebene Versatz liegt im Raum des Rechtecks und muss zurueck in
                        ' Bildkoordinaten.
                        vx = rx + ox
                        vy = ry + oy
                    End If
                End If

                If annotation IsNot Nothing AndAlso annotation.Warp IsNot Nothing AndAlso
                   Not annotation.Warp.IsEmpty Then
                    eigene = WarpObjectLayer(drawn, annotation.Warp, sourceWidth, sourceHeight, vx, vy)
                    If eigene IsNot Nothing Then drawn = eigene
                End If

                Try
                    If HasObjectAdjustments(annotation) Then
                        Dim objectAdj = annotation.Adjustments.ExtractPixelAdjustments()
                        objectAdj.SourceWidthPixels = drawn.Width
                        objectAdj.SourceHeightPixels = drawn.Height
                        Using processedLayer = ProcessBitmapBase(drawn, objectAdj)
                            DrawAnnotationLayerAt(canvas, processedLayer, blendModeName, vx - offsetX, vy - offsetY)
                        End Using
                    Else
                        DrawAnnotationLayerAt(canvas, drawn, blendModeName, vx - offsetX, vy - offsetY)
                    End If
                Finally
                    eigene?.Dispose()
                    zwischen?.Dispose()
                End Try
            End Using
        End Sub

        Private Const TransparentColorHex As String = "#00000000"

        ''' <summary>Objekte, bei denen sich Fuellung und Kontur getrennt zeichnen lassen. Ausgenommen sind
        ''' Arten, deren Inhalt nicht die "Fuellung" ist (Bild, QR-Code, SVG, Wasserzeichen mit Bild - die
        ''' kaemen sonst doppelt), reine Kontur-Arten (Linie, Pfeil, Spirale - da bliebe zum Mischen nichts
        ''' uebrig) und Pinselstriche. Ohne sichtbare Kontur ist das Aufteilen ohnehin sinnlos.</summary>
        Private Shared Function SplitsStrokeFromBlend(renderAnnotation As ImageAnnotation, kind As String) As Boolean
            If renderAnnotation Is Nothing OrElse renderAnnotation.BlendIncludesStroke Then Return False
            If IsNormalAnnotationBlendMode(renderAnnotation.BlendMode) Then Return False
            If renderAnnotation.StrokeWidth <= 0 Then Return False
            If ParseColor(renderAnnotation.StrokeColor, SKColors.Black).Alpha = 0 Then Return False
            Select Case kind
                Case "image", "selectionimage", "qr", "qrcode", "qr-code", "svg", "watermark",
                     "line", "arrow", "spiral", "brush", "eraser"
                    Return False
                Case Else
                    Return True
            End Select
        End Function

        ''' Fuellung ohne Kontur bzw. Kontur ohne Fuellung: statt jede der Zeichenroutinen um einen Schalter
        ''' zu erweitern, wird mit durchsichtig gesetzter Kontur- bzw. Fuellfarbe gezeichnet.
        Private Shared Function AnnotationFillOnly(source As ImageAnnotation) As ImageAnnotation
            Dim clone = source.Clone()
            clone.StrokeColor = TransparentColorHex
            Return clone
        End Function

        Private Shared Function AnnotationStrokeOnly(source As ImageAnnotation) As ImageAnnotation
            Dim clone = source.Clone()
            clone.FillColor = TransparentColorHex
            clone.FillColor2 = TransparentColorHex
            ' Schatten und Gluehen gehoeren zur Silhouette und liegen bereits unter der gemischten
            ' Fuellung - ein zweites Mal gezeichnet wuerden sie sich selbst verdoppeln.
            clone.ShadowEnabled = False
            clone.GlowEnabled = False
            Return clone
        End Function

        ''' <summary>Wie <see cref="DrawAnnotationLayer"/>, aber mit Versatz - eine verzerrte Ebene
        ''' ist groesser als die urspruengliche und faengt weiter oben links an.</summary>
        Private Shared Sub DrawAnnotationLayerAt(canvas As SKCanvas, layer As SKBitmap,
                                                 blendModeName As String, dx As Integer, dy As Integer)
            If dx = 0 AndAlso dy = 0 Then
                DrawAnnotationLayer(canvas, layer, blendModeName)
                Return
            End If
            Using paint = New SKPaint With {.BlendMode = ResolveAnnotationBlendMode(blendModeName), .IsAntialias = True}
                canvas.DrawBitmap(layer, dx, dy, paint)
            End Using
        End Sub

        Private Shared Sub DrawAnnotationLayer(canvas As SKCanvas, layer As SKBitmap, blendModeName As String)
            Using paint = New SKPaint With {.BlendMode = ResolveAnnotationBlendMode(blendModeName), .IsAntialias = True}
                canvas.DrawBitmap(layer, 0, 0, paint)
            End Using
        End Sub

        Private Shared Function IsNormalAnnotationBlendMode(blendModeName As String) As Boolean
            Return ResolveAnnotationBlendMode(blendModeName) = SKBlendMode.SrcOver
        End Function

        Private Shared Function ResolveAnnotationBlendMode(blendModeName As String) As SKBlendMode
            Select Case If(blendModeName, "Normal").Trim().ToLowerInvariant()
                Case "multiply" : Return SKBlendMode.Multiply
                Case "screen" : Return SKBlendMode.Screen
                Case "overlay" : Return SKBlendMode.Overlay
                Case "darken" : Return SKBlendMode.Darken
                Case "lighten" : Return SKBlendMode.Lighten
                Case "colordodge" : Return SKBlendMode.ColorDodge
                Case "colorburn" : Return SKBlendMode.ColorBurn
                Case "hardlight" : Return SKBlendMode.HardLight
                Case "softlight" : Return SKBlendMode.SoftLight
                Case "difference" : Return SKBlendMode.Difference
                Case "exclusion" : Return SKBlendMode.Exclusion
                Case "plus" : Return SKBlendMode.Plus
                Case "hue" : Return SKBlendMode.Hue
                Case "saturation" : Return SKBlendMode.Saturation
                Case "color" : Return SKBlendMode.Color
                Case "luminosity" : Return SKBlendMode.Luminosity
                Case Else : Return SKBlendMode.SrcOver
            End Select
        End Function

        ' Zeichnet ein einzelnes Objekt anhand seiner Art (Kind) - wird sowohl für das normale
        ' Zeichnen als auch (auf einer separaten Offscreen-Maske) für Schatten/Glow in
        ' DrawAnnotationEffects wiederverwendet, damit beide Pfade exakt dieselbe Silhouette ergeben.
        Private Shared Sub DrawAnnotationShape(canvas As SKCanvas, kind As String, annotation As ImageAnnotation, rect As SKRect, x As Single, y As Single, maxWidth As Single, fontSize As Single, fill As SKColor, stroke As SKColor, strokeWidth As Single, alphaFactor As Single)
            Select Case kind
                Case "frame"
                    ' Der Rahmen sitzt am Rechteck des Objekts, und das ist beim Rahmen immer das
                    ' ganze Bild (siehe ComputeAnnotationRect) - er bleibt damit an der Bildkante,
                    ' auch wenn spaeter zugeschnitten oder die Leinwand geaendert wird.
                    Dim frameFill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawFrameOnCanvas(canvas, rect, annotation.FrameSizePercent / 100.0F, fill,
                                      annotation.FrameCornerRadiusPercent / 100.0F, annotation.FrameEffect,
                                      annotation.FillKind, frameFill2,
                                      annotation.GradientAngleDegrees, annotation.GradientInverted,
                                      annotation.FrameSymbol, annotation.FrameSymbolSpacingPercent,
                                      annotation.FrameSymbolRotate, stroke, annotation.StrokeWidth)
                Case "rectangle", "rect", "selectionfill"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawShape(canvas, rect, fill, stroke, strokeWidth, False, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "roundedrectangle", "rounded-rectangle"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawRoundedRectangle(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "ellipse", "circle"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawShape(canvas, rect, fill, stroke, strokeWidth, True, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "square"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawSquare(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "triangle"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawTriangle(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "cone"
                    DrawCone(canvas, rect, fill, stroke, strokeWidth)
                Case "pyramid"
                    DrawPyramid(canvas, rect, fill, stroke, strokeWidth)
                Case "trapezoid"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawTrapezoid(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "diamond"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawDiamond(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "polygon"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawRegularPolygon(canvas, rect, 6, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "star"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawStar(canvas, rect, 5, 0.45F, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "doublestar", "double-star"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawStar(canvas, rect, 8, 0.42F, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "spiral"
                    DrawSpiral(canvas, rect, stroke, strokeWidth)
                Case "droplet"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawDroplet(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "ellipsespeechbubble", "ellipse-speech-bubble"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawEllipseSpeechBubble(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "rectspeechbubble", "rect-speech-bubble"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawRectSpeechBubble(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "speechbubble", "speech-bubble", "bubble"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawSpeechBubble(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "heart"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawHeart(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "cloud"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawCloud(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "line"
                    DrawLine(canvas, rect, stroke, strokeWidth, False)
                Case "arrow"
                    DrawLine(canvas, rect, stroke, strokeWidth, True)
                Case "symbol"
                    DrawSingleGlyph(canvas, If(String.IsNullOrWhiteSpace(annotation.Text), "★", annotation.Text), rect, fill, stroke, annotation.StrokeWidth, annotation.FontFamily)
                Case "qr", "qrcode", "qr-code"
                    ' Beim QR-Code ist FillColor die Hintergrundfarbe, StrokeColor die Modulfarbe.
                    DrawQrCode(canvas, If(String.IsNullOrWhiteSpace(annotation.Text), "FerrumPix", annotation.Text), rect, stroke, fill)
                Case "image", "selectionimage"
                    ' Ohne Seitenverhaeltnis-Sperre wird das Bild auf die Objekt-Box gestreckt.
                    DrawImageAnnotation(canvas, annotation.ImagePath, rect, annotation.Opacity, stroke, annotation.StrokeWidth, stretchToFill:=(kind = "selectionimage" OrElse Not annotation.LockAspect))
                Case "svg"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawSvgAnnotation(canvas, annotation.ImagePath, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "watermark"
                    If Not String.IsNullOrWhiteSpace(annotation.ImagePath) Then
                        DrawImageAnnotation(canvas, annotation.ImagePath, rect, annotation.Opacity, stroke, annotation.StrokeWidth, stretchToFill:=Not annotation.LockAspect)
                    Else
                        Dim watermark = If(String.IsNullOrWhiteSpace(annotation.Text), "FerrumPix", annotation.Text)
                        Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                        ' Pfad-Parameter durchreichen wie beim normalen Text: der Renderer kann
                        ' das laengst, hier wurden sie nur nicht weitergegeben - das Wasserzeichen
                        ' blieb dadurch immer gerade.
                        DrawAnnotationText(canvas, watermark, x, y, maxWidth, fontSize, WithAlpha(fill, If(fill.Alpha = 255, CByte(130), fill.Alpha)), stroke, annotation.StrokeWidth, annotation.FontFamily, rect, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted, annotation.TextPathKind, annotation.TextPathBend, annotation.TextPathStartOffset, annotation.LetterSpacingPercent, annotation.Bold, annotation.Italic)
                    End If
                Case Else
                    If Not String.IsNullOrWhiteSpace(annotation.Text) Then
                        Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                        DrawAnnotationText(canvas, annotation.Text, x, y, maxWidth, fontSize, fill, stroke, annotation.StrokeWidth, annotation.FontFamily, rect, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted, annotation.TextPathKind, annotation.TextPathBend, annotation.TextPathStartOffset, annotation.LetterSpacingPercent, annotation.Bold, annotation.Italic)
                    End If
            End Select
        End Sub

        ' Rendert das Objekt einmal auf eine transparente Offscreen-Maske derselben Canvas-Größe,
        ' färbt diese Silhouette per SKBlendMode.SrcIn auf Schatten-/Glow-Farbe um, blurrt sie und
        ' komponiert sie (versetzt bzw. additiv) VOR dem eigentlichen Objekt auf den Haupt-Canvas.
        ''' Die Maske wird nur so groß wie nötig (Objekt-Bounds + Blur/Versatz-Rand) statt
        ''' bildschirmfüllend angelegt - bei größeren Fotos und häufigen Live-Neuzeichnungen
        ''' (z.B. während des Verschiebens per Slider) spart das pro Aufruf eine große
        ''' Bitmap-Allokation samt Blur über die gesamte Canvas. Bei rotierten Objekten (der
        ''' Canvas hat zu diesem Zeitpunkt schon die RotateDegrees-Transformation aktiv, siehe
        ''' ApplyAnnotations) ist die tatsächliche Bildschirm-Bounding-Box in unrotierten
        ''' rect-Koordinaten nicht trivial zu bestimmen - dort bleibt es beim sicheren,
        ''' bildschirmfüllenden Fallback.
        ''' <summary>Grenzen, in denen das Glühen gerechnet wird (siehe DrawAnnotationEffects). Die Kosten
        ''' hängen an FLÄCHE × RADIUS, deshalb müssen beide gedeckelt werden: Skias Dilate kostet linear im
        ''' Radius, und der Radius wächst mit der Objektgröße - aber auch ein kleiner Radius auf einer sehr
        ''' großen Maske ist teuer. Was darüber liegt, wird verkleinert gerechnet und wieder hochgezogen.</summary>
        ''' <summary>Regler "Weichzeichnen" (0-100) -> Gauss-Sigma, bemessen an der Objektgroesse.
        ''' Der Faktor lag bei 0.6, solange die Weichzeichnung wirkungslos war (MaskFilter bei
        ''' DrawBitmap, siehe unten) - er war nie an einem sichtbaren Ergebnis geeicht. Mit wirksamem
        ''' Blur war damit schon der Standardwert 6 stark verwaschen und die obere Haelfte des Reglers
        ''' loeste den Schatten vollstaendig auf. Erst 0.15, dann 0.075: gemessen wurde nicht die
        ''' Deckung in der Schattenmitte (die bleibt hoch), sondern der SICHTBARE Saum neben dem
        ''' Objekt - und dessen Kontrast steckt fast ganz in der ersten Reglerhaelfte (Deckung 254 bei
        ''' Regler 10, 177 bei 50, danach nur noch 161/153). Groesseres Sigma verteilt den Schatten
        ''' dann bloss breiter, statt ihn sichtbar zu veraendern. Mit 0.075 liegt das alte Verhalten
        ''' bei Regler 50 am Ende des Reglers, und der ganze Weg ist nutzbar
        ''' ("macht nur bis zur Haelfte Sinn, danach ist der Schatten quasi weg").</summary>
        Private Const ShadowBlurSigmaFactor As Single = 0.075F

        Private Const MaxGlowDilatePx As Single = 12.0F
        Private Const MaxGlowDim As Single = 512.0F

        Private Shared Sub DrawAnnotationEffects(canvas As SKCanvas, kind As String, annotation As ImageAnnotation, rect As SKRect, x As Single, y As Single, maxWidth As Single, fontSize As Single, fill As SKColor, stroke As SKColor, strokeWidth As Single, alphaFactor As Single, canvasWidth As Integer, canvasHeight As Integer)
            ' Bewusst relativ zur Objekt-Bounding-Box (nicht zur ganzen Canvas wie bei RetouchRadius/
            ' BrushSize) skaliert: bei kleinem Text auf einem großen Foto wäre ein an der Canvas-Größe
            ' bemessener Blur-Radius so riesig, dass er sich fast unsichtbar verwaschen würde (genau das
            ' Problem "Glow hat bei Text keine Auswirkung") - Text-/Objektgröße variiert unabhängig von
            ' der Fotoauflösung, der Effekt soll aber immer proportional zum jeweiligen Objekt wirken.
            ' Skalierungsfaktor 0.4 (vormals 0.12): bei 0.12 blieb der Blur-Radius bei üblichen
            ' Slider-Werten (Default Glow=10, Shadow=6) so klein (wenige Zehntel-Prozent der
            ' Objektgröße), dass er komplett unter dem später deckend gezeichneten Objekt
            ' verschwand - "Glow wirkungslos"/"Shadow-Stärke ohne Auswirkung".
            Dim objSize = Math.Max(1.0F, Math.Min(rect.Width, rect.Height))
            ' Glühen als echtes AUSSEN-Glühen: die Silhouette wird per Dilate nach außen vergrößert und
            ' erst danach weich gezeichnet. Ein reiner (großer) Gauß-Blur verteilt die Glow-Energie so
            ' dünn, dass außerhalb des Objekts fast nichts sichtbar bleibt ("Glühen bleibt in den
            ' Objektgrenzen") - mit Dilate reicht das Glühen sichtbar und deckend über die Kante hinaus.
            Dim glowReach = objSize * Clamp(annotation.GlowBlur, 0, 100) / 100.0F * 1.5F
            Dim glowDilate = Math.Max(0, CInt(Math.Round(glowReach * 0.5F)))
            Dim glowSigma = Math.Max(0.1F, glowReach * 0.17F)
            Dim glowMaskReach = glowDilate + 3.0F * glowSigma
            Dim shadowBlurPx = objSize * Clamp(annotation.ShadowBlur, 0, 100) / 100.0F * ShadowBlurSigmaFactor
            Dim offsetX = objSize * annotation.ShadowOffsetXPercent / 100.0F
            Dim offsetY = objSize * annotation.ShadowOffsetYPercent / 100.0F

            Dim maskLeft As Integer = 0
            Dim maskTop As Integer = 0
            Dim maskWidth = canvasWidth
            Dim maskHeight = canvasHeight
            If Math.Abs(annotation.RotationDegrees) <= 0.01F Then
                Dim pad = Math.Max(glowMaskReach, shadowBlurPx * 3.0F) + Math.Max(Math.Abs(offsetX), Math.Abs(offsetY)) + 4.0F
                ' Text an Pfad: die Glyphen ragen bis zu einer Schrifthoehe ueber das
                ' Layout-Rechteck hinaus. Ohne den Zusatzrand beschneidet der Masken-Ausschnitt
                ' die Silhouette - Schatten/Gluehen fehlten an den Enden des gebogenen Textes
                ' bzw. brachen hart ab.
                If Not String.IsNullOrWhiteSpace(annotation.TextPathKind) Then
                    pad += annotation.FontSizePixels * ComputeTextPathFitRatio(annotation) * 1.2F
                End If
                maskLeft = Math.Max(0, CInt(Math.Floor(rect.Left - pad)))
                maskTop = Math.Max(0, CInt(Math.Floor(rect.Top - pad)))
                Dim maskRight = Math.Min(canvasWidth, CInt(Math.Ceiling(rect.Right + pad)))
                Dim maskBottom = Math.Min(canvasHeight, CInt(Math.Ceiling(rect.Bottom + pad)))
                maskWidth = Math.Max(1, maskRight - maskLeft)
                maskHeight = Math.Max(1, maskBottom - maskTop)
            End If

            Using mask = New SKBitmap(maskWidth, maskHeight, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using maskCanvas = New SKCanvas(mask)
                    maskCanvas.Clear(SKColors.Transparent)
                    maskCanvas.Translate(-maskLeft, -maskTop)
                    DrawAnnotationShape(maskCanvas, kind, annotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor)
                End Using

                If annotation.GlowEnabled Then
                    Dim glowColor = ApplyAlpha(ParseColor(annotation.GlowColor, SKColors.Yellow), alphaFactor * Clamp(annotation.GlowStrength, 0, 100) / 100.0F)

                    ' Das Glühen wird in KLEINERER Auflösung gerechnet und danach hochskaliert. Grund: Skias
                    ' Dilate kostet linear im Radius, und der Radius hängt an der Objektgröße - ein großer
                    ' Text mit vollem Glühen kam auf Radius 180 und brauchte über zehn Sekunden PRO Render,
                    ' bei jedem Reglertick neu. Das Ergebnis ist ohnehin ein weichgezeichneter Klumpen
                    ' (Dilate + Gauß) und enthält keine hohen Frequenzen, die beim Verkleinern verlorengehen
                    ' könnten: klein gerechnet und wieder hochgezogen sieht es genauso aus - nur schnell.
                    Dim glowScale = 1.0F
                    If glowDilate > MaxGlowDilatePx Then glowScale = MaxGlowDilatePx / CSng(glowDilate)
                    Dim longestSide = CSng(Math.Max(maskWidth, maskHeight))
                    If longestSide > MaxGlowDim Then glowScale = Math.Min(glowScale, MaxGlowDim / longestSide)
                    Dim glowW = Math.Max(1, CInt(Math.Round(maskWidth * glowScale)))
                    Dim glowH = Math.Max(1, CInt(Math.Round(maskHeight * glowScale)))
                    Dim scaledDilate = Math.Max(0, CInt(Math.Round(glowDilate * glowScale)))
                    Dim scaledSigma = Math.Max(0.1F, glowSigma * glowScale)

                    Using smallMask = New SKBitmap(glowW, glowH, SKColorType.Rgba8888, SKAlphaType.Premul)
                        Using smallCanvas = New SKCanvas(smallMask)
                            smallCanvas.Clear(SKColors.Transparent)
                            Using scalePaint = New SKPaint With {.IsAntialias = True}
                                DrawBitmapSampled(smallCanvas, mask,
                                                  New SKRect(0, 0, maskWidth, maskHeight),
                                                  New SKRect(0, 0, glowW, glowH), SamplingHigh, scalePaint)
                            End Using
                        End Using

                        ' Silhouette einfärben -> nach außen vergrößern (Dilate) -> weichzeichnen. Als
                        ' verkettete ImageFilter, damit das Glühen sichtbar über die Objektkante hinausreicht.
                        Using glowSmall = New SKBitmap(glowW, glowH, SKColorType.Rgba8888, SKAlphaType.Premul)
                            Using glowCanvas = New SKCanvas(glowSmall)
                                glowCanvas.Clear(SKColors.Transparent)
                                Using glowColorFilter = SKColorFilter.CreateBlendMode(glowColor, SKBlendMode.SrcIn)
                                    Using coloredFilter = SKImageFilter.CreateColorFilter(glowColorFilter)
                                        Dim spreadFilter As SKImageFilter = coloredFilter
                                        Dim dilatedOwned As SKImageFilter = Nothing
                                        Try
                                            If scaledDilate > 0 Then
                                                dilatedOwned = SKImageFilter.CreateDilate(scaledDilate, scaledDilate, coloredFilter)
                                                spreadFilter = dilatedOwned
                                            End If
                                            Using glowImageFilter = SKImageFilter.CreateBlur(scaledSigma, scaledSigma, spreadFilter)
                                                Using paint = New SKPaint With {.ImageFilter = glowImageFilter}
                                                    glowCanvas.DrawBitmap(smallMask, 0, 0, paint)
                                                End Using
                                            End Using
                                        Finally
                                            dilatedOwned?.Dispose()
                                        End Try
                                    End Using
                                End Using
                            End Using

                            ' Bewusst SrcOver statt additiv (Plus): Das Overlay zeichnet das Glühen auf
                            ' Transparenz (Plus und SrcOver liefern dort dasselbe) und blendet es dann per
                            ' SrcOver übers Foto - beim gebackenen Bild würde Plus die Glow-Farbe hingegen
                            ' aufs Foto ADDIEREN und dadurch auswaschen. SrcOver macht beide Pfade gleich kräftig.
                            Using paint = New SKPaint With {.BlendMode = SKBlendMode.SrcOver, .IsAntialias = True}
                                DrawBitmapSampled(canvas, glowSmall,
                                                  New SKRect(0, 0, glowW, glowH),
                                                  New SKRect(maskLeft, maskTop, maskLeft + maskWidth, maskTop + maskHeight),
                                                  SamplingHigh, paint)
                            End Using
                        End Using
                    End Using
                End If

                If annotation.ShadowEnabled Then
                    Dim shadowColor = ApplyAlpha(ParseColor(annotation.ShadowColor, New SKColor(0, 0, 0, 128)), alphaFactor * Clamp(annotation.ShadowStrength, 0, 100) / 100.0F)
                    Dim shadowSource = mask
                    Dim roundedShadowMask As SKBitmap = Nothing
                    Try
                        ' Abgerundeter Schatten rundet die ECKEN DER SILHOUETTE ab (siehe
                        ' BuildRoundedSilhouette) - die Objektform bleibt erhalten. Der Glow-Effekt
                        ' bleibt davon unberührt und folgt weiter der ungerundeten Form.
                        If annotation.ShadowRounded Then
                            Dim cornerRadius = Math.Min(rect.Width, rect.Height) / 2.0F * Clamp(annotation.ShadowCornerRadiusPercent, 0, 100) / 100.0F
                            roundedShadowMask = BuildRoundedSilhouette(mask, cornerRadius)
                            If roundedShadowMask IsNot Nothing Then shadowSource = roundedShadowMask
                        End If

                        ' Schattengröße: um die Objektmitte skalieren, sodass der Schatten über das Objekt
                        ' hinauswachsen (oder schrumpfen) kann. Der Blur-Radius wird durch den Skalierungs-
                        ' faktor geteilt, weil die anschließende Canvas-Skalierung ihn wieder hochmultipliziert -
                        ' so bleibt die Weichzeichnung unabhängig von der gewählten Größe.
                        Dim shadowScale = Clamp(annotation.ShadowSizePercent, 10, 400) / 100.0F
                        Dim shadowSigma = Math.Max(0.1F, shadowBlurPx / shadowScale)
                        ' Weichzeichnung MUSS hier ein ImageFilter sein, kein MaskFilter: der Schatten
                        ' wird per DrawBitmap gezeichnet, und ein MaskFilter wirkt nur auf die Deckung
                        ' gezeichneter GEOMETRIE - bei DrawBitmap tut er schlicht nichts (gemessen mit
                        ' SkiaSharp 3.119, sigma 4/12/30 alle unveraendert hart). Genau daran lag der
                        ' Befund "Schatten hat keine weiche Kante": der Weichzeichnen-Regler war
                        ' wirkungslos, der Schatten immer eine harte Silhouette.
                        Using shadowColorFilter = SKColorFilter.CreateBlendMode(shadowColor, SKBlendMode.SrcIn)
                            Using shadowImageFilter = SKImageFilter.CreateBlur(shadowSigma, shadowSigma)
                                Using paint = New SKPaint With {
                                    .ColorFilter = shadowColorFilter,
                                    .ImageFilter = shadowImageFilter
                                }
                                    canvas.Save()
                                    ' Versatz unskaliert (außerhalb der Skalierung angewandt), Skalierung um die Objektmitte.
                                    canvas.Translate(rect.MidX + offsetX, rect.MidY + offsetY)
                                    canvas.Scale(shadowScale, shadowScale)
                                    canvas.Translate(-rect.MidX, -rect.MidY)
                                    canvas.DrawBitmap(shadowSource, maskLeft, maskTop, paint)
                                    canvas.Restore()
                                End Using
                            End Using
                        End Using
                    Finally
                        roundedShadowMask?.Dispose()
                    End Try
                End If
            End Using
        End Sub

        ''' Harte Alpha-Schwelle bei halber Deckung: aus einer weichgezeichneten Maske wird wieder eine
        ''' scharfe Form - nur eben mit runden Ecken. Die Farbkanaele bleiben unveraendert; die
        ''' SkiaSharp-Bindung verlangt trotzdem ALLE VIER Tabellen (Nothing wirft).
        Private Shared ReadOnly IdentityColorTable As Byte() = Enumerable.Range(0, 256).Select(Function(i) CByte(i)).ToArray()
        Private Shared ReadOnly AlphaThresholdTable As Byte() = Enumerable.Range(0, 256).Select(Function(i) CByte(If(i < 128, 0, 255))).ToArray()

        ''' <summary>Rundet die ECKEN einer Silhouette ab, ohne ihre Form zu ersetzen: weichzeichnen und
        ''' anschliessend wieder hart schwellen. Ein Gauss zieht Ecken staerker ein als gerade Kanten,
        ''' die Schwelle macht daraus wieder eine scharfe Kontur - der klassische Weg, weil Skias
        ''' Dilate/Erode ein RECHTECKIGES Strukturelement nutzen und damit eckig blieben.
        ''' Vorher zeichnete der Schalter ein abgerundetes Rechteck der Bounding-Box, warf also die
        ''' Objektform weg - bei Text oder Ellipse wurde der Schatten zum Kasten
        '''. Nothing = kein Rundungsbedarf, der Aufrufer nimmt dann die Originalmaske.</summary>
        Private Shared Function BuildRoundedSilhouette(mask As SKBitmap, cornerRadius As Single) As SKBitmap
            If mask Is Nothing OrElse cornerRadius < 0.5F Then Return Nothing
            ' Der Gauss rundet mit etwa dem doppelten Sigma - so trifft der Regler die gewuenschte Ecke.
            Dim sigma = Math.Max(0.1F, cornerRadius * 0.5F)
            Dim rounded As SKBitmap = Nothing
            Try
                rounded = New SKBitmap(mask.Width, mask.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using roundCanvas = New SKCanvas(rounded)
                    roundCanvas.Clear(SKColors.Transparent)
                    Using blur = SKImageFilter.CreateBlur(sigma, sigma)
                        Using threshold = SKColorFilter.CreateTable(AlphaThresholdTable, IdentityColorTable,
                                                                    IdentityColorTable, IdentityColorTable)
                            Using sharpen = SKImageFilter.CreateColorFilter(threshold, blur)
                                Using paint = New SKPaint With {.ImageFilter = sharpen}
                                    roundCanvas.DrawBitmap(mask, 0, 0, paint)
                                End Using
                            End Using
                        End Using
                    End Using
                End Using
                Return rounded
            Catch
                rounded?.Dispose()
                Return Nothing
            End Try
        End Function

        Private Shared Function ApplyAlpha(color As SKColor, factor As Single) As SKColor
            Return New SKColor(color.Red, color.Green, color.Blue, CByte(Math.Max(0, Math.Min(255, color.Alpha * Clamp(factor, 0, 1)))))
        End Function

        ' SKTypeface.FromFamilyName ist unter Linux/Fontconfig ein bekannt langsamer Pfad
        ' (Font-Matching-Scan), der ohne Cache bei jedem einzelnen Bake einer Text-/Wasserzeichen-
        ' Annotation erneut ausgeführt wurde. SKTypeface-Instanzen sind immutable und threadsicher
        ' wiederverwendbar, daher genügt ein einfacher, nie geleerter Cache über die kleine,
        ' begrenzte Menge an im Editor tatsächlich genutzten Font-Familiennamen.
        Private Shared ReadOnly _typefaceCache As New Dictionary(Of String, SKTypeface)()
        Private Shared ReadOnly _typefaceCacheLock As New Object()

        ''' <summary>Schriftschnitt aus Familie und Stil. Der Cacheschluessel enthaelt den STIL -
        ''' ohne ihn haette der erste Aufruf (etwa normal) alle spaeteren ueberdeckt und Fett/Kursiv
        ''' waeren wirkungslos geblieben, ohne Fehlermeldung.
        ''' Fehlt der Familie ein echter Fett- oder Kursivschnitt, faellt Skia auf den naechsten
        ''' vorhandenen zurueck; ein synthetisches Schraegstellen macht es NICHT.</summary>
        Private Shared Function GetTypeface(fontFamily As String, Optional bold As Boolean = False,
                                            Optional italic As Boolean = False) As SKTypeface
            Dim key = If(fontFamily, "") & "|" & If(bold, "b", "") & If(italic, "i", "")
            SyncLock _typefaceCacheLock
                Dim cached As SKTypeface = Nothing
                If _typefaceCache.TryGetValue(key, cached) Then Return cached
                Dim stil = New SKFontStyle(If(bold, SKFontStyleWeight.Bold, SKFontStyleWeight.Normal),
                                           SKFontStyleWidth.Normal,
                                           If(italic, SKFontStyleSlant.Italic, SKFontStyleSlant.Upright))
                Dim created = SKTypeface.FromFamilyName(fontFamily, stil)
                _typefaceCache(key) = created
                Return created
            End SyncLock
        End Function

        Private Shared Sub DrawAnnotationText(canvas As SKCanvas, text As String, x As Single, y As Single, maxWidth As Single, fontSize As Single, fill As SKColor, stroke As SKColor, strokeWidth As Single, fontFamily As String, bounds As SKRect, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False, Optional textPathKind As String = "", Optional textPathBend As Single = 0, Optional textPathStartOffset As Single = 0, Optional letterSpacingPercent As Single = 0, Optional bold As Boolean = False, Optional italic As Boolean = False)
            ' Text an Pfad: EIN Zweig fuer Kontur und Fuellung, damit beide exakt dieselben
            ' Glyphenpositionen bekommen (und damit auch die Effekt-Maske, die ueber dieselbe
            ' Routine laeuft - Regel "Objektinhalt nur aus GENAU EINEM Renderpfad").
            ' warpGlyphs:=False ist entscheidend: mit dem Standard (True) VERBIEGT Skia jede
            ' Buchstabenkontur entlang der Kruemmung (innen gestaucht, aussen gedehnt) - bei
            ' grosser Schrift auf enger Kurve wirkte der Text stark verzerrt
            '. False platziert die Glyphen STARR und rotiert sie nur zur Tangente,
            ' wie Illustrator/Photoshop es tun.
            Dim path As SKPath = Nothing
            If Not String.IsNullOrWhiteSpace(textPathKind) Then
                path = BuildTextPath(bounds, textPathKind, textPathBend, textPathStartOffset)
            End If
            Try
                Using font = CreateFont(fontFamily, fontSize, bold, italic)
                    ' Abstand in Pixeln aus dem Prozentwert - relativ zur EFFEKTIVEN Schriftgroesse,
                    ' die die Pfad-Einpassung unten noch aendern kann. Wird deshalb nach jeder
                    ' Groessenaenderung neu berechnet.
                    Dim spacing = font.Size * letterSpacingPercent / 100.0F
                    ' Auf dem Pfad gibt es keinen Zeilenumbruch - Absaetze laufen als eine Zeile weiter.
                    Dim pathText = If(path IsNot Nothing,
                                      text.Replace(vbCrLf, " ").Replace(vbCr, " ").Replace(vbLf, " "),
                                      text)
                    If path IsNot Nothing Then
                        ' Text mittig auf den Pfad setzen; der Startversatz verschiebt von dort.
                        ' Der Start wird IN DEN PFAD gebacken (GetSegment) statt als hOffset
                        ' uebergeben: beide DrawTextOnPath-Ueberladungen wenden den Offset in
                        ' SkiaSharp 3.119 DOPPELT an (gemessen: Offset 100 -> Start +200) und
                        ' verschieben auf gekruemmten Pfaden zusaetzlich quer zur Kurve.
                        Using measure = New SKPathMeasure(path, False)
                            Dim pathLength = measure.Length
                            Dim textWidth = MeasureTextSpaced(font, pathText, spacing)
                            ' Schrift an den Pfad anpassen, damit kein Buchstabe wegfaellt: Kreis
                            ' waechst UND schrumpft auf den Umfang (kleine Fuge), Bogen/Welle
                            ' schrumpfen nur bei Ueberlaenge. Gleiche Formel wie
                            ' ComputeTextPathFitRatio - die Rand-Berechnungen rechnen damit.
                            If textWidth > 0 Then
                                Dim fit As Single
                                ' StartsWith, nicht Equals: "CircleInverted" ist derselbe geschlossene
                                ' Kreis. Mit Equals waere der invertierte Modus ohne Groessenanpassung
                                ' geblieben - der Text haette den Kreis ueber- oder unterlaufen.
                                If textPathKind.StartsWith("Circle", StringComparison.OrdinalIgnoreCase) Then
                                    ' Gleiche Deckelung wie in ComputeTextPathFitRatio (halber
                                    ' Radius als Obergrenze der Glyphenhoehe) - beide Formeln muessen
                                    ' synchron bleiben, sonst weichen Raender und Render voneinander ab.
                                    Dim maxGrow = Math.Max(1.0F, Math.Min(bounds.Width, bounds.Height) * 0.25F / Math.Max(1.0F, font.Size))
                                    fit = Math.Max(0.02F, Math.Min(maxGrow, pathLength * 0.97F / textWidth))
                                Else
                                    fit = Math.Max(0.02F, Math.Min(1.0F, pathLength / textWidth))
                                End If
                                If Math.Abs(fit - 1.0F) > 0.005F Then
                                    font.Size = font.Size * fit
                                    ' Abstand haengt an der Schriftgroesse - nach dem Einpassen neu.
                                    spacing = font.Size * letterSpacingPercent / 100.0F
                                    textWidth = MeasureTextSpaced(font, pathText, spacing)
                                End If
                            End If
                            Dim startDistance = Math.Max(0.0F, (pathLength - textWidth) / 2.0F)
                            ' Beim geschlossenen Kreis steckt der Startversatz bereits im Startwinkel.
                            ' Auch hier StartsWith: beim geschlossenen Kreis - egal ob normal oder
                            ' invertiert - steckt der Startversatz bereits im Startwinkel und darf
                            ' nicht ein zweites Mal aufaddiert werden.
                            If Not textPathKind.StartsWith("Circle", StringComparison.OrdinalIgnoreCase) Then
                                startDistance += textPathStartOffset / 100.0F * pathLength
                            End If
                            If startDistance > 0.5F AndAlso startDistance < pathLength - 1.0F Then
                                Dim segment As New SKPath()
                                If measure.GetSegment(startDistance, pathLength, segment, startWithMoveTo:=True) Then
                                    path.Dispose()
                                    path = segment
                                Else
                                    segment.Dispose()
                                End If
                            End If
                        End Using
                    End If

                    If strokeWidth > 0 Then
                        Using strokePaint = New SKPaint With {
                            .Color = stroke,
                            .IsAntialias = True,
                            .Style = SKPaintStyle.Stroke,
                            .StrokeWidth = Math.Max(1.0F, strokeWidth)
                        }
                            If path IsNot Nothing Then
                                DrawTextOnPathSpaced(canvas, pathText, path, font, strokePaint, spacing)
                            Else
                                DrawWrappedText(canvas, text, x, y, maxWidth, fontSize, font, strokePaint, spacing)
                            End If
                        End Using
                    End If

                    Using fillPaint = New SKPaint With {
                        .Color = fill,
                        .IsAntialias = True,
                        .Style = SKPaintStyle.Fill
                    }
                        Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
                        If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                            fillPaint.Shader = CreateFillGradientShader(bounds, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
                        End If
                        If path IsNot Nothing Then
                            DrawTextOnPathSpaced(canvas, pathText, path, font, fillPaint, spacing)
                        Else
                            DrawWrappedText(canvas, text, x, y, maxWidth, fontSize, font, fillPaint, spacing)
                        End If
                    End Using
                End Using
            Finally
                path?.Dispose()
            End Try
        End Sub

        ''' <summary>Faktor, um den die Schrift eines Pfadtextes skaliert wird, damit ALLE Buchstaben
        ''' auf den Pfad passen (beim Kreis fielen ueberzaehlige Buchstaben
        ''' einfach weg). Kreis: Text laeuft immer genau einmal um den Umfang - waechst UND schrumpft
        ''' (kleine Fuge, damit Ende und Anfang nicht kollidieren). Bogen/Welle: nur schrumpfen bei
        ''' Ueberlaenge, sonst bleibt der Groessen-Regler das Mass. Skalenunabhaengig (Pfadlaenge und
        ''' Textbreite wachsen mit demselben Faktor), daher fuer Basis- wie Vorschau-Koordinaten gueltig.
        ''' Wird auch von den Rand-Berechnungen (Dirty-Rect/Overlay/Effekt-Maske) benutzt - die muessen
        ''' mit der EFFEKTIVEN Groesse rechnen, sonst beschneiden sie gewachsene Kreis-Texte.</summary>
        Friend Shared Function ComputeTextPathFitRatio(annotation As ImageAnnotation) As Single
            If annotation Is Nothing OrElse String.IsNullOrWhiteSpace(annotation.TextPathKind) Then Return 1.0F
            Dim text = If(annotation.Text, "").Replace(vbCrLf, " ").Replace(vbCr, " ").Replace(vbLf, " ")
            ' Ein Wasserzeichen ohne eigenen Text wird als "FerrumPix" gezeichnet - die Einpassung
            ' muss auf DEMSELBEN Text rechnen, sonst passt der Kreis nicht zum sichtbaren Wort.
            If String.IsNullOrWhiteSpace(text) AndAlso
               String.Equals(annotation.Kind, "Watermark", StringComparison.OrdinalIgnoreCase) Then
                text = "FerrumPix"
            End If
            If String.IsNullOrWhiteSpace(text) Then Return 1.0F
            Try
                Dim rect = SKRect.Create(0, 0, Math.Max(1.0F, annotation.WidthPixels), Math.Max(1.0F, annotation.HeightPixels))
                Using path = BuildTextPath(rect, annotation.TextPathKind, annotation.TextPathBend, annotation.TextPathStartOffset)
                    Using measure = New SKPathMeasure(path, False)
                        Using font = CreateFont(annotation.FontFamily, Math.Max(1.0F, annotation.FontSizePixels), annotation.Bold, annotation.Italic)
                            ' Abstand einrechnen - sonst weicht die Einpassung vom gezeichneten
                            ' Text ab, und beim Kreis liefe der Text ueber den Umfang hinaus.
                            Dim distance = font.Size * annotation.LetterSpacingPercent / 100.0F
                            Dim textWidth = MeasureTextSpaced(font, text, distance)
                            If textWidth <= 0 Then Return 1.0F
                            ' StartsWith statt Equals: "CircleInverted" ist geometrisch derselbe
                            ' geschlossene Kreis und braucht dieselbe Deckelung. Mit Equals waere
                            ' der neue Modus stillschweigend ohne Groessenanpassung geblieben.
                            If annotation.TextPathKind.StartsWith("Circle", StringComparison.OrdinalIgnoreCase) Then
                                ' Wachstum gedeckelt: Glyphenhoehe hoechstens der HALBE Radius -
                                ' beim vollen Radius sprengten zwei Riesenbuchstaben Box und Kreis
                                ' (visuell verifiziert).
                                Dim maxGrow = Math.Max(1.0F, Math.Min(rect.Width, rect.Height) * 0.25F / Math.Max(1.0F, annotation.FontSizePixels))
                                Return Math.Max(0.02F, Math.Min(maxGrow, measure.Length * 0.97F / textWidth))
                            End If
                            Return Math.Max(0.02F, Math.Min(1.0F, measure.Length / textWidth))
                        End Using
                    End Using
                End Using
            Catch
                Return 1.0F
            End Try
        End Function

        ''' <summary>Pfad fuer "Text an Pfad", aus dem Objektrechteck abgeleitet und als dichte
        ''' Punktfolge aufgebaut (die Glyphen werden per Bogenlaenge platziert, eine Polylinie mit
        ''' 96 Stuetzen ist dafuer unsichtbar glatt und erspart die Winkelmathematik dreier
        ''' Sonderfaelle). Bogen: Kreisbogen ueber die Rechteckbreite, Pfeilhoehe aus der Kruemmung
        ''' (negativ = nach unten). Welle: eine Sinusperiode, Amplitude aus der Kruemmung. Kreis:
        ''' ins Rechteck eingepasst, Start oben plus Startversatz; negative Kruemmung laeuft innen
        ''' (gegen den Uhrzeigersinn).</summary>
        Private Shared Function BuildTextPath(rect As SKRect, kind As String, bend As Single, startOffset As Single) As SKPath
            Const Steps As Integer = 96
            Dim path = New SKPath()
            Dim normalized = If(kind, "").Trim().ToLowerInvariant()
            Dim amount = Math.Max(-100.0F, Math.Min(100.0F, bend)) / 100.0F

            Select Case normalized
                Case "circle", "circleinverted"
                    ' Radius aus min(Breite, Hoehe): ein Kreis bleibt ein Kreis. Ihn ueber das
                    ' Rechteck zu strecken ergaebe bei breiten Objekten eine flache Ellipse - also
                    ' faktisch einen Bogen (ausprobiert und wieder verworfen).
                    Dim radius = Math.Min(rect.Width, rect.Height) / 2.0F
                    Dim cx = rect.MidX, cy = rect.MidY
                    Dim inverted = normalized = "circleinverted"

                    ' Bildschirmkoordinaten (y nach UNTEN): der Punkt zum Winkel a ist
                    ' (cos a, sin a), also a=90 Grad = unten, a=270 Grad = oben.
                    ' Der Text wird auf dem Pfad zentriert; seine Mitte liegt damit eine halbe Runde
                    ' hinter dem Start. Beide Varianten starten deshalb UNTEN, damit der Text OBEN
                    ' sitzt - sie unterscheiden sich NUR in der Laufrichtung.
                    '
                    ' NORMAL (Winkel waechst): oben laeuft die Tangente nach rechts. Die Buchstaben
                    ' stehen mit dem Fuss auf dem Kreis und dem Kopf nach AUSSEN - Abzeichen-Oberseite.
                    '
                    ' INVERTIERT (Winkel faellt): oben laeuft die Tangente nach links, und damit
                    ' kippt die Aufrechte der Buchstaben mit. Sie haengen dann mit dem Kopf nach
                    ' INNEN und dem Fuss nach aussen - der Text liegt gleichsam auf der Innenseite
                    ' des Rings. Es ist derselbe Ort wie bei "Kreis", nur die Schrift ist auf der
                    ' Linie umgeschlagen.
                    Dim baseDirection = If(inverted, -1.0, 1.0)
                    Dim startAngle = Math.PI / 2.0 + baseDirection * startOffset / 100.0 * 2.0 * Math.PI
                    ' Negative Kruemmung dreht die Laufrichtung wie bisher zusaetzlich um.
                    Dim direction = If(amount < 0, -baseDirection, baseDirection)
                    For i = 0 To Steps
                        Dim a = startAngle + direction * 2.0 * Math.PI * i / Steps
                        Dim px = CSng(cx + radius * Math.Cos(a))
                        Dim py = CSng(cy + radius * Math.Sin(a))
                        If i = 0 Then path.MoveTo(px, py) Else path.LineTo(px, py)
                    Next

                Case "wave"
                    Dim amplitude = amount * rect.Height / 2.0F
                    For i = 0 To Steps
                        Dim t = i / CSng(Steps)
                        Dim px = rect.Left + t * rect.Width
                        Dim py = CSng(rect.MidY - amplitude * Math.Sin(t * 2.0 * Math.PI))
                        If i = 0 Then path.MoveTo(px, py) Else path.LineTo(px, py)
                    Next

                Case Else ' "arc"
                    ' Kreisbogen durch die beiden Seitenmitten, Pfeilhoehe aus der Kruemmung.
                    ' Praktisch keine Biegung -> gerade Linie (die Sehnenformel wuerde degenerieren).
                    Dim sagitta = amount * rect.Height / 2.0F
                    If Math.Abs(sagitta) < 0.5F Then
                        path.MoveTo(rect.Left, rect.MidY)
                        path.LineTo(rect.Right, rect.MidY)
                    Else
                        Dim half = rect.Width / 2.0F
                        Dim radius = (half * half + sagitta * sagitta) / (2.0F * Math.Abs(sagitta))
                        Dim cy = rect.MidY + Math.Sign(sagitta) * (radius - Math.Abs(sagitta))
                        Dim halfSweep = Math.Asin(Math.Min(1.0, half / radius))
                        For i = 0 To Steps
                            Dim a = -halfSweep + 2.0 * halfSweep * i / Steps
                            Dim px = CSng(rect.MidX + radius * Math.Sin(a))
                            Dim py = CSng(cy - Math.Sign(sagitta) * radius * Math.Cos(a))
                            If i = 0 Then path.MoveTo(px, py) Else path.LineTo(px, py)
                        Next
                    End If
            End Select
            Return path
        End Function

        ''' opacity ist auf der 0-100-Skala (wie annotation.Opacity), NICHT der bereits normalisierten
        ''' 0-1 alphaFactor-Skala, die für die übrigen (bereits alpha-vorgemischten) Fill/Stroke-Farben
        ''' verwendet wird - siehe Aufrufstelle im Select Case (Kind "image").
        Private Shared Sub DrawImageAnnotation(canvas As SKCanvas, imagePath As String, rect As SKRect, opacity As Single, stroke As SKColor, strokeWidth As Single, Optional stretchToFill As Boolean = False)
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then Return

            Using bitmap = SKBitmap.Decode(imagePath)
                If bitmap Is Nothing OrElse bitmap.Width <= 0 OrElse bitmap.Height <= 0 Then Return

                Dim fitRect = If(stretchToFill, rect, FitRectKeepingAspectRatio(rect, bitmap.Width, bitmap.Height))
                Using paint = New SKPaint With {
                    .IsAntialias = True,
                    .Color = New SKColor(255, 255, 255, CByte(Math.Max(0, Math.Min(255, 255 * Clamp(opacity, 0, 100) / 100.0F))))
                }
                    DrawBitmapSampled(canvas, bitmap, SKRect.Create(0, 0, bitmap.Width, bitmap.Height), fitRect, SamplingHigh, paint)
                End Using

                If strokeWidth > 0 Then
                    Using strokePaint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = strokeWidth, .IsAntialias = True}
                        canvas.DrawRect(fitRect, strokePaint)
                    End Using
                End If
            End Using
        End Sub

        ' Zeichnet ein beliebiges SVG-Icon (aus Assets/Icons/**) als Objekt auf dem Foto - die
        ' Pfad-Geometrie kommt aus der SVG-Datei, Füllung/Kontur/Deckkraft aus den Live-Einstellungen
        ' im Anpassungspanel (wie bei den übrigen Formen).
        Private Class ShapePathData
            Public Property Path As SKPath
            Public Property Bounds As SKRect
        End Class

        Private Shared ReadOnly _shapePathCache As New Dictionary(Of String, ShapePathData)()
        Private Shared ReadOnly _shapePathCacheLock As New Object()

        ''' Skaliert wie SvgIcon.vb (uniform/"contain", zentriert anhand der eigenen Bounds) statt
        ''' pro Achse getrennt zu strecken - sonst weicht das gebackene Rendering bei nicht-quadratischen
        ''' Ziel-Rects (jedes nicht-quadratische Foto) sichtbar von der Live-Vorschau ab.
        Private Shared Sub DrawSvgAnnotation(canvas As SKCanvas, iconPath As String, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            If String.IsNullOrWhiteSpace(iconPath) Then Return
            Dim shape = GetShapePath(iconPath)
            If shape Is Nothing OrElse shape.Path.IsEmpty OrElse shape.Bounds.Width <= 0 OrElse shape.Bounds.Height <= 0 Then Return

            Dim scaleX = rect.Width / shape.Bounds.Width
            Dim scaleY = rect.Height / shape.Bounds.Height

            canvas.Save()
            canvas.Translate(rect.Left, rect.Top)
            canvas.Scale(scaleX, scaleY)
            canvas.Translate(-shape.Bounds.Left, -shape.Bounds.Top)

            Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
            If fill.Alpha > 0 Then
                If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                    ''' shape.Bounds statt rect: der Canvas ist an dieser Stelle bereits in den lokalen
                    ''' Pfad-Koordinatenraum transformiert (s.o.), der Shader muss im selben Koordinatenraum
                    ''' wie der gezeichnete Pfad definiert werden, sonst landet der Verlauf weit außerhalb
                    ''' des sichtbaren Bereichs und wirkt wie eine einfarbige Füllung.
                    Using shader = CreateFillGradientShader(shape.Bounds, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
                        Using fillPaint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                            canvas.DrawPath(shape.Path, fillPaint)
                        End Using
                    End Using
                Else
                    Using fillPaint = New SKPaint With {.Color = fill, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                        canvas.DrawPath(shape.Path, fillPaint)
                    End Using
                End If
            End If
            If strokeWidth > 0 Then
                Dim adjustedStroke = strokeWidth / Math.Max(0.0001F, Math.Min(scaleX, scaleY))
                Using strokePaint = New SKPaint With {
                    .Color = stroke,
                    .Style = SKPaintStyle.Stroke,
                    .StrokeWidth = adjustedStroke,
                    .IsAntialias = True,
                    .StrokeCap = SKStrokeCap.Round,
                    .StrokeJoin = SKStrokeJoin.Round
                }
                    canvas.DrawPath(shape.Path, strokePaint)
                End Using
            End If
            canvas.Restore()
        End Sub

        Private Shared Function GetShapePath(iconPath As String) As ShapePathData
            SyncLock _shapePathCacheLock
                Dim cached As ShapePathData = Nothing
                If _shapePathCache.TryGetValue(iconPath, cached) Then Return cached

                Dim parsed = ParseSvgToPath(iconPath)
                _shapePathCache(iconPath) = parsed
                Return parsed
            End SyncLock
        End Function

        Private Shared Function ParseSvgToPath(iconPath As String) As ShapePathData
            Try
                Dim svgText As String
                If iconPath.StartsWith("avares://", StringComparison.OrdinalIgnoreCase) Then
                    Using stream = AssetLoader.Open(New Uri(iconPath))
                        Using reader = New StreamReader(stream)
                            svgText = reader.ReadToEnd()
                        End Using
                    End Using
                Else
                    If Not File.Exists(iconPath) Then Return Nothing
                    svgText = File.ReadAllText(iconPath)
                End If

                Dim combined = New SKPath()
                Dim shapeRegex = New Regex("<(?<tag>path|rect|circle|ellipse|line)\b(?<attrs>[^>]*?)/?>", RegexOptions.Singleline)
                For Each m As Match In shapeRegex.Matches(svgText)
                    Dim d As String = Nothing
                    Dim attrs = m.Groups("attrs").Value
                    Select Case m.Groups("tag").Value
                        Case "path" : d = GetSvgAttr(attrs, "d")
                        Case "rect" : d = SvgRectToPath(attrs)
                        Case "circle" : d = SvgCircleToPath(attrs)
                        Case "ellipse" : d = SvgEllipseToPath(attrs)
                        Case "line" : d = SvgLineToPath(attrs)
                    End Select

                    If Not String.IsNullOrWhiteSpace(d) Then
                        Try
                            Dim subPath = SKPath.ParseSvgPathData(d)
                            If subPath IsNot Nothing Then combined.AddPath(subPath)
                        Catch
                        End Try
                    End If
                Next

                If combined.IsEmpty Then Return Nothing
                Return New ShapePathData With {.Path = combined, .Bounds = combined.Bounds}
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function GetSvgAttr(attrs As String, name As String) As String
            Dim m = Regex.Match(attrs, name & "\s*=\s*""(?<v>[^""]*)""")
            Return If(m.Success, m.Groups("v").Value, Nothing)
        End Function

        Private Shared Function GetSvgAttrNumber(attrs As String, name As String, fallback As Double) As Double
            Dim v = GetSvgAttr(attrs, name)
            If v Is Nothing Then Return fallback
            Return Double.Parse(v, Globalization.CultureInfo.InvariantCulture)
        End Function

        Private Shared Function SvgRectToPath(attrs As String) As String
            Dim x = GetSvgAttrNumber(attrs, "x", 0)
            Dim y = GetSvgAttrNumber(attrs, "y", 0)
            Dim w = GetSvgAttrNumber(attrs, "width", 0)
            Dim h = GetSvgAttrNumber(attrs, "height", 0)
            If w <= 0 OrElse h <= 0 Then Return Nothing

            Dim rx = GetSvgAttrNumber(attrs, "rx", GetSvgAttrNumber(attrs, "ry", 0))
            If rx <= 0 Then
                Return $"M{SvgNum(x)},{SvgNum(y)} H{SvgNum(x + w)} V{SvgNum(y + h)} H{SvgNum(x)} Z"
            End If
            rx = Math.Min(rx, Math.Min(w / 2, h / 2))

            Return $"M{SvgNum(x + rx)},{SvgNum(y)} " &
                   $"H{SvgNum(x + w - rx)} A{SvgNum(rx)},{SvgNum(rx)} 0 0 1 {SvgNum(x + w)},{SvgNum(y + rx)} " &
                   $"V{SvgNum(y + h - rx)} A{SvgNum(rx)},{SvgNum(rx)} 0 0 1 {SvgNum(x + w - rx)},{SvgNum(y + h)} " &
                   $"H{SvgNum(x + rx)} A{SvgNum(rx)},{SvgNum(rx)} 0 0 1 {SvgNum(x)},{SvgNum(y + h - rx)} " &
                   $"V{SvgNum(y + rx)} A{SvgNum(rx)},{SvgNum(rx)} 0 0 1 {SvgNum(x + rx)},{SvgNum(y)} Z"
        End Function

        Private Shared Function SvgCircleToPath(attrs As String) As String
            Dim cx = GetSvgAttrNumber(attrs, "cx", 0)
            Dim cy = GetSvgAttrNumber(attrs, "cy", 0)
            Dim r = GetSvgAttrNumber(attrs, "r", 0)
            If r <= 0 Then Return Nothing
            Return $"M{SvgNum(cx - r)},{SvgNum(cy)} A{SvgNum(r)},{SvgNum(r)} 0 1 0 {SvgNum(cx + r)},{SvgNum(cy)} A{SvgNum(r)},{SvgNum(r)} 0 1 0 {SvgNum(cx - r)},{SvgNum(cy)} Z"
        End Function

        Private Shared Function SvgEllipseToPath(attrs As String) As String
            Dim cx = GetSvgAttrNumber(attrs, "cx", 0)
            Dim cy = GetSvgAttrNumber(attrs, "cy", 0)
            Dim rx = GetSvgAttrNumber(attrs, "rx", 0)
            Dim ry = GetSvgAttrNumber(attrs, "ry", 0)
            If rx <= 0 OrElse ry <= 0 Then Return Nothing
            Return $"M{SvgNum(cx - rx)},{SvgNum(cy)} A{SvgNum(rx)},{SvgNum(ry)} 0 1 0 {SvgNum(cx + rx)},{SvgNum(cy)} A{SvgNum(rx)},{SvgNum(ry)} 0 1 0 {SvgNum(cx - rx)},{SvgNum(cy)} Z"
        End Function

        Private Shared Function SvgLineToPath(attrs As String) As String
            Dim x1 = GetSvgAttrNumber(attrs, "x1", 0)
            Dim y1 = GetSvgAttrNumber(attrs, "y1", 0)
            Dim x2 = GetSvgAttrNumber(attrs, "x2", 0)
            Dim y2 = GetSvgAttrNumber(attrs, "y2", 0)
            Return $"M{SvgNum(x1)},{SvgNum(y1)} L{SvgNum(x2)},{SvgNum(y2)}"
        End Function

        Private Shared Function SvgNum(value As Double) As String
            Return value.ToString(Globalization.CultureInfo.InvariantCulture)
        End Function

        Private Shared Function FitRectKeepingAspectRatio(target As SKRect, sourceWidth As Integer, sourceHeight As Integer) As SKRect
            If sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return target
            Dim targetWidth = Math.Max(1.0F, target.Width)
            Dim targetHeight = Math.Max(1.0F, target.Height)
            Dim sourceRatio = sourceWidth / CSng(sourceHeight)
            Dim targetRatio = targetWidth / targetHeight
            Dim drawWidth As Single
            Dim drawHeight As Single
            If sourceRatio > targetRatio Then
                drawWidth = targetWidth
                drawHeight = drawWidth / sourceRatio
            Else
                drawHeight = targetHeight
                drawWidth = drawHeight * sourceRatio
            End If
            Dim left = target.Left + (targetWidth - drawWidth) / 2.0F
            Dim top = target.Top + (targetHeight - drawHeight) / 2.0F
            Return New SKRect(left, top, left + drawWidth, top + drawHeight)
        End Function

        Private Shared Sub DrawShape(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, ellipse As Boolean, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
            If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                Using shader = CreateFillGradientShader(rect, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
                    Using fillPaint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                        If ellipse Then canvas.DrawOval(rect, fillPaint) Else canvas.DrawRect(rect, fillPaint)
                    End Using
                End Using
            Else
                Using fillPaint = New SKPaint With {.Color = fill, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                    If fill.Alpha > 0 Then
                        If ellipse Then canvas.DrawOval(rect, fillPaint) Else canvas.DrawRect(rect, fillPaint)
                    End If
                End Using
            End If

            Using strokePaint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = strokeWidth, .IsAntialias = True}
                If ellipse Then canvas.DrawOval(rect, strokePaint) Else canvas.DrawRect(rect, strokePaint)
            End Using
        End Sub

        ' Verlauf ist bewusst auf das übergebene Rect begrenzt (nicht die ganze Canvas wie beim
        ' bestehenden Vignette-Radialgradient in ApplyVignette) - Zentrum/Winkel beziehen sich auf
        ' die Objekt-Bounds, damit der Verlauf mit dem Objekt mitwandert/rotiert.
        Private Shared Function CreateFillGradientShader(rect As SKRect, normalizedFillKind As String, color1 As SKColor, color2 As SKColor, angleDegrees As Single, Optional inverted As Boolean = False) As SKShader
            Dim startColor = If(inverted, color2, color1)
            Dim endColor = If(inverted, color1, color2)

            If normalizedFillKind = "radialgradient" Then
                Dim center = New SKPoint(rect.MidX, rect.MidY)
                Dim radius = CSng(Math.Sqrt(CDbl(rect.Width) * rect.Width + CDbl(rect.Height) * rect.Height) / 2.0)
                Return SKShader.CreateRadialGradient(center, Math.Max(1.0F, radius), New SKColor() {startColor, endColor}, Nothing, SKShaderTileMode.Clamp)
            End If

            Dim angleRad = angleDegrees * Math.PI / 180.0
            Dim dx = CSng(Math.Cos(angleRad)) * rect.Width / 2.0F
            Dim dy = CSng(Math.Sin(angleRad)) * rect.Height / 2.0F
            Dim startPoint = New SKPoint(rect.MidX - dx, rect.MidY - dy)
            Dim endPoint = New SKPoint(rect.MidX + dx, rect.MidY + dy)
            Return SKShader.CreateLinearGradient(startPoint, endPoint, New SKColor() {startColor, endColor}, Nothing, SKShaderTileMode.Clamp)
        End Function

        Private Shared Sub DrawRoundedRectangle(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim radius = Math.Min(rect.Width, rect.Height) * 0.18F
            Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
            If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                Using shader = CreateFillGradientShader(rect, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
                    Using fillPaint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                        canvas.DrawRoundRect(rect, radius, radius, fillPaint)
                    End Using
                End Using
            ElseIf fill.Alpha > 0 Then
                Using fillPaint = New SKPaint With {.Color = fill, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                    canvas.DrawRoundRect(rect, radius, radius, fillPaint)
                End Using
            End If
            Using strokePaint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = strokeWidth, .IsAntialias = True}
                canvas.DrawRoundRect(rect, radius, radius, strokePaint)
            End Using
        End Sub

        Private Shared Sub DrawSquare(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim side = Math.Min(rect.Width, rect.Height)
            Dim x = rect.MidX - side / 2.0F
            Dim y = rect.MidY - side / 2.0F
            DrawShape(canvas, New SKRect(x, y, x + side, y + side), fill, stroke, strokeWidth, False, fillKind, fill2, gradientAngleDegrees, gradientInverted)
        End Sub

        Private Shared Sub DrawTriangle(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top)
                path.LineTo(rect.Right, rect.Bottom)
                path.LineTo(rect.Left, rect.Bottom)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawCone(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top + rect.Height * 0.12F)
                path.LineTo(rect.Right * 0.86F, rect.Bottom * 0.74F)
                path.ArcTo(New SKRect(rect.Left + rect.Width * 0.15F, rect.Bottom * 0.60F, rect.Right - rect.Width * 0.15F, rect.Bottom * 0.94F), 0, 180, False)
                path.LineTo(rect.MidX, rect.Top + rect.Height * 0.12F)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth)
            End Using
        End Sub

        Private Shared Sub DrawPyramid(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top)
                path.LineTo(rect.Right, rect.Bottom)
                path.LineTo(rect.Left, rect.Bottom)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth)
            End Using
            Using linePaint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = Math.Max(1.0F, strokeWidth * 0.7F), .IsAntialias = True, .StrokeCap = SKStrokeCap.Round}
                canvas.DrawLine(rect.MidX, rect.Top, rect.MidX, rect.Bottom - rect.Height * 0.18F, linePaint)
            End Using
        End Sub

        Private Shared Sub DrawTrapezoid(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                Dim inset = rect.Width * 0.22F
                path.MoveTo(rect.Left + inset, rect.Top)
                path.LineTo(rect.Right - inset, rect.Top)
                path.LineTo(rect.Right, rect.Bottom)
                path.LineTo(rect.Left, rect.Bottom)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawDiamond(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top)
                path.LineTo(rect.Right, rect.MidY)
                path.LineTo(rect.MidX, rect.Bottom)
                path.LineTo(rect.Left, rect.MidY)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawRegularPolygon(canvas As SKCanvas, rect As SKRect, sides As Integer, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                AddRegularPoints(path, rect, Math.Max(3, sides), 0.45F, -Math.PI / 2)
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawStar(canvas As SKCanvas, rect As SKRect, points As Integer, innerRadiusFactor As Single, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                Dim cx = rect.MidX
                Dim cy = rect.MidY
                Dim outerRadius = Math.Min(rect.Width, rect.Height) * 0.46F
                Dim innerRadius = outerRadius * innerRadiusFactor
                Dim total = Math.Max(3, points) * 2
                For i = 0 To total - 1
                    Dim radius = If(i Mod 2 = 0, outerRadius, innerRadius)
                    Dim angle = -Math.PI / 2 + i * Math.PI / points
                    Dim x = CSng(cx + Math.Cos(angle) * radius)
                    Dim y = CSng(cy + Math.Sin(angle) * radius)
                    If i = 0 Then path.MoveTo(x, y) Else path.LineTo(x, y)
                Next
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawDroplet(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top + rect.Height * 0.04F)
                path.CubicTo(rect.Right - rect.Width * 0.18F, rect.Top + rect.Height * 0.34F,
                             rect.Right - rect.Width * 0.06F, rect.Top + rect.Height * 0.58F,
                             rect.Right - rect.Width * 0.22F, rect.Top + rect.Height * 0.79F)
                path.CubicTo(rect.Right - rect.Width * 0.38F, rect.Bottom - rect.Height * 0.01F,
                             rect.Left + rect.Width * 0.38F, rect.Bottom - rect.Height * 0.01F,
                             rect.Left + rect.Width * 0.22F, rect.Top + rect.Height * 0.79F)
                path.CubicTo(rect.Left + rect.Width * 0.06F, rect.Top + rect.Height * 0.58F,
                             rect.Left + rect.Width * 0.18F, rect.Top + rect.Height * 0.34F,
                             rect.MidX, rect.Top + rect.Height * 0.04F)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawSpeechBubble(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim tailHeight = rect.Height * 0.20F
            Dim radius = Math.Min(rect.Width, rect.Height) * 0.12F
            Dim body = New SKRect(rect.Left + rect.Width * 0.04F,
                                  rect.Top + rect.Height * 0.06F,
                                  rect.Right - rect.Width * 0.04F,
                                  rect.Bottom - tailHeight)
            Using path = New SKPath()
                path.MoveTo(body.Left + radius, body.Top)
                path.LineTo(body.Right - radius, body.Top)
                path.QuadTo(body.Right, body.Top, body.Right, body.Top + radius)
                path.LineTo(body.Right, body.Bottom - radius)
                path.QuadTo(body.Right, body.Bottom, body.Right - radius, body.Bottom)
                path.LineTo(rect.Left + rect.Width * 0.46F, body.Bottom)
                path.LineTo(rect.Left + rect.Width * 0.24F, rect.Bottom - rect.Height * 0.04F)
                path.LineTo(rect.Left + rect.Width * 0.27F, body.Bottom)
                path.LineTo(body.Left + radius, body.Bottom)
                path.QuadTo(body.Left, body.Bottom, body.Left, body.Bottom - radius)
                path.LineTo(body.Left, body.Top + radius)
                path.QuadTo(body.Left, body.Top, body.Left + radius, body.Top)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawEllipseSpeechBubble(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top + rect.Height * 0.07F)
                path.CubicTo(rect.Right - rect.Width * 0.12F, rect.Top + rect.Height * 0.07F,
                             rect.Right - rect.Width * 0.03F, rect.Top + rect.Height * 0.28F,
                             rect.Right - rect.Width * 0.04F, rect.Top + rect.Height * 0.48F)
                path.CubicTo(rect.Right - rect.Width * 0.05F, rect.Top + rect.Height * 0.70F,
                             rect.Right - rect.Width * 0.25F, rect.Top + rect.Height * 0.84F,
                             rect.Right - rect.Width * 0.45F, rect.Top + rect.Height * 0.86F)
                path.LineTo(rect.Left + rect.Width * 0.24F, rect.Bottom - rect.Height * 0.05F)
                path.LineTo(rect.Left + rect.Width * 0.32F, rect.Top + rect.Height * 0.82F)
                path.CubicTo(rect.Left + rect.Width * 0.13F, rect.Top + rect.Height * 0.72F,
                             rect.Left + rect.Width * 0.04F, rect.Top + rect.Height * 0.56F,
                             rect.Left + rect.Width * 0.04F, rect.Top + rect.Height * 0.40F)
                path.CubicTo(rect.Left + rect.Width * 0.04F, rect.Top + rect.Height * 0.20F,
                             rect.Left + rect.Width * 0.18F, rect.Top + rect.Height * 0.07F,
                             rect.MidX, rect.Top + rect.Height * 0.07F)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawRectSpeechBubble(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim tailHeight = rect.Height * 0.20F
            Dim body = New SKRect(rect.Left + rect.Width * 0.04F,
                                  rect.Top + rect.Height * 0.05F,
                                  rect.Right - rect.Width * 0.04F,
                                  rect.Bottom - tailHeight)
            Using path = New SKPath()
                path.MoveTo(body.Left, body.Top)
                path.LineTo(body.Right, body.Top)
                path.LineTo(body.Right, body.Bottom)
                path.LineTo(rect.MidX + rect.Width * 0.16F, body.Bottom)
                path.LineTo(rect.MidX + rect.Width * 0.03F, rect.Bottom - rect.Height * 0.04F)
                path.LineTo(rect.MidX - rect.Width * 0.10F, body.Bottom)
                path.LineTo(body.Left, body.Bottom)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawHeart(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Bottom - rect.Height * 0.10F)
                path.CubicTo(rect.Left + rect.Width * 0.08F, rect.Top + rect.Height * 0.58F, rect.Left, rect.Top + rect.Height * 0.24F, rect.Left + rect.Width * 0.26F, rect.Top + rect.Height * 0.12F)
                path.CubicTo(rect.Left + rect.Width * 0.40F, rect.Top + rect.Height * 0.05F, rect.MidX, rect.Top + rect.Height * 0.17F, rect.MidX, rect.Top + rect.Height * 0.32F)
                path.CubicTo(rect.MidX, rect.Top + rect.Height * 0.17F, rect.Left + rect.Width * 0.60F, rect.Top + rect.Height * 0.05F, rect.Left + rect.Width * 0.74F, rect.Top + rect.Height * 0.12F)
                path.CubicTo(rect.Right, rect.Top + rect.Height * 0.24F, rect.Right - rect.Width * 0.08F, rect.Top + rect.Height * 0.58F, rect.MidX, rect.Bottom - rect.Height * 0.10F)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawCloud(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.Left + rect.Width * 0.24F, rect.Bottom - rect.Height * 0.22F)
                path.CubicTo(rect.Left + rect.Width * 0.07F, rect.Bottom - rect.Height * 0.22F, rect.Left + rect.Width * 0.04F, rect.Top + rect.Height * 0.47F, rect.Left + rect.Width * 0.18F, rect.Top + rect.Height * 0.39F)
                path.CubicTo(rect.Left + rect.Width * 0.20F, rect.Top + rect.Height * 0.19F, rect.Left + rect.Width * 0.42F, rect.Top + rect.Height * 0.12F, rect.Left + rect.Width * 0.55F, rect.Top + rect.Height * 0.27F)
                path.CubicTo(rect.Left + rect.Width * 0.66F, rect.Top + rect.Height * 0.20F, rect.Left + rect.Width * 0.82F, rect.Top + rect.Height * 0.28F, rect.Left + rect.Width * 0.83F, rect.Top + rect.Height * 0.44F)
                path.CubicTo(rect.Right - rect.Width * 0.02F, rect.Top + rect.Height * 0.49F, rect.Right - rect.Width * 0.06F, rect.Bottom - rect.Height * 0.22F, rect.Right - rect.Width * 0.22F, rect.Bottom - rect.Height * 0.22F)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub AddRegularPoints(path As SKPath, rect As SKRect, count As Integer, radiusFactor As Single, startAngle As Double)
            Dim radius = Math.Min(rect.Width, rect.Height) * radiusFactor
            For i = 0 To count - 1
                Dim angle = startAngle + i * Math.PI * 2.0 / count
                Dim x = CSng(rect.MidX + Math.Cos(angle) * radius)
                Dim y = CSng(rect.MidY + Math.Sin(angle) * radius)
                If i = 0 Then path.MoveTo(x, y) Else path.LineTo(x, y)
            Next
            path.Close()
        End Sub

        Private Shared Sub DrawSpiral(canvas As SKCanvas, rect As SKRect, stroke As SKColor, strokeWidth As Single)
            Using path = New SKPath()
                Dim cx = rect.MidX
                Dim cy = rect.MidY
                Dim maxRadius = Math.Min(rect.Width, rect.Height) * 0.40F
                Dim minRadius = Math.Max(3.0F, maxRadius * 0.12F)
                Dim turns = 2.6F
                Dim points = 80
                For i = 0 To points
                    Dim t = i / CDbl(points)
                    Dim angle = -Math.PI / 2 + turns * 2 * Math.PI * t
                    Dim radius = maxRadius - (maxRadius - minRadius) * t
                    Dim x = cx + Math.Cos(angle) * radius
                    Dim y = cy + Math.Sin(angle) * radius
                    If i = 0 Then
                        path.MoveTo(CSng(x), CSng(y))
                    Else
                        path.LineTo(CSng(x), CSng(y))
                    End If
                Next
                Using paint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = Math.Max(1.0F, strokeWidth), .IsAntialias = True, .StrokeCap = SKStrokeCap.Round, .StrokeJoin = SKStrokeJoin.Round}
                    canvas.DrawPath(path, paint)
                End Using
            End Using
        End Sub

        Private Shared Sub DrawClosedPath(canvas As SKCanvas, path As SKPath, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillBounds As SKRect = Nothing, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
            If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                Dim bounds = If(fillBounds.IsEmpty, path.Bounds, fillBounds)
                Using shader = CreateFillGradientShader(bounds, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
                    Using fillPaint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                        canvas.DrawPath(path, fillPaint)
                    End Using
                End Using
            ElseIf fill.Alpha > 0 Then
                Using fillPaint = New SKPaint With {.Color = fill, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                    canvas.DrawPath(path, fillPaint)
                End Using
            End If
            Using strokePaint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = strokeWidth, .IsAntialias = True, .StrokeCap = SKStrokeCap.Round, .StrokeJoin = SKStrokeJoin.Round}
                canvas.DrawPath(path, strokePaint)
            End Using
        End Sub

        Private Shared Sub DrawLine(canvas As SKCanvas, rect As SKRect, stroke As SKColor, strokeWidth As Single, arrow As Boolean)
            Dim effectiveStrokeWidth = If(arrow, strokeWidth, Math.Max(2.0F, strokeWidth))
            Using paint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = effectiveStrokeWidth, .StrokeCap = SKStrokeCap.Round, .IsAntialias = True}
                If arrow Then
                    Dim head = Math.Min(rect.Width * 0.28F, Math.Max(12.0F, strokeWidth * 4.0F))
                    Dim pad = Math.Max(strokeWidth * 0.5F, 1.0F)
                    Dim start = New SKPoint(rect.Left + pad, rect.MidY)
                    Dim tip = New SKPoint(rect.Right - pad, rect.MidY)
                    Dim headBackX = tip.X - head
                    Dim headHalfHeight = Math.Min(rect.Height * 0.36F, head * 0.55F)
                    canvas.DrawLine(start, tip, paint)
                    canvas.DrawLine(tip, New SKPoint(headBackX, tip.Y - headHalfHeight), paint)
                    canvas.DrawLine(tip, New SKPoint(headBackX, tip.Y + headHalfHeight), paint)
                Else
                    canvas.DrawLine(rect.Left, rect.MidY, rect.Right, rect.MidY, paint)
                End If
            End Using
        End Sub

        ''' Mehrere Striche derselben Ebene (siehe EditorViewModel.AddBrushStroke - Striche werden
        ''' gesammelt, statt für jeden eine eigene Ebene anzulegen) sind im Punktestring per ";"
        ''' getrennt und werden hier als eigenständige Teilpfade (je ein eigenes MoveTo) gezeichnet -
        ''' eine einzige durchgehende Linie würde sie sonst fälschlich miteinander verbinden.
        ''' <summary>Bekannte Pinsel-Varianten (Stufe 2). "soft" ist der klassische weiche Rundpinsel;
        ''' die übrigen sind texturierte Presets. Radiergummi erzwingt immer "soft".</summary>
        Friend Shared ReadOnly BrushPresetKeys As String() = {"soft", "marker", "acrylic", "sandpaper", "pencil", "smear", "spatter",
                                                             "charcoal", "crayon", "airbrush", "calligraphy", "stipple", "watercolor"}

        Private Shared Function NormalizeBrushPreset(preset As String) As String
            If String.IsNullOrWhiteSpace(preset) Then Return "soft"
            Dim key = preset.Trim().ToLowerInvariant()
            Return If(Array.IndexOf(BrushPresetKeys, key) >= 0, key, "soft")
        End Function

        Private Shared Sub DrawBrushStroke(canvas As SKCanvas, strokes As IEnumerable(Of BrushStroke), width As Integer, height As Integer, stroke As SKColor, strokeWidth As Single, hardnessPercent As Single, flowPercent As Single, preset As String, isEraser As Boolean, Optional eraserFill As SKColor? = Nothing)
            If strokes Is Nothing Then Return

            ' Der Radiergummi bleibt immer der weiche Rundpinsel - Korn/Textur hätte dort keinen Sinn.
            Dim key = If(isEraser, "soft", NormalizeBrushPreset(preset))
            Dim resolvedStrokeWidth = Math.Max(1.0F, strokeWidth)
            Dim hardness = Clamp(hardnessPercent, 0, 100) / 100.0F
            Dim blurSigma = resolvedStrokeWidth * (1.0F - hardness) * 0.5F
            Dim flow = Clamp(flowPercent, 0, 100) / 100.0F

            ' Texturierte Presets laufen über eine eigene Ebene, in die eine Korn-Textur gestanzt wird.
            If key = "acrylic" OrElse key = "sandpaper" OrElse key = "pencil" OrElse
               key = "charcoal" OrElse key = "crayon" Then
                DrawGrainBrushStroke(canvas, strokes, width, height, stroke, resolvedStrokeWidth, blurSigma, flow, key)
                Return
            End If

            ' Schmieren/Farbkleckse werden entlang des Pfades gestempelt (richtungsabhängig bzw. gestreut).
            If key = "smear" OrElse key = "spatter" OrElse key = "airbrush" OrElse
               key = "calligraphy" OrElse key = "stipple" OrElse key = "watercolor" Then
                DrawStampBrushStroke(canvas, strokes, width, height, stroke, resolvedStrokeWidth, blurSigma, flow, key)
                Return
            End If

            Dim paintColor = stroke
            Dim blendMode = SKBlendMode.SrcOver
            If isEraser Then
                If eraserFill.HasValue AndAlso eraserFill.Value.Alpha > 0 Then
                    paintColor = eraserFill.Value
                Else
                    blendMode = SKBlendMode.DstOut
                End If
            End If

            ' Marker: harte, flache Chisel-Kante und halbtransparent, damit sich überkreuzende Striche
            ' sichtbar aufbauen (wie ein echter Filzstift). Sonst wie der weiche Rundpinsel.
            Dim isMarker = key = "marker"
            Dim effectiveFlow = If(isMarker, flow * 0.72F, flow)
            Dim cap = If(isMarker, SKStrokeCap.Square, SKStrokeCap.Round)
            Dim join = If(isMarker, SKStrokeJoin.Bevel, SKStrokeJoin.Round)

            Using paint = New SKPaint With {
                .Color = paintColor.WithAlpha(CByte(Clamp(paintColor.Alpha * effectiveFlow, 0, 255))),
                .Style = SKPaintStyle.Stroke,
                .StrokeWidth = resolvedStrokeWidth,
                .StrokeCap = cap,
                .StrokeJoin = join,
                .IsAntialias = True
            }
                paint.BlendMode = blendMode
                If blurSigma > 0.05F Then paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSigma)

                For Each brushStroke In strokes
                    If brushStroke Is Nothing OrElse brushStroke.Points.Count < 2 Then Continue For

                    Using path = New SKPath()
                        For i As Integer = 0 To brushStroke.Points.Count - 1
                            Dim p = brushStroke.Points(i)
                            Dim target = New SKPoint(Clamp(p.X, 0, width), Clamp(p.Y, 0, height))
                            If i = 0 Then
                                path.MoveTo(target)
                            Else
                                path.LineTo(target)
                            End If
                        Next
                        canvas.DrawPath(path, paint)
                    End Using
                Next
            End Using
        End Sub

        ''' <summary>Zeichnet einen Pinselstrich mit Schatten und/oder Glühen: der Strich (inkl. Textur/
        ''' Preset) wird zunächst auf eine eigene Ebene gerendert, dann werden daraus per DropShadowOnly
        ''' aus der echten Silhouette Glühen (Halo, ohne Versatz) und Schatten (mit Versatz) unter den
        ''' Strich komponiert. Größe/Blur/Versatz skalieren mit der Strichbreite.</summary>
        Private Shared Sub DrawBrushStrokeWithEffects(canvas As SKCanvas, ann As ImageAnnotation, width As Integer, height As Integer, strokeColor As SKColor, strokeWidth As Single)
            If ann.Strokes Is Nothing Then Return
            Dim minX = Single.MaxValue, minY = Single.MaxValue
            Dim maxX = Single.MinValue, maxY = Single.MinValue
            Dim any = False
            For Each bs In ann.Strokes
                If bs Is Nothing OrElse bs.Points.Count < 1 Then Continue For
                For Each p In bs.Points
                    minX = Math.Min(minX, p.X) : minY = Math.Min(minY, p.Y)
                    maxX = Math.Max(maxX, p.X) : maxY = Math.Max(maxY, p.Y)
                    any = True
                Next
            Next
            If Not any Then Return

            Dim objSize = Math.Max(1.0F, strokeWidth)
            Dim shadowDx = If(ann.ShadowEnabled, Clamp(ann.ShadowOffsetXPercent, -100, 100) / 100.0F * objSize, 0.0F)
            Dim shadowDy = If(ann.ShadowEnabled, Clamp(ann.ShadowOffsetYPercent, -100, 100) / 100.0F * objSize, 0.0F)
            Dim shadowSigma = If(ann.ShadowEnabled, Clamp(ann.ShadowBlur, 0, 100) / 100.0F * objSize * ShadowBlurSigmaFactor, 0.0F)
            Dim glowSigma = If(ann.GlowEnabled, Clamp(ann.GlowBlur, 0, 100) / 100.0F * objSize * 0.8F, 0.0F)

            Dim pad = objSize + Math.Abs(shadowDx) + Math.Abs(shadowDy) + Math.Max(shadowSigma, glowSigma) * 3.0F + 4.0F
            Dim left = CInt(Math.Floor(Clamp(minX - pad, 0, width)))
            Dim top = CInt(Math.Floor(Clamp(minY - pad, 0, height)))
            Dim right = CInt(Math.Ceiling(Clamp(maxX + pad, 0, width)))
            Dim bottom = CInt(Math.Ceiling(Clamp(maxY + pad, 0, height)))
            Dim w = right - left, h = bottom - top
            If w <= 0 OrElse h <= 0 Then Return

            Using layer = New SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using lc = New SKCanvas(layer)
                    lc.Clear(SKColors.Transparent)
                    lc.Translate(-left, -top)
                    DrawBrushStroke(lc, ann.Strokes, width, height, strokeColor, strokeWidth,
                                    ann.HardnessPercent, ann.FlowPercent, ann.BrushPreset, False)
                End Using

                ' Glühen zuerst (Halo hinter dem Strich), dann Schatten, dann der Strich selbst.
                If ann.GlowEnabled AndAlso glowSigma > 0.05F Then
                    Dim glowColor = ApplyAlpha(ParseColor(ann.GlowColor, SKColors.Yellow), Clamp(ann.GlowStrength, 0, 100) / 100.0F)
                    Using p = New SKPaint()
                        p.ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 0, glowSigma, glowSigma, glowColor)
                        canvas.DrawBitmap(layer, left, top, p)
                    End Using
                End If
                If ann.ShadowEnabled Then
                    Dim shadowColor = ApplyAlpha(ParseColor(ann.ShadowColor, New SKColor(0, 0, 0, 128)), Clamp(ann.ShadowStrength, 0, 100) / 100.0F)
                    Using p = New SKPaint()
                        p.ImageFilter = SKImageFilter.CreateDropShadowOnly(shadowDx, shadowDy, Math.Max(0.01F, shadowSigma), Math.Max(0.01F, shadowSigma), shadowColor)
                        canvas.DrawBitmap(layer, left, top, p)
                    End Using
                End If
                canvas.DrawBitmap(layer, left, top)
            End Using
        End Sub

        ' Grain-Cache: je Preset eine deterministisch erzeugte Alpha-Korn-Kachel (256x256), die als
        ' wiederholender Shader in die Strichform gestanzt wird. Deterministisch, damit Vorschau und
        ' gebackenes Ergebnis identisch sind und Re-Renders nicht flackern. Wird nie disposed (Cache).
        Private Shared ReadOnly _grainCacheLock As New Object()
        Private Shared ReadOnly _grainBitmaps As New Dictionary(Of String, SKBitmap)(StringComparer.Ordinal)

        Private Shared Function Hash01(a As Integer, b As Integer, seed As Integer) As Single
            ' Ganzzahl-Hash in Long-Arithmetik mit 32-Bit-Maskierung, damit VB keinen Overflow wirft.
            Dim n As Long = (CLng(a) * 73856093L) Xor (CLng(b) * 19349663L) Xor (CLng(seed) * 83492791L)
            n = n And &HFFFFFFFFL
            n = (n Xor (n >> 13)) And &HFFFFFFFFL
            n = (n * 40503L) And &HFFFFFFFFL
            n = (n Xor (n >> 7)) And &HFFFFFFFFL
            Return CSng(n And &HFFFFFFL) / 16777216.0F
        End Function

        Private Shared Function MapGrainAlpha(key As String, v As Single) As Byte
            Dim a As Single
            Select Case key
                Case "acrylic" : a = v * 1.7F - 0.15F      ' überwiegend deckend, raue Lücken
                Case "sandpaper" : a = v * 1.35F - 0.2F    ' gröber, mehr Lücken
                ' Kohle: harte Schwelle mit breiten Lücken - bröseliger, kontrastreicher als Bleistift.
                Case "charcoal" : a = If(v > 0.32F, (v - 0.32F) * 2.1F, 0.0F)
                ' Wachsmalstift: satt deckend mit nur wenigen Aussetzern - Wachs schmiert zu, es
                ' bröselt nicht wie Kohle. Genau daran unterscheiden sich die beiden im Strich.
                Case "crayon" : a = Clamp(v * 2.6F - 0.35F, 0.0F, 1.0F)
                Case Else ' pencil: feines, sparsames Graphitkorn
                    a = If(v > 0.4F, (v - 0.4F) * 1.5F, 0.0F)
            End Select
            Return CByte(Clamp(a * 255.0F, 0, 255))
        End Function

        Private Shared Function BuildGrainBitmap(key As String) As SKBitmap
            Const size As Integer = 256
            ' Zellgröße = Korngröße der Kachel. Kohle und Wachs sind gröber als Graphit.
            Dim cell As Integer
            Select Case key
                Case "sandpaper" : cell = 3
                Case "acrylic" : cell = 2
                Case "charcoal" : cell = 4
                Case "crayon" : cell = 5
                Case Else : cell = 1
            End Select
            Dim bmp = New SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul)
            Dim px(size * size - 1) As SKColor
            For y As Integer = 0 To size - 1
                For x As Integer = 0 To size - 1
                    Dim baseV = Hash01(x \ cell, y \ cell, 12345)
                    Dim fine = Hash01(x, y, 777)
                    Dim v = baseV * 0.7F + fine * 0.3F
                    px(y * size + x) = New SKColor(255, 255, 255, MapGrainAlpha(key, v))
                Next
            Next
            bmp.Pixels = px
            Return bmp
        End Function

        Private Shared Function GetGrainBitmap(key As String) As SKBitmap
            SyncLock _grainCacheLock
                Dim existing As SKBitmap = Nothing
                If _grainBitmaps.TryGetValue(key, existing) Then Return existing
                Dim bmp = BuildGrainBitmap(key)
                _grainBitmaps(key) = bmp
                Return bmp
            End SyncLock
        End Function

        ''' <summary>Zeichnet texturierte Striche: erst die weiche Strichform auf eine eigene Ebene, dann
        ''' die Korn-Kachel per DstIn hineingestanzt, dann als Ganzes aufs Bild komponiert. Das Korn wird
        ''' in globalen Bildkoordinaten gesampelt (Ebene ist entsprechend verschoben), damit sich
        ''' überlappende Striche dasselbe Texturfeld teilen.</summary>
        Private Shared Sub DrawGrainBrushStroke(canvas As SKCanvas, strokes As IEnumerable(Of BrushStroke), width As Integer, height As Integer, color As SKColor, strokeWidth As Single, blurSigma As Single, flow As Single, key As String)
            Dim minX = Single.MaxValue, minY = Single.MaxValue
            Dim maxX = Single.MinValue, maxY = Single.MinValue
            Dim any = False
            For Each brushStroke In strokes
                If brushStroke Is Nothing OrElse brushStroke.Points.Count < 2 Then Continue For
                For Each p In brushStroke.Points
                    minX = Math.Min(minX, p.X) : minY = Math.Min(minY, p.Y)
                    maxX = Math.Max(maxX, p.X) : maxY = Math.Max(maxY, p.Y)
                    any = True
                Next
            Next
            If Not any Then Return

            Dim pad = strokeWidth * 0.6F + blurSigma * 3.0F + 2.0F
            Dim left = CInt(Math.Floor(Clamp(minX - pad, 0, width)))
            Dim top = CInt(Math.Floor(Clamp(minY - pad, 0, height)))
            Dim right = CInt(Math.Ceiling(Clamp(maxX + pad, 0, width)))
            Dim bottom = CInt(Math.Ceiling(Clamp(maxY + pad, 0, height)))
            Dim w = right - left, h = bottom - top
            If w <= 0 OrElse h <= 0 Then Return

            Dim layerColor = color.WithAlpha(CByte(Clamp(color.Alpha * flow, 0, 255)))
            Using layer = New SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using lc = New SKCanvas(layer)
                    lc.Clear(SKColors.Transparent)
                    lc.Translate(-left, -top)

                    Using shapePaint = New SKPaint With {
                        .Color = layerColor,
                        .Style = SKPaintStyle.Stroke,
                        .StrokeWidth = strokeWidth,
                        .StrokeCap = SKStrokeCap.Round,
                        .StrokeJoin = SKStrokeJoin.Round,
                        .IsAntialias = True
                    }
                        If blurSigma > 0.05F Then shapePaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSigma)
                        For Each brushStroke In strokes
                            If brushStroke Is Nothing OrElse brushStroke.Points.Count < 2 Then Continue For
                            Using path = New SKPath()
                                For i As Integer = 0 To brushStroke.Points.Count - 1
                                    Dim p = brushStroke.Points(i)
                                    Dim target = New SKPoint(Clamp(p.X, 0, width), Clamp(p.Y, 0, height))
                                    If i = 0 Then path.MoveTo(target) Else path.LineTo(target)
                                Next
                                lc.DrawPath(path, shapePaint)
                            End Using
                        Next
                    End Using

                    ' Korn in globalen Koordinaten in die Strichform stanzen.
                    Using grainShader = SKShader.CreateBitmap(GetGrainBitmap(key), SKShaderTileMode.Repeat, SKShaderTileMode.Repeat)
                        Using grainPaint = New SKPaint With {.Shader = grainShader, .BlendMode = SKBlendMode.DstIn, .IsAntialias = False}
                            lc.DrawRect(New SKRect(left, top, right, bottom), grainPaint)
                        End Using
                    End Using
                End Using

                canvas.DrawBitmap(layer, left, top)
            End Using
        End Sub

        ''' <summary>Stempelbasierte Striche: der Pfad wird per SKPathMeasure abgetastet und an jedem
        ''' Schritt eine Form gesetzt. "smear" tupft langgezogene, weiche, zur Strichrichtung gedrehte
        ''' Ovale (verwischter Zug, der der Kurve folgt); "spatter" setzt einen gebrochenen Kern plus
        ''' zufällig gestreute Tropfen neben der Linie (viele kleine, wenige große - "zu viel Farbe").
        ''' Alle Zufallswerte stammen deterministisch aus dem Stempelindex, damit Vorschau, gebackenes
        ''' Bild und Re-Renders identisch bleiben.</summary>
        Private Shared Sub DrawStampBrushStroke(canvas As SKCanvas, strokes As IEnumerable(Of BrushStroke), width As Integer, height As Integer, color As SKColor, strokeWidth As Single, blurSigma As Single, flow As Single, key As String)
            Dim minX = Single.MaxValue, minY = Single.MaxValue
            Dim maxX = Single.MinValue, maxY = Single.MinValue
            Dim any = False
            For Each brushStroke In strokes
                If brushStroke Is Nothing OrElse brushStroke.Points.Count < 2 Then Continue For
                For Each p In brushStroke.Points
                    minX = Math.Min(minX, p.X) : minY = Math.Min(minY, p.Y)
                    maxX = Math.Max(maxX, p.X) : maxY = Math.Max(maxY, p.Y)
                    any = True
                Next
            Next
            If Not any Then Return

            Dim isSmear = key = "smear"
            ' Wie weit ein Stempel seitlich über die Spur hinausreicht - bestimmt den Rand der Ebene.
            Dim spread As Single
            Select Case key
                Case "smear" : spread = strokeWidth * 1.3F
                Case "airbrush" : spread = strokeWidth * 1.1F
                Case "calligraphy" : spread = strokeWidth * 0.8F
                Case "stipple" : spread = strokeWidth * 0.8F
                Case "watercolor" : spread = strokeWidth * 1.2F
                Case Else : spread = strokeWidth * 2.2F   ' spatter streut am weitesten
            End Select
            Dim pad = spread + blurSigma * 3.0F + 2.0F
            Dim left = CInt(Math.Floor(Clamp(minX - pad, 0, width)))
            Dim top = CInt(Math.Floor(Clamp(minY - pad, 0, height)))
            Dim right = CInt(Math.Ceiling(Clamp(maxX + pad, 0, width)))
            Dim bottom = CInt(Math.Ceiling(Clamp(maxY + pad, 0, height)))
            Dim w = right - left, h = bottom - top
            If w <= 0 OrElse h <= 0 Then Return

            Dim baseAlpha As Single = Clamp(color.Alpha * flow, 0, 255) / 255.0F

            Using layer = New SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using lc = New SKCanvas(layer)
                    lc.Clear(SKColors.Transparent)
                    lc.Translate(-left, -top)

                    Dim strokeOrdinal = 0
                    For Each brushStroke In strokes
                        If brushStroke Is Nothing OrElse brushStroke.Points.Count < 2 Then Continue For
                        Dim pts As New List(Of SKPoint)(brushStroke.Points.Count)
                        For Each p In brushStroke.Points
                            pts.Add(New SKPoint(Clamp(p.X, 0, width), Clamp(p.Y, 0, height)))
                        Next
                        Dim seedBase = 4242 + strokeOrdinal * 131
                        Select Case key
                            Case "smear" : DrawSmearStroke(lc, pts, color, strokeWidth, baseAlpha, blurSigma, seedBase)
                            Case "airbrush" : DrawAirbrushStroke(lc, pts, color, strokeWidth, baseAlpha, seedBase)
                            Case "calligraphy" : DrawCalligraphyStroke(lc, pts, color, strokeWidth, baseAlpha, blurSigma)
                            Case "stipple" : DrawStippleStroke(lc, pts, color, strokeWidth, baseAlpha, seedBase)
                            Case "watercolor" : DrawWatercolorStroke(lc, pts, color, strokeWidth, baseAlpha, seedBase)
                            Case Else : DrawSpatterStroke(lc, pts, color, strokeWidth, baseAlpha, blurSigma, seedBase)
                        End Select
                        strokeOrdinal += 1
                    Next
                End Using

                canvas.DrawBitmap(layer, left, top)
            End Using
        End Sub

        ''' <summary>Schmieren als Trockenpinsel/Borsten: ein weicher, blasser Grundkörper entlang des
        ''' Zuges plus viele dünne "Borsten"-Linien, die parallel zur Kurve laufen (je Punkt eigene
        ''' Normale, damit sie Kurven folgen). Zufällige Lücken, Deckkräfte und Anfangs-/End-Beschnitte
        ''' erzeugen die typischen Striationen und die auslaufenden Ränder. Deterministisch über seedBase.</summary>
        Private Shared Sub DrawSmearStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, blurSigma As Single, seedBase As Integer, Optional density As Single = 1.0F, Optional emboss As Boolean = False)
            Dim n = pts.Count
            If n < 2 Then Return

            ' Per-Punkt-Normale (senkrecht zur lokalen Richtung) für die seitlichen Borsten-Versätze.
            Dim normals(n - 1) As SKPoint
            For i As Integer = 0 To n - 1
                Dim aIdx = Math.Max(0, i - 1), bIdx = Math.Min(n - 1, i + 1)
                Dim tx = pts(bIdx).X - pts(aIdx).X, ty = pts(bIdx).Y - pts(aIdx).Y
                Dim tlen = CSng(Math.Sqrt(tx * tx + ty * ty))
                If tlen < 0.001F Then tlen = 1.0F
                normals(i) = New SKPoint(-ty / tlen, tx / tlen)
            Next

            ' Weicher, blasser Grundkörper - gibt dem Schmierer Substanz unter den Striationen.
            Using body = New SKPaint With {.IsAntialias = True, .Style = SKPaintStyle.Stroke,
                                           .StrokeCap = SKStrokeCap.Round, .StrokeJoin = SKStrokeJoin.Round,
                                           .StrokeWidth = strokeWidth * 0.9F,
                                           .Color = color.WithAlpha(CByte(Clamp(baseAlpha * 0.32F * density * 255.0F, 0, 255)))}
                body.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1.0F, strokeWidth * 0.2F + blurSigma))
                Using bodyPath = New SKPath()
                    bodyPath.MoveTo(pts(0))
                    For i As Integer = 1 To n - 1 : bodyPath.LineTo(pts(i)) : Next
                    lc.DrawPath(bodyPath, body)
                End Using
            End Using

            ' 3D-Impasto: jede Borste bekommt eine helle und eine dunkle Kante (fixe Lichtrichtung über
            ' die Normale), damit sie wie ein aufgetragener Farbwulst wirkt.
            Dim embossOff = Math.Max(1.0F, strokeWidth * 0.03F)

            Dim bristles = Math.Max(6, CInt(strokeWidth / 1.4F))
            ' PERF ("CPU hängt ~2 min nach großem Klecks-Strich"): früher trug
            ' jede Borste (×3 beim Impasto) einen eigenen MaskFilter-Blur - Skia rastert und blurt
            ' dafür JE ZEICHNUNG eine Maske in Strichregion-Größe; bei ~160 Borsten in Vollauflösung
            ' waren das Hunderte Blur-Durchläufe über eine Riesenregion (Minuten im Einback-Commit).
            ' Jetzt: alle Borsten SCHARF in EINEN SaveLayer, der beim Restore einmalig mit derselben
            ' Sigma geblurt wird - gleicher Weichzeichner, ein Durchlauf. Minimale Abweichung nur an
            ' Borsten-Überlappungen (Blur nach dem Mischen statt je Borste); Vorschau, Brücke und
            ' Einbacken nutzen dieselbe Routine und bleiben deshalb untereinander identisch.
            Dim bristleSigma = Math.Max(0.5F, strokeWidth * 0.03F + blurSigma)
            Using layerPaint = New SKPaint With {.ImageFilter = SKImageFilter.CreateBlur(bristleSigma, bristleSigma)}
                lc.SaveLayer(layerPaint)
                Using bristle = New SKPaint With {.IsAntialias = True, .Style = SKPaintStyle.Stroke,
                                                  .StrokeCap = SKStrokeCap.Round, .StrokeJoin = SKStrokeJoin.Round,
                                                  .StrokeWidth = Math.Max(1.0F, strokeWidth * 0.055F)}
                    For b As Integer = 0 To bristles - 1
                        If Hash01(b, 1, seedBase) < 0.1F Then Continue For ' Lücken = Streifen
                        Dim frac = (b / CSng(bristles - 1)) - 0.5F
                        Dim offset = frac * strokeWidth * 0.92F + (Hash01(b, 2, seedBase) - 0.5F) * strokeWidth * 0.08F
                        Dim a = Clamp(baseAlpha * (0.24F + Hash01(b, 3, seedBase) * 0.55F) * density, 0, 1)
                        ' Zufälliger Beschnitt vorne/hinten -> unterschiedlich lange, auslaufende Borsten.
                        Dim i0 = CInt(Math.Floor(Hash01(b, 4, seedBase) * 0.28F * (n - 1)))
                        Dim i1 = CInt(Math.Ceiling((0.55F + Hash01(b, 5, seedBase) * 0.45F) * (n - 1)))
                        i1 = Math.Min(n - 1, Math.Max(i0 + 1, i1))

                        If emboss Then
                            ' Schatten (dunkle Kante) auf der einen, Licht (helle Kante) auf der anderen Seite;
                            ' der Kern liegt zuletzt darüber, sodass nur schmale Kanten herausschauen.
                            bristle.Color = Shade(color, -0.45F).WithAlpha(CByte(Clamp(a * 0.9F * 255.0F, 0, 255)))
                            Using sp = BuildOffsetBristlePath(pts, normals, i0, i1, offset + embossOff) : lc.DrawPath(sp, bristle) : End Using
                            bristle.Color = Shade(color, 0.55F).WithAlpha(CByte(Clamp(a * 0.9F * 255.0F, 0, 255)))
                            Using hp = BuildOffsetBristlePath(pts, normals, i0, i1, offset - embossOff) : lc.DrawPath(hp, bristle) : End Using
                        End If

                        bristle.Color = color.WithAlpha(CByte(Clamp(a * 255.0F, 0, 255)))
                        Using path = BuildOffsetBristlePath(pts, normals, i0, i1, offset)
                            lc.DrawPath(path, bristle)
                        End Using
                    Next
                End Using
                lc.Restore()
            End Using
        End Sub

        ''' <summary>Baut den Teilpfad einer Borste: Punkte i0..i1, jeweils um <paramref name="offset"/>
        ''' entlang der Punkt-Normale seitlich versetzt (folgt so der Kurve).</summary>
        Private Shared Function BuildOffsetBristlePath(pts As List(Of SKPoint), normals As SKPoint(), i0 As Integer, i1 As Integer, offset As Single) As SKPath
            Dim path = New SKPath()
            For i As Integer = i0 To i1
                Dim px = pts(i).X + normals(i).X * offset
                Dim py = pts(i).Y + normals(i).Y * offset
                If i = i0 Then path.MoveTo(px, py) Else path.LineTo(px, py)
            Next
            Return path
        End Function

        ''' <summary>Hellt (factor &gt; 0, Richtung Weiß) oder dunkelt (factor &lt; 0, Richtung Schwarz)
        ''' eine Farbe ab; Alpha bleibt unberührt.</summary>
        Private Shared Function Shade(c As SKColor, factor As Single) As SKColor
            If factor >= 0 Then
                Dim f = Clamp(factor, 0, 1)
                Return New SKColor(CByte(c.Red + (255 - c.Red) * f), CByte(c.Green + (255 - c.Green) * f), CByte(c.Blue + (255 - c.Blue) * f), c.Alpha)
            Else
                Dim f = 1.0F + Clamp(factor, -1, 0)
                Return New SKColor(CByte(c.Red * f), CByte(c.Green * f), CByte(c.Blue * f), c.Alpha)
            End If
        End Function

        ''' <summary>Farbkleckse: ein satter, durchgehender Kern-Strich plus zufällig um die Linie
        ''' gestreute Tropfen (r^3-verteilt: viele kleine, wenige große) - der "zu viel Farbe"-Effekt.
        ''' Deterministisch über seedBase.</summary>
        Private Shared Sub DrawSpatterStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, blurSigma As Single, seedBase As Integer)
            Dim n = pts.Count
            If n < 2 Then Return

            Using path = New SKPath()
                path.MoveTo(pts(0))
                For i As Integer = 1 To n - 1 : path.LineTo(pts(i)) : Next

                ' Grundlinie: die streifige Schmier-Struktur mit 3D-Impasto - satt (dichte Borsten) mit
                ' sichtbaren Striationen und heller/dunkler Kante statt eines glatten Rohrs.
                DrawSmearStroke(lc, pts, color, strokeWidth, baseAlpha, blurSigma, seedBase, 1.2F, True)

                ' Gestreute Tropfen entlang des Pfades.
                Using drops = New SKPaint With {.IsAntialias = True, .Style = SKPaintStyle.Fill}
                    Using pm = New SKPathMeasure(path, False)
                        Dim length = pm.Length
                        Dim spacing = Math.Max(1.0F, strokeWidth * 0.5F)
                        Dim d As Single = 0.0F
                        Dim stampIndex = 0
                        Do
                            Dim pos As SKPoint, tan As SKPoint
                            If pm.GetPositionAndTangent(d, pos, tan) Then
                                Dim perpX = -tan.Y, perpY = tan.X
                                For j As Integer = 0 To 3
                                    If Hash01(stampIndex, 10 + j, seedBase) < 0.55F Then
                                        Dim rr = Hash01(stampIndex, 20 + j, seedBase)
                                        Dim dropR = strokeWidth * (0.03F + rr * rr * rr * 0.33F)
                                        ' Näher an der Spur: seitlicher Versatz ~1x statt 3x Strichbreite.
                                        Dim offN = (Hash01(stampIndex, 30 + j, seedBase) - 0.5F) * strokeWidth * 1.5F
                                        Dim offT = (Hash01(stampIndex, 40 + j, seedBase) - 0.5F) * strokeWidth * 1.2F
                                        Dim a = baseAlpha * (0.55F + Hash01(stampIndex, 50 + j, seedBase) * 0.45F)
                                        drops.Color = color.WithAlpha(CByte(Clamp(a * 255.0F, 0, 255)))
                                        lc.DrawCircle(pos.X + perpX * offN + tan.X * offT, pos.Y + perpY * offN + tan.Y * offT, dropR, drops)
                                    End If
                                Next
                            End If
                            stampIndex += 1
                            If d >= length Then Exit Do
                            d = Math.Min(length, d + spacing)
                        Loop
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Sprühdose: dichte Wolke feiner Punkte um die Spur, mit radial abnehmender
        ''' Wahrscheinlichkeit und Deckkraft. Anders als "spatter" (wenige große Tropfen weit gestreut)
        ''' baut sich hier durch viele winzige Punkte ein weicher Farbauftrag auf, der bei mehrfachem
        ''' Überfahren dichter wird. Alle Zufallswerte deterministisch aus Stempelindex + seedBase.</summary>
        Private Shared Sub DrawAirbrushStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, seedBase As Integer)
            If pts.Count < 2 Then Return
            Using path = New SKPath()
                path.MoveTo(pts(0))
                For i As Integer = 1 To pts.Count - 1 : path.LineTo(pts(i)) : Next

                Dim radius = Math.Max(1.0F, strokeWidth * 0.5F)
                Dim dotR = Math.Max(0.4F, strokeWidth * 0.045F)
                Using dots = New SKPaint With {.IsAntialias = True, .Style = SKPaintStyle.Fill}
                    Using pm = New SKPathMeasure(path, False)
                        Dim length = pm.Length
                        ' Eng abtasten: die Punktwolke soll durchgehend wirken, nicht perlenartig.
                        Dim spacing = Math.Max(0.6F, strokeWidth * 0.12F)
                        Dim perStep = Math.Max(3, CInt(Math.Min(26, strokeWidth * 0.55F)))
                        Dim d As Single = 0.0F
                        Dim stampIndex = 0
                        Do
                            Dim pos As SKPoint, tan As SKPoint
                            If pm.GetPositionAndTangent(d, pos, tan) Then
                                For j As Integer = 0 To perStep - 1
                                    ' Sqrt der Zufallszahl = flächengleiche Verteilung in der Kreisscheibe;
                                    ' ohne das drängen sich die Punkte in der Mitte.
                                    Dim rr = CSng(Math.Sqrt(Hash01(stampIndex, 60 + j, seedBase)))
                                    Dim ang = Hash01(stampIndex, 90 + j, seedBase) * 6.2831853F
                                    Dim rad = rr * radius
                                    Dim a = baseAlpha * (1.0F - rr * 0.85F) * 0.85F
                                    If a <= 0.004F Then Continue For
                                    dots.Color = color.WithAlpha(CByte(Clamp(a * 255.0F, 0, 255)))
                                    lc.DrawCircle(pos.X + CSng(Math.Cos(ang)) * rad,
                                                  pos.Y + CSng(Math.Sin(ang)) * rad, dotR, dots)
                                Next
                            End If
                            stampIndex += 1
                            If d >= length Then Exit Do
                            d = Math.Min(length, d + spacing)
                        Loop
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Kalligrafie-Feder: eine flache Feder mit FESTEM Anstellwinkel wird entlang des Pfades
        ''' gestempelt. Der sichtbare Strich wird dadurch dort breit, wo die Bewegung quer zur Feder läuft,
        ''' und dünn, wo sie ihr folgt - genau das macht den Schwung einer Breitfeder aus. Deshalb hier
        ''' KEIN Zufall: die Federkante ist ein fester Winkel, keine Textur.</summary>
        Private Shared Sub DrawCalligraphyStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, blurSigma As Single)
            If pts.Count < 2 Then Return
            Const nibAngleDeg As Single = 40.0F
            Dim nibRad = nibAngleDeg * 0.0174532925F
            Dim nx = CSng(Math.Cos(nibRad)), ny = CSng(Math.Sin(nibRad))
            Dim half = Math.Max(0.5F, strokeWidth * 0.5F)
            ' Dicke der Feder quer zur Kante - eine echte Breitfeder ist schmal, nicht rund.
            Dim nibThickness = Math.Max(1.0F, strokeWidth * 0.16F)

            Using path = New SKPath()
                path.MoveTo(pts(0))
                For i As Integer = 1 To pts.Count - 1 : path.LineTo(pts(i)) : Next

                Using paint = New SKPaint With {
                    .Color = color.WithAlpha(CByte(Clamp(baseAlpha * 255.0F, 0, 255))),
                    .IsAntialias = True,
                    .Style = SKPaintStyle.Stroke,
                    .StrokeWidth = nibThickness,
                    .StrokeCap = SKStrokeCap.Round
                }
                    If blurSigma > 0.05F Then paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSigma * 0.35F)
                    Using pm = New SKPathMeasure(path, False)
                        Dim length = pm.Length
                        ' Sehr eng stempeln, sonst zerfällt der Zug in einzelne Federabdrücke.
                        Dim spacing = Math.Max(0.35F, strokeWidth * 0.07F)
                        Dim d As Single = 0.0F
                        Do
                            Dim pos As SKPoint, tan As SKPoint
                            If pm.GetPositionAndTangent(d, pos, tan) Then
                                lc.DrawLine(pos.X - nx * half, pos.Y - ny * half,
                                            pos.X + nx * half, pos.Y + ny * half, paint)
                            End If
                            If d >= length Then Exit Do
                            d = Math.Min(length, d + spacing)
                        Loop
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Punktraster: gleichmäßig verteilte Tupfen entlang der Spur mit leichtem Versatz und
        ''' wechselnder Größe. Ergibt eine gepunktete Linie (Stippling/Pointillismus) statt eines
        ''' geschlossenen Zuges - der Abstand skaliert mit der Strichbreite.</summary>
        Private Shared Sub DrawStippleStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, seedBase As Integer)
            If pts.Count < 2 Then Return
            Using path = New SKPath()
                path.MoveTo(pts(0))
                For i As Integer = 1 To pts.Count - 1 : path.LineTo(pts(i)) : Next

                Using dot = New SKPaint With {.IsAntialias = True, .Style = SKPaintStyle.Fill}
                    Using pm = New SKPathMeasure(path, False)
                        Dim length = pm.Length
                        Dim spacing = Math.Max(2.0F, strokeWidth * 0.85F)
                        Dim d As Single = 0.0F
                        Dim stampIndex = 0
                        Do
                            Dim pos As SKPoint, tan As SKPoint
                            If pm.GetPositionAndTangent(d, pos, tan) Then
                                Dim perpX = -tan.Y, perpY = tan.X
                                Dim jitter = (Hash01(stampIndex, 11, seedBase) - 0.5F) * strokeWidth * 0.35F
                                Dim rr = 0.22F + Hash01(stampIndex, 22, seedBase) * 0.16F
                                Dim a = baseAlpha * (0.75F + Hash01(stampIndex, 33, seedBase) * 0.25F)
                                dot.Color = color.WithAlpha(CByte(Clamp(a * 255.0F, 0, 255)))
                                lc.DrawCircle(pos.X + perpX * jitter, pos.Y + perpY * jitter, strokeWidth * rr, dot)
                            End If
                            stampIndex += 1
                            If d >= length Then Exit Do
                            d = Math.Min(length, d + spacing)
                        Loop
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Aquarell: mehrere lasierende Durchgänge unterschiedlicher Breite mit weichen Rändern,
        ''' dazu eine dunklere, unruhige Randlinie. Der Reiz von Aquarell liegt im ÜBEREINANDER - jede
        ''' Lage ist fast durchsichtig, erst die Summe ergibt die Farbe, und die Ränder laufen aus.</summary>
        Private Shared Sub DrawWatercolorStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, seedBase As Integer)
            If pts.Count < 2 Then Return
            Using path = New SKPath()
                path.MoveTo(pts(0))
                For i As Integer = 1 To pts.Count - 1 : path.LineTo(pts(i)) : Next

                ' Von breit/blass nach schmal/kräftiger - der Kern wird dadurch von selbst satter.
                Dim widths = New Single() {1.15F, 0.92F, 0.66F, 0.42F}
                Dim alphas = New Single() {0.20F, 0.24F, 0.28F, 0.34F}
                For layerIndex As Integer = 0 To widths.Length - 1
                    Dim w = Math.Max(1.0F, strokeWidth * widths(layerIndex))
                    Dim a = baseAlpha * alphas(layerIndex)
                    ' Winziger Versatz je Lage: die Lagen decken sich nicht exakt, so entstehen die
                    ' typischen Farbränder, wo sich zwei Lasuren überlappen.
                    Dim offX = (Hash01(layerIndex, 7, seedBase) - 0.5F) * strokeWidth * 0.12F
                    Dim offY = (Hash01(layerIndex, 8, seedBase) - 0.5F) * strokeWidth * 0.12F
                    Using paint = New SKPaint With {
                        .Color = color.WithAlpha(CByte(Clamp(a * 255.0F, 0, 255))),
                        .IsAntialias = True,
                        .Style = SKPaintStyle.Stroke,
                        .StrokeWidth = w,
                        .StrokeCap = SKStrokeCap.Round,
                        .StrokeJoin = SKStrokeJoin.Round,
                        .MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, w * 0.22F)
                    }
                        lc.Save()
                        lc.Translate(offX, offY)
                        lc.DrawPath(path, paint)
                        lc.Restore()
                    End Using
                Next

                ' Randlinie: bei echtem Aquarell sammelt sich Pigment am austrocknenden Rand.
                Using edge = New SKPaint With {
                    .Color = color.WithAlpha(CByte(Clamp(baseAlpha * 0.30F * 255.0F, 0, 255))),
                    .IsAntialias = True,
                    .Style = SKPaintStyle.Stroke,
                    .StrokeWidth = Math.Max(1.0F, strokeWidth * 0.10F),
                    .StrokeCap = SKStrokeCap.Round,
                    .MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, strokeWidth * 0.06F)
                }
                    Using outline = New SKPath()
                        Using measure = New SKPathMeasure(path, False)
                            Dim length = measure.Length
                            Dim stepLen As Single = Math.Max(1.0F, strokeWidth * 0.3F)
                            Dim d As Single = 0.0F
                            Dim first = True
                            Dim idx = 0
                            Do
                                Dim pos As SKPoint, tan As SKPoint
                                If measure.GetPositionAndTangent(d, pos, tan) Then
                                    Dim perpX = -tan.Y, perpY = tan.X
                                    Dim wob = 0.42F + Hash01(idx, 5, seedBase) * 0.12F
                                    Dim ex = pos.X + perpX * strokeWidth * wob
                                    Dim ey = pos.Y + perpY * strokeWidth * wob
                                    If first Then outline.MoveTo(ex, ey) : first = False Else outline.LineTo(ex, ey)
                                End If
                                idx += 1
                                If d >= length Then Exit Do
                                d = Math.Min(length, d + stepLen)
                            Loop
                        End Using
                        lc.DrawPath(outline, edge)
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Rendert einen Beispielstrich einer Pinsel-Variante als kleine Vorschau (z. B. für den
        ''' Pinsel-Picker). Nutzt dieselbe Zeichenroutine wie das echte Malen, damit die Vorschau exakt
        ''' dem Ergebnis entspricht. Rückgabe muss vom Aufrufer disposed werden.</summary>
        Public Shared Function RenderBrushStrokePreview(preset As String, widthPx As Integer, heightPx As Integer, color As SKColor) As SKBitmap
            Dim w = Math.Max(8, widthPx)
            Dim h = Math.Max(8, heightPx)
            Dim bmp = New SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)
            Using canvas = New SKCanvas(bmp)
                canvas.Clear(SKColors.Transparent)
                ' Ein leicht geschwungener Strich quer über die Kachel, wie in Pinsel-Bibliotheken üblich.
                Dim midY = h / 2.0F
                Dim amp = h * 0.16F
                Dim pts As New List(Of StrokePoint)()
                Dim x0 = w * 0.06F, x1 = w * 0.94F
                Dim steps = 48
                For i As Integer = 0 To steps
                    Dim t = i / CSng(steps)
                    Dim x = x0 + (x1 - x0) * t
                    Dim y = midY + CSng(Math.Sin(t * Math.PI * 1.6 - 0.4)) * amp
                    pts.Add(New StrokePoint(x, y))
                Next
                Dim strokeWidth = Math.Max(2.0F, h * 0.34F)
                Dim strokes = New List(Of BrushStroke) From {New BrushStroke(pts)}
                DrawBrushStroke(canvas, strokes, w, h, color, strokeWidth, 100.0F, 100.0F, preset, False)
            End Using
            Return bmp
        End Function

        Private Shared Sub DrawSingleGlyph(canvas As SKCanvas, glyph As String, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, fontFamily As String)
            Dim text = glyph.Trim()
            If text.Length = 0 Then text = "★"
            Dim fontSize = Math.Max(12.0F, Math.Min(rect.Width, rect.Height) * 0.82F)
            Using font = CreateFont(fontFamily, fontSize)
                Using paint = New SKPaint With {.IsAntialias = True}
                    Dim bounds As SKRect
                    font.MeasureText(text, bounds, paint)
                    Dim x = rect.MidX - bounds.MidX
                    Dim y = rect.MidY - bounds.MidY
                    If strokeWidth > 0 Then
                        paint.Style = SKPaintStyle.Stroke
                        paint.StrokeWidth = strokeWidth
                        paint.Color = stroke
                        canvas.DrawText(text, x, y, font, paint)
                    End If
                    paint.Style = SKPaintStyle.Fill
                    paint.Color = fill
                    canvas.DrawText(text, x, y, font, paint)
                End Using
            End Using
        End Sub

        Private Shared Sub DrawQrCode(canvas As SKCanvas, text As String, rect As SKRect, dark As SKColor, light As SKColor)
            Using generator = New QRCodeGenerator()
                Using data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q)
                    Dim modules = data.ModuleMatrix
                    Dim count = modules.Count
                    If count <= 0 Then Return
                    Dim size = Math.Min(rect.Width, rect.Height)
                    Dim left = rect.Left + (rect.Width - size) / 2.0F
                    Dim top = rect.Top + (rect.Height - size) / 2.0F
                    Dim cell = size / count
                    Using bg = New SKPaint With {.Color = If(light.Alpha = 0, SKColors.White, light), .Style = SKPaintStyle.Fill, .IsAntialias = False}
                        canvas.DrawRect(left, top, size, size, bg)
                    End Using
                    Using fg = New SKPaint With {.Color = dark, .Style = SKPaintStyle.Fill, .IsAntialias = False}
                        For row As Integer = 0 To count - 1
                            For col As Integer = 0 To modules(row).Count - 1
                                If modules(row)(col) Then
                                    ' Math.Ceiling liefert Double, DrawRect nimmt Single.
                                    Dim edge = CSng(Math.Ceiling(cell))
                                    canvas.DrawRect(left + col * cell, top + row * cell, edge, edge, fg)
                                End If
                            Next
                        Next
                    End Using
                End Using
            End Using
        End Sub

        Private Shared Function WithAlpha(color As SKColor, alpha As Byte) As SKColor
            Return New SKColor(color.Red, color.Green, color.Blue, alpha)
        End Function

        ''' <summary>Textbreite EINSCHLIESSLICH Zeichenabstand. Muss ueberall dort benutzt werden,
        ''' wo bisher font.MeasureText stand - sonst passt die Einpassung auf den Pfad nicht mehr
        ''' zum tatsaechlich gezeichneten Text.</summary>
        Private Shared Function MeasureTextSpaced(font As SKFont, text As String, spacing As Single) As Single
            If String.IsNullOrEmpty(text) Then Return 0.0F
            Dim w = font.MeasureText(text)
            If spacing <> 0.0F AndAlso text.Length > 1 Then w += spacing * (text.Length - 1)
            Return w
        End Function

        ''' <summary>Zeichnet eine Zeile mit Zeichenabstand. Bei spacing = 0 exakt der bisherige
        ''' Weg (ein DrawText fuer die ganze Zeile, mit Kerning) - der Normalfall bleibt also
        ''' unveraendert. Erst ein gesetzter Abstand setzt die Zeichen einzeln.</summary>
        Private Shared Sub DrawTextSpaced(canvas As SKCanvas, text As String, x As Single, baseline As Single,
                                          font As SKFont, paint As SKPaint, spacing As Single)
            If spacing = 0.0F Then
                canvas.DrawText(text, x, baseline, font, paint)
                Return
            End If
            Dim cx = x
            For Each ch In text
                Dim einzeln = ch.ToString()
                canvas.DrawText(einzeln, cx, baseline, font, paint)
                cx += font.MeasureText(einzeln) + spacing
            Next
        End Sub

        ''' <summary>Text auf einem Pfad mit Zeichenabstand. Bei spacing = 0 bleibt es bei Skias
        ''' DrawTextOnPath; sonst werden die Zeichen einzeln gesetzt und zur Tangente gedreht -
        ''' dasselbe Verhalten wie warpGlyphs:=False, nur mit eigenem Vorschub.
        ''' Ohne diesen Zweig waere der Zeichenabstand bei gesetztem Pfad wirkungslos gewesen.</summary>
        Private Shared Sub DrawTextOnPathSpaced(canvas As SKCanvas, text As String, path As SKPath,
                                                font As SKFont, paint As SKPaint, spacing As Single)
            If spacing = 0.0F Then
                canvas.DrawTextOnPath(text, path, New SKPoint(0, 0), warpGlyphs:=False, font, paint)
                Return
            End If
            Using measure = New SKPathMeasure(path, False)
                Dim laenge = measure.Length
                Dim d As Single = 0.0F
                For Each ch In text
                    Dim einzeln = ch.ToString()
                    Dim width = font.MeasureText(einzeln)
                    Dim center = d + width / 2.0F
                    If center > laenge Then Exit For
                    Dim pos As SKPoint, tangente As SKPoint
                    If measure.GetPositionAndTangent(center, pos, tangente) Then
                        Dim angle = CSng(Math.Atan2(tangente.Y, tangente.X) * 180.0 / Math.PI)
                        Dim state = canvas.Save()
                        canvas.Translate(pos.X, pos.Y)
                        canvas.RotateDegrees(angle)
                        canvas.DrawText(einzeln, -width / 2.0F, 0, font, paint)
                        canvas.RestoreToCount(state)
                    End If
                    d += width + spacing
                Next
            End Using
        End Sub

        ''' <summary>Zeichnet Textzeilen eines Text-Objekts. KEIN automatischer Umbruch an der
        ''' Boxbreite mehr: die Auswahlbox wird aus den EXPLIZITEN Zeilen
        ''' gemessen (EstimateTextAnnotationSizePercent) - ein Breiten-Umbruch hier zeichnete dann
        ''' hoeher als die Box, und beim Verschieben blieben Fragmente des alten Stands stehen
        ''' (Geist ausserhalb des uebergebenen Rechtecks). Neue Zeilen entstehen ausschliesslich
        ''' ueber echte Zeilenumbrueche im Text (Strg+Enter in den Eingabefeldern).
        ''' <paramref name="maxWidth"/> bleibt in der Signatur, weil die Aufrufer sie fuer Formen
        ''' weiterreichen - fuer Text ist sie bewusst ohne Wirkung.</summary>
        Private Shared Sub DrawWrappedText(canvas As SKCanvas, text As String, x As Single, y As Single, maxWidth As Single, fontSize As Single, font As SKFont, paint As SKPaint, Optional spacing As Single = 0)
            If String.IsNullOrEmpty(text) Then Return
            Dim lineHeight = GetLineHeight(font.Metrics)
            Dim baseline = y + fontSize

            For Each line In text.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(ControlChars.Lf)
                If line.Length > 0 Then
                    DrawTextSpaced(canvas, line, x, baseline, font, paint, spacing)
                End If
                baseline += lineHeight
            Next
        End Sub

        Private Shared Function ParseColor(value As String, fallback As SKColor) As SKColor
            If String.IsNullOrWhiteSpace(value) Then Return fallback
            Dim text = value.Trim().TrimStart("#"c)
            Try
                Dim raw As UInteger
                If UInteger.TryParse(text, Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture, raw) Then
                    If text.Length <= 6 Then
                        Return New SKColor(CByte((raw >> 16) And &HFFUI), CByte((raw >> 8) And &HFFUI), CByte(raw And &HFFUI), 255)
                    End If
                    Return New SKColor(CByte((raw >> 16) And &HFFUI), CByte((raw >> 8) And &HFFUI), CByte(raw And &HFFUI), CByte((raw >> 24) And &HFFUI))
                End If
            Catch
            End Try
            Return fallback
        End Function

        ''' <summary>Unschaerfemaske mit Kreuz-Kern (5 Taps):
        '''      0   -a    0
        '''     -a  1+4a  -a
        '''      0   -a    0
        '''
        ''' Lief bis ueber SKImageFilter.CreateMatrixConvolution. Skias CPU-Faltung ist
        ''' dafuer pathologisch langsam: gemessen 8,6 s bei 6,3 MP - fuer fuenf Multiplikationen je
        ''' Pixel. Zum Vergleich braucht die gesamte verschmolzene Farbkette 17 ms.
        '''
        ''' Randbehandlung wie zuvor SKShaderTileMode.Clamp: ausserhalb liegende Nachbarn werden auf
        ''' den Rand geklemmt. Alpha bleibt unveraendert (entsprach convolveAlpha:=False).</summary>
        ''' <summary>Schärfen. Ohne Radius/Detail/Maskierung (alle 0) die bisherige feste 3×3-Maske,
        ''' bitgenau unverändert; sonst eine echte Unschärfemaske mit variablem Radius, Detailanhebung
        ''' und Kantenmaskierung.</summary>
        Private Shared Function ApplySharpness(source As SKBitmap, amount As Single, radiusAmount As Single,
                                               detailAmount As Single, maskingAmount As Single) As SKBitmap
            ' Die Maskierung MUSS die 3x3-Abkuerzung mit abwaehlen: dort gibt es keine Kantenmaske, ein
            ' allein gezogener Maskierungsregler waere sonst ein stummer No-Op.
            If radiusAmount <= 0 AndAlso detailAmount <= 0 AndAlso maskingAmount <= 0 Then Return ApplySharpness3x3(source, amount)
            Return ApplyUnsharpMask(source, amount, radiusAmount, detailAmount, maskingAmount)
        End Function

        ''' <summary>Kantenstärke, ab der die Maskierung voll durchlässt - gemessen als Summe der
        ''' Helligkeitsunterschiede zu den vier Nachbarn (0..255 je Richtung). 48 liegt deutlich über dem
        ''' Rauschen eines normal belichteten Fotos und deutlich unter einer echten Motivkante.</summary>
        Private Const SharpenEdgeFullScale As Single = 48.0F

        ''' <summary>Helligkeit eines Pixels für die Kantenmessung. Über ReadUnpremultiplied, weil die
        ''' Puffer premultipliziert vorliegen können - roh gelesen wäre in halbtransparenten Bereichen die
        ''' Alpha-Kante als Motivkante durchgegangen.</summary>
        Private Shared Function LumaAt(buf As Byte(), offset As Integer, ri As Integer, gi As Integer,
                                       bi As Integer, ai As Integer) As Single
            Dim r As Integer, g As Integer, b As Integer, a As Integer
            ReadUnpremultiplied(buf, offset, ri, gi, bi, ai, r, g, b, a)
            Return 0.299F * r + 0.587F * g + 0.114F * b
        End Function

        ''' <summary>Unschärfemaske: Bild − Gaußunschärfe ergibt die Hochfrequenzanteile, die verstärkt
        ''' aufaddiert werden. Radius steuert das Gauß-Sigma (Wirkgröße), Detail die Verstärkung.
        '''
        ''' MASKIERUNG (Adobes „Maskieren", crs:SharpenEdgeMasking): die Verstärkung wird je Pixel mit der
        ''' lokalen Kantenstärke gewichtet. 0 = überall voll (wie bisher), 100 = nur noch an Kanten. Die
        ''' Formel ist eine NÄHERUNG an Adobe (deren Maske steckt im geschlossenen Camera-Raw-Kern):
        ''' gewicht = (1 − m) + m · kante, mit kante = smoothstep der Gradientensumme. Sie hat die
        ''' richtigen Endpunkte (m=0 → überall 1, m=100 → glatte Fläche 0, Kante 1) und läuft dazwischen
        ''' stetig, statt eine harte Schwelle zu ziehen - eine harte Schwelle setzt bei Rauschen sichtbare
        ''' Flecken, weil benachbarte Pixel auf verschiedene Seiten fallen.</summary>
        Private Shared Function ApplyUnsharpMask(source As SKBitmap, amount As Single, radiusAmount As Single,
                                                 detailAmount As Single, maskingAmount As Single) As SKBitmap
            Dim sigma = 0.8F + Clamp(radiusAmount, 0, 1) * 2.7F
            Dim gain = amount * (1.0F + Clamp(detailAmount, 0, 1) * 1.5F)
            Dim masking = Clamp(maskingAmount, 0, 1)

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim blurred = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(blurred)
                Using filter = SKImageFilter.CreateBlur(sigma, sigma)
                    Using paint = New SKPaint With {.ImageFilter = filter}
                        canvas.DrawBitmap(source, 0, 0, paint)
                    End Using
                End Using
            End Using

            Dim srcBuf As Byte() = Nothing, blurBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            Dim bStride, bri, bgi, bbi, bai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) OrElse
               Not TryBorrowRgbaLikeBuffer(blurred, blurBuf, bStride, bri, bgi, bbi, bai) Then
                blurred.Dispose()
                Return result
            End If
            Dim dstBuf = New Byte(srcBuf.Length - 1) {}
            Dim w = source.Width, h = source.Height

            ForEachRow(w, h,
                Sub(y)
                    Dim rowOffset = y * stride
                    Dim bRow = y * bStride
                    ' Zeilen ober-/unterhalb für den senkrechten Gradienten; am Bildrand auf die eigene
                    ' Zeile geklemmt (wie die restliche Randbehandlung dieser Datei).
                    Dim upRow = If(y > 0, (y - 1) * stride, rowOffset)
                    Dim downRow = If(y < h - 1, (y + 1) * stride, rowOffset)
                    For x = 0 To w - 1
                        Dim o = rowOffset + x * 4
                        Dim bo = bRow + x * 4
                        Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                        ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                        Dim lr As Integer, lg As Integer, lb As Integer, la As Integer
                        ReadUnpremultiplied(blurBuf, bo, bri, bgi, bbi, bai, lr, lg, lb, la)

                        Dim pixelGain = gain
                        If masking > 0 Then
                            Dim leftOff = rowOffset + If(x > 0, (x - 1) * 4, 0)
                            Dim rightOff = rowOffset + If(x < w - 1, (x + 1) * 4, (w - 1) * 4)
                            Dim edge = Clamp((Math.Abs(LumaAt(srcBuf, leftOff, ri, gi, bi, ai) - LumaAt(srcBuf, rightOff, ri, gi, bi, ai)) +
                                              Math.Abs(LumaAt(srcBuf, upRow + x * 4, ri, gi, bi, ai) - LumaAt(srcBuf, downRow + x * 4, ri, gi, bi, ai))) /
                                             SharpenEdgeFullScale, 0, 1)
                            ' Smoothstep: an beiden Enden Ableitung null, der Übergang von "glatt" nach
                            ' "Kante" zieht deshalb keine sichtbare Linie ins Bild.
                            edge = edge * edge * (3.0F - 2.0F * edge)
                            pixelGain = gain * ((1.0F - masking) + masking * edge)
                        End If

                        WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                            ClampToByte(cr + pixelGain * (cr - lr)),
                            ClampToByte(cg + pixelGain * (cg - lg)),
                            ClampToByte(cb + pixelGain * (cb - lb)), a)
                    Next
                End Sub)

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            blurred.Dispose()
            Return result
        End Function

        Private Shared Function ApplySharpness3x3(source As SKBitmap, amount As Single) As SKBitmap
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) Then Return result

            Dim dstBuf = New Byte(srcBuf.Length - 1) {}
            Dim w = source.Width
            Dim h = source.Height
            Dim center = 1.0F + 4.0F * amount

            ForEachRow(w, h,
                Sub(y)
                    Dim top = If(y > 0, (y - 1) * stride, 0)
                    Dim centered = y * stride
                    Dim bottom = If(y < h - 1, (y + 1) * stride, (h - 1) * stride)
                    For x = 0 To w - 1
                        Dim o = centered + x * 4
                        Dim left = centered + If(x > 0, (x - 1) * 4, 0)
                        Dim right = centered + If(x < w - 1, (x + 1) * 4, (w - 1) * 4)
                        Dim ob = top + x * 4
                        Dim un = bottom + x * 4

                        Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                        ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                        Dim lr As Integer, lg As Integer, lb As Integer, la As Integer
                        Dim rr2 As Integer, rg As Integer, rb As Integer, ra As Integer
                        Dim tr As Integer, tg As Integer, tb As Integer, ta As Integer
                        Dim br As Integer, bg As Integer, bb As Integer, ba As Integer
                        ReadUnpremultiplied(srcBuf, left, ri, gi, bi, ai, lr, lg, lb, la)
                        ReadUnpremultiplied(srcBuf, right, ri, gi, bi, ai, rr2, rg, rb, ra)
                        ReadUnpremultiplied(srcBuf, ob, ri, gi, bi, ai, tr, tg, tb, ta)
                        ReadUnpremultiplied(srcBuf, un, ri, gi, bi, ai, br, bg, bb, ba)

                        WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                            ClampToByte(cr * center - amount * (lr + rr2 + tr + br)),
                            ClampToByte(cg * center - amount * (lg + rg + tg + bg)),
                            ClampToByte(cb * center - amount * (lb + rb + tb + bb)), a)
                    Next
                End Sub)

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            Return result
        End Function

        Private Shared Function ApplyGeometryTransforms(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If adj.RotationDegrees = 0 AndAlso Not adj.FlipHorizontal AndAlso Not adj.FlipVertical Then
                Return source
            End If

            Dim normalized = ((adj.RotationDegrees Mod 360) + 360) Mod 360
            Dim sw = source.Width
            Dim sh = source.Height
            Dim rw = If(normalized = 90 OrElse normalized = 270, sh, sw)
            Dim rh = If(normalized = 90 OrElse normalized = 270, sw, sh)

            ' Farbtyp/Alpha der Quelle DURCHREICHEN: das war die einzige Stufe der
            ' Geometriekette ohne (Crop, Straighten, Resize, Canvas tun es alle) - nach Drehung/Flip
            ' lief die Pipeline im Plattform-Default-Format. Auf Plattformen, deren N32 nicht
            ' Bgra8888 ist, fielen danach alle Bgra-only-Pfade still um (Auswahl-Composite,
            ' Filmnegativ-Messung, Vignette-Pufferpfad).
            Dim rotated = New SKBitmap(rw, rh, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(rotated)
                canvas.Clear(SKColors.Transparent)
                Select Case normalized
                    Case 90
                        canvas.Translate(rw, 0)
                        canvas.RotateDegrees(90)
                    Case 180
                        canvas.Translate(rw, rh)
                        canvas.RotateDegrees(180)
                    Case 270
                        canvas.Translate(0, rh)
                        canvas.RotateDegrees(270)
                End Select
                canvas.DrawBitmap(source, 0, 0)
            End Using

            If Not adj.FlipHorizontal AndAlso Not adj.FlipVertical Then Return rotated

            Dim w = rotated.Width
            Dim h = rotated.Height
            Dim result = New SKBitmap(w, h, rotated.ColorType, rotated.AlphaType)
            Using canvas = New SKCanvas(result)
                Dim matrix = SKMatrix.Identity
                If adj.FlipHorizontal Then
                    matrix = matrix.PostConcat(SKMatrix.CreateScale(-1, 1, w / 2.0F, h / 2.0F))
                End If
                If adj.FlipVertical Then
                    matrix = matrix.PostConcat(SKMatrix.CreateScale(1, -1, w / 2.0F, h / 2.0F))
                End If
                canvas.SetMatrix(matrix)
                canvas.DrawBitmap(rotated, 0, 0)
            End Using
            rotated.Dispose()
            Return result
        End Function

        ''' <summary>Perspektivische Verzerrung. Die Bildmasse bleiben gleich - was aus dem Rahmen
        ''' kippt, wird abgeschnitten, und wo nichts mehr liegt, bleibt es durchsichtig. Der Regler
        ''' "Groesse" ist dafuer da, die leeren Ecken wieder zuzudecken.
        '''
        ''' Warum die Masse gleich bleiben: die Stufe laeuft im Bild- UND im Maskenweg. Wuerde sie
        ''' die Groesse aendern, muessten beide Wege exakt dieselbe neue Groesse errechnen, sonst
        ''' passt die Maske nicht mehr aufs Bild. Gleiche Masse machen das zur Selbstverstaendlichkeit
        ''' statt zu einer Bedingung, an die jemand denken muss.</summary>
        Private Shared Function ApplyPerspective(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing OrElse adj Is Nothing Then Return source
            Dim m = ImageGeometryMapper.WarpMatrix(source.Width, source.Height,
                                                          adj.PerspectiveHorizontal, adj.PerspectiveVertical,
                                                          adj.PerspectiveAspect, adj.PerspectiveScale,
                                                          ImageGeometryMapper.CornerOffset(adj))
            ' Unbenutzt heisst unbenutzt: kein Umkopieren, keine Neuabtastung.
            If m.IsIdentity Then Return source

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                canvas.SetMatrix(m)
                Using paint = New SKPaint With {.IsAntialias = True}
                    DrawBitmapSampled(canvas, source, 0, 0, SamplingHigh, paint)
                End Using
            End Using
            Return result
        End Function

        Private Shared Function ApplyStraighten(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim degrees = adj.StraightenDegrees
            If Math.Abs(degrees) < 0.01F Then Return source

            Dim radians = Math.Abs(degrees) * Math.PI / 180.0

            If adj.StraightenExpandCanvas Then
                Dim expandedWidth = Math.Max(1, CInt(Math.Ceiling(source.Width * Math.Cos(radians) + source.Height * Math.Sin(radians))))
                Dim expandedHeight = Math.Max(1, CInt(Math.Ceiling(source.Width * Math.Sin(radians) + source.Height * Math.Cos(radians))))

                Dim expanded = New SKBitmap(expandedWidth, expandedHeight, source.ColorType, source.AlphaType)
                Using canvas = New SKCanvas(expanded)
                    ' Siehe ApplyCanvasResize: gefuellt wird einmal am Ende der Kette.
                    canvas.Clear(SKColors.Transparent)
                    canvas.Translate(expandedWidth / 2.0F, expandedHeight / 2.0F)
                    canvas.RotateDegrees(degrees)
                    Using paint = New SKPaint With {.IsAntialias = True}
                        DrawBitmapSampled(canvas, source, -source.Width / 2.0F, -source.Height / 2.0F, SamplingHigh, paint)
                    End Using
                End Using
                Return expanded
            End If

            Dim scale = Math.Max(
                source.Width / (source.Width * Math.Cos(radians) + source.Height * Math.Sin(radians)),
                source.Height / (source.Width * Math.Sin(radians) + source.Height * Math.Cos(radians)))
            scale = Math.Max(1.0, scale)

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                canvas.Translate(source.Width / 2.0F, source.Height / 2.0F)
                canvas.Scale(CSng(scale))
                canvas.RotateDegrees(degrees)
                Using paint = New SKPaint With {.IsAntialias = True}
                    DrawBitmapSampled(canvas, source, -source.Width / 2.0F, -source.Height / 2.0F, SamplingHigh, paint)
                End Using
            End Using
            Return result
        End Function

        ''' <summary>True, wenn SaveImage das Ziel-FORMAT dieser Endung tatsächlich erzeugen kann
        ''' (JPEG/PNG/WEBP/PDF). Alles andere lehnt SaveImage ab, statt still JPEG-Bytes unter
        ''' fremder Endung zu schreiben - Aufrufer nutzen die Funktion, um
        ''' in-place-Speichern/Umschreiben vorab auszuschließen und auf "Speichern unter" zu lenken.</summary>
        Public Shared Function CanEncodeToTargetExtension(path As String) As Boolean
            Select Case IO.Path.GetExtension(If(path, "")).ToLowerInvariant()
                Case ".jpg", ".jpeg", ".png", ".webp", ".pdf"
                    Return True
                Case ".fpx"
                    ' Kein Bildformat, sondern ein Projektbündel - geschrieben wird es unten über
                    ' FpxService, nicht über Skia. Ist das Format abgeschaltet, bleibt es verboten.
                    Return FpxService.Enabled
                Case Else
                    Return False
            End Select
        End Function

        ''' <paramref name="workingFull"/>: voll aufgelöstes ARBEITSBILD des Editors (Umbau Stufe C) -
        ''' wenn gesetzt, ersetzt es den Datei-Decode als Pipeline-Eingang (Besitz wechselt hierher,
        ''' wird disposed). Aufrufer übergeben einen Klon (WorkingImageService.CloneFull).
        Public Shared Function SaveImage(sourcePath As String, targetPath As String, adj As ImageAdjustments, quality As Integer,
                                         Optional preserveMetadata As Boolean = True,
                                         Optional workingFull As SKBitmap = Nothing,
                                         Optional developRaw As Boolean = True) As Boolean
            ' Zentraler Schutz: Bearbeitung einer RAW-Quelle wirkt nur auf deren eingebettete
            ' JPEG-Vorschau (siehe OpenSourceStream/DecodeOriented) - ein Speichern-in-place würde
            ' hier fälschlich die RAW-Rohdaten JPEG-kodiert über die Original-RAW-Datei schreiben.
            ' PSD/PSB sind NUR-LESEND (die Pipeline sieht nur das zusammengesetzte Gesamtbild,
            ' Ebenen gingen beim Überschreiben verloren) - gleiches Verbot.
            '
            ' Verboten ist nicht nur der GLEICHE Pfad, sondern jedes Ziel mit RAW-/PSD-Endung:
            ' die Viewer-Drehung schrieb in eine Temp-Datei MIT der Endung des Originals
            ' (".foto.ferrumpix-rotate-1234.cr2") und kopierte sie erst danach über die Quelle -
            ' der Pfadvergleich sah zwei verschiedene Dateien, und die Formatwahl unten machte aus
            ' ".cr2" mangels eigenem Zweig ein JPEG. Ergebnis: die Original-RAW war unwiederbringlich
            ' durch ihre eigene eingebettete Vorschau ersetzt. Ein Ziel
            ' mit RAW-/PSD-Endung ist IMMER falsch - wir können diese Formate nicht schreiben.
            If RawPreviewService.IsSupportedRaw(targetPath) OrElse PsdPreviewService.IsSupportedPsd(targetPath) OrElse
               HeifDecodeService.IsSupportedHeif(targetPath) OrElse TiffPreviewService.IsSupportedTiff(targetPath) Then
                workingFull?.Dispose()
                Return False
            End If
            If (RawPreviewService.IsSupportedRaw(sourcePath) OrElse PsdPreviewService.IsSupportedPsd(sourcePath) OrElse
                HeifDecodeService.IsSupportedHeif(sourcePath) OrElse TiffPreviewService.IsSupportedTiff(sourcePath)) AndAlso
               PathIdentity.AreSame(sourcePath, targetPath) Then
                workingFull?.Dispose()
                Return False
            End If
            ' Dieselbe Fehlerklasse eine Formatstufe weiter: die Formatwahl unten
            ' kennt nur PNG/WEBP/PDF und faellt sonst auf JPEG zurueck. Ein Ziel ".tiff"/".bmp"/
            ' ".gif"/".heic"/... bekam damit still JPEG-Bytes unter fremder Endung - beim
            ' in-place-Speichern wurde das Original verlustbehaftet konvertiert UND falsch
            ' etikettiert. Verboten wird am Ziel-FORMAT, nicht am Pfad (siehe RAW-Lehre oben).
            If Not CanEncodeToTargetExtension(targetPath) Then
                DiagnosticLogService.LogAlways("ImageProcessor.Save",
                    $"refused: target extension not encodable target={IO.Path.GetFileName(targetPath)}")
                workingFull?.Dispose()
                Return False
            End If
            Try
                ' .fpx-Projekte beim echten Speichern/Konvertieren immer aus Basisbild + Rezept rendern.
                ' composite.png ist nur ein schnelles Anzeige-/Thumbnail-Bild und kann bewusst verkleinert sein.
                Dim isFpxSource = FpxService.IsFpx(sourcePath)
                Using original = If(workingFull, If(isFpxSource, RenderFpxFullResolution(sourcePath), DecodeOriented(sourcePath, developRaw)))
                    If original Is Nothing Then Return False

                    Dim ext = IO.Path.GetExtension(targetPath).ToLowerInvariant()
                    Dim isPdf = ext = ".pdf"
                    ' Ziel .fpx: kein Encode, sondern ein Projektbündel aus Originaldatei + Rezept +
                    ' gerendertem Vorschaubild. Ein Stapel-Export bleibt damit weiterbearbeitbar,
                    ' statt nur ein fertiges Bild zu liefern. Eine .fpx als QUELLE bleibt außen vor -
                    ' das Bündel würde sich sonst selbst als Basisbild eintragen.
                    Dim isFpxTarget = ext = ".fpx" AndAlso FpxService.Enabled AndAlso Not isFpxSource
                    If ext = ".fpx" AndAlso Not isFpxTarget Then
                        DiagnosticLogService.LogAlways("ImageProcessor.Save",
                            $"refused: .fpx-Ziel nicht moeglich target={IO.Path.GetFileName(targetPath)}")
                        Return False
                    End If
                    Dim fileFormat = If(ext = ".png", SKEncodedImageFormat.Png,
                                 If(ext = ".webp", SKEncodedImageFormat.Webp,
                                    SKEncodedImageFormat.Jpeg))

                    Using processed = ProcessBitmap(original, adj)
                        If isFpxTarget Then
                            ' composite.png ist nur das Anzeigebild des Bündels und bewusst gedeckelt -
                            ' die volle Auflösung entsteht beim Öffnen wieder aus Basisbild + Rezept.
                            Using composite = EncodePngStream(processed, FpxCompositeMaxDimension)
                                If composite Is Nothing Then Return False
                                FpxService.Save(targetPath, adj, sourcePath, composite)
                            End Using
                            Return IO.File.Exists(targetPath)
                        End If

                        ' JPEG und PDF kennen kein Alpha: transparente Bereiche (Radierer-Löcher,
                        ' ausgeblendeter Hintergrund) liefen beim Encode auf SCHWARZ
                        '. Auf WEISS flatten - wie Photoshop.
                        Dim toEncode = processed
                        If isPdf OrElse fileFormat = SKEncodedImageFormat.Jpeg Then
                            toEncode = FlattenAlphaToWhite(processed)
                        End If
                        Try
                            If isPdf Then
                                ' Druckfertiges einseitiges PDF mit dem zuletzt im Druckdialog
                                ' gewählten Seitenlayout - so sehen Drucken und PDF-Export gleich aus.
                                If Not PrintService.WriteSinglePagePdf(toEncode, targetPath,
                                                                      AppSettingsService.Load().ToPrintOptions()) Then Return False
                            Else
                                Using image = SKImage.FromBitmap(toEncode)
                                    Using data = image.Encode(fileFormat, quality)
                                        ' Atomar: erst daneben schreiben, dann darüberbewegen - ein
                                        ' abgebrochener Encode darf das Original nicht zerstören.
                                        WriteFileAtomic(targetPath, Sub(fs) data.SaveTo(fs))
                                    End Using
                                End Using
                            End If
                        Finally
                            If Not Object.ReferenceEquals(toEncode, processed) Then toEncode.Dispose()
                        End Try
                    End Using
                    ' Metadaten nur von echten Bildquellen kopieren (ein .fpx-Bündel trägt keine).
                    ' In ein PDF lässt sich kein EXIF-Block kopieren - der Versuch würde die Datei
                    ' beschädigen.
                    If preserveMetadata AndAlso Not isFpxSource AndAlso Not isPdf AndAlso Not isFpxTarget Then TryCopyMetadata(sourcePath, targetPath)
                    Return True
                End Using
            Catch ex As Exception
                Return False
            End Try
        End Function

        ''' <summary>Weißer Untergrund für Formate ohne Alphakanal (JPEG). Liefert das Original
        ''' zurück, wenn nichts zu tun ist; sonst ein NEUES Bitmap (Aufrufer disposed es).</summary>
        Private Shared Function FlattenAlphaToWhite(source As SKBitmap) As SKBitmap
            If source Is Nothing Then Return source
            Dim flattened = New SKBitmap(source.Width, source.Height, source.ColorType, SKAlphaType.Premul)
            Using canvas = New SKCanvas(flattened)
                canvas.Clear(SKColors.White)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return flattened
        End Function

        ''' <summary>Rendert ein .fpx-Bündel in voller Basisauflösung. Der Aufrufer übernimmt das SKBitmap.</summary>
        Private Shared Function RenderFpxFullResolution(fpxPath As String) As SKBitmap
            Dim loaded = FpxService.Load(fpxPath)
            If loaded Is Nothing OrElse String.IsNullOrWhiteSpace(loaded.BaseImagePath) OrElse Not File.Exists(loaded.BaseImagePath) Then Return Nothing
            Try
                ' Gebackenes Arbeitsbild (voll aufgelöstes retouch.png) als Pipeline-Eingang -
                ' Pinsel-/Radiererstriche stehen seit Stufe D NUR noch dort, nicht mehr im Rezept.
                ' Ein Vorschauauflösungs-Altbestand (Seed) wird ignoriert (Maße-Check).
                Dim inputPath = loaded.BaseImagePath
                If Not String.IsNullOrWhiteSpace(loaded.RetouchStagePath) AndAlso File.Exists(loaded.RetouchStagePath) Then
                    Dim baseSize = GetOrientedImageSize(loaded.BaseImagePath)
                    Dim stageSize = GetOrientedImageSize(loaded.RetouchStagePath)
                    If baseSize.Width > 0 AndAlso stageSize.Width = baseSize.Width AndAlso stageSize.Height = baseSize.Height Then
                        inputPath = loaded.RetouchStagePath
                    End If
                End If
                Using baseBitmap = DecodeOriented(inputPath)
                    If baseBitmap Is Nothing Then Return Nothing
                    Return ProcessBitmap(baseBitmap, If(loaded.Adjustments, New ImageAdjustments()))
                End Using
            Finally
                If Not String.IsNullOrWhiteSpace(loaded.TempDir) Then
                    Try : Directory.Delete(loaded.TempDir, True) : Catch : End Try
                End If
            End Try
        End Function

        Public Shared Function RenderFpxFullResolutionBitmap(fpxPath As String) As Bitmap
            Using rendered = RenderFpxFullResolution(fpxPath)
                If rendered Is Nothing Then Return Nothing
                Return ToAvaloniaBitmap(rendered)
            End Using
        End Function

        Private Shared Sub TryCopyMetadata(sourcePath As String, targetPath As String)
            If String.IsNullOrWhiteSpace(sourcePath) OrElse String.IsNullOrWhiteSpace(targetPath) Then Return
            If Not File.Exists(sourcePath) OrElse Not File.Exists(targetPath) Then Return

            Try
                Dim targetExt = IO.Path.GetExtension(targetPath).ToLowerInvariant()

                Select Case targetExt
                    Case ".jpg", ".jpeg"
                        CopyJpegMetadata(sourcePath, targetPath)
                    Case ".png"
                        CopyPngMetadata(sourcePath, targetPath)
                    Case ".webp"
                        CopyWebpMetadata(sourcePath, targetPath)
                End Select
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' Schreibt eine Datei so, dass die alte Fassung bis zum letzten Moment unangetastet bleibt:
        ''' erst in eine Nachbardatei, dann darüberbewegen.
        '''
        ''' `File.Open(..., FileMode.Create)` kürzt das Ziel SOFORT auf 0 Byte. Bricht das Encodieren
        ''' danach ab - voller Datenträger, Encoder-Ausnahme, abgezogenes Netzlaufwerk, Absturz -,
        ''' ist das Original weg und an seiner Stelle liegt ein Torso. Beim Speichern ÜBER das
        ''' Original (nicht "Speichern unter") ist das ein Datenverlust ohne Rückweg.
        ''' `FpxService` macht es seit jeher so; hier gilt derselbe Weg für alle Bild-Schreibpfade.
        '''
        ''' Die Nachbardatei liegt bewusst im ZIELVERZEICHNIS - `File.Move` ist nur innerhalb
        ''' desselben Dateisystems ein Umhängen und damit unteilbar; über `Path.GetTempPath` wäre
        ''' es wieder ein Kopieren mit denselben Abbruchstellen.
        ''' </summary>
        Friend Shared Function WriteFileAtomic(targetPath As String, writer As Action(Of Stream)) As Boolean
            If String.IsNullOrWhiteSpace(targetPath) OrElse writer Is Nothing Then Return False
            Dim verzeichnis = IO.Path.GetDirectoryName(targetPath)
            If Not String.IsNullOrEmpty(verzeichnis) Then Directory.CreateDirectory(verzeichnis)
            ' Prozess-ID im Namen: zwei gleichzeitig laufende FerrumPix-Instanzen (oder der
            ' Prüfstand daneben) dürfen sich nicht dieselbe Zwischendatei teilen.
            Dim tempPath = targetPath & ".fpwrite" & Environment.ProcessId.ToString() & ".tmp"
            Try
                Using fs = File.Open(tempPath, FileMode.Create, FileAccess.Write)
                    writer(fs)
                    fs.Flush()
                End Using
                File.Move(tempPath, targetPath, overwrite:=True)
                Return True
            Catch
                Try
                    If File.Exists(tempPath) Then File.Delete(tempPath)
                Catch
                End Try
                Throw
            End Try
        End Function

        ''' <summary>Byte-Fassung von <see cref="WriteFileAtomic"/> - für die Metadaten-Nachläufe,
        ''' die die fertige Datei einlesen, umbauen und zurückschreiben.</summary>
        Friend Shared Function WriteAllBytesAtomic(targetPath As String, bytes As Byte()) As Boolean
            If bytes Is Nothing Then Return False
            Return WriteFileAtomic(targetPath, Sub(fs) fs.Write(bytes, 0, bytes.Length))
        End Function

        Private Shared Function IsJpegPath(path As String) As Boolean
            Dim ext = IO.Path.GetExtension(path).ToLowerInvariant()
            Return ext = ".jpg" OrElse ext = ".jpeg"
        End Function

        Private Shared Sub CopyJpegMetadata(sourcePath As String, targetPath As String)
            Dim metadataSegments = If(IsJpegPath(sourcePath),
                                      ReadJpegMetadataSegments(sourcePath),
                                      BuildJpegMetadataSegmentsFromSource(sourcePath))
            If metadataSegments.Count = 0 Then Return

            Dim targetBytes = File.ReadAllBytes(targetPath)
            If targetBytes.Length < 4 OrElse targetBytes(0) <> &HFF OrElse targetBytes(1) <> &HD8 Then Return

            Dim stripped = StripJpegMetadataSegments(targetBytes)
            Dim insertAt = FindJpegMetadataInsertOffset(stripped)
            Dim output As New List(Of Byte)(stripped.Length + metadataSegments.Sum(Function(s) s.Length))
            output.AddRange(stripped.Take(insertAt))
            For Each segment In metadataSegments
                output.AddRange(segment)
            Next
            output.AddRange(stripped.Skip(insertAt))
            WriteAllBytesAtomic(targetPath, output.ToArray())
        End Sub

        Private Shared Function ReadJpegMetadataSegments(path As String) As List(Of Byte())
            Dim bytes = File.ReadAllBytes(path)
            Dim result As New List(Of Byte())()
            If bytes.Length < 4 OrElse bytes(0) <> &HFF OrElse bytes(1) <> &HD8 Then Return result

            Dim offset = 2
            While offset + 4 <= bytes.Length
                If bytes(offset) <> &HFF Then Exit While
                Dim marker = bytes(offset + 1)
                If marker = &HDA OrElse marker = &HD9 Then Exit While
                If marker = &H1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) Then
                    offset += 2
                    Continue While
                End If

                Dim length = ReadUInt16BE(bytes, offset + 2)
                If length < 2 OrElse offset + 2 + length > bytes.Length Then Exit While
                Dim totalLength = 2 + length

                If IsJpegMetadataMarker(marker) Then
                    Dim segment(totalLength - 1) As Byte
                    Buffer.BlockCopy(bytes, offset, segment, 0, totalLength)
                    If marker = &HE1 AndAlso IsExifSegment(segment) Then PatchExifOrientationToNormal(segment)
                    result.Add(segment)
                End If

                offset += totalLength
            End While

            Return result
        End Function

        Private Shared Function BuildJpegMetadataSegmentsFromSource(sourcePath As String) As List(Of Byte())
            Dim result As New List(Of Byte())()
            Dim exif = ExtractExifTiffBytes(sourcePath)
            If exif IsNot Nothing AndAlso exif.Length > 0 Then
                result.Add(CreateJpegAppSegment(&HE1, CombineBytes(Text.Encoding.ASCII.GetBytes("Exif" & ChrW(0) & ChrW(0)), exif)))
            End If

            Dim xmp = ExtractXmpBytes(sourcePath)
            If xmp IsNot Nothing AndAlso xmp.Length > 0 Then
                result.Add(CreateJpegAppSegment(&HE1, CombineBytes(Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/" & ChrW(0)), xmp)))
            End If

            Return result.Where(Function(s) s IsNot Nothing).ToList()
        End Function

        Private Shared Function CreateJpegAppSegment(marker As Byte, payload As Byte()) As Byte()
            If payload Is Nothing OrElse payload.Length + 2 > UShort.MaxValue Then Return Nothing
            Dim segment(payload.Length + 3) As Byte
            segment(0) = &HFF
            segment(1) = marker
            Dim length = payload.Length + 2
            segment(2) = CByte((length >> 8) And &HFF)
            segment(3) = CByte(length And &HFF)
            Buffer.BlockCopy(payload, 0, segment, 4, payload.Length)
            Return segment
        End Function

        Private Shared Function StripJpegMetadataSegments(bytes As Byte()) As Byte()
            Dim output As New List(Of Byte)(bytes.Length)
            output.Add(bytes(0))
            output.Add(bytes(1))

            Dim offset = 2
            While offset + 4 <= bytes.Length
                If bytes(offset) <> &HFF Then Exit While
                Dim marker = bytes(offset + 1)
                If marker = &HDA OrElse marker = &HD9 Then Exit While
                If marker = &H1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) Then
                    output.Add(bytes(offset))
                    output.Add(bytes(offset + 1))
                    offset += 2
                    Continue While
                End If

                Dim length = ReadUInt16BE(bytes, offset + 2)
                If length < 2 OrElse offset + 2 + length > bytes.Length Then Exit While
                Dim totalLength = 2 + length

                If Not IsJpegMetadataMarker(marker) Then
                    output.AddRange(bytes.Skip(offset).Take(totalLength))
                End If

                offset += totalLength
            End While

            output.AddRange(bytes.Skip(offset))
            Return output.ToArray()
        End Function

        Private Shared Function FindJpegMetadataInsertOffset(bytes As Byte()) As Integer
            Dim offset = 2
            While offset + 4 <= bytes.Length AndAlso bytes(offset) = &HFF
                Dim marker = bytes(offset + 1)
                If marker <> &HE0 AndAlso marker <> &HEE Then Exit While
                Dim length = ReadUInt16BE(bytes, offset + 2)
                If length < 2 OrElse offset + 2 + length > bytes.Length Then Exit While
                offset += 2 + length
            End While
            Return offset
        End Function

        Private Shared Function IsJpegMetadataMarker(marker As Byte) As Boolean
            Return marker = &HE1 OrElse marker = &HED OrElse marker = &HE2
        End Function

        Private Shared Function IsExifSegment(segment As Byte()) As Boolean
            Return segment.Length >= 12 AndAlso
                   segment(4) = AscW("E"c) AndAlso segment(5) = AscW("x"c) AndAlso
                   segment(6) = AscW("i"c) AndAlso segment(7) = AscW("f"c) AndAlso
                   segment(8) = 0 AndAlso segment(9) = 0
        End Function

        Private Shared Sub PatchExifOrientationToNormal(segment As Byte())
            Try
                Dim tiff = 10
                If segment.Length < tiff + 8 Then Return
                Dim littleEndian = segment(tiff) = AscW("I"c) AndAlso segment(tiff + 1) = AscW("I"c)
                Dim bigEndian = segment(tiff) = AscW("M"c) AndAlso segment(tiff + 1) = AscW("M"c)
                If Not littleEndian AndAlso Not bigEndian Then Return

                Dim ifd0Offset = ReadUInt32Endian(segment, tiff + 4, littleEndian)
                Dim ifd0 = tiff + CInt(ifd0Offset)
                If ifd0 < 0 OrElse ifd0 + 2 > segment.Length Then Return

                Dim count = ReadUInt16Endian(segment, ifd0, littleEndian)
                Dim entryOffset = ifd0 + 2
                For i = 0 To count - 1
                    Dim entry = entryOffset + i * 12
                    If entry + 12 > segment.Length Then Return
                    Dim tag = ReadUInt16Endian(segment, entry, littleEndian)
                    If tag <> &H112 Then Continue For

                    Dim type = ReadUInt16Endian(segment, entry + 2, littleEndian)
                    Dim itemCount = ReadUInt32Endian(segment, entry + 4, littleEndian)
                    If type <> 3 OrElse itemCount < 1 Then Return

                    WriteUInt16Endian(segment, entry + 8, 1, littleEndian)
                    Return
                Next
            Catch
            End Try
        End Sub

        Private Shared Sub CopyPngMetadata(sourcePath As String, targetPath As String)
            Dim metadataChunks = If(IO.Path.GetExtension(sourcePath).ToLowerInvariant() = ".png",
                                    ReadPngMetadataChunks(sourcePath),
                                    BuildPngMetadataChunksFromSource(sourcePath))
            If metadataChunks.Count = 0 Then Return

            Dim targetBytes = File.ReadAllBytes(targetPath)
            If Not IsPngBytes(targetBytes) Then Return

            Dim output As New List(Of Byte)(targetBytes.Length + metadataChunks.Sum(Function(c) c.Length))
            output.AddRange(targetBytes.Take(8))

            Dim inserted = False
            Dim offset = 8
            While offset + 12 <= targetBytes.Length
                Dim length = ReadInt32BE(targetBytes, offset)
                Dim chunkEnd = offset + 12 + length
                If length < 0 OrElse chunkEnd > targetBytes.Length Then Return
                Dim chunkType = Text.Encoding.ASCII.GetString(targetBytes, offset + 4, 4)

                If Not inserted AndAlso chunkType = "IDAT" Then
                    For Each chunk In metadataChunks
                        output.AddRange(chunk)
                    Next
                    inserted = True
                End If

                If Not IsPngMetadataChunk(chunkType) Then
                    output.AddRange(targetBytes.Skip(offset).Take(12 + length))
                End If

                offset = chunkEnd
            End While

            WriteAllBytesAtomic(targetPath, output.ToArray())
        End Sub

        Private Shared Function ReadPngMetadataChunks(path As String) As List(Of Byte())
            Dim bytes = File.ReadAllBytes(path)
            Dim result As New List(Of Byte())()
            If Not IsPngBytes(bytes) Then Return result

            Dim offset = 8
            While offset + 12 <= bytes.Length
                Dim length = ReadInt32BE(bytes, offset)
                Dim chunkEnd = offset + 12 + length
                If length < 0 OrElse chunkEnd > bytes.Length Then Exit While
                Dim chunkType = Text.Encoding.ASCII.GetString(bytes, offset + 4, 4)

                If IsPngMetadataChunk(chunkType) Then
                    Dim chunk(12 + length - 1) As Byte
                    Buffer.BlockCopy(bytes, offset, chunk, 0, chunk.Length)
                    result.Add(chunk)
                End If

                offset = chunkEnd
            End While

            Return result
        End Function

        Private Shared Function BuildPngMetadataChunksFromSource(sourcePath As String) As List(Of Byte())
            Dim result As New List(Of Byte())()
            Dim exif = ExtractExifTiffBytes(sourcePath)
            If exif IsNot Nothing AndAlso exif.Length > 0 Then result.Add(CreatePngChunk("eXIf", exif))

            Dim xmp = ExtractXmpBytes(sourcePath)
            If xmp IsNot Nothing AndAlso xmp.Length > 0 Then
                Dim keyword = Text.Encoding.ASCII.GetBytes("XML:com.adobe.xmp")
                Dim payload As New List(Of Byte)()
                payload.AddRange(keyword)
                payload.Add(0)
                payload.Add(0)
                payload.Add(0)
                payload.Add(0)
                payload.Add(0)
                payload.AddRange(xmp)
                result.Add(CreatePngChunk("iTXt", payload.ToArray()))
            End If

            Return result
        End Function

        Private Shared Function IsPngBytes(bytes As Byte()) As Boolean
            Return bytes.Length >= 8 AndAlso bytes(0) = &H89 AndAlso bytes(1) = &H50 AndAlso bytes(2) = &H4E AndAlso bytes(3) = &H47 AndAlso
                   bytes(4) = &HD AndAlso bytes(5) = &HA AndAlso bytes(6) = &H1A AndAlso bytes(7) = &HA
        End Function

        Private Shared Function IsPngMetadataChunk(chunkType As String) As Boolean
            Select Case chunkType
                Case "eXIf", "iTXt", "tEXt", "zTXt", "iCCP"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        Private Shared Sub CopyWebpMetadata(sourcePath As String, targetPath As String)
            Dim sourceChunks = If(IO.Path.GetExtension(sourcePath).ToLowerInvariant() = ".webp",
                                  ReadWebpChunks(File.ReadAllBytes(sourcePath)).
                                      Where(Function(c) c.Type = "EXIF" OrElse c.Type = "XMP " OrElse c.Type = "ICCP").
                                      ToList(),
                                  BuildWebpMetadataChunksFromSource(sourcePath))
            If sourceChunks.Count = 0 Then Return

            Dim targetBytes = File.ReadAllBytes(targetPath)
            Dim targetChunks = ReadWebpChunks(targetBytes)
            If targetChunks.Count = 0 Then Return

            Dim vp8x = targetChunks.FirstOrDefault(Function(c) c.Type = "VP8X")
            Dim imageChunk = targetChunks.FirstOrDefault(Function(c) c.Type = "VP8 " OrElse c.Type = "VP8L")
            If vp8x Is Nothing AndAlso imageChunk Is Nothing Then Return

            Dim flags As Byte = 0
            Dim width As Integer = 0
            Dim height As Integer = 0
            If vp8x IsNot Nothing AndAlso vp8x.Data.Length >= 10 Then
                flags = vp8x.Data(0)
                width = 1 + CInt(vp8x.Data(4)) + (CInt(vp8x.Data(5)) << 8) + (CInt(vp8x.Data(6)) << 16)
                height = 1 + CInt(vp8x.Data(7)) + (CInt(vp8x.Data(8)) << 8) + (CInt(vp8x.Data(9)) << 16)
            ElseIf imageChunk IsNot Nothing Then
                Dim size = ReadWebpImageSize(imageChunk)
                width = size.Width
                height = size.Height
                If targetChunks.Any(Function(c) c.Type = "ALPH") Then flags = CByte(flags Or &H10)
            End If
            If width <= 0 OrElse height <= 0 Then Return

            If sourceChunks.Any(Function(c) c.Type = "ICCP") Then flags = CByte(flags Or &H20)
            If sourceChunks.Any(Function(c) c.Type = "EXIF") Then flags = CByte(flags Or &H8)
            If sourceChunks.Any(Function(c) c.Type = "XMP ") Then flags = CByte(flags Or &H4)

            Dim outputChunks As New List(Of WebpChunk)()
            outputChunks.Add(CreateWebpVp8xChunk(flags, width, height))

            For Each chunk In sourceChunks.Where(Function(c) c.Type = "ICCP")
                outputChunks.Add(chunk)
            Next
            For Each chunk In targetChunks
                If chunk.Type = "VP8X" OrElse chunk.Type = "EXIF" OrElse chunk.Type = "XMP " OrElse chunk.Type = "ICCP" Then Continue For
                outputChunks.Add(chunk)
            Next
            For Each chunk In sourceChunks.Where(Function(c) c.Type = "EXIF" OrElse c.Type = "XMP ")
                outputChunks.Add(chunk)
            Next

            WriteWebpChunks(targetPath, outputChunks)
        End Sub

        Private Class WebpChunk
            Public Property Type As String = ""
            Public Property Data As Byte() = Array.Empty(Of Byte)()
        End Class

        Private Shared Function ReadWebpChunks(bytes As Byte()) As List(Of WebpChunk)
            Dim result As New List(Of WebpChunk)()
            If bytes.Length < 12 OrElse Text.Encoding.ASCII.GetString(bytes, 0, 4) <> "RIFF" OrElse Text.Encoding.ASCII.GetString(bytes, 8, 4) <> "WEBP" Then Return result

            Dim offset = 12
            While offset + 8 <= bytes.Length
                Dim chunkType = Text.Encoding.ASCII.GetString(bytes, offset, 4)
                Dim length = ReadUInt32LE(bytes, offset + 4)
                If length > Integer.MaxValue OrElse offset + 8L + length > bytes.Length Then Exit While

                Dim data As Byte() = Array.Empty(Of Byte)()
                If length > 0 Then data = New Byte(CInt(length) - 1) {}
                If length > 0 Then Buffer.BlockCopy(bytes, offset + 8, data, 0, CInt(length))
                result.Add(New WebpChunk With {.Type = chunkType, .Data = data})

                offset += 8 + CInt(length)
                If (length Mod 2UI) = 1UI Then offset += 1
            End While

            Return result
        End Function

        Private Shared Function ReadWebpImageSize(chunk As WebpChunk) As (Width As Integer, Height As Integer)
            ' Der VP8-Keyframe-Header (verlustbehaftet) speichert die tatsächliche Breite/Höhe in je 14 Bit.
            ' VP8L (verlustfrei) speichert dagegen width-1/height-1 - deshalb nur dort das "1 +".
            ' Ein Off-by-one macht die VP8X-Canvas-Größe inkonsistent zum Bild und libwebp lehnt die Datei ab.
            If chunk.Type = "VP8 " AndAlso chunk.Data.Length >= 10 Then
                Return (Width:=CInt(chunk.Data(6)) Or ((CInt(chunk.Data(7)) And &H3F) << 8),
                        Height:=CInt(chunk.Data(8)) Or ((CInt(chunk.Data(9)) And &H3F) << 8))
            End If
            If chunk.Type = "VP8L" AndAlso chunk.Data.Length >= 5 Then
                Dim b1 = CInt(chunk.Data(1))
                Dim b2 = CInt(chunk.Data(2))
                Dim b3 = CInt(chunk.Data(3))
                Dim b4 = CInt(chunk.Data(4))
                Dim width = 1 + (((b2 And &H3F) << 8) Or b1)
                Dim height = 1 + (((b4 And &HF) << 10) Or (b3 << 2) Or ((b2 And &HC0) >> 6))
                Return (width, height)
            End If
            Return (0, 0)
        End Function

        Private Shared Function CreateWebpVp8xChunk(flags As Byte, width As Integer, height As Integer) As WebpChunk
            Dim data(9) As Byte
            data(0) = flags
            Dim storedWidth = Math.Max(0, width - 1)
            Dim storedHeight = Math.Max(0, height - 1)
            data(4) = CByte(storedWidth And &HFF)
            data(5) = CByte((storedWidth >> 8) And &HFF)
            data(6) = CByte((storedWidth >> 16) And &HFF)
            data(7) = CByte(storedHeight And &HFF)
            data(8) = CByte((storedHeight >> 8) And &HFF)
            data(9) = CByte((storedHeight >> 16) And &HFF)
            Return New WebpChunk With {.Type = "VP8X", .Data = data}
        End Function

        Private Shared Sub WriteWebpChunks(path As String, chunks As List(Of WebpChunk))
            Dim body As New List(Of Byte)()
            For Each chunk In chunks
                body.AddRange(Text.Encoding.ASCII.GetBytes(chunk.Type))
                body.AddRange(BitConverter.GetBytes(CUInt(chunk.Data.Length)))
                body.AddRange(chunk.Data)
                If (chunk.Data.Length Mod 2) = 1 Then body.Add(0)
            Next

            Dim bytes As New List(Of Byte)(12 + body.Count)
            bytes.AddRange(Text.Encoding.ASCII.GetBytes("RIFF"))
            bytes.AddRange(BitConverter.GetBytes(CUInt(4 + body.Count)))
            bytes.AddRange(Text.Encoding.ASCII.GetBytes("WEBP"))
            bytes.AddRange(body)
            File.WriteAllBytes(path, bytes.ToArray())
        End Sub

        Private Shared Function BuildWebpMetadataChunksFromSource(sourcePath As String) As List(Of WebpChunk)
            Dim result As New List(Of WebpChunk)()
            Dim exif = ExtractExifTiffBytes(sourcePath)
            If exif IsNot Nothing AndAlso exif.Length > 0 Then result.Add(New WebpChunk With {.Type = "EXIF", .Data = exif})

            Dim xmp = ExtractXmpBytes(sourcePath)
            If xmp IsNot Nothing AndAlso xmp.Length > 0 Then result.Add(New WebpChunk With {.Type = "XMP ", .Data = xmp})

            Return result
        End Function

        Private Shared Function ExtractExifTiffBytes(path As String) As Byte()
            Dim ext = IO.Path.GetExtension(path).ToLowerInvariant()
            Try
                Select Case ext
                    Case ".jpg", ".jpeg"
                        For Each segment In ReadJpegMetadataSegments(path)
                            If IsExifSegment(segment) Then
                                Dim tiffLength = segment.Length - 10
                                If tiffLength <= 0 Then Return Nothing
                                Dim tiff(tiffLength - 1) As Byte
                                Buffer.BlockCopy(segment, 10, tiff, 0, tiffLength)
                                Return tiff
                            End If
                        Next
                    Case ".png"
                        For Each chunk In ReadPngMetadataChunks(path)
                            Dim chunkType = Text.Encoding.ASCII.GetString(chunk, 4, 4)
                            If chunkType = "eXIf" Then
                                Dim length = ReadInt32BE(chunk, 0)
                                Dim data(length - 1) As Byte
                                Buffer.BlockCopy(chunk, 8, data, 0, length)
                                Return data
                            End If
                        Next
                    Case ".webp"
                        Dim chunk = ReadWebpChunks(File.ReadAllBytes(path)).FirstOrDefault(Function(c) c.Type = "EXIF")
                        If chunk IsNot Nothing Then Return chunk.Data
                End Select
            Catch
            End Try
            Return Nothing
        End Function

        Private Shared Function ExtractXmpBytes(path As String) As Byte()
            Dim ext = IO.Path.GetExtension(path).ToLowerInvariant()
            Try
                Select Case ext
                    Case ".jpg", ".jpeg"
                        For Each segment In ReadJpegMetadataSegments(path)
                            If segment.Length <= 33 OrElse segment(1) <> &HE1 OrElse IsExifSegment(segment) Then Continue For
                            Dim identifier = Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/" & ChrW(0))
                            If StartsWithBytes(segment, 4, identifier) Then
                                Dim xmpLength = segment.Length - 4 - identifier.Length
                                Dim xmp(xmpLength - 1) As Byte
                                Buffer.BlockCopy(segment, 4 + identifier.Length, xmp, 0, xmpLength)
                                Return xmp
                            End If
                        Next
                    Case ".png"
                        For Each chunk In ReadPngMetadataChunks(path)
                            Dim chunkType = Text.Encoding.ASCII.GetString(chunk, 4, 4)
                            Dim length = ReadInt32BE(chunk, 0)
                            If chunkType <> "iTXt" OrElse length <= 0 Then Continue For
                            Dim data(length - 1) As Byte
                            Buffer.BlockCopy(chunk, 8, data, 0, length)
                            Dim zero = Array.IndexOf(data, CByte(0))
                            If zero <= 0 Then Continue For
                            Dim keyword = Text.Encoding.ASCII.GetString(data, 0, zero)
                            If keyword <> "XML:com.adobe.xmp" OrElse zero + 5 >= data.Length Then Continue For
                            If data(zero + 1) <> 0 Then Continue For
                            Dim textOffset = zero + 5
                            Dim xmp(data.Length - textOffset - 1) As Byte
                            Buffer.BlockCopy(data, textOffset, xmp, 0, xmp.Length)
                            Return xmp
                        Next
                    Case ".webp"
                        Dim chunk = ReadWebpChunks(File.ReadAllBytes(path)).FirstOrDefault(Function(c) c.Type = "XMP ")
                        If chunk IsNot Nothing Then Return chunk.Data
                End Select
            Catch
            End Try
            Return Nothing
        End Function

        Private Shared Function CreatePngChunk(chunkType As String, data As Byte()) As Byte()
            Dim typeBytes = Text.Encoding.ASCII.GetBytes(chunkType)
            Dim chunk(12 + data.Length - 1) As Byte
            WriteInt32BE(chunk, 0, data.Length)
            Buffer.BlockCopy(typeBytes, 0, chunk, 4, 4)
            If data.Length > 0 Then Buffer.BlockCopy(data, 0, chunk, 8, data.Length)
            Dim crc = Crc32(chunk, 4, 4 + data.Length)
            WriteUInt32BE(chunk, 8 + data.Length, crc)
            Return chunk
        End Function

        Private Shared Function CombineBytes(first As Byte(), second As Byte()) As Byte()
            Dim result(first.Length + second.Length - 1) As Byte
            Buffer.BlockCopy(first, 0, result, 0, first.Length)
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length)
            Return result
        End Function

        Private Shared Function StartsWithBytes(bytes As Byte(), offset As Integer, prefix As Byte()) As Boolean
            If bytes.Length < offset + prefix.Length Then Return False
            For i = 0 To prefix.Length - 1
                If bytes(offset + i) <> prefix(i) Then Return False
            Next
            Return True
        End Function

        ' ACHTUNG: In VB.NET liefert "byteWert << n" wieder einen Byte und maskiert die Schiebeweite
        ' mit 7 (Byte << 8 ist also ein No-Op). Jeder Byte-Operand MUSS vor dem Shift nach Integer
        ' geweitet werden - sonst liest z.B. ReadUInt16BE(&H01, &H2E) 47 statt 302.
        Private Shared Function ReadUInt16BE(bytes As Byte(), offset As Integer) As Integer
            Return (CInt(bytes(offset)) << 8) Or CInt(bytes(offset + 1))
        End Function

        Private Shared Function ReadInt32BE(bytes As Byte(), offset As Integer) As Integer
            Return (CInt(bytes(offset)) << 24) Or (CInt(bytes(offset + 1)) << 16) Or (CInt(bytes(offset + 2)) << 8) Or CInt(bytes(offset + 3))
        End Function

        Private Shared Function ReadUInt16Endian(bytes As Byte(), offset As Integer, littleEndian As Boolean) As Integer
            If littleEndian Then Return CInt(bytes(offset)) Or (CInt(bytes(offset + 1)) << 8)
            Return ReadUInt16BE(bytes, offset)
        End Function

        Private Shared Function ReadUInt32Endian(bytes As Byte(), offset As Integer, littleEndian As Boolean) As UInteger
            If littleEndian Then Return ReadUInt32LE(bytes, offset)
            Return CUInt(CLng(bytes(offset)) << 24) Or CUInt(CLng(bytes(offset + 1)) << 16) Or CUInt(CInt(bytes(offset + 2)) << 8) Or CUInt(bytes(offset + 3))
        End Function

        Private Shared Function ReadUInt32LE(bytes As Byte(), offset As Integer) As UInteger
            Return CUInt(bytes(offset)) Or CUInt(CInt(bytes(offset + 1)) << 8) Or CUInt(CLng(bytes(offset + 2)) << 16) Or CUInt(CLng(bytes(offset + 3)) << 24)
        End Function

        Private Shared Sub WriteInt32BE(bytes As Byte(), offset As Integer, value As Integer)
            bytes(offset) = CByte((value >> 24) And &HFF)
            bytes(offset + 1) = CByte((value >> 16) And &HFF)
            bytes(offset + 2) = CByte((value >> 8) And &HFF)
            bytes(offset + 3) = CByte(value And &HFF)
        End Sub

        Private Shared Sub WriteUInt32BE(bytes As Byte(), offset As Integer, value As UInteger)
            bytes(offset) = CByte((value >> 24) And &HFFUI)
            bytes(offset + 1) = CByte((value >> 16) And &HFFUI)
            bytes(offset + 2) = CByte((value >> 8) And &HFFUI)
            bytes(offset + 3) = CByte(value And &HFFUI)
        End Sub

        Private Shared Function Crc32(bytes As Byte(), offset As Integer, count As Integer) As UInteger
            Dim crc As UInteger = &HFFFFFFFFUI
            For i = offset To offset + count - 1
                crc = crc Xor bytes(i)
                For bit = 0 To 7
                    If (crc And 1UI) <> 0UI Then
                        crc = (crc >> 1) Xor &HEDB88320UI
                    Else
                        crc >>= 1
                    End If
                Next
            Next
            Return Not crc
        End Function

        Private Shared Sub WriteUInt16Endian(bytes As Byte(), offset As Integer, value As Integer, littleEndian As Boolean)
            If littleEndian Then
                bytes(offset) = CByte(value And &HFF)
                bytes(offset + 1) = CByte((value >> 8) And &HFF)
            Else
                bytes(offset) = CByte((value >> 8) And &HFF)
                bytes(offset + 1) = CByte(value And &HFF)
            End If
        End Sub

        ''' Für das Auswahlwerkzeug "Kopieren": rendert dieselbe voll bearbeitete Pipeline wie
        ''' SaveImage (Original decodiert + alle Anpassungen/Objekte gebacken), schneidet daraus
        ''' aber nur pixelRect aus und speichert das Ergebnis als eigenständige PNG-Datei -
        ''' Grundlage für ein neues, frei verschiebbares Bild-Objekt (AddImageAnnotationAt).
        ''' <paramref name="workingFull"/>: Arbeitsbild statt Datei-Decode (siehe SaveImage; Besitz wechselt hierher).
        Public Shared Function ExtractRegionToFile(sourcePath As String, adj As ImageAdjustments, pixelRect As SKRectI, targetPngPath As String,
                                                   Optional workingFull As SKBitmap = Nothing) As Boolean
            Try
                Using original = If(workingFull, DecodeOriented(sourcePath))
                    If original Is Nothing Then Return False
                    Using processed = ProcessBitmap(original, adj)
                        Dim left = Math.Max(0, pixelRect.Left)
                        Dim top = Math.Max(0, pixelRect.Top)
                        Dim right = Math.Min(processed.Width, pixelRect.Right)
                        Dim bottom = Math.Min(processed.Height, pixelRect.Bottom)
                        Dim width = right - left
                        Dim height = bottom - top
                        If width <= 0 OrElse height <= 0 Then Return False

                        Using cropped = New SKBitmap(width, height, processed.ColorType, processed.AlphaType)
                            Using canvas = New SKCanvas(cropped)
                                canvas.DrawBitmap(processed, New SKRect(left, top, right, bottom), New SKRect(0, 0, width, height))
                            End Using
                            Using image = SKImage.FromBitmap(cropped)
                                Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                                    WriteFileAtomic(targetPngPath, Sub(fs) data.SaveTo(fs))
                                End Using
                            End Using
                        End Using
                    End Using
                End Using
                Return True
            Catch ex As Exception
                Return False
            End Try
        End Function

        ' ── Freie Selektion (Kern) ────────────────────────────────────────────────
        ' Grundbausteine für Lasso-/Zauberstab-Auswahl. Eine Maske ist ein Alpha8-Bitmap in der Größe
        ' des umschließenden Rechtecks: 255 = innerhalb der Auswahl, 0 = außerhalb. Alle Funktionen sind
        ' rein (Bitmap rein, Bitmap raus) und damit ohne laufenden Editor prüfbar.

        ''' <summary>Zauberstab: wählt ab dem Klickpunkt die zusammenhängende Fläche ähnlicher Farbe (4er-
        ''' Nachbarschaft, Toleranz 0..1 als maximaler Kanalabstand). Liefert eine Alpha8-Maske in der Größe
        ''' des umschließenden Rechtecks, oder Nothing. <paramref name="bounds"/> erhält dieses Rechteck in
        ''' Bildpixeln.</summary>
        Public Shared Function BuildMagicWandMask(image As SKBitmap, seedX As Integer, seedY As Integer,
                                                  tolerance As Single, ByRef bounds As SKRectI) As SKBitmap
            bounds = SKRectI.Empty
            If image Is Nothing Then Return Nothing
            Dim w = image.Width, h = image.Height
            If seedX < 0 OrElse seedY < 0 OrElse seedX >= w OrElse seedY >= h Then Return Nothing

            ' ProcessBitmap bleibt ohne Objekt-Overlays meist Bgra8888, ApplyAnnotations liefert
            ' dagegen bewusst Rgba8888. Der Zauberstab muss beide Szenenformate lesen können; die
            ' frühere Bindung an TryBorrowBgraBuffer ließ ihn bei Bildern mit Objekten still mit
            ' Nothing aussteigen.
            Dim rIdx, gIdx, bIdx As Integer
            Select Case image.ColorType
                Case SKColorType.Bgra8888
                    bIdx = 0 : gIdx = 1 : rIdx = 2
                Case SKColorType.Rgba8888
                    rIdx = 0 : gIdx = 1 : bIdx = 2
                Case Else
                    Return Nothing
            End Select
            Dim stride = image.RowBytes
            Dim length = stride * h
            If length <= 0 OrElse image.GetPixels() = IntPtr.Zero Then Return Nothing
            Dim buf(length - 1) As Byte
            Marshal.Copy(image.GetPixels(), buf, 0, length)

            Dim tol = CInt(Math.Round(Clamp(tolerance, 0, 1) * 255))
            Dim seedO = seedY * stride + seedX * 4
            Dim sr = CInt(buf(seedO + rIdx)), sg = CInt(buf(seedO + gIdx)), sb = CInt(buf(seedO + bIdx))

            ' 0 = unbekannt, 1 = bereits eingereiht, 2 = verworfen, 3 = ausgewählt. Die frühere
            ' Boolean-Maske markierte nur Treffer; dasselbe noch ungeprüfte oder abgelehnte Pixel
            ' konnte deshalb von mehreren Nachbarn immer wieder auf den Stack gelangen.
            Dim state = New Byte(w * h - 1) {}
            Dim stack As New Stack(Of Integer)()
            Dim seedIndex = seedY * w + seedX
            stack.Push(seedIndex)
            state(seedIndex) = 1
            Dim minX = w, minY = h, maxX = -1, maxY = -1

            While stack.Count > 0
                Dim idx = stack.Pop()
                Dim x = idx Mod w, y = idx \ w
                Dim o = y * stride + x * 4
                If Math.Abs(CInt(buf(o + rIdx)) - sr) > tol OrElse
                   Math.Abs(CInt(buf(o + gIdx)) - sg) > tol OrElse
                   Math.Abs(CInt(buf(o + bIdx)) - sb) > tol Then
                    state(idx) = 2
                    Continue While
                End If
                state(idx) = 3
                If x < minX Then minX = x
                If x > maxX Then maxX = x
                If y < minY Then minY = y
                If y > maxY Then maxY = y
                If x > 0 AndAlso state(idx - 1) = 0 Then
                    state(idx - 1) = 1
                    stack.Push(idx - 1)
                End If
                If x < w - 1 AndAlso state(idx + 1) = 0 Then
                    state(idx + 1) = 1
                    stack.Push(idx + 1)
                End If
                If y > 0 AndAlso state(idx - w) = 0 Then
                    state(idx - w) = 1
                    stack.Push(idx - w)
                End If
                If y < h - 1 AndAlso state(idx + w) = 0 Then
                    state(idx + w) = 1
                    stack.Push(idx + w)
                End If
            End While

            If maxX < minX Then Return Nothing
            Dim bw = maxX - minX + 1, bh = maxY - minY + 1
            bounds = New SKRectI(minX, minY, maxX + 1, maxY + 1)

            Dim mask = New SKBitmap(bw, bh, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim mstride = mask.RowBytes
            Dim mbuf = New Byte(mstride * bh - 1) {}
            For y = 0 To bh - 1
                For x = 0 To bw - 1
                    If state((minY + y) * w + (minX + x)) = 3 Then mbuf(y * mstride + x) = 255
                Next
            Next
            Marshal.Copy(mbuf, 0, mask.GetPixels(), mbuf.Length)
            Return mask
        End Function

        Private Shared Function ReadMaskBytes(mask As SKBitmap, ByRef stride As Integer) As Byte()
            stride = mask.RowBytes
            Dim mbuf = New Byte(stride * mask.Height - 1) {}
            Marshal.Copy(mask.GetPixels(), mbuf, 0, mbuf.Length)
            Return mbuf
        End Function

        ''' <summary>Schneidet <paramref name="source"/> mit der Maske aus: RGB bleibt, Alpha wird mit dem
        ''' Masken-Alpha multipliziert (außerhalb der Maske also transparent). Gleiche Größe vorausgesetzt.
        ''' Ergebnis ist Unpremul - passend für die PNG-Ausgabe.</summary>
        Public Shared Function ApplyMaskCutout(source As SKBitmap, mask As SKBitmap) As SKBitmap
            Return ApplyMaskCutoutCore(source, mask, inverted:=False)
        End Function

        ''' <summary>Umkehrung von <see cref="ApplyMaskCutout"/> für "Löschen/Freistellen": innerhalb der
        ''' Maske wird das Bild transparent, außerhalb bleibt es erhalten.</summary>
        Public Shared Function ApplyMaskErase(source As SKBitmap, mask As SKBitmap) As SKBitmap
            Return ApplyMaskCutoutCore(source, mask, inverted:=True)
        End Function

        ''' <summary>Der gemeinsame Kern beider Maskenschnitte. <paramref name="inverted"/> dreht nur um,
        ''' welche Seite der Maske durchlässt.
        '''
        ''' Das RGB bleibt unangetastet, nur das Alpha wird mit dem Masken-Alpha multipliziert. Das Ziel
        ''' ist UNPREMUL (so will es die PNG-Ausgabe) - dorthin schreibt SetPixel die Farbe unverändert,
        ''' es wird hier also NICHT premultipliziert. Die Quelle ist dagegen meist premultipliziert und
        ''' muss beim Lesen zurückgerechnet werden, genau wie GetPixel es täte.</summary>
        Private Shared Function ApplyMaskCutoutCore(source As SKBitmap, mask As SKBitmap,
                                                    inverted As Boolean) As SKBitmap
            Dim w = source.Width, h = source.Height
            Dim result = New SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul)
            Dim mstride As Integer
            Dim mbuf = ReadMaskBytes(mask, mstride)
            Dim maskW = mask.Width, maskH = mask.Height

            Dim srcBuf As Byte() = Nothing
            Dim sStride, ri, gi, bi, ai As Integer
            If TryBorrowRgbaLikeBuffer(source, srcBuf, sStride, ri, gi, bi, ai) Then
                Dim premultiplied = (source.AlphaType = SKAlphaType.Premul)
                Dim dStride = result.RowBytes
                Dim dstBuf = New Byte(dStride * h - 1) {}
                ForEachRow(w, h,
                    Sub(y)
                        Dim so = y * sStride
                        Dim dof = y * dStride
                        Dim mo = y * mstride
                        For x = 0 To w - 1
                            Dim o = so + x * 4
                            Dim a = srcBuf(o + ai)
                            Dim cr = srcBuf(o + ri), cg = srcBuf(o + gi), cb = srcBuf(o + bi)
                            If premultiplied Then
                                cr = UnpremultiplyByte(cr, a)
                                cg = UnpremultiplyByte(cg, a)
                                cb = UnpremultiplyByte(cb, a)
                            End If
                            Dim m = If(x < maskW AndAlso y < maskH, CInt(mbuf(mo + x)), 0)
                            If inverted Then m = 255 - m
                            Dim outAlpha = CByte(CInt(a) * m \ 255)
                            Dim d = dof + x * 4
                            ' Ziel ist Bgra8888, deshalb hier fest B, G, R, A.
                            If outAlpha = 0 Then
                                ' SetPixel NULLT bei Alpha 0 auch die Farbkanaele - gemessen an allen
                                ' 65536 Kombinationen. Ohne diesen Zweig bliebe hinter vollstaendig
                                ' ausmaskierten Stellen die alte Farbe stehen; sichtbar wird das erst,
                                ' wenn jemand das PNG spaeter weiterverarbeitet.
                                dstBuf(d) = 0
                                dstBuf(d + 1) = 0
                                dstBuf(d + 2) = 0
                                dstBuf(d + 3) = 0
                            Else
                                dstBuf(d) = cb
                                dstBuf(d + 1) = cg
                                dstBuf(d + 2) = cr
                                dstBuf(d + 3) = outAlpha
                            End If
                        Next
                    End Sub)
                Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
                Return result
            End If

            ' Rückfall für Farbtypen, die der Puffer nicht abdeckt.
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim c = source.GetPixel(x, y)
                    Dim m = If(x < maskW AndAlso y < maskH, CInt(mbuf(y * mstride + x)), 0)
                    If inverted Then m = 255 - m
                    result.SetPixel(x, y, New SKColor(c.Red, c.Green, c.Blue, CByte(CInt(c.Alpha) * m \ 255)))
                Next
            Next
            Return result
        End Function

        ''' <summary>Füllt die Maske mit einer einzelnen Farbe (Alpha = Farb-Alpha × Masken-Alpha). Grundlage
        ''' für "Auswahl mit Farbe füllen".</summary>
        Public Shared Function RenderMaskedFill(mask As SKBitmap, colorHex As String) As SKBitmap
            Return RenderMaskedFill(mask, colorHex, "Solid", "", 0, False)
        End Function

        ''' <summary>Füllt die Maske mit Vollfarbe oder Verlauf (Alpha = Füll-Alpha × Masken-Alpha).
        ''' Grundlage für "Auswahl füllen", inklusive weicher Rechteckkante.</summary>
        Public Shared Function RenderMaskedFill(mask As SKBitmap, colorHex As String, fillKind As String,
                                                color2Hex As String, gradientAngleDegrees As Single,
                                                gradientInverted As Boolean) As SKBitmap
            Dim col = ParseColor(colorHex, SKColors.White)
            Dim w = mask.Width, h = mask.Height
            Dim fill = New SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul)
            Using canvas = New SKCanvas(fill)
                canvas.Clear(SKColors.Transparent)
                Dim rect = New SKRect(0, 0, w, h)
                Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
                If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                    Dim col2 = ParseColor(color2Hex, col)
                    Using shader = CreateFillGradientShader(rect, normalizedFillKind, col, col2, gradientAngleDegrees, gradientInverted)
                        Using paint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                            canvas.DrawRect(rect, paint)
                        End Using
                    End Using
                Else
                    Using paint = New SKPaint With {.Color = col, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                        canvas.DrawRect(rect, paint)
                    End Using
                End If
            End Using

            Try
                Return ApplyMaskCutout(fill, mask)
            Finally
                fill.Dispose()
            End Try
        End Function

        ''' <summary>Rendert die LUMINANZ einer Füllung (Vollfarbe/Verlauf/radial) als dichten w×h-Byte-Puffer
        ''' (0..255): Schwarz → 0, Weiß → 255, Verlauf → Rampe. Kern für die deklarative Masken-Abstufung -
        ''' die Luminanz stuft, WIE STARK die Anpassung einer Masken-Ebene je Bereich wirkt.</summary>
        Friend Shared Function ComputeFillLuminance(w As Integer, h As Integer, fillKind As String, colorHex As String,
                                                    color2Hex As String, gradientAngleDegrees As Single,
                                                    gradientInverted As Boolean) As Byte()
            If w <= 0 OrElse h <= 0 Then Return Nothing
            Dim col = ParseColor(colorHex, SKColors.White)
            Using fill = New SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Using canvas = New SKCanvas(fill)
                    canvas.Clear(SKColors.Transparent)
                    Dim rect = New SKRect(0, 0, w, h)
                    Dim nk = If(fillKind, "Solid").Trim().ToLowerInvariant()
                    If nk = "lineargradient" OrElse nk = "radialgradient" Then
                        Dim col2 = ParseColor(color2Hex, col)
                        Using shader = CreateFillGradientShader(rect, nk, col, col2, gradientAngleDegrees, gradientInverted)
                            Using paint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                                canvas.DrawRect(rect, paint)
                            End Using
                        End Using
                    Else
                        Using paint = New SKPaint With {.Color = col, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                            canvas.DrawRect(rect, paint)
                        End Using
                    End If
                End Using
                Dim fillStride = fill.RowBytes
                Dim fillBuf = New Byte(fillStride * h - 1) {}
                Marshal.Copy(fill.GetPixels(), fillBuf, 0, fillBuf.Length)
                Dim lum = New Byte(w * h - 1) {}
                For y = 0 To h - 1
                    Dim fRow = y * fillStride, lRow = y * w
                    For x = 0 To w - 1
                        Dim o = fRow + x * 4
                        ' Bgra8888 (Unpremul): 0=B, 1=G, 2=R, 3=A. Deckung = Luminanz (Rec.601) × EIGEN-ALPHA.
                        ' Der Alpha-Anteil MUSS mitzählen: die Standard-Füllfarbe ist "#00FFFFFF" (transparentes
                        ' WEISS). Ohne Alpha ergäbe sie Luminanz 255 = überall volle Deckung, ein Verlauf von
                        ' "transparent" nach Weiß wäre also flach und stufte gar nichts ab
                        ' ("auf der Maske wirkt die Füllung nicht als Deckungsverlauf").
                        Dim l = fillBuf(o + 2) * 0.299 + fillBuf(o + 1) * 0.587 + fillBuf(o) * 0.114
                        lum(lRow + x) = CByte(Math.Round(l * fillBuf(o + 3) / 255.0))
                    Next
                Next
                Return lum
            End Using
        End Function

        ''' <summary>Multipliziert die LUMINANZ einer Füllung in eine vorhandene Alpha8-Maske. Weiterhin genutzt
        ''' für die headless-Diagnose; der interaktive Weg speichert die Füllung deklarativ auf der Ebene.</summary>
        Public Shared Function RenderMaskFilledWithGradient(mask As SKBitmap, colorHex As String, fillKind As String,
                                                            color2Hex As String, gradientAngleDegrees As Single,
                                                            gradientInverted As Boolean) As SKBitmap
            If mask Is Nothing OrElse mask.Width <= 0 OrElse mask.Height <= 0 Then Return Nothing
            Dim w = mask.Width, h = mask.Height
            Dim lum = ComputeFillLuminance(w, h, fillKind, colorHex, color2Hex, gradientAngleDegrees, gradientInverted)
            If lum Is Nothing Then Return Nothing
            Dim mStride = mask.RowBytes
            Dim mBuf = New Byte(mStride * h - 1) {}
            Marshal.Copy(mask.GetPixels(), mBuf, 0, mBuf.Length)
            Dim result = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim rStride = result.RowBytes
            Dim rBuf = New Byte(rStride * h - 1) {}
            For y = 0 To h - 1
                Dim mRow = y * mStride, rRow = y * rStride, lRow = y * w
                For x = 0 To w - 1
                    rBuf(rRow + x) = CByte(CInt(mBuf(mRow + x)) * CInt(lum(lRow + x)) \ 255)
                Next
            Next
            Marshal.Copy(rBuf, 0, result.GetPixels(), rBuf.Length)
            Return result
        End Function

        Public Shared Function RenderMaskedFillToFile(mask As SKBitmap, colorHex As String, fillKind As String,
                                                      color2Hex As String, gradientAngleDegrees As Single,
                                                      gradientInverted As Boolean, targetPngPath As String) As Boolean
            Try
                If mask Is Nothing Then Return False
                Using filled = RenderMaskedFill(mask, colorHex, fillKind, color2Hex, gradientAngleDegrees, gradientInverted)
                    Return SaveBitmapPng(filled, targetPngPath)
                End Using
            Catch ex As Exception
                Return False
            End Try
        End Function

        Public Shared Function RenderMaskedFillToFile(mask As SKBitmap, colorHex As String, targetPngPath As String) As Boolean
            Try
                If mask Is Nothing Then Return False
                Using filled = RenderMaskedFill(mask, colorHex)
                    Return SaveBitmapPng(filled, targetPngPath)
                End Using
            Catch ex As Exception
                Return False
            End Try
        End Function

        Private Shared Function SaveBitmapPng(bmp As SKBitmap, targetPngPath As String) As Boolean
            Using image = SKImage.FromBitmap(bmp)
                Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                    WriteFileAtomic(targetPngPath, Sub(fs) data.SaveTo(fs))
                End Using
            End Using
            Return True
        End Function

        ''' <summary>Wie <see cref="ExtractRegionToFile"/>, aber schneidet den Ausschnitt zusätzlich mit einer
        ''' Maske frei (unregelmäßige Auswahl). Die Maske muss die Größe des (geklemmten) Rechtecks haben.</summary>
        ''' <paramref name="workingFull"/>: Arbeitsbild statt Datei-Decode (siehe SaveImage; Besitz wechselt hierher).
        Public Shared Function ExtractRegionToFileMasked(sourcePath As String, adj As ImageAdjustments,
                                                         pixelRect As SKRectI, mask As SKBitmap, targetPngPath As String,
                                                         Optional workingFull As SKBitmap = Nothing) As Boolean
            Try
                If mask Is Nothing Then
                    workingFull?.Dispose()
                    Return False
                End If
                Using original = If(workingFull, DecodeOriented(sourcePath))
                    If original Is Nothing Then Return False
                    Using processed = ProcessBitmap(original, adj)
                        Dim left = Math.Max(0, pixelRect.Left)
                        Dim top = Math.Max(0, pixelRect.Top)
                        Dim right = Math.Min(processed.Width, pixelRect.Right)
                        Dim bottom = Math.Min(processed.Height, pixelRect.Bottom)
                        Dim width = right - left, height = bottom - top
                        If width <= 0 OrElse height <= 0 Then Return False

                        Using cropped = New SKBitmap(width, height, processed.ColorType, processed.AlphaType)
                            Using canvas = New SKCanvas(cropped)
                                canvas.DrawBitmap(processed, New SKRect(left, top, right, bottom), New SKRect(0, 0, width, height))
                            End Using
                            Using cutout = ApplyMaskCutout(cropped, mask)
                                Return SaveBitmapPng(cutout, targetPngPath)
                            End Using
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Return False
            End Try
        End Function

        ''' <summary>Speichert eine mit <paramref name="colorHex"/> gefüllte Maske als PNG - Grundlage für ein
        ''' Füll-Objekt in exakter Auswahlgröße.</summary>
        ' Baut aus dem Alpha-Kanal eines gezeichneten RGBA-Bitmaps die Alpha8-Maske (weiche Kanten bleiben
        ' als Teil-Alpha erhalten - dadurch werden Ellipse/Lasso-Auswahlen antialiased ausgeschnitten).
        Private Shared Function AlphaMaskFrom(rgba As SKBitmap) As SKBitmap
            Dim w = rgba.Width, h = rgba.Height
            Dim mask = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim mstride = mask.RowBytes
            Dim mbuf = New Byte(mstride * h - 1) {}
            Dim src As Byte() = Nothing, stride As Integer = 0
            If TryBorrowBgraBuffer(rgba, src, stride) Then
                For y = 0 To h - 1
                    For x = 0 To w - 1
                        mbuf(y * mstride + x) = src(y * stride + x * 4 + 3)
                    Next
                Next
            End If
            Marshal.Copy(mbuf, 0, mask.GetPixels(), mbuf.Length)
            Return mask
        End Function

        ''' <summary>Alpha8-Maske einer in das Rechteck (0,0,width,height) eingepassten Ellipse - für das
        ''' Kreis-/Ellipse-Auswahlwerkzeug.</summary>
        Public Shared Function BuildEllipseMask(width As Integer, height As Integer) As SKBitmap
            Return BuildEllipseMask(width, height, New SKRect(0, 0, width, height))
        End Function

        Public Shared Function BuildEllipseMask(width As Integer, height As Integer, ovalRect As SKRect) As SKBitmap
            If width <= 0 OrElse height <= 0 Then Return Nothing
            Using rgba = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Using canvas = New SKCanvas(rgba)
                    canvas.Clear(SKColors.Transparent)
                    Using paint = New SKPaint With {.Color = SKColors.White, .IsAntialias = True, .Style = SKPaintStyle.Fill}
                        canvas.DrawOval(ovalRect, paint)
                    End Using
                End Using
                Return AlphaMaskFrom(rgba)
            End Using
        End Function

        ''' <summary>Alpha8-Maske eines gefüllten Polygons (Lasso). Punkte in lokalen Koordinaten des
        ''' Rechtecks (0..width / 0..height); der Pfad wird automatisch geschlossen.</summary>
        Public Shared Function BuildPolygonMask(pointsX As Single(), pointsY As Single(), width As Integer, height As Integer) As SKBitmap
            If width <= 0 OrElse height <= 0 OrElse pointsX Is Nothing OrElse pointsY Is Nothing Then Return Nothing
            If pointsX.Length < 3 OrElse pointsX.Length <> pointsY.Length Then Return Nothing
            Using rgba = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Using canvas = New SKCanvas(rgba)
                    canvas.Clear(SKColors.Transparent)
                    Using path = New SKPath()
                        path.MoveTo(pointsX(0), pointsY(0))
                        For i = 1 To pointsX.Length - 1
                            path.LineTo(pointsX(i), pointsY(i))
                        Next
                        path.Close()
                        Using paint = New SKPaint With {.Color = SKColors.White, .IsAntialias = True, .Style = SKPaintStyle.Fill}
                            canvas.DrawPath(path, paint)
                        End Using
                    End Using
                End Using
                Return AlphaMaskFrom(rgba)
            End Using
        End Function

        ''' <summary>Zeichnet einen weichen runden Pinselstrich (Masken-Pinsel). <paramref name="pts"/>
        ''' sind die Strich-Stützpunkte im Zielraum des Canvas; <paramref name="radius"/> der Pinselradius,
        ''' <paramref name="softnessPx"/> die Randweichheit in Pixeln (Gauß-Sigma = softness/2), gemeinsam
        ''' genutzt für den weißen Masken-Stempel UND die rote Live-Vorschau (Farbe frei). Ein einzelner
        ''' Punkt (Klick) wird als gefüllte Scheibe gezeichnet. <paramref name="erase"/> = True stanzt statt
        ''' zu malen (DstOut) - für Subtrahieren in der Live-Vorschau. MaskFilter wirkt hier (DrawPath/
        ''' DrawCircle, NICHT DrawBitmap - dort wäre er wirkungslos).</summary>
        Public Shared Sub DrawSoftMaskStroke(canvas As SKCanvas, pts As IReadOnlyList(Of SKPoint),
                                             radius As Single, softnessPx As Single, color As SKColor,
                                             Optional eraseMode As Boolean = False)
            If canvas Is Nothing OrElse pts Is Nothing OrElse pts.Count = 0 OrElse radius <= 0 Then Return
            Dim blend = If(eraseMode, SKBlendMode.DstOut, SKBlendMode.SrcOver)
            Dim sigma = If(softnessPx > 0.05F, softnessPx * 0.5F, 0.0F)
            If pts.Count = 1 Then
                Using paint = New SKPaint With {.Color = color, .Style = SKPaintStyle.Fill, .IsAntialias = True, .BlendMode = blend}
                    If sigma > 0.0F Then paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma)
                    canvas.DrawCircle(pts(0).X, pts(0).Y, radius, paint)
                End Using
                Return
            End If
            Using paint = New SKPaint With {.Color = color, .Style = SKPaintStyle.Stroke, .StrokeCap = SKStrokeCap.Round,
                                            .StrokeJoin = SKStrokeJoin.Round, .StrokeWidth = radius * 2.0F, .IsAntialias = True, .BlendMode = blend}
                If sigma > 0.0F Then paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma)
                Using path = New SKPath()
                    path.MoveTo(pts(0).X, pts(0).Y)
                    For i = 1 To pts.Count - 1
                        path.LineTo(pts(i).X, pts(i).Y)
                    Next
                    canvas.DrawPath(path, paint)
                End Using
            End Using
        End Sub

        ''' <summary>Weicher Pinselstrich als Alpha8-Maske in der Größe von <paramref name="rect"/>
        ''' (Display-Bildraum). Die Strichpunkte liegen im Display-Bildraum; sie werden um rect.Left/Top
        ''' in die Stempel-lokalen Koordinaten verschoben. Für den Commit eines Strichs, danach über
        ''' ApplySelectionCandidate mit dem aktuellen Kombiniermodus verrechnet.</summary>
        ''' <summary>Bildet eine gespeicherte Maske im QUELLRAUM auf ein neues Rechteck ab - dieselbe
        ''' Abbildung, die eine Gruppen-Transformation auf ihre Objekte anwendet. Ohne das bliebe die
        ''' Korrektur einer mitbewegten Ebene an Ort und Größe stehen.
        ''' Die Maske wird dafür neu gerastert - das kostet etwas Kantenschärfe, ist aber die einzige
        ''' Möglichkeit, solange die Ursprungsform nicht als Geometrie aufbewahrt wird.</summary>
        ''' <summary>
        ''' Dreht die Maskenregion um einen Punkt - das Gegenstück zu <see cref="TransformMaskRegion"/>
        ''' für Gruppen-Drehungen. Ohne das blieb die Korrektur einer mitgedrehten Gruppe an Ort und
        ''' Lage liegen, während ihre Objekte wanderten.
        '''
        ''' Die Maske wird dabei NEU GERASTERT (Alpha8 kennt keine Drehung im Rechteck), verliert also
        ''' bei jeder Drehung ein wenig Kantenschärfe - dieselbe bewusst in Kauf genommene Einbuße wie
        ''' beim Skalieren. Gerechnet wird im Quellraum, in dem die Maske gespeichert ist.
        ''' </summary>
        Public Shared Function RotateMaskRegion(mask As ImageMask, degrees As Double,
                                                pivotX As Double, pivotY As Double) As Boolean
            If mask Is Nothing OrElse String.IsNullOrWhiteSpace(mask.PngBase64) Then Return False
            If Math.Abs(degrees) < 0.0001 Then Return True

            Dim rad = degrees * Math.PI / 180.0
            Dim cosR = Math.Cos(rad), sinR = Math.Sin(rad)
            Dim corners = {(CDbl(mask.Left), CDbl(mask.Top)), (CDbl(mask.Right), CDbl(mask.Top)),
                         (CDbl(mask.Right), CDbl(mask.Bottom)), (CDbl(mask.Left), CDbl(mask.Bottom))}
            Dim minX = Double.MaxValue, minY = Double.MaxValue, maxX = Double.MinValue, maxY = Double.MinValue
            For Each e In corners
                Dim dx = e.Item1 - pivotX, dy = e.Item2 - pivotY
                Dim nx = pivotX + dx * cosR - dy * sinR
                Dim ny = pivotY + dx * sinR + dy * cosR
                minX = Math.Min(minX, nx) : maxX = Math.Max(maxX, nx)
                minY = Math.Min(minY, ny) : maxY = Math.Max(maxY, ny)
            Next

            Dim l = CInt(Math.Floor(minX)), t = CInt(Math.Floor(minY))
            Dim w = Math.Max(1, CInt(Math.Ceiling(maxX)) - l), h = Math.Max(1, CInt(Math.Ceiling(maxY)) - t)

            Dim decoded As SKBitmap = Nothing
            Try
                decoded = SKBitmap.Decode(Convert.FromBase64String(mask.PngBase64))
                If decoded Is Nothing OrElse decoded.ColorType <> SKColorType.Alpha8 Then Return False
                Using gedreht = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(gedreht)
                        canvas.Clear(SKColors.Transparent)
                        ' Im QUELLRAUM zeichnen: erst den Ursprung des neuen Rechtecks wegschieben,
                        ' dann um den Drehpunkt drehen, dann die Maske an ihrer alten Stelle absetzen.
                        canvas.Translate(CSng(-l), CSng(-t))
                        canvas.RotateDegrees(CSng(degrees), CSng(pivotX), CSng(pivotY))
                        Using paint = New SKPaint With {.IsAntialias = True}
                            ' Ueber SKImage, weil nur DrawImage die Abtastung entgegennimmt -
                            ' DrawBitmap hat dafuer keine Ueberladung mehr.
                            Using image = SKImage.FromBitmap(decoded)
                                canvas.DrawImage(image, New SKRect(mask.Left, mask.Top,
                                                                  mask.Left + decoded.Width, mask.Top + decoded.Height),
                                                 New SKSamplingOptions(SKCubicResampler.Mitchell), paint)
                            End Using
                        End Using
                    End Using
                    Using img = SKImage.FromPixels(gedreht.PeekPixels())
                        Using data = img.Encode(SKEncodedImageFormat.Png, 100)
                            If data Is Nothing Then Return False
                            mask.PngBase64 = Convert.ToBase64String(data.ToArray())
                        End Using
                    End Using
                End Using
                mask.Left = l : mask.Top = t : mask.Right = l + w : mask.Bottom = t + h
                Return True
            Catch
                Return False
            Finally
                decoded?.Dispose()
            End Try
        End Function

        ''' <summary>Spiegelt die Maskenregion an einer Achse (Gruppen-Spiegelung). Hier bleibt die
        ''' Auflösung erhalten - gespiegelt wird pixelgenau, nur die Lage wechselt die Seite.</summary>
        Public Shared Function FlipMaskRegion(mask As ImageMask, horizontal As Boolean, axis As Double) As Boolean
            If mask Is Nothing OrElse String.IsNullOrWhiteSpace(mask.PngBase64) Then Return False

            Dim decoded As SKBitmap = Nothing
            Try
                decoded = SKBitmap.Decode(Convert.FromBase64String(mask.PngBase64))
                If decoded Is Nothing OrElse decoded.ColorType <> SKColorType.Alpha8 Then Return False
                Dim w = decoded.Width, h = decoded.Height
                Dim l = mask.Left, t = mask.Top
                If horizontal Then
                    l = CInt(Math.Round(2 * axis - mask.Right))
                Else
                    t = CInt(Math.Round(2 * axis - mask.Bottom))
                End If

                Using gespiegelt = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(gespiegelt)
                        canvas.Clear(SKColors.Transparent)
                        If horizontal Then
                            canvas.Scale(-1.0F, 1.0F, w / 2.0F, 0.0F)
                        Else
                            canvas.Scale(1.0F, -1.0F, 0.0F, h / 2.0F)
                        End If
                        canvas.DrawBitmap(decoded, 0.0F, 0.0F)
                    End Using
                    Using img = SKImage.FromPixels(gespiegelt.PeekPixels())
                        Using data = img.Encode(SKEncodedImageFormat.Png, 100)
                            If data Is Nothing Then Return False
                            mask.PngBase64 = Convert.ToBase64String(data.ToArray())
                        End Using
                    End Using
                End Using
                mask.Left = l : mask.Top = t : mask.Right = l + w : mask.Bottom = t + h
                Return True
            Catch
                Return False
            Finally
                decoded?.Dispose()
            End Try
        End Function

        Public Shared Function TransformMaskRegion(mask As ImageMask,
                                                   scaleX As Double, scaleY As Double,
                                                   pivotX As Double, pivotY As Double,
                                                   offsetX As Double, offsetY As Double) As Boolean
            If mask Is Nothing OrElse String.IsNullOrWhiteSpace(mask.PngBase64) Then Return False
            If scaleX <= 0 OrElse scaleY <= 0 Then Return False

            Dim newLeft = pivotX + (mask.Left - pivotX) * scaleX + offsetX
            Dim newTop = pivotY + (mask.Top - pivotY) * scaleY + offsetY
            Dim newRight = pivotX + (mask.Right - pivotX) * scaleX + offsetX
            Dim newBottom = pivotY + (mask.Bottom - pivotY) * scaleY + offsetY

            Dim l = CInt(Math.Round(newLeft)), t = CInt(Math.Round(newTop))
            Dim r = CInt(Math.Round(newRight)), b = CInt(Math.Round(newBottom))
            Dim w = Math.Max(1, r - l), h = Math.Max(1, b - t)

            Dim decoded As SKBitmap = Nothing
            Try
                decoded = SKBitmap.Decode(Convert.FromBase64String(mask.PngBase64))
                If decoded Is Nothing OrElse decoded.ColorType <> SKColorType.Alpha8 Then Return False
                If w = decoded.Width AndAlso h = decoded.Height Then
                    ' Reine Verschiebung: die Pixel bleiben, nur das Rechteck wandert.
                    mask.Left = l : mask.Top = t : mask.Right = l + decoded.Width : mask.Bottom = t + decoded.Height
                    Return True
                End If
                Using skaliert = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(skaliert)
                        canvas.Clear(SKColors.Transparent)
                        Using paint = New SKPaint With {.IsAntialias = True}
                            Using image = SKImage.FromBitmap(decoded)
                                canvas.DrawImage(image, New SKRect(0, 0, w, h),
                                                 New SKSamplingOptions(SKCubicResampler.Mitchell), paint)
                            End Using
                        End Using
                    End Using
                    Using img = SKImage.FromPixels(skaliert.PeekPixels())
                        Using data = img.Encode(SKEncodedImageFormat.Png, 100)
                            If data Is Nothing Then Return False
                            mask.PngBase64 = Convert.ToBase64String(data.ToArray())
                        End Using
                    End Using
                End Using
                mask.Left = l : mask.Top = t : mask.Right = l + w : mask.Bottom = t + h
                Return True
            Catch
                Return False
            Finally
                decoded?.Dispose()
            End Try
        End Function

        Public Shared Function BuildSoftBrushStampMask(pts As IReadOnlyList(Of SKPoint), radius As Single,
                                                       softnessPx As Single, rect As SKRectI) As SKBitmap
            If pts Is Nothing OrElse pts.Count = 0 OrElse rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing
            Dim local As New List(Of SKPoint)(pts.Count)
            For Each p In pts
                local.Add(New SKPoint(p.X - rect.Left, p.Y - rect.Top))
            Next
            Using rgba = New SKBitmap(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Using canvas = New SKCanvas(rgba)
                    canvas.Clear(SKColors.Transparent)
                    DrawSoftMaskStroke(canvas, local, radius, softnessPx, SKColors.White)
                End Using
                Return AlphaMaskFrom(rgba)
            End Using
        End Function

        ''' <summary>Dekodiert und verarbeitet das Bild (alle Anpassungen/Objekte gebacken) und liefert die
        ''' Zauberstab-Maske am Saatpunkt in Bildpixeln. <paramref name="bounds"/> ist das umschließende
        ''' Rechteck in Bildpixeln.</summary>
        ''' <paramref name="workingFull"/>: Arbeitsbild statt Datei-Decode (siehe SaveImage; Besitz wechselt hierher).
        ''' <summary>Das fertig gerechnete ANZEIGEBILD, wie es der Editor zeigt - Besitz beim
        ''' Aufrufer. Fuer alles, was auf dem gesehenen Bild arbeiten muss statt auf der Datei:
        ''' Zauberstab, Objektauswahl per Modell.
        '''
        ''' <paramref name="workingFull"/> ist das Arbeitsbild, falls schon eines vorliegt; sonst
        ''' wird die Quelle gelesen. Es wird HIER verbraucht, der Aufrufer gibt es nicht selbst
        ''' frei.</summary>
        Public Shared Function RenderDisplayImage(sourcePath As String, adj As ImageAdjustments,
                                                 Optional workingFull As SKBitmap = Nothing) As SKBitmap
            Try
                Using original = If(workingFull, DecodeOriented(sourcePath))
                    If original Is Nothing Then Return Nothing
                    Return ProcessBitmap(original, adj)
                End Using
            Catch ex As Exception
                Return Nothing
            End Try
        End Function

        Public Shared Function BuildMagicWandMaskFromFile(sourcePath As String, adj As ImageAdjustments,
                                                          seedX As Integer, seedY As Integer, tolerance As Single,
                                                          ByRef bounds As SKRectI,
                                                          Optional workingFull As SKBitmap = Nothing) As SKBitmap
            bounds = SKRectI.Empty
            Using processed = RenderDisplayImage(sourcePath, adj, workingFull)
                If processed Is Nothing Then Return Nothing
                Return BuildMagicWandMask(processed, seedX, seedY, tolerance, bounds)
            End Using
        End Function

        ''' <summary>Radius des GROBEN Chroma-Durchgangs in Pixeln des GERADE bearbeiteten Bildes.
        '''
        ''' Bewusst relativ zur Bildkante: Farbflecken sind ein Anteil des Bildes, kein absolutes
        ''' Pixelmass. Ein fester Radius traefe in der Vorschau (lange Kante ~1500 px) eine ganz
        ''' andere Fleckengroesse als im Export (6000 px) - Vorschau und Ergebnis saehen verschieden
        ''' aus. Weil `source` hier immer das ist, was auch angezeigt bzw. gespeichert wird, stimmen
        ''' beide ueberein. Der Deckel haelt die Rechenzeit bei sehr grossen Bildern im Rahmen.</summary>
        Private Shared Function CoarseChromaSigma(source As SKBitmap, amount As Single) As Single
            Dim longEdge = CSng(Math.Max(source.Width, source.Height))
            ' Der Teiler ist gemessen, nicht geschaetzt: bei 1600 px langer Kante und Flecken von
            ' 6 bis 12 px bleiben mit /300 noch 36 bzw. 56 Prozent stehen, mit /150 nur noch 27
            ' bzw. 39 Prozent - erst da ist der Vollausschlag das, was er verspricht. Feines
            ' Pixelrauschen faellt dabei von 11 auf 0,5 Prozent.
            Return Clamp(amount * longEdge / 150.0F, 0, 24.0F)
        End Function

        ''' <summary>Farb-Rauschreduzierung (crs:ColorNoiseReduction): das Bild wird weichgezeichnet,
        ''' danach bekommt jedes Pixel seine ORIGINAL-Helligkeit zurueck (Differenz der Rec.601-Luma
        ''' auf alle Kanaele addiert). Ergebnis: Chroma aus dem Blur, Luminanz vom Original - Kanten
        ''' und Details bleiben stehen, Farbflecken verschwinden. Chroma vertraegt deutlich mehr
        ''' Glaettung als Helligkeit, daher ein groesseres Sigma als bei ApplyNoiseReduction.
        '''
        ''' ZWEI DURCHGAENGE (Messung): der feine Pass allein (Sigma bis 2,5 px) loescht
        ''' pixelweises Farbrauschen restlos aus - genau das prueft die Diagnose seit.
        ''' Das Farbrauschen eines hochgezogenen Nachthimmels ist aber TIEFFREQUENT: Flecken von
        ''' 5 bis 15 px. Davon liessen 2,5 px gemessen 61 bis 78 Prozent stehen, der Regler war fuer
        ''' den eigentlichen Anwendungsfall wirkungslos (Nachtaufnahme).
        ''' Der grobe Pass setzt darum auf dem feinen auf; kaskadiert wirken beide wie ein Radius
        ''' von Wurzel(fein^2 + grob^2), ohne dass der feine Anteil unten am Regler seine Wirkung
        ''' verliert.</summary>
        ''' <summary>Wie schnell der FEINE Durchgang mit dem Regler hochlaeuft. Er war frueher
        ''' linear an den Reglerwert gekoppelt: bei 25 wurden 25 Prozent geglaettete Chroma mit
        ''' 1,0 px Radius beigemischt. Gemessen an einem Konzert-RAW liess das vom Farbrauschen
        ''' 3,26 von 4,39 stehen, waehrend die Referenz beim GLEICHEN Wert auf 1,25 kommt - unsere 25
        ''' wirkten wie deren 5. Feines Pixelrauschen ist aber bei kleinem Radius erledigt oder gar
        ''' nicht; ein Regler, der es erst bei Vollausschlag wegnimmt, ist falsch geeicht.
        ''' Der feine Durchgang (Radius UND Beimischung) ist deshalb bei 42 voll ausgefahren -
        ''' damit trifft 25, der uebliche Standardwert fuer RAWs, auch bei uns dessen Wirkung.
        ''' Der obere Reglerbereich bleibt nicht tot: der GROBE Durchgang skaliert weiterhin mit
        ''' dem rohen Reglerwert ueber die ganze Strecke, und nur er erreicht die tieffrequenten
        ''' Farbflecken (siehe CoarseChromaSigma).</summary>
        Private Const ChromaFineGain As Single = 3.2F

        Private Shared Function ApplyColorNoiseReduction(source As SKBitmap, amount As Single) As SKBitmap
            amount = Clamp(amount, 0, 1)
            Dim fein = Clamp(amount * ChromaFineGain, 0, 1)
            ' Feiner Pass: Sigma waechst bis 2,5 Pixel, und dieselbe Staerke blendet zwischen
            ' Original-Chroma und geglaetteter Chroma ueber. BEIDES haengt an fein, nicht am rohen
            ' Reglerwert - sonst waere der untere Reglerbereich zu schwach (siehe ChromaFineGain).
            ' Historie, damit es nicht rueckwaerts repariert wird: als NUR das Sigma gesteuert wurde,
            ' war der Regler ab etwa 30 wirkungslos (50 und 100 aenderten dieselben 53 bzw. 54 % der
            ' Pixel um maximal 7 bzw. 6 Tonwerte) - deshalb kam die Ueberblendung dazu. Sie linear an
            ' den Reglerwert zu haengen war dann die Uebertreibung in die andere Richtung.
            Dim sigma = 0.5F + fein * 2.0F
            Dim blurred = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using filter = SKImageFilter.CreateBlur(sigma, sigma)
                Using paint = New SKPaint With {.ImageFilter = filter}
                    Using canvas = New SKCanvas(blurred)
                        canvas.DrawBitmap(source, 0, 0, paint)
                    End Using
                End Using
            End Using

            ' Grober Pass auf dem feinen: nimmt die FLECKEN, die der feine Radius nicht erreicht.
            Dim coarseSigma = CoarseChromaSigma(source, amount)
            Dim coarse As SKBitmap = Nothing
            If coarseSigma > 0.5F Then
                coarse = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
                Using filter = SKImageFilter.CreateBlur(coarseSigma, coarseSigma)
                    Using paint = New SKPaint With {.ImageFilter = filter}
                        Using canvas = New SKCanvas(coarse)
                            canvas.DrawBitmap(blurred, 0, 0, paint)
                        End Using
                    End Using
                End Using
            End If

            Dim srcBuf As Byte() = Nothing
            Dim srcStride As Integer = 0
            Dim blurBuf As Byte() = Nothing
            Dim blurStride As Integer = 0
            Dim coarseBuf As Byte() = Nothing
            Dim coarseStride As Integer = 0
            If Not TryBorrowBgraBuffer(source, srcBuf, srcStride) OrElse
               Not TryBorrowBgraBuffer(blurred, blurBuf, blurStride) Then
                coarse?.Dispose()
                Return blurred
            End If
            If coarse IsNot Nothing AndAlso Not TryBorrowBgraBuffer(coarse, coarseBuf, coarseStride) Then
                coarseBuf = Nothing
            End If

            ' SCHUTZ FUER ECHTE FARBEN: ein Chroma-Blur macht keinen Unterschied zwischen einem
            ' Farbfleck und einem kleinen bunten Licht - beide sind "Farbe auf kleiner Flaeche".
            ' Gemessen verlor eine 3x3 px grosse Fensterlampe bei Vollausschlag 80 Prozent ihrer
            ' Saettigung. Rauschen ist aber SCHWACH gesaettigt und echte Lichter sind es stark:
            ' ab einer Farbabweichung von 30 nimmt die Wirkung ab, ab 90 bleibt das Pixel ganz in
            ' Ruhe. Der Schutz gilt fuer BEIDE Durchgaenge - am feinen allein vorbeigezogen haette
            ' er nichts genuetzt, denn schon dessen 2,5 px bleichen ein so kleines Licht aus.
            ' Farbsaum um ein Licht herum entsteht dadurch nicht nennenswert: die Farbe einer
            ' 3x3-Lampe verteilt sich im Blur auf einige hundert Pixel und geht darin unter.
            Const schutzVon As Double = 30.0
            Const schutzBis As Double = 90.0
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim dstBuf(srcBuf.Length - 1) As Byte
            ForEachRow(source.Width, source.Height, Sub(y)
                                                        Dim so = y * srcStride
                                                        Dim bo = y * blurStride
                                                        Dim co = y * coarseStride
                                                        For x = 0 To source.Width - 1
                                                            Dim colIdx = so + x * 4
                                                            Dim bi = bo + x * 4
                                                            ' Rec.601-Luma in Ganzzahlarithmetik (x1024).
                                                            Dim lumaSrc = (299 * CInt(srcBuf(colIdx + 2)) + 587 * CInt(srcBuf(colIdx + 1)) + 114 * CInt(srcBuf(colIdx))) \ 1000
                                                            Dim mix0 = CDbl(blurBuf(bi))
                                                            Dim mix1 = CDbl(blurBuf(bi + 1))
                                                            Dim mix2 = CDbl(blurBuf(bi + 2))
                                                            If coarseBuf IsNot Nothing Then
                                                                Dim ci = co + x * 4
                                                                mix0 = CDbl(coarseBuf(ci))
                                                                mix1 = CDbl(coarseBuf(ci + 1))
                                                                mix2 = CDbl(coarseBuf(ci + 2))
                                                            End If
                                                            Dim chroma = (Math.Abs(CInt(srcBuf(colIdx + 2)) - lumaSrc) + Math.Abs(CInt(srcBuf(colIdx)) - lumaSrc)) / 2.0
                                                            Dim schutz = 1.0 - Math.Max(0.0, Math.Min(1.0, (chroma - schutzVon) / (schutzBis - schutzVon)))
                                                            Dim wirkung = fein * schutz
                                                            Dim lumaMix = (299 * mix2 + 587 * mix1 + 114 * mix0) / 1000.0
                                                            Dim delta = lumaSrc - lumaMix
                                                            ' Chroma aus dem Blur, Luminanz vom Original - und beides
                                                            ' anteilig ueber das Original geblendet, damit der Regler
                                                            ' ueber den ganzen Weg etwas tut.
                                                            Dim nb0 = mix0 + delta
                                                            Dim nb1 = mix1 + delta
                                                            Dim nb2 = mix2 + delta
                                                            dstBuf(colIdx) = ClampToByte(CInt(srcBuf(colIdx)) + (nb0 - CInt(srcBuf(colIdx))) * wirkung)
                                                            dstBuf(colIdx + 1) = ClampToByte(CInt(srcBuf(colIdx + 1)) + (nb1 - CInt(srcBuf(colIdx + 1))) * wirkung)
                                                            dstBuf(colIdx + 2) = ClampToByte(CInt(srcBuf(colIdx + 2)) + (nb2 - CInt(srcBuf(colIdx + 2))) * wirkung)
                                                            dstBuf(colIdx + 3) = srcBuf(colIdx + 3)
                                                        Next
                                                    End Sub)
            blurred.Dispose()
            coarse?.Dispose()
            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            Return result
        End Function

        ''' <summary>Die PLUS-Seite des Farbrauschen-Reglers: faerbt das Bild ein, ohne es heller oder
        ''' dunkler zu machen. Pro Pixel wandern Rot und Blau gegenlaeufig, Gruen gleicht die Helligkeit
        ''' aus - das ist genau die Gegenrichtung zur Reduzierung (die Chroma glaettet und die Luma
        ''' stehen laesst). Vom monochromen „Koernung" (ApplyGrain) unterscheidet es sich dadurch, dass
        ''' die Helligkeit unangetastet bleibt und nur die Farbe zappelt.
        ''' Der Zufall haengt allein an der Bildgroesse (wie ApplyAddNoise): derselbe Regler ergibt
        ''' zweimal dasselbe Bild, und die Vorschau flackert beim Ziehen nicht.</summary>
        Private Shared Function ApplyColorNoiseAdd(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return source
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) Then Return result
            Dim dstBuf = New Byte(srcBuf.Length - 1) {}

            Dim random = New Random(source.Width * 733 Xor source.Height * 397)
            Dim amplitude = strength * 48.0

            For y As Integer = 0 To source.Height - 1
                Dim rowOffset = y * stride
                For x As Integer = 0 To source.Width - 1
                    Dim o = rowOffset + x * 4
                    Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                    ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                    Dim dr = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    Dim db = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    ' Rec.601: Gruen traegt 0,587 der Helligkeit - genau so viel gegensteuern, wie
                    ' Rot (0,299) und Blau (0,114) zusammen einbringen, dann bleibt die Luma gleich.
                    Dim dg = -(0.299 * dr + 0.114 * db) / 0.587
                    WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                                       ClampToByte(cr + dr), ClampToByte(cg + dg), ClampToByte(cb + db), a)
                Next
            Next

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            Return result
        End Function

        Private Shared Function ApplyNoiseReduction(source As SKBitmap, amount As Single, Optional detail As Single = 0) As SKBitmap
            Dim sigma = 0.25F + Clamp(amount, 0, 1) * 2.2F
            Dim filter = SKImageFilter.CreateBlur(sigma, sigma)
            Dim paint = New SKPaint With {.ImageFilter = filter}
            Dim blurred = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(blurred)
                canvas.DrawBitmap(source, 0, 0, paint)
            End Using
            filter.Dispose()
            paint.Dispose()

            ' Detail 0 = reines Weichzeichnen wie bisher (bitgenau der frühere Rückgabewert).
            Dim d = Clamp(detail, 0, 1)
            If d <= 0 Then Return blurred

            ' Kantenerhalt: wo Original und Weichzeichnung stark abweichen (= eine Kante), das Original
            ' anteilig zurückmischen. Flache, verrauschte Flächen bleiben geglättet.
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing, blurBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            Dim bStride, bri, bgi, bbi, bai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) OrElse
               Not TryBorrowRgbaLikeBuffer(blurred, blurBuf, bStride, bri, bgi, bbi, bai) Then
                blurred.Dispose()
                Return result
            End If
            Dim dstBuf = New Byte(srcBuf.Length - 1) {}
            Dim w = source.Width, h = source.Height

            ForEachRow(w, h,
                Sub(y)
                    Dim rowOffset = y * stride
                    Dim bRow = y * bStride
                    For x = 0 To w - 1
                        Dim o = rowOffset + x * 4
                        Dim bo = bRow + x * 4
                        Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                        ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                        Dim lr As Integer, lg As Integer, lb As Integer, la As Integer
                        ReadUnpremultiplied(blurBuf, bo, bri, bgi, bbi, bai, lr, lg, lb, la)
                        Dim diff = Math.Abs(cr - lr) + Math.Abs(cg - lg) + Math.Abs(cb - lb)
                        Dim mask = Clamp(diff / 48.0F, 0, 1) * d
                        WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                            ClampToByte(lr + (cr - lr) * mask),
                            ClampToByte(lg + (cg - lg) * mask),
                            ClampToByte(lb + (cb - lb) * mask), a)
                    Next
                End Sub)

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            blurred.Dispose()
            Return result
        End Function

        ''' <summary>Der Wert an Position <paramref name="mid"/> der sortierten Fensterwerte, gelesen aus dem
        ''' Histogramm: der kleinste Tonwert, bis zu dem mehr als <paramref name="mid"/> Werte liegen. Das ist
        ''' exakt das, was `sortierteListe(mid)` liefert - nur ohne die Liste und ohne das Sortieren.</summary>
        Private Shared Function HistogramMedian(histogram As Integer(), mid As Integer) As Byte
            Dim running As Integer = 0
            For v As Integer = 0 To 255
                running += histogram(v)
                If running > mid Then Return CByte(v)
            Next
            Return 255
        End Function

        ''' <summary>Kantenerhaltender Medianfilter - echte Rauschunterdrückung statt gleichmäßigem
        ''' Weichzeichnen.
        ''' Das Fenster wandert als HISTOGRAMM mit: beim Schritt nach rechts wird nur die austretende Spalte
        ''' ab- und die eintretende zugezählt, statt für jedes Pixel alle (2r+1)² Werte neu einzusammeln und
        ''' zu sortieren. Die vorherige Fassung baute pro Pixel drei Listen auf und rief `List.Sort` -
        ''' bei Radius 3 also 49 Elemente, sortiert, 2,56 Millionen Mal. Das war die Zähigkeit, die man an
        ''' den Reglern für Rauschreduzierung und Staub/Kratzer gespürt hat.
        ''' Der Median ist exakt, das Ergebnis daher BITGLEICH zur alten Fassung - kein Kompromiss auf
        ''' Kosten der Bildqualität.</summary>
        Private Shared Function ApplyMedianBlur(source As SKBitmap, amount As Single) As SKBitmap
            Dim clamped = Clamp(amount, 0, 1)
            Dim radius = Math.Max(1, CInt(Math.Round(1 + clamped * 2)))
            Dim w = source.Width
            Dim h = source.Height
            Dim result = New SKBitmap(w, h, source.ColorType, source.AlphaType)
            Dim rWindow As New List(Of Byte)()
            Dim gWindow As New List(Of Byte)()
            Dim bWindow As New List(Of Byte)()

            Dim srcBuf As Byte() = Nothing
            Dim stride As Integer = 0
            If TryBorrowBgraBuffer(source, srcBuf, stride) Then
                Dim dstBuf = New Byte(srcBuf.Length - 1) {}
                ' Jede Zeile bekommt eigene Histogramme - parallel dürfen sie nicht geteilt werden.
                ForEachRow(w, h, Sub(y)
                                     Dim histB = New Integer(255) {}
                                     Dim histG = New Integer(255) {}
                                     Dim histR = New Integer(255) {}
                                     Dim count As Integer = 0
                                     Dim rowOffset = y * stride
                                     Dim yFrom = Math.Max(0, y - radius)
                                     Dim yTo = Math.Min(h - 1, y + radius)

                                     ' Startfenster für x = 0 aufbauen; danach nur noch verschieben.
                                     For xx As Integer = 0 To Math.Min(w - 1, radius)
                                         For yy As Integer = yFrom To yTo
                                             Dim oo = yy * stride + xx * 4
                                             histB(srcBuf(oo)) += 1
                                             histG(srcBuf(oo + 1)) += 1
                                             histR(srcBuf(oo + 2)) += 1
                                             count += 1
                                         Next
                                     Next

                                     For x As Integer = 0 To w - 1
                                         If x > 0 Then
                                             Dim leaving = x - radius - 1
                                             If leaving >= 0 Then
                                                 For yy As Integer = yFrom To yTo
                                                     Dim oo = yy * stride + leaving * 4
                                                     histB(srcBuf(oo)) -= 1
                                                     histG(srcBuf(oo + 1)) -= 1
                                                     histR(srcBuf(oo + 2)) -= 1
                                                     count -= 1
                                                 Next
                                             End If
                                             Dim entering = x + radius
                                             If entering <= w - 1 Then
                                                 For yy As Integer = yFrom To yTo
                                                     Dim oo = yy * stride + entering * 4
                                                     histB(srcBuf(oo)) += 1
                                                     histG(srcBuf(oo + 1)) += 1
                                                     histR(srcBuf(oo + 2)) += 1
                                                     count += 1
                                                 Next
                                             End If
                                         End If

                                         Dim centerO = rowOffset + x * 4
                                         Dim mid = count \ 2
                                         dstBuf(centerO) = HistogramMedian(histB, mid)
                                         dstBuf(centerO + 1) = HistogramMedian(histG, mid)
                                         dstBuf(centerO + 2) = HistogramMedian(histR, mid)
                                         dstBuf(centerO + 3) = srcBuf(centerO + 3)
                                     Next
                                 End Sub)
                CommitBgraBuffer(result, dstBuf)
                Return result
            End If

            For y As Integer = 0 To h - 1
                For x As Integer = 0 To w - 1
                    rWindow.Clear() : gWindow.Clear() : bWindow.Clear()
                    Dim alpha = source.GetPixel(x, y).Alpha
                    For yy As Integer = Math.Max(0, y - radius) To Math.Min(h - 1, y + radius)
                        For xx As Integer = Math.Max(0, x - radius) To Math.Min(w - 1, x + radius)
                            Dim c = source.GetPixel(xx, yy)
                            rWindow.Add(c.Red)
                            gWindow.Add(c.Green)
                            bWindow.Add(c.Blue)
                        Next
                    Next
                    rWindow.Sort() : gWindow.Sort() : bWindow.Sort()
                    Dim mid = rWindow.Count \ 2
                    result.SetPixel(x, y, New SKColor(rWindow(mid), gWindow(mid), bWindow(mid), alpha))
                Next
            Next

            Return result
        End Function

        ''' Gemeinsamer Unsharp-Mask-artiger Lokalkontrast-Kern für Clarity/Structure - Unterschied
        ''' zwischen beiden Reglern ist ausschließlich blurSigma (Frequenzband) und strengthMultiplier.
        ''' <summary>Lokaler Kontrast (Unschaerfemaske) - Grundlage von Klarheit und Struktur.
        '''
        ''' Lief bis ueber GetPixel/SetPixel, also mit einem P/Invoke JE PIXEL. Gemessen
        ''' kostete Klarheit dadurch 4,3 s bei 6,3 MP - waehrend die gesamte verschmolzene Farbkette
        ''' 17 ms braucht. Jetzt ueber geliehene Puffer und ForEachRow, wie der Rest der Pipeline.
        '''
        ''' WICHTIG fuer die Bitgleichheit: GetPixel ENTpremultipliziert und SetPixel premultipliziert
        ''' wieder (gemessen: gespeichert (100,50,25,128) liefert GetPixel (199,100,50,128)).
        ''' Ein naiver Umbau auf Rohbytes wuerde deshalb bei teiltransparenten Pixeln ANDERE Ergebnisse
        ''' liefern. Das Verhalten ist unten exakt nachgebildet.</summary>
        Private Shared Function ApplyLocalContrast(source As SKBitmap, blurSigma As Single, amount As Single, strengthMultiplier As Single) As SKBitmap
            Using blurred = ApplyNoiseReduction(source, blurSigma / 8.0F)
                Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)

                Dim srcBuf As Byte() = Nothing, blurBuf As Byte() = Nothing
                Dim sStride, bStride, ri, gi, bi, ai As Integer
                Dim bri, bgi, bbi, bai As Integer
                If Not TryBorrowRgbaLikeBuffer(source, srcBuf, sStride, ri, gi, bi, ai) OrElse
                   Not TryBorrowRgbaLikeBuffer(blurred, blurBuf, bStride, bri, bgi, bbi, bai) Then
                    Return result
                End If

                Dim dstBuf = New Byte(srcBuf.Length - 1) {}
                Dim factor = amount * strengthMultiplier
                Dim width = source.Width

                ForEachRow(width, source.Height,
                    Sub(y)
                        Dim so = y * sStride
                        Dim bo = y * bStride
                        For x = 0 To width - 1
                            Dim o = so + x * 4
                            Dim p = bo + x * 4
                            Dim a = srcBuf(o + ai)
                            If a = 0 Then
                                dstBuf(o) = 0 : dstBuf(o + 1) = 0 : dstBuf(o + 2) = 0 : dstBuf(o + 3) = 0
                                Continue For
                            End If

                            ' Entpremultiplizieren wie GetPixel es tut - sonst weicht das Ergebnis
                            ' bei teiltransparenten Pixeln vom bisherigen Verhalten ab.
                            Dim cr As Integer, cg As Integer, cb As Integer
                            Dim br As Integer, bg As Integer, bb As Integer
                            If a = 255 Then
                                cr = srcBuf(o + ri) : cg = srcBuf(o + gi) : cb = srcBuf(o + bi)
                                br = blurBuf(p + bri) : bg = blurBuf(p + bgi) : bb = blurBuf(p + bbi)
                            Else
                                cr = Math.Min(255, srcBuf(o + ri) * 255 \ a)
                                cg = Math.Min(255, srcBuf(o + gi) * 255 \ a)
                                cb = Math.Min(255, srcBuf(o + bi) * 255 \ a)
                                Dim ba = blurBuf(p + bai)
                                If ba = 0 Then
                                    br = 0 : bg = 0 : bb = 0
                                Else
                                    br = Math.Min(255, blurBuf(p + bri) * 255 \ ba)
                                    bg = Math.Min(255, blurBuf(p + bgi) * 255 \ ba)
                                    bb = Math.Min(255, blurBuf(p + bbi) * 255 \ ba)
                                End If
                            End If

                            Dim nr = ClampToByte(cr + (cr - br) * factor)
                            Dim ng = ClampToByte(cg + (cg - bg) * factor)
                            Dim nb = ClampToByte(cb + (cb - bb) * factor)

                            If a <> 255 Then
                                ' Zurueck nach premultipliziert, wie SetPixel es tut.
                                nr = CByte(Math.Min(CInt(a), CInt(nr) * a \ 255))
                                ng = CByte(Math.Min(CInt(a), CInt(ng) * a \ 255))
                                nb = CByte(Math.Min(CInt(a), CInt(nb) * a \ 255))
                            End If

                            dstBuf(o + ri) = nr
                            dstBuf(o + gi) = ng
                            dstBuf(o + bi) = nb
                            dstBuf(o + ai) = a
                        Next
                    End Sub)

                Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
                Return result
            End Using
        End Function

        Private Shared Function ApplyClarity(source As SKBitmap, amount As Single) As SKBitmap
            ' Clarity = breiter Mitteltonkontrast: bewusst deutlich größerer Blur-Radius als Structure.
            ' Untergrenze so wählen, dass ApplyNoiseReduction einen effektiven Gauß-Sigma > ~1.6 liefert
            ' (blurSigma 5.0 → 0.625 → eff. ~1.6); bei 2.0 war Clarity bei kleinen Stärken fast ein No-Op
            ' und vom feinen Structure-Radius (eff. ~1.24) kaum zu unterscheiden.
            Dim sigma = 5.0F + Math.Abs(amount) * 5.0F
            Return ApplyLocalContrast(source, sigma, amount, 1.6F)
        End Function

        ''' Anders als Clarity (breiter, mit der Stärke wachsender Mitteltonkontrast-Radius): Structure
        ''' arbeitet auf einem festen, kleinen Blur-Radius (feine Textur/Detailkontrast) - vorher rief
        ''' diese Funktion nur ApplyClarity mit abgeschwächter Stärke auf und war dadurch visuell nicht
        ''' von Clarity unterscheidbar, nur schwächer.
        Private Shared Function ApplyStructure(source As SKBitmap, amount As Single) As SKBitmap
            Dim clamped = Clamp(amount, -1, 1)
            ' blurSigma muss über ApplyNoiseReduction einen effektiven Gauß-Sigma > ~1.0 ergeben, sonst
            ' ist SkiaSharps CreateBlur praktisch ein No-Op und Structure wirkungslos (Original minus
            ' unveränderte "Weichzeichnung" = 0). 1.2 lag darunter und machte den Regler tot; 3.6 ergibt
            ' effektiv ~1.24 - klar wirksam, aber weiter kleinerer Radius als Clarity (feinere Textur).
            Return ApplyLocalContrast(source, 3.6F, clamped, 2.4F)
        End Function

        ''' <summary>Dunst. Lief bis ueber GetPixel/SetPixel (P/Invoke je Pixel, gemessen
        ''' 4,3 s bei 6,3 MP); jetzt ueber geliehene Puffer. Die Alpha-Semantik von GetPixel/SetPixel
        ''' ist ueber ReadUnpremultiplied/WritePremultiplied exakt nachgebildet.</summary>
        Private Shared Function ApplyHaze(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return source
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)

            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) Then Return result
            Dim dstBuf = New Byte(srcBuf.Length - 1) {}
            Dim width = source.Width

            ForEachRow(width, source.Height,
                Sub(y)
                    Dim rowOffset = y * stride
                    For x = 0 To width - 1
                        Dim o = rowOffset + x * 4
                        Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                        ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                        Dim nr As Byte, ng As Byte, nb As Byte
                        If strength > 0 Then
                            Dim sv = strength * 0.45F
                            nr = ClampToByte(cr + (255 - cr) * sv)
                            ng = ClampToByte(cg + (255 - cg) * sv)
                            nb = ClampToByte(cb + (255 - cb) * sv)
                        Else
                            Dim sv = -strength
                            Dim contrast = 1.0F + sv * 0.55F
                            nr = ClampToByte((cr - 128) * contrast + 128 - sv * 10)
                            ng = ClampToByte((cg - 128) * contrast + 128 - sv * 10)
                            nb = ClampToByte((cb - 128) * contrast + 128 - sv * 10)
                        End If
                        WritePremultiplied(dstBuf, o, ri, gi, bi, ai, nr, ng, nb, a)
                    Next
                End Sub)

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            Return result
        End Function

        ''' <summary>Leuchten. Wie ApplyHaze von GetPixel/SetPixel auf Puffer umgestellt
        ''' (gemessen 4,5 s bei 6,3 MP).</summary>
        Private Shared Function ApplyImageGlow(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return source
            Using blurred = ApplyNoiseReduction(source, 0.8F)
                Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)

                Dim srcBuf As Byte() = Nothing, blurBuf As Byte() = Nothing
                Dim sStride, bStride, ri, gi, bi, ai As Integer
                Dim bri, bgi, bbi, bai As Integer
                If Not TryBorrowRgbaLikeBuffer(source, srcBuf, sStride, ri, gi, bi, ai) OrElse
                   Not TryBorrowRgbaLikeBuffer(blurred, blurBuf, bStride, bri, bgi, bbi, bai) Then
                    Return result
                End If
                Dim dstBuf = New Byte(srcBuf.Length - 1) {}
                Dim width = source.Width
                Dim positiv = strength > 0
                Dim sv = If(positiv, strength * 0.55F, -strength * 0.55F)

                ForEachRow(width, source.Height,
                    Sub(y)
                        Dim so = y * sStride
                        Dim bo = y * bStride
                        For x = 0 To width - 1
                            Dim o = so + x * 4
                            Dim p = bo + x * 4
                            Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                            Dim br As Integer, bg As Integer, bb As Integer, ba As Integer
                            ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                            ReadUnpremultiplied(blurBuf, p, bri, bgi, bbi, bai, br, bg, bb, ba)
                            Dim nr As Byte, ng As Byte, nb As Byte
                            If positiv Then
                                nr = ClampToByte(cr + br * sv) : ng = ClampToByte(cg + bg * sv) : nb = ClampToByte(cb + bb * sv)
                            Else
                                nr = ClampToByte(cr - br * sv) : ng = ClampToByte(cg - bg * sv) : nb = ClampToByte(cb - bb * sv)
                            End If
                            WritePremultiplied(dstBuf, o, ri, gi, bi, ai, nr, ng, nb, a)
                        Next
                    End Sub)

                Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
                Return result
            End Using
        End Function

        ''' <summary>Staub/Kratzer, Richtung wie bei den Nachbarn im Panel (positiv = den benannten
        ''' Effekt HINZUFUEGEN): positiv streut Staubkoerner und wenige fast senkrechte Kratzer wie
        ''' auf gescanntem Film, negativ ENTFERNT Stoerungen per Medianfilter (vorher war es genau
        ''' umgekehrt und die zufaelligen Querstriche sahen nach nichts aus -).</summary>
        Private Shared Function ApplyDustScratches(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return source

            If strength < 0 Then
                ' ENTFERNEN. Der Median-Radius ist zwangslaeufig ganzzahlig - er laeuft ueber den
                ' vollen Reglerbereich, die Zwischenstufen kommen aus der Deckkraft, mit der das
                ' Medianbild ueber das Original gelegt wird (siehe Regler-Bereichs-Diagnose).
                Dim removeStrength = -strength
                Using median = ApplyMedianBlur(source, removeStrength)
                    Dim blended = CloneBitmap(source)
                    Using canvas = New SKCanvas(blended)
                        Using paint = New SKPaint With {.Color = New SKColor(255, 255, 255, ClampToByte(255.0F * removeStrength))}
                            canvas.DrawBitmap(median, 0, 0, paint)
                        End Using
                    End Using
                    Return blended
                End Using
            End If

            ' HINZUFUEGEN: ueberwiegend helle Staubkoerner (Film: Staub streut Licht) plus wenige
            ' lange, fast senkrechte, leicht gewellte Kratzer - senkrecht, weil echte Kratzer vom
            ' Filmtransport in Laufrichtung entstehen. Fester Seed: gleiches Bild -> gleiches Muster.
            Dim result = CloneBitmap(source)
            Dim random = New Random(source.Width * 997 Xor source.Height * 331)
            Using canvas = New SKCanvas(result)
                Dim speckCount = CInt(Math.Round(source.Width * source.Height / 4500.0 * strength))
                For i = 0 To speckCount - 1
                    Dim bright = random.NextDouble() < 0.72
                    Dim tone = If(bright, CByte(210 + random.Next(0, 46)), CByte(random.Next(0, 40)))
                    Dim alpha = CByte(60 + random.Next(0, CInt(70 + 90 * strength)))
                    Using paint = New SKPaint With {.Color = New SKColor(tone, tone, tone, alpha), .IsAntialias = True}
                        canvas.DrawCircle(random.Next(0, source.Width), random.Next(0, source.Height),
                                          0.5F + CSng(random.NextDouble()) * 1.1F, paint)
                    End Using
                Next

                Dim scratchCount = Math.Max(1, CInt(Math.Round(9.0 * strength)))
                For i = 0 To scratchCount - 1
                    Dim x = CSng(random.NextDouble() * source.Width)
                    Dim y = CSng(random.NextDouble() * source.Height * 0.6)
                    Dim length = CSng(source.Height * (0.15 + random.NextDouble() * 0.35))
                    Dim bright = random.NextDouble() < 0.65
                    Dim tone = If(bright, CByte(225), CByte(25))
                    Dim alpha = CByte(35 + random.Next(0, CInt(30 + 60 * strength)))
                    Using path = New SKPath()
                        path.MoveTo(x, y)
                        Dim segments = Math.Max(3, CInt(length / 14.0F))
                        For seg = 1 To segments
                            ' Leichte seitliche Wanderung - ein schnurgerader Strich wirkt kuenstlich.
                            x += CSng((random.NextDouble() - 0.5) * 2.4)
                            path.LineTo(x, y + length * seg / segments)
                        Next
                        Using paint = New SKPaint With {.Color = New SKColor(tone, tone, tone, alpha),
                                                        .Style = SKPaintStyle.Stroke, .StrokeWidth = 1.0F, .IsAntialias = True}
                            canvas.DrawPath(path, paint)
                        End Using
                    End Using
                Next
            End Using
            Return result
        End Function



        Private Shared Function ResolveFilmNegativeStats(source As SKBitmap, adj As ImageAdjustments) As (BaseColor As SKColor, DensityColor As SKColor)
            Dim hasBase = Not String.IsNullOrWhiteSpace(adj.NegativeBaseColor)
            Dim hasDensity = Not String.IsNullOrWhiteSpace(adj.NegativeDensityColor)
            If hasBase AndAlso hasDensity Then
                Return (ParseColor(adj.NegativeBaseColor, SKColors.White), ParseColor(adj.NegativeDensityColor, SKColors.Black))
            End If

            ' Normalerweise misst der Editor einmal beim Einschalten und legt die Werte in den
            ' Anpassungen ab - dann sind Vorschau und Export garantiert identisch. Kommen hier trotzdem
            ' leere Werte an (Stapelverarbeitung, wiederhergestellte Anpassungen), wird eben aus dem
            ' Bild geschätzt, das gerade vorliegt.
            Dim measured = AnalyzeFilmNegativeCore(source)
            Return (If(hasBase, ParseColor(adj.NegativeBaseColor, measured.BaseColor), measured.BaseColor),
                    If(hasDensity, ParseColor(adj.NegativeDensityColor, measured.DensityColor), measured.DensityColor))
        End Function

        ''' <summary>Schätzt Filmbasis und dichtesten Punkt eines Negativscans. Misst auf dem BESCHNITTENEN
        ''' Bild, weil der Beschnitt in der Pipeline vor der Umkehr liegt: ein weggeschnittener schwarzer
        ''' Scannerrand darf den Dichtepunkt nicht mehr bestimmen.</summary>
        Public Shared Function AnalyzeFilmNegative(source As SKBitmap, adj As ImageAdjustments) As (BaseColor As SKColor, DensityColor As SKColor)
            If source Is Nothing Then Return (SKColors.White, SKColors.Black)
            Dim cropped = ApplyCrop(source, If(adj, New ImageAdjustments()))
            Try
                Return AnalyzeFilmNegativeCore(cropped)
            Finally
                If Not Object.ReferenceEquals(cropped, source) Then cropped?.Dispose()
            End Try
        End Function

        ''' <summary>Filmbasis = das hellste Tonwertniveau je Kanal (unbelichteter Träger), dichtester Punkt
        ''' = das dunkelste. Beides als Perzentil statt als Min/Max: ein einzelnes Staubkorn oder ein Kratzer
        ''' würde sonst die gesamte Umrechnung des Bildes festlegen.</summary>
        Private Shared Function AnalyzeFilmNegativeCore(bmp As SKBitmap) As (BaseColor As SKColor, DensityColor As SKColor)
            Dim histR = New Integer(255) {}
            Dim histG = New Integer(255) {}
            Dim histB = New Integer(255) {}
            Dim total As Integer = 0

            Dim buffer As Byte() = Nothing
            Dim stride As Integer = 0
            If bmp IsNot Nothing AndAlso TryBorrowBgraBuffer(bmp, buffer, stride) Then
                ' Perzentile sind ab gut hunderttausend Stichproben stabil - jedes Pixel eines 40-MP-Scans
                ' anzufassen würde die Messung nur verlangsamen, nicht verbessern.
                Dim stepPx = Math.Max(1, CInt(Math.Sqrt(bmp.Width * CDbl(bmp.Height) / 250000.0)))
                For y As Integer = 0 To bmp.Height - 1 Step stepPx
                    Dim row = y * stride
                    For x As Integer = 0 To bmp.Width - 1 Step stepPx
                        Dim o = row + x * 4
                        If buffer(o + 3) < 8 Then Continue For
                        histB(buffer(o)) += 1
                        histG(buffer(o + 1)) += 1
                        histR(buffer(o + 2)) += 1
                        total += 1
                    Next
                Next
            End If

            If total = 0 Then Return (SKColors.White, SKColors.Black)
            Dim baseColor = New SKColor(HistogramPercentile(histR, total, 0.995), HistogramPercentile(histG, total, 0.995), HistogramPercentile(histB, total, 0.995), 255)
            Dim densityColor = New SKColor(HistogramPercentile(histR, total, 0.005), HistogramPercentile(histG, total, 0.005), HistogramPercentile(histB, total, 0.005), 255)
            Return (baseColor, densityColor)
        End Function

        ''' <summary>Kleinster Tonwert, unterhalb dessen <paramref name="fraction"/> aller gezählten Pixel liegen.</summary>
        Private Shared Function HistogramPercentile(histogram As Integer(), total As Integer, fraction As Double) As Byte
            Dim target = Math.Max(1L, Math.Min(CLng(total), CLng(Math.Round(total * fraction))))
            Dim running As Long = 0
            For i As Integer = 0 To 255
                running += histogram(i)
                If running >= target Then Return CByte(i)
            Next
            Return 255
        End Function


        ' Parst "x1,y1;x2,y2;..." zu sortierten, X-eindeutigen Stützpunkten (0..255) für die Tonwertkurve.
        ' Friend, damit die Preset-Faltung (XmpPresetService.ApplyParametricCurve) die Punktkurve
        ' mit demselben Spline auswertet wie die Engine - frueher lief die Faltung ueber eine eigene
        ' LINEARE Naeherung, die die Kurvenkruemmung verwarf.
        Friend Shared Function ParseCurvePoints(pointsCsv As String) As List(Of (X As Double, Y As Double))
            Dim result As New List(Of (X As Double, Y As Double))()
            If Not String.IsNullOrWhiteSpace(pointsCsv) Then
                For Each pair In pointsCsv.Split(";"c)
                    Dim parts = pair.Split(","c)
                    If parts.Length = 2 Then
                        Dim x As Double
                        Dim y As Double
                        If Double.TryParse(parts(0), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, x) AndAlso
                           Double.TryParse(parts(1), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, y) Then
                            result.Add((Math.Max(0.0, Math.Min(255.0, x)), Math.Max(0.0, Math.Min(255.0, y))))
                        End If
                    End If
                Next
            End If
            result = result.GroupBy(Function(p) p.X).Select(Function(g) g.First()).OrderBy(Function(p) p.X).ToList()
            If result.Count = 0 Then
                result.Add((0, 0))
                result.Add((255, 255))
            ElseIf result.Count = 1 Then
                If result(0).X > 0.0001 Then
                    result.Insert(0, (0, 0))
                Else
                    result.Add((255, 255))
                End If
            End If
            If result(0).X > 0.0001 Then result.Insert(0, (0, result(0).Y))
            If result(result.Count - 1).X < 254.9999 Then result.Add((255, result(result.Count - 1).Y))
            Return result
        End Function


        ''' <summary>Catmull-Rom durch die Kurvenpunkte. GEPRUEFT UND VERWORFEN: Adobes DNG-SDK
        ''' interpoliert Tonkurven mit einem NATUERLICHEN kubischen Spline, der an derselben
        ''' Kennlinie systematisch hoeher laeuft (Rot-Kurve des Testpresets bei x=20: 7,7 gegen
        ''' unsere 6,5). Ein A/B mit ausgetauschter Interpolation, sonst identischer Kette, ergab
        ''' auf der besseren Basis eine GROESSERE Abweichung zur Referenz (8,25 -> 9,39 am einen
        ''' Motiv, 8,08 -> 9,30 am anderen) und nur auf der heutigen Basis eine kleinere
        ''' (16,85 -> 15,93). Widerspruechlich, also nicht umgestellt - erst wenn ein Referenz-
        ''' Export OHNE Preset vorliegt, laesst sich das sauber entscheiden.</summary>
        Friend Shared Function EvaluateCurveSpline(points As List(Of (X As Double, Y As Double)), x As Double) As Double
            Dim n = points.Count
            If n = 0 Then Return x
            If x <= points(0).X Then Return points(0).Y
            If x >= points(n - 1).X Then Return points(n - 1).Y

            Dim segIndex = 0
            For i As Integer = 0 To n - 2
                If x >= points(i).X AndAlso x <= points(i + 1).X Then
                    segIndex = i
                    Exit For
                End If
            Next

            Dim p0 = If(segIndex > 0, points(segIndex - 1), points(segIndex))
            Dim p1 = points(segIndex)
            Dim p2 = points(segIndex + 1)
            Dim p3 = If(segIndex + 2 < n, points(segIndex + 2), points(segIndex + 1))

            Dim span = p2.X - p1.X
            If span <= 0.0001 Then Return p1.Y
            Dim t = (x - p1.X) / span
            Dim t2 = t * t
            Dim t3 = t2 * t

            Return 0.5 * ((2 * p1.Y) +
                          (-p0.Y + p2.Y) * t +
                          (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                          (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)
        End Function

        ' Rohe Histogramm-Zähldaten je Kanal (R/G/B/Luminanz) für den Kurven-Editor.
        Public Shared Function BuildChannelHistogramCounts(source As SKBitmap) As (R As Integer(), G As Integer(), B As Integer(), L As Integer())
            Dim r = New Integer(255) {}
            Dim g = New Integer(255) {}
            Dim b = New Integer(255) {}
            Dim l = New Integer(255) {}
            If source Is Nothing Then Return (r, g, b, l)

            Dim stepX = Math.Max(1, source.Width \ 400)
            Dim stepY = Math.Max(1, source.Height \ 400)
            For y As Integer = 0 To source.Height - 1 Step stepY
                For x As Integer = 0 To source.Width - 1 Step stepX
                    Dim c = source.GetPixel(x, y)
                    r(c.Red) += 1
                    g(c.Green) += 1
                    b(c.Blue) += 1
                    Dim lum = CInt(Math.Max(0, Math.Min(255, c.Red * 0.299 + c.Green * 0.587 + c.Blue * 0.114)))
                    l(lum) += 1
                Next
            Next
            Return (r, g, b, l)
        End Function

        Public Shared Function BuildChannelHistogramCounts(sourcePath As String) As (R As Integer(), G As Integer(), B As Integer(), L As Integer())
            Try
                Using original = DecodeHistogramSource(sourcePath)
                    Return BuildChannelHistogramCounts(original)
                End Using
            Catch
                Return (New Integer(255) {}, New Integer(255) {}, New Integer(255) {}, New Integer(255) {})
            End Try
        End Function



        ''' <summary>ANKER-Farbtöne der acht HSL-Bänder (Grad), Reihenfolge Rot..Magenta. Adobes
        ''' HSL-Modell interpoliert die Regler GLATT zwischen diesen Ankern: ein Pixel genau auf einem
        ''' Anker bekommt den vollen Bandwert, dazwischen eine weiche Mischung der beiden Nachbaranker.
        ''' Frueher lagen hier Band-GRENZEN mit flachem Kern + 10 Grad Rand-Blending - das ueberdehnte
        ''' jedes Band (voller Wert ueber den ganzen Kern), waehrend Adobe schon ab dem Anker abfaellt.
        ''' Rot ueberlaeuft den Nullpunkt: nach Magenta (315) folgt Rot (0 bzw. 360).</summary>
        Private Shared ReadOnly HslBandCenters As Double() = {0.0, 30.0, 60.0, 120.0, 180.0, 225.0, 270.0, 315.0}

        Private Shared Sub GetHslBandValues(index As Integer, adj As ImageAdjustments,
                                            ByRef hueShift As Single, ByRef satShift As Single, ByRef lumShift As Single)
            Select Case index
                Case 0 : hueShift = adj.RedHue : satShift = adj.RedSaturation : lumShift = adj.RedLuminance
                Case 1 : hueShift = adj.OrangeHue : satShift = adj.OrangeSaturation : lumShift = adj.OrangeLuminance
                Case 2 : hueShift = adj.YellowHue : satShift = adj.YellowSaturation : lumShift = adj.YellowLuminance
                Case 3 : hueShift = adj.GreenHue : satShift = adj.GreenSaturation : lumShift = adj.GreenLuminance
                Case 4 : hueShift = adj.AquaHue : satShift = adj.AquaSaturation : lumShift = adj.AquaLuminance
                Case 5 : hueShift = adj.BlueHue : satShift = adj.BlueSaturation : lumShift = adj.BlueLuminance
                Case 6 : hueShift = adj.PurpleHue : satShift = adj.PurpleSaturation : lumShift = adj.PurpleLuminance
                Case Else : hueShift = adj.MagentaHue : satShift = adj.MagentaSaturation : lumShift = adj.MagentaLuminance
            End Select
        End Sub

        ''' <summary>Die Regler des Farbbands, in dem der Farbton liegt - an den Bandgrenzen WEICH in
        ''' das Nachbarband übergeblendet.
        '''
        ''' Vorher wählte diese Funktion hart per Select Case. Das machte das Ergebnis genau auf einer
        ''' Grenze unstetig: gemessen ergaben (255,191,0) und (255,192,0) - ein Tonwert
        ''' Unterschied im Grünkanal, Farbton 44,94 gegen 45,18 - einen Sprung von 153 Tonwerten,
        ''' sobald die Nachbarbänder verschieden eingestellt waren. Im Bild war das eine sichtbare
        ''' harte Kante quer durch jeden weichen Farbverlauf, der eine Bandgrenze kreuzt (Himmel,
        ''' Hauttöne). Aufgefallen ist es, weil derselbe Farbton in Single und Double auf verschiedene
        ''' Seiten der Grenze fiel - das war aber nur der Bote, nicht die Ursache.
        '''
        ''' Auf der Grenze selbst mischen beide Bänder je zur Hälfte, der Verlauf dorthin ist
        ''' smoothstep-geglättet (Ableitung an beiden Enden null, sonst wäre die Kante nur verschoben
        ''' statt beseitigt).</summary>
        Private Shared Sub GetHslBandAdjustments(hue As Double, adj As ImageAdjustments, ByRef hueShift As Single, ByRef satShift As Single, ByRef lumShift As Single)
            Dim h = ((hue Mod 360.0) + 360.0) Mod 360.0

            ' Anker-Paar finden, zwischen dem der Farbton liegt (Kreis: nach Magenta 315 folgt Rot 360).
            Dim lo = 7
            For k = 0 To 7
                If h < HslBandCenters(k) Then
                    lo = (k + 7) Mod 8          ' voriger Anker
                    Exit For
                End If
            Next
            Dim hi = (lo + 1) Mod 8

            ' Kreis-Distanz vom unteren Anker und Spannweite zum oberen (beide ueber 360 gewickelt).
            Dim span = HslBandCenters(hi) - HslBandCenters(lo)
            If span <= 0.0 Then span += 360.0
            Dim pos = h - HslBandCenters(lo)
            If pos < 0.0 Then pos += 360.0

            ' KERN VOLL, nur um die Bandgrenze herum ueberblenden. Ohne den Kern lief die Kurve
            ' vom einen Anker glatt zum naechsten durch, und volle Wirkung hatte ein Regler NUR
            ' genau auf seinem Ankerfarbton - gemessen auf 11 Prozent des Farbkreises. Ein Laub bei
            ' Farbton 93 bekam vom Gruen-Regler 0,58, ein Himmel bei 208 vom Blau-Regler 0,69: der
            ' Regler machte dort knapp die Haelfte dessen, was draufsteht. Mit dem Kern hat jedes
            ' Band auf seinem inneren Bereich die volle Wirkung (55 Prozent des Farbkreises), die
            ' Ueberblendung passiert im mittleren Bereich zwischen zwei Ankern. Die Kernbreite ist
            ' gemessen: 0,25 / 0,30 / 0,35 brachten an echten Bildfarben 68/70/70 Prozent mehr
            ' Chroma bei Laub und 62/68/72 bei Nadelgruen - ueber 0,30 hinaus wird nur noch der
            ' Uebergang schmaler, ohne dass die Wirkung nennenswert steigt.
            '
            ' Die Ableitung bleibt an BEIDEN Enden null (smoothstep auf dem gestauchten Bereich,
            ' danach konstant) - genau darum ging es beim Umbau von den harten Grenzen weg: keine
            ' Kante quer durch einen Verlauf, der eine Bandgrenze kreuzt.
            Const kern As Double = 0.30
            Dim t = If(span > 0.0, pos / span, 0.0)
            t = (t - kern) / (1.0 - 2.0 * kern)
            If t < 0.0 Then t = 0.0 Else If t > 1.0 Then t = 1.0
            Dim w = CSng(t * t * (3.0 - 2.0 * t))

            Dim h0, s0, l0, h1, s1, l1 As Single
            GetHslBandValues(lo, adj, h0, s0, l0)
            GetHslBandValues(hi, adj, h1, s1, l1)
            hueShift = h0 + (h1 - h0) * w
            satShift = s0 + (s1 - s0) * w
            lumShift = l0 + (l1 - l0) * w
        End Sub




        ''' <summary>Die Farbmatrix eines Presets - einzige Quelle für ApplyFilterPreset UND die
        ''' verschmolzene Punktoperationskette. Nothing heißt "keine Matrix": unbekanntes Preset oder
        ''' "weich" (das ist ein Weichzeichner, kein Farbfilter).
        ''' Skia liest die 5. Matrixspalte (Offset) in der Skala 0..1, NICHT 0..255 - gemessen
        ''': Offset 0.1 auf Grau 100 ergibt 126, also +25.5 Tonwerte. Die Offsets unten
        ''' sind aber als TONWERTE gemeint. Ohne die Division waren fuenf Presets unbrauchbar:
        ''' "Fade"/"Vintage" lieferten reines Weiss, "Kontrast" reines Schwarz, "Warm"/"Kuehl"
        ''' knallorange bzw. knallblau. Die Zahlen bleiben in Tonwerten lesbar, geteilt wird hier.
        ''' </summary>
        Friend Shared Function BuildFilterPresetMatrix(preset As String) As Single()
            Dim matrix As Single() = Nothing
            Select Case preset.Trim().ToLowerInvariant()
                Case "s/w", "schwarzweiss", "schwarzweiß"
                    matrix = New Single() {
                        0.299F, 0.587F, 0.114F, 0, 0,
                        0.299F, 0.587F, 0.114F, 0, 0,
                        0.299F, 0.587F, 0.114F, 0, 0,
                        0, 0, 0, 1, 0
                    }
                Case "warm"
                    ' Werte angezogen: bei 50 % Standardstaerke war der Look zuvor
                    ' praktisch unsichtbar (Kanalshift 12) - siehe Kommentar an DefaultFilterStrength.
                    matrix = New Single() {
                        1.12F, 0.02F, 0, 0, 8.0F / 255.0F,
                        0, 1.03F, 0, 0, 2.0F / 255.0F,
                        0, 0, 0.88F, 0, -10.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "kühl", "kuehl"
                    matrix = New Single() {
                        0.92F, 0, 0, 0, -4.0F / 255.0F,
                        0, 1.01F, 0, 0, 0,
                        0, 0, 1.1F, 0, 8.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "fade"
                    ' Werte angezogen (vorher Kanalshift 10, unsichtbar): ein "Fade"
                    ' lebt vom angehobenen Schwarzpunkt - Koeffizienten runter, Offset deutlich rauf.
                    matrix = New Single() {
                        0.80F, 0.04F, 0.04F, 0, 30.0F / 255.0F,
                        0.04F, 0.80F, 0.04F, 0, 30.0F / 255.0F,
                        0.04F, 0.04F, 0.82F, 0, 34.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "kontrast"
                    matrix = New Single() {
                        1.16F, 0, 0, 0, -18.0F / 255.0F,
                        0, 1.16F, 0, 0, -18.0F / 255.0F,
                        0, 0, 1.16F, 0, -18.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "sepia"
                    matrix = New Single() {
                        0.393F, 0.769F, 0.189F, 0, 0,
                        0.349F, 0.686F, 0.168F, 0, 0,
                        0.272F, 0.534F, 0.131F, 0, 0,
                        0, 0, 0, 1, 0
                    }
                Case "matt"
                    ' Werte angezogen (vorher Kanalshift 15). Matt = flacher Kontrast mit
                    ' leicht warmem Grundton, deutlicher abgesetzt von "Fade" (neutral).
                    matrix = New Single() {
                        0.84F, 0.06F, 0.04F, 0, 26.0F / 255.0F,
                        0.04F, 0.84F, 0.04F, 0, 24.0F / 255.0F,
                        0.04F, 0.06F, 0.80F, 0, 20.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "cross"
                    ' Werte angezogen (vorher Kanalshift 15). Kreuzentwicklung lebt von
                    ' GEGENLAEUFIGEN Kanaelen: Lichter ins Gruengelbe, Schatten ins Blaue.
                    matrix = New Single() {
                        1.22F, 0, 0, 0, -16.0F / 255.0F,
                        0, 1.06F, 0, 0, 6.0F / 255.0F,
                        0, 0, 0.82F, 0, 26.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "dramatisch"
                    matrix = New Single() {
                        1.24F, 0, 0, 0, -28.0F / 255.0F,
                        0, 1.18F, 0, 0, -24.0F / 255.0F,
                        0, 0, 1.12F, 0, -18.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "weich"
                    ' Echte räumliche Unschärfe statt nur einer Farbmatrix - eine Farbmatrix wirkt pro
                    ' Pixel und kann strukturell nicht weichzeichnen. Steht nur im selben Select Case,
                    ' ist aber KEIN Farbfilter: der Aufrufer fängt es vor der Matrix ab.
                    Return Nothing
                Case "noir"
                    matrix = New Single() {
                        0.404F, 0.792F, 0.154F, 0, -38.0F / 255.0F,
                        0.404F, 0.792F, 0.154F, 0, -38.0F / 255.0F,
                        0.404F, 0.792F, 0.154F, 0, -38.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "duoton", "duotone"
                    ' Echter Zweifarb-Verlauf über die Luminanz: dunkles Indigo (Schatten) zu sattem Orange (Lichter).
                    ' Jeder Pixel landet exakt auf der Verlaufslinie zwischen den beiden Zielfarben - unabhängig
                    ' vom Ausgangston. Endpunkte sind bewusst kräftig/gesättigt, nicht Richtung Weiß.
                    matrix = New Single() {
                        0.2815F, 0.5525F, 0.1073F, 0, 15.0F / 255.0F,
                        0.1759F, 0.3453F, 0.0671F, 0, 15.0F / 255.0F,
                        -0.0235F, -0.0460F, -0.0089F, 0, 60.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "polaroid"
                    ' Kräftiger warmer Gelbstich mit moderat angehobenen Schwarzwerten (Sofortbild-Charakter),
                    ' Kontrast bleibt weitgehend erhalten statt komplett verwaschen wie bei "Fade".
                    matrix = New Single() {
                        0.95F, 0.15F, -0.05F, 0, 10.0F / 255.0F,
                        0.05F, 0.90F, 0.05F, 0, 6.0F / 255.0F,
                        -0.05F, 0.05F, 0.75F, 0, 2.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "vhs"
                    ' Deutlich kühlerer Cyan-/Blaustich mit sichtbarem Kanal-Bluten, typisch für Analogvideo.
                    matrix = New Single() {
                        0.70F, 0.15F, 0.15F, 0, 0,
                        0.10F, 0.85F, 0.15F, 0, 4.0F / 255.0F,
                        0.05F, 0.20F, 0.85F, 0, 10.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case "bild auf alt", "alt", "antik", "vintage"
                    matrix = New Single() {
                        0.78F, 0.26F, 0.08F, 0, 18.0F / 255.0F,
                        0.18F, 0.74F, 0.10F, 0, 10.0F / 255.0F,
                        0.06F, 0.18F, 0.62F, 0, 2.0F / 255.0F,
                        0, 0, 0, 1, 0
                    }
                Case Else
                    Return Nothing
            End Select
            Return matrix
        End Function

        ''' Blendet eine leicht gaußgeweichzeichnete Kopie über das scharfe Original - der Radius
        ''' bleibt bewusst klein/fest ("leicht"), die Stärke steuert nur die Überblend-Deckkraft.
        Private Shared Function ApplySoftFocusBlur(source As SKBitmap, strength As Single) As SKBitmap
            strength = Clamp(strength, 0, 1)
            If strength <= 0 Then Return source
            Using blurred = ApplyNoiseReduction(source, 0.3F)
                Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
                Using canvas = New SKCanvas(result)
                    canvas.DrawBitmap(source, 0, 0)
                    Using paint = New SKPaint With {.Color = New SKColor(255, 255, 255, ClampToByte(255 * strength))}
                        canvas.DrawBitmap(blurred, 0, 0, paint)
                    End Using
                End Using
                Return result
            End Using
        End Function

        Private Class Lut3DData
            Public Property Size As Integer
            ''' Flach abgelegt, R am schnellsten laufend (Standard-.cube-Reihenfolge): Index = (b*Size*Size + g*Size + r)*3 + Kanal.
            Public Property Table As Single()
        End Class

        Private Shared ReadOnly _cubeLutCache As New Dictionary(Of String, Lut3DData)(StringComparer.OrdinalIgnoreCase)
        Private Shared ReadOnly _cubeLutCacheLock As New Object()

        ''' Lädt und parst eine .cube-3D-LUT-Datei (nur LUT_3D_SIZE wird unterstützt, kein LUT_1D_SIZE,
        ''' Domain wird als 0..1 angenommen). Ergebnis wird pro Dateipfad gecacht, da eine LUT beim
        ''' Ziehen an einem Stärke-Regler sonst bei jedem Preview-Frame neu von der Platte geparst würde.
        ''' Speicher- (SaveImage) und Vorschau-Rendering (ApplyAdjustments) können diese Methode
        ''' gleichzeitig von verschiedenen Threads aufrufen, daher SyncLock um das gesamte Dictionary.
        Private Shared Function LoadCubeLut(path As String) As Lut3DData
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return Nothing

            SyncLock _cubeLutCacheLock
                Dim cached As Lut3DData = Nothing
                If _cubeLutCache.TryGetValue(path, cached) Then Return cached
                Return LoadCubeLutUnlocked(path)
            End SyncLock
        End Function

        Private Shared Function LoadCubeLutUnlocked(path As String) As Lut3DData
            Dim size As Integer = 0
            Dim values As New List(Of Single)()
            Try
                For Each rawLine In File.ReadLines(path)
                    Dim line = rawLine.Trim()
                    If line.Length = 0 OrElse line.StartsWith("#") Then Continue For
                    If line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase) Then
                        Dim parts = line.Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                        If parts.Length >= 2 Then Integer.TryParse(parts(1), Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, size)
                        Continue For
                    End If
                    If Not Char.IsDigit(line(0)) AndAlso line(0) <> "-"c AndAlso line(0) <> "+"c AndAlso line(0) <> "."c Then Continue For

                    Dim comps = line.Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                    If comps.Length < 3 Then Continue For
                    Dim r, g, b As Single
                    If Single.TryParse(comps(0), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, r) AndAlso
                       Single.TryParse(comps(1), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, g) AndAlso
                       Single.TryParse(comps(2), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, b) Then
                        values.Add(r)
                        values.Add(g)
                        values.Add(b)
                    End If
                Next
            Catch
                ' Unlesbare Datei: wie ein Parse-Fehler behandeln (negativ cachen, s.u.).
                size = 0
            End Try

            If size < 2 OrElse values.Count <> size * size * size * 3 Then
                ' Fehlschlag NEGATIV cachen: ohne Eintrag wurde eine defekte
                ' .cube-Datei beim Ziehen am LUT-Stärke-Regler mit JEDEM Vorschau-Frame neu
                ' geparst. TryGetValue liefert für den Nothing-Eintrag True -> Aufrufer sehen
                ' weiterhin "keine LUT". Gilt (wie der Positiv-Cache) bis zum Programmende -
                ' eine extern reparierte Datei braucht einen Neustart bzw. Neu-Auswahl.
                _cubeLutCache(path) = Nothing
                Return Nothing
            End If

            Dim data = New Lut3DData With {.Size = size, .Table = values.ToArray()}
            _cubeLutCache(path) = data
            Return data
        End Function

        Private Shared Function LutChannel(table As Single(), size As Integer, r As Integer, g As Integer, b As Integer, channel As Integer) As Single
            Return table((b * size * size + g * size + r) * 3 + channel)
        End Function

        Private Shared Function TrilinearChannel(table As Single(), size As Integer, r0 As Integer, r1 As Integer, g0 As Integer, g1 As Integer, b0 As Integer, b1 As Integer, rt As Single, gt As Single, bt As Single, channel As Integer) As Single
            Dim c000 = LutChannel(table, size, r0, g0, b0, channel)
            Dim c100 = LutChannel(table, size, r1, g0, b0, channel)
            Dim c010 = LutChannel(table, size, r0, g1, b0, channel)
            Dim c110 = LutChannel(table, size, r1, g1, b0, channel)
            Dim c001 = LutChannel(table, size, r0, g0, b1, channel)
            Dim c101 = LutChannel(table, size, r1, g0, b1, channel)
            Dim c011 = LutChannel(table, size, r0, g1, b1, channel)
            Dim c111 = LutChannel(table, size, r1, g1, b1, channel)

            Dim c00 = c000 + (c100 - c000) * rt
            Dim c10 = c010 + (c110 - c010) * rt
            Dim c01 = c001 + (c101 - c001) * rt
            Dim c11 = c011 + (c111 - c011) * rt
            Dim c0 = c00 + (c10 - c00) * gt
            Dim c1 = c01 + (c11 - c01) * gt
            Return c0 + (c1 - c0) * bt
        End Function



        ''' <summary>Wendet den gewählten Vignetten-Stil auf ein abzudunkelndes Pixel an. ColorPriority
        ''' ist bitgenau das frühere multiplikative Abdunkeln (Farbton bleibt). HighlightPriority schont
        ''' helle Bereiche (Lichter bleiben stehen), PaintOverlay dunkelt ab UND entsättigt zu einem
        ''' flachen, grauen Verlauf. mix ist 0..~1 (Stärke am Pixel).</summary>
        Private Shared Sub VignetteDarken(rr As Single, gg As Single, bb As Single, mix As Single, style As VignetteStyle,
                                          ByRef outR As Byte, ByRef outG As Byte, ByRef outB As Byte)
            Select Case style
                Case VignetteStyle.HighlightPriority
                    ' Helle Pixel weniger abdunkeln: der Schutz wächst mit dem Quadrat der Luminanz.
                    Dim luma = (0.299F * rr + 0.587F * gg + 0.114F * bb) / 255.0F
                    Dim factor = 1.0F - mix * (1.0F - luma * luma * 0.7F)
                    outR = ClampToByte(rr * factor)
                    outG = ClampToByte(gg * factor)
                    outB = ClampToByte(bb * factor)
                Case VignetteStyle.PaintOverlay
                    ' Erst zur Luminanz hin entsättigen, dann flach abdunkeln - grauer Wasch-Look.
                    Dim luma = 0.299F * rr + 0.587F * gg + 0.114F * bb
                    Dim desat = mix * 0.5F
                    Dim dark = 1.0F - mix
                    outR = ClampToByte((rr + (luma - rr) * desat) * dark)
                    outG = ClampToByte((gg + (luma - gg) * desat) * dark)
                    outB = ClampToByte((bb + (luma - bb) * desat) * dark)
                Case Else ' ColorPriority: bisheriges Verhalten
                    outR = ClampToByte(rr * (1 - mix))
                    outG = ClampToByte(gg * (1 - mix))
                    outB = ClampToByte(bb * (1 - mix))
            End Select
        End Sub

        Private Shared Function ApplyVignette(source As SKBitmap, amount As Single, transition As Single, roundness As Single, feather As Single, centerXPercent As Single, centerYPercent As Single, style As VignetteStyle) As SKBitmap
            ''' Obergrenze 1.5 statt 1 - der Stärke-Regler in EffectsPanel.axaml geht bis ±150, was hier
            ''' zu amount=±1.5 wird (amount/100 im Aufrufer). Bei Clamp(...,0,1) hatte das letzte Drittel
            ''' des Reglerwegs (100..150) keinerlei sichtbaren Effekt mehr (toter Reglerbereich).
            Dim strength = Clamp(Math.Abs(amount), 0, 1.5)
            If strength <= 0 Then Return source

            Dim result = CloneBitmap(source)

            Dim cx = source.Width * Clamp(centerXPercent, 0, 100) / 100.0F
            Dim cy = source.Height * Clamp(centerYPercent, 0, 100) / 100.0F
            Dim roundedness = Clamp(roundness, -100, 100) / 100.0F
            Dim radiusX = source.Width * (0.52F - Math.Min(0, roundedness) * 0.18F)
            Dim radiusY = source.Height * (0.52F + Math.Max(0, roundedness) * 0.18F)
            Dim inner = 0.2F + Clamp(transition, 0, 100) / 100.0F * 0.55F
            Dim softness = 0.04F + Clamp(feather, 0, 100) / 100.0F * 0.42F
            Dim edgeAlpha = If(amount > 0, 255.0F, 220.0F) * strength
            Dim darken = amount > 0

            Dim srcBuf As Byte() = Nothing
            Dim stride As Integer = 0
            If TryBorrowBgraBuffer(result, srcBuf, stride) Then
                Dim dstBuf = CType(srcBuf.Clone(), Byte())
                ForEachRow(result.Width, result.Height, Sub(y)
                                                            Dim rowOffset = y * stride
                                                            For x = 0 To result.Width - 1
                                                                Dim dx = (x - cx) / Math.Max(1.0F, radiusX)
                                                                Dim dy = (y - cy) / Math.Max(1.0F, radiusY)
                                                                Dim distance = CSng(Math.Sqrt(dx * dx + dy * dy))
                                                                Dim t = Clamp((distance - inner) / softness, 0, 1)
                                                                If t <= 0 Then Continue For
                                                                t = t * t * (3.0F - 2.0F * t)

                                                                Dim o = rowOffset + x * 4
                                                                Dim bB = srcBuf(o)
                                                                Dim gG = srcBuf(o + 1)
                                                                Dim rR = srcBuf(o + 2)
                                                                Dim mix = t * edgeAlpha / 255.0F
                                                                If darken Then
                                                                    VignetteDarken(rR, gG, bB, mix, style, dstBuf(o + 2), dstBuf(o + 1), dstBuf(o))
                                                                Else
                                                                    ' Aufhellen "v + (255-v)*mix" gilt fuer STRAIGHT-Werte. Auf den
                                                                    ' PREMUL-Bytes gerechnet entstand bei Teiltransparenz (Radierer-
                                                                    ' Loecher) Farbe > Alpha - ungueltiges Premul, zu starke
                                                                    ' Aufhellung, und der GetPixel-Fallback unten rechnete anders
                                                                    '. Der Zielweiss-Wert im Premul-Raum ist
                                                                    ' schlicht das Alpha selbst: v + (a-v)*mix. Bei a=255 bitgleich
                                                                    ' zur alten Formel; Abdunkeln (Multiplikation) ist premul-korrekt.
                                                                    Dim aA = CSng(srcBuf(o + 3))
                                                                    dstBuf(o) = ClampToByte(bB + (aA - bB) * mix)
                                                                    dstBuf(o + 1) = ClampToByte(gG + (aA - gG) * mix)
                                                                    dstBuf(o + 2) = ClampToByte(rR + (aA - rR) * mix)
                                                                End If
                                                            Next
                                                        End Sub)
                CommitBgraBuffer(result, dstBuf)
                Return result
            End If

            For y = 0 To result.Height - 1
                For x = 0 To result.Width - 1
                    Dim dx = (x - cx) / Math.Max(1.0F, radiusX)
                    Dim dy = (y - cy) / Math.Max(1.0F, radiusY)
                    Dim distance = CSng(Math.Sqrt(dx * dx + dy * dy))
                    Dim t = Clamp((distance - inner) / softness, 0, 1)
                    If t <= 0 Then Continue For
                    t = t * t * (3.0F - 2.0F * t)

                    Dim c = result.GetPixel(x, y)
                    Dim mix = t * edgeAlpha / 255.0F
                    If darken Then
                        Dim vr As Byte, vg As Byte, vb As Byte
                        VignetteDarken(c.Red, c.Green, c.Blue, mix, style, vr, vg, vb)
                        result.SetPixel(x, y, New SKColor(vr, vg, vb, c.Alpha))
                    Else
                        result.SetPixel(x, y, New SKColor(ClampToByte(c.Red + (255 - c.Red) * mix),
                                                          ClampToByte(c.Green + (255 - c.Green) * mix),
                                                          ClampToByte(c.Blue + (255 - c.Blue) * mix),
                                                          c.Alpha))
                    End If
                Next
            Next
            Return result
        End Function

        ''' <summary>Koernung. Von GetPixel/SetPixel auf Puffer umgestellt (gemessen
        ''' 4,3 s bei 6,3 MP).
        ''' BEWUSST SERIELL: der Zufallsstrom haengt an der Durchlaufreihenfolge. Parallel wuerde das
        ''' Korn bei jedem Lauf anders fallen - wiederholte Laeufe muessen bitgleich sein, und
        ''' ein Bild, das sich beim zweiten Rendern aendert, waere auch fuer den Nutzer falsch.
        ''' Der Gewinn kommt allein aus dem Wegfall des P/Invoke je Pixel.</summary>
        ''' <summary>Körnung. Ohne Größe/Frequenz (beide 0) das bisherige feine 1-px-Korn, bitgenau
        ''' unverändert; sonst zellenweise gröber (Größe) und optional mit eingemischter feiner Lage
        ''' (Frequenz).</summary>
        Private Shared Function ApplyGrain(source As SKBitmap, amount As Single, Optional sizeAmount As Single = 0, Optional freqAmount As Single = 0) As SKBitmap
            If sizeAmount <= 0 AndAlso freqAmount <= 0 Then Return ApplyGrainFine(source, amount)
            Return ApplyGrainTextured(source, amount, sizeAmount, freqAmount)
        End Function

        Private Shared Function ApplyGrainFine(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return source

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) Then Return result
            Dim dstBuf = New Byte(srcBuf.Length - 1) {}

            Dim random = New Random(source.Width * 397 Xor source.Height * 151)
            Dim amplitude = 8.0 + strength * 34.0

            For y As Integer = 0 To source.Height - 1
                Dim rowOffset = y * stride
                For x As Integer = 0 To source.Width - 1
                    Dim o = rowOffset + x * 4
                    Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                    ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                    Dim noise = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                                       ClampToByte(cr + noise), ClampToByte(cg + noise), ClampToByte(cb + noise), a)
                Next
            Next

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            Return result
        End Function

        ''' <summary>Gröberes/unregelmäßigeres Korn. Größe = Zellkantenlänge (Pixel einer Zelle teilen
        ''' sich denselben Rauschwert). Frequenz = UNREGELMÄSSIGKEIT: eine grobe, niederfrequente
        ''' Amplituden-Modulation, die das Korn fleckig macht (manche Bereiche stärker, manche schwächer)
        ''' - sichtbar bei JEDER Größe, auch 0, und unabhängig von der Korn-Skala. BEWUSST SERIELL wie
        ''' <see cref="ApplyGrainFine"/>: erst das Zellraster, dann das grobe Modulationsraster - der
        ''' Zufallsstrom hängt an der Reihenfolge, damit Vorschau und Backen bitgleich bleiben.</summary>
        Private Shared Function ApplyGrainTextured(source As SKBitmap, amount As Single, sizeAmount As Single, freqAmount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return source

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) Then Return result
            Dim dstBuf = New Byte(srcBuf.Length - 1) {}

            Dim w = source.Width, h = source.Height
            Dim cell = 1 + CInt(Math.Round(Clamp(sizeAmount, 0, 1) * 5))   ' 1..6 px
            Dim freq = Clamp(freqAmount, 0, 1)
            Dim amplitude = 8.0 + strength * 34.0
            Dim random = New Random(w * 397 Xor h * 151)

            ' Korn-Zellraster zuerst, seriell und deterministisch.
            Dim gridW = (w + cell - 1) \ cell
            Dim gridH = (h + cell - 1) \ cell
            Dim cellNoise(gridW * gridH - 1) As Double
            For i = 0 To cellNoise.Length - 1
                cellNoise(i) = random.NextDouble() * 2.0 - 1.0
            Next

            ' Grobes Modulationsraster (fester grober Abstand) nur bei aktiver Frequenz - danach gezogen,
            ' damit der Korn-Strom bei freq=0 exakt gleich bleibt. Werte 0..1 = lokale Korn-Intensität.
            Const ModCell As Integer = 16
            Dim modW = (w + ModCell - 1) \ ModCell
            Dim modH = (h + ModCell - 1) \ ModCell
            Dim modNoise As Double() = Nothing
            If freq > 0 Then
                modNoise = New Double(Math.Max(1, modW * modH) - 1) {}
                For i = 0 To modNoise.Length - 1
                    modNoise(i) = random.NextDouble()
                Next
            End If

            For y As Integer = 0 To h - 1
                Dim rowOffset = y * stride
                Dim gy = y \ cell
                Dim my = y \ ModCell
                For x As Integer = 0 To w - 1
                    Dim o = rowOffset + x * 4
                    Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                    ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                    Dim n = cellNoise(gy * gridW + (x \ cell))
                    Dim amp = amplitude
                    If freq > 0 Then
                        ' 0..1 -> -1..1, mit K=0.9 skaliert: Faktor in [1-0.9*freq, 1+0.9*freq], nie <= 0.
                        Dim m = modNoise(my * modW + (x \ ModCell)) * 2.0 - 1.0
                        amp = amplitude * (1.0 + freq * m * 0.9)
                    End If
                    Dim noise = n * amp
                    WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                                       ClampToByte(cr + noise), ClampToByte(cg + noise), ClampToByte(cb + noise), a)
                Next
            Next

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            Return result
        End Function

        ''' <summary>Rauschen hinzufuegen. Von GetPixel/SetPixel auf Puffer umgestellt.
        ''' Seriell aus demselben Grund wie ApplyGrain: der Zufallsstrom haengt an der Reihenfolge.</summary>
        Private Shared Function ApplyAddNoise(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return source
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) Then Return result
            Dim dstBuf = New Byte(srcBuf.Length - 1) {}

            Dim random = New Random(source.Width * 541 Xor source.Height * 877)
            Dim amplitude = strength * 72.0

            For y As Integer = 0 To source.Height - 1
                Dim rowOffset = y * stride
                For x As Integer = 0 To source.Width - 1
                    Dim o = rowOffset + x * 4
                    Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                    ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                    ' Digitales Rauschen ist chromatisch: pro Kanal ein eigener Zufallswert, damit
                    ' farbige Sensor-Speckles entstehen. Das unterscheidet "Rauschen" klar von der
                    ' monochromen "Koernung" (ApplyGrain), die denselben Wert auf alle Kanaele legt.
                    Dim noiseR = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    Dim noiseG = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    Dim noiseB = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                                       ClampToByte(cr + noiseR), ClampToByte(cg + noiseG), ClampToByte(cb + noiseB), a)
                Next
            Next

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            Return result
        End Function

        ''' <summary>Zeichnet den Rahmen in ein Rechteck. Frueher war das eine Stufe der Pixelkette
        ''' (ApplyBorder); seit der Rahmen ein Objekt ist, zeichnet ihn derselbe Weg wie Text und
        ''' Form. Die Groessen beziehen sich auf die kuerzere Kante des uebergebenen Rechtecks, damit
        ''' ein Rahmen bei jedem Seitenverhaeltnis gleich breit wirkt.</summary>
        Friend Shared Sub DrawFrameOnCanvas(canvas As SKCanvas, bounds As SKRect, sizePercent As Single,
                                            color As SKColor, cornerRadiusPercent As Single, effect As String,
                                            Optional fillKind As String = "Solid",
                                            Optional color2 As SKColor = Nothing,
                                            Optional gradientAngleDegrees As Single = 0,
                                            Optional gradientInverted As Boolean = False,
                                            Optional symbol As String = "",
                                            Optional symbolSpacingPercent As Single = 50,
                                            Optional symbolRotate As Boolean = False,
                                            Optional symbolStrokeColor As SKColor = Nothing,
                                            Optional symbolStrokeWidth As Single = 0)
            If canvas Is Nothing Then Return
            Dim boundsWidth = bounds.Width
            Dim boundsHeight = bounds.Height
            If boundsWidth <= 0 OrElse boundsHeight <= 0 Then Return
            Dim thickness = CInt(Math.Round(Math.Min(boundsWidth, boundsHeight) * Clamp(sizePercent, 0, 0.25F)))
            If thickness <= 0 Then Return

            ' VOR dem Try: was im Finally freigegeben wird, muss dort auch sichtbar sein - eine
            ' Deklaration im Try-Block ist es in VB nicht.
            Dim gradientShader As SKShader = Nothing

            canvas.Save()
            canvas.Translate(bounds.Left, bounds.Top)
            Try
                Dim normalized = If(effect, "Einfach").Trim().ToLowerInvariant()
                Dim radius = Math.Min(boundsWidth, boundsHeight) * Clamp(cornerRadiusPercent, 0, 1) * 0.25F

                ' Verlauf wie bei den Formen, nur auf der KONTUR statt in der Flaeche: derselbe
                ' Schattierer, damit Winkel, Umkehrung und Radialform sich ueberall gleich verhalten.
                ' Er wird EINMAL gebaut und an jeden Pinsel gehaengt - der doppelte Rahmen zeichnet
                ' zwei Linien und soll denselben Verlauf tragen, nicht zwei eigene.
                Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
                If normalizedFillKind = "radialgradient" Then
                    ' Der radiale Verlauf einer FLAECHE spannt von der Mitte bis zur Ecke. Ein Rahmen
                    ' liegt aber nur im aeussersten Ring davon: bei 800x600 laege die Mitte einer
                    ' Kante bei 0,6 des Radius und die Ecke bei 1,0 - der Rahmen zeigte also nur die
                    ' letzten 40 Prozent der Farbrampe und sah fast einfarbig aus. Deshalb bekommen
                    ' die beiden Farben hier VERSCHOBENE Stuetzstellen: die Rampe faengt dort an, wo
                    ' der Rahmen anfaengt (Kantenmitte), und endet in der Ecke.
                    Dim mitte = New SKPoint(boundsWidth / 2.0F, boundsHeight / 2.0F)
                    Dim aussen = CSng(Math.Sqrt(CDbl(boundsWidth) * boundsWidth + CDbl(boundsHeight) * boundsHeight) / 2.0)
                    Dim innen = Math.Min(boundsWidth, boundsHeight) / 2.0F
                    Dim start = If(gradientInverted, color2, color)
                    Dim ende = If(gradientInverted, color, color2)
                    Dim beginn = If(aussen > 0, Clamp(innen / aussen, 0, 0.95F), 0.0F)
                    gradientShader = SKShader.CreateRadialGradient(mitte, Math.Max(1.0F, aussen),
                                                                   New SKColor() {start, ende},
                                                                   New Single() {beginn, 1.0F},
                                                                   SKShaderTileMode.Clamp)
                ElseIf normalizedFillKind = "lineargradient" Then
                    gradientShader = CreateFillGradientShader(New SKRect(0, 0, boundsWidth, boundsHeight),
                                                              normalizedFillKind, color, color2,
                                                              gradientAngleDegrees, gradientInverted)
                End If

                ' MIT SYMBOL wird der Rahmen nicht gestrichen, sondern bestempelt: die Rahmenart
                ' liefert nur noch den PFAD, auf dem die Symbole sitzen.
                If Not String.IsNullOrWhiteSpace(symbol) Then
                    StampFrameSymbols(canvas, boundsWidth, boundsHeight, thickness, radius, normalized,
                                      symbol, symbolSpacingPercent, symbolRotate, color, gradientShader,
                                      symbolStrokeColor, symbolStrokeWidth)
                    Return
                End If

                If normalized = "doppelt" Then
                    ''' Zwei dünne konzentrische Linien mit Lücke dazwischen (klassischer Passepartout-Look)
                    ''' statt einer einzelnen Linie in voller Stärke.
                    Dim thinWidth = Math.Max(1.0F, thickness * 0.35F)
                    Dim gap = thickness * 0.6F
                    Using paint = New SKPaint With {.Color = color, .Style = SKPaintStyle.Stroke, .StrokeWidth = thinWidth, .IsAntialias = True}
                        If gradientShader IsNot Nothing Then paint.Shader = gradientShader
                        Dim outerInset = thinWidth / 2.0F
                        Dim outerRect = New SKRect(outerInset, outerInset, boundsWidth - outerInset, boundsHeight - outerInset)
                        Dim innerRect = New SKRect(outerInset + gap, outerInset + gap, boundsWidth - outerInset - gap, boundsHeight - outerInset - gap)
                        If radius > 0 Then
                            canvas.DrawRoundRect(outerRect, radius, radius, paint)
                            canvas.DrawRoundRect(innerRect, Math.Max(0.0F, radius - gap), Math.Max(0.0F, radius - gap), paint)
                        Else
                            canvas.DrawRect(outerRect, paint)
                            canvas.DrawRect(innerRect, paint)
                        End If
                    End Using
                    Return
                End If

                Using paint = New SKPaint With {.Color = color, .Style = SKPaintStyle.Stroke, .StrokeWidth = thickness, .IsAntialias = True}
                    If gradientShader IsNot Nothing Then paint.Shader = gradientShader
                    Select Case normalized
                        Case "gestrichelt"
                            paint.PathEffect = SKPathEffect.CreateDash(New Single() {thickness * 1.4F, thickness * 0.9F}, 0)
                        Case "punktiert"
                            ''' Sehr kurzes "An"-Segment + runde Stroke-Caps rendert als Punktreihe statt Striche.
                            paint.StrokeCap = SKStrokeCap.Round
                            paint.PathEffect = SKPathEffect.CreateDash(New Single() {0.01F, thickness * 1.3F}, 0)
                    End Select
                    Dim inset = thickness / 2.0F
                    Dim rect = New SKRect(inset, inset, boundsWidth - inset, boundsHeight - inset)
                    Select Case normalized
                        Case "gezackt"
                            Using path = BuildZigZagBorderPath(rect, Math.Max(4, thickness))
                                canvas.DrawPath(path, paint)
                            End Using
                        Case "wellig"
                            Using path = BuildWavyBorderPath(rect, Math.Max(6, thickness * 1.5F))
                                canvas.DrawPath(path, paint)
                            End Using
                        Case Else
                            If radius > 0 Then
                                canvas.DrawRoundRect(rect, radius, radius, paint)
                            Else
                                canvas.DrawRect(rect, paint)
                            End If
                    End Select
                End Using
            Finally
                gradientShader?.Dispose()
                canvas.Restore()
            End Try
        End Sub

        ''' <summary>Baut den Pfad, auf dem der Rahmen liegt - dieselbe Form, die sonst gestrichen
        ''' wird. "Doppelt" liefert zwei ineinanderliegende Ringe, daraus werden die zwei Reihen.</summary>
        Private Shared Function BuildFramePath(width As Single, height As Single, thickness As Single,
                                               radius As Single, normalizedEffect As String) As SKPath
            Dim path = New SKPath()
            Dim inset = thickness / 2.0F
            Dim rect = New SKRect(inset, inset, width - inset, height - inset)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return path

            Select Case normalizedEffect
                Case "gezackt"
                    Using zack = BuildZigZagBorderPath(rect, Math.Max(4.0F, thickness))
                        path.AddPath(zack)
                    End Using
                Case "wellig"
                    Using welle = BuildWavyBorderPath(rect, Math.Max(6.0F, thickness * 1.5F))
                        path.AddPath(welle)
                    End Using
                Case "doppelt"
                    ' Zwei Ringe wie beim gezeichneten Doppelrahmen - daraus werden zwei Reihen Symbole.
                    Dim gap = thickness * 0.6F
                    Dim innerRect = New SKRect(rect.Left + gap, rect.Top + gap, rect.Right - gap, rect.Bottom - gap)
                    If radius > 0 Then
                        path.AddRoundRect(rect, radius, radius)
                        If innerRect.Width > 0 AndAlso innerRect.Height > 0 Then
                            path.AddRoundRect(innerRect, Math.Max(0.0F, radius - gap), Math.Max(0.0F, radius - gap))
                        End If
                    Else
                        path.AddRect(rect)
                        If innerRect.Width > 0 AndAlso innerRect.Height > 0 Then path.AddRect(innerRect)
                    End If
                Case Else
                    ' Einfach, gestrichelt, punktiert: derselbe Ring. Ein Strichmuster ergibt beim
                    ' Stempeln keinen Sinn - dafuer gibt es den eigenen Abstand.
                    If radius > 0 Then path.AddRoundRect(rect, radius, radius) Else path.AddRect(rect)
            End Select
            Return path
        End Function

        ''' <summary>Stempelt ein Symbol in gleichen Abstaenden entlang des Rahmenpfades.
        '''
        ''' Gezeichnet wird mit derselben Routine wie die Form-Objekte - ein Stern im Rahmen sieht
        ''' also aus wie ein Stern auf der Buehne. Traegt der Rahmen einen Verlauf, entstehen die
        ''' Symbole zuerst deckend auf einer eigenen Ebene und werden danach mit dem Schattierer
        ''' eingefaerbt (SrcIn); sonst truege jeder Stempel denselben Verlauf in sich statt einen
        ''' gemeinsamen ueber den ganzen Rahmen.</summary>
        Private Shared Sub StampFrameSymbols(canvas As SKCanvas, width As Single, height As Single,
                                             thickness As Single, radius As Single, normalizedEffect As String,
                                             symbol As String, spacingPercent As Single, rotate As Boolean,
                                             color As SKColor, gradientShader As SKShader,
                                             strokeColor As SKColor, strokeWidth As Single)
            Using path = BuildFramePath(width, height, thickness, radius, normalizedEffect)
                If path.IsEmpty Then Return

                Dim size = Math.Max(2.0F, thickness)
                Dim schrittweite = size * (1.0F + Math.Max(0.0F, Math.Min(400.0F, spacingPercent)) / 100.0F)
                If schrittweite <= 0.5F Then schrittweite = 1.0F

                Dim kind = NormalizeFrameSymbolKind(symbol)
                Dim malfarbe = If(gradientShader Is Nothing, color, New SKColor(255, 255, 255, color.Alpha))
                Dim vorlage = New ImageAnnotation With {.Kind = kind, .FillColor = "#FFFFFFFF", .StrokeWidth = 0}

                ' Einmal ueber den Pfad laufen und an jeder Stelle stempeln. Steht als eigener
                ' Durchgang da, weil er bei einem Verlauf ZWEIMAL gebraucht wird: die Fuellung
                ' entsteht auf einer eigenen Ebene und wird eingefaerbt, die Kontur kommt danach
                ' obendrauf und behaelt ihre eigene Farbe.
                Dim stempeln =
                    Sub(fuellung As SKColor, kontur As SKColor, konturbreite As Single)
                        Using measure = New SKPathMeasure(path, False)
                            Do
                                Dim laenge = measure.Length
                                If laenge > 0 Then
                                    ' Gleichmaessig verteilen: die Schrittweite wird auf die Laenge
                                    ' JEDER Kontur eingepasst, sonst klafft an deren Ende eine Luecke.
                                    Dim anzahl = Math.Max(1, CInt(Math.Round(laenge / schrittweite)))
                                    Dim schritt = laenge / anzahl
                                    For i = 0 To anzahl - 1
                                        Dim pos As SKPoint = Nothing, tangente As SKPoint = Nothing
                                        If Not measure.GetPositionAndTangent(i * schritt, pos, tangente) Then Continue For
                                        canvas.Save()
                                        canvas.Translate(pos.X, pos.Y)
                                        If rotate Then
                                            canvas.RotateDegrees(CSng(Math.Atan2(tangente.Y, tangente.X) * 180.0 / Math.PI))
                                        End If
                                        Dim halb = size / 2.0F
                                        Dim ziel = New SKRect(-halb, -halb, halb, halb)
                                        DrawAnnotationShape(canvas, kind, vorlage, ziel, ziel.Left, ziel.Top,
                                                            ziel.Width, size, fuellung, kontur, konturbreite, 1.0F)
                                        canvas.Restore()
                                    Next
                                End If
                            Loop While measure.NextContour()
                        End Using
                    End Sub

                Dim hatKontur = strokeWidth > 0 AndAlso strokeColor.Alpha > 0

                If gradientShader Is Nothing Then
                    stempeln(color, If(hatKontur, strokeColor, SKColors.Transparent),
                             If(hatKontur, strokeWidth, 0.0F))
                Else
                    canvas.SaveLayer()
                    Try
                        stempeln(malfarbe, SKColors.Transparent, 0.0F)
                        Using paint = New SKPaint With {.Shader = gradientShader, .BlendMode = SKBlendMode.SrcIn}
                            canvas.DrawRect(New SKRect(0, 0, width, height), paint)
                        End Using
                    Finally
                        canvas.Restore()
                    End Try
                    ' Die Kontur NACH dem Einfaerben und ausserhalb der Ebene - sonst faerbte der
                    ' Verlauf sie mit ein, und eine eigene Konturfarbe waere folgenlos.
                    If hatKontur Then stempeln(SKColors.Transparent, strokeColor, strokeWidth)
                End If
            End Using
        End Sub

        ''' <summary>Die Formen, die als Rahmensymbol zur Wahl stehen. Alle gibt es auch als Objekt;
        ''' die Liste steht hier, damit Auswahlliste und Renderer dieselbe Quelle haben.</summary>
        Public Shared ReadOnly Property FrameSymbolKinds As String()
            Get
                Return New String() {"Star", "DoubleStar", "Heart", "Diamond", "Droplet", "Cloud",
                                     "Ellipse", "Square", "Triangle", "Polygon"}
            End Get
        End Property

        Private Shared Function NormalizeFrameSymbolKind(symbol As String) As String
            Dim wert = If(symbol, "").Trim().ToLowerInvariant()
            For Each kind In FrameSymbolKinds
                If String.Equals(kind, wert, StringComparison.OrdinalIgnoreCase) Then Return kind.ToLowerInvariant()
            Next
            Return "star"
        End Function

        Private Shared Function BuildZigZagBorderPath(rect As SKRect, stepSize As Single) As SKPath
            Dim path = New SKPath()
            Dim stepV = Math.Max(4.0F, stepSize)
            path.MoveTo(rect.Left, rect.Top)
            Dim x = rect.Left
            Dim up = True
            While x < rect.Right
                x = Math.Min(rect.Right, x + stepV)
                path.LineTo(x, If(up, rect.Top + stepV * 0.5F, rect.Top))
                up = Not up
            End While
            Dim y = rect.Top
            While y < rect.Bottom
                y = Math.Min(rect.Bottom, y + stepV)
                path.LineTo(If(up, rect.Right - stepV * 0.5F, rect.Right), y)
                up = Not up
            End While
            x = rect.Right
            While x > rect.Left
                x = Math.Max(rect.Left, x - stepV)
                path.LineTo(x, If(up, rect.Bottom - stepV * 0.5F, rect.Bottom))
                up = Not up
            End While
            y = rect.Bottom
            While y > rect.Top
                y = Math.Max(rect.Top, y - stepV)
                path.LineTo(If(up, rect.Left + stepV * 0.5F, rect.Left), y)
                up = Not up
            End While
            path.Close()
            Return path
        End Function

        ''' Geschwungene/muschelförmige Randlinie: wie BuildZigZagBorderPath aufgebaut (vier Kanten,
        ''' abwechselnd nach außen/innen ausschlagend), aber mit QuadTo-Bögen statt geraden LineTo-
        ''' Segmenten - ergibt einen weichen Wellenrand statt scharfer Zacken.
        Private Shared Function BuildWavyBorderPath(rect As SKRect, stepSize As Single) As SKPath
            Dim path = New SKPath()
            Dim stepV = Math.Max(6.0F, stepSize)
            Dim amp = stepV * 0.35F

            path.MoveTo(rect.Left, rect.Top)
            Dim x = rect.Left
            Dim outward = True
            While x < rect.Right
                Dim nx = Math.Min(rect.Right, x + stepV)
                Dim midX = (x + nx) / 2.0F
                path.QuadTo(midX, rect.Top + If(outward, -amp, amp), nx, rect.Top)
                x = nx
                outward = Not outward
            End While
            Dim y = rect.Top
            While y < rect.Bottom
                Dim ny = Math.Min(rect.Bottom, y + stepV)
                Dim midY = (y + ny) / 2.0F
                path.QuadTo(rect.Right + If(outward, amp, -amp), midY, rect.Right, ny)
                y = ny
                outward = Not outward
            End While
            x = rect.Right
            While x > rect.Left
                Dim nx = Math.Max(rect.Left, x - stepV)
                Dim midX = (x + nx) / 2.0F
                path.QuadTo(midX, rect.Bottom + If(outward, amp, -amp), nx, rect.Bottom)
                x = nx
                outward = Not outward
            End While
            y = rect.Bottom
            While y > rect.Top
                Dim ny = Math.Max(rect.Top, y - stepV)
                Dim midY = (y + ny) / 2.0F
                path.QuadTo(rect.Left + If(outward, -amp, amp), midY, rect.Left, ny)
                y = ny
                outward = Not outward
            End While
            path.Close()
            Return path
        End Function

        Private Shared Function Clamp(value As Single, min As Single, max As Single) As Single
            If Single.IsNaN(value) OrElse Single.IsInfinity(value) Then Return min
            Return Math.Max(min, Math.Min(max, value))
        End Function

        Private Shared Function ClampToByte(value As Double) As Byte
            If Double.IsNaN(value) Then Return 0
            Return CByte(Math.Max(0.0, Math.Min(255.0, Math.Round(value))))
        End Function

        Private Shared Sub RgbToHsl(rByte As Byte, gByte As Byte, bByte As Byte, ByRef h As Double, ByRef s As Double, ByRef l As Double)
            Dim r = rByte / 255.0
            Dim g = gByte / 255.0
            Dim b = bByte / 255.0
            Dim maxV = Math.Max(r, Math.Max(g, b))
            Dim minV = Math.Min(r, Math.Min(g, b))
            l = (maxV + minV) / 2.0

            If Math.Abs(maxV - minV) < 0.00001 Then
                h = 0
                s = 0
                Return
            End If

            Dim d = maxV - minV
            s = If(l > 0.5, d / (2.0 - maxV - minV), d / (maxV + minV))
            If maxV = r Then
                h = (g - b) / d + If(g < b, 6.0, 0.0)
            ElseIf maxV = g Then
                h = (b - r) / d + 2.0
            Else
                h = (r - g) / d + 4.0
            End If
            h *= 60.0
        End Sub

        Private Shared Function HslToRgb(h As Double, s As Double, l As Double, alpha As Byte) As SKColor
            If s <= 0 Then
                Dim gray = ClampToByte(l * 255.0)
                Return New SKColor(gray, gray, gray, alpha)
            End If

            Dim q = If(l < 0.5, l * (1.0 + s), l + s - l * s)
            Dim p = 2.0 * l - q
            Dim hk = h / 360.0
            Dim r = HueToRgb(p, q, hk + 1.0 / 3.0)
            Dim g = HueToRgb(p, q, hk)
            Dim b = HueToRgb(p, q, hk - 1.0 / 3.0)
            Return New SKColor(ClampToByte(r * 255.0), ClampToByte(g * 255.0), ClampToByte(b * 255.0), alpha)
        End Function

        Private Shared Function HueToRgb(p As Double, q As Double, t As Double) As Double
            If t < 0 Then t += 1
            If t > 1 Then t -= 1
            If t < 1.0 / 6.0 Then Return p + (q - p) * 6.0 * t
            If t < 1.0 / 2.0 Then Return q
            If t < 2.0 / 3.0 Then Return p + (q - p) * (2.0 / 3.0 - t) * 6.0
            Return p
        End Function

        Private Shared Function RenderHistogram(source As SKBitmap, width As Integer, height As Integer) As SKBitmap
            width = Math.Max(120, width)
            height = Math.Max(70, height)
            Dim counts = BuildChannelHistogramCounts(source)
            Dim maxBin = Math.Max(1, Math.Max(counts.R.Max(), Math.Max(counts.G.Max(), counts.B.Max())))

            Dim result = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Using canvas = New SKCanvas(result)
                canvas.Clear(New SKColor(18, 20, 24, 255))
                Using gridPaint = New SKPaint With {.Color = New SKColor(255, 255, 255, 26), .StrokeWidth = 1}
                    For i As Integer = 1 To 3
                        Dim x = CSng(width * i / 4.0)
                        canvas.DrawLine(x, 0, x, height, gridPaint)
                    Next
                End Using

                DrawHistogramChannel(canvas, counts.R, maxBin, width, height, New SKColor(255, 70, 70, 165))
                DrawHistogramChannel(canvas, counts.G, maxBin, width, height, New SKColor(70, 220, 90, 165))
                DrawHistogramChannel(canvas, counts.B, maxBin, width, height, New SKColor(70, 130, 255, 165))
            End Using
            Return result
        End Function

        Private Shared Sub DrawHistogramChannel(canvas As SKCanvas, bins As Integer(), maxBin As Integer, width As Integer, height As Integer, color As SKColor)
            Using paint = New SKPaint With {.Color = color, .StrokeWidth = Math.Max(1.0F, width / 256.0F), .IsAntialias = True, .BlendMode = SKBlendMode.Plus}
                For i As Integer = 0 To 255
                    If bins(i) <= 0 Then Continue For
                    Dim x = CSng(i / 255.0 * (width - 1))
                    Dim bar = CSng(Math.Pow(bins(i) / CDbl(maxBin), 0.45) * (height - 6))
                    canvas.DrawLine(x, height - 2, x, height - 2 - bar, paint)
                Next
            End Using
        End Sub


        ''' Wird für JEDEN Live-Vorschau-Frame aufgerufen (Regler, Filter, Annotationen, Histogramm),
        ''' daher lohnt sich hier der schnelle Direktkopie-Pfad (ImageOrientationService.ToAvaloniaBitmapFast)
        ''' besonders: kein PNG-Encode/Decode-Umweg mehr, nur eine reine Zeilen-Speicherkopie. Die
        ''' interne Verarbeitungs-Pipeline erzeugt Bitmaps praktisch immer als Bgra8888/Premul (siehe
        ''' DecodeOriented sowie die durchgängige source.ColorType/AlphaType-Weitergabe in den
        ''' Anpassungsfunktionen), der PNG-Umweg bleibt nur als Sicherheitsnetz für den seltenen Fall
        ''' eines abweichenden Farbformats erhalten.
        Friend Shared Function ToAvaloniaBitmap(skBitmap As SKBitmap) As Bitmap
            If skBitmap.ColorType = SKColorType.Bgra8888 AndAlso skBitmap.AlphaType = SKAlphaType.Premul Then
                Return ImageOrientationService.ToAvaloniaBitmapFast(skBitmap)
            End If
            ' Rgba8888/Premul (z.B. jede Ausgabe von ApplyAnnotations und damit die SZENE) ebenfalls per
            ' reiner Zeilen-Speicherkopie - der fruehere PNG-Encode/Decode-Umweg kostete pro Vorschau-
            ' Update einen kompletten 9,8-MP-Roundtrip (CPU hoch, Regler nur "haeppchenweise").
            If skBitmap.ColorType = SKColorType.Rgba8888 AndAlso skBitmap.AlphaType = SKAlphaType.Premul Then
                Return ToAvaloniaBitmapFastRgba(skBitmap)
            End If

            ' Jedes andere Format erst auf 8 Bit bringen und dann denselben schnellen Weg nehmen.
            '
            ' NICHT ueber einen PNG-Umweg, wie es hier frueher stand: SKImage.FromBitmap liefert
            ' fuer manche Farbtypen (etwa Rgba16161616) schlicht Nothing, und der Encode lief danach
            ' in eine NullReferenceException. Das riss beim Oeffnen jeder RAW-Datei die Anwendung um,
            ' solange das Arbeitsbild 16 Bit trug. Der 16-Bit-Weg ist inzwischen wieder
            ' ausgebaut, die Konvertierung hier bleibt aber der robustere Weg fuer alles Unerwartete.
            '
            ' Bewusst OHNE Try/Catch um den Aufruf: ein erster Anlauf fing hier breit ab und gab im
            ' Fehlerfall Nothing zurueck - damit haette ein echter Plattformfehler stumm ein leeres
            ' Bild ergeben statt einer Meldung. Faellt hier etwas aus, soll es auffallen.
            Dim acht = New SKBitmap(New SKImageInfo(skBitmap.Width, skBitmap.Height,
                                                    SKColorType.Bgra8888, SKAlphaType.Premul))
            Try
                Using cv As New SKCanvas(acht)
                    cv.Clear(SKColors.Transparent)
                    cv.DrawBitmap(skBitmap, 0, 0)
                End Using
                Return ImageOrientationService.ToAvaloniaBitmapFast(acht)
            Finally
                acht.Dispose()
            End Try
        End Function

        ''' <summary>Wie ImageOrientationService.ToAvaloniaBitmapFast, nur fuer Rgba8888/Premul
        ''' (Avalonia PixelFormat.Rgba8888) - reine Zeilenkopie ohne Kompressions-Umweg.</summary>
        Private Shared Function ToAvaloniaBitmapFastRgba(skBitmap As SKBitmap) As Bitmap
            Dim width = skBitmap.Width
            Dim height = skBitmap.Height
            Dim wb = New WriteableBitmap(New Avalonia.PixelSize(width, height), New Avalonia.Vector(96, 96),
                                         Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Premul)
            Using fb = wb.Lock()
                Dim srcStride = skBitmap.RowBytes
                Dim dstStride = fb.RowBytes
                Dim rowBytes = Math.Min(srcStride, dstStride)
                Dim srcBase = skBitmap.GetPixels()
                Dim buffer(rowBytes - 1) As Byte
                For y = 0 To height - 1
                    Runtime.InteropServices.Marshal.Copy(IntPtr.Add(srcBase, y * srcStride), buffer, 0, rowBytes)
                    Runtime.InteropServices.Marshal.Copy(buffer, 0, IntPtr.Add(fb.Address, y * dstStride), rowBytes)
                Next
            End Using
            Return wb
        End Function
    End Class

End Namespace
