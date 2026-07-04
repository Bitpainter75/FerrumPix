Imports System
Imports System.Collections.Concurrent
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading
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

        Private Class ThumbnailCacheRootIndex
            Public Property Folders As New List(Of ThumbnailCacheFolderIndexEntry)()
        End Class

        Private Class ThumbnailCacheFolderIndexEntry
            Public Property Id As String
            Public Property FolderPath As String
            Public Property LastSeenUtc As DateTime
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
                Dim staleCachePath = FindAnyCachedThumbnail(folderCachePath, imageHash)
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
                DeleteStaleCacheFiles(folderCachePath, imageHash, cacheFileName)
                cancellationToken.ThrowIfCancellationRequested()

                If TryWriteCacheFile(filePath, cachePath, quality, isStillWanted) AndAlso File.Exists(cachePath) Then
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
                Return count
            Catch
                Return 0
            End Try
        End Function

        Public Shared Function DeleteSearchListCache(searchListId As String) As Integer
            If String.IsNullOrWhiteSpace(searchListId) Then Return 0
            Return DeleteFolderCacheById("searchlist_" & searchListId)
        End Function

        Public Shared Function GetFolderCaches() As List(Of ThumbnailCacheFolderInfo)
            Dim result As New List(Of ThumbnailCacheFolderInfo)()
            If Not Directory.Exists(CacheRoot) Then Return result

            ' Build root index from registered folders + any unregistered dirs on disk
            Dim rootIndex = LoadRootIndex()
            Dim knownIds = New HashSet(Of String)(rootIndex.Folders.Select(Function(f) f.Id), StringComparer.OrdinalIgnoreCase)
            For Each folderCachePath In Directory.GetDirectories(CacheRoot)
                Dim folderId = IO.Path.GetFileName(folderCachePath)
                If Not knownIds.Contains(folderId) Then
                    rootIndex.Folders.Add(New ThumbnailCacheFolderIndexEntry With {
                        .Id = folderId,
                        .FolderPath = "",
                        .LastSeenUtc = DateTime.UtcNow
                    })
                End If
            Next

            For Each folder In rootIndex.Folders
                Try
                    If String.IsNullOrEmpty(folder.Id) Then Continue For
                    Dim folderCachePath = IO.Path.Combine(CacheRoot, folder.Id)
                    If Not Directory.Exists(folderCachePath) Then Continue For

                    Dim jpgs = Directory.GetFiles(folderCachePath, "*.jpg", SearchOption.TopDirectoryOnly)
                    Dim sizeBytes = jpgs.Sum(Function(p)
                                                 Try
                                                     Return New FileInfo(p).Length
                                                 Catch
                                                     Return 0L
                                                 End Try
                                             End Function)

                    result.Add(New ThumbnailCacheFolderInfo With {
                        .CacheId = folder.Id,
                        .FolderPath = folder.FolderPath,
                        .ThumbnailCount = jpgs.Length,
                        .SizeBytes = sizeBytes,
                        .Exists = Not String.IsNullOrEmpty(folder.FolderPath) AndAlso Directory.Exists(folder.FolderPath)
                    })
                Catch
                End Try
            Next

            Return result.
                OrderByDescending(Function(i) i.SizeBytes).
                ThenBy(Function(i) i.DisplayName, StringComparer.CurrentCultureIgnoreCase).
                ToList()
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

                            Using resized = corrected.Resize(New SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium)
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

        Private Shared Function OpenThumbnailSource(filePath As String) As Stream
            If RawPreviewService.IsSupportedRaw(filePath) Then
                Return RawPreviewService.ExtractPreview(filePath)
            End If
            If SvgPreviewService.IsSupportedSvg(filePath) Then
                Return SvgPreviewService.ExtractPreview(filePath, CacheWidth)
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
                        Using resized = corrected.Resize(New SKImageInfo(targetWidth, targetHeight, corrected.ColorType, corrected.AlphaType), SKFilterQuality.Medium)
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
                    Using preview = RawPreviewService.ExtractPreview(filePath)
                        cancellationToken.ThrowIfCancellationRequested()
                        If preview IsNot Nothing Then Return DecodeCorrectedAndResize(preview, CacheWidth)
                    End Using
                ElseIf SvgPreviewService.IsSupportedSvg(filePath) Then
                    Using preview = SvgPreviewService.ExtractPreview(filePath, CacheWidth)
                        cancellationToken.ThrowIfCancellationRequested()
                        If preview IsNot Nothing Then Return Bitmap.DecodeToWidth(preview, CacheWidth)
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

        Private Shared Sub DeleteStaleCacheFiles(folderCachePath As String, imageHash As String, currentCacheFileName As String)
            If Not Directory.Exists(folderCachePath) Then Return

            Dim imagePrefix = imageHash & "_"
            For Each stale In Directory.GetFiles(folderCachePath, imagePrefix & "*.jpg", SearchOption.TopDirectoryOnly)
                Try
                    If Not String.Equals(IO.Path.GetFileName(stale), currentCacheFileName, StringComparison.OrdinalIgnoreCase) Then File.Delete(stale)
                Catch
                End Try
            Next
        End Sub

        Private Shared Function FindAnyCachedThumbnail(folderCachePath As String, imageHash As String) As String
            If Not Directory.Exists(folderCachePath) Then Return Nothing
            Try
                Return Directory.GetFiles(folderCachePath, imageHash & "_*.jpg", SearchOption.TopDirectoryOnly).
                    OrderByDescending(Function(p)
                                          Try
                                              Return File.GetLastWriteTimeUtc(p)
                                          Catch
                                              Return DateTime.MinValue
                                          End Try
                                      End Function).
                    FirstOrDefault()
            Catch
                Return Nothing
            End Try
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
            Catch
            End Try
        End Sub

        Private Shared Sub RemoveFolderFromRootIndex(folderId As String)
            Try
                Dim rootIndex = LoadRootIndex()
                rootIndex.Folders = rootIndex.Folders.
                    Where(Function(i) Not String.Equals(i.Id, folderId, StringComparison.OrdinalIgnoreCase)).
                    ToList()
                SaveRootIndex(rootIndex)
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

        Private Shared Sub SaveRootIndex(rootIndex As ThumbnailCacheRootIndex)
            Try
                Directory.CreateDirectory(CacheRoot)
                File.WriteAllText(RootIndexPath, JsonSerializer.Serialize(rootIndex, JsonOptions), Encoding.UTF8)
            Catch
            End Try
        End Sub

        Private Shared Function NormalizePath(path As String) As String
            If String.IsNullOrEmpty(path) Then Return ""
            Try
                Return IO.Path.GetFullPath(path).TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar).ToUpperInvariant()
            Catch
                Return path.Trim().ToUpperInvariant()
            End Try
        End Function

        Private Shared Function HashText(value As String) As String
            Using sha = SHA1.Create()
                Dim bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(If(value, "")))
                Return String.Concat(bytes.Select(Function(b) b.ToString("x2")))
            End Using
        End Function
    End Class

End Namespace
