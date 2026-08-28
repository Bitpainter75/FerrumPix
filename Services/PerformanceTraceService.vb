Imports System
Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports Avalonia.Threading

Namespace Services

    ''' <summary>
    ''' Messpunkte in der laufenden Anwendung, fuer Befunde, die sich nur am ECHTEN Fenster zeigen.
    '''
    ''' <para>Anlass war die Bildlaufleiste in grossen Ordnern (Nutzerbefund 2026-08-27): im
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
            Public Property Anzahl As Integer
            Public Property SummeMs As Double
            Public Property MaxMs As Double
        End Class

        Private Shared ReadOnly _messungen As New ConcurrentDictionary(Of String, Sammlung)()
        Private Shared ReadOnly _sperre As New Object()
        Private Shared _letzteAusgabe As DateTime = DateTime.MinValue

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
                eintrag.Anzahl += 1
                eintrag.SummeMs += milliseconds
                If milliseconds > eintrag.MaxMs Then eintrag.MaxMs = milliseconds
            End SyncLock
            AusgabeWennFaellig()
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
            Dim aufAnzeigefaden = Dispatcher.UIThread.CheckAccess()
            Dim vorheriger = _currentSection
            If aufAnzeigefaden Then
                _currentSection = name
                Threading.Volatile.Write(_sectionStartedTicks, Stopwatch.GetTimestamp())
            End If
            Dim uhr = Stopwatch.StartNew()
            Try
                action()
            Finally
                uhr.Stop()
                If aufAnzeigefaden Then _currentSection = vorheriger
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
            Dim aufAnzeigefaden = Dispatcher.UIThread.CheckAccess()
            Dim vorheriger = _currentSection
            If aufAnzeigefaden Then
                _currentSection = name
                Threading.Volatile.Write(_sectionStartedTicks, Stopwatch.GetTimestamp())
            End If
            Dim uhr = Stopwatch.StartNew()
            Try
                Return func()
            Finally
                uhr.Stop()
                If aufAnzeigefaden Then _currentSection = vorheriger
                Record(name, uhr.Elapsed.TotalMilliseconds)
            End Try
        End Function

        Private Shared Sub AusgabeWennFaellig()
            Dim jetzt = DateTime.UtcNow
            SyncLock _sperre
                If (jetzt - _letzteAusgabe).TotalSeconds < AusgabeIntervallSekunden Then Return
                _letzteAusgabe = jetzt
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
                If Not _messungen.TryRemove(name, eintrag) OrElse eintrag Is Nothing OrElse eintrag.Anzahl = 0 Then Continue For
                teile.Add($"{name}: {eintrag.Anzahl}x, Mittel {eintrag.SummeMs / eintrag.Anzahl:F1} ms, max {eintrag.MaxMs:F1} ms")
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

        Private Shared Sub StartUiThreadWatchdogLocked()
            Threading.Volatile.Write(_letzterSchlagTicks, Stopwatch.GetTimestamp())
            _watchdog = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(100)}
            AddHandler _watchdog.Tick,
                Sub() Threading.Volatile.Write(_letzterSchlagTicks, Stopwatch.GetTimestamp())
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
            While Threading.Volatile.Read(_beobachterLauf) = meinLauf
                Try
                    Threading.Thread.Sleep(50)
                    Dim letzter = Threading.Volatile.Read(_letzterSchlagTicks)
                    If letzter = 0 Then Continue While
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
