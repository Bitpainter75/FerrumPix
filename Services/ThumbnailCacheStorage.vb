Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ''' <summary>Ort und Hoechstgroesse des Kachelspeichers: wo die Kacheln liegen und das
    ''' Aufraeumen der am laengsten nicht benutzten Kacheln.
    '''
    ''' DER ORT ist "ThumbnailCache" im Datenordner (AppFolders.ThumbnailCacheFolder). Einen
    ''' eigenen gibt es nicht: wer die Kacheln woanders haben will, waehlt einen anderen
    ''' Datenordner, und sie ziehen mit allem anderen um. Der Schluessel einer Kachel ist der volle
    ''' Pfad des Bildes; geteilt ueber Rechner wird der Speicher nicht.
    '''
    ''' DIE HOECHSTGRENZE ist ab Werk aus (0 = unbegrenzt). Ist eine gesetzt, raeumt ein Lauf im
    ''' Hintergrund die am laengsten nicht benutzten Kacheln weg, bis 90 Prozent der Grenze
    ''' erreicht sind. Die Reihenfolge ist die Aenderungszeit der Kacheldatei: die Zugriffszeit
    ''' fuehren die meisten Dateisysteme nicht verlaesslich (relatime, noatime). Beim Anzeigen
    ''' wird die Aenderungszeit deshalb hoechstens einmal am Tag nachgezogen, und nur, solange eine
    ''' Grenze gesetzt ist.</summary>
    Partial Public NotInheritable Class ThumbnailCacheService

        ''' <summary>Der Ort der Kacheln: ThumbnailCache im Datenordner, unter Linux ab Werk
        ''' ~/.local/share/FerrumPix/ThumbnailCache, im Flatpak im Datenordner der Sandbox.</summary>
        Public Shared ReadOnly Property DefaultCacheRoot As String
            Get
                Return AppFolders.ThumbnailCacheFolder
            End Get
        End Property

        ''' <summary>Bis zu diesem Anteil der Grenze wird aufgeraeumt, sonst ginge es mit jeder
        ''' neuen Kachel von vorn los.</summary>
        Private Const TrimTargetPercent As Long = 90

        ''' <summary>Juengere Kacheln raeumt das Aufraeumen nie weg: sie werden gerade geschrieben
        ''' oder gleich angezeigt.</summary>
        Private Shared ReadOnly FreshTileAge As TimeSpan = TimeSpan.FromMinutes(2)

        Private Shared ReadOnly TouchInterval As TimeSpan = TimeSpan.FromDays(1)

        Private Const BytesPerGigabyte As Long = 1024L * 1024L * 1024L

        ' --- Schreiben und Aufraeumen -------------------------------------------------------

        ' Zwei Aufraeumlaeufe schliessen einander aus.
        Private Shared ReadOnly _maintenanceLock As New Object()
        ' Die Kacheln, die gerade geschrieben werden; das Aufraeumen laesst sie liegen.
        Private Shared ReadOnly _tilesBeingWritten As New ConcurrentDictionary(Of String, Byte)(StringComparer.Ordinal)
        Private Shared _cachedLimitBytes As Long = 0
        Private Shared ReadOnly _sizeLock As New Object()
        ' Belegter Platz nach der letzten Messung plus seither Geschriebenes; -1 unbekannt.
        Private Shared _knownSizeBytes As Long = -1
        Private Shared _trimRunning As Integer

        ''' <summary>Fuer den Pruefstand: ein eigener Ort mit nachgebauten Kacheln.</summary>
        Friend Shared CacheRootOverride As String = Nothing

        ''' <summary>Der Ort, an dem die Kacheln liegen.</summary>
        Private Shared ReadOnly Property CacheRoot As String
            Get
                Dim override = CacheRootOverride
                If Not String.IsNullOrEmpty(override) Then Return override
                Return DefaultCacheRoot
            End Get
        End Property

        Private Shared ReadOnly Property RootIndexPath As String
            Get
                Return IO.Path.Combine(CacheRoot, IndexFileName)
            End Get
        End Property

        ''' <summary>Der Ort, an dem die Kacheln liegen, fuer die Anzeige.</summary>
        Public Shared ReadOnly Property CurrentCacheRoot As String
            Get
                Return CacheRoot
            End Get
        End Property

        ''' <summary>Die Hoechstgroesse in Bytes, 0 = unbegrenzt.</summary>
        Public Shared ReadOnly Property CacheLimitBytes As Long
            Get
                Dim e As Boolean, q As Integer
                GetCachedSettings(e, q)
                Return Volatile.Read(_cachedLimitBytes)
            End Get
        End Property

        ' --- Schreibbereich -----------------------------------------------------------------

        ''' <summary>Meldet eine Kachel als in Arbeit an. False, wenn der Pfad nicht im
        ''' Kachelspeicher liegt: dann wird die Kachel nicht abgelegt, angezeigt wird sie trotzdem.</summary>
        Private Shared Function TryBeginTileWrite(cachePath As String) As Boolean
            If Not cachePath.StartsWith(CacheRoot & IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) Then Return False
            _tilesBeingWritten(cachePath) = 0
            Return True
        End Function

        Private Shared Sub EndTileWrite(cachePath As String, written As Boolean)
            Dim unused As Byte
            _tilesBeingWritten.TryRemove(cachePath, unused)
            If written Then AccountWrittenTile(cachePath)
        End Sub

        ''' <summary>Legt eine Kachel atomar ab: erst unter eigenem Namen daneben
        ''' (<c>&lt;Kachel&gt;.&lt;guid&gt;.part</c>), dann in einem Schritt umbenannt. Ein Leser sieht
        ''' damit nie eine halb geschriebene Kachel, auch nicht ein anderer Prozess, und ein Absturz
        ''' hinterlaesst hoechstens eine Teildatei, die weder als Kachel gilt noch mitgezaehlt wird
        ''' (alle Wege suchen "*.jpg").</summary>
        Private Shared Sub WriteTileAtomic(data As SKData, cachePath As String)
            Dim partPath = cachePath & "." & Guid.NewGuid().ToString("N") & ".part"
            Try
                Using output = File.Open(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    data.SaveTo(output)
                End Using
                File.Move(partPath, cachePath, overwrite:=True)
            Catch
                Try
                    If File.Exists(partPath) Then File.Delete(partPath)
                Catch
                End Try
                Throw
            End Try
        End Sub

        ' --- Hoechstgroesse -----------------------------------------------------------------

        ''' <summary>Zieht die Aenderungszeit einer angezeigten Kachel nach, hoechstens einmal am
        ''' Tag und nur bei gesetzter Grenze. Sie ist die Reihenfolge des Aufraeumens.</summary>
        Private Shared Sub TouchForEviction(cachePath As String)
            If Volatile.Read(_cachedLimitBytes) <= 0 Then Return
            Try
                Dim now = DateTime.UtcNow
                If now - File.GetLastWriteTimeUtc(cachePath) > TouchInterval Then File.SetLastWriteTimeUtc(cachePath, now)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                ' Nur die Reihenfolge des Aufraeumens leidet.
            End Try
        End Sub

        Private Shared Sub AccountWrittenTile(cachePath As String)
            Dim limit = Volatile.Read(_cachedLimitBytes)
            If limit <= 0 Then Return
            Dim length = 0L
            Try
                length = New FileInfo(cachePath).Length
            Catch
            End Try
            Dim needsTrim As Boolean
            SyncLock _sizeLock
                If _knownSizeBytes < 0 Then
                    needsTrim = True
                Else
                    _knownSizeBytes += length
                    needsTrim = _knownSizeBytes > limit
                End If
            End SyncLock
            If needsTrim Then StartTrim(limit)
        End Sub

        Private Shared Sub StartTrim(limit As Long)
            If Interlocked.Exchange(_trimRunning, 1) = 1 Then Return
            Dim ignored = Task.Run(
                Sub()
                    Try
                        TrimToLimit(limit)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("ThumbnailCache.Trim", ex)
                    Finally
                        Interlocked.Exchange(_trimRunning, 0)
                    End Try
                End Sub)
        End Sub

        ''' <summary>Eine Kachel auf der Platte, fuer das Aufraeumen.</summary>
        Private Structure TileFile
            Public FolderId As String
            Public Info As FileInfo
        End Structure

        ''' <summary>Misst den Speicher und raeumt, wenn er ueber <paramref name="limitBytes"/>
        ''' liegt, die am laengsten nicht benutzten Kacheln weg, bis 90 Prozent erreicht sind.
        ''' Ohne Grenze (0) geschieht nichts. Kacheln in Arbeit und solche juenger als zwei Minuten
        ''' bleiben. Danach stimmen das Namensverzeichnis im Speicher und der Index: ein leer
        ''' gewordenes Verzeichnis verschwindet samt seinem Eintrag, die uebrigen bekommen ihre
        ''' Kennzahlen neu. Gibt die Zahl der entfernten Kacheln zurueck.</summary>
        Public Shared Function TrimToLimit(limitBytes As Long) As Integer
            If limitBytes <= 0 Then Return 0
            If Not Monitor.TryEnter(_maintenanceLock) Then Return 0
            Try
                Dim root = CacheRoot
                Dim tiles As New List(Of TileFile)()
                If Directory.Exists(root) Then
                    For Each folder In Directory.GetDirectories(root)
                        Dim folderId = IO.Path.GetFileName(folder)
                        Try
                            For Each info In New DirectoryInfo(folder).EnumerateFiles("*.jpg", SearchOption.TopDirectoryOnly)
                                tiles.Add(New TileFile With {.FolderId = folderId, .Info = info})
                            Next
                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                            ' Ein unlesbares Verzeichnis bleibt, wie es ist.
                        End Try
                    Next
                End If
                Dim total = tiles.Sum(Function(t) t.Info.Length)
                Dim removed = 0
                If total > limitBytes Then
                    Dim goal = limitBytes * TrimTargetPercent \ 100
                    Dim freshSince = DateTime.UtcNow - FreshTileAge
                    Dim touched As New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)
                    For Each tile In tiles.OrderBy(Function(t) t.Info.LastWriteTimeUtc)
                        If total <= goal Then Exit For
                        If tile.Info.LastWriteTimeUtc > freshSince Then Exit For
                        If _tilesBeingWritten.ContainsKey(tile.Info.FullName) Then Continue For
                        Try
                            Dim length = tile.Info.Length
                            tile.Info.Delete()
                            total -= length
                            removed += 1
                            Dim names As List(Of String) = Nothing
                            If Not touched.TryGetValue(tile.FolderId, names) Then
                                names = New List(Of String)()
                                touched(tile.FolderId) = names
                            End If
                            names.Add(tile.Info.Name)
                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                            ' Liegt gerade in Arbeit - beim naechsten Durchgang.
                        End Try
                    Next
                    SettleTrimmedFolders(root, touched)
                End If
                SyncLock _sizeLock
                    _knownSizeBytes = total
                End SyncLock
                Return removed
            Finally
                Monitor.Exit(_maintenanceLock)
            End Try
        End Function

        ''' <summary>Bringt Namensverzeichnis und Index nach dem Aufraeumen auf den Stand der
        ''' Platte.</summary>
        Private Shared Sub SettleTrimmedFolders(root As String, touched As Dictionary(Of String, List(Of String)))
            If touched.Count = 0 Then Return
            Dim emptied As New List(Of String)()
            Dim stats As New Dictionary(Of String, (Count As Integer, SizeBytes As Long, StampUtc As DateTime))(StringComparer.OrdinalIgnoreCase)
            For Each pair In touched
                Dim index As FolderFileIndex = Nothing
                If _folderFileIndexes.TryGetValue(pair.Key, index) Then
                    For Each name In pair.Value
                        Dim separator = name.IndexOf("_"c)
                        If separator > 0 Then index.Remove(name.Substring(0, separator), name)
                    Next
                End If
                Dim folder = IO.Path.Combine(root, pair.Key)
                Try
                    Dim remaining = New DirectoryInfo(folder).EnumerateFiles("*.jpg", SearchOption.TopDirectoryOnly).ToList()
                    If remaining.Count = 0 AndAlso Not _tilesBeingWritten.Keys.Any(Function(k) k.StartsWith(folder & IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)) Then
                        Directory.Delete(folder, recursive:=True)
                        emptied.Add(pair.Key)
                    Else
                        stats(pair.Key) = (remaining.Count, remaining.Sum(Function(f) f.Length), Directory.GetLastWriteTimeUtc(folder))
                    End If
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            Next
            For Each id In emptied
                _folderFileIndexes.TryRemove(id, Nothing)
            Next
            If emptied.Count > 0 Then RemoveFoldersFromRootIndex(emptied)
            MergeFolderStats(stats)
        End Sub

        ''' <summary>Fuer die Anzeige: der belegte Platz in Bytes. Liest jedes Verzeichnis;
        ''' gehoert auf einen Hintergrundfaden.</summary>
        Public Shared Function MeasureCacheSizeBytes() As Long
            Dim root = CacheRoot
            Dim total = 0L
            Try
                If Not Directory.Exists(root) Then Return 0
                For Each folder In Directory.GetDirectories(root)
                    Try
                        For Each info In New DirectoryInfo(folder).EnumerateFiles("*.jpg", SearchOption.TopDirectoryOnly)
                            total += info.Length
                        Next
                    Catch
                    End Try
                Next
            Catch
            End Try
            Return total
        End Function

        ''' <summary>Die Grenze in Bytes zu einer Einstellung in Gigabyte.</summary>
        Friend Shared Function LimitBytesFromGigabytes(gigabytes As Integer) As Long
            Dim normalized = AppSettingsService.NormalizeThumbnailCacheLimitGigabytes(gigabytes)
            Return If(normalized <= 0, 0L, CLng(normalized) * BytesPerGigabyte)
        End Function

        ''' <summary>Startet nach einer geaenderten Grenze sofort einen Lauf, statt auf die
        ''' naechste neue Kachel zu warten.</summary>
        Public Shared Sub ApplyLimitNow()
            InvalidateSettingsCache()
            Dim limit = CacheLimitBytes
            SyncLock _sizeLock
                _knownSizeBytes = -1
            End SyncLock
            If limit > 0 Then StartTrim(limit)
        End Sub

    End Class

End Namespace
