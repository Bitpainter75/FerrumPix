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

' Die Pinselstriche und ihre Varianten: weicher Rundpinsel, Marker, Akryl, Sandpapier, Stift,
' Verwischen, Spritzer, Kohle, Wachs, Airbrush, Kalligrafie, Punktieren und Aquarell, dazu die
' Kornmaske und die Vorschau fuer die Werkzeugleiste.
' Eigener Zustand: die Liste der Pinselnamen und der Korn-Zwischenspeicher.
' Herausgeloest am 2026-08-06 aus ImageProcessor.vb, Zeile fuer Zeile unveraendert.
Namespace Services

    Partial Public Class ImageProcessor

        ''' Mehrere Striche derselben Ebene (siehe EditorViewModel.AddBrushStroke - Striche werden
        ''' gesammelt, statt für jeden eine eigene Ebene anzulegen) sind im Punktestring per ";"
        ''' getrennt und werden hier als eigenständige Teilpfade (je ein eigenes MoveTo) gezeichnet -
        ''' eine einzige durchgehende Linie würde sie sonst fälschlich miteinander verbinden.
        ''' <summary>Bekannte Pinsel-Varianten (Stufe 2). "soft" ist der klassische weiche Rundpinsel;
        ''' die übrigen sind texturierte Presets. Radiergummi erzwingt immer "soft".</summary>
        Friend Shared ReadOnly BrushPresetKeys As String() = {"soft", "marker", "acrylic", "sandpaper", "pencil", "smear", "spatter",
                                                             "charcoal", "crayon", "airbrush", "calligraphy", "stipple", "watercolor"}

        Private Shared Function NormalizeBrushPreset(preset As String) As String
            If String.IsNullOrWhiteSpace(preset) Then Return "soft"
            Dim key = preset.Trim().ToLowerInvariant()
            Return If(Array.IndexOf(BrushPresetKeys, key) >= 0, key, "soft")
        End Function

        Private Shared Sub DrawBrushStroke(canvas As SKCanvas, strokes As IEnumerable(Of BrushStroke), width As Integer, height As Integer, stroke As SKColor, strokeWidth As Single, hardnessPercent As Single, flowPercent As Single, preset As String, isEraser As Boolean, Optional eraserFill As SKColor? = Nothing)
            If strokes Is Nothing Then Return

            ' Der Radiergummi bleibt immer der weiche Rundpinsel - Korn/Textur hätte dort keinen Sinn.
            Dim key = If(isEraser, "soft", NormalizeBrushPreset(preset))
            Dim resolvedStrokeWidth = Math.Max(1.0F, strokeWidth)
            Dim hardness = Clamp(hardnessPercent, 0, 100) / 100.0F
            Dim blurSigma = resolvedStrokeWidth * (1.0F - hardness) * 0.5F
            Dim flow = Clamp(flowPercent, 0, 100) / 100.0F

            ' Texturierte Presets laufen über eine eigene Ebene, in die eine Korn-Textur gestanzt wird.
            If key = "acrylic" OrElse key = "sandpaper" OrElse key = "pencil" OrElse
               key = "charcoal" OrElse key = "crayon" Then
                DrawGrainBrushStroke(canvas, strokes, width, height, stroke, resolvedStrokeWidth, blurSigma, flow, key)
                Return
            End If

            ' Schmieren/Farbkleckse werden entlang des Pfades gestempelt (richtungsabhängig bzw. gestreut).
            If key = "smear" OrElse key = "spatter" OrElse key = "airbrush" OrElse
               key = "calligraphy" OrElse key = "stipple" OrElse key = "watercolor" Then
                DrawStampBrushStroke(canvas, strokes, width, height, stroke, resolvedStrokeWidth, blurSigma, flow, key)
                Return
            End If

            Dim paintColor = stroke
            Dim blendMode = SKBlendMode.SrcOver
            If isEraser Then
                If eraserFill.HasValue AndAlso eraserFill.Value.Alpha > 0 Then
                    paintColor = eraserFill.Value
                Else
                    blendMode = SKBlendMode.DstOut
                End If
            End If

            ' Marker: harte, flache Chisel-Kante und halbtransparent, damit sich überkreuzende Striche
            ' sichtbar aufbauen (wie ein echter Filzstift). Sonst wie der weiche Rundpinsel.
            Dim isMarker = key = "marker"
            Dim effectiveFlow = If(isMarker, flow * 0.72F, flow)
            Dim cap = If(isMarker, SKStrokeCap.Square, SKStrokeCap.Round)
            Dim join = If(isMarker, SKStrokeJoin.Bevel, SKStrokeJoin.Round)

            Using paint = New SKPaint With {
                .Color = paintColor.WithAlpha(CByte(Clamp(paintColor.Alpha * effectiveFlow, 0, 255))),
                .Style = SKPaintStyle.Stroke,
                .StrokeWidth = resolvedStrokeWidth,
                .StrokeCap = cap,
                .StrokeJoin = join,
                .IsAntialias = True
            }
                paint.BlendMode = blendMode
                If blurSigma > 0.05F Then paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSigma)

                For Each brushStroke In strokes
                    If brushStroke Is Nothing OrElse brushStroke.Points.Count < 2 Then Continue For

                    Using path = New SKPath()
                        For i As Integer = 0 To brushStroke.Points.Count - 1
                            Dim p = brushStroke.Points(i)
                            Dim target = New SKPoint(Clamp(p.X, 0, width), Clamp(p.Y, 0, height))
                            If i = 0 Then
                                path.MoveTo(target)
                            Else
                                path.LineTo(target)
                            End If
                        Next
                        canvas.DrawPath(path, paint)
                    End Using
                Next
            End Using
        End Sub

        ''' <summary>Zeichnet einen Pinselstrich mit Schatten und/oder Glühen: der Strich (inkl. Textur/
        ''' Preset) wird zunächst auf eine eigene Ebene gerendert, dann werden daraus per DropShadowOnly
        ''' aus der echten Silhouette Glühen (Halo, ohne Versatz) und Schatten (mit Versatz) unter den
        ''' Strich komponiert. Größe/Blur/Versatz skalieren mit der Strichbreite.</summary>
        Private Shared Sub DrawBrushStrokeWithEffects(canvas As SKCanvas, ann As ImageAnnotation, width As Integer, height As Integer, strokeColor As SKColor, strokeWidth As Single)
            If ann.Strokes Is Nothing Then Return
            Dim minX = Single.MaxValue, minY = Single.MaxValue
            Dim maxX = Single.MinValue, maxY = Single.MinValue
            Dim any = False
            For Each bs In ann.Strokes
                If bs Is Nothing OrElse bs.Points.Count < 1 Then Continue For
                For Each p In bs.Points
                    minX = Math.Min(minX, p.X) : minY = Math.Min(minY, p.Y)
                    maxX = Math.Max(maxX, p.X) : maxY = Math.Max(maxY, p.Y)
                    any = True
                Next
            Next
            If Not any Then Return

            Dim objSize = Math.Max(1.0F, strokeWidth)
            Dim shadowDx = If(ann.ShadowEnabled, Clamp(ann.ShadowOffsetXPercent, -100, 100) / 100.0F * objSize, 0.0F)
            Dim shadowDy = If(ann.ShadowEnabled, Clamp(ann.ShadowOffsetYPercent, -100, 100) / 100.0F * objSize, 0.0F)
            Dim shadowSigma = If(ann.ShadowEnabled, Clamp(ann.ShadowBlur, 0, 100) / 100.0F * objSize * ShadowBlurSigmaFactor, 0.0F)
            Dim glowSigma = If(ann.GlowEnabled, Clamp(ann.GlowBlur, 0, 100) / 100.0F * objSize * 0.8F, 0.0F)

            Dim pad = objSize + Math.Abs(shadowDx) + Math.Abs(shadowDy) + Math.Max(shadowSigma, glowSigma) * 3.0F + 4.0F
            Dim left = CInt(Math.Floor(Clamp(minX - pad, 0, width)))
            Dim top = CInt(Math.Floor(Clamp(minY - pad, 0, height)))
            Dim right = CInt(Math.Ceiling(Clamp(maxX + pad, 0, width)))
            Dim bottom = CInt(Math.Ceiling(Clamp(maxY + pad, 0, height)))
            Dim w = right - left, h = bottom - top
            If w <= 0 OrElse h <= 0 Then Return

            Using layer = New SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using lc = New SKCanvas(layer)
                    lc.Clear(SKColors.Transparent)
                    lc.Translate(-left, -top)
                    DrawBrushStroke(lc, ann.Strokes, width, height, strokeColor, strokeWidth,
                                    ann.HardnessPercent, ann.FlowPercent, ann.BrushPreset, False)
                End Using

                ' Glühen zuerst (Halo hinter dem Strich), dann Schatten, dann der Strich selbst.
                If ann.GlowEnabled AndAlso glowSigma > 0.05F Then
                    Dim glowColor = ApplyAlpha(ParseColor(ann.GlowColor, SKColors.Yellow), Clamp(ann.GlowStrength, 0, 100) / 100.0F)
                    Using p = New SKPaint()
                        p.ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 0, glowSigma, glowSigma, glowColor)
                        canvas.DrawBitmap(layer, left, top, p)
                    End Using
                End If
                If ann.ShadowEnabled Then
                    Dim shadowColor = ApplyAlpha(ParseColor(ann.ShadowColor, New SKColor(0, 0, 0, 128)), Clamp(ann.ShadowStrength, 0, 100) / 100.0F)
                    Using p = New SKPaint()
                        p.ImageFilter = SKImageFilter.CreateDropShadowOnly(shadowDx, shadowDy, Math.Max(0.01F, shadowSigma), Math.Max(0.01F, shadowSigma), shadowColor)
                        canvas.DrawBitmap(layer, left, top, p)
                    End Using
                End If
                canvas.DrawBitmap(layer, left, top)
            End Using
        End Sub

        ' Grain-Cache: je Preset eine deterministisch erzeugte Alpha-Korn-Kachel (256x256), die als
        ' wiederholender Shader in die Strichform gestanzt wird. Deterministisch, damit Vorschau und
        ' gebackenes Ergebnis identisch sind und Re-Renders nicht flackern. Wird nie disposed (Cache).
        Private Shared ReadOnly _grainCacheLock As New Object()
        Private Shared ReadOnly _grainBitmaps As New Dictionary(Of String, SKBitmap)(StringComparer.Ordinal)

        Private Shared Function Hash01(a As Integer, b As Integer, seed As Integer) As Single
            ' Ganzzahl-Hash in Long-Arithmetik mit 32-Bit-Maskierung, damit VB keinen Overflow wirft.
            Dim n As Long = (CLng(a) * 73856093L) Xor (CLng(b) * 19349663L) Xor (CLng(seed) * 83492791L)
            n = n And &HFFFFFFFFL
            n = (n Xor (n >> 13)) And &HFFFFFFFFL
            n = (n * 40503L) And &HFFFFFFFFL
            n = (n Xor (n >> 7)) And &HFFFFFFFFL
            Return CSng(n And &HFFFFFFL) / 16777216.0F
        End Function

        Private Shared Function MapGrainAlpha(key As String, v As Single) As Byte
            Dim a As Single
            Select Case key
                Case "acrylic" : a = v * 1.7F - 0.15F      ' überwiegend deckend, raue Lücken
                Case "sandpaper" : a = v * 1.35F - 0.2F    ' gröber, mehr Lücken
                ' Kohle: harte Schwelle mit breiten Lücken - bröseliger, kontrastreicher als Bleistift.
                Case "charcoal" : a = If(v > 0.32F, (v - 0.32F) * 2.1F, 0.0F)
                ' Wachsmalstift: satt deckend mit nur wenigen Aussetzern - Wachs schmiert zu, es
                ' bröselt nicht wie Kohle. Genau daran unterscheiden sich die beiden im Strich.
                Case "crayon" : a = Clamp(v * 2.6F - 0.35F, 0.0F, 1.0F)
                Case Else ' pencil: feines, sparsames Graphitkorn
                    a = If(v > 0.4F, (v - 0.4F) * 1.5F, 0.0F)
            End Select
            Return CByte(Clamp(a * 255.0F, 0, 255))
        End Function

        Private Shared Function BuildGrainBitmap(key As String) As SKBitmap
            Const size As Integer = 256
            ' Zellgröße = Korngröße der Kachel. Kohle und Wachs sind gröber als Graphit.
            Dim cell As Integer
            Select Case key
                Case "sandpaper" : cell = 3
                Case "acrylic" : cell = 2
                Case "charcoal" : cell = 4
                Case "crayon" : cell = 5
                Case Else : cell = 1
            End Select
            Dim bmp = New SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul)
            Dim px(size * size - 1) As SKColor
            For y As Integer = 0 To size - 1
                For x As Integer = 0 To size - 1
                    Dim baseV = Hash01(x \ cell, y \ cell, 12345)
                    Dim fine = Hash01(x, y, 777)
                    Dim v = baseV * 0.7F + fine * 0.3F
                    px(y * size + x) = New SKColor(255, 255, 255, MapGrainAlpha(key, v))
                Next
            Next
            bmp.Pixels = px
            Return bmp
        End Function

        Private Shared Function GetGrainBitmap(key As String) As SKBitmap
            SyncLock _grainCacheLock
                Dim existing As SKBitmap = Nothing
                If _grainBitmaps.TryGetValue(key, existing) Then Return existing
                Dim bmp = BuildGrainBitmap(key)
                _grainBitmaps(key) = bmp
                Return bmp
            End SyncLock
        End Function

        ''' <summary>Zeichnet texturierte Striche: erst die weiche Strichform auf eine eigene Ebene, dann
        ''' die Korn-Kachel per DstIn hineingestanzt, dann als Ganzes aufs Bild komponiert. Das Korn wird
        ''' in globalen Bildkoordinaten gesampelt (Ebene ist entsprechend verschoben), damit sich
        ''' überlappende Striche dasselbe Texturfeld teilen.</summary>
        Private Shared Sub DrawGrainBrushStroke(canvas As SKCanvas, strokes As IEnumerable(Of BrushStroke), width As Integer, height As Integer, color As SKColor, strokeWidth As Single, blurSigma As Single, flow As Single, key As String)
            Dim minX = Single.MaxValue, minY = Single.MaxValue
            Dim maxX = Single.MinValue, maxY = Single.MinValue
            Dim any = False
            For Each brushStroke In strokes
                If brushStroke Is Nothing OrElse brushStroke.Points.Count < 2 Then Continue For
                For Each p In brushStroke.Points
                    minX = Math.Min(minX, p.X) : minY = Math.Min(minY, p.Y)
                    maxX = Math.Max(maxX, p.X) : maxY = Math.Max(maxY, p.Y)
                    any = True
                Next
            Next
            If Not any Then Return

            Dim pad = strokeWidth * 0.6F + blurSigma * 3.0F + 2.0F
            Dim left = CInt(Math.Floor(Clamp(minX - pad, 0, width)))
            Dim top = CInt(Math.Floor(Clamp(minY - pad, 0, height)))
            Dim right = CInt(Math.Ceiling(Clamp(maxX + pad, 0, width)))
            Dim bottom = CInt(Math.Ceiling(Clamp(maxY + pad, 0, height)))
            Dim w = right - left, h = bottom - top
            If w <= 0 OrElse h <= 0 Then Return

            Dim layerColor = color.WithAlpha(CByte(Clamp(color.Alpha * flow, 0, 255)))
            Using layer = New SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using lc = New SKCanvas(layer)
                    lc.Clear(SKColors.Transparent)
                    lc.Translate(-left, -top)

                    Using shapePaint = New SKPaint With {
                        .Color = layerColor,
                        .Style = SKPaintStyle.Stroke,
                        .StrokeWidth = strokeWidth,
                        .StrokeCap = SKStrokeCap.Round,
                        .StrokeJoin = SKStrokeJoin.Round,
                        .IsAntialias = True
                    }
                        If blurSigma > 0.05F Then shapePaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSigma)
                        For Each brushStroke In strokes
                            If brushStroke Is Nothing OrElse brushStroke.Points.Count < 2 Then Continue For
                            Using path = New SKPath()
                                For i As Integer = 0 To brushStroke.Points.Count - 1
                                    Dim p = brushStroke.Points(i)
                                    Dim target = New SKPoint(Clamp(p.X, 0, width), Clamp(p.Y, 0, height))
                                    If i = 0 Then path.MoveTo(target) Else path.LineTo(target)
                                Next
                                lc.DrawPath(path, shapePaint)
                            End Using
                        Next
                    End Using

                    ' Korn in globalen Koordinaten in die Strichform stanzen.
                    Using grainShader = SKShader.CreateBitmap(GetGrainBitmap(key), SKShaderTileMode.Repeat, SKShaderTileMode.Repeat)
                        Using grainPaint = New SKPaint With {.Shader = grainShader, .BlendMode = SKBlendMode.DstIn, .IsAntialias = False}
                            lc.DrawRect(New SKRect(left, top, right, bottom), grainPaint)
                        End Using
                    End Using
                End Using

                canvas.DrawBitmap(layer, left, top)
            End Using
        End Sub

        ''' <summary>Stempelbasierte Striche: der Pfad wird per SKPathMeasure abgetastet und an jedem
        ''' Schritt eine Form gesetzt. "smear" tupft langgezogene, weiche, zur Strichrichtung gedrehte
        ''' Ovale (verwischter Zug, der der Kurve folgt); "spatter" setzt einen gebrochenen Kern plus
        ''' zufällig gestreute Tropfen neben der Linie (viele kleine, wenige große - "zu viel Farbe").
        ''' Alle Zufallswerte stammen deterministisch aus dem Stempelindex, damit Vorschau, gebackenes
        ''' Bild und Re-Renders identisch bleiben.</summary>
        Private Shared Sub DrawStampBrushStroke(canvas As SKCanvas, strokes As IEnumerable(Of BrushStroke), width As Integer, height As Integer, color As SKColor, strokeWidth As Single, blurSigma As Single, flow As Single, key As String)
            Dim minX = Single.MaxValue, minY = Single.MaxValue
            Dim maxX = Single.MinValue, maxY = Single.MinValue
            Dim any = False
            For Each brushStroke In strokes
                If brushStroke Is Nothing OrElse brushStroke.Points.Count < 2 Then Continue For
                For Each p In brushStroke.Points
                    minX = Math.Min(minX, p.X) : minY = Math.Min(minY, p.Y)
                    maxX = Math.Max(maxX, p.X) : maxY = Math.Max(maxY, p.Y)
                    any = True
                Next
            Next
            If Not any Then Return

            Dim isSmear = key = "smear"
            ' Wie weit ein Stempel seitlich über die Spur hinausreicht - bestimmt den Rand der Ebene.
            Dim spread As Single
            Select Case key
                Case "smear" : spread = strokeWidth * 1.3F
                Case "airbrush" : spread = strokeWidth * 1.1F
                Case "calligraphy" : spread = strokeWidth * 0.8F
                Case "stipple" : spread = strokeWidth * 0.8F
                Case "watercolor" : spread = strokeWidth * 1.2F
                Case Else : spread = strokeWidth * 2.2F   ' spatter streut am weitesten
            End Select
            Dim pad = spread + blurSigma * 3.0F + 2.0F
            Dim left = CInt(Math.Floor(Clamp(minX - pad, 0, width)))
            Dim top = CInt(Math.Floor(Clamp(minY - pad, 0, height)))
            Dim right = CInt(Math.Ceiling(Clamp(maxX + pad, 0, width)))
            Dim bottom = CInt(Math.Ceiling(Clamp(maxY + pad, 0, height)))
            Dim w = right - left, h = bottom - top
            If w <= 0 OrElse h <= 0 Then Return

            Dim baseAlpha As Single = Clamp(color.Alpha * flow, 0, 255) / 255.0F

            Using layer = New SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using lc = New SKCanvas(layer)
                    lc.Clear(SKColors.Transparent)
                    lc.Translate(-left, -top)

                    Dim strokeOrdinal = 0
                    For Each brushStroke In strokes
                        If brushStroke Is Nothing OrElse brushStroke.Points.Count < 2 Then Continue For
                        Dim pts As New List(Of SKPoint)(brushStroke.Points.Count)
                        For Each p In brushStroke.Points
                            pts.Add(New SKPoint(Clamp(p.X, 0, width), Clamp(p.Y, 0, height)))
                        Next
                        Dim seedBase = 4242 + strokeOrdinal * 131
                        Select Case key
                            Case "smear" : DrawSmearStroke(lc, pts, color, strokeWidth, baseAlpha, blurSigma, seedBase)
                            Case "airbrush" : DrawAirbrushStroke(lc, pts, color, strokeWidth, baseAlpha, seedBase)
                            Case "calligraphy" : DrawCalligraphyStroke(lc, pts, color, strokeWidth, baseAlpha, blurSigma)
                            Case "stipple" : DrawStippleStroke(lc, pts, color, strokeWidth, baseAlpha, seedBase)
                            Case "watercolor" : DrawWatercolorStroke(lc, pts, color, strokeWidth, baseAlpha, seedBase)
                            Case Else : DrawSpatterStroke(lc, pts, color, strokeWidth, baseAlpha, blurSigma, seedBase)
                        End Select
                        strokeOrdinal += 1
                    Next
                End Using

                canvas.DrawBitmap(layer, left, top)
            End Using
        End Sub

        ''' <summary>Schmieren als Trockenpinsel/Borsten: ein weicher, blasser Grundkörper entlang des
        ''' Zuges plus viele dünne "Borsten"-Linien, die parallel zur Kurve laufen (je Punkt eigene
        ''' Normale, damit sie Kurven folgen). Zufällige Lücken, Deckkräfte und Anfangs-/End-Beschnitte
        ''' erzeugen die typischen Striationen und die auslaufenden Ränder. Deterministisch über seedBase.</summary>
        Private Shared Sub DrawSmearStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, blurSigma As Single, seedBase As Integer, Optional density As Single = 1.0F, Optional emboss As Boolean = False)
            Dim n = pts.Count
            If n < 2 Then Return

            ' Per-Punkt-Normale (senkrecht zur lokalen Richtung) für die seitlichen Borsten-Versätze.
            Dim normals(n - 1) As SKPoint
            For i As Integer = 0 To n - 1
                Dim aIdx = Math.Max(0, i - 1), bIdx = Math.Min(n - 1, i + 1)
                Dim tx = pts(bIdx).X - pts(aIdx).X, ty = pts(bIdx).Y - pts(aIdx).Y
                Dim tlen = CSng(Math.Sqrt(tx * tx + ty * ty))
                If tlen < 0.001F Then tlen = 1.0F
                normals(i) = New SKPoint(-ty / tlen, tx / tlen)
            Next

            ' Weicher, blasser Grundkörper - gibt dem Schmierer Substanz unter den Striationen.
            Using body = New SKPaint With {.IsAntialias = True, .Style = SKPaintStyle.Stroke,
                                           .StrokeCap = SKStrokeCap.Round, .StrokeJoin = SKStrokeJoin.Round,
                                           .StrokeWidth = strokeWidth * 0.9F,
                                           .Color = color.WithAlpha(CByte(Clamp(baseAlpha * 0.32F * density * 255.0F, 0, 255)))}
                body.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1.0F, strokeWidth * 0.2F + blurSigma))
                Using bodyPath = New SKPath()
                    bodyPath.MoveTo(pts(0))
                    For i As Integer = 1 To n - 1 : bodyPath.LineTo(pts(i)) : Next
                    lc.DrawPath(bodyPath, body)
                End Using
            End Using

            ' 3D-Impasto: jede Borste bekommt eine helle und eine dunkle Kante (fixe Lichtrichtung über
            ' die Normale), damit sie wie ein aufgetragener Farbwulst wirkt.
            Dim embossOff = Math.Max(1.0F, strokeWidth * 0.03F)

            Dim bristles = Math.Max(6, CInt(strokeWidth / 1.4F))
            ' PERF ("CPU hängt ~2 min nach großem Klecks-Strich"): früher trug
            ' jede Borste (×3 beim Impasto) einen eigenen MaskFilter-Blur - Skia rastert und blurt
            ' dafür JE ZEICHNUNG eine Maske in Strichregion-Größe; bei ~160 Borsten in Vollauflösung
            ' waren das Hunderte Blur-Durchläufe über eine Riesenregion (Minuten im Einback-Commit).
            ' Jetzt: alle Borsten SCHARF in EINEN SaveLayer, der beim Restore einmalig mit derselben
            ' Sigma geblurt wird - gleicher Weichzeichner, ein Durchlauf. Minimale Abweichung nur an
            ' Borsten-Überlappungen (Blur nach dem Mischen statt je Borste); Vorschau, Brücke und
            ' Einbacken nutzen dieselbe Routine und bleiben deshalb untereinander identisch.
            Dim bristleSigma = Math.Max(0.5F, strokeWidth * 0.03F + blurSigma)
            Using layerPaint = New SKPaint With {.ImageFilter = SKImageFilter.CreateBlur(bristleSigma, bristleSigma)}
                lc.SaveLayer(layerPaint)
                Using bristle = New SKPaint With {.IsAntialias = True, .Style = SKPaintStyle.Stroke,
                                                  .StrokeCap = SKStrokeCap.Round, .StrokeJoin = SKStrokeJoin.Round,
                                                  .StrokeWidth = Math.Max(1.0F, strokeWidth * 0.055F)}
                    For b As Integer = 0 To bristles - 1
                        If Hash01(b, 1, seedBase) < 0.1F Then Continue For ' Lücken = Streifen
                        Dim frac = (b / CSng(bristles - 1)) - 0.5F
                        Dim offset = frac * strokeWidth * 0.92F + (Hash01(b, 2, seedBase) - 0.5F) * strokeWidth * 0.08F
                        Dim a = Clamp(baseAlpha * (0.24F + Hash01(b, 3, seedBase) * 0.55F) * density, 0, 1)
                        ' Zufälliger Beschnitt vorne/hinten -> unterschiedlich lange, auslaufende Borsten.
                        Dim i0 = CInt(Math.Floor(Hash01(b, 4, seedBase) * 0.28F * (n - 1)))
                        Dim i1 = CInt(Math.Ceiling((0.55F + Hash01(b, 5, seedBase) * 0.45F) * (n - 1)))
                        i1 = Math.Min(n - 1, Math.Max(i0 + 1, i1))

                        If emboss Then
                            ' Schatten (dunkle Kante) auf der einen, Licht (helle Kante) auf der anderen Seite;
                            ' der Kern liegt zuletzt darüber, sodass nur schmale Kanten herausschauen.
                            bristle.Color = Shade(color, -0.45F).WithAlpha(CByte(Clamp(a * 0.9F * 255.0F, 0, 255)))
                            Using sp = BuildOffsetBristlePath(pts, normals, i0, i1, offset + embossOff) : lc.DrawPath(sp, bristle) : End Using
                            bristle.Color = Shade(color, 0.55F).WithAlpha(CByte(Clamp(a * 0.9F * 255.0F, 0, 255)))
                            Using hp = BuildOffsetBristlePath(pts, normals, i0, i1, offset - embossOff) : lc.DrawPath(hp, bristle) : End Using
                        End If

                        bristle.Color = color.WithAlpha(CByte(Clamp(a * 255.0F, 0, 255)))
                        Using path = BuildOffsetBristlePath(pts, normals, i0, i1, offset)
                            lc.DrawPath(path, bristle)
                        End Using
                    Next
                End Using
                lc.Restore()
            End Using
        End Sub

        ''' <summary>Baut den Teilpfad einer Borste: Punkte i0..i1, jeweils um <paramref name="offset"/>
        ''' entlang der Punkt-Normale seitlich versetzt (folgt so der Kurve).</summary>
        Private Shared Function BuildOffsetBristlePath(pts As List(Of SKPoint), normals As SKPoint(), i0 As Integer, i1 As Integer, offset As Single) As SKPath
            Dim path = New SKPath()
            For i As Integer = i0 To i1
                Dim px = pts(i).X + normals(i).X * offset
                Dim py = pts(i).Y + normals(i).Y * offset
                If i = i0 Then path.MoveTo(px, py) Else path.LineTo(px, py)
            Next
            Return path
        End Function

        ''' <summary>Hellt (factor &gt; 0, Richtung Weiß) oder dunkelt (factor &lt; 0, Richtung Schwarz)
        ''' eine Farbe ab; Alpha bleibt unberührt.</summary>
        Private Shared Function Shade(c As SKColor, factor As Single) As SKColor
            If factor >= 0 Then
                Dim f = Clamp(factor, 0, 1)
                Return New SKColor(CByte(c.Red + (255 - c.Red) * f), CByte(c.Green + (255 - c.Green) * f), CByte(c.Blue + (255 - c.Blue) * f), c.Alpha)
            Else
                Dim f = 1.0F + Clamp(factor, -1, 0)
                Return New SKColor(CByte(c.Red * f), CByte(c.Green * f), CByte(c.Blue * f), c.Alpha)
            End If
        End Function

        ''' <summary>Farbkleckse: ein satter, durchgehender Kern-Strich plus zufällig um die Linie
        ''' gestreute Tropfen (r^3-verteilt: viele kleine, wenige große) - der "zu viel Farbe"-Effekt.
        ''' Deterministisch über seedBase.</summary>
        Private Shared Sub DrawSpatterStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, blurSigma As Single, seedBase As Integer)
            Dim n = pts.Count
            If n < 2 Then Return

            Using path = New SKPath()
                path.MoveTo(pts(0))
                For i As Integer = 1 To n - 1 : path.LineTo(pts(i)) : Next

                ' Grundlinie: die streifige Schmier-Struktur mit 3D-Impasto - satt (dichte Borsten) mit
                ' sichtbaren Striationen und heller/dunkler Kante statt eines glatten Rohrs.
                DrawSmearStroke(lc, pts, color, strokeWidth, baseAlpha, blurSigma, seedBase, 1.2F, True)

                ' Gestreute Tropfen entlang des Pfades.
                Using drops = New SKPaint With {.IsAntialias = True, .Style = SKPaintStyle.Fill}
                    Using pm = New SKPathMeasure(path, False)
                        Dim length = pm.Length
                        Dim spacing = Math.Max(1.0F, strokeWidth * 0.5F)
                        Dim d As Single = 0.0F
                        Dim stampIndex = 0
                        Do
                            Dim pos As SKPoint, tan As SKPoint
                            If pm.GetPositionAndTangent(d, pos, tan) Then
                                Dim perpX = -tan.Y, perpY = tan.X
                                For j As Integer = 0 To 3
                                    If Hash01(stampIndex, 10 + j, seedBase) < 0.55F Then
                                        Dim rr = Hash01(stampIndex, 20 + j, seedBase)
                                        Dim dropR = strokeWidth * (0.03F + rr * rr * rr * 0.33F)
                                        ' Näher an der Spur: seitlicher Versatz ~1x statt 3x Strichbreite.
                                        Dim offN = (Hash01(stampIndex, 30 + j, seedBase) - 0.5F) * strokeWidth * 1.5F
                                        Dim offT = (Hash01(stampIndex, 40 + j, seedBase) - 0.5F) * strokeWidth * 1.2F
                                        Dim a = baseAlpha * (0.55F + Hash01(stampIndex, 50 + j, seedBase) * 0.45F)
                                        drops.Color = color.WithAlpha(CByte(Clamp(a * 255.0F, 0, 255)))
                                        lc.DrawCircle(pos.X + perpX * offN + tan.X * offT, pos.Y + perpY * offN + tan.Y * offT, dropR, drops)
                                    End If
                                Next
                            End If
                            stampIndex += 1
                            If d >= length Then Exit Do
                            d = Math.Min(length, d + spacing)
                        Loop
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Sprühdose: dichte Wolke feiner Punkte um die Spur, mit radial abnehmender
        ''' Wahrscheinlichkeit und Deckkraft. Anders als "spatter" (wenige große Tropfen weit gestreut)
        ''' baut sich hier durch viele winzige Punkte ein weicher Farbauftrag auf, der bei mehrfachem
        ''' Überfahren dichter wird. Alle Zufallswerte deterministisch aus Stempelindex + seedBase.</summary>
        Private Shared Sub DrawAirbrushStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, seedBase As Integer)
            If pts.Count < 2 Then Return
            Using path = New SKPath()
                path.MoveTo(pts(0))
                For i As Integer = 1 To pts.Count - 1 : path.LineTo(pts(i)) : Next

                Dim radius = Math.Max(1.0F, strokeWidth * 0.5F)
                Dim dotR = Math.Max(0.4F, strokeWidth * 0.045F)
                Using dots = New SKPaint With {.IsAntialias = True, .Style = SKPaintStyle.Fill}
                    Using pm = New SKPathMeasure(path, False)
                        Dim length = pm.Length
                        ' Eng abtasten: die Punktwolke soll durchgehend wirken, nicht perlenartig.
                        Dim spacing = Math.Max(0.6F, strokeWidth * 0.12F)
                        Dim perStep = Math.Max(3, CInt(Math.Min(26, strokeWidth * 0.55F)))
                        Dim d As Single = 0.0F
                        Dim stampIndex = 0
                        Do
                            Dim pos As SKPoint, tan As SKPoint
                            If pm.GetPositionAndTangent(d, pos, tan) Then
                                For j As Integer = 0 To perStep - 1
                                    ' Sqrt der Zufallszahl = flächengleiche Verteilung in der Kreisscheibe;
                                    ' ohne das drängen sich die Punkte in der Mitte.
                                    Dim rr = CSng(Math.Sqrt(Hash01(stampIndex, 60 + j, seedBase)))
                                    Dim ang = Hash01(stampIndex, 90 + j, seedBase) * 6.2831853F
                                    Dim rad = rr * radius
                                    Dim a = baseAlpha * (1.0F - rr * 0.85F) * 0.85F
                                    If a <= 0.004F Then Continue For
                                    dots.Color = color.WithAlpha(CByte(Clamp(a * 255.0F, 0, 255)))
                                    lc.DrawCircle(pos.X + CSng(Math.Cos(ang)) * rad,
                                                  pos.Y + CSng(Math.Sin(ang)) * rad, dotR, dots)
                                Next
                            End If
                            stampIndex += 1
                            If d >= length Then Exit Do
                            d = Math.Min(length, d + spacing)
                        Loop
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Kalligrafie-Feder: eine flache Feder mit FESTEM Anstellwinkel wird entlang des Pfades
        ''' gestempelt. Der sichtbare Strich wird dadurch dort breit, wo die Bewegung quer zur Feder läuft,
        ''' und dünn, wo sie ihr folgt - genau das macht den Schwung einer Breitfeder aus. Deshalb hier
        ''' KEIN Zufall: die Federkante ist ein fester Winkel, keine Textur.</summary>
        Private Shared Sub DrawCalligraphyStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, blurSigma As Single)
            If pts.Count < 2 Then Return
            Const nibAngleDeg As Single = 40.0F
            Dim nibRad = nibAngleDeg * 0.0174532925F
            Dim nx = CSng(Math.Cos(nibRad)), ny = CSng(Math.Sin(nibRad))
            Dim half = Math.Max(0.5F, strokeWidth * 0.5F)
            ' Dicke der Feder quer zur Kante - eine echte Breitfeder ist schmal, nicht rund.
            Dim nibThickness = Math.Max(1.0F, strokeWidth * 0.16F)

            Using path = New SKPath()
                path.MoveTo(pts(0))
                For i As Integer = 1 To pts.Count - 1 : path.LineTo(pts(i)) : Next

                Using paint = New SKPaint With {
                    .Color = color.WithAlpha(CByte(Clamp(baseAlpha * 255.0F, 0, 255))),
                    .IsAntialias = True,
                    .Style = SKPaintStyle.Stroke,
                    .StrokeWidth = nibThickness,
                    .StrokeCap = SKStrokeCap.Round
                }
                    If blurSigma > 0.05F Then paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blurSigma * 0.35F)
                    Using pm = New SKPathMeasure(path, False)
                        Dim length = pm.Length
                        ' Sehr eng stempeln, sonst zerfällt der Zug in einzelne Federabdrücke.
                        Dim spacing = Math.Max(0.35F, strokeWidth * 0.07F)
                        Dim d As Single = 0.0F
                        Do
                            Dim pos As SKPoint, tan As SKPoint
                            If pm.GetPositionAndTangent(d, pos, tan) Then
                                lc.DrawLine(pos.X - nx * half, pos.Y - ny * half,
                                            pos.X + nx * half, pos.Y + ny * half, paint)
                            End If
                            If d >= length Then Exit Do
                            d = Math.Min(length, d + spacing)
                        Loop
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Punktraster: gleichmäßig verteilte Tupfen entlang der Spur mit leichtem Versatz und
        ''' wechselnder Größe. Ergibt eine gepunktete Linie (Stippling/Pointillismus) statt eines
        ''' geschlossenen Zuges - der Abstand skaliert mit der Strichbreite.</summary>
        Private Shared Sub DrawStippleStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, seedBase As Integer)
            If pts.Count < 2 Then Return
            Using path = New SKPath()
                path.MoveTo(pts(0))
                For i As Integer = 1 To pts.Count - 1 : path.LineTo(pts(i)) : Next

                Using dot = New SKPaint With {.IsAntialias = True, .Style = SKPaintStyle.Fill}
                    Using pm = New SKPathMeasure(path, False)
                        Dim length = pm.Length
                        Dim spacing = Math.Max(2.0F, strokeWidth * 0.85F)
                        Dim d As Single = 0.0F
                        Dim stampIndex = 0
                        Do
                            Dim pos As SKPoint, tan As SKPoint
                            If pm.GetPositionAndTangent(d, pos, tan) Then
                                Dim perpX = -tan.Y, perpY = tan.X
                                Dim jitter = (Hash01(stampIndex, 11, seedBase) - 0.5F) * strokeWidth * 0.35F
                                Dim rr = 0.22F + Hash01(stampIndex, 22, seedBase) * 0.16F
                                Dim a = baseAlpha * (0.75F + Hash01(stampIndex, 33, seedBase) * 0.25F)
                                dot.Color = color.WithAlpha(CByte(Clamp(a * 255.0F, 0, 255)))
                                lc.DrawCircle(pos.X + perpX * jitter, pos.Y + perpY * jitter, strokeWidth * rr, dot)
                            End If
                            stampIndex += 1
                            If d >= length Then Exit Do
                            d = Math.Min(length, d + spacing)
                        Loop
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Aquarell: mehrere lasierende Durchgänge unterschiedlicher Breite mit weichen Rändern,
        ''' dazu eine dunklere, unruhige Randlinie. Der Reiz von Aquarell liegt im ÜBEREINANDER - jede
        ''' Lage ist fast durchsichtig, erst die Summe ergibt die Farbe, und die Ränder laufen aus.</summary>
        Private Shared Sub DrawWatercolorStroke(lc As SKCanvas, pts As List(Of SKPoint), color As SKColor, strokeWidth As Single, baseAlpha As Single, seedBase As Integer)
            If pts.Count < 2 Then Return
            Using path = New SKPath()
                path.MoveTo(pts(0))
                For i As Integer = 1 To pts.Count - 1 : path.LineTo(pts(i)) : Next

                ' Von breit/blass nach schmal/kräftiger - der Kern wird dadurch von selbst satter.
                Dim widths = New Single() {1.15F, 0.92F, 0.66F, 0.42F}
                Dim alphas = New Single() {0.20F, 0.24F, 0.28F, 0.34F}
                For layerIndex As Integer = 0 To widths.Length - 1
                    Dim w = Math.Max(1.0F, strokeWidth * widths(layerIndex))
                    Dim a = baseAlpha * alphas(layerIndex)
                    ' Winziger Versatz je Lage: die Lagen decken sich nicht exakt, so entstehen die
                    ' typischen Farbränder, wo sich zwei Lasuren überlappen.
                    Dim offX = (Hash01(layerIndex, 7, seedBase) - 0.5F) * strokeWidth * 0.12F
                    Dim offY = (Hash01(layerIndex, 8, seedBase) - 0.5F) * strokeWidth * 0.12F
                    Using paint = New SKPaint With {
                        .Color = color.WithAlpha(CByte(Clamp(a * 255.0F, 0, 255))),
                        .IsAntialias = True,
                        .Style = SKPaintStyle.Stroke,
                        .StrokeWidth = w,
                        .StrokeCap = SKStrokeCap.Round,
                        .StrokeJoin = SKStrokeJoin.Round,
                        .MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, w * 0.22F)
                    }
                        lc.Save()
                        lc.Translate(offX, offY)
                        lc.DrawPath(path, paint)
                        lc.Restore()
                    End Using
                Next

                ' Randlinie: bei echtem Aquarell sammelt sich Pigment am austrocknenden Rand.
                Using edge = New SKPaint With {
                    .Color = color.WithAlpha(CByte(Clamp(baseAlpha * 0.30F * 255.0F, 0, 255))),
                    .IsAntialias = True,
                    .Style = SKPaintStyle.Stroke,
                    .StrokeWidth = Math.Max(1.0F, strokeWidth * 0.10F),
                    .StrokeCap = SKStrokeCap.Round,
                    .MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, strokeWidth * 0.06F)
                }
                    Using outline = New SKPath()
                        Using measure = New SKPathMeasure(path, False)
                            Dim length = measure.Length
                            Dim stepLen As Single = Math.Max(1.0F, strokeWidth * 0.3F)
                            Dim d As Single = 0.0F
                            Dim first = True
                            Dim idx = 0
                            Do
                                Dim pos As SKPoint, tan As SKPoint
                                If measure.GetPositionAndTangent(d, pos, tan) Then
                                    Dim perpX = -tan.Y, perpY = tan.X
                                    Dim wob = 0.42F + Hash01(idx, 5, seedBase) * 0.12F
                                    Dim ex = pos.X + perpX * strokeWidth * wob
                                    Dim ey = pos.Y + perpY * strokeWidth * wob
                                    If first Then outline.MoveTo(ex, ey) : first = False Else outline.LineTo(ex, ey)
                                End If
                                idx += 1
                                If d >= length Then Exit Do
                                d = Math.Min(length, d + stepLen)
                            Loop
                        End Using
                        lc.DrawPath(outline, edge)
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Rendert einen Beispielstrich einer Pinsel-Variante als kleine Vorschau (z. B. für den
        ''' Pinsel-Picker). Nutzt dieselbe Zeichenroutine wie das echte Malen, damit die Vorschau exakt
        ''' dem Ergebnis entspricht. Rückgabe muss vom Aufrufer disposed werden.</summary>
        Public Shared Function RenderBrushStrokePreview(preset As String, widthPx As Integer, heightPx As Integer, color As SKColor) As SKBitmap
            Dim w = Math.Max(8, widthPx)
            Dim h = Math.Max(8, heightPx)
            Dim bmp = New SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)
            Using canvas = New SKCanvas(bmp)
                canvas.Clear(SKColors.Transparent)
                ' Ein leicht geschwungener Strich quer über die Kachel, wie in Pinsel-Bibliotheken üblich.
                Dim midY = h / 2.0F
                Dim amp = h * 0.16F
                Dim pts As New List(Of StrokePoint)()
                Dim x0 = w * 0.06F, x1 = w * 0.94F
                Dim steps = 48
                For i As Integer = 0 To steps
                    Dim t = i / CSng(steps)
                    Dim x = x0 + (x1 - x0) * t
                    Dim y = midY + CSng(Math.Sin(t * Math.PI * 1.6 - 0.4)) * amp
                    pts.Add(New StrokePoint(x, y))
                Next
                Dim strokeWidth = Math.Max(2.0F, h * 0.34F)
                Dim strokes = New List(Of BrushStroke) From {New BrushStroke(pts)}
                DrawBrushStroke(canvas, strokes, w, h, color, strokeWidth, 100.0F, 100.0F, preset, False)
            End Using
            Return bmp
        End Function

    End Class

End Namespace
