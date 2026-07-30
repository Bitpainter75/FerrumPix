Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ''' <summary>Maske per Klick: ein Punkt im Bild, heraus kommt die Maske des getroffenen Objekts.
    '''
    ''' Das Modell arbeitet in ZWEI Schritten, und das ist der Grund fuer den ganzen Aufbau hier:
    '''
    ''' Der KODIERER liest das Bild einmal und macht daraus eine Einbettung. Das ist der teure Teil,
    ''' Sekunden auf einer CPU, und er haengt NUR am Bild - nicht am Klick. Deshalb wird er einmal je
    ''' Bild gerechnet und gemerkt.
    '''
    ''' Der DEKODIERER nimmt diese Einbettung plus die geklickten Punkte und liefert die Maske. Das
    ''' ist der billige Teil, Millisekunden. Nur er laeuft bei jedem Klick.
    '''
    ''' Ohne diese Trennung waere die Funktion unbenutzbar: jeder Klick kostete Sekunden. Mit ihr
    ''' kostet das erste Anklicken eines Bildes einmal Zeit und danach fuehlt es sich an wie der
    ''' Zauberstab.
    '''
    ''' Das Modell rechnet auf einem festen Quadrat von 1024 Pixeln. Das Bild wird laengsseitig
    ''' darauf gebracht und der Rest mit Null aufgefuellt - genau so, wie es trainiert wurde. Fuer
    ''' eine Maske reicht das: sie wird ohnehin als weiches Raster gespeichert und beim Rendern
    ''' skaliert.</summary>
    Public NotInheritable Class SubjectMaskService

        Private Sub New()
        End Sub

        Public Const EncoderFile As String = "mobilesam-encoder"   ' Schluessel, nicht Dateiname
        Public Const DecoderFile As String = "mobilesam-decoder"

        ''' <summary>Kantenlaenge, auf die das Modell rechnet. Fest, nicht verhandelbar: die
        ''' Lagekodierung des Netzes ist darauf trainiert.</summary>
        Public Const ModelEdge As Integer = 1024

        ''' <summary>Steht die Funktion zur Verfuegung? Nur wenn Laufzeit UND beide Modelle da sind.</summary>
        Public Shared ReadOnly Property Verfuegbar As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(EncoderFile)) AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(DecoderFile))
            End Get
        End Property

        ''' <summary>Die Einbettung eines Bildes samt dem Massstab, mit dem sie entstanden ist.
        ''' Ein Sitzungswert, kein Rezeptwert - sie gehoert zu genau diesem Bildinhalt.</summary>
        Public NotInheritable Class Einbettung
            Public Property Values As DenseTensor(Of Single)
            ''' <summary>Faktor Bildpixel auf Modellpixel. Ein Klick im Bild wird damit umgerechnet.</summary>
            Public Property Massstab As Double
            Public Property SourceWidth As Integer
            Public Property SourceHeight As Integer
        End Class

        ''' <summary>Bild einmal durch den Kodierer. Teuer - das Ergebnis gehoert gemerkt.
        ''' Nothing, wenn die Funktion nicht verfuegbar ist oder das Modell nicht laeuft.</summary>
        Public Shared Function Kodiere(bild As SKBitmap) As Einbettung
            If bild Is Nothing OrElse bild.Width <= 0 OrElse bild.Height <= 0 Then Return Nothing
            Dim sitzung = AiModelService.SitzungFuer(EncoderFile)
            If sitzung Is Nothing Then Return Nothing

            Dim massstab = ModelEdge / CDbl(Math.Max(bild.Width, bild.Height))
            Dim nb = Math.Max(1, CInt(Math.Round(bild.Width * massstab)))
            Dim nh = Math.Max(1, CInt(Math.Round(bild.Height * massstab)))

            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, ModelEdge, ModelEdge})
            Using klein = New SKBitmap(nb, nh, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                If Not bild.ScalePixels(klein, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                ' Die Normierung stammt aus dem Training des Modells und darf nicht geraten werden.
                Dim mittel = New Single() {123.675F, 116.28F, 103.53F}
                Dim streuung = New Single() {58.395F, 57.12F, 57.375F}
                Dim ziel = tensor.Buffer.Span
                Dim ebene = ModelEdge * ModelEdge
                For y = 0 To nh - 1
                    For x = 0 To nb - 1
                        Dim p = klein.GetPixel(x, y)
                        Dim i = y * ModelEdge + x
                        ziel(i) = (p.Red - mittel(0)) / streuung(0)
                        ziel(ebene + i) = (p.Green - mittel(1)) / streuung(1)
                        ziel(ebene * 2 + i) = (p.Blue - mittel(2)) / streuung(2)
                    Next
                Next
            End Using

            Try
                Dim eingabe = New List(Of NamedOnnxValue) From {
                    NamedOnnxValue.CreateFromTensor("image", tensor)}
                Using ergebnis = sitzung.Run(eingabe)
                    Dim raus = TryCast(ergebnis.First().Value, DenseTensor(Of Single))
                    If raus Is Nothing Then Return Nothing
                    ' Der Tensor gehoert der Ergebnisliste und stirbt mit ihr - also kopieren.
                    Dim kopie = New DenseTensor(Of Single)(raus.Dimensions.ToArray())
                    raus.Buffer.Span.CopyTo(kopie.Buffer.Span)
                    Return New Einbettung With {.Values = kopie, .Massstab = massstab,
                                                .SourceWidth = bild.Width, .SourceHeight = bild.Height}
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogAlways("MotivMaske", "Kodierer: " & ex.Message)
                Return Nothing
            End Try
        End Function

        ''' <summary>Ein angeklickter Punkt. <paramref name="Dazu"/> False heisst: diese Stelle
        ''' gehoert AUSDRUECKLICH nicht dazu - so schneidet man eine zu gross geratene Maske wieder
        ''' zurecht, ohne von vorn anzufangen.</summary>
        Public Structure Punkt
            Public XPixel As Double
            Public YPixel As Double
            Public Dazu As Boolean
            Public Sub New(x As Double, y As Double, dazuGehoerig As Boolean)
                XPixel = x
                YPixel = y
                Dazu = dazuGehoerig
            End Sub
        End Structure

        ''' <summary>Maske zu den angeklickten Punkten, als Alpha8-Bild in der Groesse des
        ''' Quellbildes. Nothing bei jedem Fehlschlag - eine halbe Maske waere schlimmer als keine.</summary>
        ''' <param name="kantePct">Wie steil die Kante ist, 0 bis 100. Klein = breiter, weicher
        ''' Uebergang (Haare, Zweige, Fell), gross = knapp und knackig (harte Gegenstaende). Das ist
        ''' KEINE Weichzeichnung im Nachhinein: es aendert, wie die Rohwerte des Modells in Deckung
        ''' uebersetzt werden, und folgt damit dem, was das Modell an dieser Stelle wirklich
        ''' unsicher findet.</param>
        ''' <param name="umfangPct">Verschiebt die Entscheidungsgrenze, -100 bis 100. Positiv laesst
        ''' die Maske wachsen, negativ schrumpfen. Modelle dieser Art schneiden gern eine Haaresbreite
        ''' INNERHALB des Objekts; damit holt man das zurueck, ohne von vorn anzufangen.</param>
        ''' <param name="koernung">Welche der drei Koernungen: 0 = fein (ein Teil), 1 = mittel (ein
        ''' Unterobjekt), 2 = grob (das ganze Objekt). Ein Klick ist mehrdeutig - meint man die
        ''' Jacke, die Person oder die Gruppe? Das Modell beantwortet alle drei auf einmal, und der
        ''' Nutzer waehlt aus, statt neu zu klicken.</param>
        Public Shared Function MaskFor(einbettung As Einbettung, punkte As IList(Of Punkt),
                                         Optional kantePct As Double = 50.0,
                                         Optional umfangPct As Double = 0.0,
                                         Optional koernung As Integer = 2) As SKBitmap
            If einbettung Is Nothing OrElse einbettung.Values Is Nothing Then Return Nothing
            If punkte Is Nothing OrElse punkte.Count = 0 Then Return Nothing
            Dim sitzung = AiModelService.SitzungFuer(DecoderFile)
            If sitzung Is Nothing Then Return Nothing

            ' Das Modell erwartet die Punkte im MODELL-Massstab, nicht in Bildpixeln.
            Dim n = punkte.Count
            Dim coords = New DenseTensor(Of Single)(New Integer() {1, n + 1, 2})
            Dim labels = New DenseTensor(Of Single)(New Integer() {1, n + 1})
            For i = 0 To n - 1
                coords(0, i, 0) = CSng(punkte(i).XPixel * einbettung.Massstab)
                coords(0, i, 1) = CSng(punkte(i).YPixel * einbettung.Massstab)
                labels(0, i) = If(punkte(i).Dazu, 1.0F, 0.0F)
            Next
            ' Der Abschlusspunkt mit der Marke -1 gehoert zum Aufbau des Modells: ohne ihn rechnet
            ' es mit einem Rahmen statt mit Punkten.
            coords(0, n, 0) = 0.0F
            coords(0, n, 1) = 0.0F
            labels(0, n) = -1.0F

            Try
                Dim eingabe = New List(Of NamedOnnxValue) From {
                    NamedOnnxValue.CreateFromTensor("image_embeddings", einbettung.Values),
                    NamedOnnxValue.CreateFromTensor("point_coords", coords),
                    NamedOnnxValue.CreateFromTensor("point_labels", labels),
                    NamedOnnxValue.CreateFromTensor("mask_input",
                        New DenseTensor(Of Single)(New Integer() {1, 1, 256, 256})),
                    NamedOnnxValue.CreateFromTensor("has_mask_input",
                        New DenseTensor(Of Single)(New Single() {0.0F}, New Integer() {1})),
                    NamedOnnxValue.CreateFromTensor("orig_im_size",
                        New DenseTensor(Of Single)(New Single() {CSng(ModelEdge), CSng(ModelEdge)},
                                                   New Integer() {2}))}
                Using ergebnis = sitzung.Run(eingabe)
                    Dim masken = TryCast(ergebnis.First(Function(r) r.Name = "masks").Value, DenseTensor(Of Single))
                    If masken Is Nothing Then Return Nothing
                    Return AlsAlphaBild(masken, einbettung, kantePct, umfangPct, koernung)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogAlways("MotivMaske", "Dekodierer: " & ex.Message)
                Return Nothing
            End Try
        End Function

        ''' <summary>Die Modellausgabe in ein Alpha8-Bild in Quellgroesse.
        '''
        ''' Das Modell liefert Rohwerte, nicht Deckung: positiv heisst "gehoert dazu". Die Null als
        ''' Schwelle ist die Vorgabe des Modells. Uebergeben wird kein hartes Ja/Nein, sondern ein
        ''' weicher Verlauf um die Schwelle herum - eine harte Kante saehe an Haaren und Zweigen
        ''' ausgeschnitten aus, und die Maske laesst sich hinterher ohnehin mit dem Pinsel
        ''' nachbessern.</summary>
        Private Shared Function AlsAlphaBild(masken As DenseTensor(Of Single), einbettung As Einbettung,
                                             kantePct As Double, umfangPct As Double,
                                             koernung As Integer) As SKBitmap
            ' Als Feld statt als Span - siehe unten, VB kann einen Span nicht indizieren.
            Dim d = masken.Dimensions.ToArray()
            If d.Length < 2 Then Return Nothing
            Dim mh = d(d.Length - 2), mw = d(d.Length - 1)
            If mh <= 0 OrElse mw <= 0 Then Return Nothing

            ' Wie viele Masken liegen vor? Das Modell mit drei Koernungen liefert vier Kanaele: der
            ' erste ist seine Einzelantwort, danach kommen fein, mittel, grob. Ein aelteres Modell
            ' liefert nur einen. Beides muss gehen, sonst waere ein Modelltausch ein Absturz.
            Dim kanaele = If(d.Length >= 3, d(d.Length - 3), 1)
            Dim gewaehlt = If(kanaele >= 4, 1 + Math.Max(0, Math.Min(2, koernung)), kanaele - 1)
            gewaehlt = Math.Max(0, Math.Min(kanaele - 1, gewaehlt))

            ' Nur der Teil, in dem wirklich Bild lag - der Rest der 1024er Flaeche ist Auffuellung.
            Dim gb = Math.Max(1, CInt(Math.Round(einbettung.SourceWidth * einbettung.Massstab)))
            Dim gh = Math.Max(1, CInt(Math.Round(einbettung.SourceHeight * einbettung.Massstab)))
            gb = Math.Min(gb, mw) : gh = Math.Min(gh, mh)

            ' Die Modellausgabe ist ein ROHWERT, kein Deckungsgrad: positiv heisst "gehoert dazu",
            ' die Null ist die Grenze. Wie steil man daraus Deckung macht, entscheidet alles.
            '
            ' Zu flach, und weit entfernte Stellen mit leicht negativem Wert bekommen noch 10 bis 30
            ' Prozent Deckung - dann liegt ein Schleier ueber dem halben Bild, das umschliessende
            ' Rechteck wird riesig, und "Abziehen" scheint nichts zu bewirken, weil ueberall schon
            ' etwas steht. Genau so war es mit dem Faktor 0,5 (geteilt durch 2).
            '
            ' Steil, und darunter ein Schwellwert, der den Rest sauber auf null zieht: die Kante
            ' bleibt weich genug fuer Haare und Zweige, das Feld daneben ist wirklich leer.
            ' Die Steilheit laeuft von sehr weich bis sehr knapp. GEMESSEN an einem Kreis mit harter
            ' Kante: bei der frueheren Zuordnung war der Uebergang in der Reglermitte noch 27
            ' Bildpunkte breit - das ist der helle Saum, der bei einer Himmelsauswahl um jedes Objekt
            ' steht und den man am fertigen Bild als Rand sieht. Die Mitte des Reglers ist der
            ' Wert, der sich an echten Fotos als brauchbarster Ausgangspunkt gezeigt hat.
            Dim k = Math.Max(0.0, Math.Min(100.0, kantePct))
            Dim Steilheit = CSng(1.0 + k / 100.0 * 90.0)
            ' Der Umfang verschiebt die Grenze. Ein Rohwert von etwa 1 entspricht dem Uebergang
            ' selbst - mehr als das waere kein Feinschliff mehr, sondern ein anderes Objekt.
            Dim Verschiebung = CSng(Math.Max(-100.0, Math.Min(100.0, umfangPct)) / 100.0 * 12.0)
            Const Mindestdeckung As Single = 0.06F

            ' ZUERST vergroessern, DANN die Kennlinie - nicht umgekehrt.
            '
            ' Das Modell antwortet auf einem groben Raster: bei einem Bild von 1900 Punkten Breite
            ' ist ein Rasterfeld gut sieben Bildpunkte breit. Wer dort schon entscheidet "gehoert
            ' dazu oder nicht", legt die Grenze auf dieses Raster fest, und das Vergroessern danach
            ' verteilt die Stufe nur weich. Am Bild sieht man das als gleichmaessig breiten Saum um
            ' alles herum - bei einer Himmelsauswahl laeuft er als weisser Rand um jedes Schaf und
            ' am ganzen Grasrand entlang.
            '
            ' Die ROHWERTE lassen sich dagegen zwischen den Rasterfeldern sauber interpolieren: der
            ' Nulldurchgang liegt dann dort, wo die Grenze wirklich verlaeuft, und nicht auf dem
            ' naechsten Rasterpunkt. Deshalb wird je Bildpunkt zwischen vier Rohwerten gemittelt und
            ' erst danach entschieden.
            Dim quelle = masken.Buffer.ToArray()
            Dim versatz = quelle.Length - kanaele * mw * mh + gewaehlt * mw * mh
            Dim zielB = einbettung.SourceWidth, zielH = einbettung.SourceHeight
            If zielB <= 0 OrElse zielH <= 0 Then Return Nothing

            Dim gross = New SKBitmap(New SKImageInfo(zielB, zielH, SKColorType.Alpha8, SKAlphaType.Premul))
            Dim puffer(zielB * zielH - 1) As Byte
            ' Umrechnung Bildpunkt -> Rasterfeld. Das halbe Feld Versatz sorgt dafuer, dass die
            ' Rohwerte in der MITTE ihres Feldes sitzen und nicht an dessen Ecke.
            Dim sx = gb / CDbl(zielB), sy = gh / CDbl(zielH)
            For y = 0 To zielH - 1
                Dim fy = (y + 0.5) * sy - 0.5
                Dim y0 = CInt(Math.Floor(fy))
                Dim ty = CSng(fy - y0)
                Dim ya = Math.Max(0, Math.Min(gh - 1, y0))
                Dim yb = Math.Max(0, Math.Min(gh - 1, y0 + 1))
                Dim zeileA = versatz + ya * mw
                Dim zeileB = versatz + yb * mw
                Dim zielZeile = y * zielB
                For x = 0 To zielB - 1
                    Dim fx = (x + 0.5) * sx - 0.5
                    Dim x0 = CInt(Math.Floor(fx))
                    Dim tx = CSng(fx - x0)
                    Dim xa = Math.Max(0, Math.Min(gb - 1, x0))
                    Dim xb = Math.Max(0, Math.Min(gb - 1, x0 + 1))
                    Dim oben = quelle(zeileA + xa) * (1.0F - tx) + quelle(zeileA + xb) * tx
                    Dim unten = quelle(zeileB + xa) * (1.0F - tx) + quelle(zeileB + xb) * tx
                    Dim roh = oben * (1.0F - ty) + unten * ty

                    Dim v = (roh + Verschiebung) * Steilheit
                    Dim sWert = 1.0F / (1.0F + CSng(Math.Exp(-v)))
                    If sWert < Mindestdeckung Then sWert = 0.0F
                    puffer(zielZeile + x) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(sWert * 255.0F)))))
                Next
            Next
            Runtime.InteropServices.Marshal.Copy(puffer, 0, gross.GetPixels(), puffer.Length)
            Return gross
        End Function

    End Class

End Namespace
