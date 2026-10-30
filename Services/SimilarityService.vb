Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Numerics
Imports SkiaSharp

Namespace Services

    ''' <summary>Wie eng "aehnlich" gefasst wird: die groesste Zahl abweichender Bits zweier
    ''' Fingerabdruecke, die noch als aehnlich gilt.</summary>
    Public Enum SimilarityLevel
        ''' Fast gleiche Aufnahmen: Serienbilder, leicht verschobener Ausschnitt.
        Strict = 5
        ''' Dasselbe Motiv in einer Serie, die Vorgabe.
        Balanced = 10
        ''' Aehnliche Szenen; mehr Treffer, mehr Fehlgriffe.
        Loose = 16
    End Enum

    ''' <summary>
    ''' Findet aehnliche Bilder ueber einen Fingerabdruck aus 64 Bit (Differenz-Hash), ohne Modell.
    '''
    ''' DER FINGERABDRUCK: das Bild auf 9 mal 8 Punkte Helligkeit verkleinert; je Zeile acht Bit,
    ''' gesetzt, wo ein Punkt heller ist als sein rechter Nachbar. Das haelt Helligkeit, Groesse und
    ''' leichte Unschaerfe aus, also genau das, worin sich die Aufnahmen einer Serie unterscheiden.
    ''' Zwei Abdruecke vergleicht die Zahl abweichender Bits.
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
        Private Const HashVersion As String = "d1"

        Private Shared ReadOnly _cache As New ConcurrentDictionary(Of String, ULong)(StringComparer.Ordinal)
        Private Shared ReadOnly _fileLock As New Object()
        Private Shared _loaded As Boolean

        ''' <summary>Ablage der Fingerabdruecke; im Pruefstand auf einen eigenen Ort umgelenkt.</summary>
        Friend Shared Property CacheFilePath As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix", "similarity.tsv")

        Private Sub New()
        End Sub

        ''' <summary>Fingerabdruck der Datei, aus dem Zwischenspeicher oder neu gerechnet. Nothing,
        ''' wenn sich kein Bild lesen liess. Gehoert auf einen Hintergrundfaden.</summary>
        Public Shared Function Fingerprint(filePath As String) As ULong?
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
            Dim cached As ULong
            If _cache.TryGetValue(key, cached) Then Return cached

            Dim hash As ULong? = Nothing
            Try
                Using bitmap = LoadSmallBitmap(filePath, info)
                    If bitmap IsNot Nothing Then hash = DifferenceHash(bitmap)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Similarity.Fingerprint", ex)
            End Try
            If hash.HasValue Then Remember(key, hash.Value)
            Return hash
        End Function

        ''' <summary>Zahl der abweichenden Bits, 0 bis 64.</summary>
        Public Shared Function Distance(a As ULong, b As ULong) As Integer
            Return BitOperations.PopCount(a Xor b)
        End Function

        ''' <summary>Der Differenz-Hash eines Bildes, siehe Klassenbeschreibung. Friend fuer den
        ''' Pruefstand.</summary>
        Friend Shared Function DifferenceHash(bitmap As SKBitmap) As ULong
            Dim info As New SKImageInfo(9, 8, SKColorType.Rgba8888, SKAlphaType.Premul)
            Using small = bitmap.Resize(info, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                If small Is Nothing Then Return 0UL
                Dim pixels = small.Bytes
                Dim stride = small.RowBytes
                Dim luma(71) As Integer
                For y = 0 To 7
                    For x = 0 To 8
                        Dim p = y * stride + x * 4
                        luma(y * 9 + x) = CInt(pixels(p)) * 299 + CInt(pixels(p + 1)) * 587 + CInt(pixels(p + 2)) * 114
                    Next
                Next
                Dim hash As ULong = 0UL
                For y = 0 To 7
                    For x = 0 To 7
                        hash <<= 1
                        If luma(y * 9 + x) > luma(y * 9 + x + 1) Then hash = hash Or 1UL
                    Next
                Next
                Return hash
            End Using
        End Function

        ''' <summary>Fasst aehnliche Bilder zu Gruppen zusammen. Zwei Bilder gehoeren zusammen, wenn
        ''' ihre Abdruecke hoechstens <paramref name="level"/> Bits auseinanderliegen, und das gilt
        ''' weiter: ist A wie B und B wie C, stehen alle drei in einer Gruppe. Zurueck kommen nur
        ''' Gruppen mit mindestens zwei Bildern, in der Reihenfolge ihres ersten Mitglieds.</summary>
        Public Shared Function Cluster(Of T)(items As IList(Of T), hashOf As Func(Of T, ULong),
                                              level As SimilarityLevel) As List(Of List(Of T))
            Dim count = If(items Is Nothing, 0, items.Count)
            Dim result As New List(Of List(Of T))()
            If count < 2 Then Return result
            Dim maxDistance = CInt(level)
            Dim parent(count - 1) As Integer
            Dim hashes(count - 1) As ULong
            For i = 0 To count - 1
                parent(i) = i
                hashes(i) = hashOf(items(i))
            Next

            Dim find = Function(x As Integer) As Integer
                           Dim root = x
                           While parent(root) <> root
                               root = parent(root)
                           End While
                           While parent(x) <> root
                               Dim nextIndex = parent(x)
                               parent(x) = root
                               x = nextIndex
                           End While
                           Return root
                       End Function

            For i = 0 To count - 2
                For j = i + 1 To count - 1
                    If BitOperations.PopCount(hashes(i) Xor hashes(j)) <= maxDistance Then
                        Dim a = find(i)
                        Dim b = find(j)
                        If a <> b Then parent(Math.Max(a, b)) = Math.Min(a, b)
                    End If
                Next
            Next

            Dim byRoot As New Dictionary(Of Integer, List(Of T))()
            Dim order As New List(Of Integer)()
            For i = 0 To count - 1
                Dim root = find(i)
                Dim group As List(Of T) = Nothing
                If Not byRoot.TryGetValue(root, group) Then
                    group = New List(Of T)()
                    byRoot(root) = group
                    order.Add(root)
                End If
                group.Add(items(i))
            Next
            For Each root In order
                If byRoot(root).Count >= 2 Then result.Add(byRoot(root))
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
                            Dim value As ULong
                            If ULong.TryParse(line.Substring(tab + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, value) Then
                                _cache(line.Substring(0, tab)) = value
                            End If
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
        Private Shared Sub Remember(key As String, value As ULong)
            _cache(key) = value
            SyncLock _fileLock
                Try
                    Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath))
                    File.AppendAllText(CacheFilePath,
                                       key & ControlChars.Tab & value.ToString("X16", CultureInfo.InvariantCulture) & Environment.NewLine)
                Catch ex As Exception
                    DiagnosticLogService.LogException("Similarity.Remember", ex)
                End Try
            End SyncLock
        End Sub

    End Class

End Namespace
