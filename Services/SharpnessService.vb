Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports SkiaSharp

Namespace Services

    ''' <summary>Misst, wie scharf eine Aufnahme ist, um in einer Serie die beste nach vorn zu legen.
    '''
    ''' Gemessen wird an einem verkleinerten Bild: beim RAW an der eingebetteten Vorschau, sonst an
    ''' der Datei selbst, beides mit der Verkleinerung des JPEG-Decoders (1/2, 1/4, 1/8), die fast
    ''' nichts kostet. Entwickelt wird nie.
    '''
    ''' <para>DAS MASS: die Streuung des Laplace-Filters (zweite Ableitung der Helligkeit) je Feld
    ''' eines 8x8-Rasters, gemittelt ueber die schaerfsten Felder. Nicht ueber das ganze Bild: ein
    ''' Portraet mit weichem Hintergrund ist dort, wo es zaehlt, scharf, und der Mittelwert ueber
    ''' alles wuerde den Hintergrund mitbewerten. Absolut sagt die Zahl nichts (sie haengt an Motiv,
    ''' ISO und Groesse der Vorschau); verglichen wird nur innerhalb einer Serie, also bei gleicher
    ''' Kamera, gleichem Motiv und gleicher Vorschaugroesse.</para>
    '''
    ''' <para>Die Werte bleiben ueber die Sitzung hinaus in einer kleinen Datei im Anwendungsordner,
    ''' nie neben den Fotos. Der Schluessel enthaelt Groesse und Aenderungszeit der Datei.</para></summary>
    Public NotInheritable Class SharpnessService

        ''' <summary>Laengste Kante, auf die verkleinert gemessen wird. Feiner bringt beim Vergleich
        ''' innerhalb einer Serie nichts mehr, groeber verschluckt leichte Verwacklung.</summary>
        Public Const AnalysisEdge As Integer = 1280

        Private Const GridSize As Integer = 8

        ''' <summary>Anteil der schaerfsten Felder, ueber den gemittelt wird.</summary>
        Private Const TopTileShare As Double = 0.1

        Private Shared ReadOnly _cache As New ConcurrentDictionary(Of String, Double)(StringComparer.Ordinal)
        Private Shared ReadOnly _fileLock As New Object()
        Private Shared _loaded As Boolean

        ''' <summary>Ablage der Messwerte; im Pruefstand auf einen eigenen Ort umgelenkt.</summary>
        Friend Shared Property CacheFilePath As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix", "sharpness.tsv")

        Private Sub New()
        End Sub

        ''' <summary>Schaerfewert der Datei, aus dem Zwischenspeicher oder neu gemessen. Nothing, wenn
        ''' sich kein Bild lesen liess. Laeuft durch <see cref="DecodeGate"/> und gehoert auf einen
        ''' Hintergrundfaden.</summary>
        Public Shared Function Measure(filePath As String) As Double?
            Dim key = CacheKey(filePath)
            If key Is Nothing Then Return Nothing
            EnsureLoaded()
            Dim cached As Double
            If _cache.TryGetValue(key, cached) Then Return cached

            Dim score = DecodeGate.Run(Function() MeasureUncached(filePath))
            If score.HasValue Then Remember(key, score.Value)
            Return score
        End Function

        Private Shared Function MeasureUncached(filePath As String) As Double?
            Try
                Using bitmap = LoadAnalysisBitmap(filePath)
                    If bitmap Is Nothing Then Return Nothing
                    Return ScoreBitmap(bitmap)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Sharpness.Measure", ex)
                Return Nothing
            End Try
        End Function

        Friend Shared Function CacheKey(filePath As String) As String
            Try
                Dim info As New FileInfo(filePath)
                If Not info.Exists Then Return Nothing
                Return String.Concat(info.FullName, "|", info.Length.ToString(CultureInfo.InvariantCulture), "|",
                                     info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Das verkleinerte Bild, an dem gemessen wird. Beim RAW nur die eingebettete
        ''' Vorschau: fehlt sie, gibt es keinen Wert, und die Serie behaelt ihre erste Aufnahme vorn.</summary>
        Private Shared Function LoadAnalysisBitmap(filePath As String) As SKBitmap
            Dim bytes As Byte()
            If RawPreviewService.IsSupportedRaw(filePath) Then
                Using preview = RawPreviewService.ExtractPreview(filePath)
                    If preview Is Nothing OrElse preview.Length = 0 Then Return Nothing
                    bytes = preview.ToArray()
                End Using
            Else
                bytes = File.ReadAllBytes(filePath)
            End If

            Using data = SKData.CreateCopy(bytes)
                Using codec = SKCodec.Create(data)
                    If codec Is Nothing Then Return Nothing
                    Dim longest = Math.Max(codec.Info.Width, codec.Info.Height)
                    If longest <= 0 Then Return Nothing
                    Dim scale = CSng(Math.Min(1.0, AnalysisEdge / CDbl(longest)))
                    Dim size = codec.GetScaledDimensions(scale)
                    If size.Width <= 0 OrElse size.Height <= 0 Then size = New SKSizeI(codec.Info.Width, codec.Info.Height)
                    Dim info As New SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
                    Dim bitmap As New SKBitmap(info)
                    Dim result = codec.GetPixels(info, bitmap.GetPixels())
                    If result <> SKCodecResult.Success AndAlso result <> SKCodecResult.IncompleteInput Then
                        bitmap.Dispose()
                        Return Nothing
                    End If
                    Return bitmap
                End Using
            End Using
        End Function

        ''' <summary>Das Mass selbst, siehe Klassenbeschreibung. Friend fuer den Pruefstand.</summary>
        Friend Shared Function ScoreBitmap(bitmap As SKBitmap) As Double
            If bitmap Is Nothing Then Return 0
            Dim width = bitmap.Width
            Dim height = bitmap.Height
            If width < 3 OrElse height < 3 Then Return 0

            ' Helligkeit grob aus R, G und B; ob die Kanaele als RGBA oder BGRA liegen, ist fuer das
            ' Mass gleich, weil Rot und Blau dasselbe Gewicht tragen.
            Dim pixels = bitmap.Bytes
            Dim stride = bitmap.RowBytes
            Dim channels = bitmap.BytesPerPixel
            Dim luma(width * height - 1) As Single
            For y = 0 To height - 1
                Dim row = y * stride
                Dim target = y * width
                For x = 0 To width - 1
                    Dim p = row + x * channels
                    luma(target + x) = (CSng(pixels(p)) + 2.0F * pixels(p + 1) + pixels(p + 2)) * 0.25F
                Next
            Next

            Dim tileSum(GridSize * GridSize - 1) As Double
            Dim tileSquares(GridSize * GridSize - 1) As Double
            Dim tileCount(GridSize * GridSize - 1) As Integer
            For y = 1 To height - 2
                Dim tileRow = Math.Min(GridSize - 1, y * GridSize \ height) * GridSize
                Dim center = y * width
                For x = 1 To width - 2
                    Dim i = center + x
                    Dim laplace = 4.0F * luma(i) - luma(i - 1) - luma(i + 1) - luma(i - width) - luma(i + width)
                    Dim tile = tileRow + Math.Min(GridSize - 1, x * GridSize \ width)
                    tileSum(tile) += laplace
                    tileSquares(tile) += CDbl(laplace) * laplace
                    tileCount(tile) += 1
                Next
            Next

            Dim variances As New List(Of Double)(tileSum.Length)
            For t = 0 To tileSum.Length - 1
                If tileCount(t) = 0 Then Continue For
                Dim mean = tileSum(t) / tileCount(t)
                variances.Add(Math.Max(0.0, tileSquares(t) / tileCount(t) - mean * mean))
            Next
            If variances.Count = 0 Then Return 0
            variances.Sort()
            variances.Reverse()
            Dim take = Math.Max(3, CInt(Math.Ceiling(variances.Count * TopTileShare)))
            Return variances.Take(take).Average()
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
                            Dim value As Double
                            If Double.TryParse(line.Substring(tab + 1), NumberStyles.Float, CultureInfo.InvariantCulture, value) Then
                                _cache(line.Substring(0, tab)) = value
                            End If
                        Next
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("Sharpness.Load", ex)
                End Try
                _loaded = True
            End SyncLock
        End Sub

        ''' <summary>Haengt den Wert an die Datei an. Eine Zeile je Messung; eine veraltete Zeile
        ''' (Datei geaendert) bleibt stehen, ihr Schluessel trifft nur nie mehr.</summary>
        Private Shared Sub Remember(key As String, value As Double)
            _cache(key) = value
            SyncLock _fileLock
                Try
                    Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath))
                    File.AppendAllText(CacheFilePath,
                                       key & ControlChars.Tab & value.ToString("R", CultureInfo.InvariantCulture) & Environment.NewLine)
                Catch ex As Exception
                    DiagnosticLogService.LogException("Sharpness.Save", ex)
                End Try
            End SyncLock
        End Sub

        ''' <summary>Fuer den Pruefstand: Zwischenspeicher leeren und neu von der Datei lesen.</summary>
        Friend Shared Sub ResetCache()
            SyncLock _fileLock
                _cache.Clear()
                _loaded = False
            End SyncLock
        End Sub
    End Class

End Namespace
