Imports System
Imports System.IO

Namespace Services

    ''' <summary>
    ''' Die Sperre, die einen schreibenden Hintergrundlauf auf EIN Fenster beschraenkt - auch ueber
    ''' mehrere gestartete Anwendungen hinweg.
    '''
    ''' <para>WARUM NICHT die Anwendung selbst auf eine Instanz begrenzen: mehrere Fenster sind
    ''' brauchbar, zwei Ordner nebeneinander zu sehen ist ein echter Anwendungsfall. Gefaehrlich ist
    ''' nicht das zweite Fenster, sondern der zweite SCHREIBLAUF.</para>
    '''
    ''' <para>WOGEGEN sie schuetzt: die Datenbank waere das kleinere Problem - sie laeuft im
    ''' WAL-Verfahren, mehrere Leser neben einem Schreiber sind genau der vorgesehene Fall, und ein
    ''' belegter Schreibzugriff wird abgewartet. Die beiden JSON-Dateien daneben koennen das nicht:
    ''' der Ordnerverzeichnis des Vorschau-Zwischenspeichers (index.json) und die Einstellungen
    ''' werden vollstaendig gelesen, geaendert und wieder geschrieben. Das ist gegen einen ABBRUCH
    ''' gesichert (erst daneben schreiben, dann ersetzen), nicht gegen einen zweiten PROZESS: dort
    ''' gewinnt schlicht der letzte, und die Ordnerzuordnungen des anderen sind weg. Zwei
    ''' gleichzeitige Indexlaeufe tun genau das im Sekundentakt.</para>
    '''
    ''' <para>WIE: eine Datei neben der Bibliothek, offen gehalten mit
    ''' <see cref="FileShare.None"/>. Solange ein Prozess sie haelt, scheitert jeder andere beim
    ''' Oeffnen. Bewusst KEIN Zeitstempel und keine Prozessnummer in der Datei: beides muesste beim
    ''' Absturz von jemandem aufgeraeumt werden, und eine Sperre, die man aufraeumen muss, ist beim
    ''' naechsten Absturz eine Sperre, die faelschlich liegt. Ein Dateihandle gibt das Betriebssystem
    ''' von sich aus frei, wenn der Prozess endet - auch wenn er abstuerzt.</para>
    ''' </summary>
    Public NotInheritable Class BackgroundRunLock
        Implements IDisposable

        Private _stream As FileStream
        ''' <summary>Eine ZAEHLHUELLE aus <see cref="TryEnterCleanup"/>: sie haelt keine Datei,
        ''' sondern nur einen Platz in der Schachtelung.</summary>
        Private ReadOnly _nested As Boolean

        Private Sub New(stream As FileStream)
            _stream = stream
        End Sub

        Private Sub New(nested As Boolean)
            _nested = nested
        End Sub

        ' ── Die LOESCHWEGE teilen sich eine Klammer ─────────────────────────────
        '
        ' Ein Loeschweg braucht dieselbe Sperre wie ein Lauf: was hier geloescht wird, legt ein Lauf
        ' im anderen Fenster sonst gleich wieder an. Er darf sie aber SCHACHTELN - "Ordner
        ' aufraeumen" holt sie einmal und ruft darin drei Wege, die jeder fuer sich abgesichert sind.
        ' Ein zweiter echter Erwerb im selben Prozess wuerde an der eigenen Datei scheitern
        ' (FileShare.None gilt auch fuer den, der sie haelt), und der Weg saehe aus wie von einem
        ' fremden Fenster blockiert.
        '
        ' Die LAEUFE nehmen weiterhin TryAcquire und schachteln NICHT: Katalogindex und
        ' Gesichtssuche schreiben in dieselben Dateien, zwei davon nebeneinander sind genau der Fall,
        ' den die Sperre verhindern soll - auch im selben Fenster.

        Private Shared ReadOnly _cleanupGate As New Object()
        Private Shared _cleanupDepth As Integer
        Private Shared _cleanupHolder As BackgroundRunLock

        ''' <summary>Betritt die Klammer fuer einen LOESCHENDEN Weg. Nothing heisst: es laeuft
        ''' gerade ein Hintergrundlauf - hier oder in einem anderen Fenster.</summary>
        Public Shared Function TryEnterCleanup() As BackgroundRunLock
            SyncLock _cleanupGate
                If _cleanupDepth > 0 Then
                    _cleanupDepth += 1
                    Return New BackgroundRunLock(nested:=True)
                End If
                Dim erworben = TryAcquire()
                If erworben Is Nothing Then Return Nothing
                _cleanupHolder = erworben
                _cleanupDepth = 1
                Return New BackgroundRunLock(nested:=True)
            End SyncLock
        End Function

        ''' <summary>Wo die Sperrdatei liegt: neben der Bibliothek, damit sie denselben Bestand
        ''' meint. Zwei Anwendungen mit verschiedenen Datenordnern sperren sich damit auch nicht
        ''' gegenseitig - sie arbeiten ja auf verschiedenen Katalogen.</summary>
        Private Shared ReadOnly Property LockPath As String
            Get
                Dim dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix")
                Return Path.Combine(dir, "background-run.lock")
            End Get
        End Property

        ''' <summary>Versucht die Sperre zu holen. Nothing heisst: ein anderes Fenster hat sie.
        '''
        ''' EINE Sperre fuer alle schreibenden Laeufe und nicht eine je Lauf: Katalogindex und
        ''' Gesichtssuche schreiben in DIESELBEN Dateien. Zwei verschiedene Laeufe in zwei Fenstern
        ''' haetten dasselbe Problem wie zweimal derselbe.</summary>
        Public Shared Function TryAcquire() As BackgroundRunLock
            Try
                ' NICHT "path" als Name: VB unterscheidet keine Gross- und Kleinschreibung, die
                ' lokale Variable verdeckte damit die Klasse Path, und Path.GetDirectoryName
                ' waere ein Aufruf auf einer Zeichenkette.
                Dim lockFile = LockPath
                Directory.CreateDirectory(Path.GetDirectoryName(lockFile))
                ' FileShare.None ist der eigentliche Riegel. Unter Linux und macOS setzt .NET das
                ' ueber eine Dateisperre des Systems um, unter Windows ueber den Freigabemodus -
                ' beides wirkt zwischen Prozessen.
                Return New BackgroundRunLock(New FileStream(lockFile, FileMode.OpenOrCreate,
                                                            FileAccess.ReadWrite, FileShare.None))
            Catch ex As IOException
                ' Belegt. Das ist kein Fehler, sondern die Antwort.
                Return Nothing
            Catch ex As UnauthorizedAccessException
                Return Nothing
            Catch ex As Exception
                ' Laesst sich die Sperre aus einem anderen Grund nicht anlegen - etwa ein
                ' schreibgeschuetzter Datenordner -, soll der Lauf trotzdem laufen. Sie ist ein
                ' Schutz gegen einen seltenen Fall und kein Grund, die Funktion abzuschalten.
                DiagnosticLogService.LogException("Hintergrundlauf.Sperre", ex)
                ' Der Typ MUSS dastehen: es gibt einen zweiten Konstruktor fuer die Zaehlhuelle, und
                ' ein blankes Nothing passte auf beide.
                Return New BackgroundRunLock(CType(Nothing, FileStream))
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _nested Then
                SyncLock _cleanupGate
                    _cleanupDepth -= 1
                    If _cleanupDepth > 0 Then Return
                    ' Die aeusserste Klammer gibt die Datei frei.
                    _cleanupDepth = 0
                    Dim halter = _cleanupHolder
                    _cleanupHolder = Nothing
                    halter?.DisposeStream()
                End SyncLock
                Return
            End If
            DisposeStream()
        End Sub

        Private Sub DisposeStream()
            Try
                _stream?.Dispose()
            Catch
            End Try
            _stream = Nothing
        End Sub

    End Class

End Namespace
