Option Strict On
Option Explicit On

Imports System.Collections.Generic
Imports System.Linq
Imports SkiaSharp

' Die Lichtquellen des Werkzeugs Beleuchtung als Stufe der Pixelkette.
'
' Gerechnet wird im LINEAREN Licht: jedes Licht ist dort eine Belichtung in Blendenstufen, je Kanal
' gewichtet mit seiner Farbe (Kanal mal 2 hoch Leuchtkraft mal Form mal Farbe). Ein dunkler Stoff
' bleibt unter Licht dunkel, ein heller wird hell - wie bei einem Licht, das bei der Aufnahme da
' war - und eine negative Leuchtkraft ist dasselbe in die andere Richtung, ein Schatten. Die Art
' "Leuchten" legt dazu einen sichtbaren Schein auf, der auch auf Schwarz erscheint, und der Lichthof
' um die Quelle ist immer ein solcher Schein.
'
' Die Stufe liegt in der Detailkette vor der Vignette: die Vignette dunkelt danach auch beleuchtete
' Ecken ab, so wie sie es mit einem hellen Bild taete.
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
                    If depth.ScalePixels(small, New SKSamplingOptions(SKFilterMode.Linear)) Then
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
            Return adj IsNot Nothing AndAlso adj.LightSources IsNot Nothing AndAlso
                   adj.LightSources.Any(Function(l) l IsNot Nothing AndAlso l.Enabled AndAlso
                                                    (Math.Abs(l.Intensity) > 0.001 OrElse l.Glow > 0.01))
        End Function

        ''' <summary>Der Teil des Zwischenspeicher-Schluessels fuer die Lichter. Das Tiefenzeichen
        ''' gehoert dazu: ob eine Karte greift, aendert das Bild.</summary>
        Friend Shared Function LightSourcesKey(adj As ImageAdjustments) As String
            If Not HasActiveLightSources(adj) Then Return ""
            Dim depth = If(adj.LightSources.Any(Function(l) l IsNot Nothing AndAlso l.Enabled AndAlso l.UseDepth) AndAlso
                           HasLightDepthMap(adj.LightDepthToken), adj.LightDepthToken, "")
            Return String.Join(";", adj.LightSources.Where(Function(l) l IsNot Nothing).Select(Function(l) l.Signature())) & "|d:" & depth
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
            Public Cr, Cg, Cb As Double
            Public GlowMode As Boolean
            Public GlowAdd As Double
            Public HaloStrength, HaloRadius As Double
            Public RayCount, RayContrast, RayPhase1, RayPhase2, RayPhase3 As Double
            Public UseDepth As Boolean
            Public DepthAtLight, DepthSigma As Double
            Public Reach2 As Double
        End Class

        Private Shared Function SmoothStep(edge0 As Double, edge1 As Double, x As Double) As Double
            If edge1 <= edge0 Then Return If(x < edge0, 0.0, 1.0)
            Dim t = Math.Max(0.0, Math.Min(1.0, (x - edge0) / (edge1 - edge0)))
            Return t * t * (3.0 - 2.0 * t)
        End Function

        ''' <summary>Abfall ueber die Reichweite, r = Abstand / Reichweite; jenseits von 1 ist
        ''' Schluss, damit ein Licht nicht das ganze Bild ueberzieht.</summary>
        Private Shared Function LightFalloffAt(r As Double, light As PreparedLight) As Double
            If r >= 1.0 Then Return 0.0
            Select Case light.Falloff
                Case LightFalloffs.Soft
                    Return 1.0 - SmoothStep(0.0, 1.0, r)
                Case LightFalloffs.Hard
                    Dim inner = 1.0 - Math.Max(0.04, light.Soft) * 0.85
                    Return 1.0 - SmoothStep(inner, 1.0, r)
                Case Else
                    ' Wie eine echte Quelle: hell im Kern, dann quadratisch abnehmend, zum Rand
                    ' hin sanft auf null gefuehrt.
                    Return (1.0 / (1.0 + 9.0 * r * r)) * (1.0 - SmoothStep(0.6, 1.0, r))
            End Select
        End Function

        Private Shared Function LightShapeAt(light As PreparedLight, px As Double, py As Double) As Double
            Dim dx = px - light.Cx, dy = py - light.Cy
            Select Case light.Kind
                Case LightSourceKinds.Streak
                    ' Licht von einer Linie durch die Quelle, in Richtung DirX/DirY. Davor voll,
                    ' dahinter ueber die Reichweite abnehmend.
                    Dim t = (dx * light.DirX + dy * light.DirY) / light.Radius
                    If t <= 0 Then Return 1.0
                    Return LightFalloffAt(Math.Min(0.9999, t), light)
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
                    Dim f = LightFalloffAt(r, light)
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

        Private Shared Function PrepareLight(light As LightSource, width As Integer, height As Integer,
                                             depth As Byte(), dw As Integer, dh As Integer) As PreparedLight
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
            If light.UseDepth AndAlso depth IsNot Nothing Then
                p.UseDepth = True
                p.DepthAtLight = SampleDepth(depth, dw, dh, light.XPercent / 100.0, light.YPercent / 100.0)
                p.DepthSigma = 0.04 + Math.Max(0.0, Math.Min(100.0, light.DepthReach)) / 100.0 * 0.6
            End If
            Return p
        End Function

        Private Shared Function SampleDepth(depth As Byte(), dw As Integer, dh As Integer, u As Double, v As Double) As Double
            Dim x = CInt(Math.Max(0, Math.Min(dw - 1, Math.Floor(u * dw))))
            Dim y = CInt(Math.Max(0, Math.Min(dh - 1, Math.Floor(v * dh))))
            Return depth(y * dw + x) / 255.0
        End Function

        Private Shared ReadOnly SrgbToLinearTable As Single() = BuildSrgbToLinearTable()

        Private Shared Function BuildSrgbToLinearTable() As Single()
            Dim t(255) As Single
            For i = 0 To 255
                Dim c = i / 255.0
                t(i) = CSng(If(c <= 0.04045, c / 12.92, Math.Pow((c + 0.055) / 1.055, 2.4)))
            Next
            Return t
        End Function

        Private Shared Function LinearToSrgbByte(v As Double) As Byte
            If v <= 0 Then Return 0
            If v >= 1 Then Return 255
            Dim c = If(v <= 0.0031308, v * 12.92, 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055)
            Return CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(c * 255.0)))))
        End Function

        ''' <summary>Weiche Schulter: was ein Licht ueber 0,8 hebt, laeuft gegen 1 aus, statt hart
        ''' auszufressen. Nur auf die Zunahme angewandt - unbeleuchtete Lichter bleiben, wie sie sind.</summary>
        Private Shared Function LightShoulder(original As Double, lit As Double) As Double
            If lit <= original OrElse lit <= 0.8 Then Return lit
            Dim shaped = 0.8 + 0.2 * (1.0 - Math.Exp(-(lit - 0.8) / 0.2))
            Return Math.Max(original, shaped)
        End Function

        ''' <summary>Die Stufe selbst. Rechnet auf einer Kopie und gibt sie zurueck, oder die Quelle,
        ''' wenn nichts zu tun ist.</summary>
        Friend Shared Function ApplyLightSources(source As SKBitmap, lights As List(Of LightSource), depthToken As String) As SKBitmap
            If source Is Nothing OrElse lights Is Nothing Then Return source
            Dim active = lights.Where(Function(l) l IsNot Nothing AndAlso l.Enabled AndAlso
                                                  (Math.Abs(l.Intensity) > 0.001 OrElse l.Glow > 0.01)).ToList()
            If active.Count = 0 Then Return source

            Dim depth As Byte() = Nothing
            Dim dw = 0, dh = 0
            If active.Any(Function(l) l.UseDepth) Then
                SyncLock _lightDepthLock
                    If _lightDepth IsNot Nothing AndAlso Not String.IsNullOrEmpty(depthToken) AndAlso
                       String.Equals(_lightDepthToken, depthToken, StringComparison.Ordinal) Then
                        depth = _lightDepth : dw = _lightDepthWidth : dh = _lightDepthHeight
                    End If
                End SyncLock
            End If

            Dim width = source.Width, height = source.Height
            Dim prepared = active.Select(Function(l) PrepareLight(l, width, height, depth, dw, dh)).ToArray()
            Dim result = CloneBitmap(source)
            Dim buf As Byte() = Nothing
            Dim stride = 0
            If Not TryBorrowBgraBuffer(result, buf, stride) Then
                result.Dispose()
                Return source
            End If
            Dim lin = SrgbToLinearTable
            Dim dst = CType(buf.Clone(), Byte())
            ForEachRow(width, height,
                Sub(y)
                    Dim row = y * stride
                    Dim py = y + 0.5
                    For x = 0 To width - 1
                        Dim px = x + 0.5
                        Dim er = 0.0, eg = 0.0, eb = 0.0
                        Dim ar = 0.0, ag = 0.0, ab = 0.0
                        Dim touched = False
                        For Each light In prepared
                            Dim f = LightShapeAt(light, px, py)
                            If light.UseDepth AndAlso f > 0 Then
                                Dim d = SampleDepth(depth, dw, dh, px / width, py / height)
                                Dim z = (d - light.DepthAtLight) / light.DepthSigma
                                f *= Math.Exp(-z * z)
                            End If
                            If f > 0.0005 Then
                                touched = True
                                If light.GlowMode AndAlso light.Ev > 0 Then
                                    Dim k = light.GlowAdd * f
                                    ar += k * light.Cr : ag += k * light.Cg : ab += k * light.Cb
                                    ' Leuchten hellt auch an, nur schwaecher als Beleuchten.
                                    Dim e = light.Ev * f * 0.5
                                    er += e * light.Cr : eg += e * light.Cg : eb += e * light.Cb
                                ElseIf light.Ev >= 0 Then
                                    Dim e = light.Ev * f
                                    er += e * light.Cr : eg += e * light.Cg : eb += e * light.Cb
                                Else
                                    ' Schatten: die Farbe toent ihn - ein warmes Licht weggenommen
                                    ' laesst eher Blau zurueck. Gleichmaessig dunkler fuer Weiss.
                                    Dim e = light.Ev * f
                                    er += e * (2.0 - light.Cr) * 0.5 + e * 0.5
                                    eg += e * (2.0 - light.Cg) * 0.5 + e * 0.5
                                    eb += e * (2.0 - light.Cb) * 0.5 + e * 0.5
                                End If
                            End If
                            If light.HaloStrength > 0 Then
                                Dim hx = (px - light.Cx) / light.HaloRadius, hy = (py - light.Cy) / light.HaloRadius
                                Dim h2 = hx * hx + hy * hy
                                If h2 < 16.0 Then
                                    Dim hk = light.HaloStrength * Math.Exp(-h2) * 0.6
                                    ar += hk * light.Cr : ag += hk * light.Cg : ab += hk * light.Cb
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
                        Dim b0 = lin(CInt(Math.Min(255.0, buf(o) * inv)))
                        Dim g0 = lin(CInt(Math.Min(255.0, buf(o + 1) * inv)))
                        Dim r0 = lin(CInt(Math.Min(255.0, buf(o + 2) * inv)))
                        Dim r1 = LightShoulder(r0, r0 * Math.Pow(2.0, er) + ar)
                        Dim g1 = LightShoulder(g0, g0 * Math.Pow(2.0, eg) + ag)
                        Dim b1 = LightShoulder(b0, b0 * Math.Pow(2.0, eb) + ab)
                        Dim scale = a / 255.0
                        dst(o) = CByte(Math.Min(CInt(a), CInt(Math.Round(LinearToSrgbByte(b1) * scale))))
                        dst(o + 1) = CByte(Math.Min(CInt(a), CInt(Math.Round(LinearToSrgbByte(g1) * scale))))
                        dst(o + 2) = CByte(Math.Min(CInt(a), CInt(Math.Round(LinearToSrgbByte(r1) * scale))))
                    Next
                End Sub)
            CommitBgraBuffer(result, dst)
            Return result
        End Function

    End Class

End Namespace
