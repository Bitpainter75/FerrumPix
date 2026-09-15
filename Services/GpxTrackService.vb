Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text.RegularExpressions
Imports System.Xml
Imports System.Xml.Linq

Namespace Services

    ''' <summary>Ein Punkt einer Aufzeichnung: Zeit, Koordinate und - wenn das Geraet sie
    ''' mitschreibt - die Hoehe.</summary>
    Public NotInheritable Class GpxTrackPoint
        ''' <summary>Immer UTC. Eine GPX-Datei traegt die Zeit in UTC; steht ausnahmsweise keine
        ''' Zeitzone dabei, wird sie als UTC gelesen (siehe <see cref="GpxTrackService.Load"/>).</summary>
        Public Property TimeUtc As DateTime
        Public Property Latitude As Double
        Public Property Longitude As Double
        ''' <summary>Meter ueber dem Bezugsellipsoid, oder Nothing. Viele Aufzeichnungen haben
        ''' keine Hoehe, und eine erfundene waere schlechter als gar keine.</summary>
        Public Property ElevationMeters As Double?
    End Class

    ''' <summary>Eine geladene Aufzeichnung: die Punkte MIT Zeit, nach Zeit sortiert.
    '''
    ''' Punkte ohne Zeit fallen beim Laden weg. Sie liessen sich keinem Bild zuordnen - der
    ''' Abgleich laeuft ueber nichts anderes als die Uhrzeit.</summary>
    Public NotInheritable Class GpxTrack
        Public Property SourcePath As String = ""
        ''' <summary>Der Name aus der Datei, wenn einer dasteht. Sonst leer; der Aufrufer zeigt
        ''' dann den Dateinamen.</summary>
        Public Property Name As String = ""
        Public ReadOnly Property Points As New List(Of GpxTrackPoint)()

        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return Points.Count = 0
            End Get
        End Property

        Public ReadOnly Property StartUtc As DateTime
            Get
                Return If(Points.Count = 0, DateTime.MinValue, Points(0).TimeUtc)
            End Get
        End Property

        Public ReadOnly Property EndUtc As DateTime
            Get
                Return If(Points.Count = 0, DateTime.MinValue, Points(Points.Count - 1).TimeUtc)
            End Get
        End Property
    End Class

    ''' <summary>Was fuer ein Bild herauskommt: die Stelle, an der die Aufzeichnung zu seiner
    ''' Aufnahmezeit war.</summary>
    Public NotInheritable Class GpxMatch
        Public Property Latitude As Double
        Public Property Longitude As Double
        Public Property ElevationMeters As Double?
        ''' <summary>Sekunden bis zum naechsten wirklich aufgezeichneten Punkt. Null heisst, dass
        ''' die Aufnahmezeit genau auf einen Punkt faellt; alles darueber ist der Abstand, den die
        ''' Zwischenrechnung ueberbrueckt hat.</summary>
        Public Property GapSeconds As Double
    End Class

    ''' <summary>Liest eine GPX-Aufzeichnung und sagt, wo sie zu einer bestimmten Uhrzeit war.
    '''
    ''' WOZU: Wer mit einem Aufzeichnungsgeraet unterwegs war (Telefon, Uhr, Logger), hat den Weg
    ''' schon auf Sekunden genau vorliegen. Die Bilder danach von Hand Ort fuer Ort zu setzen, ist
    ''' Arbeit, die die Uhrzeit bereits erledigt hat.
    '''
    ''' DIE EINE FALLE IST DIE ZEITZONE. In der Aufzeichnung steht UTC, im Bild steht die
    ''' Aufnahmezeit OHNE Zeitzone - die Kamera schreibt ihre Ortszeit hin und verschweigt, welche
    ''' das war. Beides zusammenzubringen braucht deshalb einen Versatz, und den kann nur der
    ''' Nutzer bestaetigen. <see cref="SuggestOffset"/> schlaegt ihn vor, gesetzt wird er im Dialog.
    '''
    ''' GELESEN, NICHT GESCHRIEBEN: Der Dienst ruehrt keine Datei an. Was aus einem Treffer wird,
    ''' entscheidet <see cref="LibraryService.SetGpsCoordinatesForEach"/> - derselbe Weg, den auch
    ''' der von Hand gesetzte Aufnahmeort geht.</summary>
    Public NotInheritable Class GpxTrackService

        Private Sub New()
        End Sub

        ''' <summary>Weiter als eine halbe Erdumdrehung kann keine Kamerauhr danebenliegen, ohne
        ''' dass ein Vertipper im Spiel ist.</summary>
        Public Shared ReadOnly MaxOffset As TimeSpan = TimeSpan.FromHours(14)

        ''' <summary>Vorgabe fuer den hoechsten Abstand zum naechsten aufgezeichneten Punkt. Zehn
        ''' Minuten decken eine Aufzeichnung ab, die zwischendurch den Empfang verloren hat, und
        ''' lassen ein Bild von gestern trotzdem aussen vor.</summary>
        Public Const DefaultToleranceMinutes As Integer = 10

        ' ── Laden ───────────────────────────────────────────────────────────────

        ''' <summary>Liest eine GPX-Datei. Nothing, wenn sie sich nicht lesen laesst; eine Datei
        ''' ohne brauchbare Punkte kommt als leere Aufzeichnung zurueck - der Unterschied zwischen
        ''' "kaputt" und "leer" gehoert dem Nutzer gesagt.
        '''
        ''' STROMWEISE gelesen und nicht als Baum: Eine Aufzeichnung ueber mehrere Tage im
        ''' Sekundentakt hat Hunderttausende Punkte, und ein Baum davon belegt ein Vielfaches der
        ''' Datei. Hier steht immer nur EIN Punkt im Speicher.
        '''
        ''' Der Namensraum wird nicht geprueft: GPX 1.0 und 1.1 tragen verschiedene, und Werkzeuge
        ''' haengen eigene Erweiterungen an. Gesucht wird deshalb nach dem lokalen Namen - trkpt,
        ''' rtept und wpt gleichermassen, weil manche Geraete den Weg als Route ablegen.</summary>
        Public Shared Function Load(path As String) As GpxTrack
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return Nothing

            Dim track As New GpxTrack With {.SourcePath = path}
            Try
                Dim settings As New XmlReaderSettings With {
                    .DtdProcessing = DtdProcessing.Prohibit,
                    .IgnoreWhitespace = True,
                    .IgnoreComments = True,
                    .XmlResolver = Nothing}
                Using reader = XmlReader.Create(path, settings)
                    ' KEIN While reader.Read() um das Ganze: ReadFrom und ReadElementContentAsString
                    ' ruecken den Leser SELBST auf den naechsten Knoten vor. Ein Read() danach
                    ' uebersprang den - von zwei aufeinanderfolgenden Punkten kam nur jeder zweite an.
                    reader.Read()
                    While Not reader.EOF
                        If reader.NodeType <> XmlNodeType.Element Then
                            reader.Read()
                            Continue While
                        End If
                        Select Case reader.LocalName
                            Case "trkpt", "rtept", "wpt"
                                Dim element = TryCast(XNode.ReadFrom(reader), XElement)
                                Dim point = ParsePoint(element)
                                If point IsNot Nothing Then track.Points.Add(point)
                            Case "name"
                                ' Der erste Name in der Datei: der des Weges, wenn einer dasteht.
                                ' Spaetere Namen gehoeren einzelnen Punkten und sagen nichts ueber
                                ' die Aufzeichnung als Ganzes.
                                Dim text = If(reader.ReadElementContentAsString(), "").Trim()
                                If track.Name.Length = 0 Then track.Name = text
                            Case Else
                                ' In die Tiefe: die Punkte stecken unter trk und trkseg.
                                reader.Read()
                        End Select
                    End While
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Gpx.Load", ex)
                Return Nothing
            End Try

            ' Nach Zeit sortiert, weil die Suche danach halbiert. Eine Datei mit mehreren Segmenten
            ' oder mehreren Tracks liegt nicht zwangslaeufig in zeitlicher Reihenfolge vor.
            track.Points.Sort(Function(a, b) a.TimeUtc.CompareTo(b.TimeUtc))
            Return track
        End Function

        ''' <summary>Ein Punkt aus dem Element, oder Nothing. Ohne Koordinate oder ohne Zeit ist er
        ''' fuer den Abgleich wertlos.</summary>
        Private Shared Function ParsePoint(element As XElement) As GpxTrackPoint
            If element Is Nothing Then Return Nothing

            Dim latitude As Double, longitude As Double
            If Not TryReadDouble(AttributeValue(element, "lat"), latitude) Then Return Nothing
            If Not TryReadDouble(AttributeValue(element, "lon"), longitude) Then Return Nothing
            If Not GeotagService.IsValidCoordinate(latitude, longitude) Then Return Nothing

            Dim timeUtc As DateTime
            If Not TryReadTime(ChildValue(element, "time"), timeUtc) Then Return Nothing

            Dim point As New GpxTrackPoint With {
                .Latitude = latitude,
                .Longitude = longitude,
                .TimeUtc = timeUtc}

            Dim elevation As Double
            If TryReadDouble(ChildValue(element, "ele"), elevation) AndAlso
               Not Double.IsNaN(elevation) AndAlso Not Double.IsInfinity(elevation) Then
                point.ElevationMeters = elevation
            End If
            Return point
        End Function

        ''' <summary>Ein Attribut ohne Ruecksicht auf den Namensraum. lat und lon stehen in GPX
        ''' immer ohne Praefix da, aber eine Datei aus fremder Hand haelt sich nicht immer daran.</summary>
        Private Shared Function AttributeValue(element As XElement, localName As String) As String
            Dim attribute = element.Attributes().FirstOrDefault(
                Function(a) String.Equals(a.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            Return If(attribute Is Nothing, "", attribute.Value)
        End Function

        Private Shared Function ChildValue(element As XElement, localName As String) As String
            Dim child = element.Elements().FirstOrDefault(
                Function(e) String.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            Return If(child Is Nothing, "", child.Value)
        End Function

        ''' <summary>Zahlen stehen in GPX immer mit Punkt. Eine deutsche Zahl mit Komma kaeme aus
        ''' einer von Hand zusammengebauten Datei - sie wird mitgelesen, statt den Punkt
        ''' wegzuwerfen.</summary>
        Private Shared Function TryReadDouble(text As String, ByRef value As Double) As Boolean
            value = 0
            If String.IsNullOrWhiteSpace(text) Then Return False
            Return Double.TryParse(text.Trim().Replace(","c, "."c), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, value)
        End Function

        ''' <summary>Die Zeit eines Punktes, immer als UTC. Steht keine Zeitzone dabei, gilt UTC -
        ''' so schreibt es die GPX-Spezifikation vor, und eine Ortszeit ohne Angabe waere ohnehin
        ''' nicht aufzuloesen.</summary>
        Private Shared Function TryReadTime(text As String, ByRef value As DateTime) As Boolean
            value = DateTime.MinValue
            If String.IsNullOrWhiteSpace(text) Then Return False
            Dim parsed As DateTime
            If Not DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal Or DateTimeStyles.AssumeUniversal,
                                     parsed) Then Return False
            value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            Return True
        End Function

        ' ── Abgleich ────────────────────────────────────────────────────────────

        ''' <summary>Wo die Aufzeichnung zur Aufnahmezeit dieses Bildes war. Nothing heisst: zu
        ''' dieser Zeit sagt sie nichts - das Bild bleibt ohne Ort.</summary>
        ''' <param name="captureLocal">Die Aufnahmezeit, wie sie im Bild steht: Ortszeit der
        ''' Kamera, ohne Zeitzone.</param>
        ''' <param name="cameraOffset">Wie weit die Kamerauhr der UTC voraus ist. Fuer
        ''' Mitteleuropa im Sommer also +2:00.</param>
        ''' <param name="tolerance">Wie weit die Aufnahmezeit vom naechsten aufgezeichneten Punkt
        ''' entfernt sein darf. Daran entscheidet sich, was am Anfang und am Ende der Aufzeichnung
        ''' noch dazugehoert und was nicht mehr.</param>
        Public Shared Function Match(track As GpxTrack, captureLocal As DateTime,
                                     cameraOffset As TimeSpan, tolerance As TimeSpan) As GpxMatch
            If track Is Nothing OrElse track.IsEmpty Then Return Nothing
            If tolerance < TimeSpan.Zero Then Return Nothing

            Dim wanted As DateTime
            Try
                wanted = DateTime.SpecifyKind(captureLocal, DateTimeKind.Utc) - cameraOffset
            Catch ex As ArgumentOutOfRangeException
                Return Nothing
            End Try

            Dim points = track.Points
            Dim index = LowerBound(points, wanted)

            ' Genau auf einem Punkt: nichts zu rechnen.
            If index < points.Count AndAlso points(index).TimeUtc = wanted Then
                Return New GpxMatch With {
                    .Latitude = points(index).Latitude,
                    .Longitude = points(index).Longitude,
                    .ElevationMeters = points(index).ElevationMeters,
                    .GapSeconds = 0}
            End If

            ' Vor dem ersten oder nach dem letzten Punkt: nicht rechnen, sondern den Randpunkt
            ' nehmen - und nur, wenn er nahe genug liegt. Eine Aufnahme von gestern soll nicht den
            ' ersten Punkt der heutigen Aufzeichnung bekommen.
            If index = 0 Then Return EdgeMatch(points(0), wanted, tolerance)
            If index >= points.Count Then Return EdgeMatch(points(points.Count - 1), wanted, tolerance)

            Dim before = points(index - 1)
            Dim ahead = points(index)
            Dim toBefore = (wanted - before.TimeUtc).TotalSeconds
            Dim toAhead = (ahead.TimeUtc - wanted).TotalSeconds
            Dim gap = Math.Min(toBefore, toAhead)
            If gap > tolerance.TotalSeconds Then Return Nothing

            Dim span = (ahead.TimeUtc - before.TimeUtc).TotalSeconds
            Dim fraction = If(span <= 0, 0.0, toBefore / span)
            Return New GpxMatch With {
                .Latitude = before.Latitude + (ahead.Latitude - before.Latitude) * fraction,
                .Longitude = InterpolateLongitude(before.Longitude, ahead.Longitude, fraction),
                .ElevationMeters = InterpolateElevation(before, ahead, fraction),
                .GapSeconds = gap}
        End Function

        ''' <summary>Der erste oder letzte Punkt, wenn die Aufnahmezeit nah genug daran liegt.</summary>
        Private Shared Function EdgeMatch(point As GpxTrackPoint, wanted As DateTime, tolerance As TimeSpan) As GpxMatch
            Dim gap = Math.Abs((wanted - point.TimeUtc).TotalSeconds)
            If gap > tolerance.TotalSeconds Then Return Nothing
            Return New GpxMatch With {
                .Latitude = point.Latitude,
                .Longitude = point.Longitude,
                .ElevationMeters = point.ElevationMeters,
                .GapSeconds = gap}
        End Function

        ''' <summary>Der erste Punkt, der nicht vor <paramref name="wanted"/> liegt. Halbierende
        ''' Suche, weil der Abgleich bei einem Stapel Bilder ueber Hunderttausende Punkte laeuft.</summary>
        Private Shared Function LowerBound(points As List(Of GpxTrackPoint), wanted As DateTime) As Integer
            Dim low = 0, high = points.Count
            While low < high
                Dim middle = low + (high - low) \ 2
                If points(middle).TimeUtc < wanted Then
                    low = middle + 1
                Else
                    high = middle
                End If
            End While
            Return low
        End Function

        ''' <summary>Laengengrade ueber den 180. Meridian hinweg. Ohne die Fallunterscheidung
        ''' faehrt die Zwischenrechnung zwischen 179 und -179 einmal um die halbe Erde.</summary>
        Friend Shared Function InterpolateLongitude(fromValue As Double, toValue As Double, fraction As Double) As Double
            Dim delta = toValue - fromValue
            If delta > 180 Then
                delta -= 360
            ElseIf delta < -180 Then
                delta += 360
            End If
            Dim result = fromValue + delta * fraction
            If result > 180 Then result -= 360
            If result < -180 Then result += 360
            Return result
        End Function

        ''' <summary>Die Hoehe zwischen zwei Punkten. Hat nur einer eine, gilt dessen Wert -
        ''' zwischen "300 Meter" und "keine Angabe" gibt es nichts zu mitteln.</summary>
        Private Shared Function InterpolateElevation(before As GpxTrackPoint, ahead As GpxTrackPoint,
                                                     fraction As Double) As Double?
            If before.ElevationMeters.HasValue AndAlso ahead.ElevationMeters.HasValue Then
                Return before.ElevationMeters.Value +
                       (ahead.ElevationMeters.Value - before.ElevationMeters.Value) * fraction
            End If
            If before.ElevationMeters.HasValue Then Return before.ElevationMeters
            Return ahead.ElevationMeters
        End Function

        ''' <summary>Wie viele dieser Aufnahmezeiten mit diesem Versatz einen Ort bekaemen. Der
        ''' Dialog zeigt die Zahl, waehrend am Versatz gedreht wird - sie ist die einzige
        ''' Rueckmeldung, die vor dem Schreiben ueberhaupt moeglich ist.</summary>
        Public Shared Function CountMatches(track As GpxTrack, captureTimes As IEnumerable(Of DateTime),
                                            cameraOffset As TimeSpan, tolerance As TimeSpan) As Integer
            If track Is Nothing OrElse captureTimes Is Nothing Then Return 0
            Dim count = 0
            For Each captureTime In captureTimes
                If Match(track, captureTime, cameraOffset, tolerance) IsNot Nothing Then count += 1
            Next
            Return count
        End Function

        ''' <summary>Ein Vorschlag fuer den Zeitversatz.
        '''
        ''' ERSTE ANNAHME ist die Zeitzone DIESES Rechners zur Aufnahmezeit: die Kamera war
        ''' meistens dort eingestellt, wo sie stand, und die Bilder werden meistens dort angesehen,
        ''' wo sie entstanden sind. Trifft das zu, ist der Vorschlag richtig und der Nutzer muss
        ''' nichts tun.
        '''
        ''' TRIFFT ES NICHT ZU - eine Reise, eine falsch gestellte Uhr -, liegen die Aufnahmezeiten
        ''' ausserhalb der Aufzeichnung. Dann wird der Versatz genommen, der die Mitte der
        ''' Aufnahmen auf die Mitte der Aufzeichnung legt, auf eine Viertelstunde gerundet: jede
        ''' Zeitzone der Welt ist ein Vielfaches davon. Passt auch das nicht, gilt der ungerundete
        ''' Wert - dann stand die Kamerauhr schlicht falsch.
        '''
        ''' Der Vorschlag steht im Feld und nicht heimlich dahinter. Was geschrieben wird,
        ''' entscheidet der Nutzer.</summary>
        Public Shared Function SuggestOffset(track As GpxTrack, captureTimes As IReadOnlyList(Of DateTime),
                                             tolerance As TimeSpan) As TimeSpan
            Dim localOffset = TimeSpan.Zero
            If captureTimes IsNot Nothing AndAlso captureTimes.Count > 0 Then
                localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(captureTimes(0), DateTimeKind.Local))
            End If
            If track Is Nothing OrElse track.IsEmpty OrElse captureTimes Is Nothing OrElse captureTimes.Count = 0 Then
                Return localOffset
            End If
            If CountMatches(track, captureTimes, localOffset, tolerance) > 0 Then Return localOffset

            Dim sorted = captureTimes.OrderBy(Function(t) t).ToList()
            Dim captureMiddle = sorted(sorted.Count \ 2)
            Dim trackMiddle = track.StartUtc + TimeSpan.FromTicks((track.EndUtc - track.StartUtc).Ticks \ 2)
            Dim exact = captureMiddle - trackMiddle
            If exact > MaxOffset OrElse exact < -MaxOffset Then Return localOffset

            Dim quarters = CLng(Math.Round(exact.TotalMinutes / 15.0))
            Dim rounded = TimeSpan.FromMinutes(quarters * 15)
            If CountMatches(track, captureTimes, rounded, tolerance) > 0 Then Return rounded
            If CountMatches(track, captureTimes, exact, tolerance) > 0 Then Return exact
            Return localOffset
        End Function

        ' ── Der Zeitversatz als Text ────────────────────────────────────────────

        ''' Vorzeichen, Stunden, und optional Minuten und Sekunden. Ein Doppelpunkt trennt, wie auf
        ''' jeder Uhr; wer nur die Stunde tippt, meint die volle Stunde.
        Private Shared ReadOnly OffsetPattern As New Regex(
            "^\s*([+-])?\s*(\d{1,2})(?::(\d{1,2}))?(?::(\d{1,2}))?\s*$", RegexOptions.Compiled)

        ''' <summary>Liest "+2:00", "-1:30", "2" oder "0". False heisst: daran laesst sich nicht
        ''' rechnen, und der Knopf im Dialog bleibt gesperrt.</summary>
        Public Shared Function TryParseOffset(text As String, ByRef value As TimeSpan) As Boolean
            value = TimeSpan.Zero
            If String.IsNullOrWhiteSpace(text) Then Return False
            Dim hit = OffsetPattern.Match(text)
            If Not hit.Success Then Return False

            Dim hours = Integer.Parse(hit.Groups(2).Value, CultureInfo.InvariantCulture)
            Dim minutes = If(hit.Groups(3).Success, Integer.Parse(hit.Groups(3).Value, CultureInfo.InvariantCulture), 0)
            Dim seconds = If(hit.Groups(4).Success, Integer.Parse(hit.Groups(4).Value, CultureInfo.InvariantCulture), 0)
            If minutes > 59 OrElse seconds > 59 Then Return False

            Dim span = New TimeSpan(hours, minutes, seconds)
            If span > MaxOffset Then Return False
            If hit.Groups(1).Value = "-" Then span = span.Negate()
            value = span
            Return True
        End Function

        ''' <summary>Der Versatz als Text, wie ihn das Feld annimmt. Das Vorzeichen steht immer
        ''' da - auch das Plus: "2:00" liesse offen, ob die Kamerauhr vor- oder nachgeht.</summary>
        Public Shared Function FormatOffset(value As TimeSpan) As String
            Dim sign = If(value < TimeSpan.Zero, "-", "+")
            Dim absolute = If(value < TimeSpan.Zero, value.Negate(), value)
            If absolute.Seconds <> 0 Then
                Return String.Format(CultureInfo.InvariantCulture, "{0}{1}:{2:00}:{3:00}",
                                     sign, CInt(Math.Floor(absolute.TotalHours)), absolute.Minutes, absolute.Seconds)
            End If
            Return String.Format(CultureInfo.InvariantCulture, "{0}{1}:{2:00}",
                                 sign, CInt(Math.Floor(absolute.TotalHours)), absolute.Minutes)
        End Function

    End Class

End Namespace
