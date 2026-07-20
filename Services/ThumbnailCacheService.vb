Imports System
Imports System.Collections.Concurrent
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia.Media.Imaging
Imports SkiaSharp

Namespace Services

    Public Class ThumbnailCacheFolderInfo
        Public Property CacheId As String
        Public Property FolderPath As String
        Public Property ThumbnailCount As Integer
        Public Property SizeBytes As Long
        Public Property Exists As Boolean

        Public ReadOnly Property DisplayName As String
            Get
                If String.IsNullOrEmpty(FolderPath) Then Return $"Unbekannter Ordner ({CacheId})"
                Return FolderPath
            End Get
        End Property

        Public ReadOnly Property DetailText As String
            Get
                Dim status = If(Exists, "vorhanden", "Ordner fehlt")
                Return $"{ThumbnailCount:N0} Bilder · {FormatBytes(SizeBytes)} · {status}"
            End Get
        End Property

        Private Shared Function FormatBytes(bytes As Long) As String
            If bytes < 1024 Then Return $"{bytes:N0} B"
            Dim kb = bytes / 1024.0
            If kb < 1024 Then Return $"{kb:N1} KB"
            Dim mb = kb / 1024.0
            If mb < 1024 Then Return $"{mb:N1} MB"
            Return $"{mb / 1024.0:N1} GB"
        End Function
    End Class

    Public NotInheritable Class ThumbnailCacheService
        Private Sub New()
        End Sub

        Private Const CacheWidth As Integer = 480

        ''' Entspricht der abgekündigten Stufe SKFilterQuality.Medium (linear mit Mipmaps).
        Private Shared ReadOnly SamplingMedium As New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
        Private Const IndexFileName As String = "index.json"

        ' Bei jeder Änderung, die den Pixelinhalt der Cache-Datei betrifft, ohne dass sich
        ' lastWriteTime/fileSize/quality/width ändern (z.B. die EXIF-Orientierungskorrektur),
        ' muss diese Version erhöht werden, damit alte Cache-Dateien automatisch als veraltet
        ' erkannt und neu erzeugt werden (siehe DeleteStaleCacheFiles/FindAnyCachedThumbnail).
        Private Const CacheFormatVersion As Integer = 2

        Private Shared ReadOnly CacheRoot As String =
            IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix", "ThumbnailCache")

        Private Shared ReadOnly RootIndexPath As String = IO.Path.Combine(CacheRoot, IndexFileName)
        Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {.WriteIndented = True}

        ' Settings cache: avoids reading the settings file from disk for every thumbnail
        Private Shared _cachedEnabled As Boolean = True
        Private Shared _cachedQuality As Integer = 80
        Private Shared _settingsCacheExpiry As Long = 0   ' Environment.TickCount64

        ' Per-session debounce: each cache scope is registered in the root index at most once
        Private Shared ReadOnly _registeredFolderIds As New ConcurrentDictionary(Of String, Boolean)()

        ''' <summary>
        ''' Die Dateinamen eines Cache-Ordners, nach dem Bild-Hash gruppiert - einmal je Ordner und Sitzung
        ''' eingelesen. Ohne das durchliefen <see cref="DeleteStaleCacheFiles"/> und
        ''' <see cref="FindAnyCachedThumbnail"/> den Ordner je Vorschaubild einmal komplett. Beim ersten
        ''' Befüllen wächst er dabei mit, was den Aufbau eines Ordners quadratisch machte: 5.000 Bilder
        ''' bedeuteten 5.000 Verzeichnisdurchläufe über bis zu 5.000 Einträge.
        '''
        ''' Das Verzeichnis ist eine Beschleunigung, keine Wahrheit. Fehlt ihm ein Eintrag, bleibt
        ''' höchstens eine veraltete Datei liegen oder ein brauchbares Vorschaubild wird neu erzeugt -
        ''' nie entsteht ein falsches Bild. Ob eine Cache-Datei wirklich da ist, entscheidet weiterhin
        ''' File.Exists in LoadCached.
        ''' </summary>
        Private NotInheritable Class FolderFileIndex
            ' Mehrere Vorschaubild-Worker arbeiten gleichzeitig am selben Ordner.
            Private ReadOnly _lock As New Object()
            Private ReadOnly _byImageHash As New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)
            Private _loaded As Boolean

            ''' Alle Dateinamen zu einem Bild-Hash (in aller Regel null oder einer).
            Public Function GetNames(folderCachePath As String, imageHash As String) As List(Of String)
                SyncLock _lock
                    EnsureLoadedLocked(folderCachePath)
                    Dim names As List(Of String) = Nothing
                    If Not _byImageHash.TryGetValue(imageHash, names) Then Return New List(Of String)()
                    Return New List(Of String)(names)
                End SyncLock
            End Function

            Public Sub Add(folderCachePath As String, imageHash As String, fileName As String)
                SyncLock _lock
                    EnsureLoadedLocked(folderCachePath)
                    Dim names As List(Of String) = Nothing
                    If Not _byImageHash.TryGetValue(imageHash, names) Then
                        names = New List(Of String)()
                        _byImageHash(imageHash) = names
                    End If
                    If Not names.Contains(fileName, StringComparer.OrdinalIgnoreCase) Then names.Add(fileName)
                End SyncLock
            End Sub

            Public Sub Remove(imageHash As String, fileName As String)
                SyncLock _lock
                    Dim names As List(Of String) = Nothing
                    If Not _byImageHash.TryGetValue(imageHash, names) Then Return
                    names.RemoveAll(Function(n) String.Equals(n, fileName, StringComparison.OrdinalIgnoreCase))
                    If names.Count = 0 Then _byImageHash.Remove(imageHash)
                End SyncLock
            End Sub

            Private Sub EnsureLoadedLocked(folderCachePath As String)
                If _loaded Then Return
                _loaded = True
                If Not Directory.Exists(folderCachePath) Then Return
                Try
                    For Each path In Directory.EnumerateFiles(folderCachePath, "*.jpg", SearchOption.TopDirectoryOnly)
                        Dim fileName = IO.Path.GetFileName(path)
                        ' Der Bild-Hash steht vor dem ersten "_" (siehe cacheFileName in CreateOrUpdate).
                        Dim separator = fileName.IndexOf("_"c)
                        If separator <= 0 Then Continue For
                        Dim hash = fileName.Substring(0, separator)
                        Dim names As List(Of String) = Nothing
                        If Not _byImageHash.TryGetValue(hash, names) Then
                            names = New List(Of String)()
                            _byImageHash(hash) = names
                        End If
                        names.Add(fileName)
                    Next
                Catch ex As Exception
                    ' Ein unvollständiges Verzeichnis kostet höchstens ein paar liegengebliebene Dateien.
                    DiagnosticLogService.LogException("ThumbnailCache.IndexFolder", ex)
                End Try
            End Sub
        End Class

        Private Shared ReadOnly _folderFileIndexes As New ConcurrentDictionary(Of String, FolderFileIndex)()

        ' Ein Ordner mit 5.000 Vorschaubildern kostet gut ein Megabyte an Dateinamen. Wer sich durch ein
        ' ganzes Archiv klickt, sammelt sie sonst bis zum Programmende an. Beim Überschreiten wird
        ' pauschal geleert statt verdrängt: ein weggeworfenes Verzeichnis wird beim nächsten Bedarf neu
        ' eingelesen, mehr passiert nicht.
        Private Const MaxCachedFolderIndexes As Integer = 32

        Private Shared Function GetFolderFileIndex(folderId As String) As FolderFileIndex
            If _folderFileIndexes.Count >= MaxCachedFolderIndexes AndAlso Not _folderFileIndexes.ContainsKey(folderId) Then
                _folderFileIndexes.Clear()
            End If
            Return _folderFileIndexes.GetOrAdd(folderId, Function(id) New FolderFileIndex())
        End Function

        ' Der Wurzel-Index wird aus mehreren Threads fortgeschrieben: Vorschaubild-Threads melden neue
        ' Ordner an, während GetFolderCaches im Hintergrund die Kennzahlen nachträgt. Jedes
        ' Lesen-Ändern-Schreiben läuft deshalb unter diesem Schloss.
        Private Shared ReadOnly _rootIndexLock As New Object()

        Private Class ThumbnailCacheRootIndex
            Public Property Folders As New List(Of ThumbnailCacheFolderIndexEntry)()
        End Class

        Private Class ThumbnailCacheFolderIndexEntry
            Public Property Id As String
            Public Property FolderPath As String
            Public Property LastSeenUtc As DateTime

            ''' Zwischengespeicherte Kennzahlen des Cache-Ordners. StatsStampUtc hält den
            ''' Änderungszeitpunkt des Ordners fest, zu dem sie ermittelt wurden: solange er sich nicht
            ''' bewegt hat, wurde keine Datei angelegt oder gelöscht und die Zahlen gelten weiter.
            ''' Ein einziger Verzeichnis-Stat ersetzt so das Abklappern jeder Vorschaudatei.
            Public Property ThumbnailCount As Integer = -1
            Public Property SizeBytes As Long = 0
            Public Property StatsStampUtc As DateTime = DateTime.MinValue
        End Class

        Private Shared Sub GetCachedSettings(ByRef enabled As Boolean, ByRef quality As Integer)
            If Environment.TickCount64 > Volatile.Read(_settingsCacheExpiry) Then
                Dim s = AppSettingsService.Load()
                _cachedEnabled = s.ThumbnailCacheEnabled
                _cachedQuality = AppSettingsService.NormalizeThumbnailQuality(s.ThumbnailQuality)
                Volatile.Write(_settingsCacheExpiry, Environment.TickCount64 + 10_000L)
            End If
            enabled = _cachedEnabled
            quality = _cachedQuality
        End Sub

        Public Shared Sub InvalidateSettingsCache()
            Volatile.Write(_settingsCacheExpiry, 0L)
        End Sub

        Public Shared Function LoadOrCreate(filePath As String, lastWriteTime As DateTime, fileSize As Long, Optional cancellationToken As CancellationToken = Nothing, Optional cacheScopeId As String = Nothing, Optional cacheScopeName As String = Nothing) As Bitmap
            Dim isExact As Boolean = False
            Dim cached = LoadCached(filePath, lastWriteTime, fileSize, False, isExact, cancellationToken, cacheScopeId, cacheScopeName)
            If cached IsNot Nothing Then Return cached
            Return CreateOrUpdate(filePath, lastWriteTime, fileSize, cancellationToken, cacheScopeId, cacheScopeName)
        End Function

        ' SVG/GIF/ICO-Quelldateien sind selbst schon winzig - das Dekodieren kostet kaum mehr als
        ' der Cache-Datei-Zugriff, daher lohnt sich für sie kein Thumbnail-Cache-Eintrag.
        Private Shared ReadOnly UncachedExtensions As String() = {".svg", ".gif", ".ico"}

        Private Shared Function ShouldSkipCache(filePath As String) As Boolean
            Return UncachedExtensions.Contains(IO.Path.GetExtension(filePath).ToLowerInvariant())
        End Function

        Public Shared Function LoadCached(filePath As String,
                                          lastWriteTime As DateTime,
                                          fileSize As Long,
                                          allowStale As Boolean,
                                          ByRef isExact As Boolean,
                                          Optional cancellationToken As CancellationToken = Nothing,
                                          Optional cacheScopeId As String = Nothing,
                                          Optional cacheScopeName As String = Nothing) As Bitmap
            isExact = False
            cancellationToken.ThrowIfCancellationRequested()
            If String.IsNullOrEmpty(filePath) OrElse Not File.Exists(filePath) Then Return Nothing
            If ShouldSkipCache(filePath) Then Return Nothing

            Dim enabled As Boolean
            Dim quality As Integer
            GetCachedSettings(enabled, quality)
            If Not enabled Then Return Nothing

            cancellationToken.ThrowIfCancellationRequested()

            Dim folderId = ResolveCacheId(IO.Path.GetDirectoryName(filePath), cacheScopeId)
            Dim folderCachePath = GetFolderCachePathById(folderId)
            Dim imageHash = HashText(NormalizePath(filePath))
            Dim lastWriteTicksUtc = lastWriteTime.ToUniversalTime().Ticks
            Dim cacheFileName = $"{imageHash}_{lastWriteTicksUtc}_{fileSize}_q{quality}_w{CacheWidth}_v{CacheFormatVersion}.jpg"
            Dim cachePath = IO.Path.Combine(folderCachePath, cacheFileName)

            Try
                If File.Exists(cachePath) Then
                    cancellationToken.ThrowIfCancellationRequested()
                    Using stream = File.OpenRead(cachePath)
                        isExact = True
                        Return New Bitmap(stream)
                    End Using
                End If

                If Not allowStale Then Return Nothing
                Dim staleCachePath = FindAnyCachedThumbnail(folderId, folderCachePath, imageHash)
                If Not String.IsNullOrEmpty(staleCachePath) Then
                    cancellationToken.ThrowIfCancellationRequested()
                    Using stream = File.OpenRead(staleCachePath)
                        Return New Bitmap(stream)
                    End Using
                End If
            Catch ex As OperationCanceledException
                Throw
            Catch
            End Try

            Return Nothing
        End Function

        ''' <summary>isStillWanted: optionaler günstiger Check, der VOR dem teuren Resize/Encode
        ''' abgefragt wird (z.B. "wurde das Element inzwischen weggescrollt?") - erlaubt einem
        ''' Worker, eine bereits nicht mehr benötigte Regenerierung früh und billig abzubrechen,
        ''' statt den vollen Decode+Resize+Encode-Durchlauf zu Ende zu bringen.</summary>
        Public Shared Function CreateOrUpdate(filePath As String, lastWriteTime As DateTime, fileSize As Long, Optional cancellationToken As CancellationToken = Nothing, Optional cacheScopeId As String = Nothing, Optional cacheScopeName As String = Nothing, Optional isStillWanted As Func(Of Boolean) = Nothing) As Bitmap
            cancellationToken.ThrowIfCancellationRequested()
            If String.IsNullOrEmpty(filePath) OrElse Not File.Exists(filePath) Then Return Nothing
            If ShouldSkipCache(filePath) Then Return DecodeDirect(filePath, cancellationToken)

            Dim enabled As Boolean
            Dim quality As Integer
            GetCachedSettings(enabled, quality)
            If Not enabled Then Return DecodeDirect(filePath, cancellationToken)

            Dim folderDisplayPath = ResolveCacheDisplayPath(filePath, cacheScopeId, cacheScopeName)
            Dim folderId = ResolveCacheId(IO.Path.GetDirectoryName(filePath), cacheScopeId)
            Dim folderCachePath = GetFolderCachePathById(folderId)
            Dim imageHash = HashText(NormalizePath(filePath))
            Dim lastWriteTicksUtc = lastWriteTime.ToUniversalTime().Ticks
            Dim cacheFileName = $"{imageHash}_{lastWriteTicksUtc}_{fileSize}_q{quality}_w{CacheWidth}_v{CacheFormatVersion}.jpg"
            Dim cachePath = IO.Path.Combine(folderCachePath, cacheFileName)

            Try
                Directory.CreateDirectory(folderCachePath)
                ' Register the folder in the root index once per session (not per thumbnail)
                EnsureFolderRegistered(folderId, folderDisplayPath)
                DeleteStaleCacheFiles(folderId, folderCachePath, imageHash, cacheFileName)
                cancellationToken.ThrowIfCancellationRequested()

                If TryWriteCacheFile(filePath, cachePath, quality, isStillWanted) AndAlso File.Exists(cachePath) Then
                    GetFolderFileIndex(folderId).Add(folderCachePath, imageHash, cacheFileName)
                    cancellationToken.ThrowIfCancellationRequested()
                    Using stream = File.OpenRead(cachePath)
                        Return New Bitmap(stream)
                    End Using
                End If
            Catch ex As OperationCanceledException
                Throw
            Catch
            End Try

            If isStillWanted IsNot Nothing AndAlso Not isStillWanted() Then Return Nothing
            Return DecodeDirect(filePath, cancellationToken)
        End Function

        Public Shared Function DeleteAllCaches() As Integer
            _registeredFolderIds.Clear()
            _folderFileIndexes.Clear()
            InvalidateSettingsCache()
            If Not Directory.Exists(CacheRoot) Then Return 0
            Try
                Dim count = Directory.GetFiles(CacheRoot, "*.jpg", SearchOption.AllDirectories).Length
                Directory.Delete(CacheRoot, True)
                Return count
            Catch
                Return 0
            End Try
        End Function

        Public Shared Function DeleteFolderCache(folderPath As String) As Integer
            If String.IsNullOrEmpty(folderPath) Then Return 0
            Return DeleteFolderCacheById(GetFolderCacheId(folderPath))
        End Function

        Public Shared Function DeleteFolderCacheById(cacheId As String) As Integer
            If String.IsNullOrEmpty(cacheId) OrElse cacheId <> IO.Path.GetFileName(cacheId) Then Return 0
            Dim folderCachePath = IO.Path.Combine(CacheRoot, cacheId)
            If Not Directory.Exists(folderCachePath) Then Return 0

            Try
                Dim count = Directory.GetFiles(folderCachePath, "*.jpg", SearchOption.TopDirectoryOnly).Length
                Directory.Delete(folderCachePath, True)
                RemoveFolderFromRootIndex(cacheId)
                _registeredFolderIds.TryRemove(cacheId, Nothing)
                ' Sonst zeigte das Namensverzeichnis auf Dateien, die es nicht mehr gibt.
                _folderFileIndexes.TryRemove(cacheId, Nothing)
                Return count
            Catch
                Return 0
            End Try
        End Function

        Public Shared Function DeleteSearchListCache(searchListId As String) As Integer
            If String.IsNullOrWhiteSpace(searchListId) Then Return 0
            Return DeleteFolderCacheById("searchlist_" & searchListId)
        End Function

        ''' <summary>
        ''' Kennzahlen aller Cache-Ordner. Gezählt wird nur, wenn sich ein Ordner seit der letzten
        ''' Erhebung verändert hat - erkennbar an seinem Änderungszeitpunkt, den das Dateisystem
        ''' fortschreibt, sobald ein Eintrag hinzukommt oder verschwindet. Im Normalfall kostet die
        ''' Abfrage damit einen Stat je Ordner statt einen je Vorschaubild.
        '''
        ''' Läuft potenziell lange (beim ersten Mal oder nach vielen neuen Vorschaubildern) und gehört
        ''' deshalb nicht auf den UI-Thread - siehe GetFolderCachesAsync.
        ''' </summary>
        Public Shared Function GetFolderCaches() As List(Of ThumbnailCacheFolderInfo)
            Dim result As New List(Of ThumbnailCacheFolderInfo)()
            If Not Directory.Exists(CacheRoot) Then Return result

            ' Bekannte Ordner aus dem Index, dazu alle Verzeichnisse auf der Platte, die (noch) nicht
            ' darin stehen. Der Index wird hier nur gelesen; geschrieben wird erst am Ende, gesammelt.
            Dim rootIndex = LoadRootIndex()
            Dim entries = rootIndex.Folders.
                Where(Function(f) f IsNot Nothing AndAlso Not String.IsNullOrEmpty(f.Id)).
                ToDictionary(Function(f) f.Id, Function(f) f, StringComparer.OrdinalIgnoreCase)

            For Each folderCachePath In Directory.GetDirectories(CacheRoot)
                Dim folderId = IO.Path.GetFileName(folderCachePath)
                If Not entries.ContainsKey(folderId) Then
                    entries(folderId) = New ThumbnailCacheFolderIndexEntry With {
                        .Id = folderId,
                        .FolderPath = "",
                        .LastSeenUtc = DateTime.UtcNow
                    }
                End If
            Next

            Dim freshStats As New Dictionary(Of String, (Count As Integer, SizeBytes As Long, StampUtc As DateTime))(StringComparer.OrdinalIgnoreCase)

            For Each folder In entries.Values
                Try
                    Dim folderCachePath = IO.Path.Combine(CacheRoot, folder.Id)
                    If Not Directory.Exists(folderCachePath) Then Continue For

                    Dim stamp = Directory.GetLastWriteTimeUtc(folderCachePath)
                    If folder.ThumbnailCount < 0 OrElse folder.StatsStampUtc <> stamp Then
                        Dim count = 0
                        Dim sizeBytes = 0L
                        ' EnumerateFiles statt GetFiles + New FileInfo(pfad): die zurückgegebenen
                        ' FileInfo-Objekte sind bereits aus dem Verzeichniseintrag befüllt, die Größe
                        ' kostet also keinen zweiten Zugriff je Datei.
                        For Each file In New DirectoryInfo(folderCachePath).EnumerateFiles("*.jpg", SearchOption.TopDirectoryOnly)
                            count += 1
                            Try
                                sizeBytes += file.Length
                            Catch
                            End Try
                        Next
                        folder.ThumbnailCount = count
                        folder.SizeBytes = sizeBytes
                        folder.StatsStampUtc = stamp
                        freshStats(folder.Id) = (count, sizeBytes, stamp)
                    End If

                    result.Add(New ThumbnailCacheFolderInfo With {
                        .CacheId = folder.Id,
                        .FolderPath = folder.FolderPath,
                        .ThumbnailCount = folder.ThumbnailCount,
                        .SizeBytes = folder.SizeBytes,
                        .Exists = Not String.IsNullOrEmpty(folder.FolderPath) AndAlso Directory.Exists(folder.FolderPath)
                    })
                Catch
                End Try
            Next

            MergeFolderStats(freshStats)

            Return result.
                OrderByDescending(Function(i) i.SizeBytes).
                ThenBy(Function(i) i.DisplayName, StringComparer.CurrentCultureIgnoreCase).
                ToList()
        End Function

        Public Shared Function GetFolderCachesAsync() As Task(Of List(Of ThumbnailCacheFolderInfo))
            Return Task.Run(Function() GetFolderCaches())
        End Function

        Private Shared Function TryWriteCacheFile(filePath As String, cachePath As String, quality As Integer, Optional isStillWanted As Func(Of Boolean) = Nothing) As Boolean
            Using source = OpenThumbnailSource(filePath)
                If source Is Nothing Then Return False

                Using codec = SKCodec.Create(source)
                    If codec Is Nothing Then Return False

                    Using original = DecodeScaledOriented(codec, CacheWidth)
                        If original Is Nothing Then Return False

                        ' Billiger Ausstieg VOR dem teuren Resize/Encode - falls das Element
                        ' inzwischen weggescrollt wurde, lohnt sich der Rest der Arbeit nicht mehr.
                        If isStillWanted IsNot Nothing AndAlso Not isStillWanted() Then Return False

                        Dim corrected = ImageOrientationService.ApplyOrientation(original, codec.EncodedOrigin)
                        Try
                            ' Zieldimensionen aus dem KORRIGIERTEN Bitmap berechnen, nicht aus
                            ' info.Width/Height - sonst bekommt ein gedrehtes Hochkantfoto ein
                            ' falsches (vertauschtes) Seitenverhältnis.
                            Dim targetWidth = Math.Min(CacheWidth, corrected.Width)
                            Dim scale = targetWidth / CDbl(corrected.Width)
                            Dim targetHeight = Math.Max(1, CInt(Math.Round(corrected.Height * scale)))

                            Using resized = corrected.Resize(New SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul), SamplingMedium)
                                If resized Is Nothing Then Return False
                                Using image = SKImage.FromBitmap(resized)
                                    Using data = image.Encode(SKEncodedImageFormat.Jpeg, quality)
                                        If data Is Nothing Then Return False
                                        Using output = File.Open(cachePath, FileMode.Create, FileAccess.Write, FileShare.None)
                                            data.SaveTo(output)
                                        End Using
                                    End Using
                                End Using
                            End Using
                        Finally
                            If Not Object.ReferenceEquals(corrected, original) Then corrected.Dispose()
                        End Try
                    End Using
                End Using
            End Using

            Return True
        End Function

        ''' <summary>Dekodiert direkt in einer für targetWidth passenden verkleinerten Auflösung,
        ''' statt zuerst das komplette Originalbild in voller Auflösung zu dekodieren und es DANACH
        ''' herunterzuskalieren. Bei Kamerafotos/eingebetteten RAW-Vorschauen (i.d.R. JPEG) nutzt
        ''' SkiaSharp dafür das im Decoder eingebaute DCT-Scaling, was um ein Vielfaches günstiger
        ''' ist als Volldecode+Resize - das war beim erstmaligen Aufbau des Thumbnail-Caches für
        ''' einen ganzen (noch ungecachten) Ordner der Haupttreiber für minutenlange 100%-CPU-Last.
        ''' Formate ohne Skalierungsunterstützung liefern über GetScaledDimensions einfach die
        ''' Originalgröße zurück - dort ändert sich am Verhalten nichts.</summary>
        Private Shared Function DecodeScaledOriented(codec As SKCodec, targetWidth As Integer) As SKBitmap
            Dim info = codec.Info
            If info.Width <= 0 OrElse info.Height <= 0 Then Return Nothing

            Dim decodeInfo As SKImageInfo
            If targetWidth >= info.Width Then
                decodeInfo = New SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Else
                Dim desiredScale = targetWidth / CDbl(info.Width)
                Dim scaledSize = codec.GetScaledDimensions(CSng(desiredScale))
                decodeInfo = New SKImageInfo(Math.Max(1, scaledSize.Width), Math.Max(1, scaledSize.Height), SKColorType.Bgra8888, SKAlphaType.Premul)
            End If

            Dim bmp = New SKBitmap(decodeInfo)
            Dim result = codec.GetPixels(decodeInfo, bmp.GetPixels())
            If result <> SKCodecResult.Success AndAlso result <> SKCodecResult.IncompleteInput Then
                bmp.Dispose()
                Return Nothing
            End If
            Return bmp
        End Function

        ''' <summary>Vorschaubild einer RAW-Datei in ZWEI Stufen: erst LibRaws eigene Thumbnail-API
        ''' (formatkundig - sie weiss, wo die Vorschau liegt; kein Demosaic, daher schnell), sonst
        ''' unser eigener JPEG-Scanner, der die Datei nach Signaturen durchsucht. Der Scanner bleibt
        ''' als Rueckfall unverzichtbar: ohne installiertes LibRaw ist er der einzige Weg, und bei
        ''' JPEG-XL-/H265-Vorschauen neuerer Kameras liefert LibRaw etwas, das SkiaSharp nicht
        ''' dekodieren kann.</summary>
        Private Shared Function OpenRawThumbnailSource(filePath As String) As Stream
            ' Stufe 1: LibRaws Thumbnail-API - formatkundig und schnell (bei einer 50-MP-.arw
            ' 2 ms gegen 26 ms des Scanners, der bis zu 16 MB nach JPEG-Signaturen durchsucht).
            Dim viaLibRaw = RawDecodeService.TryExtractThumbnail(filePath)
            If IsBigEnoughForTile(viaLibRaw) Then Return viaLibRaw
            viaLibRaw?.Dispose()

            ' Stufe 2: eigener Scanner - er nimmt das GROESSTE eingebettete JPEG und ist damit die
            ' Rettung, wenn die Kamera nur ein Mini-Thumbnail als primaere Vorschau fuehrt.
            Dim scanned = RawPreviewService.ExtractPreview(filePath)
            If IsBigEnoughForTile(scanned) Then Return scanned
            scanned?.Dispose()

            ' Stufe 3: wirklich entwickeln. Fuer Dateien ohne jede eingebettete Vorschau (Leica
            ' Digilux 2) und fuer zu kleine Vorschauen (Leica M8 fuehrt nur 320x240, was auf einer
            ' 480er Kachel sichtbar matschig waere; der Decode kostet dort 345 ms). Einmalig -
            ' danach liegt die Kachel im Thumbnail-Cache.
            Dim developed = RawDecodeService.TryRenderPreviewPng(filePath)
            If developed IsNot Nothing AndAlso developed.Length > 0 Then Return developed
            developed?.Dispose()

            ' Nichts hat die Zielgroesse erreicht - dann lieber eine kleine Vorschau als gar keine.
            Return RawPreviewService.ExtractPreviewWithFallback(filePath)
        End Function

        ''' <summary>Taugt diese Vorschau fuer eine Kachel, oder muesste sie hochskaliert (und damit
        ''' unscharf) werden? Liest NUR den Kopf ueber SKCodec - kein voller Decode.</summary>
        Private Shared Function IsBigEnoughForTile(stream As MemoryStream) As Boolean
            If stream Is Nothing OrElse stream.Length = 0 Then Return False
            Try
                ' NICHT SKCodec.Create(Stream): der uebernimmt den Strom und schliesst ihn - der
                ' Aufrufer braucht ihn danach aber noch zum Dekodieren (dieselbe Falle wie in
                ' ImageProcessor.DecodeOriented). Deshalb ueber eine Kopie der Bytes; bei einer
                ' Vorschau sind das wenige hundert KB.
                stream.Position = 0
                Using data = SKData.CreateCopy(stream.ToArray())
                    Using codec = SKCodec.Create(data)
                        If codec Is Nothing Then Return False
                        Dim info = codec.Info
                        ' Kachelbreite mit etwas Toleranz: eine 460-px-Vorschau auf 480 zu strecken
                        ' faellt nicht auf, ein 320-px-Bild schon.
                        Return Math.Max(info.Width, info.Height) >= CacheWidth * 0.9
                    End Using
                End Using
            Catch
                Return False
            Finally
                Try
                    stream.Position = 0
                Catch
                End Try
            End Try
        End Function

        Private Shared Function OpenThumbnailSource(filePath As String) As Stream
            If RawPreviewService.IsSupportedRaw(filePath) Then
                Return OpenRawThumbnailSource(filePath)
            End If
            If SvgPreviewService.IsSupportedSvg(filePath) Then
                Return SvgPreviewService.ExtractPreview(filePath, CacheWidth)
            End If
            If IcoPreviewService.IsSupportedIco(filePath) Then
                Return IcoPreviewService.ExtractPreview(filePath)
            End If
            If PsdPreviewService.IsSupportedPsd(filePath) Then
                Return PsdPreviewService.ExtractPreview(filePath)
            End If
            If FpxService.IsFpx(filePath) Then
                Return FpxService.ExtractComposite(filePath)
            End If
            If VideoPreviewService.IsSupportedVideo(filePath) Then
                Return VideoPreviewService.ExtractPreview(filePath, CacheWidth)
            End If

            Return File.OpenRead(filePath)
        End Function

        ' Dekodiert per SKCodec, korrigiert die EXIF-Orientierung und skaliert auf maxWidth.
        ' Fällt bei jedem SKCodec-Fehler auf das bisherige Bitmap.DecodeToWidth zurück.
        Private Shared Function DecodeCorrectedAndResize(stream As Stream, maxWidth As Integer) As Bitmap
            Using codec = SKCodec.Create(stream)
                If codec Is Nothing OrElse codec.EncodedOrigin = SKEncodedOrigin.TopLeft Then
                    stream.Seek(0, SeekOrigin.Begin)
                    Return Bitmap.DecodeToWidth(stream, maxWidth)
                End If

                Using original = DecodeScaledOriented(codec, maxWidth)
                    If original Is Nothing Then
                        stream.Seek(0, SeekOrigin.Begin)
                        Return Bitmap.DecodeToWidth(stream, maxWidth)
                    End If

                    Dim corrected = ImageOrientationService.ApplyOrientation(original, codec.EncodedOrigin)
                    Try
                        Dim targetWidth = Math.Min(maxWidth, corrected.Width)
                        Dim scale = targetWidth / CDbl(corrected.Width)
                        Dim targetHeight = Math.Max(1, CInt(Math.Round(corrected.Height * scale)))
                        Using resized = corrected.Resize(New SKImageInfo(targetWidth, targetHeight, corrected.ColorType, corrected.AlphaType), SamplingMedium)
                            Return ImageOrientationService.ToAvaloniaBitmapFast(If(resized, corrected))
                        End Using
                    Finally
                        If Not Object.ReferenceEquals(corrected, original) Then corrected.Dispose()
                    End Try
                End Using
            End Using
        End Function

        Private Shared Function DecodeDirect(filePath As String, cancellationToken As CancellationToken) As Bitmap
            Try
                cancellationToken.ThrowIfCancellationRequested()
                If RawPreviewService.IsSupportedRaw(filePath) Then
                    Using preview = OpenRawThumbnailSource(filePath)
                        cancellationToken.ThrowIfCancellationRequested()
                        If preview IsNot Nothing Then Return DecodeCorrectedAndResize(preview, CacheWidth)
                    End Using
                ElseIf SvgPreviewService.IsSupportedSvg(filePath) Then
                    Using preview = SvgPreviewService.ExtractPreview(filePath, CacheWidth)
                        cancellationToken.ThrowIfCancellationRequested()
                        If preview IsNot Nothing Then Return Bitmap.DecodeToWidth(preview, CacheWidth)
                    End Using
                ElseIf IcoPreviewService.IsSupportedIco(filePath) Then
                    Using preview = IcoPreviewService.ExtractPreview(filePath)
                        cancellationToken.ThrowIfCancellationRequested()
                        If preview IsNot Nothing Then Return Bitmap.DecodeToWidth(preview, CacheWidth)
                    End Using
                ElseIf PsdPreviewService.IsSupportedPsd(filePath) Then
                    Using preview = PsdPreviewService.ExtractPreview(filePath)
                        cancellationToken.ThrowIfCancellationRequested()
                        If preview IsNot Nothing Then Return Bitmap.DecodeToWidth(preview, CacheWidth)
                    End Using
                ElseIf FpxService.IsFpx(filePath) Then
                    Using preview = FpxService.ExtractComposite(filePath)
                        cancellationToken.ThrowIfCancellationRequested()
                        If preview IsNot Nothing Then Return DecodeCorrectedAndResize(preview, CacheWidth)
                    End Using
                ElseIf VideoPreviewService.IsSupportedVideo(filePath) Then
                    Using preview = VideoPreviewService.ExtractPreview(filePath, CacheWidth)
                        cancellationToken.ThrowIfCancellationRequested()
                        If preview IsNot Nothing Then Return Bitmap.DecodeToWidth(preview, CacheWidth)
                    End Using
                Else
                    Using stream = File.OpenRead(filePath)
                        cancellationToken.ThrowIfCancellationRequested()
                        Return DecodeCorrectedAndResize(stream, CacheWidth)
                    End Using
                End If
            Catch ex As OperationCanceledException
                Throw
            Catch
            End Try

            Return Nothing
        End Function

        ''' Löscht die Vorschaubilder desselben Originals, die zu einem anderen Änderungszeitpunkt, einer
        ''' anderen Größe oder Qualität gehören. Beim erstmaligen Befüllen eines Ordners - dem Fall, der
        ''' früher quadratisch war - findet das Verzeichnis nichts und es passiert kein Dateizugriff.
        Private Shared Sub DeleteStaleCacheFiles(folderId As String, folderCachePath As String, imageHash As String, currentCacheFileName As String)
            Dim index = GetFolderFileIndex(folderId)
            For Each staleName In index.GetNames(folderCachePath, imageHash)
                If String.Equals(staleName, currentCacheFileName, StringComparison.OrdinalIgnoreCase) Then Continue For
                Try
                    File.Delete(IO.Path.Combine(folderCachePath, staleName))
                Catch ex As Exception
                    DiagnosticLogService.LogException("ThumbnailCache.DeleteStale", ex)
                End Try
                index.Remove(imageHash, staleName)
            Next
        End Sub

        ''' Irgendein vorhandenes Vorschaubild desselben Originals - veraltet, aber sofort anzeigbar,
        ''' während im Hintergrund das aktuelle erzeugt wird. Gestattet werden nur die wenigen Kandidaten
        ''' aus dem Namensverzeichnis, nicht der ganze Ordner.
        Private Shared Function FindAnyCachedThumbnail(folderId As String, folderCachePath As String, imageHash As String) As String
            Dim names = GetFolderFileIndex(folderId).GetNames(folderCachePath, imageHash)
            If names.Count = 0 Then Return Nothing

            Return names.
                Select(Function(n) IO.Path.Combine(folderCachePath, n)).
                Where(Function(p) File.Exists(p)).
                OrderByDescending(Function(p)
                                      Try
                                          Return File.GetLastWriteTimeUtc(p)
                                      Catch
                                          Return DateTime.MinValue
                                      End Try
                                  End Function).
                FirstOrDefault()
        End Function

        Private Shared Function GetFolderCachePathById(folderId As String) As String
            Return IO.Path.Combine(CacheRoot, folderId)
        End Function

        Private Shared Function GetFolderCacheId(folderPath As String) As String
            Return HashText(NormalizePath(folderPath))
        End Function

        Private Shared Function ResolveCacheId(sourceFolderPath As String, cacheScopeId As String) As String
            If Not String.IsNullOrWhiteSpace(cacheScopeId) Then
                Dim safeScope = cacheScopeId.Trim()
                If safeScope = IO.Path.GetFileName(safeScope) Then Return safeScope
            End If
            Return GetFolderCacheId(sourceFolderPath)
        End Function

        Private Shared Function ResolveCacheDisplayPath(filePath As String, cacheScopeId As String, cacheScopeName As String) As String
            If Not String.IsNullOrWhiteSpace(cacheScopeId) Then
                Return If(String.IsNullOrWhiteSpace(cacheScopeName), cacheScopeId, cacheScopeName)
            End If
            Return IO.Path.GetDirectoryName(filePath)
        End Function

        ' Registers the folder in the root index once per session per cache scope
        Private Shared Sub EnsureFolderRegistered(folderId As String, folderPath As String)
            If String.IsNullOrEmpty(folderId) OrElse String.IsNullOrEmpty(folderPath) Then Return
            If Not _registeredFolderIds.TryAdd(folderId, True) Then Return
            RegisterFolder(folderId, folderPath)
        End Sub

        Private Shared Sub RegisterFolder(folderId As String, folderPath As String)
            If String.IsNullOrEmpty(folderId) OrElse String.IsNullOrEmpty(folderPath) Then Return
            Try
                Directory.CreateDirectory(CacheRoot)
                SyncLock _rootIndexLock
                    Dim rootIndex = LoadRootIndex()
                    Dim entry = rootIndex.Folders.FirstOrDefault(Function(i) String.Equals(i.Id, folderId, StringComparison.OrdinalIgnoreCase))
                    If entry Is Nothing Then
                        rootIndex.Folders.Add(New ThumbnailCacheFolderIndexEntry With {
                            .Id = folderId,
                            .FolderPath = folderPath,
                            .LastSeenUtc = DateTime.UtcNow
                        })
                    Else
                        entry.FolderPath = folderPath
                        entry.LastSeenUtc = DateTime.UtcNow
                    End If
                    SaveRootIndex(rootIndex)
                End SyncLock
            Catch
            End Try
        End Sub

        Private Shared Sub RemoveFolderFromRootIndex(folderId As String)
            Try
                SyncLock _rootIndexLock
                    Dim rootIndex = LoadRootIndex()
                    rootIndex.Folders = rootIndex.Folders.
                        Where(Function(i) Not String.Equals(i.Id, folderId, StringComparison.OrdinalIgnoreCase)).
                        ToList()
                    SaveRootIndex(rootIndex)
                End SyncLock
            Catch
            End Try
        End Sub

        ''' Trägt die frisch gezählten Kennzahlen nach. Bewusst getrennt vom Zählen selbst: der Index
        ''' wird nur für den kurzen Lesen-Ändern-Schreiben-Abschnitt gesperrt, nicht für den langen
        ''' Verzeichnisdurchlauf.
        Private Shared Sub MergeFolderStats(stats As Dictionary(Of String, (Count As Integer, SizeBytes As Long, StampUtc As DateTime)))
            If stats Is Nothing OrElse stats.Count = 0 Then Return
            Try
                Directory.CreateDirectory(CacheRoot)
                SyncLock _rootIndexLock
                    Dim rootIndex = LoadRootIndex()
                    For Each pair In stats
                        Dim entry = rootIndex.Folders.FirstOrDefault(Function(i) String.Equals(i.Id, pair.Key, StringComparison.OrdinalIgnoreCase))
                        If entry Is Nothing Then
                            entry = New ThumbnailCacheFolderIndexEntry With {.Id = pair.Key, .FolderPath = "", .LastSeenUtc = DateTime.UtcNow}
                            rootIndex.Folders.Add(entry)
                        End If
                        entry.ThumbnailCount = pair.Value.Count
                        entry.SizeBytes = pair.Value.SizeBytes
                        entry.StatsStampUtc = pair.Value.StampUtc
                    Next
                    SaveRootIndex(rootIndex)
                End SyncLock
            Catch
            End Try
        End Sub

        Private Shared Function LoadRootIndex() As ThumbnailCacheRootIndex
            Try
                If File.Exists(RootIndexPath) Then
                    Dim loaded = JsonSerializer.Deserialize(Of ThumbnailCacheRootIndex)(File.ReadAllText(RootIndexPath, Encoding.UTF8))
                    If loaded IsNot Nothing AndAlso loaded.Folders IsNot Nothing Then Return loaded
                End If
            Catch
            End Try
            Return New ThumbnailCacheRootIndex()
        End Function

        ''' Erst vollständig danebenschreiben, dann ersetzen: ein Abbruch mitten im Schreiben ließe
        ''' sonst eine abgeschnittene Indexdatei zurück, die LoadRootIndex verwerfen müsste - womit
        ''' sämtliche Ordnerzuordnungen verloren wären.
        Private Shared Sub SaveRootIndex(rootIndex As ThumbnailCacheRootIndex)
            Try
                Directory.CreateDirectory(CacheRoot)
                Dim tempPath = RootIndexPath & ".tmp"
                File.WriteAllText(tempPath, JsonSerializer.Serialize(rootIndex, JsonOptions), Encoding.UTF8)
                File.Move(tempPath, RootIndexPath, overwrite:=True)
            Catch
            End Try
        End Sub

        ''' <summary>Pfad-Schluessel fuer den Cache-Hash. Frueher wurde hier IMMER uppercased -
        ''' auf Linux bekamen damit /Bilder/RAW und /Bilder/raw denselben Cache-Ordner und
        ''' vermischten ihre Thumbnails. PathIdentity ebnet die Schreibweise nur unter Windows ein.
        ''' NEBENWIRKUNG: auf Linux/macOS aendert sich dadurch der Hash bestehender Caches. Sie
        ''' werden einmalig neu aufgebaut; die alten Ordner sind verwaist und lassen sich ueber
        ''' Einstellungen -> Cache leeren entfernen.</summary>
        Private Shared Function NormalizePath(path As String) As String
            Return PathIdentity.Normalize(path)
        End Function

        Private Shared Function HashText(value As String) As String
            Using sha = SHA1.Create()
                Dim bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(If(value, "")))
                Return String.Concat(bytes.Select(Function(b) b.ToString("x2")))
            End Using
        End Function
    End Class

End Namespace
