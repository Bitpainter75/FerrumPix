Imports System.Diagnostics
Imports System.IO
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>
    ''' Auf welcher Seite die Fensterknoepfe sitzen - rechts, links oder so, wie der Desktop es
    ''' vorgibt.
    '''
    ''' FerrumPix zeichnet seine Titelleiste unter Windows und Linux selbst (WindowDecorations="None"
    ''' in MainWindow.axaml), und selbst gezeichnete Knoepfe erben keine Anordnung. Wer seinen
    ''' Arbeitsplatz auf Knoepfe links eingerichtet hat, greift bei uns ins Leere - deshalb diese
    ''' Abfrage (Nutzerwunsch 2026-09-20).
    '''
    ''' Auf macOS stellt sich die Frage nicht: dort haengt der native NSWindow-Rahmen mit seinen drei
    ''' Knoepfen links, siehe MainWindow.ConfigurePlatformWindowChrome.
    ''' </summary>
    Public NotInheritable Class WindowButtonSideService

        Private Sub New()
        End Sub

        Public Const SideRight As String = "Right"
        Public Const SideLeft As String = "Left"
        Public Const SideSystem As String = "System"

        ''' <summary>Die Antwort des Desktops, einmal je Programmlauf. Sie kostet zwei bis drei
        ''' Prozessstarts; jede Fensterumstellung neu zu fragen waere Verschwendung, und eine
        ''' Anordnung, die sich waehrend der Arbeit aendert, gibt es praktisch nicht.</summary>
        Private Shared _desktopAnswer As Boolean?

        ''' <summary>Sitzen die Knoepfe links? Der einzige Aufrufer ist das Fenster selbst.</summary>
        Public Shared Function IsLeft(mode As String) As Boolean
            Select Case AppSettingsService.NormalizeWindowButtonsSide(mode)
                Case SideLeft : Return True
                Case SideRight : Return False
                Case Else : Return DesktopPrefersLeft()
            End Select
        End Function

        ''' <summary>Was der Desktop vorgibt. False, wenn sich nichts ermitteln laesst - rechts ist
        ''' die Anordnung, die Windows und die meisten Linux-Arbeitsflaechen ab Werk haben.</summary>
        Public Shared Function DesktopPrefersLeft() As Boolean
            If _desktopAnswer.HasValue Then Return _desktopAnswer.Value
            Dim left As Boolean = False
            Try
                If OperatingSystem.IsMacOS() Then
                    left = True
                ElseIf Not OperatingSystem.IsWindows() Then
                    left = If(ReadKdeSide(), If(ReadGSettingsSide(), If(ReadXfceSide(), False)))
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Fensterknoepfe.Desktop", ex)
            End Try
            _desktopAnswer = left
            Return left
        End Function

        ''' <summary>KDE Plasma schreibt seine Anordnung in eine Textdatei - kein Prozessstart noetig.
        ''' Die Knoepfe stehen dort als Buchstaben: X schliessen, A maximieren, I minimieren.</summary>
        Private Shared Function ReadKdeSide() As Boolean?
            ' NICHT "path" nennen: das verdeckt die Klasse Path, und der Aufruf darunter geht
            ' unverstaendlich kaputt (VB kennt keinen Unterschied zwischen Path und path).
            Dim configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                          ".config", "kwinrc")
            If Not File.Exists(configPath) Then Return Nothing
            Dim inSection = False
            Dim onLeft As String = Nothing
            For Each rawLine In File.ReadLines(configPath)
                Dim line = rawLine.Trim()
                If line.StartsWith("[") Then
                    ' Der Gruppenname traegt die Fassung der Fensterdekoration (kdecoration2, …).
                    inSection = line.StartsWith("[org.kde.kdecoration", StringComparison.OrdinalIgnoreCase)
                    Continue For
                End If
                If Not inSection Then Continue For
                If line.StartsWith("ButtonsOnLeft=", StringComparison.OrdinalIgnoreCase) Then
                    onLeft = line.Substring("ButtonsOnLeft=".Length)
                End If
            Next
            If onLeft Is Nothing Then Return Nothing
            Return ContainsWindowButton(onLeft, "XAI")
        End Function

        ''' <summary>GNOME und seine Verwandten halten die Anordnung in einem Text wie
        ''' „appmenu:minimize,maximize,close": vor dem Doppelpunkt steht, was links sitzt.</summary>
        Private Shared Function ReadGSettingsSide() As Boolean?
            Dim schemas = {
                ("org.gnome.desktop.wm.preferences", "button-layout"),
                ("org.cinnamon.desktop.wm.preferences", "button-layout"),
                ("org.mate.Marco.general", "button-layout")
            }
            For Each entry In schemas
                Dim value = RunTool("gsettings", $"get {entry.Item1} {entry.Item2}")
                If String.IsNullOrWhiteSpace(value) Then Continue For
                Dim layout = value.Trim().Trim(""""c, "'"c)
                Dim colon = layout.IndexOf(":"c)
                If colon < 0 Then Continue For
                Return ContainsWindowButton(layout.Substring(0, colon), "")
            Next
            Return Nothing
        End Function

        ''' <summary>Xfce schreibt dasselbe als „O|SHMC": vor dem Strich steht die linke Seite,
        ''' C schliesst, H minimiert, M maximiert.</summary>
        Private Shared Function ReadXfceSide() As Boolean?
            Dim value = RunTool("xfconf-query", "-c xfwm4 -p /general/button_layout")
            If String.IsNullOrWhiteSpace(value) Then Return Nothing
            Dim layout = value.Trim()
            Dim bar = layout.IndexOf("|"c)
            If bar < 0 Then Return Nothing
            Return ContainsWindowButton(layout.Substring(0, bar), "CHM")
        End Function

        ''' <summary>Steht in diesem Stueck der Anordnung einer der drei Fensterknoepfe?
        '''
        ''' Das Menue oder „auf allen Arbeitsflaechen" zaehlt NICHT: unter GNOME sitzt das Menue ab
        ''' Werk links, waehrend Schliessen rechts steht - wer danach ginge, bekaeme bei jedem
        ''' zweiten Arbeitsplatz die falsche Seite.
        '''
        ''' <paramref name="letters"/> sind die Kuerzel DIESER Arbeitsflaeche, leer fuer die
        ''' ausgeschriebenen Namen von GNOME. Sie muessen getrennt bleiben, weil dieselben
        ''' Buchstaben anderswo etwas anderes heissen: KDE schreibt X/A/I fuer Schliessen,
        ''' Maximieren und Minimieren und M fuer das Menue, Xfce dagegen M fuer Maximieren. Eine
        ''' gemeinsame Liste haette KDEs Standardbelegung „MS" links fuer Fensterknoepfe
        ''' gehalten.</summary>
        Private Shared Function ContainsWindowButton(part As String, letters As String) As Boolean
            If String.IsNullOrWhiteSpace(part) Then Return False
            Dim text = part.ToLowerInvariant()
            If text.Contains("close") OrElse text.Contains("minimize") OrElse text.Contains("maximize") Then Return True
            If letters = "" Then Return False
            ' Gross-/Kleinschreibung zaehlt bei den Kuerzeln, deshalb der ungewandelte Text.
            For Each c In part
                If letters.Contains(c) Then Return True
            Next
            Return False
        End Function

        ''' <summary>Ein Kommandozeilenwerkzeug fragen. Nothing, wenn es fehlt, laenger als eine
        ''' Sekunde braucht oder mit einem Fehler endet - der Aufrufer geht dann zur naechsten
        ''' Quelle weiter. Beim Start des Fensters darf nichts haengenbleiben.</summary>
        Private Shared Function RunTool(fileName As String, arguments As String) As String
            Try
                Dim info = New ProcessStartInfo(fileName, arguments) With {
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .UseShellExecute = False,
                    .CreateNoWindow = True
                }
                Using proc = Process.Start(info)
                    If proc Is Nothing Then Return Nothing
                    ' NICHT ReadToEnd: das wartet auf das Ende der Ausgabe und damit auf das Ende
                    ' des Prozesses - die Frist darunter waere nie zum Zug gekommen, und ein
                    ' haengendes Werkzeug haette den Start des Fensters mitgenommen. Beide Kanaele
                    ' laufen nebenher, sonst kann ein voller Fehlerpuffer den Prozess blockieren.
                    Dim ausgabe = proc.StandardOutput.ReadToEndAsync()
                    Dim fehler = proc.StandardError.ReadToEndAsync()
                    If Not proc.WaitForExit(1000) Then
                        Try : proc.Kill(True) : Catch : End Try
                        Return Nothing
                    End If
                    If proc.ExitCode <> 0 Then Return Nothing
                    ' Der Prozess ist beendet, der Rest der Ausgabe liegt schon in der Leitung.
                    If Not Task.WaitAll({ausgabe, fehler}, 500) Then Return Nothing
                    Return ausgabe.Result
                End Using
            Catch
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
