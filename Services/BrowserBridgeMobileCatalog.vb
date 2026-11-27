Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite

Namespace Services

    ''' <summary>Die schlanken Abfragen der mobilen Seite an den Katalog. Eigene, weil die
    ''' gewoehnlichen jede Spalte lesen (Zusammenfassungen von EXIF, IPTC, XMP): ueber einen Bestand
    ''' von hunderttausend Bildern waeren das Megabytes je Seite der Zeitleiste.</summary>
    Partial Public Class LibraryService

        ''' <summary>Je Zeile des Katalogs: Pfad, Aufnahmezeit im Rohformat und was die Kachel
        ''' veralten laesst (Stand von Datei und Beistelldatei beim letzten Einlesen), dazu das
        ''' Farbetikett fuer die Kachel.</summary>
        Friend Function GetMobileTimelineRows() As List(Of (Path As String, Taken As String, Stamp As String, Label As String))
            Dim result As New List(Of (Path As String, Taken As String, Stamp As String, Label As String))()
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT FilePath, DateTaken, ScannedSourceModifiedAt, ScannedSidecarModifiedAt, ColorLabel FROM ImageMeta"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            If reader.IsDBNull(0) Then Continue While
                            Dim taken = If(reader.IsDBNull(1), "", reader.GetString(1))
                            Dim stamp = If(reader.IsDBNull(2), "", reader.GetString(2)) & "|" & If(reader.IsDBNull(3), "", reader.GetString(3))
                            Dim label = If(reader.IsDBNull(4), "", reader.GetString(4))
                            result.Add((reader.GetString(0), taken, stamp, label))
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Die Zeilen der Bewegtbilder (Motion Photo, beide Haelften eines Live Photos):
        ''' Pfad, Art und Kennung. Fuer das Zeichen auf den Kacheln der Zeitleiste.</summary>
        Friend Function GetMobileMotionRows() As Dictionary(Of String, (Kind As MotionKind, Id As String))
            Dim result As New Dictionary(Of String, (Kind As MotionKind, Id As String))(PathIdentity.Comparer)
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT FilePath, MotionKind, MotionId FROM ImageMeta WHERE MotionKind <> 0"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            If reader.IsDBNull(0) OrElse reader.IsDBNull(1) Then Continue While
                            result(reader.GetString(0)) = (NormalizeMotionKind(reader.GetInt32(1)), If(reader.IsDBNull(2), "", reader.GetString(2)))
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Je Zeile mit Ortsnamen: Pfad, Ort, Land und Laenderkuerzel.</summary>
        Friend Function GetMobilePlaceRows() As List(Of (Path As String, City As String, Country As String, CountryCode As String))
            Dim result As New List(Of (Path As String, City As String, Country As String, CountryCode As String))()
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT FilePath, City, Country, CountryCode FROM ImageMeta WHERE City IS NOT NULL AND City <> ''"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            result.Add((reader.GetString(0), reader.GetString(1),
                                        If(reader.IsDBNull(2), "", reader.GetString(2)),
                                        If(reader.IsDBNull(3), "", reader.GetString(3))))
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Die Gesichter einer Person, das groesste zuerst: Datei und Rahmen. Die mobile
        ''' Seite nimmt das erste, das der Benutzer sehen darf.</summary>
        Friend Function GetMobileFaceRows(personId As String) As List(Of (FilePath As String, X As Double, Y As Double, Width As Double, Height As Double))
            Dim result As New List(Of (FilePath As String, X As Double, Y As Double, Width As Double, Height As Double))()
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT FilePath, X, Y, Width, Height FROM Face WHERE PersonId=$p ORDER BY MAX(Width, Height) DESC LIMIT 2000"
                    cmd.Parameters.AddWithValue("$p", personId)
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            result.Add((reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4)))
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Je benanntem Gesicht: Person und Datei.</summary>
        Friend Function GetMobilePersonRows() As List(Of (PersonId As String, Path As String))
            Dim result As New List(Of (PersonId As String, Path As String))()
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT DISTINCT Face.PersonId, Face.FilePath FROM Face JOIN Person ON Person.Id = Face.PersonId " &
                                      "WHERE Person.Name <> '' AND Person.IsUnknownBin = 0"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            result.Add((reader.GetString(0), reader.GetString(1)))
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function
    End Class

    ''' <summary>Die Listen der mobilen Seite: Zeitleiste, Ordner, Suche, Personen, Orte und das
    ''' Infoblatt eines Bildes. Jede Liste geht durch den Kontext der Anfrage
    ''' (MobileContext.CanSee), und jede Datei geht als Kennung hinaus.
    '''
    ''' <para>EIN FOTO, EINE KACHEL: liegen eine RAW und ihr JPEG (oder HEIF) mit gleichem Namen im
    ''' selben Ordner, zeigt die Seite nur die RAW, wie die Galerie mit Stapeln. Bewertungen gehen an
    ''' beide (<see cref="MobilePartnersOf"/>).</para></summary>
    Partial Public NotInheritable Class BrowserBridge

        ''' <summary>So lange gilt eine gebaute Zeitleiste, bevor der Katalog neu gelesen wird.</summary>
        Private Const MobileTimelineLifetimeMs As Long = 20_000

        ''' <summary>Hoechstens so viele Bilder je Seite der Zeitleiste.</summary>
        Private Const MaxMobilePage As Integer = 500

        ''' <summary>Hoechstens so viele Bilder in einer Liste ohne Seiten (Ordner, Suche, Person, Ort).</summary>
        Private Const MaxMobileList As Integer = 20_000

        Private NotInheritable Class MobileItem
            Public Path As String
            ''' <summary>Aufnahmezeit im Rohformat des Katalogs ("yyyy:MM:dd HH:mm:ss"), leer ohne.</summary>
            Public Taken As String
            ''' <summary>Kurzer Stand fuer die Adresse der Kachel: aendert er sich, holt der Browser
            ''' sie neu.</summary>
            Public Stamp As String
            ''' <summary>Das Farbetikett, wie es im Katalog steht ("#RRGGBB"), leer ohne.</summary>
            Public Label As String
            ''' <summary>Die Endungen der Partner, die diese RAW mitvertritt ("JPG"), leer ohne.</summary>
            Public Companions As String = ""
            ''' <summary>Das Zeichen fuer ein Bewegtbild auf der Kachel: 1 Motion Photo, 2 das Bild
            ''' eines Live Photos, dessen Film im Katalog steht; 0 ohne.</summary>
            Public Motion As Integer
        End Class

        Private NotInheritable Class MobileTimeline
            Public Items As List(Of MobileItem)
            Public Days As List(Of (Day As String, Count As Integer))
            Public BuiltAt As Long
            Public Stamp As String
        End Class

        Private Shared ReadOnly MobileTimelines As New Dictionary(Of String, MobileTimeline)(StringComparer.Ordinal)
        Private Shared ReadOnly MobileTimelineBuild As New SemaphoreSlim(1, 1)

        ''' <summary>Vergisst die gebauten Zeitleisten. Fuer den Pruefstand.</summary>
        Friend Shared Sub ForgetMobileTimelines()
            SyncLock MobileTimelines
                MobileTimelines.Clear()
            End SyncLock
        End Sub

        ''' <summary>Die Zeitleiste dieses Kontexts: gehalten fuer einige Sekunden, damit das
        ''' Nachladen der Seiten nicht jedes Mal den Katalog liest und die Reihenfolge zwischen zwei
        ''' Seiten stehen bleibt. Gebaut wird hoechstens eine zur Zeit.</summary>
        Private Shared Async Function MobileTimelineFor(mobile As MobileContext) As Task(Of MobileTimeline)
            Dim scope = mobile.ScopeKey
            Dim cached As MobileTimeline = Nothing
            SyncLock MobileTimelines
                If MobileTimelines.TryGetValue(scope, cached) AndAlso Environment.TickCount64 - cached.BuiltAt < MobileTimelineLifetimeMs Then Return cached
            End SyncLock
            Await MobileTimelineBuild.WaitAsync()
            Try
                SyncLock MobileTimelines
                    If MobileTimelines.TryGetValue(scope, cached) AndAlso Environment.TickCount64 - cached.BuiltAt < MobileTimelineLifetimeMs Then Return cached
                End SyncLock
                Dim built = Await Task.Run(Function() BuildMobileTimeline(mobile))
                SyncLock MobileTimelines
                    MobileTimelines(scope) = built
                End SyncLock
                Return built
            Finally
                MobileTimelineBuild.Release()
            End Try
        End Function

        Private Shared Function BuildMobileTimeline(mobile As MobileContext) As MobileTimeline
            Dim items As New List(Of MobileItem)()
            Dim folders As New MobileFolderCheck()
            Dim rows = LibraryService.Instance.GetMobileTimelineRows()
            ' Die Zeilen zusaetzlicher Varianten (Schluessel mit Kennung) tragen nur ihre eigenen
            ' Katalogdaten, keine Aufnahmezeit: sie kommen unten mit ihrer Datei.
            Dim variantRows As New Dictionary(Of String, String)(StringComparer.Ordinal)
            For Each row In rows
                If VariantKey.IsVariant(row.Path) Then variantRows(LibraryService.PathKey(row.Path)) = row.Label
            Next
            Dim filesWithVariantRows As New HashSet(Of String)(variantRows.Keys.Select(Function(k) VariantKey.FileOf(k)), PathIdentity.Comparer)
            For Each row In rows
                If VariantKey.IsVariant(row.Path) Then Continue For
                If Not MediaFileTypes.IsDisplayable(row.Path) OrElse Not mobile.CanSee(row.Path) Then Continue For
                ' Ein Ordner, den es nicht mehr gibt: der Katalog ist dort veraltet, jede Kachel
                ' bliebe leer. Ein Laufwerk, das gerade fehlt, bleibt (Kacheln aus dem Speicher).
                If Not folders.Exists(row.Path) Then Continue For
                Dim item As New MobileItem With {.Path = row.Path, .Taken = row.Taken, .Stamp = ShortStamp(row.Stamp), .Label = row.Label}
                items.Add(item)
                ' Varianten wie in der Galerie hinter ihrer Datei, mit deren Aufnahmezeit. Gelesen
                ' wird nur eine Begleitdatei, die im Ordner liegt (je Ordner einmal aufgelistet,
                ' neu nur nach einer Aenderung im Ordner) oder zu der der Katalog Varianten fuehrt.
                If RawPreviewService.IsSupportedRaw(row.Path) AndAlso (filesWithVariantRows.Contains(row.Path) OrElse folders.HasRecipe(row.Path)) Then
                    For Each variantItem In MobileVariantItems(item, variantRows)
                        items.Add(variantItem)
                    Next
                End If
            Next
            MarkMobileMotion(items, LibraryService.Instance.GetMobileMotionRows())
            items = SortByTaken(CollapsePairs(items))
            Dim days As New List(Of (Day As String, Count As Integer))()
            For Each item In items
                Dim day = DayOf(item.Taken)
                If days.Count > 0 AndAlso days(days.Count - 1).Day = day Then
                    days(days.Count - 1) = (day, days(days.Count - 1).Count + 1)
                Else
                    days.Add((day, 1))
                End If
            Next
            Return New MobileTimeline With {
                .Items = items, .Days = days, .BuiltAt = Environment.TickCount64, .Stamp = TimelineStamp(items)}
        End Function

        ''' <summary>Der Stand einer Zeitleiste aus ihrem Inhalt: Reihenfolge, Aufnahmezeit und Stand
        ''' jeder Kachel. Ein Neubau ohne Aenderung (alle 20 Sekunden) behaelt ihn; die Seite laedt dann
        ''' nicht neu. Ein zufaelliger Stand je Neubau liess sie bei jedem Nachladen einer Seite nach
        ''' 20 Sekunden alles verwerfen: die gerade geholten Bilder blieben leer, bis neu geladen war.</summary>
        Private Shared Function TimelineStamp(items As List(Of MobileItem)) As String
            Using hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                For Each item In items
                    hash.AppendData(Encoding.UTF8.GetBytes(item.Path & ChrW(1) & item.Taken & ChrW(1) & item.Stamp & ChrW(2)))
                Next
                Return Convert.ToHexString(hash.GetHashAndReset(), 0, 6).ToLowerInvariant()
            End Using
        End Function

        ''' <summary>Gibt es den Ordner einer Datei noch? Je Ordner einmal gefragt, damit eine Liste
        ''' aus dem Katalog nicht jede Datei einzeln ansieht (auf einer Netzfreigabe Sekunden). Ein
        ''' Ordner auf einem Laufwerk, das gerade nicht erreichbar ist, gilt als vorhanden.</summary>
        Private NotInheritable Class MobileFolderCheck
            Private ReadOnly _known As New Dictionary(Of String, Boolean)(PathIdentity.Comparer)
            Private ReadOnly _stamps As New Dictionary(Of String, Long)(PathIdentity.Comparer)

            ''' <summary>Die Begleitdateien je Ordner ueber alle Listen hinweg, mit dem Stand des
            ''' Ordners beim Auflisten: neu gelesen wird nur ein Ordner, in dem sich etwas getan hat.</summary>
            Private Shared ReadOnly Recipes As New Dictionary(Of String, (Stamp As Long, Files As HashSet(Of String)))(PathIdentity.Comparer)

            Public Function Exists(filePath As String) As Boolean
                Dim folder = IO.Path.GetDirectoryName(VariantKey.FileOf(filePath))
                If String.IsNullOrEmpty(folder) Then Return False
                Dim found As Boolean
                If _known.TryGetValue(folder, found) Then Return found
                Try
                    If OfflineVolumes.IsFolderUnreachable(folder) Then
                        found = True
                    Else
                        Dim info As New DirectoryInfo(folder)
                        found = info.Exists
                        If found Then _stamps(folder) = info.LastWriteTimeUtc.Ticks
                    End If
                Catch
                    found = False
                End Try
                _known(folder) = found
                Return found
            End Function

            ''' <summary>Liegt neben der Datei eine Begleitdatei (.fpxmp)? Je Ordner einmal
            ''' aufgelistet, wie die Galerie es fuer ihre Varianten tut; erst nach
            ''' <see cref="Exists"/> fuer denselben Ordner.</summary>
            Public Function HasRecipe(filePath As String) As Boolean
                Dim folder = IO.Path.GetDirectoryName(filePath)
                Dim stamp As Long
                If String.IsNullOrEmpty(folder) OrElse Not _stamps.TryGetValue(folder, stamp) Then Return False
                Dim entry As (Stamp As Long, Files As HashSet(Of String)) = Nothing
                SyncLock Recipes
                    If Not Recipes.TryGetValue(folder, entry) OrElse entry.Stamp <> stamp Then
                        Dim files As New HashSet(Of String)(PathIdentity.Comparer)
                        Try
                            For Each recipe In Directory.EnumerateFiles(folder, "*" & RawSidecarService.Extension)
                                files.Add(recipe)
                            Next
                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                        End Try
                        entry = (stamp, files)
                        Recipes(folder) = entry
                    End If
                End SyncLock
                Return entry.Files.Contains(RawSidecarService.SidecarPathFor(filePath))
            End Function
        End Class

        ''' <summary>Die Kacheln der zusaetzlichen Varianten einer Datei, wie die Galerie sie hinter
        ''' ihre Datei stellt (GalleryViewModel.WithVariantItems): aus der Begleitdatei, mit der
        ''' Aufnahmezeit der Datei und dem eigenen Etikett aus dem Katalog.</summary>
        Private Shared Function MobileVariantItems(fileItem As MobileItem, labels As Dictionary(Of String, String)) As List(Of MobileItem)
            Dim result As New List(Of MobileItem)()
            Dim variants As IReadOnlyList(Of RawSidecarService.SidecarVariant)
            Try
                variants = RawSidecarService.ReadVariants(fileItem.Path)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return result
            End Try
            For Each sidecarVariant In variants
                Dim key = VariantKey.Make(fileItem.Path, sidecarVariant.Id)
                Dim label As String = Nothing
                If labels Is Nothing OrElse Not labels.TryGetValue(LibraryService.PathKey(key), label) Then label = ""
                result.Add(New MobileItem With {.Path = key, .Taken = fileItem.Taken, .Stamp = ShortStamp(fileItem.Stamp & "|" & sidecarVariant.Id),
                                                .Label = If(label, "")})
            Next
            Return result
        End Function

        ''' <summary>Der Name, unter dem die Galerie ein Bild zeigt: der Dateiname, bei einer
        ''' Variante dahinter ihr Name in Klammern (ImageItem.VariantFileName).</summary>
        Friend Shared Function MobileDisplayName(key As String) As String
            Dim fileName = IO.Path.GetFileName(VariantKey.FileOf(key))
            If Not VariantKey.IsVariant(key) Then Return fileName
            Return Models.ImageItem.VariantFileName(fileName, RawSidecarService.VariantName(key))
        End Function

        ''' <summary>"yyyy-MM-dd" aus der Aufnahmezeit, leer ohne gueltiges Datum.</summary>
        Private Shared Function DayOf(taken As String) As String
            If String.IsNullOrEmpty(taken) OrElse taken.Length < 10 Then Return ""
            Dim day = taken.Substring(0, 10).Replace(":"c, "-"c)
            Dim parsed As DateTime
            If Not DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, parsed) Then Return ""
            Return day
        End Function

        ''' <summary>Die Aufnahmezeit fuer die Seite: ISO ohne Zone, leer ohne gueltiges Datum.</summary>
        Private Shared Function IsoOf(taken As String) As String
            Dim day = DayOf(taken)
            If day.Length = 0 Then Return ""
            If taken.Length >= 19 Then
                Dim clock = taken.Substring(11, 8)
                If clock.Length = 8 AndAlso clock(2) = ":"c AndAlso clock(5) = ":"c Then Return day & "T" & clock
            End If
            Return day & "T00:00:00"
        End Function

        Private Shared Function ShortStamp(text As String) As String
            ' FNV-1a: kurz und fest; nur ein Merkmal fuer den Zwischenspeicher des Browsers.
            Dim hash As UInteger = 2166136261UI
            For Each c In If(text, "")
                hash = CUInt(((hash Xor CUInt(AscW(c))) * 16777619UL) And &HFFFFFFFFUL)
            Next
            Return hash.ToString("x8", CultureInfo.InvariantCulture)
        End Function

        ''' <summary>Setzt das Zeichen fuer ein Bewegtbild, wie die Galerie ihr Abzeichen: ein Motion
        ''' Photo immer, das Bild eines Live Photos nur, wenn sein Film (dieselbe Kennung) unter den
        ''' bekannten Zeilen steht. Der Film selbst traegt schon das Zeichen eines Videos.</summary>
        Private Shared Sub MarkMobileMotion(items As List(Of MobileItem), motionOf As IDictionary(Of String, (Kind As MotionKind, Id As String)))
            If motionOf Is Nothing OrElse motionOf.Count = 0 Then Return
            Dim liveVideos As New HashSet(Of String)(StringComparer.Ordinal)
            For Each entry In motionOf.Values
                If entry.Kind = MotionKind.LivePhotoVideo AndAlso Not String.IsNullOrEmpty(entry.Id) Then liveVideos.Add(entry.Id)
            Next
            For Each item In items
                Dim entry As (Kind As MotionKind, Id As String) = Nothing
                If Not motionOf.TryGetValue(item.Path, entry) Then Continue For
                If entry.Kind = MotionKind.MotionPhoto Then
                    item.Motion = 1
                ElseIf entry.Kind = MotionKind.LivePhotoImage AndAlso liveVideos.Contains(If(entry.Id, "")) Then
                    item.Motion = 2
                End If
            Next
        End Sub

        ''' <summary>Dasselbe aus den schon gelesenen Zeilen einer Liste (Ordner, Suche): ein Live
        ''' Photo zaehlt dort, wenn sein Film in derselben Liste steht.</summary>
        Private Shared Sub MarkMobileMotion(items As List(Of MobileItem), metas As Dictionary(Of String, LibraryImageMeta))
            Dim motionOf As New Dictionary(Of String, (Kind As MotionKind, Id As String))(PathIdentity.Comparer)
            For Each pair In metas
                If pair.Value IsNot Nothing AndAlso pair.Value.MotionKind <> MotionKind.None Then motionOf(pair.Key) = (pair.Value.MotionKind, If(pair.Value.MotionId, ""))
            Next
            MarkMobileMotion(items, motionOf)
        End Sub

        ''' <summary>Neueste zuerst, Bilder ohne Datum am Ende, gleiche Zeit nach Pfad.</summary>
        Private Shared Function SortByTaken(items As List(Of MobileItem)) As List(Of MobileItem)
            Return items.OrderBy(Function(i) If(DayOf(i.Taken).Length = 0, 1, 0)).
                         ThenByDescending(Function(i) i.Taken, StringComparer.Ordinal).
                         ThenBy(Function(i) i.Path, StringComparer.Ordinal).ToList()
        End Function

        Private Shared Function PairKey(path As String) As String
            Return LibraryService.PathKey(IO.Path.Combine(If(IO.Path.GetDirectoryName(path), ""), IO.Path.GetFileNameWithoutExtension(path)))
        End Function

        ''' <summary>Laesst das JPEG (oder HEIF) neben einer RAW gleichen Namens weg; die RAW steht fuer
        ''' beide. Wie bei der Zaehlung der Personen (LibraryService.CountPhotos) zaehlen Aufnahmezeit
        ''' und Kamera dabei nicht.</summary>
        Private Shared Function CollapsePairs(items As List(Of MobileItem)) As List(Of MobileItem)
            Dim rawStems As New Dictionary(Of String, MobileItem)(StringComparer.Ordinal)
            For Each item In items
                If RawPreviewService.IsCameraRaw(item.Path) Then rawStems(PairKey(item.Path)) = item
            Next
            If rawStems.Count = 0 Then Return items
            Dim result As New List(Of MobileItem)(items.Count)
            For Each item In items
                Dim raw As MobileItem = Nothing
                If Not RawPreviewService.IsCameraRaw(item.Path) AndAlso ImageStackService.IsCompanionFormat(item.Path) AndAlso
                   rawStems.TryGetValue(PairKey(item.Path), raw) Then
                    ' Die RAW nennt ihren Partner ("+JPG"), damit man weiss, dass es zwei Dateien sind.
                    Dim extension = IO.Path.GetExtension(item.Path).TrimStart("."c).ToUpperInvariant()
                    If extension = "JPEG" Then extension = "JPG"
                    If Not raw.Companions.Split(","c).Contains(extension) Then
                        raw.Companions = If(raw.Companions.Length = 0, extension, raw.Companions & "," & extension)
                    End If
                    Continue For
                End If
                result.Add(item)
            Next
            Return result
        End Function

        ''' <summary>Die Partner einer RAW: JPEG oder HEIF gleichen Namens im selben Ordner. Sie bekommen
        ''' dieselbe Bewertung wie die RAW, wie die verborgenen Partner in der Galerie.</summary>
        Friend Shared Function MobilePartnersOf(path As String) As List(Of String)
            Dim result As New List(Of String)()
            If Not RawPreviewService.IsCameraRaw(path) Then Return result
            Dim folder = IO.Path.GetDirectoryName(path)
            Dim stem = IO.Path.GetFileNameWithoutExtension(path)
            If String.IsNullOrEmpty(folder) OrElse stem.IndexOfAny({"*"c, "?"c}) >= 0 Then Return result
            Try
                For Each candidate In Directory.EnumerateFiles(folder, stem & ".*")
                    If PathIdentity.AreSame(candidate, path) Then Continue For
                    If Not String.Equals(IO.Path.GetFileNameWithoutExtension(candidate), stem, PathIdentity.Comparison) Then Continue For
                    If ImageStackService.IsCompanionFormat(candidate) AndAlso Not RawPreviewService.IsCameraRaw(candidate) Then result.Add(candidate)
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Return result
        End Function

        ''' <summary>Ein Bild fuer die Seite: Kennung, Aufnahmezeit, Video ja oder nein, Stand.
        ''' Fuer einen Benutzer ohne Administratorrolle dazu seine Stufe an der Datei ("r",
        ''' FolderAccessLevel als Zahl): die Aktionsleiste der Auswahl blendet damit aus, wofuer die
        ''' Stufe bei keinem gewaehlten Bild reicht. Ohne "r" gilt Verwalten (Administrator). Reine
        ''' Zeichenkettenarbeit wie CanSee; geprueft wird jeder Weg trotzdem am Server.</summary>
        Private Shared Function MobileItemJson(item As MobileItem, mobile As MobileContext) As Dictionary(Of String, Object)
            Dim result As New Dictionary(Of String, Object) From {
                {"i", MobileIdFor(MobileFileKind, item.Path)},
                {"t", IsoOf(item.Taken)},
                {"s", item.Stamp},
                {"n", MobileDisplayName(item.Path)}}
            ' Die Partner, die die RAW mitvertritt ("JPG"), fuer den Hinweis "+JPG".
            If item.Companions.Length > 0 Then result("j") = item.Companions
            If MediaFileTypes.IsVideo(item.Path) Then result("v") = 1
            If item.Motion > 0 Then result("m") = item.Motion
            ' Das Farbetikett fuer den Punkt auf der Kachel, in der Schreibweise der Galerie.
            Dim label = If(item.Label, "").Trim().ToUpperInvariant()
            If label.Length > 0 Then result("l") = label
            If mobile IsNot Nothing AndAlso Not mobile.IsAdmin Then result("r") = CInt(mobile.LevelAt(VariantKey.FileOf(item.Path)))
            Return result
        End Function

        Private Shared Function MobileListJson(items As IEnumerable(Of MobileItem), mobile As MobileContext) As List(Of Dictionary(Of String, Object))
            Return items.Take(MaxMobileList).Select(Function(item) MobileItemJson(item, mobile)).ToList()
        End Function

        ''' <summary>Bilder aus Pfaden des Katalogs (Suche, Person, Ort): gefiltert durch den
        ''' Kontext, Paare zusammengelegt, neueste zuerst.</summary>
        Private Shared Function MobileItemsFromPaths(mobile As MobileContext, paths As IEnumerable(Of String)) As List(Of MobileItem)
            ' Auch Varianten (aus der Suche): sichtbar, wenn ihre Datei es ist. Ordner, die es nicht
            ' mehr gibt, fallen weg wie in der Zeitleiste.
            Dim folders As New MobileFolderCheck()
            Dim visible = paths.Where(Function(p) MediaFileTypes.IsDisplayable(VariantKey.FileOf(p)) AndAlso mobile.CanSeeItem(p) AndAlso folders.Exists(p)).
                                Distinct(PathIdentity.Comparer).ToList()
            ' Eine Variante hat im Katalog nur ihre eigenen Markierungen; Aufnahmezeit und Stand
            ' kommen von ihrer Datei.
            Dim metas = LibraryService.Instance.GetMetaForPaths(visible.Concat(visible.Where(AddressOf VariantKey.IsVariant).Select(AddressOf VariantKey.FileOf)).Distinct(PathIdentity.Comparer))
            Dim items As New List(Of MobileItem)(visible.Count)
            For Each path In visible
                Dim meta As LibraryImageMeta = Nothing
                metas.TryGetValue(path, meta)
                Dim fileMeta As LibraryImageMeta = meta
                If VariantKey.IsVariant(path) Then
                    fileMeta = Nothing
                    metas.TryGetValue(VariantKey.FileOf(path), fileMeta)
                End If
                items.Add(New MobileItem With {
                    .Path = path, .Taken = If(fileMeta?.DateTaken, ""), .Label = If(meta?.ColorLabel, ""),
                    .Stamp = ShortStamp(If(fileMeta?.ScannedSourceModifiedAt, "") & "|" & If(fileMeta?.ScannedSidecarModifiedAt, "") & "|" & VariantKey.IdOf(path))})
            Next
            MarkMobileMotion(items, metas)
            Return SortByTaken(CollapsePairs(items))
        End Function

        ' --- Zeitleiste --------------------------------------------------------------------------

        ''' <summary>GET /api/m/timeline?offset=&amp;limit=&amp;days=1. Eine Seite der Zeitleiste;
        ''' mit days=1 dazu die Tage samt Anzahl, aus denen die Seite ihr Raster und die Sprungleiste
        ''' rechnet, ohne alle Bilder zu laden. "stamp" wechselt, wenn die Zeitleiste neu gebaut
        ''' wurde; dann holt die Seite die Tage neu.</summary>
        Private Shared Async Function HandleMobileTimelineAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim timeline = Await MobileTimelineFor(mobile)
            Dim offset = Math.Max(0, CInt(Math.Clamp(ReadQueryInteger(context.Request, "offset", 0), 0, Integer.MaxValue)))
            Dim limit = CInt(Math.Clamp(ReadQueryInteger(context.Request, "limit", 200), 1, MaxMobilePage))
            Dim page = timeline.Items.Skip(offset).Take(limit).Select(Function(item) MobileItemJson(item, mobile)).ToList()
            Dim result As New Dictionary(Of String, Object) From {
                {"stamp", timeline.Stamp}, {"total", timeline.Items.Count}, {"offset", offset}, {"items", page}}
            If context.Request.QueryString("days") = "1" Then
                result("days") = timeline.Days.Select(Function(d) New Object() {d.Day, d.Count}).ToList()
            End If
            Await WriteMobileJsonAsync(context, 200, result)
        End Function

        Private Shared Function ReadQueryInteger(request As HttpListenerRequest, name As String, fallback As Long) As Long
            Dim value As Long
            If Long.TryParse(request.QueryString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, value) Then Return value
            Return fallback
        End Function

        ' --- Ordner -------------------------------------------------------------------------------

        ''' <summary>GET /api/m/folder?id=. Ohne Kennung die Wurzeln, sonst die Unterordner und Bilder
        ''' des Ordners, neueste zuerst. "upload" sagt, ob hier hochgeladen werden darf.</summary>
        Private Shared Async Function HandleMobileFolderAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim id = If(context.Request.QueryString("id"), "")
            If id.Length = 0 Then
                Dim roots = Await Task.Run(Function() mobile.BrowseRoots())
                Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {
                    {"id", ""}, {"name", ""}, {"parent", Nothing}, {"upload", False},
                    {"folders", roots.Select(Function(r) FolderJson(r)).ToList()},
                    {"items", New List(Of Object)()}})
                Return
            End If
            Dim folder = mobile.ResolveFolder(id)
            If folder Is Nothing Then
                Await WriteMobileJsonAsync(context, 404, MobileProblem("not found"))
                Return
            End If
            Dim result = Await Task.Run(Function() ListMobileFolder(mobile, folder))
            Await WriteMobileJsonAsync(context, 200, result)
        End Function

        Private Shared Function FolderJson(folder As String) As Dictionary(Of String, Object)
            Return New Dictionary(Of String, Object) From {
                {"i", MobileIdFor(MobileFolderKind, folder)}, {"n", FolderDisplayName(folder)}}
        End Function

        Private Shared Function ListMobileFolder(mobile As MobileContext, folder As String) As Dictionary(Of String, Object)
            Dim folders As New List(Of String)()
            Dim files As New List(Of FileInfo)()
            Try
                Dim listing As New DirectoryInfo(folder)
                For Each entry In listing.EnumerateFileSystemInfos()
                    If entry.Name.StartsWith("."c) OrElse (entry.Attributes And (FileAttributes.Hidden Or FileAttributes.System)) <> 0 Then Continue For
                    If TypeOf entry Is DirectoryInfo Then
                        If mobile.CanSeeFolder(entry.FullName) Then folders.Add(entry.FullName)
                    ElseIf MediaFileTypes.IsDisplayable(entry.Name) AndAlso mobile.CanSee(entry.FullName) Then
                        files.Add(DirectCast(entry, FileInfo))
                    End If
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            folders.Sort(Function(a, b) String.Compare(IO.Path.GetFileName(a), IO.Path.GetFileName(b), StringComparison.CurrentCultureIgnoreCase))

            Dim metas = LibraryService.Instance.GetMetaForPaths(files.Select(Function(f) f.FullName))
            Dim items As New List(Of MobileItem)(files.Count)
            For Each info In files
                Dim meta As LibraryImageMeta = Nothing
                metas.TryGetValue(info.FullName, meta)
                Dim taken = If(meta?.DateTaken, "")
                ' Ohne Katalogzeile gilt die Aenderungszeit der Datei, damit auch ein frisch
                ' hochgeladenes Bild an seinem Platz steht.
                If DayOf(taken).Length = 0 Then
                    Try
                        taken = info.LastWriteTime.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)
                    Catch
                        taken = ""
                    End Try
                End If
                Dim stamp As String
                Try
                    stamp = ShortStamp(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) & "|" &
                                       info.Length.ToString(CultureInfo.InvariantCulture) & "|" &
                                       If(meta?.ScannedSidecarModifiedAt, ""))
                Catch
                    stamp = ""
                End Try
                items.Add(New MobileItem With {.Path = info.FullName, .Taken = taken, .Stamp = stamp, .Label = If(meta?.ColorLabel, "")})
            Next
            ' Varianten wie in der Galerie hinter ihrer Datei; gelesen werden nur die Begleitdateien,
            ' die es in diesem Ordner gibt.
            Dim recipes As HashSet(Of String) = Nothing
            Try
                recipes = New HashSet(Of String)(Directory.EnumerateFiles(folder, "*" & RawSidecarService.Extension), PathIdentity.Comparer)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            If recipes IsNot Nothing AndAlso recipes.Count > 0 Then
                Dim variantItems As New List(Of MobileItem)()
                For Each item In items
                    If Not RawPreviewService.IsSupportedRaw(item.Path) OrElse Not recipes.Contains(RawSidecarService.SidecarPathFor(item.Path)) Then Continue For
                    variantItems.AddRange(MobileVariantItems(item, Nothing))
                Next
                If variantItems.Count > 0 Then
                    Dim variantMetas = LibraryService.Instance.GetMetaForPaths(variantItems.Select(Function(v) v.Path))
                    For Each variantItem In variantItems
                        Dim variantMeta As LibraryImageMeta = Nothing
                        If variantMetas.TryGetValue(variantItem.Path, variantMeta) Then variantItem.Label = If(variantMeta.ColorLabel, "")
                    Next
                    items.AddRange(variantItems)
                End If
            End If
            MarkMobileMotion(items, metas)
            items = SortByTaken(CollapsePairs(items))

            Dim parent = IO.Path.GetDirectoryName(folder)
            Dim isRoot = mobile.BrowseRoots().Any(Function(r) PathIdentity.AreSame(r, folder))
            Dim parentId As Object = ""
            If Not isRoot AndAlso Not String.IsNullOrEmpty(parent) AndAlso mobile.CanSeeFolder(parent) Then
                parentId = MobileIdFor(MobileFolderKind, parent)
            End If
            Return New Dictionary(Of String, Object) From {
                {"id", MobileIdFor(MobileFolderKind, folder)},
                {"name", FolderDisplayName(folder)},
                {"parent", parentId},
                {"upload", mobile.CanUpload(folder)},
                {"share", CanShareFolder(mobile, folder)},
                {"folders", folders.Select(Function(f) FolderJson(f)).ToList()},
                {"items", MobileListJson(items, mobile)}}
        End Function

        ' --- Suche, Personen, Orte ----------------------------------------------------------------

        ''' <summary>GET /api/m/search?q=. Derselbe Suchtext wie in der Galerie: dieselbe Auswertung
        ''' (SearchTextQuery: +Wort, -Wort, freie Woerter als ODER, Anfuehrungszeichen, OR/ODER,
        ''' Muster) gegen denselben Wortvorrat (SearchVocabulary: Dateiname, Stichwoerter, Katalogtexte,
        ''' Ort, Bewertung und Favorit, KI-Begriffe, Personen).</summary>
        Private Shared Async Function HandleMobileSearchAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim query = If(context.Request.QueryString("q"), "").Trim()
            If query.Length > 200 Then query = query.Substring(0, 200)
            Dim items = Await Task.Run(
                Function()
                    If query.Length = 0 Then Return New List(Of MobileItem)()
                    Return MobileItemsFromPaths(mobile, MobileSearchPaths(mobile, query))
                End Function)
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"items", MobileListJson(items, mobile)}})
        End Function

        ''' <summary>Die Treffer eines Suchtexts unter den Bildern, die dieser Benutzer sieht. Die
        ''' Kandidaten kommen aus der Sicht des Kontexts (CanSee) VOR dem Vergleich: was er nicht sehen
        ''' darf, wird gar nicht erst verglichen und kommt in keiner Zahl vor.</summary>
        Friend Shared Function MobileSearchPaths(mobile As MobileContext, query As String) As List(Of String)
            Dim result As New List(Of String)()
            Dim parsed = SearchTextQuery.ParseSearchText(query)
            If parsed.IsEmpty Then Return result
            Dim library = LibraryService.Instance
            Dim visible = library.GetMobileTimelineRows().
                Select(Function(r) r.Path).
                Where(Function(p) MediaFileTypes.IsDisplayable(VariantKey.FileOf(p)) AndAlso mobile.CanSeeItem(p)).ToList()
            Dim metas = library.GetMetaForPaths(visible)
            For Each path In visible
                Dim meta As LibraryImageMeta = Nothing
                If Not metas.TryGetValue(path, meta) Then Continue For
                ' Eine Variante unter ihrem Namen, wie die Galerie sie zeigt.
                If SearchTextQuery.MatchesParsedSearchText(parsed, MobileDisplayName(path), SearchVocabulary.ForMetaWithCatalogWords(meta)) Then
                    result.Add(path)
                End If
            Next
            Return result
        End Function

        ''' <summary>GET /api/m/suggest?q=: Personen und Orte zum zuletzt getippten Wort, als
        ''' Vorschlaege zum Einfuegen. Nur Namen, die auf Bildern vorkommen, die dieser Benutzer
        ''' sieht; hoechstens acht.</summary>
        Private Shared Async Function HandleMobileSuggestAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim query = If(context.Request.QueryString("q"), "").Trim()
            If query.Length > 100 Then query = query.Substring(0, 100)
            Dim found = Await Task.Run(Function() MobileSuggestions(mobile, query))
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"suggestions", found}})
        End Function

        Friend Shared Function MobileSuggestions(mobile As MobileContext, word As String) As List(Of Dictionary(Of String, Object))
            Dim result As New List(Of Dictionary(Of String, Object))()
            word = If(word, "").Trim().TrimStart("+"c, "-"c).Trim(""""c)
            If word.Length < 1 Then Return result
            Dim library = LibraryService.Instance
            Dim names = library.GetPeople().Where(Function(p) Not p.IsUnknownBin AndAlso p.Name.Trim().Length > 0).
                                            ToDictionary(Function(p) p.Id, Function(p) p.Name.Trim(), StringComparer.Ordinal)
            Dim people As New SortedSet(Of String)(StringComparer.CurrentCultureIgnoreCase)
            For Each row In library.GetMobilePersonRows()
                Dim name As String = Nothing
                If Not names.TryGetValue(row.PersonId, name) OrElse people.Contains(name) Then Continue For
                If name.Contains(word, StringComparison.CurrentCultureIgnoreCase) AndAlso mobile.CanSee(row.Path) Then people.Add(name)
            Next
            Dim places As New SortedSet(Of String)(StringComparer.CurrentCultureIgnoreCase)
            For Each row In library.GetMobilePlaceRows()
                Dim city = row.City.Trim()
                If city.Length = 0 OrElse places.Contains(city) Then Continue For
                If city.Contains(word, StringComparison.CurrentCultureIgnoreCase) AndAlso mobile.CanSee(row.Path) Then places.Add(city)
            Next
            For Each name In people.Take(8)
                result.Add(New Dictionary(Of String, Object) From {{"k", "person"}, {"n", name}})
            Next
            For Each city In places.Take(8 - result.Count)
                result.Add(New Dictionary(Of String, Object) From {{"k", "place"}, {"n", city}})
            Next
            Return result
        End Function

        ''' <summary>GET /api/m/people: die benannten Personen mit der Zahl ihrer sichtbaren Fotos.
        ''' Die Sammelgruppe und unbenannte Gruppen fehlen.</summary>
        Private Shared Async Function HandleMobilePeopleAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim people = Await Task.Run(
                Function()
                    Dim library = LibraryService.Instance
                    Dim names = library.GetPeople().Where(Function(p) Not p.IsUnknownBin AndAlso p.Name.Trim().Length > 0).
                                                    ToDictionary(Function(p) p.Id, Function(p) p.Name, StringComparer.Ordinal)
                    Dim filesByPerson As New Dictionary(Of String, List(Of String))(StringComparer.Ordinal)
                    For Each row In library.GetMobilePersonRows()
                        If Not names.ContainsKey(row.PersonId) OrElse Not mobile.CanSee(row.Path) Then Continue For
                        Dim files As List(Of String) = Nothing
                        If Not filesByPerson.TryGetValue(row.PersonId, files) Then
                            files = New List(Of String)()
                            filesByPerson(row.PersonId) = files
                        End If
                        files.Add(row.Path)
                    Next
                    Return filesByPerson.Select(Function(pair) New Dictionary(Of String, Object) From {
                                                    {"i", pair.Key}, {"n", names(pair.Key)},
                                                    {"c", LibraryService.CountPhotos(pair.Value)}}).
                                         OrderBy(Function(p) CStr(p("n")), StringComparer.CurrentCultureIgnoreCase).ToList()
                End Function)
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"people", people}})
        End Function

        ''' <summary>Eine Personenkennung des Katalogs, wie sie die Seite schickt: nur Buchstaben,
        ''' Ziffern und Bindestrich. Was sonst kommt, fuehrt zu keiner Person.</summary>
        Private Shared Function IsPersonId(id As String) As Boolean
            Return Not String.IsNullOrEmpty(id) AndAlso id.Length <= 64 AndAlso id.All(Function(c) Char.IsAsciiLetterOrDigit(c) OrElse c = "-"c)
        End Function

        ''' <summary>GET /api/m/person?id=: die sichtbaren Fotos einer benannten Person.</summary>
        Private Shared Async Function HandleMobilePersonAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim id = If(context.Request.QueryString("id"), "")
            If Not IsPersonId(id) Then
                Await WriteMobileJsonAsync(context, 404, MobileProblem("not found"))
                Return
            End If
            Dim items = Await Task.Run(Function() MobileItemsFromPaths(mobile, LibraryService.Instance.GetPathsForPeople({id})))
            ' Eine Person ohne ein Bild, das dieser Benutzer sehen darf, antwortet wie eine unbekannte.
            If items.Count = 0 Then
                Await WriteMobileJsonAsync(context, 404, MobileProblem("not found"))
                Return
            End If
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"items", MobileListJson(items, mobile)}})
        End Function

        ''' <summary>GET /api/m/places: die Orte mit der Zahl ihrer sichtbaren Fotos, die haeufigsten
        ''' zuerst. Der Landesname in der Sprache der Anwendung.</summary>
        Private Shared Async Function HandleMobilePlacesAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim places = Await Task.Run(
                Function()
                    Dim groups As New Dictionary(Of String, (Name As String, Files As List(Of String)))(StringComparer.OrdinalIgnoreCase)
                    For Each row In LibraryService.Instance.GetMobilePlaceRows()
                        If Not mobile.CanSee(row.Path) Then Continue For
                        Dim city = row.City.Trim()
                        If city.Length = 0 Then Continue For
                        Dim group As (Name As String, Files As List(Of String)) = Nothing
                        If Not groups.TryGetValue(city, group) Then
                            group = (PlacePanelService.Compose(city, PlaceLookupService.LocalizedCountry(row.CountryCode, row.Country)), New List(Of String)())
                            groups(city) = group
                        End If
                        group.Files.Add(row.Path)
                    Next
                    Return groups.Select(Function(pair) New Dictionary(Of String, Object) From {
                                             {"i", MobileIdFor(MobilePlaceKind, pair.Key)}, {"n", pair.Value.Name},
                                             {"c", LibraryService.CountPhotos(pair.Value.Files)}}).
                                  OrderByDescending(Function(p) CInt(p("c"))).
                                  ThenBy(Function(p) CStr(p("n")), StringComparer.CurrentCultureIgnoreCase).ToList()
                End Function)
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"places", places}})
        End Function

        ''' <summary>GET /api/m/place?id=: die sichtbaren Fotos eines Orts.</summary>
        Private Shared Async Function HandleMobilePlaceAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim city = MobileValueOf(MobilePlaceKind, context.Request.QueryString("id"))
            If city Is Nothing Then
                Await WriteMobileJsonAsync(context, 404, MobileProblem("not found"))
                Return
            End If
            Dim items = Await Task.Run(Function() MobileItemsFromPaths(mobile, LibraryService.Instance.GetPathsForPlaces({city})))
            If items.Count = 0 Then
                Await WriteMobileJsonAsync(context, 404, MobileProblem("not found"))
                Return
            End If
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"items", MobileListJson(items, mobile)}})
        End Function

        ' --- Infoblatt --------------------------------------------------------------------------

        ''' <summary>GET /api/m/info?id=: was das Infoblatt zeigt, dazu Bewertung und Markierungen fuer
        ''' die Leiste des Vollbilds. Die Belichtung kommt als Teile; die Seite fuegt sie zusammen.</summary>
        Private Shared Async Function HandleMobileInfoAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            ' Datei oder Variante. Auch eine Datei, die es nicht mehr gibt, hat ein Infoblatt: Name
            ' und Ordner, und "missing" sagt der Seite, dass nichts davon zu holen ist.
            Dim path = mobile.ResolveImage(context.Request.QueryString("id"))
            If path Is Nothing Then
                Await WriteMobileJsonAsync(context, 404, MobileProblem(LocalizationService.T("Das Bild konnte nicht geladen werden")))
                Return
            End If
            Dim result = Await Task.Run(Function() MobileInfoOf(mobile, path))
            Await WriteMobileJsonAsync(context, 200, result)
        End Function

        ''' <summary>Das Format fuer das Infoblatt: die Endung in Grossbuchstaben, bei einer RAW
        ''' "RAW (CR3)".</summary>
        Friend Shared Function MobileFormatName(path As String) As String
            Dim extension = IO.Path.GetExtension(VariantKey.FileOf(path)).TrimStart("."c).ToUpperInvariant()
            If extension = "JPEG" Then extension = "JPG"
            If RawPreviewService.IsSupportedRaw(VariantKey.FileOf(path)) Then Return "RAW (" & extension & ")"
            Return extension
        End Function

        Private Shared Function MobileInfoOf(mobile As MobileContext, key As String) As Dictionary(Of String, Object)
            Dim library = LibraryService.Instance
            Dim path = VariantKey.FileOf(key)
            Dim isVariant = VariantKey.IsVariant(key)
            ' Markierungen gehoeren der Variante, Aufnahmedaten der Datei.
            Dim metas = library.GetMetaForPaths(If(isVariant, {key, path}, {path}))
            Dim meta As LibraryImageMeta = Nothing
            metas.TryGetValue(path, meta)
            Dim result = MobileMarksOf(key)
            result("name") = MobileDisplayName(key)
            result("fileName") = IO.Path.GetFileName(path)
            If isVariant Then result("variant") = RawSidecarService.VariantName(key)
            result("format") = MobileFormatName(path)
            Dim folder = IO.Path.GetDirectoryName(path)
            result("folder") = FolderDisplayName(If(folder, ""))
            If Not String.IsNullOrEmpty(folder) AndAlso mobile.CanSeeFolder(folder) Then result("folderId") = MobileIdFor(MobileFolderKind, folder)
            result("video") = MediaFileTypes.IsVideo(path)
            Dim exists = MobileContext.ImageExists(key)
            If Not exists Then result("missing") = True
            ' Partner nur an der Datei selbst; eine Variante steht fuer sich.
            Dim partners = If(isVariant OrElse Not exists, New List(Of String)(), MobilePartnersOf(path).Where(Function(p) mobile.CanSee(p)).ToList())
            result("partners") = partners.Select(Function(p) IO.Path.GetFileName(p)).ToList()
            ' Was die Seite anbietet; der Server prueft jeden Weg trotzdem selbst. Eine Variante
            ' wird bewertet, aber nicht umbenannt, geloescht oder geteilt: das trifft ihre Datei.
            result("canRate") = exists AndAlso mobile.CanRate(path) AndAlso partners.All(Function(p) mobile.CanRate(p))
            Dim canManage = exists AndAlso Not isVariant AndAlso mobile.CanRename(path) AndAlso mobile.CanDelete(path) AndAlso
                            partners.All(Function(p) mobile.CanRename(p) AndAlso mobile.CanDelete(p))
            result("canManage") = canManage
            ' Loescht die Anwendung ohne Papierkorb (Einstellung), sagt die Rueckfrage der Seite
            ' das ausdruecklich: dann ist das Bild endgueltig weg.
            If canManage Then result("deleteForever") = AppSettingsService.Load().DeleteSkipTrash
            ' Link zum Teilen: "Mitarbeiten" auf dem Bild (BrowserBridgeShares.vb).
            result("canShare") = exists AndAlso Not isVariant AndAlso CanShareFile(mobile, path)
            ' Ein Bewegtbild: die Seite zeigt den Knopf, der den Film holt (/api/m/motion), mit
            ' denselben Rechten wie das Original.
            If exists AndAlso mobile.CanDownload(path) Then
                Dim clip = MobileMotionClipOf(path)
                If clip.Path IsNot Nothing AndAlso mobile.CanDownload(clip.Path) Then result("motion") = If(clip.Embedded, "photo", "live")
            End If
            Try
                If exists Then result("bytes") = New FileInfo(path).Length
            Catch
            End Try
            If meta IsNot Nothing Then
                result("taken") = IsoOf(ExifService.ToDisplayTime(meta.DateTaken, meta.DateTakenOffset))
                result("camera") = meta.Camera
                result("lens") = meta.Lens
                Dim exposure As New List(Of String)()
                If meta.Aperture.HasValue AndAlso meta.Aperture.Value > 0 Then exposure.Add("f/" & meta.Aperture.Value.ToString("0.#", CultureInfo.CurrentCulture))
                If Not String.IsNullOrWhiteSpace(meta.ShutterSpeed) Then exposure.Add(meta.ShutterSpeed.Trim() & " s")
                If meta.Iso.HasValue AndAlso meta.Iso.Value > 0 Then exposure.Add("ISO " & meta.Iso.Value.ToString(CultureInfo.InvariantCulture))
                If meta.FocalLengthMm.HasValue AndAlso meta.FocalLengthMm.Value > 0 Then exposure.Add(meta.FocalLengthMm.Value.ToString("0.#", CultureInfo.CurrentCulture) & " mm")
                result("exposure") = exposure
                result("place") = PlacePanelService.Compose(meta.City, PlaceLookupService.LocalizedCountry(meta.CountryCode, meta.Country))
                result("tags") = meta.Tags
                If meta.ImageWidth.HasValue AndAlso meta.ImageHeight.HasValue Then
                    result("width") = meta.ImageWidth.Value
                    result("height") = meta.ImageHeight.Value
                End If
            End If
            result("people") = library.GetPeopleForImage(path).Where(Function(p) Not p.IsUnknownBin AndAlso p.Name.Trim().Length > 0).
                                       Select(Function(p) p.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList()
            Return result
        End Function

    End Class

End Namespace
