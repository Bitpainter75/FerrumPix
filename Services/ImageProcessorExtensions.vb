Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ' Die Erweiterungen in der Bildkette: aus der Beschreibung einer Erweiterung
    ' (ExtensionDefinition) wird ein Shader, und der laeuft als Stufe wie die eingebauten.
    '
    ' EINE RECHNUNG, DREI WEGE: auf der Karte in der Sitzung der Detailstufen, auf dem Prozessor
    ' ueber Skias eigenen Shader-Lauf (in Baendern auf allen Kernen) und auf dem tiefen Bild der
    ' 16-Bit-Ausgabe. Es ist jedes Mal DERSELBE Text des Autors; nur der Rahmen darum liest und
    ' schreibt je nach Bildformat anders. Eine zweite, von Hand geschriebene Fassung fuer den
    ' Prozessor gibt es nicht und darf es nicht geben: ein Autor liefert eine Rechnung, nicht drei.
    Partial Public Class ImageProcessor

        ''' <summary>Was eine Erweiterung an Hilfsfunktionen vorfindet. Alle Namen beginnen mit
        ''' "fp", und Reglerschluessel duerfen so nicht beginnen (ExtensionService.ReadControl), damit
        ''' eine spaeter ergaenzte Funktion keine bestehende Erweiterung bricht.</summary>
        Private Const ExtensionShaderHelpers As String = "
float fpLuma(float3 c) {
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}
float fpToLinear1(float v) {
    return v <= 0.04045 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4);
}
float fpToSrgb1(float v) {
    return v <= 0.0031308 ? 12.92 * v : 1.055 * pow(v, 1.0 / 2.4) - 0.055;
}
float3 fpToLinear(float3 c) {
    c = clamp(c, 0.0, 1.0);
    return float3(fpToLinear1(c.r), fpToLinear1(c.g), fpToLinear1(c.b));
}
float3 fpToSrgb(float3 c) {
    c = clamp(c, 0.0, 1.0);
    return float3(fpToSrgb1(c.r), fpToSrgb1(c.g), fpToSrgb1(c.b));
}
float fpHue1(float a, float b, float t) {
    if (t < 0.0) t += 1.0;
    if (t > 1.0) t -= 1.0;
    if (t < 1.0 / 6.0) return a + (b - a) * 6.0 * t;
    if (t < 0.5) return b;
    if (t < 2.0 / 3.0) return a + (b - a) * (2.0 / 3.0 - t) * 6.0;
    return a;
}
float3 fpHslToRgb(float3 hsl) {
    float h = fract(hsl.x), s = clamp(hsl.y, 0.0, 1.0), l = clamp(hsl.z, 0.0, 1.0);
    if (s <= 0.000001) return float3(l);
    float b = l <= 0.5 ? l * (1.0 + s) : l + s - l * s;
    float a = 2.0 * l - b;
    return float3(fpHue1(a, b, h + 1.0 / 3.0), fpHue1(a, b, h), fpHue1(a, b, h - 1.0 / 3.0));
}
float3 fpRgbToHsl(float3 c) {
    float hi = max(c.r, max(c.g, c.b)), lo = min(c.r, min(c.g, c.b));
    float l = (hi + lo) * 0.5, d = hi - lo;
    if (d <= 0.000001) return float3(0.0, 0.0, l);
    float s = l > 0.5 ? d / (2.0 - hi - lo) : d / (hi + lo);
    float h;
    if (hi == c.r) h = (c.g - c.b) / d + (c.g < c.b ? 6.0 : 0.0);
    else if (hi == c.g) h = (c.b - c.r) / d + 2.0;
    else h = (c.r - c.g) / d + 4.0;
    return float3(h / 6.0, s, l);
}
"

        ''' <summary>Der Rahmen um die Rechnung fuer ein 8-Bit-Bild: liest wie die eingebauten
        ''' Stufen in Bytes, gibt der Rechnung die Farbe ohne Deckungsanteil von 0 bis 1 und
        ''' schreibt gerundet zurueck. Ein voll durchsichtiger Punkt bleibt, wie er ist.</summary>
        Private Const ExtensionShaderMain As String = "
half4 main(float2 p) {
    float4 b = toBytes(src.eval(p));
    if (b.a <= 0.0) return half4(0.0);
    float3 c = clamp(effect(b.rgb / b.a), 0.0, 1.0);
    return writePremul(floor(c * 255.0 + 0.5), b.a);
}
"

        ''' <summary>Derselbe Rahmen fuer das tiefe Bild: ohne den Umweg ueber Bytes, damit die
        ''' 16 Bit erhalten bleiben.</summary>
        Private Const ExtensionShaderMainDeep As String = "
