Imports System
Imports System.Collections.Generic
Imports Microsoft.Data.Sqlite

Namespace Services

    ''' <summary>Ein Ort mit der Anzahl Bilder, die dort aufgenommen wurden.</summary>
    Public Class PlaceEntry
        Public Property City As String = ""
        Public Property Country As String = ""

        ''' <summary>Das Laenderkuerzel - damit steht der Landesname in der Sprache der Oberflaeche
        ''' zur Verfuegung, ohne eine eigene Uebersetzungstabelle fuer 246 Laender.</summary>
        Public Property CountryCode As String = ""
        Public Property Count As Integer

        ''' <summary>"Norden, Deutschland (42)". Fehlt eines von beiden, faellt es samt Komma weg -
        ''' eine Zeile, die mit einem Komma beginnt, sieht nach einem Fehler aus.</summary>
        Public ReadOnly Property Label As String
            Get
                Return $"{PlaceText} ({Count})"
            End Get
        End Property

        Public ReadOnly Property PlaceText As String
            Get
                Dim land = PlaceLookupService.LocalizedCountry(CountryCode, Country)
                If City.Length > 0 AndAlso land.Length > 0 Then Return $"{City}, {land}"
                Return If(City.Length > 0, City, land)
            End Get
        End Property
    End Class

    ''' <summary>Der Aufnahmeort als fertige Zeile fuer die Infoleiste.
    '''
    ''' Eigene Stelle aus demselben Grund wie beim FacePanelService: Galerie, Betrachter und Editor
    ''' zeigen dieselbe Leiste, haben aber je ein eigenes ViewModel.</summary>
    Public NotInheritable Class PlacePanelService

        Private Sub New()
        End Sub

        Public Shared Function TextFor(filePath As String) As String
            If String.IsNullOrWhiteSpace(filePath) Then Return ""
            ' Ein Immich-Element liegt nicht im Dateisystem und steht in keinem lokalen Katalog.
            If ImmichService.IsImmichPseudoPath(filePath) Then Return ""
            Dim place = LibraryService.Instance.GetPlace(filePath)
            Dim land = PlaceLookupService.LocalizedCountry(place.CountryCode, place.Country)
            If place.City.Length > 0 AndAlso land.Length > 0 Then Return $"{place.City}, {land}"
            Return If(place.City.Length > 0, place.City, land)
        End Function

    End Class

    Partial Public Class LibraryService

        ''' <summary>Ort und Land zu einem Bild, aus dem Katalog.
        '''
        ''' Steht dort nichts, es liegen aber Koordinaten vor, wird EINMAL nachgeschlagen und das
        ''' Ergebnis zurueckgeschrieben. Der Grund ist der Bestand: Ort und Land sind erst spaeter
        ''' dazugekommen, und alles, was vorher eingelesen wurde, traegt sie nicht. Ohne dieses
        ''' Nachziehen bliebe der Ort bei jedem aelteren Bild leer, bis jemand den Ordner erneut
        ''' einliest - und niemand liest 13000 Bilder neu ein, um einen Ortsnamen zu sehen.</summary>
        Public Function GetPlace(filePath As String) As (City As String, Country As String, CountryCode As String)
            If String.IsNullOrWhiteSpace(filePath) Then Return ("", "", "")
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Dim city = ""
                    Dim country = ""
                    Dim code = ""
                    Dim latitude As Double? = Nothing
                    Dim longitude As Double? = Nothing
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT City, Country, GpsLatitude, GpsLongitude, CountryCode FROM ImageMeta WHERE FilePath=$p"
                        cmd.Parameters.AddWithValue("$p", filePath)
                        Using reader = cmd.ExecuteReader()
                            If Not reader.Read() Then Return ("", "", "")
                            city = If(reader.IsDBNull(0), "", reader.GetString(0))
                            country = If(reader.IsDBNull(1), "", reader.GetString(1))
                            If Not reader.IsDBNull(2) Then latitude = reader.GetDouble(2)
                            If Not reader.IsDBNull(3) Then longitude = reader.GetDouble(3)
                            code = If(reader.IsDBNull(4), "", reader.GetString(4))
                        End Using
                    End Using

                    ' Steht der Ort schon da, fehlt bei einem alten Eintrag nur das Kuerzel - und
                    ' damit die Uebersetzung des Landesnamens. Das Nachschlagen kostet eine Abfrage
                    ' und laesst den gespeicherten Ortsnamen unangetastet.
                    If city.Length > 0 AndAlso code.Length > 0 Then Return (city, country, code)
                    If Not latitude.HasValue OrElse Not longitude.HasValue Then Return (city, country, code)
                    If Not PlaceLookupService.Enabled Then Return (city, country, code)

                    Dim hit = PlaceLookupService.Nearest(latitude.Value, longitude.Value)
                    If hit Is Nothing Then Return (city, country, code)
                    Dim ort = If(city.Length > 0, city, hit.Name)
                    If country.Length = 0 Then country = hit.Country
                    WritePlace(conn, Nothing, filePath, ort, country, hit.CountryCode)
                    Return (ort, country, hit.CountryCode)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Library.GetPlace", ex)
                Return ("", "", "")
            End Try
        End Function

        ''' <summary>Jeder benutzte Ort mit der Anzahl Bilder. Fuer das Auswahlmenue der Galerie.</summary>
        Public Function GetPlaceCounts() As List(Of PlaceEntry)
            Dim result As New List(Of PlaceEntry)()
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText =
                            "SELECT City, COALESCE(Country,''), COUNT(*), COALESCE(CountryCode,'') FROM ImageMeta " &
                            "WHERE City IS NOT NULL AND City <> '' " &
                            "GROUP BY City, COALESCE(Country,''), COALESCE(CountryCode,'') ORDER BY City COLLATE NOCASE"
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                result.Add(New PlaceEntry With {
                                    .City = reader.GetString(0),
                                    .Country = reader.GetString(1),
                                    .Count = reader.GetInt32(2),
                                    .CountryCode = reader.GetString(3)})
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Library.GetPlaceCounts", ex)
            End Try
            Return result
        End Function

        ''' <summary>Alle Bildpfade zu diesen Orten. Ort ODER Land, genau wie die Suchbedingung sie
        ''' vergleicht - wer "Germany" waehlt, meint das Land.
        '''
        ''' Fuer die VORAUSWAHL des Suchdurchlaufs: ohne sie geht er ueber JEDEN Katalogeintrag und
        ''' fragt fuer jeden die Datei ab. Bei 13000 Eintraegen und einem Ort mit dreissig Bildern
        ''' hiess das minutenlang eine leere Galerie, weil die Treffer irgendwo weit hinten lagen.
        ''' Die Bedingung wird danach trotzdem ausgewertet - das hier waehlt nur vor.</summary>
        Public Function GetPathsForPlaces(cities As IReadOnlyList(Of String)) As List(Of String)
            Dim result As New List(Of String)()
            If cities Is Nothing OrElse cities.Count = 0 Then Return result
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        Dim names As New List(Of String)()
                        For i = 0 To cities.Count - 1
                            names.Add("$c" & i)
                            cmd.Parameters.AddWithValue("$c" & i, cities(i))
                        Next
                        Dim list = String.Join(",", names)
                        cmd.CommandText =
                            $"SELECT FilePath FROM ImageMeta WHERE City COLLATE NOCASE IN ({list}) " &
                            $"OR Country COLLATE NOCASE IN ({list})"
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                result.Add(reader.GetString(0))
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Library.GetPathsForPlaces", ex)
            End Try
            Return result
        End Function

        ''' <summary>Traegt Ort und Land fuer alle Eintraege nach, die Koordinaten haben und noch
        ''' keinen Namen.
        '''
        ''' Gemessen an Patricks Bestand: 13333 Eintraege, davon 4471 mit Koordinaten und NULL mit
        ''' Ortsnamen - die Spalten kamen erst spaeter dazu und werden nur beim Einlesen gefuellt.
        ''' Ohne diesen Lauf ist der Ortsfilter auf einem gewachsenen Bestand dauerhaft leer, und
        ''' genau so sah es auch aus.
        '''
        ''' EIN Schreibvorgang am Ende, nicht einer je Bild: 4471 einzelne Transaktionen waeren
        ''' 4471 Plattenzugriffe. Gelesen wird vorher in einem Rutsch, damit die Verbindung waehrend
        ''' des Nachschlagens nicht offen auf der Tabelle liegt.</summary>
        ''' <returns>Wie viele Eintraege einen Namen bekommen haben.</returns>
        Public Function FillMissingPlaces() As Integer
            If Not PlaceLookupService.Enabled Then Return 0
            Try
                Dim pending As New List(Of (Path As String, Latitude As Double, Longitude As Double,
                                            Country As String, City As String))()
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        ' Auch Eintraege, die einen Ort haben, aber noch kein Kuerzel: ihnen fehlt
                        ' nur die Uebersetzung des Landesnamens.
                        cmd.CommandText =
                            "SELECT FilePath, GpsLatitude, GpsLongitude, COALESCE(Country,''), COALESCE(City,'') FROM ImageMeta " &
                            "WHERE (City IS NULL OR City='' OR CountryCode IS NULL OR CountryCode='') " &
                            "AND GpsLatitude IS NOT NULL AND GpsLongitude IS NOT NULL"
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                pending.Add((reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2),
                                             reader.GetString(3), reader.GetString(4)))
                            End While
                        End Using
                    End Using
                End Using
                If pending.Count = 0 Then Return 0

                Dim found As New List(Of (Path As String, City As String, Country As String, Code As String))()
                For Each row In pending
                    Dim hit = PlaceLookupService.Nearest(row.Latitude, row.Longitude)
                    ' Kein Treffer heisst: kein Ort innerhalb der Grenze. Das Feld bleibt leer -
                    ' ein falscher Ortsname waere schlechter als keiner.
                    If hit Is Nothing Then Continue For
                    ' Ein VORHANDENER Ortsname bleibt stehen - er kann aus der Datei selbst stammen.
                    found.Add((row.Path,
                               If(row.City.Length > 0, row.City, hit.Name),
                               If(row.Country.Length > 0, row.Country, hit.Country),
                               hit.CountryCode))
                Next
                If found.Count = 0 Then Return 0

                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using tx = conn.BeginTransaction()
                        For Each row In found
                            WritePlace(conn, tx, row.Path, row.City, row.Country, row.Code)
                        Next
                        tx.Commit()
                    End Using
                End Using
                Return found.Count
            Catch ex As Exception
                DiagnosticLogService.LogException("Library.FillMissingPlaces", ex)
                Return 0
            End Try
        End Function

        Private Shared Sub WritePlace(conn As SqliteConnection, tx As SqliteTransaction,
                                      filePath As String, city As String, country As String,
                                      countryCode As String)
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = "UPDATE ImageMeta SET City=$c, Country=$l, CountryCode=$k WHERE FilePath=$p"
                cmd.Parameters.AddWithValue("$c", If(city, ""))
                cmd.Parameters.AddWithValue("$l", If(country, ""))
                cmd.Parameters.AddWithValue("$k", If(countryCode, ""))
                cmd.Parameters.AddWithValue("$p", filePath)
                cmd.ExecuteNonQuery()
            End Using
        End Sub

    End Class

End Namespace
