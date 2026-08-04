Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports Microsoft.Data.Sqlite

Namespace Services

    ''' <summary>Eine Person mit der Anzahl Bilder, in denen sie vorkommt.</summary>
    Public Class PersonEntry
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property ImageCount As Integer

        ''' <summary>Die Sammelgruppe fuer herausgeloeste Gesichter. Sie ist KEINE Person, und das
        ''' hat Folgen: sie zieht nichts an (siehe LoadPersonCentroidsLocked) und nimmt keinen Namen
        ''' an (siehe ApplyPersonName).</summary>
        Public Property IsUnknownBin As Boolean
        ''' <summary>Eine Person ohne Namen ist der Normalfall direkt nach der Erkennung: die
        ''' Gruppe steht, der Name kommt vom Benutzer und oft erst viel spaeter.</summary>
        Public ReadOnly Property IsNamed As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(Name)
            End Get
        End Property
    End Class

    Partial Public Class LibraryService

        ''' <summary>Ab wann zwei Gesichter als dieselbe Person gelten. Liegt im Erkennungsdienst,
        ''' damit Schwelle und Messung an einer Stelle stehen.
        '''
        ''' BEI JEDEM ZUGRIFF gelesen und nicht einmal gemerkt: sie haengt am Vergleichsmodell, und
        ''' das kann zur Laufzeit dazukommen - wer die grosse Datei erst waehrend der Sitzung holt,
        ''' rechnete sonst mit der Schwelle des kleinen weiter.</summary>
        Private Shared ReadOnly Property SameThreshold As Double
            Get
                Return FaceDetectionService.SamePersonThreshold
            End Get
        End Property

        ' VERWORFEN: ein Mindestabstand zur zweitbesten Gruppe. Der Gedanke war, einen Muenzwurf
        ' zwischen zwei fast gleich guten Gruppen zu vermeiden. Gemessen hat er das Gegenteil
        ' bewirkt: zwei Gruppen mit fast gleicher Mitte - und genau die entstehen, wenn dieselbe
        ' Person einmal geteilt wurde - machen JEDE weitere Aufnahme zum Grenzfall, und es entsteht
        ' eine dritte, vierte, fuenfte Gruppe. Fuer den Nutzen gibt es keine Messung, fuer den
        ' Schaden eine.

        ' ── Was muss ueberhaupt gescannt werden ──────────────────────────────────

        ''' <summary>Braucht dieses Bild einen Durchlauf? Nur, wenn es noch nie gescannt wurde oder
        ''' sich seither geaendert hat.
        '''
        ''' Auch ein Bild OHNE Gesichter wird als gescannt vermerkt. Ohne das wuerde jeder weitere
        ''' Lauf ueber einen Landschaftsordner alles erneut durchsuchen - und das sind gerade die
        ''' Ordner, in denen nie etwas gefunden wird.</summary>
        Public Function NeedsFaceScan(filePath As String, sourceModifiedAt As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT SourceModifiedAt FROM ScannedImage WHERE FilePath=$p"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    Dim r = cmd.ExecuteScalar()
                    If r Is Nothing OrElse TypeOf r Is DBNull Then Return True
                    Return Not String.Equals(CStr(r), If(sourceModifiedAt, ""), StringComparison.Ordinal)
                End Using
            End Using
        End Function

        ''' <summary>Traegt das Ergebnis eines Durchlaufs ein: alte Gesichter dieses Bildes weg, neue
        ''' hin, und jedes neue Gesicht einer Person zuordnen.
        '''
        ''' Die alten Gesichter werden GELOESCHT und nicht ergaenzt - ein zweiter Lauf ueber ein
        ''' geaendertes Bild soll nicht dieselbe Person doppelt eintragen. Personen bleiben dabei
        ''' bestehen, auch wenn sie dadurch vorruebergehend ohne Gesicht dastehen; ihr Name ist
        ''' Handarbeit des Benutzers und darf nicht an einem Neu-Scan haengen.</summary>
        Public Sub SaveFaces(filePath As String, sourceModifiedAt As String,
                             faces As IReadOnlyList(Of DetectedFace))
            If String.IsNullOrWhiteSpace(filePath) Then Return

            ' Die Mitten werden waehrend der Transaktion FORTGESCHRIEBEN, damit jedes weitere
            ' Gesicht dieses Bildes gegen den aktuellen Stand vergleicht. Kommt die Transaktion nicht
            ' durch, ist die Datenbank unveraendert und der Speicher waere die einzige Stelle, an der
            ' die Aenderung noch stuende - deshalb fliegt er dann weg und wird neu gelesen.
            Dim committed = False
            Try
                SaveFacesCore(filePath, sourceModifiedAt, faces, committed)
            Finally
                If Not committed Then InvalidateCentroids()
            End Try
        End Sub

        ''' <summary>Der Rumpf von <see cref="SaveFaces"/>. Eigene Methode, damit das Absichern der
        ''' Gruppenmitten oben in zwei Zeilen steht, statt die ganze Transaktion eine Ebene tiefer zu
        ''' schieben.</summary>
        ''' <param name="committed">Wird auf True gesetzt, sobald die Transaktion durch ist.</param>
        Private Sub SaveFacesCore(filePath As String, sourceModifiedAt As String,
                                  faces As IReadOnlyList(Of DetectedFace), ByRef committed As Boolean)
            Dim stamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)

            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using tx = conn.BeginTransaction()
                    ' HANDARBEIT BLEIBT STEHEN. Wer einen Namen eingetragen oder eine
                    ' Fehlzuordnung geloest hat, hat damit entschieden - ein erneuter Durchlauf
                    ' darf das nicht ueberschreiben. Diese Gesichter werden weder geloescht noch
                    ' neu angelegt; ein neuer Fund, der auf demselben Fleck liegt, faellt weg.
                    Dim manual As New List(Of (X As Double, Y As Double, W As Double, H As Double))()
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "SELECT X, Y, Width, Height FROM Face WHERE FilePath=$p AND IsManual=1"
                        cmd.Parameters.AddWithValue("$p", filePath)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                manual.Add((reader.GetDouble(0), reader.GetDouble(1),
                                            reader.GetDouble(2), reader.GetDouble(3)))
                            End While
                        End Using
                    End Using

                    ' Was gleich geloescht wird, faellt aus den Gruppenmitten heraus - sonst zoege
                    ' ein erneuter Durchlauf gegen Mitten, in denen die eigenen alten Gesichter
                    ' noch stecken. Gelesen VOR dem Loeschen, angewandt NACH dem Commit.
                    Dim removedFromCentroids As New List(Of (PersonId As String, Vector As Single()))()
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "SELECT f.PersonId, f.Embedding FROM Face f " &
                                          "JOIN Person p ON p.Id = f.PersonId " &
                                          "WHERE f.FilePath=$p AND f.IsManual=0 " &
                                          "AND f.Embedding IS NOT NULL AND p.IsUnknownBin=0"
                        cmd.Parameters.AddWithValue("$p", filePath)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                removedFromCentroids.Add((reader.GetString(0),
                                                          FromBlob(CType(reader.GetValue(1), Byte()))))
                            End While
                        End Using
                    End Using

                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM Face WHERE FilePath=$p AND IsManual=0"
                        cmd.Parameters.AddWithValue("$p", filePath)
                        cmd.ExecuteNonQuery()
                    End Using
                    ' Der Speicher muss die Loeschung schon KENNEN, bevor das erste neue Gesicht
                    ' zugeordnet wird - sonst verglichen die neuen Gesichter gegen die alten
                    ' Mitten desselben Bildes.
                    RemoveFromCentroids(removedFromCentroids)

                    Dim written = 0
                    ' EINE PERSON STEHT EINMAL AUF EINEM BILD. Wer hier schon zugeordnet wurde,
                    ' kommt fuer die uebrigen Gesichter DESSELBEN Bildes nicht mehr in Frage.
                    '
                    ' Der Grund ist gemessen: auf einem Gruppenfoto lieferte die Verfeinerung fuer
                    ' zwei benachbarte Gesichter zweimal dasselbe, beide bekamen dieselbe
                    ' Merkmalsreihe und landeten in einer Gruppe - 158 von 240 Bildern trugen
                    ' dieselbe Person mehrfach. Die Ursache ist behoben, aber die Regel bleibt: sie
                    ' kostet nichts und faengt jede weitere Verwechslung dieser Art ab. Der Preis
                    ' waere ein Bild, auf dem jemand zweimal zu sehen ist (Spiegel, Foto im Foto) -
                    ' dort entsteht eine zweite Gruppe, und die verschmilzt beim Benennen.
                    Dim usedOnThisImage As New HashSet(Of String)(StringComparer.Ordinal)
                    ' Die von Hand gesetzten Gesichter sind schon vergeben - ihre Personen kommen
                    ' fuer die uebrigen Gesichter dieses Bildes nicht mehr in Frage.
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        ' OHNE die Sammelgruppe: auf einem Bild koennen mehrere herausgeloeste
                        ' Gesichter liegen, und sie alle gehoeren in denselben Ablagekorb.
                        cmd.CommandText = "SELECT f.PersonId FROM Face f " &
                                          "JOIN Person p ON p.Id = f.PersonId " &
                                          "WHERE f.FilePath=$p AND f.IsManual=1 AND p.IsUnknownBin=0"
                        cmd.Parameters.AddWithValue("$p", filePath)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                usedOnThisImage.Add(reader.GetString(0))
                            End While
                        End Using
                    End Using

                    If faces IsNot Nothing Then
                        For Each face In faces
                            ' Liegt hier schon ein von Hand gesetztes Gesicht, gilt dieses.
                            If OverlapsManual(manual, face) Then Continue For
                            Dim personId As String = Nothing
                            ' Ohne Merkmale keine Person: das Gesicht ist zu klein aufgeloest
                            ' (siehe FaceDetectionService.MinimumRasterSize). Es wird trotzdem
                            ' eingetragen - gefunden wurde es ja.
                            If face.Embedding IsNot Nothing AndAlso face.Embedding.Length > 0 Then
                                personId = MatchOrCreatePersonLocked(conn, tx, face.Embedding, stamp, usedOnThisImage)
                                If personId IsNot Nothing Then usedOnThisImage.Add(personId)
                            End If

                            Using cmd = conn.CreateCommand()
                                cmd.Transaction = tx
                                cmd.CommandText =
                                    "INSERT INTO Face(Id,FilePath,PersonId,X,Y,Width,Height,Score,Embedding,ScannedAt) " &
                                    "VALUES($id,$p,$person,$x,$y,$w,$h,$s,$e,$t)"
                                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"))
                                cmd.Parameters.AddWithValue("$p", filePath)
                                cmd.Parameters.AddWithValue("$person", If(personId, CObj(DBNull.Value)))
                                cmd.Parameters.AddWithValue("$x", CDbl(face.X))
                                cmd.Parameters.AddWithValue("$y", CDbl(face.Y))
                                cmd.Parameters.AddWithValue("$w", CDbl(face.Width))
                                cmd.Parameters.AddWithValue("$h", CDbl(face.Height))
                                cmd.Parameters.AddWithValue("$s", CDbl(face.Score))
                                cmd.Parameters.AddWithValue("$e", If(face.Embedding Is Nothing,
                                                                     CObj(DBNull.Value), ToBlob(face.Embedding)))
                                cmd.Parameters.AddWithValue("$t", stamp)
                                cmd.ExecuteNonQuery()
                            End Using
                            ' Erst NACH dem Eintrag in die Mitten: das naechste Gesicht dieses
                            ' Bildes soll bereits dagegen vergleichen koennen.
                            If personId IsNot Nothing Then AddToCentroids(personId, face.Embedding)
                            written += 1
                        Next
                    End If
                    written += manual.Count

                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText =
                            "INSERT INTO ScannedImage(FilePath,SourceModifiedAt,FaceCount,ScannedAt) " &
                            "VALUES($p,$m,$c,$t) " &
                            "ON CONFLICT(FilePath) DO UPDATE SET SourceModifiedAt=excluded.SourceModifiedAt, " &
                            "FaceCount=excluded.FaceCount, ScannedAt=excluded.ScannedAt"
                        cmd.Parameters.AddWithValue("$p", filePath)
                        cmd.Parameters.AddWithValue("$m", If(sourceModifiedAt, ""))
                        cmd.Parameters.AddWithValue("$c", written)
                        cmd.Parameters.AddWithValue("$t", stamp)
                        cmd.ExecuteNonQuery()
                    End Using

                    tx.Commit()
                    committed = True
                End Using
            End Using
        End Sub

        ''' <summary>Liegt an dieser Stelle schon ein von Hand gesetztes Gesicht?
        '''
        ''' Verglichen wird die Ueberdeckung, nicht die Gleichheit: der Erkenner findet dasselbe
        ''' Gesicht beim naechsten Lauf ein paar Bildpunkte versetzt, und ein exakter Vergleich
        ''' liesse dann zwei Eintraege fuer dasselbe Gesicht entstehen.</summary>
        Private Shared Function OverlapsManual(manual As List(Of (X As Double, Y As Double, W As Double, H As Double)),
                                               face As DetectedFace) As Boolean
            If manual Is Nothing OrElse manual.Count = 0 Then Return False
            Dim faceCenterX = CDbl(face.X) + face.Width / 2
            Dim faceCenterY = CDbl(face.Y) + face.Height / 2

            For Each m In manual
                Dim x1 = Math.Max(m.X, CDbl(face.X))
                Dim y1 = Math.Max(m.Y, CDbl(face.Y))
                Dim x2 = Math.Min(m.X + m.W, CDbl(face.X) + face.Width)
                Dim y2 = Math.Min(m.Y + m.H, CDbl(face.Y) + face.Height)
                Dim intersection = Math.Max(0.0, x2 - x1) * Math.Max(0.0, y2 - y1)
                Dim union = m.W * m.H + CDbl(face.Width) * face.Height - intersection
                If union > 0 AndAlso intersection / union >= 0.4 Then Return True

                ' ZWEITES KRITERIUM: liegt eine Mitte im anderen Kasten, ist es derselbe Kopf.
                ' Die Ueberdeckung allein reicht nicht - ein erneuter Lauf setzt den Kasten
                ' gelegentlich etwas versetzt oder deutlich groesser, und dann faellt derselbe Kopf
                ' unter die Grenze. Er bekaeme eine neue Zeile, wuerde normal gruppiert, und ein
                ' Gesicht, das der Benutzer als "Unbekannt" abgelegt hat, staende wieder als Person
                ' in der Wand. Genau das darf nicht passieren.
                If faceCenterX >= m.X AndAlso faceCenterX <= m.X + m.W AndAlso
                   faceCenterY >= m.Y AndAlso faceCenterY <= m.Y + m.H Then Return True
                Dim mCenterX = m.X + m.W / 2
                Dim mCenterY = m.Y + m.H / 2
                If mCenterX >= face.X AndAlso mCenterX <= face.X + face.Width AndAlso
                   mCenterY >= face.Y AndAlso mCenterY <= face.Y + face.Height Then Return True
            Next
            Return False
        End Function

        ' ── Die Sammelgruppe ─────────────────────────────────────────────────────

        ''' <summary>Die eine Gruppe, in die herausgeloeste Gesichter wandern - angelegt, sobald sie
        ''' zum ersten Mal gebraucht wird.
        '''
        ''' WARUM ueberhaupt eine Sammelgruppe: ohne sie bekaeme jedes herausgeloeste Gesicht eine
        ''' eigene Gruppe, und nach zwanzig Korrekturen staende die Wand voller Einzelkacheln. Mit
        ''' ihr gibt es EINEN Ort fuer alles, was nicht dazugehoert.
        '''
        ''' UND WARUM SIE KEINE PERSON IST: eine Gruppe aus lauter verschiedenen Menschen haette eine
        ''' Mitte, die nichts bedeutet - und zugeordnet wird gegen die Mitte. Sie waere ein Magnet
        ''' fuer jedes unsichere Gesicht und wuerde beim naechsten Durchlauf alles einsammeln.
        ''' Deshalb steht sie in KEINEM Vergleich (<see cref="LoadPersonCentroidsLocked"/>) und nimmt
        ''' keinen Namen an (<see cref="ApplyPersonName"/>). Sie ist ein Ablagekorb, keine Person.</summary>
        Public Function GetOrCreateUnknownBin() As String
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Return GetOrCreateUnknownBinLocked(conn, Nothing)
            End Using
        End Function

        Private Shared Function GetOrCreateUnknownBinLocked(conn As SqliteConnection, tx As SqliteTransaction) As String
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = "SELECT Id FROM Person WHERE IsUnknownBin=1 LIMIT 1"
                Dim r = cmd.ExecuteScalar()
                If r IsNot Nothing AndAlso Not TypeOf r Is DBNull Then Return CStr(r)
            End Using

            Dim newId = Guid.NewGuid().ToString("N")
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = "INSERT INTO Person(Id,Name,CreatedAt,IsUnknownBin) VALUES($id,'',$t,1)"
                cmd.Parameters.AddWithValue("$id", newId)
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                cmd.ExecuteNonQuery()
            End Using
            Return newId
        End Function

        Public Function IsUnknownBin(personId As String) As Boolean
            If String.IsNullOrWhiteSpace(personId) Then Return False
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT IsUnknownBin FROM Person WHERE Id=$id"
                    cmd.Parameters.AddWithValue("$id", personId)
                    Dim r = cmd.ExecuteScalar()
                    Return r IsNot Nothing AndAlso Not TypeOf r Is DBNull AndAlso CInt(r) <> 0
                End Using
            End Using
        End Function

        ' ── Zuordnung ────────────────────────────────────────────────────────────

        ''' <summary>Sucht die Person, zu der dieses Gesicht am besten passt, und legt sonst eine neue
        ''' an.
        '''
        ''' VERGLICHEN WIRD GEGEN DEN MITTELWERT aller Gesichter einer Person, nicht gegen ein
        ''' einzelnes. Der Grund ist gemessen: die hoechste Aehnlichkeit zwischen zwei FREMDEN lag
        ''' bei 0,35, die Schwelle liegt bei 0,363 - ein Hundertstel Abstand. Gegen ein einzelnes
        ''' unguenstiges Vorkommen (Gegenlicht, Halbprofil) waere eine Fehlzuordnung damit eine Frage
        ''' der Tagesform. Der Mittelwert ueber mehrere Aufnahmen mittelt genau diese Ausreisser weg.
        '''
        ''' Der Vergleich laeuft GLOBAL ueber den ganzen Bestand, nicht ueber den gerade laufenden
        ''' Ordner. Sonst waere dieselbe Person je Ordner eine andere - und weil die Erkennung
        ''' bewusst ordnerweise ausgeloest wird, waere das der Regelfall statt der Ausnahme.</summary>
        ''' <param name="excluded">Personen, die auf DIESEM Bild schon vergeben sind.</param>
        Private Function MatchOrCreatePersonLocked(conn As SqliteConnection, tx As SqliteTransaction,
                                                   embedding As Single(), stamp As String,
                                                   excluded As HashSet(Of String)) As String
            Dim bestId As String = Nothing
            Dim bestScore As Double = 0

            For Each kv In LoadPersonCentroidsLocked(conn, tx)
                If excluded IsNot Nothing AndAlso excluded.Contains(kv.Key) Then Continue For
                Dim score = FaceDetectionService.Similarity(embedding, kv.Value)
                If score > bestScore Then
                    bestScore = score
                    bestId = kv.Key
                End If
            Next

            If bestId IsNot Nothing AndAlso bestScore >= SameThreshold Then Return bestId

            Dim newId = Guid.NewGuid().ToString("N")
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = "INSERT INTO Person(Id,Name,CreatedAt) VALUES($id,'',$t)"
                cmd.Parameters.AddWithValue("$id", newId)
                cmd.Parameters.AddWithValue("$t", stamp)
                cmd.ExecuteNonQuery()
            End Using
            Return newId
        End Function

        ' ── Die Gruppenmitten ────────────────────────────────────────────────────
        '
        ' GEHALTEN WIRD DIE SUMME, NICHT DER MITTELWERT. Aus Summe und Anzahl laesst sich die Mitte
        ' jederzeit ausrechnen, und beide lassen sich fortschreiben: ein Gesicht kommt dazu, eines
        ' faellt weg. Aus einem Mittelwert allein ginge das nicht zurueck.
        '
        ' WARUM UEBERHAUPT: die Mitten wurden bei JEDEM einzelnen Gesicht neu ueber den GANZEN
        ' Bestand gelesen. Der Aufwand wuchs damit im Quadrat der Gesichterzahl. Gemessen am Bestand
        ' sind 571 Reihen zu 2048 Byte rund 1,17 MB je Durchgang - bei 664 Gesichtern faellt das
        ' nicht auf. Bei den 25000 Bildern, von denen die Planung ausgeht, sind es gut 37000
        ' Gesichter: 76 MB je Durchgang, 37000 mal gelesen, zusammen ueber zwei Terabyte allein fuer
        ' die Mitten. Das ist mehr, als die Erkennung selbst kostet.
        '
        ' Der alte Kommentar nannte die PERSONENzahl als Maßstab. Das war die falsche Groesse: die
        ' Personenzahl bestimmt nur, wie viele Mitten herauskommen - gelesen wurde ueber alle
        ' GESICHTER.

        Private _centroidSums As Dictionary(Of String, Double())
        Private _centroidCounts As Dictionary(Of String, Integer)

        ''' <summary>Die gemerkten Mitten wegwerfen. Ruft JEDE Aenderung, die Gesichter zwischen
        ''' Personen bewegt oder Personen entfernt - dort ist das Fortschreiben nicht die Muehe wert,
        ''' weil es Handgriffe einzeln und selten sind, waehrend der Durchlauf zehntausende Gesichter
        ''' hintereinander eintraegt.</summary>
        Private Sub InvalidateCentroids()
            SyncLock _centroidLock
                _centroidSums = Nothing
                _centroidCounts = Nothing
            End SyncLock
        End Sub

        Private ReadOnly _centroidLock As New Object()

        ''' <summary>Je Person der Mittelwert ihrer Merkmalsreihen.
        '''
        ''' Gelesen wird die Datenbank nur beim ERSTEN Mal und nach jeder Aenderung, die die Mitten
        ''' verschiebt. Danach steht die Summe im Speicher und wird fortgeschrieben.</summary>
        Private Function LoadPersonCentroidsLocked(conn As SqliteConnection,
                                                   tx As SqliteTransaction) As Dictionary(Of String, Single())
            SyncLock _centroidLock
                EnsureCentroidsLocked(conn, tx)

                Dim result As New Dictionary(Of String, Single())(StringComparer.Ordinal)
                For Each kv In _centroidSums
                    Dim n = Math.Max(1, _centroidCounts(kv.Key))
                    Dim mean(kv.Value.Length - 1) As Single
                    For i = 0 To kv.Value.Length - 1
                        mean(i) = CSng(kv.Value(i) / n)
                    Next
                    result(kv.Key) = mean
                Next
                Return result
            End SyncLock
        End Function

        ''' <summary>Baut Summe und Anzahl je Person aus der Datenbank auf, falls sie fehlen.</summary>
        Private Sub EnsureCentroidsLocked(conn As SqliteConnection, tx As SqliteTransaction)
            If _centroidSums IsNot Nothing Then Return

            Dim sums As New Dictionary(Of String, Double())(StringComparer.Ordinal)
            Dim counts As New Dictionary(Of String, Integer)(StringComparer.Ordinal)

            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                ' Die Sammelgruppe bleibt AUSSEN VOR. Ihre Mitte waere der Durchschnitt lauter
                ' verschiedener Menschen - ein Magnet, der beim naechsten Durchlauf alles
                ' Unsichere einsammelt.
                cmd.CommandText = "SELECT f.PersonId, f.Embedding FROM Face f " &
                                  "JOIN Person p ON p.Id = f.PersonId " &
                                  "WHERE f.Embedding IS NOT NULL AND p.IsUnknownBin=0"
                Using reader = cmd.ExecuteReader()
                    While reader.Read()
                        Dim id = reader.GetString(0)
                        Dim vec = FromBlob(CType(reader.GetValue(1), Byte()))
                        AddToCentroidLocked(sums, counts, id, vec)
                    End While
                End Using
            End Using

            _centroidSums = sums
            _centroidCounts = counts
        End Sub

        ''' <summary>Eine Merkmalsreihe zur Summe einer Person schlagen.
        '''
        ''' Die ERSTE Reihe legt die Laenge fest, jede abweichende faellt weg - nach einem
        ''' Modellwechsel liegen alte 128er-Reihen neben neuen 512ern, und die beschreiben etwas
        ''' voellig anderes. Dieselbe Sperre wie in <see cref="FaceDetectionService.Similarity"/>.</summary>
        Private Shared Sub AddToCentroidLocked(sums As Dictionary(Of String, Double()),
                                               counts As Dictionary(Of String, Integer),
                                               personId As String, vec As Single())
            If String.IsNullOrEmpty(personId) OrElse vec Is Nothing OrElse vec.Length = 0 Then Return
            Dim acc As Double() = Nothing
            If Not sums.TryGetValue(personId, acc) Then
                acc = New Double(vec.Length - 1) {}
                sums(personId) = acc
                counts(personId) = 0
            End If
            If acc.Length <> vec.Length Then Return
            For i = 0 To vec.Length - 1
                acc(i) += vec(i)
            Next
            counts(personId) += 1
        End Sub

        ''' <summary>Eine Merkmalsreihe wieder abziehen. Faellt die Anzahl auf null, verschwindet die
        ''' Person aus den Mitten - eine Gruppe ohne Gesicht zieht nichts an.</summary>
        Private Shared Sub RemoveFromCentroidLocked(sums As Dictionary(Of String, Double()),
                                                    counts As Dictionary(Of String, Integer),
                                                    personId As String, vec As Single())
            If String.IsNullOrEmpty(personId) OrElse vec Is Nothing OrElse vec.Length = 0 Then Return
            Dim acc As Double() = Nothing
            If Not sums.TryGetValue(personId, acc) Then Return
            If acc.Length <> vec.Length Then Return
            For i = 0 To vec.Length - 1
                acc(i) -= vec(i)
            Next
            counts(personId) -= 1
            If counts(personId) <= 0 Then
                sums.Remove(personId)
                counts.Remove(personId)
            End If
        End Sub

        ''' <summary>Gesichter aus den gemerkten Mitten nehmen, weil sie gerade geloescht wurden.
        '''
        ''' SOFORT und nicht erst nach dem Commit: die neuen Gesichter DESSELBEN Bildes werden noch
        ''' in derselben Transaktion zugeordnet und muessen gegen Mitten vergleichen, in denen die
        ''' eigenen alten Funde nicht mehr stecken. Genau so verhielt sich die alte Fassung, die bei
        ''' jedem Gesicht frisch aus der Datenbank las. Bricht die Transaktion ab, wirft
        ''' <see cref="SaveFaces"/> den Speicher weg.
        '''
        ''' Sind die Mitten gar nicht aufgebaut, ist nichts zu tun - der naechste Zugriff liest sie
        ''' ohnehin frisch.</summary>
        Private Sub RemoveFromCentroids(removed As List(Of (PersonId As String, Vector As Single())))
            SyncLock _centroidLock
                If _centroidSums Is Nothing Then Return
                For Each row In removed
                    RemoveFromCentroidLocked(_centroidSums, _centroidCounts, row.PersonId, row.Vector)
                Next
            End SyncLock
        End Sub

        ''' <summary>Ein frisch eingetragenes Gesicht in die Mitten aufnehmen. Damit steht es schon
        ''' fuer das NAECHSTE Gesicht desselben Durchlaufs zur Verfuegung - auch das machte die alte
        ''' Fassung so, weil sie nach jedem Eintrag neu las.</summary>
        Private Sub AddToCentroids(personId As String, vec As Single())
            SyncLock _centroidLock
                If _centroidSums Is Nothing Then Return
                AddToCentroidLocked(_centroidSums, _centroidCounts, personId, vec)
            End SyncLock
        End Sub

        ' ── Lesen ────────────────────────────────────────────────────────────────

        ''' <summary>Die vergebenen Namen, alphabetisch. Fuer die Vorschlagsliste beim Eintippen.
        '''
        ''' WARUM ES DIE BRAUCHT: einen Namen von Hand ein zweites Mal zu tippen ist die haeufigste
        ''' Stelle, an der aus einer Person zwei werden - "Anna" und "anna" sind fuer die Bibliothek
        ''' zwei Menschen. Wer den vorhandenen Namen aus der Liste nimmt, trifft ihn zeichengenau,
        ''' und genau daran haengt, ob zwei Gruppen verschmelzen oder nebeneinander stehenbleiben.
        '''
        ''' Ohne Sammelkorb: der traegt keinen Namen und ist keine Person.</summary>
        Public Function GetPersonNames() As List(Of String)
            Dim result As New List(Of String)()
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText =
                            "SELECT DISTINCT Name FROM Person " &
                            "WHERE Name <> '' AND IsUnknownBin=0 ORDER BY Name COLLATE NOCASE"
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                result.Add(reader.GetString(0))
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Library.GetPersonNames", ex)
            End Try
            Return result
        End Function

        ''' <summary>Alle Personen mit der Zahl der Bilder, in denen sie vorkommen. Gezaehlt werden
        ''' BILDER, nicht Gesichter - steht jemand zweimal auf demselben Foto, ist das ein Bild.</summary>
        Public Function GetPeople() As List(Of PersonEntry)
            Dim result As New List(Of PersonEntry)()
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    ' Die Sammelgruppe ganz nach hinten: sie ist keine Person, und oben stehen
                    ' die, mit denen man arbeitet.
                    cmd.CommandText =
                        "SELECT p.Id, p.Name, COUNT(DISTINCT f.FilePath), p.IsUnknownBin " &
                        "FROM Person p LEFT JOIN Face f ON f.PersonId = p.Id " &
                        "GROUP BY p.Id, p.Name, p.IsUnknownBin " &
                        "ORDER BY p.IsUnknownBin, p.Name = '' , p.Name COLLATE NOCASE"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            result.Add(New PersonEntry With {
                                .Id = reader.GetString(0),
                                .Name = reader.GetString(1),
                                .ImageCount = reader.GetInt32(2),
                                .IsUnknownBin = reader.GetInt32(3) <> 0})
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Das GROESSTE Gesicht einer Person - ihr Aushaengeschild in der Liste.
        '''
        ''' Das groesste und nicht das erste: es ist am ehesten scharf, und man erkennt darauf, wen
        ''' man vor sich hat. Genau darum geht es in einer Liste von hundert Gruppen.</summary>
        Public Function GetPersonCover(personId As String) As (FilePath As String, X As Double, Y As Double,
                                                              Width As Double, Height As Double)
            If String.IsNullOrWhiteSpace(personId) Then Return ("", 0, 0, 0, 0)
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "SELECT FilePath, X, Y, Width, Height FROM Face WHERE PersonId=$p " &
                        "ORDER BY MAX(Width, Height) DESC LIMIT 1"
                    cmd.Parameters.AddWithValue("$p", personId)
                    Using reader = cmd.ExecuteReader()
                        If Not reader.Read() Then Return ("", 0, 0, 0, 0)
                        Return (reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2),
                                reader.GetDouble(3), reader.GetDouble(4))
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Wie viele BILDER eine Person hat - gezaehlt werden Bilder, nicht Gesichter.
        ''' Fuer die Kachel nach einer Aenderung: eine Zahl, die nicht mehr stimmt, ist schlimmer als
        ''' keine.</summary>
        Public Function GetPersonImageCount(personId As String) As Integer
            If String.IsNullOrWhiteSpace(personId) Then Return 0
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT COUNT(DISTINCT FilePath) FROM Face WHERE PersonId=$p"
                    cmd.Parameters.AddWithValue("$p", personId)
                    Dim r = cmd.ExecuteScalar()
                    Return If(r Is Nothing OrElse TypeOf r Is DBNull, 0, CInt(r))
                End Using
            End Using
        End Function

        ''' <summary>Alle Gesichter EINER Person, das groesste zuerst. Fuer die Personenverwaltung:
        ''' dort raeumt man eine Gruppe auf, und dazu muss man sehen, wer alles darin steckt.</summary>
        Public Function GetFacesForPerson(personId As String, Optional limit As Integer = 400) As List(Of (FaceId As String,
                                                                                                          FilePath As String,
                                                                                                          X As Double, Y As Double,
                                                                                                          Width As Double, Height As Double,
                                                                                                          IsManual As Boolean))
            Dim result As New List(Of (FaceId As String, FilePath As String, X As Double, Y As Double, Width As Double, Height As Double, IsManual As Boolean))()
            If String.IsNullOrWhiteSpace(personId) Then Return result
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "SELECT Id, FilePath, X, Y, Width, Height, IsManual FROM Face WHERE PersonId=$p " &
                        "ORDER BY MAX(Width, Height) DESC LIMIT $n"
                    cmd.Parameters.AddWithValue("$p", personId)
                    cmd.Parameters.AddWithValue("$n", Math.Max(1, limit))
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            result.Add((reader.GetString(0), reader.GetString(1),
                                        reader.GetDouble(2), reader.GetDouble(3),
                                        reader.GetDouble(4), reader.GetDouble(5),
                                        reader.GetInt32(6) <> 0))
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Wirft eine Gruppe weg. Die GESICHTER bleiben - sie wurden ja gefunden -, sie
        ''' gehoeren danach nur zu niemandem mehr und tauchen in keiner Personenliste auf.
        '''
        ''' Fuer Gruppen, die gar keine Person sind: ein Muster in einer Hecke, ein Gesicht auf einem
        ''' Plakat im Hintergrund. Ein erneuter Durchlauf legt sie neu an, wenn er sie wieder
        ''' findet - deshalb ist das kein Ausschluss auf Dauer, sondern Aufraeumen.</summary>
        Public Sub DeletePerson(personId As String)
            If String.IsNullOrWhiteSpace(personId) Then Return
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using tx = conn.BeginTransaction()
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "UPDATE Face SET PersonId=NULL, IsManual=0 WHERE PersonId=$p"
                        cmd.Parameters.AddWithValue("$p", personId)
                        cmd.ExecuteNonQuery()
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM Person WHERE Id=$p"
                        cmd.Parameters.AddWithValue("$p", personId)
                        cmd.ExecuteNonQuery()
                    End Using
                    tx.Commit()
                    ' Gesichter haben die Gruppe gewechselt: die gemerkten Mitten stimmen nicht mehr.
                    InvalidateCentroids()
                End Using
            End Using
        End Sub

        ''' <summary>Die Personen auf einem Bild. Fuer den Abschnitt im Infopanel.</summary>
        Public Function GetPeopleForImage(filePath As String) As List(Of PersonEntry)
            Dim result As New List(Of PersonEntry)()
            If String.IsNullOrWhiteSpace(filePath) Then Return result
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "SELECT DISTINCT p.Id, p.Name FROM Face f " &
                        "JOIN Person p ON p.Id = f.PersonId WHERE f.FilePath=$p " &
                        "ORDER BY p.Name = '', p.Name COLLATE NOCASE"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            result.Add(New PersonEntry With {.Id = reader.GetString(0), .Name = reader.GetString(1)})
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Alle Bildpfade, auf denen diese Personen zu sehen sind. Mehrere Personen wirken
        ''' als UND: gesucht wird, wer gemeinsam auf einem Bild steht - so ist die Frage gemeint,
        ''' wenn jemand zwei Namen anklickt.</summary>
        Public Function GetPathsForPeople(personIds As IReadOnlyList(Of String)) As List(Of String)
            Dim result As New List(Of String)()
            If personIds Is Nothing OrElse personIds.Count = 0 Then Return result
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    Dim names = personIds.Select(Function(unused, i) "$p" & i).ToList()
                    cmd.CommandText =
                        $"SELECT FilePath FROM Face WHERE PersonId IN ({String.Join(",", names)}) " &
                        "GROUP BY FilePath HAVING COUNT(DISTINCT PersonId) = $n"
                    For i = 0 To personIds.Count - 1
                        cmd.Parameters.AddWithValue("$p" & i, personIds(i))
                    Next
                    cmd.Parameters.AddWithValue("$n", personIds.Count)
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            result.Add(reader.GetString(0))
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Alle Bildpfade, auf denen diese Personen NAMENTLICH zu sehen sind. Mehrere Namen
        ''' wirken als UND, genau wie bei <see cref="GetPathsForPeople"/>.
        '''
        ''' Fuer die Vorauswahl des Suchdurchlaufs, der sonst ueber jeden Katalogeintrag geht. Ueber
        ''' den NAMEN und nicht ueber die Id, weil die Suchbedingung so gebaut ist - sie muss auch in
        ''' einer gespeicherten Suche lesbar bleiben.</summary>
        Public Function GetPathsForPersonNames(names As IReadOnlyList(Of String)) As List(Of String)
            Dim result As New List(Of String)()
            If names Is Nothing OrElse names.Count = 0 Then Return result
            ' Ohne Ruecksicht auf die Schreibweise vereinzelt, denn genau so wird gleich verglichen.
            ' Stuende "Anna" und "anna" in der Liste, waeren zwei verschiedene Gruppen auf demselben
            ' Bild verlangt - und das Bild fiele aus der Vorauswahl, obwohl es passt.
            Dim wanted = names.Where(Function(n) Not String.IsNullOrWhiteSpace(n)).
                               Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            If wanted.Count = 0 Then Return result
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        Dim parameters As New List(Of String)()
                        For i = 0 To wanted.Count - 1
                            parameters.Add("$n" & i)
                            cmd.Parameters.AddWithValue("$n" & i, wanted(i))
                        Next
                        ' DIE VORAUSWAHL MUSS EINE OBERMENGE DER AUSWERTUNG SEIN. Verglichen wird
                        ' oben ohne Ruecksicht auf Gross- und Kleinschreibung, gezaehlt wurde
                        ' frueher mit - zwei Gruppen "Anna" und "anna" auf einem Bild zaehlten damit
                        ' als zwei Personen und warfen das Bild heraus, das die eigentliche
                        ' Auswertung danach getroffen haette. Deshalb LOWER beim Zaehlen.
                        ' Und ">=" statt "=": das Vereinzeln hier oben und das von SQLite muessen
                        ' nicht bis auf den letzten Buchstaben dasselbe tun (Umlaute etwa fasst
                        ' SQLite nicht zusammen). Ein Vorfilter darf zu viel liefern, nur nie zu
                        ' wenig - mehr als die verlangten Namen kann ohnehin nicht zusammenkommen,
                        ' weil die Bedingung oben nur diese durchlaesst.
                        cmd.CommandText =
                            "SELECT f.FilePath FROM Face f JOIN Person p ON p.Id = f.PersonId " &
                            $"WHERE p.Name COLLATE NOCASE IN ({String.Join(",", parameters)}) " &
                            "GROUP BY f.FilePath HAVING COUNT(DISTINCT LOWER(p.Name)) >= $n"
                        cmd.Parameters.AddWithValue("$n", wanted.Count)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                result.Add(reader.GetString(0))
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Library.GetPathsForPersonNames", ex)
            End Try
            Return result
        End Function

        ''' <summary>Die Gesichter EINES Bildes samt Lage und zugeordneter Person.
        '''
        ''' Gebraucht fuer die Zuordnung im Infopanel: fuenf leere Namensfelder untereinander sagen
        ''' niemandem, welches zu welchem Gesicht gehoert. Mit der Box laesst sich der Ausschnitt
        ''' danebenstellen, und dann ist es offensichtlich.
        '''
        ''' Die FaceId kommt mit, weil sich eine Fehlzuordnung nur an EINEM Gesicht loesen laesst
        ''' (siehe <see cref="DetachFace"/>) - ueber die PersonId waere immer die ganze Gruppe
        ''' gemeint, also auch jedes andere Bild.</summary>
        Public Function GetFacesForImage(filePath As String) As List(Of (FaceId As String, PersonId As String, Name As String,
                                                                        X As Double, Y As Double,
                                                                        Width As Double, Height As Double))
            Dim result As New List(Of (FaceId As String, PersonId As String, Name As String, X As Double, Y As Double, Width As Double, Height As Double))()
            If String.IsNullOrWhiteSpace(filePath) Then Return result
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    ' Nach Lage sortiert, links vor rechts: die Reihenfolge im Panel entspricht damit
                    ' der im Bild, und das Zuordnen faellt noch leichter.
                    cmd.CommandText =
                        "SELECT f.Id, f.PersonId, COALESCE(p.Name,''), f.X, f.Y, f.Width, f.Height " &
                        "FROM Face f LEFT JOIN Person p ON p.Id = f.PersonId " &
                        "WHERE f.FilePath=$p ORDER BY f.X"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            If reader.IsDBNull(1) Then Continue While
                            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                                        reader.GetDouble(3), reader.GetDouble(4),
                                        reader.GetDouble(5), reader.GetDouble(6)))
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Loest EIN Gesicht aus seiner Person heraus - der Weg fuer eine Fehlzuordnung.
        '''
        ''' Das Gesicht bekommt eine EIGENE, namenlose Gruppe statt gar keiner. Der Grund ist die
        ''' Bedienung: mit leerer Zuordnung waere das Gesicht danach an nichts mehr zu fassen, mit
        ''' eigener Gruppe traegt man einfach den richtigen Namen ein - und heisst schon jemand so,
        ''' verschmilzt <see cref="NamePerson"/> beides. "Aufheben" und "neu setzen" sind damit
        ''' derselbe Handgriff wie ueberall sonst.
        '''
        ''' Geaendert wird GENAU EINE Zeile in Face. Jede andere Aufnahme derselben Person bleibt,
        ''' wo sie ist - das ist der ganze Sinn der Sache.
        '''
        ''' Bleibt die alte Gruppe leer zurueck, wird sie nur dann geloescht, wenn sie NAMENLOS war.
        ''' Ein vergebener Name ist Handarbeit des Benutzers und verschwindet nicht, weil gerade das
        ''' letzte Gesicht woanders hin gehoerte.</summary>
        ''' <returns>Die Id der neuen Gruppe, oder Nothing, wenn es das Gesicht nicht gibt.</returns>
        Public Function DetachFace(faceId As String) As String
            If String.IsNullOrWhiteSpace(faceId) Then Return Nothing
            Dim stamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using tx = conn.BeginTransaction()
                    Dim oldPersonId As String = Nothing
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "SELECT PersonId FROM Face WHERE Id=$id"
                        cmd.Parameters.AddWithValue("$id", faceId)
                        Dim r = cmd.ExecuteScalar()
                        If r Is Nothing Then Return Nothing
                        If Not TypeOf r Is DBNull Then oldPersonId = CStr(r)
                    End Using

                    ' In die SAMMELGRUPPE, nicht in eine eigene neue: sonst staende die
                    ' Personenwand nach zwanzig Korrekturen voller Einzelkacheln.
                    Dim newId = GetOrCreateUnknownBinLocked(conn, tx)

                    ' Von Hand geloest heisst von Hand entschieden: ein erneuter Durchlauf faellt
                    ' sonst in dieselbe Fehlzuordnung zurueck, und die Berichtigung waere weg.
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "UPDATE Face SET PersonId=$new, IsManual=1 WHERE Id=$id"
                        cmd.Parameters.AddWithValue("$new", newId)
                        cmd.Parameters.AddWithValue("$id", faceId)
                        cmd.ExecuteNonQuery()
                    End Using

                    If oldPersonId IsNot Nothing Then
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText =
                                "DELETE FROM Person WHERE Id=$old AND Name='' " &
                                "AND NOT EXISTS(SELECT 1 FROM Face WHERE PersonId=$old)"
                            cmd.Parameters.AddWithValue("$old", oldPersonId)
                            cmd.ExecuteNonQuery()
                        End Using
                    End If

                    tx.Commit()
                    ' Gesichter haben die Gruppe gewechselt: die gemerkten Mitten stimmen nicht mehr.
                    InvalidateCentroids()
                    Return newId
                End Using
            End Using
        End Function

        ''' <summary>Zu jedem Bildpfad die Namen der Personen darauf, in EINER Abfrage.
        '''
        ''' Fuer den Suchdurchlauf: dort wird je Bild gefragt, ob eine Person darauf steht, und eine
        ''' eigene Abfrage je Bild liesse bei zehntausenden Fotos die Oberflaeche stehen. Unbenannte
        ''' Gruppen bleiben draussen - nach ihnen laesst sich nicht suchen, sie haben ja keinen
        ''' Namen.</summary>
        Public Function GetPersonNamesByPath() As Dictionary(Of String, List(Of String))
            Dim result As New Dictionary(Of String, List(Of String))(StringComparer.Ordinal)
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "SELECT DISTINCT f.FilePath, p.Name FROM Face f " &
                        "JOIN Person p ON p.Id = f.PersonId WHERE p.Name <> ''"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            Dim path = reader.GetString(0)
                            Dim names As List(Of String) = Nothing
                            If Not result.TryGetValue(path, names) Then
                                names = New List(Of String)()
                                result(path) = names
                            End If
                            names.Add(reader.GetString(1))
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Gibt einer Gruppe einen Namen. Traegt schon jemand anderes denselben Namen,
        ''' werden beide Gruppen VERSCHMOLZEN - zwei Gruppen mit demselben Namen waeren fuer den
        ''' Benutzer eine Person, und er hat mit der Benennung genau das gesagt.
        '''
        ''' DIE SAMMELGRUPPE BLEIBT AUSSEN VOR, und die Sperre sitzt HIER und nicht nur in
        ''' <see cref="ApplyPersonName"/>. Sie stand dort einmal allein, und die Personenverwaltung
        ''' ging an ihr vorbei: ein vorhandener Name am geoeffneten Korb liess den Verschmelzungszweig
        ''' greifen, schob SAEMTLICHE herausgeloesten Gesichter zu dieser Person und loeschte die
        ''' Korbzeile - jede Berichtigung des Benutzers auf einen Schlag zurueckgenommen. Ein neuer
        ''' Name machte den Korb such- und filterbar, weil GetPersonNamesByPath nur nach einem
        ''' nichtleeren Namen fragt. Eine Regel, die nur ein Aufrufer einhaelt, ist keine.</summary>
        ''' <param name="faceId">Das Gesicht, an dem der Name eingetragen wurde, sofern bekannt.
        ''' Es gilt danach als von Hand gesetzt und ueberlebt jeden weiteren Durchlauf.
        '''
        ''' NUR DIESES EINE, nicht die ganze Gruppe: sonst waere nach dem Benennen einer Person mit
        ''' hundert Bildern jedes davon festgeschrieben, und eine falsche Zuordnung darin liesse
        ''' sich durch keinen Neulauf mehr berichtigen. Fest ist, was der Benutzer angefasst
        ''' hat.</param>
        Public Sub NamePerson(personId As String, name As String, Optional faceId As String = Nothing)
            If String.IsNullOrWhiteSpace(personId) Then Return
            Dim clean = If(name, "").Trim()
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using tx = conn.BeginTransaction()
                    ' In DERSELBEN Transaktion gefragt wie alles Weitere - eine Abfrage davor waere
                    ' eine Aussage ueber einen Zustand, der beim Schreiben schon ein anderer sein
                    ' kann.
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "SELECT IsUnknownBin FROM Person WHERE Id=$id"
                        cmd.Parameters.AddWithValue("$id", personId)
                        Dim bin = cmd.ExecuteScalar()
                        If bin IsNot Nothing AndAlso Not TypeOf bin Is DBNull AndAlso CInt(bin) <> 0 Then Return
                    End Using

                    Dim mergeInto As String = Nothing
                    If clean.Length > 0 Then
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText = "SELECT Id FROM Person WHERE Name=$n COLLATE NOCASE AND Id<>$id LIMIT 1"
                            cmd.Parameters.AddWithValue("$n", clean)
                            cmd.Parameters.AddWithValue("$id", personId)
                            Dim r = cmd.ExecuteScalar()
                            If r IsNot Nothing AndAlso Not TypeOf r Is DBNull Then mergeInto = CStr(r)
                        End Using
                    End If

                    If mergeInto IsNot Nothing Then
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText = "UPDATE Face SET PersonId=$into WHERE PersonId=$from; " &
                                              "DELETE FROM Person WHERE Id=$from"
                            cmd.Parameters.AddWithValue("$into", mergeInto)
                            cmd.Parameters.AddWithValue("$from", personId)
                            cmd.ExecuteNonQuery()
                        End Using
                    Else
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText = "UPDATE Person SET Name=$n WHERE Id=$id"
                            cmd.Parameters.AddWithValue("$n", clean)
                            cmd.Parameters.AddWithValue("$id", personId)
                            cmd.ExecuteNonQuery()
                        End Using
                    End If

                    ' Das angefasste Gesicht ist ab jetzt Handarbeit und bleibt bei jedem weiteren
                    ' Durchlauf, wo es ist.
                    If Not String.IsNullOrWhiteSpace(faceId) Then
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText = "UPDATE Face SET IsManual=1 WHERE Id=$f"
                            cmd.Parameters.AddWithValue("$f", faceId)
                            cmd.ExecuteNonQuery()
                        End Using
                    End If
                    tx.Commit()
                    ' Gesichter haben die Gruppe gewechselt: die gemerkten Mitten stimmen nicht mehr.
                    InvalidateCentroids()
                End Using
            End Using
        End Sub

        ''' <summary>Was ein eingetragener Name bewirkt hat.</summary>
        Public Enum PersonNameOutcome
            Unchanged
            GroupNamed
            FaceMoved
        End Enum

        ''' <summary>Der Name, den jemand an EINEM Gesicht eintraegt - und was er bedeutet.
        '''
        ''' Drei Faelle, und der Unterschied ist der ganze Punkt:
        '''
        ''' 1. DIE GRUPPE HAT NOCH KEINEN NAMEN. Dann benennt der Eintrag die Gruppe, und heisst
        '''    schon jemand so, verschmelzen beide. Das ist der Normalfall nach einem Durchlauf: die
        '''    Gruppierung steht, der Mensch sagt, wer das ist.
        '''
        ''' 2. DIE GRUPPE HAT EINEN NAMEN, und der neue kommt sonst nirgends vor. Dann ist es eine
        '''    Umbenennung der Gruppe - typischerweise ein Tippfehler, der geradegezogen wird.
        '''
        ''' 3. DIE GRUPPE HAT EINEN NAMEN, und der neue gehoert einer ANDEREN Person. Dann ist es
        '''    eine Berichtigung DIESES Gesichts: es ist nicht Steffen, sondern Christina. Verschoben
        '''    wird genau diese eine Zeile, und sie gilt danach als Handarbeit. Die uebrigen Bilder
        '''    der Gruppe bleiben, wo sie sind - waere es anders, zoege ein einziger falsch
        '''    einsortierter Kopf die ganze Gruppe mit sich, und das ist genau der Schaden, den man
        '''    gerade beheben wollte.</summary>
        ''' <returns>Was geschehen ist - fuer eine Rueckmeldung an den Benutzer.</returns>
        Public Function ApplyPersonName(personId As String, name As String, faceId As String) As PersonNameOutcome
            If String.IsNullOrWhiteSpace(personId) Then Return PersonNameOutcome.Unchanged
            Dim clean = If(name, "").Trim()

            Dim currentName = ""
            Dim targetId As String = Nothing
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT Name FROM Person WHERE Id=$id"
                    cmd.Parameters.AddWithValue("$id", personId)
                    Dim r = cmd.ExecuteScalar()
                    If r Is Nothing OrElse TypeOf r Is DBNull Then Return PersonNameOutcome.Unchanged
                    currentName = CStr(r)
                End Using

                If clean.Length > 0 Then
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT Id FROM Person WHERE Name=$n COLLATE NOCASE AND Id<>$id LIMIT 1"
                        cmd.Parameters.AddWithValue("$n", clean)
                        cmd.Parameters.AddWithValue("$id", personId)
                        Dim r = cmd.ExecuteScalar()
                        If r IsNot Nothing AndAlso Not TypeOf r Is DBNull Then targetId = CStr(r)
                    End Using
                End If
            End Using

            If String.Equals(currentName, clean, StringComparison.OrdinalIgnoreCase) Then Return PersonNameOutcome.Unchanged

            ' Fall 3: benannte Gruppe, und der neue Name gehoert schon jemandem.
            If currentName.Length > 0 AndAlso targetId IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(faceId) Then
                MoveFaceToPerson(faceId, targetId)
                Return PersonNameOutcome.FaceMoved
            End If

            ' DIE SAMMELGRUPPE NIMMT KEINEN NAMEN AN. Sie ist ein Ablagekorb aus lauter
            ' verschiedenen Menschen; sie zu benennen hiesse, sie alle zu einer Person zu erklaeren.
            ' Ein Name an einem Gesicht IN ihr meint deshalb immer nur dieses eine: es verlaesst den
            ' Korb und geht zu der genannten Person, oder zu einer neuen mit diesem Namen.
            If IsUnknownBin(personId) Then
                If String.IsNullOrWhiteSpace(faceId) OrElse clean.Length = 0 Then Return PersonNameOutcome.Unchanged
                Dim ziel = If(targetId, CreateNamedPerson(clean))
                MoveFaceToPerson(faceId, ziel)
                Return PersonNameOutcome.FaceMoved
            End If

            ' Fall 1 und 2.
            NamePerson(personId, clean, faceId)
            Return PersonNameOutcome.GroupNamed
        End Function

        ''' <summary>Legt eine Person mit diesem Namen an. Fuer ein Gesicht, das aus dem Ablagekorb
        ''' zu jemandem geht, den es noch nicht gibt.</summary>
        Private Function CreateNamedPerson(name As String) As String
            Dim newId = Guid.NewGuid().ToString("N")
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "INSERT INTO Person(Id,Name,CreatedAt) VALUES($id,$n,$t)"
                    cmd.Parameters.AddWithValue("$id", newId)
                    cmd.Parameters.AddWithValue("$n", If(name, "").Trim())
                    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                    cmd.ExecuteNonQuery()
                End Using
            End Using
            Return newId
        End Function

        ''' <summary>Haengt EIN Gesicht an eine andere Person und schreibt es als Handarbeit fest.
        ''' Bleibt die alte Gruppe leer und NAMENLOS zurueck, verschwindet sie - ein vergebener Name
        ''' dagegen bleibt, er ist Handarbeit des Benutzers.</summary>
        Public Sub MoveFaceToPerson(faceId As String, personId As String)
            If String.IsNullOrWhiteSpace(faceId) OrElse String.IsNullOrWhiteSpace(personId) Then Return
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using tx = conn.BeginTransaction()
                    Dim oldPersonId As String = Nothing
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "SELECT PersonId FROM Face WHERE Id=$id"
                        cmd.Parameters.AddWithValue("$id", faceId)
                        Dim r = cmd.ExecuteScalar()
                        If r Is Nothing Then Return
                        If Not TypeOf r Is DBNull Then oldPersonId = CStr(r)
                    End Using

                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "UPDATE Face SET PersonId=$new, IsManual=1 WHERE Id=$id"
                        cmd.Parameters.AddWithValue("$new", personId)
                        cmd.Parameters.AddWithValue("$id", faceId)
                        cmd.ExecuteNonQuery()
                    End Using

                    If oldPersonId IsNot Nothing AndAlso Not String.Equals(oldPersonId, personId, StringComparison.Ordinal) Then
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText =
                                "DELETE FROM Person WHERE Id=$old AND Name='' " &
                                "AND NOT EXISTS(SELECT 1 FROM Face WHERE PersonId=$old)"
                            cmd.Parameters.AddWithValue("$old", oldPersonId)
                            cmd.ExecuteNonQuery()
                        End Using
                    End If
                    tx.Commit()
                    ' Gesichter haben die Gruppe gewechselt: die gemerkten Mitten stimmen nicht mehr.
                    InvalidateCentroids()
                End Using
            End Using
        End Sub

        ''' <summary>Alles zu Personen wegwerfen. Fuer den Fall, dass jemand die Funktion wieder
        ''' abschaltet - biometrische Merkmale sollen dann nicht liegenbleiben.
        '''
        ''' HAELT ZUERST EINEN LAUFENDEN SUCHLAUF AN. Sonst faellt das Leeren mitten hinein, und der
        ''' Lauf schreibt danach weiter: die Tabellen waeren hinterher nicht leer, sondern halb
        ''' gefuellt, und der Benutzer haette genau das nicht erreicht, wofuer er den Schalter
        ''' umgelegt hat. Laesst der Lauf sich nicht anhalten, wird NICHTS geloescht - lieber gar
        ''' nicht als halb: ein fehlender Scan-Vermerk zu vorhandenen Gesichtern taeuscht beim
        ''' Wiedereinschalten einen erledigten Durchlauf vor.
        '''
        ''' EINE TRANSAKTION fuer alle drei Tabellen. Kaeme die zweite Loeschung nicht durch, waeren
        ''' die Gesichter weg und die Vermerke da - dann gilt jedes Bild als bereits durchsucht, und
        ''' ein Neu-Scan liefe nie wieder an.</summary>
        ''' <returns>False, wenn nichts geleert wurde, weil der Suchlauf nicht anzuhalten war.</returns>
        Public Function ClearAllFaces() As Boolean
            If Not FaceScanRunner.RequestStopAndWait() Then
                DiagnosticLogService.LogAlways("Library.ClearAllFaces",
                                               "Nicht geleert: der Gesichtsdurchlauf steht noch")
                Return False
            End If

            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using tx = conn.BeginTransaction()
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM Face"
                        cmd.ExecuteNonQuery()
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM Person"
                        cmd.ExecuteNonQuery()
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM ScannedImage"
                        cmd.ExecuteNonQuery()
                    End Using
                    tx.Commit()
                End Using
            End Using
            ' Es gibt keine Mitten mehr, weil es keine Gesichter mehr gibt.
            InvalidateCentroids()
            Return True
        End Function

        ' ── Merkmalsreihe als BLOB ───────────────────────────────────────────────

        Private Shared Function ToBlob(values As Single()) As Byte()
            Dim bytes(values.Length * 4 - 1) As Byte
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length)
            Return bytes
        End Function

        Private Shared Function FromBlob(bytes As Byte()) As Single()
            If bytes Is Nothing OrElse bytes.Length < 4 Then Return Nothing
            Dim values(bytes.Length \ 4 - 1) As Single
            Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 4)
            Return values
        End Function

    End Class

End Namespace
