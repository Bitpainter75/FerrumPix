Imports System
Imports System.Globalization
Imports System.Text.RegularExpressions

Namespace Services

    ''' <summary>Vergleicht gespeicherte Aenderungszeiten mit den heutigen, unabhaengig von
    ''' Zeitzone und Genauigkeit der Ablage.
    '''
    ''' <para>Die Frische-Stempel des Katalogs (ImageMeta.ScannedSourceModifiedAt,
    ''' ScannedSidecarModifiedAt, AiTagScan.SourceModifiedAt) stehen als
    ''' <c>LastWriteTime.ToString("o")</c> da, also als ORTSZEIT MIT VERSATZ. Zeichengleich
    ''' verglichen hing die Frage "unveraendert?" damit an der Zeitzone des Prozesses: derselbe
    ''' Zeitpunkt heisst unter UTC "14:17:29.5360000+00:00" und unter Europe/Berlin
    ''' "16:17:29.5360000+02:00". Ein Behaelter, der einmal mit und einmal ohne TZ startet, oder ein
    ''' Rechner, der die Zone wechselt, las so den ganzen Bestand neu ein, Bild fuer Bild, obwohl
    ''' keine Datei angefasst war.</para>
    '''
    ''' <para>Hier zaehlt deshalb der ZEITPUNKT. Geschrieben wird weiter wie bisher; ein Katalog
    ''' aus jeder frueheren Fassung und Zone gilt ohne Neueinlesung als frisch, solange die Datei
    ''' dieselbe Zeit traegt.</para>
    '''
    ''' <para>Und die GENAUIGKEIT: dieselbe Datei kann ueber zwei Wege mit verschieden vielen
    ''' Nachkommastellen ankommen (eine Freigabe, die nur Sekunden, Millisekunden oder
    ''' Mikrosekunden liefert, gegen ein lokales Dateisystem mit 100 ns). Endet eine der beiden
    ''' Zeiten glatt auf einer dieser Stufen, wird auf DIESER Stufe verglichen. Eine echte Aenderung
    ''' innerhalb derselben Sekunde wie die alte, glatte Zeit ginge dabei unter; das kommt bei
    ''' Fotos nicht vor, ein Neueinlesen des ganzen Bestands bei jedem Wegwechsel dagegen
    ''' sicher.</para></summary>
    Public NotInheritable Class FileTimeStamp
        Private Sub New()
        End Sub

        ''' <summary>Ein Zeitpunkt im Format "o" oder ISO 8601, mit oder ohne Versatz.</summary>
        Private Shared ReadOnly TimePattern As New Regex(
            "\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})?",
            RegexOptions.CultureInvariant Or RegexOptions.Compiled)

        ''' <summary>Die Stufen, auf die eine Ablage ihre Zeiten kuerzt, in Ticks (100 ns):
        ''' Sekunde, Millisekunde, Mikrosekunde.</summary>
        Private Shared ReadOnly PrecisionSteps As Long() = {TimeSpan.TicksPerSecond, TimeSpan.TicksPerMillisecond, 10L}

        ''' <summary>Traegt die Datei noch die gespeicherte Aenderungszeit?</summary>
        ''' <param name="stored">Der gespeicherte Stempel, leer heisst "nie gelesen".</param>
        ''' <param name="current">Die heutige Zeit der Datei, wie FileInfo.LastWriteTime sie
        ''' liefert.</param>
        Public Shared Function Matches(stored As String, current As DateTime) As Boolean
            If String.IsNullOrEmpty(stored) Then Return False
            ' UEBER DEN TEXT, wie er geschrieben wird, und nicht ueber LastWriteTimeUtc: in der
            ' doppelt vorkommenden Stunde der Zeitumstellung setzt "o" einen Versatz, der nicht der
            ' wahre sein muss. Gleich gebildet ist er auf beiden Seiten gleich, und ein unter
            ' derselben Zone gespeicherter Stempel stimmt wie bisher Zeichen fuer Zeichen.
            Return SameText(stored, current.ToString("o", CultureInfo.InvariantCulture))
        End Function

        ''' <summary>Meinen zwei Stempel dasselbe? Zusammengesetzte Stempel (die der Beistelldateien,
        ''' LibraryService.ComposeSidecarStamp) muessen bis auf die Zeitpunkte darin zeichengleich
        ''' sein; jeder Zeitpunkt wird als solcher verglichen.</summary>
        Public Shared Function SameText(stored As String, current As String) As Boolean
            stored = If(stored, "")
            current = If(current, "")
            If String.Equals(stored, current, StringComparison.Ordinal) Then Return True
            Dim storedTimes = TimePattern.Matches(stored)
            Dim currentTimes = TimePattern.Matches(current)
            If storedTimes.Count = 0 OrElse storedTimes.Count <> currentTimes.Count Then Return False
            Dim storedAt = 0
            Dim currentAt = 0
            For i = 0 To storedTimes.Count - 1
                Dim a = storedTimes(i)
                Dim b = currentTimes(i)
                ' Was zwischen den Zeitpunkten steht ("|fpxmp:", "+arp:"), muss gleich sein.
                If Not String.Equals(stored.Substring(storedAt, a.Index - storedAt),
                                     current.Substring(currentAt, b.Index - currentAt), StringComparison.Ordinal) Then Return False
                If Not SameInstant(a.Value, b.Value) Then Return False
                storedAt = a.Index + a.Length
                currentAt = b.Index + b.Length
            Next
            Return String.Equals(stored.Substring(storedAt), current.Substring(currentAt), StringComparison.Ordinal)
        End Function

        ''' <summary>Derselbe Zeitpunkt, auf der groberen Genauigkeit der beiden verglichen?</summary>
        Friend Shared Function SameInstant(a As String, b As String) As Boolean
            Dim first As DateTimeOffset
            Dim second As DateTimeOffset
            ' Ohne Versatz geschrieben (Kind Unspecified) gilt die Ortszeit, wie beim Schreiben.
            If Not DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, first) Then Return False
            If Not DateTimeOffset.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, second) Then Return False
            Dim x = first.UtcTicks
            Dim y = second.UtcTicks
            If x = y Then Return True
            For Each stepTicks In PrecisionSteps
                If x Mod stepTicks = 0 OrElse y Mod stepTicks = 0 Then
                    Return x \ stepTicks = y \ stepTicks
                End If
            Next
            Return False
        End Function
    End Class
End Namespace
