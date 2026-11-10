Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Xml.Linq
Imports Microsoft.Data.Sqlite

Namespace Services

    ''' <summary>Liest einen Katalog des Referenzprogramms (.lrcat) fuer den Katalog von FerrumPix:
    ''' Bewertung, Farbetikett, Markierung, Stichwoerter und die Entwicklung je Foto, dazu eine dort
    ''' geaenderte Aufnahmezeit (ueber ExifService.ReadExif, siehe <see cref="EditedCaptureTime"/>) sowie
    ''' Copyright, Koordinate, Ort und Land (<see cref="ImportedMetadataFor"/>).
    '''
    ''' GLEICHER WEG WIE DIE .pp3: dieselbe Einstellung schaltet ihn ein
    ''' (<see cref="AppSettings.ReadPp3Sidecars"/>), dazu der Pfad des Katalogs
    ''' (<see cref="AppSettings.ForeignCatalogPath"/>). Was er liefert, geht ueber
    ''' <see cref="Pp3SidecarService.Read"/> in Ordnerlauf und Katalogindex, mit denselben Regeln:
    ''' gefuellt wird nur, was im Katalog von FerrumPix leer ist, Stichwoerter werden vereinigt. Die
    ''' Entwicklung geht wie die einer XMP-Beistelldatei einmalig in eine neue .fpxmp, nur bei RAW und
    ''' PSD ohne eigene (RawSidecarService.TryImportFromForeignCatalog).
    '''
    ''' NUR LESEN. Die Datei ist eine SQLite-Datenbank; geoeffnet wird sie schreibgeschuetzt und,
    ''' ohne Schreibprotokoll daneben, als unveraenderlich, dann legt SQLite auch nichts neben ihr an.
    ''' Liegt "&lt;katalog&gt;.lock" daneben, hat das Referenzprogramm ihn gerade offen; dann bleibt es beim
    ''' zuletzt gelesenen Stand, gelesen wird nach dem Schliessen. Liegt ein Schreibprotokoll
    ''' ("-wal") mit Inhalt daneben, stehen die neuesten Aenderungen dort und werden mitgelesen.
    '''
    ''' WELCHES FOTO: Das Referenzprogramm kennt absolute Pfade des Rechners, auf dem der Katalog lag
    ''' ("C:/Fotos/2024/..."). Zugeordnet wird deshalb ueber den Dateinamen und das laengste
    ''' gemeinsame Pfadende, mindestens Datei und Ordner darueber; zwei gleich gute Kandidaten heissen
    ''' keiner. So greift ein Katalog von einem anderen Rechner, solange die Ordner unter ihren Namen
    ''' mitgewandert sind, und ein IMG_0001.CR2 aus einem ganz anderen Ordner bekommt nichts.
    ''' Virtuelle Kopien bleiben aussen vor: sie haben bei uns kein Gegenstueck.
    '''
    ''' DAS SCHEMA (Tabellen und Spalten, so wie das Referenzprogramm sie seit Jahren fuehrt):
    ''' Adobe_images (rating, pick, colorLabels, rootFile, masterImage, captureTime,
    ''' originalCaptureTime), AgLibraryFile (baseName,
    ''' extension, folder), AgLibraryFolder (pathFromRoot, rootFolder), AgLibraryRootFolder
    ''' (absolutePath), AgLibraryKeywordImage und AgLibraryKeyword (name), und
    ''' Adobe_imageDevelopSettings (text, eine Lua-Tabelle mit den crs:-Schluesseln ohne Vorsilbe, und
    ''' hasDevelopAdjustmentsEx, groesser 0 heisst bearbeitet).</summary>
    Public NotInheritable Class ForeignCatalogService

        ''' <summary>Was der Katalog zu einem Foto sagt.</summary>
        Public NotInheritable Class CatalogEntry
            Friend Id As Long
            ''' <summary>Die Teile des Pfads in Kleinbuchstaben, der letzte ist der Dateiname.</summary>
            Friend Components As String()
            ''' <summary>1 bis 5, oder Nothing ohne Bewertung.</summary>
            Public Property Rating As Integer?
            ''' <summary>1 behalten, -1 verworfen, oder Nothing.</summary>
            Public Property PickState As Integer?
            ''' <summary>Der Hex-Wert unserer Palette, oder leer.</summary>
            Public Property ColorLabel As String = ""
            Public ReadOnly Property Keywords As New List(Of String)()
            ''' <summary>Im Referenzprogramm bearbeitet (hasDevelopAdjustmentsEx groesser 0, ohne die Spalte
            ''' mehr als ein Schritt im Verlauf).</summary>
            Public Property Edited As Boolean
            ''' <summary>Die Aufnahmezeit, wie das Referenzprogramm sie fuehrt (Adobe_images.captureTime),
            ''' als Uhrzeit ohne Zone und ohne Sekundenbruchteile; Nothing ohne lesbaren Wert.</summary>
            Public Property CaptureTime As DateTime?
            ''' <summary>Die Aufnahmezeit vor der ersten Aenderung dort (originalCaptureTime); Nothing,
            ''' wenn sie nie geaendert wurde.</summary>
            Public Property OriginalCaptureTime As DateTime?
            ''' <summary>Was aus der Datenbank je Foto dazukommt (Copyright, Ort, Zone), beim ersten
            ''' Bedarf gelesen (<see cref="ImportedMetadataFor"/>); gilt, solange dieser Stand des
            ''' Katalogs gilt.</summary>
            Friend Imported As ImportedMetadata
            Friend ImportedRead As Boolean
        End Class

        ''' <summary>Was aus dem Katalog des Referenzprogramms an die Datei uebernommen wird: Copyright,
        ''' Koordinate, Ortsnamen und eine dort GEAENDERTE Aufnahmezeit samt der Zeit vor der Aenderung
        ''' und der Zone. Leere Felder heissen: steht dort nicht. Bei RAW und PSD steht es als
        ''' Abschrift in der .fpxmp (RawSidecarService.TryReadImported), sonst kommt es beim Lesen
        ''' aus dem Katalog.</summary>
        Public NotInheritable Class ImportedMetadata
            Public Property Copyright As String = ""
            Public Property Latitude As Double?
            Public Property Longitude As Double?
            Public Property City As String = ""
            Public Property Country As String = ""
            ''' <summary>Die geaenderte Aufnahmezeit als Uhrzeit vor Ort; nur belegt, wenn sie dort
            ''' wirklich geaendert wurde, dann immer zusammen mit <see cref="OriginalCaptureTime"/>.</summary>
            Public Property CaptureTime As DateTime?
            Public Property OriginalCaptureTime As DateTime?
            ''' <summary>Die Zone der Aufnahmezeit ("+02:00"), wie das Referenzprogramm sie im XMP-Paket
            ''' fuehrt; leer, wenn keine bekannt ist.</summary>
            Public Property CaptureOffset As String = ""

            Public ReadOnly Property HasCoordinates As Boolean
                Get
                    Return Latitude.HasValue AndAlso Longitude.HasValue
                End Get
            End Property

            Public ReadOnly Property HasEditedCaptureTime As Boolean
                Get
                    Return CaptureTime.HasValue AndAlso OriginalCaptureTime.HasValue
                End Get
            End Property

            Public ReadOnly Property IsEmpty As Boolean
                Get
                    Return Copyright.Length = 0 AndAlso Not HasCoordinates AndAlso City.Length = 0 AndAlso
                           Country.Length = 0 AndAlso Not HasEditedCaptureTime
                End Get
            End Property

            Public Function SameAs(other As ImportedMetadata) As Boolean
                If other Is Nothing Then Return IsEmpty
                Return String.Equals(Copyright, other.Copyright, StringComparison.Ordinal) AndAlso
                       Nullable.Equals(Latitude, other.Latitude) AndAlso Nullable.Equals(Longitude, other.Longitude) AndAlso
                       String.Equals(City, other.City, StringComparison.Ordinal) AndAlso
                       String.Equals(Country, other.Country, StringComparison.Ordinal) AndAlso
                       Nullable.Equals(CaptureTime, other.CaptureTime) AndAlso
                       Nullable.Equals(OriginalCaptureTime, other.OriginalCaptureTime) AndAlso
                       String.Equals(CaptureOffset, other.CaptureOffset, StringComparison.Ordinal)
            End Function
        End Class

        ''' <summary>Was beim Lesen herauskam.</summary>
        Public Enum CatalogState
            ''' Kein Pfad gesetzt oder der Schalter aus.
            Off
            Missing
            ''' Das Referenzprogramm hat ihn offen; es gilt der zuletzt gelesene Stand, falls es einen gibt.
            Locked
            NotACatalog
            Unreadable
            Read
        End Enum

        Private NotInheritable Class CatalogIndex
            Public CatalogPath As String = ""
            Public Stamp As String = ""
            Public State As CatalogState
            Public Problem As String = ""
            Public ByName As Dictionary(Of String, List(Of CatalogEntry))
            Public ImageCount As Integer
        End Class

        ''' <summary>So viele Pfadteile muessen von hinten uebereinstimmen: Datei und Ordner.</summary>
        Private Const MinMatchedComponents As Integer = 2
        ''' <summary>So lange gilt ein gelesener Zeitstempel des Katalogs, bevor er neu gelesen wird.
        ''' Der Frische-Stempel fragt ihn fuer jedes Bild eines Ordners.</summary>
        Private Shared ReadOnly StampLifetime As TimeSpan = TimeSpan.FromSeconds(2)

        Private Shared ReadOnly _lock As New Object()
        Private Shared _index As CatalogIndex
        Private Shared _stampPath As String = ""
        Private Shared _stampValue As String = ""
        Private Shared _stampReadAt As DateTime = DateTime.MinValue
        ''' <summary>Der Stempel des zuletzt ohne Sperre gesehenen Katalogs. Solange das Referenzprogramm ihn
        ''' offen hat, gilt dieser, damit nicht jede Aenderung dort jedes Bild neu einlesen laesst.</summary>
        Private Shared _lastUnlockedStamp As String = ""

        Private Sub New()
        End Sub

        ''' <summary>Der eingestellte Pfad, oder leer.</summary>
        Public Shared ReadOnly Property ConfiguredPath As String
            Get
                Try
                    Return If(AppSettingsService.Load().ForeignCatalogPath, "").Trim()
                Catch
                    Return ""
                End Try
            End Get
        End Property

        ''' <summary>Ein Katalog ist eingestellt und der Schalter der fremden Beistelldateien an.</summary>
        Public Shared ReadOnly Property IsEnabled As Boolean
            Get
                Return ConfiguredPath.Length > 0 AndAlso Pp3SidecarService.IsEnabled
            End Get
        End Property

        ''' <summary>Was der Katalog zu diesem Foto sagt, oder Nothing (aus, nicht gefunden, nicht
        ''' eindeutig). Liest beim ersten Aufruf und nach jeder Aenderung des Katalogs den Katalog
        ''' ein, das kostet bei grossen Katalogen einige Sekunden und gehoert in einen
        ''' Hintergrundweg.</summary>
        Public Shared Function Lookup(imagePath As String) As CatalogEntry
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not IsEnabled Then Return Nothing
            Return LookupIn(CurrentIndex(), imagePath)
        End Function

        Private Shared Function LookupIn(index As CatalogIndex, imagePath As String) As CatalogEntry
            If index Is Nothing OrElse index.ByName Is Nothing Then Return Nothing
            Dim parts = SplitPath(imagePath)
            If parts.Length = 0 Then Return Nothing
            Dim candidates As List(Of CatalogEntry) = Nothing
            If Not index.ByName.TryGetValue(parts(parts.Length - 1), candidates) Then Return Nothing
            Dim best As CatalogEntry = Nothing
            Dim bestLength = 0
            Dim tie = False
            For Each candidate In candidates
                Dim n = CommonSuffix(parts, candidate.Components)
                If n > bestLength Then
                    best = candidate
                    bestLength = n
                    tie = False
                ElseIf n = bestLength Then
                    tie = True
                End If
            Next
            If best Is Nothing OrElse tie OrElse bestLength < MinMatchedComponents Then Return Nothing
            Return best
        End Function

        ''' <summary>Der Anteil des Katalogs am Frische-Stempel der Beistelldateien, fuer jedes Bild
        ''' derselbe: aendert sich der Katalog, wird neu eingelesen. Leer, wenn er aus ist oder fehlt.
        ''' Liest nur Zeitstempel, nie den Katalog selbst.</summary>
        Public Shared Function StampForAll() As String
            If Not IsEnabled Then Return ""
            Dim stamp = CatalogStamp(ConfiguredPath)
            Return If(stamp.Length = 0, "", "lrcat:" & stamp)
        End Function

        ''' <summary>Zustand und Zahl der Fotos fuer die Einstellungen. Liest den Katalog, wenn noch
        ''' nicht geschehen; gehoert deshalb in einen Hintergrundweg.</summary>
        Public Shared Function Describe(ByRef imageCount As Integer, ByRef problem As String) As CatalogState
            imageCount = 0
            problem = ""
            If Not IsEnabled Then Return CatalogState.Off
            Dim index = CurrentIndex()
            If index Is Nothing Then Return CatalogState.Off
            imageCount = index.ImageCount
            problem = index.Problem
            Return index.State
        End Function

        ''' <summary>Die Entwicklung eines Fotos als XMP-Text mit crs:-Schluesseln, in der Form, die
        ''' XmpPresetService.LoadLookFromText liest; leer, wenn es keine gibt.</summary>
        Public Shared Function DevelopAsXmp(entry As CatalogEntry) As String
            If entry Is Nothing OrElse Not IsEnabled Then Return ""
            Try
                Dim path = ConfiguredPath
                If File.Exists(path & ".lock") Then Return ""
                Dim text As String = Nothing
                Using conn = Open(path)
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT text FROM Adobe_imageDevelopSettings WHERE image = $id"
                        cmd.Parameters.AddWithValue("$id", entry.Id)
                        Dim value = cmd.ExecuteScalar()
                        If value IsNot Nothing AndAlso Not TypeOf value Is DBNull Then text = Convert.ToString(value, CultureInfo.InvariantCulture)
                    End Using
                End Using
                If String.IsNullOrWhiteSpace(text) Then Return ""
                Return LuaDevelopToXmp(text)
            Catch ex As Exception
                DiagnosticLogService.LogException("ForeignCatalog.Entwicklung", ex)
                Return ""
            End Try
        End Function

        ''' <summary>Titel, Ueberschrift, Beschreibung, Urheber und Quellenangabe eines Fotos, oder
        ''' Nothing. Sie stehen im XMP-Paket, das Referenzprogramm je Foto in
        ''' Adobe_AdditionalMetadata.xmp ablegt (vier Byte Laenge, dann mit zlib gepackt, oder als
        ''' Text), und gelesen werden sie mit demselben Leser wie jede XMP
        ''' (ImageDescriptionService.FromXmpDescriptions). Die Bildunterschrift steht ausserdem in
        ''' AgLibraryIPTC.caption und fuellt die Beschreibung, wenn das Paket keine traegt.</summary>
        Public Shared Function DescriptionFor(entry As CatalogEntry) As ImageDescription
            If entry Is Nothing OrElse Not IsEnabled Then Return Nothing
            Try
                Dim path = ConfiguredPath
                If File.Exists(path & ".lock") Then Return Nothing
                Dim result As ImageDescription = Nothing
                Dim caption = ""
                Using conn = Open(path)
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT xmp FROM Adobe_AdditionalMetadata WHERE image = $id"
                        cmd.Parameters.AddWithValue("$id", entry.Id)
                        Try
                            Dim packet = PacketText(cmd.ExecuteScalar())
                            If packet.Length > 0 Then
                                Dim doc = XDocument.Parse(packet)
                                result = ImageDescriptionService.FromXmpDescriptions(
                                    doc.Descendants(XName.Get("Description", "http://www.w3.org/1999/02/22-rdf-syntax-ns#")))
                            End If
                        Catch ex As SqliteException
                            ' Aeltere oder fremde Kataloge ohne die Tabelle: dann eben nur die Unterschrift.
                        End Try
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT caption FROM AgLibraryIPTC WHERE image = $id"
                        cmd.Parameters.AddWithValue("$id", entry.Id)
                        Try
                            Dim value = cmd.ExecuteScalar()
                            If value IsNot Nothing AndAlso Not TypeOf value Is DBNull Then caption = Convert.ToString(value, CultureInfo.InvariantCulture)
                        Catch ex As SqliteException
                        End Try
                    End Using
                End Using
                If result Is Nothing Then result = New ImageDescription()
                If result.Description.Length = 0 AndAlso Not String.IsNullOrWhiteSpace(caption) Then result.Description = caption
                For Each field In ImageDescription.AllFields
                    result.SetValue(field, ImageDescriptionService.NormalizeValue(field, result.GetValue(field)))
                Next
                Return If(result.IsEmpty, Nothing, result)
            Catch ex As Exception
                DiagnosticLogService.LogException("ForeignCatalog.Beschreibung", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Was der Katalog an ein Foto uebergibt (<see cref="ImportedMetadata"/>), oder Nothing,
        ''' wenn er nichts davon traegt. Gelesen beim ersten Bedarf und am Eintrag gemerkt: der Weg
        ''' laeuft bei jedem Lesen der Aufnahmedaten mit (ExifService.ReadExif), und ein neuer Stand
        ''' des Katalogs bringt ohnehin neue Eintraege.
        '''
        ''' DIE AUFNAHMEZEIT kommt aus dem Eintrag (captureTime neben originalCaptureTime) und nur,
        ''' wenn sie dort geaendert wurde; ihre Zone aus dem XMP-Paket (exif:DateTimeOriginal,
        ''' ersatzweise photoshop:DateCreated und xmp:CreateDate), denn nur dort fuehrt das
        ''' Referenzprogramm sie, und eine Aenderung behaelt sie bei.
        '''
        ''' COPYRIGHT UND ORT kommen zuerst aus dem XMP-Paket in Adobe_AdditionalMetadata.xmp, dasselbe
        ''' wie fuer den Titel: dc:rights, exif:GPSLatitude und exif:GPSLongitude, photoshop:City und
        ''' photoshop:Country, gelesen wie in einer Beistelldatei. Was dort fehlt, kommt aus den
        ''' Tabellen, in denen das Referenzprogramm dieselben Werte fuer seine Filter fuehrt:
        ''' AgLibraryIPTC.copyright, AgHarvestedExifMetadata.gpsLatitude/gpsLongitude und die
        ''' Ortsnamen ueber AgHarvestedIptcMetadata (cityRef, countryRef) aus AgInternedIptcCity und
        ''' AgInternedIptcCountry. Jede Abfrage fuer sich: fehlt einem Katalog eine Tabelle, bleibt es
        ''' bei den uebrigen. Die Tabellen folgen fremden Lesern, ein echter Katalog lag nicht vor.
        '''
        ''' Hat das Referenzprogramm den Katalog offen (".lock"), gilt der gemerkte Stand; fehlt der,
        ''' kommt nur die Aufnahmezeit aus dem Speicher, ohne Zone und ungemerkt.</summary>
        Public Shared Function ImportedMetadataFor(entry As CatalogEntry) As ImportedMetadata
            If entry Is Nothing OrElse Not IsEnabled Then Return Nothing
            If entry.ImportedRead Then Return entry.Imported
            Dim result As New ImportedMetadata()
            If entry.CaptureTime.HasValue AndAlso entry.OriginalCaptureTime.HasValue AndAlso
               entry.CaptureTime.Value <> entry.OriginalCaptureTime.Value Then
                result.CaptureTime = entry.CaptureTime
                result.OriginalCaptureTime = entry.OriginalCaptureTime
            End If
            Dim path = ConfiguredPath
            If File.Exists(path & ".lock") Then Return If(result.IsEmpty, Nothing, result)
            Try
                Using conn = Open(path)
                    Dim packet = ""
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT xmp FROM Adobe_AdditionalMetadata WHERE image = $id"
                        cmd.Parameters.AddWithValue("$id", entry.Id)
                        Try
                            packet = PacketText(cmd.ExecuteScalar())
                        Catch ex As SqliteException
                        End Try
                    End Using
                    If packet.Length > 0 Then
                        Try
                            Dim descriptions = XDocument.Parse(packet).Descendants(XName.Get("Description", RdfNamespace)).ToList()
                            result.Copyright = CopyrightService.NormalizeText(PacketValue(descriptions, XName.Get("rights", "http://purl.org/dc/elements/1.1/")))
                            Dim latitude = XmpSidecarService.ParseCoordinate(PacketValue(descriptions, XName.Get("GPSLatitude", "http://ns.adobe.com/exif/1.0/")))
                            Dim longitude = XmpSidecarService.ParseCoordinate(PacketValue(descriptions, XName.Get("GPSLongitude", "http://ns.adobe.com/exif/1.0/")))
                            If latitude.HasValue AndAlso longitude.HasValue Then
                                result.Latitude = latitude
                                result.Longitude = longitude
                            End If
                            result.City = PacketValue(descriptions, XName.Get("City", "http://ns.adobe.com/photoshop/1.0/"))
                            result.Country = PacketValue(descriptions, XName.Get("Country", "http://ns.adobe.com/photoshop/1.0/"))
                            If result.HasEditedCaptureTime Then
                                For Each name In {XName.Get("DateTimeOriginal", "http://ns.adobe.com/exif/1.0/"),
                                                  XName.Get("DateCreated", "http://ns.adobe.com/photoshop/1.0/"),
                                                  XName.Get("CreateDate", "http://ns.adobe.com/xap/1.0/")}
                                    Dim clock = ExifService.ParseXmpClock(PacketValue(descriptions, name))
                                    If clock.Clock.HasValue Then
                                        result.CaptureOffset = clock.Offset
                                        Exit For
                                    End If
                                Next
                            End If
                        Catch ex As Exception
                            DiagnosticLogService.LogException("ForeignCatalog.Ort", ex)
                        End Try
                    End If
                    If result.Copyright.Length = 0 Then
                        result.Copyright = CopyrightService.NormalizeText(ScalarText(conn, "SELECT copyright FROM AgLibraryIPTC WHERE image = $id", entry.Id))
                    End If
                    If Not result.HasCoordinates Then
                        Try
                            Using cmd = conn.CreateCommand()
                                cmd.CommandText = "SELECT gpsLatitude, gpsLongitude FROM AgHarvestedExifMetadata WHERE image = $id"
                                cmd.Parameters.AddWithValue("$id", entry.Id)
                                Using reader = cmd.ExecuteReader()
                                    If reader.Read() Then
                                        Dim latitude = NumberAt(reader, 0)
                                        Dim longitude = NumberAt(reader, 1)
                                        ' 0/0 ist der Leerwert der Tabelle, kein Ort im Golf von Guinea.
                                        If latitude.HasValue AndAlso longitude.HasValue AndAlso
                                           Math.Abs(latitude.Value) <= 90 AndAlso Math.Abs(longitude.Value) <= 180 AndAlso
                                           Not (latitude.Value = 0 AndAlso longitude.Value = 0) Then
                                            result.Latitude = latitude
                                            result.Longitude = longitude
                                        End If
                                    End If
                                End Using
                            End Using
                        Catch ex As SqliteException
                        End Try
                    End If
                    If result.City.Length = 0 Then
                        result.City = ScalarText(conn, "SELECT c.value FROM AgHarvestedIptcMetadata h JOIN AgInternedIptcCity c ON c.id_local = h.cityRef WHERE h.image = $id", entry.Id)
                    End If
                    If result.Country.Length = 0 Then
                        result.Country = ScalarText(conn, "SELECT c.value FROM AgHarvestedIptcMetadata h JOIN AgInternedIptcCountry c ON c.id_local = h.countryRef WHERE h.image = $id", entry.Id)
                    End If
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ForeignCatalog.Ort", ex)
                Return Nothing
            End Try
            Dim value = If(result.IsEmpty, Nothing, result)
            entry.Imported = value
            entry.ImportedRead = True
            Return value
        End Function

        Private Const RdfNamespace As String = "http://www.w3.org/1999/02/22-rdf-syntax-ns#"

        ''' <summary>Der erste belegte Wert aus den rdf:Description des Pakets: als Attribut oder als
        ''' Kindelement, bei einer Sprachalternative der Eintrag x-default, sonst der erste.</summary>
        Private Shared Function PacketValue(descriptions As List(Of XElement), name As XName) As String
            For Each description In descriptions
                Dim attribute = description.Attribute(name)
                If attribute IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(attribute.Value) Then Return attribute.Value.Trim()
                Dim element = description.Element(name)
                If element Is Nothing Then Continue For
                Dim items = element.Descendants(XName.Get("li", RdfNamespace)).ToList()
                Dim chosen = items.FirstOrDefault(Function(e) e.Attributes().Any(Function(a) a.Name.LocalName = "lang" AndAlso a.Value = "x-default"))
                If chosen Is Nothing Then chosen = items.FirstOrDefault()
                Dim text = If(chosen IsNot Nothing, chosen.Value, element.Value)
                If Not String.IsNullOrWhiteSpace(text) Then Return text.Trim()
            Next
            Return ""
        End Function

        ''' <summary>Ein Textwert zu einem Foto, leer bei fehlender Tabelle, Spalte oder Zeile.</summary>
        Private Shared Function ScalarText(conn As SqliteConnection, sql As String, id As Long) As String
            Try
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = sql
                    cmd.Parameters.AddWithValue("$id", id)
                    Dim value = cmd.ExecuteScalar()
                    If value Is Nothing OrElse TypeOf value Is DBNull Then Return ""
                    Return Convert.ToString(value, CultureInfo.InvariantCulture).Trim()
                End Using
            Catch ex As SqliteException
                Return ""
            End Try
        End Function

        ''' <summary>Das XMP-Paket als Text: so, wie es steht, wenn es mit "&lt;" beginnt, sonst
        ''' nach vier Byte Laenge (Big Endian) mit zlib gepackt. Leer, wenn nichts Lesbares da ist.</summary>
        Private Shared Function PacketText(value As Object) As String
            If value Is Nothing OrElse TypeOf value Is DBNull Then Return ""
            Dim text = TryCast(value, String)
            If text IsNot Nothing Then Return text
            Dim bytes = TryCast(value, Byte())
            If bytes Is Nothing OrElse bytes.Length = 0 Then Return ""
            If bytes(0) = AscW("<"c) Then Return Encoding.UTF8.GetString(bytes)
            If bytes.Length < 6 Then Return ""
            Using input As New MemoryStream(bytes, 4, bytes.Length - 4)
                Using inflater As New Compression.ZLibStream(input, Compression.CompressionMode.Decompress)
                    Using reader As New StreamReader(inflater, Encoding.UTF8)
                        Return reader.ReadToEnd()
                    End Using
                End Using
            End Using
        End Function

        ' Einlesen

        Private Shared Function CurrentIndex() As CatalogIndex
            SyncLock _lock
                Dim path = ConfiguredPath
                If path.Length = 0 Then Return Nothing
                Dim locked = File.Exists(path & ".lock")
                If locked AndAlso _index IsNot Nothing AndAlso _index.ByName IsNot Nothing AndAlso
                   String.Equals(_index.CatalogPath, path, StringComparison.Ordinal) Then
                    ' Der zuletzt gelesene Stand gilt weiter, bis das Referenzprogramm den Katalog schliesst.
                    _index.State = CatalogState.Locked
                    Return _index
                End If
                Dim stamp = If(locked, "", ReadCatalogStamp(path))
                If _index IsNot Nothing AndAlso String.Equals(_index.CatalogPath, path, StringComparison.Ordinal) AndAlso
                   _index.Stamp.Length > 0 AndAlso String.Equals(_index.Stamp, stamp, StringComparison.Ordinal) Then
                    _index.State = CatalogState.Read
                    Return _index
                End If
                If locked Then
                    _index = New CatalogIndex With {.CatalogPath = path, .State = CatalogState.Locked}
                ElseIf Not File.Exists(path) Then
                    _index = New CatalogIndex With {.CatalogPath = path, .State = CatalogState.Missing}
                Else
                    _index = Load(path, stamp)
                End If
                Return _index
            End SyncLock
        End Function

        Private Shared Function Load(path As String, stamp As String) As CatalogIndex
            Dim result As New CatalogIndex With {.CatalogPath = path, .Stamp = stamp}
            Dim clock = Diagnostics.Stopwatch.StartNew()
            Try
                Using conn = Open(path)
                    Dim byId As New Dictionary(Of Long, CatalogEntry)()
                    Using cmd = conn.CreateCommand()
                        ' Nur die Originale: eine virtuelle Kopie traegt in masterImage die Kennung
                        ' ihres Originals. Ein Original hat dort NULL ODER 0, je nach Katalog; mit nur
                        ' "IS NULL" fand sich in einem Katalog mit 0 kein einziges Foto.
                        cmd.CommandText =
                            "SELECT i.id_local, i.rating, i.pick, i.colorLabels, r.absolutePath, fo.pathFromRoot, f.baseName, f.extension " &
                            "FROM Adobe_images i " &
                            "JOIN AgLibraryFile f ON f.id_local = i.rootFile " &
                            "JOIN AgLibraryFolder fo ON fo.id_local = f.folder " &
                            "JOIN AgLibraryRootFolder r ON r.id_local = fo.rootFolder " &
                            "WHERE i.masterImage IS NULL OR i.masterImage = 0"
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                Dim baseName = TextAt(reader, 6)
                                If baseName.Length = 0 Then Continue While
                                Dim extension = TextAt(reader, 7)
                                Dim fileName = If(extension.Length = 0, baseName, baseName & "." & extension)
                                Dim entry As New CatalogEntry With {
                                    .Id = reader.GetInt64(0),
                                    .Components = SplitPath(TextAt(reader, 4) & "/" & TextAt(reader, 5) & "/" & fileName)}
                                Dim rating = NumberAt(reader, 1)
                                If rating.HasValue AndAlso rating.Value >= 1 Then entry.Rating = CInt(Math.Min(5, Math.Round(rating.Value)))
                                Dim pick = NumberAt(reader, 2)
                                If pick.HasValue AndAlso pick.Value > 0 Then entry.PickState = 1
                                If pick.HasValue AndAlso pick.Value < 0 Then entry.PickState = -1
                                entry.ColorLabel = XmpSidecarService.MapLabel(TextAt(reader, 3))
                                byId(entry.Id) = entry
                            End While
                        End Using
                    End Using

                    Using cmd = conn.CreateCommand()
                        cmd.CommandText =
                            "SELECT ki.image, k.name FROM AgLibraryKeywordImage ki " &
                            "JOIN AgLibraryKeyword k ON k.id_local = ki.tag WHERE k.name IS NOT NULL"
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                Dim entry As CatalogEntry = Nothing
                                If Not byId.TryGetValue(reader.GetInt64(0), entry) Then Continue While
                                Dim name = TextAt(reader, 1).Trim()
                                If name.Length > 0 AndAlso Not entry.Keywords.Contains(name, StringComparer.OrdinalIgnoreCase) Then entry.Keywords.Add(name)
                            End While
                        End Using
                    End Using

                    ReadEdited(conn, byId)
                    ReadCaptureTimes(conn, byId)

                    result.ByName = New Dictionary(Of String, List(Of CatalogEntry))(StringComparer.Ordinal)
                    For Each entry In byId.Values
                        Dim name = entry.Components(entry.Components.Length - 1)
                        Dim list As List(Of CatalogEntry) = Nothing
                        If Not result.ByName.TryGetValue(name, list) Then
                            list = New List(Of CatalogEntry)()
                            result.ByName(name) = list
                        End If
                        list.Add(entry)
                    Next
                    result.ImageCount = byId.Count
                    result.State = CatalogState.Read
                End Using
                DiagnosticLogService.LogAlways("ForeignCatalog", $"{IO.Path.GetFileName(path)}: {result.ImageCount} Fotos in {clock.ElapsedMilliseconds} ms")
            Catch ex As SqliteException When ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) OrElse
                                             ex.SqliteErrorCode = 26
                ' 26 ist SQLITE_NOTADB: gar keine Datenbank.
                result.State = CatalogState.NotACatalog
                result.Problem = ex.Message
                result.ByName = Nothing
                DiagnosticLogService.LogAlways("ForeignCatalog", $"{IO.Path.GetFileName(path)}: kein Katalog des Referenzprogramms ({ex.Message})")
            Catch ex As Exception
                result.State = CatalogState.Unreadable
                result.Problem = ex.Message
                result.ByName = Nothing
                DiagnosticLogService.LogException("ForeignCatalog.Lesen", ex)
            End Try
            Return result
        End Function

        ''' <summary>Das Kennzeichen "bearbeitet" je Foto. Ohne hasDevelopAdjustmentsEx (aeltere
        ''' Kataloge, oder die Spalte leer) zaehlt der Verlauf: mehr als der Schritt, mit dem das Foto
        ''' hereinkam. Fehlt beides, gilt keines als bearbeitet.</summary>
        Private Shared Sub ReadEdited(conn As SqliteConnection, byId As Dictionary(Of Long, CatalogEntry))
            Dim unknown As New HashSet(Of Long)(byId.Keys)
            Try
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT image, hasDevelopAdjustmentsEx FROM Adobe_imageDevelopSettings"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            Dim entry As CatalogEntry = Nothing
                            If Not byId.TryGetValue(reader.GetInt64(0), entry) Then Continue While
                            Dim flag = NumberAt(reader, 1)
                            If Not flag.HasValue Then Continue While
                            entry.Edited = flag.Value > 0
                            unknown.Remove(entry.Id)
                        End While
                    End Using
                End Using
            Catch ex As SqliteException
                DiagnosticLogService.LogAlways("ForeignCatalog", "ohne hasDevelopAdjustmentsEx: " & ex.Message)
            End Try
            If unknown.Count = 0 Then Return
            Try
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT image, count(*) FROM Adobe_libraryImageDevelopHistoryStep GROUP BY image"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            Dim id = reader.GetInt64(0)
                            If Not unknown.Contains(id) Then Continue While
                            byId(id).Edited = reader.GetInt64(1) > 1
                        End While
                    End Using
                End Using
            Catch ex As SqliteException
                DiagnosticLogService.LogAlways("ForeignCatalog", "ohne Entwicklungsverlauf: " & ex.Message)
            End Try
        End Sub

        ''' <summary>Aufnahmezeit und Aufnahmezeit vor der ersten Aenderung je Foto. Eigene Abfrage und
        ''' nicht in der Hauptabfrage: fehlt einem Katalog eine der beiden Spalten, bleibt es bei
        ''' Bewertung, Stichwoertern und Entwicklung, statt dass der ganze Katalog als unlesbar gilt.</summary>
        Private Shared Sub ReadCaptureTimes(conn As SqliteConnection, byId As Dictionary(Of Long, CatalogEntry))
            Try
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT id_local, captureTime, originalCaptureTime FROM Adobe_images"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            Dim entry As CatalogEntry = Nothing
                            If Not byId.TryGetValue(reader.GetInt64(0), entry) Then Continue While
                            entry.CaptureTime = ParseCatalogTime(TextAt(reader, 1))
                            entry.OriginalCaptureTime = ParseCatalogTime(TextAt(reader, 2))
                        End While
                    End Using
                End Using
            Catch ex As SqliteException
                DiagnosticLogService.LogAlways("ForeignCatalog", "ohne Aufnahmezeiten: " & ex.Message)
            End Try
        End Sub

        Private Shared ReadOnly CatalogTimePattern As New Regex(
            "^\s*(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2}))?)?", RegexOptions.CultureInvariant)

        ''' <summary>Eine Zeit aus dem Katalog als Uhrzeit, wie das Referenzprogramm sie zeigt und
        ''' sortiert: der Teil vor einer Zone, ohne Sekundenbruchteile. Die Spalte ist freier Text und
        ''' kommt in vielen Schreibweisen vor ("2024-07-01T14:00:00.000", mit Zone "+02:00", ohne
        ''' Sekunden, als blosses Datum); eine Zone steht dort nur bei Ersatzwerten (Aenderungs- oder
        ''' Importzeit) und wird wie dort nicht angewendet. Nothing, wenn nichts Lesbares dasteht.</summary>
        Public Shared Function ParseCatalogTime(text As String) As DateTime?
            If String.IsNullOrWhiteSpace(text) Then Return Nothing
            Dim m = CatalogTimePattern.Match(text)
            If Not m.Success Then Return Nothing
            Dim number = Function(index As Integer) As Integer
                             If Not m.Groups(index).Success Then Return 0
                             Return Integer.Parse(m.Groups(index).Value, CultureInfo.InvariantCulture)
                         End Function
            Try
                Return New DateTime(number(1), number(2), number(3), number(4), number(5), number(6))
            Catch ex As ArgumentOutOfRangeException
                Return Nothing
            End Try
        End Function

        ''' <summary>Die im Referenzprogramm geaenderte Aufnahmezeit, im EXIF-Rohformat, wenn sie fuer die
        ''' Datei gelten soll, sonst leer.
        '''
        ''' NUR EINE GEAENDERTE ZEIT. Ohne Aenderung fuehrt der Katalog die Zeit der Datei, oder fuer eine
        ''' Datei ohne Datum ihr Aenderungsdatum in UTC; beides weiss die Datei selbst besser.
        '''
        ''' DIE DATEI GEHT VOR, SOBALD SIE SELBST EINE NEUERE ZEIT TRAEGT. Gilt die Katalogzeit nur, solange
        ''' die Datei noch die Zeit von VOR der Aenderung traegt (oder gar keine echte), dann:
        ''' - hat das Referenzprogramm die Zeit schon in die Datei geschrieben, steht dort die neue, und
        '''   es aendert sich nichts;
        ''' - hat jemand die Zeit danach in FerrumPix korrigiert (JPEG im Bild, sonst Beistelldatei),
        '''   steht dort eine dritte, und die gewinnt. Ohne diese Regel holte jeder Kataloglauf die
        '''   Zeit aus dem fremden Katalog zurueck, und die eigene Korrektur waere wirkungslos.</summary>
        ''' <param name="fileTime">Die geltende Zeit der Datei samt Beistelldatei, EXIF-Rohformat.</param>
        ''' <param name="fileTimeIsFallback">Die Datei traegt keine Aufnahmezeit, nur ein Aenderungsdatum.</param>
        Public Shared Function EditedCaptureTime(imported As ImportedMetadata, fileTime As String, fileTimeIsFallback As Boolean) As String
            If imported Is Nothing OrElse Not imported.HasEditedCaptureTime Then Return ""
            If imported.CaptureTime.Value = imported.OriginalCaptureTime.Value Then Return ""
            Dim edited = imported.CaptureTime.Value.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)
            If String.IsNullOrWhiteSpace(fileTime) OrElse fileTimeIsFallback Then Return edited
            Dim original = imported.OriginalCaptureTime.Value.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)
            Return If(String.Equals(fileTime.Trim(), original, StringComparison.Ordinal), edited, "")
        End Function

        ''' <summary>Wie <see cref="Lookup"/>, liest den Katalog aber nie selbst ein, auch nicht nach
        ''' einer Aenderung: fuer Aufrufer auf dem Oberflaechenfaden, die das Einlesen eines grossen
        ''' Katalogs (einige Sekunden) nicht abwarten duerfen. Es gilt der zuletzt gelesene Stand; ist
        ''' noch keiner da, wird das Einlesen im Hintergrund angestossen und Nothing geliefert.
        ''' Ordnerlauf und Katalogindex laufen im Hintergrund ueber <see cref="Lookup"/> und bekommen
        ''' den neuesten Stand.</summary>
        Public Shared Function LookupIfLoaded(imagePath As String) As CatalogEntry
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not IsEnabled Then Return Nothing
            Dim index As CatalogIndex
            SyncLock _lock
                index = _index
            End SyncLock
            If index Is Nothing OrElse Not String.Equals(index.CatalogPath, ConfiguredPath, StringComparison.Ordinal) Then
                Threading.Tasks.Task.Run(Sub() CurrentIndex())
                Return Nothing
            End If
            Return LookupIn(index, imagePath)
        End Function

        ''' <summary>Schreibgeschuetzt und ohne Pool (der hielte die Datei offen). Ohne
        ''' Schreibprotokoll mit Inhalt als unveraenderlich: dann legt SQLite nichts neben die Datei
        ''' und nimmt keine Sperre. Mit Protokoll muss SQLite es lesen und braucht dafuer den
        ''' gemeinsamen Speicher daneben; das ist der einzige Fall, in dem eine Datei entsteht.</summary>
        Private Shared Function Open(path As String) As SqliteConnection
            Dim walLength = 0L
            Try
                Dim wal = New FileInfo(path & "-wal")
                If wal.Exists Then walLength = wal.Length
            Catch
            End Try
            Dim source = If(walLength > 0, path, New Uri(path).AbsoluteUri & "?immutable=1")
            Dim builder As New SqliteConnectionStringBuilder With {
                .DataSource = source,
                .Mode = SqliteOpenMode.ReadOnly,
                .Pooling = False}
            Dim conn As New SqliteConnection(builder.ToString())
            conn.Open()
            Return conn
        End Function

        ''' <summary>Gedrosselt, siehe <see cref="StampLifetime"/>. Solange das Referenzprogramm den Katalog
        ''' offen hat, gilt der letzte Stempel davor.</summary>
        Private Shared Function CatalogStamp(path As String) As String
            SyncLock _lock
                Dim now = DateTime.UtcNow
                If String.Equals(_stampPath, path, StringComparison.Ordinal) AndAlso now - _stampReadAt < StampLifetime Then Return _stampValue
                Dim value As String
                If File.Exists(path & ".lock") Then
                    value = _lastUnlockedStamp
                Else
                    value = ReadCatalogStamp(path)
                    _lastUnlockedStamp = value
                End If
                _stampPath = path
                _stampValue = value
                _stampReadAt = now
                Return value
            End SyncLock
        End Function

        ''' <summary>Aenderungszeit und Laenge des Katalogs und seines Schreibprotokolls; leer, wenn
        ''' die Datei fehlt.</summary>
        Private Shared Function ReadCatalogStamp(path As String) As String
            Try
                Dim info As New FileInfo(path)
                If Not info.Exists Then Return ""
                Dim stamp = info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) & "." & info.Length.ToString(CultureInfo.InvariantCulture)
                Dim wal As New FileInfo(path & "-wal")
                If wal.Exists Then stamp &= "+" & wal.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) & "." & wal.Length.ToString(CultureInfo.InvariantCulture)
                Return stamp
            Catch
                Return ""
            End Try
        End Function

        ' Pfade

        ''' <summary>Die Teile eines Pfads in Kleinbuchstaben, beide Trennzeichen, ohne leere Teile
        ''' und ohne Laufwerksbuchstaben-Sonderbehandlung (der faellt beim Vergleich von hinten nie
        ''' ins Gewicht).</summary>
        Friend Shared Function SplitPath(path As String) As String()
            If String.IsNullOrEmpty(path) Then Return Array.Empty(Of String)()
            Return path.Split({"/"c, "\"c}, StringSplitOptions.RemoveEmptyEntries).
                Select(Function(p) p.ToLowerInvariant()).ToArray()
        End Function

        Private Shared Function CommonSuffix(a As String(), b As String()) As Integer
            Dim n = 0
            Do While n < a.Length AndAlso n < b.Length AndAlso
                     String.Equals(a(a.Length - 1 - n), b(b.Length - 1 - n), StringComparison.Ordinal)
                n += 1
            Loop
            Return n
        End Function

        Private Shared Function TextAt(reader As SqliteDataReader, ordinal As Integer) As String
            If reader.IsDBNull(ordinal) Then Return ""
            Return Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
        End Function

        Private Shared Function NumberAt(reader As SqliteDataReader, ordinal As Integer) As Double?
            If reader.IsDBNull(ordinal) Then Return Nothing
            Dim value = reader.GetValue(ordinal)
            Dim d As Double
            If TypeOf value Is String Then
                If Double.TryParse(CStr(value), NumberStyles.Float, CultureInfo.InvariantCulture, d) Then Return d
                Return Nothing
            End If
            Try
                Return Convert.ToDouble(value, CultureInfo.InvariantCulture)
            Catch
                Return Nothing
            End Try
        End Function

        ' Die Entwicklung: Lua-Tabelle nach XMP

        ''' <summary>Eine Lua-Tabelle: benannte Eintraege und Eintraege ohne Namen, in Reihenfolge.</summary>
        Friend NotInheritable Class LuaTable
            Public ReadOnly Named As New Dictionary(Of String, Object)(StringComparer.Ordinal)
            Public ReadOnly Items As New List(Of Object)()
        End Class

        ''' <summary>Uebersetzt den Text aus Adobe_imageDevelopSettings ("s = { Exposure2012 = 0.5,
        ''' ToneCurvePV2012 = { 0, 0, 255, 255 }, ... }") in ein XMP mit denselben Schluesseln:
        ''' Zahlen, Texte und Wahrheitswerte als crs:-Attribute, Zahlenlisten als rdf:Seq (Kurven als
        ''' Paare "x, y", wie sie in einer XMP stehen), Textlisten ebenso, das Profil (Look) als
        ''' eigener Block. Listen von Tabellen (lokale Korrekturen, Retusche) bleiben aussen vor; sie
        ''' liest auch der XMP-Weg nicht.</summary>
        Friend Shared Function LuaDevelopToXmp(luaText As String) As String
            Dim root = TryCast(ParseLua(luaText), LuaTable)
            If root Is Nothing OrElse root.Named.Count = 0 Then Return ""
            Dim attributes As New StringBuilder()
            Dim elements As New StringBuilder()
            For Each pair In root.Named
                If Not IsPlainName(pair.Key) Then Continue For
                Dim table = TryCast(pair.Value, LuaTable)
                If table Is Nothing Then
                    Dim scalar = ScalarText(pair.Value)
                    If scalar IsNot Nothing Then attributes.Append(" crs:").Append(pair.Key).Append("=""").Append(EscapeXml(scalar)).Append(""""c)
                    Continue For
                End If
                If String.Equals(pair.Key, "Look", StringComparison.Ordinal) Then
                    AppendLook(elements, table)
                    Continue For
                End If
                If table.Named.Count > 0 OrElse table.Items.Count = 0 Then Continue For
                Dim items As New List(Of String)()
                If table.Items.All(Function(i) TypeOf i Is Double) Then
                    Dim numbers = table.Items.Cast(Of Double)().ToList()
                    If pair.Key.StartsWith("ToneCurve", StringComparison.Ordinal) Then
                        For k = 0 To numbers.Count - 2 Step 2
                            items.Add(FormatNumber(numbers(k)) & ", " & FormatNumber(numbers(k + 1)))
                        Next
                    Else
                        items.AddRange(numbers.Select(AddressOf FormatNumber))
                    End If
                ElseIf table.Items.All(Function(i) TypeOf i Is String) Then
                    items.AddRange(table.Items.Cast(Of String)())
                Else
                    Continue For
                End If
                elements.Append("<crs:").Append(pair.Key).Append("><rdf:Seq>")
                For Each item In items
                    elements.Append("<rdf:li>").Append(EscapeXml(item)).Append("</rdf:li>")
                Next
                elements.Append("</rdf:Seq></crs:").Append(pair.Key).Append(">"c)
            Next
            Return "<x:xmpmeta xmlns:x=""adobe:ns:meta/""><rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">" &
                   "<rdf:Description rdf:about="""" xmlns:crs=""http://ns.adobe.com/camera-raw-settings/1.0/""" & attributes.ToString() & ">" &
                   elements.ToString() & "</rdf:Description></rdf:RDF></x:xmpmeta>"
        End Function

        ''' <summary>Das Profil: Name, Staerke und ob die Staerke gilt als Attribute, die Gruppe als
        ''' Alternative. Die Gruppe steht als Text oder als Tabelle mit Sprachschluesseln.</summary>
        Private Shared Sub AppendLook(elements As StringBuilder, look As LuaTable)
            elements.Append("<crs:Look><rdf:Description")
            For Each field In {"Name", "Amount", "SupportsAmount", "UUID"}
                Dim value As Object = Nothing
                If look.Named.TryGetValue(field, value) Then
                    Dim scalar = ScalarText(value)
                    If scalar IsNot Nothing Then elements.Append(" crs:").Append(field).Append("=""").Append(EscapeXml(scalar)).Append(""""c)
                End If
            Next
            elements.Append(">"c)
            Dim group As Object = Nothing
            If look.Named.TryGetValue("Group", group) Then
                Dim groupText = TryCast(group, String)
                Dim groupTable = TryCast(group, LuaTable)
                If groupText Is Nothing AndAlso groupTable IsNot Nothing Then
                    Dim fallback As Object = Nothing
                    If groupTable.Named.TryGetValue("x-default", fallback) Then
                        groupText = TryCast(fallback, String)
                    ElseIf groupTable.Named.Count > 0 Then
                        groupText = TryCast(groupTable.Named.Values.First(), String)
                    End If
                End If
                If Not String.IsNullOrEmpty(groupText) Then
                    elements.Append("<crs:Group><rdf:Alt><rdf:li xml:lang=""x-default"">").Append(EscapeXml(groupText)).Append("</rdf:li></rdf:Alt></crs:Group>")
                End If
            End If
            elements.Append("</rdf:Description></crs:Look>")
        End Sub

        Private Shared Function ScalarText(value As Object) As String
            If TypeOf value Is Boolean Then Return If(CBool(value), "True", "False")
            If TypeOf value Is Double Then Return FormatNumber(CDbl(value))
            Return TryCast(value, String)
        End Function

        Private Shared Function FormatNumber(value As Double) As String
            Return value.ToString("R", CultureInfo.InvariantCulture)
        End Function

        Private Shared Function IsPlainName(name As String) As Boolean
            If String.IsNullOrEmpty(name) OrElse Not Char.IsLetter(name(0)) Then Return False
            Return name.All(Function(c) (c >= "A"c AndAlso c <= "Z"c) OrElse (c >= "a"c AndAlso c <= "z"c) OrElse (c >= "0"c AndAlso c <= "9"c))
        End Function

        Private Shared Function EscapeXml(value As String) As String
            Return If(value, "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("""", "&quot;")
        End Function

        ''' <summary>Liest einen Lua-Wert, wie das Referenzprogramm ihn ablegt: optional "name =" davor, dann
        ''' Tabelle, Text (in Anfuehrungszeichen oder als [[langer Text]]), Zahl, true, false oder
        ''' nil. Nothing bei allem, was nicht dazu passt.</summary>
        Friend Shared Function ParseLua(text As String) As Object
            If String.IsNullOrWhiteSpace(text) Then Return Nothing
            Try
                Dim reader As New LuaReader(text)
                Return reader.ReadDocument()
            Catch ex As FormatException
                DiagnosticLogService.LogAlways("ForeignCatalog", "Entwicklung nicht lesbar: " & ex.Message)
                Return Nothing
            End Try
        End Function

        Private NotInheritable Class LuaReader
            Private ReadOnly _text As String
            Private _pos As Integer
            Private _depth As Integer
            Private Const MaxDepth As Integer = 64

            Public Sub New(text As String)
                _text = text
            End Sub

            Public Function ReadDocument() As Object
                SkipSpace()
                ' "s = { ... }": der Name davor zaehlt nicht.
                Dim start = _pos
                Dim name = ReadIdentifier()
                If name IsNot Nothing Then
                    SkipSpace()
                    If Peek() = "="c AndAlso PeekAt(1) <> "="c Then
                        _pos += 1
                    Else
                        _pos = start
                    End If
                End If
                Return ReadValue()
            End Function

            Private Function ReadValue() As Object
                SkipSpace()
                If _pos >= _text.Length Then Throw New FormatException("Ende statt Wert")
                Dim c = _text(_pos)
                If c = "{"c Then Return ReadTable()
                If c = """"c OrElse c = "'"c Then Return ReadQuoted()
                If c = "["c AndAlso (PeekAt(1) = "["c OrElse PeekAt(1) = "="c) Then Return ReadLongString()
                Dim start = _pos
                Do While _pos < _text.Length AndAlso Not IsDelimiter(_text(_pos))
                    _pos += 1
                Loop
                Dim token = _text.Substring(start, _pos - start)
                Select Case token
                    Case "true" : Return True
                    Case "false" : Return False
                    Case "nil" : Return Nothing
                End Select
                Dim number As Double
                If Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, number) Then Return number
                Throw New FormatException($"unerwartet an Stelle {start}: {Shorten(token)}")
            End Function

            Private Function ReadTable() As LuaTable
                _depth += 1
                If _depth > MaxDepth Then Throw New FormatException("zu tief verschachtelt")
                _pos += 1
                Dim table As New LuaTable()
                Do
                    SkipSpace()
                    If _pos >= _text.Length Then Throw New FormatException("Tabelle nicht geschlossen")
                    If _text(_pos) = "}"c Then
                        _pos += 1
                        Exit Do
                    End If
                    Dim key As String = Nothing
                    If _text(_pos) = "["c AndAlso PeekAt(1) <> "["c AndAlso PeekAt(1) <> "="c Then
                        _pos += 1
                        Dim keyValue = ReadValue()
                        SkipSpace()
                        Expect("]"c)
                        SkipSpace()
                        Expect("="c)
                        key = ScalarKey(keyValue)
                    Else
                        Dim start = _pos
                        Dim name = ReadIdentifier()
                        If name IsNot Nothing Then
                            SkipSpace()
                            If Peek() = "="c AndAlso PeekAt(1) <> "="c Then
                                _pos += 1
                                key = name
                            Else
                                _pos = start
                            End If
                        End If
                    End If
                    Dim value = ReadValue()
                    If key Is Nothing Then
                        table.Items.Add(value)
                    ElseIf value IsNot Nothing Then
                        table.Named(key) = value
                    End If
                    SkipSpace()
                    If Peek() = ","c OrElse Peek() = ";"c Then _pos += 1
                Loop
                _depth -= 1
                Return table
            End Function

            Private Function ReadQuoted() As String
                Dim quote = _text(_pos)
                _pos += 1
                Dim sb As New StringBuilder()
                Do
                    If _pos >= _text.Length Then Throw New FormatException("Text nicht geschlossen")
                    Dim c = _text(_pos)
                    If c = quote Then
                        _pos += 1
                        Exit Do
                    End If
                    If c = "\"c AndAlso _pos + 1 < _text.Length Then
                        Dim n = _text(_pos + 1)
                        Select Case n
                            Case "n"c : sb.Append(vbLf)
                            Case "t"c : sb.Append(vbTab)
                            Case "r"c : sb.Append(vbCr)
                            Case Else : sb.Append(n)
                        End Select
                        _pos += 2
                    Else
                        sb.Append(c)
                        _pos += 1
                    End If
                Loop
                Return sb.ToString()
            End Function

            ''' <summary>[[...]] oder [==[...]==]; ein Zeilenumbruch gleich nach der Klammer gehoert
            ''' nicht zum Text.</summary>
            Private Function ReadLongString() As String
                Dim start = _pos
                _pos += 1
                Dim level = 0
                Do While Peek() = "="c
                    level += 1
                    _pos += 1
                Loop
                If Peek() <> "["c Then Throw New FormatException($"unerwartet an Stelle {start}")
                _pos += 1
                Dim close = "]" & New String("="c, level) & "]"
                Dim finish = _text.IndexOf(close, _pos, StringComparison.Ordinal)
                If finish < 0 Then Throw New FormatException("langer Text nicht geschlossen")
                Dim value = _text.Substring(_pos, finish - _pos)
                _pos = finish + close.Length
                If value.StartsWith(vbCrLf, StringComparison.Ordinal) Then Return value.Substring(2)
                If value.StartsWith(vbLf, StringComparison.Ordinal) Then Return value.Substring(1)
                Return value
            End Function

            Private Function ReadIdentifier() As String
                If _pos >= _text.Length Then Return Nothing
                Dim c = _text(_pos)
                If Not (Char.IsLetter(c) OrElse c = "_"c) Then Return Nothing
                Dim start = _pos
                Do While _pos < _text.Length AndAlso (Char.IsLetterOrDigit(_text(_pos)) OrElse _text(_pos) = "_"c)
                    _pos += 1
                Loop
                Dim name = _text.Substring(start, _pos - start)
                ' Wahrheitswerte und nil sind Werte, keine Namen.
                If name = "true" OrElse name = "false" OrElse name = "nil" Then
                    _pos = start
                    Return Nothing
                End If
                Return name
            End Function

            Private Sub SkipSpace()
                Do While _pos < _text.Length
                    If Char.IsWhiteSpace(_text(_pos)) Then
                        _pos += 1
                    ElseIf _text(_pos) = "-"c AndAlso PeekAt(1) = "-"c Then
                        Do While _pos < _text.Length AndAlso _text(_pos) <> ChrW(10)
                            _pos += 1
                        Loop
                    Else
                        Exit Do
                    End If
                Loop
            End Sub

            Private Sub Expect(c As Char)
                If Peek() <> c Then Throw New FormatException($"erwartet {c} an Stelle {_pos}")
                _pos += 1
            End Sub

            Private Function Peek() As Char
                Return If(_pos < _text.Length, _text(_pos), ChrW(0))
            End Function

            Private Function PeekAt(offset As Integer) As Char
                Dim at = _pos + offset
                Return If(at < _text.Length, _text(at), ChrW(0))
            End Function

            Private Shared Function IsDelimiter(c As Char) As Boolean
                Return Char.IsWhiteSpace(c) OrElse c = ","c OrElse c = ";"c OrElse c = "}"c OrElse c = "]"c OrElse c = "="c
            End Function

            Private Shared Function ScalarKey(value As Object) As String
                If TypeOf value Is Double Then Return CDbl(value).ToString("R", CultureInfo.InvariantCulture)
                Return Convert.ToString(value, CultureInfo.InvariantCulture)
            End Function

            Private Shared Function Shorten(value As String) As String
                Return If(value.Length > 24, value.Substring(0, 24), value)
            End Function
        End Class

    End Class

End Namespace
