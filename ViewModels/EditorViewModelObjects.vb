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
                    Me.RaisePropertyChanged(NameOf(AnnotationDisplayFlipVertical))
                    FlipSelectionBox(horizontal:=False)
                    Return
                End If
                Me.RaiseAndSetIfChanged(_annotationFlipV, value)
                Me.RaisePropertyChanged(NameOf(AnnotationDisplayFlipVertical))
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

        ''' <summary>Untergrenze der Objektgroesse in Prozent, nach Art des Objekts. EINE Stelle
        ''' dafuer, weil sie an vier Orten gebraucht wird - in beiden Setzern, beim Setzen des ganzen
        ''' Rechtecks aus dem Canvas und an den Bedienelementen. Liefen die auseinander, klemmte das
        ''' Bedienelement bei einem anderen Wert als das Rezept, und der Nutzer kaeme an eine Groesse
        ''' nicht heran, die die Anwendung sehr wohl haelt - genau so stand es hier.</summary>
        Private Function MinAnnotationWidthPercentValue() As Double
            If EffectiveAnnotationKind = "QR" Then Return PercentOfAxis(MinQrSidePixels(), DisplayImageWidthPixels)
            Dim isTextual = IsTextualAnnotationKind(EffectiveAnnotationKind) AndAlso Not IsWatermarkImageSource
            Return If(isTextual, MinTextAnnotationWidthPercent, MinShapeAnnotationWidthPercent)
        End Function

        Private Function MinAnnotationHeightPercentValue() As Double
            If EffectiveAnnotationKind = "QR" Then Return PercentOfAxis(MinQrSidePixels(), DisplayImageHeightPixels)
            Dim isTextual = IsTextualAnnotationKind(EffectiveAnnotationKind) AndAlso Not IsWatermarkImageSource
            Return If(isTextual, MinTextAnnotationHeightPercent, MinShapeAnnotationHeightPercent)
        End Function

        ''' <summary>Die kleinste KANTE eines QR-Codes in Bildpunkten. Er ist quadratisch, also hat
        ''' er EINE Untergrenze; die beiden Prozentwerte darueber sind nur ihre Umrechnung auf die
        ''' jeweilige Achse.
        '''
        ''' Vorher standen dort zwei unabhaengige Prozentwerte, 5 der Breite und 4 der Hoehe. Beide
        ''' beziehen sich auf eine ANDERE Kante, und auf einem nicht quadratischen Bild ergab die
        ''' Untergrenze damit ein Rechteck: auf 2480x1403 waeren es 124 zu 56 Bildpunkte gewesen.
        '''
        ''' Ein Prozent, wie bei allem anderen auch, gemessen an der KUERZEREN Bildkante - damit ist
        ''' die Grenze auf einem hochkanten und einem querformatigen Bild dieselbe. Ob ein so kleiner
        ''' Code noch zu lesen ist, entscheidet nicht die Anwendung: das haengt an der Ausgabegroesse
        ''' und am Drucker, und wer ihn klein haben will, hat dafuer seinen Grund.</summary>
        Private Function MinQrSidePixels() As Double
            Dim size = GetAnnotationDisplayPixelSize()
            Dim shorter = Math.Min(size.Width, size.Height)
            If shorter <= 0 Then Return 1.0
            Return Math.Max(1.0, shorter * MinShapeAnnotationWidthPercent / 100.0)
        End Function

        ''' <summary>Eine Laenge in Bildpunkten als Anteil einer Bildkante. Ohne bekannte Kante
        ''' bleibt es beim alten Zahlenwert, statt durch null zu teilen.</summary>
        Private Shared Function PercentOfAxis(pixels As Double, axisPixels As Integer) As Double
            If axisPixels <= 0 Then Return 5.0
            Return Math.Max(0.0, pixels) / axisPixels * 100.0
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
                        ' EINE Kantenlaenge, daraus beide Achsen - nur so bleibt der Code quadratisch.
                        ' Die Untergrenze gehoert deshalb an die Kante und nicht an die beiden
                        ' Prozentwerte: die beziehen sich auf verschieden lange Bildkanten.
                        Dim sizePixels = Math.Max(MinQrSidePixels(), displaySize.Width * Math.Min(MaxAnnotationWidthPercentValue(), value) / 100.0)
                        _annotationWidthPercent = Math.Min(MaxAnnotationWidthPercentValue(), sizePixels / displaySize.Width * 100.0)
                        _annotationHeightPercent = Math.Min(MaxAnnotationHeightPercentValue(), sizePixels / displaySize.Height * 100.0)
                        RaiseAnnotationSizeChanged()
                        SyncSelectedAnnotation()
                        Return
                    End If
                End If
                Dim minWidth = MinAnnotationWidthPercentValue()
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
                        ' Wie beim Breiten-Setzer: eine Kantenlaenge, daraus beide Achsen.
                        Dim sizePixels = Math.Max(MinQrSidePixels(), displaySize.Height * Math.Min(MaxAnnotationHeightPercentValue(), value) / 100.0)
                        _annotationWidthPercent = Math.Min(MaxAnnotationWidthPercentValue(), sizePixels / displaySize.Width * 100.0)
                        _annotationHeightPercent = Math.Min(MaxAnnotationHeightPercentValue(), sizePixels / displaySize.Height * 100.0)
                        RaiseAnnotationSizeChanged()
                        SyncSelectedAnnotation()
                        Return
                    End If
                End If
                Dim minHeight = MinAnnotationHeightPercentValue()
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

        ''' <summary>Kehrt die Laufrichtung des Textpfads um und setzt die Buchstaben damit auf
        ''' die andere Seite seiner Grundlinie. Gilt fuer jede Textpfadform und Text-Wasserzeichen.</summary>
        Public Property AnnotationTextPathInverted As Boolean
            Get
                Return _annotationTextPathInverted
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationTextPathInverted, value)
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

        ''' <summary>True fuer beide Varianten des freien Textpfads. Die inverse Variante benutzt
        ''' dieselben Stuetzpunkte, laeuft sie aber in Gegenrichtung ab, damit der Text auf der
        ''' anderen Seite der Grundlinie sitzt.</summary>
        Friend Shared Function IsFreeTextPath(kind As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(kind) AndAlso
                   kind.StartsWith("Free", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Behaelt ein Text auf FREIEM Pfad seine Box? Ja, sobald die Grundlinie gezeichnet
        ''' ist: die Punkte liegen in Prozent der Box, und jede Neuvermessung auf den geraden
        ''' Textkasten streckte die gezeichnete Kurve auf den neuen Kasten - die Grundlinie verformte
        ''' sich mit jedem Tastendruck. Die Box legt stattdessen RefitPathBoundsToPoints um die
        ''' Punkte, wie beim Pfad-Objekt; ein Zug am Auswahlrahmen skaliert Grundlinie und Box
        ''' gemeinsam.</summary>
        Private Function FreeTextPathKeepsBox() As Boolean
            If Not IsFreeTextPath(_annotationTextPathKind) Then Return False
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
            If IsFreeTextPath(AnnotationTextPathKind) Then
                Dim target = CurrentObject()
                If target IsNot Nothing AndAlso String.IsNullOrWhiteSpace(target.PathPoints) Then
                    ' Der Entwurf braucht dieselbe Zeigerbehandlung wie ein eigenstaendiger Pfad.
                    ' Ohne den Werkzeugwechsel konnte der erste Klick je nach zuvor aktivem Werkzeug
                    ' noch als Verschieben/Einsetzen des Textes verarbeitet werden. Das betraf auch
                    ' Text-Wasserzeichen, weil sie den gleichen Entwurf verwenden.
                    If _currentTool <> EditorTool.Path Then CurrentTool = EditorTool.Path
                    BeginPathDraftFor(target)
                End If
            End If

            ' Die Form ändert die sichtbare Ausdehnung des Textes (ein Kreis hat eine quadratische
            ' Box, Bogen/Welle wieder den Textkasten). Der Property-Setter schreibt bislang nur die
            ' neue Form in das Objekt; ohne diesen Schritt blieb der Auswahlrahmen auf den Maßen und
            ' der Position der VORHERIGEN Form stehen, obwohl der Renderer schon den neuen Pfad
            ' zeichnete. Freie Pfade mit vorhandenen Punkten sind in SyncSelectedTextAnnotationSize
            ' bewusst ausgenommen: deren Box gehört zur gezeichneten Grundlinie und darf nicht
            ' umgemessen werden.
            SyncSelectedTextAnnotationSize()
            SyncSelectedAnnotation()
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

            ' Vier Schreibvorgaenge, ein Handgriff - siehe BeginObjectHistoryGroup.
            BeginObjectHistoryGroup()
            Try
                AnnotationWidthPixels = seite
                AnnotationHeightPixels = seite
                AnnotationXPixels = Math.Max(0, centerX - seite \ 2)
                AnnotationYPixels = Math.Max(0, centerY - seite \ 2)
            Finally
                EndObjectHistoryGroup()
            End Try
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
        ''' <summary>Wo der Zeiger steht, WÄHREND ein Entwurf läuft und keine Taste gedrückt ist -
        ''' das Gummiband. Ohne es sieht man die Form eines Abschnitts erst nach dem Klick, und genau
        ''' das ist der Grund, aus dem das Zeichnen sich blind anfühlte.</summary>
        Private _pathPreviewPoint As SKPoint? = Nothing
        ''' Steht der Zeiger dabei auf dem ERSTEN Punkt? Dann schließt der nächste Klick den Pfad.
        Private _pathPreviewClosesPath As Boolean = False
        ''' Beim Nachziehen: welcher Punkt und welcher Teil von ihm haengt am Zeiger.
        Private _pathDragIndex As Integer = -1
        Private _pathDragPart As String = ""
        Private _pathDragCapturedUndo As Boolean = False
        ''' <summary>War der gezogene Punkt beim ZUGBEGINN glatt? Die Frage muss dort einmal
        ''' beantwortet werden und nicht je Zeigerbewegung: sobald der erste Schritt die Griffe
        ''' bewegt hat, wäre die Antwort eine andere, und ein glatter Punkt verlöre seine Bindung
        ''' mitten im Zug.</summary>
        Private _pathDragWasSmooth As Boolean = False
        ''' Stand des Stützpunktes beim Zugbeginn - Bezug für die Winkelrastung.
        Private _pathDragStartAnchor As SKPoint? = Nothing

        ' ── Die Gruppe als Renderschritt ────────────────────────────────────────
        '
        ' Deckkraft und Mischmethode der GRUPPE stehen bewusst in eigenen Eigenschaften und nicht in
        ' den vielbenutzten Objekt-Eigenschaften: dort haengen Mehrfachauswahl, Weitergabe an alle
        ' markierten Objekte und die Kompositor-Grenze dran, und eine Gruppe ist etwas anderes als
        ' die Summe ihrer Mitglieder.

        ''' <summary>Die Gruppe, deren KOPFZEILE gerade markiert ist - sonst Nothing.</summary>
        Private Function SelectedGroupForProperties() As AnnotationGroup
            If _selectedLayerRow Is Nothing OrElse Not _selectedLayerRow.IsGroupHeader Then Return Nothing
            If _selectedLayerRow.Group Is Nothing Then Return Nothing
            Return FindAnnotationGroup(_selectedLayerRow.Group.Id)
        End Function

        ''' <summary>Zeigt das Panel die Gruppenregler? Nur bei markierter Kopfzeile.</summary>
        Public ReadOnly Property ShowGroupProperties As Boolean
            Get
                Return SelectedGroupForProperties() IsNot Nothing
            End Get
        End Property

        ''' <summary>Ist die markierte Gruppe ein eigener Renderschritt? Der Hinweistext im Panel
        ''' haengt daran: bei Durchgriff verhaelt sie sich wie vorher, und das muss dort stehen.</summary>
        Public ReadOnly Property IsSelectedGroupRenderStep As Boolean
            Get
                Dim g = SelectedGroupForProperties()
                Return g IsNot Nothing AndAlso g.IsRenderStep()
            End Get
        End Property

        Public Property GroupOpacity As Double
            Get
                Dim g = SelectedGroupForProperties()
                Return If(g Is Nothing, 100.0, g.Opacity)
            End Get
            Set(value As Double)
                Dim g = SelectedGroupForProperties()
                If g Is Nothing Then Return
                Dim clamped = Math.Max(0, Math.Min(100, value))
                If Math.Abs(g.Opacity - clamped) < 0.0001 Then Return
                ApplyGroupRenderChange(g, Sub() g.Opacity = clamped)
            End Set
        End Property

        Public Property GroupBlendMode As String
            Get
                Dim g = SelectedGroupForProperties()
                Return If(g Is Nothing, "Normal", NormalizeAnnotationBlendMode(g.BlendMode))
            End Get
            Set(value As String)
                Dim g = SelectedGroupForProperties()
                If g Is Nothing Then Return
                Dim normalized = NormalizeAnnotationBlendMode(value)
                If String.Equals(NormalizeAnnotationBlendMode(g.BlendMode), normalized, StringComparison.Ordinal) Then Return
                ApplyGroupRenderChange(g, Sub() g.BlendMode = normalized)
            End Set
        End Property

        Public ReadOnly Property SelectedGroupBlendModeOption As AnnotationBlendModeOption
            Get
                Dim current = GroupBlendMode
                ' Die Schleife statt FirstOrDefault, und "entry" statt "option": "Option" ist in VB ein
                ' Schlüsselwort und macht aus dem Ausdruck einen Syntaxfehler.
                For Each entry In AnnotationBlendModeOptions
                    If entry IsNot Nothing AndAlso String.Equals(entry.Key, current, StringComparison.Ordinal) Then Return entry
                Next
                Return Nothing
            End Get
        End Property

        ''' <summary>Eine Änderung an der Gruppe schreiben und die Anzeige nachziehen.
        '''
        ''' DER SZENENAUFBAU MUSS NEU: die Mitglieder einer wirksamen Gruppe gehören in den gebackenen
        ''' Block (siehe <c>ComputeCompositorStartIndex</c>), und genau diese Grenze verschiebt sich
        ''' mit dem ersten Schritt weg von hundert Prozent. Ein Blit über die alte Szene zeigte die
        ''' Mitglieder danach doppelt: einmal gebacken, einmal darüber gezeichnet.</summary>
        Private Sub ApplyGroupRenderChange(group As AnnotationGroup, change As Action)
            If group Is Nothing OrElse change Is Nothing Then Return
            PushUndo(LocalizationService.T("Gruppe geändert"))
            change()
            _hasChanges = True
            Me.RaisePropertyChanged(NameOf(GroupOpacity))
            Me.RaisePropertyChanged(NameOf(GroupBlendMode))
            Me.RaisePropertyChanged(NameOf(SelectedGroupBlendModeOption))
            Me.RaisePropertyChanged(NameOf(IsSelectedGroupRenderStep))
            ' Die Kopfleiste des Ebenenpanels zeigt beide Werte - ohne diese Meldung bliebe der
            ' Regler auf dem alten Stand stehen, obwohl die Gruppe schon anders aussieht.
            Me.RaisePropertyChanged(NameOf(SelectedLayerOpacity))
            Me.RaisePropertyChanged(NameOf(SelectedLayerBlendModeOption))
            RaiseResetButtonStateChanged()
            RequestOverlayStateNotify()
            ' DIE SZENE MUSS NEU, nicht nur die Anzeige. Die Mitglieder einer wirksamen Gruppe liegen
            ' im GEBACKENEN Block (siehe ComputeCompositorStartIndex), und der schnelle Weg legt nur
            ' die zwischengespeicherten Objekte über die vorhandene Szene - die Gruppenebene entsteht
            ' dabei gar nicht. Deshalb sah eine geänderte Mischmethode aus, als täte sie nichts
            ' (Nutzerbefund 2026-08-08). Ohne Rechteck heißt: die ganze Szene, denn eine Gruppe kann
            ' überall liegen.
            RefreshOverlayAfterAnnotationChange()
        End Sub

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
            ClearPathNodeSelection()
            _pathDraft.Clear()
            _pathShapingIndex = -1
            _pathDraftTargetId = ""
            _pathPreviewPoint = Nothing
            _pathPreviewClosesPath = False
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
            PushUndo(LocalizationService.T("Pfad geschlossen"))
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
            PushUndo(LocalizationService.T("Pfadpunkt hinzugefügt"))
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
            ' MEHRERE GESAMMELTE PUNKTE auf einmal - von hinten nach vorn, sonst verschieben sich die
            ' Indizes unter der eigenen Schleife weg. Es bleiben immer mindestens zwei stehen: unter
            ' zwei Punkten wäre es kein Pfad mehr.
            If _selectedPathNodes.Count > 1 Then
                Dim gewaehlt = _selectedPathNodes.Where(Function(i) i >= 0 AndAlso i < nodes.Count).
                                                  OrderByDescending(Function(i) i).ToList()
                If nodes.Count - gewaehlt.Count < 2 Then
                    StatusText = LocalizationService.T("Ein Pfad braucht mindestens zwei Punkte.")
                    Return
                End If
                PushUndo(LocalizationService.T("Pfadpunkt entfernt"))
                For Each index In gewaehlt
                    nodes.RemoveAt(index)
                Next
                target.PathPoints = ImageProcessor.FormatPathPoints(nodes)
                ClearPathNodeSelection()
                _lastTouchedPathNode = -1
                _hasChanges = True
                RaisePathOverlayChanged()
                RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(target))
                Return
            End If
            RemovePathNodeAt(If(_lastTouchedPathNode >= 0 AndAlso _lastTouchedPathNode < nodes.Count,
                                _lastTouchedPathNode, nodes.Count - 1))
        End Sub

        ''' <summary>Denselben Weg mit ausdrücklichem Punkt - für Alt-Klick auf den Stützpunkt.</summary>
        Private Sub RemovePathNodeAt(index As Integer)
            Dim target = PathEditTarget()
            If target Is Nothing Then Return
            Dim nodes = ImageProcessor.ParsePathPoints(target.PathPoints)
            If nodes.Count <= 2 Then Return
            If index < 0 OrElse index >= nodes.Count Then Return
            PushUndo(LocalizationService.T("Pfadpunkt entfernt"))
            nodes.RemoveAt(index)
            target.PathPoints = ImageProcessor.FormatPathPoints(nodes)
            ' Die Reihenfolge hat sich geaendert - eine Menge aus Indizes zeigt danach auf andere
            ' Punkte als gemeint.
            ClearPathNodeSelection()
            _lastTouchedPathNode = -1
            _hasChanges = True
            RaisePathOverlayChanged()
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(target))
        End Sub

        ''' <summary>Nimmt im laufenden ENTWURF den zuletzt gesetzten Punkt zurück (Rücktaste). Esc
        ''' verwirft weiterhin den ganzen Entwurf; ohne diesen Schritt dazwischen kostete ein
        ''' verrutschter Punkt die ganze bisherige Arbeit. Der Entwurf liegt außerhalb des
        ''' Rückgängig-Stapels, deshalb steht das hier und nicht dort.</summary>
        Public Sub RemoveLastPathDraftPoint()
            If _pathDraft.Count = 0 Then Return
            _pathDraft.RemoveAt(_pathDraft.Count - 1)
            _pathShapingIndex = -1
            If _pathDraft.Count = 0 Then
                _pathPreviewPoint = Nothing
                _pathPreviewClosesPath = False
            End If
            RaisePathOverlayChanged()
        End Sub

        ''' <summary>Welcher Abschnitt liegt unter dem Zeiger, und an welcher Stelle? Rückgabe -1
        ''' heißt: keiner in Reichweite.
        '''
        ''' Abgetastet wird, statt zu rechnen: die Nullstellen der Abstandsfunktion einer kubischen
        ''' Kurve führen auf eine Gleichung fünften Grades, und für einen Treffertest reicht eine
        ''' Abtastung, die feiner ist als die Greifzone. Gemessen wird in der GREIFZONE als Einheit -
        ''' dieselbe Ellipse wie bei den Stützpunkten, damit ein Treffer an einem schmalen Bild
        ''' nicht plötzlich anders ausfällt.</summary>
        Private Shared Function FindPathSegmentAt(nodes As List(Of ImageProcessor.PathNode),
                                                  closed As Boolean, point As SKPoint,
                                                  slopXPercent As Double, slopYPercent As Double,
                                                  ByRef t As Double) As Integer
            t = 0
            If nodes Is Nothing OrElse nodes.Count < 2 Then Return -1
            Const steps As Integer = 32
            Dim bestSegment = -1
            Dim bestDistance = 1.0
            Dim last = If(closed, nodes.Count - 1, nodes.Count - 2)
            For i = 0 To last
                Dim a = nodes(i)
                Dim b = nodes((i + 1) Mod nodes.Count)
                For s = 1 To steps - 1
                    Dim u = s / CDbl(steps)
                    Dim p = EvaluateCubic(a.Anchor, a.HandleOut, b.HandleIn, b.Anchor, u)
                    Dim dx = (p.X - point.X) / Math.Max(0.0001, slopXPercent)
                    Dim dy = (p.Y - point.Y) / Math.Max(0.0001, slopYPercent)
                    Dim d = dx * dx + dy * dy
                    If d < bestDistance Then
                        bestDistance = d
                        bestSegment = i
                        t = u
                    End If
                Next
            Next
            Return bestSegment
        End Function

        Private Shared Function EvaluateCubic(p0 As SKPoint, p1 As SKPoint, p2 As SKPoint,
                                              p3 As SKPoint, t As Double) As SKPoint
            Dim m = 1.0 - t
            Dim a = m * m * m, b = 3 * m * m * t, c = 3 * m * t * t, d = t * t * t
            Return New SKPoint(CSng(a * p0.X + b * p1.X + c * p2.X + d * p3.X),
                               CSng(a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y))
        End Function

        Private Shared Function LerpPoint(a As SKPoint, b As SKPoint, t As Double) As SKPoint
            Return New SKPoint(CSng(a.X + (b.X - a.X) * t), CSng(a.Y + (b.Y - a.Y) * t))
        End Function

        ''' <summary>Teilt einen Abschnitt an der Stelle <paramref name="t"/> und setzt dort einen
        ''' Stützpunkt. Die FORM bleibt dabei exakt erhalten: die Teilung nach de Casteljau liefert
        ''' zwei Kurven, die zusammen dieselbe Linie ergeben wie die eine vorher. Genau das
        ''' unterscheidet den Klick auf die Kurve vom Knopf im Panel, der einen Punkt auf die
        ''' Sehnenmitte setzt und die Kurve dabei verzieht.
        ''' Rückgabe: der Index des neuen Punktes, oder -1.</summary>
        Private Function SplitPathSegmentAt(target As ImageAnnotation,
                                            nodes As List(Of ImageProcessor.PathNode),
                                            segment As Integer, t As Double) As Integer
            If target Is Nothing OrElse nodes Is Nothing OrElse segment < 0 OrElse nodes.Count < 2 Then Return -1
            Dim nextIndex = (segment + 1) Mod nodes.Count
            Dim a = nodes(segment)
            Dim b = nodes(nextIndex)

            Dim p01 = LerpPoint(a.Anchor, a.HandleOut, t)
            Dim p12 = LerpPoint(a.HandleOut, b.HandleIn, t)
            Dim p23 = LerpPoint(b.HandleIn, b.Anchor, t)
            Dim p012 = LerpPoint(p01, p12, t)
            Dim p123 = LerpPoint(p12, p23, t)
            Dim anchor = LerpPoint(p012, p123, t)

            PushUndo(LocalizationService.T("Pfadpunkt hinzugefügt"))
            a.HandleOut = p01
            b.HandleIn = p23
            nodes(segment) = a
            nodes(nextIndex) = b
            Dim inserted = New ImageProcessor.PathNode With {
                .Anchor = anchor, .HandleIn = p012, .HandleOut = p123}
            ' Beim GESCHLOSSENEN Pfad kann der Abschnitt der letzte sein, der zum ersten Punkt
            ' zurückläuft. Der neue Punkt gehört dann ans Ende, nicht an den Anfang.
            Dim insertAt = segment + 1
            nodes.Insert(insertAt, inserted)
            ClearPathNodeSelection()
            WritePathNodesFromDisplay(target, nodes)
            _lastTouchedPathNode = insertAt
            _hasChanges = True
            RaisePathOverlayChanged()
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(target))
            Return insertAt
        End Function

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
            PushUndo(LocalizationService.T("Pfadpunkt geglättet"))
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

        ''' <summary>MEHRERE Stützpunkte auf einmal: mit Umschalt angeklickte Punkte sammeln sich
        ''' hier, ein Zug an einem von ihnen bewegt alle, und Entfernen nimmt alle weg.
        '''
        ''' Die Menge steht über INDIZES, und deshalb wird sie geleert, sobald sich die Reihenfolge
        ''' ändern kann (Punkt eingefügt oder entfernt, anderes Objekt markiert). Sie mitzuschieben
        ''' wäre möglich, aber jede Stelle, die es vergisst, verschöbe stillschweigend die falschen
        ''' Punkte - und das sieht man erst am Bild.</summary>
        Private ReadOnly _selectedPathNodes As New HashSet(Of Integer)()

        ''' <summary>Die Menge leeren. Steht als eigene Stelle da, damit jeder Aufrufer denselben
        ''' Weg nimmt.</summary>
        Private Sub ClearPathNodeSelection()
            If _selectedPathNodes.Count = 0 Then Return
            _selectedPathNodes.Clear()
        End Sub

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
        ''' oder ein Textobjekt bzw. Text-Wasserzeichen, dessen Grundlinie ein freier Pfad ist. NUR im Pfad-Werkzeug: sonst
        ''' laegen die Stuetzpunkte unter dem Auswahlrahmen des Objekts, und ein Zug meinte je nach
        ''' getroffenem Pixel das eine oder das andere - dieselbe Entscheidung wie beim Verzerren,
        ''' wo die Ecken den Rahmen verdraengen.
        '''
        ''' GEDREHTE OBJEKTE ZAEHLEN SEIT DEM 2026-08-08 MIT. Bis dahin waren sie ausgenommen, weil
        ''' das Anzeigerechteck die unrotierte Huelle ist und die Punkte beim Ziehen danebenlagen.
        ''' Jetzt drehen Lesen und Schreiben die Punkte um die Rechteckmitte mit (siehe
        ''' <c>RotateDisplayPercent</c>), und das Nachziehen der Grenzen rechnet im UNrotierten Raum.</summary>
        Private Function PathEditTarget() As ImageAnnotation
            If _currentTool <> EditorTool.Path Then Return Nothing
            Dim a = CurrentObject()
            If a Is Nothing Then Return Nothing
            Dim kind = NormalizeAnnotationKind(a.Kind)
            ' Ein Wasserzeichen mit Text wird vom Renderer wie ein Textobjekt gezeichnet. Es muss
            ' deshalb auch hier als freie Grundlinie gelten; zuvor konnten dessen Punkte nach dem
            ' Setzen weder angezeigt noch bearbeitet werden.
            Dim isTextBearingKind = String.Equals(kind, "Text", StringComparison.Ordinal) OrElse
                                    (String.Equals(kind, "Watermark", StringComparison.Ordinal) AndAlso
                                     String.IsNullOrWhiteSpace(a.ImagePath))
            Dim isTextOnFreePath = isTextBearingKind AndAlso IsFreeTextPath(a.TextPathKind)
            If Not String.Equals(kind, "Path", StringComparison.Ordinal) AndAlso Not isTextOnFreePath Then Return Nothing
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

        ''' <summary>Wandelt den markierten Pfad in ein Textobjekt um. Die Punkte werden zur
        ''' editierbaren Grundlinie des Textes; eine zweite, gleich aussehende Pfadebene bleibt
        ''' nicht zurueck. Punkte und Box werden dabei kopiert, nicht geteilt: zwei Objekte auf
        ''' derselben Punktliste zögen einander die Grundlinie weg, sobald eines skaliert wird.</summary>
        Public Sub CreateTextOnSelectedPath()
            Dim source = CurrentObject()
            If source Is Nothing OrElse String.IsNullOrWhiteSpace(source.PathPoints) Then Return
            If Not String.Equals(NormalizeAnnotationKind(source.Kind), "Path", StringComparison.Ordinal) Then Return
            PushUndo()
            ' Die Identität und Ebenen-Zugehörigkeit gehören zur umgewandelten Ebene,
            ' nicht zum alten Pfadtyp. So bleiben Gruppen, Masken und Verweise von
            ' Korrekturebenen intakt.
            Dim text = New ImageAnnotation With {
                .Kind = "Text",
                .Text = LocalizationService.T("Text"),
                .Id = source.Id,
                .CustomName = source.CustomName,
                .GroupId = source.GroupId,
                .MaskId = source.MaskId,
                .ClipToLayerBelow = source.ClipToLayerBelow,
                .XPixels = source.XPixels, .YPixels = source.YPixels,
                .WidthPixels = source.WidthPixels, .HeightPixels = source.HeightPixels,
                .RotationDegrees = source.RotationDegrees,
                .TextPathKind = "Free",
                .PathPoints = source.PathPoints,
                .PathClosed = source.PathClosed,
                .IsVisible = source.IsVisible,
                .IsLocked = source.IsLocked
            }
            Dim index = _annotations.IndexOf(source)
            If index < 0 Then Return
            ' "Text auf dem Pfad" ist eine Umwandlung, keine Kopie. Das Textobjekt übernimmt
            ' die Grundlinie und steht an exakt derselben Stelle in der Ebenenliste.
            _annotations(index) = text
            _hasChanges = True
            ' Die Ebene blieb am SELBEN Index. Ein direktes `SelectedAnnotationIndex = index`
            ' waere damit ein No-op und liesse den Editor-Puffer beim alten Pfad stehen. Noch
            ' schlimmer: der anschliessende Wechsel ins Textwerkzeug wuerde diese vermeintlich
            ' unveraenderte Auswahl nach der allgemeinen Werkzeugregel abwaehlen. Erst loesen,
            ' dann ins Textwerkzeug und DANACH die neue Textebene waehlen: so laedt der Puffer
            ' Text, TextPathKind="Free" und PathPoints gemeinsam und das Textfeld bleibt aktiv.
            SelectedAnnotationIndex = -1
            If _currentTool <> EditorTool.Text Then CurrentTool = EditorTool.Text
            SelectedAnnotationIndex = index
            RaisePathOverlayChanged()
            RebuildLayerRows()
            RaiseResetButtonStateChanged()
            NameHistoryStep(LocalizationService.T("Text auf den Pfad gesetzt"))
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(text))
        End Sub

        Public ReadOnly Property CanEditPathNodes As Boolean
            Get
                Return _pathDraft.Count = 0 AndAlso PathEditTarget() IsNot Nothing
            End Get
        End Property

        ''' <summary>Die zwei markierten OFFENEN Pfade, die sich verbinden lassen - sonst Nothing.
        ''' Geschlossene bleiben außen vor: sie haben kein Ende, an das etwas anschließen könnte.</summary>
        Private Function PathPairToJoin() As (First As ImageAnnotation, Second As ImageAnnotation)?
            If _pathDraft.Count > 0 Then Return Nothing
            If _currentTool <> EditorTool.Path Then Return Nothing
            Dim selected = SelectedAnnotations
            If selected Is Nothing OrElse selected.Count <> 2 Then Return Nothing
            For Each a In selected
                If a Is Nothing OrElse a.PathClosed Then Return Nothing
                If Not String.Equals(NormalizeAnnotationKind(a.Kind), "Path", StringComparison.Ordinal) Then Return Nothing
                If ImageProcessor.ParsePathPoints(a.PathPoints).Count < 2 Then Return Nothing
            Next
            Return (selected(0), selected(1))
        End Function

        Public ReadOnly Property CanJoinPaths As Boolean
            Get
                Return PathPairToJoin().HasValue
            End Get
        End Property

        ''' <summary>ZWEI PFADE ZU EINEM. Verbunden werden die beiden Enden, die einander am nächsten
        ''' liegen - das ist fast immer das Gemeinte, und die Alternative wäre, den Nutzer nach vier
        ''' Möglichkeiten zu fragen. Wo nötig, wird eine Punktliste dafür umgedreht; dabei tauschen
        ''' die Griffe die Seiten, sonst klappt die Krümmung jedes Punktes um.
        '''
        ''' Der ZWEITE Pfad verschwindet, seine Punkte leben im ersten weiter. Farbe, Kontur und alles
        ''' andere behält der erste: von zwei Sätzen Eigenschaften kann nur einer bleiben, und der des
        ''' angeklickten Ankers ist die naheliegende Wahl.</summary>
        Public Sub JoinSelectedPaths()
            Dim pair = PathPairToJoin()
            If Not pair.HasValue Then Return
            Dim first = pair.Value.First, second = pair.Value.Second
            Dim firstNodes = ReadPathNodesInRectFor(first)
            Dim secondNodes = ReadPathNodesInRectFor(second)
            If firstNodes.Count < 2 OrElse secondNodes.Count < 2 Then Return

            ' Vier mögliche Verbindungen, gemessen am Abstand der beteiligten Enden.
            Dim aStart = firstNodes(0).Anchor, aEnd = firstNodes(firstNodes.Count - 1).Anchor
            Dim bStart = secondNodes(0).Anchor, bEnd = secondNodes(secondNodes.Count - 1).Anchor
            Dim distance = Function(p As SKPoint, q As SKPoint) As Double
                              Dim dx = CDbl(p.X - q.X), dy = CDbl(p.Y - q.Y)
                              Return dx * dx + dy * dy
                          End Function
            Dim candidates = {(distance(aEnd, bStart), False, False),
                          (distance(aEnd, bEnd), False, True),
                          (distance(aStart, bStart), True, False),
                          (distance(aStart, bEnd), True, True)}
            Dim best = candidates(0)
            For Each fall In candidates
                If fall.Item1 < best.Item1 Then best = fall
            Next

            Dim firstRun = If(best.Item2, ReversePathNodes(firstNodes), firstNodes)
            Dim secondRun = If(best.Item3, ReversePathNodes(secondNodes), secondNodes)

            PushUndo()
            Dim joined = New List(Of ImageProcessor.PathNode)(firstRun)
            joined.AddRange(secondRun)
            ' Bezug ist das Rechteck DES ERSTEN; die Grenzen zieht RefitPathBoundsToPoints gleich
            ' danach auf die vereinte Punktmenge nach.
            WritePathNodesInRect(first, joined, StoredAnnotationRectToDisplayPercent(first),
                                 StoredAnnotationRotationToDisplay(first))
            _annotations.Remove(second)
            _extraSelectedAnnotations.Clear()
            SelectedAnnotationIndex = _annotations.IndexOf(first)
            RefitPathBoundsToPoints()
            _hasChanges = True
            ClearPathNodeSelection()
            RaisePathOverlayChanged()
            RebuildLayerRows()
            RaiseResetButtonStateChanged()
            StatusText = LocalizationService.T("Pfade verbunden")
            NameHistoryStep(LocalizationService.T("Pfade verbunden"))
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(first))
        End Sub

        ''' <summary>Die Punkte EINES Objekts in Anzeige-Prozent - auch wenn es nicht das markierte
        ''' ist. <c>ReadPathNodesForDisplay</c> nimmt dafür das Rechteck der MARKIERUNG, und beim
        ''' Verbinden sind zwei Objekte im Spiel.</summary>
        Private Function ReadPathNodesInRectFor(annotation As ImageAnnotation) As List(Of ImageProcessor.PathNode)
            Dim nodes = ImageProcessor.ParsePathPoints(annotation.PathPoints)
            Dim r = StoredAnnotationRectToDisplayPercent(annotation)
            If r.Width <= 0 OrElse r.Height <= 0 Then Return New List(Of ImageProcessor.PathNode)()
            Dim rotation = StoredAnnotationRotationToDisplay(annotation)
            Dim cx = r.X + r.Width / 2.0, cy = r.Y + r.Height / 2.0
            Dim toDisplay = Function(p As SKPoint) RotateDisplayPercent(
                New SKPoint(CSng(r.X + p.X / 100.0 * r.Width), CSng(r.Y + p.Y / 100.0 * r.Height)),
                cx, cy, rotation)
            Return nodes.Select(Function(n) New ImageProcessor.PathNode With {
                .Anchor = toDisplay(n.Anchor), .HandleIn = toDisplay(n.HandleIn), .HandleOut = toDisplay(n.HandleOut)}).ToList()
        End Function

        ''' <summary>Punktliste umdrehen. Die Griffe TAUSCHEN dabei die Seiten: was vorher in einen
        ''' Punkt hineinlief, läuft danach aus ihm heraus.</summary>
        Private Shared Function ReversePathNodes(nodes As List(Of ImageProcessor.PathNode)) As List(Of ImageProcessor.PathNode)
            Return nodes.AsEnumerable().Reverse().Select(
                Function(n) New ImageProcessor.PathNode With {
                    .Anchor = n.Anchor, .HandleIn = n.HandleOut, .HandleOut = n.HandleIn}).ToList()
        End Function

        ''' <summary>Lässt sich aus dem markierten Pfad eine Auswahl machen? Eine freie
        ''' Text-Grundlinie bleibt außen vor: sie ist die Linie, auf der Buchstaben sitzen, und die
        ''' Fläche darum herum meint niemand.</summary>
        Public ReadOnly Property CanCreateSelectionFromPath As Boolean
            Get
                If _pathDraft.Count > 0 Then Return False
                Dim a = PathEditTarget()
                Return a IsNot Nothing AndAlso
                       String.Equals(NormalizeAnnotationKind(a.Kind), "Path", StringComparison.Ordinal) AndAlso
                       ImageProcessor.ParsePathPoints(a.PathPoints).Count >= 2
            End Get
        End Property

        ''' <summary>Berechnet das geschlossene Polygon eines Pfades für Auswahl und Maskenebene.
        ''' Beide sind pixelbasierte Schnappschüsse; der Pfad selbst bleibt editierbare Geometrie.</summary>
        Private Function TryBuildSelectedPathPolygon(ByRef xs As Double(), ByRef ys As Double()) As Boolean
            xs = Nothing : ys = Nothing
            Dim target = PathEditTarget()
            If target Is Nothing OrElse Not String.Equals(NormalizeAnnotationKind(target.Kind), "Path", StringComparison.Ordinal) Then Return False
            Dim nodes = ReadPathNodesForDisplay(target)
            If nodes.Count < 2 Then Return False

            Const steps As Integer = 16
            Dim xValues As New List(Of Double)()
            Dim yValues As New List(Of Double)()
            Dim last = If(target.PathClosed, nodes.Count - 1, nodes.Count - 2)
            For i = 0 To last
                Dim a = nodes(i)
                Dim b = nodes((i + 1) Mod nodes.Count)
                For s = 0 To steps - 1
                    Dim p = EvaluateCubic(a.Anchor, a.HandleOut, b.HandleIn, b.Anchor, s / CDbl(steps))
                    xValues.Add(p.X) : yValues.Add(p.Y)
                Next
            Next
            If Not target.PathClosed Then
                xValues.Add(nodes(nodes.Count - 1).Anchor.X)
                yValues.Add(nodes(nodes.Count - 1).Anchor.Y)
            End If
            If xValues.Count < 3 Then Return False
            xs = xValues.ToArray() : ys = yValues.ToArray()
            Return True
        End Function

        ''' <summary>DER EIGENTLICHE NUTZEN DES WERKZEUGS: die Kurve wird zur Auswahl.
        '''
        ''' Damit ist ein Freisteller nachträglich korrigierbar - man zieht einen Stützpunkt und macht
        ''' die Auswahl neu, statt mit dem Pinsel nachzubessern. Lasso, Zauberstab und Motivklick
        ''' liefern alle Pixel, die sich nicht mehr befragen lassen; ein Pfad bleibt Geometrie.
        '''
        ''' Gebaut wird sie über den LASSO-Weg: die Kurve wird in ein Vieleck abgetastet und geht dann
        ''' durch dieselbe Maschinerie wie eine Freihandauswahl. Damit gelten Verknüpfungsmodus,
        ''' weiche Kante, Rückgängig und Ameisenlinie ohne eine einzige neue Zeile. Ein OFFENER Pfad
        ''' wird dabei durch eine gerade Linie geschlossen - eine Auswahl ohne geschlossenen Rand gibt
        ''' es nicht, und der Standard macht es genauso.</summary>
        Public Sub CreateSelectionFromSelectedPath()
            Dim xs As Double() = Nothing, ys As Double() = Nothing
            If Not TryBuildSelectedPathPolygon(xs, ys) Then Return
            SetSelectionLasso(xs, ys)

            ' DANACH GEHÖRT DIE BÜHNE DER AUSWAHL, NICHT DEM PFAD - dieselbe Regel wie bei der Form
            ' einer Ebene als Auswahl (siehe LoadSelectionFromAnnotationAlpha), und aus denselben
            ' Gründen: der Rahmen der markierten Ebene läge über der frisch geholten Ameisenlinie,
            ' ein Zug darin verschöbe den Pfad statt der Auswahl - und die Entf-Taste meinte das
            ' markierte OBJEKT statt des Auswahlinhalts (Nutzerbefund 2026-08-09: "per Entf kann ich
            ' den Inhalt nicht entfernen").
            '
            ' Zum Nachbessern holt man den Pfad im Pfad-Werkzeug mit einem Klick zurück; die Auswahl
            ' entsteht mit demselben Knopf neu. Erst die Auswahl, dann das Werkzeug: der Wechsel ins
            ' Auswahl-Werkzeug setzt den Verknüpfungsmodus zurück, und der galt noch für diesen Zug.
            CurrentTool = EditorTool.Selection
            SelectionMode = "Move"
            SelectedAnnotationIndex = -1
            StatusText = LocalizationService.T("Auswahl aus dem Pfad erstellt")
            NameHistoryStep(LocalizationService.T("Auswahl aus dem Pfad erstellt"))
        End Sub

        ''' <summary>Erzeugt direkt eine neue, unabhängige Maskenebene aus dem markierten Pfad.
        ''' Der Pfad bleibt als editierbare Vorlage erhalten und kann nach einer Änderung erneut
        ''' in eine Maskenebene überführt werden.</summary>
        ''' <summary>Erzeugt die Auswahl aus dem Pfad und friert sie dann wie jede andere
        ''' bildgrosse Auswahl im Hintergrund als Ebenenmaske ein.</summary>
        Private Async Function CreateMaskLayerFromSelectedPathAsync() As Task
            Dim xs As Double() = Nothing, ys As Double() = Nothing
            If Not TryBuildSelectedPathPolygon(xs, ys) Then Return

            ' SetSelectionLasso kann bei einer ausserhalb liegenden oder flachen Kontur ohne
            ' Auswahl zurückkehren. Erst dieselben billigen Vorbedingungen pruefen, DANN den
            ' Undo-Schnappschuss anlegen - sonst blieb ein leerer Verlaufsschritt zurueck.
            Dim minX = xs.Min(), maxX = xs.Max(), minY = ys.Min(), maxY = ys.Max()
            If (maxX - minX) < 0.5 OrElse (maxY - minY) < 0.5 Then Return
            Dim selectionSize = GetAnnotationDisplayPixelSize()
            If selectionSize.Width <= 0 OrElse selectionSize.Height <= 0 Then Return
            Dim selectionRect = New SKRectI(
                Math.Max(0, CInt(Math.Round(selectionSize.Width * minX / 100.0))),
                Math.Max(0, CInt(Math.Round(selectionSize.Height * minY / 100.0))),
                Math.Min(selectionSize.Width, CInt(Math.Round(selectionSize.Width * maxX / 100.0))),
                Math.Min(selectionSize.Height, CInt(Math.Round(selectionSize.Height * maxY / 100.0))))
            If selectionRect.Width <= 0 OrElse selectionRect.Height <= 0 Then Return

            PushUndo(LocalizationService.T("Maskenebene aus Pfad erstellt"))
            SetSelectionLasso(xs, ys, captureUndo:=False)
            If Not _hasActiveSelection Then Return
            SetActiveSelectionIsMask(True)
            ' NUR die zurueckgegebene Ebene zaehlt. Der Hintergrundlauf kann fehlschlagen, und ein
            ' Bild- oder Auswahlwechsel waehrenddessen verwirft sein Ergebnis; die zuletzt MARKIERTE
            ' Ebene ist dann noch die von vorher. Wer die nimmt, oeffnet eine fremde Maske und
            ' schreibt einen Erfolg in den Verlauf, den es nicht gab.
            Dim layer = Await CreateAdjustmentLayerFromSelectionAsync(captureUndo:=False)
            If layer Is Nothing OrElse String.IsNullOrEmpty(layer.MaskId) Then Return
            If _currentTool <> EditorTool.Mask Then CurrentTool = EditorTool.Mask
            MaskMode = "Brush"
            LoadMaskIntoSelection(layer.MaskId, showAsMask:=True)
            StatusText = LocalizationService.T("Maskenebene aus Pfad erstellt")
            NameHistoryStep(LocalizationService.T("Maskenebene aus Pfad erstellt"))
        End Function

        ''' <summary>Dreht einen Punkt in ANZEIGE-Prozent um einen Mittelpunkt.
        '''
        ''' GERECHNET WIRD IN ANZEIGEPUNKTEN: Prozent der Breite und Prozent der Höhe sind bei einem
        ''' nicht quadratischen Bild verschieden lang, eine Drehung darin wäre eine Scherung. Der
        ''' Renderer dreht aus demselben Grund in Leinwandpunkten.</summary>
        Private Function RotateDisplayPercent(p As SKPoint, centerX As Double, centerY As Double,
                                              degrees As Double) As SKPoint
            If Math.Abs(degrees) < 0.001 Then Return p
            Dim size = GetAnnotationDisplayPixelSize()
            Dim sx = If(size.Width > 0, CDbl(size.Width), 100.0)
            Dim sy = If(size.Height > 0, CDbl(size.Height), 100.0)
            Dim dx = (p.X - centerX) / 100.0 * sx
            Dim dy = (p.Y - centerY) / 100.0 * sy
            Dim radians = degrees * Math.PI / 180.0
            Dim nx = dx * Math.Cos(radians) - dy * Math.Sin(radians)
            Dim ny = dx * Math.Sin(radians) + dy * Math.Cos(radians)
            Return New SKPoint(CSng(centerX + nx / sx * 100.0), CSng(centerY + ny / sy * 100.0))
        End Function

        ''' <summary>Die Punkte des markierten Pfades in ANZEIGE-Prozent, mit der Drehung des Objekts.
        '''
        ''' Das Rechteck ist die UNROTIERTE Hülle; die Punkte liegen darin und werden anschließend um
        ''' seine Mitte gedreht - dieselbe Reihenfolge wie im Renderer. Ohne den zweiten Schritt lagen
        ''' die Stützpunkte eines gedrehten Pfades neben ihrer Kurve, und deshalb war das Nachziehen
        ''' dort früher ganz gesperrt.</summary>
        Private Function ReadPathNodesForDisplay(annotation As ImageAnnotation) As List(Of ImageProcessor.PathNode)
            Dim nodes = ImageProcessor.ParsePathPoints(annotation.PathPoints)
            Dim r = GetSelectedAnnotationDisplayRectPercent()
            If r.Width <= 0 OrElse r.Height <= 0 Then Return New List(Of ImageProcessor.PathNode)()
            Dim rotation = StoredAnnotationRotationToDisplay(annotation)
            Dim cx = r.X + r.Width / 2.0, cy = r.Y + r.Height / 2.0
            Dim toDisplay = Function(p As SKPoint) RotateDisplayPercent(
                New SKPoint(CSng(r.X + p.X / 100.0 * r.Width), CSng(r.Y + p.Y / 100.0 * r.Height)),
                cx, cy, rotation)
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
            WritePathNodesInRect(annotation, nodes, GetSelectedAnnotationDisplayRectPercent(),
                                 StoredAnnotationRotationToDisplay(annotation))
        End Sub

        ''' <summary>Dieselbe Umrechnung mit ausdruecklich uebergebenem Bezugsrechteck in
        ''' Anzeige-Prozent. <paramref name="rotationDegrees"/> ist die Drehung des Objekts: sie wird
        ''' HERAUSgerechnet, bevor die Punkte auf das Rechteck bezogen werden - genau die
        ''' Gegenrichtung zu <c>ReadPathNodesForDisplay</c>.</summary>
        Private Sub WritePathNodesInRect(annotation As ImageAnnotation,
                                         nodes As List(Of ImageProcessor.PathNode),
                                         r As (X As Double, Y As Double, Width As Double, Height As Double),
                                         Optional rotationDegrees As Double = 0.0)
            If annotation Is Nothing OrElse nodes Is Nothing Then Return
            If r.Width <= 0 OrElse r.Height <= 0 Then Return
            Dim cx = r.X + r.Width / 2.0, cy = r.Y + r.Height / 2.0
            Dim toObject = Function(p As SKPoint) As SKPoint
                               Dim u = RotateDisplayPercent(p, cx, cy, -rotationDegrees)
                               Return New SKPoint(CSng((u.X - r.X) / r.Width * 100.0),
                                                  CSng((u.Y - r.Y) / r.Height * 100.0))
                           End Function
            annotation.PathPoints = ImageProcessor.FormatPathPoints(
                nodes.Select(Function(n) New ImageProcessor.PathNode With {
                    .Anchor = toObject(n.Anchor), .HandleIn = toObject(n.HandleIn), .HandleOut = toObject(n.HandleOut)}))
        End Sub

        ''' <summary>Was das Overlay zeichnet: Anzahl, geschlossen-Kennzeichen, Entwurf-Kennzeichen,
        ''' dann je Punkt sechs Zahlen in ANZEIGE-Prozent. Nothing = nichts zu zeichnen.
        '''
        ''' HINTEN ANGEHÄNGT stehen fünf weitere Zahlen: ob eine Gummiband-Vorschau anliegt, wo sie
        ''' hinzeigt, ob der nächste Klick dort den Pfad SCHLIESSEN würde, und welcher Punkt zuletzt
        ''' angefasst wurde (-1 = keiner). Angehängt und nicht dazwischengeschoben, damit ein
        ''' Overlay, das sie nicht kennt, weiterhin das Richtige zeichnet.</summary>
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
                Dim values(nodes.Count * 6 + 7) As Double
                values(0) = nodes.Count
                values(1) = If(closed, 1.0, 0.0)
                values(2) = If(_pathDraft.Count > 0, 1.0, 0.0)
                For i = 0 To nodes.Count - 1
                    Dim o = 3 + i * 6
                    values(o) = nodes(i).Anchor.X : values(o + 1) = nodes(i).Anchor.Y
                    values(o + 2) = nodes(i).HandleIn.X : values(o + 3) = nodes(i).HandleIn.Y
                    values(o + 4) = nodes(i).HandleOut.X : values(o + 5) = nodes(i).HandleOut.Y
                Next
                Dim tail = 3 + nodes.Count * 6
                Dim preview = If(_pathDraft.Count > 0, _pathPreviewPoint, Nothing)
                values(tail) = If(preview.HasValue, 1.0, 0.0)
                values(tail + 1) = If(preview.HasValue, preview.Value.X, 0.0)
                values(tail + 2) = If(preview.HasValue, preview.Value.Y, 0.0)
                values(tail + 3) = If(preview.HasValue AndAlso _pathPreviewClosesPath, 1.0, 0.0)
                values(tail + 4) = If(_pathDraft.Count > 0, -1.0, CDbl(_lastTouchedPathNode))
                ' Die mit Umschalt gesammelten Punkte hängen GANZ hinten und in variabler Zahl: erst
                ' wie viele, dann ihre Indizes. So bleibt der feste Teil davor unverändert.
                If _pathDraft.Count = 0 AndAlso _selectedPathNodes.Count > 0 Then
                    Dim gewaehlt = _selectedPathNodes.Where(Function(i) i >= 0 AndAlso i < nodes.Count).OrderBy(Function(i) i).ToList()
                    If gewaehlt.Count > 0 Then
                        Dim erweitert(values.Length + gewaehlt.Count) As Double
                        Array.Copy(values, erweitert, values.Length)
                        erweitert(values.Length) = gewaehlt.Count
                        For i = 0 To gewaehlt.Count - 1
                            erweitert(values.Length + 1 + i) = gewaehlt(i)
                        Next
                        Return erweitert
                    End If
                End If
                Return values
            End Get
        End Property

        ''' <summary>Das Gummiband nachführen: der Zeiger bewegt sich, ohne dass eine Taste gedrückt
        ''' ist. Nur während eines Entwurfs, und bewusst OHNE die große Meldekaskade - das läuft je
        ''' Zeigerbewegung, und die Ansicht holt sich die Werte ohnehin selbst ab.</summary>
        Public Sub UpdatePathHoverPoint(xPercent As Double, yPercent As Double,
                                        slopXPercent As Double, slopYPercent As Double)
            If _pathDraft.Count = 0 Then
                ClearPathHoverPoint()
                Return
            End If
            Dim point = New SKPoint(CSng(xPercent), CSng(yPercent))
            _pathPreviewPoint = point
            _pathPreviewClosesPath = _pathDraft.Count >= 2 AndAlso
                                     IsNearPathPoint(_pathDraft(0).Anchor, point, slopXPercent, slopYPercent)
            Me.RaisePropertyChanged(NameOf(PathOverlayValues))
        End Sub

        ''' <summary>Zeiger weg vom Bild oder Entwurf vorbei: das Gummiband verschwindet. Ein
        ''' stehengebliebenes zeigte auf eine Stelle, an der der Zeiger nicht mehr ist.</summary>
        Public Sub ClearPathHoverPoint()
            If Not _pathPreviewPoint.HasValue AndAlso Not _pathPreviewClosesPath Then Return
            _pathPreviewPoint = Nothing
            _pathPreviewClosesPath = False
            Me.RaisePropertyChanged(NameOf(PathOverlayValues))
        End Sub

        Private Sub RaisePathOverlayChanged()
            Me.RaisePropertyChanged(NameOf(PathOverlayValues))
            Me.RaisePropertyChanged(NameOf(HasPathDraft))
            Me.RaisePropertyChanged(NameOf(IsPathPenActive))
            Me.RaisePropertyChanged(NameOf(CanEditPathNodes))
            Me.RaisePropertyChanged(NameOf(HidesSelectionFrameForPath))
            Me.RaisePropertyChanged(NameOf(CanCreateTextOnPath))
            Me.RaisePropertyChanged(NameOf(CanCreateSelectionFromPath))
            Me.RaisePropertyChanged(NameOf(CanJoinPaths))
            Me.RaisePropertyChanged(NameOf(PathNodeCount))
            Me.RaisePropertyChanged(NameOf(IsSelectedPathClosed))
            ' Der Auswahlrahmen legt seine Griffe ab, solange die Punkte gemeint sind - sonst laegen
            ' beide auf demselben Rechteck.
            Me.RaisePropertyChanged(NameOf(ShowsObjectFrameHandles))
            RequestOverlayStateNotify()
        End Sub

        ''' <summary>Ein Druck auf die Buehne, solange Pfade gemeint sind. True heisst: verarbeitet.
        '''
        ''' <paramref name="snapAngle"/> (Umschalt) setzt den neuen Punkt in einer Achtelkreis-Richtung
        ''' vom vorigen aus. <paramref name="removeNode"/> (Alt) entfernt einen getroffenen
        ''' Stützpunkt, statt ihn zu ziehen - der Standardweg, einen Punkt loszuwerden, ohne den Blick
        ''' ins Panel.</summary>
        Public Function TryBeginPathPointer(xPercent As Double, yPercent As Double,
                                            slopXPercent As Double, slopYPercent As Double,
                                            Optional snapAngle As Boolean = False,
                                            Optional removeNode As Boolean = False,
                                            Optional resumeAtEnd As Boolean = False,
                                            Optional addToSelection As Boolean = False) As Boolean
            Dim point = New SKPoint(CSng(xPercent), CSng(yPercent))

            ' 1) Ein laufender Entwurf: auf den ersten Punkt geklickt heisst SCHLIESSEN.
            If _pathDraft.Count > 0 Then
                If _pathDraft.Count >= 2 AndAlso IsNearPathPoint(_pathDraft(0).Anchor, point, slopXPercent, slopYPercent) Then
                    FinishPathDraft(keep:=True, closed:=True)
                    Return True
                End If
                Dim placed = If(snapAngle, SnapToEighthCircle(_pathDraft(_pathDraft.Count - 1).Anchor, point), point)
                _pathDraft.Add(ImageProcessor.PathNode.Corner(placed.X, placed.Y))
                _pathShapingIndex = _pathDraft.Count - 1
                RaisePathOverlayChanged()
                Return True
            End If

            ' 2) Punkte eines fertigen Pfades nachziehen.
            Dim target = PathEditTarget()
            If target IsNot Nothing Then
                Dim nodes = ReadPathNodesForDisplay(target)

                ' WAS AM NAECHSTEN LIEGT, GEWINNT - und bei gleichem Abstand der STUETZPUNKT.
                '
                ' Vorher hatten die Griffe unbedingten Vorrang, und zwar in der Reihenfolge der
                ' Punktliste: wer einen Stuetzpunkt anklickte, dessen Griffe kurz sind (also bei
                ' jedem herausgezoomten Bild), zog den Griff und verbog die Kurve, statt den Punkt zu
                ' verschieben (Nutzerbefund 2026-08-08: "man kann keinen verschieben"). Der Abstand
                ' entscheidet das ohne Sonderfall, und der Stuetzpunkt ist der wichtigere Anfasser.
                Dim bestIndex = -1
                Dim bestPart = ""
                Dim bestDistance = Double.MaxValue
                For i = 0 To nodes.Count - 1
                    Dim anchorDistance = PathPointDistance(nodes(i).Anchor, point, slopXPercent, slopYPercent)
                    If anchorDistance <= 1.0 AndAlso anchorDistance < bestDistance Then
                        bestDistance = anchorDistance : bestIndex = i : bestPart = "anchor"
                    End If
                    ' Ein Griff zaehlt nur, wenn er sichtbar absteht: bei einer Ecke liegt er AUF dem
                    ' Stuetzpunkt, und dann ist der Stuetzpunkt gemeint.
                    If Not IsSamePathPoint(nodes(i).HandleOut, nodes(i).Anchor) Then
                        Dim d = PathPointDistance(nodes(i).HandleOut, point, slopXPercent, slopYPercent)
                        If d <= 1.0 AndAlso d < bestDistance Then
                            bestDistance = d : bestIndex = i : bestPart = "out"
                        End If
                    End If
                    If Not IsSamePathPoint(nodes(i).HandleIn, nodes(i).Anchor) Then
                        Dim d = PathPointDistance(nodes(i).HandleIn, point, slopXPercent, slopYPercent)
                        If d <= 1.0 AndAlso d < bestDistance Then
                            bestDistance = d : bestIndex = i : bestPart = "in"
                        End If
                    End If
                Next
                If bestIndex >= 0 AndAlso bestPart <> "anchor" Then Return BeginPathDrag(bestIndex, bestPart)

                For i = 0 To nodes.Count - 1
                    If bestIndex = i AndAlso bestPart = "anchor" Then
                        ' ALT auf einem Stützpunkt entfernt ihn. Der Zug endet damit, bevor er
                        ' angefangen hat - True heißt trotzdem "verarbeitet", sonst fiele der Klick
                        ' durch und verschöbe das Objekt.
                        If removeNode Then
                            RemovePathNodeAt(i)
                            Return True
                        End If
                        ' DOPPELKLICK AUF EIN OFFENES ENDE zeichnet dort weiter. Ein einfacher Klick
                        ' zieht den Punkt, wie bisher - beides an derselben Stelle unterzubringen
                        ' geht nur über die Zahl der Klicks, und Ziehen ist der häufigere Fall.
                        If resumeAtEnd AndAlso Not target.PathClosed AndAlso
                           (i = 0 OrElse i = nodes.Count - 1) Then
                            ResumePathDraftAt(target, nodes, atStart:=(i = 0))
                            Return True
                        End If
                        ' UMSCHALT sammelt Punkte. Ein bereits gesammelter fällt wieder heraus; der
                        ' Zug beginnt nur, wenn der angeklickte danach dazugehört - sonst zöge das
                        ' Abwählen gleich die restliche Menge mit.
                        If addToSelection Then
                            If _selectedPathNodes.Contains(i) Then
                                _selectedPathNodes.Remove(i)
                                _lastTouchedPathNode = If(_selectedPathNodes.Count > 0, _selectedPathNodes.First(), -1)
                                RaisePathOverlayChanged()
                                Return True
                            End If
                            _selectedPathNodes.Add(i)
                            Return BeginPathDrag(i, "anchor")
                        End If
                        ' Ohne Umschalt: ein Klick auf einen Punkt AUSSERHALB der Menge macht ihn zum
                        ' einzigen. Innerhalb bleibt die Menge stehen und wandert gemeinsam.
                        If Not _selectedPathNodes.Contains(i) Then ClearPathNodeSelection()
                        Return BeginPathDrag(i, "anchor")
                    End If
                Next

                ' Kein Punkt getroffen, aber die KURVE? Dann meint der Klick sie: ein neuer Stützpunkt
                ' entsteht genau dort, formerhaltend. Beim anschließenden Ziehen biegt sich der
                ' Abschnitt, statt das Objekt zu verschieben (siehe SplitPathSegmentAt).
                ' MIT ABSTAND ZU DEN PUNKTEN. Ein neuer Stützpunkt entsteht nur, wo eindeutig KEIN
                ' Anfasser gemeint sein kann - sonst kostet ein knapp danebengegangener Griff einen
                ' ungewollten Punkt, und das ist der teurere Fehler (Nutzerbefund 2026-08-08: "es
                ' wird direkt ein neuer Knoten erstellt"). Zwei Greifzonen Abstand zu jedem
                ' Stützpunkt und jedem abstehenden Griff.
                Dim nearAnyHandle = False
                For i = 0 To nodes.Count - 1
                    If PathPointDistance(nodes(i).Anchor, point, slopXPercent, slopYPercent) <= 2.0 Then nearAnyHandle = True
                    If Not IsSamePathPoint(nodes(i).HandleOut, nodes(i).Anchor) AndAlso
                       PathPointDistance(nodes(i).HandleOut, point, slopXPercent, slopYPercent) <= 2.0 Then nearAnyHandle = True
                    If Not IsSamePathPoint(nodes(i).HandleIn, nodes(i).Anchor) AndAlso
                       PathPointDistance(nodes(i).HandleIn, point, slopXPercent, slopYPercent) <= 2.0 Then nearAnyHandle = True
                    If nearAnyHandle Then Exit For
                Next
                Dim t As Double
                Dim segment = If(nearAnyHandle, -1,
                                 FindPathSegmentAt(nodes, target.PathClosed, point,
                                                   slopXPercent, slopYPercent, t))
                If segment >= 0 Then
                    Dim inserted = SplitPathSegmentAt(target, nodes, segment, t)
                    If inserted >= 0 Then
                        Dim started = BeginPathDrag(inserted, "anchor")
                        ' Der Schritt zurück liegt schon vom Einfügen her auf dem Stapel. Ohne diese
                        ' Zeile käme beim ersten Ziehen ein zweiter dazu, und ein einziger Klick auf
                        ' die Kurve kostete zweimal Rückgängig.
                        _pathDragCapturedUndo = True
                        Return started
                    End If
                End If
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

        ''' <summary>WEITERZEICHNEN an einem offenen Ende: die vorhandenen Punkte werden zum Entwurf,
        ''' und der nächste Klick hängt sich hinten an.
        '''
        ''' Am ANFANG angesetzt wird die Liste UMGEDREHT - gezeichnet wird immer nach hinten. Beim
        ''' Umdrehen tauschen die Griffe die Seiten: was vorher in den Punkt hineinlief, läuft
        ''' danach aus ihm heraus. Ohne den Tausch klappte die Krümmung jedes Punktes um.
        '''
        ''' Der Entwurf schreibt am Ende in DASSELBE Objekt zurück (Ziel-Kennung gesetzt), es
        ''' entsteht also kein zweiter Pfad daneben.</summary>
        Private Sub ResumePathDraftAt(target As ImageAnnotation,
                                      nodes As List(Of ImageProcessor.PathNode), atStart As Boolean)
            If target Is Nothing OrElse nodes Is Nothing OrElse nodes.Count = 0 Then Return
            Dim ordered = If(atStart, ReversePathNodes(nodes), nodes.ToList())
            _pathDraft.Clear()
            _pathDraft.AddRange(ordered)
            _pathShapingIndex = -1
            _pathDraftTargetId = target.Id
            _pathPreviewPoint = Nothing
            _pathPreviewClosesPath = False
            StatusText = LocalizationService.T("Weiterzeichnen: Punkte setzen, Eingabe schließt ab")
            RaisePathOverlayChanged()
        End Sub

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
            _pathDragWasSmooth = False
            _pathDragStartAnchor = Nothing
            Dim target = PathEditTarget()
            If target IsNot Nothing Then
                Dim nodes = ReadPathNodesForDisplay(target)
                If index >= 0 AndAlso index < nodes.Count Then
                    _pathDragWasSmooth = IsSmoothPathNode(nodes(index))
                    _pathDragStartAnchor = nodes(index).Anchor
                End If
            End If
            _lastTouchedPathNode = index
            RaisePathOverlayChanged()
            Return True
        End Function

        ''' <summary>Ist dieser Punkt GLATT? Das heißt: beide Griffe stehen sichtbar ab und zeigen in
        ''' entgegengesetzte Richtungen. Die Punktart wird bewusst ABGELEITET und nicht gespeichert -
        ''' gespeichert stünde sie neben den Griffen und könnte ihnen widersprechen, und ein Pfad aus
        ''' einer älteren Datei hätte sie gar nicht.</summary>
        Friend Shared Function IsSmoothPathNode(node As ImageProcessor.PathNode) As Boolean
            Dim ix = CDbl(node.HandleIn.X - node.Anchor.X), iy = CDbl(node.HandleIn.Y - node.Anchor.Y)
            Dim ox = CDbl(node.HandleOut.X - node.Anchor.X), oy = CDbl(node.HandleOut.Y - node.Anchor.Y)
            Dim li = Math.Sqrt(ix * ix + iy * iy), lo = Math.Sqrt(ox * ox + oy * oy)
            If li < 0.0001 OrElse lo < 0.0001 Then Return False
            ' Entgegengesetzt heißt: das Skalarprodukt der Einheitsvektoren liegt nahe bei -1. Die
            ' Schwelle lässt rund fünf Grad Abweichung durch - Rundungen aus dem Umrechnen zwischen
            ' Objekt- und Anzeigeraum sollen einen glatten Punkt nicht zur Ecke machen.
            Return (ix * ox + iy * oy) / (li * lo) < -0.996
        End Function

        ''' <summary>Liegt der Zeiger auf einem Stützpunkt, einem Griff ODER auf der Kurve? Nur zur
        ''' Frage, ob das Text-Overlay den Druck DURCHLASSEN muss - es liegt über der Bühne, und ohne
        ''' diese Frage käme kein Druck je bei den Punkten an. Gegriffen wird weiter unten.
        '''
        ''' DIE KURVE GEHÖRT MIT DAZU, seit ein Klick auf sie dort einen Stützpunkt setzt: sonst
        ''' verschöbe derselbe Klick über dem Objektrechteck das ganze Objekt, und derselbe Klick
        ''' daneben legte einen Punkt an - dieselbe Geste mit zwei Bedeutungen, je nachdem, ob der
        ''' unsichtbare Rahmen darüber liegt.</summary>
        Public Function HitsPathPointPercent(xPercent As Double, yPercent As Double,
                                             slopXPercent As Double, slopYPercent As Double) As Boolean
            Dim values = PathOverlayValues
            If values Is Nothing OrElse values.Length < 9 Then Return False
            Dim count = CInt(values(0))
            Dim point = New SKPoint(CSng(xPercent), CSng(yPercent))
            Dim nodes As New List(Of ImageProcessor.PathNode)()
            For i = 0 To count - 1
                Dim o = 3 + i * 6
                If o + 5 >= values.Length Then Exit For
                For k = 0 To 2
                    Dim candidate = New SKPoint(CSng(values(o + k * 2)), CSng(values(o + k * 2 + 1)))
                    If IsNearPathPoint(candidate, point, slopXPercent, slopYPercent) Then Return True
                Next
                nodes.Add(New ImageProcessor.PathNode With {
                    .Anchor = New SKPoint(CSng(values(o)), CSng(values(o + 1))),
                    .HandleIn = New SKPoint(CSng(values(o + 2)), CSng(values(o + 3))),
                    .HandleOut = New SKPoint(CSng(values(o + 4)), CSng(values(o + 5)))})
            Next
            If nodes.Count < 2 Then Return False
            Dim t As Double
            Return FindPathSegmentAt(nodes, values(1) > 0.5, point, slopXPercent, slopYPercent, t) >= 0
        End Function

        Private Shared Function IsNearPathPoint(a As SKPoint, b As SKPoint,
                                                slopX As Double, slopY As Double) As Boolean
            Return PathPointDistance(a, b, slopX, slopY) <= 1.0
        End Function

        ''' <summary>Abstand zweier Punkte in GREIFZONEN gemessen: 0 heißt aufeinander, 1 heißt genau
        ''' am Rand der Zone. Die Zone ist bei einem nicht quadratischen Bild eine Ellipse, deshalb
        ''' zwei Radien. Dieselbe Zahl vergleicht damit Stützpunkte und Griffe untereinander.</summary>
        Private Shared Function PathPointDistance(a As SKPoint, b As SKPoint,
                                                  slopX As Double, slopY As Double) As Double
            Dim dx = (a.X - b.X) / Math.Max(0.0001, slopX)
            Dim dy = (a.Y - b.Y) / Math.Max(0.0001, slopY)
            Return Math.Sqrt(dx * dx + dy * dy)
        End Function

        Private Shared Function IsSamePathPoint(a As SKPoint, b As SKPoint) As Boolean
            Return Math.Abs(a.X - b.X) < 0.0001F AndAlso Math.Abs(a.Y - b.Y) < 0.0001F
        End Function

        ''' <summary>Zeigerbewegung mit gedrückter Taste: entweder formt sie die Griffe des eben
        ''' gesetzten Punktes, oder sie zieht einen vorhandenen Punkt bzw. Griff.
        '''
        ''' <paramref name="snapAngle"/> (Umschalt) rastet die Richtung auf Achtelkreise,
        ''' <paramref name="breakHandles"/> (Alt) bricht die Bindung eines glatten Punktes.</summary>
        Public Sub UpdatePathPointer(xPercent As Double, yPercent As Double,
                                     Optional snapAngle As Boolean = False,
                                     Optional breakHandles As Boolean = False)
            Dim point = New SKPoint(CSng(xPercent), CSng(yPercent))
            If _pathShapingIndex >= 0 AndAlso _pathShapingIndex < _pathDraft.Count Then
                ' Der Zug vom gesetzten Punkt weg spannt die Kurve auf: der ausgehende Griff folgt dem
                ' Zeiger, der eingehende spiegelt ihn. Ein Punkt ohne Zug bleibt damit eine Ecke.
                Dim node = _pathDraft(_pathShapingIndex)
                Dim shaped = If(snapAngle, SnapToEighthCircle(node.Anchor, point), point)
                node.HandleOut = shaped
                node.HandleIn = New SKPoint(node.Anchor.X * 2.0F - shaped.X, node.Anchor.Y * 2.0F - shaped.Y)
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
                    ' Verschieben um, statt ihre Form zu behalten. Umschalt rastet dabei die Richtung,
                    ' in die er WANDERT, bezogen auf seinen Stand beim Zugbeginn.
                    Dim goal = If(snapAngle AndAlso _pathDragStartAnchor.HasValue,
                                  SnapToEighthCircle(_pathDragStartAnchor.Value, point), point)
                    Dim dx = goal.X - edited.Anchor.X, dy = goal.Y - edited.Anchor.Y
                    edited.Anchor = goal
                    edited.HandleIn = New SKPoint(edited.HandleIn.X + dx, edited.HandleIn.Y + dy)
                    edited.HandleOut = New SKPoint(edited.HandleOut.X + dx, edited.HandleOut.Y + dy)
                    ' MEHRERE GESAMMELTE PUNKTE wandern um dieselbe Strecke mit. Berechnet wird sie
                    ' am gezogenen Punkt, nicht je Punkt am Zeiger - sonst rutschten alle aufeinander.
                    If _selectedPathNodes.Count > 1 AndAlso _selectedPathNodes.Contains(_pathDragIndex) Then
                        For Each index In _selectedPathNodes
                            If index = _pathDragIndex OrElse index < 0 OrElse index >= nodes.Count Then Continue For
                            Dim other = nodes(index)
                            other.Anchor = New SKPoint(other.Anchor.X + dx, other.Anchor.Y + dy)
                            other.HandleIn = New SKPoint(other.HandleIn.X + dx, other.HandleIn.Y + dy)
                            other.HandleOut = New SKPoint(other.HandleOut.X + dx, other.HandleOut.Y + dy)
                            nodes(index) = other
                        Next
                    End If
                Case "in"
                    Dim goal = If(snapAngle, SnapToEighthCircle(edited.Anchor, point), point)
                    edited.HandleIn = goal
                    ' EIN GLATTER PUNKT BLEIBT GLATT. Ohne das wurde jeder glatt gesetzte Punkt beim
                    ' ersten Nachjustieren zur Ecke: der gezogene Griff wanderte, der andere blieb
                    ' stehen, und die Kurve knickte. Der gegenüberliegende Griff dreht deshalb mit und
                    ' behält seine LÄNGE - nur die Richtung folgt. Alt bricht die Bindung, wie im
                    ' Standard, und macht aus dem Punkt eine Ecke mit zwei eigenen Griffen.
                    If _pathDragWasSmooth AndAlso Not breakHandles Then
                        edited.HandleOut = MirrorHandleDirection(edited.Anchor, goal, edited.HandleOut)
                    End If
                Case Else
                    Dim goal = If(snapAngle, SnapToEighthCircle(edited.Anchor, point), point)
                    edited.HandleOut = goal
                    If _pathDragWasSmooth AndAlso Not breakHandles Then
                        edited.HandleIn = MirrorHandleDirection(edited.Anchor, goal, edited.HandleIn)
                    End If
            End Select
            nodes(_pathDragIndex) = edited
            WritePathNodesFromDisplay(target, nodes)
            RaisePathOverlayChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Der gegenüberliegende Griff eines GLATTEN Punktes: er zeigt genau entgegengesetzt
        ''' zum gezogenen und behält dabei seinen Abstand zum Stützpunkt. Die Länge mitzuziehen wäre
        ''' die symmetrische Variante - der Standard hält nur die Richtung, damit eine einmal
        ''' eingestellte Krümmung der anderen Seite beim Nachziehen erhalten bleibt.
        '''
        ''' GERECHNET WIRD IN ANZEIGEPUNKTEN, aus demselben Grund wie bei der Winkelrastung: Prozent
        ''' der Breite und Prozent der Höhe sind bei einem nicht quadratischen Bild verschieden lang.
        ''' In Prozent gerechnet bliebe die Länge nur als Zahl gleich, am Bild würde der Griff beim
        ''' Drehen sichtbar länger oder kürzer - gemessen 20 gegen 44 an einem Prüfpfad.</summary>
        Private Function MirrorHandleDirection(anchor As SKPoint, dragged As SKPoint,
                                               opposite As SKPoint) As SKPoint
            Dim size = GetAnnotationDisplayPixelSize()
            Dim sx = If(size.Width > 0, CDbl(size.Width), 100.0)
            Dim sy = If(size.Height > 0, CDbl(size.Height), 100.0)
            Dim dx = (dragged.X - anchor.X) / 100.0 * sx, dy = (dragged.Y - anchor.Y) / 100.0 * sy
            Dim length = Math.Sqrt(dx * dx + dy * dy)
            If length < 0.0001 Then Return opposite
            Dim ox = (opposite.X - anchor.X) / 100.0 * sx, oy = (opposite.Y - anchor.Y) / 100.0 * sy
            Dim keep = Math.Sqrt(ox * ox + oy * oy)
            If keep < 0.0001 Then Return opposite
            Return New SKPoint(CSng(anchor.X - dx / length * keep / sx * 100.0),
                               CSng(anchor.Y - dy / length * keep / sy * 100.0))
        End Function

        ''' <summary>Rastet einen Punkt auf die nächste Achtelkreis-Richtung um einen Ursprung, mit
        ''' unveränderter Entfernung.
        '''
        ''' GERECHNET WIRD IN ANZEIGEPUNKTEN, nicht in Prozent: Prozent der Breite und Prozent der
        ''' Höhe sind bei einem nicht quadratischen Bild verschieden lang, und eine Rastung darin
        ''' ergäbe sichtbar schiefe Winkel statt der versprochenen fünfundvierzig Grad.</summary>
        Private Function SnapToEighthCircle(origin As SKPoint, point As SKPoint) As SKPoint
            Dim size = GetAnnotationDisplayPixelSize()
            Dim sx = If(size.Width > 0, CDbl(size.Width), 100.0)
            Dim sy = If(size.Height > 0, CDbl(size.Height), 100.0)
            Dim dx = (point.X - origin.X) / 100.0 * sx
            Dim dy = (point.Y - origin.Y) / 100.0 * sy
            Dim length = Math.Sqrt(dx * dx + dy * dy)
            If length < 0.0001 Then Return point
            Dim step45 = Math.PI / 4.0
            Dim angle = Math.Round(Math.Atan2(dy, dx) / step45) * step45
            Dim nx = Math.Cos(angle) * length
            Dim ny = Math.Sin(angle) * length
            Return New SKPoint(CSng(origin.X + nx / sx * 100.0), CSng(origin.Y + ny / sy * 100.0))
        End Function

        Public Sub EndPathPointer()
            ' OB ETWAS PASSIERT IST, sagt der Merker fuer den Rueckgaengig-Schritt und NICHT der
            ' Zug-Index. Den setzt BeginPathDrag schon beim Druecken, ein blosser Klick auf einen
            ' Punkt oder Griff traf also die Bedingung ebenso wie ein echter Zug - das Bild galt
            ' danach als geaendert, und beim Schliessen kam die Nachfrage nach dem Speichern, obwohl
            ' nichts anders lag als vorher. Der Merker dagegen wird erst in UpdatePathDrag gesetzt,
            ' also erst bei der ersten Bewegung; beim Einfuegen eines Punktes auf der Kurve setzt ihn
            ' TryBeginPathPointer selbst, denn dort hat sich der Pfad schon durch den Klick geaendert.
            Dim changed = _pathDragCapturedUndo
            _pathShapingIndex = -1
            _pathDragIndex = -1
            _pathDragPart = ""
            _pathDragCapturedUndo = False
            _pathDragWasSmooth = False
            _pathDragStartAnchor = Nothing
            If changed Then
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

            Dim current = GetSelectedAnnotationDisplayRectPercent()
            If current.Width <= 0 OrElse current.Height <= 0 Then Return
            Dim rotation = StoredAnnotationRotationToDisplay(target)
            Dim centerOldX = current.X + current.Width / 2.0, centerOldY = current.Y + current.Height / 2.0

            ' Die Huelle gehoert in den UNROTIERTEN Raum: das Objektrechteck ist unrotiert, und die
            ' Huelle der gedrehten Punkte waere ein anderes, groesseres Rechteck - der Pfad waere
            ' nach jedem Zug gewachsen.
            Dim unrotated = nodes.Select(Function(n) New ImageProcessor.PathNode With {
                .Anchor = RotateDisplayPercent(n.Anchor, centerOldX, centerOldY, -rotation),
                .HandleIn = RotateDisplayPercent(n.HandleIn, centerOldX, centerOldY, -rotation),
                .HandleOut = RotateDisplayPercent(n.HandleOut, centerOldX, centerOldY, -rotation)}).ToList()

            Dim minX = Double.MaxValue, minY = Double.MaxValue
            Dim maxX = Double.MinValue, maxY = Double.MinValue
            For Each n In unrotated
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

            ' EIN GEDREHTES OBJEKT DREHT UM SEINE EIGENE MITTE, und die wandert mit dem neuen
            ' Rechteck. Bliebe das Rechteck stehen, wo die Huelle liegt, saesse der Pfad danach
            ' verschoben: gedreht um die NEUE Mitte landet derselbe Punkt woanders als gedreht um die
            ' alte. Der Ausgleich ist die Verschiebung, die diesen Unterschied gerade aufhebt -
            ' (Mitte alt minus Mitte neu), vermindert um dieselbe Strecke gedreht.
            Dim shiftX = 0.0, shiftY = 0.0
            If Math.Abs(rotation) > 0.001 Then
                Dim centerNewX = minX + (maxX - minX) / 2.0, centerNewY = minY + (maxY - minY) / 2.0
                Dim rotatedCenter = RotateDisplayPercent(New SKPoint(CSng(centerOldX), CSng(centerOldY)), centerNewX, centerNewY, rotation)
                shiftX = centerOldX - rotatedCenter.X
                shiftY = centerOldY - rotatedCenter.Y
            End If

            ' Nichts tun, solange sich praktisch nichts geaendert hat: sonst schriebe jeder Zug das
            ' Rechteck neu und die Rundung wanderte mit.
            If Math.Abs(current.X - (minX + shiftX)) < 0.01 AndAlso
               Math.Abs(current.Y - (minY + shiftY)) < 0.01 AndAlso
               Math.Abs(current.Width - (maxX - minX)) < 0.01 AndAlso
               Math.Abs(current.Height - (maxY - minY)) < 0.01 Then Return

            Dim stored = DisplayAnnotationRectToStoredPercent(NormalizeAnnotationKind(target.Kind),
                                                              minX + shiftX, minY + shiftY,
                                                              maxX - minX, maxY - minY)
            target.XPixels = CSng(PercentXToPixels(stored.X))
            target.YPixels = CSng(PercentYToPixels(stored.Y))
            target.WidthPixels = CSng(Math.Max(1.0, PercentXToPixels(stored.Width)))
            target.HeightPixels = CSng(Math.Max(1.0, PercentYToPixels(stored.Height)))
            ' Die Punkte beziehen sich auf das NEUE Rechteck, und genau dieses wird ausdruecklich
            ' mitgegeben. Der Bezug ueber die Editor-Puffer taugt hier NICHT: die stehen bis
            ' LoadSelectedAnnotationIntoEditor noch auf dem ALTEN Rechteck, und die Punkte laegen
            ' danach im falschen Bezug - der Pfad sprang und verzerrte sich nach jedem Zug, der die
            ' Grenzen aenderte. Uebergeben werden die UNROTIERTEN Punkte samt derselben Verschiebung;
            ' die Drehung kommt beim naechsten Lesen von selbst wieder dazu.
            Dim shifted = unrotated.Select(Function(n) New ImageProcessor.PathNode With {
                .Anchor = New SKPoint(CSng(n.Anchor.X + shiftX), CSng(n.Anchor.Y + shiftY)),
                .HandleIn = New SKPoint(CSng(n.HandleIn.X + shiftX), CSng(n.HandleIn.Y + shiftY)),
                .HandleOut = New SKPoint(CSng(n.HandleOut.X + shiftX), CSng(n.HandleOut.Y + shiftY))}).ToList()
            WritePathNodesInRect(target, shifted,
                                 (minX + shiftX, minY + shiftY, maxX - minX, maxY - minY))
            LoadSelectedAnnotationIntoEditor()
        End Sub

        ''' <summary>Entwurf abschliessen. <paramref name="keep"/> False verwirft ihn (Esc), True legt
        ''' das Objekt an - sofern mindestens zwei Punkte stehen. Ein Pfad aus einem Punkt ist keiner;
        ''' er wird stillschweigend verworfen, statt ein unsichtbares Objekt zu hinterlassen.</summary>
        Public Sub FinishPathDraft(keep As Boolean, Optional closed As Boolean = False)
            Dim nodes = _pathDraft.ToList()
            Dim targetId = _pathDraftTargetId
            ClearPathNodeSelection()
            _pathDraft.Clear()
            _pathShapingIndex = -1
            _pathDraftTargetId = ""
            _pathPreviewPoint = Nothing
            _pathPreviewClosesPath = False
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
                    ' Beim WEITERZEICHNEN reichen die neuen Punkte in aller Regel über das bisherige
                    ' Rechteck hinaus - dann muss es ihnen folgen, sonst sitzt der Pfad beim nächsten
                    ' Skalieren falsch. Eine Text-GRUNDLINIE bleibt davon verschont: dort ist das
                    ' Rechteck die Textbox, und die soll eine Linie nicht umstellen
                    ' (siehe FreeTextPathKeepsBox).
                    If String.Equals(NormalizeAnnotationKind(target.Kind), "Path", StringComparison.Ordinal) AndAlso
                       Object.ReferenceEquals(target, PathEditTarget()) Then
                        RefitPathBoundsToPoints()
                    End If
                    RaisePathOverlayChanged()
                    RebuildLayerRows()
                    ' Zwei verschiedene Vorgänge landen hier: die Grundlinie eines Textes entsteht,
                    ' oder ein vorhandener Pfad wurde weitergezeichnet. Im Verlauf soll stehen, was
                    ' wirklich passiert ist.
                    NameHistoryStep(If(String.Equals(NormalizeAnnotationKind(target.Kind), "Path", StringComparison.Ordinal),
                                       LocalizationService.T("Pfad weitergezeichnet"),
                                       LocalizationService.T("Grundlinie gesetzt")))
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
            ' EIN NEUER PFAD ZEICHNET NICHTS: keine Füllung, Konturbreite null. Er ist zuerst
            ' GEOMETRIE - die Auswahl daraus, eine Grundlinie für Text, später eine Maske -, und für
            ' all das wäre eine Linie quer über dem Foto eine Überraschung. Wer eine gezeichnete
            ' Kurve will, zieht die Konturbreite hoch oder gibt ihm eine Füllung; beides steht im
            ' Eigenschaften-Panel und wirkt sofort. Beim Bearbeiten ist er ohnehin zu sehen: das
            ' Overlay zeichnet seine Kurve, solange er markiert ist.
            '
            ' Der Kommentar steht VOR dem Initialisierer und nicht darin: ein Kommentar zwischen
            ' zwei Feldern bricht in VB die Zeilenfortsetzung nach dem Komma ab.
            Dim annotation = New ImageAnnotation With {
                .Kind = "Path",
                .PathPoints = ImageProcessor.FormatPathPoints(objectNodes),
                .PathClosed = closed,
                .XPixels = CSng(PercentXToPixels(stored.X)),
                .YPixels = CSng(PercentYToPixels(stored.Y)),
                .WidthPixels = CSng(Math.Max(1.0, PercentXToPixels(stored.Width))),
                .HeightPixels = CSng(Math.Max(1.0, PercentYToPixels(stored.Height))),
                .FillColor = "#00FFFFFF",
                .StrokeColor = _annotationStrokeColor,
                .StrokeWidth = 0.0F,
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
            NameHistoryStep(LocalizationService.T("Pfad gezeichnet"))
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(annotation))
        End Sub
    End Class

End Namespace
