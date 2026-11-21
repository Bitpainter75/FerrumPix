Imports System
Imports System.IO

Namespace Services

    ''' <summary>
    ''' Fehler- und Diagnoseprotokoll unter ~/.local/share/FerrumPix/logs.
    '''
    ''' ZWEI Stufen mit unterschiedlicher Regel:
    ''' - <see cref="LogAlways"/> (Ablaufspuren, HTTP-Antworten, Renderzeiten) schreibt NUR bei
    '''   eingeschaltetem EnableDiagnosticLogging - im Normalbetrieb soll keine Datei anwachsen.
    ''' - <see cref="LogException"/> schreibt IMMER. Eine unerwartete Ausnahme ist kein Ablauf,
    '''   sondern ein Fehler: seitdem die Async-Einstiegspunkte sie abfangen, stürzt die App
    '''   nicht mehr ab - ohne diese Zeile wäre der Fehler dafür spurlos verschwunden. Die Datei ist
    '''   auf <see cref="MaxErrorLogBytes"/> gedeckelt und wird bei Überschreitung EINMAL rotiert,
    '''   damit daraus nichts Wachsendes wird.
    ''' </summary>
    Public NotInheritable Class DiagnosticLogService
        Private Sub New()
        End Sub

        Private Shared ReadOnly LogDirectory As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix", "logs")

        Private Shared ReadOnly LogPath As String = Path.Combine(LogDirectory, "diagnostics.log")

        ''' <summary>Fehlerdatei für den Normalbetrieb (getrennt von der ausführlichen
        ''' diagnostics.log, damit ein eingeschaltetes Diagnose-Log sie nicht überschwemmt).</summary>
        Private Shared ReadOnly ErrorLogPath As String = Path.Combine(LogDirectory, "errors.log")

        Private Const MaxErrorLogBytes As Long = 1024L * 1024L

        Private Shared ReadOnly _writeLock As New Object()

        ''' <summary>Ob ausführlich protokolliert wird - EINMAL gelesen und danach gemerkt.
        '''
        ''' <para><see cref="AppSettingsService.Load"/> merkt sich zwar den Text der
        ''' Einstellungsdatei, deserialisiert ihn aber bei JEDEM Aufruf neu und normalisiert dabei
        ''' rund fünfzig Felder. Als erste Zeile jeder Protokollzeile stand das an einer Stelle, die
        ''' im Zweifel hundertfach je Bedienschritt durchlaufen wird - und zwar auch dann, wenn gar
        ''' nichts geschrieben wird. Wer den Schalter im Dialog umlegt, meldet es über
        ''' <see cref="RefreshEnabled"/>.</para></summary>
        Private Shared _enabled As Integer = -1   ' -1 = noch nicht gelesen, 0 = aus, 1 = an

        ''' <summary>Der Startparameter hat das Protokoll erzwungen. Dann darf die EINSTELLUNG es
        ''' nicht wieder ausschalten: sie wird beim ersten Laden der Einstellungsdatei angewandt, und
        ''' das passiert kurz nach dem Start. Wer mit dem Parameter startet, will das Protokoll fuer
        ''' DIESEN Lauf, unabhaengig davon, was in der Datei steht - und genau der Fall, den er
        ''' aufklaeren soll, ist der, in dem noch niemand etwas einstellen konnte.</summary>
        Private Shared _forcedOn As Boolean

        Public Shared ReadOnly Property IsVerboseEnabled As Boolean
            Get
                Dim state = Threading.Volatile.Read(_enabled)
                If state >= 0 Then Return state = 1
                Dim value = False
                Try
                    value = AppSettingsService.Load().EnableDiagnosticLogging
                Catch
                End Try
                Threading.Volatile.Write(_enabled, If(value, 1, 0))
                Return value
            End Get
        End Property

        ''' <summary>Nach dem Umlegen des Schalters in den Einstellungen aufrufen.</summary>
        Public Shared Sub RefreshEnabled(value As Boolean)
            If _forcedOn AndAlso Not value Then Return
            Threading.Volatile.Write(_enabled, If(value, 1, 0))
        End Sub

        ''' <summary>Das Protokoll fuer diesen Lauf einschalten und eingeschaltet lassen.</summary>
        Public Shared Sub ForceEnable()
            _forcedOn = True
            Threading.Volatile.Write(_enabled, 1)
        End Sub

        ''' <summary>Laeuft dieser Start mit dem Protokollschalter? Nur dafuer gedacht, dass der
        ''' Start selbst etwas SIEHT - siehe <see cref="EchoToConsole"/>.</summary>
        Public Shared ReadOnly Property IsForcedOn As Boolean
            Get
                Return _forcedOn
            End Get
        End Property

        ''' <summary>Jede Protokollzeile zusaetzlich auf die Standardausgabe, aber NUR beim
        ''' erzwungenen Protokoll.
        '''
        ''' WOZU: Eine Anwendung ohne Fenster und ohne Konsolenausgabe ist von aussen nicht von
        ''' einer zu unterscheiden, die gar nicht erst angelaufen ist. Genau daran ist eine
        ''' Fehlersuche unter Windows haengengeblieben: der Aufruf kehrte sofort zurueck, es stand
        ''' nichts im Fenster, es entstand keine Datei - und niemand konnte sagen, ob das Programm
        ''' nie startete oder nur nichts zu sagen hatte. Wer mit dem Schalter startet, sieht jetzt
        ''' beim Mitlesen, wie weit es kommt.
        '''
        ''' NICHT im Normalbetrieb: ohne Schalter kostet die Spiegelung genau eine Abfrage, und die
        ''' Ausgabe einer Fensteranwendung landet sonst im Nichts oder in einer fremden Konsole.</summary>
        Private Shared Sub EchoToConsole(entry As String)
            If Not _forcedOn Then Return
            Try
                Console.Out.Write(entry)
            Catch
                ' Keine Konsole angebunden (Doppelklick unter Windows, Dienst, Pipe zu). Das
                ' Protokoll steht davon unberuehrt in der Datei.
            End Try
        End Sub

        ''' <summary>Der Ordner, in dem die beiden Protokolldateien liegen - fuer die Meldung an den
        ''' Nutzer, der sie heraussuchen soll.
        '''
        ''' NICHT "Directory" nennen: das verdeckt in dieser Klasse System.IO.Directory, das sie beim
        ''' Anlegen des Ordners selbst benutzt.</summary>
        Public Shared ReadOnly Property LogFolder As String
            Get
                Return LogDirectory
            End Get
        End Property

        ''' <summary>Die ausfuehrliche Protokolldatei, fuer die Anzeige in den Einstellungen. Der
        ''' Pfad haengt am System (unter Windows %LocalAppData%, unter macOS
        ''' ~/Library/Application Support, unter Linux ~/.local/share); ein fester Text dafuer
        ''' fuehrte auf dem Mac in den falschen Ordner (Nutzermeldung).</summary>
        Public Shared ReadOnly Property LogFilePath As String
            Get
                Return LogPath
            End Get
        End Property

        ''' <summary>Das Ende der ausfuehrlichen Protokolldatei als Text, hoechstens
        ''' <paramref name="maxBytes"/>. Beginnt der Ausschnitt mitten in einer Zeile, faellt der
        ''' Rest dieser Zeile weg. Leer, wenn es die Datei nicht gibt. Unter derselben Sperre wie das
        ''' Schreiben, damit keine halbe Zeile mitkommt.</summary>
        Public Shared Function ReadTail(maxBytes As Integer) As String
            Return ReadTailOf(LogPath, maxBytes)
        End Function

        ''' <summary>Die Fehlerdatei des Normalbetriebs; sie wird auch ohne Diagnoseprotokoll
        ''' geschrieben.</summary>
        Public Shared ReadOnly Property ErrorLogFilePath As String
            Get
                Return ErrorLogPath
            End Get
        End Property

        ''' <summary>Wie <see cref="ReadTail"/>, fuer die Fehlerdatei.</summary>
        Public Shared Function ReadErrorTail(maxBytes As Integer) As String
            Return ReadTailOf(ErrorLogPath, maxBytes)
        End Function

        Private Shared Function ReadTailOf(path As String, maxBytes As Integer) As String
            SyncLock _writeLock
                Try
                    If Not File.Exists(path) Then Return ""
                    Using stream As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
                        Dim start = Math.Max(0L, stream.Length - Math.Max(1, maxBytes))
                        stream.Position = start
                        Dim buffer(CInt(stream.Length - start) - 1) As Byte
                        Dim read = 0
                        While read < buffer.Length
                            Dim n = stream.Read(buffer, read, buffer.Length - read)
                            If n <= 0 Then Exit While
                            read += n
                        End While
                        Dim offset = 0
                        If start > 0 Then
                            Dim newline = Array.IndexOf(buffer, CByte(10), 0, read)
                            If newline >= 0 Then offset = newline + 1
                        End If
                        Return System.Text.Encoding.UTF8.GetString(buffer, offset, read - offset)
                    End Using
                Catch ex As Exception
                    Return ex.Message
                End Try
            End SyncLock
        End Function

        ''' <summary>Schreibt eine Info-Zeile - nur bei eingeschaltetem EnableDiagnosticLogging.</summary>
        Public Shared Sub LogAlways(area As String, message As String)
            If Not IsVerboseEnabled Then Return
            Dim entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{area}] {message}" & Environment.NewLine
            ' ERST die Konsole, dann die Datei: scheitert das Schreiben (kein Schreibrecht im
            ' Benutzerordner, volle Platte), ist die Zeile trotzdem zu sehen - und gerade dieser
            ' Fall ist einer, bei dem hinterher niemand eine Datei findet.
            EchoToConsole(entry)
            Try
                SyncLock _writeLock
                    Directory.CreateDirectory(LogDirectory)
                    File.AppendAllText(LogPath, entry)
                End SyncLock
            Catch
            End Try
        End Sub

        ''' <summary>Hält eine Ausnahme fest - unabhängig vom Diagnose-Schalter.</summary>
        Public Shared Sub LogException(area As String, ex As Exception)
            If ex Is Nothing Then Return
            Dim entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{area}] {ex}" & Environment.NewLine &
                        New String("-"c, 80) & Environment.NewLine
            Dim verbose = IsVerboseEnabled
            EchoToConsole(entry)
            Try
                SyncLock _writeLock
                    Directory.CreateDirectory(LogDirectory)
                    ' Bei eingeschaltetem Diagnose-Log zusätzlich in die Ablaufspur, damit die
                    ' Ausnahme dort an ihrer zeitlichen Stelle steht.
                    If verbose Then File.AppendAllText(LogPath, entry)
                    RotateIfTooLarge()
                    File.AppendAllText(ErrorLogPath, entry)
                End SyncLock
            Catch
            End Try
        End Sub

        ''' <summary>Groesser soll keine der Protokolldateien auf Dauer werden.</summary>
        Friend Const MaxLogBytes As Long = 10L * 1024L * 1024L

        ''' <summary>So viel vom Ende bleibt nach dem Kuerzen stehen. Weniger als die Grenze, damit
        ''' nicht schon die naechste Zeile sie wieder ueberschreitet.</summary>
        Friend Const KeptLogBytes As Long = 8L * 1024L * 1024L

        Private Shared _trimStarted As Integer

        ''' <summary>Kuerzt die Protokolldateien einmal je Lauf im Hintergrund auf ihr Ende, wenn sie
        ''' ueber <see cref="MaxLogBytes"/> liegen: das ausfuehrliche Protokoll und die Fehlerdatei
        ''' samt ihrer Vorgaengerfassung.
        '''
        ''' WOZU. Das ausfuehrliche Protokoll hatte keine Grenze und wuchs bei eingeschaltetem
        ''' Schalter ueber Wochen (gesehen: 27 MB). Geprueft wird beim Start und nicht bei jeder
        ''' Zeile: waehrend eines Laufs darf die Datei ueber die Grenze wachsen, der naechste Start
        ''' raeumt auf. Die Fehlerdatei ist zusaetzlich schon beim Schreiben gedeckelt
        ''' (<see cref="RotateIfTooLarge"/>); hier faellt sie nur auf, wenn sie von frueher gross ist.</summary>
        Public Shared Sub TrimLogsSoon()
            If Threading.Interlocked.Exchange(_trimStarted, 1) <> 0 Then Return
            Threading.Tasks.Task.Run(
                Sub()
                    For Each logFile In {LogPath, ErrorLogPath, ErrorLogPath & ".1"}
                        Try
                            SyncLock _writeLock
                                TrimFileToTail(logFile, MaxLogBytes, KeptLogBytes)
                            End SyncLock
                        Catch
                            ' Ein Protokoll, das sich nicht kuerzen laesst, darf den Start nicht stoeren.
                        End Try
                    Next
                End Sub)
        End Sub

        ''' <summary>Laesst von einer Textdatei ueber <paramref name="maxBytes"/> nur die letzten
        ''' <paramref name="keepBytes"/> stehen, beginnend an einem Zeilenanfang. True, wenn gekuerzt
        ''' wurde. Geschrieben wird in eine Nachbardatei, die die alte ersetzt: bricht der Vorgang
        ''' ab, steht die alte Datei unversehrt da.</summary>
        Friend Shared Function TrimFileToTail(filePath As String, maxBytes As Long, keepBytes As Long) As Boolean
            Dim info = New FileInfo(filePath)
            If Not info.Exists OrElse info.Length <= maxBytes Then Return False
            keepBytes = Math.Max(0L, Math.Min(keepBytes, maxBytes))
            Dim partPath = filePath & ".kuerzen"
            Using source = New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                source.Position = source.Length - keepBytes
                ' Bis hinter den naechsten Zeilenumbruch: die erste Zeile soll ganz sein.
                Dim skipped = 0
                While skipped < 65536
                    Dim value = source.ReadByte()
                    If value < 0 Then Exit While
                    skipped += 1
                    If value = 10 Then Exit While
                End While
                Using target = New FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None)
                    source.CopyTo(target)
                End Using
            End Using
            File.Move(partPath, filePath, True)
            Return True
        End Function

        ''' <summary>Leert beide Protokolle und loescht die Vorgaengerfassung der Fehlerdatei, auf
        ''' Wunsch des Nutzers, etwa bevor er einen Fehler fuer ein Protokoll nachstellt. Geleert,
        ''' nicht geloescht: so bleibt die Datei da, wo der Nutzer sie gerade sucht. Unter der
        ''' Schreibsperre, damit keine halbe Zeile stehen bleibt.</summary>
        ''' <returns>Leer, oder der Grund, warum es nicht ging.</returns>
        Public Shared Function Clear() As String
            SyncLock _writeLock
                Try
                    For Each path In {LogPath, ErrorLogPath}
                        If File.Exists(path) Then File.WriteAllText(path, "")
                    Next
                    If File.Exists(ErrorLogPath & ".1") Then File.Delete(ErrorLogPath & ".1")
                    Return ""
                Catch ex As Exception
                    Return ex.Message
                End Try
            End SyncLock
        End Function

        ''' <summary>Deckelt die Fehlerdatei: ist sie voll, wird sie EINMAL zur .1 und neu begonnen.
        ''' Kein Ringpuffer mit vielen Ständen - eine Vorgängerfassung reicht, um einen Fehler zu
        ''' verfolgen, und mehr als 2 MB soll das Protokoll nie belegen.</summary>
        Private Shared Sub RotateIfTooLarge()
            Try
                Dim info = New FileInfo(ErrorLogPath)
                If Not info.Exists OrElse info.Length < MaxErrorLogBytes Then Return
                Dim alt = ErrorLogPath & ".1"
                If File.Exists(alt) Then File.Delete(alt)
                File.Move(ErrorLogPath, alt)
            Catch
            End Try
        End Sub
    End Class

End Namespace
