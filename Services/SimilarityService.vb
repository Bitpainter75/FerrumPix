Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports SkiaSharp

Namespace Services

    ''' <summary>Wie eng "aehnlich" gefasst wird. Die Schwelle dazu nennt
    ''' <see cref="SimilarityService.MaxDistance"/>.</summary>
    Public Enum SimilarityLevel
        ''' Fast gleiche Aufnahmen: Serienbilder, leicht verschobener Ausschnitt.
        Strict
        ''' Dasselbe Motiv in einer Serie, die Vorgabe.
        Balanced
        ''' Aehnliche Szenen; mehr Treffer, mehr Fehlgriffe.
        Loose
    End Enum

    ''' <summary>
    ''' Findet aehnliche Bilder ueber einen Fingerabdruck aus einem Grauraster, ohne Modell.
    '''
    ''' DER FINGERABDRUCK: das Bild auf 16 mal 16 Punkte Helligkeit verkleinert, 256 Byte. Zwei
    ''' Abdruecke vergleicht die mittlere Abweichung der Punkte, nachdem von jedem sein eigener
    ''' Mittelwert abgezogen ist; so trennt ein Flackern der Belichtung eine Serie nicht. Das Mass
    ''' ist eine Zahl in Graustufen (0 bis 255).
    '''
    ''' WARUM NICHT MEHR DER DIFFERENZ-HASH: 64 Bit auf 9 mal 8 Punkten sahen nur, wo das Bild grob
    ''' heller oder dunkler wird. Zwei Sonnenuntergaenge ueber einem Feld, Monate auseinander, lagen
    ''' damit so nah wie zwei Bilder einer Serie, und ein leichter Zoom verschob schon so viele Bits,
    ''' dass "Streng" die Serie trennte (Nutzerbefund). Das Raster hat viermal so viele Punkte und
    ''' vergleicht ihre Helligkeit selbst, nicht nur ihre Richtung.
    '''
    ''' WORAUS: zuerst die Kachel aus dem Vorschauspeicher, wenn sie fuer genau diesen Stand der
    ''' Datei schon da ist (klein und schon gedreht). Sonst die Quelle der Kachel
    ''' (ThumbnailCacheService.OpenThumbnailSource, beim RAW die eingebettete Vorschau), mit der
    ''' Verkleinerung des Decoders und nach ihrer Drehangabe gedreht. Entwickelt wird nie.
    '''
    ''' WAS ES NICHT KANN: dieselbe Szene aus einem anderen Blickwinkel erkennt es nicht, und ein
    ''' gespiegeltes oder um 90 Grad gedrehtes Bild gilt als anders. Ein Treffer ist ein Hinweis
    ''' zum Aussortieren, kein Beleg, dass eine Datei entbehrlich ist.
    '''
    ''' Die Werte bleiben ueber die Sitzung hinaus in einer kleinen Datei im Anwendungsordner, nie
    ''' neben den Fotos, mit Groesse und Aenderungszeit im Schluessel (wie SharpnessService).
    ''' </summary>
    Public NotInheritable Class SimilarityService

        ''' Steigt, wenn sich die Rechnung aendert; alte Zeilen treffen dann nicht mehr.
        Private Const HashVersion As String = "g1"

        ''' Kantenlaenge des Grauraster.
        Friend Const GridSize As Integer = 16

        Private Const SignatureLength As Integer = GridSize * GridSize

        ' Die Schwellen der drei Stufen in Graustufen mittlerer Abweichung.
        Private Const StrictDistance As Double = 8.0
        Private Const BalancedDistance As Double = 12.0
        Private Const LooseDistance As Double = 16.0

        Private Shared ReadOnly _cache As New ConcurrentDictionary(Of String, Byte())(StringComparer.Ordinal)
        Private Shared ReadOnly _fileLock As New Object()
        Private Shared _loaded As Boolean

        ''' <summary>Ablage der Fingerabdruecke; im Pruefstand auf einen eigenen Ort umgelenkt.</summary>
        Friend Shared Property CacheFilePath As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix", "similarity.tsv")

        Private Sub New()
        End Sub

        ''' <summary>Die groesste mittlere Abweichung in Graustufen, die auf einer Stufe noch als
        ''' aehnlich gilt.</summary>
        Public Shared Function MaxDistance(level As SimilarityLevel) As Double
            Select Case level
                Case SimilarityLevel.Strict : Return StrictDistance
                Case SimilarityLevel.Loose : Return LooseDistance
                Case Else : Return BalancedDistance
            End Select
        End Function

        ''' <summary>Fingerabdruck der Datei, aus dem Zwischenspeicher oder neu gerechnet. Nothing,
        ''' wenn sich kein Bild lesen liess. Gehoert auf einen Hintergrundfaden.</summary>
        Public Shared Function Fingerprint(filePath As String) As Byte()
            Dim info As FileInfo
            Try
                info = New FileInfo(filePath)
                If Not info.Exists Then Return Nothing
            Catch
                Return Nothing
            End Try
            Dim key = String.Concat(HashVersion, "|", info.FullName, "|", info.Length.ToString(CultureInfo.InvariantCulture), "|",
                                    info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))
            EnsureLoaded()
            Dim cached As Byte() = Nothing
            If _cache.TryGetValue(key, cached) Then Return cached

            Dim signature As Byte() = Nothing
            Try
                Using bitmap = LoadSmallBitmap(filePath, info)
                    If bitmap IsNot Nothing Then signature = GreyGrid(bitmap)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Similarity.Fingerprint", ex)
            End Try
            If signature IsNot Nothing Then Remember(key, signature)
            Return signature
        End Function

        ''' <summary>Mittlere Abweichung zweier Abdruecke in Graustufen, jeder um seinen eigenen
        ''' Mittelwert bereinigt. 0 heisst gleich.</summary>
        Public Shared Function Distance(a As Byte(), b As Byte()) As Double
            Return CenteredDistance(Centered(a), Centered(b), Double.MaxValue)
        End Function

        ''' <summary>Das Grauraster eines Bildes, siehe Klassenbeschreibung. Friend fuer den
        ''' Pruefstand.</summary>
        Friend Shared Function GreyGrid(bitmap As SKBitmap) As Byte()
            Dim info As New SKImageInfo(GridSize, GridSize, SKColorType.Rgba8888, SKAlphaType.Premul)
            Using small = bitmap.Resize(info, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                If small Is Nothing Then Return Nothing
                Dim pixels = small.Bytes
                Dim stride = small.RowBytes
                Dim grid(SignatureLength - 1) As Byte
                For y = 0 To GridSize - 1
                    For x = 0 To GridSize - 1
                        Dim p = y * stride + x * 4
                        Dim luma = (CInt(pixels(p)) * 299 + CInt(pixels(p + 1)) * 587 + CInt(pixels(p + 2)) * 114 + 500) \ 1000
                        grid(y * GridSize + x) = CByte(Math.Min(255, luma))
                    Next
                Next
                Return grid
            End Using
        End Function

        ''' <summary>Der Abdruck als Abweichung von seinem Mittelwert.</summary>
        Private Shared Function Centered(signature As Byte()) As Single()
            Dim result(SignatureLength - 1) As Single
            If signature Is Nothing OrElse signature.Length <> SignatureLength Then Return result
            Dim sum = 0
            For Each v In signature
                sum += v
            Next
            Dim mean = CSng(sum / SignatureLength)
            For i = 0 To SignatureLength - 1
                result(i) = signature(i) - mean
            Next
            Return result
        End Function

        ''' <summary>Mittlere Abweichung zweier bereinigter Abdruecke. Bricht ab, sobald die Summe
        ''' die Grenze ueberschreitet, und meldet dann einen Wert darueber.</summary>
        Private Shared Function CenteredDistance(a As Single(), b As Single(), limit As Double) As Double
            Dim maxSum = If(limit >= Double.MaxValue / SignatureLength, Double.MaxValue, limit * SignatureLength)
            Dim sum = 0.0
            For i = 0 To SignatureLength - 1
                sum += Math.Abs(a(i) - b(i))
                If sum > maxSum Then Return sum / SignatureLength
            Next
            Return sum / SignatureLength
        End Function

        ''' <summary>Fasst aehnliche Bilder zu Gruppen zusammen. Jedes neue Bild muss zu JEDEM
        ''' Mitglied einer Gruppe hoechstens <see cref="MaxDistance"/> Abstand haben. Damit kann
        ''' eine Gruppe nicht ueber eine Kette wachsen: Sind A und B sowie B und C aehnlich, A und C
        ''' aber nicht, bleibt C ausserhalb der Gruppe A/B. Zurueck kommen nur Gruppen mit
        ''' mindestens zwei Bildern, in der Reihenfolge ihres ersten Mitglieds.</summary>
        Public Shared Function Cluster(Of T)(items As IList(Of T), signatureOf As Func(Of T, Byte()),
                                              level As SimilarityLevel) As List(Of List(Of T))
            Dim count = If(items Is Nothing, 0, items.Count)
            Dim result As New List(Of List(Of T))()
            If count < 2 Then Return result
            Dim limit = MaxDistance(level)
            ' Jeder Abdruck wird genau EINMAL geholt und bereinigt. signatureOf ist beim Aufrufer ein
            ' Zugriff ueber den Dateipfad; im Vergleich unten je Gruppenmitglied aufgerufen, waeren
            ' das bei einigen tausend Bildern Millionen Zugriffe statt eines Felds.
            Dim prepared = New Single(count - 1)() {}
            For i = 0 To count - 1
                prepared(i) = Centered(signatureOf(items(i)))
            Next

            ' Die Gruppen fuehren Indizes, nicht die Elemente selbst.
            Dim groups As New List(Of List(Of Integer))()
            For i = 0 To count - 1
                Dim item = prepared(i)
                ' Die Eingabereihenfolge ist die Aufnahmezeit. Gibt es mehrere passende Gruppen,
                ' gewinnt deshalb die aelteste: die Einteilung bleibt bei jedem Lauf gleich.
                Dim target As List(Of Integer) = Nothing
                For Each candidate In groups
                    Dim fitsAll = True
                    For Each member In candidate
                        If CenteredDistance(item, prepared(member), limit) > limit Then
                            fitsAll = False
                            Exit For
                        End If
                    Next
                    If fitsAll Then
                        target = candidate
                        Exit For
                    End If
                Next
                If target Is Nothing Then
                    target = New List(Of Integer)()
                    groups.Add(target)
                End If
                target.Add(i)
            Next

            For Each group In groups
                If group.Count < 2 Then Continue For
                Dim members As New List(Of T)(group.Count)
                For Each index In group
                    members.Add(items(index))
                Next
                result.Add(members)
            Next
            Return result
        End Function

        ''' <summary>Ein kleines Bild der Datei, siehe Klassenbeschreibung.</summary>
        Private Shared Function LoadSmallBitmap(filePath As String, info As FileInfo) As SKBitmap
            Dim cachedThumbnail = ThumbnailCacheService.FindExactCachedThumbnail(filePath, info.LastWriteTime, info.Length)
            If Not String.IsNullOrEmpty(cachedThumbnail) Then
                Dim fromCache = SKBitmap.Decode(cachedThumbnail)
                If fromCache IsNot Nothing Then Return fromCache
            End If

            Return DecodeGate.Run(
                Function() As SKBitmap
                    Using source = ThumbnailCacheService.OpenThumbnailSource(filePath)
                        If source Is Nothing Then Return Nothing
                        Using data = SKData.Create(source)
                            If data Is Nothing Then Return Nothing
                            Using codec = SKCodec.Create(data)
                                If codec Is Nothing Then Return Nothing
                                Dim longest = Math.Max(codec.Info.Width, codec.Info.Height)
                                If longest <= 0 Then Return Nothing
                                Dim size = codec.GetScaledDimensions(CSng(Math.Min(1.0, 256.0 / longest)))
                                If size.Width <= 0 OrElse size.Height <= 0 Then size = New SKSizeI(codec.Info.Width, codec.Info.Height)
                                Dim decodeInfo As New SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
                                Dim bitmap As New SKBitmap(decodeInfo)
                                Dim result = codec.GetPixels(decodeInfo, bitmap.GetPixels())
                                If result <> SKCodecResult.Success AndAlso result <> SKCodecResult.IncompleteInput Then
                                    bitmap.Dispose()
                                    Return Nothing
                                End If
                                Return Rotated(bitmap, codec.EncodedOrigin)
                            End Using
                        End Using
                    End Using
                End Function)
        End Function

        ''' <summary>Dreht nach der Drehangabe der Datei, damit der Abdruck zur Kachel passt. Nur die
        ''' drei Drehungen; gespiegelte Angaben kommen bei Kameras praktisch nicht vor.</summary>
        Private Shared Function Rotated(bitmap As SKBitmap, origin As SKEncodedOrigin) As SKBitmap
            Dim degrees As Integer
            Select Case origin
                Case SKEncodedOrigin.BottomRight : degrees = 180
                Case SKEncodedOrigin.RightTop : degrees = 90
                Case SKEncodedOrigin.LeftBottom : degrees = 270
                Case Else : Return bitmap
            End Select
            Dim swap = degrees <> 180
            Dim result As New SKBitmap(If(swap, bitmap.Height, bitmap.Width), If(swap, bitmap.Width, bitmap.Height),
                                       bitmap.ColorType, bitmap.AlphaType)
            Using canvas As New SKCanvas(result)
                canvas.Translate(result.Width / 2.0F, result.Height / 2.0F)
                canvas.RotateDegrees(degrees)
                canvas.Translate(-bitmap.Width / 2.0F, -bitmap.Height / 2.0F)
                canvas.DrawBitmap(bitmap, 0, 0)
            End Using
            bitmap.Dispose()
            Return result
        End Function

        Private Shared Sub EnsureLoaded()
            If _loaded Then Return
            SyncLock _fileLock
                If _loaded Then Return
                Try
                    If File.Exists(CacheFilePath) Then
                        For Each line In File.ReadLines(CacheFilePath)
                            Dim tab = line.LastIndexOf(ControlChars.Tab)
                            If tab <= 0 Then Continue For
                            ' Zeilen der frueheren Fassung (16 Zeichen) haben die falsche Laenge und
                            ' werden uebergangen; ihr Schluessel traefe ohnehin nicht mehr.
                            Dim hex = line.Substring(tab + 1)
                            If hex.Length <> SignatureLength * 2 Then Continue For
                            Dim value As Byte()
                            Try
                                value = Convert.FromHexString(hex)
                            Catch
                                Continue For
                            End Try
                            _cache(line.Substring(0, tab)) = value
                        Next
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("Similarity.Load", ex)
                End Try
                _loaded = True
            End SyncLock
        End Sub

        ''' <summary>Haengt den Wert an die Datei an. Eine veraltete Zeile (Datei geaendert) bleibt
        ''' stehen, ihr Schluessel trifft nur nie mehr.</summary>
        Private Shared Sub Remember(key As String, value As Byte())
            _cache(key) = value
            SyncLock _fileLock
                Try
                    Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath))
                    File.AppendAllText(CacheFilePath, key & ControlChars.Tab & Convert.ToHexString(value) & Environment.NewLine)
                Catch ex As Exception
                    DiagnosticLogService.LogException("Similarity.Remember", ex)
                End Try
            End SyncLock
        End Sub

    End Class

End Namespace
