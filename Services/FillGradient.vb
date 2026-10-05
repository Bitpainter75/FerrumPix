Imports System.Globalization

Namespace Services

    ''' <summary>Ein Farbstopp eines Fuellverlaufs: Farbe als ARGB und Lage in Prozent (0 bis 100).
    ''' Unabhaengig von Skia und Avalonia, damit Renderer, ViewModel und Steuerelement dieselbe
    ''' Liste lesen.</summary>
    Public Structure GradientStopValue
        Public ReadOnly Argb As UInteger
        Public ReadOnly Position As Double

        Public Sub New(argb As UInteger, position As Double)
            Me.Argb = argb
            Me.Position = Math.Max(0.0, Math.Min(100.0, position))
        End Sub

        Public ReadOnly Property A As Byte
            Get
                Return CByte((Argb >> 24) And &HFFUI)
            End Get
        End Property

        Public ReadOnly Property R As Byte
            Get
                Return CByte((Argb >> 16) And &HFFUI)
            End Get
        End Property

        Public ReadOnly Property G As Byte
            Get
                Return CByte((Argb >> 8) And &HFFUI)
            End Get
        End Property

        Public ReadOnly Property B As Byte
            Get
                Return CByte(Argb And &HFFUI)
            End Get
        End Property

        Public ReadOnly Property Hex As String
            Get
                Return "#" & Argb.ToString("X8", CultureInfo.InvariantCulture)
            End Get
        End Property

        Public Function WithPosition(position As Double) As GradientStopValue
            Return New GradientStopValue(Argb, position)
        End Function

        Public Function WithArgb(argb As UInteger) As GradientStopValue
            Return New GradientStopValue(argb, Position)
        End Function
    End Structure

    ''' <summary>Die Fuellung eines Objekts oder einer Auswahl als Verlauf: Form, Farbstopps und die
    ''' Lage im Rechteck des Objekts.
    '''
    ''' Gespeichert wird das in einzelnen Feldern (ImageAnnotation.FillKind, GradientStops ...,
    ''' MaskedAdjustmentLayer.FillKind, FillStops ...). Diese Klasse liest sie EINMAL zusammen, damit
    ''' jeder Renderweg (Objekt, Auswahl, Maske, Vorschau) denselben Verlauf zeichnet.
    '''
    ''' Die Stoppliste ist eine Zeichenkette "#AARRGGBB@Prozent;...". Leer heisst: zwei Stopps aus
    ''' Farbe 1 und Farbe 2, so wie Dateien aus der Zeit vor den Stopps gespeichert sind. Wer Stopps
    ''' setzt, haelt Farbe 1 und Farbe 2 auf dem ersten und letzten Stopp, damit Programmteile, die nur
    ''' diese beiden kennen (Farbmischer, Haken "Aktiv"), weiter das Richtige sehen.</summary>
    Public NotInheritable Class GradientFillSpec

        Public Const KindSolid As String = "Solid"
        Public Const KindLinear As String = "LinearGradient"
        Public Const KindRadial As String = "RadialGradient"
        Public Const KindAngle As String = "AngleGradient"
        Public Const KindReflected As String = "ReflectedGradient"
        Public Const KindDiamond As String = "DiamondGradient"

        Public Const RepeatNone As String = ""
        Public Const RepeatRepeat As String = "Repeat"
        Public Const RepeatMirror As String = "Mirror"

        Public Property Kind As String = KindLinear
        Public Property Stops As New List(Of GradientStopValue)()
        Public Property AngleDegrees As Single
        Public Property Inverted As Boolean
        ''' <summary>Groesse des Verlaufs in Prozent des Objekts (10 bis 400). 100 spannt ihn
        ''' genau ueber das Objekt.</summary>
        Public Property ScalePercent As Single = 100.0F
        ''' <summary>Verschiebung der Mitte in Prozent der halben Breite bzw. Hoehe (-100 bis 100).</summary>
        Public Property OffsetXPercent As Single
        Public Property OffsetYPercent As Single
        ''' <summary>Was jenseits des Verlaufs geschieht: "" haelt die Randfarbe, "Repeat" wiederholt
        ''' ihn, "Mirror" wiederholt ihn gespiegelt.</summary>
        Public Property Repeat As String = RepeatNone

        ''' <summary>Die gespeicherte Fuellart auf einen der festen Namen. Alles Unbekannte ist
        ''' Vollfarbe. Ein Wert mit "|" stammt aus einem Zwischenstand, der die Stopps im Namen trug;
        ''' er wird auf seine Form zurueckgefuehrt.</summary>
        Public Shared Function NormalizeKind(value As String) As String
            Dim text = If(value, "").Trim()
            Dim bar = text.IndexOf("|"c)
            If bar >= 0 Then text = text.Substring(0, bar)
            Select Case text.ToLowerInvariant()
                Case "lineargradient", "multilineargradient" : Return KindLinear
                Case "radialgradient", "multiradialgradient" : Return KindRadial
                Case "anglegradient" : Return KindAngle
                Case "reflectedgradient" : Return KindReflected
                Case "diamondgradient" : Return KindDiamond
                Case Else : Return KindSolid
            End Select
        End Function

        Public Shared Function IsGradientKind(value As String) As Boolean
            Return NormalizeKind(value) <> KindSolid
        End Function

        Public Shared Function NormalizeRepeat(value As String) As String
            Select Case If(value, "").Trim().ToLowerInvariant()
                Case "repeat" : Return RepeatRepeat
                Case "mirror" : Return RepeatMirror
                Case Else : Return RepeatNone
            End Select
        End Function

        Public Shared Function ClampScale(value As Double) As Single
            If Double.IsNaN(value) OrElse value <= 0 Then Return 100.0F
            Return CSng(Math.Max(10.0, Math.Min(400.0, value)))
        End Function

        Public Shared Function ClampOffset(value As Double) As Single
            If Double.IsNaN(value) Then Return 0.0F
            Return CSng(Math.Max(-100.0, Math.Min(100.0, value)))
        End Function

        ''' <summary>Liest "#RRGGBB" oder "#AARRGGBB".</summary>
        Public Shared Function TryParseArgb(text As String, ByRef argb As UInteger) As Boolean
            If String.IsNullOrWhiteSpace(text) Then Return False
            Dim hex = text.Trim().TrimStart("#"c)
            If hex.Length <> 6 AndAlso hex.Length <> 8 Then Return False
            Dim raw As UInteger
            If Not UInteger.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, raw) Then Return False
            argb = If(hex.Length = 6, raw Or &HFF000000UI, raw)
            Return True
        End Function

        Public Shared Function ArgbOrDefault(text As String, fallback As UInteger) As UInteger
            Dim argb As UInteger
            Return If(TryParseArgb(text, argb), argb, fallback)
        End Function

        ''' <summary>Liest eine Stoppliste. Weniger als zwei gueltige Stopps: Nothing.</summary>
        Public Shared Function TryParseStops(text As String) As List(Of GradientStopValue)
            If String.IsNullOrWhiteSpace(text) Then Return Nothing
            Dim result As New List(Of GradientStopValue)()
            For Each entry In text.Split(";"c)
                Dim parts = entry.Split("@"c)
                If parts.Length <> 2 Then Continue For
                Dim argb As UInteger
                Dim position As Double
                If Not TryParseArgb(parts(0), argb) Then Continue For
                If Not Double.TryParse(parts(1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, position) Then Continue For
                If Double.IsNaN(position) Then Continue For
                result.Add(New GradientStopValue(argb, position))
            Next
            If result.Count < 2 Then Return Nothing
            ' Stabil sortieren: zwei Stopps an derselben Stelle behalten ihre Reihenfolge und
            ' ergeben eine harte Kante.
            Return result.Select(Function(s, i) (s, i)).OrderBy(Function(x) x.s.Position).ThenBy(Function(x) x.i).
                Select(Function(x) x.s).ToList()
        End Function

        ''' <summary>Die Stoppliste, ersatzweise zwei Stopps aus Farbe 1 und Farbe 2.</summary>
        Public Shared Function ParseStops(text As String, color1 As String, color2 As String) As List(Of GradientStopValue)
            Dim parsed = TryParseStops(text)
            If parsed IsNot Nothing Then Return parsed
            Return New List(Of GradientStopValue) From {
                New GradientStopValue(ArgbOrDefault(color1, &HFFFFFFFFUI), 0),
                New GradientStopValue(ArgbOrDefault(color2, &HFFFFFFFFUI), 100)
            }
        End Function

        Public Shared Function FormatStops(stops As IEnumerable(Of GradientStopValue)) As String
            If stops Is Nothing Then Return ""
            Return String.Join(";", stops.Select(Function(s) s.Hex & "@" &
                                                     Math.Round(s.Position, 1).ToString("0.#", CultureInfo.InvariantCulture)))
        End Function

        ''' <summary>Bringt eine Stoppliste in die gespeicherte Form; ungueltig wird leer.</summary>
        Public Shared Function NormalizeStops(text As String) As String
            Dim parsed = TryParseStops(text)
            Return If(parsed Is Nothing, "", FormatStops(parsed))
        End Function

        ''' <summary>Die Farbe des Verlaufs an einer Stelle (0 bis 100), ungemischt mit dem Alpha
        ''' gerechnet wie Skia es tut. Fuer einen neuen Stopp, der die vorhandene Farbe uebernimmt.</summary>
        Public Shared Function ColorAt(stops As IList(Of GradientStopValue), position As Double) As UInteger
            If stops Is Nothing OrElse stops.Count = 0 Then Return &HFFFFFFFFUI
            If position <= stops(0).Position Then Return stops(0).Argb
            For i = 1 To stops.Count - 1
                Dim a = stops(i - 1), b = stops(i)
                If position <= b.Position Then
                    Dim span = b.Position - a.Position
                    Dim t = If(span <= 0.0001, 1.0, (position - a.Position) / span)
                    Return Lerp(a.Argb, b.Argb, t)
                End If
            Next
            Return stops(stops.Count - 1).Argb
        End Function

        Private Shared Function Lerp(c1 As UInteger, c2 As UInteger, t As Double) As UInteger
            Dim result As UInteger = 0
            For shift = 0 To 24 Step 8
                Dim v1 = CDbl((c1 >> shift) And &HFFUI)
                Dim v2 = CDbl((c2 >> shift) And &HFFUI)
                Dim v = CUInt(Math.Max(0, Math.Min(255, Math.Round(v1 + (v2 - v1) * t))))
                result = result Or (v << shift)
            Next
            Return result
        End Function

        ''' <summary>Baut den Verlauf aus den gespeicherten Feldern. Nothing bei Vollfarbe.</summary>
        Public Shared Function Create(fillKind As String, color1 As String, color2 As String, stops As String,
                                      angleDegrees As Double, inverted As Boolean,
                                      scalePercent As Double, offsetXPercent As Double, offsetYPercent As Double,
                                      repeat As String) As GradientFillSpec
            Dim kind = NormalizeKind(fillKind)
            If kind = KindSolid Then Return Nothing
            ' Zwischenstand mit den Stopps im Namen der Fuellart (siehe NormalizeKind).
            Dim stopText = stops
            If String.IsNullOrWhiteSpace(stopText) AndAlso fillKind IsNot Nothing AndAlso fillKind.Contains("|"c) Then
                stopText = fillKind.Substring(fillKind.IndexOf("|"c) + 1)
            End If
            Return New GradientFillSpec With {
                .Kind = kind,
                .Stops = ParseStops(stopText, color1, color2),
                .AngleDegrees = CSng(If(Double.IsNaN(angleDegrees), 0.0, angleDegrees)),
                .Inverted = inverted,
                .ScalePercent = ClampScale(scalePercent),
                .OffsetXPercent = ClampOffset(offsetXPercent),
                .OffsetYPercent = ClampOffset(offsetYPercent),
                .Repeat = NormalizeRepeat(repeat)
            }
        End Function

        Public Shared Function FromAnnotation(a As ImageAnnotation) As GradientFillSpec
            If a Is Nothing Then Return Nothing
            Return Create(a.FillKind, a.FillColor, a.FillColor2, a.GradientStops, a.GradientAngleDegrees, a.GradientInverted,
                          a.GradientScalePercent, a.GradientOffsetXPercent, a.GradientOffsetYPercent, a.GradientRepeat)
        End Function

        Public Shared Function FromLayer(layer As MaskedAdjustmentLayer) As GradientFillSpec
            If layer Is Nothing Then Return Nothing
            Return Create(layer.FillKind, layer.FillColor, layer.FillColor2, layer.FillStops, layer.FillAngle, layer.FillInverted,
                          layer.FillScale, layer.FillOffsetX, layer.FillOffsetY, layer.FillRepeat)
        End Function

        ''' <summary>Die Stopps in Zeichenrichtung: umgekehrt, wenn der Verlauf invertiert ist.</summary>
        Public Function EffectiveStops() As List(Of GradientStopValue)
            Dim list = If(Stops, New List(Of GradientStopValue)())
            If list.Count = 0 Then list = New List(Of GradientStopValue) From {New GradientStopValue(&HFFFFFFFFUI, 0), New GradientStopValue(&HFFFFFFFFUI, 100)}
            If list.Count = 1 Then list = New List(Of GradientStopValue) From {list(0).WithPosition(0), list(0).WithPosition(100)}
            If Not Inverted Then Return New List(Of GradientStopValue)(list)
            Dim reversed = list.Select(Function(s) s.WithPosition(100.0 - s.Position)).ToList()
            reversed.Reverse()
            Return reversed
        End Function

        ''' <summary>Eine Abschrift mit der Deckkraft des Objekts in jedem Stopp.</summary>
        Public Function WithAlphaFactor(factor As Single) As GradientFillSpec
            Dim f = Math.Max(0.0F, Math.Min(1.0F, factor))
            Dim copy = DirectCast(MemberwiseClone(), GradientFillSpec)
            copy.Stops = If(Stops, New List(Of GradientStopValue)()).Select(
                Function(s) s.WithArgb((s.Argb And &HFFFFFFUI) Or (CUInt(Math.Round(s.A * f)) << 24))).ToList()
            Return copy
        End Function

        ''' <summary>Ist irgendein Stopp sichtbar? Sonst zeichnet der Verlauf nichts.</summary>
        Public Function HasVisibleColor() As Boolean
            Return Stops IsNot Nothing AndAlso Stops.Any(Function(s) s.A > 0)
        End Function

        ''' <summary>Ob Winkel, Groesse und Wiederholung bei dieser Form etwas bewirken.</summary>
        Public Shared Function UsesAngle(kind As String) As Boolean
            Dim k = NormalizeKind(kind)
            Return k = KindLinear OrElse k = KindAngle OrElse k = KindReflected OrElse k = KindDiamond
        End Function

        Public Shared Function UsesScale(kind As String) As Boolean
            Dim k = NormalizeKind(kind)
            Return k <> KindSolid AndAlso k <> KindAngle
        End Function

        Public Shared Function UsesCenter(kind As String) As Boolean
            Return NormalizeKind(kind) <> KindSolid
        End Function

        ''' <summary>Die eingebauten Vorlagen. Der Name ist der deutsche Ausgangstext und wird erst
        ''' beim Anzeigen uebersetzt (LocalizationService.T), die Stopps sind die gespeicherte Form.</summary>
        Public Shared ReadOnly BuiltInPresets As IReadOnlyList(Of (Name As String, Stops As String)) = New(String, String)() {
            ("Schwarz zu Weiß", "#FF000000@0;#FFFFFFFF@100"),
            ("Ausblenden", "#FF000000@0;#00000000@100"),
            ("Sonnenuntergang", "#FF2B1055@0;#FFD53369@45;#FFFF9A44@75;#FFFFE29F@100"),
            ("Morgenrot", "#FFFF5F6D@0;#FFFFC371@100"),
            ("Ozean", "#FF061A40@0;#FF0353A4@40;#FF00A6C0@75;#FFB9F6CA@100"),
            ("Himmel", "#FF2980B9@0;#FF6DD5FA@60;#FFFFFFFF@100"),
            ("Wald", "#FF0B3D0B@0;#FF2E7D32@45;#FF9CCC65@100"),
            ("Feuer", "#FF3A0000@0;#FFB71C1C@30;#FFFF6F00@65;#FFFFEB3B@100"),
            ("Violett", "#FF240046@0;#FF7B2CBF@50;#FFE0AAFF@100"),
            ("Pfirsich", "#FFFFDAB9@0;#FFFF9A8B@50;#FFFF6A88@100"),
            ("Gold", "#FF7A5410@0;#FFD4A537@30;#FFFFF1B5@50;#FFC99A2E@70;#FF7A5410@100"),
            ("Silber", "#FF5E6066@0;#FFC9CCD1@30;#FFFFFFFF@50;#FFB4B7BD@70;#FF5E6066@100"),
            ("Kupfer", "#FF4E2511@0;#FFB87333@40;#FFF2C49B@55;#FF9C5A2B@100"),
            ("Regenbogen", "#FFFF0040@0;#FFFF8C00@17;#FFFFE600@33;#FF00C853@50;#FF00B0FF@67;#FF3D5AFE@83;#FFAA00FF@100"),
            ("Neon", "#FF00F5D4@0;#FF00BBF9@35;#FF9B5DE5@70;#FFF15BB5@100")
        }

    End Class

End Namespace
