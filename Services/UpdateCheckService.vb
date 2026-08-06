Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Net.Http
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Fragt die im Projekt hinterlegte Versionsnummer ab und vergleicht sie mit der
    ''' laufenden Fassung.
    '''
    ''' Gelesen wird die Datei VERSION im Hauptzweig - dieselbe Datei, aus der auch das Bauskript
    ''' die Nummer zieht. Damit gibt es keine zweite Stelle, die gepflegt werden müsste und
    ''' auseinanderlaufen könnte.
    '''
    ''' Der Abruf holt eine kurze Textdatei und schickt nichts mit, was über die Anwendung hinaus
    ''' etwas verrät: kein Rechnername, keine Kennung, keine Zählung. Er läuft nur beim Öffnen der
    ''' Einstellungen, und wenn er scheitert, bleibt es still - eine fehlende Verbindung ist kein
    ''' Fehler, den der Nutzer wegklicken müsste.</summary>
    Public NotInheritable Class UpdateCheckService

        Private Sub New()
        End Sub

        Public Const VersionAddress As String = "https://raw.githubusercontent.com/Bitpainter75/FerrumPix/main/VERSION"

        ''' <summary>Wohin der Hinweis führt: die zuletzt veröffentlichte Fassung mit allen Paketen.</summary>
        Public Const ReleasesAddress As String = "https://github.com/Bitpainter75/FerrumPix/releases/latest"

        Private Shared ReadOnly _client As New Lazy(Of HttpClient)(
            Function()
                Dim client = New HttpClient()
                ' Kurz gehalten: die Anzeige darf niemand aufhalten, und wer offline ist, wartet
                ' sonst eine halbe Minute auf ein Ergebnis, das ohnehin nicht kommt.
                client.Timeout = TimeSpan.FromSeconds(10)
                client.DefaultRequestHeaders.UserAgent.ParseAdd("FerrumPix")
                ' Ohne diese Bitte liefert ein Zwischenspeicher auf dem Weg unter Umständen noch
                ' die Nummer von gestern, und die Anzeige bliebe nach einer Veröffentlichung leer.
                client.DefaultRequestHeaders.CacheControl = New System.Net.Http.Headers.CacheControlHeaderValue With {.NoCache = True}
                Return client
            End Function)

        ''' <summary>Holt die veröffentlichte Nummer. Leerer Text heißt: nicht erreichbar oder
        ''' unbrauchbare Antwort - in beiden Fällen wird nichts angezeigt.</summary>
        Public Shared Async Function FetchLatestVersionAsync(Optional cancel As CancellationToken = Nothing) As Task(Of String)
            Try
                Using response = Await _client.Value.GetAsync(VersionAddress, cancel)
                    If Not response.IsSuccessStatusCode Then Return ""
                    Dim body = Await response.Content.ReadAsStringAsync(cancel)
                    Return Sanitize(body)
                End Using
            Catch ex As Exception
                ' Absichtlich nur ins Diagnoselog: eine gescheiterte Abfrage ist ein Nichtereignis.
                DiagnosticLogService.LogException("UpdateCheckService.FetchLatestVersionAsync", ex)
                Return ""
            End Try
        End Function

        ''' <summary>Übernimmt aus der Antwort nur, was wie eine Versionsnummer aussieht. Was am
        ''' anderen Ende steht, bestimmt nicht die Anwendung - eine Fehlerseite oder ein
        ''' Anmeldeformular darf nicht als Version in der Oberfläche landen.</summary>
        Public Shared Function Sanitize(raw As String) As String
            If String.IsNullOrWhiteSpace(raw) Then Return ""
            Dim first = raw.Split(New Char() {ChrW(10), ChrW(13)}, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            If first Is Nothing Then Return ""
            first = first.Trim()
            If first.Length = 0 OrElse first.Length > 32 Then Return ""
            ' Zahlengruppen mit Punkt, dahinter höchstens zwei Anhängsel wie "-6" oder "-beta2".
            If Not Regex.IsMatch(first, "^[0-9]{1,5}(\.[0-9]{1,5}){0,3}([-.][0-9A-Za-z]{1,12}){0,2}$") Then Return ""
            Return first
        End Function

        ''' <summary>True, wenn <paramref name="published"/> eine ANDERE Fassung bezeichnet als die
        ''' laufende - nicht nur eine höhere.
        '''
        ''' Absicht: auch ein geplanter Rückschritt soll ankommen. Wird eine Fassung zurückgezogen
        ''' und eine ältere veröffentlicht, ist das für den Nutzer genauso ein Handlungsgrund wie
        ''' eine neue, und ein Vergleich auf "höher" würde ihn stumm verschlucken.
        '''
        ''' Verglichen werden die Zahlengruppen der Reihe nach, fehlende Gruppen zählen als Null:
        ''' 0.9.21 und 0.9.21.0 sind dieselbe Fassung, 0.9.21-6 ist eine andere. Buchstabenanhängsel
        ''' bleiben außen vor - dann steht dort nichts, worüber die Anwendung raten müsste.</summary>
        Public Shared Function IsDifferent(published As String, current As String) As Boolean
            Dim theirs = NumberGroups(published)
            Dim ours = NumberGroups(current)
            ' Ohne eine der beiden Nummern gibt es nichts zu vergleichen, und dann wird geschwiegen.
            If theirs.Count = 0 OrElse ours.Count = 0 Then Return False
            For i = 0 To Math.Max(theirs.Count, ours.Count) - 1
                Dim left As Long = If(i < theirs.Count, theirs(i), 0L)
                Dim right As Long = If(i < ours.Count, ours(i), 0L)
                If left <> right Then Return True
            Next
            Return False
        End Function

        ''' <summary>Die Zahlengruppen einer Versionsangabe, höchstens sechs.</summary>
        Private Shared Function NumberGroups(text As String) As List(Of Long)
            Dim groups As New List(Of Long)()
            If String.IsNullOrWhiteSpace(text) Then Return groups
            For Each hit As Match In Regex.Matches(text, "[0-9]+")
                Dim value As Long
                ' Eine absurd lange Ziffernfolge fliegt raus, statt den Vergleich zu sprengen.
                If Not Long.TryParse(hit.Value, NumberStyles.None, CultureInfo.InvariantCulture, value) Then Continue For
                groups.Add(value)
                If groups.Count >= 6 Then Exit For
            Next
            Return groups
        End Function

    End Class

End Namespace