half4 main(float2 p) {
    float4 s = float4(src.eval(p));
    if (s.a <= 0.0) return half4(0.0);
    float3 c = clamp(effect(s.rgb / s.a), 0.0, 1.0);
    return half4(half3(c * s.a), half(s.a));
}
"

        ''' <summary>Uebersetzt die Rechnung einer Erweiterung in ihre beiden Shader und haengt sie
        ''' an die Erweiterung. Liefert leer bei Erfolg, sonst die Meldung des Uebersetzers mit
        ''' Zeilennummern, die im Text des Autors zaehlen und nicht im Rahmen davor.</summary>
        Friend Shared Function BuildExtensionEffects(definition As ExtensionDefinition) As String
            Dim head As New StringBuilder()
            head.Append(StageShaderCommon)
            head.Append("uniform shader src;").Append(vbLf)
            For Each control In definition.Controls
                head.Append("uniform float ").Append(control.Key).Append(";").Append(vbLf)
            Next
            head.Append(ExtensionShaderHelpers)
            Dim prefix = head.ToString()
            Dim prefixLines = prefix.Count(Function(c) c = ControlChars.Lf)
            Dim body = definition.ShaderSource.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf) & vbLf

            Dim errors As String = Nothing
            Dim effect = SKRuntimeEffect.CreateShader(prefix & body & ExtensionShaderMain, errors)
            If effect Is Nothing Then Return ExtensionShaderErrors(errors, prefixLines)
            Dim deepEffect = SKRuntimeEffect.CreateShader(prefix & body & ExtensionShaderMainDeep, errors)
            If deepEffect Is Nothing Then
                effect.Dispose()
                Return ExtensionShaderErrors(errors, prefixLines)
            End If
            definition.Effect = effect
            definition.DeepEffect = deepEffect
            Return ""
        End Function

        ''' <summary>Die Meldung des Uebersetzers mit Zeilennummern im Text des Autors. Fehlt die
        ''' Funktion "effect" ganz, sagt der Uebersetzer nur, dass der Rahmen sie nicht findet;
        ''' das wird hier in einen Satz uebersetzt, den ein Autor versteht.</summary>
        Private Shared Function ExtensionShaderErrors(errors As String, prefixLines As Integer) As String
            Dim text = If(errors, "").Trim()
            If text.Length = 0 Then Return "the shader did not compile"
            text = Regex.Replace(text, "error: (\d+):",
                Function(m)
                    Dim line = Integer.Parse(m.Groups(1).Value, Globalization.CultureInfo.InvariantCulture) - prefixLines
                    Return "error: " & If(line >= 1, "line " & line.ToString(Globalization.CultureInfo.InvariantCulture), "frame") & ":"
                End Function)
            If text.Contains("'effect'") Then
                text &= vbLf & "The shader must define: float3 effect(float3 color)"
            End If
            Return text
        End Function

        ''' <summary>Die Werte der Regler fuer einen Lauf: was das Rezept nennt, in den Grenzen der
        ''' Erweiterung, und fuer alles, was es nicht nennt, die Vorgabe. Ein Wert unter einem
        ''' Schluessel, den die Erweiterung nicht (mehr) kennt, zaehlt nicht.</summary>
        Friend Shared Function ExtensionUniformValues(definition As ExtensionDefinition, entry As ExtensionEntry) As (Key As String, Value As Single)()
            Return definition.Controls.Select(
                Function(control)
                    Dim value As Double
                    If entry Is Nothing OrElse Not entry.Values.TryGetValue(control.Key, value) Then value = control.DefaultValue
                    Return (control.Key, CSng(control.Clamp(value)))
                End Function).ToArray()
        End Function

        ''' <summary>Laeuft in diesem Rezept eine Erweiterung, die auf diesem Rechner auch vorliegt?</summary>
        Friend Shared Function HasRunnableExtensions(adj As ImageAdjustments) As Boolean
            If adj Is Nothing OrElse String.IsNullOrEmpty(adj.ExtensionSettings) Then Return False
            Return ExtensionRecipe.Parse(adj.ExtensionSettings).
                Any(Function(e) e.Enabled AndAlso ExtensionService.Find(e.Id) IsNot Nothing)
        End Function

        ''' <summary>Was die Erweiterungen zum Schluessel der Zwischenspeicher beitragen: ihre
        ''' Einstellungen und der Stand des Bestands. Ohne Einstellungen leer, damit ein Rezept ohne
        ''' Erweiterung nicht hinfaellig wird, nur weil jemand eine Erweiterung laedt.</summary>
        Friend Shared Function ExtensionsKey(adj As ImageAdjustments) As String
            If adj Is Nothing OrElse String.IsNullOrEmpty(adj.ExtensionSettings) Then Return ""
            Return adj.ExtensionSettings & "#" & ExtensionService.Revision.ToString(Globalization.CultureInfo.InvariantCulture)
        End Function

        ''' <summary>Rechnet die Erweiterungen einer Stelle der Kette, in der Reihenfolge des
        ''' Rezepts. Eine ausgeschaltete, eine fehlende und eine fuer die andere Stelle bestimmte
        ''' Erweiterung tut hier nichts.</summary>
        Private Shared Function ApplyExtensionStages(session As GpuDetailSession, processed As SKBitmap, ByRef owned As Boolean,
                                                     adj As ImageAdjustments, stage As String) As SKBitmap
            If String.IsNullOrEmpty(adj.ExtensionSettings) Then Return processed
            For Each entry In ExtensionRecipe.Parse(adj.ExtensionSettings)
                If Not entry.Enabled Then Continue For
                Dim definition = ExtensionService.Find(entry.Id)
                If definition Is Nothing OrElse definition.Effect Is Nothing Then Continue For
                If Not String.Equals(definition.Stage, stage, StringComparison.Ordinal) Then Continue For
                Dim values = ExtensionUniformValues(definition, entry)
                processed = RunDetailStage(session, processed, owned, "Pixel: Erweiterung " & definition.Id,
                                           ExtensionOnGpu(definition, values),
                                           Function(s) If(RunExtensionOnBitmap(s, definition.Effect, values), CopyBitmap(s)),
                                           Function(d) RunExtensionOnBitmap(d, definition.DeepEffect, values))
            Next
            Return processed
        End Function

        Private Shared Function ExtensionOnGpu(definition As ExtensionDefinition,
                                               values As (Key As String, Value As Single)()) As Func(Of GRContext, SKImage, SKSurface)
            Return Function(gr, image)
                       Dim u = New SKRuntimeEffectUniforms(definition.Effect)
                       For Each value In values
                           u(value.Key) = value.Value
                       Next
                       Return DrawStage(gr, image.Width, image.Height, definition.Effect, u, ("src", image))
                   End Function
        End Function

        ''' <summary>Kleinste Bandhoehe beim Lauf auf dem Prozessor. Darunter kostet das Anlegen
        ''' von Flaeche und Shader je Band mehr, als der zweite Kern bringt.</summary>
        Private Const ExtensionBandMinRows As Integer = 64

        ''' <summary>Rechnet einen Erweiterungs-Shader ueber ein Bild im Speicher, 8 Bit oder tief,
        ''' und liefert ein neues Bild im selben Format. Skia zeichnet einen Shader auf dem
        ''' Prozessor mit EINEM Faden; deshalb in waagerechten Baendern, jedes mit eigener
        ''' Zeichenflaeche auf seinem Teil des Zielspeichers. Die Rechnung sieht nur den einen
        ''' Bildpunkt, die Baender beruehren einander also nicht. Nothing, wenn Skia fuer dieses
        ''' Format keine Zeichenflaeche hergibt; die Stufe gilt dann als nicht gerechnet.</summary>
        Friend Shared Function RunExtensionOnBitmap(source As SKBitmap, effect As SKRuntimeEffect,
                                                    values As (Key As String, Value As Single)()) As SKBitmap
            If source Is Nothing OrElse effect Is Nothing Then Return Nothing
            Dim info = source.Info
            Dim result = New SKBitmap(info)
            Dim pixels = result.GetPixels()
            If pixels = IntPtr.Zero Then
                result.Dispose()
                Return Nothing
            End If
            Dim width = info.Width, height = info.Height, rowBytes = result.RowBytes
            Dim bands = Math.Max(1, Math.Min(Environment.ProcessorCount, height \ ExtensionBandMinRows))
            Dim failed = False
            Try
                Using image = SKImage.FromBitmap(source)
                    Parallel.For(0, bands,
                        Sub(band)
                            Dim top = CInt(CLng(height) * band \ bands)
                            Dim bottom = CInt(CLng(height) * (band + 1) \ bands)
                            If bottom <= top Then Return
                            Using target As New SKBitmap()
                                If Not target.InstallPixels(info.WithSize(width, bottom - top), IntPtr.Add(pixels, top * rowBytes), rowBytes) Then
                                    failed = True
                                    Return
                                End If
                                Dim uniforms = New SKRuntimeEffectUniforms(effect)
                                For Each value In values
                                    uniforms(value.Key) = value.Value
                                Next
                                Dim children = New SKRuntimeEffectChildren(effect)
                                Using child = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                                                             New SKSamplingOptions(SKFilterMode.Nearest)),
                                      canvas As New SKCanvas(target)
                                    children("src") = child
                                    Using shader = effect.ToShader(uniforms, children),
                                          paint = New SKPaint With {.Shader = shader, .BlendMode = SKBlendMode.Src}
                                        ' Das Band liegt im Bild bei "top"; die Rechnung liest ihren
                                        ' Bildpunkt an der Stelle des Bildes, nicht des Bandes.
                                        canvas.Translate(0, -top)
                                        canvas.DrawRect(0, top, width, bottom - top, paint)
                                    End Using
                                End Using
                            End Using
                        End Sub)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Extensions.Run", ex)
                failed = True
            End Try
            If failed Then
                result.Dispose()
                Return Nothing
            End If
            Return result
        End Function

    End Class

End Namespace
