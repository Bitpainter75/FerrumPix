Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ''' <summary>Rauschen mit einem gelernten Modell entfernen.
    '''
    ''' Der Unterschied zu den vorhandenen Reglern ist die Art der Entscheidung: ein Weichzeichner
    ''' muss zwischen Rauschen und feiner Struktur abwaegen und verliert dabei immer etwas, weil er
    ''' beides nur an der Groesse unterscheiden kann. Das Modell hat gelernt, wie Korn aussieht und
    ''' wie ein Haar - es nimmt das eine weg und laesst das andere stehen.
    '''
    ''' ACHTUNG, das ist der Grund fuer jede Vorsicht hier: Entrauschen laeuft ueber JEDEN Bildpunkt,
    ''' nicht ueber einen Fleck wie das Objektentfernen. Gemessen kostet das rund siebzehn Sekunden
    ''' je Megapixel auf der CPU, ein Foto mit 12 Megapixeln also gut dreieinhalb Minuten. Das ist
    ''' keine Groesse, die sich durch besseren Code aendert - sie steckt im Modell.</summary>
    Public NotInheritable Class DenoiseModelService

        Private Sub New()
        End Sub

        ''' <summary>Welches der beiden Modelle rechnet.
        '''
        ''' Die beiden unterscheiden sich nicht in der Aufgabe, sondern im Handel zwischen Zeit und
        ''' Zeichnung, und der ist gemessen (Zahlen in FALLEN_UND_ENTSCHEIDUNGEN.md):
        '''
        ''' QUALITAET nimmt an einer nachtlichen Fassade 79 Prozent des Rauschens und laesst dabei
        ''' 75 Prozent der mittleren Zeichnung und 92 Prozent der starken Kanten stehen. Es kostet
        ''' rund siebzehn Sekunden je Megapixel, ein Zwoelf-Megapixel-Foto also gut dreieinhalb
        ''' Minuten.
        '''
        ''' SCHNELL nimmt genauso viel Rauschen, laesst aber nur 56 beziehungsweise 81 Prozent
        ''' stehen - sichtbar glatter. Dafuer rechnet es rund sechsmal schneller, dasselbe Foto also
        ''' in gut einer halben Minute.
        '''
        ''' Keines ist allgemein besser. Wer die Wartezeit hat, nimmt das erste.
        '''
        ''' ZWEI weitere Wege waren eingebaut und sind wieder heraus, beide nach einem Test am
        ''' echten Foto: DRUNet (Rauschstaerke als vierter Eingangskanal, schlechter als der
        ''' schnelle Weg bei dreifacher Rechenzeit) und ein auf das Wiederherstellen von Fotos
        ''' trainiertes Modell (das teuerste der drei, ohne dass sich das im Bild gezeigt haette).
        ''' Die Zahlen und die Begruendung stehen in FALLEN_UND_ENTSCHEIDUNGEN.md - nicht erneut
        ''' vorschlagen.</summary>
        Public Enum DenoiseKind
            Quality = 0
            Fast = 1
        End Enum

        ''' <summary>Das Modell fuer Qualitaet. Heisst ModelFile, weil es das voreingestellte ist -
        ''' die Diagnose und die Verfuegbarkeitspruefung fragen danach.</summary>
        Public Const ModelFile As String = "scunet"

        ''' <summary>Und das schnelle.</summary>
        Public Const FastModelFile As String = "nafnet"

        Private Shared Function KeyFor(kind As DenoiseKind) As String
            Select Case kind
                Case DenoiseKind.Fast : Return FastModelFile
                Case Else : Return ModelFile
            End Select
        End Function

        ''' <summary>Der Name des Weges fuer den Bericht. NICHT "NameOf" nennen - das ist in VB ein
        ''' Schluesselwort, und der Name waere still ueberdeckt.</summary>
        Private Shared Function KindName(kind As DenoiseKind) As String
            Select Case kind
                Case DenoiseKind.Fast : Return "schnell"
                Case Else : Return "Qualität"
            End Select
        End Function

        ''' <summary>Kantenlaenge einer Kachel.
        '''
        ''' Das Modell nimmt freie Groessen, also waere auch das ganze Bild moeglich - aber der
        ''' Speicher waechst mit der Flaeche, und ein 40-Megapixel-Bild als ein Tensor sind mehrere
        ''' Gigabyte. Die Rechenzeit haengt ohnehin nur an der Gesamtflaeche und nicht an der
        ''' Kachelgroesse; gemessen sind 256er und 512er Kacheln gleich schnell.</summary>
        Private Const TileEdge As Integer = 512

        ''' <summary>Wie weit sich zwei Kacheln ueberlappen.
        '''
        ''' Am Kachelrand hat das Modell auf einer Seite keinen Zusammenhang, und was es dort
        ''' entscheidet, weicht von der Nachbarkachel ab. Ohne Ueberlappung stuende ein Gitter im
        ''' Bild. Uebernommen wird nur der innere Teil, der Rand dient als Anlauf.
        '''
        ''' Die Ueberlappung ist reine Mehrarbeit: sie wird gerechnet und dann verworfen. Bei 32
        ''' Punkten waren das rund dreissig Prozent, bei 16 sind es noch knapp vierzehn. Gemessen an
        ''' einem echten RAW: der Sprung an der Kachelgrenze steigt dabei von 0,47 auf 0,61 Stufen
        ''' und bleibt damit unter dem, was im Bild ohnehin von Spalte zu Spalte passiert (0,38) -
        ''' auch bei achtfach ueberhoehtem Kontrast steht dort keine Linie. Bei 8 Punkten wird es
        ''' messbar schlechter, deshalb ist hier die Grenze und nicht tiefer.</summary>
        Private Const TileOverlap As Integer = 16

        ''' <summary>Was der letzte Durchlauf gerechnet hat. Wird nach dem Entrauschen angezeigt.</summary>
        Public Shared Property LastReport As String = ""

        ''' <summary>Wird nach jeder Kachel gerufen, mit erledigten und gesamten Kacheln. Bei
        ''' Minuten an Rechenzeit ist eine Rueckmeldung keine Hoeflichkeit, sondern der Unterschied
        ''' zwischen "es arbeitet" und "es haengt".</summary>
        Public Shared Property Progress As Action(Of Integer, Integer)

        Public Shared ReadOnly Property Available As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(ModelFile))
            End Get
        End Property

        ''' <summary>Steht der schnelle Weg zur Verfuegung? Eigene Frage, weil seine Modelldatei
        ''' getrennt geholt wird - wer nur die eine hat, soll den einen Weg trotzdem benutzen
        ''' koennen statt vor einem toten Knopf zu stehen.</summary>
        Public Shared ReadOnly Property FastAvailable As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(FastModelFile))
            End Get
        End Property


        ''' <summary>Das Bild entrauschen. Zurueck kommt eine KOPIE, oder Nothing bei jedem
        ''' Fehlschlag.
        '''
        ''' <paramref name="strength"/> geht von 0 bis 1 und gilt NUR fuer die HELLIGKEIT; die Farbe
        ''' kommt immer voll aus dem Modell. Das ist keine Vereinfachung, sondern gemessen:
        '''
        ''' Bei der Helligkeit ist die Staerke ein echter Handel. An einer naechtlichen Fassade nimmt
        ''' das Modell bei voller Staerke 79 Prozent des Rauschens, laesst von der SCHWACHEN Zeichnung
        ''' aber nur 49 Prozent stehen - Fensterrahmen werden waechsern. Bei 60 Prozent sind es 49
        ''' Prozent Rauschen gegen 60 Prozent Zeichnung. Welche Seite man will, haengt am Bild: an
        ''' einem Innenraum war die volle Staerke die bessere Wahl, an der Fassade nicht.
        '''
        ''' Bei der Farbe gibt es diesen Handel nicht. Farbrauschen sind grossflaechige Flecken ohne
        ''' Entsprechung im Motiv; gemessen verschwinden 89 Prozent davon, ohne dass die Helligkeit
        ''' angefasst wird. Es gibt also nichts zu sparen, und ein Regler dafuer waere ein Regler,
        ''' den man immer aufdreht.
        '''
        ''' Deshalb ist Staerke 0 auch KEIN Nichtstun: die Farbe wird dann trotzdem entrauscht und
        ''' die Helligkeit unangetastet gelassen. Das ist ein sinnvoller Fall und faellt nicht
        ''' durch.</summary>
        ''' <param name="cancel">Abbruch durch den Nutzer. Gewartet wird bis zur naechsten
        ''' KACHELGRENZE - ein laufender Modelldurchlauf laesst sich nicht mittendrin anhalten, und
        ''' eine Kachel dauert Sekunden, nicht Minuten. Wird abgebrochen, kommt NOTHING zurueck: der
        ''' Aufrufer soll ein halb entrauschtes Bild gar nicht erst in die Finger bekommen.</param>
        Public Shared Function Denoise(image As SKBitmap,
                                       Optional kind As DenoiseKind = DenoiseKind.Quality,
                                       Optional strength As Single = 1.0F,
                                       Optional cancel As Threading.CancellationToken = Nothing) As SKBitmap
            If image Is Nothing OrElse image.Width <= 0 OrElse image.Height <= 0 Then Return Nothing
            Dim session = AiModelService.SessionFor(KeyFor(kind))
            If session Is Nothing Then Return Nothing
            If image.ColorType <> SKColorType.Bgra8888 Then Return Nothing

            Dim amount = Math.Max(0.0F, Math.Min(1.0F, strength))

            Dim result As SKBitmap = Nothing
            Dim padded As SKBitmap = Nothing
            Try
                ' Ist das Bild in einer Achse kuerzer als eine Kachel, gibt es keinen Ausschnitt, der
                ' sich nach innen schieben liesse: JEDE Kachel faellt aus, und zurueck kaeme eine
                ' unveraenderte Kopie samt Erfolgsmeldung. Das trifft nicht nur Briefmarken, sondern
                ' auch flache Zuschnitte wie 3000 mal 400. Deshalb wird vorher auf Kachelgroesse
                ' GESPIEGELT padded - gespiegelt und nicht mit einer Farbe gefuellt, weil eine
                ' harte Kunstkante am Bildrand das Modell dort etwas erfinden liesse, was es dann in
                ' die letzten Bildpunkte hineinrechnet.
                Dim tiledSource = image
                If image.Width < TileEdge OrElse image.Height < TileEdge Then
                    padded = New SKBitmap(Math.Max(image.Width, TileEdge),
                                              Math.Max(image.Height, TileEdge),
                                              image.ColorType, image.AlphaType)
                    Using canvas = New SKCanvas(padded)
                        Using shader = SKShader.CreateBitmap(image, SKShaderTileMode.Mirror, SKShaderTileMode.Mirror)
                            Using paint = New SKPaint With {.Shader = shader, .BlendMode = SKBlendMode.Src}
                                canvas.DrawRect(New SKRect(0, 0, padded.Width, padded.Height), paint)
                            End Using
                        End Using
                    End Using
                    tiledSource = padded
                End If

                result = New SKBitmap(tiledSource.Width, tiledSource.Height, tiledSource.ColorType, tiledSource.AlphaType)
                Using canvas = New SKCanvas(result)
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(tiledSource, 0, 0, paint)
                    End Using
                End Using

                Dim stride = Math.Max(1, TileEdge - 2 * TileOverlap)
                Dim tileCount = ((tiledSource.Width + stride - 1) \ stride) * ((tiledSource.Height + stride - 1) \ stride)
                Dim name = session.InputMetadata.Keys.First()
                Dim clock = Diagnostics.Stopwatch.StartNew()
                Dim done = 0

                Dim y = 0
                While y < tiledSource.Height
                    Dim x = 0
                    While x < tiledSource.Width
                        ' ABBRUCH an der Kachelgrenze - die einzige Stelle, an der es sauber geht.
                        ' Zurueck kommt Nothing, nicht das halbe Ergebnis: ein Bild, das oben
                        ' entrauscht ist und unten nicht, waere das schlechteste aller Enden.
                        If cancel.IsCancellationRequested Then
                            DiagnosticLogService.LogAlways("Entrauschen",
                                $"abgebrochen nach {done} von {tileCount} Kacheln")
                            Return Nothing
                        End If
                        ' Der Ausschnitt ist IMMER genau eine Kachel gross und wird am Bildrand nach
                        ' innen geschoben, statt beschnitten zu werden.
                        '
                        ' Das ist keine Bequemlichkeit: das Modell zerlegt seine Eingabe in Fenster
                        ' und halbiert sie mehrfach, also muss die Kantenlaenge durch 64 teilbar
                        ' sein. Ein beschnittener Randstreifen von 480 Punkten bricht mitten im
                        ' Modell ab, mit einer Meldung ueber eine Umformung, die die Ursache nicht
                        ' erkennen laesst.
                        Dim l = Math.Max(0, Math.Min(x - TileOverlap, tiledSource.Width - TileEdge))
                        Dim t = Math.Max(0, Math.Min(y - TileOverlap, tiledSource.Height - TileEdge))
                        Dim r = Math.Min(tiledSource.Width, l + TileEdge)
                        Dim b = Math.Min(tiledSource.Height, t + TileEdge)
                        ' Und der Teil davon, der wirklich uebernommen wird.
                        Dim keepL = x, keepT = y
                        Dim keepR = Math.Min(tiledSource.Width, x + stride)
                        Dim keepB = Math.Min(tiledSource.Height, y + stride)
                        If r - l = TileEdge AndAlso b - t = TileEdge AndAlso
                           keepR > keepL AndAlso keepB > keepT Then
                            DenoiseTile(session, name, tiledSource, result,
                                        New SKRectI(l, t, r, b), New SKRectI(keepL, keepT, keepR, keepB), amount)
                            done += 1
                            Progress?.Invoke(done, tileCount)
                        End If
                        x += stride
                    End While
                    y += stride
                End While

                ' Vom paddeden Ergebnis bleibt nur der Bereich des echten Bildes; die gespiegelten
                ' Raender waren Anlauf fuer das Modell und haben in der Ausgabe nichts zu suchen.
                If padded IsNot Nothing Then
                    Dim cropped = New SKBitmap(image.Width, image.Height, image.ColorType, image.AlphaType)
                    Using canvas = New SKCanvas(cropped)
                        Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                            canvas.DrawBitmap(result, 0, 0, paint)
                        End Using
                    End Using
                    result.Dispose()
                    result = cropped
                End If

                clock.Stop()
                ' Welches Modell gerechnet hat, gehoert in den Bericht: bei zwei Wegen ist die Frage
                ' "warum sieht das anders aus als beim letzten Mal" sonst nicht zu beantworten.
                LastReport = $"Bild {image.Width}x{image.Height}, {done} Kacheln, " &
                             $"{clock.Elapsed.TotalSeconds:F0} s, Stärke {amount * 100:F0} %, " &
                             $"{KindName(kind)}"
                DiagnosticLogService.LogAlways("Entrauschen", LastReport)
                Dim finished = result
                result = Nothing
                Return finished
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Entrauschen", ex.Message)
                result?.Dispose()
                Return Nothing
            Finally
                padded?.Dispose()
            End Try
        End Function

        ''' <summary>Eine Kachel durch das Modell und nur ihren INNEREN Teil zurueckschreiben.</summary>
        Private Shared Sub DenoiseTile(session As InferenceSession, name As String,
                                       source As SKBitmap, target As SKBitmap,
                                       window As SKRectI, keep As SKRectI, amount As Single)
            Dim w = window.Width, h = window.Height
            Dim layer = w * h
            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, h, w})
            Dim z = tensor.Buffer.Span

            ' Zeilenweise ueber den Speicher, nicht ueber GetPixel - bei einem ganzen Foto ist das
            ' der Unterschied zwischen Sekunden und Minuten allein fuer das Einlesen.
            Dim srcStride = source.RowBytes
            Dim row(srcStride - 1) As Byte
            Dim srcPixels = source.GetPixels()
            For yy = 0 To h - 1
                Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(srcPixels, (window.Top + yy) * srcStride), row, 0, srcStride)
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
                Dim values = output.Buffer.Span
                If values.Length < rLayer * 3 Then Return

                Dim dstStride = target.RowBytes
                Dim targetPixels = target.GetPixels()
                Dim targetRow(dstStride - 1) As Byte
                Dim premultiplied = target.AlphaType = SKAlphaType.Premul
                For yy = keep.Top To keep.Bottom - 1
                    Runtime.InteropServices.Marshal.Copy(
                        IntPtr.Add(targetPixels, yy * dstStride), targetRow, 0, dstStride)
                    Dim sy = Math.Min(yy - window.Top, rh - 1)
                    For xx = keep.Left To keep.Right - 1
                        Dim sx = Math.Min(xx - window.Left, rw - 1)
                        Dim i = sy * rw + sx
                        Dim p = xx * 4
                        Dim newRed = values(i) * 255.0F
                        Dim newGreen = values(rLayer + i) * 255.0F
                        Dim newBlue = values(rLayer * 2 + i) * 255.0F
                        ' Ein einzelner unsinniger Wert darf nicht den Bildpunkt verderben - dann
                        ' bleibt eben das Original stehen.
                        If Single.IsNaN(newRed) OrElse Single.IsNaN(newGreen) OrElse Single.IsNaN(newBlue) Then
                            Continue For
                        End If

                        ' HIER wird die Staerke wirksam, und zwar getrennt nach Helligkeit und Farbe
                        ' (die Begruendung steht bei Denoise). Der Weg dahin ist eine Verschiebung
                        ' und keine zweite Ueberblendung: die Modellausgabe wird als Ganzes so weit
                        ' heller oder dunkler geschoben, dass ihre Helligkeit den gewuenschten Wert
                        ' trifft. Weil alle drei Kanaele dieselbe Verschiebung bekommen, bleiben ihre
                        ' ABSTAENDE zueinander unveraendert - und genau die sind die Farbe. So kommt
                        ' die Farbe voll aus dem Modell, waehrend die Helligkeit dosiert wird.
                        '
                        ' Bei Staerke 1 ist die Verschiebung null und es steht Bildpunkt fuer
                        ' Bildpunkt dasselbe da wie vorher, als noch alle drei Kanaele gleich
                        ' gemischt wurden.
                        Dim oldLuma = 0.2126F * targetRow(p + 2) + 0.7152F * targetRow(p + 1) + 0.0722F * targetRow(p)
                        Dim newLuma = 0.2126F * newRed + 0.7152F * newGreen + 0.0722F * newBlue
                        Dim shift = (oldLuma - newLuma) * (1.0F - amount)
                        targetRow(p) = ToByte(newBlue + shift)
                        targetRow(p + 1) = ToByte(newGreen + shift)
                        targetRow(p + 2) = ToByte(newRed + shift)
                        ' VORMULTIPLIZIERT heisst: kein Kanal darf ueber der Deckung liegen. Das
                        ' Modell weiss davon nichts - es bekommt an einem halbdurchsichtigen Rand
                        ' die bereits gedaempften Werte und kann sie anheben. Ein Bildpunkt mit mehr
                        ' Farbe als Deckung bricht die Zusage des Formats, und was daraus wird,
                        ' haengt dann am Zeichenweg. Der Alphakanal selbst bleibt unangetastet.
                        If premultiplied Then
                            Dim alpha = targetRow(p + 3)
                            If targetRow(p) > alpha Then targetRow(p) = alpha
                            If targetRow(p + 1) > alpha Then targetRow(p + 1) = alpha
                            If targetRow(p + 2) > alpha Then targetRow(p + 2) = alpha
                        End If
                    Next
                    Runtime.InteropServices.Marshal.Copy(targetRow, 0, IntPtr.Add(targetPixels, yy * dstStride), dstStride)
                Next
            End Using
        End Sub

        ''' <summary>Einen gerechneten Kanalwert in eine Stufe zurueckholen.
        '''
        ''' Das Beschneiden am Rand ist die einzige Stelle, an der die Verschiebung die Farbe doch
        ''' antastet: ein Kanal, der oben oder unten anstoesst, kann der Verschiebung nicht mehr
        ''' folgen. Das trifft nur ausgefressene Lichter und abgesoffene Tiefen, wo ohnehin keine
        ''' Farbe mehr steht.</summary>
        ''' <remarks>Beschnitten wird in Fliesskomma und ERST DANN gerundet: ein Modellwert weit
        ''' jenseits des Wertebereichs wuerde beim Umwandeln in eine Ganzzahl sonst eine Ausnahme
        ''' werfen, und die risse den ganzen Lauf ab.</remarks>
        Private Shared Function ToByte(value As Single) As Byte
            Return CByte(Math.Round(Math.Max(0.0F, Math.Min(255.0F, value))))
        End Function

    End Class

End Namespace
