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
            Threading.Volatile.Write(_enabled, If(value, 1, 0))
        End Sub

        ''' <summary>Schreibt eine Info-Zeile - nur bei eingeschaltetem EnableDiagnosticLogging.</summary>
        Public Shared Sub LogAlways(area As String, message As String)
            If Not IsVerboseEnabled Then Return
            Try
                Dim entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{area}] {message}" & Environment.NewLine
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
