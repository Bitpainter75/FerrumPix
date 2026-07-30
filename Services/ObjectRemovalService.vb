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
    ''' Das Modell rechnet auf einem FESTEN Quadrat von 512 Pixeln. Das bestimmt den ganzen Aufbau:
    ''' es bekommt nicht das Foto, sondern einen quadratischen Ausschnitt um die Luecke herum, mit
    ''' genug Umgebung, dass es etwas zum Fortsetzen hat. Ein 40-Megapixel-Bild auf 512 Pixel zu
    ''' quetschen wuerde jede Struktur vernichten, die es fortsetzen soll.
    '''
    ''' Zurueckgeschrieben wird NUR innerhalb der Maske, mit weichem Rand. Ausserhalb bleibt das
    ''' Original Pixel fuer Pixel stehen: das Modell gibt sein ganzes Quadrat neu aus, und das
    ''' unbesehen zu uebernehmen hiesse, das halbe Foto durch eine 512er Fassung seiner selbst zu
    ''' ersetzen.</summary>
    Public NotInheritable Class ObjectRemovalService

        Private Sub New()
        End Sub

        Public Const ModelFile As String = "lama"

        ''' <summary>Groesste Kante, mit der gerechnet wird. Das Modell nimmt jede Groesse an, aber
        ''' die Rechenzeit waechst mit der Flaeche - und ein Ausschnitt, der viel groesser ist als
        ''' das, was die Luecke braucht, kostet nur Zeit.</summary>
        Public Const MaxKante As Integer = 768

        ''' <summary>Auf dieses Vielfache muessen Breite und Hoehe aufgerundet werden.</summary>
        Public Const ModelGrid As Integer = 32

        ''' <summary>Wie viel Umgebung mindestens um die Luecke herum mitgegeben wird, als Vielfaches
        ''' ihrer laengsten Kante. Ohne Umgebung hat das Modell nichts, was es fortsetzen koennte.</summary>
        Private Const UmgebungFaktor As Double = 1.6

        ''' <summary>Und mindestens so viele Bildpunkte, damit auch eine winzige Luecke noch
        ''' Zusammenhang bekommt.</summary>
        Private Const UmgebungMindestens As Integer = 96

        ''' <summary>Ab welcher Deckung ein Bildpunkt als LUECKE gilt.
        '''
        ''' Nicht "groesser als null". Eine Maske aus der Objekterkennung hat weiche Raender und oft
        ''' einen schwachen Schleier weit ausserhalb des Objekts. Zaehlt jeder Hauch als Luecke, dann
        ''' ist das umschliessende Rechteck plotzlich das halbe Foto, der Ausschnitt entsprechend
        ''' gross, und das Modell soll auf 512 Pixeln das halbe Bild neu erfinden. Heraus kommt eine
        ''' verwaschene, verzogene Fassung dessen, was da war - der Fehler, der wie ein Geometrie-
        ''' fehler aussieht und keiner ist.</summary>
        Private Const LueckenSchwelleFest As Byte = 96

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
        Private Shared Function SchwelleFuer(maske As SKBitmap) As Byte
            If maske Is Nothing Then Return LueckenSchwelleFest
            Dim hoechst As Integer = 0
            ' Grob abtasten: fuer den Hoechstwert reicht jeder vierte Punkt, und das spart bei einem
            ' 40-Megapixel-Bild einen kompletten Durchlauf.
            For y = 0 To maske.Height - 1 Step 2
                For x = 0 To maske.Width - 1 Step 2
                    Dim a = maske.GetPixel(x, y).Alpha
                    If a > hoechst Then hoechst = a
                    If hoechst >= 255 Then Exit For
                Next
                If hoechst >= 255 Then Exit For
            Next
            If hoechst <= 0 Then Return LueckenSchwelleFest
            Return CByte(Math.Max(24, Math.Min(160, hoechst \ 2)))
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
        Private Const WachstumAnteil As Double = 0.02

        ''' <summary>Und mindestens so viele Bildpunkte, damit auch eine kleine Luecke ihren Saum
        ''' verliert.</summary>
        Private Const WachstumMindestens As Integer = 4

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
        Public Shared Property LetzterBericht As String = ""

        Public Shared ReadOnly Property Verfuegbar As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(ModelFile))
            End Get
        End Property

        ''' <summary>Das umschliessende Rechteck aller gesetzten Maskenpunkte, oder ein leeres.</summary>
        Public Shared Function LueckenRechteck(maske As SKBitmap) As SKRectI
            If maske Is Nothing OrElse maske.Width <= 0 OrElse maske.Height <= 0 Then Return SKRectI.Empty
            Dim LueckenSchwelle = SchwelleFuer(maske)
            Dim l = Integer.MaxValue, t = Integer.MaxValue, r = Integer.MinValue, b = Integer.MinValue
            For y = 0 To maske.Height - 1
                For x = 0 To maske.Width - 1
                    If maske.GetPixel(x, y).Alpha < LueckenSchwelle Then Continue For
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
        Public Shared Function AusschnittFuer(luecke As SKRectI, bildBreite As Integer, bildHoehe As Integer) As SKRectI
            If luecke.Width <= 0 OrElse luecke.Height <= 0 Then Return SKRectI.Empty
            If bildBreite <= 0 OrElse bildHoehe <= 0 Then Return SKRectI.Empty

            Dim randX = Math.Max(UmgebungMindestens, CInt(luecke.Width * UmgebungFaktor))
            Dim randY = Math.Max(UmgebungMindestens, CInt(luecke.Height * UmgebungFaktor))
            Dim l = Math.Max(0, luecke.Left - randX)
            Dim t = Math.Max(0, luecke.Top - randY)
            Dim r = Math.Min(bildBreite, luecke.Right + randX)
            Dim b = Math.Min(bildHoehe, luecke.Bottom + randY)
            If r <= l OrElse b <= t Then Return SKRectI.Empty
            Return New SKRectI(l, t, r, b)
        End Function

        ''' <summary>Die Luecke fuellen. <paramref name="maske"/> hat Bildgroesse; alles ueber der
        ''' Schwelle gilt als zu fuellen. Zurueck kommt eine KOPIE des Bildes mit gefuellter Luecke,
        ''' oder Nothing bei jedem Fehlschlag.</summary>
        Public Shared Function Fuelle(bild As SKBitmap, maske As SKBitmap) As SKBitmap
            If bild Is Nothing OrElse maske Is Nothing Then Return Nothing
            If bild.Width <= 0 OrElse bild.Height <= 0 Then Return Nothing
            Dim sitzung = AiModelService.SitzungFuer(ModelFile)
            If sitzung Is Nothing Then Return Nothing

            Dim eigeneMaske As SKBitmap = Nothing
            Dim m = maske
            Try
                If maske.Width <> bild.Width OrElse maske.Height <> bild.Height Then
                    eigeneMaske = New SKBitmap(New SKImageInfo(bild.Width, bild.Height,
                                                               SKColorType.Alpha8, SKAlphaType.Premul))
                    If Not maske.ScalePixels(eigeneMaske, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                    m = eigeneMaske
                End If

                Dim rohLuecke = LueckenRechteck(m)
                If rohLuecke.Width <= 0 OrElse rohLuecke.Height <= 0 Then Return Nothing
                ' Die Maske WACHSEN lassen, bevor irgendetwas anderes passiert - sonst bleibt der
                ' Saum aus halb zum Objekt gehoerenden Punkten stehen.
                Dim wachstum = Math.Max(WachstumMindestens,
                                        CInt(Math.Round(Math.Max(rohLuecke.Width, rohLuecke.Height) * WachstumAnteil)))
                Dim gewachsen = Erweitere(m, wachstum)
                If gewachsen IsNot Nothing Then
                    eigeneMaske?.Dispose()
                    eigeneMaske = gewachsen
                    m = gewachsen
                End If

                Dim luecke = LueckenRechteck(m)
                If luecke.Width <= 0 OrElse luecke.Height <= 0 Then Return Nothing
                Dim fenster = AusschnittFuer(luecke, bild.Width, bild.Height)
                If fenster.Width <= 0 Then Return Nothing

                ' Das Modell rechnet auf FREIER Groesse - nur ein Vielfaches von 32 muss es sein.
                ' Deshalb wird der Ausschnitt nicht mehr in ein festes Quadrat gequetscht, sondern
                ' nur so weit verkleinert, wie die Obergrenze es verlangt. Genau daran ist die
                ' erste Fassung gescheitert: ein grosses Objekt auf 512 Punkte gestaucht ergab
                ' einen weichen Verlauf statt Hintergrund.
                Dim faktor = Math.Min(1.0, MaxKante / CDbl(Math.Max(fenster.Width, fenster.Height)))
                Dim aw = Math.Max(32, CInt(Math.Round(fenster.Width * faktor)))
                Dim ah = Math.Max(32, CInt(Math.Round(fenster.Height * faktor)))
                Dim pw = AufVielfaches(aw), ph = AufVielfaches(ah)

                Using klein = New SKBitmap(aw, ah, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                    Using c = New SKCanvas(klein)
                        c.Clear(SKColors.Black)
                        Using img = SKImage.FromBitmap(bild)
                            c.DrawImage(img, New SKRect(fenster.Left, fenster.Top, fenster.Right, fenster.Bottom),
                                        New SKRect(0, 0, aw, ah),
                                        New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), Nothing)
                        End Using
                    End Using

                    Using kleinMaske = New SKBitmap(New SKImageInfo(aw, ah, SKColorType.Alpha8, SKAlphaType.Premul))
                        Using c = New SKCanvas(kleinMaske)
                            c.Clear(SKColors.Transparent)
                            Using img = SKImage.FromBitmap(m)
                                c.DrawImage(img, New SKRect(fenster.Left, fenster.Top, fenster.Right, fenster.Bottom),
                                            New SKRect(0, 0, aw, ah),
                                            New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), Nothing)
                            End Using
                        End Using

                        Dim schwelle = SchwelleFuer(kleinMaske)
                        Dim gesetzt As Long = 0
                        For y = 0 To ah - 1
                            For x = 0 To aw - 1
                                If kleinMaske.GetPixel(x, y).Alpha >= schwelle Then gesetzt += 1
                            Next
                        Next
                        Dim bericht = $"Bild {bild.Width}x{bild.Height}, Lücke {luecke.Width}x{luecke.Height}, " &
                                      $"Ausschnitt {fenster.Width}x{fenster.Height}, gerechnet auf {pw}x{ph}, " &
                                      $"Schwelle {schwelle}, Maske {gesetzt} von {aw * ah} Punkten"
                        LetzterBericht = bericht
                        DiagnosticLogService.LogAlways("ObjektEntfernen", bericht)
                        If gesetzt <= 0 Then
                            DiagnosticLogService.LogAlways("ObjektEntfernen",
                                "Maske im Modelleingang LEER - es gibt nichts zu fuellen")
                            Return Nothing
                        End If

                        ' Bei eingeschalteter Diagnose: den Ausschnitt und die Maske ablegen, GENAU
                        ' so, wie sie ins Modell gehen. Ob die Umgebung mitgeht oder nur die Luecke
                        ' ankommt, sieht man an zwei Bildern in einer Sekunde - und muss es nicht aus
                        ' Zahlen erschliessen.
                        SchreibeDiagnoseBilder(klein, kleinMaske, schwelle)

                        Dim gefuellt = Rechne(sitzung, klein, kleinMaske, aw, ah, pw, ph, schwelle)
                        If gefuellt Is Nothing Then Return Nothing
                        Using gefuellt
                            Return InsertInto(bild, m, gefuellt, fenster)
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogAlways("ObjektEntfernen", ex.Message)
                Return Nothing
            Finally
                eigeneMaske?.Dispose()
            End Try
        End Function

        ''' <summary>Die Maske um <paramref name="radius"/> Bildpunkte nach aussen erweitern.
        '''
        ''' Ueber eine Unschaerfe mit anschliessender niedriger Schwelle: was auch nur ein wenig
        ''' Deckung abbekommt, gilt danach als voll gedeckt. Das ist eine Ausdehnung um ungefaehr den
        ''' Radius und kostet nichts, waehrend ein echter Maximumfilter mit dem Radius waechst.</summary>
        Private Shared Function Erweitere(maske As SKBitmap, radius As Integer) As SKBitmap
            If maske Is Nothing OrElse radius < 1 Then Return Nothing
            Dim schwelle = SchwelleFuer(maske)
            Try
                Dim ziel = New SKBitmap(New SKImageInfo(maske.Width, maske.Height,
                                                        SKColorType.Alpha8, SKAlphaType.Premul))
                Using hart = New SKBitmap(New SKImageInfo(maske.Width, maske.Height,
                                                          SKColorType.Alpha8, SKAlphaType.Premul))
                    Dim n = maske.Width * maske.Height
                    Dim puffer(n - 1) As Byte
                    Runtime.InteropServices.Marshal.Copy(maske.GetPixels(), puffer, 0, n)
                    For i = 0 To n - 1
                        puffer(i) = If(puffer(i) >= schwelle, CByte(255), CByte(0))
                    Next
                    Runtime.InteropServices.Marshal.Copy(puffer, 0, hart.GetPixels(), n)

                    Using canvas = New SKCanvas(ziel)
                        canvas.Clear(SKColors.Transparent)
                        Using paint = New SKPaint()
                            paint.ImageFilter = SKImageFilter.CreateBlur(radius * 0.6F, radius * 0.6F)
                            canvas.DrawBitmap(hart, 0, 0, paint)
                        End Using
                    End Using

                    ' Niedrig schwellen: aus dem weichen Rand der Unschaerfe wird wieder eine volle
                    ' Deckung, und die Maske ist um rund einen Radius groesser als vorher.
                    Runtime.InteropServices.Marshal.Copy(ziel.GetPixels(), puffer, 0, n)
                    For i = 0 To n - 1
                        puffer(i) = If(puffer(i) >= 40, CByte(255), CByte(0))
                    Next
                    Runtime.InteropServices.Marshal.Copy(puffer, 0, ziel.GetPixels(), n)
                End Using
                Return ziel
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Auf das naechste Vielfache aufrunden, das das Modell verlangt.</summary>
        Private Shared Function AufVielfaches(wert As Integer) As Integer
            Dim r = wert Mod ModelGrid
            If r = 0 Then Return wert
            Return wert + (ModelGrid - r)
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
        Private Shared Function Rechne(sitzung As InferenceSession, klein As SKBitmap,
                                       kleinMaske As SKBitmap, aw As Integer, ah As Integer,
                                       pw As Integer, ph As Integer, schwelle As Byte) As SKBitmap
            Dim ebene = pw * ph
            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 4, ph, pw})
            Dim z = tensor.Buffer.Span
            For y = 0 To ph - 1
                Dim qy = Math.Min(y, ah - 1)
                For x = 0 To pw - 1
                    Dim qx = Math.Min(x, aw - 1)
                    Dim i = y * pw + x
                    Dim p = klein.GetPixel(qx, qy)
                    ' Die Maske ist ein SCHALTER, kein Deckungsgrad. Ausserhalb des belegten Teils
                    ' bleibt sie null - dort soll nichts gefuellt werden.
                    Dim drin = x < aw AndAlso y < ah
                    Dim loch = If(drin AndAlso kleinMaske.GetPixel(qx, qy).Alpha >= schwelle, 1.0F, 0.0F)
                    Dim sichtbar = 1.0F - loch
                    z(i) = p.Red / 255.0F * sichtbar
                    z(ebene + i) = p.Green / 255.0F * sichtbar
                    z(ebene * 2 + i) = p.Blue / 255.0F * sichtbar
                    z(ebene * 3 + i) = loch
                Next
            Next

            Dim name = sitzung.InputMetadata.Keys.First()
            Dim eingabe = New List(Of NamedOnnxValue) From {NamedOnnxValue.CreateFromTensor(name, tensor)}
            Using ergebnis = sitzung.Run(eingabe)
                Dim raus = TryCast(ergebnis.First().Value, DenseTensor(Of Single))
                If raus Is Nothing Then Return Nothing
                Dim masse = raus.Dimensions.ToArray()
                Dim rh = masse(masse.Length - 2), rw = masse(masse.Length - 1)
                Dim rebene = rw * rh
                Dim werte = raus.Buffer.Span
                If werte.Length < rebene * 3 Then Return Nothing
                ' Die Ausgabe steht in 0 bis 1 - anders als bei der Vorgaengerin, die 0 bis 255 gab.
                Dim ziel = New SKBitmap(aw, ah, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                For y = 0 To ah - 1
                    Dim sy = Math.Min(y, rh - 1)
                    For x = 0 To aw - 1
                        Dim sx = Math.Min(x, rw - 1)
                        Dim i = sy * rw + sx
                        ziel.SetPixel(x, y, New SKColor(Klemme(werte(i) * 255.0F),
                                                        Klemme(werte(rebene + i) * 255.0F),
                                                        Klemme(werte(rebene * 2 + i) * 255.0F), 255))
                    Next
                Next

                ' NACHRECHNEN, ob ueberhaupt etwas passiert ist.
                Dim summe As Long = 0, zahl As Long = 0
                For y = 0 To ah - 1
                    For x = 0 To aw - 1
                        If kleinMaske.GetPixel(x, y).Alpha < schwelle Then Continue For
                        Dim vorher = klein.GetPixel(x, y)
                        Dim nachher = ziel.GetPixel(x, y)
                        summe += Math.Abs(CInt(vorher.Red) - nachher.Red) +
                                 Math.Abs(CInt(vorher.Green) - nachher.Green) +
                                 Math.Abs(CInt(vorher.Blue) - nachher.Blue)
                        zahl += 1
                    Next
                Next
                Dim mittel = If(zahl > 0, summe / CDbl(zahl * 3), 0.0)
                LetzterBericht &= $", Änderung {mittel:F1} Stufen"
                DiagnosticLogService.LogAlways("ObjektEntfernen",
                    $"Aenderung in der Luecke: {mittel:F1} Stufen ueber {zahl} Punkte")
                If zahl > 0 AndAlso mittel < 4.0 Then
                    LetzterBericht &= " - das Modell hat nur wiederholt statt zu füllen"
                    DiagnosticLogService.LogAlways("ObjektEntfernen",
                        "Das Modell hat den Ausschnitt nur WIEDERHOLT statt zu fuellen.")
                End If
                Return ziel
            End Using
        End Function

        ''' <summary>Legt Ausschnitt und Maske als PNG neben das Protokoll - nur bei
        ''' eingeschalteter Diagnose. Zwei Bilder beantworten die Frage "bekommt das Modell die
        ''' Umgebung?" unmittelbar; aus Zahlen laesst sie sich nur erschliessen.</summary>
        Private Shared Sub SchreibeDiagnoseBilder(klein As SKBitmap, kleinMaske As SKBitmap, schwelle As Byte)
            Try
                If Not AppSettingsService.Load().EnableDiagnosticLogging Then Return
                Dim ordner = IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FerrumPix", "logs")
                IO.Directory.CreateDirectory(ordner)

                Using bild = SKImage.FromBitmap(klein)
                    Using daten = bild.Encode(SKEncodedImageFormat.Png, 92)
                        IO.File.WriteAllBytes(IO.Path.Combine(ordner, "entfernen-ausschnitt.png"), daten.ToArray())
                    End Using
                End Using

                ' Die Maske als Graustufenbild, damit man sie ueberhaupt sehen kann - ein
                ' Alphakanal allein ist in jedem Betrachter unsichtbar.
                Using sichtbar = New SKBitmap(kleinMaske.Width, kleinMaske.Height,
                                              SKColorType.Bgra8888, SKAlphaType.Opaque)
                    For y = 0 To kleinMaske.Height - 1
                        For x = 0 To kleinMaske.Width - 1
                            Dim a = kleinMaske.GetPixel(x, y).Alpha
                            Dim v = If(a >= schwelle, CByte(255), CByte(a \ 3))
                            sichtbar.SetPixel(x, y, New SKColor(v, v, v, 255))
                        Next
                    Next
                    Using bild = SKImage.FromBitmap(sichtbar)
                        Using daten = bild.Encode(SKEncodedImageFormat.Png, 92)
                            IO.File.WriteAllBytes(IO.Path.Combine(ordner, "entfernen-maske.png"), daten.ToArray())
                        End Using
                    End Using
                End Using
                DiagnosticLogService.LogAlways("ObjektEntfernen",
                    "Ausschnitt und Maske abgelegt: entfernen-ausschnitt.png, entfernen-maske.png")
            Catch
            End Try
        End Sub

        Private Shared Function Klemme(v As Single) As Byte
            If Single.IsNaN(v) Then Return 0
            Return CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(v)))))
        End Function

        ''' <summary>Das gefuellte Quadrat NUR innerhalb der Maske ins Bild zurueckschreiben.
        '''
        ''' Der weiche Rand ist kein Schoenheitsmittel: die gefuellte Flaeche kommt aus einer
        ''' Skalierung und trifft die Helligkeit der Nachbarschaft nie auf den Punkt genau. Ohne
        ''' Ueberblendung steht an der Maskenkante eine sichtbare Naht.</summary>
        Private Shared Function InsertInto(bild As SKBitmap, maske As SKBitmap,
                                         gefuellt As SKBitmap, fenster As SKRectI) As SKBitmap
            Dim ergebnis = New SKBitmap(bild.Width, bild.Height, bild.ColorType, bild.AlphaType)
            Using canvas = New SKCanvas(ergebnis)
                canvas.Clear(SKColors.Transparent)
                Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                    canvas.DrawBitmap(bild, 0, 0, paint)
                End Using

                ' Die Maske mit weicher Kante als Schablone. Sie wird ueber einen Weichzeichner
                ' erzeugt, damit der Uebergang wirklich ausleuchtet und nicht nur eine Stufe hat.
                ' Ueberblendet wird mit der DECKUNG DER MASKE SELBST. Ihre weiche Kante ist genau
                ' der Uebergang, den man haben will - eine eigene Naht darueberzulegen macht ihn nur
                ' unschaerfer. Die Schwelle gilt weiter fuer das umschliessende Rechteck und fuer
                ' das, was das Modell als Luecke sieht; hier zaehlt der Verlauf.
                Using gross = New SKBitmap(fenster.Width, fenster.Height, bild.ColorType, bild.AlphaType)
                    If Not gefuellt.ScalePixels(gross, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) Then
                        ergebnis.Dispose()
                        Return Nothing
                    End If
                    Using gefuellteQuelle = SKShader.CreateBitmap(gross, SKShaderTileMode.Clamp,
                                                                  SKShaderTileMode.Clamp,
                                                                  SKMatrix.CreateTranslation(fenster.Left, fenster.Top))
                        Using paint = New SKPaint With {.Shader = gefuellteQuelle}
                            ' Die Maske bestimmt WO und WIE STARK, der Schattierer WAS.
                            canvas.DrawBitmap(maske, 0, 0, paint)
                        End Using
                    End Using
                End Using
            End Using
            Return ergebnis
        End Function

    End Class

End Namespace
