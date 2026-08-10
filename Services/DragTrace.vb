Imports System.Diagnostics
Imports System.Threading

Namespace Services

    ''' <summary>Ablaufspur des Ziehens und Ablegens - fuer das Diagnoselog.
    '''
    ''' WOZU: Bleibt die Anwendung waehrend eines Zuges stehen, sagt kein Abbruch und keine Ausnahme,
    ''' WO sie steht. Das Protokoll schreibt jede Zeile sofort auf die Platte (File.AppendAllText),
    ''' also steht dort noch, was zuletzt BEGONNEN wurde - und genau das ist die Antwort: der Schritt
    ''' ohne zugehoerige Abschlusszeile ist der, der nicht zurueckkam.
    '''
    ''' Deshalb wird hier VOR dem verdaechtigen Aufruf geschrieben und danach noch einmal, mit Dauer.
    ''' Der verdaechtige Aufruf ist das Lesen der Ziehlast: unter X11 blockiert es den Faden und
    ''' pollt X-Ereignisse (siehe <see cref="DragPayloadCache"/>).
    '''
    ''' Ein Zug erzeugt hunderte Zeigerberichte. Gedeckelt wird deshalb zweifach: die ersten Schritte
    ''' eines Zuges werden immer festgehalten, danach nur noch Auffaelliges - ein Lesen, das laenger
    ''' als eine Zwanzigstelsekunde gedauert hat.</summary>
    Public NotInheritable Class DragTrace

        Private Sub New()
        End Sub

        Private Const SlowFromMs As Long = 50
        Private Const AlwaysFirstN As Integer = 3

        Private Shared _overCount As Integer = 0
        Private Shared _readCount As Integer = 0
        Private Shared _readMs As Long = 0
        Private Shared _slowReads As Integer = 0

        ''' <summary>Ob ueberhaupt protokolliert wird - EINMAL JE ZUG gelesen, nicht je Bericht.
        ''' Der Protokolldienst fragt dafuer die Einstellungen, und die kommen aus einer
        ''' JSON-Zeichenkette, die bei jedem Aufruf neu gelesen wird. Bei hunderten Berichten je
        ''' Sekunde ist das genau die Sorte Kleinigkeit, die eine Ziehgeste zaeh macht.</summary>
        Private Shared _enabled As Boolean = False

        ''' <summary>Ein eigener Zug beginnt.</summary>
        Public Shared Sub Begin(source As String, pathCount As Integer, withFiles As Boolean)
            Interlocked.Exchange(_overCount, 0)
            Interlocked.Exchange(_readCount, 0)
            Interlocked.Exchange(_readMs, 0)
            Interlocked.Exchange(_slowReads, 0)
            _lastTarget = ""
            _enabled = AppSettingsService.Load().EnableDiagnosticLogging
            If Not _enabled Then Return
            DiagnosticLogService.LogAlways("Drag", $"start quelle={source} pfade={pathCount} mitDateien={withFiles}")
        End Sub

        Private Shared _lastTarget As String = ""

        ''' <summary>Ein Zeigerbericht ueber einem Ziel.
        '''
        ''' JEDER ZIELWECHSEL wird festgehalten, nicht nur jeder x-te Bericht. Genau daran haengt die
        ''' Frage, die eine Deckelung nach Anzahl verschweigt: LEBTE die Geste noch, als der Zeiger
        ''' das naechste Ziel erreichte? Steht der Wechsel im Protokoll und danach nichts mehr, ist
        ''' sie dort gestorben; fehlt er, kam der Zeiger nie an.</summary>
        Public Shared Sub Over(target As String)
            If Not _enabled Then Return
            Dim n = Interlocked.Increment(_overCount)
            Dim changed = Not String.Equals(_lastTarget, target, StringComparison.Ordinal)
            If changed Then _lastTarget = target
            If changed OrElse n <= AlwaysFirstN OrElse n Mod 50 = 0 Then
                DiagnosticLogService.LogAlways("Drag", $"over #{n} ziel={target}" & If(changed, " (neu)", ""))
            End If
        End Sub

        ''' <summary>VOR dem Lesen der Ziehlast ueber das Fenstersystem. Fehlt danach die Zeile aus
        ''' <see cref="Read"/>, ist genau hier Schluss gewesen.</summary>
        Public Shared Sub BeforeRead(channel As String)
            If Not _enabled Then Return
            Dim n = Interlocked.Increment(_readCount)
            If n <= AlwaysFirstN OrElse n Mod 200 = 0 Then
                DiagnosticLogService.LogAlways("Drag", $"lese #{n} ueber {channel} …")
            End If
        End Sub

        ''' <summary>Nach dem Lesen, mit Dauer.</summary>
        Public Shared Sub Read(channel As String, ms As Long, pathCount As Integer)
            If Not _enabled Then Return
            Interlocked.Add(_readMs, ms)
            If ms >= SlowFromMs Then
                Dim slowSoFar = Interlocked.Increment(_slowReads)
                DiagnosticLogService.LogAlways("Drag", $"LANGSAM: {channel} brauchte {ms} ms, pfade={pathCount} (langsam bisher {slowSoFar})")
            ElseIf _readCount <= AlwaysFirstN Then
                DiagnosticLogService.LogAlways("Drag", $"gelesen ueber {channel}: {ms} ms, pfade={pathCount}")
            End If
        End Sub

        ''' <summary>Der Zug ist vorbei - mit der Bilanz.</summary>
        Public Shared Sub Finish(outcome As String)
            If Not _enabled Then Return
            DiagnosticLogService.LogAlways("Drag",
                $"ende {outcome} berichte={_overCount} lesevorgaenge={_readCount} lesezeit={_readMs} ms langsam={_slowReads}")
        End Sub

        ''' <summary>Misst einen Lesevorgang und schreibt beide Zeilen darum.</summary>
        ''' readPayload und NICHT "read": ein Parameter dieses Namens verdeckt die Methode Read
        ''' darueber, und der Aufruf unten landete dann auf dem Func statt auf ihr (VB-Falle).
        Public Shared Function Measure(Of T)(channel As String, readPayload As Func(Of T), countOf As Func(Of T, Integer)) As T
            If Not _enabled Then Return readPayload()
            BeforeRead(channel)
            Dim watch = Stopwatch.StartNew()
            Dim result = readPayload()
            watch.Stop()
            Read(channel, watch.ElapsedMilliseconds, countOf(result))
            Return result
        End Function

    End Class

End Namespace
