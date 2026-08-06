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

    ''' <summary>Objekte auf der Editor-Buehne und der freie Pfad: die Eigenschaften des markierten
    ''' Objekts (Lage, Groesse, Fuellung, Text, Schatten, Schein, Textpfad) und der Pfad-Entwurf
    ''' samt Nachziehen seiner Punkte.
    '''
    ''' Zweite Scheibe der Dateiaufteilung (2026-08-04), Regeln wie in
    ''' <c>ViewModels/EditorViewModelMask.vb</c>: geschnitten entlang des ZUSTANDS, reiner
    ''' TEXTumzug, Kontrolle ueber die Zaehlung der Bloecke vor und nach dem Schnitt. Was die
    ''' Objekte inhaltlich ausmacht, steht in <c>Audits/EDITOR_OBJEKTE.md</c>.</summary>
    Partial Public Class EditorViewModel

        ''' Sichtbarkeit der Checkbox: nur wo Verzerren real droht - Bild-Objekte und
        ''' Wasserzeichen mit Bilddatei (QR bleibt hart 1:1, Formen/Text duerfen frei).
        Public ReadOnly Property ShowAnnotationAspectLock As Boolean
            Get
                ' Bei einer MEHRFACHauswahl beschriebe dieser Bereich nur den Anker - er bleibt weg.
                If HasMultiAnnotationSelection Then Return False
                Return String.Equals(EffectiveAnnotationKind, "Image", StringComparison.OrdinalIgnoreCase) OrElse
                       IsWatermarkImageSource
            End Get
        End Property

        Public Property AnnotationFlipVertical As Boolean
            Get
                Return _annotationFlipV
            End Get
            Set(value As Boolean)
                If HasMultiAnnotationSelection AndAlso Not _isLoadingAnnotation Then
                    Me.RaiseAndSetIfChanged(_annotationFlipV, value)
                    FlipSelectionBox(horizontal:=False)
                    Return
                End If
                Me.RaiseAndSetIfChanged(_annotationFlipV, value)
                SyncSelectedAnnotation()
                RaiseEnvelopeChanged()
            End Set
        End Property

        Public Property AnnotationIsVisible As Boolean
            Get
                Return _annotationIsVisible
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationIsVisible, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        Private Const AnnotationMinVisiblePercent As Double = 1.0

        Private Shared Function ClampAnnotationPositionPercent(value As Double, sizePercent As Double) As Double
            Dim size = Math.Max(0.0, sizePercent)
            Dim minValue = GetAnnotationPositionMinimumPercent(size)
            Dim maxValue = 100.0 - AnnotationMinVisiblePercent
            Return Math.Max(minValue, Math.Min(maxValue, value))
        End Function

        Private Shared Function GetAnnotationPositionMinimumPercent(sizePercent As Double) As Double
            Return -Math.Max(0.0, Math.Max(0.0, sizePercent) - AnnotationMinVisiblePercent)
        End Function

        ''' Obergrenze der Objektbreite in Prozent der Bildbreite: mindestens das ganze Bild (100%),
        ''' bei kleineren Bildern bis hinauf zur MaxAnnotationEdgePixels-Kante.
        Private Function MaxAnnotationWidthPercentValue() As Double
            Dim width = DisplayImageWidthPixels
            If width <= 0 Then Return 100.0
            Return Math.Max(100.0, MaxAnnotationEdgePixels / width * 100.0)
        End Function

        Private Function MaxAnnotationHeightPercentValue() As Double
            Dim height = DisplayImageHeightPixels
            If height <= 0 Then Return 100.0
            Return Math.Max(100.0, MaxAnnotationEdgePixels / height * 100.0)
        End Function

        Public Property AnnotationXPercent As Double
            Get
                If HasMultiAnnotationSelection Then Return SelectionBoxComponent(0)
                Return _annotationXPercent
            End Get
            Set(value As Double)
                If HasMultiAnnotationSelection AndAlso Not _isLoadingAnnotation Then
                    SetSelectionBoxComponent(0, value)
                    Return
                End If
                Dim normalized = If(ShowWatermarkAnchorControls,
                                    ClampAnnotationOffsetPercent(value),
                                    ClampAnnotationPositionPercent(value, _annotationWidthPercent))
                Me.RaiseAndSetIfChanged(_annotationXPercent, normalized)
                Me.RaisePropertyChanged(NameOf(AnnotationXPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationXSliderValue))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationYPercent As Double
            Get
                If HasMultiAnnotationSelection Then Return SelectionBoxComponent(1)
                Return _annotationYPercent
            End Get
            Set(value As Double)
                If HasMultiAnnotationSelection AndAlso Not _isLoadingAnnotation Then
                    SetSelectionBoxComponent(1, value)
                    Return
                End If
                Dim normalized = If(ShowWatermarkAnchorControls,
                                    ClampAnnotationOffsetPercent(value),
                                    ClampAnnotationPositionPercent(value, _annotationHeightPercent))
                Me.RaiseAndSetIfChanged(_annotationYPercent, normalized)
                Me.RaisePropertyChanged(NameOf(AnnotationYPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationYSliderValue))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationXSliderValue As Double
            Get
                Return CDbl(AnnotationXPixels)
            End Get
            Set(value As Double)
                AnnotationXPixels = CInt(Math.Round(value))
            End Set
        End Property

        Public Property AnnotationYSliderValue As Double
            Get
                Return CDbl(AnnotationYPixels)
            End Get
            Set(value As Double)
                AnnotationYPixels = CInt(Math.Round(value))
            End Set
        End Property

        Public Property AnnotationWidthPercent As Double
            Get
                If HasMultiAnnotationSelection Then Return SelectionBoxComponent(2)
                Return _annotationWidthPercent
            End Get
            Set(value As Double)
                If HasMultiAnnotationSelection AndAlso Not _isLoadingAnnotation Then
                    SetSelectionBoxComponent(2, value)
                    Return
                End If
                If EffectiveAnnotationKind = "QR" Then
                    Dim displaySize = GetAnnotationDisplayPixelSize()
                    If displaySize.Width > 0 AndAlso displaySize.Height > 0 Then
                        Dim sizePixels = Math.Max(1.0, displaySize.Width * Math.Max(5, Math.Min(MaxAnnotationWidthPercentValue(), value)) / 100.0)
                        _annotationWidthPercent = Math.Max(5, Math.Min(MaxAnnotationWidthPercentValue(), sizePixels / displaySize.Width * 100.0))
                        _annotationHeightPercent = Math.Max(4, Math.Min(MaxAnnotationHeightPercentValue(), sizePixels / displaySize.Height * 100.0))
                        RaiseAnnotationSizeChanged()
                        SyncSelectedAnnotation()
                        Return
                    End If
                End If
                Dim isTextual = IsTextualAnnotationKind(EffectiveAnnotationKind) AndAlso Not IsWatermarkImageSource
                Dim minWidth = If(isTextual, MinTextAnnotationWidthPercent, 5.0)
                Me.RaiseAndSetIfChanged(_annotationWidthPercent, Math.Max(minWidth, Math.Min(MaxAnnotationWidthPercentValue(), value)))
                Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationWidthSliderMinimum))
                Me.RaisePropertyChanged(NameOf(AnnotationWidthSliderMaximum))
                RaiseAnnotationPositionControlProperties()
                ' Zieht die Hoehe mit, wenn das Seitenverhaeltnis gesperrt ist - dabei laeuft
                ' SyncSelectedAnnotation bereits ueber den Hoehen-Setter.
                If Not TryCoupleAnnotationAspect(vonBreite:=True) Then SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationHeightPercent As Double
            Get
                If HasMultiAnnotationSelection Then Return SelectionBoxComponent(3)
                Return _annotationHeightPercent
            End Get
            Set(value As Double)
                If HasMultiAnnotationSelection AndAlso Not _isLoadingAnnotation Then
                    SetSelectionBoxComponent(3, value)
                    Return
                End If
                If EffectiveAnnotationKind = "QR" Then
                    Dim displaySize = GetAnnotationDisplayPixelSize()
                    If displaySize.Width > 0 AndAlso displaySize.Height > 0 Then
                        Dim sizePixels = Math.Max(1.0, displaySize.Height * Math.Max(4, Math.Min(MaxAnnotationHeightPercentValue(), value)) / 100.0)
                        _annotationWidthPercent = Math.Max(5, Math.Min(MaxAnnotationWidthPercentValue(), sizePixels / displaySize.Width * 100.0))
                        _annotationHeightPercent = Math.Max(4, Math.Min(MaxAnnotationHeightPercentValue(), sizePixels / displaySize.Height * 100.0))
                        RaiseAnnotationSizeChanged()
                        SyncSelectedAnnotation()
                        Return
                    End If
                End If
                Dim isTextual = IsTextualAnnotationKind(EffectiveAnnotationKind) AndAlso Not IsWatermarkImageSource
                Dim minHeight = If(isTextual, MinTextAnnotationHeightPercent, 4.0)
                Me.RaiseAndSetIfChanged(_annotationHeightPercent, Math.Max(minHeight, Math.Min(MaxAnnotationHeightPercentValue(), value)))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightSliderMinimum))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightSliderMaximum))
                RaiseAnnotationPositionControlProperties()
                If Not TryCoupleAnnotationAspect(vonBreite:=False) Then SyncSelectedAnnotation()
            End Set
        End Property

        ' "Solid", "LinearGradient" oder "RadialGradient" - nur für Rechteck/Ellipse-Objekte relevant,
        ' siehe ImageAnnotation.FillKind. Dient sowohl zum Bearbeiten des ausgewählten Objekts als auch
        ' (wie AnnotationFillColor) als "aktueller Stift" für neu erzeugte Objekte (FillSelection).
        Public Property AnnotationFillKind As String
            Get
                Return _annotationFillKind
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationFillKind, If(String.IsNullOrWhiteSpace(value), "Solid", value))
                Me.RaisePropertyChanged(NameOf(ShowGradientFillControls))
                Me.RaisePropertyChanged(NameOf(ShowLinearGradientAngleControl))
                Me.RaisePropertyChanged(NameOf(ShowRadialGradientControl))
                Me.RaisePropertyChanged(NameOf(SelectionFillPreviewBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        ''' <summary>Pfadform des Text-Objekts: "" (gerade), "Arc", "Circle", "Wave".</summary>
        Public Property AnnotationTextPathKind As String
            Get
                Return _annotationTextPathKind
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationTextPathKind, If(value, ""))
                Me.RaisePropertyChanged(NameOf(ShowTextPathControls))
                Me.RaisePropertyChanged(NameOf(IsTextPathNone))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationTextPathBend As Double
            Get
                Return _annotationTextPathBend
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationTextPathBend, Math.Max(-100, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationTextPathStartOffset As Double
            Get
                Return _annotationTextPathStartOffset
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationTextPathStartOffset, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        ''' <summary>Zeichenabstand in Prozent der Schriftgroesse. Prozent statt Pixel, damit der
        ''' Abstand beim Skalieren des Objekts mitwaechst.</summary>
        Public Property AnnotationLetterSpacingPercent As Double
            Get
                Return _annotationLetterSpacingPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationLetterSpacingPercent, Math.Max(-20, Math.Min(200, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        ''' <summary>Fett/Kursiv des Text-Objekts. Wirkt nur, wenn die gewaehlte Schriftfamilie
        ''' einen solchen Schnitt mitbringt - Skia erzeugt keinen synthetischen.</summary>
        Public Property AnnotationBold As Boolean
            Get
                Return _annotationBold
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationBold, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationItalic As Boolean
            Get
                Return _annotationItalic
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationItalic, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        ''' <summary>Die Pfad-Zeile gilt fuer alles, was Text zeichnet - also auch fuer das
        ''' Wasserzeichen. Frueher war sie dort ausgeblendet, weil die Zeichenstelle die
        ''' Pfad-Parameter nicht durchreichte; der Renderer selbst konnte es immer schon
        '''.
        ''' Ein Wasserzeichen mit BILD hat keinen Text und damit auch keinen Pfad.</summary>
        Public ReadOnly Property ShowTextPathRow As Boolean
            Get
                ' Bei einer MEHRFACHauswahl beschriebe dieser Bereich nur den Anker - er bleibt weg.
                If HasMultiAnnotationSelection Then Return False
                If EffectiveAnnotationKind = "Text" Then Return True
                Return EffectiveAnnotationKind = "Watermark" AndAlso String.IsNullOrWhiteSpace(SelectedAnnotationImagePath)
            End Get
        End Property

        ''' <summary>Kruemmungs-/Startregler nur, wenn ueberhaupt eine Pfadform gewaehlt ist.</summary>
        Public ReadOnly Property ShowTextPathControls As Boolean
            Get
                ' Bei einer MEHRFACHauswahl beschriebe dieser Bereich nur den Anker - er bleibt weg.
                If HasMultiAnnotationSelection Then Return False
                Return ShowTextPathRow AndAlso Not String.IsNullOrWhiteSpace(_annotationTextPathKind)
            End Get
        End Property

        Public ReadOnly Property IsTextPathNone As Boolean
            Get
                Return String.IsNullOrWhiteSpace(_annotationTextPathKind)
            End Get
        End Property

        ''' "None" kommt vom "Kein"-Knopf: ein leeres CommandParameter laesst sich in XAML nicht
        ''' ausdruecken, ohne den XAML-Compiler zu brechen (AVLN2000 bei ConverterParameter=).
        ''' <summary>True fuer beide Kreisformen. Bewusst StartsWith - "CircleInverted" ist
        ''' geometrisch derselbe Kreis, und ein Equals-Vergleich hat den invertierten Modus schon
        ''' einmal stillschweigend anders behandelt.</summary>
        Friend Shared Function IsCircleTextPath(kind As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(kind) AndAlso
                   kind.StartsWith("Circle", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Behaelt ein Text auf FREIEM Pfad seine Box? Ja, sobald die Grundlinie gezeichnet
        ''' ist: die Punkte liegen in Prozent der Box, und jede Neuvermessung auf den geraden
        ''' Textkasten streckte die gezeichnete Kurve auf den neuen Kasten - die Grundlinie verformte
        ''' sich mit jedem Tastendruck. Die Box legt stattdessen RefitPathBoundsToPoints um die
        ''' Punkte, wie beim Pfad-Objekt; ein Zug am Auswahlrahmen skaliert Grundlinie und Box
        ''' gemeinsam.</summary>
        Private Function FreeTextPathKeepsBox() As Boolean
            If Not String.Equals(_annotationTextPathKind, "Free", StringComparison.OrdinalIgnoreCase) Then Return False
            Dim a = CurrentObject()
            Return a IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(a.PathPoints)
        End Function

        Public Sub SetAnnotationTextPathKind(kind As String)
            AnnotationTextPathKind = If(String.Equals(kind, "None", StringComparison.OrdinalIgnoreCase), "", If(kind, ""))
            ' Ein Kreis braucht eine quadratische Box, sonst steht der Selektionsrahmen weit um den
            ' Text herum: der Radius ist min(Breite, Hoehe), eine breite Textbox laesst also den
            ' groessten Teil des Rahmens leer (mit Bild).
            ' Die Alternative - den Kreis ueber das Rechteck strecken - ergaebe bei breiten Objekten
            ' eine flache Ellipse und damit faktisch einen Bogen; ausprobiert und verworfen.
            ' Box sofort an den Kreis anpassen. MakeAnnotationBoxSquare reicht dafuer NICHT:
            ' bei Textobjekten wird die Box laufend aus dem gemessenen Textkasten neu berechnet und
            ' das Quadrat sofort wieder ueberschrieben - deshalb FitBoxToCircleTextPath, das an
            ' genau diesen Stellen mitlaeuft.
            If IsCircleTextPath(AnnotationTextPathKind) Then
                ' Beim Umschalten auf Kreis steht in der Box noch der Textkasten - er IST hier der
                ' Vorzustand, gegen den die Mittelpunkt-Korrektur rechnen muss.
                FitBoxToCircleTextPath(_annotationWidthPercent, _annotationHeightPercent)
                Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
                SyncSelectedAnnotation()
            End If
            ' FREIER PFAD: die Grundlinie gibt es noch nicht - also gleich danach fragen, statt den
            ' Nutzer raten zu lassen. Steht schon eine, bleibt sie und laesst sich mit den Griffen
            ' nachziehen.
            If String.Equals(AnnotationTextPathKind, "Free", StringComparison.OrdinalIgnoreCase) Then
                Dim target = CurrentObject()
                If target IsNot Nothing AndAlso String.IsNullOrWhiteSpace(target.PathPoints) Then
                    BeginPathDraftFor(target)
                End If
            End If
        End Sub

        ''' <summary>Macht die Objektbox quadratisch (kleinere Seite gewinnt) und behaelt dabei den
        ''' Mittelpunkt, damit der Text nicht wegspringt.</summary>
        Private Sub MakeAnnotationBoxSquare()
            Dim width = AnnotationWidthPixels
            Dim height = AnnotationHeightPixels
            If width <= 0 OrElse height <= 0 OrElse width = height Then Return

            Dim seite = Math.Min(width, height)
            Dim centerX = AnnotationXPixels + width \ 2
            Dim centerY = AnnotationYPixels + height \ 2

            AnnotationWidthPixels = seite
            AnnotationHeightPixels = seite
            AnnotationXPixels = Math.Max(0, centerX - seite \ 2)
            AnnotationYPixels = Math.Max(0, centerY - seite \ 2)
        End Sub

        Public Property AnnotationFillColor2 As String
            Get
                Return _annotationFillColor2
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationFillColor2, NormalizeAvaloniaColor(value, _annotationFillColor2))
                Me.RaisePropertyChanged(NameOf(AnnotationFillColor2Value))
                Me.RaisePropertyChanged(NameOf(AnnotationFillColor2Brush))
                Me.RaisePropertyChanged(NameOf(SelectionFillPreviewBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationFillColor2Value As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_annotationFillColor2, Avalonia.Media.Colors.White)
            End Get
            Set(value As Avalonia.Media.Color)
                AnnotationFillColor2 = value.ToString()
            End Set
        End Property

        Public Property AnnotationGradientAngleDegrees As Double
            Get
                Return _annotationGradientAngle
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationGradientAngle, Math.Max(0, Math.Min(360, value)))
                Me.RaisePropertyChanged(NameOf(SelectionFillPreviewBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGradientInverted As Boolean
            Get
                Return _annotationGradientInverted
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationGradientInverted, value)
                Me.RaisePropertyChanged(NameOf(SelectionFillPreviewBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowEnabled As Boolean
            Get
                Return _annotationShadowEnabled
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationShadowEnabled, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowOffsetX As Double
            Get
                Return _annotationShadowOffsetX
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowOffsetX, Math.Max(-100, Math.Min(100, value)))
                If _isLoadingAnnotation Then
                    _annotationShadowLightAngle = ComputeShadowLightAngle(_annotationShadowOffsetX, _annotationShadowOffsetY)
                    Me.RaisePropertyChanged(NameOf(AnnotationShadowLightAngle))
                End If
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowOffsetY As Double
            Get
                Return _annotationShadowOffsetY
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowOffsetY, Math.Max(-100, Math.Min(100, value)))
                If _isLoadingAnnotation Then
                    _annotationShadowLightAngle = ComputeShadowLightAngle(_annotationShadowOffsetX, _annotationShadowOffsetY)
                    Me.RaisePropertyChanged(NameOf(AnnotationShadowLightAngle))
                End If
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowLightAngle As Double
            Get
                Return _annotationShadowLightAngle
            End Get
            Set(value As Double)
                Dim normalized = NormalizeDegrees(value)
                If Math.Abs(normalized - _annotationShadowLightAngle) < 0.0001 Then Return
                _annotationShadowLightAngle = normalized
                Dim distance = Math.Sqrt(_annotationShadowOffsetX * _annotationShadowOffsetX + _annotationShadowOffsetY * _annotationShadowOffsetY)
                If distance < 1 Then distance = 6
                Dim shadowAngle = (_annotationShadowLightAngle + 180.0) * Math.PI / 180.0
                _annotationShadowOffsetX = Math.Max(-100, Math.Min(100, Math.Cos(shadowAngle) * distance))
                _annotationShadowOffsetY = Math.Max(-100, Math.Min(100, Math.Sin(shadowAngle) * distance))
                Me.RaisePropertyChanged(NameOf(AnnotationShadowOffsetX))
                Me.RaisePropertyChanged(NameOf(AnnotationShadowOffsetY))
                Me.RaisePropertyChanged(NameOf(AnnotationShadowLightAngle))
                SyncSelectedAnnotation()
            End Set
        End Property

        Private Shared Function ComputeShadowLightAngle(offsetX As Double, offsetY As Double) As Double
            Dim shadowAngle = Math.Atan2(offsetY, offsetX) * 180.0 / Math.PI
            Return NormalizeDegrees(shadowAngle + 180.0)
        End Function

        Private Shared Function NormalizeDegrees(value As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return 0
            Return (value Mod 360.0 + 360.0) Mod 360.0
        End Function

        Public Property AnnotationShadowBlur As Double
            Get
                Return _annotationShadowBlur
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowBlur, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowStrength As Double
            Get
                Return _annotationShadowStrength
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowStrength, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowColor As String
            Get
                Return _annotationShadowColor
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationShadowColor, NormalizeAvaloniaColor(value, _annotationShadowColor))
                Me.RaisePropertyChanged(NameOf(AnnotationShadowColorValue))
                Me.RaisePropertyChanged(NameOf(AnnotationShadowColorBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_annotationShadowColor, Avalonia.Media.Color.FromArgb(128, 0, 0, 0))
            End Get
            Set(value As Avalonia.Media.Color)
                AnnotationShadowColor = value.ToString()
            End Set
        End Property

        Public Property AnnotationShadowRounded As Boolean
            Get
                Return _annotationShadowRounded
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationShadowRounded, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowCornerRadius As Double
            Get
                Return _annotationShadowCornerRadius
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowCornerRadius, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowSize As Double
            Get
                Return _annotationShadowSize
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowSize, Math.Max(25, Math.Min(300, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGlowEnabled As Boolean
            Get
                Return _annotationGlowEnabled
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationGlowEnabled, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGlowBlur As Double
            Get
                Return _annotationGlowBlur
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationGlowBlur, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGlowStrength As Double
            Get
                Return _annotationGlowStrength
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationGlowStrength, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGlowColor As String
            Get
                Return _annotationGlowColor
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationGlowColor, NormalizeAvaloniaColor(value, _annotationGlowColor))
                Me.RaisePropertyChanged(NameOf(AnnotationGlowColorValue))
                Me.RaisePropertyChanged(NameOf(AnnotationGlowColorBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGlowColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_annotationGlowColor, Avalonia.Media.Colors.Yellow)
            End Get
            Set(value As Avalonia.Media.Color)
                AnnotationGlowColor = value.ToString()
            End Set
        End Property
        ' ── Freier Pfad: setzen und nachziehen ──────────────────────────────────
        '
        ' Der ENTWURF laeuft in ANZEIGE-Prozent, weil dort auch der Zeiger liegt. Erst beim
        ' Abschliessen wird er auf das umschliessende Rechteck bezogen und in OBJEKT-Prozent
        ' abgelegt - ab dann macht der Pfad Verschieben, Skalieren und Drehen ohne Zutun mit.
        '
        ' Ein FERTIGER Pfad wird andersherum gelesen: seine Punkte kommen aus dem Objektraum in den
        ' Anzeigeraum, werden dort gezogen und wandern zurueck. Beide Richtungen gehen ueber
        ' dieselben zwei Funktionen, damit es keine zweite Formel gibt.

        Private _pathDraft As New List(Of ImageProcessor.PathNode)()
        ''' <summary>Bekommt ein VORHANDENES Objekt die Punkte (leer = es entsteht ein neues
        ''' Pfad-Objekt)? So bekommt ein Textobjekt seine freie Grundlinie, ohne dass daneben ein
        ''' zweites Objekt entsteht.</summary>
        Private _pathDraftTargetId As String = ""
        ''' Der Punkt, dessen Griffe der laufende Zug gerade formt (-1 = keiner).
        Private _pathShapingIndex As Integer = -1
        ''' Beim Nachziehen: welcher Punkt und welcher Teil von ihm haengt am Zeiger.
        Private _pathDragIndex As Integer = -1
        Private _pathDragPart As String = ""
        Private _pathDragCapturedUndo As Boolean = False

        ''' <summary>Zeigt das Panel des Pfad-Werkzeugs.</summary>
        Public ReadOnly Property ShowPathAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Path
            End Get
        End Property

        ''' <summary>Wartet das Werkzeug auf Pfadpunkte? Im PFAD-Werkzeug ohne markierten Pfad ja -
        ''' dann fängt der erste Klick einen neuen an. Ist einer markiert, gehören die Klicks seinen
        ''' Punkten; einen weiteren Pfad beginnt man dann über den Knopf im Panel.</summary>
        Public ReadOnly Property IsPathPenActive As Boolean
            Get
                If _pathDraft.Count > 0 OrElse _pathDraftTargetId <> "" Then Return True
                If String.Equals(NormalizeAnnotationKind(_pendingInsertKind), "Path", StringComparison.Ordinal) Then Return True
                Return _currentTool = EditorTool.Path AndAlso PathEditTarget() Is Nothing
            End Get
        End Property

        ''' <summary>Einen neuen Pfad beginnen, auch wenn gerade einer markiert ist.</summary>
        Public Sub BeginNewPath()
            SelectedAnnotationIndex = -1
            _pathDraft.Clear()
            _pathShapingIndex = -1
            _pathDraftTargetId = ""
            PendingInsertKind = "Path"
            If _currentTool <> EditorTool.Path Then CurrentTool = EditorTool.Path
            StatusText = LocalizationService.T("Punkte setzen, Eingabe schließt ab")
            RaisePathOverlayChanged()
        End Sub

        ''' <summary>Zahl der Stützpunkte des bearbeiteten Pfades - für die Anzeige im Panel.</summary>
        Public ReadOnly Property PathNodeCount As Integer
            Get
                If _pathDraft.Count > 0 Then Return _pathDraft.Count
                Dim target = PathEditTarget()
                If target Is Nothing Then Return 0
                Return ImageProcessor.ParsePathPoints(target.PathPoints).Count
            End Get
        End Property

        Public ReadOnly Property IsSelectedPathClosed As Boolean
            Get
                Dim target = PathEditTarget()
                Return target IsNot Nothing AndAlso target.PathClosed
            End Get
        End Property

        ''' <summary>Pfad schließen oder wieder öffnen. Geschlossen heißt: der letzte Punkt läuft zum
        ''' ersten zurück - für die KONTUR ein Unterschied, für die Füllung keiner.</summary>
        Public Sub ToggleSelectedPathClosed()
            Dim target = PathEditTarget()
            If target Is Nothing Then Return
            PushUndo()
            target.PathClosed = Not target.PathClosed
            _hasChanges = True
            RaisePathOverlayChanged()
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(target))
        End Sub

        ''' <summary>Setzt einen Stützpunkt in die MITTE des längsten Abschnitts. Das ist bewusst
        ''' keine Klickgeste auf der Kurve: die Kurve liegt unter dem Pfad selbst, und ein Klick dort
        ''' bedeutet schon „diesen Pfad greifen". Der Knopf ist eindeutig.</summary>
        Public Sub AddPathNode()
            Dim target = PathEditTarget()
            If target Is Nothing Then Return
            Dim nodes = ImageProcessor.ParsePathPoints(target.PathPoints)
            If nodes.Count < 2 Then Return
            ' Laengster Abschnitt nach dem Abstand der Stuetzpunkte - fein genug, um zu treffen, was
            ' der Nutzer meint, und ohne die Kurve abtasten zu muessen.
            Dim best = 0
            Dim bestLength = -1.0
            Dim last = If(target.PathClosed, nodes.Count - 1, nodes.Count - 2)
            For i = 0 To last
                Dim b = nodes((i + 1) Mod nodes.Count)
                Dim dx = b.Anchor.X - nodes(i).Anchor.X
                Dim dy = b.Anchor.Y - nodes(i).Anchor.Y
                Dim length = dx * dx + dy * dy
                If length > bestLength Then
                    bestLength = length
                    best = i
                End If
            Next
            Dim nextIndex = (best + 1) Mod nodes.Count
            Dim mid = New SKPoint((nodes(best).Anchor.X + nodes(nextIndex).Anchor.X) / 2.0F,
                                  (nodes(best).Anchor.Y + nodes(nextIndex).Anchor.Y) / 2.0F)
            PushUndo()
            nodes.Insert(best + 1, ImageProcessor.PathNode.Corner(mid.X, mid.Y))
            target.PathPoints = ImageProcessor.FormatPathPoints(nodes)
            _hasChanges = True
            RaisePathOverlayChanged()
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(target))
        End Sub

        ''' <summary>Entfernt den zuletzt angefassten Stützpunkt, sonst den letzten. Unter zwei
        ''' Punkten wäre es kein Pfad mehr - dort hört es auf.</summary>
        Public Sub RemovePathNode()
            Dim target = PathEditTarget()
            If target Is Nothing Then Return
            Dim nodes = ImageProcessor.ParsePathPoints(target.PathPoints)
            If nodes.Count <= 2 Then Return
            Dim index = If(_lastTouchedPathNode >= 0 AndAlso _lastTouchedPathNode < nodes.Count,
                           _lastTouchedPathNode, nodes.Count - 1)
            PushUndo()
            nodes.RemoveAt(index)
            target.PathPoints = ImageProcessor.FormatPathPoints(nodes)
            _lastTouchedPathNode = -1
            _hasChanges = True
            RaisePathOverlayChanged()
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(target))
        End Sub

        ''' <summary>Macht aus dem zuletzt angefassten Punkt eine ECKE (Griffe auf den Stützpunkt)
        ''' oder wieder eine glatte Stelle (Griffe entlang der Nachbarn ausgerichtet).</summary>
        Public Sub ToggleLastPathNodeSmooth()
            Dim target = PathEditTarget()
            If target Is Nothing Then Return
            Dim nodes = ImageProcessor.ParsePathPoints(target.PathPoints)
            If nodes.Count < 2 Then Return
            Dim index = If(_lastTouchedPathNode >= 0 AndAlso _lastTouchedPathNode < nodes.Count,
                           _lastTouchedPathNode, nodes.Count - 1)
            Dim node = nodes(index)
            PushUndo()
            Dim isCorner = Math.Abs(node.HandleIn.X - node.Anchor.X) < 0.0001F AndAlso
                           Math.Abs(node.HandleIn.Y - node.Anchor.Y) < 0.0001F AndAlso
                           Math.Abs(node.HandleOut.X - node.Anchor.X) < 0.0001F AndAlso
                           Math.Abs(node.HandleOut.Y - node.Anchor.Y) < 0.0001F
            If isCorner Then
                ' Glaetten: die Griffe zeigen entlang der Verbindung der beiden Nachbarn, je ein
                ' Drittel des Abstands lang. Das ist die uebliche Naeherung und ergibt eine Kurve,
                ' die durch den Punkt laeuft, statt an ihm zu knicken.
                ' Bei einem OFFENEN Pfad haben erster und letzter Punkt nur EINEN Nachbarn. Der
                ' Umlauf zum jeweils anderen Ende richtete die Griffe quer durch den Pfad aus, und
                ' die Kurve knickte unplausibel. Dort zeigt die Richtung deshalb auf den einen
                ' vorhandenen Nachbarn; der Faktor bleibt derselbe (die halbe Nachbarstrecke der
                ' inneren Punkte entspricht einem Drittel des einen Abschnitts).
                Dim dirX As Single
                Dim dirY As Single
                If Not target.PathClosed AndAlso index = 0 Then
                    Dim nextAnchor = nodes(1).Anchor
                    dirX = (nextAnchor.X - node.Anchor.X) / 3.0F
                    dirY = (nextAnchor.Y - node.Anchor.Y) / 3.0F
                ElseIf Not target.PathClosed AndAlso index = nodes.Count - 1 Then
                    Dim previous = nodes(nodes.Count - 2).Anchor
                    dirX = (node.Anchor.X - previous.X) / 3.0F
                    dirY = (node.Anchor.Y - previous.Y) / 3.0F
                Else
                    Dim previous = nodes((index - 1 + nodes.Count) Mod nodes.Count).Anchor
                    Dim nextAnchor = nodes((index + 1) Mod nodes.Count).Anchor
                    dirX = (nextAnchor.X - previous.X) / 6.0F
                    dirY = (nextAnchor.Y - previous.Y) / 6.0F
                End If
                node.HandleIn = New SKPoint(node.Anchor.X - dirX, node.Anchor.Y - dirY)
                node.HandleOut = New SKPoint(node.Anchor.X + dirX, node.Anchor.Y + dirY)
            Else
                node.HandleIn = node.Anchor
                node.HandleOut = node.Anchor
            End If
            nodes(index) = node
            target.PathPoints = ImageProcessor.FormatPathPoints(nodes)
            _hasChanges = True
            RaisePathOverlayChanged()
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(target))
        End Sub

        ''' Der zuletzt angefasste Stuetzpunkt - Bezug fuer Entfernen und Glaetten.
        Private _lastTouchedPathNode As Integer = -1

        Public ReadOnly Property HasPathDraft As Boolean
            Get
                Return _pathDraft.Count > 0
            End Get
        End Property

        ''' <summary>Solange die PUNKTE eines Pfades gemeint sind (ein Entwurf läuft oder im
        ''' Pfad-Werkzeug ist ein Pfad bzw. eine freie Grundlinie markiert), verschwindet der
        ''' Auswahlrahmen GANZ, nicht nur seine Griffe: er liegt auf denselben Ecken wie die
        ''' äußeren Stützpunkte, seine Greifzonen fingen die Klicks darauf ab, und beim Setzen
        ''' neuer Punkte über dem Objekt schluckte er den Druck. Welches Objekt gemeint ist,
        ''' zeigen die Punkte selbst.</summary>
        Public ReadOnly Property HidesSelectionFrameForPath As Boolean
            Get
                Return _pathDraft.Count > 0 OrElse _pathDraftTargetId <> "" OrElse PathEditTarget() IsNot Nothing
            End Get
        End Property

        ''' <summary>Das Objekt, dessen Punkte gerade nachgezogen werden koennen: ein markierter Pfad
        ''' oder ein Textobjekt, dessen Grundlinie ein freier Pfad ist. NUR im Pfad-Werkzeug: sonst
        ''' laegen die Stuetzpunkte unter dem Auswahlrahmen des Objekts, und ein Zug meinte je nach
        ''' getroffenem Pixel das eine oder das andere - dieselbe Entscheidung wie beim Verzerren,
        ''' wo die Ecken den Rahmen verdraengen. GEDREHTE Objekte bleiben aussen vor: ihr
        ''' Anzeigerechteck ist die unrotierte Huelle, und die Punkte laegen beim Ziehen daneben.
        ''' Erst drehen, dann ziehen waere ein eigener Umbau.</summary>
        Private Function PathEditTarget() As ImageAnnotation
            If _currentTool <> EditorTool.Path Then Return Nothing
            Dim a = CurrentObject()
            If a Is Nothing OrElse Math.Abs(a.RotationDegrees) > 0.01F Then Return Nothing
            Dim kind = NormalizeAnnotationKind(a.Kind)
            Dim isFreeTextPath = String.Equals(kind, "Text", StringComparison.Ordinal) AndAlso
                                 String.Equals(a.TextPathKind, "Free", StringComparison.OrdinalIgnoreCase)
            If Not String.Equals(kind, "Path", StringComparison.Ordinal) AndAlso Not isFreeTextPath Then Return Nothing
            Return a
        End Function

        ''' <summary>Der Knopf "Text auf dem Pfad": nur fuer ein markiertes PFAD-Objekt mit Punkten.
        ''' Eine freie Grundlinie ist selbst schon Text, und ohne Punkte gaebe es nichts zu
        ''' uebernehmen.</summary>
        Public ReadOnly Property CanCreateTextOnPath As Boolean
            Get
                If _pathDraft.Count > 0 Then Return False
                Dim a = PathEditTarget()
                Return a IsNot Nothing AndAlso
                       String.Equals(NormalizeAnnotationKind(a.Kind), "Path", StringComparison.Ordinal) AndAlso
                       Not String.IsNullOrWhiteSpace(a.PathPoints)
            End Get
        End Property

        ''' <summary>Erzeugt ein Textobjekt, dessen Grundlinie die Punkte des markierten Pfades
        ''' uebernimmt - der Standardweg "Pfad zeichnen, Text daraufsetzen". Der Pfad selbst bleibt
        ''' bestehen: wer nur die Linie als Grundlinie wollte, blendet ihn aus oder loescht ihn.
        ''' Punkte und Box werden KOPIERT, nicht geteilt - zwei Objekte auf derselben Punktliste
        ''' zoegen einander die Grundlinie weg, sobald eines skaliert wird.</summary>
        Public Sub CreateTextOnSelectedPath()
            Dim source = CurrentObject()
            If source Is Nothing OrElse String.IsNullOrWhiteSpace(source.PathPoints) Then Return
            If Not String.Equals(NormalizeAnnotationKind(source.Kind), "Path", StringComparison.Ordinal) Then Return
            PushUndo()
            Dim text = New ImageAnnotation With {
                .Kind = "Text",
                .Text = LocalizationService.T("Text"),
                .XPixels = source.XPixels, .YPixels = source.YPixels,
                .WidthPixels = source.WidthPixels, .HeightPixels = source.HeightPixels,
                .RotationDegrees = source.RotationDegrees,
                .TextPathKind = "Free",
                .PathPoints = source.PathPoints,
                .PathClosed = source.PathClosed,
                .IsVisible = True
            }
            Dim index = _annotations.IndexOf(source)
            If index < 0 Then index = _annotations.Count - 1
            _annotations.Insert(index + 1, text)
            _hasChanges = True
            ' Danach ins TEXT-Werkzeug: dort steht das Eingabefeld, und genau das Tippen ist der
            ' naechste Schritt. Aus dem Pfad-Werkzeug heraus bleibt das Werkzeug beim Markieren
            ' sonst bewusst stehen (IsObjectTransformTool), deshalb der ausdrueckliche Wechsel.
            ' Die Grundlinie bleibt im Pfad-Werkzeug an ihren Punkten aenderbar.
            SelectedAnnotationIndex = index + 1
            If _currentTool <> EditorTool.Text Then CurrentTool = EditorTool.Text
            RaisePathOverlayChanged()
            RebuildLayerRows()
            RaiseResetButtonStateChanged()
            AddHistoryEntry(LocalizationService.T("Text auf den Pfad gesetzt"))
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(text))
        End Sub

        Public ReadOnly Property CanEditPathNodes As Boolean
            Get
                Return _pathDraft.Count = 0 AndAlso PathEditTarget() IsNot Nothing
            End Get
        End Property

        ''' <summary>Die Punkte des markierten Pfades in ANZEIGE-Prozent.</summary>
        Private Function ReadPathNodesForDisplay(annotation As ImageAnnotation) As List(Of ImageProcessor.PathNode)
            Dim nodes = ImageProcessor.ParsePathPoints(annotation.PathPoints)
            Dim r = GetSelectedAnnotationDisplayRectPercent()
            If r.Width <= 0 OrElse r.Height <= 0 Then Return New List(Of ImageProcessor.PathNode)()
            Dim toDisplay = Function(p As SKPoint) New SKPoint(CSng(r.X + p.X / 100.0 * r.Width),
                                                               CSng(r.Y + p.Y / 100.0 * r.Height))
            Return nodes.Select(Function(n) New ImageProcessor.PathNode With {
                .Anchor = toDisplay(n.Anchor), .HandleIn = toDisplay(n.HandleIn), .HandleOut = toDisplay(n.HandleOut)}).ToList()
        End Function

        ''' <summary>Gegenrichtung: Anzeige-Prozent zurueck in das Rechteck DES OBJEKTS. Das Rechteck
        ''' bleibt dabei unveraendert - ein Punkt darf ueber seinen Rand hinaus, so wie eine Ecke der
        ''' Objektverzerrung auch.
        ''' Bezug ist das Rechteck der MARKIERUNG. Wer die Punkte eines anderen Objekts schreibt oder
        ''' das Modellrechteck gerade selbst gesetzt hat, nimmt WritePathNodesInRect und gibt das
        ''' passende Rechteck ausdruecklich mit - sonst rechnen Modell und Bezug auseinander.</summary>
        Private Sub WritePathNodesFromDisplay(annotation As ImageAnnotation, nodes As List(Of ImageProcessor.PathNode))
            WritePathNodesInRect(annotation, nodes, GetSelectedAnnotationDisplayRectPercent())
        End Sub

        ''' <summary>Dieselbe Umrechnung mit ausdruecklich uebergebenem Bezugsrechteck in
        ''' Anzeige-Prozent.</summary>
        Private Shared Sub WritePathNodesInRect(annotation As ImageAnnotation,
                                                nodes As List(Of ImageProcessor.PathNode),
                                                r As (X As Double, Y As Double, Width As Double, Height As Double))
            If annotation Is Nothing OrElse nodes Is Nothing Then Return
            If r.Width <= 0 OrElse r.Height <= 0 Then Return
            Dim toObject = Function(p As SKPoint) New SKPoint(CSng((p.X - r.X) / r.Width * 100.0),
                                                              CSng((p.Y - r.Y) / r.Height * 100.0))
            annotation.PathPoints = ImageProcessor.FormatPathPoints(
                nodes.Select(Function(n) New ImageProcessor.PathNode With {
                    .Anchor = toObject(n.Anchor), .HandleIn = toObject(n.HandleIn), .HandleOut = toObject(n.HandleOut)}))
        End Sub

        ''' <summary>Was das Overlay zeichnet: Anzahl, geschlossen-Kennzeichen, Entwurf-Kennzeichen,
        ''' dann je Punkt sechs Zahlen in ANZEIGE-Prozent. Nothing = nichts zu zeichnen.</summary>
        Public ReadOnly Property PathOverlayValues As Double()
            Get
                Dim nodes As List(Of ImageProcessor.PathNode)
                Dim closed = False
                If _pathDraft.Count > 0 Then
                    nodes = _pathDraft
                Else
                    Dim target = PathEditTarget()
                    If target Is Nothing Then Return Nothing
                    nodes = ReadPathNodesForDisplay(target)
                    closed = target.PathClosed
                End If
                If nodes.Count = 0 Then Return Nothing
                Dim values(nodes.Count * 6 + 2) As Double
                values(0) = nodes.Count
                values(1) = If(closed, 1.0, 0.0)
                values(2) = If(_pathDraft.Count > 0, 1.0, 0.0)
                For i = 0 To nodes.Count - 1
                    Dim o = 3 + i * 6
                    values(o) = nodes(i).Anchor.X : values(o + 1) = nodes(i).Anchor.Y
                    values(o + 2) = nodes(i).HandleIn.X : values(o + 3) = nodes(i).HandleIn.Y
                    values(o + 4) = nodes(i).HandleOut.X : values(o + 5) = nodes(i).HandleOut.Y
                Next
                Return values
            End Get
        End Property

        Private Sub RaisePathOverlayChanged()
            Me.RaisePropertyChanged(NameOf(PathOverlayValues))
            Me.RaisePropertyChanged(NameOf(HasPathDraft))
            Me.RaisePropertyChanged(NameOf(IsPathPenActive))
            Me.RaisePropertyChanged(NameOf(CanEditPathNodes))
            Me.RaisePropertyChanged(NameOf(HidesSelectionFrameForPath))
            Me.RaisePropertyChanged(NameOf(CanCreateTextOnPath))
            Me.RaisePropertyChanged(NameOf(PathNodeCount))
            Me.RaisePropertyChanged(NameOf(IsSelectedPathClosed))
            ' Der Auswahlrahmen legt seine Griffe ab, solange die Punkte gemeint sind - sonst laegen
            ' beide auf demselben Rechteck.
            Me.RaisePropertyChanged(NameOf(ShowsObjectFrameHandles))
            RequestOverlayStateNotify()
        End Sub

        ''' <summary>Ein Druck auf die Buehne, solange Pfade gemeint sind. True heisst: verarbeitet.</summary>
        Public Function TryBeginPathPointer(xPercent As Double, yPercent As Double,
                                            slopXPercent As Double, slopYPercent As Double) As Boolean
            Dim point = New SKPoint(CSng(xPercent), CSng(yPercent))

            ' 1) Ein laufender Entwurf: auf den ersten Punkt geklickt heisst SCHLIESSEN.
            If _pathDraft.Count > 0 Then
                If _pathDraft.Count >= 2 AndAlso IsNearPathPoint(_pathDraft(0).Anchor, point, slopXPercent, slopYPercent) Then
                    FinishPathDraft(keep:=True, closed:=True)
                    Return True
                End If
                _pathDraft.Add(ImageProcessor.PathNode.Corner(point.X, point.Y))
                _pathShapingIndex = _pathDraft.Count - 1
                RaisePathOverlayChanged()
                Return True
            End If

            ' 2) Punkte eines fertigen Pfades nachziehen.
            Dim target = PathEditTarget()
            If target IsNot Nothing Then
                Dim nodes = ReadPathNodesForDisplay(target)
                For i = 0 To nodes.Count - 1
                    ' Griffe zuerst: sie liegen bei einem Eckpunkt AUF dem Stuetzpunkt, und dann soll
                    ' der Stuetzpunkt gewinnen - deshalb zaehlt ein Griff nur, wenn er abgesetzt ist.
                    If Not IsSamePathPoint(nodes(i).HandleOut, nodes(i).Anchor) AndAlso
                       IsNearPathPoint(nodes(i).HandleOut, point, slopXPercent, slopYPercent) Then
                        Return BeginPathDrag(i, "out")
                    End If
                    If Not IsSamePathPoint(nodes(i).HandleIn, nodes(i).Anchor) AndAlso
                       IsNearPathPoint(nodes(i).HandleIn, point, slopXPercent, slopYPercent) Then
                        Return BeginPathDrag(i, "in")
                    End If
                Next
                For i = 0 To nodes.Count - 1
                    If IsNearPathPoint(nodes(i).Anchor, point, slopXPercent, slopYPercent) Then
                        Return BeginPathDrag(i, "anchor")
                    End If
                Next
            End If

            ' Ein Klick auf einen VORHANDENEN Pfad meint IHN und keinen neuen - sonst liesse sich ein
            ' fertiger Pfad im Pfad-Werkzeug nie wieder anfassen. Die Auswahl uebernimmt danach der
            ' normale Weg, und beim naechsten Klick liegen seine Punkte schon da.
            If _pathDraft.Count = 0 Then
                Dim hit = HitTestAnnotation(xPercent, yPercent, slopXPercent, slopYPercent)
                If hit >= 0 AndAlso hit < _annotations.Count AndAlso _annotations(hit) IsNot Nothing AndAlso
                   String.Equals(NormalizeAnnotationKind(_annotations(hit).Kind), "Path", StringComparison.Ordinal) Then
                    Return False
                End If
            End If

            ' 3) Erster Punkt - fuer ein neues Pfad-Objekt oder fuer die Grundlinie eines Textes.
            '    Bei MARKIERTEM Pfad ist IsPathPenActive False: ein Klick ins Leere faellt dann
            '    durch und waehlt ab, wie in jedem anderen Werkzeug - einen weiteren Pfad beginnt
            '    man ueber den Knopf im Panel. Vorher startete genau dieser Klick einen ungewollten
            '    Ein-Punkt-Entwurf, der die Punktanzeige des markierten Pfads verdraengte.
            If IsPathPenActive Then
                _pathDraft.Clear()
                _pathDraft.Add(ImageProcessor.PathNode.Corner(point.X, point.Y))
                _pathShapingIndex = 0
                RaisePathOverlayChanged()
                Return True
            End If

            Return False
        End Function

        ''' <summary>Fängt einen Entwurf an, dessen Punkte in ein VORHANDENES Objekt gehen.</summary>
        Private Sub BeginPathDraftFor(target As ImageAnnotation)
            If target Is Nothing Then Return
            _pathDraft.Clear()
            _pathShapingIndex = -1
            _pathDraftTargetId = target.Id
            StatusText = LocalizationService.T("Punkte setzen, Eingabe schließt ab")
            RaisePathOverlayChanged()
        End Sub

        Private Function BeginPathDrag(index As Integer, part As String) As Boolean
            _pathDragIndex = index
            _pathDragPart = part
            _pathDragCapturedUndo = False
            _lastTouchedPathNode = index
            RaisePathOverlayChanged()
            Return True
        End Function

        ''' <summary>Liegt der Zeiger auf einem Stützpunkt oder Griff? Nur zur Frage, ob das
        ''' Text-Overlay den Druck DURCHLASSEN muss - es liegt über der Bühne, und ohne diese Frage
        ''' käme kein Druck je bei den Punkten an. Gegriffen wird weiter unten.</summary>
        Public Function HitsPathPointPercent(xPercent As Double, yPercent As Double,
                                             slopXPercent As Double, slopYPercent As Double) As Boolean
            Dim values = PathOverlayValues
            If values Is Nothing OrElse values.Length < 9 Then Return False
            Dim count = CInt(values(0))
            Dim point = New SKPoint(CSng(xPercent), CSng(yPercent))
            For i = 0 To count - 1
                Dim o = 3 + i * 6
                If o + 5 >= values.Length Then Exit For
                For k = 0 To 2
                    Dim candidate = New SKPoint(CSng(values(o + k * 2)), CSng(values(o + k * 2 + 1)))
                    If IsNearPathPoint(candidate, point, slopXPercent, slopYPercent) Then Return True
                Next
            Next
            Return False
        End Function

        Private Shared Function IsNearPathPoint(a As SKPoint, b As SKPoint,
                                                slopX As Double, slopY As Double) As Boolean
            Dim dx = (a.X - b.X) / Math.Max(0.0001, slopX)
            Dim dy = (a.Y - b.Y) / Math.Max(0.0001, slopY)
            Return dx * dx + dy * dy <= 1.0
        End Function

        Private Shared Function IsSamePathPoint(a As SKPoint, b As SKPoint) As Boolean
            Return Math.Abs(a.X - b.X) < 0.0001F AndAlso Math.Abs(a.Y - b.Y) < 0.0001F
        End Function

        ''' <summary>Zeigerbewegung: entweder formt sie die Griffe des eben gesetzten Punktes, oder
        ''' sie zieht einen vorhandenen Punkt bzw. Griff.</summary>
        Public Sub UpdatePathPointer(xPercent As Double, yPercent As Double)
            Dim point = New SKPoint(CSng(xPercent), CSng(yPercent))
            If _pathShapingIndex >= 0 AndAlso _pathShapingIndex < _pathDraft.Count Then
                ' Der Zug vom gesetzten Punkt weg spannt die Kurve auf: der ausgehende Griff folgt dem
                ' Zeiger, der eingehende spiegelt ihn. Ein Punkt ohne Zug bleibt damit eine Ecke.
                Dim node = _pathDraft(_pathShapingIndex)
                node.HandleOut = point
                node.HandleIn = New SKPoint(node.Anchor.X * 2.0F - point.X, node.Anchor.Y * 2.0F - point.Y)
                _pathDraft(_pathShapingIndex) = node
                RaisePathOverlayChanged()
                Return
            End If

            If _pathDragIndex < 0 Then Return
            Dim target = PathEditTarget()
            If target Is Nothing Then Return
            Dim nodes = ReadPathNodesForDisplay(target)
            If _pathDragIndex >= nodes.Count Then Return
            If Not _pathDragCapturedUndo Then
                CaptureUndoState("Pfad")
                _pathDragCapturedUndo = True
            End If
            Dim edited = nodes(_pathDragIndex)
            Select Case _pathDragPart
                Case "anchor"
                    ' Der Stuetzpunkt nimmt seine Griffe MIT - sonst klappt die Kurve bei jedem
                    ' Verschieben um, statt ihre Form zu behalten.
                    Dim dx = point.X - edited.Anchor.X, dy = point.Y - edited.Anchor.Y
                    edited.Anchor = point
                    edited.HandleIn = New SKPoint(edited.HandleIn.X + dx, edited.HandleIn.Y + dy)
                    edited.HandleOut = New SKPoint(edited.HandleOut.X + dx, edited.HandleOut.Y + dy)
                Case "in"
                    edited.HandleIn = point
                Case Else
                    edited.HandleOut = point
            End Select
            nodes(_pathDragIndex) = edited
            WritePathNodesFromDisplay(target, nodes)
            RaisePathOverlayChanged()
            SchedulePreviewUpdate()
        End Sub

        Public Sub EndPathPointer()
            Dim wasDragging = _pathDragIndex >= 0
            _pathShapingIndex = -1
            _pathDragIndex = -1
            _pathDragPart = ""
            _pathDragCapturedUndo = False
            If wasDragging Then
                ' Ein Punkt darf ueber das Objektrechteck hinausgezogen werden - danach muss das
                ' Rechteck ihm folgen. Sonst waere der Teil ausserhalb nicht mehr zu sehen: alles
                ' andere am Objekt rechnet mit diesem Rechteck, vom Auffrischen der Anzeige bis zum
                ' Treffertest.
                RefitPathBoundsToPoints()
                _hasChanges = True
                RefreshPreviewImmediately()
            End If
            RaisePathOverlayChanged()
        End Sub

        ''' <summary>Zieht das Objektrechteck auf die tatsaechliche Ausdehnung des Pfades nach und
        ''' rechnet die Punkte auf das neue Rechteck um. Am Bild aendert sich dadurch NICHTS - die
        ''' Punkte liegen hinterher an denselben Stellen, nur ihr Bezug stimmt wieder.</summary>
        Private Sub RefitPathBoundsToPoints()
            Dim target = PathEditTarget()
            If target Is Nothing Then Return
            Dim nodes = ReadPathNodesForDisplay(target)
            If nodes.Count < 2 Then Return

            Dim minX = Double.MaxValue, minY = Double.MaxValue
            Dim maxX = Double.MinValue, maxY = Double.MinValue
            For Each n In nodes
                For Each p In {n.Anchor, n.HandleIn, n.HandleOut}
                    minX = Math.Min(minX, p.X) : maxX = Math.Max(maxX, p.X)
                    minY = Math.Min(minY, p.Y) : maxY = Math.Max(maxY, p.Y)
                Next
            Next
            Const MinimumExtentPercent As Double = 1.0
            If maxX - minX < MinimumExtentPercent Then
                Dim mid = (minX + maxX) / 2.0
                minX = mid - MinimumExtentPercent / 2.0 : maxX = mid + MinimumExtentPercent / 2.0
            End If
            If maxY - minY < MinimumExtentPercent Then
                Dim mid = (minY + maxY) / 2.0
                minY = mid - MinimumExtentPercent / 2.0 : maxY = mid + MinimumExtentPercent / 2.0
            End If

            Dim current = GetSelectedAnnotationDisplayRectPercent()
            ' Nichts tun, solange sich praktisch nichts geaendert hat: sonst schriebe jeder Zug das
            ' Rechteck neu und die Rundung wanderte mit.
            If Math.Abs(current.X - minX) < 0.01 AndAlso Math.Abs(current.Y - minY) < 0.01 AndAlso
               Math.Abs(current.Width - (maxX - minX)) < 0.01 AndAlso
               Math.Abs(current.Height - (maxY - minY)) < 0.01 Then Return

            Dim stored = DisplayAnnotationRectToStoredPercent(NormalizeAnnotationKind(target.Kind),
                                                              minX, minY, maxX - minX, maxY - minY)
            target.XPixels = CSng(PercentXToPixels(stored.X))
            target.YPixels = CSng(PercentYToPixels(stored.Y))
            target.WidthPixels = CSng(Math.Max(1.0, PercentXToPixels(stored.Width)))
            target.HeightPixels = CSng(Math.Max(1.0, PercentYToPixels(stored.Height)))
            ' Die Punkte beziehen sich auf das NEUE Rechteck, und genau dieses wird ausdruecklich
            ' mitgegeben. Der Bezug ueber die Editor-Puffer taugt hier NICHT: die stehen bis
            ' LoadSelectedAnnotationIntoEditor noch auf dem ALTEN Rechteck, und die Punkte laegen
            ' danach im falschen Bezug - der Pfad sprang und verzerrte sich nach jedem Zug, der die
            ' Grenzen aenderte.
            WritePathNodesInRect(target, nodes, (minX, minY, maxX - minX, maxY - minY))
            LoadSelectedAnnotationIntoEditor()
        End Sub

        ''' <summary>Entwurf abschliessen. <paramref name="keep"/> False verwirft ihn (Esc), True legt
        ''' das Objekt an - sofern mindestens zwei Punkte stehen. Ein Pfad aus einem Punkt ist keiner;
        ''' er wird stillschweigend verworfen, statt ein unsichtbares Objekt zu hinterlassen.</summary>
        Public Sub FinishPathDraft(keep As Boolean, Optional closed As Boolean = False)
            Dim nodes = _pathDraft.ToList()
            Dim targetId = _pathDraftTargetId
            _pathDraft.Clear()
            _pathShapingIndex = -1
            _pathDraftTargetId = ""
            If Not keep OrElse nodes.Count < 2 Then
                RaisePathOverlayChanged()
                Return
            End If

            ' Die Punkte gehoeren einem VORHANDENEN Objekt (Grundlinie eines Textes): dann entsteht
            ' kein zweites Objekt, die Punkte wandern nur in seinen Raum.
            If targetId <> "" Then
                Dim target = _annotations.FirstOrDefault(Function(a) a IsNot Nothing AndAlso
                                                             String.Equals(a.Id, targetId, StringComparison.Ordinal))
                If target IsNot Nothing Then
                    PushUndo()
                    target.PathClosed = closed
                    ' Bezug ist das Rechteck DES ZIELS, nicht das der Markierung: die kann sich
                    ' waehrend eines Grundlinien-Entwurfs ueber das Ebenenpanel geaendert haben, und
                    ' die Punkte laegen dann im Rechteck eines fremden Objekts.
                    WritePathNodesInRect(target, nodes, StoredAnnotationRectToDisplayPercent(target))
                    _hasChanges = True
                    RaisePathOverlayChanged()
                    RebuildLayerRows()
                    AddHistoryEntry(LocalizationService.T("Grundlinie gesetzt"))
                    RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(target))
                    Return
                End If
            End If

            ' Das umschliessende Rechteck aus Stuetzpunkten UND Griffen: ein Griff darf die Kurve
            ' ueber die Stuetzpunkte hinaustragen, und was ausserhalb des Objektrechtecks liegt,
            ' waere spaeter beim Skalieren nicht mehr richtig bezogen.
            Dim minX = Double.MaxValue, minY = Double.MaxValue
            Dim maxX = Double.MinValue, maxY = Double.MinValue
            For Each n In nodes
                For Each p In {n.Anchor, n.HandleIn, n.HandleOut}
                    minX = Math.Min(minX, p.X) : maxX = Math.Max(maxX, p.X)
                    minY = Math.Min(minY, p.Y) : maxY = Math.Max(maxY, p.Y)
                Next
            Next
            ' Ein waagerechter oder senkrechter Pfad hat eine Seite ohne Ausdehnung. Ein Rechteck mit
            ' Breite oder Hoehe null liesse sich weder anfassen noch skalieren, deshalb ein Mindestmass.
            Const MinimumExtentPercent As Double = 1.0
            If maxX - minX < MinimumExtentPercent Then
                Dim mid = (minX + maxX) / 2.0
                minX = mid - MinimumExtentPercent / 2.0 : maxX = mid + MinimumExtentPercent / 2.0
            End If
            If maxY - minY < MinimumExtentPercent Then
                Dim mid = (minY + maxY) / 2.0
                minY = mid - MinimumExtentPercent / 2.0 : maxY = mid + MinimumExtentPercent / 2.0
            End If

            Dim width = maxX - minX, height = maxY - minY
            Dim toObject = Function(p As SKPoint) New SKPoint(CSng((p.X - minX) / width * 100.0),
                                                              CSng((p.Y - minY) / height * 100.0))
            Dim objectNodes = nodes.Select(Function(n) New ImageProcessor.PathNode With {
                .Anchor = toObject(n.Anchor), .HandleIn = toObject(n.HandleIn), .HandleOut = toObject(n.HandleOut)})

            PushUndo()
            Dim stored = DisplayAnnotationRectToStoredPercent("Path", minX, minY, width, height)
            Dim annotation = New ImageAnnotation With {
                .Kind = "Path",
                .PathPoints = ImageProcessor.FormatPathPoints(objectNodes),
                .PathClosed = closed,
                .XPixels = CSng(PercentXToPixels(stored.X)),
                .YPixels = CSng(PercentYToPixels(stored.Y)),
                .WidthPixels = CSng(Math.Max(1.0, PercentXToPixels(stored.Width))),
                .HeightPixels = CSng(Math.Max(1.0, PercentYToPixels(stored.Height))),
                .FillColor = If(closed, _annotationFillColor, "#00FFFFFF"),
                .StrokeColor = _annotationStrokeColor,
                .StrokeWidth = CSng(Math.Max(1.0, _annotationStrokeWidth)),
                .Opacity = CSng(_annotationOpacity),
                .BlendMode = _annotationBlendMode,
                .BlendIncludesStroke = _annotationBlendIncludesStroke,
                .IsVisible = True
            }
            _annotations.Add(annotation)
            PendingInsertKind = ""
            SelectedAnnotationIndex = _annotations.Count - 1
            _hasChanges = True
            RaisePathOverlayChanged()
            RebuildLayerRows()
            RaiseResetButtonStateChanged()
            AddHistoryEntry(LocalizationService.T("Pfad gezeichnet"))
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(annotation))
        End Sub
    End Class

End Namespace
