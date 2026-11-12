Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ''' <summary>Rauschen mit einem gelernten Modell entfernen, NUR FUER RAW-DATEIEN.
    '''
    ''' Der Unterschied zu den Reglern ist die Art der Entscheidung: ein Weichzeichner muss zwischen
    ''' Rauschen und feiner Struktur abwaegen und verliert dabei immer etwas, weil er beides nur an
    ''' der Groesse unterscheiden kann. Das Modell hat gelernt, wie Sensorrauschen aussieht und wie
    ''' ein Haar - es nimmt das eine weg und laesst das andere stehen.
    '''
    ''' DAS MODELL IST TREENET (RawForge, MIT; trainiert auf RawNIND, CC BY-SA 4.0), und es rechnet
    ''' auf den LINEAREN Daten der RAW-Stufe, vor Belichtungsrampe und Tonkurve. Dort sieht es das
    ''' Rauschen so, wie der Sensor es geliefert hat, und bekommt die ISO als zweiten Eingang. Die
    ''' frueheren Modelle (SCUNet, NAFNet) rechneten fuer alle Bilder am fertigen 8-Bit-Bild; fuer RAWs
    ''' sind sie ersetzt, SCUNet ganz:
    ''' gemessen an zwoelf RAWs von ISO 100 bis 204800 verschiebt TreeNet die Farben am wenigsten
    ''' und behaelt die meiste Zeichnung, bei der Zeit des frueheren schnellen Modells (Zahlen in
    ''' OFFENE_PUNKTE.md, "Entrauschen mit Modell", und in Diagnostics/TreeNetMessung).
    '''
    ''' WARUM NUR RAW: das Modell ist auf Sensordaten trainiert. Ein JPEG hat Tonkurve, Schaerfung
    ''' und Kompression hinter sich, sein Rauschen sieht anders aus. Fertige Bilder bekommen das
    ''' fruehere schnelle Modell (NAFNet, an Handyfotos gelernt) am 8-Bit-Bild, eingebacken
    ''' (DenoiseModelServiceImage.vb). Derselbe Knopf waehlt den Weg nach der Datei.
    '''
    ''' AUCH X-TRANS, mit demselben Modell. Es bekommt die Daten NACH dem Demosaic, und dort hielt
    ''' es an sechs Fuji-RAWs (ISO 1600 bis 8000, Diagnostics/TreeNetMessung) mit: kleinster
    ''' Farbabstand und meiste Zeichnung in allen sechs, keine Labyrinthmuster. RawForges eigenes
    ''' X-Trans-Modell (nur als PyTorch-Datei) braucht es deshalb nicht.</summary>
    Partial Public NotInheritable Class DenoiseModelService

        Private Sub New()
        End Sub

        ''' <summary>Der Schluessel des Modells im Register (AiModelService.KnownEntries).</summary>
        Public Const ModelFile As String = "treenet"

        ''' <summary>Kantenlaenge einer Kachel, im Regelfall. Das Modell nimmt freie Groessen, aber
        ''' der Speicher waechst mit der Flaeche. Durch 64 teilbar, wie bei allen Modellen hier.</summary>
        Private Const LargeTileEdge As Integer = 512

        ''' <summary>Die kleine Kachel fuer Karten mit wenig eigenem Speicher.</summary>
        Private Const SmallTileEdge As Integer = 256

        ''' <summary>Ab wieviel eigenem Kartenspeicher die grosse Kachel gefahren wird, in Mebibyte.
        ''' Vier Gigabyte, weil auf der Karte neben der Kachel auch die Gewichte, der Bildschirminhalt
        ''' und alles andere liegt, was gerade laeuft.</summary>
        Private Const LargeTileMinimumMiB As Integer = 4096

        ''' <summary>Die Entscheidung selbst, nur an der Zahl, damit sie sich ohne die Karte pruefen
        ''' laesst. Unbekannt (0) heisst GROSS: die allermeisten Karten haben genug.</summary>
        Friend Shared Function TileEdgeForMemory(memoryMiB As Integer) As Integer
            If memoryMiB <= 0 OrElse memoryMiB >= LargeTileMinimumMiB Then Return LargeTileEdge
            Return SmallTileEdge
        End Function

        ''' <summary>Wie gross die Kachel in DIESEM Durchlauf sein darf. Entschieden an der Karte,
        ''' auf der die Sitzung GEBAUT wurde (leer heisst Prozessor), nicht an der Einstellung.</summary>
        Private Shared Function TileEdgeForRun(accelerator As String) As Integer
            If String.IsNullOrEmpty(accelerator) Then Return LargeTileEdge
            Dim memory = GpuAccelerationService.DeviceMemoryMiBFor(accelerator)
            Dim edge = TileEdgeForMemory(memory)
            If edge < LargeTileEdge Then
                DiagnosticLogService.LogAlways("Entrauschen",
                    $"Karte hat {memory} MiB, Kachel auf {edge} verkleinert")
            End If
            Return edge
        End Function

        ''' <summary>Wie weit sich zwei Kacheln ueberlappen, in Bildpunkten. Im Ueberlapp werden die
        ''' beiden Ergebnisse WEICH ueberblendet (linear ueber die ganze Breite), nicht hart
        ''' geschnitten: TreeNet ist ein U-Netz mit grossem Blickfeld, und am Kachelrand fehlt ihm
        ''' auf einer Seite der Zusammenhang. RawForge selbst ueberlappt um ein Viertel der Kachel;
        ''' bei 512er Kacheln waeren das 128 Punkte und gut die Haelfte Mehrarbeit. 32 Punkte mit
        ''' Ueberblendung kosten rund 14 Prozent.</summary>
        Private Const TileOverlap As Integer = 32
        ''' <summary>Jede Kachel beginnt auf einem Vielfachen davon. TreeNet ist ein U-Netz, das die
        ''' Aufloesung mehrfach halbiert; um ungerade Punkte verschoben rechnet es merklich anders.
        ''' Gemessen an der Pruef-RAW (M50, ISO 12800): gleich verteilte Kacheln an beliebigen
        ''' Punkten liessen oben links ein Restrauschen von 0,46, auf Vielfachen von 32 0,32; der
        ''' frueher feste Schritt von 480 lag bei 0,37.</summary>
        Private Const TileAlign As Integer = 32

        ''' <summary>Rec.2020 ist der Raum, in dem TreeNet trainiert ist; von linearem sRGB (D65)
        ''' dorthin.</summary>
        Private Shared ReadOnly SrgbToRec2020 As Single() = {
            0.6274F, 0.3293F, 0.0433F,
            0.0691F, 0.9195F, 0.0114F,
            0.0164F, 0.088F, 0.8956F}

        ''' <summary>Was der letzte Durchlauf gerechnet hat. Wird nach dem Entrauschen angezeigt.</summary>
        Public Shared Property LastReport As String = ""

        ''' <summary>Wird nach jeder Kachel gerufen, mit erledigten und gesamten Kacheln.</summary>
        Public Shared Property Progress As Action(Of Integer, Integer)

        Public Shared ReadOnly Property Available As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(ModelFile))
            End Get
        End Property

        ' ── Welche Dateien ─────────────────────────────────────────────────────────────────────

        Private Shared ReadOnly _supportLock As New Object()
        Private Shared _supportPath As String = ""
        Private Shared _supportWriteTime As DateTime
        Private Shared _supportResult As Boolean

        ''' <summary>Kann das Modell diese Datei entrauschen? Jede RAW, die LibRaw selbst in die
        ''' RAW-Stufe entwickelt: mit Bayer- oder X-Trans-Muster und ebenso ohne (verkleinerte RAW,
        ''' lineare DNG, Schwarzweiss). TreeNet rechnet auf dem entwickelten linearen RGB, und an
        ''' den Dateien ohne Muster verschob es die Farbe wenig und behielt die Zeichnung
        ''' (Diagnostics/TreeNetMessung, "RAWs ohne Farbmuster"). Nicht: eine DNG-Huelle um ein
        ''' fertiges Bild (sie laeuft an der RAW-Stufe vorbei) und eine Datei, die LibRaw nicht
        ''' lesen kann; die gehen an das Bildmodell.
        '''
        ''' Kostet ein open_file (unter einer Millisekunde); das Ergebnis der letzten Datei wird
        ''' gemerkt, weil die Oberflaeche oft fragt.</summary>
        Public Shared Function SupportsFile(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) OrElse Not RawPreviewService.IsSupportedRaw(path) Then Return False
            Dim writeTime As DateTime
            Try
                writeTime = IO.File.GetLastWriteTimeUtc(path)
            Catch
                Return False
            End Try
            SyncLock _supportLock
                If String.Equals(_supportPath, path, StringComparison.Ordinal) AndAlso _supportWriteTime = writeTime Then
                    Return _supportResult
                End If
            End SyncLock
            Dim pattern = RawDecodeService.ReadSensorPattern(path)
            Dim result = pattern <> RawDecodeService.SensorPattern.Unknown AndAlso pattern <> RawDecodeService.SensorPattern.Finished
            DiagnosticLogService.LogAlways("Entrauschen", $"{IO.Path.GetFileName(path)}: Sensormuster {pattern}")
            SyncLock _supportLock
                _supportPath = path
                _supportWriteTime = writeTime
                _supportResult = result
            End SyncLock
            Return result
        End Function

        ' ── Staerke aus dem Bild ─────────────────────────────────────────────────────────────
        '
        ' Eine feste Staerke bedeutet bei jedem Bild etwas anderes. Die Automatik fragt deshalb nicht
        ' "wie stark", sondern "wie viel Rauschen soll uebrig bleiben", und rechnet die Staerke daraus.
        '
        ' DAS GEHT, WEIL DIE STAERKE EINE MISCHUNG IST: das Ergebnis liegt zwischen dem des Modells
        ' und dem Original, bei Staerke s bleibt also (1 - s) des Rauschens stehen. Dazu kommt, was
        ' das Modell selbst uebrig laesst, und das ist je Modell anders geeicht:
        '
        '   Bildmodell (fertige Bilder):  Rest^2 = Modellrest^2 + (Aufschlag * (1 - s) * Rauschen)^2
        '   TreeNet (RAW-Stufe):          Rest   = Rauschen * ((1 - s) + s * Anteil)
        '
        ' Bei TreeNet ist der Rest ein ANTEIL des Rauschens und kein fester Wert, und die Mischung
        ' (RawDecodeService.BlendDenoised, in den linearen Daten) verlaeuft dazwischen gerade.
        '
        ' Gemessen wird mit ImageProcessor.EstimateNoiseLevel am entwickelten, noch nicht
        ' entrauschten Bild.

        ''' <summary>So viel Helligkeitsrauschen soll nach der Automatik uebrig bleiben, als Streuung
        ''' in Grauwerten. Am Bild beurteilt; ein Bild unter rund 2,7 gilt als sauber.</summary>
        Public Const AutoTargetNoise As Single = 2.4F

        ''' <summary>Dasselbe Ziel fuer TreeNet in der RAW-Stufe, halb so viel wie bei fertigen
        ''' Bildern. Mit 2,4 blieb bei ISO 12800 sichtbares Helligkeitskorn stehen: die R6 bekam 84 und
        ''' behielt mehr als doppelt so viel Feinrauschen wie bei 100 (0,76 gegen 0,32 L*). Das
        ''' Modell kostet in der RAW-Stufe kaum Zeichnung, das Ziel darf deshalb tiefer liegen; ab
        ''' etwa ISO 6400 heisst das volle oder fast volle Staerke, bei ISO 400 bis 800 rund 60 bis 70
        ''' statt 10 bis 30. Messung: Diagnostics/TreeNetMessung/Kombi und Staerke.</summary>
        Public Const RawAutoTargetNoise As Single = 1.2F

        ''' <summary>Was das BILDMODELL (NAFNet, fertige Bilder) bei voller Staerke selbst an
        ''' Streuung stehen laesst, in Grauwerten (0,3 bis 0,9 an neun Aufnahmen).</summary>
        Friend Const ModelResidualNoise As Single = 0.5F

        ''' <summary>Welcher ANTEIL des Rauschens bei TreeNet in der RAW-Stufe nach voller Staerke
        ''' noch gemessen wird. Kein fester Rest wie beim Bildmodell: TreeNet laesst je Bild einen
        ''' Anteil stehen, und die Mischung verlaeuft dazwischen gerade. Mit treenet-v2 (Heavy, dann
        ''' SuperLight) gemessen 0,03 bis 0,33, im Mittel 0,14, wo die Schaetzung bei voller Staerke
        ''' ueberhaupt noch etwas fand. 0,10 trifft die Staerke, bei der das Ziel erreicht wird, an
        ''' 26 RAWs von ISO 125 bis 204800 im Mittel auf 5 Punkte; der alte Wert 0,22 (Heavy allein)
        ''' laege mit v2 im Mittel 9 daneben. Wer noch treenet-v1 hat, bekommt eine um einige Punkte
        ''' zu kleine Staerke. Messung: Diagnostics/TreeNetMessung/Staerke.</summary>
        Friend Const RawModelResidualShare As Single = 0.1F

        ''' <summary>Um so viel liegt das Restrauschen einer Mischung ueber der Rechnung
        ''' (1 - Staerke) mal Rauschen (gemessen das 1,0- bis 1,4-Fache, meist 1,1 bis 1,2).</summary>
        Friend Const MixResidualFactor As Single = 1.15F

        ''' <summary>Unterhalb dieser Staerke lohnt der Lauf nicht. Der Stapel laesst solche Bilder
        ''' aus, der Editor sagt es dazu.</summary>
        Public Const AutoMinimumStrength As Double = 25.0

        ''' <summary>Unterhalb dieser Bildgroesse misst die Automatik nicht, in Bildpunkten (drei
        ''' Megapixel). Die Schaetzung trennt Rauschen von Zeichnung darueber, wie beides beim
        ''' Verkleinern waechst; ein schon verkleinertes Bild taeuscht sie (gemessen an 29 JPEGs).
        ''' Fuer eine RAW greift die Grenze kaum, sie bleibt als Schutz.</summary>
        Public Const AutoMinimumPixels As Long = 3000000

        ''' <summary>Was die Automatik fuer ein Bild vorschlaegt.</summary>
        Public NotInheritable Class DenoiseSuggestion
            ''' <summary>Gemessenes Helligkeitsrauschen in Grauwerten; NaN, wenn nichts zu messen war.</summary>
            Public Property NoiseLevel As Single = Single.NaN
            ''' <summary>Die Staerke, 0 bis 100.</summary>
            Public Property Strength As Double
            ''' <summary>Lohnt der Lauf?</summary>
            Public Property Worthwhile As Boolean
            ''' <summary>Das Bild liegt unter <see cref="AutoMinimumPixels"/>; gemessen wurde nicht.</summary>
            Public Property TooSmall As Boolean
        End Class

        ''' <summary>Das Rauschziel fuer einen Restkorn-Wert von 0 bis 100. Bei 50 gilt
        ''' <see cref="AutoTargetNoise"/> (in der RAW-Stufe <see cref="RawAutoTargetNoise"/>), jede
        ''' 50 Schritte halbieren oder verdoppeln es.</summary>
        Public Shared Function TargetNoiseFor(grain As Double, Optional rawStage As Boolean = False) As Single
            Dim g = If(Double.IsNaN(grain), 50.0, Math.Max(0.0, Math.Min(100.0, grain)))
            Return CSng(If(rawStage, RawAutoTargetNoise, AutoTargetNoise) * Math.Pow(2.0, (g - 50.0) / 50.0))
        End Function

        ''' <summary>Die Staerke fuer ein gemessenes Rauschen. Reine Rechnung, damit sie sich ohne
        ''' Bild pruefen laesst.</summary>
        Public Shared Function SuggestFor(noiseLevel As Single, Optional grain As Double = 50.0,
                                          Optional rawStage As Boolean = False) As DenoiseSuggestion
            Dim suggestion As New DenoiseSuggestion With {.NoiseLevel = noiseLevel}
            If Single.IsNaN(noiseLevel) OrElse noiseLevel <= 0 Then Return suggestion
            Dim target = TargetNoiseFor(grain, rawStage)
            Dim strength As Double
            If rawStage Then
                ' TreeNet in der RAW-Stufe: Rest(s) = Rauschen * ((1 - s) + s * Anteil), aufgeloest
                ' nach s fuer Rest = Ziel. Erreicht auch die volle Staerke das Ziel nicht, gilt sie.
                strength = (1.0 - target / noiseLevel) / (1.0 - RawModelResidualShare)
            Else
                Dim allowed = Math.Sqrt(Math.Max(0.0, CDbl(target) * target - CDbl(ModelResidualNoise) * ModelResidualNoise)) /
                              MixResidualFactor
                strength = 1.0 - allowed / noiseLevel
            End If
            suggestion.Strength = Math.Round(Math.Max(0.0, Math.Min(1.0, strength)) * 100.0)
            suggestion.Worthwhile = suggestion.Strength >= AutoMinimumStrength
            Return suggestion
        End Function

        ''' <summary>Das Bild messen und die Staerke vorschlagen. Das Bild wird nur gelesen.</summary>
        Public Shared Function Suggest(image As SKBitmap, Optional grain As Double = 50.0,
                                       Optional rawStage As Boolean = False) As DenoiseSuggestion
            If image IsNot Nothing AndAlso CLng(image.Width) * image.Height < AutoMinimumPixels Then
                Dim tooSmall = SuggestFor(Single.NaN, grain, rawStage)
                tooSmall.TooSmall = True
                Return tooSmall
            End If
            Return SuggestFor(ImageProcessor.EstimateNoiseLevel(image), grain, rawStage)
        End Function

        ''' <summary>Ein Entrausch-Auftrag fuer den Stapel: wie die Staerke entsteht. Er geht an
        ''' <see cref="ImageProcessor.SaveImage"/>; dort wird er bei einer RAW in ihr Rezept
        ''' geschrieben (ImageAdjustments.RawModelDenoise) und wirkt in der RAW-Stufe. Bilder, die
        ''' keine RAW sind, laesst er aus.</summary>
        Public NotInheritable Class DenoiseRequest
            ''' <summary>Wahr: die Staerke wird je Bild gemessen, <see cref="Grain"/> gilt.
            ''' Falsch: <see cref="Strength"/> gilt fuer jedes Bild.</summary>
            Public Property Automatic As Boolean = True
            ''' <summary>Feste Staerke, 0 bis 100.</summary>
            Public Property Strength As Double = 70.0
            ''' <summary>Restkorn der Automatik, 0 bis 100; siehe <see cref="TargetNoiseFor"/>.</summary>
            Public Property Grain As Double = 50.0
        End Class

        ''' <summary>Die Staerke, mit der ein Auftrag dieses Bild entrauscht, oder Nothing, wenn es
        ''' so bleibt (Automatik haelt den Lauf fuer unnoetig, Bild zu klein). Gemessen wird am
        ''' noch nicht entrauschten, entwickelten Bild.</summary>
        Friend Shared Function StrengthForRequest(developed As SKBitmap, request As DenoiseRequest, sourceName As String,
                                                  Optional rawStage As Boolean = False) As Double?
            If request Is Nothing Then Return Nothing
            If Not request.Automatic Then Return Math.Max(0.0, Math.Min(100.0, request.Strength))
            Dim suggestion = Suggest(developed, request.Grain, rawStage)
            If suggestion.TooSmall Then
                DiagnosticLogService.LogAlways("Entrauschen",
                    $"{sourceName}: zu klein, um Rauschen von Zeichnung zu trennen - ausgelassen")
                Return Nothing
            End If
            If Not suggestion.Worthwhile Then
                DiagnosticLogService.LogAlways("Entrauschen",
                    $"{sourceName}: Rauschen {suggestion.NoiseLevel:F2}, Staerke waere {suggestion.Strength:F0} - ausgelassen")
                Return Nothing
            End If
            DiagnosticLogService.LogAlways("Entrauschen",
                $"{sourceName}: Rauschen {suggestion.NoiseLevel:F2}, Staerke {suggestion.Strength:F0}")
            Return suggestion.Strength
        End Function

        ' ── Alte Rezepte ─────────────────────────────────────────────────────────────────────

        ''' <summary>Ein Rezept aus der Zeit der beiden 8-Bit-Modelle auf das neue Entrauschen
        ''' umstellen. Dort stand ein Entrauschen als gebackener Vorgang ("quality" oder "fast",
        ''' mit Staerke); die Modelle gibt es nicht mehr.
        '''
        ''' BEI EINER RAW wird der Vermerk zur Einstellung der RAW-Stufe mit derselben Staerke, und
        ''' der Vorgang verschwindet aus der Liste. Das Bild sieht danach etwas anders aus als beim
        ''' Speichern, meist besser; so entschieden, statt die alten Modelle nur dafuer zu behalten.
        '''
        ''' NUR, WENN DER VORGANG NICHT IN DEN PIXELN STECKT (BakedOperationsApplied falsch). Bei
        ''' einer .fpx mit angewandtem Vorgang liegt das alte Entrauschen schon im Arbeitsbild des
        ''' Buendels; ein zweites in der RAW-Stufe waere doppelt.
        '''
        ''' BEI ALLEM ANDEREN (PSD, RAWs ohne passendes Muster) bleibt der Vermerk stehen und wird mit
        ''' dem Bildmodell nachgezogen (DenoiseModelServiceImage.vb); ein alter Vermerk "quality"
        ''' ebenso, das fruehere gruendliche Modell gibt es nicht mehr.</summary>
        Public Shared Sub MigrateLegacyRecipe(adj As ImageAdjustments, sourcePath As String)
            If adj Is Nothing OrElse adj.BakedOperations Is Nothing OrElse adj.BakedOperationsApplied Then Return
            ' ERST der Vermerk, DANN die Datei: das Sensormuster kostet ein open_file in LibRaw, und
            ' das Lesen der Begleitdatei laeuft beim Blaettern fuer jedes Bild.
            Dim legacy = adj.BakedOperations.LastOrDefault(Function(o) o IsNot Nothing AndAlso
                String.Equals(o.Kind, BakedOperation.KindDenoise, StringComparison.OrdinalIgnoreCase))
            If legacy Is Nothing Then Return
            If Not UsesRawStage(sourcePath) Then Return
            adj.BakedOperations.RemoveAll(Function(o) o IsNot Nothing AndAlso
                String.Equals(o.Kind, BakedOperation.KindDenoise, StringComparison.OrdinalIgnoreCase))
            adj.RawModelDenoise = True
            adj.RawModelDenoiseStrength = Math.Max(0.0F, Math.Min(100.0F, legacy.DenoiseStrength))
            DiagnosticLogService.LogAlways("Entrauschen",
                $"{IO.Path.GetFileName(sourcePath)}: altes Entrauschen ({legacy.DenoiseModel}, Staerke {legacy.DenoiseStrength:F0}) auf die RAW-Stufe umgestellt")
        End Sub

        ' ── Das Rechnen ──────────────────────────────────────────────────────────────────────

        ''' <summary>Lineare 16-Bit-Daten der RAW-Stufe entrauschen. Zurueck kommt ein NEUER Puffer
        ''' im selben Aufbau (RGB, 16 Bit je Kanal, Zeile an Zeile), oder Nothing bei Fehlschlag oder
        ''' Abbruch.
        '''
        ''' DIE DATEN, wie RawDecodeService sie ablegt: entweder schon in linearem sRGB
        ''' (<paramref name="toneMatrix"/> Nothing) oder im Kameraraum nach dem Weissabgleich, dann
        ''' fuehrt die Matrix nach linearem sRGB. Weiss liegt bei 65535 / <paramref name="normalization"/>
        ''' (beim Entklemmen liegen die Daten um diesen Faktor tiefer, damit die Lichter Platz haben).
        '''
        ''' DAS MODELL BEKOMMT, was RawForge ihm gibt: lineares Rec.2020, Weiss bei 1, auf 0 bis 1
        ''' beschnitten, dazu ISO / 6400. Was ueber Weiss liegt (geretteter Lichter), geht nicht
        ''' verloren: zurueck kommt der Eingang plus die Aenderung des Modells, und die ist dort, wo
        ''' das Modell nur Weiss sah, null.</summary>
        Friend Shared Function DenoiseLinear(data As Byte(), width As Integer, height As Integer,
                                             normalization As Double, toneMatrix As Single(),
                                             iso As Double,
                                             cancel As Threading.CancellationToken) As Byte()
            If data Is Nothing OrElse width <= 0 OrElse height <= 0 Then Return Nothing
            If CLng(data.Length) < CLng(width) * height * 6 Then Return Nothing
            Dim loaded = AiModelService.SessionAndDeviceFor(ModelFile)
            If loaded Is Nothing OrElse loaded.Session Is Nothing Then
                DiagnosticLogService.LogAlways("Entrauschen",
                    $"Modell {ModelFile} steht nicht bereit - keine Sitzung. Fehlt die Datei, oder hat die Karte sie abgelehnt?")
                Return Nothing
            End If
            Dim session = loaded.Session

            ' Vom Speicherwert nach Rec.2020 (Weiss = 1) und zurueck.
            Dim toSrgb = If(toneMatrix, New Single() {1, 0, 0, 0, 1, 0, 0, 0, 1})
            Dim forward = Multiply3(SrgbToRec2020, toSrgb)
            Dim inverse = Invert3(forward)
            If inverse Is Nothing Then
                DiagnosticLogService.LogAlways("Entrauschen", "Farbmatrix nicht umkehrbar - Lauf faellt aus")
                Return Nothing
            End If
            Dim scale = CSng(Math.Max(1.0, normalization) / 65535.0)
            Dim cond = CSng(Math.Max(50.0, Math.Min(65535.0, If(iso > 0, iso, 800.0))) / 6400.0)

            Dim edge = TileEdgeForRun(loaded.Accelerator)
            Dim wo = GpuAccelerationService.DeviceLabelFor(loaded.Accelerator)
            DiagnosticLogService.LogAlways("Entrauschen",
                $"Start: {ModelFile}, Bild {width}x{height}, Kachel {edge}, ISO {iso:F0}, auf {wo}")

            ' DAS ERGEBNIS ENTSTEHT IN EINEM BAND, das eine Kachelzeile hoch ist, nicht im ganzen
            ' Bild. Jeder Bildpunkt sammelt die gewichteten Ergebnisse der Kacheln, die ihn decken;
            ' sobald die naechste Kachelzeile ihn nicht mehr erreicht, ist er fertig und geht in den
            ' Ausgang. Den Eingang rechnet jede Kachel selbst aus den 16-Bit-Daten. Als Ganzbild in
            ' Gleitkomma waeren das bei 45 Megapixeln weit ueber ein Gigabyte gewesen.
            Dim tileW = Math.Min(edge, RoundUp64(width))
            Dim tileH = Math.Min(edge, RoundUp64(height))
            Dim xs = TileStarts(width, tileW)
            Dim ys = TileStarts(height, tileH)
            Dim tileCount = xs.Count * ys.Count
            Dim inputName = session.InputMetadata.Keys.First(Function(k) Not String.Equals(k, "cond", StringComparison.Ordinal))
            Dim condName = session.InputMetadata.Keys.FirstOrDefault(Function(k) String.Equals(k, "cond", StringComparison.Ordinal))
            If condName Is Nothing Then
                DiagnosticLogService.LogAlways("Entrauschen", "Modell ohne Eingang cond - nicht das erwartete Modell")
                Return Nothing
            End If
            Dim condTensor = New DenseTensor(Of Single)(New Single() {cond}, New Integer() {1, 1})
            ' Je Kachel eine eigene Rampe, ueber die TATSAECHLICHE Ueberlappung mit ihren Nachbarn.
            Dim rampsX = Enumerable.Range(0, xs.Count).Select(Function(i) TileRamp(xs, i, tileW)).ToList()
            Dim rampsY = Enumerable.Range(0, ys.Count).Select(Function(i) TileRamp(ys, i, tileH)).ToList()
            Dim band As New TileBand With {
                .Width = width, .Rows = tileH,
                .Sum = New Single(width * tileH * 3 - 1) {},
                .Weights = New Single(width * tileH - 1) {}}
            Dim samples = data
            Dim result(data.Length - 1) As Byte
            Dim clock = Diagnostics.Stopwatch.StartNew()
            Dim done = 0
            Try
                For j = 0 To ys.Count - 1
                    Dim y0 = ys(j)
                    For i = 0 To xs.Count - 1
                        Dim x0 = xs(i)
                        If cancel.IsCancellationRequested Then
                            DiagnosticLogService.LogAlways("Entrauschen", $"abgebrochen nach {done} von {tileCount} Kacheln")
                            Return Nothing
                        End If
                        RunTile(session, inputName, condName, condTensor, samples, forward, scale, band,
                                width, height, x0, y0, tileW, tileH, rampsX(i), rampsY(j))
                        done += 1
                        Progress?.Invoke(done, tileCount)
                    Next
                    ' Fertig sind alle Zeilen, die die naechste Kachelzeile nicht mehr erreicht.
                    Dim finishedTo = If(j + 1 < ys.Count, ys(j + 1), height)
                    FlushRows(band, samples, result, forward, inverse, scale, band.Top, finishedTo)
                    band.ShiftTo(finishedTo)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Entrauschen", ex.Message)
                Return Nothing
            End Try
            If RestoreCoarseLevelsEnabled Then RestoreCoarseLevels(samples, result, width, height, CoarseLevelCell, CoarseLevelLuminanceOnly)
            clock.Stop()
            LastReport = String.Format(
                LocalizationService.T("Bild {0}x{1}, {2} Kacheln, {3} s"),
                width, height, done, clock.Elapsed.TotalSeconds.ToString("F0"))
            DiagnosticLogService.LogAlways("Entrauschen", LastReport)
            Return result
        End Function

        ''' <summary>Kantenlaenge der Bloecke, ueber die RestoreCoarseLevels Pegel und Farbe
        ''' zurueckholt, in Bildpunkten der RAW-Stufe.
        ''' Fuer Messungen veraenderbar (TreeNetMessung/Tiefen, ueber Reflexion).</summary>
        Friend Shared CoarseLevelCell As Integer = 16

        ''' <summary>Gleicht RestoreCoarseLevels nur die Helligkeit an und laesst die Farbe, wie das
        ''' Modell sie gemacht hat (Vorgabe, Begruendung dort)? Fuer Messungen veraenderbar
        ''' (TreeNetMessung/Tiefen, ueber Reflexion).</summary>
        Friend Shared CoarseLevelLuminanceOnly As Boolean = True

        ''' <summary>Holt den Pegel der groben Flaechen nach dem Modell zurueck. Nur fuer
        ''' Messungen abschaltbar (TreeNetMessung/Tiefen, ueber Reflexion).</summary>
        Friend Shared RestoreCoarseLevelsEnabled As Boolean = True

        ''' <summary>TreeNet soll das RAUSCHEN nehmen, nicht den Pegel der Flaechen. Gemessen hat es
        ''' in angehobenen Tiefen beides verschoben: im Mittel 1,6 L* dunkler und ein kraeftigerer
        ''' Stich, bei ISO 32000 und mehr bis 5 L* und 15 Delta E (Diagnostics/TreeNetMessung,
        ''' "Tiefen"). Vermutlich nimmt es mit dem Rauschen auch die Anhebung weg, die das
        ''' Abschneiden negativer Werte nach dem Schwarzabzug im Rauschen der tiefsten Schatten
        ''' erzeugt.
        '''
        ''' Deshalb hier: je Block von <paramref name="cell"/> Punkten der Mittelwert von Eingang und
        ''' Ergebnis je Kanal, der Unterschied zwischen den Blockmitten bilinear verteilt und auf das
        ''' Ergebnis gelegt. Was feiner ist als ein Block (das Rauschen, die Zeichnung), bleibt, wie
        ''' das Modell es gemacht hat; was groeber ist, kommt vom Sensor. Ueber Weiss aendert sich
        ''' nichts: dort ist das Ergebnis ohnehin der Eingang. Kostet zwei Durchgaenge ueber das Bild
        ''' und einige hundert Kilobyte, keine Gleitkomma-Kopie des Bildes.
        '''
        ''' NUR DIE HELLIGKEIT (<paramref name="luminanceOnly"/>): der Unterschied der drei Kanaele
        ''' wird zu einem Helligkeitswert und gleich auf alle gelegt. Die Farbe der Blockmittel ist bei
        ''' hoher ISO selbst noch verrauscht; je Kanal angeglichen kam sie als grobe Farbflecken
        ''' zurueck, sichtbar an einer R6 bei ISO 12800.
        '''
        ''' Gemessen an 13 RAWs von ISO 1600 bis 204800, angehobene Tiefen, gegen das Bild ohne
        ''' Entrauschen: die Helligkeit weicht im Mittel um 0,6 statt 1,6 L* ab (schlimmster Fall 1,3
        ''' statt 5,3), die Buntheit steigt um 1,2 statt 2,3 (je Kanal angeglichen 3,0), die Flecken in
        ''' der Farbe liegen bei 4,9 (ohne Abgleich 5,2, je Kanal 6,7). Angeglichen wird in den
        ''' LINEAREN Daten: in einem Potenzraum naeher am Sehen (Gamma 2,2 und 3,0) wurde die
        ''' Helligkeit schlechter getroffen (1,7 und 2,1 L*). Groessere Bloecke (32, 64) nahmen die
        ''' Farbflecken je Kanal auch weg, liessen aber den kraeftigeren Stich stehen.</summary>
        Private Shared Sub RestoreCoarseLevels(noisy As Byte(), result As Byte(), width As Integer, height As Integer,
                                               cell As Integer, luminanceOnly As Boolean)
            Dim gw = (width + cell - 1) \ cell
            Dim gh = (height + cell - 1) \ cell
            Dim diff(gw * gh * 3 - 1) As Single
            Threading.Tasks.Parallel.For(0, gh,
                Sub(by)
                    Dim sumN(gw * 3 - 1) As Double, sumR(gw * 3 - 1) As Double, count(gw - 1) As Integer
                    Dim yEnd = Math.Min(height, (by + 1) * cell)
                    For y = by * cell To yEnd - 1
                        Dim rowStart = CLng(y) * width * 6
                        For x = 0 To width - 1
                            Dim bx = x \ cell
                            Dim p = CInt(rowStart + CLng(x) * 6)
                            For c = 0 To 2
                                Dim q = p + c * 2
                                sumN(bx * 3 + c) += CInt(noisy(q)) Or (CInt(noisy(q + 1)) << 8)
                                sumR(bx * 3 + c) += CInt(result(q)) Or (CInt(result(q + 1)) << 8)
                            Next
                            count(bx) += 1
                        Next
                    Next
                    For bx = 0 To gw - 1
                        If count(bx) = 0 Then Continue For
                        For c = 0 To 2
                            diff((by * gw + bx) * 3 + c) = CSng((sumN(bx * 3 + c) - sumR(bx * 3 + c)) / count(bx))
                        Next
                    Next
                End Sub)
            Threading.Tasks.Parallel.For(0, height,
                Sub(y)
                    Dim fy = (y + 0.5) / cell - 0.5
                    Dim y0 = Math.Max(0, Math.Min(gh - 1, CInt(Math.Floor(fy))))
                    Dim y1 = Math.Min(gh - 1, y0 + 1)
                    Dim ty = CSng(Math.Max(0.0, Math.Min(1.0, fy - y0)))
                    Dim rowStart = CLng(y) * width * 6
                    For x = 0 To width - 1
                        Dim fx = (x + 0.5) / cell - 0.5
                        Dim x0 = Math.Max(0, Math.Min(gw - 1, CInt(Math.Floor(fx))))
                        Dim x1 = Math.Min(gw - 1, x0 + 1)
                        Dim tx = CSng(Math.Max(0.0, Math.Min(1.0, fx - x0)))
                        Dim p = CInt(rowStart + CLng(x) * 6)
                        Dim shift(2) As Single
                        For c = 0 To 2
                            Dim d00 = diff((y0 * gw + x0) * 3 + c), d01 = diff((y0 * gw + x1) * 3 + c)
                            Dim d10 = diff((y1 * gw + x0) * 3 + c), d11 = diff((y1 * gw + x1) * 3 + c)
                            shift(c) = (d00 * (1 - tx) + d01 * tx) * (1 - ty) + (d10 * (1 - tx) + d11 * tx) * ty
                        Next
                        If luminanceOnly Then
                            ' Nur die Helligkeit des Unterschieds, gleich auf alle drei Kanaele: die
                            ' Farbe der Blockmittel ist bei hoher ISO selbst noch verrauscht und kaeme
                            ' sonst als grobe Farbflecken zurueck.
                            Dim l = 0.2126F * shift(0) + 0.7152F * shift(1) + 0.0722F * shift(2)
                            shift(0) = l : shift(1) = l : shift(2) = l
                        End If
                        For c = 0 To 2
                            Dim d = shift(c)
                            Dim q = p + c * 2
                            Dim v = (CInt(result(q)) Or (CInt(result(q + 1)) << 8)) + d
                            Dim s = If(v <= 0.0F, 0, If(v >= 65535.0F, 65535, CInt(Math.Round(v))))
                            result(q) = CByte(s And &HFF)
                            result(q + 1) = CByte(s >> 8)
                        Next
                    Next
                End Sub)
        End Sub

        ''' <summary>Die Sammelstelle fuer eine Kachelzeile: Summe der gewichteten Ergebnisse und
        ''' der Gewichte je Bildpunkt, fuer die Zeilen ab <see cref="Top"/>.</summary>
        Private NotInheritable Class TileBand
            Public Width As Integer
            Public Rows As Integer
            Public Top As Integer
            Public Sum As Single()
            Public Weights As Single()

            ''' <summary>Das Band nach unten schieben: was ab <paramref name="newTop"/> schon
            ''' gesammelt ist, wandert nach oben, der Rest wird geleert.</summary>
            Public Sub ShiftTo(newTop As Integer)
                Dim shift = newTop - Top
                If shift <= 0 Then Return
                If shift < Rows Then
                    Array.Copy(Sum, shift * Width * 3, Sum, 0, (Rows - shift) * Width * 3)
                    Array.Copy(Weights, shift * Width, Weights, 0, (Rows - shift) * Width)
                    Array.Clear(Sum, (Rows - shift) * Width * 3, shift * Width * 3)
                    Array.Clear(Weights, (Rows - shift) * Width, shift * Width)
                Else
                    Array.Clear(Sum, 0, Sum.Length)
                    Array.Clear(Weights, 0, Weights.Length)
                End If
                Top = newTop
            End Sub
        End Class

        ''' <summary>Ein Bildpunkt der 16-Bit-Daten in Rec.2020, Weiss bei 1, ungeklemmt.</summary>
        Private Shared Sub ReadRec2020(samples As Byte(), index As Long, forward As Single(), scale As Single,
                                       ByRef r As Single, ByRef g As Single, ByRef b As Single)
            Dim p = CInt(index * 6)
            Dim sr = (CInt(samples(p)) Or (CInt(samples(p + 1)) << 8)) * scale
            Dim sg = (CInt(samples(p + 2)) Or (CInt(samples(p + 3)) << 8)) * scale
            Dim sb = (CInt(samples(p + 4)) Or (CInt(samples(p + 5)) << 8)) * scale
            r = forward(0) * sr + forward(1) * sg + forward(2) * sb
            g = forward(3) * sr + forward(4) * sg + forward(5) * sb
            b = forward(6) * sr + forward(7) * sg + forward(8) * sb
        End Sub

        ''' <summary>Fertige Zeilen in den Ausgang: Eingang plus die Aenderung des Modells, zurueck
        ''' in den Speicheraufbau. Ein Bildpunkt ohne Gewicht (nur moeglich, wenn eine Kachel dort
        ''' Unsinn lieferte) bleibt, wie er war.</summary>
        Private Shared Sub FlushRows(band As TileBand, samples As Byte(), result As Byte(),
                                     forward As Single(), inverse As Single(), scale As Single,
                                     fromRow As Integer, toRow As Integer)
            Dim width = band.Width
            Dim back = 1.0F / scale
            Threading.Tasks.Parallel.For(fromRow, toRow,
                Sub(y)
                    Dim bandRow = (y - band.Top) * width
                    For x = 0 To width - 1
                        Dim pixel = CLng(y) * width + x
                        Dim r, g, b As Single
                        ReadRec2020(samples, pixel, forward, scale, r, g, b)
                        Dim bi = bandRow + x
                        Dim w = band.Weights(bi)
                        If w > 0 Then
                            r += band.Sum(bi * 3) / w - Clamp01(r)
                            g += band.Sum(bi * 3 + 1) / w - Clamp01(g)
                            b += band.Sum(bi * 3 + 2) / w - Clamp01(b)
                        End If
                        Dim p = CInt(pixel * 6)
                        WriteSample(result, p, (inverse(0) * r + inverse(1) * g + inverse(2) * b) * back)
                        WriteSample(result, p + 2, (inverse(3) * r + inverse(4) * g + inverse(5) * b) * back)
                        WriteSample(result, p + 4, (inverse(6) * r + inverse(7) * g + inverse(8) * b) * back)
                    Next
                End Sub)
        End Sub

        Private Shared Sub WriteSample(target As Byte(), offset As Integer, value As Single)
            Dim s = ToSample(value)
            target(offset) = CByte(s And &HFFUS)
            target(offset + 1) = CByte(s >> 8)
        End Sub

        ''' <summary>Eine Kachel durch das Modell und gewichtet ins Band.</summary>
        Private Shared Sub RunTile(session As InferenceSession, inputName As String, condName As String,
                                   condTensor As DenseTensor(Of Single),
                                   samples As Byte(), forward As Single(), scale As Single, band As TileBand,
                                   width As Integer, height As Integer,
                                   x0 As Integer, y0 As Integer, tileW As Integer, tileH As Integer,
                                   rampX As Single(), rampY As Single())
            Dim layer = tileW * tileH
            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, tileH, tileW})
            Dim z = tensor.Buffer
            Threading.Tasks.Parallel.For(0, tileH,
                Sub(yy)
                    ' Ein Bild, das kleiner ist als die Kachel, wird am Rand gespiegelt.
                    Dim sy = Mirror(y0 + yy, height)
                    Dim span = z.Span
                    For xx = 0 To tileW - 1
                        Dim sx = Mirror(x0 + xx, width)
                        Dim r, g, b As Single
                        ReadRec2020(samples, CLng(sy) * width + sx, forward, scale, r, g, b)
                        Dim i = yy * tileW + xx
                        span(i) = Clamp01(r)
                        span(layer + i) = Clamp01(g)
                        span(layer * 2 + i) = Clamp01(b)
                    Next
                End Sub)
            Using run = session.Run(New List(Of NamedOnnxValue) From {
                    NamedOnnxValue.CreateFromTensor(inputName, tensor),
                    NamedOnnxValue.CreateFromTensor(condName, condTensor)})
                Dim output = TryCast(run.First().Value, DenseTensor(Of Single))
                If output Is Nothing Then Return
                Dim values = output.Buffer.Span
                If values.Length < layer * 3 Then Return
                For yy = 0 To tileH - 1
                    Dim ty = y0 + yy
                    If ty >= height Then Exit For
                    Dim bandRow = (ty - band.Top) * width
                    For xx = 0 To tileW - 1
                        Dim tx = x0 + xx
                        If tx >= width Then Exit For
                        Dim i = yy * tileW + xx
                        Dim r = values(i), g = values(layer + i), b = values(layer * 2 + i)
                        ' Ein unsinniger Wert verdirbt nicht den Bildpunkt: dann zaehlt die Kachel
                        ' dort nicht, und eine Nachbarkachel oder der Eingang bleibt stehen.
                        If Single.IsNaN(r) OrElse Single.IsNaN(g) OrElse Single.IsNaN(b) Then Continue For
                        Dim w = rampX(xx) * rampY(yy)
                        Dim bi = bandRow + tx
                        band.Sum(bi * 3) += r * w
                        band.Sum(bi * 3 + 1) += g * w
                        band.Sum(bi * 3 + 2) += b * w
                        band.Weights(bi) += w
                    Next
                Next
            End Using
        End Sub

        ''' <summary>Die Anfaenge der Kacheln in einer Achse: so wenige Kacheln, dass jede ihre
        ''' Nachbarn um mindestens TileOverlap ueberlappt, GLEICHMAESSIG verteilt, die erste am
        ''' Anfang, die letzte buendig mit dem Rand.
        '''
        ''' Die Anfaenge liegen auf Vielfachen von TileAlign, auch der letzte (die Kachel ragt dann
        ''' etwas ueber den Rand); ergibt das irgendwo weniger als TileOverlap, kommt eine Kachel dazu.
        '''
        ''' Vorher ging der Schritt fest um Kachel minus Ueberlapp weiter, und die letzte wurde an
        ''' den Rand gesetzt. Bei 1024 Punkten und 512er Kacheln gab das 0, 480 und 512: die beiden
        ''' letzten deckten sich auf 480 Punkten, und dort mischten sich zwei Modellergebnisse zu
        ''' gleichen Teilen (Review-Befund). Gleich verteilt ueberlappen alle Nachbarn gleich weit, und
        ''' TileRamp blendet ueber genau diese Strecke.</summary>
        Private Shared Function TileStarts(length As Integer, tile As Integer) As List(Of Integer)
            Dim starts As New List(Of Integer)
            If length <= tile Then
                starts.Add(0)
                Return starts
            End If
            Dim stepSize = Math.Max(1, tile - TileOverlap)
            Dim count = Math.Max(2, CInt(Math.Ceiling((length - TileOverlap) / CDbl(stepSize))))
            Do
                starts.Clear()
                For i = 0 To count - 2
                    starts.Add(TileAlign * CInt(Math.Round(i * (length - tile) / CDbl(count - 1) / TileAlign)))
                Next
                ' Auch die letzte auf dem Raster: sie ragt dann um weniger als TileAlign ueber den
                ' Rand hinaus, und RunTile spiegelt dort wie bei einem Bild, das kleiner ist als die
                ' Kachel. Was jenseits des Randes herauskommt, wird verworfen.
                starts.Add(TileAlign * CInt(Math.Ceiling((length - tile) / CDbl(TileAlign))))
                Dim ok = True
                For i = 1 To starts.Count - 1
                    If starts(i - 1) + tile - starts(i) < TileOverlap Then ok = False
                Next
                If ok Then Return starts
                count += 1
            Loop
        End Function

        ''' <summary>Gewicht je Spalte oder Zeile der Kachel <paramref name="index"/>: zu jedem
        ''' Nachbarn hin linear von fast null auf eins ueber die TATSAECHLICHE Ueberlappung mit ihm,
        ''' innen eins, am Bildrand (kein Nachbar) eins. Genommen wird das Kleinere beider Seiten,
        ''' damit das Gewicht stetig bleibt, auch wenn sich zwei Rampen in einer schmalen Kachel
        ''' treffen. Zwei Nachbarn ergaenzen sich in ihrer Ueberlappung zu eins; geteilt wird
        ''' ohnehin durch die Summe der Gewichte.</summary>
        Private Shared Function TileRamp(starts As List(Of Integer), index As Integer, tile As Integer) As Single()
            Dim leftOverlap = If(index > 0, starts(index - 1) + tile - starts(index), 0)
            Dim rightOverlap = If(index < starts.Count - 1, starts(index) + tile - starts(index + 1), 0)
            Dim r(tile - 1) As Single
            For k = 0 To tile - 1
                Dim v = 1.0F
                If leftOverlap > 0 Then v = Math.Min(v, (k + 0.5F) / leftOverlap)
                If rightOverlap > 0 Then v = Math.Min(v, (tile - k - 0.5F) / rightOverlap)
                r(k) = v
            Next
            Return r
        End Function

        Private Shared Function RoundUp64(value As Integer) As Integer
            Return ((value + 63) \ 64) * 64
        End Function

        Private Shared Function Mirror(i As Integer, length As Integer) As Integer
            If length <= 1 Then Return 0
            Dim period = 2 * (length - 1)
            Dim m = i Mod period
            If m < 0 Then m += period
            Return If(m < length, m, period - m)
        End Function

        Private Shared Function Clamp01(v As Single) As Single
            Return If(v < 0.0F, 0.0F, If(v > 1.0F, 1.0F, v))
        End Function

        Private Shared Function ToSample(v As Single) As UShort
            If Single.IsNaN(v) OrElse v <= 0.0F Then Return 0US
            If v >= 65535.0F Then Return 65535US
            Return CUShort(Math.Round(v))
        End Function

        Private Shared Function Multiply3(a As Single(), b As Single()) As Single()
            Dim m(8) As Single
            For r = 0 To 2
                For c = 0 To 2
                    m(r * 3 + c) = a(r * 3) * b(c) + a(r * 3 + 1) * b(3 + c) + a(r * 3 + 2) * b(6 + c)
                Next
            Next
            Return m
        End Function

        Friend Shared Function Invert3(m As Single()) As Single()
            Dim a = CDbl(m(0)), b = CDbl(m(1)), c = CDbl(m(2))
            Dim d = CDbl(m(3)), e = CDbl(m(4)), f = CDbl(m(5))
            Dim g = CDbl(m(6)), h = CDbl(m(7)), k = CDbl(m(8))
            Dim det = a * (e * k - f * h) - b * (d * k - f * g) + c * (d * h - e * g)
            If Math.Abs(det) < 0.000000000001 Then Return Nothing
            Return New Single() {
                CSng((e * k - f * h) / det), CSng((c * h - b * k) / det), CSng((b * f - c * e) / det),
                CSng((f * g - d * k) / det), CSng((a * k - c * g) / det), CSng((c * d - a * f) / det),
                CSng((d * h - e * g) / det), CSng((b * g - a * h) / det), CSng((a * e - b * d) / det)}
        End Function

        ''' <summary>Legt ein Bild auf Bgra8888 um. Nothing, wenn das nicht gelingt; das
        ''' uebergebene Bitmap bleibt in jedem Fall dem Aufrufer. Gebraucht vom Hochskalieren.</summary>
        Friend Shared Function AsBgra8888(bitmap As SKBitmap) As SKBitmap
            If bitmap Is Nothing Then Return Nothing
            Dim converted As SKBitmap = Nothing
            Try
                converted = New SKBitmap(New SKImageInfo(bitmap.Width, bitmap.Height,
                                                         SKColorType.Bgra8888, bitmap.AlphaType))
                Using canvas = New SKCanvas(converted)
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(bitmap, 0, 0, paint)
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("DenoiseModelService.AsBgra8888", ex)
                converted?.Dispose()
                Return Nothing
            End Try
            Return converted
        End Function

    End Class

End Namespace
