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

' Der Rahmen: seine Pfade, die Symbolreihe entlang des Pfades und die beiden Zierkanten.
' Er war frueher eine Stufe der Pixelkette und ist heute ein Objekt, gezeichnet wie Text und Form.
' Herausgeloest am 2026-08-06 aus ImageProcessor.vb, Zeile fuer Zeile unveraendert.
Namespace Services

    Partial Public Class ImageProcessor

        ''' <summary>Zeichnet den Rahmen in ein Rechteck. Frueher war das eine Stufe der Pixelkette
        ''' (ApplyBorder); seit der Rahmen ein Objekt ist, zeichnet ihn derselbe Weg wie Text und
        ''' Form. Die Groessen beziehen sich auf die kuerzere Kante des uebergebenen Rechtecks, damit
        ''' ein Rahmen bei jedem Seitenverhaeltnis gleich breit wirkt.</summary>
        Friend Shared Sub DrawFrameOnCanvas(canvas As SKCanvas, bounds As SKRect, sizePercent As Single,
                                            color As SKColor, cornerRadiusPercent As Single, effect As String,
                                            Optional gradient As GradientFillSpec = Nothing,
                                            Optional symbol As String = "",
                                            Optional symbolSpacingPercent As Single = 50,
                                            Optional symbolRotate As Boolean = False,
                                            Optional symbolStrokeColor As SKColor = Nothing,
                                            Optional symbolStrokeWidth As Single = 0,
                                            Optional marginPercent As Single = 0)
            If canvas Is Nothing Then Return
            If bounds.Width <= 0 OrElse bounds.Height <= 0 Then Return
            ' Staerke, Rundung und Abstand beziehen sich auf die kuerzere Seite des GANZEN Rechtecks,
            ' damit ein Abstand die Linie nicht duenner macht. Der Abstand rueckt nur das Rechteck,
            ' auf dem der Rahmen liegt, nach innen; alles Weitere rechnet in diesem Rechteck.
            Dim shortSide = Math.Min(bounds.Width, bounds.Height)
            Dim thickness = CInt(Math.Round(shortSide * Clamp(sizePercent, 0, 0.25F)))
            If thickness <= 0 Then Return
            Dim margin = CSng(Math.Round(shortSide * Clamp(marginPercent, 0, 0.4F)))
            Dim boundsWidth = bounds.Width - 2 * margin
            Dim boundsHeight = bounds.Height - 2 * margin
            If boundsWidth <= thickness OrElse boundsHeight <= thickness Then Return

            ' VOR dem Try: was im Finally freigegeben wird, muss dort auch sichtbar sein - eine
            ' Deklaration im Try-Block ist es in VB nicht.
            Dim gradientShader As SKShader = Nothing

            canvas.Save()
            canvas.Translate(bounds.Left + margin, bounds.Top + margin)
            Try
                Dim normalized = If(effect, "Einfach").Trim().ToLowerInvariant()
                Dim radius = shortSide * Clamp(cornerRadiusPercent, 0, 1) * 0.25F

                ' Verlauf wie bei den Formen, nur auf der KONTUR statt in der Flaeche. Er wird EINMAL
                ' gebaut und an jeden Pinsel gehaengt - der doppelte Rahmen zeichnet zwei Linien und
                ' soll denselben Verlauf tragen, nicht zwei eigene.
                If gradient IsNot Nothing Then gradientShader = CreateFrameGradientShader(boundsWidth, boundsHeight, thickness, gradient)
                ' Die Deckkraft steckt beim Verlauf schon in jedem Stopp. Die Farbe des Pinsels
                ' wirkt neben dem Schattierer nur noch mit ihrem Alpha und nahm sie sonst ein
                ' zweites Mal.
                Dim strokePaintColor = If(gradientShader Is Nothing, color, SKColors.White)

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
                    Using paint = New SKPaint With {.Color = strokePaintColor, .Style = SKPaintStyle.Stroke, .StrokeWidth = thinWidth, .IsAntialias = True}
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

                Using paint = New SKPaint With {.Color = strokePaintColor, .Style = SKPaintStyle.Stroke, .StrokeWidth = thickness, .IsAntialias = True}
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

        ''' <summary>Der Verlauf des Rahmens, mit denselben Feldern wie bei den Formen.
        '''
        ''' Linear, Gespiegelt und Winkel kommen unveraendert aus CreateFillGradientShader: sie
        ''' wirken entlang des Rahmens so wie in einer Flaeche. Radial und Raute dagegen wachsen von
        ''' der Mitte aus, und der Rahmen liegt nur in ihrem aeussersten Ring. Bei 800x600 laege die
        ''' Mitte einer Kante bei 0,6 des radialen Radius und die Ecke bei 1,0, der Rahmen zeigte
        ''' also nur die letzten 40 Prozent der Farbrampe und sah fast einfarbig aus; die Raute einer
        ''' Flaeche endet sogar an der Kantenmitte, jenseits davon stuende nur noch der letzte Stopp.
        ''' Beide bekommen deshalb hier eine Rampe entlang der MITTELLINIE des Rahmens: sie faengt
        ''' an der naeheren Kantenmitte an und endet bei Groesse 100 in der Ecke dieser Linie. Bis
        ''' zur Bildecke gerechnet erreichte der Rahmen den letzten Stopp nur in einem Zipfel von
        ''' wenigen Punkten (gemessen: Blau auf 0 von 1340 Punkten der Mittellinie). Die Groesse
        ''' streckt die Spanne, die Mitte verschiebt den Ursprung, die Wiederholung gilt wie ueberall.</summary>
        Private Shared Function CreateFrameGradientShader(width As Single, height As Single, thickness As Single,
                                                          spec As GradientFillSpec) As SKShader
            Dim rect = New SKRect(0, 0, width, height)
            If spec.Kind <> GradientFillSpec.KindRadial AndAlso spec.Kind <> GradientFillSpec.KindDiamond Then
                Return CreateFillGradientShader(rect, spec)
            End If

            Dim stops = spec.EffectiveStops()
            Dim colors = stops.Select(Function(s) New SKColor(s.R, s.G, s.B, s.A)).ToArray()
            Dim positions = stops.Select(Function(s) CSng(s.Position / 100.0)).ToArray()
            Dim tile = SKShaderTileMode.Clamp
            Select Case spec.Repeat
                Case GradientFillSpec.RepeatRepeat : tile = SKShaderTileMode.Repeat
                Case GradientFillSpec.RepeatMirror : tile = SKShaderTileMode.Mirror
            End Select
            Dim scale = Math.Max(0.1F, spec.ScalePercent / 100.0F)
            Dim center = New SKPoint(width / 2.0F + spec.OffsetXPercent / 100.0F * width / 2.0F,
                                     height / 2.0F + spec.OffsetYPercent / 100.0F * height / 2.0F)
            ' Halbe Breite und Hoehe der Mittellinie.
            Dim halfWidth = Math.Max(1.0F, (width - thickness) / 2.0F)
            Dim halfHeight = Math.Max(1.0F, (height - thickness) / 2.0F)

            If spec.Kind = GradientFillSpec.KindDiamond Then
                ' Im Einheitsmass der Raute (halbe Bildbreite und -hoehe) liegt eine Kantenmitte der
                ' Mittellinie bei halfHeight / (height / 2) bzw. halfWidth / (width / 2), ihre Ecke
                ' bei der Summe aus beidem.
                Dim topEdge = halfHeight / (height / 2.0F)
                Dim sideEdge = halfWidth / (width / 2.0F)
                Dim rampBegin = Math.Min(topEdge, sideEdge)
                Dim rampEnd = topEdge + sideEdge
                Dim diamond = CreateDiamondShader(center, width / 2.0F, height / 2.0F, spec.AngleDegrees,
                                                  colors, positions, tile,
                                                  rampStart:=rampBegin, rampSpan:=(rampEnd - rampBegin) * scale)
                If diamond IsNot Nothing Then Return diamond
                ' Ohne Laufzeit-Schattierer (steht im Diagnoselog) radial wie unten.
            End If

            ' Ein Kreis um die Mitte, zwei konzentrische Radien statt verschobener Stuetzstellen:
            ' so wiederholt die Wiederholung die Rampe und nicht die Luecke davor.
            Dim outer = CSng(Math.Sqrt(CDbl(halfWidth) * halfWidth + CDbl(halfHeight) * halfHeight))
            Dim inner = Math.Min(Math.Min(halfWidth, halfHeight), outer * 0.95F)
            Dim span = Math.Max(0.5F, (outer - inner) * scale)
            Return SKShader.CreateTwoPointConicalGradient(center, inner, center, inner + span, colors, positions, tile)
        End Function

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
                ' Deckend: SrcIn nimmt das Alpha der Ebene mal das des Verlaufs, und in dem steckt
                ' die Deckkraft schon.
                Dim fillPaintColor = If(gradientShader Is Nothing, color, SKColors.White)
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
                        stempeln(fillPaintColor, SKColors.Transparent, 0.0F)
                        ' Die GANZE Ebene einfaerben, nicht nur das Rahmenrechteck: "Wellig" schwingt
                        ' nach aussen darueber hinaus, und mit Randabstand liegen diese Symbole
                        ' sichtbar im Bild. Ausserhalb des Rechtecks blieben sie ungefaerbt und damit
                        ' durchsichtig (Nutzerbefund). SrcIn wirkt ohnehin nur, wo gestempelt wurde.
                        Using paint = New SKPaint With {.Shader = gradientShader, .BlendMode = SKBlendMode.SrcIn}
                            canvas.DrawPaint(paint)
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

    End Class

End Namespace
