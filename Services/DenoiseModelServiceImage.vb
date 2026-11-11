Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ' Entrauschen mit Modell fuer FERTIGE Bilder: JPEG, PNG, PSD, TIFF und DNG-Huellen um fertige
    ' Bilder, die an der RAW-Stufe vorbeilaufen (SensorPattern.Finished). Das Modell ist NAFNet SIDD width32, gelernt an
    ' Handyfotos in sRGB, also genau an fertigen Bildern. Es rechnet am entwickelten 8-Bit-Bild und
    ' wird in die Pixel gerechnet (gebackener Vorgang, ImageAdjustments.BakedOperations, Vermerk
    ' "fast"). Bis 0.9.80 war es der Weg "zuegig" fuer alle Bilder; der Weg "gruendlich" (SCUNet)
    ' ist entfallen, ein alter Vermerk "quality" rechnet mit diesem Modell.

    Partial Public NotInheritable Class DenoiseModelService

        ''' <summary>Der Schluessel des Modells fuer fertige Bilder im Register.</summary>
        Public Const ImageModelFile As String = "nafnet"

        ''' <summary>Wie weit sich zwei Kacheln des Bildmodells ueberlappen. Uebernommen wird nur der
        ''' innere Teil, der Rand dient als Anlauf. Gemessen an einem echten RAW: der Sprung an der
        ''' Kachelgrenze liegt bei 16 Punkten unter dem, was im Bild ohnehin von Spalte zu Spalte
        ''' passiert; bei 8 wird es messbar schlechter.</summary>
        Private Const ImageTileOverlap As Integer = 16

        Public Shared ReadOnly Property ImageAvailable As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(ImageModelFile))
            End Get
        End Property

        ''' <summary>Welcher Weg fuer diese Datei gilt: True heisst TreeNet in der RAW-Stufe, False das
        ''' Bildmodell am fertigen Bild.</summary>
        Public Shared Function UsesRawStage(path As String) As Boolean
            Return SupportsFile(path)
        End Function

        ''' <summary>Ein Stapelauftrag am fertigen Bild. Zurueck kommt ein NEUES Bitmap, oder
        ''' Nothing, wenn das Bild so bleibt.</summary>
        Friend Shared Function ApplyImageRequest(image As SKBitmap, request As DenoiseRequest, sourceName As String,
                                                 cancel As Threading.CancellationToken) As SKBitmap
            If image Is Nothing OrElse request Is Nothing OrElse Not ImageAvailable Then Return Nothing
            Dim strength = StrengthForRequest(image, request, sourceName)
            If Not strength.HasValue Then Return Nothing
            Return DenoiseImage(image, CSng(strength.Value / 100.0), cancel)
        End Function

        ''' <summary>Das Bild entrauschen. Zurueck kommt eine KOPIE, oder Nothing bei jedem
        ''' Fehlschlag.
        '''
        ''' <paramref name="strength"/> geht von 0 bis 1 und gilt NUR fuer die HELLIGKEIT; die Farbe
        ''' kommt immer voll aus dem Modell. Das ist keine Vereinfachung, sondern gemessen:
        '''
        ''' Bei der Helligkeit ist die Staerke ein echter Handel. An einer naechtlichen Fassade nahm
        ''' das fruehere gruendliche Modell bei voller Staerke 79 Prozent des Rauschens, laesst von der SCHWACHEN Zeichnung
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
        Public Shared Function DenoiseImage(image As SKBitmap,
                                            Optional strength As Single = 1.0F,
                                            Optional cancel As Threading.CancellationToken = Nothing) As SKBitmap
            If image Is Nothing OrElse image.Width <= 0 OrElse image.Height <= 0 Then
                DiagnosticLogService.LogAlways("Entrauschen", "kein Bild uebergeben")
                Return Nothing
            End If
            ' Sitzung und Rechenwerk in EINEM Stueck: die Kachelgroesse haengt daran, und zweimal
            ' zu fragen hiesse, die zweite Antwort koennte schon zu einer anderen Sitzung gehoeren.
            Dim loaded = AiModelService.SessionAndDeviceFor(ImageModelFile)
            ' DIESE BEIDEN AUSSTIEGE WAREN STUMM, und das ist die schlechteste Sorte Fehler: der
            ' Lauf endet ohne Wirkung und ohne Spur, und in der Anwendung sieht es aus, als habe das
            ' Entrauschen nichts getan. Genau so kam es aus einer Nutzermeldung zurueck.
            If loaded Is Nothing OrElse loaded.Session Is Nothing Then
                DiagnosticLogService.LogAlways("Entrauschen",
                    $"Modell {ImageModelFile} steht nicht bereit - keine Sitzung. Fehlt die Datei, oder hat die Karte sie abgelehnt?")
                Return Nothing
            End If
            Dim session = loaded.Session
            ' EIN ANDERS BELEGTES BILD WIRD UMGELEGT, statt den Lauf ausfallen zu lassen. Die
            ' Kachelschleife liest die Bytes fest als BGRA; welchen Farbtyp ein Bild hat, entscheidet
            ' aber nicht diese Funktion:
            '
            '   - Ein Arbeitsbild mit Objekt-Ebenen liegt als Rgba8888 vor.
            '   - Skia nimmt auf Apple-Geraeten Rgba8888 als bevorzugten Typ, dort ist JEDES
            '     Arbeitsbild so belegt. Auf dem Mac liess sich deshalb gar nicht entrauschen, und
            '     zu sehen war nur "Entrauschen fehlgeschlagen" (Nutzermeldung).
            '
            ' Der Aufrufer bekommt das Ergebnis als Bgra8888 zurueck - alle Wege hier zeichnen es
            ' ueber eine Leinwand weiter oder uebernehmen es als neues Arbeitsbild, und beides legt
            ' den Farbtyp selbst wieder zurecht.
            Dim converted As SKBitmap = Nothing
            If image.ColorType <> SKColorType.Bgra8888 Then
                converted = AsBgra8888(image)
                If converted Is Nothing Then
                    DiagnosticLogService.LogAlways("Entrauschen",
                        $"Bild liegt als {image.ColorType} vor und liess sich nicht auf Bgra8888 umlegen - Lauf faellt aus")
                    Return Nothing
                End If
                image = converted
            End If

            Dim amount = Math.Max(0.0F, Math.Min(1.0F, strength))
            Dim edge = TileEdgeForRun(loaded.Accelerator)

            ' DIE ZEILE VOR DEM RECHNEN, und sie ist der eigentliche Grund fuer dieses Protokoll:
            ' geht dem Modell auf der Karte der Speicher aus, endet nicht der Lauf, sondern der
            ' ganze PROZESS - aus nativem Code heraus, ohne Ausnahme, die sich fangen liesse. Der
            ' Abschlussbericht weiter unten kommt dann nie. Was hier steht, ist danach die letzte
            ' Zeile im Protokoll und sagt, WOMIT es passiert ist: Modell, Bildgroesse, Kachelkante,
            ' Rechenwerk und der Speicher, den die Karte gemeldet hat.
            ' Der KLARNAME der Karte und nicht ihr Schluessel: diese Zeile geht in Fehlerberichte,
            ' und "10de:1287:0000:01:00.0" sagt dort niemandem etwas.
            Dim wo = GpuAccelerationService.DeviceLabelFor(loaded.Accelerator)
            Dim speicher = If(String.IsNullOrEmpty(loaded.Accelerator), 0,
                              GpuAccelerationService.DeviceMemoryMiBFor(loaded.Accelerator))
            DiagnosticLogService.LogAlways("Entrauschen",
                $"Start: {ImageModelFile}, Bild {image.Width}x{image.Height}, Kachel {edge}, " &
                $"Staerke {CInt(amount * 100)} %, auf {wo}, Kartenspeicher {speicher} MiB")

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
                If image.Width < edge OrElse image.Height < edge Then
                    padded = New SKBitmap(Math.Max(image.Width, edge),
                                              Math.Max(image.Height, edge),
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

                Dim stride = Math.Max(1, edge - 2 * ImageTileOverlap)
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
                        Dim l = Math.Max(0, Math.Min(x - ImageTileOverlap, tiledSource.Width - edge))
                        Dim t = Math.Max(0, Math.Min(y - ImageTileOverlap, tiledSource.Height - edge))
                        Dim r = Math.Min(tiledSource.Width, l + edge)
                        Dim b = Math.Min(tiledSource.Height, t + edge)
                        ' Und der Teil davon, der wirklich uebernommen wird.
                        Dim keepL = x, keepT = y
                        Dim keepR = Math.Min(tiledSource.Width, x + stride)
                        Dim keepB = Math.Min(tiledSource.Height, y + stride)
                        If r - l = edge AndAlso b - t = edge AndAlso
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
                '
                ' Eine SCHABLONE mit Platzhaltern statt einer Verkettung: der Bericht landet in der
                ' Statuszeile und im Verlauf, und zusammengesetzte Texte lassen sich nicht
                ' uebersetzen. Der Name des Weges wird EINZELN uebersetzt und danach eingesetzt.
                LastReport = String.Format(
                    LocalizationService.T("Bild {0}x{1}, {2} Kacheln, {3} s, Stärke {4} %, {5}"),
                    image.Width, image.Height, done,
                    clock.Elapsed.TotalSeconds.ToString("F0"),
                    (amount * 100).ToString("F0"),
                    LocalizationService.T("schnell"))
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
                ' Nur die eigene Umlegung, nie das Bild des Aufrufers: <converted> ist Nothing,
                ' wenn das Bild schon als Bgra8888 kam, und <image> zeigt dann auf sein Bitmap.
                converted?.Dispose()
            End Try
        End Function

        ''' <summary>Unterhalb dieses mittleren Tonwerts wird eine Kachel fuer das schnelle Modell
        ''' angehoben, siehe <see cref="DenoiseTile"/>.</summary>
        Friend Const DarkTileLevel As Single = 48.0F

        ''' <summary>Eine Kachel durch das Modell und nur ihren INNEREN Teil zurueckschreiben.
        '''
        ''' DAS MODELL ZERSTOERT SEHR DUNKLE KACHELN. Liegt der mittlere Tonwert einer Kachel unter rund
        ''' 20 und traegt sie wenig Rauschen, gibt NAFNet ein buntes Gitter zurueck statt eines Bildes,
        ''' am Konzertfoto im Pruefbestand ein ganzer Vorhang. Eine solche Kachel wird deshalb vor dem
        ''' Modell um einen VERSATZ angehoben und danach um denselben Betrag abgesenkt. Ein Versatz und
        ''' keine Verstaerkung, weil er das Rauschen unveraendert laesst: das Modell soll genau das
        ''' Rauschen sehen, das im Bild steht. Beschnitten wird dabei nichts, der Eingang ist
        ''' Fliesskomma und darf ueber 1 gehen.</summary>
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
            Dim lumaSum = 0.0
            For yy = 0 To h - 1
                Runtime.InteropServices.Marshal.Copy(
                    RowStart(srcPixels, window.Top + yy, srcStride), row, 0, srcStride)
                Dim line = yy * w
                For xx = 0 To w - 1
                    Dim p = (window.Left + xx) * 4
                    Dim i = line + xx
                    ' BGRA im Speicher, das Modell erwartet RGB in 0 bis 1.
                    z(i) = row(p + 2) / 255.0F
                    z(layer + i) = row(p + 1) / 255.0F
                    z(layer * 2 + i) = row(p) / 255.0F
                    lumaSum += 0.2126 * row(p + 2) + 0.7152 * row(p + 1) + 0.0722 * row(p)
                Next
            Next

            ' Der Versatz in Tonwerten; null fuer jede Kachel, die hell genug ist.
            Dim lift = 0.0F
            Dim mean = CSng(lumaSum / Math.Max(1, layer))
            If mean < DarkTileLevel Then
                lift = DarkTileLevel - mean
                Dim offset = lift / 255.0F
                For i = 0 To layer * 3 - 1
                    z(i) += offset
                Next
            End If

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
                        RowStart(targetPixels, yy, dstStride), targetRow, 0, dstStride)
                    Dim sy = Math.Min(yy - window.Top, rh - 1)
                    For xx = keep.Left To keep.Right - 1
                        Dim sx = Math.Min(xx - window.Left, rw - 1)
                        Dim i = sy * rw + sx
                        Dim p = xx * 4
                        Dim newRed = values(i) * 255.0F - lift
                        Dim newGreen = values(rLayer + i) * 255.0F - lift
                        Dim newBlue = values(rLayer * 2 + i) * 255.0F - lift
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
                        ' MIT DITHER GERUNDET. Das Modell nimmt genau das Rauschen weg, das vorher
                        ' selbst als Dither wirkte; hart gerundet kommt ein glatter dunkler Verlauf
                        ' als Treppe zurueck, und das Anheben der Tiefen in der Reglerkette dahinter
                        ' spreizt sie (gemessen: Treppe 0,39 statt 0,14 Tonwerte bei Tiefen +100).
                        ' Dieselbe Schwelle fuer alle drei Kanaele, sonst faerbt sich Grau ein, und
                        ' dieselbe Matrix wie die Punktoperationskette.
                        Dim dither = ImageProcessor.DitherMatrix(((yy And 7) << 3) Or (xx And 7))
                        targetRow(p) = ToByte(newBlue + shift + dither)
                        targetRow(p + 1) = ToByte(newGreen + shift + dither)
                        targetRow(p + 2) = ToByte(newRed + shift + dither)
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
                    Runtime.InteropServices.Marshal.Copy(targetRow, 0, RowStart(targetPixels, yy, dstStride), dstStride)
                Next
            End Using
        End Sub

        ''' <summary>Der Anfang einer Bildzeile im Speicher.
        '''
        ''' Der Versatz wird in 64 Bit gerechnet. Hier reicht es fuer gewoehnliche Fotos zwar auch
        ''' in 32 Bit, aber ab rund 537 Megapixeln - Panoramen aus vielen Aufnahmen erreichen das -
        ''' ueberschreitet Zeilennummer mal Zeilenlaenge den Bereich einer 32-Bit-Zahl, und in VB
        ''' WIRFT die Multiplikation dann, statt still umzulaufen. Derselbe Weg wie beim
        ''' Hochskalieren, wo die Grenze schon bei gewoehnlichen Kameragroessen faellt.</summary>
        Private Shared Function RowStart(basePointer As IntPtr, row As Integer, stride As Integer) As IntPtr
            Return New IntPtr(basePointer.ToInt64() + CLng(row) * CLng(stride))
        End Function

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
