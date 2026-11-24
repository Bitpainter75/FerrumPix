Option Strict On
Option Explicit On

Imports System.Collections.Generic
Imports System.Linq
Imports SkiaSharp

' Die Lichtquellen des Werkzeugs Beleuchtung als Stufe der Pixelkette.
'
' Gerechnet wird auf den SZENENWERTEN, wie die Belichtung der RAW-Stufe: jeder Bildpunkt geht zurueck
' durch die Tonkurve und ihre Schulter (RawStageExposureAt rechnet denselben Weg), wird dort mit
' seinem Licht multipliziert und laeuft wieder vorwaerts. Ein helles Licht rollt so durch dieselbe
' Schulter wie das ganze Bild, statt auf dem fertigen Bild ins Weiss zu laufen. Ein dunkler Stoff
' bleibt unter Licht dunkel, ein heller wird hell, wie bei einem Licht, das bei der Aufnahme da war;
' eine negative Leuchtkraft ist ein Schatten.
'
' Je Kanal: Szenenwert mal (Umgebung plus Summe der Lichter), das Licht als Zuwachs (2 hoch
' Leuchtkraft minus 1) mal Form mal Farbe. Die Farbe ist auf gleiche Helligkeit gebracht, ein blaues
' Licht ist also nicht schwaecher als ein weisses. Die Umgebung hellt oder dunkelt das ganze Bild
' vorher; abgedunkelt und mit Lichtern entsteht eine neu beleuchtete Szene.
'
' Mit der Tiefenkarte steht die Szene im Raum: jede Quelle hat eine Hoehe ueber ihr, die Flaechen
' bekommen eine Ausrichtung aus dem Gefaelle der Tiefe (was dem Licht zugewandt ist, wird hell), und
' auf Wunsch wirft ein Licht Schatten hinter Gegenstaende, die vor ihm stehen.
'
' Die Stufe liegt VOR der Punktkette (ImageProcessor.ApplyPointChainStageCore): Belichtung,
' Tonregler, Kurven und Farbe arbeiten danach auf dem beleuchteten Bild. Bei einem Filmnegativ
' danach, denn vor der Umkehr wuerde ein Licht abdunkeln.
Namespace Services

    Partial Public Class ImageProcessor

        ' DIE TIEFENKARTE DER LICHTER. Die Kette ist statisch und bekommt nur das Rezept; die Karte
        ' rechnet der Editor (Tiefenmodell) und legt sie hier ab. Das Rezept traegt im nicht
        ' gespeicherten LightDepthToken, zu welcher Karte es gehoert. Passt das Zeichen nicht (ein
        ' anderes Bild im Stapel, eine Karte von vor einer Geometrieaenderung), rechnen die Lichter
        ' ohne Tiefe. Abgelegt wird eine verkleinerte Kopie: gelesen wird nach Prozent.
        '
        ' Die Kopie bleibt so fein, dass die Kanten der Karte (der Editor liefert sie auf die Kanten
        ' des Fotos ausgerichtet) am Bild noch scharf liegen: an ihr haengen das Tiefengewicht und
        ' der Rand von Relief und Schatten. Die Flaechen und die Schatten selbst rechnet DepthField
        ' auf einer groeberen, geglaetteten Fassung.
        Private Shared ReadOnly _lightDepthLock As New Object()
        Private Shared _lightDepthToken As String = ""
        Private Shared _lightDepth As Byte()
        Private Shared _lightDepthWidth As Integer
        Private Shared _lightDepthHeight As Integer
        Private Const LightDepthEdge As Integer = 2048
        ''' <summary>Die Szene im Raum zur abgelegten Karte, beim ersten Licht mit Tiefe gebaut.</summary>
        Private Shared _lightField As DepthField
        ''' <summary>Relief und Schatten je Licht auf der groben Karte, nach Tiefenzeichen und
        ''' Signatur des Lichts: Vorschau, Zoom-Detail und Ausgabe rechnen dasselbe Licht mehrfach.</summary>
        Private Shared ReadOnly _lightModulations As New Dictionary(Of String, Single())()
        Private Const LightModulationLimit As Integer = 16

        ''' <summary>Legt die Tiefenkarte (Alpha8, 255 = nah) fuer die Lichter ab. Nothing raeumt sie weg.</summary>
        Public Shared Sub SetLightDepthMap(token As String, depth As SKBitmap)
            Dim bytes As Byte() = Nothing
            Dim w = 0, h = 0
            If depth IsNot Nothing AndAlso depth.Width > 0 AndAlso depth.Height > 0 Then
                Dim scale = Math.Min(1.0, LightDepthEdge / CDbl(Math.Max(depth.Width, depth.Height)))
                w = Math.Max(1, CInt(Math.Round(depth.Width * scale)))
                h = Math.Max(1, CInt(Math.Round(depth.Height * scale)))
                Using small = New SKBitmap(New SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul))
                    If ImageProcessor.ScalePixelsFast(depth, small, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) Then
                        ' Zeile fuer Zeile: die Zeilen einer Alpha8-Karte koennen aufgefuellt sein.
                        Dim stride = small.RowBytes
                        Dim raw = small.Bytes
                        bytes = New Byte(w * h - 1) {}
                        For y = 0 To h - 1
                            Array.Copy(raw, y * stride, bytes, y * w, w)
                        Next
                    End If
                End Using
            End If
            SyncLock _lightDepthLock
                _lightDepthToken = If(bytes Is Nothing, "", If(token, ""))
                _lightDepth = bytes
                _lightDepthWidth = w
                _lightDepthHeight = h
                _lightField = Nothing
                _lightModulations.Clear()
            End SyncLock
        End Sub

        ''' <summary>Die Szene im Raum fuer dieses Zeichen, oder Nothing ohne passende Karte. Gebaut
        ''' wird ausserhalb der Sperre: das Glaetten dauert, und eine zweite Rechnung nebenher
        ''' liefert dasselbe.</summary>
        Private Shared Function LightFieldFor(token As String) As DepthField
            Dim bytes As Byte()
            Dim w, h As Integer
            SyncLock _lightDepthLock
                If _lightDepth Is Nothing OrElse String.IsNullOrEmpty(token) OrElse
                   Not String.Equals(_lightDepthToken, token, StringComparison.Ordinal) Then Return Nothing
                If _lightField IsNot Nothing Then Return _lightField
                bytes = _lightDepth : w = _lightDepthWidth : h = _lightDepthHeight
            End SyncLock
            Dim field = New DepthField(token, bytes, w, h)
            SyncLock _lightDepthLock
                If String.Equals(_lightDepthToken, token, StringComparison.Ordinal) AndAlso _lightField Is Nothing Then _lightField = field
            End SyncLock
            Return field
        End Function

        ''' <summary>Relief und Schatten eines Lichts auf der groben Karte, aus dem Zwischenspeicher
        ''' oder neu gerechnet. Nothing, wenn das Licht beides nicht braucht.</summary>
        Private Shared Function LightModulationFor(field As DepthField, source As LightSource, light As PreparedLight,
                                                   width As Integer, height As Integer) As Single()
            Dim wantsShadow = source.CastShadows AndAlso light.Ev > 0
            If light.Shading <= 0 AndAlso Not wantsShadow Then Return Nothing
            Dim key = field.Token & "|" & source.Signature()
            Dim cached As Single() = Nothing
            SyncLock _lightDepthLock
                If _lightModulations.TryGetValue(key, cached) Then Return cached
            End SyncLock
            Dim map = field.Modulation(light, field.LightPosition(light, width, height), wantsShadow)
            SyncLock _lightDepthLock
                If String.Equals(_lightDepthToken, field.Token, StringComparison.Ordinal) Then
                    If _lightModulations.Count >= LightModulationLimit Then _lightModulations.Clear()
                    _lightModulations(key) = map
                End If
            End SyncLock
            Return map
        End Function

        ''' <summary>Liegt fuer dieses Zeichen eine Karte bereit?</summary>
        Public Shared Function HasLightDepthMap(token As String) As Boolean
            SyncLock _lightDepthLock
                Return _lightDepth IsNot Nothing AndAlso Not String.IsNullOrEmpty(token) AndAlso
                       String.Equals(_lightDepthToken, token, StringComparison.Ordinal)
            End SyncLock
        End Function

        ''' <summary>Gibt es ein Licht, das etwas tut?</summary>
        Friend Shared Function HasActiveLightSources(adj As ImageAdjustments) As Boolean
            If adj Is Nothing Then Return False
            If Math.Abs(adj.LightAmbient) > 0.001 Then Return True
            Return adj.LightSources IsNot Nothing AndAlso
                   adj.LightSources.Any(Function(l) l IsNot Nothing AndAlso l.Enabled AndAlso
                                                    (Math.Abs(l.Intensity) > 0.001 OrElse l.Glow > 0.01))
        End Function

        ''' <summary>Der Teil des Zwischenspeicher-Schluessels fuer die Lichter. Das Tiefenzeichen
        ''' gehoert dazu: ob eine Karte greift, aendert das Bild.</summary>
        Friend Shared Function LightSourcesKey(adj As ImageAdjustments) As String
            If Not HasActiveLightSources(adj) Then Return ""
            Dim depth = If(If(adj.LightSources, New List(Of LightSource)()).Any(Function(l) l IsNot Nothing AndAlso l.Enabled AndAlso l.UseDepth) AndAlso
                           HasLightDepthMap(adj.LightDepthToken), adj.LightDepthToken, "")
            Return String.Join(";", If(adj.LightSources, New List(Of LightSource)()).Where(Function(l) l IsNot Nothing).Select(Function(l) l.Signature())) &
                   "|a:" & adj.LightAmbient.ToString("R", Globalization.CultureInfo.InvariantCulture) & "|d:" & depth
        End Function

        ''' <summary>Der Schluessel einer Tiefenkarte: alles, was bestimmt, welche Pixel an welcher
        ''' Stelle des Bildes stehen, auf das die Lichter treffen. Bild, Geometriekette, Drehung,
        ''' Spiegelung, Zuschnitt, Leinwand und Stand des Arbeitsbildes (Retusche, Pinsel); die
        ''' Regler nicht, denn die Tiefe aendert sich nicht, wenn jemand die Belichtung zieht.
        ''' Eine Karte gilt nur, solange dieser Schluessel gleich bleibt.</summary>
        Friend Shared Function LightDepthKey(imagePath As String, adj As ImageAdjustments) As String
            If adj Is Nothing OrElse String.IsNullOrWhiteSpace(imagePath) Then Return ""
            Dim inv = Globalization.CultureInfo.InvariantCulture
            Return String.Join("|", imagePath, GeometryOperationsKey(adj.GeometryOperations),
                               adj.RotationDegrees.ToString(inv), adj.StraightenDegrees.ToString("R", inv),
                               adj.StraightenExpandCanvas, adj.StraightenAutoCrop, adj.FlipHorizontal, adj.FlipVertical,
                               adj.CropLeftPercent.ToString("R", inv), adj.CropTopPercent.ToString("R", inv),
                               adj.CropRightPercent.ToString("R", inv), adj.CropBottomPercent.ToString("R", inv),
                               adj.CanvasWidth.ToString(inv), adj.CanvasHeight.ToString(inv), adj.CanvasAnchor,
                               adj.WorkingImageVersion.ToString(inv), adj.SourceWidthPixels.ToString(inv),
                               adj.SourceHeightPixels.ToString(inv))
        End Function

        ''' <summary>Die Farbe einer Farbtemperatur, auf den hellsten Kanal normiert (verbreitete
        ''' Naeherungsformel aus Messwerten des Schwarzkoerpers, fuer 1000 bis 40000 K).</summary>
        Friend Shared Function KelvinToRgb(kelvin As Double) As (R As Double, G As Double, B As Double)
            Dim t = Math.Max(1000.0, Math.Min(40000.0, kelvin)) / 100.0
            Dim r, g, b As Double
            If t <= 66 Then
                r = 255
                g = 99.4708025861 * Math.Log(t) - 161.1195681661
                b = If(t <= 19, 0, 138.5177312231 * Math.Log(t - 10) - 305.0447927307)
            Else
                r = 329.698727446 * Math.Pow(t - 60, -0.1332047592)
                g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492)
                b = 255
            End If
            r = Math.Max(0, Math.Min(255, r)) : g = Math.Max(0, Math.Min(255, g)) : b = Math.Max(0, Math.Min(255, b))
            Dim m = Math.Max(1.0, Math.Max(r, Math.Max(g, b)))
            Return (r / m, g / m, b / m)
        End Function

        Private NotInheritable Class PreparedLight
            Public Kind As String
            Public Cx, Cy, Radius, Ev As Double
            Public DirX, DirY As Double
            Public HalfCone, InnerCone As Double
            Public Ex, Ey, CosA, SinA As Double
            Public Soft As Double
            Public Falloff As String
            ''' <summary>Die Farbe auf den hellsten Kanal gebracht (fuer die Toenung des Schattens)
            ''' und auf gleiche Helligkeit gebracht (fuer das Licht), beides linear.</summary>
            Public Cr, Cg, Cb As Double
            Public Wr, Wg, Wb As Double
            Public GlowMode As Boolean
            Public GlowAdd As Double
            Public HaloStrength, HaloRadius As Double
            Public RayCount, RayContrast, RayPhase1, RayPhase2, RayPhase3 As Double
            Public UseDepth As Boolean
            Public DepthAtLight, DepthSigma As Double
            Public Reach2 As Double
            ''' <summary>Hoehe der Quelle ueber der Szene in Bildpunkten, und ihr Quadrat.</summary>
            Public Height, Height2 As Double
            ''' <summary>Wie stark die Ausrichtung der Flaechen formt, 0 bis 1; 0 ohne Tiefe.</summary>
            Public Shading As Double
            ''' <summary>Wie weich der Rand des geworfenen Schattens auslaeuft, 0 bis 1.</summary>
            Public ShadowSoft As Double
            ''' <summary>Relief und Schatten zusammen auf der groben Karte von DepthField: der Faktor,
            ''' mit dem das Licht an einer Stelle ankommt (1 = wie ohne, 0 = nichts, ueber 1 auf
            ''' einer dem Licht zugeneigten Flaeche); Nothing ohne beides.</summary>
            Public Modulation As Single()
        End Class

        Private Shared Function SmoothStep(edge0 As Double, edge1 As Double, x As Double) As Double
            If edge1 <= edge0 Then Return If(x < edge0, 0.0, 1.0)
            Dim t = Math.Max(0.0, Math.Min(1.0, (x - edge0) / (edge1 - edge0)))
            Return t * t * (3.0 - 2.0 * t)
        End Function

        ''' <summary>Abfall ueber die Reichweite, r = Abstand / Reichweite; jenseits von 1 ist
        ''' Schluss, damit ein Licht nicht das ganze Bild ueberzieht. <paramref name="lifted"/> ist
        ''' der Abfall "Natuerlich" im Raum: der Abstand zur Quelle mit ihrer Hoehe gerechnet, im
        ''' Kern auf 1 gebracht. Eine hoch haengende Quelle gibt einen breiten, flachen Kegel.</summary>
        Private Shared Function LightFalloffAt(r As Double, light As PreparedLight, lifted As Double) As Double
            If r >= 1.0 Then Return 0.0
            Select Case light.Falloff
                Case LightFalloffs.Soft
                    Return 1.0 - SmoothStep(0.0, 1.0, r)
                Case LightFalloffs.Hard
                    Dim inner = 1.0 - Math.Max(0.04, light.Soft) * 0.85
                    Return 1.0 - SmoothStep(inner, 1.0, r)
                Case Else
                    Return lifted * (1.0 - SmoothStep(0.6, 1.0, r))
            End Select
        End Function

        ''' <summary>Der Abfall nach Abstand im Raum: (1 + 9 h^2) / (1 + 9 (d^2 + h^2)), Abstaende in
        ''' Einheiten der Reichweite, h die Hoehe der Quelle. Im Kern 1, dann wie eine echte Quelle
        ''' mit dem Quadrat des Abstands.</summary>
        Private Shared Function LiftedFalloff(d2 As Double, light As PreparedLight) As Double
            Dim r2 = light.Radius * light.Radius
            Dim h2 = light.Height2 / r2
            Return (1.0 + 9.0 * h2) / (1.0 + 9.0 * (d2 / r2 + h2))
        End Function

        Private Shared Function LightShapeAt(light As PreparedLight, px As Double, py As Double) As Double
            Dim dx = px - light.Cx, dy = py - light.Cy
            Select Case light.Kind
                Case LightSourceKinds.Streak
                    ' Licht von einer Linie durch die Quelle, in Richtung DirX/DirY. Davor voll,
                    ' dahinter ueber die Reichweite abnehmend.
                    Dim t = (dx * light.DirX + dy * light.DirY) / light.Radius
                    If t <= 0 Then Return 1.0
                    Dim along = t * light.Radius
                    Return LightFalloffAt(Math.Min(0.9999, t), light, LiftedFalloff(along * along, light))
                Case LightSourceKinds.Area
                    Dim lx = dx * light.CosA + dy * light.SinA
                    Dim ly = -dx * light.SinA + dy * light.CosA
                    Dim rr = Math.Sqrt((lx / light.Ex) * (lx / light.Ex) + (ly / light.Ey) * (ly / light.Ey))
                    If rr >= 1.0 Then Return 0.0
                    If light.Falloff = LightFalloffs.Natural Then Return Math.Exp(-3.0 * rr * rr) * (1.0 - SmoothStep(0.7, 1.0, rr))
                    Dim inner = Math.Max(0.0, 1.0 - Math.Max(0.04, light.Soft))
                    If light.Falloff = LightFalloffs.Hard Then inner = Math.Max(inner, 0.85)
                    Return 1.0 - SmoothStep(inner, 1.0, rr)
                Case Else
                    Dim d2 = dx * dx + dy * dy
                    If d2 >= light.Reach2 Then Return 0.0
                    Dim r = Math.Sqrt(d2) / light.Radius
                    Dim f = LightFalloffAt(r, light, LiftedFalloff(d2, light))
                    If f <= 0 Then Return 0.0
                    If light.Kind = LightSourceKinds.Spot Then
                        Dim len = Math.Sqrt(d2)
                        If len > 0.0001 Then
                            Dim cosAngle = (dx * light.DirX + dy * light.DirY) / len
                            Dim a = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosAngle)))
                            f *= 1.0 - SmoothStep(light.InnerCone, light.HalfCone, a)
                        End If
                    ElseIf light.Kind = LightSourceKinds.Rays Then
                        Dim theta = Math.Atan2(dy, dx)
                        Dim n = light.RayCount
                        Dim p = 0.5 + 0.5 * (0.55 * Math.Sin(n * theta + light.RayPhase1) +
                                             0.3 * Math.Sin(1.73 * n * theta + light.RayPhase2) +
                                             0.15 * Math.Sin(2.91 * n * theta + light.RayPhase3))
                        p = Math.Max(0.0, Math.Min(1.0, p))
                        f *= (1.0 - light.RayContrast) + light.RayContrast * p * p * p
                    End If
                    Return f
            End Select
        End Function

        ''' <summary>Die sRGB-Werte einer Farbe (0 bis 1) linear.</summary>
        Private Shared Function ColorToLinear(c As Double) As Double
            Return If(c <= 0.04045, c / 12.92, Math.Pow((c + 0.055) / 1.055, 2.4))
        End Function

        Private Shared Function PrepareLight(light As LightSource, width As Integer, height As Integer,
                                             field As DepthField) As PreparedLight
            Dim s = Math.Min(width, height)
            Dim p As New PreparedLight With {
                .Kind = LightSourceKinds.Normalize(light.Kind),
                .Cx = width * Math.Max(-50.0, Math.Min(150.0, light.XPercent)) / 100.0,
                .Cy = height * Math.Max(-50.0, Math.Min(150.0, light.YPercent)) / 100.0,
                .Radius = Math.Max(1.0, s * Math.Max(1.0, Math.Min(300.0, light.RadiusPercent)) / 100.0),
                .Ev = Math.Max(-6.0, Math.Min(6.0, light.Intensity)),
                .Soft = Math.Max(0.0, Math.Min(100.0, light.Softness)) / 100.0,
                .Falloff = If(String.IsNullOrEmpty(light.Falloff), LightFalloffs.Natural, light.Falloff),
                .GlowMode = light.BlendMode = LightBlendModes.Glow
            }
            p.Reach2 = p.Radius * p.Radius
            p.Height = s * Math.Max(0.0, Math.Min(200.0, light.HeightPercent)) / 100.0
            p.Height2 = p.Height * p.Height
            Dim angle = light.AngleDegrees * Math.PI / 180.0
            p.DirX = Math.Cos(angle) : p.DirY = Math.Sin(angle)
            p.CosA = p.DirX : p.SinA = p.DirY
            p.HalfCone = Math.Max(1.0, Math.Min(179.0, light.ConeDegrees)) * Math.PI / 360.0
            p.InnerCone = p.HalfCone * (1.0 - Math.Max(0.05, p.Soft) * 0.8)
            Dim aspect = Math.Max(10.0, Math.Min(1000.0, light.AspectPercent)) / 100.0
            p.Ex = p.Radius * Math.Sqrt(aspect) : p.Ey = p.Radius / Math.Sqrt(aspect)
            Dim rgb As (R As Double, G As Double, B As Double)
            If light.UseTemperature Then
                rgb = KelvinToRgb(light.TemperatureKelvin)
            Else
                Dim c As SKColor
                If Not SKColor.TryParse(If(light.Color, ""), c) Then c = SKColors.White
                Dim m = Math.Max(1.0, Math.Max(CDbl(c.Red), Math.Max(CDbl(c.Green), CDbl(c.Blue))))
                rgb = (c.Red / m, c.Green / m, c.Blue / m)
            End If
            p.Cr = rgb.R : p.Cg = rgb.G : p.Cb = rgb.B
            ' Auf gleiche Helligkeit: ein farbiges Licht hebt die Helligkeit so wie ein weisses, sonst
            ' waere ein blaues kaum zu sehen. Gedeckelt, damit ein fast reines Blau nicht ins
            ' Unendliche waechst.
            Dim lr = ColorToLinear(rgb.R), lg = ColorToLinear(rgb.G), lb = ColorToLinear(rgb.B)
            Dim luma = Math.Max(0.25, 0.2126 * lr + 0.7152 * lg + 0.0722 * lb)
            p.Wr = lr / luma : p.Wg = lg / luma : p.Wb = lb / luma
            ' Leuchten: ein sichtbarer Schein, so stark wie ein Licht dieser Stufen auf Mittelgrau.
            p.GlowAdd = If(p.GlowMode AndAlso p.Ev > 0, (Math.Pow(2.0, p.Ev) - 1.0) * 0.18, 0.0)
            p.HaloStrength = Math.Max(0.0, Math.Min(100.0, light.Glow)) / 100.0 * 0.9 * Math.Max(0.4, Math.Min(2.0, Math.Max(0.0, p.Ev)))
            p.HaloRadius = Math.Max(2.0, s * 0.035 + p.Radius * 0.08)
            p.RayCount = Math.Max(3.0, Math.Min(60.0, light.RayCount))
            p.RayContrast = Math.Max(0.0, Math.Min(100.0, light.RayContrast)) / 100.0
            ' Die Strahlen sollen je Licht anders fallen, aber bei jedem Rendern gleich: die Phasen
            ' haengen an der Kennung.
            Dim seed = If(light.Id, "").Aggregate(17, Function(acc, ch) (acc * 31 + AscW(ch)) And &HFFFFFF)
            p.RayPhase1 = (seed Mod 997) / 997.0 * 6.283
            p.RayPhase2 = ((seed \ 997) Mod 991) / 991.0 * 6.283
            p.RayPhase3 = ((seed \ 7) Mod 983) / 983.0 * 6.283
            If light.UseDepth AndAlso field IsNot Nothing Then
                p.UseDepth = True
                p.DepthAtLight = field.DepthAt(light.XPercent / 100.0, light.YPercent / 100.0)
                p.DepthSigma = 0.04 + Math.Max(0.0, Math.Min(100.0, light.DepthReach)) / 100.0 * 0.6
                p.Shading = Math.Max(0.0, Math.Min(100.0, light.Shading)) / 100.0
                p.ShadowSoft = Math.Max(0.0, Math.Min(100.0, light.ShadowSoftness)) / 100.0
                p.Modulation = LightModulationFor(field, light, p, width, height)
            End If
            Return p
        End Function

        ' ── Die Szene im Raum: Tiefe, Flaechen, Schatten ─────────────────────────────

        ''' <summary>Die Tiefenkarte der Lichter als Hoehenfeld, dazu die Ausrichtung der Flaechen.
        ''' Koordinaten in Einheiten der langen Bildseite; die Tiefe (1 = nah) reicht ueber
        ''' <see cref="Relief"/> davon in den Raum.
        '''
        ''' Zwei Fassungen der Karte: die feine, wie sie abgelegt ist (Kanten auf den Kanten des
        ''' Fotos), und eine grobe (<see cref="CoarseEdge"/> Punkte), auf der Flaechen und Schatten
        ''' gerechnet werden. Die grobe ist kantenerhaltend geglaettet: die feine traegt nur 256
        ''' Stufen, und aus deren Treppen wurden im Relief Hoehenlinien quer durch jede sanft
        ''' geneigte Flaeche. Gelesen wird die grobe mit der feinen Tiefe als Fuehrung
        ''' (<see cref="SampleJoint"/>), damit Relief und Schatten am Umriss eines Gegenstands
        ''' enden und nicht ein paar Bildpunkte daneben.</summary>
        Private NotInheritable Class DepthField
            ''' <summary>Wie tief die Szene ist, in Einheiten der langen Seite. Ein Tiefenmodell sagt
            ''' nur, was naeher liegt, nicht wie viel; ein halbes Bild Tiefe gibt Flaechen eine
            ''' sichtbare, aber keine uebertriebene Neigung.</summary>
            Public Const Relief As Double = 0.5
            ''' <summary>Laengere Seite der groben Karte.</summary>
            Public Const CoarseEdge As Integer = 640
            ''' <summary>Ab diesem Tiefensprung zwischen zwei Nachbarstellen der groben Karte gilt
            ''' die Stelle als Umriss und nicht als Flaeche: das Gefaelle ueber einen Umriss hinweg
            ''' ist keine Neigung, sondern der Abstand zweier Gegenstaende.</summary>
            Private Const EdgeStep As Single = 0.006F
            ''' <summary>Wie tief die Szene fuer die Schatten ist. Flacher als fuer das Relief: mit
            ''' der vollen Tiefe stand ein Motiv ein Drittel der Bildbreite vor seinem Hintergrund,
            ''' und eine Quelle in ueblicher Hoehe warf seinen Schatten ueber das halbe Bild.</summary>
            Private Const ShadowRelief As Double = 0.2
            ''' <summary>Tiefenabstand, ueber den das Glaetten und das gefuehrte Lesen nicht
            ''' hinweggreifen.</summary>
            Private Const RangeSigma As Single = 0.03F
            Public ReadOnly Token As String
            Public ReadOnly FineWidth, FineHeight As Integer
            Public ReadOnly Fine As Byte()
            Public ReadOnly Width, Height As Integer
            Public ReadOnly Depth As Single()
            Public ReadOnly Nx, Ny, Nz As Single()
            ''' <summary>Die grobe Tiefe in halbierten Stufen (0 ist <see cref="Depth"/>), je Stelle
            ''' das Naechste ihres Bereichs. Ein Schattenstrahl, dessen Schritte mehrere Stellen
            ''' weit sind, liest die passende Stufe und uebersieht so keinen schmalen Gegenstand.</summary>
            Private ReadOnly Nearest As Single()()
            Private ReadOnly NearestWidth, NearestHeight As Integer()
            ''' <summary>Laenge einer Stelle der groben Karte in Einheiten der langen Seite.</summary>
            Public ReadOnly CellX, CellY As Double

            Public Sub New(token As String, bytes As Byte(), fineWidth As Integer, fineHeight As Integer)
                Me.Token = token
                Fine = bytes : Me.FineWidth = fineWidth : Me.FineHeight = fineHeight
                Dim fineLong = CDbl(Math.Max(fineWidth, fineHeight))
                Dim scale = Math.Min(1.0, CoarseEdge / fineLong)
                Dim w = Math.Max(1, CInt(Math.Round(fineWidth * scale)))
                Dim h = Math.Max(1, CInt(Math.Round(fineHeight * scale)))
                Width = w : Height = h
                CellX = fineWidth / fineLong / w
                CellY = fineHeight / fineLong / h
                ' Flaechentreu verkleinert und als Gleitkomma behalten: der Mittelwert ueber die
                ' feinen Stellen traegt Zwischenwerte, die ein zweites Runden auf das Byte wegnaehme.
                ' Laeuft ein Umriss durch die Stelle, zaehlt nur die Seite, auf der ihre Mitte liegt:
                ' ein Mittel aus Gegenstand und Hintergrund gehoerte zu keinem von beiden, und was
                ' auf so einer Stelle gerechnet wird, stand als gesprenkelter Rand am Umriss.
                Const SplitSpan As Integer = 16
                Dim raw(w * h - 1) As Single
                ImageProcessor.ForEachRow(w, h,
                    Sub(y)
                        Dim fy0 = CInt(Math.Floor(y * CDbl(fineHeight) / h))
                        Dim fy1 = Math.Max(fy0 + 1, Math.Min(fineHeight, CInt(Math.Floor((y + 1) * CDbl(fineHeight) / h))))
                        For x = 0 To w - 1
                            Dim fx0 = CInt(Math.Floor(x * CDbl(fineWidth) / w))
                            Dim fx1 = Math.Max(fx0 + 1, Math.Min(fineWidth, CInt(Math.Floor((x + 1) * CDbl(fineWidth) / w))))
                            Dim acc = 0, low = 255, high = 0
                            For fy = fy0 To fy1 - 1
                                Dim o = fy * fineWidth
                                For fx = fx0 To fx1 - 1
                                    Dim b = CInt(bytes(o + fx))
                                    acc += b
                                    If b < low Then low = b
                                    If b > high Then high = b
                                Next
                            Next
                            Dim count = (fy1 - fy0) * (fx1 - fx0)
                            If high - low > SplitSpan Then
                                Dim middle = (low + high) * 0.5
                                Dim near = bytes(((fy0 + fy1) \ 2) * fineWidth + (fx0 + fx1) \ 2) >= middle
                                acc = 0 : count = 0
                                For fy = fy0 To fy1 - 1
                                    Dim o = fy * fineWidth
                                    For fx = fx0 To fx1 - 1
                                        Dim b = CInt(bytes(o + fx))
                                        If (b >= middle) = near Then
                                            acc += b : count += 1
                                        End If
                                    Next
                                Next
                            End If
                            raw(y * w + x) = acc / (255.0F * Math.Max(1, count))
                        Next
                    End Sub)
                ' Mit wachsendem Abstand der Nachbarn: glaettet auch die breiten Treppen einer
                ' kaum geneigten Flaeche, ohne dass der Aufwand mit der Reichweite waechst.
                Dim smooth = raw
                For Each spacing In {1, 2, 4, 8, 2, 1}
                    smooth = SmoothKeepingEdges(smooth, w, h, spacing)
                Next
                Depth = smooth
                ' Die Tiefe in halbierten Stufen, je Stelle das Naechste aus ihren vier Stellen der
                ' Stufe darunter: fuer die weiten Schritte der Schattenstrahlen.
                Dim levels As New List(Of Single()) From {smooth}
                Dim widths As New List(Of Integer) From {w}
                Dim heights As New List(Of Integer) From {h}
                While levels.Count < 6 AndAlso Math.Min(widths(widths.Count - 1), heights(heights.Count - 1)) >= 8
                    Dim below = levels(levels.Count - 1)
                    Dim bw = widths(widths.Count - 1), bh = heights(heights.Count - 1)
                    Dim nw = (bw + 1) \ 2, nh = (bh + 1) \ 2
                    Dim level(nw * nh - 1) As Single
                    For y = 0 To nh - 1
                        Dim ya = y * 2, yb = Math.Min(bh - 1, y * 2 + 1)
                        For x = 0 To nw - 1
                            Dim xa = x * 2, xb = Math.Min(bw - 1, x * 2 + 1)
                            level(y * nw + x) = Math.Max(Math.Max(below(ya * bw + xa), below(ya * bw + xb)),
                                                         Math.Max(below(yb * bw + xa), below(yb * bw + xb)))
                        Next
                    Next
                    levels.Add(level) : widths.Add(nw) : heights.Add(nh)
                End While
                Nearest = levels.ToArray() : NearestWidth = widths.ToArray() : NearestHeight = heights.ToArray()
                ReDim Nx(w * h - 1) : ReDim Ny(w * h - 1) : ReDim Nz(w * h - 1)
                Dim d = Depth
                ImageProcessor.ForEachRow(w, h,
                    Sub(y)
                        Dim y0 = Math.Max(0, y - 1), y1 = Math.Min(h - 1, y + 1)
                        For x = 0 To w - 1
                            Dim x0 = Math.Max(0, x - 1), x1 = Math.Min(w - 1, x + 1)
                            Dim i = y * w + x
                            Dim dzx = SurfaceSlope(d(y * w + x0), d(i), d(y * w + x1), x0 < x, x1 > x) * Relief / CellX
                            Dim dzy = SurfaceSlope(d(y0 * w + x), d(i), d(y1 * w + x), y0 < y, y1 > y) * Relief / CellY
                            Dim len = Math.Sqrt(dzx * dzx + dzy * dzy + 1.0)
                            Nx(i) = CSng(-dzx / len) : Ny(i) = CSng(-dzy / len) : Nz(i) = CSng(1.0 / len)
                        Next
                    End Sub)
            End Sub

            ''' <summary>Das Gefaelle der Flaeche an einer Stelle aus ihr und ihren beiden Nachbarn,
            ''' je Kartenstelle. Liegen beide Seiten auf derselben Flaeche, der Mittelwert; springt
            ''' eine Seite (ein Umriss), zaehlt nur die andere, und springen beide, ist die Stelle
            ''' eben. Mit dem Gefaelle ueber den Sprung hinweg stand um jeden Gegenstand eine
            ''' steile Wand, die das Relief als dunklen Rand zeichnete.</summary>
            Private Shared Function SurfaceSlope(before As Single, center As Single, after As Single,
                                                 hasBefore As Boolean, hasAfter As Boolean) As Double
                Dim left = SurfaceShare(center - before), right = SurfaceShare(after - center)
                If hasBefore AndAlso hasAfter Then Return (left + right) * 0.5
                If hasBefore Then Return left
                If hasAfter Then Return right
                Return 0.0
            End Function

            ''' <summary>Was von einem Tiefenschritt als Neigung zaehlt: ein kleiner ganz, ein
            ''' grosser gar nicht, dazwischen stetig. Eine harte Grenze entschied an einem Umriss
            ''' von Stelle zu Stelle anders, und das Relief zeichnete dort Sprenkel.</summary>
            Private Shared Function SurfaceShare([step] As Single) As Double
                Dim q = [step] / EdgeStep
                Return [step] / (1.0 + q * q)
            End Function

            ''' <summary>Glaettet die Tiefe innerhalb einer Flaeche und laesst Umrisse stehen: jeder
            ''' Nachbar zaehlt nach seinem Tiefenabstand zur Stelle (bilateral, 5 mal 5 Nachbarn im
            ''' Abstand <paramref name="spacing"/>).</summary>
            Private Shared Function SmoothKeepingEdges(src As Single(), w As Integer, h As Integer, spacing As Integer) As Single()
                Dim dst(src.Length - 1) As Single
                Dim invSigma2 = 1.0F / (RangeSigma * RangeSigma)
                ImageProcessor.ForEachRow(w, h,
                    Sub(y)
                        For x = 0 To w - 1
                            Dim center = src(y * w + x)
                            Dim acc = 0.0F, weight = 0.0F
                            For dy = -2 To 2
                                Dim yy = y + dy * spacing
                                If yy < 0 OrElse yy >= h Then Continue For
                                Dim o = yy * w
                                For dx = -2 To 2
                                    Dim xx = x + dx * spacing
                                    If xx < 0 OrElse xx >= w Then Continue For
                                    Dim v = src(o + xx)
                                    Dim q = (v - center) * (v - center) * invSigma2
                                    ' Naeherung der Glocke, ohne Exponentialfunktion je Nachbar.
                                    Dim k = 1.0F / (1.0F + q + q * q)
                                    acc += v * k : weight += k
                                Next
                            Next
                            dst(y * w + x) = acc / weight
                        Next
                    End Sub)
                Return dst
            End Function

            ''' <summary>Bilinear, u und v in 0 bis 1.</summary>
            Public Function Sample(map As Single(), u As Double, v As Double) As Double
                Dim fx = Math.Max(0.0, Math.Min(Width - 1.0, u * Width - 0.5))
                Dim fy = Math.Max(0.0, Math.Min(Height - 1.0, v * Height - 0.5))
                Dim x0 = CInt(Math.Floor(fx)), y0 = CInt(Math.Floor(fy))
                Dim x1 = Math.Min(Width - 1, x0 + 1), y1 = Math.Min(Height - 1, y0 + 1)
                Dim wx = fx - x0, wy = fy - y0
                Dim top = map(y0 * Width + x0) + (map(y0 * Width + x1) - map(y0 * Width + x0)) * wx
                Dim bottom = map(y1 * Width + x0) + (map(y1 * Width + x1) - map(y1 * Width + x0)) * wx
                Return top + (bottom - top) * wy
            End Function

            Public Function DepthAt(u As Double, v As Double) As Double
                Return Sample(Depth, Math.Max(0.0, Math.Min(1.0, u)), Math.Max(0.0, Math.Min(1.0, v)))
            End Function

            ''' <summary>Die Tiefe aus der feinen Karte, bilinear, u und v in 0 bis 1.</summary>
            Public Function FineAt(u As Double, v As Double) As Double
                Dim fx = Math.Max(0.0, Math.Min(FineWidth - 1.0, u * FineWidth - 0.5))
                Dim fy = Math.Max(0.0, Math.Min(FineHeight - 1.0, v * FineHeight - 0.5))
                Dim x0 = CInt(Math.Floor(fx)), y0 = CInt(Math.Floor(fy))
                Dim x1 = Math.Min(FineWidth - 1, x0 + 1), y1 = Math.Min(FineHeight - 1, y0 + 1)
                Dim wx = fx - x0, wy = fy - y0
                Dim top = Fine(y0 * FineWidth + x0) + (CDbl(Fine(y0 * FineWidth + x1)) - Fine(y0 * FineWidth + x0)) * wx
                Dim bottom = Fine(y1 * FineWidth + x0) + (CDbl(Fine(y1 * FineWidth + x1)) - Fine(y1 * FineWidth + x0)) * wx
                Return (top + (bottom - top) * wy) / 255.0
            End Function

            ''' <summary>Liest eine Karte in der Groesse der groben Tiefe, gefuehrt von der Tiefe
            ''' des Bildpunkts (<paramref name="depth"/>, aus der feinen Karte): von den vier
            ''' Nachbarstellen zaehlen die, die in der Tiefe zum Bildpunkt passen. Am Umriss eines
            ''' Gegenstands kommt der Wert so von seiner Seite der Kante, statt ueber sie hinweg
            ''' gemittelt zu werden.</summary>
            Public Function SampleJoint(map As Single(), u As Double, v As Double, depth As Double) As Double
                Dim fx = Math.Max(0.0, Math.Min(Width - 1.0, u * Width - 0.5))
                Dim fy = Math.Max(0.0, Math.Min(Height - 1.0, v * Height - 0.5))
                Dim x0 = CInt(Math.Floor(fx)), y0 = CInt(Math.Floor(fy))
                Dim x1 = Math.Min(Width - 1, x0 + 1), y1 = Math.Min(Height - 1, y0 + 1)
                Dim wx = fx - x0, wy = fy - y0
                Dim i00 = y0 * Width + x0, i01 = y0 * Width + x1, i10 = y1 * Width + x0, i11 = y1 * Width + x1
                Dim k00 = (1.0 - wx) * (1.0 - wy) * RangeWeight(Me.Depth(i00) - depth)
                Dim k01 = wx * (1.0 - wy) * RangeWeight(Me.Depth(i01) - depth)
                Dim k10 = (1.0 - wx) * wy * RangeWeight(Me.Depth(i10) - depth)
                Dim k11 = wx * wy * RangeWeight(Me.Depth(i11) - depth)
                Dim sum = k00 + k01 + k10 + k11
                If sum < 0.000001 Then
                    Dim top = map(i00) + (map(i01) - map(i00)) * wx
                    Dim bottom = map(i10) + (map(i11) - map(i10)) * wx
                    Return top + (bottom - top) * wy
                End If
                Return (map(i00) * k00 + map(i01) * k01 + map(i10) * k10 + map(i11) * k11) / sum
            End Function

            Private Shared Function RangeWeight(difference As Double) As Double
                Dim q = difference * difference / (RangeSigma * RangeSigma)
                Return 1.0 / (1.0 + q * q) + 0.0001
            End Function

            ''' <summary>Die Lage der Quelle im Raum: ueber der Tiefe an ihrem Ort, um ihre Hoehe zum
            ''' Betrachter hin. Ein Streiflicht hat keinen Ort, nur eine Richtung (Lx/Ly/Lz als
            ''' Einheitsvektor zur Quelle).</summary>
            Public Function LightPosition(light As PreparedLight, imageWidth As Integer, imageHeight As Integer) As (X As Double, Y As Double, Z As Double, Directional As Boolean)
                Dim longSide = CDbl(Math.Max(imageWidth, imageHeight))
                Dim h = light.Height / longSide
                If light.Kind = LightSourceKinds.Streak Then
                    ' Das Licht kommt entgegen seiner Laufrichtung, unter einem Winkel aus der Hoehe.
                    ' Auch ganz unten nicht flacher als ein tiefer Sonnenstand: sonst reicht jeder
                    ' Schatten ueber das Bild hinaus.
                    Dim lx = -light.DirX, ly = -light.DirY, lz = 0.1 + h * 3.0
                    Dim len = Math.Sqrt(lx * lx + ly * ly + lz * lz)
                    Return (lx / len, ly / len, lz / len, True)
                End If
                Dim z = light.DepthAtLight * Relief + h
                Return (light.Cx / longSide, light.Cy / longSide, z, False)
            End Function

            ''' <summary>Relief und Schatten eines Lichts zusammen, je Stelle der groben Karte: der
            ''' Faktor, mit dem das Licht dort ankommt.</summary>
            Public Function Modulation(light As PreparedLight,
                                       pos As (X As Double, Y As Double, Z As Double, Directional As Boolean),
                                       withShadow As Boolean) As Single()
                Dim w = Width, h = Height
                Dim result(w * h - 1) As Single
                Dim shadow = If(withShadow, ShadowMap(light, pos), Nothing)
                Dim shading = light.Shading
                ImageProcessor.ForEachRow(w, h,
                    Sub(y)
                        For x = 0 To w - 1
                            Dim i = y * w + x
                            Dim m = 1.0
                            If shading > 0 Then m = 1.0 - shading + shading * Facing(i, x, y, pos)
                            If shadow IsNot Nothing Then m *= shadow(i)
                            result(i) = CSng(m)
                        Next
                    End Sub)
                Return result
            End Function

            ''' <summary>Wie viel ein Licht an einer Stelle nach der Ausrichtung der Flaeche gibt,
            ''' im Verhaeltnis zu einer Flaeche, die an derselben Stelle zur Kamera schaut: 1 auf
            ''' einer ebenen Flaeche, mehr auf einer zum Licht geneigten, weniger bis 0 auf einer
            ''' abgewandten. Das Relief formt so nur, was geneigt ist, und macht den Lichtkegel auf
            ''' ebenem Grund nicht enger; ein wenig laeuft das Licht um die Kante herum, sonst
            ''' stuende hinter jeder Rundung eine harte Grenze.</summary>
            Private Function Facing(i As Integer, x As Integer, y As Integer,
                                    pos As (X As Double, Y As Double, Z As Double, Directional As Boolean)) As Double
                Dim lx, ly, lz As Double
                If pos.Directional Then
                    lx = pos.X : ly = pos.Y : lz = pos.Z
                Else
                    lx = pos.X - (x + 0.5) * CellX : ly = pos.Y - (y + 0.5) * CellY
                    lz = pos.Z - Depth(i) * Relief
                    Dim len = Math.Sqrt(lx * lx + ly * ly + lz * lz)
                    If len < 0.000001 Then Return 1.0
                    lx /= len : ly /= len : lz /= len
                End If
                Const Wrap As Double = 0.25
                Const Most As Double = 2.5
                Dim ndl = Nx(i) * lx + Ny(i) * ly + Nz(i) * lz
                Return Math.Min(Most, Math.Max(0.0, ndl + Wrap) / Math.Max(0.2, lz + Wrap))
            End Function

            ''' <summary>Die Schattenkarte eines Lichts, 1 = frei, 0 = verdeckt: fuer jede Stelle
            ''' ein Strahl zur Quelle ueber das Hoehenfeld.
            '''
            ''' Die Schritte liegen an der Stelle selbst dicht und werden zur Quelle hin weiter:
            ''' was einen Schatten wirft, steht meist nah an ihm, und gleich weite Schritte
            ''' uebersprangen schmale Gegenstaende. Die Weiche kommt aus dem Strahl und nicht aus
            ''' einem Weichzeichner danach: wie weit ein Strahl unter der Oberflaeche laufen muss, um
            ''' als verdeckt zu gelten, waechst mit seinem Weg. Am Fuss eines Gegenstands ist der
            ''' Schatten so scharf, weiter weg wird er weich, und die Weiche des Lichts stellt ein,
            ''' wie schnell.
            '''
            ''' Ein Hoehenfeld weiss nicht, wie dick ein Gegenstand ist. Steht die Quelle vor ihm,
            ''' gilt er als massiv bis zum Grund, der Schatten haengt an seinem Umriss. Steht sie
            ''' hinter seiner Vorderseite, ist er nur eine Schale von <c>Shell</c> Dicke: ein Strahl,
            ''' der weit hinter ihm durchlaeuft, ist frei. Das Streiflicht hat keinen Ort und steht
            ''' damit immer davor.</summary>
            Private Function ShadowMap(light As PreparedLight,
                                       pos As (X As Double, Y As Double, Z As Double, Directional As Boolean)) As Single()
                Dim w = Width, h = Height
                Dim result(w * h - 1) As Single
                Dim d = Depth
                Const Steps As Integer = 96
                Const Shell As Double = 0.1
                ' Die Quelle steht um ihre Hoehe ueber der Tiefe an ihrem Ort, in der flacheren
                ' Tiefe der Schatten.
                Dim lampZ = pos.Z - light.DepthAtLight * (Relief - ShadowRelief)
                ' Wie weit unter der Oberflaeche ein Strahl gleich an der Stelle als verdeckt gilt,
                ' und um wie viel das je Wegstrecke waechst.
                ' Die Schattenkante (ShadowSoft) greift hier NICHT ein, sie zeichnet den Schatten am
                ' Ende weich. Zog sie die Weite des Strahls mit auf, frass die Weiche den Schatten
                ' von aussen auf, statt seinen Rand nach beiden Seiten auslaufen zu lassen, und eine
                ' Flaeche, die knapp vor der Quelle liegt und sich selbst beschattet, bekam wieder
                ' Licht.
                Const nearWidth As Double = 0.004
                Dim spread = 0.02 + light.Soft * 0.2
                ImageProcessor.ForEachRow(w, h,
                    Sub(y)
                        For x = 0 To w - 1
                            Dim px = (x + 0.5) * CellX, py = (y + 0.5) * CellY
                            Dim pz = d(y * w + x) * ShadowRelief
                            Dim rx, ry, rz As Double
                            If pos.Directional Then
                                ' Bis ueber den Bildrand: weiter weg verdeckt nichts mehr.
                                Dim reach = 1.2 / Math.Max(0.05, Math.Sqrt(pos.X * pos.X + pos.Y * pos.Y))
                                rx = pos.X * reach : ry = pos.Y * reach : rz = pos.Z * reach
                            Else
                                rx = pos.X - px : ry = pos.Y - py : rz = lampZ - pz
                            End If
                            Dim length = Math.Sqrt(rx * rx + ry * ry + rz * rz)
                            ' Weg des Strahls in Stellen der Karte, fuer die Wahl der Stufe.
                            Dim cells = Math.Sqrt((rx / CellX) * (rx / CellX) + (ry / CellY) * (ry / CellY))
                            ' Versetzt je Stelle, damit die Schritte keine Stufen ins Bild zeichnen.
                            Dim jitter = ((x * 0.06711056 + y * 0.00583715) * 52.9829189) Mod 1.0
                            Dim lit = 1.0
                            For k = 1 To Steps
                                Dim s = (k - jitter) / Steps
                                Dim t = s * Math.Sqrt(s)
                                Dim sx = (px + rx * t) / CellX - 0.5, sy = (py + ry * t) / CellY - 0.5
                                If sx < 0 OrElse sy < 0 OrElse sx > w - 1 OrElse sy > h - 1 Then Exit For
                                ' Die Stufe, deren Stellen so weit sind wie dieser Schritt.
                                Dim stride = cells * 1.5 * Math.Sqrt(s) / Steps
                                Dim level = 0
                                While level < Nearest.Length - 1 AndAlso stride > (1 << level) * 1.5
                                    level += 1
                                End While
                                Dim map = Nearest(level)
                                Dim lw = NearestWidth(level), lh = NearestHeight(level)
                                Dim size = CDbl(1 << level)
                                Dim lx = Math.Max(0.0, Math.Min(lw - 1.0, (sx + 0.5) / size - 0.5))
                                Dim ly = Math.Max(0.0, Math.Min(lh - 1.0, (sy + 0.5) / size - 0.5))
                                Dim x0 = CInt(Math.Floor(lx)), y0 = CInt(Math.Floor(ly))
                                Dim x1 = Math.Min(lw - 1, x0 + 1), y1 = Math.Min(lh - 1, y0 + 1)
                                Dim wx = lx - x0, wy = ly - y0
                                Dim top = map(y0 * lw + x0) + (map(y0 * lw + x1) - map(y0 * lw + x0)) * wx
                                Dim bottom = map(y1 * lw + x0) + (map(y1 * lw + x1) - map(y1 * lw + x0)) * wx
                                Dim surface = (top + (bottom - top) * wy) * ShadowRelief
                                Dim below = surface - (pz + rz * t)
                                If below <= 0 Then Continue For
                                Dim blocked = Math.Min(1.0, below / (nearWidth + spread * length * t))
                                If Not pos.Directional AndAlso surface > lampZ Then blocked *= 1.0 - SmoothStep(Shell * 0.6, Shell, below)
                                If 1.0 - blocked < lit Then
                                    lit = 1.0 - blocked
                                    If lit <= 0.002 Then Exit For
                                End If
                            Next
                            result(y * w + x) = CSng(Math.Max(0.0, lit))
                        Next
                    End Sub)
                ' Gegen das Korn aus dem Versatz der Schritte (eine knappe halbe Stelle, auch bei
                ' Schattenkante 0), und darueber die Schattenkante: stetig mit dem Regler bis gut
                ' ein Hundertstel der langen Seite, zweimal fuer einen runden Verlauf. Immer
                ' innerhalb der Flaeche: auch das wenige gegen das Korn soll nicht ueber einen
                ' Umriss mitteln, und eine Breite in ganzen Stellen liess den Regler im unteren
                ' Fuenftel ohne Wirkung.
                Dim sigma = 0.45 + light.ShadowSoft * Math.Max(w, h) * 0.007
                Return BlurWithinSurface(BlurWithinSurface(result, sigma), sigma)
            End Function

            ''' <summary>Zeichnet eine Karte der groben Groesse weich (Glocke mit der Breite
            ''' <paramref name="sigma"/> in Stellen), ohne ueber einen Umriss zu greifen: ein Nachbar
            ''' zaehlt nur, wenn er in der Tiefe zur Stelle passt. Sonst liefe das Licht auf einem
            ''' Gegenstand in den Schatten hinter ihm, und der Schatten bekaeme am Umriss einen
            ''' hellen Rand.</summary>
            Private Function BlurWithinSurface(src As Single(), sigma As Double) As Single()
                Dim w = Width, h = Height
                Dim d = Depth
                Dim radius = Math.Max(1, CInt(Math.Ceiling(sigma * 2.5)))
                Dim kernel(radius) As Double
                For k = 0 To radius
                    kernel(k) = Math.Exp(-(k * k) / (2.0 * sigma * sigma))
                Next
                Dim tmp(src.Length - 1) As Single
                Dim dst(src.Length - 1) As Single
                ImageProcessor.ForEachRow(w, h,
                    Sub(y)
                        For x = 0 To w - 1
                            Dim center = d(y * w + x)
                            Dim acc = 0.0, weight = 0.0
                            For k = Math.Max(0, x - radius) To Math.Min(w - 1, x + radius)
                                Dim g = RangeWeight(d(y * w + k) - center) * kernel(Math.Abs(k - x))
                                acc += src(y * w + k) * g : weight += g
                            Next
                            tmp(y * w + x) = CSng(acc / weight)
                        Next
                    End Sub)
                ImageProcessor.ForEachRow(w, h,
                    Sub(y)
                        For x = 0 To w - 1
                            Dim center = d(y * w + x)
                            Dim acc = 0.0, weight = 0.0
                            For k = Math.Max(0, y - radius) To Math.Min(h - 1, y + radius)
                                Dim g = RangeWeight(d(k * w + x) - center) * kernel(Math.Abs(k - y))
                                acc += tmp(k * w + x) * g : weight += g
                            Next
                            dst(y * w + x) = CSng(acc / weight)
                        Next
                    End Sub)
                Return dst
            End Function
        End Class

        ' ── Szenenwerte: zurueck durch Kurve und Schulter, vorwaerts wieder ─────────────

        ''' <summary>Die Tabellen der Szenenwerte fuer ein Knie: rueckwaerts je Tonwert (256), und
        ''' vorwaerts von der Rampe (nach der Schulter, 0 bis 1) auf den Tonwert.</summary>
        Private NotInheritable Class SceneTables
            Public Knee As Double
            Public Backward(255) As Double
            Public Forward As Single()
            Public Const ForwardSize As Integer = 4096
        End Class

        Private Shared _sceneTables As SceneTables
        Private Shared ReadOnly _sceneTablesLock As New Object()

        Private Shared Function SceneTablesFor(knee As Double) As SceneTables
            SyncLock _sceneTablesLock
                If _sceneTables IsNot Nothing AndAlso _sceneTables.Knee = knee Then Return _sceneTables
            End SyncLock
            Dim t As New SceneTables With {.Knee = knee}
            For i = 0 To 255
                ' Derselbe Weg wie RawStageExposureAt: Tonwert linear, durch die umgekehrte ACR3-Kurve
                ' auf die Rampe, durch die umgekehrte Schulter auf den Szenenwert.
                Dim lin = ColorToLinear(i / 255.0)
                t.Backward(i) = RawDecodeService.ShoulderInverse(RawDecodeService.Acr3CurveInverse(lin), knee)
            Next
            Dim fwd(SceneTables.ForwardSize) As Single
            For i = 0 To SceneTables.ForwardSize
                Dim lin = RawDecodeService.Acr3CurveAt(i / CDbl(SceneTables.ForwardSize))
                Dim c = If(lin <= 0.0031308, lin * 12.92, 1.055 * Math.Pow(lin, 1.0 / 2.4) - 0.055)
                fwd(i) = CSng(Math.Max(0.0, Math.Min(255.0, c * 255.0)))
            Next
            t.Forward = fwd
            SyncLock _sceneTablesLock
                _sceneTables = t
            End SyncLock
            Return t
        End Function

        ''' <summary>Ein Szenenwert vorwaerts auf den Tonwert (0 bis 255, ungerundet).
        ''' <paramref name="original"/> ist der Szenenwert vor dem Licht: ohne eigene Schulter der
        ''' Datei (Knie 1) liefe ein Licht hart an die Grenze; dann nimmt eine weiche Schulter ab 0,8
        ''' nur die ZUNAHME auf, Unbeleuchtetes bleibt, wie es war.</summary>
        Private Shared Function SceneForward(t As SceneTables, scene As Double, original As Double) As Double
            If t.Knee >= 1.0 AndAlso scene > original AndAlso scene > 0.8 Then
                scene = Math.Max(Math.Min(original, 1.0), 0.8 + 0.2 * (1.0 - Math.Exp(-(scene - 0.8) / 0.2)))
            End If
            Dim ramp = RawDecodeService.ShoulderAt(Math.Max(0.0, scene), t.Knee)
            Dim pos = Math.Max(0.0, Math.Min(1.0, ramp)) * SceneTables.ForwardSize
            Dim i = Math.Min(SceneTables.ForwardSize - 1, CInt(Math.Floor(pos)))
            Dim f = pos - i
            Return t.Forward(i) + (t.Forward(i + 1) - t.Forward(i)) * f
        End Function

        ''' <summary>Die Stufe selbst. Rechnet auf einer Kopie und gibt sie zurueck, oder die Quelle,
        ''' wenn nichts zu tun ist. <paramref name="knee"/> ist die Schulter, die im Quellbild steckt
        ''' (1 ohne).</summary>
        Friend Shared Function ApplyLightSources(source As SKBitmap, lights As List(Of LightSource), ambient As Double,
                                                 depthToken As String, knee As Double) As SKBitmap
            If source Is Nothing Then Return source
            Dim active = If(lights, New List(Of LightSource)()).Where(Function(l) l IsNot Nothing AndAlso l.Enabled AndAlso
                                                  (Math.Abs(l.Intensity) > 0.001 OrElse l.Glow > 0.01)).ToList()
            Dim ambientGain = Math.Pow(2.0, Math.Max(-6.0, Math.Min(3.0, ambient)))
            Dim hasAmbient = Math.Abs(ambientGain - 1.0) > 0.0001
            If active.Count = 0 AndAlso Not hasAmbient Then Return source

            Dim width = source.Width, height = source.Height
            Dim field As DepthField = Nothing
            If active.Any(Function(l) l.UseDepth) Then field = LightFieldFor(depthToken)

            Dim prepared = active.Select(Function(l) PrepareLight(l, width, height, field)).ToArray()
            Dim tables = SceneTablesFor(Math.Max(0.01, Math.Min(1.0, knee)))
            Dim result = CloneBitmap(source)
            Dim buf As Byte() = Nothing
            Dim stride = 0
            If Not TryBorrowBgraBuffer(result, buf, stride) Then
                result.Dispose()
                Return source
            End If
            Dim back = tables.Backward
            Dim dst = CType(buf.Clone(), Byte())
            ' Haengt an der Quelle ein tiefes Bild (Ausgabe in 16 Bit, ImageProcessorDeep.vb), laeuft
            ' es zeilenweise mit: dieselben Verstaerkungen, die Werte aus dem tiefen Bild gelesen
            ' und ohne Rundung auf das Byte zurueckgeschrieben.
            Dim deepSource = DeepOf(source)
            Dim deepResult As SKBitmap = If(deepSource Is Nothing, Nothing, NewDeepBitmap(width, height, deepSource.AlphaType))
            Dim deepSourcePixels = If(deepResult Is Nothing, IntPtr.Zero, deepSource.GetPixels())
            Dim deepResultPixels = If(deepResult Is Nothing, IntPtr.Zero, deepResult.GetPixels())
            ForEachRow(width, height,
                Sub(y)
                    Dim row = y * stride
                    Dim py = y + 0.5
                    Dim v = py / height
                    Dim deepRow As Short() = Nothing
                    If deepResult IsNot Nothing Then
                        deepRow = New Short(width * 4 - 1) {}
                        ReadDeepRow(deepSource, deepSourcePixels, y, deepRow)
                    End If
                    For x = 0 To width - 1
                        Dim px = x + 0.5
                        Dim u = px / width
                        Dim gr = ambientGain, gg = ambientGain, gb = ambientGain
                        Dim ar = 0.0, ag = 0.0, ab = 0.0
                        Dim touched = hasAmbient
                        ' Die Tiefe des Bildpunkts aus der feinen Karte, einmal fuer alle Lichter.
                        Dim depthHere = -1.0
                        For li = 0 To prepared.Length - 1
                            Dim light = prepared(li)
                            Dim f = LightShapeAt(light, px, py)
                            If light.UseDepth AndAlso f > 0 Then
                                If depthHere < 0 Then depthHere = field.FineAt(u, v)
                                Dim z = (depthHere - light.DepthAtLight) / light.DepthSigma
                                f *= Math.Exp(-z * z)
                            End If
                            If f > 0.0005 Then
                                touched = True
                                If light.Ev >= 0 Then
                                    ' Form und Schatten nur fuer Licht: ein Schatten wird nicht von
                                    ' einer Flaeche abgewandt.
                                    If light.Modulation IsNot Nothing Then
                                        f *= Math.Max(0.0, field.SampleJoint(light.Modulation, u, v, depthHere))
                                    End If
                                    Dim k = (Math.Pow(2.0, light.Ev) - 1.0) * f
                                    If light.GlowMode Then
                                        Dim glow = light.GlowAdd * f
                                        ar += glow * light.Wr : ag += glow * light.Wg : ab += glow * light.Wb
                                        ' Leuchten hellt auch an, nur schwaecher als Beleuchten.
                                        k *= 0.5
                                    End If
                                    gr += k * light.Wr : gg += k * light.Wg : gb += k * light.Wb
                                Else
                                    ' Schatten: die Farbe toent ihn, ein warmes Licht weggenommen
                                    ' laesst eher Blau zurueck. Gleichmaessig dunkler fuer Weiss.
                                    Dim k = (Math.Pow(2.0, light.Ev) - 1.0) * f
                                    gr += k * (1.5 - 0.5 * light.Cr)
                                    gg += k * (1.5 - 0.5 * light.Cg)
                                    gb += k * (1.5 - 0.5 * light.Cb)
                                End If
                            End If
                            If light.HaloStrength > 0 Then
                                Dim hx = (px - light.Cx) / light.HaloRadius, hy = (py - light.Cy) / light.HaloRadius
                                Dim h2 = hx * hx + hy * hy
                                If h2 < 16.0 Then
                                    Dim hk = light.HaloStrength * Math.Exp(-h2) * 0.6
                                    ar += hk * light.Wr : ag += hk * light.Wg : ab += hk * light.Wb
                                    touched = True
                                End If
                            End If
                        Next
                        If Not touched Then Continue For

                        Dim o = row + x * 4
                        Dim a = buf(o + 3)
                        If a = 0 Then Continue For
                        Dim inv = 255.0 / a
                        ' Premul: erst auf die geraden Werte, dann zurueck.
                        Dim b0 = back(CInt(Math.Min(255.0, buf(o) * inv)))
                        Dim g0 = back(CInt(Math.Min(255.0, buf(o + 1) * inv)))
                        Dim r0 = back(CInt(Math.Min(255.0, buf(o + 2) * inv)))
                        Dim r1 = SceneForward(tables, r0 * Math.Max(0.0, gr) + ar, r0)
                        Dim g1 = SceneForward(tables, g0 * Math.Max(0.0, gg) + ag, g0)
                        Dim b1 = SceneForward(tables, b0 * Math.Max(0.0, gb) + ab, b0)
                        Dim scale = a / 255.0
                        dst(o) = CByte(Math.Min(CInt(a), CInt(Math.Round(b1 * scale))))
                        dst(o + 1) = CByte(Math.Min(CInt(a), CInt(Math.Round(g1 * scale))))
                        dst(o + 2) = CByte(Math.Min(CInt(a), CInt(Math.Round(r1 * scale))))

                        If deepRow IsNot Nothing Then
                            Dim q = x * 4
                            Dim deepAlpha = deepRow(q + 3) And &HFFFF
                            If deepAlpha > 0 Then
                                ' Auf die geraden Werte in der Einheit der 8-Bit-Stufen, wie oben.
                                Dim deepInv = 255.0 / deepAlpha
                                Dim dr0 = BackwardAt(back, (deepRow(q) And &HFFFF) * deepInv)
                                Dim dg0 = BackwardAt(back, (deepRow(q + 1) And &HFFFF) * deepInv)
                                Dim db0 = BackwardAt(back, (deepRow(q + 2) And &HFFFF) * deepInv)
                                Dim deepScale = deepAlpha / 255.0
                                deepRow(q) = ToDeepShort(Math.Min(deepAlpha, CInt(Math.Round(
                                    SceneForward(tables, dr0 * Math.Max(0.0, gr) + ar, dr0) * deepScale))))
                                deepRow(q + 1) = ToDeepShort(Math.Min(deepAlpha, CInt(Math.Round(
                                    SceneForward(tables, dg0 * Math.Max(0.0, gg) + ag, dg0) * deepScale))))
                                deepRow(q + 2) = ToDeepShort(Math.Min(deepAlpha, CInt(Math.Round(
                                    SceneForward(tables, db0 * Math.Max(0.0, gb) + ab, db0) * deepScale))))
                            End If
                        End If
                    Next
                    If deepRow IsNot Nothing Then WriteDeepRow(deepResult, deepResultPixels, y, deepRow)
                End Sub)
            If deepResult IsNot Nothing Then
                ' Das 8-Bit-Bild folgt dem tiefen (ImageProcessorDeepStages.vb), statt aus der
                ' eigenen Rechnung oben zu kommen.
                Dim shallow = ShallowFromDeep(deepResult, source.ColorType, source.AlphaType)
                If shallow IsNot Nothing Then
                    result.Dispose()
                    AttachDeep(shallow, deepResult)
                    Return shallow
                End If
                deepResult.Dispose()
            End If
            CommitBgraBuffer(result, dst)
            Return result
        End Function

        ''' <summary>Die Tabelle vom Tonwert zum Szenenlicht an einer Stelle zwischen zwei Bytes, linear
        ''' gelesen. Die 8-Bit-Stufe schlaegt nur an ganzen Bytes nach.</summary>
        Private Shared Function BackwardAt(back As Double(), value As Double) As Double
            If Not value > 0.0 Then Return back(0)
            If value >= 255.0 Then Return back(255)
            Dim index = CInt(Math.Floor(value))
            Return back(index) + (back(index + 1) - back(index)) * (value - index)
        End Function

    End Class

End Namespace
