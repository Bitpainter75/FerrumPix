Imports System
Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports Avalonia.Threading

Namespace Services

    ''' <summary>
    ''' Messpunkte in der laufenden Anwendung, fuer Befunde, die sich nur am ECHTEN Fenster zeigen.
    '''
    ''' <para>Anlass war die Bildlaufleiste in grossen Ordnern (Nutzerbefund): im
    ''' Pruefstand ohne Bildschirm liessen sich Suche, Fadenfuehrung, Rollbereich und das Erzeugen
    ''' der Vorschaubilder als Ursache ausschliessen, der Rest liegt im Aufbau der Kacheln - und den
    ''' misst ein Lauf ohne Grafikkarte nicht ehrlich. Also misst die Anwendung selbst, der Benutzer
    ''' fuehrt die Bewegung aus, und das Protokoll sagt, wo die Zeit bleibt.</para>
    '''
    ''' <para>ES DARF NICHTS KOSTEN, wenn niemand misst: alle Wege pruefen zuerst den Schalter
    ''' (<see cref="DiagnosticLogService.IsVerboseEnabled"/>, ein gemerktes Feld) und kehren sonst
    ''' sofort zurueck. Gesammelt wird im Speicher; geschrieben wird gebuendelt, sonst waere das
    ''' Protokollieren selbst die groesste Last.</para>
    ''' </summary>
    Public NotInheritable Class PerformanceTraceService

        Private Sub New()
        End Sub

        Private NotInheritable Class Sammlung
            Public Property Count As Integer
            Public Property SumMs As Double
            Public Property MaxMs As Double
        End Class

        Private Shared ReadOnly _messungen As New ConcurrentDictionary(Of String, Sammlung)()
        Private Shared ReadOnly _sperre As New Object()
        Private Shared _lastReport As DateTime = DateTime.MinValue

        ''' <summary>Wie oft eine Zusammenfassung ins Protokoll geht. Kurz genug, um eine einzelne
        ''' Bewegung wiederzufinden, lang genug, dass das Schreiben nicht selbst zaehlt.</summary>
        Private Const AusgabeIntervallSekunden As Double = 2.0

        ''' <summary>Ab wann eine Stockung des Anzeigefadens eine Zeile wert ist. Ein Bild bei 60 Hz
        ''' dauert 16 ms; alles unter 200 ms merkt beim Rollen niemand.</summary>
        Private Const StockungAbMs As Double = 200.0

        Public Shared ReadOnly Property IsActive As Boolean
            Get
                Return DiagnosticLogService.IsVerboseEnabled
            End Get
        End Property

        ''' <summary>Eine gemessene Dauer festhalten. Der Name ist der Messpunkt, nicht der Vorgang -
        ''' gleiche Namen werden zusammengezaehlt.</summary>
        Public Shared Sub Record(name As String, milliseconds As Double)
            If Not IsActive OrElse String.IsNullOrEmpty(name) Then Return
            Dim eintrag = _messungen.GetOrAdd(name, Function(k) New Sammlung())
            SyncLock eintrag
                eintrag.Count += 1
                eintrag.SumMs += milliseconds
                If milliseconds > eintrag.MaxMs Then eintrag.MaxMs = milliseconds
            End SyncLock
            ReportWhenDue()
        End Sub

        ''' <summary>Was der Anzeigefaden gerade tut, soweit es einen Messpunkt dafuer gibt.
        ''' Der Waechter liest das, wenn er eine Stockung feststellt - sonst wuesste man nur, DASS
        ''' der Faden stand, und nicht wobei. Ein leerer Wert ist selbst eine Auskunft: dann liegt
        ''' der Blockierer an einer Stelle ohne Messpunkt.</summary>
        Private Shared _currentSection As String = ""
        Private Shared _sectionStartedTicks As Long = 0

        ''' <summary>Misst einen Block und haelt seine Dauer fest. Ist nicht eingeschaltet, laeuft der
        ''' Block ohne jede Zutat.</summary>
        Public Shared Sub Measure(name As String, action As Action)
            If action Is Nothing Then Return
            If Not IsActive Then
                action()
                Return
            End If
            ' Nur der ANZEIGEFADEN traegt den Marker: der Waechter fragt nach ihm, und ein
            ' Hintergrundfaden wuerde ihn nur ueberschreiben.
            Dim onUiThread = Dispatcher.UIThread.CheckAccess()
            Dim vorheriger = _currentSection
            If onUiThread Then
                _currentSection = name
                Threading.Volatile.Write(_sectionStartedTicks, Stopwatch.GetTimestamp())
            End If
            Dim uhr = Stopwatch.StartNew()
            Try
                action()
            Finally
                uhr.Stop()
                If onUiThread Then _currentSection = vorheriger
                Record(name, uhr.Elapsed.TotalMilliseconds)
            End Try
        End Sub

        ''' <summary>Dasselbe fuer einen Block MIT Ergebnis. Ohne diese Fassung fuehrt jeder Aufrufer,
        ''' der einen Wert zurueckgibt, seine Uhr von Hand - und dann heisst der Messpunkt an jeder
        ''' Stelle ein wenig anders, obwohl im Protokoll gerade das Zusammenzaehlen gleicher Namen
        ''' den Wert ausmacht.</summary>
        Public Shared Function Measure(Of T)(name As String, func As Func(Of T)) As T
            If func Is Nothing Then Return Nothing
            If Not IsActive Then Return func()
            Dim onUiThread = Dispatcher.UIThread.CheckAccess()
            Dim vorheriger = _currentSection
            If onUiThread Then
                _currentSection = name
                Threading.Volatile.Write(_sectionStartedTicks, Stopwatch.GetTimestamp())
            End If
            Dim uhr = Stopwatch.StartNew()
            Try
                Return func()
            Finally
                uhr.Stop()
                If onUiThread Then _currentSection = vorheriger
                Record(name, uhr.Elapsed.TotalMilliseconds)
            End Try
        End Function

        Private Shared Sub ReportWhenDue()
            Dim jetzt = DateTime.UtcNow
            SyncLock _sperre
                If (jetzt - _lastReport).TotalSeconds < AusgabeIntervallSekunden Then Return
                _lastReport = jetzt
            End SyncLock
            Flush()
        End Sub

        ''' <summary>Schreibt, was seit der letzten Ausgabe zusammengekommen ist, und faengt von
        ''' vorn an. Die Zeile nennt je Messpunkt Anzahl, Mittel und Groesstwert - der Groesstwert
        ''' ist der interessante: eine einzelne lange Stockung verschwindet im Mittel.</summary>
        Public Shared Sub Flush()
            If _messungen.IsEmpty Then Return
            Dim teile As New List(Of String)()
            For Each name In _messungen.Keys.OrderBy(Function(k) k).ToList()
                Dim eintrag As Sammlung = Nothing
                If Not _messungen.TryRemove(name, eintrag) OrElse eintrag Is Nothing OrElse eintrag.Count = 0 Then Continue For
                teile.Add($"{name}: {eintrag.Count}x, Mittel {eintrag.SumMs / eintrag.Count:F1} ms, max {eintrag.MaxMs:F1} ms")
            Next
            If teile.Count = 0 Then Return
            DiagnosticLogService.LogAlways("Messung", String.Join(" | ", teile))
        End Sub

        ' ── Wächter über dem Anzeigefaden ────────────────────────────────────────

        Private Shared _watchdog As DispatcherTimer
        Private Shared _beobachter As Threading.Thread
        Private Shared _letzterSchlagTicks As Long = 0

        ''' <summary>Die Nummer des jüngsten Beobachters. Ein gemeinsames "läuft"-Kennzeichen genügt
        ''' nicht: wer den Schalter schnell aus- und wieder einschaltet, startet einen zweiten Faden,
        ''' während der erste seine Schlafpause noch nicht beendet hat - der sieht danach wieder
        ''' "läuft" und arbeitet neben dem neuen weiter. Zwei Fäden bedeuten doppelte
        ''' Protokollzeilen und einen Faden, der nie mehr aufhört. Mit einer Nummer beendet sich der
        ''' alte beim nächsten Blick von selbst.</summary>
        Private Shared _beobachterLauf As Integer = 0

        ''' <summary>Startet den Waechter. Er besteht aus ZWEI Teilen, und das ist der Punkt:
        '''
        ''' <para>Ein Zeitgeber auf dem Anzeigefaden setzt zehnmal je Sekunde einen Zeitstempel. Er
        ''' allein wuerde eine Stockung erst melden, wenn sie vorbei ist - und dann ist nicht mehr
        ''' festzustellen, WOBEI der Faden stand.</para>
        '''
        ''' <para>Deshalb schaut ein eigener Faden von aussen zu. Er sieht die Stockung, WAEHREND
        ''' sie laeuft, und schreibt mit, welcher Messpunkt gerade offen ist. Steht dort nichts,
        ''' ist auch das eine Auskunft: dann blockiert eine Stelle, die keinen Messpunkt hat.</para></summary>
        Public Shared Sub StartUiThreadWatchdog()
            ' Unter der Sperre: der Zeitgeber wird vom Anzeigefaden gestartet, der Prüfstand ruft
            ' denselben Weg aber auch aus einem anderen Faden. Zwei gleichzeitige Starts saehen
            ' beide "noch kein Waechter da" und liessen zwei Beobachter zurueck.
            SyncLock _sperre
                If _watchdog IsNot Nothing Then Return
                If Not IsActive Then Return
                StartUiThreadWatchdogLocked()
            End SyncLock
        End Sub

        ''' <summary>Stand der Speicherbereinigung beim letzten Herzschlag: Zahl der Gen-2-Läufe und
        ''' die gesamte Pausenzeit bis dahin. Der Beobachter zieht ihn am Ende einer Stockung vom
        ''' dann aktuellen Stand ab - so steht neben jeder Stockung, ob und wie lange in ihr
        ''' bereinigt wurde.</summary>
        Private Shared _schlagGen2 As Integer = 0
        Private Shared _schlagPauseTicks As Long = 0

        ''' <summary>Der zweite Blick auf den Anzeigefaden: ein Auftrag mit HOECHSTEM Vorrang (Send),
        ''' den der Beobachter alle 50 ms hineinreicht. Der Herzschlag laeuft mit Hintergrund-Vorrang
        ''' und bleibt deshalb auch dann aus, wenn der Faden zwar arbeitet, aber nur Eingaben, Bindungen
        ''' und Layout abarbeitet - beim Ziehen eines Reglers ist das der Normalfall. Kommt dagegen
        ''' der Ping nicht an, steht der Faden wirklich in EINER langen Operation. Beide Messungen
        ''' zusammen trennen "ausgelastet" von "blockiert".
        '''
        ''' 0 heisst: kein Ping unterwegs. Sonst der Zeitstempel, zu dem er losgeschickt wurde.</summary>
        Private Shared _pingGesendetTicks As Long = 0
        Private Shared _pingGen2 As Integer = 0
        Private Shared _pingPauseTicks As Long = 0
        Private Shared _pingGemeldet As Boolean = False
        Private Shared _pingWobei As String = ""

        Private Shared Sub PingAngekommen(gesendet As Long)
            Dim dauerMs = (Stopwatch.GetTimestamp() - gesendet) * 1000.0 / Stopwatch.Frequency
            If dauerMs >= StockungAbMs Then
                Dim gen2 = GC.CollectionCount(2) - _pingGen2
                Dim pauseMs = TimeSpan.FromTicks(GC.GetTotalPauseDuration().Ticks - _pingPauseTicks).TotalMilliseconds
                Record("Anzeigefaden blockiert", dauerMs)
                DiagnosticLogService.LogAlways("Messung",
                    $"Anzeigefaden blockiert {dauerMs:F0} ms - Bereinigung darin: {gen2} Gen-2-Laeufe, Pause {pauseMs:F0} ms - {_pingWobei}")
            End If
            _pingGemeldet = False
            _pingWobei = ""
            Threading.Volatile.Write(_pingGesendetTicks, 0L)
        End Sub

        Private Shared Sub PingPruefen()
            Dim gesendet = Threading.Volatile.Read(_pingGesendetTicks)
            If gesendet = 0 Then
                Dim jetzt = Stopwatch.GetTimestamp()
                _pingGen2 = GC.CollectionCount(2)
                _pingPauseTicks = GC.GetTotalPauseDuration().Ticks
                Threading.Volatile.Write(_pingGesendetTicks, jetzt)
                Dispatcher.UIThread.Post(Sub() PingAngekommen(jetzt), DispatcherPriority.Send)
                Return
            End If
            ' Unterwegs und schon lange: der Faden steht. Einmal festhalten, WOBEI - am Ende, wenn der
            ' Ping ankommt, ist der Messpunkt laengst geschlossen.
            Dim wartetMs = (Stopwatch.GetTimestamp() - gesendet) * 1000.0 / Stopwatch.Frequency
            If wartetMs < StockungAbMs OrElse _pingGemeldet Then Return
            _pingGemeldet = True
            Dim abschnitt = _currentSection
            Dim seit = Threading.Volatile.Read(_sectionStartedTicks)
            Dim abschnittMs = If(seit = 0, 0.0, (Stopwatch.GetTimestamp() - seit) * 1000.0 / Stopwatch.Frequency)
            _pingWobei = If(String.IsNullOrEmpty(abschnitt),
                            "kein Messpunkt offen - der Blockierer hat keinen",
                            $"offener Messpunkt: {abschnitt} (seit {abschnittMs:F0} ms)")
        End Sub

        Private Shared Sub Herzschlag()
            _schlagGen2 = GC.CollectionCount(2)
            Threading.Volatile.Write(_schlagPauseTicks, GC.GetTotalPauseDuration().Ticks)
            Threading.Volatile.Write(_letzterSchlagTicks, Stopwatch.GetTimestamp())
        End Sub

        Private Shared Sub StartUiThreadWatchdogLocked()
            Herzschlag()
            _watchdog = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(100)}
            AddHandler _watchdog.Tick, Sub() Herzschlag()
            _watchdog.Start()

            Dim meinLauf = Threading.Interlocked.Increment(_beobachterLauf)
            _beobachter = New Threading.Thread(Sub() BeobachterSchleife(meinLauf)) With {
                .IsBackground = True,
                .Name = "FerrumPix UI-Waechter",
                .Priority = Threading.ThreadPriority.AboveNormal
            }
            _beobachter.Start()
        End Sub

        Private Shared Sub BeobachterSchleife(meinLauf As Integer)
            Dim zuletztGemeldet As Long = 0
            ' Die laufende Stockung: ihr letzter Herzschlag davor, der Stand der Bereinigung dort
            ' und der Messpunkt, der bei ihrer Entdeckung offen war. Ohne das ENDE kennte das
            ' Protokoll nur den Augenblick der Entdeckung (knapp ueber 200 ms) - eine Stockung von
            ' zwei Sekunden saehe dann genauso aus wie eine von zweihundert Millisekunden.
            Dim offenSeit As Long = 0
            Dim offenGen2 = 0
            Dim offenPause As Long = 0
            Dim offenWobei = ""
            While Threading.Volatile.Read(_beobachterLauf) = meinLauf
                Try
                    Threading.Thread.Sleep(50)
                    PingPruefen()
                    Dim letzter = Threading.Volatile.Read(_letzterSchlagTicks)
                    If letzter = 0 Then Continue While

                    ' Ende einer gemeldeten Stockung: der Herzschlag ist weitergegangen.
                    If offenSeit <> 0 AndAlso letzter <> offenSeit Then
                        Dim dauerMs = (letzter - offenSeit) * 1000.0 / Stopwatch.Frequency
                        Dim gen2 = GC.CollectionCount(2) - offenGen2
                        Dim pauseMs = TimeSpan.FromTicks(GC.GetTotalPauseDuration().Ticks - offenPause).TotalMilliseconds
                        Record("Anzeigefaden Stockung ganz", dauerMs)
                        Record("Anzeigefaden Stockung davon Bereinigung", pauseMs)
                        DiagnosticLogService.LogAlways("Messung",
                            $"Anzeigefaden stand {dauerMs:F0} ms - Bereinigung darin: {gen2} Gen-2-Laeufe, Pause {pauseMs:F0} ms - {offenWobei}")
                        offenSeit = 0
                    End If

                    Dim stillMs = (Stopwatch.GetTimestamp() - letzter) * 1000.0 / Stopwatch.Frequency
                    If stillMs < StockungAbMs Then Continue While
                    ' Nur EINMAL je Stockung melden, nicht bei jedem Blick.
                    If letzter = zuletztGemeldet Then Continue While
                    zuletztGemeldet = letzter

                    Dim abschnitt = _currentSection
                    Dim seit = Threading.Volatile.Read(_sectionStartedTicks)
                    Dim abschnittMs = If(seit = 0, 0.0, (Stopwatch.GetTimestamp() - seit) * 1000.0 / Stopwatch.Frequency)
                    Dim wobei = If(String.IsNullOrEmpty(abschnitt),
                                   "kein Messpunkt offen - der Blockierer hat keinen",
                                   $"offener Messpunkt: {abschnitt} (seit {abschnittMs:F0} ms)")
                    Record("Anzeigefaden stockt", stillMs)
                    DiagnosticLogService.LogAlways("Messung", $"Anzeigefaden steht seit {stillMs:F0} ms - {wobei}")
                    offenSeit = letzter
                    offenGen2 = _schlagGen2
                    offenPause = Threading.Volatile.Read(_schlagPauseTicks)
                    offenWobei = wobei
                Catch
                End Try
            End While
        End Sub

        Public Shared Sub StopUiThreadWatchdog()
            SyncLock _sperre
                ' Die Nummer weiterdrehen: der laufende Beobachter sieht beim naechsten Blick, dass
                ' er nicht mehr der aktuelle ist, und beendet sich. Ein sofort danach gestarteter
                ' neuer bekommt seine eigene Nummer und laeuft nicht neben ihm her.
                Threading.Interlocked.Increment(_beobachterLauf)
                _beobachter = Nothing
                _watchdog?.Stop()
                _watchdog = Nothing
            End SyncLock
            Flush()
        End Sub

    End Class

End Namespace
