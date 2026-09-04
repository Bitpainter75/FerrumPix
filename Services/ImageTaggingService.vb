Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ''' <summary>Lokale automatische Bildverschlagwortung mit RAM++ ONNX. Das Modell sieht nur
    ''' Pixel im Speicher; weder Bild noch Ergebnis verlassen den Rechner.
    '''
    ''' RAM++ arbeitet als Multi-Label-Modell: jeder Begriff besitzt einen eigenen Logit und eine
    ''' eigene, mit dem Modell gelieferte Mindestschwelle. Eine Softmax-Auswahl wäre hier falsch,
    ''' weil ein Foto gleichzeitig Hund, Strand und Sonnenuntergang enthalten kann.</summary>
    Public NotInheritable Class ImageTaggingService

        Private Sub New()
        End Sub

        Public Const ModelKey As String = "ram-plus"
        Public Const TagsKey As String = "ram-plus-tags"
        Public Const ThresholdsKey As String = "ram-plus-thresholds"
        Private Const ModelEdge As Integer = 384
        ''' <summary>Kurzlebige Originalkopien für Server-Analysen und -XMP-Synchronisation.
        ''' Sie liegen bewusst nicht im deterministischen Viewer-Cache der Serverquellen.</summary>
        Public Shared ReadOnly WorkFolder As String = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix", "ai-work")

        Private Shared ReadOnly _catalogLock As New Object()
        Private Shared _tags As String()
        Private Shared _thresholds As Single()
        Private Shared _catalogTagsFile As String = ""
        Private Shared _catalogThresholdsFile As String = ""

        Public Shared ReadOnly Property Available As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(ModelKey)) AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(TagsKey)) AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(ThresholdsKey))
            End Get
        End Property

        Public Shared ReadOnly Property Enabled As Boolean
            Get
                Return Available AndAlso AppSettingsService.Load().AiTaggingEnabled
            End Get
        End Property

        Public Shared ReadOnly Property ModelVersion As String
            Get
                Return If(AiModelService.BestFile(ModelKey), "")
            End Get
        End Property

        ''' <summary>Alles, was die Auswahl der Treffer beeinflusst, ist Teil des Scan-Stempels.
        ''' Dadurch führt eine strengere/lockerere Einstellung oder eine Änderung der Pipeline
        ''' gezielt zu einer Neuberechnung des Bestands statt zu dauerhaft gemischten Ergebnissen.</summary>
        Public Shared ReadOnly Property AnalysisVersion As String
            Get
                Dim settings = AppSettingsService.Load()
                Dim maximum = Math.Max(1, Math.Min(50, settings.AiTaggingMaximumTags))
                Dim minimum = Math.Max(0, Math.Min(100, settings.AiTaggingMinimumConfidence))
                Return $"{ModelVersion}|pipeline-2-upright-preview|maximum-{maximum}|minimum-{minimum}"
            End Get
        End Property

        ''' <summary>Analysiert eine lokale Datei und ersetzt ihren bisherigen KI-Stand.
        '''
        ''' UNTERSCHEIDET ZWEI LEERE ANTWORTEN: eine leere Liste heisst "gelaufen, nichts erkannt"
        ''' und ist im Katalog vermerkt; NOTHING heisst "gar nicht gelaufen" - fehlendes Modell,
        ''' Abbruch oder ein Fehler. Nur so kann der Aufrufer zaehlen, was wirklich geschehen ist,
        ''' und nur so bleibt ein Bild ohne Treffer nicht ewig auf der Liste.</summary>
        Public Shared Function TagFile(filePath As String, Optional cancel As Threading.CancellationToken = Nothing) As List(Of AiImageTag)
            If String.IsNullOrWhiteSpace(filePath) OrElse Not Available OrElse cancel.IsCancellationRequested Then Return Nothing
            Dim session = AiModelService.SessionFor(ModelKey)
            If session Is Nothing Then Return Nothing
            EnsureCatalog()
            If _tags Is Nothing OrElse _thresholds Is Nothing OrElse _tags.Length <> _thresholds.Length Then Return Nothing

            Try
                Dim info As New FileInfo(filePath)
                Dim stamp = If(info.Exists, info.LastWriteTime.ToString("o"), "")
                ' DURCH DIE DECODE-SCHLEUSE, und nur der Decode. Es laeuft immer nur einer in der
                ' ganzen Anwendung (siehe DecodeGate); ohne sie stuende dieser hier neben dem Bild
                ' im Betrachter und neben den Kacheln der Galerie. Das Modell selbst bleibt
                ' DRAUSSEN: ein Ordner braucht Minuten, und solange stuende sonst jede Anzeige.
                Using source = DecodeGate.Run(Function() DecodeUpright(filePath))
                    If source Is Nothing OrElse source.Width < 1 OrElse source.Height < 1 Then
                        ' Nicht lesbar: trotzdem als analysiert vermerken, sonst versucht es jeder
                        ' weitere Kataloglauf erneut - bei einem Bestand voller RAWs oder Videos
                        ' waere das ein Decode-Versuch je Datei und Lauf, dauerhaft.
                        LibraryService.Instance.ReplaceAiTags(filePath, New List(Of AiImageTag)(),
                                                              stamp, ModelKey, AnalysisVersion)
                        Return New List(Of AiImageTag)()
                    End If

                    Dim scored = Run(session, source, cancel)
                    If cancel.IsCancellationRequested Then Return Nothing
                    LibraryService.Instance.ReplaceAiTags(filePath, scored, stamp, ModelKey, AnalysisVersion)
                    If AppSettingsService.Load().WriteAiTagsToXmp Then
                        LibraryService.Instance.SyncAiTagsToXmp(filePath)
                    End If
                    Return scored
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("KIStichwörter.Analysieren", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Ermittelt Begriffe ohne einen Eintrag im lokalen Bildkatalog zu schreiben.
        ''' Server-Assets speichern das Ergebnis unter ihrer stabilen Server-ID in ihrem eigenen
        ''' lokalen Metadaten-Index.</summary>
        Public Shared Function AnalyzeFile(filePath As String, Optional cancel As Threading.CancellationToken = Nothing) As List(Of AiImageTag)
            If String.IsNullOrWhiteSpace(filePath) OrElse Not Available OrElse cancel.IsCancellationRequested Then Return Nothing
            Dim session = AiModelService.SessionFor(ModelKey)
            If session Is Nothing Then Return Nothing
            EnsureCatalog()
            If _tags Is Nothing OrElse _thresholds Is Nothing OrElse _tags.Length <> _thresholds.Length Then Return Nothing
            Try
                Using source = DecodeGate.Run(Function() DecodeUpright(filePath))
                    If source Is Nothing OrElse source.Width < 1 OrElse source.Height < 1 Then Return New List(Of AiImageTag)()
                    If cancel.IsCancellationRequested Then Return Nothing
                    Dim scored = Run(session, source, cancel)
                    ' UND NOCH EINMAL DANACH. Ein Abbruch mitten im Lauf verlaesst Run mit einer
                    ' LEEREN Liste, und die heisst "gelaufen, nichts erkannt" - der Aufrufer schriebe
                    ' sie mit dem aktuellen Serverstempel weg, und das Bild waere dauerhaft als
                    ' analysiert vermerkt, ohne je analysiert worden zu sein.
                    If cancel.IsCancellationRequested Then Return Nothing
                    Return scored
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("KIStichwörter.ServerAnalysieren", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Bildpunkte einer beliebigen anzeigbaren Datei, AUFRECHT.
        '''
        ''' Ueber <see cref="ThumbnailCacheService.OpenThumbnailSource"/> statt ueber ein blankes
        ''' SKBitmap.Decode der Datei: das kennt nur JPEG, PNG und Verwandte und liefert bei RAW,
        ''' HEIF, PSD, SVG, .fpx und jedem Video schlicht NICHTS - genau die Haelfte eines echten
        ''' Bestands waere nie verschlagwortet worden.
        '''
        ''' Und AUFRECHT, weil SKCodec die Bildpunkte so liefert, wie sie in der Datei liegen, und
        ''' das EXIF-Feld fuer die Drehung unbeachtet laesst. Ein Hochformat vom Handy ginge sonst
        ''' LIEGEND ins Modell; das erkennt darauf wenig bis nichts - ohne Fehler, ohne Meldung, und
        ''' das Bild gilt danach als erledigt. Dieselbe Falle wie bei der Gesichtssuche (siehe
        ''' <see cref="FaceScanRunner"/>).</summary>
        Private Shared Function DecodeUpright(filePath As String) As SKBitmap
            Using stream = ThumbnailCacheService.OpenThumbnailSource(filePath)
                If stream Is Nothing Then Return Nothing
                ' Ein einziger Codec fuer Drehung UND Bildpunkte: ein getrenntes Auslesen muesste
                ' den Strom zurueckspulen, und die ausgepackten Vorschauen sind nicht alle spulbar.
                Using codec = SKCodec.Create(stream)
                    If codec Is Nothing Then Return Nothing
                    Return FaceScanRunner.ApplyOrientationOwned(SKBitmap.Decode(codec), codec.EncodedOrigin)
                End Using
            End Using
        End Function

        Public Shared Function NeedsAnalysis(filePath As String, lastWriteTime As DateTime) As Boolean
            If Not Enabled Then Return False
            Return LibraryService.Instance.AiTagsNeedRefresh(filePath, lastWriteTime.ToString("o"), ModelKey, AnalysisVersion)
        End Function

        ''' <summary>Räumt Arbeitskopien eines zuvor abgebrochenen Serverlaufs auf. Erfolgreiche
        ''' Läufe entfernen ihre Datei selbst; dieser Schritt behandelt nur den Prozessabbruch.
        ''' Er wird vor einem neuen Serverlauf aufgerufen, wenn in diesem Prozess keine solche
        ''' Arbeit laufen kann.</summary>
        Public Shared Sub ClearAbandonedWorkCopies()
            Try
                If Not Directory.Exists(WorkFolder) Then Return
                For Each filePath In Directory.EnumerateFiles(WorkFolder)
                    Try
                        File.Delete(filePath)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("KIStichwörter.ArbeitskopieAufräumen", ex)
                    End Try
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("KIStichwörter.ArbeitsordnerAufräumen", ex)
            End Try
        End Sub

        Private Shared Sub EnsureCatalog()
            SyncLock _catalogLock
                Dim tagsFile = AiModelService.BestFile(TagsKey)
                Dim thresholdsFile = AiModelService.BestFile(ThresholdsKey)
                Dim tagsStamp = CatalogFileStamp(tagsFile)
                Dim thresholdsStamp = CatalogFileStamp(thresholdsFile)
                If _tags IsNot Nothing AndAlso _thresholds IsNot Nothing AndAlso
                   String.Equals(_catalogTagsFile, tagsStamp, StringComparison.Ordinal) AndAlso
                   String.Equals(_catalogThresholdsFile, thresholdsStamp, StringComparison.Ordinal) Then Return
                Try
                    ' Ein Modellupdate kann die Reihenfolge des Vokabulars ändern. Den alten
                    ' Speicherstand dann keinesfalls weiterverwenden: falsche Indizes wären falsche
                    ' Begriffe ohne sichtbaren Fehler.
                    _tags = Nothing
                    _thresholds = Nothing
                    _catalogTagsFile = ""
                    _catalogThresholdsFile = ""
                    Dim tagPath = AiModelService.ModelPath(tagsFile)
                    Dim thresholdPath = AiModelService.ModelPath(thresholdsFile)
                    If String.IsNullOrEmpty(tagPath) OrElse String.IsNullOrEmpty(thresholdPath) Then Return
                    Dim tags = File.ReadAllLines(tagPath).Select(Function(t) t.Trim()).ToArray()
                    Dim thresholds = File.ReadAllLines(thresholdPath).
                        Select(Function(t)
                                   Dim value As Single
                                   Return If(Single.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, value), value, 1.0F)
                               End Function).ToArray()
                    If tags.Length = 0 OrElse tags.Length <> thresholds.Length Then
                        DiagnosticLogService.LogAlways("KIStichwörter", "Stichwortliste und Schwellenwerte passen nicht zusammen")
                        Return
                    End If
                    _tags = tags
                    _thresholds = thresholds
                    _catalogTagsFile = tagsStamp
                    _catalogThresholdsFile = thresholdsStamp
                Catch ex As Exception
                    DiagnosticLogService.LogException("KIStichwörter.Vokabular", ex)
                End Try
            End SyncLock
        End Sub

        Private Shared Function CatalogFileStamp(fileName As String) As String
            Try
                Dim path = AiModelService.ModelPath(fileName)
                If String.IsNullOrEmpty(path) Then Return ""
                Dim info As New FileInfo(path)
                Return path & "|" & info.Length.ToString(CultureInfo.InvariantCulture) & "|" &
                       info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
            Catch
                Return ""
            End Try
        End Function

        Private Shared Function Run(session As InferenceSession, source As SKBitmap,
                                    cancel As Threading.CancellationToken) As List(Of AiImageTag)
            Dim input(3 * ModelEdge * ModelEdge - 1) As Single
            Using fitted = New SKBitmap(ModelEdge, ModelEdge, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Using canvas = New SKCanvas(fitted)
                    canvas.Clear(SKColors.Black)
                    ImageProcessor.DrawBitmapSampled(canvas, source,
                                                     New SKRect(0, 0, source.Width, source.Height),
                                                     New SKRect(0, 0, ModelEdge, ModelEdge),
                                                     ImageProcessor.SamplingHigh, Nothing)
                End Using
                Dim raw(fitted.RowBytes * ModelEdge - 1) As Byte
                Marshal.Copy(fitted.GetPixels(), raw, 0, raw.Length)
                Dim plane = ModelEdge * ModelEdge
                Dim mean = New Single() {0.485F, 0.456F, 0.406F}
                Dim deviation = New Single() {0.229F, 0.224F, 0.225F}
                For y = 0 To ModelEdge - 1
                    If cancel.IsCancellationRequested Then Return New List(Of AiImageTag)()
                    Dim row = y * fitted.RowBytes
                    For x = 0 To ModelEdge - 1
                        Dim pixel = row + x * 4
                        Dim index = y * ModelEdge + x
                        ' Modellvertrag: auf 384x384 STAUCHEN, RGB/255 und dann ImageNet-
                        ' Normalisierung. Skia liefert BGRA; die Reihenfolge und die drei Zahlen
                        ' sind mit dem offiziellen transform.py gekoppelt und keine Optimierung.
                        input(index) = (raw(pixel + 2) / 255.0F - mean(0)) / deviation(0)
                        input(plane + index) = (raw(pixel + 1) / 255.0F - mean(1)) / deviation(1)
                        input(2 * plane + index) = (raw(pixel) / 255.0F - mean(2)) / deviation(2)
                    Next
                Next
            End Using

            Dim tensor = New DenseTensor(Of Single)(input, New Integer() {1, 3, ModelEdge, ModelEdge})
            Dim name = session.InputMetadata.Keys.First()
            Dim scores As Single()
            Using output = session.Run(New NamedOnnxValue() {NamedOnnxValue.CreateFromTensor(name, tensor)})
                scores = output.First().AsTensor(Of Single)().ToArray()
            End Using

            Dim settings = AppSettingsService.Load()
            Dim minimum = Math.Max(0.0F, Math.Min(1.0F, settings.AiTaggingMinimumConfidence / 100.0F))
            Dim maximum = Math.Max(1, Math.Min(50, settings.AiTaggingMaximumTags))
            Dim result As New List(Of AiImageTag)()
            Dim count = Math.Min(scores.Length, _tags.Length)
            For i = 0 To count - 1
                Dim logit = Math.Max(-30.0F, Math.Min(30.0F, scores(i)))
                Dim score = CSng(1.0 / (1.0 + Math.Exp(-logit)))
                If score < Math.Max(minimum, _thresholds(i)) Then Continue For
                result.Add(New AiImageTag With {.Canonical = _tags(i), .Score = score,
                                                   .ModelKey = ModelKey, .ModelVersion = AnalysisVersion})
            Next
            Return result.OrderByDescending(Function(t) t.Score).ThenBy(Function(t) t.Canonical, StringComparer.OrdinalIgnoreCase).
                          Take(maximum).ToList()
        End Function

    End Class

End Namespace
