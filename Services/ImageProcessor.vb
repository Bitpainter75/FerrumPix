Imports System
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.IO
Imports System.Linq
Imports System.Runtime.CompilerServices
Imports System.Threading
Imports SkiaSharp
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports System.Text.RegularExpressions
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

    Public Class RetouchSpot
        Public Property XPercent As Single
        Public Property YPercent As Single
        Public Property RadiusPercent As Single

        Public Function Clone() As RetouchSpot
            Return New RetouchSpot With {.XPercent = XPercent, .YPercent = YPercent, .RadiusPercent = RadiusPercent}
        End Function
    End Class

    Public Class ImageAnnotation
        Implements INotifyPropertyChanged

        Private _kind As String = "Text"
        Private _text As String = ""
        Private _imagePath As String = ""
        Private _xPercent As Single = 50
        Private _yPercent As Single = 50
        Private _widthPercent As Single = 30
        Private _heightPercent As Single = 10
        Private _fillColor As String = "#FFFFFFFF"
        Private _strokeColor As String = "#FF000000"
        Private _strokeWidth As Single = 0
        Private _fontSizePercent As Single = 6
        Private _fontFamily As String = "Arial"
        Private _opacity As Single = 100
        Private _rotationDegrees As Single = 0
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
        Private _glowEnabled As Boolean = False
        Private _glowBlur As Single = 10
        Private _glowStrength As Single = 100
        Private _glowColor As String = "#FFFFFF00"

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
            Return name.Replace("_", " ")
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
                    Case "watermark" : Return base & "09_FormenSymbole/077_Bild.svg"
                    Case "image", "selectionimage" : Return base & "09_FormenSymbole/077_Bild.svg"
                    Case "qr", "qrcode", "qr-code" : Return base & "03_Editor/37_QR_Code.svg"
                    Case "rectangle", "rect", "selectionfill" : Return base & "09_FormenSymbole/005_Rechteck.svg"
                    Case "square" : Return base & "09_FormenSymbole/003_Quadrat.svg"
                    Case "triangle" : Return base & "09_FormenSymbole/007_Dreieck.svg"
                    Case "ellipse", "circle" : Return base & "09_FormenSymbole/017_Oval.svg"
                    Case "cone" : Return base & "09_FormenSymbole/018_Halbkreis.svg"
                    Case "pyramid" : Return base & "09_FormenSymbole/031_Diamant_facette.svg"
                    Case "trapezoid" : Return base & "09_FormenSymbole/013_Trapez.svg"
                    Case "diamond" : Return base & "09_FormenSymbole/011_Raute.svg"
                    Case "spiral" : Return base & "09_FormenSymbole/053_Spirale.svg"
                    Case "droplet" : Return base & "09_FormenSymbole/051_Tropfen.svg"
                    Case "speechbubble", "speech-bubble", "sprechblase", "bubble" : Return base & "09_FormenSymbole/048_Sprechblase.svg"
                    Case "brush", "eraser" : Return base & "03_Editor/39_Pinsel.svg"
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
                Case "selectionfill", "selectionimage" : Return "Auswahl"
                Case "ellipse", "circle" : Return "Ellipse"
                Case "square" : Return "Quadrat"
                Case "triangle" : Return "Dreieck"
                Case "cone" : Return "Kegel"
                Case "pyramid" : Return "Pyramide"
                Case "trapezoid" : Return "Trapez"
                Case "diamond" : Return "Raute"
                Case "spiral" : Return "Spirale"
                Case "droplet" : Return "Tropfen"
                Case "speechbubble", "speech-bubble", "sprechblase", "bubble" : Return "Sprechblase"
                Case "line" : Return "Linie"
                Case "arrow" : Return "Pfeil"
                Case "brush" : Return "Pinsel"
                Case "eraser" : Return "Radiergummi"
                Case "symbol" : Return "Symbol"
                Case "qr", "qrcode", "qr-code" : Return "QR-Code"
                Case Else : Return If(String.IsNullOrWhiteSpace(kind), "Ebene", kind)
            End Select
        End Function

        Public Property XPercent As Single
            Get
                Return _xPercent
            End Get
            Set(value As Single)
                SetField(_xPercent, value)
            End Set
        End Property

        Public Property YPercent As Single
            Get
                Return _yPercent
            End Get
            Set(value As Single)
                SetField(_yPercent, value)
            End Set
        End Property

        Public Property WidthPercent As Single
            Get
                Return _widthPercent
            End Get
            Set(value As Single)
                SetField(_widthPercent, value)
            End Set
        End Property

        Public Property HeightPercent As Single
            Get
                Return _heightPercent
            End Get
            Set(value As Single)
                SetField(_heightPercent, value)
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

        Public Property StrokeWidth As Single
            Get
                Return _strokeWidth
            End Get
            Set(value As Single)
                SetField(_strokeWidth, value)
            End Set
        End Property

        Public Property FontSizePercent As Single
            Get
                Return _fontSizePercent
            End Get
            Set(value As Single)
                SetField(_fontSizePercent, value)
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

        Public Property RotationDegrees As Single
            Get
                Return _rotationDegrees
            End Get
            Set(value As Single)
                SetField(_rotationDegrees, value)
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

        Public Function Clone() As ImageAnnotation
            Return New ImageAnnotation With {
                .Kind = Kind,
                .Text = Text,
                .ImagePath = ImagePath,
                .XPercent = XPercent,
                .YPercent = YPercent,
                .WidthPercent = WidthPercent,
                .HeightPercent = HeightPercent,
                .FillColor = FillColor,
                .StrokeColor = StrokeColor,
                .StrokeWidth = StrokeWidth,
                .FontSizePercent = FontSizePercent,
                .FontFamily = FontFamily,
                .Opacity = Opacity,
                .RotationDegrees = RotationDegrees,
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
                .GlowEnabled = GlowEnabled,
                .GlowBlur = GlowBlur,
                .GlowStrength = GlowStrength,
                .GlowColor = GlowColor
            }
        End Function

        Private Sub SetField(Of T)(ByRef field As T, value As T, <CallerMemberName> Optional propertyName As String = Nothing)
            If EqualityComparer(Of T).Default.Equals(field, value) Then Return
            field = value
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
        End Sub
    End Class

    Public Class ImageAdjustments
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
        Public Property CurveRgbPoints As String = "0,0;255,255"
        Public Property CurveRedPoints As String = "0,0;255,255"
        Public Property CurveGreenPoints As String = "0,0;255,255"
        Public Property CurveBluePoints As String = "0,0;255,255"
        Public Property CurveLuminancePoints As String = "0,0;255,255"
        Public Property RedHue As Single = 0
        Public Property RedSaturation As Single = 0
        Public Property OrangeHue As Single = 0
        Public Property OrangeSaturation As Single = 0
        Public Property YellowHue As Single = 0
        Public Property YellowSaturation As Single = 0
        Public Property GreenHue As Single = 0
        Public Property GreenSaturation As Single = 0
        Public Property AquaHue As Single = 0
        Public Property AquaSaturation As Single = 0
        Public Property BlueHue As Single = 0
        Public Property BlueSaturation As Single = 0
        Public Property PurpleHue As Single = 0
        Public Property PurpleSaturation As Single = 0
        Public Property MagentaHue As Single = 0
        Public Property MagentaSaturation As Single = 0
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
        Public Property RetouchSpots As New System.Collections.Generic.List(Of RetouchSpot)()
        Public Property Annotations As New System.Collections.Generic.List(Of ImageAnnotation)()

        Public Shared Function IsIdentityCurve(pointsCsv As String) As Boolean
            Return String.IsNullOrWhiteSpace(pointsCsv) OrElse String.Equals(pointsCsv.Trim(), "0,0;255,255", StringComparison.Ordinal)
        End Function

        Public Function HasChanges() As Boolean
            Return Exposure <> 0 OrElse Brightness <> 0 OrElse Contrast <> 0 OrElse
                   Saturation <> 0 OrElse Vibrance <> 0 OrElse Highlights <> 0 OrElse ShadowsLevel <> 0 OrElse
                   Whites <> 0 OrElse Blacks <> 0 OrElse Temperature <> 0 OrElse Tint <> 0 OrElse
                   Sharpness <> 0 OrElse NoiseReduction <> 0 OrElse DustScratches <> 0 OrElse Haze <> 0 OrElse
                   AddNoise <> 0 OrElse [Structure] <> 0 OrElse Glow <> 0 OrElse Vignette <> 0 OrElse
                   VignetteTransition <> 55 OrElse VignetteRoundness <> 0 OrElse VignetteFeather <> 70 OrElse
                   VignetteCenterX <> 50 OrElse VignetteCenterY <> 50 OrElse
                   Grain <> 0 OrElse BorderSize <> 0 OrElse BorderCornerRadius <> 0 OrElse
                   Not String.Equals(BorderEffect, "Einfach", StringComparison.OrdinalIgnoreCase) OrElse Clarity <> 0 OrElse
                   Not IsIdentityCurve(CurveRgbPoints) OrElse Not IsIdentityCurve(CurveRedPoints) OrElse
                   Not IsIdentityCurve(CurveGreenPoints) OrElse Not IsIdentityCurve(CurveBluePoints) OrElse
                   Not IsIdentityCurve(CurveLuminancePoints) OrElse HasHslChanges() OrElse
                   RotationDegrees <> 0 OrElse StraightenDegrees <> 0 OrElse
                   FlipHorizontal OrElse FlipVertical OrElse CropLeftPercent <> 0 OrElse CropTopPercent <> 0 OrElse
                   CropRightPercent <> 0 OrElse CropBottomPercent <> 0 OrElse ResizeWidth > 0 OrElse ResizeHeight > 0 OrElse
                   CanvasWidth > 0 OrElse CanvasHeight > 0 OrElse
                   RetouchSpots.Count > 0 OrElse Annotations.Count > 0 OrElse
                   Not String.Equals(FilterPreset, "Keine", StringComparison.OrdinalIgnoreCase)
        End Function

        Public Function HasHslChanges() As Boolean
            Return RedHue <> 0 OrElse RedSaturation <> 0 OrElse OrangeHue <> 0 OrElse OrangeSaturation <> 0 OrElse
                   YellowHue <> 0 OrElse YellowSaturation <> 0 OrElse GreenHue <> 0 OrElse GreenSaturation <> 0 OrElse
                   AquaHue <> 0 OrElse AquaSaturation <> 0 OrElse BlueHue <> 0 OrElse BlueSaturation <> 0 OrElse
                   PurpleHue <> 0 OrElse PurpleSaturation <> 0 OrElse MagentaHue <> 0 OrElse MagentaSaturation <> 0
        End Function

        Public Function Clone() As ImageAdjustments
            Return New ImageAdjustments With {
                .Exposure = Exposure,
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
                .CurveRgbPoints = CurveRgbPoints,
                .CurveRedPoints = CurveRedPoints,
                .CurveGreenPoints = CurveGreenPoints,
                .CurveBluePoints = CurveBluePoints,
                .CurveLuminancePoints = CurveLuminancePoints,
                .RedHue = RedHue,
                .RedSaturation = RedSaturation,
                .OrangeHue = OrangeHue,
                .OrangeSaturation = OrangeSaturation,
                .YellowHue = YellowHue,
                .YellowSaturation = YellowSaturation,
                .GreenHue = GreenHue,
                .GreenSaturation = GreenSaturation,
                .AquaHue = AquaHue,
                .AquaSaturation = AquaSaturation,
                .BlueHue = BlueHue,
                .BlueSaturation = BlueSaturation,
                .PurpleHue = PurpleHue,
                .PurpleSaturation = PurpleSaturation,
                .MagentaHue = MagentaHue,
                .MagentaSaturation = MagentaSaturation,
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
                .RetouchSpots = RetouchSpots.Select(Function(s) s.Clone()).ToList(),
                .Annotations = Annotations.Select(Function(a) a.Clone()).ToList()
            }
        End Function
    End Class

    Friend Module AnnotationLayoutHelpers
        Private Function ClampPercent(value As Single, min As Single, max As Single) As Single
            Return Math.Max(min, Math.Min(max, value))
        End Function

        Friend Function NormalizeAnnotationAnchor(value As String) As String
            Select Case If(value, "").Trim()
                Case "TopLeft", "Top", "TopRight", "Left", "Center", "Right", "BottomLeft", "Bottom", "BottomRight"
                    Return value.Trim()
                Case Else
                    Return "BottomRight"
            End Select
        End Function

        Friend Function ComputeAnnotationRect(sourceWidth As Integer, sourceHeight As Integer, kind As String, annotation As ImageAnnotation) As SKRect
            Dim width = Math.Max(1.0F, sourceWidth * ClampPercent(annotation.WidthPercent, 1, 100) / 100.0F)
            Dim height = Math.Max(1.0F, sourceHeight * ClampPercent(annotation.HeightPercent, 1, 100) / 100.0F)
            Dim normalizedKind = If(kind, "").Trim().ToLowerInvariant()
            Dim x As Single
            Dim y As Single

            If normalizedKind = "watermark" Then
                Dim offsetX = sourceWidth * ClampPercent(annotation.XPercent, -100, 100) / 100.0F
                Dim offsetY = sourceHeight * ClampPercent(annotation.YPercent, -100, 100) / 100.0F
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
                x = sourceWidth * ClampPercent(annotation.XPercent, -100, 100) / 100.0F
                y = sourceHeight * ClampPercent(annotation.YPercent, -100, 100) / 100.0F
            End If

            Return New SKRect(x, y, x + width, y + height)
        End Function
    End Module

    Public Class ImageProcessor

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
            Return File.OpenRead(path)
        End Function

        Private Shared Function DecodeOriented(path As String) As SKBitmap
            Using stream = OpenSourceStream(path)
                If stream Is Nothing Then Return Nothing
                Using codec = SKCodec.Create(stream)
                    If codec Is Nothing OrElse codec.EncodedOrigin = SKEncodedOrigin.TopLeft Then
                        stream.Seek(0, SeekOrigin.Begin)
                        Return SKBitmap.Decode(stream)
                    End If

                    Dim info = codec.Info
                    Dim decodeInfo = New SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Dim original = New SKBitmap(decodeInfo)
                    Dim result = codec.GetPixels(decodeInfo, original.GetPixels())
                    If result <> SKCodecResult.Success AndAlso result <> SKCodecResult.IncompleteInput Then
                        original.Dispose()
                        stream.Seek(0, SeekOrigin.Begin)
                        Return SKBitmap.Decode(stream)
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

        Public Shared Function LoadThumbnail(imagePath As String, maxSize As Integer) As Bitmap
            Try
                Using stream = File.OpenRead(imagePath)
                    Return Bitmap.DecodeToWidth(stream, maxSize)
                End Using
            Catch
                Return Nothing
            End Try
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
                        Using paint = New SKPaint With {.FilterQuality = SKFilterQuality.High, .IsAntialias = True}
                            canvas.DrawBitmap(original, New SKRect(0, 0, original.Width, original.Height), New SKRect(0, 0, width, height), paint)
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
            Dim exposure = CSng(Math.Pow(2.0, adj.Exposure / 100.0 * 4.0))

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
                (lumR * invSat + sat) * tempR * exposure, lumG * invSat * tempR * exposure, lumB * invSat * tempR * exposure, 0, 0,
                lumR * invSat * exposure, (lumG * invSat + sat) * tintG * exposure, lumB * invSat * exposure, 0, 0,
                lumR * invSat * tempB * exposure, lumG * invSat * tempB * exposure, (lumB * invSat + sat) * tempB * exposure, 0, 0,
                0, 0, 0, 1, 0
            }

            Dim contrast = Math.Max(0.0F, 1.0F + adj.Contrast / 100.0F * 0.5F)
            ' Helligkeit und Kontrast im üblichen Fotobereich halten, damit die Vorschau nicht ausreißt.
            ' SkiaSharps ColorMatrix arbeitet auf normalisierten [0,1]-Farbwerten - auch der Verschiebungs-
            ' anteil (5. Spalte) ist in dieser Skala anzugeben, nicht in 0-255. Ohne die Division durch 255
            ' würde bereits eine kleine Helligkeits-/Kontraständerung das Bild sofort auf Weiß/Schwarz ziehen.
            Dim brightness = adj.Brightness / 100.0F * 48.0F / 255.0F
            Dim contrastOffset = brightness + (1.0F - contrast) * (128.0F / 255.0F)
            Dim contrastMatrix = New Single() {
                contrast, 0, 0, 0, contrastOffset,
                0, contrast, 0, 0, contrastOffset,
                0, 0, contrast, 0, contrastOffset,
                0, 0, 0, 1, 0
            }

            Dim colorFilter = SKColorFilter.CreateColorMatrix(colorMatrix)
            Dim contrastFilter = SKColorFilter.CreateColorMatrix(contrastMatrix)
            Dim paint = New SKPaint With {.ColorFilter = colorFilter}

            Dim result = New SKBitmap(source.Width, source.Height)
            Using stage = New SKBitmap(source.Width, source.Height)
                Using canvas = New SKCanvas(stage)
                    canvas.DrawBitmap(source, 0, 0, paint)
                End Using
                paint.Dispose()
                colorFilter.Dispose()

                Using contrastPaint = New SKPaint With {.ColorFilter = contrastFilter}
                    Using canvas = New SKCanvas(result)
                        canvas.DrawBitmap(stage, 0, 0, contrastPaint)
                    End Using
                End Using
            End Using

            contrastFilter.Dispose()

            Return result
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
            processed = ReplaceBitmap(processed, ApplyColorAdjustments(processed, adj))
            processed = ReplaceBitmap(processed, ApplyTonalLUT(processed, adj))
            processed = ReplaceBitmap(processed, ApplyCurve(processed, adj))
            processed = ReplaceBitmap(processed, ApplyHsl(processed, adj))
            processed = ReplaceBitmap(processed, ApplyFilterPreset(processed, adj.FilterPreset, adj.FilterStrength / 100.0F))

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

            Return processed
        End Function

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
                adj.CurveRgbPoints, adj.CurveRedPoints, adj.CurveGreenPoints, adj.CurveBluePoints, adj.CurveLuminancePoints,
                adj.RedHue, adj.RedSaturation, adj.OrangeHue, adj.OrangeSaturation, adj.YellowHue, adj.YellowSaturation,
                adj.GreenHue, adj.GreenSaturation, adj.AquaHue, adj.AquaSaturation, adj.BlueHue, adj.BlueSaturation,
                adj.PurpleHue, adj.PurpleSaturation, adj.MagentaHue, adj.MagentaSaturation,
                adj.RotationDegrees, adj.StraightenDegrees, adj.StraightenExpandCanvas, adj.FlipHorizontal, adj.FlipVertical,
                adj.CropLeftPercent, adj.CropTopPercent, adj.CropRightPercent, adj.CropBottomPercent,
                adj.ResizeWidth, adj.ResizeHeight, adj.LockResizeAspect, adj.ResizeInterpolation,
                adj.CanvasWidth, adj.CanvasHeight, adj.LockCanvasAspect, adj.CanvasAnchor, adj.CanvasBackgroundColor,
                adj.FilterPreset, adj.FilterStrength,
                String.Join(";", adj.RetouchSpots.Select(Function(s) $"{s.XPercent},{s.YPercent},{s.RadiusPercent}"))
            })
        End Function

        Private Shared Function ReplaceBitmap(oldBitmap As SKBitmap, newBitmap As SKBitmap) As SKBitmap
            If newBitmap Is Nothing OrElse Object.ReferenceEquals(oldBitmap, newBitmap) Then Return oldBitmap
            oldBitmap.Dispose()
            Return newBitmap
        End Function

        Private Shared Function CloneBitmap(source As SKBitmap) As SKBitmap
            Dim clone = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(clone)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return clone
        End Function

        Private Shared Function ApplyCrop(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim leftPct = Clamp(adj.CropLeftPercent, 0, 95) / 100.0F
            Dim topPct = Clamp(adj.CropTopPercent, 0, 95) / 100.0F
            Dim rightPct = Clamp(adj.CropRightPercent, 0, 95) / 100.0F
            Dim bottomPct = Clamp(adj.CropBottomPercent, 0, 95) / 100.0F

            If leftPct = 0 AndAlso topPct = 0 AndAlso rightPct = 0 AndAlso bottomPct = 0 Then Return source

            Dim left = CInt(Math.Round(source.Width * leftPct))
            Dim top = CInt(Math.Round(source.Height * topPct))
            Dim right = source.Width - CInt(Math.Round(source.Width * rightPct))
            Dim bottom = source.Height - CInt(Math.Round(source.Height * bottomPct))

            If right <= left + 1 OrElse bottom <= top + 1 Then Return source

            Dim cropWidth = right - left
            Dim cropHeight = bottom - top
            Dim result = New SKBitmap(cropWidth, cropHeight, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                Dim srcRect = New SKRect(left, top, right, bottom)
                Dim dstRect = New SKRect(0, 0, cropWidth, cropHeight)
                canvas.DrawBitmap(source, srcRect, dstRect)
            End Using
            Return result
        End Function

        Private Shared Function ApplyRetouch(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If adj.RetouchSpots Is Nothing OrElse adj.RetouchSpots.Count = 0 Then Return source

            Dim result = CloneBitmap(source)
            For Each spot In adj.RetouchSpots
                Dim cx = CInt(Math.Round(source.Width * Clamp(spot.XPercent, 0, 100) / 100.0F))
                Dim cy = CInt(Math.Round(source.Height * Clamp(spot.YPercent, 0, 100) / 100.0F))
                Dim radius = Math.Max(2, CInt(Math.Round(Math.Min(source.Width, source.Height) * Clamp(spot.RadiusPercent, 0.1F, 20) / 100.0F)))
                Dim sampleRadius = radius * 2

                Dim sr As Long = 0
                Dim sg As Long = 0
                Dim sb As Long = 0
                Dim sa As Long = 0
                Dim count As Integer = 0
                For yy As Integer = Math.Max(0, cy - sampleRadius) To Math.Min(source.Height - 1, cy + sampleRadius)
                    For xx As Integer = Math.Max(0, cx - sampleRadius) To Math.Min(source.Width - 1, cx + sampleRadius)
                        Dim d = Math.Sqrt((xx - cx) * (xx - cx) + (yy - cy) * (yy - cy))
                        If d >= radius * 1.25 AndAlso d <= sampleRadius Then
                            Dim c = source.GetPixel(xx, yy)
                            sr += c.Red : sg += c.Green : sb += c.Blue : sa += c.Alpha
                            count += 1
                        End If
                    Next
                Next
                If count = 0 Then Continue For

                Dim fill = New SKColor(CByte(sr \ count), CByte(sg \ count), CByte(sb \ count), CByte(sa \ count))
                For yy As Integer = Math.Max(0, cy - radius) To Math.Min(source.Height - 1, cy + radius)
                    For xx As Integer = Math.Max(0, cx - radius) To Math.Min(source.Width - 1, cx + radius)
                        Dim d = Math.Sqrt((xx - cx) * (xx - cx) + (yy - cy) * (yy - cy))
                        If d <= radius Then
                            Dim edge = Math.Min(1.0, Math.Max(0.0, (radius - d) / Math.Max(1.0, radius * 0.45)))
                            Dim original = result.GetPixel(xx, yy)
                            Dim r = ClampToByte(original.Red * (1.0 - edge) + fill.Red * edge)
                            Dim g = ClampToByte(original.Green * (1.0 - edge) + fill.Green * edge)
                            Dim b = ClampToByte(original.Blue * (1.0 - edge) + fill.Blue * edge)
                            result.SetPixel(xx, yy, New SKColor(r, g, b, original.Alpha))
                        End If
                    Next
                Next
            Next
            Return result
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
                Using paint = New SKPaint With {.FilterQuality = ToFilterQuality(adj.ResizeInterpolation), .IsAntialias = True}
                    canvas.DrawBitmap(source, New SKRect(0, 0, source.Width, source.Height), New SKRect(0, 0, targetWidth, targetHeight), paint)
                End Using
            End Using
            Return result
        End Function

        Private Shared Function ToFilterQuality(mode As ResizeInterpolationMode) As SKFilterQuality
            Select Case mode
                Case ResizeInterpolationMode.Nearest
                    Return SKFilterQuality.None
                Case ResizeInterpolationMode.Bilinear
                    Return SKFilterQuality.Low
                Case Else
                    Return SKFilterQuality.High
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

        Private Shared Function ApplyAnnotations(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If adj.Annotations Is Nothing OrElse adj.Annotations.Count = 0 Then Return source

            Dim result = CloneBitmap(source)

            ' Pinsel- und Radiergummi-Striche werden zuerst auf einer eigenen transparenten
            ' Ebene komponiert, damit der Radiergummi (SKBlendMode.Clear) nur vorherige Striche
            ' entfernt und nicht das Foto darunter.
            Dim paintLayer As SKBitmap = Nothing
            If adj.Annotations.Any(Function(a) a IsNot Nothing AndAlso a.IsVisible AndAlso IsPaintKind(a.Kind)) Then
                paintLayer = New SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using paintCanvas = New SKCanvas(paintLayer)
                    paintCanvas.Clear(SKColors.Transparent)
                    For Each annotation In adj.Annotations
                        If annotation Is Nothing OrElse Not annotation.IsVisible OrElse Not IsPaintKind(annotation.Kind) Then Continue For
                        Dim alphaFactor = Clamp(annotation.Opacity, 0, 100) / 100.0F
                    Dim stroke = ApplyAlpha(ParseColor(annotation.StrokeColor, SKColors.Black), alphaFactor)
                    ' Pinselgröße ist (wie RetouchRadius) ein Prozentsatz der kleineren Bildkante,
                        ' damit die Strichbreite unabhängig von der Bildauflösung gleich wirkt.
                        Dim strokeWidth = Math.Max(1.0F, Math.Min(source.Width, source.Height) * Clamp(annotation.StrokeWidth, 0.05F, 100) / 100.0F)
                        Dim isEraser = annotation.Kind.Trim().ToLowerInvariant() = "eraser"
                        DrawBrushStroke(paintCanvas, annotation.Text, source.Width, source.Height, stroke, strokeWidth, annotation.HardnessPercent, isEraser)
                    Next
                End Using
            End If

            Dim paintLayerDrawn = False
            Using canvas = New SKCanvas(result)
                For Each annotation In adj.Annotations
                    If annotation Is Nothing OrElse Not annotation.IsVisible Then Continue For
                    Dim kind = If(annotation.Kind, "Text").Trim().ToLowerInvariant()

                    If IsPaintKind(kind) Then
                        If Not paintLayerDrawn AndAlso paintLayer IsNot Nothing Then
                            canvas.DrawBitmap(paintLayer, 0, 0)
                            paintLayerDrawn = True
                        End If
                        Continue For
                    End If

                    Dim rect = ComputeAnnotationRect(source.Width, source.Height, kind, annotation)
                    Dim x = rect.Left
                    Dim y = rect.Top
                    Dim maxWidth = rect.Width
                    Dim maxHeight = rect.Height
                    Dim fontSize = Math.Max(8.0F, source.Height * Clamp(annotation.FontSizePercent, 0.5F, 50) / 100.0F)
                    Dim alphaFactor = Clamp(annotation.Opacity, 0, 100) / 100.0F
                    Dim fill = ApplyAlpha(ParseColor(annotation.FillColor, SKColors.White), alphaFactor)
                    Dim stroke = ApplyAlpha(ParseColor(annotation.StrokeColor, SKColors.Black), alphaFactor)
                    Dim strokeWidth = Math.Max(1.0F, annotation.StrokeWidth)

                    canvas.Save()
                    If Math.Abs(annotation.RotationDegrees) > 0.01F Then
                        canvas.RotateDegrees(annotation.RotationDegrees, rect.MidX, rect.MidY)
                    End If

                    If annotation.ShadowEnabled OrElse annotation.GlowEnabled Then
                        DrawAnnotationEffects(canvas, kind, annotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor, source.Width, source.Height)
                    End If
                    DrawAnnotationShape(canvas, kind, annotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor)
                    canvas.Restore()
                Next
            End Using
            paintLayer?.Dispose()
            Return result
        End Function

        ' Zeichnet ein einzelnes Objekt anhand seiner Art (Kind) - wird sowohl für das normale
        ' Zeichnen als auch (auf einer separaten Offscreen-Maske) für Schatten/Glow in
        ' DrawAnnotationEffects wiederverwendet, damit beide Pfade exakt dieselbe Silhouette ergeben.
        Private Shared Sub DrawAnnotationShape(canvas As SKCanvas, kind As String, annotation As ImageAnnotation, rect As SKRect, x As Single, y As Single, maxWidth As Single, fontSize As Single, fill As SKColor, stroke As SKColor, strokeWidth As Single, alphaFactor As Single)
            Select Case kind
                Case "rectangle", "rect", "selectionfill"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawShape(canvas, rect, fill, stroke, strokeWidth, False, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "ellipse", "circle"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawShape(canvas, rect, fill, stroke, strokeWidth, True, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "square"
                    DrawSquare(canvas, rect, fill, stroke, strokeWidth)
                Case "triangle"
                    DrawTriangle(canvas, rect, fill, stroke, strokeWidth)
                Case "cone"
                    DrawCone(canvas, rect, fill, stroke, strokeWidth)
                Case "pyramid"
                    DrawPyramid(canvas, rect, fill, stroke, strokeWidth)
                Case "trapezoid"
                    DrawTrapezoid(canvas, rect, fill, stroke, strokeWidth)
                Case "diamond"
                    DrawDiamond(canvas, rect, fill, stroke, strokeWidth)
                Case "spiral"
                    DrawSpiral(canvas, rect, stroke, strokeWidth)
                Case "droplet"
                    DrawDroplet(canvas, rect, fill, stroke, strokeWidth)
                Case "speechbubble", "speech-bubble", "bubble"
                    DrawSpeechBubble(canvas, rect, fill, stroke, strokeWidth)
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
                    DrawImageAnnotation(canvas, annotation.ImagePath, rect, annotation.Opacity, stroke, annotation.StrokeWidth)
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
            Dim glowBlurPx = objSize * Clamp(annotation.GlowBlur, 0, 100) / 100.0F * 0.8F
            Dim shadowBlurPx = objSize * Clamp(annotation.ShadowBlur, 0, 100) / 100.0F * 0.6F
            Dim offsetX = objSize * annotation.ShadowOffsetXPercent / 100.0F
            Dim offsetY = objSize * annotation.ShadowOffsetYPercent / 100.0F

            Dim maskLeft As Integer = 0
            Dim maskTop As Integer = 0
            Dim maskWidth = canvasWidth
            Dim maskHeight = canvasHeight
            If Math.Abs(annotation.RotationDegrees) <= 0.01F Then
                Dim pad = Math.Max(glowBlurPx, shadowBlurPx) * 3.0F + Math.Max(Math.Abs(offsetX), Math.Abs(offsetY)) + 4.0F
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
                    Using glowColorFilter = SKColorFilter.CreateBlendMode(glowColor, SKBlendMode.SrcIn)
                        Using glowMaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.1F, glowBlurPx))
                            Using paint = New SKPaint With {.ColorFilter = glowColorFilter, .MaskFilter = glowMaskFilter, .BlendMode = SKBlendMode.Plus}
                                canvas.DrawBitmap(mask, maskLeft, maskTop, paint)
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

                        Using shadowColorFilter = SKColorFilter.CreateBlendMode(shadowColor, SKBlendMode.SrcIn)
                            Using shadowMaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.1F, shadowBlurPx))
                                Using paint = New SKPaint With {.ColorFilter = shadowColorFilter, .MaskFilter = shadowMaskFilter}
                                    canvas.DrawBitmap(shadowSource, maskLeft + offsetX, maskTop + offsetY, paint)
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
            If strokeWidth > 0 Then
                Using strokePaint = New SKPaint With {
                    .Color = stroke,
                    .TextSize = fontSize,
                    .Typeface = GetTypeface(fontFamily),
                    .IsAntialias = True,
                    .Style = SKPaintStyle.Stroke,
                    .StrokeWidth = Math.Max(1.0F, strokeWidth)
                }
                    DrawWrappedText(canvas, text, x, y, maxWidth, fontSize, strokePaint)
                End Using
            End If

            Using fillPaint = New SKPaint With {
                .Color = fill,
                .TextSize = fontSize,
                .Typeface = GetTypeface(fontFamily),
                .IsAntialias = True,
                .Style = SKPaintStyle.Fill
            }
                Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
                If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                    fillPaint.Shader = CreateFillGradientShader(bounds, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
                End If
                DrawWrappedText(canvas, text, x, y, maxWidth, fontSize, fillPaint)
            End Using
        End Sub

        ''' opacity ist auf der 0-100-Skala (wie annotation.Opacity), NICHT der bereits normalisierten
        ''' 0-1 alphaFactor-Skala, die für die übrigen (bereits alpha-vorgemischten) Fill/Stroke-Farben
        ''' verwendet wird - siehe Aufrufstelle im Select Case (Kind "image").
        Private Shared Sub DrawImageAnnotation(canvas As SKCanvas, imagePath As String, rect As SKRect, opacity As Single, stroke As SKColor, strokeWidth As Single)
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then Return

            Using bitmap = SKBitmap.Decode(imagePath)
                If bitmap Is Nothing OrElse bitmap.Width <= 0 OrElse bitmap.Height <= 0 Then Return

                Dim fitRect = FitRectKeepingAspectRatio(rect, bitmap.Width, bitmap.Height)
                Using paint = New SKPaint With {
                    .IsAntialias = True,
                    .FilterQuality = SKFilterQuality.High,
                    .Color = New SKColor(255, 255, 255, CByte(Math.Max(0, Math.Min(255, 255 * Clamp(opacity, 0, 100) / 100.0F))))
                }
                    canvas.DrawBitmap(bitmap, SKRect.Create(0, 0, bitmap.Width, bitmap.Height), fitRect, paint)
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
                    Using shader = CreateFillGradientShader(rect, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
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

        Private Shared Sub DrawSquare(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single)
            Dim side = Math.Min(rect.Width, rect.Height)
            Dim x = rect.MidX - side / 2.0F
            Dim y = rect.MidY - side / 2.0F
            DrawShape(canvas, New SKRect(x, y, x + side, y + side), fill, stroke, strokeWidth, False)
        End Sub

        Private Shared Sub DrawTriangle(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top)
                path.LineTo(rect.Right, rect.Bottom)
                path.LineTo(rect.Left, rect.Bottom)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth)
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

        Private Shared Sub DrawTrapezoid(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single)
            Using path = New SKPath()
                Dim inset = rect.Width * 0.22F
                path.MoveTo(rect.Left + inset, rect.Top)
                path.LineTo(rect.Right - inset, rect.Top)
                path.LineTo(rect.Right, rect.Bottom)
                path.LineTo(rect.Left, rect.Bottom)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth)
            End Using
        End Sub

        Private Shared Sub DrawDiamond(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top)
                path.LineTo(rect.Right, rect.MidY)
                path.LineTo(rect.MidX, rect.Bottom)
                path.LineTo(rect.Left, rect.MidY)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth)
            End Using
        End Sub

        Private Shared Sub DrawDroplet(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top)
                path.CubicTo(rect.Right, rect.Top + rect.Height * 0.30F, rect.Right * 0.92F, rect.Bottom * 0.70F, rect.MidX, rect.Bottom)
                path.CubicTo(rect.Left * 0.08F, rect.Bottom * 0.70F, rect.Left, rect.Top + rect.Height * 0.30F, rect.MidX, rect.Top)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth)
            End Using
        End Sub

        Private Shared Sub DrawSpeechBubble(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single)
            Dim tailWidth = rect.Width * 0.18F
            Dim tailHeight = rect.Height * 0.18F
            Dim radius = Math.Min(rect.Width, rect.Height) * 0.18F
            Using path = New SKPath()
                path.MoveTo(rect.Left + radius, rect.Top)
                path.LineTo(rect.Right - radius, rect.Top)
                path.QuadTo(rect.Right, rect.Top, rect.Right, rect.Top + radius)
                path.LineTo(rect.Right, rect.Bottom - radius - tailHeight)
                path.QuadTo(rect.Right, rect.Bottom - tailHeight, rect.Right - radius, rect.Bottom - tailHeight)
                path.LineTo(rect.MidX + tailWidth * 0.25F, rect.Bottom - tailHeight)
                path.LineTo(rect.MidX, rect.Bottom)
                path.LineTo(rect.MidX - tailWidth * 0.35F, rect.Bottom - tailHeight)
                path.LineTo(rect.Left + radius, rect.Bottom - tailHeight)
                path.QuadTo(rect.Left, rect.Bottom - tailHeight, rect.Left, rect.Bottom - radius - tailHeight)
                path.LineTo(rect.Left, rect.Top + radius)
                path.QuadTo(rect.Left, rect.Top, rect.Left + radius, rect.Top)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth)
            End Using
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

        Private Shared Sub DrawClosedPath(canvas As SKCanvas, path As SKPath, fill As SKColor, stroke As SKColor, strokeWidth As Single)
            Using fillPaint = New SKPaint With {.Color = fill, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                If fill.Alpha > 0 Then canvas.DrawPath(path, fillPaint)
            End Using
            Using strokePaint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = strokeWidth, .IsAntialias = True, .StrokeCap = SKStrokeCap.Round, .StrokeJoin = SKStrokeJoin.Round}
                canvas.DrawPath(path, strokePaint)
            End Using
        End Sub

        Private Shared Sub DrawLine(canvas As SKCanvas, rect As SKRect, stroke As SKColor, strokeWidth As Single, arrow As Boolean)
            Using paint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = strokeWidth, .StrokeCap = SKStrokeCap.Round, .IsAntialias = True}
                canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, paint)
                If arrow Then
                    Dim angle = Math.Atan2(rect.Bottom - rect.Top, rect.Right - rect.Left)
                    Dim head = Math.Max(10.0F, strokeWidth * 4.0F)
                    Dim a1 = angle - Math.PI * 0.78
                    Dim a2 = angle + Math.PI * 0.78
                    canvas.DrawLine(rect.Right, rect.Bottom, CSng(rect.Right + Math.Cos(a1) * head), CSng(rect.Bottom + Math.Sin(a1) * head), paint)
                    canvas.DrawLine(rect.Right, rect.Bottom, CSng(rect.Right + Math.Cos(a2) * head), CSng(rect.Bottom + Math.Sin(a2) * head), paint)
                End If
            End Using
        End Sub

        ''' Mehrere Striche derselben Ebene (siehe EditorViewModel.AddBrushStroke - Striche werden
        ''' gesammelt, statt für jeden eine eigene Ebene anzulegen) sind im Punktestring per ";"
        ''' getrennt und werden hier als eigenständige Teilpfade (je ein eigenes MoveTo) gezeichnet -
        ''' eine einzige durchgehende Linie würde sie sonst fälschlich miteinander verbinden.
        Private Shared Sub DrawBrushStroke(canvas As SKCanvas, pointsText As String, width As Integer, height As Integer, stroke As SKColor, strokeWidth As Single, hardnessPercent As Single, isEraser As Boolean)
            If String.IsNullOrWhiteSpace(pointsText) Then Return

            Dim resolvedStrokeWidth = Math.Max(1.0F, strokeWidth)
            Dim hardness = Clamp(hardnessPercent, 0, 100) / 100.0F
            Dim blurSigma = resolvedStrokeWidth * (1.0F - hardness) * 0.5F

            Using paint = New SKPaint With {
                .Color = stroke,
                .Style = SKPaintStyle.Stroke,
                .StrokeWidth = resolvedStrokeWidth,
                .StrokeCap = SKStrokeCap.Round,
                .StrokeJoin = SKStrokeJoin.Round,
                .IsAntialias = True
            }
                If isEraser Then paint.BlendMode = SKBlendMode.Clear
                If blurSigma > 0.05F Then paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSigma)

                For Each strokeText In pointsText.Split(";"c)
                    Dim points As New List(Of SKPoint)()
                    For Each token In strokeText.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
                        Dim parts = token.Split(","c)
                        If parts.Length <> 2 Then Continue For
                        Dim x As Single
                        Dim y As Single
                        If Single.TryParse(parts(0), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, x) AndAlso
                           Single.TryParse(parts(1), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, y) Then
                            points.Add(New SKPoint(width * Clamp(x, 0, 100) / 100.0F, height * Clamp(y, 0, 100) / 100.0F))
                        End If
                    Next
                    If points.Count < 2 Then Continue For

                    Using path = New SKPath()
                        path.MoveTo(points(0))
                        For i As Integer = 1 To points.Count - 1
                            path.LineTo(points(i))
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
            Using paint = New SKPaint With {.TextSize = fontSize, .Typeface = GetTypeface(fontFamily), .IsAntialias = True}
                Dim bounds As SKRect
                paint.MeasureText(text, bounds)
                Dim x = rect.MidX - bounds.MidX
                Dim y = rect.MidY - bounds.MidY
                If strokeWidth > 0 Then
                    paint.Style = SKPaintStyle.Stroke
                    paint.StrokeWidth = strokeWidth
                    paint.Color = stroke
                    canvas.DrawText(text, x, y, paint)
                End If
                paint.Style = SKPaintStyle.Fill
                paint.Color = fill
                canvas.DrawText(text, x, y, paint)
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

        Private Shared Sub DrawWrappedText(canvas As SKCanvas, text As String, x As Single, y As Single, maxWidth As Single, fontSize As Single, paint As SKPaint)
            If String.IsNullOrEmpty(text) Then Return
            Dim lineHeight = fontSize * 1.22F
            Dim baseline = y + fontSize

            For Each paragraph In text.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(ControlChars.Lf)
                Dim current = ""
                For Each word In paragraph.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
                    Dim candidate = If(String.IsNullOrEmpty(current), word, current & " " & word)
                    If current.Length > 0 AndAlso paint.MeasureText(candidate) > maxWidth Then
                        canvas.DrawText(current, x, baseline, paint)
                        baseline += lineHeight
                        current = word
                    Else
                        current = candidate
                    End If
                Next

                If current.Length > 0 Then
                    canvas.DrawText(current, x, baseline, paint)
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
            Dim result = New SKBitmap(source.Width, source.Height)
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
                    Using paint = New SKPaint With {.FilterQuality = SKFilterQuality.High, .IsAntialias = True}
                        canvas.DrawBitmap(source, -source.Width / 2.0F, -source.Height / 2.0F, paint)
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
                Using paint = New SKPaint With {.FilterQuality = SKFilterQuality.High, .IsAntialias = True}
                    canvas.DrawBitmap(source, -source.Width / 2.0F, -source.Height / 2.0F, paint)
                End Using
            End Using
            Return result
        End Function

        Public Shared Function SaveImage(sourcePath As String, targetPath As String, adj As ImageAdjustments, quality As Integer) As Boolean
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
                    Return True
                End Using
            Catch ex As Exception
                Return False
            End Try
        End Function

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

        ' Kantenerhaltender Medianfilter - echte Rauschunterdrückung statt gleichmäßigem Weichzeichnen.
        Private Shared Function ApplyMedianBlur(source As SKBitmap, amount As Single) As SKBitmap
            Dim clamped = Clamp(amount, 0, 1)
            Dim radius = Math.Max(1, CInt(Math.Round(1 + clamped * 2)))
            Dim w = source.Width
            Dim h = source.Height
            Dim result = New SKBitmap(w, h, source.ColorType, source.AlphaType)
            Dim rWindow As New List(Of Byte)()
            Dim gWindow As New List(Of Byte)()
            Dim bWindow As New List(Of Byte)()

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

        Private Shared Function ApplyClarity(source As SKBitmap, amount As Single) As SKBitmap
            Dim sigma = 2.0F + Math.Abs(amount) * 5.0F
            Using blurred = ApplyNoiseReduction(source, sigma / 8.0F)
                Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
                For y As Integer = 0 To source.Height - 1
                    For x As Integer = 0 To source.Width - 1
                        Dim c = source.GetPixel(x, y)
                        Dim b = blurred.GetPixel(x, y)
                        ' Ohne die expliziten CInt-Weitungen würde "c.Red - b.Red" als Byte-Subtraktion
                        ' ausgewertet - da Byte vorzeichenlos ist, wirft VB im Checked-Kontext eine
                        ' OverflowException, sobald der weichgezeichnete Pixel heller ist als das
                        ' Original (b.Red > c.Red), was in praktisch jedem Foto ständig vorkommt.
                        Dim r = ClampToByte(CInt(c.Red) + (CInt(c.Red) - CInt(b.Red)) * amount * 1.6F)
                        Dim g = ClampToByte(CInt(c.Green) + (CInt(c.Green) - CInt(b.Green)) * amount * 1.6F)
                        Dim bl = ClampToByte(CInt(c.Blue) + (CInt(c.Blue) - CInt(b.Blue)) * amount * 1.6F)
                        result.SetPixel(x, y, New SKColor(r, g, bl, c.Alpha))
                    Next
                Next
                Return result
            End Using
        End Function

        Private Shared Function ApplyStructure(source As SKBitmap, amount As Single) As SKBitmap
            Return ApplyClarity(source, Clamp(amount, -1, 1) * 0.65F)
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
            If strength > 0 Then Return ApplyMedianBlur(source, strength * 0.75F)

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
            Using filter = SKColorFilter.CreateTable(Nothing, finalR, finalG, finalB)
                Using paint = New SKPaint With {.ColorFilter = filter}
                    Using canvas = New SKCanvas(result)
                        canvas.DrawBitmap(source, 0, 0, paint)
                    End Using
                End Using
            End Using

            If Not ImageAdjustments.IsIdentityCurve(adj.CurveLuminancePoints) Then
                Dim lumLut = BuildCurveLut(adj.CurveLuminancePoints)
                Dim withLuminance = New SKBitmap(result.Width, result.Height, result.ColorType, result.AlphaType)
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

        Private Shared Function ApplyHsl(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If Not adj.HasHslChanges() Then Return source

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            For y As Integer = 0 To source.Height - 1
                For x As Integer = 0 To source.Width - 1
                    Dim c = source.GetPixel(x, y)
                    Dim h As Double
                    Dim s As Double
                    Dim l As Double
                    RgbToHsl(c.Red, c.Green, c.Blue, h, s, l)
                    Dim hueShift As Single = 0
                    Dim satShift As Single = 0
                    GetHslBandAdjustments(h, adj, hueShift, satShift)
                    h = (h + hueShift + 360.0) Mod 360.0
                    s = Math.Max(0.0, Math.Min(1.0, s * (1.0 + satShift / 100.0)))
                    Dim nc = HslToRgb(h, s, l, c.Alpha)
                    result.SetPixel(x, y, nc)
                Next
            Next
            Return result
        End Function

        Private Shared Sub GetHslBandAdjustments(hue As Double, adj As ImageAdjustments, ByRef hueShift As Single, ByRef satShift As Single)
            Select Case hue
                Case < 15, >= 345
                    hueShift = adj.RedHue : satShift = adj.RedSaturation
                Case < 45
                    hueShift = adj.OrangeHue : satShift = adj.OrangeSaturation
                Case < 75
                    hueShift = adj.YellowHue : satShift = adj.YellowSaturation
                Case < 165
                    hueShift = adj.GreenHue : satShift = adj.GreenSaturation
                Case < 195
                    hueShift = adj.AquaHue : satShift = adj.AquaSaturation
                Case < 255
                    hueShift = adj.BlueHue : satShift = adj.BlueSaturation
                Case < 285
                    hueShift = adj.PurpleHue : satShift = adj.PurpleSaturation
                Case Else
                    hueShift = adj.MagentaHue : satShift = adj.MagentaSaturation
            End Select
        End Sub

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
                    matrix = New Single() {
                        1.0F, 0.025F, 0.025F, 0, 6,
                        0.025F, 1.0F, 0.025F, 0, 6,
                        0.025F, 0.025F, 1.0F, 0, 6,
                        0, 0, 0, 1, 0
                    }
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

        Private Shared Function ApplyVignette(source As SKBitmap, amount As Single, transition As Single, roundness As Single, feather As Single, centerXPercent As Single, centerYPercent As Single) As SKBitmap
            Dim strength = Clamp(Math.Abs(amount), 0, 1)
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
                    Dim noise = (random.NextDouble() * 2.0 - 1.0) * amplitude
                    result.SetPixel(x, y, New SKColor(ClampToByte(c.Red + noise),
                                                      ClampToByte(c.Green + noise),
                                                      ClampToByte(c.Blue + noise),
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
                Using paint = New SKPaint With {.Color = color, .Style = SKPaintStyle.Stroke, .StrokeWidth = thickness, .IsAntialias = True}
                    If normalized = "gestrichelt" Then
                        paint.PathEffect = SKPathEffect.CreateDash(New Single() {thickness * 1.4F, thickness * 0.9F}, 0)
                    End If
                    Dim inset = thickness / 2.0F
                    Dim rect = New SKRect(inset, inset, source.Width - inset, source.Height - inset)
                    Dim radius = Math.Min(source.Width, source.Height) * Clamp(cornerRadiusPercent, 0, 1) * 0.25F
                    If normalized = "gezackt" Then
                        Using path = BuildZigZagBorderPath(rect, Math.Max(4, thickness))
                            canvas.DrawPath(path, paint)
                        End Using
                    ElseIf radius > 0 Then
                        canvas.DrawRoundRect(rect, radius, radius, paint)
                    Else
                        canvas.DrawRect(rect, paint)
                    End If
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

            Dim colorFilter = SKColorFilter.CreateTable(Nothing, lut, lut, lut)
            Dim paint = New SKPaint With {.ColorFilter = colorFilter}
            Dim result = New SKBitmap(source.Width, source.Height)
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
