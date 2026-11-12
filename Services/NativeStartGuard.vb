Imports System
Imports System.Diagnostics
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq

Namespace Services

    ''' <summary>Der Startschutz fuer native Bibliotheken, die den Prozess mitreissen koennen.
    '''
    ''' DER ANLASS: unter Windows 10 22H2 stuerzte ONNX Runtime schon beim Anlegen seiner Umgebung
    ''' (CreateDefaultEnv) mit einer Zugriffsverletzung ab. Die laesst sich in .NET
    ''' nicht fangen, der Prozess endet sofort, und weil die Laufzeit beim Aufbau des Hauptfensters
    ''' geprueft wird, startete die Anwendung bei jedem Versuch gar nicht erst. Ohne Modelle, ohne
    ''' Grafikkarte, ohne jede Moeglichkeit, etwas abzuschalten.
    '''
    ''' DER WEG: vor dem heiklen Aufruf eine Merkdatei anlegen, danach loeschen. Bleibt sie liegen,
    ''' ist der Prozess genau dort gestorben, und beim naechsten Start bleibt der Teil aus. Die
    ''' Anwendung kommt so spaetestens beim zweiten Start hoch, nur ohne die betroffene Funktion.
    '''
    ''' WIEDER VERSUCHT wird mit einer anderen Programmfassung: die Merkdatei traegt die Fassung
    ''' und die Aenderungszeit der Programmdatei, jede Aktualisierung oder Neuinstallation gibt dem
    ''' Teil also eine neue Chance. JEDER PROZESS HAT SEINE EIGENE MERKDATEI und loescht nur sie;
    ''' die eines gleichzeitig laufenden zweiten FerrumPix ist kein Absturz und sperrt nichts.
    '''
    ''' GELADEN WIRD ERST BEI BEDARF (AiModelService.RuntimeAvailable): beim Start fasst FerrumPix
    ''' keinen der beiden Teile an. Der Schutz greift deshalb beim ersten echten Gebrauch, und eine
    ''' gesperrte KI-Funktion meldet sich beim naechsten Versuch, statt erneut abzustuerzen.</summary>
    Public NotInheritable Class NativeStartGuard
        Private Sub New()
        End Sub

        ''' <summary>Die Laufzeit fuer gelernte Modelle: Umgebung und erste Sitzungsoption.</summary>
        Public Const ModelRuntime As String = "modelllaufzeit"
        ''' <summary>Die Grafikbeschleunigung: Bibliothek anmelden und Karten abfragen.</summary>
        Public Const GpuDevices As String = "grafikkarten"

        ''' <summary>Fuer den Pruefstand: ein anderer Ordner und eine feste Fassung.</summary>
        Friend Shared DirectoryOverride As String = Nothing
        Friend Shared StampOverride As String = Nothing

        Private Shared ReadOnly _blocked As New HashSet(Of String)(StringComparer.Ordinal)

        Private Shared ReadOnly Property GuardDirectory As String
            Get
                If Not String.IsNullOrEmpty(DirectoryOverride) Then Return DirectoryOverride
                Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "FerrumPix", "startschutz")
            End Get
        End Property

        ''' <summary>Die Marke DIESES Prozesses: <c>teil.pid.marke</c>. Je Prozess eine eigene Datei,
        ''' denn zwei gleichzeitig startende FerrumPix teilten sich sonst eine: der zuerst fertige
        ''' loeschte sie, und stuerzte der andere danach ab, blieb keine Spur.</summary>
        Private Shared Function OwnMarkerPath(name As String) As String
            Return Path.Combine(GuardDirectory,
                                name & "." & Environment.ProcessId.ToString(Globalization.CultureInfo.InvariantCulture) & ".marke")
        End Function

        ''' <summary>Fassung und Aenderungszeit der Programmdatei. Aendert sich eines davon, wird
        ''' ein gesperrter Teil wieder versucht.</summary>
        Private Shared Function CurrentStamp() As String
            If Not String.IsNullOrEmpty(StampOverride) Then Return StampOverride
            Dim version = ""
            Try
                version = If(Reflection.Assembly.GetExecutingAssembly().
                             GetCustomAttributes(GetType(Reflection.AssemblyInformationalVersionAttribute), False).
                             OfType(Of Reflection.AssemblyInformationalVersionAttribute)().FirstOrDefault()?.InformationalVersion, "")
            Catch
            End Try
            Dim written = ""
            Try
                Dim exe = Environment.ProcessPath
                If Not String.IsNullOrEmpty(exe) AndAlso File.Exists(exe) Then
                    written = File.GetLastWriteTimeUtc(exe).Ticks.ToString(Globalization.CultureInfo.InvariantCulture)
                End If
            Catch
            End Try
            Return version & "|" & written
        End Function

        ''' <summary>Vor dem heiklen Aufruf. Falsch heisst: beim letzten Start ist der Prozess genau
        ''' hier gestorben, der Teil bleibt aus. Wahr heisst: weitermachen und danach
        ''' <see cref="Leave"/> rufen. Kann die Merkdatei nicht geschrieben werden, wird trotzdem
        ''' weitergemacht; der Schutz fehlt dann, die Funktion nicht.</summary>
        Public Shared Function TryEnter(name As String) As Boolean
            If HasCrashMarker(name) Then Return False
            Try
                Directory.CreateDirectory(GuardDirectory)
                File.WriteAllLines(OwnMarkerPath(name),
                                   {CurrentStamp(), Environment.ProcessId.ToString(Globalization.CultureInfo.InvariantCulture)})
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Startschutz", $"{name}: Merkdatei nicht nutzbar - {ex.Message}")
            End Try
            Return True
        End Function

        ''' <summary>Nur nachsehen, ohne etwas anzulegen: liegt fuer diesen Teil die Marke eines
        ''' Absturzes dieser Programmfassung? Dann gilt er ab sofort als gesperrt. Fuer Fragen wie
        ''' "Knopf zeigen?", die die Bibliothek nicht laden duerfen.
        '''
        ''' Durchgesehen werden ALLE Marken des Teils, je eine je Prozess. Ein Absturz ist eine Marke
        ''' dieser Programmfassung, deren Prozess nicht mehr laeuft. Nicht dazu zaehlen die eigene
        ''' (dieser Prozess steckt gerade selbst im Aufruf, ein anderer Faden fragt) und die eines
        ''' noch laufenden zweiten FerrumPix. Marken einer anderen Fassung sind erledigt und werden
        ''' weggeraeumt.</summary>
        Public Shared Function HasCrashMarker(name As String) As Boolean
            If IsBlocked(name) Then Return True
            Dim crashed = False
            Try
                If Not Directory.Exists(GuardDirectory) Then Return False
                Dim stamp = CurrentStamp()
                For Each marker In Directory.GetFiles(GuardDirectory, name & ".*.marke")
                    Dim lines = File.ReadAllLines(marker)
                    Dim markedStamp = If(lines.Length > 0, lines(0), "")
                    Dim markedPid = -1
                    If lines.Length > 1 Then Integer.TryParse(lines(1), markedPid)
                    If markedPid = Environment.ProcessId OrElse IsOtherRunningInstance(markedPid) Then Continue For
                    If markedStamp <> stamp Then
                        Try
                            File.Delete(marker)
                        Catch
                        End Try
                        Continue For
                    End If
                    crashed = True
                Next
            Catch
                Return False
            End Try
            If Not crashed Then Return False
            SyncLock _blocked
                _blocked.Add(name)
            End SyncLock
            DiagnosticLogService.LogAlways("Startschutz",
                $"{name}: beim letzten Start abgestuerzt, bleibt bis zur naechsten Programmfassung aus")
            Return True
        End Function

        ''' <summary>Nach dem heiklen Aufruf, ob er gelang oder mit einer Ausnahme scheiterte: der
        ''' Prozess lebt, also war es kein Absturz. Geloescht wird nur die EIGENE Marke.</summary>
        Public Shared Sub Leave(name As String)
            Try
                File.Delete(OwnMarkerPath(name))
            Catch
            End Try
        End Sub

        ''' <summary>Ist dieser Teil in diesem Programmlauf wegen eines frueheren Absturzes aus?</summary>
        Public Shared Function IsBlocked(name As String) As Boolean
            SyncLock _blocked
                Return _blocked.Contains(name)
            End SyncLock
        End Function

        ''' <summary>Gehoert die Merkdatei einem anderen, noch laufenden FerrumPix? Dann steckt der
        ''' gerade selbst in dem Aufruf, und das ist kein Absturz.</summary>
        Private Shared Function IsOtherRunningInstance(pid As Integer) As Boolean
            If pid <= 0 OrElse pid = Environment.ProcessId Then Return False
            Try
                Using other = Process.GetProcessById(pid)
                    Using own = Process.GetCurrentProcess()
                        Return Not other.HasExited AndAlso
                               String.Equals(other.ProcessName, own.ProcessName, StringComparison.OrdinalIgnoreCase)
                    End Using
                End Using
            Catch
                Return False
            End Try
        End Function
    End Class
End Namespace
