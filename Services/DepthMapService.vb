Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ''' <summary>Die Tiefenkarte eines Bildes: fuer jeden Bildpunkt, wie weit er entfernt ist.
    '''
    ''' RELATIVE Tiefe, nicht in Metern. Das Modell weiss nicht, wie gross die Szene ist - es weiss
    ''' nur, was naeher und was weiter ist. Fuer alles, wofuer sie hier gebraucht wird, reicht das
    ''' genau: eine Maske nach Entfernung, und spaeter eine Unschaerfe, die mit der Entfernung
    ''' zunimmt. Wer Meter braucht, braucht ein anderes Modell.
    '''
    ''' Ausgeliefert wird sie als Graustufenbild in Bildgroesse: HELL ist nah, DUNKEL ist fern. Das
    ''' ist die Richtung, die das Modell liefert (es gibt inverse Tiefe aus), und sie wird bewusst
    ''' nicht gedreht - "hell = nah" ist auch die Anschauung, die man beim Anschauen einer solchen
    ''' Karte hat.
    '''
    ''' Die Werte werden auf 0 bis 255 GESPREIZT, jedes Bild fuer sich. Absolute Vergleichbarkeit
    ''' zwischen zwei Fotos gibt es bei relativer Tiefe ohnehin nicht; ohne Spreizung laege ein Bild
    ''' mit geringer Tiefenstaffelung in einem schmalen Band und waere als Maske unbrauchbar.</summary>
    Public NotInheritable Class DepthMapService

        Private Sub New()
        End Sub

        Public Const ModelFile As String = "midas-small"

        ''' <summary>Kantenlaenge, auf die das Modell rechnet. Fest: die kleine Fassung ist auf
        ''' 256 Pixel trainiert.</summary>
        Public Const ModelEdge As Integer = 256

        Public Shared ReadOnly Property Available As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(ModelFile))
            End Get
        End Property

        ''' <summary>Die Tiefenkarte als Alpha8-Bild in der Groesse des Quellbildes. Nothing bei
        ''' jedem Fehlschlag - eine halbe Tiefenkarte waere schlimmer als keine.</summary>
        Public Shared Function Compute(image As SKBitmap) As SKBitmap
            If image Is Nothing OrElse image.Width <= 0 OrElse image.Height <= 0 Then Return Nothing
            Dim session = AiModelService.SessionFor(ModelFile)
            If session Is Nothing Then Return Nothing

            ' Das Modell rechnet auf einem festen Quadrat. Das Bild wird darauf VERZERRT, nicht
            ' eingepasst: eine Auffuellung mit Schwarz haette das Netz als sehr weit entfernte
            ' Flaeche gelesen und die Spreizung der echten Tiefen zusammengedrueckt.
            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, ModelEdge, ModelEdge})
            Using small = New SKBitmap(ModelEdge, ModelEdge, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                If Not image.ScalePixels(small, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                ' Normierung aus dem Training des Modells - nicht geraten.
                Dim average = New Single() {0.485F, 0.456F, 0.406F}
                Dim spread = New Single() {0.229F, 0.224F, 0.225F}
                Dim target = tensor.Buffer.Span
                Dim layer = ModelEdge * ModelEdge
                For y = 0 To ModelEdge - 1
                    For x = 0 To ModelEdge - 1
                        Dim p = small.GetPixel(x, y)
                        Dim i = y * ModelEdge + x
                        target(i) = (p.Red / 255.0F - average(0)) / spread(0)
                        target(layer + i) = (p.Green / 255.0F - average(1)) / spread(1)
                        target(layer * 2 + i) = (p.Blue / 255.0F - average(2)) / spread(2)
                    Next
                Next
            End Using

            Try
                Dim input = New List(Of NamedOnnxValue) From {
                    NamedOnnxValue.CreateFromTensor("image", tensor)}
                Using result = session.Run(input)
                    Dim output = TryCast(result.First().Value, DenseTensor(Of Single))
                    If output Is Nothing Then Return Nothing
                    Return AsGrayscale(output.Buffer.ToArray(), image.Width, image.Height)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Tiefenkarte", ex.Message)
                Return Nothing
            End Try
        End Function

        Private Shared Function AsGrayscale(values As Single(), targetWidth As Integer, targetHeight As Integer) As SKBitmap
            If values Is Nothing OrElse values.Length < ModelEdge * ModelEdge Then Return Nothing
            Dim offset = values.Length - ModelEdge * ModelEdge

            Dim min = Single.MaxValue, max = Single.MinValue
            For i = offset To values.Length - 1
                Dim v = values(i)
                If Single.IsNaN(v) OrElse Single.IsInfinity(v) Then Continue For
                If v < min Then min = v
                If v > max Then max = v
            Next
            Dim span = max - min
            ' Ein voellig flaches Ergebnis ist keine Tiefenkarte, sondern ein Fehlschlag.
            If span <= 0.0001F Then Return Nothing

            Using small = New SKBitmap(New SKImageInfo(ModelEdge, ModelEdge, SKColorType.Alpha8, SKAlphaType.Premul))
                Dim buffer(ModelEdge * ModelEdge - 1) As Byte
                For i = 0 To buffer.Length - 1
                    Dim v = (values(offset + i) - min) / span
                    buffer(i) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(v * 255.0F)))))
                Next
                Runtime.InteropServices.Marshal.Copy(buffer, 0, small.GetPixels(), buffer.Length)

                Dim large = New SKBitmap(New SKImageInfo(targetWidth, targetHeight, SKColorType.Alpha8, SKAlphaType.Premul))
                If Not small.ScalePixels(large, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then
                    large.Dispose()
                    Return Nothing
                End If
                Return large
            End Using
        End Function

        ''' <summary>Aus einer Tiefenkarte eine Maske: alles, dessen Tiefe zwischen den beiden
        ''' Grenzen liegt. Beide in Prozent, 0 = am weitesten entfernt, 100 = am naechsten.
        '''
        ''' <paramref name="featherPct"/> laesst die Maske an beiden Grenzen auslaufen, statt hart
        ''' abzuschneiden. Ohne das entstuende an jeder Tiefenstufe eine sichtbare Kante quer durchs
        ''' Bild - eine Tiefenkarte ist stetig, eine harte Schwelle darin ist immer zu sehen.</summary>
        Public Shared Function MaskFromDepth(depth As SKBitmap, fromPct As Double, toPct As Double,
                                             featherPct As Double) As SKBitmap
            If depth Is Nothing OrElse depth.Width <= 0 OrElse depth.Height <= 0 Then Return Nothing
            Dim from = Math.Max(0.0, Math.Min(100.0, Math.Min(fromPct, toPct)))
            Dim bis = Math.Max(0.0, Math.Min(100.0, Math.Max(fromPct, toPct)))
            Dim soft = Math.Max(0.0, Math.Min(50.0, featherPct))

            Dim w = depth.Width, h = depth.Height
            Dim output = New SKBitmap(New SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul))
            Dim buffer(w * h - 1) As Byte
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim t = depth.GetPixel(x, y).Alpha / 255.0 * 100.0
                    Dim d As Double
                    If t < from Then
                        d = If(soft <= 0, 0.0, 1.0 - Math.Min(1.0, (from - t) / soft))
                    ElseIf t > bis Then
                        d = If(soft <= 0, 0.0, 1.0 - Math.Min(1.0, (t - bis) / soft))
                    Else
                        d = 1.0
                    End If
                    buffer(y * w + x) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(d * 255.0)))))
                Next
            Next
            Runtime.InteropServices.Marshal.Copy(buffer, 0, output.GetPixels(), buffer.Length)
            Return output
        End Function


        ''' <summary>Die Form des Verwischungskerns, zeilenweise als SPANNE: fuer jede Zeile des
        ''' Kerns, von welcher bis zu welcher Spalte sie reicht.
        '''
        ''' Die Spanne ist der ganze Trick. Eine Scheibe ist konvex, also hat jede ihrer Zeilen genau
        ''' einen zusammenhaengenden Abschnitt - und ueber Praefixsummen kostet so ein Abschnitt zwei
        ''' Zugriffe statt seiner Laenge. Damit kostet ein Bildpunkt 2r+1 Rechenschritte statt
        ''' (2r+1) mal (2r+1). Bei Radius 12 ist das der Unterschied zwischen 25 und 625, und genau
        ''' daran ist die erste Fassung gescheitert: als allgemeine Faltung gerechnet brauchte eine
        ''' Vorschau ueber eine Minute.
        '''
        ''' <paramref name="corners"/> 0 heisst rund, sonst die Anzahl der Blendenlamellen.</summary>
        Private Shared Sub SpannenFuer(radius As Integer, corners As Integer, rotation As Double,
                                       ByRef xFrom As Integer(), ByRef xTo As Integer(),
                                       ByRef count As Long)
            Dim n = radius * 2 + 1
            ReDim xFrom(n - 1)
            ReDim xTo(n - 1)
            count = 0
            Dim eckzahl = If(corners >= 3, corners, 0)
            Dim angleOffset = rotation * Math.PI / 180.0
            For row = 0 To n - 1
                Dim dy = row - radius
                Dim left = Integer.MaxValue, right = Integer.MinValue
                For column = 0 To n - 1
                    Dim dx = column - radius
                    Dim r = Math.Sqrt(dx * dx + dy * dy)
                    Dim inside As Boolean
                    If eckzahl = 0 Then
                        inside = r <= radius + 0.5
                    Else
                        ' Ein regelmaessiges Vieleck: der zulaessige Radius haengt vom Winkel ab.
                        ' Zwischen zwei Ecken liegt die Kante naeher an der Mitte als die Ecke.
                        Dim w = Math.Atan2(dy, dx) - angleOffset
                        Dim part = 2.0 * Math.PI / eckzahl
                        Dim remainder = w - part * Math.Floor(w / part) - part / 2.0
                        Dim limit = radius * Math.Cos(Math.PI / eckzahl) / Math.Max(0.2, Math.Cos(remainder))
                        inside = r <= limit
                    End If
                    If inside Then
                        If dx < left Then left = dx
                        If dx > right Then right = dx
                    End If
                Next
                If right < left Then
                    ' Leere Zeile: als Spanne der Laenge null vermerken.
                    xFrom(row) = 0
                    xTo(row) = -1
                Else
                    xFrom(row) = left
                    xTo(row) = right
                    count += right - left + 1
                End If
            Next
        End Sub

        ''' <summary>Groesster Radius, mit dem direkt gerechnet wird. Darueber wird die Stufe auf
        ''' einer verkleinerten Kopie gerechnet und wieder hochgezogen - das haelt die Kosten je
        ''' Stufe unabhaengig davon, wie stark verwischt wird.</summary>
        Private Const MaxKernelRadius As Integer = 12

        ''' <summary>Zeichnet <paramref name="source"/> mit einer Scheibe verwischt.
        '''
        ''' Die LICHTER werden vor dem Verwischen angehoben und danach zurueckgenommen. Ohne das
        ''' mittelt sich ihre Helligkeit weg, und aus einem Lichtpunkt wird ein matter Fleck statt
        ''' einer leuchtenden Scheibe. Das ist der Schritt, der "unscharf" von "Bokeh" trennt - mehr
        ''' noch als die Form des Kerns.
        '''
        ''' Am Bildrand wird der Randwert fortgesetzt. Die Alternative waere, dort weniger Punkte zu
        ''' mitteln; dann wuerde der Rand heller oder dunkler als seine Umgebung.</summary>
        Private Shared Function BlurWithDisc(source As SKBitmap, radius As Double,
                                                    corners As Integer, highlightsPct As Double) As SKBitmap
            If source Is Nothing OrElse radius < 0.5 Then Return Nothing
            Dim scale = 1.0
            Dim r = CInt(Math.Round(radius))
            If r > MaxKernelRadius Then
                scale = MaxKernelRadius / radius
                r = MaxKernelRadius
            End If
            If r < 1 Then Return Nothing

            Dim xFrom As Integer() = Nothing, xTo As Integer() = Nothing
            Dim count As Long = 0
            SpannenFuer(r, corners, 12.0, xFrom, xTo, count)
            If count <= 0 Then Return Nothing

            Dim aw = Math.Max(1, CInt(Math.Round(source.Width * scale)))
            Dim ah = Math.Max(1, CInt(Math.Round(source.Height * scale)))
            If aw <= r * 2 OrElse ah <= r * 2 Then Return Nothing

            Dim highlights = Math.Max(0.0, Math.Min(100.0, highlightsPct)) / 100.0
            ' Hin- und Rueckkennlinie der Lichteranhebung, als Tabelle statt als Potenz je Bildpunkt.
            ' Gemittelt wird in einem GESPREIZTEN Raum: v hoch p mit p groesser eins zieht die
            ' hellen Werte auseinander, sodass sie den Mittelwert bestimmen statt in ihm unterzugehen.
            ' Die Wurzel danach bringt die Helligkeit wieder zurecht. Andersherum (p kleiner eins)
            ' waere es das Gegenteil und zoege alles ins Dunkle.
            Dim high(255) As Single
            Dim exponentHigh = 1.0 + highlights * 3.0
            For i = 0 To 255
                high(i) = CSng(Math.Pow(i / 255.0, exponentHigh))
            Next
            Dim exponentDown = 1.0 / exponentHigh

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using small = New SKBitmap(aw, ah, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Dim sampling = If(scale < 1.0,
                                   New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                                   New SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
                If Not source.ScalePixels(small, sampling) Then
                    result.Dispose()
                    Return Nothing
                End If

                ' Praefixsummen JE ZEILE und je Kanal, auf den angehobenen Werten. Eine Zeile hat
                ' aw+1 Eintraege, damit die Spanne a bis b als P(b+1) minus P(a) herauskommt.
                Dim width1 = aw + 1
                Dim sums As Single()() = New Single(2)() {}
                For k = 0 To 2
                    sums(k) = New Single(ah * width1 - 1) {}
                Next
                For y = 0 To ah - 1
                    Dim row = y * width1
                    Dim pass0 As Single = 0, pass1 As Single = 0, pass2 As Single = 0
                    For x = 0 To aw - 1
                        Dim p = small.GetPixel(x, y)
                        pass0 += high(p.Red) : pass1 += high(p.Green) : pass2 += high(p.Blue)
                        sums(0)(row + x + 1) = pass0
                        sums(1)(row + x + 1) = pass1
                        sums(2)(row + x + 1) = pass2
                    Next
                Next

                Using blurred = New SKBitmap(aw, ah, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                    Dim divisor = CSng(1.0 / count)
                    For y = 0 To ah - 1
                        For x = 0 To aw - 1
                            Dim s0 As Single = 0, s1 As Single = 0, s2 As Single = 0
                            For row = 0 To r * 2
                                If xTo(row) < xFrom(row) Then Continue For
                                Dim qy = y + row - r
                                If qy < 0 Then qy = 0
                                If qy > ah - 1 Then qy = ah - 1
                                Dim basis = qy * width1
                                Dim a = x + xFrom(row)
                                Dim b = x + xTo(row)
                                ' Ausserhalb der Zeile wird der Randwert fortgesetzt: der fehlende
                                ' Teil der Spanne zaehlt so oft, wie er ueber den Rand hinausragt.
                                Dim leftExtra = 0, rightExtra = 0
                                If a < 0 Then
                                    leftExtra = -a
                                    a = 0
                                End If
                                If b > aw - 1 Then
                                    rightExtra = b - (aw - 1)
                                    b = aw - 1
                                End If
                                s0 += sums(0)(basis + b + 1) - sums(0)(basis + a)
                                s1 += sums(1)(basis + b + 1) - sums(1)(basis + a)
                                s2 += sums(2)(basis + b + 1) - sums(2)(basis + a)
                                If leftExtra > 0 Then
                                    s0 += (sums(0)(basis + 1) - sums(0)(basis)) * leftExtra
                                    s1 += (sums(1)(basis + 1) - sums(1)(basis)) * leftExtra
                                    s2 += (sums(2)(basis + 1) - sums(2)(basis)) * leftExtra
                                End If
                                If rightExtra > 0 Then
                                    s0 += (sums(0)(basis + aw) - sums(0)(basis + aw - 1)) * rightExtra
                                    s1 += (sums(1)(basis + aw) - sums(1)(basis + aw - 1)) * rightExtra
                                    s2 += (sums(2)(basis + aw) - sums(2)(basis + aw - 1)) * rightExtra
                                End If
                            Next
                            blurred.SetPixel(x, y, New SKColor(
                                BackFromLight(s0 * divisor, exponentDown),
                                BackFromLight(s1 * divisor, exponentDown),
                                BackFromLight(s2 * divisor, exponentDown), 255))
                        Next
                    Next

                    Dim back = If(scale < 1.0,
                                     New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                                     New SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
                    If Not blurred.ScalePixels(result, back) Then
                        result.Dispose()
                        Return Nothing
                    End If
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Die Rueckkennlinie der Lichteranhebung, zurueck auf 0 bis 255.</summary>
        Private Shared Function BackFromLight(v As Single, exponent As Double) As Byte
            If Single.IsNaN(v) OrElse v <= 0 Then Return 0
            Dim z = Math.Pow(Math.Min(1.0, v), exponent) * 255.0
            Return CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(z)))))
        End Function

        ''' <summary>Eine Kennlinie als Tabelle: Wert hoch <paramref name="exponent"/>. Kleiner 1
        ''' hebt an, groesser 1 nimmt zurueck.</summary>
        Private Shared Function ToneCurveTable(exponent As Single) As Byte()
            Dim t(255) As Byte
            For i = 0 To 255
                Dim v = Math.Pow(i / 255.0, exponent)
                t(i) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(v * 255.0)))))
            Next
            Return t
        End Function

        ''' <summary>Tiefenabhaengige Unschaerfe: was weiter weg ist, wird staerker verwischt.
        '''
        ''' Der Unterschied zu einem gewoehnlichen Weichzeichner ist nicht die Staerke, sondern dass
        ''' sie je Bildpunkt aus seiner ENTFERNUNG kommt. Ein gleichmaessig unscharfer Hintergrund
        ''' sieht nach Weichzeichner aus; eine Unschaerfe, die mit der Entfernung zunimmt, sieht nach
        ''' offener Blende aus.
        '''
        ''' Gerechnet wird in STUFEN, nicht je Pixel: das Bild wird mehrfach unterschiedlich stark
        ''' verwischt, und dazwischen wird nach der Tiefenkarte ueberblendet. Ein echter Radius je
        ''' Pixel waere bei 45 MP nicht bezahlbar, und der sichtbare Unterschied ist gering, solange
        ''' die Stufen dicht genug liegen und weich ineinander laufen.
        '''
        ''' <paramref name="depth"/> ist die Karte in Bildgroesse: HELL ist nah, DUNKEL ist fern.
        ''' <paramref name="focusPct"/> ist die Entfernung, die scharf bleibt (0 bis 100, 100 = nah).
        ''' <paramref name="strengthPct"/> ist der groesste Radius am fernsten Punkt, in Prozent der
        ''' kurzen Bildkante.
        ''' <paramref name="transitionPct"/> bestimmt, wie schnell die Unschaerfe um die Fokusebene
        ''' herum zunimmt - klein ergibt eine schmale Schaerfeebene, gross eine breite.</summary>
        ''' <param name="cornerCount">Form der Blende: 0 rund, sonst die Zahl der Lamellen.</param>
        ''' <param name="highlightsPct">Wie stark Lichtpunkte als leuchtende Scheiben erhalten bleiben.</param>
        ''' <param name="fromPct">Untere Grenze des scharfen Bandes (0 ist am weitesten weg).</param>
        ''' <param name="toPct">Obere Grenze des scharfen Bandes (100 ist am naechsten).</param>
        Public Shared Function DepthBlur(source As SKBitmap, depth As SKBitmap,
                                                fromPct As Double, toPct As Double,
                                                strengthPct As Double,
                                                transitionPct As Double,
                                                Optional cornerCount As Integer = 0,
                                                Optional highlightsPct As Double = 60.0) As SKBitmap
            If source Is Nothing OrElse depth Is Nothing Then Return Nothing
            If source.Width <= 0 OrElse source.Height <= 0 Then Return Nothing
            Dim strength = Math.Max(0.0, Math.Min(100.0, strengthPct))
            If strength <= 0.01 Then Return Nothing

            ' Ein BAND statt einer Ebene. Eine echte Schaerfentiefe reicht nicht symmetrisch um
            ' einen Punkt: nach hinten ist sie deutlich groesser als nach vorn. Mit zwei Grenzen
            ' laesst sich das einstellen, mit Mitte plus Breite nicht.
            Dim from = Math.Max(0.0, Math.Min(100.0, Math.Min(fromPct, toPct)))
            Dim bis = Math.Max(0.0, Math.Min(100.0, Math.Max(fromPct, toPct)))
            Dim uebergang = Math.Max(1.0, Math.Min(100.0, transitionPct))
            ' Der groesste Radius bezieht sich auf die KURZE Kante: derselbe Prozentwert soll bei
            ' Hoch- und Querformat gleich aussehen.
            Dim maxRadius = Math.Min(source.Width, source.Height) * strength / 100.0 * 0.08
            If maxRadius < 0.5 Then Return Nothing

            ' Die Tiefenkarte auf Bildgroesse bringen, falls sie kleiner ist.
            Dim map = depth
            Dim ownMap As SKBitmap = Nothing
            Try
                If depth.Width <> source.Width OrElse depth.Height <> source.Height Then
                    ownMap = New SKBitmap(New SKImageInfo(source.Width, source.Height,
                                                               SKColorType.Alpha8, SKAlphaType.Premul))
                    If Not depth.ScalePixels(ownMap, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                    map = ownMap
                End If

                ' Mit Scheibenkernen fallen Stufenuebergaenge eher auf als bei weichen Glocken -
                ' eine Scheibe hat einen klaren Rand, und zwei Scheiben verschiedener Groesse
                ' nebeneinander sieht man. Deshalb mehr Stufen als bei der ersten Fassung.
                Const Steps As Integer = 8
                Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
                Using canvas = New SKCanvas(result)
                    canvas.Clear(SKColors.Transparent)
                    ' Stufe 0 ist das scharfe Bild, darueber jede weitere Stufe staerker verwischt.
                    canvas.DrawBitmap(source, 0, 0)
                    For stufe = 1 To Steps
                        Dim share = stufe / CDbl(Steps)
                        ' Die Maske dieser Stufe: wie weit ist dieser Punkt von der Fokusebene weg,
                        ' gemessen in Anteilen des Uebergangs, geklemmt auf diese Stufe.
                        Using mask = StepMask(map, from, bis, uebergang, share, 1.0 / Steps)
                            If mask Is Nothing Then Continue For
                            Using blurred = BlurWithDisc(source, maxRadius * share,
                                                                  cornerCount, highlightsPct)
                                If blurred Is Nothing Then Continue For
                                ' Die verwischte Fassung mit der Stufenmaske daruebermischen.
                                Using p3 = New SKPaint()
                                    Using shader = SKShader.CreateBitmap(blurred, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp)
                                        p3.Shader = shader
                                        canvas.DrawBitmap(mask, 0, 0, p3)
                                    End Using
                                End Using
                            End Using
                        End Using
                    Next
                End Using
                Return result
            Finally
                ownMap?.Dispose()
            End Try
        End Function

        ''' <summary>Wie stark diese Unschaerfestufe an jedem Punkt beitraegt. Die Stufen ueberlappen
        ''' sich bewusst und laufen weich aus - ohne das saehe man ihre Grenzen als Ringe.</summary>
        Private Shared Function StepMask(depth As SKBitmap, fromPct As Double, toPct As Double,
                                            transitionPct As Double,
                                            share As Double, width As Double) As SKBitmap
            Dim w = depth.Width, h = depth.Height
            Dim output = New SKBitmap(New SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul))
            Dim buffer(w * h - 1) As Byte
            Dim empty = True
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim t = depth.GetPixel(x, y).Alpha / 255.0 * 100.0
                    ' Abstand aus dem scharfen BAND heraus, auf 0..1 gebracht. Innerhalb ist er null
                    ' - dort bleibt es scharf, egal wie breit das Band ist.
                    Dim outside = 0.0
                    If t < fromPct Then
                        outside = fromPct - t
                    ElseIf t > toPct Then
                        outside = t - toPct
                    End If
                    Dim d = Math.Min(1.0, outside / transitionPct)
                    ' Dreieck um diese Stufe herum: voll bei "anteil", null eine Stufenbreite daneben.
                    Dim g = 1.0 - Math.Min(1.0, Math.Abs(d - share) / width)
                    If g > 0.0 Then empty = False
                    buffer(y * w + x) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(g * 255.0)))))
                Next
            Next
            If empty Then
                output.Dispose()
                Return Nothing
            End If
            Runtime.InteropServices.Marshal.Copy(buffer, 0, output.GetPixels(), buffer.Length)
            Return output
        End Function

    End Class

End Namespace
