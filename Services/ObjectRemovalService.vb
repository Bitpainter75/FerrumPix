Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ''' <summary>Einen markierten Bereich verschwinden lassen und plausibel wieder auffuellen.
    '''
    ''' Der Unterschied zum Reparaturpinsel ist nicht die Groesse, sondern die Art: der Pinsel
    ''' UEBERTRAEGT Textur von einer anderen Stelle, das Modell SETZT die Struktur fort. Ein Horizont,
    ''' der durch die Luecke laeuft, wird weitergezogen statt zugekleistert - genau der Fall, an dem
    ''' jede Patch-Variante scheitert, weil es an der Ausleihstelle keinen passenden Horizont gibt.
    '''
    ''' Das Modell nimmt FREIE Groessen. Es bekommt trotzdem nicht das Foto, sondern einen
    ''' Ausschnitt um die Luecke herum, mit genug Umgebung, dass es etwas zum Fortsetzen hat - ein
    ''' 40-Megapixel-Bild auf Modellgroesse zu quetschen wuerde jede Struktur vernichten, die es
    ''' fortsetzen soll. Wie gross gerechnet wird, entscheiden <see cref="MaxEdge"/> und
    ''' <see cref="ToModelSize"/>; die Groesse ist dabei kein Nebenschauplatz, sondern der groesste
    ''' Zeitposten des ganzen Vorgangs.
    '''
    ''' Zurueckgeschrieben wird NUR innerhalb der Maske, mit weichem Rand. Ausserhalb bleibt das
    ''' Original Pixel fuer Pixel stehen: das Modell gibt seinen ganzen Ausschnitt neu aus, und das
    ''' unbesehen zu uebernehmen hiesse, das halbe Foto durch eine verkleinerte Fassung seiner
    ''' selbst zu ersetzen.</summary>
    Public NotInheritable Class ObjectRemovalService

        Private Sub New()
        End Sub

        Public Const ModelFile As String = "lama"

        ''' <summary>Groesste Kante, mit der gerechnet wird.
        '''
        ''' Stand frueher auf 768 und ist auf 1024 angehoben, weil das NICHTS kostet: die
        ''' Rechengroesse wird ohnehin auf die naechste Zweierpotenz aufgefuellt (siehe
        ''' <see cref="ToModelSize"/>), und alles zwischen 513 und 1024 landet in derselben Kachel.
        ''' Bei 768 wurde also auf 1024 gerechnet und ein Drittel der Flaeche mit fortgeschriebenem
        ''' Rand verschenkt. Mit 1024 steckt dort Bildinhalt statt Saum - mehr Aufloesung fuer die
        ''' Fuellung, bei gleicher Rechenzeit.</summary>
        Public Const MaxEdge As Integer = 1024

        ''' <summary>Kleinste Rechengroesse. Darunter lohnt das Aufrunden nicht, und dem Modell
        ''' bleibt zu wenig Umgebung.</summary>
        Private Const SmallestModelSize As Integer = 128

        ''' <summary>Wie viel Umgebung mindestens um die Luecke herum mitgegeben wird, als Vielfaches
        ''' ihrer laengsten Kante. Ohne Umgebung hat das Modell nichts, was es fortsetzen koennte.</summary>
        Private Const SurroundingFactor As Double = 1.6

        ''' <summary>Und mindestens so viele Bildpunkte, damit auch eine winzige Luecke noch
        ''' Zusammenhang bekommt.</summary>
        Private Const SurroundingMinimum As Integer = 96

        ''' <summary>Ab welcher Deckung ein Bildpunkt als LUECKE gilt.
        '''
        ''' Nicht "groesser als null". Eine Maske aus der Objekterkennung hat weiche Raender und oft
        ''' einen schwachen Schleier weit ausserhalb des Objekts. Zaehlt jeder Hauch als Luecke, dann
        ''' ist das umschliessende Rechteck plotzlich das halbe Foto, der Ausschnitt entsprechend
        ''' gross, und das Modell soll auf 512 Pixeln das halbe Bild neu erfinden. Heraus kommt eine
        ''' verwaschene, verzogene Fassung dessen, was da war - der Fehler, der wie ein Geometrie-
        ''' fehler aussieht und keiner ist.</summary>
        Private Const GapThresholdFixed As Byte = 96

        ''' <summary>Die Schwelle, ab der ein Punkt als Luecke gilt - abgeleitet aus der Maske
        ''' SELBST, nicht fest.
        '''
        ''' Eine feste Schwelle geht in beide Richtungen schief. Zu niedrig, und der schwache
        ''' Schleier einer Objektmaske macht das halbe Foto zur Luecke. Zu hoch, und eine Maske, die
        ''' nirgends ueber 90 kommt, ist ploetzlich leer - dann bekommt das Modell nichts zu fuellen
        ''' und gibt eine unscharfe Fassung dessen zurueck, was da war. Beides ist passiert.
        '''
        ''' Die Haelfte des HOECHSTEN vorkommenden Wertes trifft beides: bei einer vollen Maske sind
        ''' das rund 128, bei einer schwachen entsprechend weniger. Nach unten und oben begrenzt,
        ''' damit weder ein Rauschen noch eine fast leere Maske die Schwelle bestimmt.</summary>
        Private Shared Function ThresholdFor(mask As SKBitmap) As Byte
            If mask Is Nothing Then Return GapThresholdFixed
            Dim highest As Integer = 0
            ' Grob abtasten: fuer den Hoechstwert reicht jeder vierte Punkt, und das spart bei einem
            ' 40-Megapixel-Bild einen kompletten Durchlauf.
            For y = 0 To mask.Height - 1 Step 2
                For x = 0 To mask.Width - 1 Step 2
                    Dim a = mask.GetPixel(x, y).Alpha
                    If a > highest Then highest = a
                    If highest >= 255 Then Exit For
                Next
                If highest >= 255 Then Exit For
            Next
            If highest <= 0 Then Return GapThresholdFixed
            Return CByte(Math.Max(24, Math.Min(160, highest \ 2)))
        End Function

        ''' <summary>Um wie viel die Maske VOR dem Fuellen waechst, als Anteil der laengsten
        ''' Lueckenkante.
        '''
        ''' Eine Maske aus der Objekterkennung klebt genau am Objekt. Genau dort liegen aber die
        ''' Bildpunkte, die zur Haelfte zum Objekt gehoeren: die Kantenglaettung, der Bewegungsrand,
        ''' bei einem Tier die einzelnen Haare. Fuellt man nur INNERHALB dieser Maske, bleibt rundum
        ''' ein Saum aus Objektpunkten stehen - und der hat noch die Form des Objekts. Das Ergebnis
        ''' sieht aus, als waere gar nichts passiert, nur weicher.
        '''
        ''' Deshalb waechst die Maske vorher. Mit einer grosszuegig gemalten Maske passiert das von
        ''' selbst - genau daran hat sich der Unterschied gezeigt.</summary>
        Private Const GrowthShare As Double = 0.02

        ''' <summary>Und mindestens so viele Bildpunkte, damit auch eine kleine Luecke ihren Saum
        ''' verliert.</summary>
        Private Const GrowthMinimum As Integer = 4

        ''' <summary>Breite des weichen Randes beim Zurueckschreiben, Ohne ihn zeigt
        ''' sich die Kante der Maske als Naht - die gefuellte Flaeche kommt aus einer Skalierung und
        ''' trifft die Nachbarschaft nie ganz genau.</summary>
        Private Const SeamWidth As Single = 2.5F

        ''' <summary>Was der letzte Durchlauf gerechnet hat, in einem Satz. Wird nach dem Entfernen
        ''' angezeigt.
        '''
        ''' Nicht nur ins Protokoll: eine Ferndiagnose, die voraussetzt, dass jemand erst einen
        ''' Schalter findet und eine Datei heraussucht, ist keine. Die sechs Zahlen entscheiden den
        ''' Fall, und sie gehoeren dorthin, wo man sie ohne Umweg sieht.</summary>
        Public Shared Property LastReport As String = ""

        Public Shared ReadOnly Property Available As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(ModelFile))
            End Get
        End Property

        ''' <summary>Das umschliessende Rechteck aller gesetzten Maskenpunkte, oder ein leeres.</summary>
        Public Shared Function GapRect(mask As SKBitmap) As SKRectI
            If mask Is Nothing OrElse mask.Width <= 0 OrElse mask.Height <= 0 Then Return SKRectI.Empty
            Dim GapThreshold = ThresholdFor(mask)
            Dim l = Integer.MaxValue, t = Integer.MaxValue, r = Integer.MinValue, b = Integer.MinValue
            For y = 0 To mask.Height - 1
                For x = 0 To mask.Width - 1
                    If mask.GetPixel(x, y).Alpha < GapThreshold Then Continue For
                    If x < l Then l = x
                    If x > r Then r = x
                    If y < t Then t = y
                    If y > b Then b = y
                Next
            Next
            If r < l OrElse b < t Then Return SKRectI.Empty
            Return New SKRectI(l, t, r + 1, b + 1)
        End Function

        ''' <summary>Der Ausschnitt, den das Modell zu sehen bekommt: die Luecke plus Umgebung.
        '''
        ''' NICHT quadratisch und NICHT groesser als noetig. Eine erste Fassung erzwang ein Quadrat
        ''' und nahm im Zweifel die kurze Bildkante - bei einem grossen Bild wurde daraus ein
        ''' Ausschnitt von mehreren tausend Punkten, den 512 Pixel nicht mehr aufloesen. Heraus kam
        ''' eine unscharfe Fassung dessen, was da war, statt einer Fuellung.
        '''
        ''' Die Umgebung betraegt das Anderthalbfache der Luecke, mindestens aber 128 Punkte. Sie ist
        ''' der Zusammenhang, aus dem das Modell fortsetzt: ohne sie hat es nichts, mit zu viel davon
        ''' verschwindet die Luecke in der Verkleinerung.</summary>
        Public Shared Function RegionFor(gap As SKRectI, imageWidth As Integer, imageHeight As Integer) As SKRectI
            If gap.Width <= 0 OrElse gap.Height <= 0 Then Return SKRectI.Empty
            If imageWidth <= 0 OrElse imageHeight <= 0 Then Return SKRectI.Empty

            Dim marginX = Math.Max(SurroundingMinimum, CInt(gap.Width * SurroundingFactor))
            Dim marginY = Math.Max(SurroundingMinimum, CInt(gap.Height * SurroundingFactor))
            Dim l = Math.Max(0, gap.Left - marginX)
            Dim t = Math.Max(0, gap.Top - marginY)
            Dim r = Math.Min(imageWidth, gap.Right + marginX)
            Dim b = Math.Min(imageHeight, gap.Bottom + marginY)
            If r <= l OrElse b <= t Then Return SKRectI.Empty
            Return New SKRectI(l, t, r, b)
        End Function

        ''' <summary>Die Luecke fuellen. <paramref name="mask"/> hat Bildgroesse; alles ueber der
        ''' Schwelle gilt als zu fuellen. Zurueck kommt eine KOPIE des Bildes mit gefuellter Luecke,
        ''' oder Nothing bei jedem Fehlschlag.</summary>
        ''' <param name="cancel">Abbruch durch den Nutzer. ANDERS ALS BEIM ENTRAUSCHEN gibt es hier
        ''' keine Kachelgrenze: das Fuellen ist EIN Modelldurchlauf von mehreren Sekunden, und der
        ''' laesst sich nicht anhalten. Geprueft wird davor und danach - wer waehrenddessen abbricht,
        ''' wartet den Durchlauf noch ab, bekommt aber sein Bild unveraendert zurueck. Ehrlicher als
        ''' ein Knopf, der so tut, als koennte er das Modell stoppen.</param>
        Public Shared Function Fill(image As SKBitmap, mask As SKBitmap,
                                    Optional cancel As Threading.CancellationToken = Nothing) As SKBitmap
            If image Is Nothing OrElse mask Is Nothing Then Return Nothing
            If image.Width <= 0 OrElse image.Height <= 0 Then Return Nothing
            If cancel.IsCancellationRequested Then Return Nothing
            Dim session = AiModelService.SessionFor(ModelFile)
            If session Is Nothing Then Return Nothing

            Dim ownMask As SKBitmap = Nothing
            Dim m = mask
            Try
                If mask.Width <> image.Width OrElse mask.Height <> image.Height Then
                    ownMask = New SKBitmap(New SKImageInfo(image.Width, image.Height,
                                                               SKColorType.Alpha8, SKAlphaType.Premul))
                    If Not mask.ScalePixels(ownMask, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                    m = ownMask
                End If

                Dim rawGap = GapRect(m)
                If rawGap.Width <= 0 OrElse rawGap.Height <= 0 Then Return Nothing
                ' Die Maske WACHSEN lassen, bevor irgendetwas anderes passiert - sonst bleibt der
                ' Saum aus halb zum Objekt gehoerenden Punkten stehen.
                Dim growth = Math.Max(GrowthMinimum,
                                        CInt(Math.Round(Math.Max(rawGap.Width, rawGap.Height) * GrowthShare)))
                Dim grown = Grow(m, growth)
                If grown IsNot Nothing Then
                    ownMask?.Dispose()
                    ownMask = grown
                    m = grown
                End If

                Dim gap = GapRect(m)
                If gap.Width <= 0 OrElse gap.Height <= 0 Then Return Nothing
                Dim window = RegionFor(gap, image.Width, image.Height)
                If window.Width <= 0 Then Return Nothing

                ' Das Modell rechnet auf FREIER Groesse - nur ein Vielfaches von 32 muss es sein.
                ' Deshalb wird der Ausschnitt nicht mehr in ein festes Quadrat gequetscht, sondern
                ' nur so weit verkleinert, wie die Obergrenze es verlangt. Genau daran ist die
                ' erste Fassung gescheitert: ein grosses Objekt auf 512 Punkte gestaucht ergab
                ' einen weichen Verlauf statt Hintergrund.
                Dim factor = Math.Min(1.0, MaxEdge / CDbl(Math.Max(window.Width, window.Height)))
                Dim aw = Math.Max(32, CInt(Math.Round(window.Width * factor)))
                Dim ah = Math.Max(32, CInt(Math.Round(window.Height * factor)))
                Dim pw = ToModelSize(aw), ph = ToModelSize(ah)

                Using small = New SKBitmap(aw, ah, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                    Using c = New SKCanvas(small)
                        c.Clear(SKColors.Black)
                        Using img = SKImage.FromBitmap(image)
                            c.DrawImage(img, New SKRect(window.Left, window.Top, window.Right, window.Bottom),
                                        New SKRect(0, 0, aw, ah),
                                        New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), Nothing)
                        End Using
                    End Using

                    Using smallMask = New SKBitmap(New SKImageInfo(aw, ah, SKColorType.Alpha8, SKAlphaType.Premul))
                        Using c = New SKCanvas(smallMask)
                            c.Clear(SKColors.Transparent)
                            Using img = SKImage.FromBitmap(m)
                                c.DrawImage(img, New SKRect(window.Left, window.Top, window.Right, window.Bottom),
                                            New SKRect(0, 0, aw, ah),
                                            New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), Nothing)
                            End Using
                        End Using

                        Dim threshold = ThresholdFor(smallMask)
                        Dim setPixels As Long = 0
                        For y = 0 To ah - 1
                            For x = 0 To aw - 1
                                If smallMask.GetPixel(x, y).Alpha >= threshold Then setPixels += 1
                            Next
                        Next
                        Dim report = $"Bild {image.Width}x{image.Height}, Lücke {gap.Width}x{gap.Height}, " &
                                      $"Ausschnitt {window.Width}x{window.Height}, gerechnet auf {pw}x{ph}, " &
                                      $"Schwelle {threshold}, Maske {setPixels} von {aw * ah} Punkten"
                        LastReport = report
                        DiagnosticLogService.LogAlways("ObjektEntfernen", report)
                        If setPixels <= 0 Then
                            DiagnosticLogService.LogAlways("ObjektEntfernen",
                                "Maske im Modelleingang LEER - es gibt nichts zu fuellen")
                            Return Nothing
                        End If

                        ' Bei eingeschalteter Diagnose: den Ausschnitt und die Maske ablegen, GENAU
                        ' so, wie sie ins Modell gehen. Ob die Umgebung mitgeht oder nur die Luecke
                        ' ankommt, sieht man an zwei Bildern in einer Sekunde - und muss es nicht aus
                        ' Zahlen erschliessen.
                        WriteDiagnosticImages(small, smallMask, threshold)

                        Dim filled = Compute(session, small, smallMask, aw, ah, pw, ph, threshold)
                        If filled Is Nothing Then Return Nothing
                        Using filled
                            ' Wer waehrend des Durchlaufs abgebrochen hat, bekommt sein Bild
                            ' unveraendert zurueck. Das Ergebnis wird verworfen, obwohl es fertig
                            ' ist - genau das hat der Nutzer verlangt.
                            If cancel.IsCancellationRequested Then
                                DiagnosticLogService.LogAlways("ObjektEntfernen", "abgebrochen, Ergebnis verworfen")
                                Return Nothing
                            End If
                            Return InsertInto(image, m, filled, window)
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogAlways("ObjektEntfernen", ex.Message)
                Return Nothing
            Finally
                ownMask?.Dispose()
            End Try
        End Function

        ''' <summary>Die Maske um <paramref name="radius"/> Bildpunkte nach aussen erweitern.
        '''
        ''' Ueber eine Unschaerfe mit anschliessender niedriger Schwelle: was auch nur ein wenig
        ''' Deckung abbekommt, gilt danach als voll gedeckt. Das ist eine Ausdehnung um ungefaehr den
        ''' Radius und kostet nichts, waehrend ein echter Maximumfilter mit dem Radius waechst.</summary>
        Private Shared Function Grow(mask As SKBitmap, radius As Integer) As SKBitmap
            If mask Is Nothing OrElse radius < 1 Then Return Nothing
            Dim threshold = ThresholdFor(mask)
            Try
                Dim target = New SKBitmap(New SKImageInfo(mask.Width, mask.Height,
                                                        SKColorType.Alpha8, SKAlphaType.Premul))
                Using hard = New SKBitmap(New SKImageInfo(mask.Width, mask.Height,
                                                          SKColorType.Alpha8, SKAlphaType.Premul))
                    Dim n = mask.Width * mask.Height
                    Dim buffer(n - 1) As Byte
                    Runtime.InteropServices.Marshal.Copy(mask.GetPixels(), buffer, 0, n)
                    For i = 0 To n - 1
                        buffer(i) = If(buffer(i) >= threshold, CByte(255), CByte(0))
                    Next
                    Runtime.InteropServices.Marshal.Copy(buffer, 0, hard.GetPixels(), n)

                    Using canvas = New SKCanvas(target)
                        canvas.Clear(SKColors.Transparent)
                        Using paint = New SKPaint()
                            paint.ImageFilter = SKImageFilter.CreateBlur(radius * 0.6F, radius * 0.6F)
                            canvas.DrawBitmap(hard, 0, 0, paint)
                        End Using
                    End Using

                    ' Niedrig schwellen: aus dem weichen Rand der Unschaerfe wird wieder eine volle
                    ' Deckung, und die Maske ist um rund einen Radius groesser als vorher.
                    Runtime.InteropServices.Marshal.Copy(target.GetPixels(), buffer, 0, n)
                    For i = 0 To n - 1
                        buffer(i) = If(buffer(i) >= 40, CByte(255), CByte(0))
                    Next
                    Runtime.InteropServices.Marshal.Copy(buffer, 0, target.GetPixels(), n)
                End Using
                Return target
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Auf das naechste Vielfache aufrunden, das das Modell verlangt.</summary>
        ''' <summary>Die Rechengroesse einer Achse: aufgerundet auf die naechste ZWEIERPOTENZ.
        '''
        ''' Das ist keine Kosmetik, sondern der groesste einzelne Zeitposten beim Entfernen. Das
        ''' Modell baut auf Fourier-Faltungen, und deren schnelle Transformation greift nur bei
        ''' Zweierpotenzen. Gemessen an derselben Datei:
        '''
        '''   512 mal 512    1,5 s        576 mal 576    9,4 s
        '''   1024 mal 512   3,2 s        768 mal 576   11,4 s  (was hier frueher stand)
        '''   1024 mal 1024  7,4 s        896 mal 896   18,6 s
        '''
        ''' 1024 mal 1024 ist also bei VIERFACHER Flaeche schneller als 576 mal 576. Wer hier auf
        ''' ein Vielfaches von 32 zurueckgeht, macht das Entfernen um das Drei- bis Siebenfache
        ''' langsamer, ohne dass sich am Bild etwas verbessert.
        '''
        ''' Aufloesung kostet das nichts: <see cref="Compute"/> fuellt den Bereich zwischen dem
        ''' Ausschnitt und der Rechengroesse nicht mit Farbe, sondern schreibt die letzte Zeile und
        ''' Spalte fort, und die Maske bleibt dort null. Aufgerundet wird also der SAUM, nicht das
        ''' Bild - nichts wird gestaucht.</summary>
        Private Shared Function ToModelSize(value As Integer) As Integer
            Dim size = SmallestModelSize
            While size < value
                size *= 2
            End While
            Return size
        End Function

        ''' <summary>Ein Durchlauf des Modells.
        '''
        ''' EIN Eingang mit VIER Kanaelen: die ersten drei tragen das Bild, aus dem die Luecke
        ''' AUSGELOESCHT ist, der vierte die Maske. Das ist die Signatur dieser Modelldatei - die
        ''' Vorgaengerin hatte zwei getrennte Eingaenge und loeschte selbst aus. Wer das verwechselt,
        ''' bekommt ein Bild zurueck, in dem das Objekt noch steht.
        '''
        ''' <paramref name="pw"/> und <paramref name="ph"/> sind auf das Modellraster aufgerundet;
        ''' was ueber aw und ah hinausgeht, wird mit dem Randwert gefuellt und danach
        ''' weggeschnitten.</summary>
        Private Shared Function Compute(session As InferenceSession, small As SKBitmap,
                                       smallMask As SKBitmap, aw As Integer, ah As Integer,
                                       pw As Integer, ph As Integer, threshold As Byte) As SKBitmap
            Dim layer = pw * ph
            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 4, ph, pw})
            Dim z = tensor.Buffer.Span
            For y = 0 To ph - 1
                Dim qy = Math.Min(y, ah - 1)
                For x = 0 To pw - 1
                    Dim qx = Math.Min(x, aw - 1)
                    Dim i = y * pw + x
                    Dim p = small.GetPixel(qx, qy)
                    ' Die Maske ist ein SCHALTER, kein Deckungsgrad. Ausserhalb des belegten Teils
                    ' bleibt sie null - dort soll nichts gefuellt werden.
                    Dim inside = x < aw AndAlso y < ah
                    Dim hole = If(inside AndAlso smallMask.GetPixel(qx, qy).Alpha >= threshold, 1.0F, 0.0F)
                    Dim visible = 1.0F - hole
                    z(i) = p.Red / 255.0F * visible
                    z(layer + i) = p.Green / 255.0F * visible
                    z(layer * 2 + i) = p.Blue / 255.0F * visible
                    z(layer * 3 + i) = hole
                Next
            Next

            Dim name = session.InputMetadata.Keys.First()
            Dim input = New List(Of NamedOnnxValue) From {NamedOnnxValue.CreateFromTensor(name, tensor)}
            Using result = session.Run(input)
                Dim output = TryCast(result.First().Value, DenseTensor(Of Single))
                If output Is Nothing Then Return Nothing
                Dim dims = output.Dimensions.ToArray()
                Dim rh = dims(dims.Length - 2), rw = dims(dims.Length - 1)
                Dim rLayer = rw * rh
                Dim values = output.Buffer.Span
                If values.Length < rLayer * 3 Then Return Nothing
                ' Die Ausgabe steht in 0 bis 1 - anders als bei der Vorgaengerin, die 0 bis 255 gab.
                Dim target = New SKBitmap(aw, ah, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                For y = 0 To ah - 1
                    Dim sy = Math.Min(y, rh - 1)
                    For x = 0 To aw - 1
                        Dim sx = Math.Min(x, rw - 1)
                        Dim i = sy * rw + sx
                        target.SetPixel(x, y, New SKColor(ClampByte(values(i) * 255.0F),
                                                        ClampByte(values(rLayer + i) * 255.0F),
                                                        ClampByte(values(rLayer * 2 + i) * 255.0F), 255))
                    Next
                Next

                ' NACHRECHNEN, ob ueberhaupt etwas passiert ist.
                Dim sum As Long = 0, number As Long = 0
                For y = 0 To ah - 1
                    For x = 0 To aw - 1
                        If smallMask.GetPixel(x, y).Alpha < threshold Then Continue For
                        Dim before = small.GetPixel(x, y)
                        Dim after = target.GetPixel(x, y)
                        sum += Math.Abs(CInt(before.Red) - after.Red) +
                                 Math.Abs(CInt(before.Green) - after.Green) +
                                 Math.Abs(CInt(before.Blue) - after.Blue)
                        number += 1
                    Next
                Next
                Dim average = If(number > 0, sum / CDbl(number * 3), 0.0)
                LastReport &= $", Änderung {average:F1} Stufen"
                DiagnosticLogService.LogAlways("ObjektEntfernen",
                    $"Aenderung in der Luecke: {average:F1} Stufen ueber {number} Punkte")
                If number > 0 AndAlso average < 4.0 Then
                    LastReport &= " - das Modell hat nur wiederholt statt zu füllen"
                    DiagnosticLogService.LogAlways("ObjektEntfernen",
                        "Das Modell hat den Ausschnitt nur WIEDERHOLT statt zu fuellen.")
                End If
                Return target
            End Using
        End Function

        ''' <summary>Legt Ausschnitt und Maske als PNG neben das Protokoll - nur bei
        ''' eingeschalteter Diagnose. Zwei Bilder beantworten die Frage "bekommt das Modell die
        ''' Umgebung?" unmittelbar; aus Zahlen laesst sie sich nur erschliessen.</summary>
        Private Shared Sub WriteDiagnosticImages(small As SKBitmap, smallMask As SKBitmap, threshold As Byte)
            Try
                If Not AppSettingsService.Load().EnableDiagnosticLogging Then Return
                Dim folder = IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FerrumPix", "logs")
                IO.Directory.CreateDirectory(folder)

                Using image = SKImage.FromBitmap(small)
                    Using data = image.Encode(SKEncodedImageFormat.Png, 92)
                        IO.File.WriteAllBytes(IO.Path.Combine(folder, "entfernen-ausschnitt.png"), data.ToArray())
                    End Using
                End Using

                ' Die Maske als Graustufenbild, damit man sie ueberhaupt sehen kann - ein
                ' Alphakanal allein ist in jedem Betrachter unsichtbar.
                Using visible = New SKBitmap(smallMask.Width, smallMask.Height,
                                              SKColorType.Bgra8888, SKAlphaType.Opaque)
                    For y = 0 To smallMask.Height - 1
                        For x = 0 To smallMask.Width - 1
                            Dim a = smallMask.GetPixel(x, y).Alpha
                            Dim v = If(a >= threshold, CByte(255), CByte(a \ 3))
                            visible.SetPixel(x, y, New SKColor(v, v, v, 255))
                        Next
                    Next
                    Using image = SKImage.FromBitmap(visible)
                        Using data = image.Encode(SKEncodedImageFormat.Png, 92)
                            IO.File.WriteAllBytes(IO.Path.Combine(folder, "entfernen-maske.png"), data.ToArray())
                        End Using
                    End Using
                End Using
                DiagnosticLogService.LogAlways("ObjektEntfernen",
                    "Ausschnitt und Maske abgelegt: entfernen-ausschnitt.png, entfernen-maske.png")
            Catch
            End Try
        End Sub

        Private Shared Function ClampByte(v As Single) As Byte
            If Single.IsNaN(v) Then Return 0
            Return CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(v)))))
        End Function

        ''' <summary>Das gefuellte Quadrat NUR innerhalb der Maske ins Bild zurueckschreiben.
        '''
        ''' Der weiche Rand ist kein Schoenheitsmittel: die gefuellte Flaeche kommt aus einer
        ''' Skalierung und trifft die Helligkeit der Nachbarschaft nie auf den Punkt genau. Ohne
        ''' Ueberblendung steht an der Maskenkante eine sichtbare Naht.</summary>
        Private Shared Function InsertInto(image As SKBitmap, mask As SKBitmap,
                                         filled As SKBitmap, window As SKRectI) As SKBitmap
            Dim result = New SKBitmap(image.Width, image.Height, image.ColorType, image.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                    canvas.DrawBitmap(image, 0, 0, paint)
                End Using

                ' Die Maske mit weicher Kante als Schablone. Sie wird ueber einen Weichzeichner
                ' erzeugt, damit der Uebergang wirklich ausleuchtet und nicht nur eine Stufe hat.
                ' Ueberblendet wird mit der DECKUNG DER MASKE SELBST. Ihre weiche Kante ist genau
                ' der Uebergang, den man haben will - eine eigene Naht darueberzulegen macht ihn nur
                ' unschaerfer. Die Schwelle gilt weiter fuer das umschliessende Rechteck und fuer
                ' das, was das Modell als Luecke sieht; hier zaehlt der Verlauf.
                Using large = New SKBitmap(window.Width, window.Height, image.ColorType, image.AlphaType)
                    If Not filled.ScalePixels(large, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) Then
                        result.Dispose()
                        Return Nothing
                    End If
                    Using filledSource = SKShader.CreateBitmap(large, SKShaderTileMode.Clamp,
                                                                  SKShaderTileMode.Clamp,
                                                                  SKMatrix.CreateTranslation(window.Left, window.Top))
                        Using paint = New SKPaint With {.Shader = filledSource}
                            ' Die Maske bestimmt WO und WIE STARK, der Schattierer WAS.
                            canvas.DrawBitmap(mask, 0, 0, paint)
                        End Using
                    End Using
                End Using
            End Using
            Return result
        End Function

    End Class

End Namespace
