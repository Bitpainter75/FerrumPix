Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>
    ''' G'MIC als optionales Programm. FerrumPix liefert nichts davon mit: der Nutzer installiert es
    ''' selbst, und der Eintrag im Editor erscheint nur, wenn gmic_qt gefunden wurde - dieselbe
    ''' Regel wie bei dnglab.
    '''
    ''' Warum gmic_qt und nicht das Photoshop-Plugin gmic-8bf: das Plugin ist eine Windows-DLL, die
    ''' ihrerseits nur dieses Qt-Programm startet. Es einzubinden hiesse, die Filterschnittstelle von
    ''' Photoshop nachzubauen und unter Linux Wine dazwischenzuschalten - fuer dasselbe Fenster.
    '''
    ''' GESUCHT WIRD NACH DER DATEI, das Programm wird dafuer nicht gestartet. gmic_qt hat keinen
    ''' Versionsschalter, und "--help" eines Oberflaechenprogramms ist kein verlaesslicher Test.
    '''
    ''' DER RUNDWEG: gmic_qt bekommt eine PNG-Datei und mit "-o" den Pfad fuer das Ergebnis. PNG,
    ''' weil Qt es ohne Zusatzmodul liest und schreibt; TIFF braucht dort ein Modul, das nicht auf
    ''' jedem System installiert ist. Solange die Pipeline mit 8 Bit rechnet, verliert PNG nichts.
    ''' </summary>
    Public NotInheritable Class GmicService

        Private Sub New()
        End Sub

        Private Shared ReadOnly _sync As New Object()
        Private Shared _cacheValid As Boolean
        Private Shared _cachedFor As String = ""
        Private Shared _cachedPath As String

        ''' <summary>Wurde gmic_qt gefunden? Billig nach dem ersten Aufruf: das Ergebnis wird
        ''' gemerkt, bis sich der eingetragene Pfad aendert oder <see cref="Refresh"/> laeuft.</summary>
        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                Return Not String.IsNullOrEmpty(FindGmicQt())
            End Get
        End Property

        ''' <summary>Der Pfad zu gmic_qt, sonst Nothing.</summary>
        Public Shared Function FindGmicQt() As String
            Dim configured = If(AppSettingsService.Load()?.GmicQtPath, "").Trim()
            SyncLock _sync
                If _cacheValid AndAlso String.Equals(_cachedFor, configured, StringComparison.Ordinal) Then Return _cachedPath
            End SyncLock
            Dim found = Locate(configured)
            SyncLock _sync
                _cachedPath = found
                _cachedFor = configured
                _cacheValid = True
            End SyncLock
            Return found
        End Function

        ''' <summary>Vergisst das gemerkte Ergebnis - nach einer Installation soll der Eintrag ohne
        ''' Neustart erscheinen, sobald die Einstellungen wieder geoeffnet werden.</summary>
        Public Shared Sub Refresh()
            SyncLock _sync
                _cacheValid = False
            End SyncLock
        End Sub

        ''' <summary>Ein eingetragener Pfad gilt allein: steht dort nichts Brauchbares, wird NICHT
        ''' still im Suchpfad weitergesucht - sonst liefe ein anderes Programm als das eingetragene.</summary>
        Friend Shared Function Locate(configured As String) As String
            If Not String.IsNullOrWhiteSpace(configured) Then
                Dim resolved = ResolveMacBundle(configured.Trim())
                Return If(IsExecutableFile(resolved), resolved, Nothing)
            End If
            Dim name = If(OperatingSystem.IsWindows(), "gmic_qt.exe", "gmic_qt")
            For Each directory In SearchDirectories()
                Dim candidate As String
                Try
                    candidate = Path.Combine(directory, name)
                Catch
                    Continue For
                End Try
                If IsExecutableFile(candidate) Then Return candidate
            Next
            Return Nothing
        End Function

        ''' <summary>Der Suchpfad, dazu die ueblichen Orte von Homebrew und /usr/local. Ein aus dem
        ''' Dock gestartetes Programm bekommt auf dem Mac einen kurzen Suchpfad, in dem beide
        ''' fehlen.</summary>
        Private Shared Function SearchDirectories() As List(Of String)
            Dim result As New List(Of String)()
            Dim pathVariable = If(Environment.GetEnvironmentVariable("PATH"), "")
            For Each entry In pathVariable.Split(IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                If Not result.Contains(entry) Then result.Add(entry)
            Next
            If Not OperatingSystem.IsWindows() Then
                For Each extra In {"/usr/local/bin", "/opt/homebrew/bin", "/usr/bin"}
                    If Not result.Contains(extra) Then result.Add(extra)
                Next
            End If
            Return result
        End Function

        Private Shared Function ResolveMacBundle(configured As String) As String
            If OperatingSystem.IsMacOS() AndAlso configured.TrimEnd("/"c).EndsWith(".app", StringComparison.OrdinalIgnoreCase) AndAlso
               Directory.Exists(configured) Then
                Return Path.Combine(configured, "Contents", "MacOS", "gmic_qt")
            End If
            Return configured
        End Function

        Private Shared Function IsExecutableFile(candidate As String) As Boolean
            Try
                If String.IsNullOrWhiteSpace(candidate) OrElse Not File.Exists(candidate) Then Return False
                If OperatingSystem.IsWindows() Then Return True
                Dim mode = File.GetUnixFileMode(candidate)
                Return (mode And (UnixFileMode.UserExecute Or UnixFileMode.GroupExecute Or UnixFileMode.OtherExecute)) <> 0
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Die Werte fuer gmic_qt. OPTIONEN VOR DEN DATEIEN: gmic_qt liest alles nach der
        ''' ersten Datei als weitere Eingabedatei. Mit "eingabe -o ausgabe" hielt es "-o" fuer ein
        ''' Bild, meldete "File cannot be read: -o" und beendete sich sofort, ohne Fenster
        ''' (Nutzerbefund).</summary>
        Friend Shared Function BuildArguments(inputPath As String, outputPath As String) As List(Of String)
            Return New List(Of String) From {"-o", outputPath, inputPath}
        End Function

        ''' <summary>Startet gmic_qt mit dem Eingangsbild und wartet, bis das Fenster geschlossen ist.
        ''' Ob ein Ergebnis entstand, sagt allein die Ausgabedatei: der Rueckgabewert des Programms
        ''' ist dafuer nicht belegt. False nur, wenn gar nicht gestartet werden konnte.</summary>
        Public Shared Async Function RunAsync(inputPath As String, outputPath As String) As Task(Of Boolean)
            Dim program = FindGmicQt()
            If String.IsNullOrEmpty(program) Then Return False
            Try
                Dim info As New ProcessStartInfo(program) With {.UseShellExecute = False, .RedirectStandardError = True}
                For Each value In BuildArguments(inputPath, outputPath)
                    info.ArgumentList.Add(value)
                Next
                ShellOpenService.PassActivationToken(info)
                Using process = System.Diagnostics.Process.Start(info)
                    If process Is Nothing Then Return False
                    ' Nebenlaeufig lesen: ein voller Puffer hielte das Programm sonst an.
                    Dim errorText = process.StandardError.ReadToEndAsync()
                    Await process.WaitForExitAsync()
                    Dim message = If(Await errorText, "").Trim()
                    ' Ohne diese Zeile blieb ein Fehlstart unsichtbar: gmic_qt beendete sich sofort,
                    ' und im Protokoll stand nichts, nur in der Statuszeile "ohne Ergebnis".
                    If process.ExitCode <> 0 OrElse Not File.Exists(outputPath) Then
                        If message.Length > 2000 Then message = message.Substring(message.Length - 2000)
                        DiagnosticLogService.LogAlways("Gmic.Run",
                            $"exit={process.ExitCode} ausgabe={File.Exists(outputPath)} {message}")
                    End If
                    Return True
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Gmic.Run", ex)
                Return False
            End Try
        End Function

    End Class

End Namespace
