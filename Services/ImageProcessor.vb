Imports System
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

    Public Structure StrokePoint
        Public ReadOnly X As Single
        Public ReadOnly Y As Single

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

        Public Sub New(points As IEnumerable(Of StrokePoint))
            _points = If(points, Enumerable.Empty(Of StrokePoint)()).ToArray()
        End Sub

        Public ReadOnly Property Points As IReadOnlyList(Of StrokePoint)
            Get
                Return _points
            End Get
        End Property

        Public Function Scale(scaleX As Single, scaleY As Single) As BrushStroke
            Return New BrushStroke(_points.Select(Function(p) New StrokePoint(p.X * scaleX, p.Y * scaleY)))
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

    Public Class ImageAnnotation
        Implements INotifyPropertyChanged

        Private _kind As String = "Text"
        Private _text As String = ""
        Private _imagePath As String = ""
        Private _xPixels As Single = 0
        Private _yPixels As Single = 0
        Private _widthPixels As Single = 480
        Private _heightPixels As Single = 180
        Private _fillColor As String = "#FFFFFFFF"
        Private _strokeColor As String = "#FF000000"
        Private _eraserFillColor As String = ""
        Private _strokeWidth As Single = 0
        Private _fontSizePixels As Single = 48
        Private _fontFamily As String = "Arial"
        Private _opacity As Single = 100
        Private _blendMode As String = "Normal"
        Private _flowPercent As Single = 100
        Private _rotationDegrees As Single = 0
        ' Spiegelung des Objekts um seine eigene Mitte (nicht um die Bildmitte): das Drehen-Werkzeug
        ' wirkt mit seinen vier Knöpfen auf das markierte Objekt, und Spiegeln können die Anfasser nicht.
        Private _flipHorizontal As Boolean = False
        Private _flipVertical As Boolean = False
        Private _anchor As String = ""
        Private _isVisible As Boolean = True
        Private _hardnessPercent As Single = 100
        Private _fillKind As String = "Solid"
        Private _fillColor2 As String = "#FFFFFFFF"
        Private _gradientAngleDegrees As Single = 0
        Private _gradientInverted As Boolean = False
        Private _shadowEnabled As Boolean = False
        Private _shadowOffsetXPercent As Single = 2
        Private _shadowOffsetYPercent As Single = 2
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

        Public ReadOnly Property LayerLabel As String
            Get
                Dim baseLabel = GermanKindLabel(_kind)
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
                    Return $"{baseLabel}: {IO.Path.GetFileName(_imagePath)}"
                End If
                If _kind IsNot Nothing AndAlso _kind.Equals("Svg", StringComparison.OrdinalIgnoreCase) AndAlso Not String.IsNullOrWhiteSpace(_imagePath) Then
                    Return GetSvgLayerLabel(_imagePath)
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
                    Return If(String.IsNullOrWhiteSpace(_imagePath), base & "09_FormenSymbole/005_Rechteck.svg", _imagePath)
                End If
                If _kind IsNot Nothing AndAlso _kind.Equals("Symbol", StringComparison.OrdinalIgnoreCase) Then
                    Select Case _text
                        Case "♥" : Return base & "09_FormenSymbole/047_Herz.svg"
                        Case "✓" : Return base & "09_FormenSymbole/107_Check.svg"
                        Case Else : Return base & "09_FormenSymbole/061_Stern.svg"
                    End Select
                End If
                Select Case If(_kind, "").Trim().ToLowerInvariant()
                    Case "text" : Return base & "03_Editor/34_Text.svg"
                    Case "watermark" : Return "avares://FerrumPix/Assets/Icons/outline/rubber-stamp.svg"
                    Case "image", "selectionimage" : Return base & "09_FormenSymbole/077_Bild.svg"
                    Case "qr", "qrcode", "qr-code" : Return base & "03_Editor/37_QR_Code.svg"
                    Case "rectangle", "rect", "selectionfill" : Return base & "09_FormenSymbole/005_Rechteck.svg"
                    Case "roundedrectangle", "rounded-rectangle" : Return base & "outline/square-rounded.svg"
                    Case "square" : Return base & "09_FormenSymbole/003_Quadrat.svg"
                    Case "triangle" : Return base & "09_FormenSymbole/007_Dreieck.svg"
                    Case "ellipse", "circle" : Return base & "09_FormenSymbole/017_Oval.svg"
                    Case "cone" : Return base & "09_FormenSymbole/018_Halbkreis.svg"
                    Case "pyramid" : Return base & "09_FormenSymbole/031_Diamant_facette.svg"
                    Case "trapezoid" : Return base & "outline/trapezoid.svg"
                    Case "diamond" : Return base & "outline/square-rotated.svg"
                    Case "polygon" : Return base & "outline/hexagon.svg"
                    Case "star" : Return base & "outline/star.svg"
                    Case "doublestar", "double-star" : Return base & "outline/eight-point-star.svg"
                    Case "spiral" : Return base & "09_FormenSymbole/053_Spirale.svg"
                    Case "droplet" : Return base & "outline/droplet-shape.svg"
                    Case "ellipsespeechbubble", "ellipse-speech-bubble" : Return base & "outline/ellipse-speech-bubble-shape.svg"
                    Case "rectspeechbubble", "rect-speech-bubble" : Return base & "outline/message.svg"
                    Case "speechbubble", "speech-bubble", "sprechblase", "bubble" : Return base & "outline/speech-bubble-shape.svg"
                    Case "heart" : Return base & "outline/heart.svg"
                    Case "cloud" : Return base & "outline/cloud.svg"
                    Case "line" : Return base & "outline/line-shape.svg"
                    Case "arrow" : Return base & "outline/arrow-right.svg"
                    Case "brush" : Return base & "03_Editor/39_Pinsel.svg"
                    Case "eraser" : Return "avares://FerrumPix/Assets/Icons/outline/eraser.svg"
                    Case Else : Return base & "09_FormenSymbole/005_Rechteck.svg"
                End Select
            End Get
        End Property

        Private Shared Function GermanKindLabel(kind As String) As String
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
                .ImagePath = ImagePath,
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
                .FlowPercent = FlowPercent,
                .RotationDegrees = RotationDegrees,
                .FlipHorizontal = FlipHorizontal,
                .FlipVertical = FlipVertical,
                .Adjustments = If(Adjustments Is Nothing, Nothing, Adjustments.Clone()),
                .Anchor = Anchor,
                .IsVisible = IsVisible,
                .HardnessPercent = HardnessPercent,
                .FillKind = FillKind,
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
        Public Property NoiseReduction As Single = 0
        Public Property NoiseReductionMethod As NoiseReductionMethod = NoiseReductionMethod.Gaussian
        Public Property DustScratches As Single = 0
        Public Property Haze As Single = 0
        Public Property AddNoise As Single = 0
        Public Property [Structure] As Single = 0
        Public Property Glow As Single = 0
        Public Property Vibrance As Single = 0
        Public Property Vignette As Single = 0
        Public Property VignetteTransition As Single = 55
        Public Property VignetteRoundness As Single = 0
        Public Property VignetteFeather As Single = 70
        Public Property VignetteCenterX As Single = 50
        Public Property VignetteCenterY As Single = 50
        Public Property Grain As Single = 0
        Public Property BorderSize As Single = 0
        Public Property BorderColor As String = "#FFFFFFFF"
        Public Property BorderCornerRadius As Single = 0
        Public Property BorderEffect As String = "Einfach"
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
        Public Property SplitToningShadowHue As Single = 0
        Public Property SplitToningShadowSaturation As Single = 0
        Public Property SplitToningHighlightHue As Single = 0
        Public Property SplitToningHighlightSaturation As Single = 0
        Public Property SplitToningBalance As Single = 0
        Public Property RotationDegrees As Integer = 0
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
        Public Property ResizeInterpolation As ResizeInterpolationMode = ResizeInterpolationMode.Bilinear
        Public Property CanvasWidth As Integer = 0
        Public Property CanvasHeight As Integer = 0
        Public Property LockCanvasAspect As Boolean = True
        Public Property CanvasAnchor As String = "Center"
        Public Property CanvasBackgroundColor As String = "#FF000000"
        Public Property FilterPreset As String = "Keine"
        Public Property FilterStrength As Single = 100
        Public Property LutPath As String = ""
        Public Property LutStrength As Single = 100
        Public Property RetouchSpots As New System.Collections.Generic.List(Of RetouchSpot)()
        Public Property Annotations As New System.Collections.Generic.List(Of ImageAnnotation)()
        Public Property HasActiveSelection As Boolean = False
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
            "SourceWidthPixels", "SourceHeightPixels",
            "RotationDegrees", "StraightenDegrees", "StraightenExpandCanvas", "FlipHorizontal", "FlipVertical",
            "CropLeftPercent", "CropTopPercent", "CropRightPercent", "CropBottomPercent",
            "ResizeWidth", "ResizeHeight", "LockResizeAspect", "ResizeInterpolation",
            "CanvasWidth", "CanvasHeight", "LockCanvasAspect", "CanvasAnchor", "CanvasBackgroundColor",
            "BorderSize", "BorderColor", "BorderCornerRadius", "BorderEffect",
            "RetouchSpots", "Annotations",
            "HasActiveSelection", "SelectionXPercent", "SelectionYPercent", "SelectionWidthPercent",
            "SelectionHeightPercent", "SelectionShapeMode", "SelectionShapePointsX", "SelectionShapePointsY",
            "SelectionMaskLeft", "SelectionMaskTop", "SelectionMaskRight", "SelectionMaskBottom",
            "SelectionMaskPngBase64", "SelectionFeatherPixels"
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

        ''' <summary>Nur die Pixel-Anpassungen als eigenes Objekt - das ist der Satz, den ein Objekt mitträgt.</summary>
        Public Function ExtractPixelAdjustments() As ImageAdjustments
            Dim result = New ImageAdjustments()
            result.CopyPixelAdjustmentsFrom(Me)
            Return result
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
            Return New ImageAdjustments With {
                .Exposure = Exposure,
                .SourceWidthPixels = SourceWidthPixels,
                .SourceHeightPixels = SourceHeightPixels,
                .Brightness = Brightness,
                .Contrast = Contrast,
                .Saturation = Saturation,
                .Vibrance = Vibrance,
                .Highlights = Highlights,
                .ShadowsLevel = ShadowsLevel,
                .Whites = Whites,
                .Blacks = Blacks,
                .Temperature = Temperature,
                .Tint = Tint,
                .Sharpness = Sharpness,
                .NoiseReduction = NoiseReduction,
                .NoiseReductionMethod = NoiseReductionMethod,
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
                .BorderSize = BorderSize,
                .BorderColor = BorderColor,
                .BorderCornerRadius = BorderCornerRadius,
                .BorderEffect = BorderEffect,
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
                .SplitToningShadowHue = SplitToningShadowHue,
                .SplitToningShadowSaturation = SplitToningShadowSaturation,
                .SplitToningHighlightHue = SplitToningHighlightHue,
                .SplitToningHighlightSaturation = SplitToningHighlightSaturation,
                .SplitToningBalance = SplitToningBalance,
                .RotationDegrees = RotationDegrees,
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
                .HasActiveSelection = HasActiveSelection,
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
                .SelectionFeatherPixels = SelectionFeatherPixels
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
    End Module

    Public Class ImageProcessor

        ''' SKPaint trug bis SkiaSharp 2 die Schrift selbst. Sein interner Ersatz-SKFont hat
        ''' LinearMetrics=True - ein frisch erzeugter SKFont dagegen False, was Textbreiten und das
        ''' Rendering messbar verändert (geprüft: identische Bytes erst mit LinearMetrics=True).
        Private Shared Function CreateFont(fontFamily As String, fontSize As Single) As SKFont
            Return New SKFont(GetTypeface(fontFamily), fontSize) With {.LinearMetrics = True}
        End Function

        ''' SkiaSharp hat SKFilterQuality zugunsten von SKSamplingOptions abgekündigt. Diese Werte sind
        ''' exakt die, auf die SkiaSharp die alten Stufen intern abbildet (siehe SkiaExtensions.ToSamplingOptions):
        ''' High = kubisch (Mitchell), Medium = linear mit Mipmaps.
        Private Shared ReadOnly SamplingHigh As New SKSamplingOptions(SKCubicResampler.Mitchell)

        ''' Zeichnet eine Bitmap mit ausdrücklicher Abtastung. SKCanvas.DrawBitmap kennt keine
        ''' SKSamplingOptions-Überladung, DrawImage schon - ohne sie fiele die Skalierung auf
        ''' Nearest zurück, weil SKSamplingOptions.Default nicht filtert.
        Private Shared Sub DrawBitmapSampled(canvas As SKCanvas, bitmap As SKBitmap, source As SKRect, dest As SKRect,
                                             sampling As SKSamplingOptions, paint As SKPaint)
            Using image = SKImage.FromBitmap(bitmap)
                canvas.DrawImage(image, source, dest, sampling, paint)
            End Using
        End Sub

        Private Shared Sub DrawBitmapSampled(canvas As SKCanvas, bitmap As SKBitmap, x As Single, y As Single,
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

        ' Ersetzt SKBitmap.Decode(path) an den Stellen, die das tatsächlich bearbeitete Foto laden
        ' (nicht Icons/Sticker-Assets oder reine Pixel-Statistik) - korrigiert die EXIF-Orientierung
        ' einmalig an der Quelle, damit die gesamte Anpassungs-/Export-Pipeline darauf aufbaut.
        ''' Liefert den zu dekodierenden Bild-Stream für einen Pfad - bei RAW-Dateien die
        ''' eingebettete JPEG-Vorschau (RawPreviewService.ExtractPreview), da die RAW-Rohdaten
        ''' selbst hier nicht dekodiert werden können; sonst die Datei direkt. Einziger Engpass
        ''' hinter Preview, Geometrie-Preview UND Speichern (siehe DecodeOriented) - macht RAW an
        ''' allen drei Stellen mit einer einzigen Änderung nutzbar, ohne die RAW-Datei je selbst
        ''' als Schreibziel zu berühren (Speichern schreibt immer in eine neue Zieldatei).
        Private Shared Function OpenSourceStream(path As String) As Stream
            If RawPreviewService.IsSupportedRaw(path) Then Return RawPreviewService.ExtractPreview(path)
            ' ICO ist ein Container, den SkiaSharp nicht kennt - hier als PNG hereingereicht.
            If IcoPreviewService.IsSupportedIco(path) Then Return IcoPreviewService.ExtractPreview(path)
            Return File.OpenRead(path)
        End Function

        Private Shared Function DecodeOriented(path As String) As SKBitmap
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

        Public Shared Function ApplyAdjustments(sourcePath As String, adj As ImageAdjustments) As Bitmap
            Using original = DecodeOriented(sourcePath)
                If original Is Nothing Then Return Nothing

                Using processed = ProcessBitmap(original, adj)
                    Return ToAvaloniaBitmap(processed)
                End Using
            End Using
        End Function

        Public Shared Function ApplyAdjustments(source As SKBitmap, adj As ImageAdjustments) As Bitmap
            If source Is Nothing Then Return Nothing

            SyncLock _baseCacheLock
                Dim baseBitmap = GetOrComputeBaseLocked(source, adj)
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

        Public Shared Function CloneForEditing(source As SKBitmap) As SKBitmap
            If source Is Nothing Then Return Nothing
            Return CloneBitmap(source)
        End Function

        Public Shared Function RenderBitmapPatch(source As SKBitmap, rect As SKRectI) As Bitmap
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
                Return ToAvaloniaBitmap(patch)
            End Using
        End Function

        Public Shared Function RenderRetouchMaskPatch(spots As IEnumerable(Of RetouchSpot),
                                                      rect As SKRectI,
                                                      bitmapWidth As Integer,
                                                      bitmapHeight As Integer,
                                                      sourceWidthPixels As Integer,
                                                      sourceHeightPixels As Integer) As Bitmap
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
                Return ToAvaloniaBitmap(patch)
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

        ''' <summary>Zeichnet das selektierte Objekt so, wie es im gebackenen Bild aussieht - Silhouette
        ''' mit Schatten und Glühen, darüber das Objekt selbst. Die View legt das Bitmap deckungsgleich
        ''' über die Objekt-Border (siehe AnnotationOverlayRender).</summary>
        Public Shared Function RenderAnnotationOverlay(annotation As ImageAnnotation, pixelWidth As Integer, pixelHeight As Integer) As AnnotationOverlayRender
            If annotation Is Nothing Then Return Nothing

            Dim renderAnnotation = annotation.Clone()
            renderAnnotation.RotationDegrees = 0

            ' Interne Objektauflösung (gedeckelt, Seitenverhältnis erhalten). Die Ränder sind damit in
            ' Bitmap-Pixeln bemessen, nicht in Display-Pixeln; die View rechnet sie über das zurückgegebene
            ' Objekt-Rechteck um, statt die Formel nachzubauen.
            Dim requestedLongest = CSng(Math.Max(1, Math.Max(pixelWidth, pixelHeight)))
            Dim renderScale = If(requestedLongest > MaxOverlayRenderDim, MaxOverlayRenderDim / requestedLongest, 1.0F)
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
            Dim shadowPad = If(renderAnnotation.ShadowEnabled, objSize * Clamp(renderAnnotation.ShadowBlur, 0, 100) / 100.0F * 1.8F + shadowGrow, 0.0F)
            Dim offsetX = If(renderAnnotation.ShadowEnabled, objSize * renderAnnotation.ShadowOffsetXPercent / 100.0F, 0.0F)
            Dim offsetY = If(renderAnnotation.ShadowEnabled, objSize * renderAnnotation.ShadowOffsetYPercent / 100.0F, 0.0F)
            Dim effectPad = Math.Max(glowPad, shadowPad)
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

            Using bitmap = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
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
                    Using processed = ProcessBitmapBase(bitmap, objectAdj)
                        Return New AnnotationOverlayRender With {
                            .Image = ToAvaloniaBitmap(processed),
                            .BitmapWidth = width,
                            .BitmapHeight = height,
                            .ObjectX = leftPad,
                            .ObjectY = topPad,
                            .ObjectWidth = objW,
                            .ObjectHeight = objH
                        }
                    End Using
                End If

                Return New AnnotationOverlayRender With {
                    .Image = ToAvaloniaBitmap(bitmap),
                    .BitmapWidth = width,
                    .BitmapHeight = height,
                    .ObjectX = leftPad,
                    .ObjectY = topPad,
                    .ObjectWidth = objW,
                    .ObjectHeight = objH
                }
            End Using
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
                processed = ReplaceBitmap(processed, ApplyResize(processed, adj))
                processed = ReplaceBitmap(processed, ApplyCanvasResize(processed, adj))

                Using processedBitmap = processed
                    Return ToAvaloniaBitmap(processedBitmap)
                End Using
            End Using
        End Function

        Public Shared Function ApplyGeometryAdjustments(source As SKBitmap, adj As ImageAdjustments) As Bitmap
            If source Is Nothing Then Return Nothing

            Dim processed As SKBitmap = CloneBitmap(source)
            processed = ReplaceBitmap(processed, ApplyCrop(processed, adj))
            processed = ReplaceBitmap(processed, ApplyGeometryTransforms(processed, adj))
            processed = ReplaceBitmap(processed, ApplyStraighten(processed, adj))
            processed = ReplaceBitmap(processed, ApplyResize(processed, adj))
            processed = ReplaceBitmap(processed, ApplyCanvasResize(processed, adj))

            Using processedBitmap = processed
                Return ToAvaloniaBitmap(processedBitmap)
            End Using
        End Function

        Public Shared Function BuildHistogramImage(sourcePath As String, width As Integer, height As Integer) As Bitmap
            Try
                Using original = DecodeOriented(sourcePath)
                    If original Is Nothing Then Return Nothing
                    Using histogram = RenderHistogram(original, width, height)
                        Return ToAvaloniaBitmap(histogram)
                    End Using
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        Public Shared Function BuildHistogramImage(source As SKBitmap, width As Integer, height As Integer) As Bitmap
            If source Is Nothing Then Return Nothing
            Using histogram = RenderHistogram(source, width, height)
                Return ToAvaloniaBitmap(histogram)
            End Using
        End Function

        Public Shared Function LoadPreviewSource(imagePath As String, maxDimension As Integer) As SKBitmap
            Try
                Using original = DecodeOriented(imagePath)
                    If original Is Nothing Then Return Nothing

                    Dim limit = Math.Max(256, maxDimension)
                    Dim longest = Math.Max(original.Width, original.Height)
                    If longest <= limit Then
                        Return CloneBitmap(original)
                    End If

                    Dim scale = limit / CDbl(longest)
                    Dim width = Math.Max(1, CInt(Math.Round(original.Width * scale)))
                    Dim height = Math.Max(1, CInt(Math.Round(original.Height * scale)))
                    Dim result = New SKBitmap(width, height, original.ColorType, original.AlphaType)
                    Using canvas = New SKCanvas(result)
                        canvas.Clear(SKColors.Transparent)
                        Using paint = New SKPaint With {.IsAntialias = True}
                            DrawBitmapSampled(canvas, original, New SKRect(0, 0, original.Width, original.Height), New SKRect(0, 0, width, height), SamplingHigh, paint)
                        End Using
                    End Using
                    Return result
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ' Gibt die Bildabmessungen zurück
        Public Shared Function GetImageSize(imagePath As String) As (Width As Integer, Height As Integer)
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

        Private Shared Function ApplyColorAdjustments(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If adj.Exposure = 0 AndAlso adj.Temperature = 0 AndAlso adj.Tint = 0 AndAlso adj.Saturation = 0 AndAlso
               adj.Vibrance = 0 AndAlso adj.Contrast = 0 AndAlso adj.Brightness = 0 Then
                Return source
            End If

            ' Belichtung ist ein reiner Kanal-Faktor und wandert deshalb aus der Farbmatrix in die
            ' Tonwertkurve unten: Dort greift die weiche Schulter. In der Matrix wurde alles über Weiß
            ' hart abgeschnitten - eine um +1 EV angehobene Aufnahme verlor ihre Lichter ersatzlos.
            ' Skalare Verstärkung und die (lineare) Sättigungs-/Temperaturmatrix sind vertauschbar,
            ' das Ergebnis bleibt in der Bildmitte also dasselbe.
            Dim exposureGain = CSng(Math.Pow(2.0, adj.Exposure / 100.0 * 4.0))

            ' Temperatur: warm = mehr Rot/weniger Blau, kalt = mehr Blau/weniger Rot
            Dim tempR = 1.0F + adj.Temperature / 200.0F
            Dim tempB = 1.0F - adj.Temperature / 200.0F

            ' Farbstich (Tint): grün/magenta
            Dim tintG = 1.0F + adj.Tint / 200.0F

            ' Sättigung: De-/Saturierung mit Luminanz-Gewichten
            Dim lumR = 0.299F
            Dim lumG = 0.587F
            Dim lumB = 0.114F
            Dim sat = 1.0F + adj.Saturation / 100.0F + adj.Vibrance / 200.0F
            Dim invSat = 1.0F - sat

            Dim colorMatrix = New Single() {
                (lumR * invSat + sat) * tempR, lumG * invSat * tempR, lumB * invSat * tempR, 0, 0,
                lumR * invSat, (lumG * invSat + sat) * tintG, lumB * invSat, 0, 0,
                lumR * invSat * tempB, lumG * invSat * tempB, (lumB * invSat + sat) * tempB, 0, 0,
                0, 0, 0, 1, 0
            }

            ' Kontrast: Faktor 0,25 … 1,75. Mit dem früheren Halbfaktor (0,5 … 1,5) hob der Vollausschlag
            ' die Streuung nur um gut ein Drittel an - zu zaghaft für einen Regler am Anschlag.
            Dim contrast = Math.Max(0.0F, 1.0F + adj.Contrast / 100.0F * 0.75F)
            ' Helligkeit in Tonwertstufen (80 von 255) statt der früheren 48: Der Vollausschlag verschob ein
            ' normal belichtetes Foto sonst nur um knapp ein Fünftel des Tonwertumfangs.
            Dim brightness = adj.Brightness / 100.0F * 80.0F / 255.0F

            Dim toneLut = BuildToneCurveLut(exposureGain, contrast, brightness)

            Dim colorFilter = SKColorFilter.CreateColorMatrix(colorMatrix)
            Dim toneFilter = SKColorFilter.CreateTable(IdentityByteTable, toneLut, toneLut, toneLut)
            Dim paint = New SKPaint With {.ColorFilter = colorFilter}

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using stage = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
                Using canvas = New SKCanvas(stage)
                    canvas.DrawBitmap(source, 0, 0, paint)
                End Using
                paint.Dispose()
                colorFilter.Dispose()

                Using tonePaint = New SKPaint With {.ColorFilter = toneFilter}
                    Using canvas = New SKCanvas(result)
                        canvas.DrawBitmap(stage, 0, 0, tonePaint)
                    End Using
                End Using
            End Using

            toneFilter.Dispose()

            Return result
        End Function

        ''' <summary>Grundbreite von Fuß und Schulter der Tonwertkurve, in Anteilen des Tonwertumfangs.</summary>
        Private Const ToneShoulderBase As Single = 0.12F
        Private Const ToneShoulderMax As Single = 0.4F

        ''' <summary>Belichtung, Kontrast und Helligkeit als eine gemeinsame Tonwertkurve. Der lineare Teil
        ''' bleibt unverändert - die Kurve knickt erst kurz vor Schwarz und Weiß ab und nähert sich den
        ''' Enden asymptotisch, statt dort abgeschnitten zu werden. Vorher wurde hart geklemmt: Kontrast am
        ''' Anschlag riss rund ein Fünftel der Pixel auf reines Schwarz oder Weiß, und Belichtung +50
        ''' (= +2 EV) brannte vier Fünftel des Bildes ersatzlos weiß aus. Die Schulter wird nur so weit
        ''' eingeblendet, wie die Einstellung überhaupt aus dem Tonwertumfang herausläuft - eine Einstellung,
        ''' die ohnehin nirgends anstößt, geht damit unverändert durch.</summary>
        Private Shared Function BuildToneCurveLut(exposureGain As Single, contrast As Single, brightness As Single) As Byte()
            Dim low = ToneTransfer(0.0F, exposureGain, contrast, brightness)
            Dim high = ToneTransfer(1.0F, exposureGain, contrast, brightness)
            Dim overshootHigh = Math.Max(0.0F, high - 1.0F)
            Dim overshootLow = Math.Max(0.0F, -low)

            ' Schulter und Fuß wachsen mit dem, was hinausläuft: Eine feste, schmale Schulter reicht für
            ' Kontrast und Helligkeit, presst aber bei +1 EV die halbe Tonwertskala in die obersten 12% -
            ' das bleibt optisch Weiß. Je weiter die Einstellung übersteuert, desto früher setzt die
            ' Kurve an und desto mehr Zeichnung bleibt in Lichtern bzw. Tiefen.
            Dim shoulder = Clamp(ToneShoulderBase + 0.5F * overshootHigh, ToneShoulderBase, ToneShoulderMax)
            Dim toe = Clamp(ToneShoulderBase + 0.5F * overshootLow, ToneShoulderBase, ToneShoulderMax)
            Dim rolloff = Clamp((overshootHigh + overshootLow) / ToneShoulderBase, 0.0F, 1.0F)

            Dim lut = New Byte(255) {}
            For i As Integer = 0 To 255
                Dim y = ToneTransfer(i / 255.0F, exposureGain, contrast, brightness)
                Dim hard = Clamp(y, 0.0F, 1.0F)
                Dim soft = SoftShoulder(y, toe, shoulder)
                lut(i) = ClampToByte(255.0F * (hard + (soft - hard) * rolloff))
            Next
            Return lut
        End Function

        Private Shared Function ToneTransfer(x As Single, exposureGain As Single, contrast As Single, brightness As Single) As Single
            Dim y = x * exposureGain
            y = (y - 0.5F) * contrast + 0.5F
            Return y + brightness
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
            Return ReplaceBitmap(processed, ApplyAnnotations(processed, adj))
        End Function

        ' Alle Pipeline-Schritte AUSSER dem Einzeichnen der Objekte (Annotations). Wird von
        ' ProcessBitmap sowie vom Basis-Cache in ApplyAdjustments(source As SKBitmap, ...) genutzt.
        Private Shared Function ProcessBitmapBase(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim processed As SKBitmap = CloneBitmap(source)

            processed = ReplaceBitmap(processed, ApplyRetouch(processed, adj))
            processed = ReplaceBitmap(processed, ApplyCrop(processed, adj))
            processed = ReplaceBitmap(processed, ApplyGeometryTransforms(processed, adj))
            processed = ReplaceBitmap(processed, ApplyStraighten(processed, adj))
            processed = ReplaceBitmap(processed, ApplyResize(processed, adj))
            processed = ReplaceBitmap(processed, ApplyCanvasResize(processed, adj))
            ' Die Umkehr steht VOR allen Farbanpassungen: Belichtung, Weißabgleich, Kurven und Filter
            ' sollen auf dem fertigen Positiv arbeiten - auf dem Negativ wären sie seitenverkehrt
            ' (Aufhellen würde abdunkeln) und für den Nutzer unbrauchbar.
            processed = ReplaceBitmap(processed, ApplyFilmNegative(processed, adj))
            processed = ReplaceBitmap(processed, ApplyColorAdjustments(processed, adj))
            processed = ReplaceBitmap(processed, ApplyTonalLUT(processed, adj))
            processed = ReplaceBitmap(processed, ApplyCurve(processed, adj))
            processed = ReplaceBitmap(processed, ApplyHsl(processed, adj))
            processed = ReplaceBitmap(processed, ApplySplitToning(processed, adj))
            processed = ReplaceBitmap(processed, ApplyFilterPreset(processed, adj.FilterPreset, adj.FilterStrength / 100.0F))
            processed = ReplaceBitmap(processed, ApplyCubeLut(processed, adj.LutPath, adj.LutStrength / 100.0F))

            If adj.Clarity <> 0 Then
                processed = ReplaceBitmap(processed, ApplyClarity(processed, adj.Clarity / 100.0F))
            End If
            If adj.[Structure] <> 0 Then
                processed = ReplaceBitmap(processed, ApplyStructure(processed, adj.[Structure] / 100.0F))
            End If
            If adj.Haze <> 0 Then
                processed = ReplaceBitmap(processed, ApplyHaze(processed, adj.Haze / 100.0F))
            End If

            If adj.NoiseReduction > 0 Then
                If adj.NoiseReductionMethod = NoiseReductionMethod.Median Then
                    processed = ReplaceBitmap(processed, ApplyMedianBlur(processed, adj.NoiseReduction / 100.0F))
                Else
                    processed = ReplaceBitmap(processed, ApplyNoiseReduction(processed, adj.NoiseReduction / 100.0F))
                End If
            End If
            If adj.DustScratches <> 0 Then
                processed = ReplaceBitmap(processed, ApplyDustScratches(processed, adj.DustScratches / 100.0F))
            End If
            If adj.Glow <> 0 Then
                processed = ReplaceBitmap(processed, ApplyImageGlow(processed, adj.Glow / 100.0F))
            End If

            If adj.Sharpness > 0 Then
                processed = ReplaceBitmap(processed, ApplySharpness(processed, adj.Sharpness / 100.0F))
            End If

            If adj.Vignette <> 0 Then
                processed = ReplaceBitmap(processed, ApplyVignette(processed, adj.Vignette / 100.0F, adj.VignetteTransition, adj.VignetteRoundness, adj.VignetteFeather, adj.VignetteCenterX, adj.VignetteCenterY))
            End If

            If adj.Grain > 0 Then
                processed = ReplaceBitmap(processed, ApplyGrain(processed, adj.Grain / 100.0F))
            End If
            If adj.AddNoise > 0 Then
                processed = ReplaceBitmap(processed, ApplyAddNoise(processed, adj.AddNoise / 100.0F))
            End If

            If adj.BorderSize > 0 Then
                processed = ReplaceBitmap(processed, ApplyBorder(processed, adj.BorderSize / 100.0F, adj.BorderColor, adj.BorderCornerRadius / 100.0F, adj.BorderEffect))
            End If

            ' Auswahl-Skopus: Anpassungen nur INNERHALB der aktiven Auswahl wirken lassen. `source` ist die
            ' unveränderte Eingabe (= "vorher"); nur wenn keine Geometrie aktiv ist, entspricht der Bildraum
            ' 1:1 dem Basisbild-Raum (evtl. für die Vorschau herunterskaliert), in dem die Maske gespeichert
            ' ist - sonst würde sie nicht zum verarbeiteten Bild passen (siehe SelectionGeometryIsNeutral).
            If adj.HasActiveSelection AndAlso SelectionGeometryIsNeutral(adj) Then
                Dim scopeMask = BuildSelectionScopeMask(adj, processed.Width, processed.Height)
                If scopeMask IsNot Nothing Then
                    Using scopeMask
                        processed = ReplaceBitmap(processed, CompositeSelectionScoped(source, processed, scopeMask))
                    End Using
                End If
            End If

            Return processed
        End Function

        ''' <summary>Auswahl-Skopus ist nur sicher, solange keine Geometrie aktiv ist: die Maske liegt im
        ''' Basisbild-Raum, Crop/Resize/Rotate/Flip/Straighten/Canvas würden den Bildraum verschieben und die
        ''' Maske fehlausrichten. Der Editor löscht die Auswahl beim Anwenden von Geometrie; dieser Guard ist
        ''' die zusätzliche Absicherung (fällt sonst auf globale Anpassung zurück).</summary>
        Private Shared Function SelectionGeometryIsNeutral(adj As ImageAdjustments) As Boolean
            Return adj.RotationDegrees = 0 AndAlso Not adj.FlipHorizontal AndAlso Not adj.FlipVertical AndAlso
                   Math.Abs(adj.StraightenDegrees) < 0.01F AndAlso
                   adj.CropLeftPercent = 0 AndAlso adj.CropTopPercent = 0 AndAlso
                   adj.CropRightPercent = 0 AndAlso adj.CropBottomPercent = 0 AndAlso
                   adj.ResizeWidth <= 0 AndAlso adj.ResizeHeight <= 0 AndAlso
                   adj.CanvasWidth <= 0 AndAlso adj.CanvasHeight <= 0
        End Function

        ''' <summary>Baut aus der aktiven Auswahl eine Alpha8-Maske in der Größe des verarbeiteten Bildes
        ''' (<paramref name="targetW"/>×<paramref name="targetH"/>). Unregelmäßige Auswahlen kommen aus der
        ''' gespeicherten Alpha8-Maske (Basisbild-Pixel, per Nearest-Sampling auf die Zielgröße gebracht),
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
            If mask Is Nothing OrElse adj.SelectionFeatherPixels <= 0.05F Then Return mask

            Dim scale = If(adj.SourceWidthPixels > 0, targetW / CSng(adj.SourceWidthPixels), 1.0F)
            Dim blurred = BlurAlphaMask(mask, adj.SelectionFeatherPixels * scale)
            If blurred Is Nothing Then Return mask
            mask.Dispose()
            Return blurred
        End Function

        Private Shared Function BuildSelectionScopeMaskCore(adj As ImageAdjustments, targetW As Integer, targetH As Integer) As SKBitmap
            If targetW <= 0 OrElse targetH <= 0 Then Return Nothing
            Dim baseW = adj.SourceWidthPixels
            Dim scale = If(baseW > 0, targetW / CDbl(baseW), 1.0)

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
                            Dim baseY = If(scale > 0, CInt(ty / scale), ty)
                            Dim ly = baseY - adj.SelectionMaskTop
                            If ly < 0 OrElse ly >= dH Then Continue For
                            Dim mRow = ty * mStride, dRow = ly * dStride
                            For tx = 0 To targetW - 1
                                Dim baseX = If(scale > 0, CInt(tx / scale), tx)
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

            If adj.HasActiveSelection Then
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

        Private Shared Function Clamp3(value As Integer, lo As Integer, hi As Integer) As Integer
            Return Math.Max(lo, Math.Min(hi, value))
        End Function

        ''' <summary>Mischt <paramref name="adjusted"/> (angepasst) über <paramref name="baseline"/> (unverändert)
        ''' anhand der Alpha8-<paramref name="mask"/>: out = baseline·(255−m) + adjusted·m. Alle drei müssen
        ''' dieselben Maße haben. Fällt bei ungeeignetem Farbformat auf eine Kopie von adjusted zurück.</summary>
        Private Shared Function CompositeSelectionScoped(baseline As SKBitmap, adjusted As SKBitmap, mask As SKBitmap) As SKBitmap
            Dim w = adjusted.Width, h = adjusted.Height
            Dim aStride As Integer, bStride As Integer
            Dim aBuf As Byte() = Nothing, bBuf As Byte() = Nothing
            If Not TryBorrowBgraBuffer(adjusted, aBuf, aStride) OrElse Not TryBorrowBgraBuffer(baseline, bBuf, bStride) Then
                Return CloneBitmap(adjusted)
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
        ' gecachte Basis wiederverwendet werden.
        Private Shared Function ComputeBaseKey(adj As ImageAdjustments) As String
            Return String.Join("|", New Object() {
                adj.Exposure, adj.Brightness, adj.Contrast, adj.Saturation, adj.Highlights, adj.ShadowsLevel,
                adj.Whites, adj.Blacks, adj.Temperature, adj.Tint, adj.Sharpness, adj.NoiseReduction, adj.NoiseReductionMethod,
                adj.DustScratches, adj.Haze, adj.AddNoise, adj.[Structure], adj.Glow,
                adj.Vibrance, adj.Vignette, adj.VignetteTransition, adj.VignetteRoundness, adj.VignetteFeather,
                adj.VignetteCenterX, adj.VignetteCenterY, adj.Grain, adj.BorderSize, adj.BorderColor,
                adj.BorderCornerRadius, adj.BorderEffect, adj.Clarity,
                adj.NegativeEnabled, adj.NegativeMonochrome, adj.NegativeBaseColor, adj.NegativeDensityColor, adj.NegativeGamma,
                adj.CurveRgbPoints, adj.CurveRedPoints, adj.CurveGreenPoints, adj.CurveBluePoints, adj.CurveLuminancePoints,
                adj.RedHue, adj.RedSaturation, adj.RedLuminance, adj.OrangeHue, adj.OrangeSaturation, adj.OrangeLuminance,
                adj.YellowHue, adj.YellowSaturation, adj.YellowLuminance, adj.GreenHue, adj.GreenSaturation, adj.GreenLuminance,
                adj.AquaHue, adj.AquaSaturation, adj.AquaLuminance, adj.BlueHue, adj.BlueSaturation, adj.BlueLuminance,
                adj.PurpleHue, adj.PurpleSaturation, adj.PurpleLuminance, adj.MagentaHue, adj.MagentaSaturation, adj.MagentaLuminance,
                adj.SplitToningShadowHue, adj.SplitToningShadowSaturation,
                adj.SplitToningHighlightHue, adj.SplitToningHighlightSaturation, adj.SplitToningBalance,
                adj.RotationDegrees, adj.StraightenDegrees, adj.StraightenExpandCanvas, adj.FlipHorizontal, adj.FlipVertical,
                adj.CropLeftPercent, adj.CropTopPercent, adj.CropRightPercent, adj.CropBottomPercent,
                adj.ResizeWidth, adj.ResizeHeight, adj.LockResizeAspect, adj.ResizeInterpolation,
                adj.CanvasWidth, adj.CanvasHeight, adj.LockCanvasAspect, adj.CanvasAnchor, adj.CanvasBackgroundColor,
                adj.FilterPreset, adj.FilterStrength, adj.LutPath, adj.LutStrength,
                adj.HasActiveSelection, adj.SelectionXPercent, adj.SelectionYPercent,
                adj.SelectionWidthPercent, adj.SelectionHeightPercent, adj.SelectionShapeMode,
                adj.SelectionMaskLeft, adj.SelectionMaskTop, adj.SelectionMaskRight, adj.SelectionMaskBottom,
                If(String.IsNullOrEmpty(adj.SelectionMaskPngBase64), 0, adj.SelectionMaskPngBase64.Length),
                adj.SelectionFeatherPixels,
                String.Join(";", adj.RetouchSpots.Select(Function(s) $"{s.XPixels},{s.YPixels},{s.RadiusPixels},{s.StrengthPercent},{s.OpacityPercent},{s.FlowPercent},{If(s.Mode, "")},{s.SourceXPixels},{s.SourceYPixels},{s.StrokeId}"))
            })
        End Function

        Private Shared Function ReplaceBitmap(oldBitmap As SKBitmap, newBitmap As SKBitmap) As SKBitmap
            If newBitmap Is Nothing OrElse Object.ReferenceEquals(oldBitmap, newBitmap) Then Return oldBitmap
            oldBitmap.Dispose()
            Return newBitmap
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

        ''' <summary>
        ''' Retuschiert die gesetzten Punkte. Punkte mit Klonquelle kopieren die Textur von dort
        ''' herüber; Punkte ohne Quelle arbeiten als Healing-Pinsel: sie ziehen Textur vom Rand des
        ''' Pinsels nach innen und gleichen sie farblich an die Umgebung an. Dadurch lassen sich kleine
        ''' Flecken und einfache störende Bildteile entfernen, ohne nur eine flache Mischfarbe über die
        ''' Stelle zu legen.
        '''
        ''' Gelesen wird stets aus <paramref name="source"/>, dem unretuschierten Bild. Sonst zöge ein
        ''' Zug, dessen Quelle über bereits geklonte Stellen läuft, seine eigenen Kopien mit.
        ''' </summary>
        Private Shared Function ApplyRetouch(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If adj.RetouchSpots Is Nothing OrElse adj.RetouchSpots.Count = 0 Then Return source

            Dim result = CloneBitmap(source)

            Using canvas = New SKCanvas(result)
                Dim pendingHeal As New List(Of RetouchSpot)()
                Dim pendingHealStrokeId As Integer? = Nothing
                For Each spot In adj.RetouchSpots
                    If IsHealingSpot(spot) Then
                        If pendingHeal.Count > 0 AndAlso pendingHealStrokeId.HasValue AndAlso spot.StrokeId <> pendingHealStrokeId.Value Then
                            FlushHealingSpots(result, canvas, pendingHeal, adj.SourceWidthPixels, adj.SourceHeightPixels)
                            pendingHeal.Clear()
                        End If
                        pendingHeal.Add(spot)
                        pendingHealStrokeId = spot.StrokeId
                        Continue For
                    End If

                    If pendingHeal.Count > 0 Then
                        FlushHealingSpots(result, canvas, pendingHeal, adj.SourceWidthPixels, adj.SourceHeightPixels)
                        pendingHeal.Clear()
                        pendingHealStrokeId = Nothing
                    End If
                    DrawRetouchSpot(result, source, canvas, spot, adj.SourceWidthPixels, adj.SourceHeightPixels)
                Next
                If pendingHeal.Count > 0 Then
                    FlushHealingSpots(result, canvas, pendingHeal, adj.SourceWidthPixels, adj.SourceHeightPixels)
                End If
            End Using
            Return result
        End Function

        Private Shared Sub FlushHealingSpots(result As SKBitmap, canvas As SKCanvas, pendingHeal As List(Of RetouchSpot),
                                             sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If pendingHeal Is Nothing OrElse pendingHeal.Count = 0 Then Return
            If pendingHeal.Count = 1 Then
                DrawRetouchSpot(result, result, canvas, pendingHeal(0), sourceWidthPixels, sourceHeightPixels)
            Else
                DrawHealingRegion(result, canvas, result, pendingHeal, sourceWidthPixels, sourceHeightPixels)
            End If
        End Sub

        Public Shared Sub ApplyRetouchSpotInPlace(target As SKBitmap, sampleSource As SKBitmap, spot As RetouchSpot,
                                                  sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If target Is Nothing OrElse sampleSource Is Nothing OrElse spot Is Nothing Then Return
            Using canvas = New SKCanvas(target)
                If IsHealingSpot(spot) Then
                    DrawRetouchSpot(target, target, canvas, spot, sourceWidthPixels, sourceHeightPixels)
                Else
                    DrawRetouchSpot(target, sampleSource, canvas, spot, sourceWidthPixels, sourceHeightPixels)
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
            ElseIf String.Equals(spot.Mode, "Heal", StringComparison.OrdinalIgnoreCase) Then
                ' Der Reparaturpinsel arbeitet stroke-akkumuliert: spätere Punkte sollen bereits
                ' reparierte Pixel als Umgebung sehen.
                DrawHealingSpot(result, canvas, result, cx, cy, radius, alphaFactor)
            Else
                ' Verwischen soll auf dem bereits retuschierten Ergebnis aufbauen, damit nach einer
                ' Reparatur nicht wieder Textur aus dem Ursprungsbild "hineingewischt" wird.
                Dim fill = AverageSurroundingColor(result, cx, cy, radius)
                If fill.HasValue Then DrawSoftDisc(canvas, cx, cy, radius, fill.Value, alphaFactor)
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

        ''' Healing ohne explizite Quelle: Statt radial Pixel vom Rand nach innen zu ziehen
        ''' (Speichen/Pusteblumenmuster) wird eine kohärente Nachbarfläche außerhalb des Pinsels
        ''' gewählt und weich eingestempelt. Gibt es keine brauchbare Fläche, fällt der Pinsel auf
        ''' eine weiche Umgebungsfarbe zurück.
        Private Shared Sub DrawHealingSpot(result As SKBitmap, canvas As SKCanvas, source As SKBitmap, cx As Single, cy As Single, radius As Single, flow As Single)
            If flow <= 0.001F OrElse radius <= 0.5F Then Return

            Dim surrounding = AverageSurroundingColor(source, cx, cy, radius, 1.35F, 2.4F)
            If Not surrounding.HasValue Then Return

            Dim sample = FindHealingPatch(source, cx, cy, radius, surrounding.Value)
            If sample.Found Then
                DrawAdjustedHealingPatch(result, source, cx, cy, sample.Center.X, sample.Center.Y, radius, flow, surrounding.Value, sample.Average)
                Return
            End If

            DrawSoftDisc(canvas, cx, cy, radius, surrounding.Value, flow)
        End Sub

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

            Dim scaled As New List(Of (X As Single, Y As Single, Radius As Single, Flow As Single))()
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
                Dim flow = Clamp(spot.FlowPercent, 0, 100) / 100.0F *
                           Clamp(spot.OpacityPercent, 0, 100) / 100.0F *
                           Clamp(spot.StrengthPercent, 0, 100) / 100.0F
                If flow <= 0.001F Then Continue For
                scaled.Add((cx, cy, radius, flow))
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

            Using mask = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using maskCanvas = New SKCanvas(mask)
                    maskCanvas.Clear(SKColors.Transparent)
                    maskCanvas.Translate(-left, -top)
                    For Each s In scaled
                        Using shader = SKShader.CreateRadialGradient(New SKPoint(s.X, s.Y), s.Radius,
                                                                     {SKColors.White.WithAlpha(CByte(255 * s.Flow)),
                                                                      SKColors.White.WithAlpha(CByte(255 * s.Flow)),
                                                                      SKColors.Transparent},
                                                                     RetouchFeatherStops, SKShaderTileMode.Clamp)
                            Using paint = New SKPaint With {.Shader = shader, .IsAntialias = True, .BlendMode = SKBlendMode.SrcOver}
                                maskCanvas.DrawCircle(s.X, s.Y, s.Radius, paint)
                            End Using
                        End Using
                    Next
                End Using

                If DrawInpaintedHealingRegion(result, mask, left, top) Then
                    Return
                End If

                Dim targetAverage = AverageRegionSurroundingColor(source, mask, left, top, maxRadius)
                If Not targetAverage.HasValue Then
                    For Each s In scaled
                        Dim fill = AverageSurroundingColor(source, s.X, s.Y, s.Radius)
                        If fill.HasValue Then DrawSoftDisc(canvas, s.X, s.Y, s.Radius, fill.Value, s.Flow)
                    Next
                    Return
                End If

                Dim sample = FindHealingRegionPatch(source, mask, left, top, width, height, maxRadius, targetAverage.Value)
                If Not sample.Found Then
                    For Each s In scaled
                        DrawSoftDisc(canvas, s.X, s.Y, s.Radius, targetAverage.Value, s.Flow)
                    Next
                    Return
                End If

                DrawAdjustedHealingRegion(result, source, mask, left, top, sample.Left, sample.Top,
                                          targetAverage.Value, sample.Average)
            End Using
        End Sub

        Private Shared Function DrawInpaintedHealingRegion(result As SKBitmap, mask As SKBitmap,
                                                           targetLeft As Integer, targetTop As Integer) As Boolean
            If result Is Nothing OrElse mask Is Nothing OrElse mask.Width <= 0 OrElse mask.Height <= 0 Then Return False

            Dim width = mask.Width
            Dim height = mask.Height
            Dim count = width * height
            Dim maskAlpha(count - 1) As Byte
            Dim filled(count - 1) As Boolean
            Dim queued(count - 1) As Boolean
            Dim maskedCount = 0

            Using work = CloneBitmap(result)
                For maskY = 0 To height - 1
                    Dim y = targetTop + maskY
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        Dim alpha = mask.GetPixel(mx, maskY).Alpha
                        maskAlpha(index) = NormalizeHealingMaskAlpha(alpha)
                        Dim isMasked = alpha > 8 AndAlso y >= 0 AndAlso y < result.Height AndAlso
                                       targetLeft + mx >= 0 AndAlso targetLeft + mx < result.Width
                        filled(index) = Not isMasked
                        If isMasked Then maskedCount += 1
                    Next
                Next

                If maskedCount = 0 Then Return False

                If DrawPatchBasedInpaintedHealingRegion(result, work, maskAlpha, targetLeft, targetTop, width, height) Then
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
                    Dim x = targetLeft + mx
                    Dim y = targetTop + maskY
                    Dim average = AverageUnmaskedRays(work, maskAlpha, targetLeft, targetTop, width, height, mx, maskY)
                    If Not average.HasValue Then
                        average = AverageFilledNeighborhood(work, filled, targetLeft, targetTop, width, height, mx, maskY)
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
                If ShouldSmoothInpaintedRegion(work, maskAlpha, targetLeft, targetTop, width, height) Then
                    SmoothInpaintedRegion(work, maskAlpha, targetLeft, targetTop, width, height)
                End If

                For maskY = 0 To height - 1
                    Dim y = targetTop + maskY
                    If y < 0 OrElse y >= result.Height Then Continue For
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        Dim alpha = maskAlpha(index)
                        If alpha <= 8 OrElse Not filled(index) Then Continue For
                        Dim x = targetLeft + mx
                        If x < 0 OrElse x >= result.Width Then Continue For

                        Dim localAlpha = Clamp(alpha / 255.0F, 0.0F, 1.0F)
                        If localAlpha <= 0.001F Then Continue For
                        Dim target = result.GetPixel(x, y)
                        Dim repaired = work.GetPixel(x, y)
                        result.SetPixel(x, y, New SKColor(
                            BlendByte(target.Red, repaired.Red, localAlpha),
                            BlendByte(target.Green, repaired.Green, localAlpha),
                            BlendByte(target.Blue, repaired.Blue, localAlpha),
                            BlendByte(target.Alpha, repaired.Alpha, localAlpha)))
                    Next
                Next
            End Using

            Return True
        End Function

        Private Shared Function DrawPatchBasedInpaintedHealingRegion(result As SKBitmap, work As SKBitmap,
                                                                     maskAlpha As Byte(),
                                                                     targetLeft As Integer, targetTop As Integer,
                                                                     width As Integer, height As Integer) As Boolean
            If result Is Nothing OrElse work Is Nothing OrElse maskAlpha Is Nothing OrElse width <= 0 OrElse height <= 0 Then Return False

            Dim known(width * height - 1) As Boolean
            Dim remaining = 0
            For i = 0 To known.Length - 1
                known(i) = maskAlpha(i) <= 8
                If Not known(i) Then remaining += 1
            Next
            If remaining = 0 Then Return False

            Dim repairExtent = Math.Max(width, height)
            Dim patchRadius = If(repairExtent > 180, 4, 5)
            Dim maxPasses = Math.Min(repairExtent + patchRadius * 2, 96)
            Dim maxPatchCopiesPerPass = If(repairExtent > 240, 36, If(repairExtent > 120, 48, 64))
            Dim repaired = 0

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
                    Dim mx = index Mod width
                    Dim maskY = index \ width
                    Dim sourcePatch = FindBestHealingSourcePatch(work, maskAlpha, known, targetLeft, targetTop,
                                                                 width, height, mx, maskY, patchRadius)
                    If Not sourcePatch.Found Then Continue For

                    changedThisPass += CopyHealingPatch(work, maskAlpha, known, targetLeft, targetTop,
                                                        width, height, mx, maskY, sourcePatch.X, sourcePatch.Y, patchRadius)
                    patchCopiesThisPass += 1
                    If patchCopiesThisPass >= maxPatchCopiesPerPass Then Exit For
                Next

                If changedThisPass = 0 Then Exit For
                repaired += changedThisPass
                remaining -= changedThisPass
                If remaining <= 0 Then Exit For
            Next

            If repaired = 0 Then Return False
            BlendInpaintedBoundary(work, maskAlpha, targetLeft, targetTop, width, height)
            For maskY = 0 To height - 1
                Dim y = targetTop + maskY
                If y < 0 OrElse y >= result.Height Then Continue For
                For mx = 0 To width - 1
                    Dim index = maskY * width + mx
                    If maskAlpha(index) <= 8 OrElse Not known(index) Then Continue For
                    Dim x = targetLeft + mx
                    If x < 0 OrElse x >= result.Width Then Continue For

                    Dim localAlpha = Clamp(maskAlpha(index) / 255.0F, 0.0F, 1.0F)
                    Dim target = result.GetPixel(x, y)
                    Dim repairedColor = work.GetPixel(x, y)
                    result.SetPixel(x, y, New SKColor(
                        BlendByte(target.Red, repairedColor.Red, localAlpha),
                        BlendByte(target.Green, repairedColor.Green, localAlpha),
                        BlendByte(target.Blue, repairedColor.Blue, localAlpha),
                        BlendByte(target.Alpha, repairedColor.Alpha, localAlpha)))
                Next
            Next

            Return True
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

        Private Shared Function FindBestHealingSourcePatch(work As SKBitmap, maskAlpha As Byte(), known As Boolean(),
                                                           targetLeft As Integer, targetTop As Integer,
                                                           width As Integer, height As Integer,
                                                           mx As Integer, my As Integer,
                                                           patchRadius As Integer) As (X As Integer, Y As Integer, Found As Boolean)
            Dim targetX = targetLeft + mx
            Dim targetY = targetTop + my
            Dim extent = Math.Max(width, height)
            Dim searchRadius = Math.Min(88, Math.Max(28, extent \ 3))
            Dim stepSize = If(extent > 240, 12, If(extent > 120, 8, 6))
            Dim bestX = 0
            Dim bestY = 0
            Dim bestScore = Double.MaxValue
            Dim found = False
            Dim evaluated = 0
            Dim maxEvaluations = If(extent > 240, 180, If(extent > 120, 260, 360))

            Dim minY = Math.Max(patchRadius, targetY - searchRadius)
            Dim maxY = Math.Min(work.Height - patchRadius - 1, targetY + searchRadius)
            Dim minX = Math.Max(patchRadius, targetX - searchRadius)
            Dim maxX = Math.Min(work.Width - patchRadius - 1, targetX + searchRadius)
            Dim offsetSeed = Math.Abs((targetX * 31 + targetY * 17) Mod stepSize)

            For sy = minY + offsetSeed To maxY Step stepSize
                For sx = minX + ((offsetSeed * 3) Mod stepSize) To maxX Step stepSize
                    If Math.Abs(sx - targetX) <= patchRadius AndAlso Math.Abs(sy - targetY) <= patchRadius Then Continue For
                    If Not IsOriginalKnownPatch(maskAlpha, targetLeft, targetTop, width, height, sx, sy, patchRadius) Then Continue For

                    Dim score = HealingPatchScore(work, maskAlpha, known, targetLeft, targetTop,
                                                  width, height, mx, my, sx, sy, patchRadius)
                    evaluated += 1
                    If score < bestScore Then
                        bestScore = score
                        bestX = sx
                        bestY = sy
                        found = True
                    End If
                    If evaluated >= maxEvaluations Then Exit For
                Next
                If evaluated >= maxEvaluations Then Exit For
            Next

            If found AndAlso extent <= 160 Then
                For sy = Math.Max(patchRadius, bestY - 4) To Math.Min(work.Height - patchRadius - 1, bestY + 4) Step 2
                    For sx = Math.Max(patchRadius, bestX - 4) To Math.Min(work.Width - patchRadius - 1, bestX + 4) Step 2
                        If Not IsOriginalKnownPatch(maskAlpha, targetLeft, targetTop, width, height, sx, sy, patchRadius) Then Continue For
                        Dim score = HealingPatchScore(work, maskAlpha, known, targetLeft, targetTop,
                                                      width, height, mx, my, sx, sy, patchRadius)
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
                                                  patchRadius As Integer) As Double
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
                    score += ColorDistanceSquared(work.GetPixel(tx, ty), work.GetPixel(px, py)) * weight
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
                                                 patchRadius As Integer) As Integer
            Dim copied = 0
            Dim targetX = targetLeft + mx
            Dim targetY = targetTop + my

            For oy = -patchRadius To patchRadius
                Dim oySq = oy * oy
                Dim y = targetY + oy
                Dim py = sy + oy
                If y < 0 OrElse y >= work.Height OrElse py < 0 OrElse py >= work.Height Then Continue For
                For ox = -patchRadius To patchRadius
                    If ox * ox + oySq > patchRadius * patchRadius Then Continue For
                    Dim lx = mx + ox
                    Dim ly = my + oy
                    If lx < 0 OrElse ly < 0 OrElse lx >= width OrElse ly >= height Then Continue For
                    Dim index = ly * width + lx
                    If known(index) OrElse maskAlpha(index) <= 8 Then Continue For

                    Dim x = targetX + ox
                    Dim px = sx + ox
                    If x < 0 OrElse x >= work.Width OrElse px < 0 OrElse px >= work.Width Then Continue For

                    work.SetPixel(x, y, work.GetPixel(px, py))
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
                For i = 0 To 31
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

        Private Shared Function FindHealingPatch(source As SKBitmap, cx As Single, cy As Single, radius As Single,
                                                 targetColor As SKColor) As (Center As SKPoint, Average As SKColor, Found As Boolean)
            If source Is Nothing OrElse source.Width <= 0 OrElse source.Height <= 0 Then Return (New SKPoint(0, 0), SKColors.Transparent, False)

            Dim patchRadius = Math.Max(2.0F, radius * 0.82F)
            Dim distances = {Math.Max(radius * 2.25F, radius + 8.0F), radius * 3.0F, radius * 3.85F}
            Dim targetStats = SampleRingPatchStats(source, cx, cy, radius * 1.05F, radius * 1.75F)
            Dim best = New SKPoint(0, 0)
            Dim bestAverage = SKColors.Transparent
            Dim bestScore = Double.MaxValue
            Dim found = False

            For Each sampleDistance In distances
                For i = 0 To 15
                    Dim angle = (Math.PI * 2.0 * i) / 16.0
                    Dim candidate = New SKPoint(cx + CSng(Math.Cos(angle) * sampleDistance),
                                                cy + CSng(Math.Sin(angle) * sampleDistance))

                    If candidate.X - patchRadius < 0 OrElse candidate.Y - patchRadius < 0 OrElse
                       candidate.X + patchRadius >= source.Width OrElse candidate.Y + patchRadius >= source.Height Then Continue For

                    Dim overlapDx = candidate.X - cx
                    Dim overlapDy = candidate.Y - cy
                    If overlapDx * overlapDx + overlapDy * overlapDy < (radius * 2.12F) * (radius * 2.12F) Then Continue For

                    Dim boundaryScore = HealingBoundaryScore(source, cx, cy, candidate.X, candidate.Y, radius)
                    If boundaryScore = Double.MaxValue Then Continue For

                    Dim quickStats = SamplePatchStats(source, candidate.X, candidate.Y, Math.Max(2.0F, radius * 0.38F))
                    If quickStats.Count <= 0 Then Continue For
                    Dim outlierPenalty = PatchOutlierPenalty(source, candidate.X, candidate.Y, patchRadius, targetColor)

                    Dim stats = quickStats
                    If boundaryScore < bestScore * 0.8 OrElse Not found Then
                        stats = SamplePatchStats(source, candidate.X, candidate.Y, patchRadius)
                        If stats.Count <= 0 Then Continue For
                    End If

                    Dim colorDistance = ColorDistanceSquared(stats.Average, targetColor)
                    Dim varianceDelta = If(targetStats.Count > 0, Math.Abs(stats.Variance - targetStats.Variance), stats.Variance)
                    Dim score = boundaryScore * 1.85 + colorDistance * 0.45 + varianceDelta * 0.06 + outlierPenalty
                    If score < bestScore Then
                        bestScore = score
                        best = candidate
                        bestAverage = stats.Average
                        found = True
                    End If
                Next
            Next

            Return (best, bestAverage, found)
        End Function

        Private Shared Function HealingBoundaryScore(source As SKBitmap, cx As Single, cy As Single,
                                                     sx As Single, sy As Single, radius As Single) As Double
            Dim samples = 20
            Dim score = 0.0
            Dim count = 0
            For i = 0 To samples - 1
                Dim angle = (Math.PI * 2.0 * i) / samples
                Dim dx = CSng(Math.Cos(angle))
                Dim dy = CSng(Math.Sin(angle))
                Dim tx = CInt(Math.Round(cx + dx * radius * 1.08F))
                Dim ty = CInt(Math.Round(cy + dy * radius * 1.08F))
                Dim px = CInt(Math.Round(sx + dx * radius * 0.92F))
                Dim py = CInt(Math.Round(sy + dy * radius * 0.92F))
                If tx < 0 OrElse ty < 0 OrElse tx >= source.Width OrElse ty >= source.Height OrElse
                   px < 0 OrElse py < 0 OrElse px >= source.Width OrElse py >= source.Height Then Continue For

                score += ColorDistanceSquared(source.GetPixel(tx, ty), source.GetPixel(px, py))
                count += 1
            Next
            If count = 0 Then Return Double.MaxValue
            Return score / count
        End Function

        Private Shared Sub DrawAdjustedHealingPatch(result As SKBitmap, source As SKBitmap,
                                                    cx As Single, cy As Single, sx As Single, sy As Single,
                                                    radius As Single, flow As Single,
                                                    targetAverage As SKColor, sourceAverage As SKColor)
            If result Is Nothing OrElse source Is Nothing OrElse flow <= 0.001F Then Return

            Dim left = Math.Max(0, CInt(Math.Floor(cx - radius)))
            Dim top = Math.Max(0, CInt(Math.Floor(cy - radius)))
            Dim right = Math.Min(result.Width - 1, CInt(Math.Ceiling(cx + radius)))
            Dim bottom = Math.Min(result.Height - 1, CInt(Math.Ceiling(cy + radius)))
            If right < left OrElse bottom < top Then Return

            Dim radiusSq = radius * radius
            Dim hardRadius = radius * 0.74F
            Dim hardSq = hardRadius * hardRadius
            Dim featherRange = Math.Max(0.001F, radius - hardRadius)
            Dim dr = CInt(targetAverage.Red) - CInt(sourceAverage.Red)
            Dim dg = CInt(targetAverage.Green) - CInt(sourceAverage.Green)
            Dim db = CInt(targetAverage.Blue) - CInt(sourceAverage.Blue)
            dr = Math.Max(-56, Math.Min(56, dr))
            dg = Math.Max(-56, Math.Min(56, dg))
            db = Math.Max(-56, Math.Min(56, db))

            For y = top To bottom
                Dim dy = CSng(y) - cy
                For x = left To right
                    Dim dx = CSng(x) - cx
                    Dim distSq = dx * dx + dy * dy
                    If distSq > radiusSq Then Continue For

                    Dim srcX = CInt(Math.Round(sx + dx))
                    Dim srcY = CInt(Math.Round(sy + dy))
                    If srcX < 0 OrElse srcY < 0 OrElse srcX >= source.Width OrElse srcY >= source.Height Then Continue For

                    Dim distance = CSng(Math.Sqrt(distSq))
                    Dim localAlpha = flow
                    If distSq > hardSq Then
                        localAlpha *= Clamp((radius - distance) / featherRange, 0.0F, 1.0F)
                    End If
                    Dim sample = source.GetPixel(srcX, srcY)
                    sample = SuppressHealingOutlier(sample, sourceAverage, targetAverage)
                    Dim target = result.GetPixel(x, y)
                    If ColorDistanceSquared(target, targetAverage) > 90 * 90 Then
                        localAlpha = Math.Max(localAlpha, flow * 0.9F)
                    End If
                    If localAlpha <= 0.001F Then Continue For

                    result.SetPixel(x, y, New SKColor(
                        BlendByte(target.Red, ClampByte(CInt(sample.Red) + dr), localAlpha),
                        BlendByte(target.Green, ClampByte(CInt(sample.Green) + dg), localAlpha),
                        BlendByte(target.Blue, ClampByte(CInt(sample.Blue) + db), localAlpha),
                        BlendByte(target.Alpha, sample.Alpha, localAlpha)))
                Next
            Next
        End Sub

        Private Shared Function PatchOutlierPenalty(source As SKBitmap, cx As Single, cy As Single,
                                                    radius As Single, targetAverage As SKColor) As Double
            Dim stepSize = Math.Max(1, CInt(Math.Round(radius / 2.0F)))
            Dim radiusSq = radius * radius
            Dim outliers = 0
            Dim count = 0
            For y = Math.Max(0, CInt(Math.Floor(cy - radius))) To Math.Min(source.Height - 1, CInt(Math.Ceiling(cy + radius))) Step stepSize
                Dim dy = CSng(y) - cy
                For x = Math.Max(0, CInt(Math.Floor(cx - radius))) To Math.Min(source.Width - 1, CInt(Math.Ceiling(cx + radius))) Step stepSize
                    Dim dx = CSng(x) - cx
                    If dx * dx + dy * dy > radiusSq Then Continue For
                    count += 1
                    If ColorDistanceSquared(source.GetPixel(x, y), targetAverage) > 90 * 90 Then outliers += 1
                Next
            Next
            If count = 0 Then Return 1000000.0
            Dim ratio = CDbl(outliers) / count
            Return ratio * ratio * 180000.0
        End Function

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

        Private Shared Function AverageRegionSurroundingColor(source As SKBitmap, mask As SKBitmap,
                                                              left As Integer, top As Integer,
                                                              radius As Single) As SKColor?
            Dim reach = Math.Max(3, CInt(Math.Ceiling(radius * 1.5F)))
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim count = 0
            Dim minX = Math.Max(0, left - reach)
            Dim minY = Math.Max(0, top - reach)
            Dim maxX = Math.Min(source.Width - 1, left + mask.Width + reach)
            Dim maxY = Math.Min(source.Height - 1, top + mask.Height + reach)
            For y = minY To maxY
                For x = minX To maxX
                    Dim mx = x - left
                    Dim my = y - top
                    If mx >= 0 AndAlso my >= 0 AndAlso mx < mask.Width AndAlso my < mask.Height AndAlso
                       mask.GetPixel(mx, my).Alpha > 0 Then Continue For

                    Dim nearMask = False
                    For oy = -reach To reach Step Math.Max(1, reach \ 3)
                        If nearMask Then Exit For
                        For ox = -reach To reach Step Math.Max(1, reach \ 3)
                            Dim nx = mx + ox
                            Dim ny = my + oy
                            If nx >= 0 AndAlso ny >= 0 AndAlso nx < mask.Width AndAlso ny < mask.Height AndAlso
                               mask.GetPixel(nx, ny).Alpha > 32 Then
                                nearMask = True
                                Exit For
                            End If
                        Next
                    Next
                    If Not nearMask Then Continue For

                    Dim c = source.GetPixel(x, y)
                    sr += c.Red : sg += c.Green : sb += c.Blue : sa += c.Alpha
                    count += 1
                Next
            Next
            If count = 0 Then Return Nothing
            Return New SKColor(CByte(sr \ count), CByte(sg \ count), CByte(sb \ count), CByte(sa \ count))
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

        Private Shared Function SamplePatchStats(source As SKBitmap, cx As Single, cy As Single, radius As Single,
                                                 Optional sourceBuffer As Byte() = Nothing,
                                                 Optional sourceStride As Integer = 0,
                                                 Optional hasBuffer As Boolean = False) As (Average As SKColor, Variance As Double, Count As Integer)
            Dim stepSize = Math.Max(1, CInt(Math.Round(radius / 2.25F)))
            Dim radiusSq = radius * radius
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0
            Dim sr2 As Long = 0, sg2 As Long = 0, sb2 As Long = 0
            Dim count = 0

            For y = Math.Max(0, CInt(Math.Floor(cy - radius))) To Math.Min(source.Height - 1, CInt(Math.Ceiling(cy + radius))) Step stepSize
                Dim dy = CSng(y) - cy
                For x = Math.Max(0, CInt(Math.Floor(cx - radius))) To Math.Min(source.Width - 1, CInt(Math.Ceiling(cx + radius))) Step stepSize
                    Dim dx = CSng(x) - cx
                    If dx * dx + dy * dy > radiusSq Then Continue For
                    Dim c = ReadPixel(source, x, y, sourceBuffer, sourceStride, hasBuffer)
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

        Private Shared Function ReadPixel(source As SKBitmap, x As Integer, y As Integer,
                                          sourceBuffer As Byte(), sourceStride As Integer, hasBuffer As Boolean) As SKColor
            If hasBuffer AndAlso sourceBuffer IsNot Nothing AndAlso sourceStride > 0 Then
                Dim index = y * sourceStride + x * 4
                Return New SKColor(sourceBuffer(index + 2), sourceBuffer(index + 1), sourceBuffer(index), sourceBuffer(index + 3))
            End If
            Return source.GetPixel(x, y)
        End Function

        Private Shared Function ColorDistanceSquared(a As SKColor, b As SKColor) As Double
            Dim dr = CInt(a.Red) - CInt(b.Red)
            Dim dg = CInt(a.Green) - CInt(b.Green)
            Dim db = CInt(a.Blue) - CInt(b.Blue)
            Return dr * dr + dg * dg + db * db
        End Function

        ''' Mittelt den Ring zwischen dem 1,25- und dem 2-fachen Radius um das Ziel - der Rückfall,
        ''' wenn keine Klonquelle gesetzt wurde. Liefert Nothing, wenn der Ring komplett außerhalb
        ''' des Bildes liegt.
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

            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim count As Integer = 0
            For yy = Math.Max(0, icy - reach) To Math.Min(source.Height - 1, icy + reach)
                Dim dy = CSng(yy - icy)
                Dim dySq = dy * dy
                For xx = Math.Max(0, icx - reach) To Math.Min(source.Width - 1, icx + reach)
                    Dim dx = CSng(xx - icx)
                    Dim dSq = dx * dx + dySq
                    If dSq >= innerSq AndAlso dSq <= outerSq Then
                        Dim c = source.GetPixel(xx, yy)
                        sr += c.Red : sg += c.Green : sb += c.Blue : sa += c.Alpha
                        count += 1
                    End If
                Next
            Next
            If count = 0 Then Return Nothing
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
            If targetWidth <= 0 AndAlso targetHeight <= 0 Then Return source

            If targetWidth <= 0 Then targetWidth = CInt(Math.Round(source.Width * (targetHeight / CDbl(source.Height))))
            If targetHeight <= 0 Then targetHeight = CInt(Math.Round(source.Height * (targetWidth / CDbl(source.Width))))

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
                canvas.Clear(ParseColor(adj.CanvasBackgroundColor, SKColors.Black))
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
            If adj.Annotations Is Nothing OrElse adj.Annotations.Count = 0 Then Return source

            Dim result = CloneBitmap(source)
            Dim scaleX As Single = 1.0F
            Dim scaleY As Single = 1.0F
            If adj.SourceWidthPixels > 0 AndAlso adj.SourceHeightPixels > 0 AndAlso source.Width > 0 AndAlso source.Height > 0 Then
                scaleX = source.Width / CSng(adj.SourceWidthPixels)
                scaleY = source.Height / CSng(adj.SourceHeightPixels)
            End If

            Using canvas = New SKCanvas(result)
                For Each annotation In adj.Annotations
                    If annotation Is Nothing OrElse Not annotation.IsVisible Then Continue For
                    Dim renderAnnotation = ScaleAnnotationForSource(annotation, scaleX, scaleY)
                    Dim kind = If(renderAnnotation.Kind, "Text").Trim().ToLowerInvariant()

                    If IsPaintKind(kind) Then
                        Dim alphaFactor = Clamp(renderAnnotation.Opacity, 0, 100) / 100.0F
                        Dim stroke = ApplyAlpha(ParseColor(renderAnnotation.StrokeColor, SKColors.Black), alphaFactor)
                        Dim strokeWidth = Math.Max(1.0F, Clamp(renderAnnotation.StrokeWidth, 1, Math.Max(source.Width, source.Height)))
                        Dim isEraser = kind = "eraser"
                        Dim eraserFill As SKColor? = Nothing
                        If isEraser AndAlso Not String.IsNullOrWhiteSpace(renderAnnotation.EraserFillColor) Then
                            eraserFill = ApplyAlpha(ParseColor(renderAnnotation.EraserFillColor, SKColors.Transparent), alphaFactor)
                        End If
                        DrawBrushStroke(canvas, renderAnnotation.Strokes, source.Width, source.Height, stroke, strokeWidth,
                                        renderAnnotation.HardnessPercent, renderAnnotation.FlowPercent, isEraser, eraserFill)
                        Continue For
                    End If

                    Dim rect = ComputeAnnotationRect(source.Width, source.Height, kind, renderAnnotation)

                    ' Objekt MIT eigenen Anpassungen: erst allein auf eine transparente Ebene zeichnen, dann
                    ' die Pixel-Pipeline darauf laufen lassen (Belichtung, Farbe, Filter … treffen so nur das
                    ' Objekt), dann an Ort und Stelle in der Z-Reihenfolge einkomponieren. Ohne eigene
                    ' Anpassungen wird wie bisher direkt gezeichnet - kein zusätzlicher Speicher, keine Zeit.
                    If HasObjectAdjustments(annotation) OrElse Not IsNormalAnnotationBlendMode(renderAnnotation.BlendMode) Then
                        Using layer = New SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
                            Using layerCanvas = New SKCanvas(layer)
                                layerCanvas.Clear(SKColors.Transparent)
                                DrawAnnotationOnCanvas(layerCanvas, kind, renderAnnotation, rect, source.Width, source.Height)
                            End Using
                            If HasObjectAdjustments(annotation) Then
                                Dim objectAdj = annotation.Adjustments.ExtractPixelAdjustments()
                                objectAdj.SourceWidthPixels = source.Width
                                objectAdj.SourceHeightPixels = source.Height
                                Using processedLayer = ProcessBitmapBase(layer, objectAdj)
                                    DrawAnnotationLayer(canvas, processedLayer, renderAnnotation.BlendMode)
                                End Using
                            Else
                                DrawAnnotationLayer(canvas, layer, renderAnnotation.BlendMode)
                            End If
                        End Using
                    Else
                        DrawAnnotationOnCanvas(canvas, kind, renderAnnotation, rect, source.Width, source.Height)
                    End If
                Next
            End Using
            Return result
        End Function

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
                    DrawImageAnnotation(canvas, annotation.ImagePath, rect, annotation.Opacity, stroke, annotation.StrokeWidth, stretchToFill:=kind = "selectionimage")
                Case "svg"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawSvgAnnotation(canvas, annotation.ImagePath, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "watermark"
                    If Not String.IsNullOrWhiteSpace(annotation.ImagePath) Then
                        DrawImageAnnotation(canvas, annotation.ImagePath, rect, annotation.Opacity, stroke, annotation.StrokeWidth)
                    Else
                        Dim watermark = If(String.IsNullOrWhiteSpace(annotation.Text), "FerrumPix", annotation.Text)
                        Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                        DrawAnnotationText(canvas, watermark, x, y, maxWidth, fontSize, WithAlpha(fill, If(fill.Alpha = 255, CByte(130), fill.Alpha)), stroke, annotation.StrokeWidth, annotation.FontFamily, rect, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                    End If
                Case Else
                    If Not String.IsNullOrWhiteSpace(annotation.Text) Then
                        Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                        DrawAnnotationText(canvas, annotation.Text, x, y, maxWidth, fontSize, fill, stroke, annotation.StrokeWidth, annotation.FontFamily, rect, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
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
            Dim shadowBlurPx = objSize * Clamp(annotation.ShadowBlur, 0, 100) / 100.0F * 0.6F
            Dim offsetX = objSize * annotation.ShadowOffsetXPercent / 100.0F
            Dim offsetY = objSize * annotation.ShadowOffsetYPercent / 100.0F

            Dim maskLeft As Integer = 0
            Dim maskTop As Integer = 0
            Dim maskWidth = canvasWidth
            Dim maskHeight = canvasHeight
            If Math.Abs(annotation.RotationDegrees) <= 0.01F Then
                Dim pad = Math.Max(glowMaskReach, shadowBlurPx * 3.0F) + Math.Max(Math.Abs(offsetX), Math.Abs(offsetY)) + 4.0F
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
                        ' Abgerundeter Schatten nutzt eine eigene Rechteck-Maske statt der exakten
                        ' Objekt-Silhouette - der Glow-Effekt bleibt davon unberührt und folgt weiter
                        ' der echten Objektform.
                        If annotation.ShadowRounded Then
                            roundedShadowMask = New SKBitmap(maskWidth, maskHeight, SKColorType.Rgba8888, SKAlphaType.Premul)
                            Using roundCanvas = New SKCanvas(roundedShadowMask)
                                roundCanvas.Clear(SKColors.Transparent)
                                roundCanvas.Translate(-maskLeft, -maskTop)
                                Dim cornerRadius = Math.Min(rect.Width, rect.Height) / 2.0F * Clamp(annotation.ShadowCornerRadiusPercent, 0, 100) / 100.0F
                                Using roundPaint = New SKPaint With {.Color = SKColors.Black, .IsAntialias = True}
                                    roundCanvas.DrawRoundRect(rect, cornerRadius, cornerRadius, roundPaint)
                                End Using
                            End Using
                            shadowSource = roundedShadowMask
                        End If

                        ' Schattengröße: um die Objektmitte skalieren, sodass der Schatten über das Objekt
                        ' hinauswachsen (oder schrumpfen) kann. Der Blur-Radius wird durch den Skalierungs-
                        ' faktor geteilt, weil die anschließende Canvas-Skalierung ihn wieder hochmultipliziert -
                        ' so bleibt die Weichzeichnung unabhängig von der gewählten Größe.
                        Dim shadowScale = Clamp(annotation.ShadowSizePercent, 10, 400) / 100.0F
                        Using shadowColorFilter = SKColorFilter.CreateBlendMode(shadowColor, SKBlendMode.SrcIn)
                            Using shadowMaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.1F, shadowBlurPx / shadowScale))
                                Using paint = New SKPaint With {.ColorFilter = shadowColorFilter, .MaskFilter = shadowMaskFilter}
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

        Private Shared Function GetTypeface(fontFamily As String) As SKTypeface
            Dim key = If(fontFamily, "")
            SyncLock _typefaceCacheLock
                Dim cached As SKTypeface = Nothing
                If _typefaceCache.TryGetValue(key, cached) Then Return cached
                Dim created = SKTypeface.FromFamilyName(fontFamily)
                _typefaceCache(key) = created
                Return created
            End SyncLock
        End Function

        Private Shared Sub DrawAnnotationText(canvas As SKCanvas, text As String, x As Single, y As Single, maxWidth As Single, fontSize As Single, fill As SKColor, stroke As SKColor, strokeWidth As Single, fontFamily As String, bounds As SKRect, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using font = CreateFont(fontFamily, fontSize)
                If strokeWidth > 0 Then
                    Using strokePaint = New SKPaint With {
                        .Color = stroke,
                        .IsAntialias = True,
                        .Style = SKPaintStyle.Stroke,
                        .StrokeWidth = Math.Max(1.0F, strokeWidth)
                    }
                        DrawWrappedText(canvas, text, x, y, maxWidth, fontSize, font, strokePaint)
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
                    DrawWrappedText(canvas, text, x, y, maxWidth, fontSize, font, fillPaint)
                End Using
            End Using
        End Sub

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
        Private Shared Sub DrawBrushStroke(canvas As SKCanvas, strokes As IEnumerable(Of BrushStroke), width As Integer, height As Integer, stroke As SKColor, strokeWidth As Single, hardnessPercent As Single, flowPercent As Single, isEraser As Boolean, Optional eraserFill As SKColor? = Nothing)
            If strokes Is Nothing Then Return

            Dim resolvedStrokeWidth = Math.Max(1.0F, strokeWidth)
            Dim hardness = Clamp(hardnessPercent, 0, 100) / 100.0F
            Dim blurSigma = resolvedStrokeWidth * (1.0F - hardness) * 0.5F
            Dim flow = Clamp(flowPercent, 0, 100) / 100.0F
            Dim paintColor = stroke
            Dim blendMode = SKBlendMode.SrcOver
            If isEraser Then
                If eraserFill.HasValue AndAlso eraserFill.Value.Alpha > 0 Then
                    paintColor = eraserFill.Value
                Else
                    blendMode = SKBlendMode.DstOut
                End If
            End If

            Using paint = New SKPaint With {
                .Color = paintColor.WithAlpha(CByte(paintColor.Alpha * flow)),
                .Style = SKPaintStyle.Stroke,
                .StrokeWidth = resolvedStrokeWidth,
                .StrokeCap = SKStrokeCap.Round,
                .StrokeJoin = SKStrokeJoin.Round,
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
                                    canvas.DrawRect(left + col * cell, top + row * cell, Math.Ceiling(cell), Math.Ceiling(cell), fg)
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

        Private Shared Sub DrawWrappedText(canvas As SKCanvas, text As String, x As Single, y As Single, maxWidth As Single, fontSize As Single, font As SKFont, paint As SKPaint)
            If String.IsNullOrEmpty(text) Then Return
            Dim lineHeight = GetLineHeight(font.Metrics)
            Dim baseline = y + fontSize

            For Each paragraph In text.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(ControlChars.Lf)
                Dim current = ""
                For Each word In paragraph.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
                    Dim candidate = If(String.IsNullOrEmpty(current), word, current & " " & word)
                    If current.Length > 0 AndAlso font.MeasureText(candidate, paint) > maxWidth Then
                        canvas.DrawText(current, x, baseline, font, paint)
                        baseline += lineHeight
                        current = word
                    Else
                        current = candidate
                    End If
                Next

                If current.Length > 0 Then
                    canvas.DrawText(current, x, baseline, font, paint)
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

        Private Shared Function ApplySharpness(source As SKBitmap, amount As Single) As SKBitmap
            ' Einfacher Schärfe-Kernel (Unsharp Mask)
            Dim kernel = New Single() {
                0, -amount, 0,
                -amount, 1 + 4 * amount, -amount,
                0, -amount, 0
            }
            Dim imageFilter = SKImageFilter.CreateMatrixConvolution(
                New SKSizeI(3, 3), kernel, 1.0F, 0.0F,
                New SKPointI(1, 1), SKShaderTileMode.Clamp, False)

            Dim paint = New SKPaint With {.ImageFilter = imageFilter}
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.DrawBitmap(source, 0, 0, paint)
            End Using
            imageFilter.Dispose()
            paint.Dispose()
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

            Dim rotated = New SKBitmap(rw, rh)
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
            Dim result = New SKBitmap(w, h)
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

        Private Shared Function ApplyStraighten(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim degrees = adj.StraightenDegrees
            If Math.Abs(degrees) < 0.01F Then Return source

            Dim radians = Math.Abs(degrees) * Math.PI / 180.0

            If adj.StraightenExpandCanvas Then
                Dim expandedWidth = Math.Max(1, CInt(Math.Ceiling(source.Width * Math.Cos(radians) + source.Height * Math.Sin(radians))))
                Dim expandedHeight = Math.Max(1, CInt(Math.Ceiling(source.Width * Math.Sin(radians) + source.Height * Math.Cos(radians))))

                Dim expanded = New SKBitmap(expandedWidth, expandedHeight, source.ColorType, source.AlphaType)
                Using canvas = New SKCanvas(expanded)
                    canvas.Clear(ParseColor(adj.CanvasBackgroundColor, SKColors.Transparent))
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

        Public Shared Function SaveImage(sourcePath As String, targetPath As String, adj As ImageAdjustments, quality As Integer, Optional preserveMetadata As Boolean = True) As Boolean
            ' Zentraler Schutz: Bearbeitung einer RAW-Quelle wirkt nur auf deren eingebettete
            ' JPEG-Vorschau (siehe OpenSourceStream/DecodeOriented) - ein Speichern-in-place würde
            ' hier fälschlich die RAW-Rohdaten JPEG-kodiert über die Original-RAW-Datei schreiben.
            If RawPreviewService.IsSupportedRaw(sourcePath) AndAlso String.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) Then
                Return False
            End If
            Try
                Using original = DecodeOriented(sourcePath)
                    If original Is Nothing Then Return False

                    Dim ext = IO.Path.GetExtension(targetPath).ToLowerInvariant()
                    Dim format = If(ext = ".png", SKEncodedImageFormat.Png,
                                 If(ext = ".webp", SKEncodedImageFormat.Webp,
                                    SKEncodedImageFormat.Jpeg))

                    Using processed = ProcessBitmap(original, adj)
                        Using image = SKImage.FromBitmap(processed)
                            Using data = image.Encode(format, quality)
                                Using fs = File.Open(targetPath, FileMode.Create, FileAccess.Write)
                                    data.SaveTo(fs)
                                End Using
                            End Using
                        End Using
                    End Using
                    If preserveMetadata Then TryCopyMetadata(sourcePath, targetPath)
                    Return True
                End Using
            Catch ex As Exception
                Return False
            End Try
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
            File.WriteAllBytes(targetPath, output.ToArray())
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

            File.WriteAllBytes(targetPath, output.ToArray())
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
        Public Shared Function ExtractRegionToFile(sourcePath As String, adj As ImageAdjustments, pixelRect As SKRectI, targetPngPath As String) As Boolean
            Try
                Using original = DecodeOriented(sourcePath)
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
                                    Using fs = File.Open(targetPngPath, FileMode.Create, FileAccess.Write)
                                        data.SaveTo(fs)
                                    End Using
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

            Dim buf As Byte() = Nothing, stride As Integer = 0
            If Not TryBorrowBgraBuffer(image, buf, stride) Then Return Nothing

            Dim tol = CInt(Math.Round(Clamp(tolerance, 0, 1) * 255))
            Dim seedO = seedY * stride + seedX * 4
            Dim sb = CInt(buf(seedO)), sg = CInt(buf(seedO + 1)), sr = CInt(buf(seedO + 2))

            Dim inside = New Boolean(w * h - 1) {}
            Dim stack As New Stack(Of Integer)()
            stack.Push(seedY * w + seedX)
            Dim minX = w, minY = h, maxX = -1, maxY = -1

            While stack.Count > 0
                Dim idx = stack.Pop()
                If inside(idx) Then Continue While
                Dim x = idx Mod w, y = idx \ w
                Dim o = y * stride + x * 4
                If Math.Abs(CInt(buf(o)) - sb) > tol OrElse
                   Math.Abs(CInt(buf(o + 1)) - sg) > tol OrElse
                   Math.Abs(CInt(buf(o + 2)) - sr) > tol Then Continue While
                inside(idx) = True
                If x < minX Then minX = x
                If x > maxX Then maxX = x
                If y < minY Then minY = y
                If y > maxY Then maxY = y
                If x > 0 Then stack.Push(idx - 1)
                If x < w - 1 Then stack.Push(idx + 1)
                If y > 0 Then stack.Push(idx - w)
                If y < h - 1 Then stack.Push(idx + w)
            End While

            If maxX < minX Then Return Nothing
            Dim bw = maxX - minX + 1, bh = maxY - minY + 1
            bounds = New SKRectI(minX, minY, maxX + 1, maxY + 1)

            Dim mask = New SKBitmap(bw, bh, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim mstride = mask.RowBytes
            Dim mbuf = New Byte(mstride * bh - 1) {}
            For y = 0 To bh - 1
                For x = 0 To bw - 1
                    If inside((minY + y) * w + (minX + x)) Then mbuf(y * mstride + x) = 255
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
            Dim w = source.Width, h = source.Height
            Dim result = New SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul)
            Dim mstride As Integer
            Dim mbuf = ReadMaskBytes(mask, mstride)
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim c = source.GetPixel(x, y)
                    Dim m = If(x < mask.Width AndAlso y < mask.Height, CInt(mbuf(y * mstride + x)), 0)
                    result.SetPixel(x, y, New SKColor(c.Red, c.Green, c.Blue, CByte(CInt(c.Alpha) * m \ 255)))
                Next
            Next
            Return result
        End Function

        ''' <summary>Umkehrung von <see cref="ApplyMaskCutout"/> für "Löschen/Freistellen": innerhalb der
        ''' Maske wird das Bild transparent, außerhalb bleibt es erhalten.</summary>
        Public Shared Function ApplyMaskErase(source As SKBitmap, mask As SKBitmap) As SKBitmap
            Dim w = source.Width, h = source.Height
            Dim result = New SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul)
            Dim mstride As Integer
            Dim mbuf = ReadMaskBytes(mask, mstride)
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim c = source.GetPixel(x, y)
                    Dim m = If(x < mask.Width AndAlso y < mask.Height, CInt(mbuf(y * mstride + x)), 0)
                    result.SetPixel(x, y, New SKColor(c.Red, c.Green, c.Blue, CByte(CInt(c.Alpha) * (255 - m) \ 255)))
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
                    Using fs = File.Open(targetPngPath, FileMode.Create, FileAccess.Write)
                        data.SaveTo(fs)
                    End Using
                End Using
            End Using
            Return True
        End Function

        ''' <summary>Wie <see cref="ExtractRegionToFile"/>, aber schneidet den Ausschnitt zusätzlich mit einer
        ''' Maske frei (unregelmäßige Auswahl). Die Maske muss die Größe des (geklemmten) Rechtecks haben.</summary>
        Public Shared Function ExtractRegionToFileMasked(sourcePath As String, adj As ImageAdjustments,
                                                         pixelRect As SKRectI, mask As SKBitmap, targetPngPath As String) As Boolean
            Try
                If mask Is Nothing Then Return False
                Using original = DecodeOriented(sourcePath)
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

        ''' <summary>Dekodiert und verarbeitet das Bild (alle Anpassungen/Objekte gebacken) und liefert die
        ''' Zauberstab-Maske am Saatpunkt in Bildpixeln. <paramref name="bounds"/> ist das umschließende
        ''' Rechteck in Bildpixeln.</summary>
        Public Shared Function BuildMagicWandMaskFromFile(sourcePath As String, adj As ImageAdjustments,
                                                          seedX As Integer, seedY As Integer, tolerance As Single,
                                                          ByRef bounds As SKRectI) As SKBitmap
            bounds = SKRectI.Empty
            Try
                Using original = DecodeOriented(sourcePath)
                    If original Is Nothing Then Return Nothing
                    Using processed = ProcessBitmap(original, adj)
                        Return BuildMagicWandMask(processed, seedX, seedY, tolerance, bounds)
                    End Using
                End Using
            Catch ex As Exception
                Return Nothing
            End Try
        End Function

        Private Shared Function ApplyNoiseReduction(source As SKBitmap, amount As Single) As SKBitmap
            Dim sigma = 0.25F + Clamp(amount, 0, 1) * 2.2F
            Dim filter = SKImageFilter.CreateBlur(sigma, sigma)
            Dim paint = New SKPaint With {.ImageFilter = filter}
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.DrawBitmap(source, 0, 0, paint)
            End Using
            filter.Dispose()
            paint.Dispose()
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
        Private Shared Function ApplyLocalContrast(source As SKBitmap, blurSigma As Single, amount As Single, strengthMultiplier As Single) As SKBitmap
            Using blurred = ApplyNoiseReduction(source, blurSigma / 8.0F)
                Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
                For y As Integer = 0 To source.Height - 1
                    For x As Integer = 0 To source.Width - 1
                        Dim c = source.GetPixel(x, y)
                        Dim b = blurred.GetPixel(x, y)
                        ' Ohne die expliziten CInt-Weitungen würde "c.Red - b.Red" als Byte-Subtraktion
                        ' ausgewertet - da Byte vorzeichenlos ist, wirft VB im Checked-Kontext eine
                        ' OverflowException, sobald der weichgezeichnete Pixel heller ist als das
                        ' Original (b.Red > c.Red), was in praktisch jedem Foto ständig vorkommt.
                        Dim r = ClampToByte(CInt(c.Red) + (CInt(c.Red) - CInt(b.Red)) * amount * strengthMultiplier)
                        Dim g = ClampToByte(CInt(c.Green) + (CInt(c.Green) - CInt(b.Green)) * amount * strengthMultiplier)
                        Dim bl = ClampToByte(CInt(c.Blue) + (CInt(c.Blue) - CInt(b.Blue)) * amount * strengthMultiplier)
                        result.SetPixel(x, y, New SKColor(r, g, bl, c.Alpha))
                    Next
                Next
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

        Private Shared Function ApplyHaze(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return source
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            For y As Integer = 0 To source.Height - 1
                For x As Integer = 0 To source.Width - 1
                    Dim c = source.GetPixel(x, y)
                    If strength > 0 Then
                        Dim s = strength * 0.45F
                        result.SetPixel(x, y, New SKColor(ClampToByte(c.Red + (255 - c.Red) * s),
                                                          ClampToByte(c.Green + (255 - c.Green) * s),
                                                          ClampToByte(c.Blue + (255 - c.Blue) * s),
                                                          c.Alpha))
                    Else
                        Dim s = -strength
                        Dim contrast = 1.0F + s * 0.55F
                        result.SetPixel(x, y, New SKColor(ClampToByte((c.Red - 128) * contrast + 128 - s * 10),
                                                          ClampToByte((c.Green - 128) * contrast + 128 - s * 10),
                                                          ClampToByte((c.Blue - 128) * contrast + 128 - s * 10),
                                                          c.Alpha))
                    End If
                Next
            Next
            Return result
        End Function

        Private Shared Function ApplyImageGlow(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return source
            Using blurred = ApplyNoiseReduction(source, 0.8F)
                Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
                For y As Integer = 0 To source.Height - 1
                    For x As Integer = 0 To source.Width - 1
                        Dim c = source.GetPixel(x, y)
                        Dim b = blurred.GetPixel(x, y)
                        If strength > 0 Then
                            Dim s = strength * 0.55F
                            result.SetPixel(x, y, New SKColor(ClampToByte(c.Red + b.Red * s),
                                                              ClampToByte(c.Green + b.Green * s),
                                                              ClampToByte(c.Blue + b.Blue * s),
                                                              c.Alpha))
                        Else
                            Dim s = -strength * 0.55F
                            result.SetPixel(x, y, New SKColor(ClampToByte(c.Red - b.Red * s),
                                                              ClampToByte(c.Green - b.Green * s),
                                                              ClampToByte(c.Blue - b.Blue * s),
                                                              c.Alpha))
                        End If
                    Next
                Next
                Return result
            End Using
        End Function

        Private Shared Function ApplyDustScratches(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, -1, 1)
            If Math.Abs(strength) <= 0.001F Then Return source

            If strength > 0 Then
                ' Der Median-Radius ist zwangsläufig ganzzahlig. Durch den früheren 0,75-Faktor erreichte er
                ' über den ganzen Reglerweg nur die Stufen 1 und 2 und stand ab etwa 35 fest - die Werte 50
                ' und 100 lieferten pixelgleiche Bilder, die obere Reglerhälfte tat also nichts. Jetzt läuft
                ' der Radius über seinen vollen Bereich, und die Zwischenstufen kommen aus der Deckkraft,
                ' mit der das Medianbild über das Original gelegt wird.
                Using median = ApplyMedianBlur(source, strength)
                    Dim blended = CloneBitmap(source)
                    Using canvas = New SKCanvas(blended)
                        Using paint = New SKPaint With {.Color = New SKColor(255, 255, 255, ClampToByte(255.0F * strength))}
                            canvas.DrawBitmap(median, 0, 0, paint)
                        End Using
                    End Using
                    Return blended
                End Using
            End If

            Dim result = CloneBitmap(source)
            Dim random = New Random(source.Width * 997 Xor source.Height * 331)
            Dim count = CInt(Math.Round(source.Width * source.Height / 9000.0 * -strength))
            Using canvas = New SKCanvas(result)
                For i = 0 To count - 1
                    Dim x = random.Next(0, source.Width)
                    Dim y = random.Next(0, source.Height)
                    Dim len = random.Next(2, Math.Max(3, CInt(12 + 30 * -strength)))
                    Dim color = If(random.NextDouble() < 0.5, SKColors.White, SKColors.Black)
                    Using paint = New SKPaint With {.Color = New SKColor(color.Red, color.Green, color.Blue, CByte(80 + 100 * -strength)), .StrokeWidth = Math.Max(1.0F, 1.5F * -strength), .IsAntialias = True}
                        canvas.DrawLine(x, y, Math.Min(source.Width - 1, x + len), Math.Min(source.Height - 1, y + random.Next(-2, 3)), paint)
                    End Using
                Next
            End Using
            Return result
        End Function

        ''' <summary>Rechnet ein gescanntes Filmnegativ in ein Positiv um. Jeder Kanal wird zwischen der
        ''' Filmbasis (dem hellsten Wert des Scans = unbelichteter Träger = Schatten der Szene) und dem
        ''' dichtesten Wert (= hellstes Motivdetail) normiert und umgekehrt. Weil jeder Kanal auf seine
        ''' EIGENE Basis normiert wird, fällt die orange Maske des Farbnegativfilms von selbst heraus -
        ''' eine bloße 255-x-Umkehr würde sie als kräftigen Blaustich stehen lassen.</summary>
        Private Shared Function ApplyFilmNegative(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If Not adj.NegativeEnabled Then Return source

            Dim stats = ResolveFilmNegativeStats(source, adj)
            Dim gamma = CSng(Math.Pow(2.0, adj.NegativeGamma / 100.0))
            Dim redLut = BuildFilmNegativeLut(stats.BaseColor.Red, stats.DensityColor.Red, gamma)
            Dim greenLut = BuildFilmNegativeLut(stats.BaseColor.Green, stats.DensityColor.Green, gamma)
            Dim blueLut = BuildFilmNegativeLut(stats.BaseColor.Blue, stats.DensityColor.Blue, gamma)

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim tableFilter = SKColorFilter.CreateTable(IdentityByteTable, redLut, greenLut, blueLut)
            Dim filter = tableFilter
            Dim grayFilter As SKColorFilter = Nothing
            If adj.NegativeMonochrome Then
                ' S/W-Negativ: erst wie beim Farbfilm kanalweise auf die eigene Basis normieren (das
                ' nimmt auch dem S/W-Träger seinen leichten Eigenfarbton), dann entsättigen. Nur so ist
                ' das Ergebnis wirklich neutral - eine gemeinsame Graubasis für alle Kanäle würde einen
                ' farbigen Träger als Farbstich stehen lassen.
                grayFilter = SKColorFilter.CreateColorMatrix(New Single() {
                    0.299F, 0.587F, 0.114F, 0, 0,
                    0.299F, 0.587F, 0.114F, 0, 0,
                    0.299F, 0.587F, 0.114F, 0, 0,
                    0, 0, 0, 1, 0
                })
                filter = SKColorFilter.CreateCompose(grayFilter, tableFilter)
            End If

            Using paint = New SKPaint With {.ColorFilter = filter}
                Using canvas = New SKCanvas(result)
                    canvas.DrawBitmap(source, 0, 0, paint)
                End Using
            End Using

            If Not Object.ReferenceEquals(filter, tableFilter) Then filter.Dispose()
            grayFilter?.Dispose()
            tableFilter.Dispose()
            Return result
        End Function

        ''' <summary>Tonwerttabelle eines Kanals: <paramref name="baseValue"/> (Filmbasis) wird zu Schwarz,
        ''' <paramref name="densityValue"/> (der dichteste, also dunkelste Wert) zu Weiß. Dazwischen wird in
        ''' Dichte gerechnet, nicht linear: die Silberschicht dämpft das Licht multiplikativ, der Logarithmus
        ''' ist also die natürliche Achse des Films. Eine lineare Umkehr staucht dagegen die Lichter und
        ''' erzeugt den typischen flauen, milchigen Scan-Look.</summary>
        Private Shared Function BuildFilmNegativeLut(baseValue As Byte, densityValue As Byte, gamma As Single) As Byte()
            ' Basis und Dichtepunkt dürfen weder null noch identisch sein, sonst hat die Kurve keine
            ' Spanne (und der Logarithmus keinen definierten Wert).
            Dim baseLevel = Math.Max(2.0, CDbl(baseValue))
            Dim densityLevel = Math.Min(Math.Max(1.0, CDbl(densityValue)), baseLevel - 1.0)
            Dim span = Math.Log(baseLevel / densityLevel)

            Dim lut = New Byte(255) {}
            Dim invGamma = 1.0 / Math.Max(0.05, CDbl(gamma))
            For i As Integer = 0 To 255
                Dim level = Math.Max(1.0, CDbl(i))
                ' 0 an der Filmbasis (dort war kein Licht -> Schwarz), 1 am dichtesten Punkt (-> Weiß).
                Dim t = Clamp(CSng(Math.Log(baseLevel / level) / span), 0.0F, 1.0F)
                lut(i) = ClampToByte(255.0 * Math.Pow(t, invGamma))
            Next
            Return lut
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

        Private Shared Function ApplyCurve(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If ImageAdjustments.IsIdentityCurve(adj.CurveRgbPoints) AndAlso ImageAdjustments.IsIdentityCurve(adj.CurveRedPoints) AndAlso
               ImageAdjustments.IsIdentityCurve(adj.CurveGreenPoints) AndAlso ImageAdjustments.IsIdentityCurve(adj.CurveBluePoints) AndAlso
               ImageAdjustments.IsIdentityCurve(adj.CurveLuminancePoints) Then
                Return source
            End If

            Dim rgbLut = BuildCurveLut(adj.CurveRgbPoints)
            Dim redLut = BuildCurveLut(adj.CurveRedPoints)
            Dim greenLut = BuildCurveLut(adj.CurveGreenPoints)
            Dim blueLut = BuildCurveLut(adj.CurveBluePoints)

            Dim finalR = New Byte(255) {}
            Dim finalG = New Byte(255) {}
            Dim finalB = New Byte(255) {}
            For i As Integer = 0 To 255
                finalR(i) = redLut(rgbLut(i))
                finalG(i) = greenLut(rgbLut(i))
                finalB(i) = blueLut(rgbLut(i))
            Next

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using filter = SKColorFilter.CreateTable(IdentityByteTable, finalR, finalG, finalB)
                Using paint = New SKPaint With {.ColorFilter = filter}
                    Using canvas = New SKCanvas(result)
                        canvas.DrawBitmap(source, 0, 0, paint)
                    End Using
                End Using
            End Using

            If Not ImageAdjustments.IsIdentityCurve(adj.CurveLuminancePoints) Then
                Dim lumLut = BuildCurveLut(adj.CurveLuminancePoints)
                Dim withLuminance = New SKBitmap(result.Width, result.Height, result.ColorType, result.AlphaType)

                Dim srcBuf As Byte() = Nothing
                Dim stride As Integer = 0
                If TryBorrowBgraBuffer(result, srcBuf, stride) Then
                    Dim dstBuf = New Byte(srcBuf.Length - 1) {}
                    ForEachRow(result.Width, result.Height, Sub(y)
                                                                Dim rowOffset = y * stride
                                                                For x As Integer = 0 To result.Width - 1
                                                                    Dim o = rowOffset + x * 4
                                                                    Dim h As Double
                                                                    Dim s As Double
                                                                    Dim l As Double
                                                                    RgbToHsl(srcBuf(o + 2), srcBuf(o + 1), srcBuf(o), h, s, l)
                                                                    Dim newL = lumLut(ClampToByte(l * 255.0)) / 255.0
                                                                    Dim nc = HslToRgb(h, s, newL, srcBuf(o + 3))
                                                                    dstBuf(o) = nc.Blue : dstBuf(o + 1) = nc.Green : dstBuf(o + 2) = nc.Red : dstBuf(o + 3) = nc.Alpha
                                                                Next
                                                            End Sub)
                    CommitBgraBuffer(withLuminance, dstBuf)
                Else
                    For y As Integer = 0 To result.Height - 1
                        For x As Integer = 0 To result.Width - 1
                            Dim c = result.GetPixel(x, y)
                            Dim h As Double
                            Dim s As Double
                            Dim l As Double
                            RgbToHsl(c.Red, c.Green, c.Blue, h, s, l)
                            Dim newL = lumLut(ClampToByte(l * 255.0)) / 255.0
                            withLuminance.SetPixel(x, y, HslToRgb(h, s, newL, c.Alpha))
                        Next
                    Next
                End If

                result.Dispose()
                result = withLuminance
            End If

            Return result
        End Function

        ' Parst "x1,y1;x2,y2;..." zu sortierten, X-eindeutigen Stützpunkten (0..255) für die Tonwertkurve.
        Private Shared Function ParseCurvePoints(pointsCsv As String) As List(Of (X As Double, Y As Double))
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

        ' Baut per Catmull-Rom-Spline eine 256-Byte-LUT aus den Kurvenpunkten.
        Private Shared Function BuildCurveLut(pointsCsv As String) As Byte()
            Dim lut = New Byte(255) {}
            If ImageAdjustments.IsIdentityCurve(pointsCsv) Then
                For i As Integer = 0 To 255
                    lut(i) = CByte(i)
                Next
                Return lut
            End If

            Dim points = ParseCurvePoints(pointsCsv)
            For i As Integer = 0 To 255
                lut(i) = ClampToByte(EvaluateCurveSpline(points, i))
            Next
            Return lut
        End Function

        Private Shared Function EvaluateCurveSpline(points As List(Of (X As Double, Y As Double)), x As Double) As Double
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
                Using original = DecodeOriented(sourcePath)
                    Return BuildChannelHistogramCounts(original)
                End Using
            Catch
                Return (New Integer(255) {}, New Integer(255) {}, New Integer(255) {}, New Integer(255) {})
            End Try
        End Function

        Private Shared Function ProcessHslPixel(r As Byte, g As Byte, b As Byte, a As Byte, adj As ImageAdjustments) As SKColor
            Dim h As Double
            Dim s As Double
            Dim l As Double
            RgbToHsl(r, g, b, h, s, l)
            Dim hueShift As Single = 0
            Dim satShift As Single = 0
            Dim lumShift As Single = 0
            GetHslBandAdjustments(h, adj, hueShift, satShift, lumShift)
            h = (h + hueShift + 360.0) Mod 360.0
            s = Math.Max(0.0, Math.Min(1.0, s * (1.0 + satShift / 100.0)))
            ' Luminanz multiplikativ wie die Sättigung: -100 zieht das Farbband nach Schwarz, +100
            ' verdoppelt seine Helligkeit. Graue Pixel haben keinen Farbton und bleiben unberührt.
            l = Math.Max(0.0, Math.Min(1.0, l * (1.0 + lumShift / 100.0)))
            Return HslToRgb(h, s, l, a)
        End Function

        Private Shared Function ApplyHsl(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If Not adj.HasHslChanges() Then Return source

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)

            Dim srcBuf As Byte() = Nothing
            Dim stride As Integer = 0
            If TryBorrowBgraBuffer(source, srcBuf, stride) Then
                Dim dstBuf = New Byte(srcBuf.Length - 1) {}
                ForEachRow(source.Width, source.Height, Sub(y)
                                                            Dim rowOffset = y * stride
                                                            For x As Integer = 0 To source.Width - 1
                                                                Dim o = rowOffset + x * 4
                                                                Dim nc = ProcessHslPixel(srcBuf(o + 2), srcBuf(o + 1), srcBuf(o), srcBuf(o + 3), adj)
                                                                dstBuf(o) = nc.Blue : dstBuf(o + 1) = nc.Green : dstBuf(o + 2) = nc.Red : dstBuf(o + 3) = nc.Alpha
                                                            Next
                                                        End Sub)
                CommitBgraBuffer(result, dstBuf)
                Return result
            End If

            For y As Integer = 0 To source.Height - 1
                For x As Integer = 0 To source.Width - 1
                    Dim c = source.GetPixel(x, y)
                    result.SetPixel(x, y, ProcessHslPixel(c.Red, c.Green, c.Blue, c.Alpha, adj))
                Next
            Next
            Return result
        End Function

        Private Shared Sub GetHslBandAdjustments(hue As Double, adj As ImageAdjustments, ByRef hueShift As Single, ByRef satShift As Single, ByRef lumShift As Single)
            Select Case hue
                Case < 15, >= 345
                    hueShift = adj.RedHue : satShift = adj.RedSaturation : lumShift = adj.RedLuminance
                Case < 45
                    hueShift = adj.OrangeHue : satShift = adj.OrangeSaturation : lumShift = adj.OrangeLuminance
                Case < 75
                    hueShift = adj.YellowHue : satShift = adj.YellowSaturation : lumShift = adj.YellowLuminance
                Case < 165
                    hueShift = adj.GreenHue : satShift = adj.GreenSaturation : lumShift = adj.GreenLuminance
                Case < 195
                    hueShift = adj.AquaHue : satShift = adj.AquaSaturation : lumShift = adj.AquaLuminance
                Case < 255
                    hueShift = adj.BlueHue : satShift = adj.BlueSaturation : lumShift = adj.BlueLuminance
                Case < 285
                    hueShift = adj.PurpleHue : satShift = adj.PurpleSaturation : lumShift = adj.PurpleLuminance
                Case Else
                    hueShift = adj.MagentaHue : satShift = adj.MagentaSaturation : lumShift = adj.MagentaLuminance
            End Select
        End Sub

        ''' Färbt Schatten und Lichter getrennt ein (Lightroom-Split-Toning-Konzept): Balance verschiebt
        ''' den Umschlagpunkt zwischen "gilt als Schatten" und "gilt als Licht" auf der Luminanzachse,
        ''' die Sättigungsregler steuern zugleich die Deckkraft der jeweiligen Einfärbung.
        Private Shared Function ProcessSplitToningPixel(r As Byte, g As Byte, b As Byte, a As Byte, adj As ImageAdjustments,
                                                         hasShadow As Boolean, hasHighlight As Boolean, pivot As Double) As SKColor
            Dim h As Double
            Dim s As Double
            Dim l As Double
            RgbToHsl(r, g, b, h, s, l)

            Dim rr As Double = r
            Dim gg As Double = g
            Dim bb As Double = b

            If hasShadow Then
                Dim weight = Math.Max(0.0, Math.Min(1.0, (pivot - l) / pivot))
                If weight > 0 Then
                    Dim tint = HslToRgb(adj.SplitToningShadowHue, adj.SplitToningShadowSaturation / 100.0, l, 255)
                    Dim amount = weight * (adj.SplitToningShadowSaturation / 100.0)
                    rr += (tint.Red - rr) * amount
                    gg += (tint.Green - gg) * amount
                    bb += (tint.Blue - bb) * amount
                End If
            End If
            If hasHighlight Then
                Dim weight = Math.Max(0.0, Math.Min(1.0, (l - pivot) / (1.0 - pivot)))
                If weight > 0 Then
                    Dim tint = HslToRgb(adj.SplitToningHighlightHue, adj.SplitToningHighlightSaturation / 100.0, l, 255)
                    Dim amount = weight * (adj.SplitToningHighlightSaturation / 100.0)
                    rr += (tint.Red - rr) * amount
                    gg += (tint.Green - gg) * amount
                    bb += (tint.Blue - bb) * amount
                End If
            End If

            Return New SKColor(ClampToByte(rr), ClampToByte(gg), ClampToByte(bb), a)
        End Function

        Private Shared Function ApplySplitToning(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim hasShadow = adj.SplitToningShadowSaturation <> 0
            Dim hasHighlight = adj.SplitToningHighlightSaturation <> 0
            If Not hasShadow AndAlso Not hasHighlight Then Return source

            Dim balance = Clamp(adj.SplitToningBalance, -100, 100) / 100.0
            Dim pivot = Math.Max(0.1, Math.Min(0.9, 0.5 - balance * 0.4))

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)

            Dim srcBuf As Byte() = Nothing
            Dim stride As Integer = 0
            If TryBorrowBgraBuffer(source, srcBuf, stride) Then
                Dim dstBuf = New Byte(srcBuf.Length - 1) {}
                ForEachRow(source.Width, source.Height, Sub(y)
                                                            Dim rowOffset = y * stride
                                                            For x As Integer = 0 To source.Width - 1
                                                                Dim o = rowOffset + x * 4
                                                                Dim nc = ProcessSplitToningPixel(srcBuf(o + 2), srcBuf(o + 1), srcBuf(o), srcBuf(o + 3), adj, hasShadow, hasHighlight, pivot)
                                                                dstBuf(o) = nc.Blue : dstBuf(o + 1) = nc.Green : dstBuf(o + 2) = nc.Red : dstBuf(o + 3) = nc.Alpha
                                                            Next
                                                        End Sub)
                CommitBgraBuffer(result, dstBuf)
                Return result
            End If

            For y As Integer = 0 To source.Height - 1
                For x As Integer = 0 To source.Width - 1
                    Dim c = source.GetPixel(x, y)
                    result.SetPixel(x, y, ProcessSplitToningPixel(c.Red, c.Green, c.Blue, c.Alpha, adj, hasShadow, hasHighlight, pivot))
                Next
            Next
            Return result
        End Function

        Private Shared Function ApplyFilterPreset(source As SKBitmap, preset As String, strength As Single) As SKBitmap
            If String.IsNullOrWhiteSpace(preset) OrElse String.Equals(preset, "Keine", StringComparison.OrdinalIgnoreCase) Then
                Return source
            End If
            strength = Clamp(strength, 0, 1)
            If strength <= 0 Then Return source

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
                    matrix = New Single() {
                        1.04F, 0, 0, 0, 2,
                        0, 1.01F, 0, 0, 0,
                        0, 0, 0.97F, 0, -2,
                        0, 0, 0, 1, 0
                    }
                Case "kühl", "kuehl"
                    matrix = New Single() {
                        0.92F, 0, 0, 0, -4,
                        0, 1.01F, 0, 0, 0,
                        0, 0, 1.1F, 0, 8,
                        0, 0, 0, 1, 0
                    }
                Case "fade"
                    matrix = New Single() {
                        0.96F, 0.02F, 0.02F, 0, 8,
                        0.02F, 0.96F, 0.02F, 0, 8,
                        0.02F, 0.02F, 0.96F, 0, 8,
                        0, 0, 0, 1, 0
                    }
                Case "kontrast"
                    matrix = New Single() {
                        1.16F, 0, 0, 0, -18,
                        0, 1.16F, 0, 0, -18,
                        0, 0, 1.16F, 0, -18,
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
                    matrix = New Single() {
                        0.92F, 0.03F, 0.02F, 0, 14,
                        0.02F, 0.92F, 0.02F, 0, 14,
                        0.02F, 0.03F, 0.92F, 0, 14,
                        0, 0, 0, 1, 0
                    }
                Case "cross"
                    matrix = New Single() {
                        1.08F, 0, 0, 0, -4,
                        0, 1.0F, 0, 0, 4,
                        0, 0, 0.9F, 0, 10,
                        0, 0, 0, 1, 0
                    }
                Case "dramatisch"
                    matrix = New Single() {
                        1.24F, 0, 0, 0, -28,
                        0, 1.18F, 0, 0, -24,
                        0, 0, 1.12F, 0, -18,
                        0, 0, 0, 1, 0
                    }
                Case "weich"
                    ''' Echte räumliche Unschärfe statt nur einer Farbmatrix - eine Farbmatrix wirkt
                    ''' pro Pixel und kann strukturell nicht weichzeichnen (das war vorher hier nur ein
                    ''' minimaler Weißabgleich/Aufhellungs-Trick, sah "Weich" praktisch identisch zu
                    ''' "Matt"/"Fade").
                    Return ApplySoftFocusBlur(source, strength)
                Case "noir"
                    matrix = New Single() {
                        0.404F, 0.792F, 0.154F, 0, -38,
                        0.404F, 0.792F, 0.154F, 0, -38,
                        0.404F, 0.792F, 0.154F, 0, -38,
                        0, 0, 0, 1, 0
                    }
                Case "duoton", "duotone"
                    ' Echter Zweifarb-Verlauf über die Luminanz: dunkles Indigo (Schatten) zu sattem Orange (Lichter).
                    ' Jeder Pixel landet exakt auf der Verlaufslinie zwischen den beiden Zielfarben - unabhängig
                    ' vom Ausgangston. Endpunkte sind bewusst kräftig/gesättigt, nicht Richtung Weiß.
                    matrix = New Single() {
                        0.2815F, 0.5525F, 0.1073F, 0, 15,
                        0.1759F, 0.3453F, 0.0671F, 0, 15,
                        -0.0235F, -0.0460F, -0.0089F, 0, 60,
                        0, 0, 0, 1, 0
                    }
                Case "polaroid"
                    ' Kräftiger warmer Gelbstich mit moderat angehobenen Schwarzwerten (Sofortbild-Charakter),
                    ' Kontrast bleibt weitgehend erhalten statt komplett verwaschen wie bei "Fade".
                    matrix = New Single() {
                        0.95F, 0.15F, -0.05F, 0, 10,
                        0.05F, 0.90F, 0.05F, 0, 6,
                        -0.05F, 0.05F, 0.75F, 0, 2,
                        0, 0, 0, 1, 0
                    }
                Case "vhs"
                    ' Deutlich kühlerer Cyan-/Blaustich mit sichtbarem Kanal-Bluten, typisch für Analogvideo.
                    matrix = New Single() {
                        0.70F, 0.15F, 0.15F, 0, 0,
                        0.10F, 0.85F, 0.15F, 0, 4,
                        0.05F, 0.20F, 0.85F, 0, 10,
                        0, 0, 0, 1, 0
                    }
                Case "bild auf alt", "alt", "antik", "vintage"
                    matrix = New Single() {
                        0.78F, 0.26F, 0.08F, 0, 18,
                        0.18F, 0.74F, 0.10F, 0, 10,
                        0.06F, 0.18F, 0.62F, 0, 2,
                        0, 0, 0, 1, 0
                    }
                Case Else
                    Return source
            End Select

            Dim colorFilter = SKColorFilter.CreateColorMatrix(matrix)
            Dim paint = New SKPaint With {.ColorFilter = colorFilter}
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.DrawBitmap(source, 0, 0)
                paint.Color = New SKColor(255, 255, 255, ClampToByte(255 * strength))
                canvas.DrawBitmap(source, 0, 0, paint)
            End Using
            colorFilter.Dispose()
            paint.Dispose()
            Return result
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

            If size < 2 OrElse values.Count <> size * size * size * 3 Then Return Nothing

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

        Private Shared Function ProcessCubeLutPixel(r As Byte, g As Byte, b As Byte, a As Byte, table As Single(), size As Integer, maxIndex As Integer, strength As Single) As SKColor
            Dim rf = r / 255.0F * maxIndex
            Dim gf = g / 255.0F * maxIndex
            Dim bf = b / 255.0F * maxIndex

            Dim r0 = CInt(Math.Floor(rf))
            Dim g0 = CInt(Math.Floor(gf))
            Dim b0 = CInt(Math.Floor(bf))
            Dim r1 = Math.Min(maxIndex, r0 + 1)
            Dim g1 = Math.Min(maxIndex, g0 + 1)
            Dim b1 = Math.Min(maxIndex, b0 + 1)
            Dim rt = rf - r0
            Dim gt = gf - g0
            Dim bt = bf - b0

            Dim outR = TrilinearChannel(table, size, r0, r1, g0, g1, b0, b1, rt, gt, bt, 0) * 255.0F
            Dim outG = TrilinearChannel(table, size, r0, r1, g0, g1, b0, b1, rt, gt, bt, 1) * 255.0F
            Dim outB = TrilinearChannel(table, size, r0, r1, g0, g1, b0, b1, rt, gt, bt, 2) * 255.0F

            If strength >= 0.999F Then
                Return New SKColor(ClampToByte(outR), ClampToByte(outG), ClampToByte(outB), a)
            End If
            Return New SKColor(
                ClampToByte(r + (outR - r) * strength),
                ClampToByte(g + (outG - g) * strength),
                ClampToByte(b + (outB - b) * strength),
                a)
        End Function

        Private Shared Function ApplyCubeLut(source As SKBitmap, path As String, strength As Single) As SKBitmap
            strength = Clamp(strength, 0, 1)
            If strength <= 0 OrElse String.IsNullOrWhiteSpace(path) Then Return source

            Dim lut = LoadCubeLut(path)
            If lut Is Nothing Then Return source

            Dim size = lut.Size
            Dim maxIndex = size - 1
            Dim table = lut.Table
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)

            Dim srcBuf As Byte() = Nothing
            Dim stride As Integer = 0
            If TryBorrowBgraBuffer(source, srcBuf, stride) Then
                Dim dstBuf = New Byte(srcBuf.Length - 1) {}
                ForEachRow(source.Width, source.Height, Sub(y)
                                                            Dim rowOffset = y * stride
                                                            For x = 0 To source.Width - 1
                                                                Dim o = rowOffset + x * 4
                                                                Dim nc = ProcessCubeLutPixel(srcBuf(o + 2), srcBuf(o + 1), srcBuf(o), srcBuf(o + 3), table, size, maxIndex, strength)
                                                                dstBuf(o) = nc.Blue : dstBuf(o + 1) = nc.Green : dstBuf(o + 2) = nc.Red : dstBuf(o + 3) = nc.Alpha
                                                            Next
                                                        End Sub)
                CommitBgraBuffer(result, dstBuf)
                Return result
            End If

            For y = 0 To source.Height - 1
                For x = 0 To source.Width - 1
                    Dim c = source.GetPixel(x, y)
                    result.SetPixel(x, y, ProcessCubeLutPixel(c.Red, c.Green, c.Blue, c.Alpha, table, size, maxIndex, strength))
                Next
            Next
            Return result
        End Function

        Private Shared Function ApplyVignette(source As SKBitmap, amount As Single, transition As Single, roundness As Single, feather As Single, centerXPercent As Single, centerYPercent As Single) As SKBitmap
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
                                                                    dstBuf(o) = ClampToByte(bB * (1 - mix))
                                                                    dstBuf(o + 1) = ClampToByte(gG * (1 - mix))
                                                                    dstBuf(o + 2) = ClampToByte(rR * (1 - mix))
                                                                Else
                                                                    dstBuf(o) = ClampToByte(bB + (255 - bB) * mix)
                                                                    dstBuf(o + 1) = ClampToByte(gG + (255 - gG) * mix)
                                                                    dstBuf(o + 2) = ClampToByte(rR + (255 - rR) * mix)
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
                        result.SetPixel(x, y, New SKColor(ClampToByte(c.Red * (1 - mix)),
                                                          ClampToByte(c.Green * (1 - mix)),
                                                          ClampToByte(c.Blue * (1 - mix)),
                                                          c.Alpha))
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

        Private Shared Function ApplyGrain(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return source

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim random = New Random(source.Width * 397 Xor source.Height * 151)
            Dim amplitude = 8.0 + strength * 34.0

            For y As Integer = 0 To source.Height - 1
                For x As Integer = 0 To source.Width - 1
                    Dim c = source.GetPixel(x, y)
                    Dim noise = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    Dim r = ClampToByte(c.Red + noise)
                    Dim g = ClampToByte(c.Green + noise)
                    Dim b = ClampToByte(c.Blue + noise)
                    result.SetPixel(x, y, New SKColor(r, g, b, c.Alpha))
                Next
            Next

            Return result
        End Function

        Private Shared Function ApplyAddNoise(source As SKBitmap, amount As Single) As SKBitmap
            Dim strength = Clamp(amount, 0, 1)
            If strength <= 0 Then Return source
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim random = New Random(source.Width * 541 Xor source.Height * 877)
            Dim amplitude = strength * 72.0
            For y As Integer = 0 To source.Height - 1
                For x As Integer = 0 To source.Width - 1
                    Dim c = source.GetPixel(x, y)
                    ' Digitales Rauschen ist chromatisch: pro Kanal ein eigener Zufallswert, damit
                    ' farbige Sensor-Speckles entstehen. Das unterscheidet "Rauschen" klar von der
                    ' monochromen "Körnung" (ApplyGrain), die denselben Wert auf alle Kanäle legt.
                    Dim noiseR = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    Dim noiseG = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    Dim noiseB = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    result.SetPixel(x, y, New SKColor(ClampToByte(c.Red + noiseR),
                                                      ClampToByte(c.Green + noiseG),
                                                      ClampToByte(c.Blue + noiseB),
                                                      c.Alpha))
                Next
            Next
            Return result
        End Function

        Private Shared Function ApplyBorder(source As SKBitmap, sizePercent As Single, colorValue As String, cornerRadiusPercent As Single, effect As String) As SKBitmap
            Dim thickness = CInt(Math.Round(Math.Min(source.Width, source.Height) * Clamp(sizePercent, 0, 0.25F)))
            If thickness <= 0 Then Return source

            Dim result = CloneBitmap(source)
            Using canvas = New SKCanvas(result)
                Dim color = ParseColor(colorValue, SKColors.White)
                Dim normalized = If(effect, "Einfach").Trim().ToLowerInvariant()
                Dim radius = Math.Min(source.Width, source.Height) * Clamp(cornerRadiusPercent, 0, 1) * 0.25F

                If normalized = "doppelt" Then
                    ''' Zwei dünne konzentrische Linien mit Lücke dazwischen (klassischer Passepartout-Look)
                    ''' statt einer einzelnen Linie in voller Stärke.
                    Dim thinWidth = Math.Max(1.0F, thickness * 0.35F)
                    Dim gap = thickness * 0.6F
                    Using paint = New SKPaint With {.Color = color, .Style = SKPaintStyle.Stroke, .StrokeWidth = thinWidth, .IsAntialias = True}
                        Dim outerInset = thinWidth / 2.0F
                        Dim outerRect = New SKRect(outerInset, outerInset, source.Width - outerInset, source.Height - outerInset)
                        Dim innerRect = New SKRect(outerInset + gap, outerInset + gap, source.Width - outerInset - gap, source.Height - outerInset - gap)
                        If radius > 0 Then
                            canvas.DrawRoundRect(outerRect, radius, radius, paint)
                            canvas.DrawRoundRect(innerRect, Math.Max(0.0F, radius - gap), Math.Max(0.0F, radius - gap), paint)
                        Else
                            canvas.DrawRect(outerRect, paint)
                            canvas.DrawRect(innerRect, paint)
                        End If
                    End Using
                    Return result
                End If

                Using paint = New SKPaint With {.Color = color, .Style = SKPaintStyle.Stroke, .StrokeWidth = thickness, .IsAntialias = True}
                    Select Case normalized
                        Case "gestrichelt"
                            paint.PathEffect = SKPathEffect.CreateDash(New Single() {thickness * 1.4F, thickness * 0.9F}, 0)
                        Case "punktiert"
                            ''' Sehr kurzes "An"-Segment + runde Stroke-Caps rendert als Punktreihe statt Striche.
                            paint.StrokeCap = SKStrokeCap.Round
                            paint.PathEffect = SKPathEffect.CreateDash(New Single() {0.01F, thickness * 1.3F}, 0)
                    End Select
                    Dim inset = thickness / 2.0F
                    Dim rect = New SKRect(inset, inset, source.Width - inset, source.Height - inset)
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
            End Using
            Return result
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

        Private Shared Function ApplyTonalLUT(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If adj.Highlights = 0 AndAlso adj.ShadowsLevel = 0 AndAlso adj.Whites = 0 AndAlso adj.Blacks = 0 Then
                Return source
            End If

            Dim lut = New Byte(255) {}
            For i As Integer = 0 To 255
                Dim v = CDbl(i) / 255.0

                ' Blacks: Schwarzpunkt anheben/senken (0 bis 0.4) - breiteres Fenster und stärkerer Effekt,
                ' damit die Änderung auch auf normal belichteten Fotos deutlich sichtbar ist.
                Dim bw = Math.Max(0.0, 1.0 - v * 2.5)
                v += (adj.Blacks / 100.0) * bw * 0.4

                ' Shadows: zentriert bei 0.25
                Dim sw = Math.Max(0.0, 1.0 - Math.Abs(v - 0.25) * 6.0)
                v += (adj.ShadowsLevel / 100.0) * sw * 0.35

                ' Whites: Weißpunkt anheben/senken (0.6 bis 1.0)
                Dim ww = Math.Max(0.0, 1.0 - (1.0 - v) * 2.5)
                v += (adj.Whites / 100.0) * ww * 0.4

                ' Highlights: zentriert bei 0.75
                Dim hw = Math.Max(0.0, 1.0 - Math.Abs(v - 0.75) * 6.0)
                v += (adj.Highlights / 100.0) * hw * 0.35

                lut(i) = ClampToByte(v * 255.0)
            Next

            Dim colorFilter = SKColorFilter.CreateTable(IdentityByteTable, lut, lut, lut)
            Dim paint = New SKPaint With {.ColorFilter = colorFilter}
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.DrawBitmap(source, 0, 0, paint)
            End Using
            colorFilter.Dispose()
            paint.Dispose()
            Return result
        End Function

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

            Using image = SKImage.FromBitmap(skBitmap)
                Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                    Using ms = New MemoryStream()
                        data.SaveTo(ms)
                        ms.Seek(0, SeekOrigin.Begin)
                        Return New Bitmap(ms)
                    End Using
                End Using
            End Using
        End Function
    End Class

End Namespace
