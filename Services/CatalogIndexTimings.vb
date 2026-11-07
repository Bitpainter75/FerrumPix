Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text

Namespace Services

    ''' <summary>Die Schritte, in die der Katalogindex die Arbeit an einer Datei aufteilt.</summary>
    Public Enum CatalogIndexStep
        ''' <summary>Aenderungszeit und Beistelldateien vergleichen, ob die Datei ueberhaupt
        ''' gelesen werden muss.</summary>
        Check = 0
        ''' <summary>Aufnahmedaten aus der Datei lesen und zusammenfassen.</summary>
        Metadata = 1
        ''' <summary>Die Zeile in den Katalog schreiben (SyncExifData).</summary>
        Catalog = 2
        ''' <summary>.fpxmp, XMP, .pp3 und die Stichwoerter daraus samt Bewertung und Etikett.</summary>
        Sidecars = 3
        ''' <summary>Die Kachel bereitlegen.</summary>
        Thumbnail = 4
        ''' <summary>Die Bildverschlagwortung.</summary>
        AiTagging = 5
    End Enum

    ''' <summary>
    ''' Die Zeitmessung eines Katalogindex-Laufs fuer das Diagnoseprotokoll.
    '''
    ''' <para>NUR MIT EINGESCHALTETEM PROTOKOLL. Ohne Protokoll gibt es gar keine Instanz, und der
    ''' Lauf ruft nichts davon auf. Die Messung soll beantworten, wohin die Zeit eines langen Laufs
    ''' gegangen ist, ohne dass jemand nachfragen muss.</para>
    '''
    ''' <para>KEINE ZEILE JE DATEI. Bei einem Bestand mit Zehntausenden Fotos wuerde das Protokoll
    ''' riesig, und das Schreiben bremste den Lauf selbst. Statt dessen: Summen je Schritt und je
    ''' Dateityp, die langsamsten Dateien am Ende, Ausreisser sofort und in festen Abstaenden ein
    ''' Zwischenstand. Die letzten beiden, weil ein Lauf, der im Ruhezustand des Rechners oder durch
    ''' Stromausfall endet, seine Schlusszeile nie schreibt.</para>
    '''
    ''' <para>EINFAEDIG wie der Lauf selbst, deshalb ohne Sperren.</para>
    ''' </summary>
    Public NotInheritable Class CatalogIndexTimings

        ''' <summary>Ab dieser Dauer bekommt eine Datei sofort eine eigene Zeile.</summary>
        Public Const OutlierMilliseconds As Double = 2000.0

        ''' <summary>Abstand der Zwischenstaende.</summary>
        Public Shared ReadOnly ProgressInterval As TimeSpan = TimeSpan.FromMinutes(5)

        ''' <summary>So viele langsamste Dateien nennt die Schlusszeile.</summary>
        Private Const SlowestCount As Integer = 10

        Private Shared ReadOnly StepNames As String() = {
            "Check", "Metadata", "Catalog", "Sidecars", "Thumbnail", "Image recognition"
        }

        Private NotInheritable Class Stat
            Public Count As Integer
            Public TotalMs As Double
            Public MaxMs As Double

            Public Sub Add(ms As Double)
                Count += 1
                TotalMs += ms
                If ms > MaxMs Then MaxMs = ms
            End Sub
        End Class

        Private NotInheritable Class SlowFile
            Public Path As String
            Public TotalMs As Double
            Public SlowestStep As String
            Public SlowestStepMs As Double
            Public RawSources As String
        End Class

        Private ReadOnly _steps As Stat() = Enumerable.Range(0, StepNames.Length).Select(Function(i) New Stat()).ToArray()
        Private ReadOnly _types As New Dictionary(Of String, Stat)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _slowest As New List(Of SlowFile)()
        Private ReadOnly _runWatch As Stopwatch = Stopwatch.StartNew()
        Private _nextProgress As TimeSpan = ProgressInterval

        ' Die laufende Datei.
        Private _filePath As String
        Private _fileStart As Long
        Private ReadOnly _fileSteps As Double() = New Double(StepNames.Length - 1) {}
        Private ReadOnly _fileRawSources As New List(Of String)()

        ' Je Schritt und RAW-Quelle, etwa "Bilderkennung entwickelt".
        Private ReadOnly _rawSources As New Dictionary(Of String, Stat)(StringComparer.Ordinal)

        ''' <summary>Eine Instanz, wenn protokolliert wird, sonst Nothing.</summary>
        Public Shared Function StartIfLogging() As CatalogIndexTimings
            Return If(DiagnosticLogService.IsVerboseEnabled, New CatalogIndexTimings(), Nothing)
        End Function

        Private Sub New()
        End Sub

        ''' <summary>Wie lange der Lauf schon dauert.</summary>
        Public ReadOnly Property Elapsed As TimeSpan
            Get
                Return _runWatch.Elapsed
            End Get
        End Property

        ''' <summary>Der Anfang der Arbeit an einer Datei.</summary>
        Public Sub BeginFile(filePath As String)
            _filePath = filePath
            _fileStart = Stopwatch.GetTimestamp()
            Array.Clear(_fileSteps, 0, _fileSteps.Length)
            _fileRawSources.Clear()
            ' Ein Vermerk aus einer frueheren Datei darf nicht dieser zugerechnet werden.
            ThumbnailCacheService.TakeLastRawSource()
        End Sub

        ''' <summary>Ein Zeitpunkt, von dem aus <see cref="AddStep"/> misst.</summary>
        Public Shared Function Mark() As Long
            Return Stopwatch.GetTimestamp()
        End Function

        ''' <summary>Rechnet die Zeit seit <paramref name="startMark"/> dem Schritt zu.
        '''
        ''' <para>Bei Kachel und Bilderkennung auch, WOHER das Bild einer RAW kam (siehe
        ''' ThumbnailCacheService.TakeLastRawSource): eingebettete Vorschau oder Entwicklung. Die
        ''' Bilderkennung holt ihre Bildpunkte ueber denselben Weg wie die Kachel, und bei einer RAW
        ''' ohne brauchbare Vorschau heisst das eine Entwicklung je Bild, auch wenn die Kachel
        ''' laengst da ist.</para></summary>
        Public Sub AddStep(stepKind As CatalogIndexStep, startMark As Long)
            Dim ms = Stopwatch.GetElapsedTime(startMark).TotalMilliseconds
            _fileSteps(stepKind) += ms
            If stepKind = CatalogIndexStep.Thumbnail OrElse stepKind = CatalogIndexStep.AiTagging Then
                Dim source = ThumbnailCacheService.TakeLastRawSource()
                If source IsNot Nothing Then
                    Dim key = $"{StepNames(stepKind)} {source}"
                    Dim stat As Stat = Nothing
                    If Not _rawSources.TryGetValue(key, stat) Then
                        stat = New Stat()
                        _rawSources(key) = stat
                    End If
                    stat.Add(ms)
                    _fileRawSources.Add(key)
                End If
            End If
        End Sub

        ''' <summary>Das Ende der Arbeit an einer Datei: Summen fortschreiben, Ausreisser sofort
        ''' melden.</summary>
        Public Sub EndFile()
            If _filePath Is Nothing Then Return
            Dim totalMs = Stopwatch.GetElapsedTime(_fileStart).TotalMilliseconds
            For i = 0 To _fileSteps.Length - 1
                If _fileSteps(i) > 0 Then _steps(i).Add(_fileSteps(i))
            Next

            Dim extension = Path.GetExtension(_filePath)
            If String.IsNullOrEmpty(extension) Then extension = "(none)"
            Dim typeStat As Stat = Nothing
            If Not _types.TryGetValue(extension, typeStat) Then
                typeStat = New Stat()
                _types(extension) = typeStat
            End If
            typeStat.Add(totalMs)

            Dim slowestIndex = 0
            For i = 1 To _fileSteps.Length - 1
                If _fileSteps(i) > _fileSteps(slowestIndex) Then slowestIndex = i
            Next
            Dim entry As New SlowFile With {
                .Path = _filePath, .TotalMs = totalMs,
                .SlowestStep = StepNames(slowestIndex), .SlowestStepMs = _fileSteps(slowestIndex),
                .RawSources = String.Join(", ", _fileRawSources)
            }
            If _slowest.Count < SlowestCount OrElse totalMs > _slowest(_slowest.Count - 1).TotalMs Then
                _slowest.Add(entry)
                _slowest.Sort(Function(a, b) b.TotalMs.CompareTo(a.TotalMs))
                If _slowest.Count > SlowestCount Then _slowest.RemoveAt(_slowest.Count - 1)
            End If

            If totalMs >= OutlierMilliseconds Then
                DiagnosticLogService.LogAlways("CatalogIndex.Slow",
                    $"{Ms(totalMs)} {_filePath} ({FileStepsText()})")
            End If
            _filePath = Nothing
        End Sub

        ''' <summary>Schreibt alle <see cref="ProgressInterval"/> einen Zwischenstand.</summary>
        Public Sub ReportProgressIfDue(done As Integer, total As Integer, currentFolder As String)
            Dim elapsed = _runWatch.Elapsed
            If elapsed < _nextProgress Then Return
            _nextProgress = elapsed + ProgressInterval
            Dim perMinute = If(elapsed.TotalMinutes > 0, done / elapsed.TotalMinutes, 0.0)
            DiagnosticLogService.LogAlways("CatalogIndex.Progress",
                $"{done} of {total} after {Duration(elapsed)}, " &
                $"{perMinute.ToString("0", CultureInfo.InvariantCulture)} files per minute, folder {currentFolder}")
        End Sub

        ''' <summary>Die Schlusszeilen: Schritte, Dateitypen, langsamste Dateien.</summary>
        Public Sub LogSummary()
            Dim stepParts As New List(Of String)()
            For i = 0 To _steps.Length - 1
                Dim s = _steps(i)
                If s.Count = 0 Then Continue For
                stepParts.Add($"{StepNames(i)}: {StatText(s)}")
            Next
            If stepParts.Count > 0 Then
                DiagnosticLogService.LogAlways("CatalogIndex.Steps", String.Join(" | ", stepParts))
            End If

            If _types.Count > 0 Then
                DiagnosticLogService.LogAlways("CatalogIndex.FileTypes",
                    String.Join(" | ", _types.OrderByDescending(Function(p) p.Value.TotalMs).
                                              Select(Function(p) $"{p.Key}: {StatText(p.Value)}")))
            End If

            If _rawSources.Count > 0 Then
                DiagnosticLogService.LogAlways("CatalogIndex.RawSources",
                    String.Join(" | ", _rawSources.OrderBy(Function(p) p.Key, StringComparer.Ordinal).
                                                   Select(Function(p) $"{p.Key}: {StatText(p.Value)}")))
            End If

            If _slowest.Count > 0 Then
                DiagnosticLogService.LogAlways("CatalogIndex.Slowest",
                    String.Join(" | ", _slowest.Select(Function(f) $"{Ms(f.TotalMs)} {f.Path} (of which {f.SlowestStep} {Ms(f.SlowestStepMs)}" &
                                                       If(String.IsNullOrEmpty(f.RawSources), "", ", " & f.RawSources) & ")")))
            End If
        End Sub

        ''' <summary>Die Schalter, die bestimmen, was ein Lauf je Datei tut und wie lange er dafuer
        ''' braucht. Am Anfang UND am Ende eines Laufs ins Protokoll: wer mitten im Lauf umschaltet,
        ''' erklaert sonst Zahlen, die zu keiner der beiden Stellungen passen.</summary>
        Public Shared Function SettingsText() As String
            Dim s = AppSettingsService.Load()
            Return String.Join(", ", {
                $"image recognition while indexing {OnOff(ImageTaggingService.Enabled)}",
                $"image recognition model {If(ImageTaggingService.Available, "loaded", "not loaded")}",
                $"recognised keywords to XMP {OnOff(s.WriteAiTagsToXmp)}",
                $"read .pp3/.arp {OnOff(Pp3SidecarService.IsEnabled)}",
                $"develop RAW thumbnails {OnOff(s.DevelopRawThumbnails)}",
                $"LibRaw {If(RawDecodeService.IsAvailable, "available", "missing")}",
                $"index on startup {OnOff(s.CatalogIndexOnStartup)}"
            })
        End Function

        Private Shared Function OnOff(value As Boolean) As String
            Return If(value, "on", "off")
        End Function

        Private Function FileStepsText() As String
            Dim parts As New List(Of String)()
            For i = 0 To _fileSteps.Length - 1
                If _fileSteps(i) > 0 Then parts.Add($"{StepNames(i)} {Ms(_fileSteps(i))}")
            Next
            For Each source In _fileRawSources
                parts.Add(source)
            Next
            Return String.Join(", ", parts)
        End Function

        Private Shared Function StatText(s As Stat) As String
            Dim mean = If(s.Count > 0, s.TotalMs / s.Count, 0.0)
            Return $"{s.Count}x, total {Duration(TimeSpan.FromMilliseconds(s.TotalMs))}, mean {Ms(mean)}, max {Ms(s.MaxMs)}"
        End Function

        Private Shared Function Ms(value As Double) As String
            Return value.ToString("0.0", CultureInfo.InvariantCulture) & " ms"
        End Function

        ''' <summary>Eine Dauer, lesbar: Sekunden, Minuten oder Stunden mit Minuten.</summary>
        Public Shared Function Duration(value As TimeSpan) As String
            If value.TotalSeconds < 60 Then Return value.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) & " s"
            If value.TotalMinutes < 60 Then Return $"{CInt(Math.Floor(value.TotalMinutes))} min {value.Seconds} s"
            Return $"{CInt(Math.Floor(value.TotalHours))} h {value.Minutes} min"
        End Function

    End Class

End Namespace
