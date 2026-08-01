Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Runtime.InteropServices
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ''' <summary>Ein gefundenes Gesicht: wo es liegt, wie sicher, und die Zahlenreihe, mit der sich
    ''' zwei Gesichter vergleichen lassen.
    '''
    ''' Die Koordinaten stehen in BILDpunkten der Quelle, nicht im Modellraster - der Aufrufer soll
    ''' nicht zuruecksrechnen muessen.</summary>
    Public Class DetectedFace
        Public Property X As Single
        Public Property Y As Single
        Public Property Width As Single
        Public Property Height As Single
        Public Property Score As Single

        ''' <summary>Fuenf Punkte, abwechselnd X und Y: linkes Auge, rechtes Auge, Nasenspitze,
        ''' linker Mundwinkel, rechter Mundwinkel. Sie richten den Ausschnitt fuer den Vergleich aus -
        ''' ein schraeg gehaltener Kopf ergaebe sonst eine andere Zahlenreihe als derselbe Kopf
        ''' gerade.</summary>
        Public Property Landmarks As Single() = New Single(9) {}

        ''' <summary>128 Zahlen aus dem Vergleichsmodell, oder Nothing, wenn nur gesucht wurde.</summary>
        Public Property Embedding As Single()
    End Class

    ''' <summary>Gesichter in einem Bild finden und vergleichbar machen.
    '''
    ''' ZWEI MODELLE, zwei getrennte Aufgaben, und das ist keine Verdopplung:
    '''
    ''' Das SUCHmodell geht einmal ueber das Bild und liefert Rechtecke. Es sagt, DASS da jemand ist,
    ''' und nichts weiter - es kennt keine Personen.
    '''
    ''' Das VERGLEICHSmodell bekommt ein einzelnes ausgeschnittenes Gesicht und macht daraus 128
    ''' Zahlen. Zwei Aufnahmen derselben Person ergeben aehnliche Reihen, zwei verschiedene Personen
    ''' unaehnliche. Auch dieses Modell weiss nicht, WER jemand ist - Namen vergibt allein der
    ''' Benutzer.
    '''
    ''' Gemessen auf 24 Kernen: Suchen 47 bis 69 ms je Foto, Vergleichen rund 35 ms je Gesicht. Auf
    ''' einem Bild mit fuenf Personen also gut eine Viertelsekunde. Das Suchmodell rechnet auf einem
    ''' festen Quadrat von 640 Punkten; ein Gesicht, das darin kleiner als etwa zehn Punkte wird,
    ''' findet es nicht mehr. Bei einem 5500 Punkte breiten Foto ist das ein Gesicht unter rund 90
    ''' Punkten - fuer Gruppenbilder aus normaler Entfernung reicht es, fuer eine Menschenmenge im
    ''' Hintergrund nicht.
    '''
    ''' FUER IMMICH reicht deshalb die Vorschau: das Original in voller Aufloesung zu holen brachte
    ''' nichts, weil ohnehin auf 640 verkleinert wird. Nur der Ausschnitt fuer das Vergleichsmodell
    ''' wird aus der besten verfuegbaren Fassung geschnitten.</summary>
    Public NotInheritable Class FaceDetectionService

        Private Sub New()
        End Sub

        Public Const DetectorKey As String = "yunet"      ' Schluessel, nicht Dateiname
        ''' <summary>Das Vergleichsmodell: ArcFace ResNet100 aus dem ONNX Model Zoo (261 MB,
        ''' Apache-2.0 und damit mit der GPL vertraeglich).
        '''
        ''' ES GAB EINMAL ZWEI. Das kleine sface (38 MB) war die Grundausstattung und zugleich der
        ''' Engpass: gemessen an 60 echten Fotos erkannte es bei 1 Prozent Fehlzuordnung 39,0 Prozent
        ''' der echten Paare, ArcFace 42,5 Prozent - und auf den Testfotos fiel die hoechste
        ''' Fremd-Aehnlichkeit von 0,35 auf 0,21. Zwei Modelle nebeneinander hiessen zwei Schwellen,
        ''' zwei Merkmalslaengen und bei jeder Frage die Rueckfrage, welches gerade gilt. Fuer den
        ''' Gegenwert lohnt das nicht. Wer die Datei nicht holt, hat die Funktion nicht - wie bei
        ''' jedem anderen Modell auch.</summary>
        Public Const RecognizerKey As String = "arcface"

        ''' <summary>Kantenlaenge des Suchmodells. Fest verdrahtet im Modell, nicht verhandelbar.</summary>
        Public Const DetectorEdge As Integer = 640

        ''' <summary>Kantenlaenge des Vergleichsmodells.</summary>
        Public Const RecognizerEdge As Integer = 112

        ''' <summary>Ab welcher Aehnlichkeit zwei Gesichter als dieselbe Person gelten.
        '''
        ''' GEMESSEN, nicht uebernommen. Als Fremdsatz dienen Gesichtspaare AUF DEMSELBEN BILD - die
        ''' sind praktisch immer verschiedene Menschen; als echte Paare acht benannte Personen ueber
        ''' verschiedene Bilder. An 60 echten Fotos mit 210 Gesichtern, verglichen bei GLEICHER
        ''' Fehlerrate:
        '''
        ''' <code>
        ''' bei 1 % Fehlzuordnung   Schwelle   echte Paare erkannt
        '''   ArcFace                 0,373           42,5 %
        '''   ArcFace mit BGR         0,450           32,4 %
        '''   sface (abgeloest)       0,479           39,0 %
        ''' </code>
        '''
        ''' Die zweite Zeile ist die Gegenprobe zur Kanalreihenfolge: mit vertauschten Kanaelen laeuft
        ''' das Modell anstandslos weiter und liefert brauchbar aussehende Zahlen - es trennt nur
        ''' schlechter. Ein Fehler, den man nur durch Messen findet.
        '''
        ''' Mehr als die Haelfte der echten Paare faellt bei 0,38 unter die Schwelle. Das ist der
        ''' BILLIGERE Fehler: eine geteilte Person fuehrt zu zwei Gruppen, und die verschmelzen beim
        ''' Benennen von selbst. Eine falsche Zusammenlegung dagegen ist Handarbeit an jedem
        ''' einzelnen Gesicht. Dazu kommt: zugeordnet wird gegen den MITTELWERT einer Gruppe, und der
        ''' ist stabiler als ein einzelnes Gesicht - die Tabelle ist der pessimistische Fall.</summary>
        Public Const SamePersonThreshold As Double = 0.38

        ''' <summary>Ab welcher Sicherheit ein Fund als Gesicht gilt. Darunter haeuften sich im
        ''' Versuch Baumkronen und Muster.</summary>
        Public Const DefaultScoreThreshold As Single = 0.6F

        ''' <summary>Steht die Funktion zur Verfuegung? Beide Modelle und die Laufzeit werden
        ''' gebraucht - mit nur dem Suchmodell wuesste man zwar, dass jemand im Bild ist, aber nie,
        ''' ob es dieselbe Person ist wie nebenan.</summary>
        Public Shared ReadOnly Property Available As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(DetectorKey)) AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(RecognizerKey))
            End Get
        End Property

        ''' <summary>Zusaetzlich zu <see cref="Available"/>: hat der Benutzer die Funktion auch
        ''' eingeschaltet? Die Modelle allein schalten nichts frei - biometrische Merkmale entstehen
        ''' erst auf ausdrueckliche Ansage.</summary>
        Public Shared ReadOnly Property Enabled As Boolean
            Get
                Return Available AndAlso AppSettingsService.Load().FaceRecognitionEnabled
            End Get
        End Property

        ''' <summary>Sucht die Gesichter in einem Bild. Ohne <paramref name="withEmbeddings"/> bleibt
        ''' <see cref="DetectedFace.Embedding"/> leer - das spart die 35 ms je Gesicht, wenn nur die
        ''' Lage gebraucht wird.</summary>
        Public Shared Function Detect(source As SKBitmap,
                                      Optional scoreThreshold As Single = DefaultScoreThreshold,
                                      Optional withEmbeddings As Boolean = True) As List(Of DetectedFace)
            Dim result As New List(Of DetectedFace)()
            If source Is Nothing OrElse source.Width < 8 OrElse source.Height < 8 Then Return result
            If Not Available Then Return result

            Dim detector = AiModelService.SessionFor(DetectorKey)
            If detector Is Nothing Then Return result

            Try
                result = RunDetector(detector, source, scoreThreshold)
                If withEmbeddings AndAlso result.Count > 0 Then
                    Dim recognizer = AiModelService.SessionFor(RecognizerKey)
                    If recognizer IsNot Nothing Then
                        For Each face In result
                            ' Zu klein IM BILD: dann gibt es bewusst gar keine Merkmale.
                            If Math.Max(face.Width, face.Height) < MinimumFaceSize Then Continue For
                            Dim refined = RefineLandmarks(detector, source, face)
                            If refined Is Nothing Then Continue For   ' zu klein, siehe unten
                            face.Embedding = RunRecognizer(recognizer, source, refined)
                        Next
                    End If
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Gesichter.Suchen", ex)
                Return New List(Of DetectedFace)()
            End Try
            Return result
        End Function

        ''' <summary>Aehnlichkeit zweier Merkmalsreihen ueber den Kosinus: 1 heisst gleich, 0 heisst
        ''' unabhaengig. Verglichen wird gegen <see cref="SamePersonThreshold"/>.</summary>
        Public Shared Function Similarity(a As Single(), b As Single()) As Double
            If a Is Nothing OrElse b Is Nothing OrElse a.Length = 0 OrElse a.Length <> b.Length Then Return 0
            Dim dot As Double = 0, na As Double = 0, nb As Double = 0
            For i = 0 To a.Length - 1
                dot += CDbl(a(i)) * b(i)
                na += CDbl(a(i)) * a(i)
                nb += CDbl(b(i)) * b(i)
            Next
            Dim denom = Math.Sqrt(na) * Math.Sqrt(nb)
            If denom <= 0.000000001 Then Return 0
            Return dot / denom
        End Function

        Public Shared Function IsSamePerson(a As Single(), b As Single()) As Boolean
            Return Similarity(a, b) >= SamePersonThreshold
        End Function

        ' ── Suchmodell ───────────────────────────────────────────────────────────

        ''' <summary>Das Suchmodell ist ANKERFREI und liefert seine Funde auf drei Rastern
        ''' gleichzeitig (Schrittweite 8, 16 und 32). Jede Rasterzelle traegt vier Werte: wie sicher
        ''' es ein Gesicht ist (zwei Werte, die multipliziert werden), wo dessen Mitte relativ zur
        ''' Zelle liegt, und wie gross es ist - letzteres logarithmisch, deshalb die Potenzierung.
        '''
        ''' Feine Raster finden kleine Gesichter, grobe grosse. Ein Gesicht faellt dabei meist
        ''' mehreren Zellen auf; das raeumt <see cref="SuppressOverlaps"/> auf.</summary>
        Private Shared Function RunDetector(session As InferenceSession, source As SKBitmap,
                                            scoreThreshold As Single) As List(Of DetectedFace)
            Dim found As New List(Of DetectedFace)()

            ' Laengsseitig einpassen, Rest schwarz - das Seitenverhaeltnis muss bleiben, sonst
            ' verzerrt sich jedes Gesicht und die Fundrate faellt.
            Dim scale = Math.Min(DetectorEdge / CSng(source.Width), DetectorEdge / CSng(source.Height))
            Dim fittedWidth = Math.Max(1, CInt(Math.Round(source.Width * scale)))
            Dim fittedHeight = Math.Max(1, CInt(Math.Round(source.Height * scale)))

            Dim input(3 * DetectorEdge * DetectorEdge - 1) As Single
            Using fitted = New SKBitmap(DetectorEdge, DetectorEdge, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Using canvas = New SKCanvas(fitted)
                    canvas.Clear(SKColors.Black)
                    canvas.DrawBitmap(source,
                                      New SKRect(0, 0, source.Width, source.Height),
                                      New SKRect(0, 0, fittedWidth, fittedHeight),
                                      New SKPaint With {.FilterQuality = SKFilterQuality.High})
                End Using

                ' Ueber den Byte-Puffer statt ueber GetPixel: bei 410000 Punkten ist das der
                ' Unterschied zwischen Millisekunden und Sekunden (siehe RENDERPIPELINE.md, Regel 2).
                Dim stride = fitted.RowBytes
                Dim raw(stride * DetectorEdge - 1) As Byte
                Marshal.Copy(fitted.GetPixels(), raw, 0, raw.Length)

                Dim plane = DetectorEdge * DetectorEdge
                For y = 0 To DetectorEdge - 1
                    Dim rowOffset = y * stride
                    Dim rowIndex = y * DetectorEdge
                    For x = 0 To DetectorEdge - 1
                        Dim o = rowOffset + x * 4
                        Dim k = rowIndex + x
                        ' Das Modell erwartet BGR als Rohwerte, ohne Normalisierung. Bgra8888 liegt
                        ' im Speicher genau als B, G, R, A - deshalb hier ohne Umsortieren.
                        input(k) = raw(o)
                        input(plane + k) = raw(o + 1)
                        input(2 * plane + k) = raw(o + 2)
                    Next
                Next
            End Using

            Dim inputName = session.InputMetadata.First().Key
            Using outputs = session.Run(New List(Of NamedOnnxValue) From {
                    NamedOnnxValue.CreateFromTensor(inputName,
                        New DenseTensor(Of Single)(input, New Integer() {1, 3, DetectorEdge, DetectorEdge}))})

                Dim byName = outputs.ToDictionary(Function(r) r.Name, Function(r) r.AsTensor(Of Single)().ToArray())

                For Each stride In New Integer() {8, 16, 32}
                    Dim cls As Single() = Nothing, obj As Single() = Nothing
                    Dim box As Single() = Nothing, kps As Single() = Nothing
                    If Not byName.TryGetValue("cls_" & stride, cls) Then Continue For
                    If Not byName.TryGetValue("obj_" & stride, obj) Then Continue For
                    If Not byName.TryGetValue("bbox_" & stride, box) Then Continue For
                    If Not byName.TryGetValue("kps_" & stride, kps) Then Continue For

                    Dim columns = DetectorEdge \ stride
                    For i = 0 To cls.Length - 1
                        Dim score = CSng(Math.Sqrt(Clamp01(cls(i)) * Clamp01(obj(i))))
                        If score < scoreThreshold Then Continue For

                        Dim gridX = i Mod columns
                        Dim gridY = i \ columns
                        Dim centerX = (gridX + box(i * 4)) * stride
                        Dim centerY = (gridY + box(i * 4 + 1)) * stride
                        Dim w = CSng(Math.Exp(box(i * 4 + 2))) * stride
                        Dim h = CSng(Math.Exp(box(i * 4 + 3))) * stride

                        Dim face As New DetectedFace With {
                            .X = (centerX - w / 2) / scale,
                            .Y = (centerY - h / 2) / scale,
                            .Width = w / scale,
                            .Height = h / scale,
                            .Score = score}
                        For p = 0 To 4
                            face.Landmarks(p * 2) = (gridX + kps(i * 10 + p * 2)) * stride / scale
                            face.Landmarks(p * 2 + 1) = (gridY + kps(i * 10 + p * 2 + 1)) * stride / scale
                        Next
                        found.Add(face)
                    Next
                Next
            End Using

            Return SuppressOverlaps(found, 0.3F)
        End Function

        Private Shared Function Clamp01(v As Single) As Single
            If Single.IsNaN(v) Then Return 0
            Return Math.Max(0.0F, Math.Min(1.0F, v))
        End Function

        ''' <summary>Dasselbe Gesicht faellt mehreren Rasterzellen auf. Behalten wird der sicherste
        ''' Fund; jeder weitere, der sich zu stark mit einem behaltenen ueberdeckt, faellt weg.</summary>
        Private Shared Function SuppressOverlaps(input As List(Of DetectedFace), maxOverlap As Single) As List(Of DetectedFace)
            Dim keep As New List(Of DetectedFace)()
            For Each candidate In input.OrderByDescending(Function(f) f.Score)
                Dim covered = False
                For Each kept In keep
                    If Overlap(kept, candidate) > maxOverlap Then
                        covered = True
                        Exit For
                    End If
                Next
                If Not covered Then keep.Add(candidate)
            Next
            Return keep
        End Function

        ''' <summary>Gemeinsame Flaeche zweier Rechtecke, geteilt durch ihre Gesamtflaeche.</summary>
        Private Shared Function Overlap(a As DetectedFace, b As DetectedFace) As Single
            Dim x1 = Math.Max(a.X, b.X)
            Dim y1 = Math.Max(a.Y, b.Y)
            Dim x2 = Math.Min(a.X + a.Width, b.X + b.Width)
            Dim y2 = Math.Min(a.Y + a.Height, b.Y + b.Height)
            Dim intersection = Math.Max(0.0F, x2 - x1) * Math.Max(0.0F, y2 - y1)
            Dim union = a.Width * a.Height + b.Width * b.Height - intersection
            If union <= 0 Then Return 0
            Return intersection / union
        End Function

        ' ── Vergleichsmodell ─────────────────────────────────────────────────────

        ''' <summary>Wie gross ein Gesicht im Modellraster mindestens sein muss, damit seine Merkmale
        ''' etwas taugen. GEMESSEN als Kennlinie ueber die Rastergroesse, mit der Aehnlichkeit
        ''' desselben Gesichts zu sich selbst und zu einer anderen Person:
        '''
        '''   242 bis 40 Punkte : selbst 0,81 bis 1,00, fremd hoechstens 0,07  - sicher
        '''    30 Punkte        : selbst 0,51                                  - halbiert
        '''    20 Punkte        : selbst 0,46
        '''    15 Punkte        : selbst 0,30                                  - unter der Schwelle
        '''    11 Punkte        : selbst 0,01                                  - wertlos
        '''
        ''' Bemerkenswert ist, WIE es kaputtgeht: die Fremd-Aehnlichkeit bleibt immer niedrig. Kleine
        ''' Gesichter fuehren also nicht dazu, dass Fremde zusammengeworfen werden, sondern dazu, dass
        ''' dieselbe Person nicht mehr wiedererkannt wird - die Merkmale sind dann Rauschen, und
        ''' Rauschen liegt gelegentlich zufaellig ueber der Schwelle. Genau daran ist die erste
        ''' Fassung gescheitert (zwei Fremde bei 0,37).</summary>
        Private Const MinimumRasterSize As Single = 40.0F

        ''' <summary>Wie gross ein Gesicht IM BILD mindestens sein muss, damit es Merkmale bekommt.
        '''
        ''' Die Rastergrenze oben misst im Suchmodell, nicht im Bild - und weil der Ausschnitt der
        ''' Verfeinerung immer vier Gesichtsbreiten gross ist, kommt dort jedes Gesicht auf 160
        ''' Punkte, auch ein winziges. Fuer das VERGLEICHSmodell zaehlt aber die echte Aufloesung:
        ''' sein Raster ist 112 Punkte breit, und ein Gesicht von 40 Punkten wird dafuer
        ''' hochgerechnet. Zu vergleichen gibt es dann nichts mehr - herausgerechnete Bildpunkte
        ''' tragen keine Information.
        '''
        ''' GEMESSEN an einem echten Bestand, ueber Gesichtspaare auf DEMSELBEN Bild (also sicher
        ''' verschiedene Menschen), Anteil ueber der alten Schwelle 0,363:
        '''
        ''' <code>
        ''' kleineres Gesicht   Fremdpaare ueber der Schwelle
        '''   40 bis 59 Punkte           15,8 %
        '''   60 bis 79 Punkte           27,2 %
        '''   80 bis 199 Punkte           rund 6 %
        ''' </code>
        '''
        ''' Unter 80 Punkten sind die Merkmale also nicht schwach, sondern Rauschen - und Rauschen
        ''' trifft gelegentlich zufaellig. Solche Gesichter bleiben ein gueltiger FUND (sie stehen im
        ''' Panel und im Bild), sie lassen sich nur keiner Person zuordnen. Auf dem gemessenen
        ''' Bestand betrifft das 22,7 Prozent der Gesichter, aber nur 14 von 304 Bildern verlieren
        ''' dadurch jede zuordenbare Person.</summary>
        Public Const MinimumFaceSize As Single = 80.0F

        ''' <summary>Das Gesicht ein zweites Mal suchen, diesmal in einem engen Ausschnitt.
        '''
        ''' WARUM: Die fuenf Punkte kommen aus dem 640er Raster ueber das GANZE Bild. Ein Gesicht,
        ''' das dort 20 Punkte breit ist, hat entsprechend grobe Punkte - und auf ihnen steht die
        ''' ganze Ausrichtung. Ein Ausschnitt von vier Gesichtsbreiten bringt dasselbe Gesicht auf
        ''' rund 160 Punkte, also mitten in den sicheren Bereich der Kennlinie oben.
        '''
        ''' Der Preis ist ein weiterer Suchlauf je Gesicht (rund 50 ms). Das ist es wert: ohne ihn
        ''' sind auf einem Gruppenfoto aus normaler Entfernung ALLE Merkmale unbrauchbar, und die
        ''' Erkennung findet zwar Gesichter, kann aber keine Person daraus machen.
        '''
        ''' Liefert Nothing, wenn das Gesicht auch im Ausschnitt zu klein bleibt - dann gibt es
        ''' bewusst keine Merkmale statt schlechter. Ein Gesicht ohne Merkmale ist als Fund weiter
        ''' gueltig, es laesst sich nur keiner Person zuordnen.</summary>
        Private Shared Function RefineLandmarks(detector As InferenceSession, source As SKBitmap,
                                                face As DetectedFace) As DetectedFace
            Dim faceSize = Math.Max(face.Width, face.Height)
            If faceSize <= 0 Then Return Nothing

            ' Vier Gesichtsbreiten Ausschnitt: genug Kontext, damit das Suchmodell das Gesicht
            ' wiederfindet, und eng genug fuer eine hohe Rasteraufloesung.
            Dim windowSize = CInt(Math.Round(faceSize * 4.0F))
            windowSize = Math.Max(64, Math.Min(windowSize, Math.Min(source.Width, source.Height)))

            ' Steht das Gesicht schon gross genug im Vollbild, ist der zweite Lauf verschenkt.
            Dim rasterInFull = DetectorEdge * faceSize / Math.Max(source.Width, source.Height)
            If rasterInFull >= 160.0F Then Return face

            Dim cx = face.X + face.Width / 2
            Dim cy = face.Y + face.Height / 2
            Dim left = CInt(Math.Round(cx - windowSize / 2.0F))
            Dim top = CInt(Math.Round(cy - windowSize / 2.0F))
            left = Math.Max(0, Math.Min(left, source.Width - windowSize))
            top = Math.Max(0, Math.Min(top, source.Height - windowSize))

            ' Bleibt das Gesicht auch im engsten sinnvollen Ausschnitt unter der Grenze, ist es
            ' schlicht zu klein aufgeloest.
            Dim rasterInWindow = DetectorEdge * faceSize / windowSize
            If rasterInWindow < MinimumRasterSize Then Return Nothing

            Try
                Using window = New SKBitmap(windowSize, windowSize, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(window)
                        canvas.Clear(SKColors.Black)
                        canvas.DrawBitmap(source,
                                          New SKRect(left, top, left + windowSize, top + windowSize),
                                          New SKRect(0, 0, windowSize, windowSize),
                                          New SKPaint With {.FilterQuality = SKFilterQuality.High})
                    End Using

                    ' Etwas nachsichtiger als im Vollbild: hier ist bekannt, dass ein Gesicht drin
                    ' ist, es geht nur noch um genaue Punkte.
                    Dim inWindow = RunDetector(detector, window, 0.5F)
                    If inWindow.Count = 0 Then Return Nothing

                    ' GENOMMEN WIRD, WAS SICH MIT DEM BEKANNTEN KASTEN DECKT, nicht das mittigste.
                    '
                    ' BEFUND aus einem echten Bestand: von 8644 Gesichtspaaren AUF DEMSELBEN BILD
                    ' lagen 1470 ueber der Aehnlichkeitsschwelle, viele bei 0,97 bis 0,999. Zwei
                    ' verschiedene Gesichter koennen nicht fast identisch sein - es war zweimal
                    ' DASSELBE. Der Ausschnitt ist vier Gesichtsbreiten gross, auf einem Gruppenfoto
                    ' stehen darin mehrere Koepfe, und "am naechsten zur Mitte" traf bei zwei
                    ' benachbarten Gesichtern zweimal denselben. Beide bekamen dieselbe
                    ' Merkmalsreihe, landeten in einer Gruppe und zogen von dort aus weitere
                    ' Fremde nach: 158 von 240 Bildern trugen dieselbe Person mehrfach.
                    '
                    ' Hier ist bekannt, WO das Gesicht liegt - gesucht sind nur genauere Punkte.
                    ' Also die Ueberdeckung mit dem bekannten Kasten, und nicht die Naehe zur Mitte.
                    Dim expected As New DetectedFace With {
                        .X = face.X - left, .Y = face.Y - top,
                        .Width = face.Width, .Height = face.Height}
                    Dim best = inWindow.OrderByDescending(Function(f) Overlap(expected, f)).First()

                    ' Deckt sich nichts, hat der zweite Lauf ein ANDERES Gesicht gefunden. Dann
                    ' lieber die groben Punkte des ersten Laufs behalten: sie gehoeren wenigstens
                    ' zum richtigen Gesicht. Eine ungenaue Ausrichtung ergibt eine schwache
                    ' Merkmalsreihe, und die trifft im Zweifel niemanden - die Reihe des NACHBARN
                    ' dagegen trifft mit voller Ueberzeugung den Falschen.
                    If Overlap(expected, best) < 0.3F Then Return face

                    ' Zurueck in Bildkoordinaten - der Aufrufer erwartet sie dort.
                    Dim refined As New DetectedFace With {
                        .X = best.X + left, .Y = best.Y + top,
                        .Width = best.Width, .Height = best.Height,
                        .Score = face.Score}
                    For p = 0 To 4
                        refined.Landmarks(p * 2) = best.Landmarks(p * 2) + left
                        refined.Landmarks(p * 2 + 1) = best.Landmarks(p * 2 + 1) + top
                    Next
                    Return refined
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Gesichter.Verfeinern", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Die Sollpositionen der fuenf Punkte im 112er Ausschnitt, auf die jedes Gesicht
        ''' gedreht und skaliert wird. Diese Zahlen gehoeren zum Modell und sind nicht frei waehlbar -
        ''' es hat gelernt, Gesichter in genau dieser Lage zu vergleichen.</summary>
        Private Shared ReadOnly ReferenceLandmarks As Single() = {
            38.2946F, 51.6963F, 73.5318F, 51.5014F, 56.0252F, 71.7366F,
            41.5493F, 92.3655F, 70.7299F, 92.2041F}

        ''' <summary>Schneidet das Gesicht aus und macht daraus 128 Zahlen.
        '''
        ''' AUSGERICHTET, nicht bloss ausgeschnitten. Das Modell vergleicht Gesichter in einer festen
        ''' Lage: Augen waagerecht, immer an derselben Stelle, immer im selben Massstab. Die fuenf
        ''' Punkte des Suchmodells werden deshalb per Drehung, Skalierung und Verschiebung auf
        ''' <see cref="ReferenceLandmarks"/> gelegt.
        '''
        ''' BEFUND, der das erzwungen hat: ein simpler quadratischer Ausschnitt mit 15 Prozent Rand
        ''' liess zwei verschiedene Personen auf 0,47 Aehnlichkeit kommen - deutlich ueber der
        ''' Schwelle von 0,363, also eine Verwechslung. Mit Ausrichtung faellt derselbe Vergleich
        ''' klar darunter. Ein schraeg gehaltener Kopf ergibt sonst eine andere Zahlenreihe als
        ''' derselbe Kopf gerade, und genau das misst das Modell dann statt der Person.</summary>
        Private Shared Function RunRecognizer(session As InferenceSession, source As SKBitmap,
                                              face As DetectedFace) As Single()
            Dim transform = EstimateAlignment(face.Landmarks)

            Dim input(3 * RecognizerEdge * RecognizerEdge - 1) As Single
            Using crop = New SKBitmap(RecognizerEdge, RecognizerEdge, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Using canvas = New SKCanvas(crop)
                    canvas.Clear(SKColors.Black)
                    canvas.SetMatrix(transform)
                    canvas.DrawBitmap(source, 0, 0,
                                      New SKPaint With {.FilterQuality = SKFilterQuality.High})
                End Using

                Dim stride = crop.RowBytes
                Dim raw(stride * RecognizerEdge - 1) As Byte
                Marshal.Copy(crop.GetPixels(), raw, 0, raw.Length)

                Dim plane = RecognizerEdge * RecognizerEdge
                ' ArcFace stammt aus der MXNet-Welt und will RGB. Bgra8888 liegt im Speicher als
                ' B, G, R, A - die aeussere Ebene wird also vertauscht, die mittlere ist Gruen.
                ' Vertauscht laeuft das Modell anstandslos weiter und liefert brauchbar aussehende
                ' Zahlen; es trennt nur schlechter (siehe die Messung an SamePersonThreshold).
                Const firstOffset As Integer = 2
                Const lastOffset As Integer = 0
                For y = 0 To RecognizerEdge - 1
                    Dim rowOffset = y * stride
                    Dim rowIndex = y * RecognizerEdge
                    For x = 0 To RecognizerEdge - 1
                        Dim o = rowOffset + x * 4
                        Dim k = rowIndex + x
                        input(k) = raw(o + firstOffset)
                        input(plane + k) = raw(o + 1)
                        input(2 * plane + k) = raw(o + lastOffset)
                    Next
                Next
            End Using

            Dim inputName = session.InputMetadata.First().Key
            Using outputs = session.Run(New List(Of NamedOnnxValue) From {
                    NamedOnnxValue.CreateFromTensor(inputName,
                        New DenseTensor(Of Single)(input, New Integer() {1, 3, RecognizerEdge, RecognizerEdge}))})
                Return outputs.First().AsTensor(Of Single)().ToArray()
            End Using
        End Function

        ''' <summary>Sucht die Drehung, Skalierung und Verschiebung, die die fuenf gefundenen Punkte
        ''' am besten auf die Sollpositionen legt (kleinste Quadrate ueber alle fuenf).
        '''
        ''' BEWUSST NUR AEHNLICHKEIT, keine freie Abbildung: erlaubt sind Drehen, gleichmaessiges
        ''' Skalieren und Verschieben. Eine freiere Abbildung koennte die fuenf Punkte exakt treffen,
        ''' wuerde dabei aber das Gesicht scheren und stauchen - und damit genau die Proportionen
        ''' zerstoeren, an denen das Modell die Person erkennt.
        '''
        ''' Gerechnet wird mit den Schwerpunkten beider Punktwolken: die Drehung steckt im Verhaeltnis
        ''' der Kreuz- zur Punktsumme der zentrierten Punkte, der Massstab im Verhaeltnis der
        ''' Streuungen.</summary>
        Private Shared Function EstimateAlignment(landmarks As Single()) As SKMatrix
            Dim n = 5
            Dim srcMeanX As Double = 0, srcMeanY As Double = 0
            Dim dstMeanX As Double = 0, dstMeanY As Double = 0
            For i = 0 To n - 1
                srcMeanX += landmarks(i * 2) : srcMeanY += landmarks(i * 2 + 1)
                dstMeanX += ReferenceLandmarks(i * 2) : dstMeanY += ReferenceLandmarks(i * 2 + 1)
            Next
            srcMeanX /= n : srcMeanY /= n : dstMeanX /= n : dstMeanY /= n

            Dim dot As Double = 0, cross As Double = 0, srcVar As Double = 0
            For i = 0 To n - 1
                Dim sx = landmarks(i * 2) - srcMeanX
                Dim sy = landmarks(i * 2 + 1) - srcMeanY
                Dim dx = ReferenceLandmarks(i * 2) - dstMeanX
                Dim dy = ReferenceLandmarks(i * 2 + 1) - dstMeanY
                dot += sx * dx + sy * dy
                cross += sx * dy - sy * dx
                srcVar += sx * sx + sy * sy
            Next

            ' Entartete Punktwolke (alle fuenf Punkte praktisch aufeinander): dann gibt es keine
            ' sinnvolle Drehung. Statt zu raten wird schlicht der Kasten eingepasst.
            If srcVar < 0.000001 Then
                Dim fallbackScale = RecognizerEdge / Math.Max(1.0F, Math.Max(CSng(srcMeanX), CSng(srcMeanY)))
                Return SKMatrix.CreateScale(fallbackScale, fallbackScale)
            End If

            ' a und b tragen Drehung UND Massstab zusammen: die Abbildung ist
            ' [a -b; b a] mal Punkt plus Verschiebung.
            Dim a = dot / srcVar
            Dim b = cross / srcVar
            Dim tx = dstMeanX - (a * srcMeanX - b * srcMeanY)
            Dim ty = dstMeanY - (b * srcMeanX + a * srcMeanY)

            Dim m As SKMatrix = SKMatrix.CreateIdentity()
            m.ScaleX = CSng(a) : m.SkewX = CSng(-b) : m.TransX = CSng(tx)
            m.SkewY = CSng(b) : m.ScaleY = CSng(a) : m.TransY = CSng(ty)
            Return m
        End Function

    End Class

End Namespace
