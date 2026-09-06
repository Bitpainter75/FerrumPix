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
        Public Shared ReadOnly Property Available As Boolean
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
        Public Shared Function Kodiere(image As SKBitmap) As Einbettung
            If image Is Nothing OrElse image.Width <= 0 OrElse image.Height <= 0 Then Return Nothing
            Dim session = AiModelService.SessionFor(EncoderFile)
            If session Is Nothing Then Return Nothing

            Dim scale = ModelEdge / CDbl(Math.Max(image.Width, image.Height))
            Dim nb = Math.Max(1, CInt(Math.Round(image.Width * scale)))
            Dim nh = Math.Max(1, CInt(Math.Round(image.Height * scale)))

            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, ModelEdge, ModelEdge})
            Using small = New SKBitmap(nb, nh, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                If Not image.ScalePixels(small, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                ' Die Normierung stammt aus dem Training des Modells und darf nicht geraten werden.
                Dim average = New Single() {123.675F, 116.28F, 103.53F}
                Dim spread = New Single() {58.395F, 57.12F, 57.375F}
                Dim target = tensor.Buffer.Span
                Dim layer = ModelEdge * ModelEdge
                For y = 0 To nh - 1
                    For x = 0 To nb - 1
                        Dim p = small.GetPixel(x, y)
                        Dim i = y * ModelEdge + x
                        target(i) = (p.Red - average(0)) / spread(0)
                        target(layer + i) = (p.Green - average(1)) / spread(1)
                        target(layer * 2 + i) = (p.Blue - average(2)) / spread(2)
                    Next
                Next
            End Using

            Try
                Dim input = New List(Of NamedOnnxValue) From {
                    NamedOnnxValue.CreateFromTensor("image", tensor)}
                Using result = session.Run(input)
                    Dim output = TryCast(result.First().Value, DenseTensor(Of Single))
                    If output Is Nothing Then Return Nothing
                    ' Der Tensor gehoert der Ergebnisliste und stirbt mit ihr - also kopieren.
                    Dim kopie = New DenseTensor(Of Single)(output.Dimensions.ToArray())
                    output.Buffer.Span.CopyTo(kopie.Buffer.Span)
                    Return New Einbettung With {.Values = kopie, .Massstab = scale,
                                                .SourceWidth = image.Width, .SourceHeight = image.Height}
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogAlways("MotivMaske", "Kodierer: " & ex.Message)
                Return Nothing
            End Try
        End Function

        ''' <summary>Ein angeklickter Punkt. <paramref name="Dazu"/> False heisst: diese Stelle
        ''' gehoert AUSDRUECKLICH nicht dazu - so schneidet man eine zu gross geratene Maske wieder
        ''' zurecht, ohne von vorn anzufangen.</summary>
        Public Structure Point
            Public XPixel As Double
            Public YPixel As Double
            Public Dazu As Boolean
            Public Sub New(x As Double, y As Double, dazuGehoerig As Boolean)
                XPixel = x
                YPixel = y
                Dazu = dazuGehoerig
            End Sub
        End Structure

        ''' <summary>Groesste einstellbare Breite des weichen Uebergangs, in Bildpunkten der
        ''' Maske.</summary>
        Public Const MaxEdgePixels As Double = 20.0

        ''' <summary>Wie weit die Maske hoechstens wachsen oder schrumpfen darf, in Bildpunkten.</summary>
        Public Const MaxExtentPixels As Double = 30.0

        ''' <summary>Die Vorgabe fuer die Kante: ein sichtbarer, aber schmaler Verlauf.</summary>
        Public Const DefaultEdgePixels As Double = 3.0

        ''' <summary>Die Vorgabe fuer den Umfang. Modelle dieser Art schneiden gern eine Haaresbreite
        ''' INNERHALB des Objekts; drei Bildpunkte holen das zurueck, ohne merklich Umgebung
        ''' mitzunehmen.</summary>
        Public Const DefaultExtentPixels As Double = 3.0

        ''' <summary>Maske zu den angeklickten Punkten, als Alpha8-Bild in der Groesse des
        ''' Quellbildes. Nothing bei jedem Fehlschlag - eine halbe Maske waere schlimmer als keine.</summary>
        ''' <param name="edgePixels">Breite des weichen Uebergangs in BILDPUNKTEN, 0 bis
        ''' <see cref="MaxEdgePixels"/>. Der Verlauf laeuft nur NACH AUSSEN: innen bleibt volle
        ''' Deckung. Null heisst harte Kante.
        '''
        ''' Die Zahl ist der Uebergang, nicht ein Reglerprozent - das ist der Unterschied zur
        ''' frueheren Fassung. Dort stand eine Steilheit im Rohwert des Modells, und die Breite in
        ''' Bildpunkten fiel hyperbolisch: gemessen an einem Foto von 4800 Punkten Breite waren es
        ''' bei Reglerwert 0 rund 1685 Bildpunkte, bei 10 noch 332, bei 20 dann 21 und ab 30 unter
        ''' zehn. Der ganze sichtbare Bereich lag in den ersten zwanzig Prozent des Wegs, die
        ''' Vorgabe stand mit 4,3 Punkten weit dahinter.</param>
        ''' <param name="extentPixels">Wie weit die Maske um ihre Kante herum waechst, in
        ''' BILDPUNKTEN, negativ schrumpft sie.
        '''
        ''' Auch das war vorher eine Verschiebung im Rohwert und damit vom Motiv abhaengig. Am
        ''' selben Foto gemessen: bei -30 war die Maske LEER, bei 0 belegte sie 9,9 von 23,0
        ''' Millionen Punkten, bei 25 dann 12,1, bei 40 schon 19,8 und ab 60 das GANZE Bild. Zwei
        ''' Drittel des Reglerwegs waren also entweder nichts oder alles.</param>
        ''' <param name="grain">Welche der drei Koernungen: 0 = fein (ein Teil), 1 = mittel (ein
        ''' Unterobjekt), 2 = grob (das ganze Objekt). Ein Klick ist mehrdeutig - meint man die
        ''' Jacke, die Person oder die Gruppe? Das Modell beantwortet alle drei auf einmal, und der
        ''' Nutzer waehlt aus, statt neu zu klicken.</param>
        Public Shared Function MaskFor(einbettung As Einbettung, points As IList(Of Point),
                                         Optional edgePixels As Double = DefaultEdgePixels,
                                         Optional extentPixels As Double = DefaultExtentPixels,
                                         Optional grain As Integer = 2) As SKBitmap
            If einbettung Is Nothing OrElse einbettung.Values Is Nothing Then Return Nothing
            If points Is Nothing OrElse points.Count = 0 Then Return Nothing
            Dim session = AiModelService.SessionFor(DecoderFile)
            If session Is Nothing Then Return Nothing

            ' Das Modell erwartet die Punkte im MODELL-Massstab, nicht in Bildpixeln.
            Dim n = points.Count
            Dim coords = New DenseTensor(Of Single)(New Integer() {1, n + 1, 2})
            Dim labels = New DenseTensor(Of Single)(New Integer() {1, n + 1})
            For i = 0 To n - 1
                coords(0, i, 0) = CSng(points(i).XPixel * einbettung.Massstab)
                coords(0, i, 1) = CSng(points(i).YPixel * einbettung.Massstab)
                labels(0, i) = If(points(i).Dazu, 1.0F, 0.0F)
            Next
            ' Der Abschlusspunkt mit der Marke -1 gehoert zum Aufbau des Modells: ohne ihn rechnet
            ' es mit einem Rahmen statt mit Punkten.
            coords(0, n, 0) = 0.0F
            coords(0, n, 1) = 0.0F
            labels(0, n) = -1.0F

            Try
                Dim input = New List(Of NamedOnnxValue) From {
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
                Using result = session.Run(input)
                    Dim masks = TryCast(result.First(Function(r) r.Name = "masks").Value, DenseTensor(Of Single))
                    If masks Is Nothing Then Return Nothing
                    Return AsAlphaImage(masks, einbettung, edgePixels, extentPixels, grain, points)
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
        Private Shared Function AsAlphaImage(masks As DenseTensor(Of Single), einbettung As Einbettung,
                                             edgePixels As Double, extentPixels As Double,
                                             grain As Integer, points As IList(Of Point)) As SKBitmap
            ' Als Feld statt als Span - siehe unten, VB kann einen Span nicht indizieren.
            Dim d = masks.Dimensions.ToArray()
            If d.Length < 2 Then Return Nothing
            Dim mh = d(d.Length - 2), mw = d(d.Length - 1)
            If mh <= 0 OrElse mw <= 0 Then Return Nothing

            ' Wie viele Masken liegen vor? Das Modell mit drei Koernungen liefert vier Kanaele: der
            ' erste ist seine Einzelantwort, danach kommen fein, mittel, grob. Ein aelteres Modell
            ' liefert nur einen. Beides muss gehen, sonst waere ein Modelltausch ein Absturz.
            Dim kanaele = If(d.Length >= 3, d(d.Length - 3), 1)
            Dim gewaehlt = If(kanaele >= 4, 1 + Math.Max(0, Math.Min(2, grain)), kanaele - 1)
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
            ' BEIDE REGLER RECHNEN IN BILDPUNKTEN, nicht im Rohwert des Modells. Der Rohwert sagt
            ' nur "wie sicher gehoert das dazu"; wie weit eine Aenderung daran die Kante verschiebt,
            ' haengt am Motiv. Genau das war der Fehler der frueheren Fassung, und es ist gemessen:
            '
            '   Kante (Uebergang in Bildpunkten, Foto 4800 Punkte breit)
            '     Regler 0 -> 1685, 5 -> 872, 10 -> 332, 20 -> 21, 30 -> 9,8, 50 -> 4,3, 100 -> 2,0
            '   Umfang (belegte Flaeche desselben Fotos, 23,0 Mio Punkte insgesamt)
            '     -30 -> LEER, -10 -> 0,4 Mio, 0 -> 9,9, 25 -> 12,1, 40 -> 19,8, 60 -> ALLES
            '
            ' Der brauchbare Teil lag also bei der Kante in den ersten zwanzig Reglerprozent und
            ' beim Umfang zwischen -10 und +30; ausserhalb war die Maske leer oder das ganze Bild.
            '
            ' Gerechnet wird deshalb mit einem ECHTEN Abstand: erst entscheidet der Rohwert nur noch
            ' "innen oder aussen", danach laeuft eine Abstandsrechnung ueber die fertige Flaeche und
            ' sagt je Bildpunkt, wie weit er von der Grenze weg ist. Erst darauf wirken Umfang und
            ' Kante, beide in Bildpunkten.
            '
            ' Der naheliegende kuerzere Weg - Rohwert geteilt durch sein oertliches Gefaelle - ist
            ' ausprobiert und gemessen VERWORFEN: er gilt nur unmittelbar an der Kante. An einem
            ' gezeichneten Kreis auf einfarbigem Grund, wo das Gefaelle an der Kante steil und
            ' daneben fast null ist, ergab er fuer drei Bildpunkte Umfang ein Flaechenwachstum von
            ' 56 Prozent und ab zehn Punkten lief die Maske ueber das ganze Bild.
            Dim FeatherPixels = CSng(Math.Max(0.0, Math.Min(MaxEdgePixels, edgePixels)))
            Dim GrowPixels As Single = CSng(Math.Max(-MaxExtentPixels, Math.Min(MaxExtentPixels, extentPixels)))

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
            Dim source = masks.Buffer.ToArray()
            Dim offset = source.Length - kanaele * mw * mh + gewaehlt * mw * mh
            Dim targetB = einbettung.SourceWidth, zielH = einbettung.SourceHeight
            If targetB <= 0 OrElse zielH <= 0 Then Return Nothing

            Dim large = New SKBitmap(New SKImageInfo(targetB, zielH, SKColorType.Alpha8, SKAlphaType.Premul))
            Dim buffer(targetB * zielH - 1) As Byte
            ' Umrechnung Bildpunkt -> Rasterfeld. Das halbe Feld Versatz sorgt dafuer, dass die
            ' Rohwerte in der MITTE ihres Feldes sitzen und nicht an dessen Ecke.
            Dim sx = gb / CDbl(targetB), sy = gh / CDbl(zielH)

            ' SCHRITT 1: innen oder aussen. Der Rohwert wird dafuer weiterhin bilinear zwischen den
            ' Rasterfeldern interpoliert - so liegt die Grenze dort, wo sie wirklich verlaeuft, und
            ' nicht auf dem naechsten Rasterpunkt.
            Dim inside(targetB * zielH - 1) As Byte
            For y = 0 To zielH - 1
                Dim fy = (y + 0.5) * sy - 0.5
                Dim y0 = CInt(Math.Floor(fy))
                Dim ty = CSng(fy - y0)
                Dim ya = Math.Max(0, Math.Min(gh - 1, y0))
                Dim yb = Math.Max(0, Math.Min(gh - 1, y0 + 1))
                Dim rowA = offset + ya * mw
                Dim rowB = offset + yb * mw
                Dim targetRow = y * targetB
                For x = 0 To targetB - 1
                    Dim fx = (x + 0.5) * sx - 0.5
                    Dim x0 = CInt(Math.Floor(fx))
                    Dim tx = CSng(fx - x0)
                    Dim xa = Math.Max(0, Math.Min(gb - 1, x0))
                    Dim xb = Math.Max(0, Math.Min(gb - 1, x0 + 1))
                    Dim top = source(rowA + xa) * (1.0F - tx) + source(rowA + xb) * tx
                    Dim bottom = source(rowB + xa) * (1.0F - tx) + source(rowB + xb) * tx
                    If top * (1.0F - ty) + bottom * ty >= 0.0F Then inside(targetRow + x) = 1
                Next
            Next

            ' SCHRITT 1b: NUR DIE FLAECHE, IN DIE GEKLICKT WURDE.
            '
            ' Auf einer strukturlosen Flaeche - Himmel, eine glatte Wand, ein gezeichneter
            ' Hintergrund - liegt der Rohwert des Modells nahe null, und sein Vorzeichen kippt dann
            ' von Punkt zu Punkt. Solche Sprenkel sind fuer sich harmlos, fuer eine Abstandsrechnung
            ' aber nicht: jeder von ihnen ist eine eigene Grenze, der weiche Verlauf zieht um jeden
            ' einen Hof, und die Hoefe schliessen sich zu einem Schleier. GEMESSEN an einem
            ' gezeichneten Kreis mit Radius 150: die Maske reichte so bis 206 Punkte hinaus.
            '
            ' Deshalb bleibt stehen, was mit einem angeklickten Punkt zusammenhaengt. Das ist auch
            ' die Erwartung an das Werkzeug: geklickt wird ein Gegenstand, nicht Streusel ueber dem
            ' ganzen Bild. Findet sich kein Anker - etwa weil der Klick knapp daneben liegt -,
            ' bleibt alles stehen; lieber zu viel als gar nichts.
            If points IsNot Nothing AndAlso points.Count > 0 Then
                Dim keep(inside.Length - 1) As Byte
                Dim stack As New Stack(Of Integer)()
                For Each p In points
                    If Not p.Dazu Then Continue For
                    Dim px = CInt(Math.Round(p.XPixel)), py = CInt(Math.Round(p.YPixel))
                    If px < 0 OrElse py < 0 OrElse px >= targetB OrElse py >= zielH Then Continue For
                    Dim idx = py * targetB + px
                    If inside(idx) = 0 OrElse keep(idx) <> 0 Then Continue For
                    keep(idx) = 1
                    stack.Push(idx)
                Next
                If stack.Count > 0 Then
                    While stack.Count > 0
                        Dim idx = stack.Pop()
                        Dim ix = idx Mod targetB, iy = idx \ targetB
                        If ix > 0 AndAlso inside(idx - 1) <> 0 AndAlso keep(idx - 1) = 0 Then
                            keep(idx - 1) = 1 : stack.Push(idx - 1)
                        End If
                        If ix < targetB - 1 AndAlso inside(idx + 1) <> 0 AndAlso keep(idx + 1) = 0 Then
                            keep(idx + 1) = 1 : stack.Push(idx + 1)
                        End If
                        If iy > 0 AndAlso inside(idx - targetB) <> 0 AndAlso keep(idx - targetB) = 0 Then
                            keep(idx - targetB) = 1 : stack.Push(idx - targetB)
                        End If
                        If iy < zielH - 1 AndAlso inside(idx + targetB) <> 0 AndAlso keep(idx + targetB) = 0 Then
                            keep(idx + targetB) = 1 : stack.Push(idx + targetB)
                        End If
                    End While
                    inside = keep
                End If
            End If

            ' SCHRITT 1c: EINZELNE PUNKTE ZAEHLEN NICHT ALS FLAECHE.
            '
            ' Auf einer strukturlosen Flaeche liegt der Rohwert nahe null und sein Vorzeichen kippt
            ' von Punkt zu Punkt. Solche Sprenkel haengen ueber Ecken oft noch mit dem Motiv
            ' zusammen, ueberstehen also den Schritt davor - und der weiche Verlauf zieht um jeden
            ' einen Hof, bis sich die Hoefe zu einem Schleier schliessen. GEMESSEN am gezeichneten
            ' Kreis mit Radius 150: mit harter Kante endete die Maske bei 151 Punkten, mit drei
            ' Punkten Verlauf reichte sie bis 205.
            '
            ' Gemessen wird der Abstand deshalb nicht zum Rand der rohen Flaeche, sondern zu ihrem
            ' KERN: nur was ringsum dazugehoert, zaehlt als Anker. Ein einzelner Punkt hat keinen
            ' Kern und traegt damit keinen Hof mehr. Der Kern liegt einen Punkt weiter innen als der
            ' Rand, und genau dieser eine Punkt kommt beim Umfang wieder dazu.
            Dim core(inside.Length - 1) As Byte
            For y = 0 To zielH - 1
                Dim row = y * targetB
                For x = 0 To targetB - 1
                    If inside(row + x) = 0 Then Continue For
                    If x = 0 OrElse y = 0 OrElse x = targetB - 1 OrElse y = zielH - 1 Then
                        core(row + x) = 1
                        Continue For
                    End If
                    If inside(row + x - 1) <> 0 AndAlso inside(row + x + 1) <> 0 AndAlso
                       inside(row - targetB + x) <> 0 AndAlso inside(row + targetB + x) <> 0 Then
                        core(row + x) = 1
                    End If
                Next
            Next
            ' Faellt dabei alles weg - eine Maske aus lauter Einzelpunkten -, bleibt es beim
            ' Rohbefund; eine leere Maske waere schlechter als eine unsaubere.
            Dim coreCount = 0
            For i = 0 To core.Length - 1
                coreCount += core(i)
            Next
            If coreCount > 0 Then
                inside = core
                GrowPixels += 1.0F
            End If

            ' SCHRITT 2: der Abstand zur Grenze, in Bildpunkten.
            '
            ' Zwei Durchlaeufe ueber das Bild, einer vorwaerts, einer rueckwaerts, mit den Gewichten
            ' 1 fuer den geraden und Wurzel zwei fuer den schraegen Nachbarn. Das ist die uebliche
            ' Naeherung; sie liegt ein paar Prozent ueber dem wahren Abstand und kostet zwei Laeufe
            ' statt einer Suche je Punkt.
            Dim reach = CSng(MaxEdgePixels + MaxExtentPixels) + 2.0F
            Dim dist(targetB * zielH - 1) As Single
            For i = 0 To dist.Length - 1
                dist(i) = reach
            Next
            ' Die Grenze selbst: jeder Punkt, der einen Nachbarn der anderen Art hat, liegt auf ihr.
            For y = 0 To zielH - 1
                Dim row = y * targetB
                For x = 0 To targetB - 1
                    Dim self = inside(row + x)
                    Dim onBorder = (x > 0 AndAlso inside(row + x - 1) <> self) OrElse
                                   (x < targetB - 1 AndAlso inside(row + x + 1) <> self) OrElse
                                   (y > 0 AndAlso inside(row - targetB + x) <> self) OrElse
                                   (y < zielH - 1 AndAlso inside(row + targetB + x) <> self)
                    If onBorder Then dist(row + x) = 0.0F
                Next
            Next
            Const Diagonal As Single = 1.41421356F
            For y = 0 To zielH - 1
                Dim row = y * targetB
                Dim above = row - targetB
                For x = 0 To targetB - 1
                    Dim best = dist(row + x)
                    If best <= 0.0F Then Continue For
                    If x > 0 Then best = Math.Min(best, dist(row + x - 1) + 1.0F)
                    If y > 0 Then
                        best = Math.Min(best, dist(above + x) + 1.0F)
                        If x > 0 Then best = Math.Min(best, dist(above + x - 1) + Diagonal)
                        If x < targetB - 1 Then best = Math.Min(best, dist(above + x + 1) + Diagonal)
                    End If
                    dist(row + x) = best
                Next
            Next
            For y = zielH - 1 To 0 Step -1
                Dim row = y * targetB
                Dim below = row + targetB
                For x = targetB - 1 To 0 Step -1
                    Dim best = dist(row + x)
                    If best <= 0.0F Then Continue For
                    If x < targetB - 1 Then best = Math.Min(best, dist(row + x + 1) + 1.0F)
                    If y < zielH - 1 Then
                        best = Math.Min(best, dist(below + x) + 1.0F)
                        If x < targetB - 1 Then best = Math.Min(best, dist(below + x + 1) + Diagonal)
                        If x > 0 Then best = Math.Min(best, dist(below + x - 1) + Diagonal)
                    End If
                    dist(row + x) = best
                Next
            Next

            ' SCHRITT 3: Umfang und Kante, beide in Bildpunkten.
            '
            ' Der Verlauf laeuft NUR NACH AUSSEN: innen bleibt volle Deckung, nach aussen faellt sie
            ' ueber die eingestellte Breite auf null. Frueher sass die Kurve mittig auf der Grenze
            ' und fraß dieselbe Strecke nach innen weg - wer eine weiche Kante wollte, verlor dafuer
            ' Motiv.
            For i = 0 To buffer.Length - 1
                ' Vorzeichen aus der Zugehoerigkeit, Betrag aus der Abstandsrechnung. Der halbe
                ' Punkt Versatz ruecke die Null auf die Kante zwischen den Bildpunkten statt auf den
                ' Bildpunkt selbst.
                Dim signed = If(inside(i) <> 0, dist(i) + 0.5F, -(dist(i) + 0.5F)) + GrowPixels
                Dim sValue As Single
                If signed >= 0.0F Then
                    sValue = 1.0F
                ElseIf FeatherPixels <= 0.0F Then
                    sValue = 0.0F
                Else
                    Dim t = 1.0F + signed / FeatherPixels
                    If t <= 0.0F Then
                        sValue = 0.0F
                    Else
                        ' Sanfter Ein- und Ausstieg statt einer geraden Rampe: an beiden Enden ist
                        ' die Ableitung null, der Uebergang setzt also nicht mit einem Knick an.
                        sValue = t * t * (3.0F - 2.0F * t)
                    End If
                End If
                buffer(i) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(sValue * 255.0F)))))
            Next
            Runtime.InteropServices.Marshal.Copy(buffer, 0, large.GetPixels(), buffer.Length)
            Return large
        End Function

    End Class

End Namespace
