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
        Private Shared ReadOnly _lightDepthLock As New Object()
        Private Shared _lightDepthToken As String = ""
        Private Shared _lightDepth As Byte()
        Private Shared _lightDepthWidth As Integer
        Private Shared _lightDepthHeight As Integer
        Private Const LightDepthEdge As Integer = 512

        ''' <summary>Legt die Tiefenkarte (Alpha8, 255 = nah) fuer die Lichter ab. Nothing raeumt sie weg.</summary>
        Public Shared Sub SetLightDepthMap(token As String, depth As SKBitmap)
            Dim bytes As Byte() = Nothing
            Dim w = 0, h = 0
            If depth IsNot Nothing AndAlso depth.Width > 0 AndAlso depth.Height > 0 Then
                Dim scale = Math.Min(1.0, LightDepthEdge / CDbl(Math.Max(depth.Width, depth.Height)))
                w = Math.Max(1, CInt(Math.Round(depth.Width * scale)))
                h = Math.Max(1, CInt(Math.Round(depth.Height * scale)))
                Using small = New SKBitmap(New SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul))
                    If ImageProcessor.ScalePixelsFast(depth, small, New SKSamplingOptions(SKFilterMode.Linear)) Then
                        bytes = small.Bytes
                    End If
                End Using
            End If
            SyncLock _lightDepthLock
                _lightDepthToken = If(bytes Is Nothing, "", If(token, ""))
                _lightDepth = bytes
                _lightDepthWidth = w
                _lightDepthHeight = h
            End SyncLock
        End Sub

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
            ''' <summary>Die Schattenkarte in der Groesse der Tiefenkarte, 1 = frei, 0 = verdeckt;
            ''' Nothing ohne Schatten.</summary>
            Public Shadow As Single()
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
                If light.CastShadows AndAlso p.Ev > 0 Then p.Shadow = field.ShadowMap(p, width, height)
            End If
            Return p
        End Function

        ' ── Die Szene im Raum: Tiefe, Flaechen, Schatten ─────────────────────────────

        ''' <summary>Die Tiefenkarte der Lichter als Hoehenfeld, dazu die Ausrichtung der Flaechen.
        ''' Koordinaten in Einheiten der langen Bildseite; die Tiefe (1 = nah) reicht ueber
        ''' <see cref="Relief"/> davon in den Raum. Die Karte ist eine verkleinerte Kopie (512 Punkte),
        ''' gelesen wird nach Prozent.</summary>
        Private NotInheritable Class DepthField
            ''' <summary>Wie tief die Szene ist, in Einheiten der langen Seite. Ein Tiefenmodell sagt
            ''' nur, was naeher liegt, nicht wie viel; ein halbes Bild Tiefe gibt Flaechen eine
            ''' sichtbare, aber keine uebertriebene Neigung.</summary>
            Public Const Relief As Double = 0.5
            Public ReadOnly Width, Height As Integer
            Public ReadOnly Depth As Single()
            Public ReadOnly Nx, Ny, Nz As Single()
            ''' <summary>Laenge einer Kartenstelle in Einheiten der langen Seite.</summary>
            Public ReadOnly CellX, CellY As Double

            Public Sub New(bytes As Byte(), w As Integer, h As Integer, imageWidth As Integer, imageHeight As Integer)
                Width = w : Height = h
                Dim raw(w * h - 1) As Single
                For i = 0 To raw.Length - 1
                    raw(i) = bytes(i) / 255.0F
                Next
                ' Ein Tiefenmodell rauscht im Kleinen; geglaettet, bevor daraus Flaechen werden.
                Depth = BoxBlur(BoxBlur(raw, w, h, 2), w, h, 2)
                Dim longSide = CDbl(Math.Max(imageWidth, imageHeight))
                CellX = imageWidth / longSide / w
                CellY = imageHeight / longSide / h
                ReDim Nx(w * h - 1) : ReDim Ny(w * h - 1) : ReDim Nz(w * h - 1)
                Dim d = Depth
                ImageProcessor.ForEachRow(w, h,
                    Sub(y)
                        Dim y0 = Math.Max(0, y - 1), y1 = Math.Min(h - 1, y + 1)
                        For x = 0 To w - 1
                            Dim x0 = Math.Max(0, x - 1), x1 = Math.Min(w - 1, x + 1)
                            Dim dzx = (d(y * w + x1) - d(y * w + x0)) * Relief / Math.Max(1, x1 - x0) / CellX
                            Dim dzy = (d(y1 * w + x) - d(y0 * w + x)) * Relief / Math.Max(1, y1 - y0) / CellY
                            Dim len = Math.Sqrt(dzx * dzx + dzy * dzy + 1.0)
                            Dim i = y * w + x
                            Nx(i) = CSng(-dzx / len) : Ny(i) = CSng(-dzy / len) : Nz(i) = CSng(1.0 / len)
                        Next
                    End Sub)
            End Sub

            Private Shared Function BoxBlur(src As Single(), w As Integer, h As Integer, radius As Integer) As Single()
                Dim tmp(src.Length - 1) As Single
                Dim dst(src.Length - 1) As Single
                For y = 0 To h - 1
                    For x = 0 To w - 1
                        Dim acc = 0.0F, n = 0
                        For k = Math.Max(0, x - radius) To Math.Min(w - 1, x + radius)
                            acc += src(y * w + k) : n += 1
                        Next
                        tmp(y * w + x) = acc / n
                    Next
                Next
                For y = 0 To h - 1
                    For x = 0 To w - 1
                        Dim acc = 0.0F, n = 0
                        For k = Math.Max(0, y - radius) To Math.Min(h - 1, y + radius)
                            acc += tmp(k * w + x) : n += 1
                        Next
                        dst(y * w + x) = acc / n
                    Next
                Next
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

            ''' <summary>Die Lage der Quelle im Raum: ueber der Tiefe an ihrem Ort, um ihre Hoehe zum
            ''' Betrachter hin. Ein Streiflicht hat keinen Ort, nur eine Richtung (Lx/Ly/Lz als
            ''' Einheitsvektor zur Quelle).</summary>
            Public Function LightPosition(light As PreparedLight, imageWidth As Integer, imageHeight As Integer) As (X As Double, Y As Double, Z As Double, Directional As Boolean)
                Dim longSide = CDbl(Math.Max(imageWidth, imageHeight))
                Dim h = light.Height / longSide
                If light.Kind = LightSourceKinds.Streak Then
                    ' Das Licht kommt entgegen seiner Laufrichtung, unter einem Winkel aus der Hoehe.
                    Dim lx = -light.DirX, ly = -light.DirY, lz = Math.Max(0.05, h * 2.0)
                    Dim len = Math.Sqrt(lx * lx + ly * ly + lz * lz)
                    Return (lx / len, ly / len, lz / len, True)
                End If
                Dim z = light.DepthAtLight * Relief + h
                Return (light.Cx / longSide, light.Cy / longSide, z, False)
            End Function

            ''' <summary>Die Schattenkarte eines Lichts: fuer jede Kartenstelle ein Strahl zur Quelle
            ''' ueber das Hoehenfeld. Liegt die Szene unterwegs naeher am Betrachter als der Strahl,
            ''' steht etwas dazwischen. Danach je nach Weiche des Lichts weichgezeichnet.</summary>
            Public Function ShadowMap(light As PreparedLight, imageWidth As Integer, imageHeight As Integer) As Single()
                Dim pos = LightPosition(light, imageWidth, imageHeight)
                Dim w = Width, h = Height
                Dim result(w * h - 1) As Single
                Dim d = Depth
                Dim stepX = CellX, stepY = CellY
                Const Steps As Integer = 48
                ImageProcessor.ForEachRow(w, h,
                    Sub(y)
                        For x = 0 To w - 1
                            Dim px = (x + 0.5) * stepX, py = (y + 0.5) * stepY
                            Dim pz = d(y * w + x) * Relief
                            Dim rx, ry, rz As Double
                            If pos.Directional Then
                                ' Bis zum Bildrand genuegt: weiter weg verdeckt nichts mehr.
                                Dim reach = 0.6 / Math.Max(0.05, Math.Sqrt(pos.X * pos.X + pos.Y * pos.Y))
                                rx = pos.X * reach : ry = pos.Y * reach : rz = pos.Z * reach
                            Else
                                rx = pos.X - px : ry = pos.Y - py : rz = pos.Z - pz
                            End If
                            ' Versetzt je Stelle, damit die Schritte keine Stufen ins Bild zeichnen.
                            Dim jitter = ((x * 0.06711056 + y * 0.00583715) * 52.9829189) Mod 1.0
                            Dim occlusion = 0.0
                            For k = 1 To Steps
                                Dim t = (k - jitter) / Steps
                                Dim sx = (px + rx * t) / stepX, sy = (py + ry * t) / stepY
                                If sx < 0 OrElse sy < 0 OrElse sx >= w OrElse sy >= h Then Exit For
                                Dim rayZ = pz + rz * t
                                Dim gap = d(CInt(Math.Floor(sy)) * w + CInt(Math.Floor(sx))) * Relief - rayZ
                                ' Etwas liegt vor dem Strahl, aber nicht so weit, dass es ein
                                ' Hintergrund hinter ihm waere, den die Karte nur nah zeichnet.
                                If gap > 0.01 AndAlso gap < 0.3 Then
                                    occlusion = Math.Max(occlusion, SmoothStep(0.01, 0.04, gap) * (1.0 - SmoothStep(0.2, 0.3, gap)))
                                    If occlusion >= 1.0 Then Exit For
                                End If
                            Next
                            result(y * w + x) = CSng(1.0 - occlusion)
                        Next
                    End Sub)
                Dim radius = CInt(Math.Round(1 + light.Soft * Math.Max(w, h) * 0.02))
                Return BoxBlur(BoxBlur(result, w, h, radius), w, h, radius)
            End Function
        End Class

        ''' <summary>Wie viel ein Licht an einem Punkt nach der Ausrichtung der Flaeche gibt, 0 bis 1.
        ''' Eine Flaeche, die dem Licht zugewandt ist, bekommt alles, eine abgewandte nichts; ein
        ''' wenig laeuft das Licht um die Kante herum, sonst stuende hinter jeder Rundung eine harte
        ''' Grenze. Auf einer zur Kamera gewandten Flaeche direkt unter der Quelle 1.</summary>
        Private Shared Function SurfaceFactor(field As DepthField, light As PreparedLight,
                                              pos As (X As Double, Y As Double, Z As Double, Directional As Boolean),
                                              u As Double, v As Double, longSide As Double,
                                              px As Double, py As Double) As Double
            Dim nx = field.Sample(field.Nx, u, v), ny = field.Sample(field.Ny, u, v), nz = field.Sample(field.Nz, u, v)
            Dim nLen = Math.Sqrt(nx * nx + ny * ny + nz * nz)
            If nLen < 0.000001 Then Return 1.0
            nx /= nLen : ny /= nLen : nz /= nLen
            Dim lx, ly, lz As Double
            If pos.Directional Then
                lx = pos.X : ly = pos.Y : lz = pos.Z
            Else
                lx = pos.X - px / longSide : ly = pos.Y - py / longSide
                lz = pos.Z - field.Sample(field.Depth, u, v) * DepthField.Relief
                Dim len = Math.Sqrt(lx * lx + ly * ly + lz * lz)
                If len < 0.000001 Then Return 1.0
                lx /= len : ly /= len : lz /= len
            End If
            Const Wrap As Double = 0.25
            Dim ndl = nx * lx + ny * ly + nz * lz
            Return Math.Max(0.0, (ndl + Wrap) / (1.0 + Wrap))
        End Function

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
            If active.Any(Function(l) l.UseDepth) Then
                SyncLock _lightDepthLock
                    If _lightDepth IsNot Nothing AndAlso Not String.IsNullOrEmpty(depthToken) AndAlso
                       String.Equals(_lightDepthToken, depthToken, StringComparison.Ordinal) Then
                        field = New DepthField(_lightDepth, _lightDepthWidth, _lightDepthHeight, width, height)
                    End If
                End SyncLock
            End If

            Dim prepared = active.Select(Function(l) PrepareLight(l, width, height, field)).ToArray()
            Dim positions = prepared.Select(Function(l) If(field IsNot Nothing AndAlso l.UseDepth,
                                                           field.LightPosition(l, width, height),
                                                           (0.0, 0.0, 0.0, False))).ToArray()
            Dim tables = SceneTablesFor(Math.Max(0.01, Math.Min(1.0, knee)))
            Dim longSide = CDbl(Math.Max(width, height))
            Dim result = CloneBitmap(source)
            Dim buf As Byte() = Nothing
            Dim stride = 0
            If Not TryBorrowBgraBuffer(result, buf, stride) Then
                result.Dispose()
                Return source
            End If
            Dim back = tables.Backward
            Dim dst = CType(buf.Clone(), Byte())
            ForEachRow(width, height,
                Sub(y)
                    Dim row = y * stride
                    Dim py = y + 0.5
                    Dim v = py / height
                    For x = 0 To width - 1
                        Dim px = x + 0.5
                        Dim u = px / width
                        Dim gr = ambientGain, gg = ambientGain, gb = ambientGain
                        Dim ar = 0.0, ag = 0.0, ab = 0.0
                        Dim touched = hasAmbient
                        For li = 0 To prepared.Length - 1
                            Dim light = prepared(li)
                            Dim f = LightShapeAt(light, px, py)
                            If light.UseDepth AndAlso f > 0 Then
                                Dim d = field.DepthAt(u, v)
                                Dim z = (d - light.DepthAtLight) / light.DepthSigma
                                f *= Math.Exp(-z * z)
                            End If
                            If f > 0.0005 Then
                                touched = True
                                If light.Ev >= 0 Then
                                    ' Form und Schatten nur fuer Licht: ein Schatten wird nicht von
                                    ' einer Flaeche abgewandt.
                                    If light.Shading > 0 Then
                                        Dim facing = SurfaceFactor(field, light, positions(li), u, v, longSide, px, py)
                                        f *= 1.0 - light.Shading + light.Shading * facing
                                    End If
                                    If light.Shadow IsNot Nothing Then
                                        f *= Math.Max(0.0, Math.Min(1.0, field.Sample(light.Shadow, u, v)))
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
                    Next
                End Sub)
            CommitBgraBuffer(result, dst)
            Return result
        End Function

    End Class

End Namespace
