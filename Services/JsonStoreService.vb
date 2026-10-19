Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions

Namespace Services

    ''' <summary>
    ''' Die Handgriffe, die eine JSON-Ablage mit Nutzerdaten braucht, an EINER Stelle.
    '''
    ''' Warum das ueberhaupt eigene Routinen sind: Favoriten und Suchlisten haben frueher mit
    ''' File.WriteAllText direkt in die Zieldatei geschrieben und jeden Fehler mit einem leeren
    ''' Catch geschluckt. Das Lesen fing Parserfehler ebenso ab und lieferte eine LEERE Liste.
    ''' Weil Aendern aus Lesen und Zurueckschreiben besteht, ersetzte der naechste gesetzte
    ''' Favorit den gesamten Bestand durch eine Datei mit genau diesem einen Eintrag - ohne
    ''' jede Meldung. Favoriten und Suchlisten sind Handarbeit und nicht wiederherstellbar.
    '''
    ''' AppSettingsService haelt Zwischenspeicher und Fehlerbehandlung selbst, schreibt aber ueber
    ''' <see cref="WriteAtomicOrThrow"/> hier. So gibt es die Regel nur einmal.
    ''' </summary>
    Public NotInheritable Class JsonStoreService

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Schreibt ueber eine Nachbardatei und benennt dann um. NIE direkt in die Zieldatei:
        ''' FileMode.Create kuerzt sie sofort auf 0 Byte, und bricht das Schreiben danach ab
        ''' (volle Platte, Absturz, Stromausfall), ist der alte Inhalt bereits verloren.
        '''
        ''' Liefert False, wenn nichts geschrieben wurde. Der Aufrufer darf das nicht
        ''' stillschweigend uebergehen.
        ''' </summary>
        ''' Der Parameter heisst bewusst NICHT "path": VB unterscheidet keine Gross- und
        ''' Kleinschreibung, "Path.GetDirectoryName" loeste dann auf den Parameter auf.
        Public Shared Function WriteAtomic(targetFile As String, json As String, area As String) As Boolean
            Try
                WriteAtomicOrThrow(targetFile, json)
                Return True
            Catch ex As Exception
                ' Volle Platte, fehlende Rechte: frueher fiel das lautlos unter den Tisch und der
                ' Nutzer glaubte, seine Aenderung sei gespeichert.
                DiagnosticLogService.LogException(area & ".Save", ex)
                Return False
            End Try
        End Function

        ''' <summary>Dasselbe wie <see cref="WriteAtomic"/>, aber ein Fehler kommt als Ausnahme beim
        ''' Aufrufer an. Fuer AppSettingsService: es protokolliert selbst und haelt den Stand dann
        ''' vorgemerkt.</summary>
        Public Shared Sub WriteAtomicOrThrow(targetFile As String, json As String)
            Dim folder = Path.GetDirectoryName(targetFile)
            If Not String.IsNullOrEmpty(folder) Then Directory.CreateDirectory(folder)

            ' Eine Nachbardatei JE SCHREIBVORGANG. Mit einer festen ".tmp" schrieben zwei
            ' Vorgaenge auf dieselbe Datei in dieselbe Nachbardatei: der eine scheiterte, oder der
            ' eine haengte die halbe Datei des anderen um. Das Muster des Namens kennt
            ' RemoveStaleTemps; wer es aendert, aendert es dort mit.
            Dim tempPath = targetFile & "." & Guid.NewGuid().ToString("N").Substring(0, 8) & ".tmp"
            Try
                Using fs = New FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    Using writer = New StreamWriter(fs, New UTF8Encoding(encoderShouldEmitUTF8Identifier:=False), 4096, leaveOpen:=True)
                        writer.Write(json)
                    End Using
                    ' Bis auf die Platte, bevor umbenannt wird: sonst kann nach einem Stromausfall
                    ' der neue Name gelten und der Inhalt fehlen, und die Datei ist leer.
                    fs.Flush(flushToDisk:=True)
                End Using
                File.Move(tempPath, targetFile, overwrite:=True)
            Catch
                Try
                    If File.Exists(tempPath) Then File.Delete(tempPath)
                Catch
                End Try
                Throw
            End Try
        End Sub

        ''' <summary>
        ''' Legt eine unlesbare Datei zur Seite, statt sie beim naechsten Speichern ueberschreiben
        ''' zu lassen. Das ist der wichtigste der Handgriffe: er unterbricht die Kette aus
        ''' leerem Lesen und Zurueckschreiben und macht den Bestand von Hand rettbar.
        ''' </summary>
        Public Shared Sub BackupUnreadable(targetFile As String, area As String)
            Try
                If Not File.Exists(targetFile) Then Return
                File.Move(targetFile, targetFile & ".corrupt", overwrite:=True)
                DiagnosticLogService.LogAlways(area & ".Load",
                    "Datei war unlesbar und wurde nach " & Path.GetFileName(targetFile) & ".corrupt gesichert")
            Catch ex As Exception
                DiagnosticLogService.LogException(area & ".Backup", ex)
            End Try
        End Sub

        Private Shared ReadOnly _cleanedTargets As New HashSet(Of String)(PathIdentity.Comparer)
        Private Shared ReadOnly _cleanedTargetsLock As New Object()

        ''' <summary>
        ''' Raeumt liegengebliebene Nachbardateien von WriteAtomic weg: die mit dem Namensmuster
        ''' "ziel.json.xxxxxxxx.tmp" (acht Hexzeichen) und die feste "ziel.json.tmp" frueherer
        ''' Fassungen, beide nur neben DIESER Zieldatei und nur, wenn sie aelter als eine Stunde
        ''' sind. Ein Absturz zwischen Schreiben und Umbenennen laesst sie liegen, und weil der Name
        ''' je Vorgang zufaellig ist, ueberschreibt sie auch kein spaeterer Lauf mehr.
        '''
        ''' Die Stunde schuetzt einen Schreibvorgang, der gerade in einer zweiten Instanz laeuft.
        ''' Einmal je Zieldatei und Prozess: die Ablage wird beim Aendern immer wieder gelesen.
        '''
        ''' NUR HIER, im eigenen Einstellungsordner. Beiseitegelegte Fassungen in Fotoordnern
        ''' (".ferrumpix-old-") raeumt nichts automatisch: eine davon kann die einzige verbliebene
        ''' Kopie sein, und die Meldung des gescheiterten Vorgangs hat dem Nutzer genau diesen Ort
        ''' genannt.
        ''' </summary>
        Public Shared Sub RemoveStaleTemps(targetFile As String, area As String)
            If String.IsNullOrWhiteSpace(targetFile) Then Return
            SyncLock _cleanedTargetsLock
                If Not _cleanedTargets.Add(targetFile) Then Return
            End SyncLock
            Try
                Dim folder = Path.GetDirectoryName(targetFile)
                If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then Return
                Dim name = Path.GetFileName(targetFile)
                Dim pattern = New Regex("^" & Regex.Escape(name) & "(\.[0-9a-f]{8})?\.tmp$",
                                        If(OperatingSystem.IsWindows(), RegexOptions.IgnoreCase, RegexOptions.None))
                Dim cutoff = DateTime.UtcNow.AddHours(-1)
                For Each candidate In Directory.EnumerateFiles(folder, name & "*.tmp")
                    If Not pattern.IsMatch(Path.GetFileName(candidate)) Then Continue For
                    Try
                        If File.GetLastWriteTimeUtc(candidate) > cutoff Then Continue For
                        File.Delete(candidate)
                        DiagnosticLogService.LogAlways(area & ".Load",
                            "Liegengebliebene Nachbardatei entfernt: " & Path.GetFileName(candidate))
                    Catch ex As Exception
                        DiagnosticLogService.LogException(area & ".RemoveStaleTemps", ex)
                    End Try
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException(area & ".RemoveStaleTemps", ex)
            End Try
        End Sub

    End Class

End Namespace
