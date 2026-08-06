Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports Microsoft.Data.Sqlite

Namespace Services

    ''' <summary>Ein Ort mit Land und der Entfernung zum gesuchten Punkt.</summary>
    Public Class PlaceHit
        Public Property Name As String = ""
        Public Property Country As String = ""

        ''' <summary>Das Laenderkuerzel nach ISO 3166 ("DE", "FR"). Es steht in der Ortstabelle und
        ''' ist der Schluessel zur UEBERSETZUNG: .NET kennt zu jedem Kuerzel den Landesnamen in jeder
        ''' Sprache, eine eigene Uebersetzungstabelle fuer 246 Laender braucht es damit nicht.</summary>
        Public Property CountryCode As String = ""

        Public Property DistanceKm As Double
    End Class

    ''' <summary>Ortsnamen zu Koordinaten, ausschliesslich vom eigenen Geraet.
    '''
    ''' WARUM NICHT UEBER EINEN DIENST IM NETZ: Der naheliegende Weg waere eine Abfrage bei einem
    ''' Geokodierer. Er scheidet aus zwei Gruenden aus. Erstens verbieten die Nutzungsbedingungen des
    ''' offenen Dienstes systematische Abfragen ausdruecklich - genau das waere ein Durchlauf ueber
    ''' einen Fotobestand - und lassen hoechstens eine Anfrage je Sekunde zu; fuer 25000 Bilder waeren
    ''' das sieben Stunden. Zweitens, und wichtiger: jede Abfrage traegt einen Aufnahmeort nach
    ''' draussen. Bei einer Anwendung, die sonst nichts sendet, waere das ein stiller Bruch mit dem,
    ''' was sie zusagt.
    '''
    ''' Stattdessen eine Tabelle von 170540 Orten neben der Anwendung (12,6 MB, ueber denselben Knopf
    ''' wie die Modelle). Fuer die Frage "wie heisst es hier" reicht das: gebraucht wird ein Ortsname,
    ''' keine Adresse.
    '''
    ''' Die Suche geht ueber ein KAESTCHEN um den Punkt und rechnet den echten Abstand nur darin.
    ''' Ohne das waere jede Abfrage ein Durchlauf durch alle 170540 Zeilen. Das Kaestchen waechst in
    ''' Stufen, bis etwas darin liegt - in bewohnten Gegenden reicht die erste, auf dem Meer greift
    ''' keine.</summary>
    Public NotInheritable Class PlaceLookupService

        Private Sub New()
        End Sub

        Public Const PlaceFileKey As String = "orte"

        ''' <summary>Weiter entfernte Orte gelten NICHT mehr als Aufnahmeort.
        '''
        ''' Ohne diese Grenze bekaeme jedes Foto einen Namen, auch eines mitten auf dem Atlantik -
        ''' gemessen war der naechste Ort dort 1343 km entfernt. Ein falscher Ortsname ist schlechter
        ''' als gar keiner: er landet im Filter, in der Suche und spaeter womoeglich in den
        ''' Metadaten. 50 km decken auch abgelegene Aufnahmestellen ab, ohne ins Beliebige zu
        ''' geraten.</summary>
        Public Const MaxDistanceKm As Double = 50.0

        ''' <summary>Steht die Funktion zur Verfuegung? Nur mit der Ortstabelle.</summary>
        Public Shared ReadOnly Property Available As Boolean
            Get
                Return Not String.IsNullOrEmpty(AiModelService.BestFile(PlaceFileKey))
            End Get
        End Property

        ''' <summary>Darf sie auch benutzt werden? Zusaetzlich zur vorhandenen Tabelle muss der
        ''' Benutzer sie eingeschaltet haben - dieselbe Regel wie bei der Personenerkennung. Ohne
        ''' diese Abfrage waere der Schalter in den Einstellungen ohne Wirkung, und die Tabelle
        ''' fuellte Ortsnamen, sobald sie irgendwann einmal geladen wurde.</summary>
        Public Shared ReadOnly Property Enabled As Boolean
            Get
                Return Available AndAlso AppSettingsService.Load().PhotoMapEnabled
            End Get
        End Property

        Private Shared _connectionString As String = Nothing
        Private Shared ReadOnly _lock As New Object()

        Private Shared Function ConnectionString() As String
            SyncLock _lock
                If _connectionString IsNot Nothing Then Return _connectionString
                Dim file = AiModelService.BestFile(PlaceFileKey)
                If String.IsNullOrEmpty(file) Then Return Nothing
                Dim path = AiModelService.ModelPath(file)
                If String.IsNullOrEmpty(path) Then Return Nothing
                ' Nur lesen: die Tabelle ist Beigabe, keine Ablage. Ein versehentliches Schreiben
                ' wuerde ihre Pruefsumme ungueltig machen.
                _connectionString = $"Data Source={path};Mode=ReadOnly"
                Return _connectionString
            End SyncLock
        End Function

        ''' <summary>Der Landesname in der Sprache der Oberflaeche.
        '''
        ''' .NET kennt zu jedem ISO-Kuerzel den Landesnamen in jeder Sprache - eine eigene
        ''' Uebersetzungstabelle fuer 246 Laender waere Pflegearbeit ohne Gegenwert und in sieben
        ''' Sprachen ein Fehlerherd. Der englische Name aus der Ortstabelle bleibt der Rueckfall:
        ''' ohne Kuerzel, bei einem unbekannten Kuerzel und dort, wo das System keinen Namen kennt.
        '''
        ''' Gefragt wird die Sprache der ANWENDUNG, nicht die des Systems: wer FerrumPix auf Deutsch
        ''' stellt, will "Deutschland" lesen, auch wenn das Betriebssystem englisch laeuft.</summary>
        Public Shared Function LocalizedCountry(countryCode As String, fallback As String) As String
            Dim code = If(countryCode, "").Trim()
            If code.Length <> 2 Then Return If(fallback, "")
            Try
                Dim region As New Globalization.RegionInfo(code.ToUpperInvariant())
                Dim name = region.DisplayName
                Return If(String.IsNullOrWhiteSpace(name), If(fallback, ""), name)
            Catch
                ' Ein Kuerzel, das dieses System nicht kennt - dann der Name aus der Tabelle.
                Return If(fallback, "")
            End Try
        End Function

        Private Shared _codeByCountryName As Dictionary(Of String, String)

        ''' <summary>Das ISO-Kuerzel zu einem ENGLISCHEN Landesnamen ("Germany" -> "DE").
        '''
        ''' Fuer Ortsnamen, die von aussen kommen: ein Immich-Server benennt den Aufnahmeort selbst,
        ''' schickt aber nur den englischen Landesnamen und kein Kuerzel. Ueber den Umweg steht auch
        ''' dort der Landesname in der Sprache der Oberflaeche, statt als einziger Eintrag der Leiste
        ''' englisch dazustehen.
        '''
        ''' Gebaut wird die Zuordnung EINMAL aus den Regionen, die .NET kennt - die Ortstabelle
        ''' taugt dafuer nicht: sie ist Beigabe und fehlt womoeglich, waehrend der Serverbestand
        ''' trotzdem Orte hat. Ein unbekannter Name gibt einen leeren Text, und der Aufrufer bleibt
        ''' beim Namen, den er hat.</summary>
        Public Shared Function CountryCodeForName(countryName As String) As String
            Dim wanted = If(countryName, "").Trim()
            If wanted.Length = 0 Then Return ""
            SyncLock _lock
                If _codeByCountryName Is Nothing Then
                    Dim map As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                    Try
                        For Each culture In CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                            Try
                                Dim region As New RegionInfo(culture.Name)
                                Dim code = region.TwoLetterISORegionName
                                If code.Length <> 2 Then Continue For
                                map(region.EnglishName) = code
                                map(region.NativeName) = code
                            Catch
                                ' Eine Kultur ohne Region (z.B. "eo") - die naechste.
                            End Try
                        Next
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Orte.Laendernamen", ex)
                    End Try
                    _codeByCountryName = map
                End If
                Dim found As String = Nothing
                If _codeByCountryName.TryGetValue(wanted, found) Then Return found
            End SyncLock
            Return ""
        End Function

        ''' <summary>Der naechstgelegene Ort, oder Nothing, wenn keiner nah genug liegt.</summary>
        Public Shared Function Nearest(latitude As Double, longitude As Double) As PlaceHit
            If Double.IsNaN(latitude) OrElse Double.IsNaN(longitude) Then Return Nothing
            If latitude < -90 OrElse latitude > 90 OrElse longitude < -180 OrElse longitude > 180 Then Return Nothing
            Dim cs = ConnectionString()
            If cs Is Nothing Then Return Nothing

            Try
                Using conn = New SqliteConnection(cs)
                    conn.Open()
                    ' In Stufen weiten. Die erste deckt bewohnte Gegenden ab; die letzte reicht
                    ' knapp ueber die Hoechstentfernung hinaus, damit ein Ort am Rand nicht durch
                    ' das Kaestchen faellt.
                    For Each boxDegrees In New Double() {0.15, 0.5, 1.0}
                        Dim hit = NearestInBox(conn, latitude, longitude, boxDegrees)
                        If hit IsNot Nothing Then
                            If hit.DistanceKm > MaxDistanceKm Then Return Nothing
                            Return hit
                        End If
                    Next
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Orte.Nachschlagen", ex)
            End Try
            Return Nothing
        End Function

        Private Shared Function NearestInBox(conn As SqliteConnection, lat As Double, lon As Double,
                                             boxDegrees As Double) As PlaceHit
            ' Ein Grad Laenge ist am Aequator so lang wie ein Grad Breite und schrumpft zu den Polen
            ' hin auf null. Ohne diese Korrektur waere das Kaestchen in Nordeuropa viel zu schmal.
            Dim cosLat = Math.Max(0.05, Math.Cos(lat * Math.PI / 180.0))
            Dim lonSpan = boxDegrees / cosLat

            Using cmd = conn.CreateCommand()
                ' DIE DATUMSGRENZE. Die Laenge laeuft bei 180 Grad auf minus 180 um. Ein Foto bei
                ' 179,9 Grad bekam mit einem einzigen Bereich ein Kaestchen von 179,7 bis 180,1 - das
                ' liegt zur Haelfte ausserhalb jedes gespeicherten Wertes, und der Ort bei minus
                ' 179,9 Grad, keine 20 km entfernt, fiel heraus. Ueberschreitet das Kaestchen die
                ' Grenze, wird es deshalb in ZWEI Bereiche zerlegt, einen an jedem Ende.
                ' Die Entfernungsrechnung selbst kommt damit zurecht: sie geht ueber den Sinus der
                ' halben Differenz, und der ist ueber die Grenze hinweg richtig.
                Dim lonMin = lon - lonSpan
                Dim lonMax = lon + lonSpan
                Dim lonCondition As String
                If lonSpan >= 180.0 Then
                    ' Das Kaestchen umspannt die ganze Erde - dann gibt es in der Laenge nichts mehr
                    ' einzugrenzen.
                    lonCondition = "Lon BETWEEN -180 AND 180"
                ElseIf lonMax > 180.0 Then
                    lonCondition = "(Lon BETWEEN $lonMin AND 180 OR Lon BETWEEN -180 AND $lonMax)"
                    cmd.Parameters.AddWithValue("$lonMin", lonMin)
                    cmd.Parameters.AddWithValue("$lonMax", lonMax - 360.0)
                ElseIf lonMin < -180.0 Then
                    lonCondition = "(Lon BETWEEN $lonMin AND 180 OR Lon BETWEEN -180 AND $lonMax)"
                    cmd.Parameters.AddWithValue("$lonMin", lonMin + 360.0)
                    cmd.Parameters.AddWithValue("$lonMax", lonMax)
                Else
                    lonCondition = "Lon BETWEEN $lonMin AND $lonMax"
                    cmd.Parameters.AddWithValue("$lonMin", lonMin)
                    cmd.Parameters.AddWithValue("$lonMax", lonMax)
                End If

                cmd.CommandText =
                    "SELECT Name, Country, Lat, Lon, CountryCode FROM Place " &
                    "WHERE Lat BETWEEN $latMin AND $latMax AND " & lonCondition
                cmd.Parameters.AddWithValue("$latMin", lat - boxDegrees)
                cmd.Parameters.AddWithValue("$latMax", lat + boxDegrees)

                Dim best As PlaceHit = Nothing
                Dim bestKm As Double = Double.MaxValue
                Using reader = cmd.ExecuteReader()
                    While reader.Read()
                        Dim pLat = reader.GetDouble(2)
                        Dim pLon = reader.GetDouble(3)
                        Dim km = DistanceKm(lat, lon, pLat, pLon)
                        If km < bestKm Then
                            bestKm = km
                            best = New PlaceHit With {
                                .Name = reader.GetString(0),
                                .Country = reader.GetString(1),
                                .CountryCode = If(reader.IsDBNull(4), "", reader.GetString(4)),
                                .DistanceKm = km}
                        End If
                    End While
                End Using
                Return best
            End Using
        End Function

        ''' <summary>Abstand zweier Punkte auf der Kugel. Die einfache ebene Naeherung reicht hier
        ''' nicht: sie liegt in Nordeuropa deutlich daneben, und der Abstand entscheidet ueber die
        ''' Hoechstentfernung.</summary>
        Private Shared Function DistanceKm(lat1 As Double, lon1 As Double,
                                           lat2 As Double, lon2 As Double) As Double
            Const EarthRadiusKm As Double = 6371.0
            Dim dLat = (lat2 - lat1) * Math.PI / 180.0
            Dim dLon = (lon2 - lon1) * Math.PI / 180.0
            Dim a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2)
            Return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a))
        End Function

    End Class

End Namespace
