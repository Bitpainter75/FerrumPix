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
    float3 c = clamp(FP_EFFECT_CALL(b.rgb / b.a), 0.0, 1.0);
    return writePremul(floor(c * 255.0 + 0.5), b.a);
}
"

        ''' <summary>Derselbe Rahmen fuer das tiefe Bild: ohne den Umweg ueber Bytes, damit die
        ''' 16 Bit erhalten bleiben.</summary>
        Private Const ExtensionShaderMainDeep As String = "
half4 main(float2 p) {
    float4 s = float4(src.eval(p));
    if (s.a <= 0.0) return half4(0.0);
    float3 c = clamp(FP_EFFECT_CALL(s.rgb / s.a), 0.0, 1.0);
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
                If control.IsColor Then
                    head.Append("uniform float3 ").Append(control.Key).Append(";").Append(vbLf)
                ElseIf control.IsCurve Then
                    ' DIE KURVE IST EINE TABELLE mit 256 Stuetzwerten in einem Bild von 256 mal 1
                    ' Punkten, je Wert 16 Bit verteilt auf Rot und Gruen (ExtensionCurveTable). Ein
                    ' Feld als Uniform ginge nicht: die Shadersprache erlaubt dort keinen Zugriff
                    ' ueber einen gerechneten Index. Fuer den Autor ist sie eine Funktion mit dem
                    ' Namen des Schluessels, linear zwischen den Stuetzwerten.
                    Dim k = control.Key
                    head.Append("uniform shader fpCurveTable_").Append(k).Append(";").Append(vbLf)
                    head.Append("float fpCurveAt_").Append(k).Append("(float i) {").Append(vbLf)
                    head.Append("    half4 c = fpCurveTable_").Append(k).Append(".eval(float2(i + 0.5, 0.5));").Append(vbLf)
                    head.Append("    return (floor(float(c.r) * 255.0 + 0.5) * 256.0 + floor(float(c.g) * 255.0 + 0.5)) / 65535.0;").Append(vbLf)
                    head.Append("}").Append(vbLf)
                    head.Append("float ").Append(k).Append("(float x) {").Append(vbLf)
                    head.Append("    float t = clamp(x, 0.0, 1.0) * 255.0;").Append(vbLf)
                    head.Append("    float i = floor(t);").Append(vbLf)
                    head.Append("    return mix(fpCurveAt_").Append(k).Append("(i), fpCurveAt_").Append(k).Append("(min(i + 1.0, 255.0)), t - i);").Append(vbLf)
                    head.Append("}").Append(vbLf)
                Else
                    head.Append("uniform float ").Append(control.Key).Append(";").Append(vbLf)
                End If
            Next
            head.Append(ExtensionShaderHelpers)
            Dim prefix = head.ToString()
            Dim prefixLines = prefix.Count(Function(c) c = ControlChars.Lf)
            Dim body = definition.ShaderSource.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf) & vbLf

            ' DIE LAGE IM BILD bekommt nur, wer sie verlangt: effect(float3 color, float2 pos). Sie
            ' laeuft von 0 bis 1 ueber das Bild, das die Kette gerade rechnet; das ist immer das
            ' ganze Bild (auch das Zoom-Detail rechnet es ganz und schneidet erst danach aus), bei
            ' einem Objekt das Objekt. Ueber einem Ausschnitt rechnet eine Erweiterung nie
            ' (LayerAdjustmentsAreCropSafe). Damit ein Effekt in Vorschau und Ausgabe gleich
            ' aussieht, gibt es die Lage nur als Anteil, die Groesse nur fuers Seitenverhaeltnis.
            definition.UsesPosition = PositionSignature.IsMatch(body)

            If definition.UsesPosition Then
                Dim positionHead = "uniform float2 fpSize;" & vbLf &
                                   "float2 fpPosOf(float2 p) { return p / max(fpSize, float2(1.0)); }" & vbLf
                prefix &= positionHead
                prefixLines += 2
            End If
            Dim mainNormal = ExtensionShaderMain.Replace("FP_EFFECT_CALL(b.rgb / b.a)",
                If(definition.UsesPosition, "effect(b.rgb / b.a, fpPosOf(p))", "effect(b.rgb / b.a)"))
            Dim mainDeep = ExtensionShaderMainDeep.Replace("FP_EFFECT_CALL(s.rgb / s.a)",
                If(definition.UsesPosition, "effect(s.rgb / s.a, fpPosOf(p))", "effect(s.rgb / s.a)"))
            Dim errors As String = ""
            Dim effect = CompiledExtensionShader(prefix & body & mainNormal, errors)
            If effect Is Nothing Then Return ExtensionShaderErrors(errors, prefixLines)
            Dim deepEffect = CompiledExtensionShader(prefix & body & mainDeep, errors)
            If deepEffect Is Nothing Then Return ExtensionShaderErrors(errors, prefixLines)
            definition.Effect = effect
            definition.DeepEffect = deepEffect
            Return ""
        End Function

        ''' <summary>Erkennt die Rechnung mit Lage: float3 effect(float3 name, float2 name).</summary>
        Private Shared ReadOnly PositionSignature As New Regex("float3\s+effect\s*\(\s*float3\s+\w+\s*,\s*float2\s+\w+\s*\)",
                                                               RegexOptions.CultureInvariant)

        ' ── Die uebersetzten Shader ───────────────────────────────────────────────
        '
        ' JEDER SHADERTEXT WIRD EINMAL UEBERSETZT und danach wiederverwendet. Ein SKRuntimeEffect
        ' haelt nativen Speicher. Vorher entstanden bei jedem Laden zwei neue, auch beim Pruefen
        ' einer Datei vor dem Installieren und beim Neu einlesen, und die alten blieben liegen:
        ' wiederholtes "Neu einlesen" liess den Speicher wachsen.
        '
        ' FREIGEGEBEN WIRD NICHTS. Ein Effekt kann noch in einer laufenden Vorschau oder auf der
        ' Karte stecken, waehrend der Bestand neu eingelesen wird; ihn dann freizugeben, liesse
        ' Skia auf freigegebenem Speicher rechnen. Wachsen kann der Bestand nur mit jedem NEUEN
        ' Shadertext, also mit jeder Aenderung an einer Erweiterung: bei einem Autor, der seine
        ' Datei immer wieder aendert, ein paar Kilobyte je Fassung, und nach einem Neustart weg.

        Private Shared ReadOnly _extensionShaders As New Dictionary(Of String, SKRuntimeEffect)(StringComparer.Ordinal)
        Private Shared ReadOnly _extensionShaderErrors As New Dictionary(Of String, String)(StringComparer.Ordinal)

        ''' <summary>Der uebersetzte Shader zu diesem Text, beim ersten Mal uebersetzt. Nothing mit
        ''' der Meldung des Uebersetzers, wenn er nicht uebersetzt; auch die bleibt gemerkt, damit
        ''' eine kaputte Datei nicht bei jedem Neu einlesen von vorn uebersetzt wird.</summary>
        Private Shared Function CompiledExtensionShader(source As String, ByRef errors As String) As SKRuntimeEffect
            SyncLock _extensionShaders
                Dim effect As SKRuntimeEffect = Nothing
                If _extensionShaders.TryGetValue(source, effect) Then Return effect
                Dim known As String = Nothing
                If _extensionShaderErrors.TryGetValue(source, known) Then
                    errors = known
                    Return Nothing
                End If
                Dim message As String = Nothing
                effect = SKRuntimeEffect.CreateShader(source, message)
                If effect Is Nothing Then
                    _extensionShaderErrors(source) = If(message, "")
                    errors = If(message, "")
                    Return Nothing
                End If
                _extensionShaders(source) = effect
                Return effect
            End SyncLock
        End Function

        ''' <summary>Wie viele Shader der Erweiterungen uebersetzt vorliegen. Fuer die Pruefung, dass
        ''' Neu einlesen keine neuen anlegt.</summary>
        Friend Shared ReadOnly Property ExtensionShaderCount As Integer
            Get
                SyncLock _extensionShaders
                    Return _extensionShaders.Count
                End SyncLock
            End Get
        End Property

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
                text &= vbLf & "The shader must define: float3 effect(float3 color), or float3 effect(float3 color, float2 pos) for the position in the picture"
            End If
            Return text
        End Function

        ''' <summary>Was ein Lauf einer Erweiterung bekommt: Zahlen, Farben und die Tabellen der
        ''' Kurven.</summary>
        ''' <remarks>Die Tabellen liegen hier als verwalteter Speicher, nicht als SKImage. Die
        ''' Eingaben leben so lange wie die Kette sie braucht, und das weiss niemand genau: laeuft die
        ''' Stufe auf der Karte, kann ihr Gegenstueck auf dem Prozessor spaeter noch nachgerechnet
        ''' werden (BringDetailSessionHome), oder nie. Ein natives Bild hier blieb deshalb bis zur
        ''' Finalisierung liegen, je Vorschau eines, beim Reglerzug in Mengen. Jeder Lauf baut sich
        ''' seine Bilder aus der Tabelle (CurveImages) und gibt sie nach dem Zeichnen frei.</remarks>
        Friend NotInheritable Class ExtensionInputs
            Public ReadOnly Floats As New List(Of (Key As String, Value As Single))()
            Public ReadOnly Colors As New List(Of (Key As String, Value As Single()))()
            Public ReadOnly Curves As New List(Of (ChildName As String, Table As Byte()))()

            ''' <summary>Verlangt die Rechnung die Lage im Bild? Dann braucht sie die Groesse des
            ''' Bildes, das gerade gerechnet wird.</summary>
            Public UsesPosition As Boolean

            Public Sub SetUniforms(uniforms As SKRuntimeEffectUniforms, width As Integer, height As Integer)
                If UsesPosition Then uniforms("fpSize") = New Single() {CSng(width), CSng(height)}
                For Each value In Floats
                    uniforms(value.Key) = value.Value
                Next
                For Each value In Colors
                    uniforms(value.Key) = value.Value
                Next
            End Sub

            ''' <summary>Je Kurve ein frisches Bild ihrer Tabelle. Der Aufrufer gibt sie frei
            ''' (DisposeCurveImages), sobald gezeichnet ist.</summary>
            Public Function CurveImages() As List(Of (Name As String, Image As SKImage))
                Dim images As New List(Of (Name As String, Image As SKImage))(Curves.Count)
                For Each curve In Curves
                    images.Add((curve.ChildName, ExtensionCurveImageFromTable(curve.Table)))
                Next
                Return images
            End Function
        End Class

        Private Shared Sub DisposeCurveImages(images As List(Of (Name As String, Image As SKImage)))
            If images Is Nothing Then Return
            For Each item In images
                item.Image?.Dispose()
            Next
            images.Clear()
        End Sub

        ''' <summary>Die Werte fuer einen Lauf: was das Rezept nennt, in den Grenzen der
        ''' Erweiterung, und fuer alles, was es nicht nennt, die Vorgabe. Ein Wert unter einem
        ''' Schluessel, den die Erweiterung nicht (mehr) kennt, zaehlt nicht.</summary>
        Friend Shared Function ExtensionInputsFor(definition As ExtensionDefinition, entry As ExtensionEntry) As ExtensionInputs
            Dim inputs As New ExtensionInputs With {.UsesPosition = definition.UsesPosition}
            For Each control In definition.Controls
                If control.IsColor Then
                    Dim text As String = Nothing
                    If entry Is Nothing OrElse Not entry.Colors.TryGetValue(control.Key, text) Then text = control.DefaultColor
                    Dim color = SKColor.Parse(text)
                    inputs.Colors.Add((control.Key, New Single() {color.Red / 255.0F, color.Green / 255.0F, color.Blue / 255.0F}))
                ElseIf control.IsCurve Then
                    Dim points As List(Of (X As Double, Y As Double)) = Nothing
                    If entry Is Nothing OrElse Not entry.Curves.TryGetValue(control.Key, points) Then points = control.DefaultCurve
                    inputs.Curves.Add(("fpCurveTable_" & control.Key, ExtensionCurveTable(points)))
                Else
                    Dim value As Double
                    If entry Is Nothing OrElse Not entry.Values.TryGetValue(control.Key, value) Then value = control.DefaultValue
                    inputs.Floats.Add((control.Key, CSng(control.Clamp(value))))
                End If
            Next
            Return inputs
        End Function

        ''' <summary>Die Tabelle einer Kurve: 256 Werte der Kurve an x = i / 255, je Wert 16 Bit,
        ''' das hohe Byte in Rot, das niedrige in Gruen, deckend. Dieselbe Kurvenform wie die
        ''' Tonwertkurve (EvaluateCurveSpline: steigend bleibt steigend, ohne Ueberschwingen).</summary>


        ''' <summary>Die Tabelle einer Kurve als Bildpunkte von 256 mal 1, je Wert 16 Bit auf Rot
        ''' und Gruen verteilt, im verwalteten Speicher.</summary>
        Private Shared Function ExtensionCurveTable(points As List(Of (X As Double, Y As Double))) As Byte()
            Dim bytes(256 * 4 - 1) As Byte
            For i = 0 To 255
                Dim y = Math.Max(0.0, Math.Min(1.0, EvaluateCurveSpline(points, i / 255.0)))
                Dim v = CInt(Math.Round(y * 65535.0))
                bytes(i * 4) = CByte(v >> 8)
                bytes(i * 4 + 1) = CByte(v And &HFF)
                bytes(i * 4 + 2) = 0
                bytes(i * 4 + 3) = 255
            Next
            Return bytes
        End Function

        Private Shared Function ExtensionCurveImageFromTable(bytes As Byte()) As SKImage
            Dim info = New SKImageInfo(256, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul)
            ' Eine Kopie der Daten gehoert danach dem Bild (Skia zaehlt die Verweise).
            Return SKImage.FromPixels(info, SKData.CreateCopy(bytes), info.RowBytes)
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
                ' Die Eingaben leben so lange wie die Kette sie braucht: laeuft die Stufe auf der
                ' Karte, kann ihr Gegenstueck auf dem Prozessor noch spaeter nachgerechnet werden
                ' (BringDetailSessionHome). Sie halten deshalb nichts Natives; die Kurventabellen
                ' sind je ein Kilobyte verwalteter Speicher.
                Dim inputs = ExtensionInputsFor(definition, entry)
                processed = RunDetailStage(session, processed, owned, "Pixel: Erweiterung " & definition.Id,
                                           ExtensionOnGpu(definition, inputs),
                                           Function(s) If(RunExtensionOnBitmap(s, definition.Effect, inputs), CopyBitmap(s)),
                                           Function(d) RunExtensionOnBitmap(d, definition.DeepEffect, inputs))
            Next
            Return processed
        End Function

        Private Shared Function ExtensionOnGpu(definition As ExtensionDefinition,
                                               inputs As ExtensionInputs) As Func(Of GRContext, SKImage, SKSurface)
            Return Function(gr, image)
                       Dim u = New SKRuntimeEffectUniforms(definition.Effect)
                       inputs.SetUniforms(u, image.Width, image.Height)
                       Dim tables = inputs.CurveImages()
                       Try
                           Dim children = New List(Of (Name As String, Image As SKImage)) From {("src", image)}
                           children.AddRange(tables)
                           Return DrawStage(gr, image.Width, image.Height, definition.Effect, u, children.ToArray())
                       Finally
                           DisposeCurveImages(tables)
                       End Try
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
                                                    inputs As ExtensionInputs) As SKBitmap
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
            Dim curveImages = inputs.CurveImages()
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
                                inputs.SetUniforms(uniforms, width, height)
                                Dim children = New SKRuntimeEffectChildren(effect)
                                Dim tables As New List(Of SKShader)()
                                For Each curve In curveImages
                                    Dim table = curve.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                                                                     New SKSamplingOptions(SKFilterMode.Nearest))
                                    tables.Add(table)
                                    children(curve.Name) = table
                                Next
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
                                For Each table In tables
                                    table.Dispose()
                                Next
                            End Using
                        End Sub)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Extensions.Run", ex)
                failed = True
            Finally
                DisposeCurveImages(curveImages)
            End Try
            If failed Then
                result.Dispose()
                Return Nothing
            End If
            Return result
        End Function

    End Class

End Namespace
