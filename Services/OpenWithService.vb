Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Text
Imports FerrumPix.Models

Namespace Services

    ''' <summary>
    ''' "Öffnen mit": gibt die ORIGINALDATEI an ein Programm, das der Nutzer in den Einstellungen
    ''' eingetragen hat. Ohne Bearbeitungen und ohne Rueckweg (entschieden 2026-09-11) - es wird
    ''' nichts gerendert, es entsteht keine Datei, und FerrumPix wartet auf nichts. Wer seine
    ''' Bearbeitung weitergeben will, speichert oder exportiert vorher bewusst.
    '''
    ''' Warum nicht ueber <see cref="ShellOpenService"/>: der kennt nur die Systemvorgabe fuer den
    ''' Dateityp, und die ist bei RAW oft FerrumPix selbst. Hier wird ein bestimmtes Programm mit
    ''' eigenen Parametern gestartet - als einzelne Werte und ohne Shell, damit Leerzeichen und
    ''' Sonderzeichen im Pfad nichts anrichten. Das Aktivierungs-Token fuer Wayland geht genauso
    ''' mit wie dort, sonst bliebe das neue Fenster hinten.
    ''' </summary>
    Public NotInheritable Class OpenWithService

        Private Sub New()
        End Sub

        ''' <summary>Steht fuer den Dateipfad. Bei mehreren Dateien wird jeder Parameter, der ihn
        ''' enthaelt, je Datei wiederholt.</summary>
        Public Const FilePlaceholder As String = "%f"

        ''' <summary>Die eingetragenen Programme, die sich starten lassen - ein Eintrag ohne
        ''' Programm steht zwar in den Einstellungen (er wird gerade ausgefuellt), aber nicht im
        ''' Menue.</summary>
        Public Shared Function ConfiguredPrograms() As IReadOnlyList(Of OpenWithProgramSettings)
            Dim programs = AppSettingsService.Load()?.OpenWithPrograms
            If programs Is Nothing Then Return New List(Of OpenWithProgramSettings)()
            Return programs.Where(Function(p) p IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(p.ProgramPath)).ToList()
        End Function

        Public Shared Function FindProgram(id As String) As OpenWithProgramSettings
            If String.IsNullOrWhiteSpace(id) Then Return Nothing
            Return ConfiguredPrograms().FirstOrDefault(Function(p) String.Equals(p.Id, id, StringComparison.Ordinal))
        End Function

        ''' <summary>Was im Menue steht: der eingetragene Name, sonst der Dateiname des Programms -
        ''' bei einer Befehlszeile im Programmfeld der des ersten Stuecks. Der Name stammt vom Nutzer
        ''' und wird nicht uebersetzt.</summary>
        Public Shared Function DisplayName(program As OpenWithProgramSettings) As String
            If program Is Nothing Then Return ""
            Dim name = If(program.Name, "").Trim()
            If name.Length > 0 Then Return name
            Dim path = SplitProgramField(program.ProgramPath).Program.TrimEnd("/"c, "\"c)
            Dim fileName = IO.Path.GetFileNameWithoutExtension(path)
            Return If(String.IsNullOrWhiteSpace(fileName), path, fileName)
        End Function

        ''' <summary>Kann dieses Element weitergegeben werden? Nur eine LOKALE Datei: ein Serverbild
        ''' traegt einen Pseudo-Pfad, den kein fremdes Programm oeffnen kann, und ein Ordner ist
        ''' kein Bild.</summary>
        Public Shared Function IsOpenable(item As ImageItem) As Boolean
            If item Is Nothing Then Return False
            If item.IsFolder OrElse item.IsParentFolderEntry OrElse item.IsRemoteAsset OrElse item.IsTrashed Then Return False
            Return Not String.IsNullOrWhiteSpace(item.FilePath)
        End Function

        ''' <summary>Zerlegt die Parameterzeile in einzelne Werte. Doppelte und einfache
        ''' Anfuehrungszeichen fassen zusammen, was Leerzeichen enthaelt; sie selbst gehoeren nicht
        ''' zum Wert. Ein Rueckstrich hat keine Sonderbedeutung - er kommt in Windows-Pfaden vor.</summary>
        Public Shared Function SplitArguments(text As String) As List(Of String)
            Dim result As New List(Of String)()
            If String.IsNullOrWhiteSpace(text) Then Return result
            Dim current As New StringBuilder()
            Dim inToken = False
            Dim quote As Char = ChrW(0)
            For Each c In text
                If quote <> ChrW(0) Then
                    If c = quote Then
                        quote = ChrW(0)
                    Else
                        current.Append(c)
                    End If
                ElseIf c = """"c OrElse c = "'"c Then
                    quote = c
                    inToken = True
                ElseIf Char.IsWhiteSpace(c) Then
                    If inToken Then
                        result.Add(current.ToString())
                        current.Clear()
                        inToken = False
                    End If
                Else
                    current.Append(c)
                    inToken = True
                End If
            Next
            If inToken Then result.Add(current.ToString())
            Return result
        End Function

        ''' <summary>Die Werte fuer den Aufruf: jeder Parameter mit %f einmal je Datei, und ohne %f
        ''' die Dateien hinten angehaengt. So bekommt ein Programm eine ganze Auswahl in EINEM
        ''' Aufruf, statt fuer jedes Bild eine eigene Instanz zu starten.</summary>
        Public Shared Function BuildArguments(arguments As String, filePaths As IList(Of String)) As List(Of String)
            Return ExpandPlaceholders(SplitArguments(arguments), filePaths)
        End Function

        Private Shared Function ExpandPlaceholders(tokens As IEnumerable(Of String), filePaths As IList(Of String)) As List(Of String)
            Dim files = If(filePaths, New List(Of String)())
            Dim result As New List(Of String)()
            Dim placed = False
            For Each token In tokens
                If token.Contains(FilePlaceholder) Then
                    For Each file In files
                        result.Add(token.Replace(FilePlaceholder, file))
                    Next
                    placed = True
                Else
                    result.Add(token)
                End If
            Next
            If Not placed Then result.AddRange(files)
            Return result
        End Function

        ''' <summary>Das Programmfeld, zerlegt in Programm und vorangestellte Werte.
        '''
        ''' Im Feld darf eine ganze Befehlszeile stehen, etwa env "WINEPREFIX=/home/name/.wine" wine
        ''' fuer ein Windows-Programm unter Wine: ist der Eintrag keine vorhandene Datei und enthaelt er
        ''' Leerzeichen, wird er wie die Parameter zerlegt, das erste Stueck ist das Programm. Ein
        ''' VORHANDENER Pfad mit Leerzeichen bleibt ein Pfad - sonst liefe "/opt/Mein Programm/app"
        ''' als Programm "/opt/Mein" los.</summary>
        Private Shared Function SplitProgramField(programField As String) As (Program As String, Leading As List(Of String))
            Dim program = If(programField, "").Trim()
            Dim leading As New List(Of String)()
            If program.Length = 0 OrElse File.Exists(program) OrElse Not program.Any(AddressOf Char.IsWhiteSpace) Then
                Return (program, leading)
            End If
            Dim parts = SplitArguments(program)
            If parts.Count = 0 Then Return (program, leading)
            leading.AddRange(parts.Skip(1))
            Return (parts(0), leading)
        End Function

        ''' <summary>Programm und Werte fuer den Start ausserhalb eines Mac-Buendels. Die Werte aus
        ''' dem Programmfeld stehen vor den Parametern, und %f wirkt in beiden - wer die ganze Zeile
        ''' samt %f ins Programmfeld schreibt, bekommt dasselbe wie mit getrennten Feldern.</summary>
        Friend Shared Function BuildCommand(programField As String, arguments As String,
                                            files As IList(Of String)) As (FileName As String, Arguments As List(Of String))
            Dim split = SplitProgramField(programField)
            Dim tokens = split.Leading.Concat(SplitArguments(arguments))
            Return (split.Program, ExpandPlaceholders(tokens, files))
        End Function

        ''' <summary>Startet das Programm mit den Dateien. True, wenn der Start gelang - was das
        ''' Programm danach tut, liegt ausserhalb.</summary>
        Public Shared Function Launch(program As OpenWithProgramSettings, filePaths As IEnumerable(Of String),
                                      source As String) As Boolean
            If program Is Nothing OrElse String.IsNullOrWhiteSpace(program.ProgramPath) Then Return False
            Dim files = If(filePaths, Enumerable.Empty(Of String)()).
                        Where(Function(p) Not String.IsNullOrWhiteSpace(p) AndAlso File.Exists(p)).
                        ToList()
            If files.Count = 0 Then Return False

            Try
                Dim info = CreateStartInfo(program.ProgramPath.Trim(), program.Arguments, files)
                ShellOpenService.PassActivationToken(info)
                Using Process.Start(info)
                End Using
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException(source, ex)
                Return False
            End Try
        End Function

        ''' <summary>Die Werte fuer "open", wenn das Programm auf dem Mac ein .app-Buendel ist.
        '''
        ''' OHNE %f gehen die Dateien an open selbst: so oeffnet der Mac Dokumente, auch in einem
        ''' schon laufenden Programm. Eigene Parameter stehen dahinter nach "--args".
        '''
        ''' MIT %f steht der Pfad IN einem Parameter, und der erreicht das Programm nur ueber
        ''' "--args". Diese Werte liest ein Programm aber nur beim Start; laeuft es schon, verwirft
        ''' open sie still. Deshalb dann "-n" fuer eine neue Instanz - sonst ginge genau das
        ''' eingetragene Argument verloren, und das Programm oeffnete sich ohne Datei. Frueher fielen
        ''' Parameter mit %f hier ganz weg.</summary>
        Friend Shared Function BuildMacOpenArguments(programPath As String, arguments As String,
                                                    files As IList(Of String)) As List(Of String)
            Dim result As New List(Of String)()
            Dim fileList = If(files, New List(Of String)())
            If SplitArguments(arguments).Any(Function(t) t.Contains(FilePlaceholder)) Then
                result.Add("-n")
                result.Add("-a")
                result.Add(programPath)
                result.Add("--args")
                result.AddRange(BuildArguments(arguments, fileList))
                Return result
            End If

            result.Add("-a")
            result.Add(programPath)
            result.AddRange(fileList)
            Dim own = SplitArguments(arguments)
            If own.Count > 0 Then
                result.Add("--args")
                result.AddRange(own)
            End If
            Return result
        End Function

        ''' <summary>Auf dem Mac ist ein Programm meist ein .app-Buendel und kein ausfuehrbarer Pfad;
        ''' es startet ueber "open -a", siehe <see cref="BuildMacOpenArguments"/>. Alles andere
        ''' ueber <see cref="BuildCommand"/>.</summary>
        Private Shared Function CreateStartInfo(programPath As String, arguments As String,
                                                files As List(Of String)) As ProcessStartInfo
            Dim info As ProcessStartInfo
            If OperatingSystem.IsMacOS() AndAlso
               programPath.TrimEnd("/"c).EndsWith(".app", StringComparison.OrdinalIgnoreCase) Then
                info = New ProcessStartInfo("open")
                For Each token In BuildMacOpenArguments(programPath, arguments, files)
                    info.ArgumentList.Add(token)
                Next
            Else
                Dim command = BuildCommand(programPath, arguments, files)
                info = New ProcessStartInfo(command.FileName)
                For Each token In command.Arguments
                    info.ArgumentList.Add(token)
                Next
            End If
            info.UseShellExecute = False
            Return info
        End Function

    End Class

End Namespace
