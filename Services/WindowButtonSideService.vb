Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Wo die Fensterknoepfe sitzen und in welcher Reihenfolge - eine Antwort, weil beides
    ''' aus derselben Angabe des Desktops kommt.</summary>
    Public NotInheritable Class WindowButtonLayout

        Public Sub New(onLeft As Boolean, order As IEnumerable(Of String))
            Me.OnLeft = onLeft
            Me.Order = order.ToArray()
        End Sub

        ''' Sitzen die Knoepfe links?
        Public ReadOnly Property OnLeft As Boolean

        ''' <summary>Die drei Knoepfe von LINKS NACH RECHTS, als Rollennamen
        ''' (<see cref="WindowButtonSideService.RoleClose"/> und die beiden anderen). Dieselbe
        ''' Leserichtung, in der die Desktops ihre Anordnung schreiben und in der die Knopfreihe
        ''' gezeichnet wird - eine zweite Leserichtung waere eine Fehlerquelle ohne Gegenwert.
        ''' Immer alle drei, siehe die Klasse darunter.</summary>
        Public ReadOnly Property Order As String()

    End Class

    ''' <summary>
    ''' Auf welcher Seite die Fensterknoepfe sitzen und in welcher Reihenfolge - rechts, links oder
    ''' so, wie der Desktop es vorgibt.
    '''
    ''' FerrumPix zeichnet seine Titelleiste unter Windows und Linux selbst (WindowDecorations="None"
    ''' in MainWindow.axaml), und selbst gezeichnete Knoepfe erben keine Anordnung. Wer seinen
    ''' Arbeitsplatz auf Knoepfe links eingerichtet hat, greift bei uns ins Leere - deshalb diese
    ''' Abfrage.
    '''
    ''' **DIE ANORDNUNG DES DESKTOPS BESTIMMT DIE REIHENFOLGE, NICHT DEN BESTAND.** Sie sagt
    ''' naemlich beides: GNOME steht seit Jahren ab Werk auf "appmenu:close",
    ''' nennt also nur EINEN Fensterknopf. Wer das woertlich uebernaehme, naehme dem Grossteil der
    ''' GNOME-Anwender Minimieren und Maximieren weg - und unsere Leiste ist der einzige Weg dorthin.
    ''' Genannte Knoepfe kommen deshalb in der genannten Reihenfolge, nicht genannte haengen hinten
    ''' an.
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

        ''' Die drei Knoepfe, die es bei uns gibt. Der Name plus "Button" ist der im Markup.
        Public Const RoleClose As String = "Close"
        Public Const RoleMinimize As String = "Minimize"
        Public Const RoleMaximize As String = "Maximize"

        ''' <summary>Die Reihenfolge, die FerrumPix ohne Antwort des Desktops nimmt, von links nach
        ''' rechts. Beide Male steht Schliessen an der Fensterkante - so kennt man es links von
        ''' macOS und rechts von Windows.</summary>
        Private Shared ReadOnly DefaultOrderLeft As String() = {RoleClose, RoleMinimize, RoleMaximize}
        Private Shared ReadOnly DefaultOrderRight As String() = {RoleMinimize, RoleMaximize, RoleClose}

        ''' <summary>Die Antwort des Desktops, einmal je Programmlauf. Sie kostet zwei bis drei
        ''' Prozessstarts; jede Fensterumstellung neu zu fragen waere Verschwendung, und eine
        ''' Anordnung, die sich waehrend der Arbeit aendert, gibt es praktisch nicht.</summary>
        Private Shared _desktopAnswer As WindowButtonLayout

        ''' <summary>Seite und Reihenfolge fuer die gewaehlte Einstellung. Die EINSTELLUNG bestimmt
        ''' nur die Seite; die Reihenfolge kommt immer vom Desktop, wenn er eine nennt. Wer
        ''' ausdruecklich "links" waehlt, will die Knoepfe links - nicht eine andere Reihenfolge, als
        ''' er sie ueberall sonst gewohnt ist.
        '''
        ''' Wechselt die Seite gegen die des Desktops, gilt die WERKSREIHENFOLGE dieser Seite. Seine
        ''' Reihenfolge laesst sich dorthin nicht sinnvoll mitnehmen: links und rechts sind keine
        ''' Spiegelbilder voneinander. macOS liest links Schliessen, Minimieren, Maximieren, Windows
        ''' rechts Minimieren, Maximieren, Schliessen - gespiegelt ergaebe das eine Anordnung, die es
        ''' nirgends gibt. Wer die Seite bewusst wechselt, bekommt also die dort uebliche.</summary>
        Public Shared Function Resolve(mode As String) As WindowButtonLayout
            Dim desktop = DesktopLayout()
            Dim wanted As Boolean
            Select Case AppSettingsService.NormalizeWindowButtonsSide(mode)
                Case SideLeft : wanted = True
                Case SideRight : wanted = False
                Case Else : Return desktop
            End Select
            If wanted = desktop.OnLeft Then Return desktop
            Return New WindowButtonLayout(wanted, If(wanted, DefaultOrderLeft, DefaultOrderRight))
        End Function

        ''' Sitzen die Knoepfe links?
        Public Shared Function IsLeft(mode As String) As Boolean
            Return Resolve(mode).OnLeft
        End Function

        ''' <summary>Was der Desktop vorgibt. Laesst sich nichts ermitteln, gilt rechts in der
        ''' Werksreihenfolge - so haben es Windows und die meisten Linux-Arbeitsflaechen.</summary>
        Public Shared Function DesktopLayout() As WindowButtonLayout
            If _desktopAnswer IsNot Nothing Then Return _desktopAnswer
            Dim answer As WindowButtonLayout = Nothing
            Try
                If OperatingSystem.IsMacOS() Then
                    answer = New WindowButtonLayout(True, DefaultOrderLeft)
                ElseIf Not OperatingSystem.IsWindows() Then
                    answer = If(ReadKdeLayout(), If(ReadGSettingsLayout(), ReadXfceLayout()))
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Fensterknoepfe.Desktop", ex)
            End Try
            If answer Is Nothing Then answer = New WindowButtonLayout(False, DefaultOrderRight)
            _desktopAnswer = answer
            Return answer
        End Function

        ''' <summary>Nur fuer die Diagnose: die gemerkte Antwort vergessen, damit sich das Auswerten
        ''' mit gestellten Werten pruefen laesst.</summary>
        Friend Shared Sub ForgetDesktopAnswer()
            _desktopAnswer = Nothing
        End Sub

        ''' <summary>KDE Plasma schreibt seine Anordnung in eine Textdatei - kein Prozessstart noetig.
        ''' Die Knoepfe stehen dort als Buchstaben: X schliessen, A maximieren, I minimieren.</summary>
        Private Shared Function ReadKdeLayout() As WindowButtonLayout
            ' NICHT "path" nennen: das verdeckt die Klasse Path, und der Aufruf darunter geht
            ' unverstaendlich kaputt (VB kennt keinen Unterschied zwischen Path und path).
            Dim configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                          ".config", "kwinrc")
            If Not File.Exists(configPath) Then Return Nothing
            Dim inSection = False
            Dim onLeft As String = Nothing, onRight As String = Nothing
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
                ElseIf line.StartsWith("ButtonsOnRight=", StringComparison.OrdinalIgnoreCase) Then
                    onRight = line.Substring("ButtonsOnRight=".Length)
                End If
            Next
            If onLeft Is Nothing AndAlso onRight Is Nothing Then Return Nothing
            Return Decide(ParseGroup(onLeft, KdeLetters), ParseGroup(onRight, KdeLetters))
        End Function

        ''' <summary>GNOME und seine Verwandten halten die Anordnung in einem Text wie
        ''' „appmenu:minimize,maximize,close": vor dem Doppelpunkt steht, was links sitzt.</summary>
        Private Shared Function ReadGSettingsLayout() As WindowButtonLayout
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
                Dim decided = Decide(ParseGroup(layout.Substring(0, colon), Nothing),
                                     ParseGroup(layout.Substring(colon + 1), Nothing))
                If decided IsNot Nothing Then Return decided
            Next
            Return Nothing
        End Function

        ''' <summary>Xfce schreibt dasselbe als „O|SHMC": vor dem Strich steht die linke Seite,
        ''' C schliesst, H minimiert, M maximiert.</summary>
        Private Shared Function ReadXfceLayout() As WindowButtonLayout
            Dim value = RunTool("xfconf-query", "-c xfwm4 -p /general/button_layout")
            If String.IsNullOrWhiteSpace(value) Then Return Nothing
            Dim layout = value.Trim()
            Dim bar = layout.IndexOf("|"c)
            If bar < 0 Then Return Nothing
            Return Decide(ParseGroup(layout.Substring(0, bar), XfceLetters),
                          ParseGroup(layout.Substring(bar + 1), XfceLetters))
        End Function

        ''' <summary>Die Kuerzel der Arbeitsflaechen. Sie muessen GETRENNT bleiben, weil dieselben
        ''' Buchstaben anderswo etwas anderes heissen: KDE schreibt M fuer das Menue, Xfce M fuer
        ''' Maximieren. Eine gemeinsame Liste haette KDEs Standardbelegung „MS" links fuer
        ''' Fensterknoepfe gehalten.</summary>
        Private Shared ReadOnly KdeLetters As New Dictionary(Of Char, String) From {
            {"X"c, RoleClose}, {"A"c, RoleMaximize}, {"I"c, RoleMinimize}}

        Private Shared ReadOnly XfceLetters As New Dictionary(Of Char, String) From {
            {"C"c, RoleClose}, {"M"c, RoleMaximize}, {"H"c, RoleMinimize}}

        ''' <summary>Die Fensterknoepfe eines Anordnungsstuecks, in der genannten Reihenfolge.
        '''
        ''' Alles andere faellt weg, und das ist der Punkt: das Menue oder „auf allen
        ''' Arbeitsflaechen" ist kein Fensterknopf. Unter GNOME sitzt das Menue ab Werk links,
        ''' waehrend Schliessen rechts steht - wer danach ginge, bekaeme bei jedem zweiten
        ''' Arbeitsplatz die falsche Seite.
        '''
        ''' <paramref name="letters"/> sind die Kuerzel DIESER Arbeitsflaeche, Nothing fuer die
        ''' ausgeschriebenen Namen von GNOME.</summary>
        Private Shared Function ParseGroup(part As String, letters As Dictionary(Of Char, String)) As List(Of String)
            Dim roles As New List(Of String)
            If String.IsNullOrWhiteSpace(part) Then Return roles
            For Each token In part.Split(","c)
                Dim role As String
                Select Case token.Trim().ToLowerInvariant()
                    Case "close" : role = RoleClose
                    Case "minimize" : role = RoleMinimize
                    Case "maximize" : role = RoleMaximize
                    Case Else : Continue For
                End Select
                If Not roles.Contains(role) Then roles.Add(role)
            Next
            If roles.Count > 0 OrElse letters Is Nothing Then Return roles
            ' Gross-/Kleinschreibung zaehlt bei den Kuerzeln, deshalb der ungewandelte Text.
            For Each c In part
                Dim role As String = Nothing
                If letters.TryGetValue(c, role) AndAlso Not roles.Contains(role) Then roles.Add(role)
            Next
            Return roles
        End Function

        ''' <summary>Aus den beiden Gruppen Seite und Reihenfolge machen. Nothing, wenn keine der
        ''' beiden einen Fensterknopf nennt - dann hat diese Quelle nichts zu sagen und die naechste
        ''' kommt dran.
        '''
        ''' Die Seite ist die mit MEHR Fensterknoepfen, bei Gleichstand rechts: das ist die
        ''' Anordnung, von der jeder ausgeht, der nichts umgestellt hat.
        '''
        ''' **EINE SEITE, AUCH WENN DER DESKTOP AUF BEIDE VERTEILT.** Wer zum Beispiel
        ''' „close:minimize,maximize" eingerichtet hat, will Schliessen links und die anderen beiden
        ''' rechts. Unsere Leiste hat nur EINEN Knopfblock, und das Logo sitzt auf der freien Seite;
        ''' geteilt gaebe es keine freie Seite mehr. Die Knoepfe bleiben deshalb zusammen und gehen
        ''' dorthin, wo die Mehrheit von ihnen steht - der Rest haengt in der Werksreihenfolge an.
        ''' Siehe OFFENE_PUNKTE.md, falls das einmal wirklich geteilt werden soll.</summary>
        Private Shared Function Decide(leftRoles As List(Of String), rightRoles As List(Of String)) As WindowButtonLayout
            If leftRoles.Count = 0 AndAlso rightRoles.Count = 0 Then Return Nothing
            Dim onLeft = leftRoles.Count > rightRoles.Count
            ' Was der Desktop nicht nennt, haengt hinten an - in der Werksreihenfolge dieser Seite,
            ' damit der Rest untereinander nicht willkuerlich steht. Beides in derselben
            ' Leserichtung von links nach rechts, in der der Desktop es schreibt.
            Dim order As New List(Of String)(If(onLeft, leftRoles, rightRoles))
            For Each role In If(onLeft, DefaultOrderLeft, DefaultOrderRight)
                If Not order.Contains(role) Then order.Add(role)
            Next
            Return New WindowButtonLayout(onLeft, order)
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
                    Dim output = proc.StandardOutput.ReadToEndAsync()
                    Dim errorOutput = proc.StandardError.ReadToEndAsync()
                    If Not proc.WaitForExit(1000) Then
                        Try : proc.Kill(True) : Catch : End Try
                        Return Nothing
                    End If
                    If proc.ExitCode <> 0 Then Return Nothing
                    ' Der Prozess ist beendet, der Rest der Ausgabe liegt schon in der Leitung.
                    If Not Task.WaitAll({output, errorOutput}, 500) Then Return Nothing
                    Return output.Result
                End Using
            Catch
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
