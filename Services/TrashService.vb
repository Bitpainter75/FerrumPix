Imports System.Diagnostics
Imports System.IO

Namespace Services

    ''' <summary>Verschiebt Dateien/Ordner in den Papierkorb des Betriebssystems statt sie dauerhaft zu
    ''' löschen. Linux: <c>gio trash</c> (glib, auf Desktop-Systemen vorhanden). macOS: Finder-Papierkorb
    ''' über AppleScript. Windows: Papierkorb über
    ''' Microsoft.VisualBasic.FileIO. Liefert False, wenn kein Papierkorb verfügbar ist oder die Verschiebung
    ''' scheitert. Still dauerhaft gelöscht wird nie: der Aufrufer zeigt den Grund und fragt, ob er
    ''' stattdessen endgültig löschen soll.</summary>
    Public NotInheritable Class TrashService
        Private Sub New()
        End Sub

        Public Shared Function MoveToTrash(path As String) As Boolean
            Dim reason As String = Nothing
            Return MoveToTrash(path, reason)
        End Function

        ''' <summary>Wie oben, nennt bei einem Fehlschlag aber den Grund, den das System angibt
        ''' (die Fehlerausgabe von <c>gio</c> oder <c>osascript</c>, unter Windows die Ausnahme).
        ''' Leer, wenn das System nichts sagt. Der Wortlaut kommt vom System und steht deshalb in
        ''' dessen Sprache, nicht in der der Anwendung.</summary>
        Public Shared Function MoveToTrash(path As String, ByRef reason As String) As Boolean
            reason = ""
            If String.IsNullOrEmpty(path) Then Return False
            If Not (File.Exists(path) OrElse Directory.Exists(path)) Then Return False
            Try
                If OperatingSystem.IsWindows() Then
                    Return MoveToTrashWindows(path)
                ElseIf OperatingSystem.IsMacOS() Then
                    Return RunTrashTool(MacOsCommand(path), reason)
                Else
                    If RunTrashTool(GioCommand(path), reason) Then Return True
                    Return MoveToVolumeTrash(path, reason)
                End If
            Catch ex As Exception
                ' Auch ein fehlendes Programm (gio nicht installiert) landet hier und ist ein Grund.
                reason = If(ex.Message, "").Trim()
                If OperatingSystem.IsLinux() Then Return MoveToVolumeTrash(path, reason)
                Return False
            End Try
        End Function

        ''' <summary>Der Rueckfall unter Linux, wenn das Papierkorb-Programm ablehnt: der Papierkorb
        ''' des Laufwerks, von FerrumPix selbst bedient (siehe <see cref="VolumeTrash"/>). Gilt er
        ''' hier nicht oder scheitert er ebenfalls, bleibt der Grund des Systems stehen: der ist
        ''' es, den der Nutzer in seinem System wiederfindet.</summary>
        Private Shared Function MoveToVolumeTrash(path As String, ByRef reason As String) As Boolean
            Dim ownReason As String = Nothing
            If Not VolumeTrash.TryMove(path, ownReason) Then
                If Not String.IsNullOrEmpty(ownReason) Then
                    DiagnosticLogService.LogAlways("Trash.Volume", "own trash folder not usable: " & ownReason)
                End If
                Return False
            End If
            DiagnosticLogService.LogAlways("Trash.Volume", "system trash refused (" & reason & "), moved into the trash folder of the drive")
            reason = ""
            Return True
        End Function

        ''' <summary>Startet das Papierkorb-Programm und liest seine Fehlerausgabe. Beide Ströme
        ''' werden nebenher gelesen: ein voller Puffer hielte das Programm sonst an, und die Wartezeit
        ''' liefe ab, obwohl es längst fertig sein könnte.</summary>
        Private Shared Function RunTrashTool(psi As ProcessStartInfo, ByRef reason As String) As Boolean
            Using proc = Process.Start(psi)
                If proc Is Nothing Then Return False
                Dim errorText = proc.StandardError.ReadToEndAsync()
                Dim outputText = proc.StandardOutput.ReadToEndAsync()
                If Not proc.WaitForExit(15000) Then
                    Try : proc.Kill(True) : Catch : End Try
                    Return False
                End If
                If proc.ExitCode = 0 Then Return True
                Dim text = ""
                Try
                    If errorText.Wait(2000) Then text = errorText.Result
                    If String.IsNullOrWhiteSpace(text) AndAlso outputText.Wait(2000) Then text = outputText.Result
                Catch
                End Try
                reason = ReasonFromOutput(text)
                Return False
            End Using
        End Function

        ''' <summary>Der Grund aus der Fehlerausgabe: die letzte nicht leere Zeile, ohne den
        ''' Programmnamen und ohne die Adresse der Datei davor. <c>gio</c> schreibt
        ''' <c>gio: file:///pfad/bild.jpg: Grund</c>; der Dateiname steht in der Meldung der
        ''' Anwendung schon daneben.</summary>
        Friend Shared Function ReasonFromOutput(output As String) As String
            If String.IsNullOrWhiteSpace(output) Then Return ""
            Dim line = output.Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries).
                Select(Function(l) l.Trim()).
                LastOrDefault(Function(l) l.Length > 0)
            If String.IsNullOrEmpty(line) Then Return ""
            If line.StartsWith("gio: ", StringComparison.Ordinal) Then line = line.Substring(5).TrimStart()
            If line.StartsWith("file://", StringComparison.Ordinal) Then
                Dim cut = line.IndexOf(": ", StringComparison.Ordinal)
                If cut >= 0 AndAlso cut + 2 < line.Length Then line = line.Substring(cut + 2).Trim()
            End If
            Return line
        End Function

        Private Shared Function MoveToTrashWindows(path As String) As Boolean
            ' RecycleOption/RecycleBin funktioniert nur unter Windows; der Aufruf ist durch
            ' OperatingSystem.IsWindows abgesichert. OnlyErrorDialogs = keine Rückfrage, nur bei Fehlern eine.
            If Directory.Exists(path) Then
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin)
            Else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin)
            End If
            Return True
        End Function

        Private Shared Function GioCommand(path As String) As ProcessStartInfo
            Dim psi As New ProcessStartInfo("gio") With {
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }
            psi.ArgumentList.Add("trash")
            psi.ArgumentList.Add("--")
            psi.ArgumentList.Add(path)
            Return psi
        End Function

        Private Shared Function MacOsCommand(path As String) As ProcessStartInfo
            Dim psi As New ProcessStartInfo("osascript") With {
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }
            psi.ArgumentList.Add("-e")
            psi.ArgumentList.Add("on run argv")
            psi.ArgumentList.Add("-e")
            psi.ArgumentList.Add("tell application ""Finder"" to delete POSIX file (item 1 of argv)")
            psi.ArgumentList.Add("-e")
            psi.ArgumentList.Add("end run")
            psi.ArgumentList.Add(path)
            Return psi
        End Function
    End Class

End Namespace
