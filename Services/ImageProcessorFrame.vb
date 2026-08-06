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
                                            Optional fillKind As String = "Solid",
                                            Optional color2 As SKColor = Nothing,
                                            Optional gradientAngleDegrees As Single = 0,
                                            Optional gradientInverted As Boolean = False,
                                            Optional symbol As String = "",
                                            Optional symbolSpacingPercent As Single = 50,
                                            Optional symbolRotate As Boolean = False,
                                            Optional symbolStrokeColor As SKColor = Nothing,
                                            Optional symbolStrokeWidth As Single = 0)
            If canvas Is Nothing Then Return
            Dim boundsWidth = bounds.Width
            Dim boundsHeight = bounds.Height
            If boundsWidth <= 0 OrElse boundsHeight <= 0 Then Return
            Dim thickness = CInt(Math.Round(Math.Min(boundsWidth, boundsHeight) * Clamp(sizePercent, 0, 0.25F)))
            If thickness <= 0 Then Return

            ' VOR dem Try: was im Finally freigegeben wird, muss dort auch sichtbar sein - eine
            ' Deklaration im Try-Block ist es in VB nicht.
            Dim gradientShader As SKShader = Nothing

            canvas.Save()
            canvas.Translate(bounds.Left, bounds.Top)
            Try
                Dim normalized = If(effect, "Einfach").Trim().ToLowerInvariant()
                Dim radius = Math.Min(boundsWidth, boundsHeight) * Clamp(cornerRadiusPercent, 0, 1) * 0.25F

                ' Verlauf wie bei den Formen, nur auf der KONTUR statt in der Flaeche: derselbe
                ' Schattierer, damit Winkel, Umkehrung und Radialform sich ueberall gleich verhalten.
                ' Er wird EINMAL gebaut und an jeden Pinsel gehaengt - der doppelte Rahmen zeichnet
                ' zwei Linien und soll denselben Verlauf tragen, nicht zwei eigene.
                Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
                If normalizedFillKind = "radialgradient" Then
                    ' Der radiale Verlauf einer FLAECHE spannt von der Mitte bis zur Ecke. Ein Rahmen
                    ' liegt aber nur im aeussersten Ring davon: bei 800x600 laege die Mitte einer
                    ' Kante bei 0,6 des Radius und die Ecke bei 1,0 - der Rahmen zeigte also nur die
                    ' letzten 40 Prozent der Farbrampe und sah fast einfarbig aus. Deshalb bekommen
                    ' die beiden Farben hier VERSCHOBENE Stuetzstellen: die Rampe faengt dort an, wo
                    ' der Rahmen anfaengt (Kantenmitte), und endet in der Ecke.
                    Dim mitte = New SKPoint(boundsWidth / 2.0F, boundsHeight / 2.0F)
                    Dim aussen = CSng(Math.Sqrt(CDbl(boundsWidth) * boundsWidth + CDbl(boundsHeight) * boundsHeight) / 2.0)
                    Dim innen = Math.Min(boundsWidth, boundsHeight) / 2.0F
                    Dim start = If(gradientInverted, color2, color)
                    Dim ende = If(gradientInverted, color, color2)
                    Dim beginn = If(aussen > 0, Clamp(innen / aussen, 0, 0.95F), 0.0F)
                    gradientShader = SKShader.CreateRadialGradient(mitte, Math.Max(1.0F, aussen),
                                                                   New SKColor() {start, ende},
                                                                   New Single() {beginn, 1.0F},
                                                                   SKShaderTileMode.Clamp)
                ElseIf normalizedFillKind = "lineargradient" Then
                    gradientShader = CreateFillGradientShader(New SKRect(0, 0, boundsWidth, boundsHeight),
                                                              normalizedFillKind, color, color2,
                                                              gradientAngleDegrees, gradientInverted)
                End If

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
                    Using paint = New SKPaint With {.Color = color, .Style = SKPaintStyle.Stroke, .StrokeWidth = thinWidth, .IsAntialias = True}
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

                Using paint = New SKPaint With {.Color = color, .Style = SKPaintStyle.Stroke, .StrokeWidth = thickness, .IsAntialias = True}
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
                Dim malfarbe = If(gradientShader Is Nothing, color, New SKColor(255, 255, 255, color.Alpha))
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
                        stempeln(malfarbe, SKColors.Transparent, 0.0F)
                        Using paint = New SKPaint With {.Shader = gradientShader, .BlendMode = SKBlendMode.SrcIn}
                            canvas.DrawRect(New SKRect(0, 0, width, height), paint)
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
