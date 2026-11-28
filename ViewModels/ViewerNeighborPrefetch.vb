Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia.Media.Imaging
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>
    ''' Entwickelt im Betrachter die Bilder vor, die die Pfeiltasten als naechste erreichen: drei in
    ''' Laufrichtung und eins zurueck. Die Laufrichtung kippt erst nach zwei Schritten in die andere
    ''' Richtung; ein einzelner Schritt zurueck (nachsehen, dann weiter) laesst sie stehen.
    '''
    ''' <para>EIN DECODE ZUR ZEIT. Jeder Vorlauf geht durch <see cref="DecodeGate"/>, als Arbeit, die
    ''' weicht (<see cref="DecodeGate.RunYielding"/>). Seine Frage an
    ''' <see cref="RawDecodeService.RunWhileWanted"/> verlangt, dass niemand vor der Schleuse wartet:
    ''' kommt ein echter Auftrag (das Bild, zu dem geblaettert wurde, eine Kachel, ein Analysebild),
    ''' bricht der laufende LibRaw-Lauf beim naechsten Fortschrittsrueckruf ab und gibt die Schleuse
    ''' frei. Ein neuer Vorlauf beginnt erst, wenn die Schleuse leer ist und niemand wartet.</para>
    '''
    ''' <para>WIRD ZU EINEM BILD GEBLAETTERT, DAS GERADE VORLAEUFT, uebernimmt der echte Auftrag den
    ''' Lauf (<see cref="TakeAsync"/>), statt ihn abzubrechen und neu anzufangen. Ab da bricht er
    ''' nicht mehr ab, wenn jemand wartet.</para>
    '''
    ''' <para>DAS LESEN DER DATEI geschieht VOR der Schleuse, in Stuecken zu 1 MB und zwischen ihnen
    ''' abbrechbar. Ein Lesen kann LibRaw nicht abbrechen; auf einer Netzfreigabe hielte es die
    ''' Schleuse sonst so lange, wie die Datei ueber die Leitung braucht (gemessen an einer CR3 mit
    ''' 21 MB auf einer SMB-Freigabe: kalt 265 ms, danach aus dem Seitenspeicher des Systems 40 ms).
    ''' So liest der Decode aus dem Seitenspeicher, und die Schleuse traegt nur die Rechnung.</para>
    '''
    ''' <para>DER SPEICHER hat eine Grenze, die vor jedem Vorlauf neu gerechnet wird
    ''' (<see cref="BudgetBytes"/>): hoechstens vier Bilder, hoechstens 1 GB, und nie mehr, als von
    ''' zwei Dritteln des Arbeitsspeichers nach 2 GB fuer den Rest, dem Entwicklungs-Zwischenspeicher
    ''' des Editors und dem gezeigten Bild bleibt. Passt das naechste Bild nicht mehr, laeuft keiner.</para>
    '''
    ''' <para>OB EIN BILD NOCH GILT, sagt ein Schluessel aus der Kennung der Entwicklung
    ''' (<see cref="RawDecodeService.DevelopmentKey"/>, dieselben Groessen wie der Zwischenspeicher der
    ''' Entwicklung), dem Stand der Beistelldateien, dem Stand der Einstellungen und der Aufloesung.
    ''' Beim Herausgeben wird er neu gerechnet; weicht er ab, wird das Bild verworfen und normal
    ''' entwickelt. Geschrieben wird nie etwas.</para>
    ''' </summary>
    Friend NotInheritable Class ViewerNeighborPrefetch

        Friend Const AheadCount As Integer = 3
        Friend Const BehindCount As Integer = 1
        Friend Const MaxEntries As Integer = AheadCount + BehindCount
        ''' So viele Schritte hintereinander gegen die Laufrichtung kippen sie.
        Friend Const FlipAfterSteps As Integer = 2
        ''' Wartezeit vor dem ersten Vorlauf nach einem gezeigten Bild: wer die Taste haelt, blaettert
        ''' schneller, als ein Vorlauf fertig wird, und soll keinen anstossen.
        Friend Const StartDelayMs As Integer = 150
        ''' So lange wird gewartet, bis die Schleuse wieder frei ist, je Blick.
        Private Const IdlePollMs As Integer = 25
        Private Const ReadChunkBytes As Integer = 1 << 20
        ''' Der Entwicklungs-Zwischenspeicher des Editors (8 und 16 Bit, rund 180 MB je Bild bei 24 MP)
        ''' liegt neben dem Vorlauf und wird von der Grenze abgezogen.
        Friend Const EditorCacheReserveBytes As Long = 180L * 1024 * 1024
        Friend Const BudgetCapBytes As Long = 1024L * 1024 * 1024
        Private Const SystemReserveBytes As Long = 2048L * 1024 * 1024

        ''' <summary>Fuer den Pruefstand: eine feste Grenze in Byte statt der gerechneten; -1 = gerechnet.</summary>
        Friend Shared BudgetOverrideBytes As Long = -1

        Private NotInheritable Class Entry
            Public Path As String
            Public Reduced As Boolean
            Public KeyTask As Task(Of String)
            Public Bitmap As Bitmap
            Public RawDeveloped As Boolean
            Public Bytes As Long
            Public Finished As Boolean
            Public Discarded As Boolean
            Public Adopted As Integer
            Public ReadOnly Completion As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        End Class

        Private ReadOnly _decode As Func(Of String, Boolean, (Bitmap As Bitmap, RawDeveloped As Boolean))
        Private ReadOnly _willDevelop As Func(Of String, Boolean)
        Private ReadOnly _release As Action(Of Bitmap)
        Private ReadOnly _lock As New Object()
        Private ReadOnly _entries As New List(Of Entry)()
        Private ReadOnly _skipped As New HashSet(Of String)(PathIdentity.Comparer)
        Private _targets As New List(Of String)()
        Private _reduced As Boolean
        Private _currentPath As String
        Private _warmPath As String
        Private _currentImageBytes As Long
        Private _estimatedBytes As Long
        Private _workerRunning As Boolean
        Private _direction As Integer = 1
        Private _oppositeSteps As Integer

        ' Zaehler fuer Protokoll und Pruefstand.
        Friend StartedCount As Integer
        Friend StoredCount As Integer
        Friend AbortedCount As Integer
        Friend HitCount As Integer
        Friend AdoptedCount As Integer
        Friend StaleCount As Integer
        Friend BudgetStopCount As Integer
        Friend WarmCount As Integer

        ''' <param name="decode">Der Decode des Betrachters fuer eine Datei und die Aufloesung (halb =
        ''' True). Laeuft unter der Schleuse auf dem Faden des Vorlaufs.</param>
        ''' <param name="willDevelop">Entwickelt der Betrachter diese Datei? Nur dann lohnt ein Vorlauf.</param>
        ''' <param name="release">Gibt ein verworfenes Bild frei (der Betrachter: einen Takt spaeter,
        ''' weil ein schon einmal gezeigtes Bild noch in der Anzeige stecken kann).</param>
        Friend Sub New(decode As Func(Of String, Boolean, (Bitmap As Bitmap, RawDeveloped As Boolean)),
                       willDevelop As Func(Of String, Boolean),
                       Optional release As Action(Of Bitmap) = Nothing)
            _decode = decode
            _willDevelop = willDevelop
            _release = If(release, Sub(b) b?.Dispose())
        End Sub

        ' ── Laufrichtung und Ziele ────────────────────────────────────────────────

        ''' <summary>Meldet einen Schritt: +1 vorwaerts, -1 zurueck.</summary>
        Friend Sub NoteStep(stepSign As Integer)
            If stepSign = 0 Then Return
            SyncLock _lock
                If stepSign = _direction Then
                    _oppositeSteps = 0
                Else
                    _oppositeSteps += 1
                    If _oppositeSteps >= FlipAfterSteps Then
                        _direction = Math.Sign(stepSign)
                        _oppositeSteps = 0
                    End If
                End If
            End SyncLock
        End Sub

        Friend ReadOnly Property Direction As Integer
            Get
                SyncLock _lock
                    Return _direction
                End SyncLock
            End Get
        End Property

        ''' <summary>Die Stellen, die vorentwickelt werden, in ihrer Reihenfolge: drei in Laufrichtung,
        ''' dann eins zurueck. Mit <paramref name="wrap"/> laeuft die Reihe ringsum wie der
        ''' Filmstreifen; die gezeigte Stelle und doppelte fallen weg.</summary>
        Friend Shared Function TargetIndices(current As Integer, count As Integer, direction As Integer, wrap As Boolean) As List(Of Integer)
            Dim result As New List(Of Integer)()
            If count < 2 OrElse current < 0 OrElse current >= count Then Return result
            Dim d = If(direction < 0, -1, 1)
            Dim offsets As New List(Of Integer)()
            For k = 1 To AheadCount
                offsets.Add(k * d)
            Next
            For k = 1 To BehindCount
                offsets.Add(-k * d)
            Next
            For Each offset In offsets
                Dim index = current + offset
                If wrap Then
                    index = ((index Mod count) + count) Mod count
                ElseIf index < 0 OrElse index >= count Then
                    Continue For
                End If
                If index <> current AndAlso Not result.Contains(index) Then result.Add(index)
            Next
            Return result
        End Function

        ' ── Auftraege ─────────────────────────────────────────────────────────────

        ''' <summary>Setzt die Ziele nach einem gezeigten Bild. Was nicht mehr Ziel ist, wird verworfen
        ''' (ein laufender Vorlauf bricht ab). <paramref name="warmPath"/>: nach den Zielen den
        ''' 16-Bit-Zwischenspeicher des Editors fuer dieses Bild fuellen (bei voller Aufloesung, wo die
        ''' Vorlaeufe ihn umwerfen).</summary>
        Friend Sub Schedule(targets As IEnumerable(Of String), reduced As Boolean, currentPath As String,
                            warmPath As String, currentImageBytes As Long)
            Dim dropped As New List(Of Entry)()
            Dim start = False
            SyncLock _lock
                _targets = If(targets Is Nothing, New List(Of String)(), targets.ToList())
                _reduced = reduced
                _currentPath = currentPath
                _warmPath = warmPath
                _currentImageBytes = Math.Max(0, currentImageBytes)
                If _estimatedBytes <= 0 Then _estimatedBytes = _currentImageBytes
                _skipped.Clear()
                For Each e In _entries.ToList()
                    If Not IsTargetLocked(e.Path, e.Reduced) Then
                        _entries.Remove(e)
                        e.Discarded = True
                        If e.Finished AndAlso e.Bitmap IsNot Nothing Then dropped.Add(e)
                    End If
                Next
                EnforceBudgetLocked(dropped)
                If (_targets.Count > 0 OrElse _warmPath IsNot Nothing) AndAlso Not _workerRunning Then
                    _workerRunning = True
                    start = True
                End If
            End SyncLock
            ReleaseAll(dropped)
            If start Then Task.Run(AddressOf WorkerAsync)
        End Sub

        ''' <summary>Verwirft alles und haelt an (Betrachter verlassen, Diashow, Vergleich).</summary>
        Friend Sub Clear()
            Schedule(Nothing, _reduced, Nothing, Nothing, 0)
        End Sub

        ''' <summary>Das vorentwickelte Bild fuer diese Datei, fertig oder noch laufend, oder ein
        ''' leeres Ergebnis. Ein laufender Vorlauf wird uebernommen und laeuft bis zum Ende. Der
        ''' Besitz am Bild geht an den Aufrufer. <c>Key</c> ist der Schluessel, unter dem das Bild
        ''' gilt (fuer <see cref="Offer"/>).</summary>
        Friend Async Function TakeAsync(path As String, reduced As Boolean) As Task(Of (Bitmap As Bitmap, RawDeveloped As Boolean, Key As String))
            Dim entry As Entry = Nothing
            SyncLock _lock
                entry = FindLocked(path, reduced)
                If entry Is Nothing Then Return (Nothing, False, Nothing)
                _entries.Remove(entry)
                If Not entry.Finished Then
                    Interlocked.Exchange(entry.Adopted, 1)
                    Interlocked.Increment(AdoptedCount)
                End If
            End SyncLock
            Await entry.Completion.Task.ConfigureAwait(False)
            Dim bmp As Bitmap
            Dim developed As Boolean
            SyncLock _lock
                bmp = entry.Bitmap
                developed = entry.RawDeveloped
                entry.Bitmap = Nothing
            End SyncLock
            If bmp Is Nothing Then Return (Nothing, False, Nothing)
            Dim stored = If(entry.KeyTask Is Nothing, "", Await entry.KeyTask.ConfigureAwait(False))
            Dim generation = AppSettingsService.Generation
            Dim current = Await Task.Run(Function() KeyFor(path, reduced, generation)).ConfigureAwait(False)
            If String.IsNullOrEmpty(stored) OrElse Not String.Equals(stored, current, StringComparison.Ordinal) Then
                Interlocked.Increment(StaleCount)
                DiagnosticLogService.LogAlways("Viewer.Prefetch", $"veraltet, neu entwickelt file={IO.Path.GetFileName(path)}")
                _release(bmp)
                Return (Nothing, False, Nothing)
            End If
            Interlocked.Increment(HitCount)
            DiagnosticLogService.LogAlways("Viewer.Prefetch", $"Treffer file={IO.Path.GetFileName(path)}")
            Return (bmp, developed, current)
        End Function

        ''' <summary>Uebernimmt ein schon gezeigtes Bild, das der Betrachter gerade verlaesst: es ist
        ''' beim Weiterblaettern das eine zurueck und muss so nicht neu entwickelt werden. True, wenn
        ''' der Vorlauf es behaelt; dann gehoert es ihm. <paramref name="keyTask"/> ist der Schluessel
        ''' vom Beginn seiner Entwicklung.</summary>
        Friend Function Offer(path As String, reduced As Boolean, bitmap As Bitmap, rawDeveloped As Boolean,
                              keyTask As Task(Of String)) As Boolean
            If bitmap Is Nothing OrElse keyTask Is Nothing OrElse String.IsNullOrEmpty(path) Then Return False
            Dim bytes = CLng(bitmap.PixelSize.Width) * bitmap.PixelSize.Height * 4
            SyncLock _lock
                If FindLocked(path, reduced) IsNot Nothing Then Return False
                ' Keine Grenze der Anzahl hier: angeboten wird beim Weiterblaettern, bevor das neue
                ' Bild abgeholt ist, und die vier alten Ziele liegen dann noch alle da. Das naechste
                ' Schedule laesst ohnehin nur die Ziele stehen.
                If StoredBytesLocked() + bytes > BudgetBytes(_currentImageBytes) Then Return False
                Dim entry As New Entry With {.Path = path, .Reduced = reduced, .KeyTask = keyTask, .Bitmap = bitmap,
                                             .RawDeveloped = rawDeveloped, .Bytes = bytes, .Finished = True}
                entry.Completion.TrySetResult(True)
                _entries.Add(entry)
            End SyncLock
            Return True
        End Function

        ''' <summary>Der Schluessel, unter dem ein entwickeltes Bild gilt. Liest Kopfdaten und
        ''' Beistelldateien, also nie auf dem Oberflaechenfaden.</summary>
        Friend Shared Function KeyFor(path As String, reduced As Boolean, settingsGeneration As Integer) As String
            Dim development = RawDecodeService.DevelopmentKey(path)
            If development.Length = 0 Then Return ""
            Return String.Join("||", development, LibraryService.SidecarStamp(path), reduced.ToString(),
                               settingsGeneration.ToString(Globalization.CultureInfo.InvariantCulture))
        End Function

        ''' <summary>Die Grenze des Speichers in Byte, neu gerechnet bei jeder Frage.</summary>
        Friend Shared Function BudgetBytes(currentImageBytes As Long) As Long
            If BudgetOverrideBytes >= 0 Then Return BudgetOverrideBytes
            Dim total As Long
            Try
                total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
            Catch
                Return 0
            End Try
            Dim room = (total \ 3) * 2 - SystemReserveBytes - EditorCacheReserveBytes - Math.Max(0, currentImageBytes)
            Return Math.Max(0, Math.Min(BudgetCapBytes, room))
        End Function

        ' ── Stand fuer Pruefstand und Protokoll ──────────────────────────────────

        Friend ReadOnly Property StoredBytes As Long
            Get
                SyncLock _lock
                    Return StoredBytesLocked()
                End SyncLock
            End Get
        End Property

        ''' <summary>Die Dateien mit fertigem Bild.</summary>
        Friend Function StoredPaths() As List(Of String)
            SyncLock _lock
                Return _entries.Where(Function(e) e.Finished AndAlso e.Bitmap IsNot Nothing).Select(Function(e) e.Path).ToList()
            End SyncLock
        End Function

        ''' <summary>Die Datei, die gerade vorlaeuft, oder Nothing.</summary>
        Friend Function RunningPath() As String
            SyncLock _lock
                Return _entries.Where(Function(e) Not e.Finished).Select(Function(e) e.Path).FirstOrDefault()
            End SyncLock
        End Function

        Friend ReadOnly Property IsWorking As Boolean
            Get
                SyncLock _lock
                    Return _workerRunning
                End SyncLock
            End Get
        End Property

        ' ── Der Arbeiter ──────────────────────────────────────────────────────────

        Private Async Function WorkerAsync() As Task
            Try
                Await Task.Delay(StartDelayMs).ConfigureAwait(False)
                Do
                    ' Erst wenn niemand die Schleuse haelt oder vor ihr wartet. Haelt sie jemand, waere
                    ' der Vorlauf nur der naechste in der Reihe, und der echte Auftrag nach ihm.
                    While DecodeGate.IsBusy OrElse DecodeGate.WaitingCount > 0
                        Await Task.Delay(IdlePollMs).ConfigureAwait(False)
                    End While
                    Dim job As Entry = Nothing
                    Dim warm As String = Nothing
                    SyncLock _lock
                        Dim budgetFull = False
                        For Each target In _targets
                            If _skipped.Contains(target) OrElse FindLocked(target, _reduced) IsNot Nothing Then Continue For
                            If _entries.Count >= MaxEntries OrElse
                               StoredBytesLocked() + _estimatedBytes > BudgetBytes(_currentImageBytes) Then
                                budgetFull = True
                                Exit For
                            End If
                            job = New Entry With {.Path = target, .Reduced = _reduced}
                            _entries.Add(job)
                            Exit For
                        Next
                        If budgetFull Then Interlocked.Increment(BudgetStopCount)
                        If job Is Nothing Then
                            warm = _warmPath
                            _warmPath = Nothing
                            If warm Is Nothing Then
                                _workerRunning = False
                                Return
                            End If
                        End If
                    End SyncLock
                    If job IsNot Nothing Then
                        RunJob(job)
                    Else
                        RunWarm(warm)
                    End If
                Loop
            Catch ex As Exception
                DiagnosticLogService.LogException("Viewer.Prefetch", ex)
                SyncLock _lock
                    _workerRunning = False
                End SyncLock
            End Try
        End Function

        Private Sub RunJob(job As Entry)
            Dim name = IO.Path.GetFileName(job.Path)
            Dim result As (Bitmap As Bitmap, RawDeveloped As Boolean) = (Nothing, False)
            Dim wanted As Func(Of Boolean) = Function() IsWanted(job)
            Dim failed = False
            Try
                If Not _willDevelop(job.Path) Then
                    ' Die eingebettete Vorschau ist schnell genug; das Bild bleibt fuer diese Ziele aussen vor.
                    MarkSkipped(job)
                    Return
                End If
                Dim generation = AppSettingsService.Generation
                Dim key = KeyFor(job.Path, job.Reduced, generation)
                job.KeyTask = Task.FromResult(key)
                If key.Length = 0 Then
                    MarkSkipped(job)
                    Return
                End If
                If Not ReadAhead(job, wanted) Then
                    DiagnosticLogService.LogAlways("Viewer.Prefetch", $"Lesen abgebrochen file={name}")
                Else
                    Interlocked.Increment(StartedCount)
                    result = DecodeGate.RunYielding(
                        Function() As (Bitmap As Bitmap, RawDeveloped As Boolean)
                            If Not wanted() Then Return (Nothing, False)
                            Return RawDecodeService.RunWhileWanted(wanted, Function() _decode(job.Path, job.Reduced))
                        End Function)
                    failed = result.Bitmap Is Nothing AndAlso wanted()
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Viewer.Prefetch", ex)
                failed = True
            End Try
            Finish(job, result, failed)
        End Sub

        Private Sub MarkSkipped(job As Entry)
            SyncLock _lock
                _skipped.Add(job.Path)
            End SyncLock
            Finish(job, (Nothing, False), failed:=False)
        End Sub

        ''' <summary>Liest die Datei einmal ganz, damit der Decode danach aus dem Seitenspeicher des
        ''' Systems liest. Bricht zwischen zwei Stuecken ab, wenn der Vorlauf nicht mehr gebraucht wird
        ''' oder ein anderer Decode laeuft (der liest dann selbst und soll die Leitung haben).</summary>
        Private Shared Function ReadAhead(job As Entry, wanted As Func(Of Boolean)) As Boolean
            Using stream As New FileStream(job.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete,
                                           1, FileOptions.SequentialScan)
                Dim buffer(ReadChunkBytes - 1) As Byte
                Do
                    If Not wanted() Then Return False
                    If Volatile.Read(job.Adopted) = 0 AndAlso DecodeGate.IsBusy Then Return False
                    If stream.Read(buffer, 0, buffer.Length) <= 0 Then Return True
                Loop
            End Using
        End Function

        ''' <summary>Wird dieser Vorlauf noch gebraucht? Uebernommen immer; sonst, solange er Ziel ist
        ''' und niemand vor der Schleuse wartet. Laeuft auch im Fortschrittsrueckruf von LibRaw.</summary>
        Private Function IsWanted(job As Entry) As Boolean
            If Volatile.Read(job.Adopted) = 1 Then Return True
            SyncLock _lock
                If job.Discarded OrElse Not IsTargetLocked(job.Path, job.Reduced) Then Return False
            End SyncLock
            Return DecodeGate.WaitingCount = 0
        End Function

        Private Sub Finish(job As Entry, result As (Bitmap As Bitmap, RawDeveloped As Boolean), failed As Boolean)
            Dim dropped As New List(Of Entry)()
            Dim releaseNow As Bitmap = Nothing
            Dim name = IO.Path.GetFileName(job.Path)
            Dim kept = False
            SyncLock _lock
                job.Finished = True
                Dim adopted = Volatile.Read(job.Adopted) = 1
                If result.Bitmap IsNot Nothing AndAlso (adopted OrElse (Not job.Discarded AndAlso IsTargetLocked(job.Path, job.Reduced))) Then
                    kept = True
                    job.Bitmap = result.Bitmap
                    job.RawDeveloped = result.RawDeveloped
                    job.Bytes = CLng(result.Bitmap.PixelSize.Width) * result.Bitmap.PixelSize.Height * 4
                    _estimatedBytes = job.Bytes
                    If Not adopted Then
                        Interlocked.Increment(StoredCount)
                        EnforceBudgetLocked(dropped)
                    End If
                Else
                    releaseNow = result.Bitmap
                    If Not adopted Then _entries.Remove(job)
                    If failed Then
                        _skipped.Add(job.Path)
                    ElseIf result.Bitmap Is Nothing AndAlso Not _skipped.Contains(job.Path) Then
                        Interlocked.Increment(AbortedCount)
                    End If
                End If
            End SyncLock
            If kept Then
                DiagnosticLogService.LogAlways("Viewer.Prefetch", $"fertig file={name}; {job.Bytes \ (1024 * 1024)} MB")
            Else
                DiagnosticLogService.LogAlways("Viewer.Prefetch", $"{If(failed, "fehlgeschlagen", "verworfen")} file={name}")
            End If
            If releaseNow IsNot Nothing Then _release(releaseNow)
            ReleaseAll(dropped)
            job.Completion.TrySetResult(True)
        End Sub

        ''' <summary>Fuellt nach den Zielen den 16-Bit-Zwischenspeicher fuer das gezeigte Bild. Weicht
        ''' wie ein Vorlauf, und nur solange es noch das gezeigte ist.</summary>
        Private Sub RunWarm(path As String)
            Try
                Interlocked.Increment(WarmCount)
                Dim filled = DecodeGate.RunYielding(
                    Function() RawDecodeService.WarmRawCache(path, Function() IsWarmWanted(path)))
                DiagnosticLogService.LogAlways("Viewer.Prefetch", $"Editor vorgewaermt={filled} file={IO.Path.GetFileName(path)}")
            Catch ex As Exception
                DiagnosticLogService.LogException("Viewer.Prefetch", ex)
            End Try
        End Sub

        Private Function IsWarmWanted(path As String) As Boolean
            SyncLock _lock
                If Not String.Equals(path, _currentPath, PathIdentity.Comparison) Then Return False
            End SyncLock
            Return DecodeGate.WaitingCount = 0
        End Function

        ' ── Helfer unter _lock ────────────────────────────────────────────────────

        Private Function FindLocked(path As String, reduced As Boolean) As Entry
            For Each e In _entries
                If e.Reduced = reduced AndAlso String.Equals(e.Path, path, PathIdentity.Comparison) Then Return e
            Next
            Return Nothing
        End Function

        Private Function IsTargetLocked(path As String, reduced As Boolean) As Boolean
            If reduced <> _reduced Then Return False
            For Each t In _targets
                If String.Equals(t, path, PathIdentity.Comparison) Then Return True
            Next
            Return False
        End Function

        Private Function StoredBytesLocked() As Long
            Dim sum As Long = 0
            For Each e In _entries
                If e.Finished AndAlso e.Bitmap IsNot Nothing Then sum += e.Bytes
            Next
            Return sum
        End Function

        ''' <summary>Ueber der Grenze faellt das Bild mit dem niedrigsten Rang (zuletzt in den Zielen)
        ''' heraus, bis der Rest passt.</summary>
        Private Sub EnforceBudgetLocked(dropped As List(Of Entry))
            Dim budget = BudgetBytes(_currentImageBytes)
            While StoredBytesLocked() > budget
                Dim victim As Entry = Nothing
                Dim victimRank = -1
                For Each e In _entries
                    If Not e.Finished OrElse e.Bitmap Is Nothing Then Continue For
                    Dim rank = _targets.FindIndex(Function(t) String.Equals(t, e.Path, PathIdentity.Comparison))
                    If rank < 0 Then rank = Integer.MaxValue
                    If rank > victimRank Then
                        victim = e
                        victimRank = rank
                    End If
                Next
                If victim Is Nothing Then Exit While
                _entries.Remove(victim)
                victim.Discarded = True
                dropped.Add(victim)
            End While
        End Sub

        Private Sub ReleaseAll(dropped As List(Of Entry))
            For Each e In dropped
                Dim bmp As Bitmap
                SyncLock _lock
                    bmp = e.Bitmap
                    e.Bitmap = Nothing
                End SyncLock
                If bmp IsNot Nothing Then _release(bmp)
            Next
        End Sub

    End Class

End Namespace
