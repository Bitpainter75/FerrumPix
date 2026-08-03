Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ''' <summary>Ein Bild mit einem gelernten Modell vergroessern.
    '''
    ''' Der Unterschied zur gewoehnlichen Vergroesserung ist derselbe wie beim Entrauschen: eine
    ''' Interpolation kann nur mitteln, was schon da ist, und je weiter man sie treibt, desto
    ''' weicher wird das Ergebnis. Das Modell hat gelernt, wie eine Kante, ein Haar oder eine
    ''' Ziegelfuge in gross AUSSIEHT, und setzt sie neu - es erfindet also Bildpunkte, statt sie
    ''' zu verschmieren.
    '''
    ''' Genau deshalb ist es KEINE Reglerfunktion und laeuft nicht in der Vorschaukette mit: es
    ''' kostet Sekunden bis Minuten und wird wie das Entrauschen in die Bildpunkte gerechnet.
    '''
    ''' MEHRERE MODELLE, EIN WEG. Alle Kandidaten teilen denselben Vertrag - [1,3,h,w] hinein,
    ''' [1,3,s*h,s*w] heraus, freie Bildgroesse. Der Massstab wird deshalb nicht eingetragen,
    ''' sondern beim ersten Lauf GEMESSEN. Ein weiteres Modell derselben Bauform ist damit ein
    ''' Registereintrag und keine Codeaenderung.</summary>
    Public NotInheritable Class UpscaleModelService

        Private Sub New()
        End Sub

        ''' <summary>Ein Modell, wie es in der Auswahl steht. Der Schluessel ist der des
        ''' Modellregisters; was fehlt, erscheint dort nicht.</summary>
        Public NotInheritable Class UpscaleModel
            Public Property Key As String = ""
            ''' <summary>Der deutsche Quelltext fuer die Anzeige - uebersetzt wird erst beim Lesen,
            ''' sonst steht die Liste nach einem Sprachwechsel in der alten Sprache da.</summary>
            Public Property Label As String = ""
            Public Property Hint As String = ""
            ''' <summary>Der erwartete Massstab. Nur fuer die Anzeige und die Vorauswahl - was das
            ''' Modell wirklich tut, wird beim Rechnen gemessen.</summary>
            Public Property Scale As Integer = 4
        End Class

        ''' <summary>Die bekannten Modelle, in der Reihenfolge, in der sie in der Auswahl stehen.
        '''
        ''' Die Rollen sind gemessen und nicht geraten (Zahlen im Audit): das kleine Modell ist auf
        ''' dem PROZESSOR sechzehnmal schneller als das grosse und auf der Karte dreimal langsamer -
        ''' bei so wenig Rechnung je Bildpunkt kostet die Fahrt zur Karte mehr als die Rechnung.
        ''' Wer keine Karte hat, nimmt also das kleine; wer eine hat, das grosse.</summary>
        Public Shared ReadOnly Property KnownModels As IReadOnlyList(Of UpscaleModel) =
            New List(Of UpscaleModel) From {
                New UpscaleModel With {.Key = "realesrgan-x4", .Scale = 4,
                                       .Label = "Vierfach, gründlich",
                                       .Hint = "Das genaueste für Fotos. Auf einer Grafikkarte gut eine Minute je Foto, auf dem Prozessor mehrere."},
                New UpscaleModel With {.Key = "realesrgan-x2", .Scale = 2,
                                       .Label = "Zweifach, gründlich",
                                       .Hint = "Dasselbe für die doppelte Größe. Deutlich schneller als vierfach, weil viermal weniger Fläche entsteht."},
                New UpscaleModel With {.Key = "realesrgan-fast-x4", .Scale = 4,
                                       .Label = "Vierfach, zügig",
                                       .Hint = "Ein sehr kleines Modell: auf dem Prozessor sechzehnmal schneller als der gründliche Weg, dafür etwas weniger Zeichnung. Es nimmt dabei auch Rauschen mit heraus. Ohne Grafikkarte die richtige Wahl."},
                New UpscaleModel With {.Key = "realesrgan-fast-wdn-x4", .Scale = 4,
                                       .Label = "Vierfach, zügig, Korn bleibt",
                                       .Hint = "Dasselbe kleine Modell in der Fassung, die das Korn STEHEN lässt. Für Aufnahmen, deren Körnung zum Bild gehört und nicht mit weggerechnet werden soll."},
                New UpscaleModel With {.Key = "nomos8ksc-x4", .Scale = 4,
                                       .Label = "Vierfach, für Aufnahmen aus dem Netz",
                                       .Hint = "Auf Fotos trainiert, die schon einmal durch eine JPEG-Kompression gegangen sind - es rechnet deren Artefakte und leichte Unschärfe mit heraus. Für Bilder aus dem Netz oder aus alten Sicherungen."},
                New UpscaleModel With {.Key = "lsdirplusn-x4", .Scale = 4,
                                       .Label = "Vierfach, zurückhaltend",
                                       .Hint = "Auf über 84000 unbeschädigten Fotos trainiert, ohne künstlich verschlechterte Vorlagen. Es erfindet weniger dazu als die übrigen - für Aufnahmen, die schon gut sind."},
                New UpscaleModel With {.Key = "realesrgan-anime-x4", .Scale = 4,
                                       .Label = "Vierfach, für Zeichnungen",
                                       .Hint = "Für Gezeichnetes statt Fotografiertes: hält Flächen glatt und Linien scharf. An einem Foto wirkt es wachsartig."},
                New UpscaleModel With {.Key = "hfa2k-x4", .Scale = 4,
                                       .Label = "Vierfach, für Zeichnungen, zweite Wahl",
                                       .Hint = "Derselbe Zweck, aber auf Einzelbildern moderner Animationsfilme trainiert statt auf Zeichnungen allgemein. Welches der beiden besser trifft, hängt an der Vorlage."}}

        ''' <summary>Kantenlaenge einer Kachel, je Massstab.
        '''
        ''' Die AUSGABE ist das Nadeloehr, nicht die Eingabe: bei vierfacher Vergroesserung wird aus
        ''' einer 256er Kachel eine von 1024, und genau dort geht auf einer Karte mit acht Gigabyte
        ''' der Speicher aus, wenn nebenher noch ein Browser laeuft. Deshalb haengt die Kachel am
        ''' Massstab und ist nicht fest verdrahtet.</summary>
        Private Shared Function TileEdgeFor(scale As Integer) As Integer
            Return If(scale >= 4, 192, 256)
        End Function

        ''' <summary>Wie weit sich zwei Kacheln ueberlappen - in der EINGABE gerechnet.
        '''
        ''' Dieselbe Begruendung wie beim Entrauschen: am Kachelrand hat das Modell auf einer Seite
        ''' keinen Zusammenhang, und was es dort erfindet, weicht von der Nachbarkachel ab. Ohne
        ''' Ueberlappung stuende ein Gitter im Bild - beim Hochskalieren staerker als beim
        ''' Entrauschen, weil hier wirklich neue Bildpunkte entstehen.</summary>
        Private Const TileOverlap As Integer = 16

        ''' <summary>Was der letzte Durchlauf gerechnet hat. Wird danach angezeigt.</summary>
        Public Shared Property LastReport As String = ""

        ''' <summary>Wird nach jeder Kachel gerufen, mit erledigten und gesamten Kacheln.</summary>
        Public Shared Property Progress As Action(Of Integer, Integer)

        ''' <summary>Die Modelle, deren Datei wirklich vorliegt. Was fehlt, steht nicht in der
        ''' Auswahl - ein Eintrag, der ins Leere fuehrt, ist schlechter als keiner.</summary>
        Public Shared ReadOnly Property AvailableModels As IReadOnlyList(Of UpscaleModel)
            Get
                If Not AiModelService.RuntimeAvailable Then Return New List(Of UpscaleModel)()
                Return KnownModels.Where(
                    Function(m) Not String.IsNullOrEmpty(AiModelService.BestFile(m.Key))).ToList()
            End Get
        End Property

        Public Shared ReadOnly Property Available As Boolean
            Get
                Return AvailableModels.Count > 0
            End Get
        End Property

        ''' <summary>Der Eintrag zu einem Schluessel, oder der erste vorhandene.</summary>
        Public Shared Function ModelFor(key As String) As UpscaleModel
            Dim treffer = KnownModels.FirstOrDefault(
                Function(m) String.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
            If treffer IsNot Nothing AndAlso Not String.IsNullOrEmpty(AiModelService.BestFile(treffer.Key)) Then
                Return treffer
            End If
            Return AvailableModels.FirstOrDefault()
        End Function

        ''' <summary>Das Bild vergroessern. Zurueck kommt eine KOPIE, oder Nothing bei jedem
        ''' Fehlschlag - der Aufrufer behaelt dann sein Original.
        '''
        ''' Der Massstab kommt aus dem MODELL und nicht aus einem Wunsch: ein Vierfach-Modell macht
        ''' vierfach. Wer eine andere Zielgroesse will, laesst danach gewoehnlich skalieren - vom
        ''' Grossen herunter ist ein Mitteln und verliert nichts, im Gegensatz zum Hinaufrechnen.</summary>
        Public Shared Function Upscale(image As SKBitmap, key As String) As SKBitmap
            If image Is Nothing OrElse image.Width <= 0 OrElse image.Height <= 0 Then Return Nothing
            If image.ColorType <> SKColorType.Bgra8888 Then Return Nothing
            Dim model = ModelFor(key)
            If model Is Nothing Then Return Nothing
            Dim session = AiModelService.SessionFor(model.Key)
            If session Is Nothing Then Return Nothing

            Dim result As SKBitmap = Nothing
            Try
                Dim name = session.InputMetadata.Keys.First()

                ' Den Massstab MESSEN statt glauben: eine Kachel durch das Modell, und das
                ' Verhaeltnis der Kanten ist die Antwort. So traegt derselbe Weg jedes weitere
                ' Modell der Familie, und ein falscher Eintrag im Register faellt hier auf, statt
                ' ein verzerrtes Bild zu erzeugen.
                Dim scale = MeasureScale(session, name)
                If scale < 2 OrElse scale > 8 Then
                    DiagnosticLogService.LogAlways("Hochskalieren", $"unerwarteter Massstab {scale}")
                    Return Nothing
                End If

                Dim tileEdge = TileEdgeFor(scale)
                Dim stride = Math.Max(1, tileEdge - 2 * TileOverlap)
                Dim tilesX = Math.Max(1, CInt(Math.Ceiling(image.Width / CDbl(stride))))
                Dim tilesY = Math.Max(1, CInt(Math.Ceiling(image.Height / CDbl(stride))))
                Dim tiles = tilesX * tilesY

                result = New SKBitmap(image.Width * scale, image.Height * scale,
                                      image.ColorType, image.AlphaType)
                Dim clock = Diagnostics.Stopwatch.StartNew()
                Dim done = 0

                Dim y = 0
                While y < image.Height
                    Dim x = 0
                    While x < image.Width
                        ' Der Ausschnitt darf ueber den Bildrand hinausragen; er wird dann nach
                        ' innen geschoben. Ein beschnittener Randstreifen waere kleiner als die
                        ' Kachel, und das ist bei diesen Modellen zwar erlaubt, kostet aber eine
                        ' zweite Aufwaermrunde auf der Karte.
                        Dim l = Math.Max(0, Math.Min(x - TileOverlap, image.Width - tileEdge))
                        Dim t = Math.Max(0, Math.Min(y - TileOverlap, image.Height - tileEdge))
                        Dim r = Math.Min(image.Width, l + tileEdge)
                        Dim b = Math.Min(image.Height, t + tileEdge)
                        Dim keepL = x, keepT = y
                        Dim keepR = Math.Min(image.Width, x + stride)
                        Dim keepB = Math.Min(image.Height, y + stride)
                        If r > l AndAlso b > t AndAlso keepR > keepL AndAlso keepB > keepT Then
                            UpscaleTile(session, name, image, result, scale,
                                        New SKRectI(l, t, r, b), New SKRectI(keepL, keepT, keepR, keepB))
                            done += 1
                            Progress?.Invoke(done, tiles)
                        End If
                        x += stride
                    End While
                    y += stride
                End While

                clock.Stop()
                LastReport = $"Bild {image.Width}x{image.Height} auf " &
                             $"{result.Width}x{result.Height}, {done} Kacheln, " &
                             $"{clock.Elapsed.TotalSeconds:F0} s, {model.Label}"
                DiagnosticLogService.LogAlways("Hochskalieren", LastReport)
                Dim fertig = result
                result = Nothing
                Return fertig
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Hochskalieren", ex.Message)
                result?.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Den Massstab des Modells an einer kleinen Kachel messen.</summary>
        Private Shared Function MeasureScale(session As InferenceSession, name As String) As Integer
            Const probe As Integer = 32
            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, probe, probe})
            Using run = session.Run(New List(Of NamedOnnxValue) From {
                    NamedOnnxValue.CreateFromTensor(name, tensor)})
                Dim output = TryCast(run.First().Value, DenseTensor(Of Single))
                If output Is Nothing Then Return 0
                Dim dims = output.Dimensions.ToArray()
                If dims.Length < 2 Then Return 0
                Return dims(dims.Length - 1) \ probe
            End Using
        End Function

        ''' <summary>Eine Kachel durch das Modell und nur ihren INNEREN Teil zurueckschreiben -
        ''' derselbe Aufbau wie beim Entrauschen, nur dass Ziel- und Quellraum sich um den
        ''' Massstab unterscheiden.</summary>
        Private Shared Sub UpscaleTile(session As InferenceSession, name As String,
                                       source As SKBitmap, target As SKBitmap, scale As Integer,
                                       window As SKRectI, keep As SKRectI)
            Dim w = window.Width, h = window.Height
            Dim layer = w * h
            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, h, w})
            Dim z = tensor.Buffer.Span

            Dim srcStride = source.RowBytes
            Dim row(srcStride - 1) As Byte
            Dim basis = source.GetPixels()
            For yy = 0 To h - 1
                Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(basis, (window.Top + yy) * srcStride), row, 0, srcStride)
                Dim line = yy * w
                For xx = 0 To w - 1
                    Dim p = (window.Left + xx) * 4
                    Dim i = line + xx
                    ' BGRA im Speicher, das Modell erwartet RGB in 0 bis 1.
                    z(i) = row(p + 2) / 255.0F
                    z(layer + i) = row(p + 1) / 255.0F
                    z(layer * 2 + i) = row(p) / 255.0F
                Next
            Next

            Using run = session.Run(New List(Of NamedOnnxValue) From {
                    NamedOnnxValue.CreateFromTensor(name, tensor)})
                Dim output = TryCast(run.First().Value, DenseTensor(Of Single))
                If output Is Nothing Then Return
                Dim dims = output.Dimensions.ToArray()
                Dim rh = dims(dims.Length - 2), rw = dims(dims.Length - 1)
                Dim rLayer = rw * rh
                Dim werte = output.Buffer.Span
                If werte.Length < rLayer * 3 Then Return

                Dim dstStride = target.RowBytes
                Dim ziel = target.GetPixels()
                Dim zeile(dstStride - 1) As Byte
                For yy = keep.Top * scale To keep.Bottom * scale - 1
                    If yy < 0 OrElse yy >= target.Height Then Continue For
                    Runtime.InteropServices.Marshal.Copy(
                        IntPtr.Add(ziel, yy * dstStride), zeile, 0, dstStride)
                    Dim sy = Math.Min(Math.Max(0, yy - window.Top * scale), rh - 1)
                    For xx = keep.Left * scale To keep.Right * scale - 1
                        If xx < 0 OrElse xx >= target.Width Then Continue For
                        Dim sx = Math.Min(Math.Max(0, xx - window.Left * scale), rw - 1)
                        Dim i = sy * rw + sx
                        Dim p = xx * 4
                        zeile(p) = ToByte(werte(rLayer * 2 + i) * 255.0F)
                        zeile(p + 1) = ToByte(werte(rLayer + i) * 255.0F)
                        zeile(p + 2) = ToByte(werte(i) * 255.0F)
                        zeile(p + 3) = 255
                    Next
                    Runtime.InteropServices.Marshal.Copy(zeile, 0, IntPtr.Add(ziel, yy * dstStride), dstStride)
                Next
            End Using
        End Sub

        ''' <summary>Einen gerechneten Kanalwert in eine Stufe zurueckholen. Beschnitten wird in
        ''' Fliesskomma und ERST DANN gerundet: ein Modellwert weit jenseits des Wertebereichs
        ''' wuerde beim Umwandeln in eine Ganzzahl sonst eine Ausnahme werfen.</summary>
        Private Shared Function ToByte(value As Single) As Byte
            If Single.IsNaN(value) Then Return 0
            Return CByte(Math.Max(0.0F, Math.Min(255.0F, value)))
        End Function

    End Class

End Namespace
