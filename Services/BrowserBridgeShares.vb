Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>
    ''' Freigaben per Link: Bilder fuer Menschen ohne Konto (Plan BROWSER_MOBIL.md, "Gebaut:
    ''' Teilen"; Ablage in ShareStore.vb).
    '''
    ''' <para>DIE SEITE DER EMPFAENGER liegt unter /s/&lt;token&gt;, ohne Anmeldung, mit eigener
    ''' kleiner Schnittstelle unter /s/&lt;token&gt;/...: Liste, Kachel, Vorschau, Infoblatt,
    ''' Original, ZIP, Kennwort, Abbruch. Sie kennt nichts sonst: keine Sitzung der Anwendung, kein
    ''' WebSocket, keine Wege unter /api/m/. Bilder gehen mit EIGENEN Kennungen je Freigabe hinaus
    ''' (<see cref="ShareItemId"/>): ein HMAC aus Freigabe und Pfad, aufgeloest nur gegen die Liste
    ''' dieser einen Freigabe. Eine Kennung der mobilen Seite fuehrt hier nirgendwohin und eine der
    ''' Freigabe dort nicht.</para>
    '''
    ''' <para>WER TEILEN DARF, fragt nur MobileContext: auf jedem geteilten Bild bzw. dem Ordner
    ''' mindestens "Mitarbeiten" (<see cref="CanShareFile"/>, <see cref="CanShareFolder"/>), ein
    ''' Administrator alles. Dieselbe Pruefung gilt bei JEDEM Abruf noch einmal mit dem Kontext des
    ''' Teilenden: verliert er das Recht, wird er gesperrt oder geloescht, ist seine Freigabe aus
    ''' (ein Ordner ganz, aus einer Liste fallen die Bilder heraus; bleibt keines, ist auch sie aus).</para>
    '''
    ''' <para>UNABHAENGIG VOM SPERREN DER ANWENDUNG, so entschieden: ein Link soll auch dann gehen,
    ''' wenn die Anwendung gesperrt ist; sonst stuende jede Freigabe still, sobald der Eigentuemer
    ''' sperrt oder die Sperrzeit ablaeuft. Fuer den Fall, dass alles sofort ruhen soll, gibt es
    ''' "Alle Freigaben pausieren" (ShareStore.SetPaused). Ohne Kennwort der Anwendung (vor der
    ''' Einrichtung, nach dem Notweg) antwortet jeder Link wie ein unbekannter.</para>
    '''
    ''' <para>ABGELAUFEN, WIDERRUFEN, PAUSIERT oder ohne Recht antwortet ein Link wie ein unbekannter
    ''' (404). Gegen Durchprobieren zaehlt jedes unbekannte Token als Fehlversuch der Absenderadresse,
    ''' mit derselben Bremse wie die Anmeldung, aber einem eigenen Zaehler ("share ...") in einer
    ''' eigenen Liste, damit sich Raten an Links und Raten am Anmeldefenster nicht gegenseitig
    ''' sperren oder verdraengen.</para>
    '''
    ''' <para>WAS HINAUSGEHT: Kachel und Vorschau immer mit Bearbeitung und ohne Metadaten (wie die
    ''' mobile Seite, durch dieselbe Warteschlange und DecodeGate), nie eine Beistelldatei.
    ''' Herunterladen nur, wenn es erlaubt ist (<see cref="ShareDeliveryFor"/>): ein bearbeitetes
    ''' Bild (Vermerk "bearbeitet", Variante) und jedes Format ausser JPEG, PNG und WebP (RAW, HEIC,
    ''' TIFF, PSD, ...) als JPEG mit Bearbeitung in voller Groesse ohne Metadaten, wie die Seite es
    ''' zeigt; ein unbearbeitetes JPEG, PNG oder WebP unveraendert, wenn Ort, Personen und Kamera
    ''' mitgehen, sonst als JPEG ohne Metadaten (JpegMetadataStrip, PNG und WebP gerechnet); ein
    ''' Video nur mit allen Metadaten (sein Ort laesst sich nicht herausnehmen).
    ''' Dasselbe gilt fuer jeden Eintrag des ZIP.</para>
    ''' </summary>
    Partial Public NotInheritable Class BrowserBridge

        Friend Const SharePrefix As String = "/s/"
        Private Const ShareAssetPrefix As String = "/s/_/"
        Private Const ShareAssetFolder As String = "avares://FerrumPix/Assets/BrowserBridge/share/"
        Friend Const ShareCookieName As String = "ferrumpix_share"
        Friend Const MobileSharesScriptRoute As String = "/m/shares.js"
        Friend Const MobileSharesStyleRoute As String = "/m/shares.css"
        Private Const ShareItemKind As Char = "s"c

        ''' <summary>Hoechstens so viele Bilder zeigt eine Freigabe, auch ein grosser Ordner.</summary>
        Private Const MaxShareItems As Integer = 20_000

        ''' <summary>Hoechstens so viele Bilder nimmt eine Auswahl oder ein Suchergebnis auf.</summary>
        Friend Const MaxShareListPaths As Integer = ShareStore.MaxPathsPerShare

        Private Const MaxShareTitleLength As Integer = 120
        Private Const MinSharePasswordLength As Integer = 8
        Private Const ShareListingLifetimeMs As Long = 20_000
        Private Const MaxShareVisits As Integer = 4096

        ''' <summary>Wie lange ein Browser nach dem richtigen Kennwort einer Freigabe ohne neue
        ''' Eingabe weiter sieht, gerechnet ab der letzten Nutzung.</summary>
        Private Shared ReadOnly ShareVisitLifetime As TimeSpan = TimeSpan.FromHours(12)

        ''' <summary>Die Stufen fuer das Ende einer Freigabe in Tagen; 0 heisst nie.</summary>
        Friend Shared ReadOnly ShareDayChoices As Integer() = {7, 30, 90, 365, 0}
        Friend Const DefaultShareDays As Integer = 30

        Private Shared ReadOnly ShareIdKey As Byte() = RandomNumberGenerator.GetBytes(32)
        Private Shared ReadOnly ShareAssets As New Concurrent.ConcurrentDictionary(Of String, String)(StringComparer.Ordinal)
        Private Shared _shareVersion As String
        Private Shared ReadOnly ShareVerifyGate As New Object()
        Private Shared _lastShareLinkBase As String = ""

        ''' <summary>Ein Browser, der das Kennwort einer Freigabe eingegeben hat.</summary>
        Private NotInheritable Class ShareVisit
            Public ShareId As String
            ''' <summary>Der Hash zur Zeit der Eingabe; ein neues Kennwort macht den Besuch ungueltig.</summary>
            Public PasswordHash As String
            Public LastUsedUtc As DateTime
        End Class

        Private Shared ReadOnly ShareVisits As New Dictionary(Of String, ShareVisit)(StringComparer.Ordinal)

        ''' <summary>Ein Bild einer Freigabe, wie es ausgeliefert wird.</summary>
        Friend NotInheritable Class ShareEntry
            Public Id As String
            ''' <summary>Der Pfad der Datei, bei einer zusaetzlichen Variante ihr Schluessel
            ''' (VariantKey); die Datei dahinter gibt VariantKey.FileOf.</summary>
            Public Path As String
            ''' <summary>Aufnahmezeit als ISO ohne Zone, leer ohne Datum.</summary>
            Public Taken As String
            ''' <summary>Stand der Datei fuer den Zwischenspeicher des Browsers.</summary>
            Public Stamp As String
        End Class

        Private NotInheritable Class ShareListing
            Public Key As String
            Public BuiltAt As Long
            Public Entries As List(Of ShareEntry)
        End Class

        Private Shared ReadOnly ShareListings As New Dictionary(Of String, ShareListing)(StringComparer.Ordinal)

        ''' <summary>Listen, die gerade gebaut werden, je Freigabe und Stand. Unter der Sperre von
        ''' <see cref="ShareListings"/>.</summary>
        Private Shared ReadOnly ShareListingBuilds As New Dictionary(Of String, Lazy(Of List(Of ShareEntry)))(StringComparer.Ordinal)

        ''' <summary>Was bei einem Herunterladen hinausgeht.</summary>
        Friend Enum ShareDelivery
            ''' <summary>Nichts: Herunterladen ist aus, oder ein Video mit Metadaten, die nicht mitgehen.</summary>
            None
            ''' <summary>Die Datei, wie sie ist.</summary>
            AsIs
            ''' <summary>Das JPEG ohne Metadaten, die Bilddaten unveraendert.</summary>
            StrippedJpeg
            ''' <summary>Ein JPEG mit Bearbeitung in voller Groesse, ohne Metadaten.</summary>
            Rendered
        End Enum

        ' --- Rechte ------------------------------------------------------------------------------

        ''' <summary>Darf dieser Benutzer die Datei teilen? Er muss sie ausliefern duerfen und auf ihr
        ''' mindestens "Mitarbeiten" haben (ein Administrator hat alles). Allein ueber den Kontext.
        ''' Eine zusaetzliche Variante (VariantKey) nach den Rechten ihrer Datei, und nur, solange es
        ''' sie in der Begleitdatei noch gibt.</summary>
        Friend Shared Function CanShareFile(mobile As MobileContext, filePath As String) As Boolean
            If mobile Is Nothing OrElse String.IsNullOrEmpty(filePath) Then Return False
            Dim file = VariantKey.FileOf(filePath)
            If Not MediaFileTypes.IsDisplayable(file) OrElse Not mobile.CanDownload(file) OrElse
               mobile.LevelAt(file) < FolderAccessLevel.Contribute Then Return False
            Return Not VariantKey.IsVariant(filePath) OrElse MobileContext.ImageExists(filePath)
        End Function

        ''' <summary>Darf dieser Benutzer den Ordner teilen? Er muss hineinsehen duerfen und auf ihm
        ''' mindestens "Mitarbeiten" haben. Bilder in Unterordnern mit engerer Regel fallen beim
        ''' Ausliefern trotzdem heraus (<see cref="CanShareFile"/> je Bild).</summary>
        Friend Shared Function CanShareFolder(mobile As MobileContext, folder As String) As Boolean
            If mobile Is Nothing OrElse String.IsNullOrEmpty(folder) Then Return False
            Try
                If Not Directory.Exists(folder) Then Return False
            Catch
                Return False
            End Try
            Return mobile.CanSeeFolder(folder) AndAlso mobile.LevelAt(folder) >= FolderAccessLevel.Contribute
        End Function

        ''' <summary>Gilt die Freigabe gerade? Nicht pausiert, nicht abgelaufen, der Teilende kann
        ''' sich anmelden, und bei einem Ordner darf er ihn noch teilen. Ohne Kennwort der
        ''' Anwendung gilt keine.</summary>
        Friend Shared Function IsShareActive(record As ShareRecord) As Boolean
            If record Is Nothing OrElse Not AppLockService.IsProtected Then Return False
            If ShareStore.Paused OrElse ShareStore.IsExpired(record, AppLockService.Clock()) Then Return False
            If Not UserAccountService.IsActive(record.OwnerUserId) Then Return False
            If record.KindValue() = ShareKind.Folder Then
                Return CanShareFolder(New MobileContext(record.OwnerUserId, ""), record.Folder)
            End If
            Return True
        End Function

        ''' <summary>Die Freigabe zu einem Token, wenn sie gilt UND noch etwas zeigt; sonst Nothing.
        ''' Eine Liste, aus der jedes Bild herausgefallen ist (kein Recht mehr, alles geloescht),
        ''' gilt wie eine beendete.</summary>
        Friend Shared Function ResolveShare(token As String) As ShareRecord
            Dim record = ShareStore.FindByToken(token)
            If Not IsShareActive(record) Then Return Nothing
            If record.KindValue() <> ShareKind.Folder AndAlso ShareEntriesOf(record).Count = 0 Then Return Nothing
            Return record
        End Function

        ''' <summary>Gibt die Freigabe Ort, Personen und Kamera mit? Nur dann darf eine Datei mit
        ''' allen ihren Metadaten hinaus.</summary>
        Private Shared Function SharesAllMetadata(record As ShareRecord) As Boolean
            Return record.ShareLocation AndAlso record.SharePeople AndAlso record.ShareCamera
        End Function

        ''' <summary>Laesst sich das Bild aus der Freigabe herunterladen, egal in welcher Form? Billig
        ''' (kein Blick ins Rezept), fuer die Liste: Herunterladen erlaubt, und ein Video nur mit
        ''' allen Metadaten (sein Ort laesst sich nicht herausnehmen).</summary>
        Friend Shared Function CanDownloadFromShare(record As ShareRecord, key As String) As Boolean
            If record Is Nothing OrElse Not record.AllowDownload Then Return False
            Return Not MediaFileTypes.IsVideo(VariantKey.FileOf(key)) OrElse SharesAllMetadata(record)
        End Function

        ''' <summary>Geht das Bild gerechnet hinaus, mit seiner Bearbeitung? Eine zusaetzliche
        ''' Variante (ihr eigenes Rezept), eine RAW (sie wird entwickelt) und jede Datei, deren
        ''' Begleitdatei den Vermerk "bearbeitet" traegt (RawSidecarService.IsEditedFile, auch ein
        ''' FerrumPix-Projekt). So, wie die Seite es zeigt.</summary>
        Friend Shared Function IsRenderedForShare(key As String) As Boolean
            If VariantKey.IsVariant(key) Then Return True
            Dim filePath = VariantKey.FileOf(key)
            If MediaFileTypes.IsVideo(filePath) Then Return False
            Return RawPreviewService.IsSupportedRaw(filePath) OrElse RawSidecarService.IsEditedFile(filePath)
        End Function

        ''' <summary>Was beim Herunterladen dieses Bildes hinausgeht. Ein bearbeitetes Bild
        ''' (<see cref="IsRenderedForShare"/>) immer gerechnet, mit Bearbeitung und ohne Metadaten,
        ''' wie die Seite es zeigt; ebenso jedes Format ausser JPEG, PNG und WebP
        ''' (<see cref="IsPortableImageFormat"/>), eine RAW also nie als RAW. Ein unbearbeitetes
        ''' JPEG, PNG oder WebP unveraendert, wenn Ort, Personen und Kamera mitgehen, sonst ein JPEG
        ''' ohne Metadaten (PNG und WebP gerechnet). Ein Video nur mit allen Metadaten, sonst gar
        ''' nicht.</summary>
        Friend Shared Function ShareDeliveryFor(record As ShareRecord, filePath As String) As ShareDelivery
            If Not CanDownloadFromShare(record, filePath) Then Return ShareDelivery.None
            Dim file = VariantKey.FileOf(filePath)
            If MediaFileTypes.IsVideo(file) Then Return ShareDelivery.AsIs
            ' Als Original gehen nur Formate, die jeder Empfaenger oeffnen kann; alles andere (RAW,
            ' DNG, HEIC, TIFF, PSD, JPEG XL, ...) kommt immer als JPEG an, auch mit allen Metadaten.
            If Not IsPortableImageFormat(file) OrElse IsRenderedForShare(filePath) Then Return ShareDelivery.Rendered
            If SharesAllMetadata(record) Then Return ShareDelivery.AsIs
            If IsJpegExtension(file) Then Return ShareDelivery.StrippedJpeg
            ' PNG und WebP ohne alle Metadaten: es gibt kein Herausnehmen wie beim JPEG, also gerechnet.
            Return ShareDelivery.Rendered
        End Function

        ''' <summary>Darf ein Bild dieses Formats aus einer Freigabe oder der mobilen Seite als
        ''' Original hinaus? Nur JPEG, PNG und WebP: die oeffnet jedes Geraet. RAW, DNG,
        ''' HEIC/HEIF/AVIF, TIFF, PSD, FerrumPix-Projekt, JPEG XL, BMP, GIF und der Rest gehen dort
        ''' nur als gerechnetes JPEG hinaus. Videos fragt niemand hier (eigene Regel). Die volle
        ''' Oberflaeche im Browser bleibt davon unberuehrt: dort geht alles als Original.</summary>
        Friend Shared Function IsPortableImageFormat(filePath As String) As Boolean
            Dim extension = IO.Path.GetExtension(VariantKey.FileOf(filePath)).ToLowerInvariant()
            Return IsJpegExtension(filePath) OrElse extension = ".png" OrElse extension = ".webp"
        End Function

        Private Shared Function IsJpegExtension(filePath As String) As Boolean
            Dim extension = IO.Path.GetExtension(VariantKey.FileOf(filePath)).ToLowerInvariant()
            Return extension = ".jpg" OrElse extension = ".jpeg" OrElse extension = ".jpe"
        End Function

        ''' <summary>Darf der Film eines Bewegtbilds hinaus? Nach derselben Regel wie ein Video:
        ''' Herunterladen erlaubt und alle Metadaten (der Film traegt seine eigenen, auch den Ort).
        ''' Nie an einer Variante.</summary>
        Private Shared Function ShareMotionAllowed(record As ShareRecord, key As String) As Boolean
            Return record IsNot Nothing AndAlso record.AllowDownload AndAlso SharesAllMetadata(record) AndAlso Not VariantKey.IsVariant(key)
        End Function

        ''' <summary>Der Name eines gerechneten Bildes: der Name der Datei mit ".jpg", bei einer
        ''' Variante ihr Name in Klammern dahinter, ohne Zeichen, die ein Dateiname nicht traegt.</summary>
        Friend Shared Function ShareRenderedName(key As String) As String
            Return CleanShareFileName(ShareImageTitle(key)) & ".jpg"
        End Function

        ''' <summary>Das Kamera-JPEG eines RAW-Paars, das statt der RAW hinausgeht (Freigabe und
        ''' mobile Seite, einzeln und im ZIP): nur bei einer UNBEARBEITETEN RAW (kein Vermerk
        ''' "bearbeitet", keine Variante) mit einem JPEG, PNG oder WebP gleichen Namens daneben, das
        ''' <paramref name="canUse"/> erlaubt. Sonst Nothing, und die RAW geht gerechnet unter dem
        ''' Namen des Paares hinaus. So kommt je Paar genau EIN JPEG an, ohne Namensgleichheit.</summary>
        Friend Shared Function PairJpegFor(key As String, canUse As Func(Of String, Boolean)) As String
            If String.IsNullOrEmpty(key) OrElse VariantKey.IsVariant(key) Then Return Nothing
            If Not RawPreviewService.IsCameraRaw(key) OrElse RawSidecarService.IsEditedFile(key) Then Return Nothing
            Return MobilePartnersOf(key).FirstOrDefault(Function(p) IsPortableImageFormat(p) AndAlso canUse(p))
        End Function

        ''' <summary>Der Name eines Bildes ohne Endung, bei einer Variante mit ihrem Namen in
        ''' Klammern; als Titel einer Freigabe aus einem Bild.</summary>
        Private Shared Function ShareImageTitle(key As String) As String
            Dim stem = IO.Path.GetFileNameWithoutExtension(VariantKey.FileOf(key))
            If Not VariantKey.IsVariant(key) Then Return stem
            Return stem & " (" & RawSidecarService.VariantName(key) & ")"
        End Function

        Private Shared Function CleanShareFileName(name As String) As String
            Dim invalid = IO.Path.GetInvalidFileNameChars()
            Dim clean As New StringBuilder()
            For Each c In If(name, "")
                clean.Append(If(invalid.Contains(c) OrElse Char.IsControl(c) OrElse c = "/"c OrElse c = "\"c OrElse c = ":"c, "_"c, c))
            Next
            Return clean.ToString().Trim()
        End Function

        ' --- Die Bilder einer Freigabe ---------------------------------------------------------------

        ''' <summary>Die Kennung eines Bildes in einer Freigabe: "s" und 16 Zeichen aus einem HMAC
        ''' ueber Freigabe und Pfad mit einem Schluessel je Lauf. Dieselbe Datei hat in zwei
        ''' Freigaben verschiedene Kennungen, und keine gleicht einer der mobilen Seite.</summary>
        Friend Shared Function ShareItemId(shareId As String, filePath As String) As String
            Dim hash = HMACSHA256.HashData(ShareIdKey, Encoding.UTF8.GetBytes(shareId & "|" & LibraryService.PathKey(filePath)))
            Return ShareItemKind & Convert.ToBase64String(hash, 0, 12).Replace("+"c, "-"c).Replace("/"c, "_"c)
        End Function

        ''' <summary>Die Bilder einer Freigabe, wie der Teilende sie jetzt teilen darf. Gehalten
        ''' 20 Sekunden je Freigabe und Stand von Ablage, Benutzern und Fotoordnern.</summary>
        Friend Shared Function ShareEntriesOf(record As ShareRecord) As List(Of ShareEntry)
            Dim key = String.Join("|", record.Id, ShareStore.Version.ToString(CultureInfo.InvariantCulture),
                                  UserAccountService.Version.ToString(CultureInfo.InvariantCulture), String.Join(";", FolderRoots.Roots))
            Dim now = Environment.TickCount64
            Dim build As Lazy(Of List(Of ShareEntry)) = Nothing
            Dim owner = False
            SyncLock ShareListings
                Dim held As ShareListing = Nothing
                If ShareListings.TryGetValue(record.Id, held) AndAlso held.Key = key AndAlso now - held.BuiltAt < ShareListingLifetimeMs Then Return held.Entries
                ' EINE Berechnung je Freigabe und Stand: wer waehrenddessen fragt, wartet auf sie,
                ' statt einen grossen Ordner ein zweites Mal zu durchlaufen.
                Dim pendingKey = record.Id & "|" & key
                If Not ShareListingBuilds.TryGetValue(pendingKey, build) Then
                    Dim snapshot = record
                    build = New Lazy(Of List(Of ShareEntry))(Function() BuildShareEntries(snapshot), Threading.LazyThreadSafetyMode.ExecutionAndPublication)
                    ShareListingBuilds(pendingKey) = build
                    owner = True
                End If
            End SyncLock
            Dim built As List(Of ShareEntry)
            Try
                built = build.Value
            Finally
                If owner Then
                    SyncLock ShareListings
                        ShareListingBuilds.Remove(record.Id & "|" & key)
                    End SyncLock
                End If
            End Try
            If owner Then
                SyncLock ShareListings
                    If ShareListings.Count > 64 Then ShareListings.Clear()
                    ShareListings(record.Id) = New ShareListing With {.Key = key, .BuiltAt = now, .Entries = built}
                End SyncLock
            End If
            Return built
        End Function

        ''' <summary>Wie viele Listen gerade gebaut wurden, statt auf eine laufende zu warten. Fuer
        ''' den Pruefstand.</summary>
        Friend Shared ShareListingBuildCount As Integer

        ''' <summary>Vergisst die gehaltenen Listen. Fuer den Pruefstand.</summary>
        Friend Shared Sub ForgetShareListings()
            SyncLock ShareListings
                ShareListings.Clear()
            End SyncLock
        End Sub

        Private Shared Function BuildShareEntries(record As ShareRecord) As List(Of ShareEntry)
            Threading.Interlocked.Increment(ShareListingBuildCount)
            Dim owner As New MobileContext(record.OwnerUserId, "")
            Dim candidates As List(Of String)
            If record.KindValue() = ShareKind.Folder Then
                candidates = If(CanShareFolder(owner, record.Folder),
                                EnumerateShareFolder(owner, record.Folder, record.IncludeSubfolders),
                                New List(Of String)())
            Else
                ' Nur die Datei, die geteilt wurde: liegt unter dem Pfad inzwischen eine andere
                ' (Fingerabdruck aus Groesse und Zeit weicht ab), fehlt das Bild. Eine Variante,
                ' die aus der Begleitdatei verschwunden ist, ebenso.
                candidates = record.Paths.Where(Function(p) MobileContext.ImageExists(p) AndAlso ShareStore.IsSameFile(record, p)).ToList()
            End If
            Dim paths = CollapseSharePairs(candidates.Where(Function(p) CanShareFile(owner, p)).
                                                     Distinct(PathIdentity.Comparer).Take(MaxShareItems).ToList())
            ' Die Aufnahmezeit einer Variante ist die ihrer Datei.
            Dim metas = LibraryService.Instance.GetMetaForPaths(paths.Select(AddressOf VariantKey.FileOf).Distinct(PathIdentity.Comparer))
            Dim entries As New List(Of ShareEntry)(paths.Count)
            For Each filePath In paths
                Dim meta As LibraryImageMeta = Nothing
                metas.TryGetValue(VariantKey.FileOf(filePath), meta)
                entries.Add(New ShareEntry With {
                    .Id = ShareItemId(record.Id, filePath),
                    .Path = filePath,
                    .Taken = ShareTakenIso(If(meta?.DateTaken, "")),
                    .Stamp = ShareStampOf(filePath)})
            Next
            ' Wie ein Album: aelteste zuerst, ohne Datum nach Namen am Ende.
            Return entries.OrderBy(Function(e) If(e.Taken.Length = 0, 1, 0)).
                           ThenBy(Function(e) e.Taken, StringComparer.Ordinal).
                           ThenBy(Function(e) IO.Path.GetFileName(e.Path), StringComparer.CurrentCultureIgnoreCase).ToList()
        End Function

        ''' <summary>Die Bilder eines Ordners, auf Wunsch mit Unterordnern: ohne Verstecktes, ohne
        ''' Verweise (ein Verweis nach draussen fuehrte aus dem Ordner hinaus), ohne Ordner, in die
        ''' der Teilende nicht sehen darf.</summary>
        Private Shared Function EnumerateShareFolder(owner As MobileContext, folder As String, includeSubfolders As Boolean) As List(Of String)
            Dim result As New List(Of String)()
            Dim pending As New Stack(Of String)()
            pending.Push(folder)
            While pending.Count > 0 AndAlso result.Count < MaxShareItems
                Dim current = pending.Pop()
                Dim subfolders As New List(Of String)()
                Try
                    For Each entry In New DirectoryInfo(current).EnumerateFileSystemInfos()
                        If entry.Name.StartsWith("."c) Then Continue For
                        If (entry.Attributes And (FileAttributes.Hidden Or FileAttributes.System Or FileAttributes.ReparsePoint)) <> 0 Then Continue For
                        If TypeOf entry Is DirectoryInfo Then
                            If includeSubfolders AndAlso owner.CanSeeFolder(entry.FullName) Then subfolders.Add(entry.FullName)
                        ElseIf MediaFileTypes.IsDisplayable(entry.Name) Then
                            result.Add(entry.FullName)
                        End If
                    Next
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
                subfolders.Sort(StringComparer.Ordinal)
                For i = subfolders.Count - 1 To 0 Step -1
                    pending.Push(subfolders(i))
                Next
            End While
            Return result
        End Function

        ''' <summary>Liegen eine RAW und ihr JPEG (oder HEIF) gleichen Namens im selben Ordner, steht
        ''' nur die RAW da, mit ihrer Bearbeitung; dieselbe Paarregel wie in der mobilen Seite. Eine
        ''' zusaetzliche Variante bleibt immer: sie wurde ausdruecklich geteilt.</summary>
        Private Shared Function CollapseSharePairs(paths As List(Of String)) As List(Of String)
            Dim rawStems As New HashSet(Of String)(StringComparer.Ordinal)
            For Each filePath In paths
                If Not VariantKey.IsVariant(filePath) AndAlso RawPreviewService.IsCameraRaw(filePath) Then rawStems.Add(SharePairKey(filePath))
            Next
            If rawStems.Count = 0 Then Return paths
            Return paths.Where(Function(p) VariantKey.IsVariant(p) OrElse RawPreviewService.IsCameraRaw(p) OrElse
                                           Not ImageStackService.IsCompanionFormat(p) OrElse
                                           Not rawStems.Contains(SharePairKey(p))).ToList()
        End Function

        Private Shared Function SharePairKey(filePath As String) As String
            Return LibraryService.PathKey(IO.Path.Combine(If(IO.Path.GetDirectoryName(filePath), ""), IO.Path.GetFileNameWithoutExtension(filePath)))
        End Function

        ''' <summary>"yyyy-MM-ddTHH:mm:ss" aus der Aufnahmezeit des Katalogs, leer ohne Datum.</summary>
        Private Shared Function ShareTakenIso(taken As String) As String
            If String.IsNullOrEmpty(taken) OrElse taken.Length < 10 Then Return ""
            Dim day = taken.Substring(0, 10).Replace(":"c, "-"c)
            Dim parsed As DateTime
            If Not DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, parsed) Then Return ""
            If taken.Length >= 19 Then
                Dim clock = taken.Substring(11, 8)
                If clock(2) = ":"c AndAlso clock(5) = ":"c AndAlso clock.All(Function(c) Char.IsAsciiDigit(c) OrElse c = ":"c) Then Return day & "T" & clock
            End If
            Return day & "T00:00:00"
        End Function

        ''' <summary>Stand der Datei und ihres Rezepts, kurz; bei einer Variante dazu ihre Kennung.</summary>
        Private Shared Function ShareStampOf(filePath As String) As String
            Dim parts As New StringBuilder(VariantKey.IdOf(filePath))
            Try
                Dim info As New FileInfo(VariantKey.FileOf(filePath))
                parts.Append(info.LastWriteTimeUtc.Ticks).Append("|"c).Append(info.Length)
                Dim recipe = RawSidecarService.SidecarPathFor(filePath)
                If Not String.IsNullOrEmpty(recipe) AndAlso File.Exists(recipe) Then parts.Append("|"c).Append(File.GetLastWriteTimeUtc(recipe).Ticks)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts.ToString())), 0, 4).ToLowerInvariant()
        End Function

        ''' <summary>Das Bild zu einer Kennung dieser Freigabe, wenn es noch da ist und der Teilende
        ''' es noch teilen darf; sonst Nothing. Der einzige Weg von einer Kennung zu einem Pfad.</summary>
        Private Shared Function ResolveShareEntry(record As ShareRecord, id As String) As ShareEntry
            If String.IsNullOrEmpty(id) OrElse id.Length <> 17 OrElse id(0) <> ShareItemKind Then Return Nothing
            Dim found = ShareEntriesOf(record).FirstOrDefault(Function(e) String.Equals(e.Id, id, StringComparison.Ordinal))
            If found Is Nothing OrElse Not MobileContext.ImageExists(found.Path) Then Return Nothing
            ' Die Liste ist bis zu 20 Sekunden alt; dazwischen kann eine fremde Datei den Pfad genommen haben.
            If record.KindValue() <> ShareKind.Folder AndAlso Not ShareStore.IsSameFile(record, found.Path) Then Return Nothing
            If Not CanShareFile(New MobileContext(record.OwnerUserId, ""), found.Path) Then Return Nothing
            Return found
        End Function

        ' --- Die Seite der Empfaenger ------------------------------------------------------------------

        ''' <summary>Gehoert der Pfad zu einer Freigabe?</summary>
        Friend Shared Function IsShareRoute(path As String) As Boolean
            Return Not String.IsNullOrEmpty(path) AndAlso path.StartsWith(SharePrefix, StringComparison.Ordinal)
        End Function

        ''' <summary>Alles unter /s/. Ohne Anmeldung, ohne Blick auf die Sperre der Anwendung; Weg und
        ''' Absender sind schon geprueft (IsAcceptedRoute in HandleContextAsync). Die Sprache kommt
        ''' aus Accept-Language des Browsers.</summary>
        Friend Shared Async Function HandleShareRouteAsync(context As HttpListenerContext, path As String) As Task
            ' Die Wege fragen Platte und Ablage; die naechste Anfrage soll nicht dahinter warten.
            Await Task.Yield()
            Dim request = context.Request
            Using LocalizationService.UseRequestCulture(LocalizationService.CultureForAcceptLanguage(request.Headers("Accept-Language")))
                If path.StartsWith(ShareAssetPrefix, StringComparison.Ordinal) Then
                    Await ServeShareAssetAsync(context, path.Substring(ShareAssetPrefix.Length))
                    Return
                End If
                Dim rest = path.Substring(SharePrefix.Length)
                Dim slash = rest.IndexOf("/"c)
                Dim token = If(slash < 0, rest, rest.Substring(0, slash))
                Dim action = If(slash < 0, "", rest.Substring(slash + 1))
                Dim isPage = action.Length = 0

                ' Bremse gegen Raten: je Absenderadresse, eigener Zaehler.
                Dim guessCounter = AppLockService.ShareCounterPrefix & SenderKey(request)
                Dim wait = AppLockService.RetryAfter(guessCounter)
                If wait > TimeSpan.Zero Then
                    context.Response.Headers("Retry-After") = CInt(Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
                    SecurityLog.Throttled("share", LogAddress(request), wait)
                    If isPage Then
                        Await ServeSharePageAsync(context, Nothing, 429)
                    Else
                        Await WriteMobileJsonAsync(context, 429, MobileProblem(LocalizationService.T("Zu viele Anfragen. Bitte später noch einmal versuchen.")))
                    End If
                    Return
                End If

                Dim record = Await Task.Run(Function() ResolveShare(token))
                If record Is Nothing Then
                    ' Nur ein Token, das es gar nicht gibt, zaehlt als Raten; ein abgelaufener oder
                    ' pausierter Link antwortet genauso, ohne den Empfaenger auszubremsen.
                    If ShareStore.FindByToken(token) Is Nothing Then AppLockService.RegisterFailedAttempt(guessCounter)
                    If isPage Then
                        Await ServeSharePageAsync(context, Nothing, 404)
                    Else
                        Await WriteMobileJsonAsync(context, 404, New Dictionary(Of String, Object) From {{"ok", False}, {"gone", True}})
                    End If
                    Return
                End If

                Dim isPost = String.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                Dim isGet = String.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) OrElse
                            String.Equals(request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
                Dim writes = action = "unlock" OrElse action = "cancel"
                If (writes AndAlso Not isPost) OrElse (Not writes AndAlso Not isGet) Then
                    context.Response.StatusCode = 405
                    context.Response.Close()
                    Return
                End If
                If writes AndAlso Not IsOwnOrigin(request) Then
                    Await WriteMobileJsonAsync(context, 403, New Dictionary(Of String, Object) From {{"ok", False}})
                    Return
                End If

                If isPage Then
                    ShareStore.NoteView(record.Id)
                    ' Je Freigabe und Absender hoechstens einmal je Stunde (SecurityLog.ShareOpened).
                    SecurityLog.ShareOpened(record.Id, LogAddress(request))
                    Await ServeSharePageAsync(context, record, 200)
                    Return
                End If
                If action = "unlock" Then
                    Await HandleShareUnlockAsync(context, record, token)
                    Return
                End If
                ' Alles Weitere nur mit dem Kennwort der Freigabe, wenn sie eines hat.
                If Not HasShareAccess(request, record) Then
                    Await WriteMobileJsonAsync(context, 401, New Dictionary(Of String, Object) From {{"ok", False}, {"password", True}})
                    Return
                End If
                Select Case action
                    Case "list" : Await HandleShareListAsync(context, record)
                    Case "thumb" : Await HandleShareThumbAsync(context, record)
                    Case "preview" : Await HandleSharePreviewAsync(context, record)
                    Case "info" : Await HandleShareInfoAsync(context, record)
                    Case "original" : Await HandleShareOriginalAsync(context, record)
                    Case "motion" : Await HandleShareMotionAsync(context, record)
                    Case "zip" : Await HandleShareZipAsync(context, record)
                    Case "cancel" : Await HandleShareCancelAsync(context, record)
                    Case Else
                        context.Response.StatusCode = 404
                        context.Response.Close()
                End Select
            End Using
        End Function

        ''' <summary>Skript und Stil der Seite, ohne Daten, fuer jeden abrufbar (wie die Huelle der
        ''' mobilen Seite). Nur genau diese beiden Namen.</summary>
        Private Shared Async Function ServeShareAssetAsync(context As HttpListenerContext, name As String) As Task
            Dim contentType As String
            Select Case name
                Case "share.js" : contentType = "text/javascript; charset=utf-8"
                Case "share.css" : contentType = "text/css; charset=utf-8"
                Case Else
                    context.Response.StatusCode = 404
                    context.Response.Close()
                    Return
            End Select
            Dim bytes = Encoding.UTF8.GetBytes(ShareAssetText(name))
            Dim response = context.Response
            response.StatusCode = 200
            response.ContentType = contentType
            response.Headers("Cache-Control") = "public, max-age=604800"
            response.Headers("X-Content-Type-Options") = "nosniff"
            response.ContentLength64 = bytes.Length
            Await response.OutputStream.WriteAsync(bytes)
            response.Close()
        End Function

        Private Shared Function ShareAssetText(name As String) As String
            Return ShareAssets.GetOrAdd(name, Function(n) LoadAssetText(ShareAssetFolder & n))
        End Function

        ''' <summary>Versionsschluessel von Seite, Skript und Stil, fuer die Adressen von Skript und Stil.</summary>
        Private Shared Function ShareVersion() As String
            If _shareVersion Is Nothing Then
                Dim all = String.Concat(ShareAssetText("index.html"), ShareAssetText("share.js"), ShareAssetText("share.css"))
                _shareVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all))).Substring(0, 12).ToLowerInvariant()
            End If
            Return _shareVersion
        End Function

        ''' <summary>Die Seite: dieselbe Huelle fuer eine gueltige Freigabe (200), einen unbekannten,
        ''' abgelaufenen oder widerrufenen Link (404) und die Bremse (429); die Seite unterscheidet
        ''' nur ueber den Zustand im Block der Texte. Den Titel einer Freigabe holt sie erst ueber die
        ''' Liste, nach dem Kennwort.</summary>
        Private Shared Async Function ServeSharePageAsync(context As HttpListenerContext, record As ShareRecord, status As Integer) As Task
            Dim state = If(status = 200, "ok", If(status = 429, "busy", "gone"))
            Dim texts = SharePageTexts()
            texts("state") = state
            Dim page = ShareAssetText("index.html").
                Replace("__FERRUMPIX_LANG__", LocalizationService.HtmlLanguage(LocalizationService.TextCulture), StringComparison.Ordinal).
                Replace("__FERRUMPIX_VERSION__", ShareVersion(), StringComparison.Ordinal).
                Replace("__FERRUMPIX_SHARE_TEXTS__", JsonSerializer.Serialize(texts).Replace("</", "<\/", StringComparison.Ordinal), StringComparison.Ordinal)
            Dim bytes = Encoding.UTF8.GetBytes(page)
            Dim response = context.Response
            response.StatusCode = status
            response.ContentType = "text/html; charset=utf-8"
            SetSharePageHeaders(response)
            response.ContentLength64 = bytes.Length
            Await response.OutputStream.WriteAsync(bytes)
            response.Close()
        End Function

        ''' <summary>Kopfzeilen der Seite: nichts Eingebettetes, kein Rahmen, kein Verweis nach
        ''' aussen (die Adresse traegt das Token), nicht in Suchmaschinen.</summary>
        Private Shared Sub SetSharePageHeaders(response As HttpListenerResponse)
            response.Headers("Cache-Control") = "no-store"
            response.Headers("Content-Security-Policy") =
                "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; media-src 'self' blob:; " &
                "connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'"
            response.Headers("X-Content-Type-Options") = "nosniff"
            response.Headers("X-Frame-Options") = "DENY"
            response.Headers("Referrer-Policy") = "no-referrer"
            response.Headers("X-Robots-Tag") = "noindex, nofollow"
        End Sub

        ''' <summary>Die Texte der Seite in der Sprache des Browsers.</summary>
        Private Shared Function SharePageTexts() As Dictionary(Of String, String)
            Return New Dictionary(Of String, String) From {
                {"count", LocalizationService.T("{0} Bilder")},
                {"download", LocalizationService.T("Herunterladen")},
                {"zip", LocalizationService.T("Als ZIP herunterladen")},
                {"passwordTitle", LocalizationService.T("Diese Freigabe ist mit einem Kennwort geschützt.")},
                {"password", LocalizationService.T("Kennwort")},
                {"open", LocalizationService.T("Öffnen")},
                {"wrongPassword", LocalizationService.T("Das Kennwort ist falsch.")},
                {"busy", LocalizationService.T("Zu viele Anfragen. Bitte später noch einmal versuchen.")},
                {"gone", LocalizationService.T("Dieser Link ist ungültig, abgelaufen oder wurde widerrufen.")},
                {"until", LocalizationService.T("Verfügbar bis {0}")},
                {"close", LocalizationService.T("Schließen")},
                {"previous", LocalizationService.T("Vorheriges Bild")},
                {"next", LocalizationService.T("Nächstes Bild")},
                {"info", LocalizationService.T("Informationen")},
                {"date", LocalizationService.T("Aufnahmedatum")},
                {"camera", LocalizationService.T("Kamera")},
                {"lens", LocalizationService.T("Objektiv")},
                {"exposure", LocalizationService.T("Belichtung")},
                {"place", LocalizationService.T("Ort")},
                {"people", LocalizationService.T("Personen")},
                {"file", LocalizationService.T("Datei")},
                {"resolution", LocalizationService.T("Auflösung")},
                {"fileSize", LocalizationService.T("Dateigröße")},
                {"format", LocalizationService.T("Format")},
                {"loading", LocalizationService.T("Wird geladen…")},
                {"loadFailed", LocalizationService.T("Das Bild konnte nicht geladen werden")},
                {"noPhotos", LocalizationService.T("Hier gibt es keine Fotos")},
                {"video", LocalizationService.T("Video")},
                {"playMotion", LocalizationService.T("Film zu diesem Bild abspielen")},
                {"stopMotion", LocalizationService.T("Film anhalten und zum Bild zurück")},
                {"mute", LocalizationService.T("Stumm")},
                {"sharedWith", LocalizationService.T("Geteilt mit FerrumPix")}}
        End Function

        ' --- Kennwort der Freigabe ------------------------------------------------------------------

        ''' <summary>Darf diese Anfrage die Bilder sehen? Ohne Kennwort der Freigabe immer; sonst mit
        ''' einem Besuch (Cookie nach dem richtigen Kennwort), der zu dieser Freigabe und ihrem
        ''' jetzigen Kennwort gehoert und nicht abgelaufen ist.</summary>
        Private Shared Function HasShareAccess(request As HttpListenerRequest, record As ShareRecord) As Boolean
            If String.IsNullOrEmpty(record.PasswordHash) Then Return True
            Dim value = ReadHexCookie(request, ShareCookieName)
            If value Is Nothing Then Return False
            Dim key = SessionKey(value)
            Dim now = AppLockService.Clock()
            SyncLock ShareVisits
                Dim visit As ShareVisit = Nothing
                If Not ShareVisits.TryGetValue(key, visit) Then Return False
                If visit.ShareId <> record.Id OrElse visit.PasswordHash <> record.PasswordHash OrElse now - visit.LastUsedUtc > ShareVisitLifetime Then
                    ShareVisits.Remove(key)
                    Return False
                End If
                visit.LastUsedUtc = now
            End SyncLock
            Return True
        End Function

        ''' <summary>POST /s/&lt;token&gt;/unlock mit {"password"}. Dieselbe Rechnung wie das Kennwort
        ''' der Anmeldung (PBKDF2, feste Zeit) und dieselbe Bremse, gezaehlt je Freigabe und
        ''' Absenderadresse; eines zur Zeit, damit nicht viele Versuche vor dem ersten Zaehlen laufen.
        ''' Richtig: ein Cookie nur fuer den Pfad dieser Freigabe.</summary>
        Private Shared Async Function HandleShareUnlockAsync(context As HttpListenerContext, record As ShareRecord, token As String) As Task
            Dim request = context.Request
            Dim body = Await ReadMobileJsonAsync(request)
            Dim password As String = Nothing
            Dim field As JsonElement
            If body.HasValue AndAlso body.Value.TryGetProperty("password", field) AndAlso field.ValueKind = JsonValueKind.String Then password = field.GetString()
            If password Is Nothing OrElse password.Length > 1024 Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If
            If String.IsNullOrEmpty(record.PasswordHash) Then
                Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}})
                Return
            End If
            Dim counter = AppLockService.ShareCounterPrefix & "password " & record.Id & " " & SenderKey(request)
            Dim outcome = Await Task.Run(
                Function()
                    SyncLock ShareVerifyGate
                        If AppLockService.RetryAfter(counter) > TimeSpan.Zero Then Return -1
                        If AppLockService.VerifyHash(password, record.PasswordHash) Then Return 1
                        AppLockService.RegisterFailedAttempt(counter)
                        Return 0
                    End SyncLock
                End Function)
            If outcome < 0 Then
                Dim wait = AppLockService.RetryAfter(counter)
                context.Response.Headers("Retry-After") = CInt(Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
                SecurityLog.Throttled("share.password", LogAddress(request), wait, record.Id)
                Await WriteMobileJsonAsync(context, 429, MobileProblem(LocalizationService.T("Zu viele Anfragen. Bitte später noch einmal versuchen.")))
                Return
            End If
            If outcome = 0 Then
                SecurityLog.ShareWrongPassword(record.Id, LogAddress(request))
                Await WriteMobileJsonAsync(context, 401, MobileProblem(LocalizationService.T("Das Kennwort ist falsch.")))
                Return
            End If
            Dim value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            SyncLock ShareVisits
                If ShareVisits.Count >= MaxShareVisits Then
                    ShareVisits.Remove(ShareVisits.OrderBy(Function(v) v.Value.LastUsedUtc).First().Key)
                End If
                ShareVisits(SessionKey(value)) = New ShareVisit With {.ShareId = record.Id, .PasswordHash = record.PasswordHash, .LastUsedUtc = AppLockService.Clock()}
            End SyncLock
            context.Response.Headers.Add("Set-Cookie",
                $"{ShareCookieName}={value}; Max-Age={CLng(ShareVisitLifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture)}; Path={SharePrefix}{token}; HttpOnly; SameSite=Strict" &
                If(IsSecureRequest(request), "; Secure", ""))
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}})
        End Function

        ' --- Schnittstelle der Seite -------------------------------------------------------------------

        ''' <summary>GET /s/&lt;token&gt;/list: Titel, Ende, was erlaubt ist und die Bilder.</summary>
        Private Shared Async Function HandleShareListAsync(context As HttpListenerContext, record As ShareRecord) As Task
            Dim result = Await Task.Run(
                Function()
                    Dim entries = ShareEntriesOf(record)
                    Dim items As New List(Of Dictionary(Of String, Object))(entries.Count)
                    ' Das ZIP nimmt jedes Bild in der Form, in der es auch einzeln hinausgeht, ein
                    ' bearbeitetes also gerechnet (PlanShareZip); es fehlt nur, wenn eines gar nicht
                    ' hinaus darf (ein Video ohne alle Metadaten).
                    Dim zip = record.AllowDownload AndAlso entries.Count > 0
                    ' Bewegtbilder nur, wo ihr Film hinausgehen darf (HandleShareMotionAsync), und
                    ' nach dem Katalog in einer Abfrage fuer alle; ob der Film wirklich da ist,
                    ' entscheidet erst das Abspielen.
                    Dim motionMeta = LibraryService.Instance.GetMetaForPaths(
                        entries.Where(Function(e) ShareMotionAllowed(record, e.Path) AndAlso LivePhotoService.IsImageHalf(e.Path)).
                                Select(Function(e) e.Path))
                    For Each entry In entries
                        Dim item As New Dictionary(Of String, Object) From {
                            {"i", entry.Id}, {"t", entry.Taken}, {"s", entry.Stamp}, {"n", MobileDisplayName(entry.Path)}}
                        ' Billig, ohne Blick ins Rezept: ob es hinaus darf, nicht in welcher Form.
                        Dim canDownload = CanDownloadFromShare(record, entry.Path)
                        If MediaFileTypes.IsVideo(VariantKey.FileOf(entry.Path)) Then
                            item("v") = 1
                            ' Abspielen heisst das Original holen; das geht nur, wo es auch heruntergeladen werden darf.
                            If canDownload Then item("p") = 1
                        End If
                        Dim meta As LibraryImageMeta = Nothing
                        If motionMeta.TryGetValue(entry.Path, meta) AndAlso
                           (meta.MotionKind = MotionKind.MotionPhoto OrElse meta.MotionKind = MotionKind.LivePhotoImage) Then item("m") = 1
                        If canDownload Then
                            item("x") = 1
                        Else
                            zip = False
                        End If
                        items.Add(item)
                    Next
                    Dim expiry = ShareStore.ExpiryOf(record)
                    Return New Dictionary(Of String, Object) From {
                        {"ok", True},
                        {"title", record.Title},
                        {"expires", If(expiry.HasValue, expiry.Value.ToString("o", CultureInfo.InvariantCulture), "")},
                        {"download", record.AllowDownload},
                        {"zip", zip},
                        {"items", items}}
                End Function)
            Await WriteMobileJsonAsync(context, 200, result)
        End Function

        ''' <summary>Die Arbeit einer Anfrage in der Schlange der mobilen Seite: abbrechbar je
        ''' Freigabe und Absender.</summary>
        Private Shared Function ShareJobKey(record As ShareRecord, request As HttpListenerRequest) As String
            Dim id = If(request.Headers("X-FerrumPix-Request"), If(request.QueryString("r"), ""))
            If id.Length = 0 OrElse id.Length > 32 OrElse Not id.All(Function(c) Char.IsAsciiLetterOrDigit(c)) Then Return ""
            Return ShareJobScope(record, request) & "|" & id
        End Function

        Private Shared Function ShareJobScope(record As ShareRecord, request As HttpListenerRequest) As String
            Return "share " & record.Id & " " & SenderKey(request)
        End Function

        ''' <summary>POST /s/&lt;token&gt;/cancel mit {"r": [...]}: wie /api/m/cancel, nur fuer die
        ''' Arbeiten dieser Freigabe und dieses Absenders.</summary>
        Private Shared Async Function HandleShareCancelAsync(context As HttpListenerContext, record As ShareRecord) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            Dim ids As New List(Of String)()
            Dim list As JsonElement
            If body.HasValue AndAlso body.Value.TryGetProperty("r", list) AndAlso list.ValueKind = JsonValueKind.Array Then
                For Each entry In list.EnumerateArray().Take(512)
                    If entry.ValueKind = JsonValueKind.String Then ids.Add(entry.GetString())
                Next
            End If
            Dim removed = CancelMobileJobs(ShareJobScope(record, context.Request), ids)
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}, {"cancelled", removed}})
        End Function

        Private Shared Sub WriteShareNotFound(context As HttpListenerContext)
            context.Response.StatusCode = 404
            context.Response.Close()
        End Sub

        ''' <summary>GET /s/&lt;token&gt;/thumb?i=: die Kachel aus dem Vorschaubild-Speicher wie in der
        ''' mobilen Seite; fehlt sie, legt die Schlange sie an.</summary>
        Private Shared Async Function HandleShareThumbAsync(context As HttpListenerContext, record As ShareRecord) As Task
            Dim entry = Await Task.Run(Function() ResolveShareEntry(record, context.Request.QueryString("i")))
            If entry Is Nothing Then
                WriteShareNotFound(context)
                Return
            End If
            ' Datei oder Variante: Kachelspeicher und Rechnung nehmen den Schluessel (eigenes
            ' Rezept), der Stand ist der der Datei.
            Dim filePath = entry.Path
            Dim info As New FileInfo(VariantKey.FileOf(filePath))
            Dim stamp = info.LastWriteTimeUtc
            Dim size = info.Length
            Dim etag = """" & entry.Stamp & "t"""
            If IsNotModified(context.Request, etag) Then
                WriteNotModified(context, etag)
                Return
            End If
            ' Dateien, die das Rezept nennt, nur nach der Leseregel des Teilenden (RecipeReferences).
            ' Nennt es eine, die er nicht lesen darf, gilt der gemeinsame Kachelspeicher nicht:
            ' gerechnet wird ohne sie und ohne abzulegen.
            Dim reader = New MobileContext(record.OwnerUserId, "").ReferenceReader
            Dim restricted = Not RecipeReferences.AllReadable(filePath, reader)
            Dim tile = If(restricted, "", ThumbnailCacheService.FindExactCachedThumbnail(filePath, stamp, size))
            If tile.Length > 0 Then
                Dim cached As Byte() = Nothing
                Try
                    cached = Await File.ReadAllBytesAsync(tile)
                Catch ex As IOException
                End Try
                If cached IsNot Nothing Then
                    Await WriteMobileImageAsync(context, cached, "image/jpeg", etag)
                    Return
                End If
            End If
            Dim key = ShareJobKey(record, context.Request)
            Dim bytes = Await RunMobileJobAsync(key,
                Function()
                    If restricted Then Return RenderMobilePreview(filePath, 480, reader)
                    ThumbnailCacheService.EnsureCached(filePath, stamp, size)
                    Dim written = ThumbnailCacheService.FindExactCachedThumbnail(filePath, stamp, size)
                    If written.Length > 0 Then Return File.ReadAllBytes(written)
                    Return RenderMobilePreview(filePath, 480, reader)
                End Function)
            If bytes Is Nothing Then
                context.Response.StatusCode = If(WasMobileJobCancelled(key), 409, 404)
                context.Response.Close()
                Return
            End If
            Await WriteMobileImageAsync(context, bytes, "image/jpeg", etag)
        End Function

        ''' <summary>GET /s/&lt;token&gt;/preview?i=&amp;w=: das Bild in Bildschirmgroesse mit
        ''' Bearbeitung, ohne Metadaten; derselbe Weg und Zwischenspeicher wie die mobile Seite.</summary>
        Private Shared Async Function HandleSharePreviewAsync(context As HttpListenerContext, record As ShareRecord) As Task
            Dim entry = Await Task.Run(Function() ResolveShareEntry(record, context.Request.QueryString("i")))
            If entry Is Nothing Then
                WriteShareNotFound(context)
                Return
            End If
            Dim wanted = 2048L
            Dim parsed As Long
            If Long.TryParse(context.Request.QueryString("w"), NumberStyles.None, CultureInfo.InvariantCulture, parsed) Then wanted = parsed
            Dim edge = MobilePreviewEdges.FirstOrDefault(Function(e) e >= wanted)
            If edge = 0 Then edge = MobilePreviewEdges(MobilePreviewEdges.Length - 1)
            Dim cacheKey = MobilePreviewKey(entry.Path, edge)
            ' Wie die Kachel: Dateien des Rezepts nur nach der Leseregel des Teilenden; gekuerzt
            ' gerechnet steht die Vorschau unter eigenem Schluessel (RecipeReferences).
            Dim reader = New MobileContext(record.OwnerUserId, "").ReferenceReader
            If Not RecipeReferences.AllReadable(entry.Path, reader) Then cacheKey &= "|u:" & record.OwnerUserId
            Dim etag = """" & entry.Stamp & "p" & edge.ToString(CultureInfo.InvariantCulture) & """"
            If IsNotModified(context.Request, etag) Then
                WriteNotModified(context, etag)
                Return
            End If
            Dim held = TakeMobilePreview(cacheKey)
            If held Is Nothing Then
                Dim key = ShareJobKey(record, context.Request)
                Dim filePath = entry.Path
                held = Await RunMobileJobAsync(key, Function() RenderMobilePreview(filePath, edge, reader))
                If held Is Nothing Then
                    context.Response.StatusCode = If(WasMobileJobCancelled(key), 409, 404)
                    context.Response.Close()
                    Return
                End If
                KeepMobilePreview(cacheKey, held)
            End If
            Await WriteMobileImageAsync(context, held, "image/jpeg", etag)
        End Function

        ''' <summary>GET /s/&lt;token&gt;/info?i=: Name und Datum, dazu Kamera, Ort und Personen nur,
        ''' wenn die Freigabe sie mitgibt.</summary>
        Private Shared Async Function HandleShareInfoAsync(context As HttpListenerContext, record As ShareRecord) As Task
            Dim result = Await Task.Run(
                Function()
                    Dim entry = ResolveShareEntry(record, context.Request.QueryString("i"))
                    If entry Is Nothing Then Return Nothing
                    Return ShareInfoOf(record, entry)
                End Function)
            If result Is Nothing Then
                Await WriteMobileJsonAsync(context, 404, MobileProblem(LocalizationService.T("Das Bild konnte nicht geladen werden")))
                Return
            End If
            Await WriteMobileJsonAsync(context, 200, result)
        End Function

        Friend Shared Function ShareInfoOf(record As ShareRecord, entry As ShareEntry) As Dictionary(Of String, Object)
            Dim library = LibraryService.Instance
            ' Bei einer Variante: ihr Name, Aufnahmedaten und Personen der Datei.
            Dim filePath = VariantKey.FileOf(entry.Path)
            Dim meta As LibraryImageMeta = Nothing
            library.GetMetaForPaths({filePath}).TryGetValue(filePath, meta)
            Dim result As New Dictionary(Of String, Object) From {
                {"ok", True}, {"name", MobileDisplayName(entry.Path)}, {"taken", entry.Taken},
                {"video", MediaFileTypes.IsVideo(filePath)}, {"format", MobileFormatName(filePath)}}
            ' Nur Name, Format und Groesse der Datei; ihr Ordner bleibt drinnen (kein Pfad nach aussen).
            Try
                result("bytes") = New FileInfo(filePath).Length
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            If meta IsNot Nothing Then
                If meta.ImageWidth.HasValue AndAlso meta.ImageHeight.HasValue Then
                    result("width") = meta.ImageWidth.Value
                    result("height") = meta.ImageHeight.Value
                End If
                If record.ShareCamera Then
                    result("camera") = meta.Camera
                    result("lens") = meta.Lens
                    Dim exposure As New List(Of String)()
                    If meta.Aperture.HasValue AndAlso meta.Aperture.Value > 0 Then exposure.Add("f/" & meta.Aperture.Value.ToString("0.#", CultureInfo.CurrentCulture))
                    If Not String.IsNullOrWhiteSpace(meta.ShutterSpeed) Then exposure.Add(meta.ShutterSpeed.Trim() & " s")
                    If meta.Iso.HasValue AndAlso meta.Iso.Value > 0 Then exposure.Add("ISO " & meta.Iso.Value.ToString(CultureInfo.InvariantCulture))
                    If meta.FocalLengthMm.HasValue AndAlso meta.FocalLengthMm.Value > 0 Then exposure.Add(meta.FocalLengthMm.Value.ToString("0.#", CultureInfo.CurrentCulture) & " mm")
                    result("exposure") = exposure
                End If
                If record.ShareLocation Then
                    result("place") = PlacePanelService.Compose(meta.City, PlaceLookupService.LocalizedCountry(meta.CountryCode, meta.Country))
                End If
            End If
            If record.SharePeople Then
                result("people") = library.GetPeopleForImage(filePath).Where(Function(p) Not p.IsUnknownBin AndAlso p.Name.Trim().Length > 0).
                                           Select(Function(p) p.Name).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList()
            End If
            Return result
        End Function

        ''' <summary>GET /s/&lt;token&gt;/original?i=&amp;dl=1: das Bild zum Speichern (dl=1) oder ein
        ''' Video zum Abspielen. Ohne Erlaubnis 403; was hinausgeht, entscheidet
        ''' <see cref="ShareDeliveryFor"/>.</summary>
        Private Shared Async Function HandleShareOriginalAsync(context As HttpListenerContext, record As ShareRecord) As Task
            Dim entry = Await Task.Run(Function() ResolveShareEntry(record, context.Request.QueryString("i")))
            If entry Is Nothing Then
                WriteShareNotFound(context)
                Return
            End If
            ' Der Schluessel (Datei oder Variante) fuer die Rechnung, die Datei fuer alles andere. Ein
            ' unbearbeitetes RAW-Paar gibt das JPEG der Kamera (PairJpegFor).
            Dim owner As New MobileContext(record.OwnerUserId, "")
            Dim key = Await Task.Run(Function() If(PairJpegFor(entry.Path, Function(p) CanShareFile(owner, p)), entry.Path))
            Dim filePath = VariantKey.FileOf(key)
            Dim delivery = Await Task.Run(Function() ShareDeliveryFor(record, key))
            Dim attachment = context.Request.QueryString("dl") = "1"
            Select Case delivery
                Case ShareDelivery.None
                    context.Response.StatusCode = 403
                    context.Response.Close()
                Case ShareDelivery.AsIs
                    Await SendShareFileAsync(context, filePath, attachment)
                Case ShareDelivery.StrippedJpeg
                    Dim stripped = Await Task.Run(Function() StrippedJpegOf(filePath))
                    If stripped Is Nothing Then
                        Await SendRenderedJpegAsync(context, record, key)
                    Else
                        Await SendShareBytesAsync(context, stripped, "image/jpeg", IO.Path.GetFileName(filePath), attachment)
                    End If
                Case Else
                    Await SendRenderedJpegAsync(context, record, key)
            End Select
        End Function

        ''' <summary>Das JPEG ohne Metadaten, Nothing, wenn es sich nicht lesen oder nicht sauber
        ''' zerlegen laesst.</summary>
        Friend Shared Function StrippedJpegOf(filePath As String) As Byte()
            Try
                Return JpegMetadataStrip.Strip(File.ReadAllBytes(filePath))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Nothing
            End Try
        End Function

        ''' <summary>Das Bild in voller Groesse als JPEG mit Bearbeitung, ohne Metadaten; derselbe
        ''' Weg wie das volle JPEG der mobilen Seite (RenderFullMobileJpeg, eine Variante mit ihrem
        ''' Rezept), durch die Schlange und die Schleuse wie jede Rechnung der Seite.</summary>
        Private Shared Async Function SendRenderedJpegAsync(context As HttpListenerContext, record As ShareRecord, key As String) As Task
            Dim bytes = Await RunMobileJobAsync(ShareJobKey(record, context.Request), Function() RenderShareJpeg(record, key))
            If bytes Is Nothing Then
                WriteShareNotFound(context)
                Return
            End If
            Await SendShareBytesAsync(context, bytes, "image/jpeg", ShareRenderedName(key), attachment:=True)
        End Function

        ''' <summary>Rechnet das Bild einer Freigabe in voller Groesse; Dateien, die das Rezept nennt,
        ''' nur nach der Leseregel des Teilenden (RecipeReferences). Nothing, wenn es nicht gelingt.</summary>
        Private Shared Function RenderShareJpeg(record As ShareRecord, key As String) As Byte()
            Return RenderFullMobileJpegSafe(key, New MobileContext(record.OwnerUserId, "").ReferenceReader)
        End Function

        Private Shared Async Function SendShareBytesAsync(context As HttpListenerContext, bytes As Byte(), contentType As String,
                                                          fileName As String, attachment As Boolean) As Task
            Try
                Dim response = context.Response
                response.StatusCode = 200
                response.ContentType = contentType
                response.Headers("Cache-Control") = "no-store"
                response.Headers("X-Content-Type-Options") = "nosniff"
                response.Headers("Content-Disposition") = If(attachment, ContentDisposition(fileName), "inline")
                response.ContentLength64 = bytes.Length
                If Not String.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase) Then
                    Await response.OutputStream.WriteAsync(bytes)
                End If
                response.Close()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException
                Try
                    context.Response.Abort()
                Catch
                End Try
            End Try
        End Function

        ''' <summary>Die Datei, wie sie ist, mit Bereichen (ein Video springt).</summary>
        Private Shared Function SendShareFileAsync(context As HttpListenerContext, filePath As String, attachment As Boolean) As Task
            Return SendFileRangeAsync(context, filePath, MobileContentType(filePath),
                                      If(attachment, ContentDisposition(IO.Path.GetFileName(filePath)), "inline"))
        End Function

        ''' <summary>GET /s/&lt;token&gt;/motion?i=: der Film eines Motion Photos oder Live Photos
        ''' zum Abspielen, mit Bereichen. Nur mit Herunterladen und mit Ort, Personen und Kamera
        ''' (<see cref="ShareMotionAllowed"/>): der Film traegt
        ''' seine eigenen Metadaten, auch den Ort, und die lassen sich aus MP4 und MOV nicht
        ''' herausnehmen. Dieselbe Regel wie bei Videos. Sonst 403, ohne Film 404.</summary>
        Private Shared Async Function HandleShareMotionAsync(context As HttpListenerContext, record As ShareRecord) As Task
            Dim entry = Await Task.Run(Function() ResolveShareEntry(record, context.Request.QueryString("i")))
            If entry Is Nothing Then
                WriteShareNotFound(context)
                Return
            End If
            If Not ShareMotionAllowed(record, entry.Path) Then
                context.Response.StatusCode = 403
                context.Response.Close()
                Return
            End If
            Dim clip = Await Task.Run(Function() MobileMotionClipOf(entry.Path))
            If clip.Path Is Nothing Then
                WriteShareNotFound(context)
                Return
            End If
            ' Die .mov eines Live Photos gehoert zum Bild: wer das Bild teilen darf, darf auch sie
            ' zeigen, solange sie noch ausgeliefert werden duerfte.
            If Not clip.Embedded AndAlso Not CanShareFile(New MobileContext(record.OwnerUserId, ""), clip.Path) Then
                WriteShareNotFound(context)
                Return
            End If
            Await SendMotionClipAsync(context, clip)
        End Function

        ''' <summary>GET /s/&lt;token&gt;/zip: alle Bilder der Freigabe als EIN ZIP, stroemend, ohne
        ''' Beistelldateien, jedes in der Form, in der es auch einzeln hinausgeht
        ''' (<see cref="ShareDeliveryFor"/>; ein bearbeitetes gerechnet). Nur, wenn Herunterladen
        ''' erlaubt ist und jedes Bild hinaus darf; sonst 403, und die Seite bietet das ZIP gar
        ''' nicht an.</summary>
        Private Shared Async Function HandleShareZipAsync(context As HttpListenerContext, record As ShareRecord) As Task
            ' Ein ZIP liest jedes Bild einer Freigabe; mehr als eines je Freigabe und eine Handvoll
            ' insgesamt laesst die Bruecke nicht gleichzeitig laufen.
            If Not TryEnterShareZip(record.Id) Then
                context.Response.Headers("Retry-After") = "30"
                Await WriteMobileJsonAsync(context, 429, MobileProblem(LocalizationService.T("Zu viele Anfragen. Bitte später noch einmal versuchen.")))
                Return
            End If
            Try
                Await SendShareZipAsync(context, record)
            Finally
                LeaveShareZip(record.Id)
            End Try
        End Function

        ''' <summary>Hoechstens so viele ZIPs von Freigaben laufen gleichzeitig, ueber alle Freigaben.</summary>
        Friend Const MaxShareZipsTotal As Integer = 3

        ''' <summary>Laufende ZIPs je Freigabe (hoechstens eines). Die Sperre ist das Woerterbuch selbst.</summary>
        Private Shared ReadOnly RunningShareZips As New Dictionary(Of String, Integer)(StringComparer.Ordinal)

        ''' <summary>Nimmt einen Platz fuer ein ZIP dieser Freigabe; False, wenn schon eines fuer sie
        ''' laeuft oder alle Plaetze belegt sind.</summary>
        Friend Shared Function TryEnterShareZip(shareId As String) As Boolean
            SyncLock RunningShareZips
                If RunningShareZips.ContainsKey(shareId) Then Return False
                If RunningShareZips.Count >= MaxShareZipsTotal Then Return False
                RunningShareZips(shareId) = 1
                Return True
            End SyncLock
        End Function

        Friend Shared Sub LeaveShareZip(shareId As String)
            SyncLock RunningShareZips
                RunningShareZips.Remove(shareId)
            End SyncLock
        End Sub

        Private Shared Async Function SendShareZipAsync(context As HttpListenerContext, record As ShareRecord) As Task
            Dim plan = Await Task.Run(Function() PlanShareZip(record))
            If plan Is Nothing Then
                context.Response.StatusCode = 403
                context.Response.Close()
                Return
            End If
            Dim response = context.Response
            SecurityLog.ZipDownload(SecurityLog.NoValue, LogAddress(context.Request), plan.Count, record.Id)
            Try
                response.StatusCode = 200
                response.ContentType = "application/zip"
                response.Headers("Cache-Control") = "no-store"
                response.Headers("X-Content-Type-Options") = "nosniff"
                response.Headers("Content-Disposition") = ContentDisposition(ShareZipName(record))
                ' Ein gerechnetes Bild hat vorab keine Groesse: dann ohne Laenge, in Stuecken.
                If StreamingZipWriter.HasUnknownSizes(plan) Then
                    If context.Request.ProtocolVersion >= HttpVersion.Version11 Then response.SendChunked = True
                Else
                    response.ContentLength64 = StreamingZipWriter.TotalLength(plan)
                End If
                Await StreamingZipWriter.WriteAsync(response.OutputStream, plan)
                response.Close()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                       TypeOf ex Is InvalidOperationException
                Try
                    response.Abort()
                Catch
                End Try
            End Try
        End Function

        ''' <summary>Die Eintraege des ZIP einer Freigabe, Nothing, wenn es keines geben darf. Ein JPEG
        ''' ohne Metadaten entsteht beim Schreiben; seine Groesse kommt aus
        ''' <see cref="StrippedJpegSizeOf"/> (beim ersten Mal einmal gerechnet, danach gemerkt), statt
        ''' alle im Speicher zu halten. Ein gerechnetes Bild (bearbeitet, Variante, RAW, ein anderes
        ''' Format ohne Metadaten, und ein JPEG, das sich nicht sauber zerlegen laesst) entsteht
        ''' ebenfalls erst beim Schreiben, eines zur Zeit durch die Schleuse (DecodeGate); seine
        ''' Groesse steht vorher nicht fest (StreamingZipWriter.UnknownSize), das Archiv geht dann
        ''' ohne Laenge vorab hinaus. Misslingt die Rechnung, fehlt nur dieses Bild.</summary>
        Friend Shared Function PlanShareZip(record As ShareRecord) As List(Of ZipEntryPlan)
            If Not record.AllowDownload Then Return Nothing
            Dim entries = ShareEntriesOf(record)
            If entries.Count = 0 Then Return Nothing
            Dim owner As New MobileContext(record.OwnerUserId, "")
            Dim used As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim result As New List(Of ZipEntryPlan)(entries.Count)
            For Each entry In entries
                ' Ein RAW-Paar gibt EIN JPEG: unbearbeitet das der Kamera (PairJpegFor), bearbeitet
                ' das gerechnete der RAW unter dem Namen des Paares.
                Dim key = If(PairJpegFor(entry.Path, Function(p) CanShareFile(owner, p)), entry.Path)
                Dim filePath = VariantKey.FileOf(key)
                Dim delivery = ShareDeliveryFor(record, key)
                If delivery = ShareDelivery.None Then Return Nothing
                If Not MobileContext.ImageExists(key) Then Continue For
                If delivery = ShareDelivery.AsIs Then
                    result.Add(StreamingZipWriter.PlanFile(filePath, UniqueEntryName(IO.Path.GetFileName(filePath), used), allowPacking:=False))
                    Continue For
                End If
                If delivery = ShareDelivery.StrippedJpeg Then
                    Dim strippedSize = StrippedJpegSizeOf(filePath)
                    If strippedSize >= 0 Then
                        result.Add(New ZipEntryPlan With {
                            .SourcePath = filePath, .EntryName = UniqueEntryName(IO.Path.GetFileName(filePath), used), .Size = strippedSize,
                            .Modified = File.GetLastWriteTime(filePath),
                            .Producer = Function() StrippedJpegOf(filePath)})
                        Continue For
                    End If
                    ' Laesst sich das JPEG nicht von Metadaten befreien (Fuellbytes, fehlendes Ende),
                    ' geht es wie beim Einzeldownload gerechnet hinaus, statt das ganze ZIP abzulehnen.
                End If
                result.Add(New ZipEntryPlan With {
                    .SourcePath = filePath, .EntryName = UniqueEntryName(ShareRenderedName(key), used),
                    .Size = StreamingZipWriter.UnknownSize,
                    .Modified = File.GetLastWriteTime(filePath),
                    .Producer = Function() RenderShareJpeg(record, key)})
            Next
            Return If(result.Count = 0, Nothing, result)
        End Function

        ''' <summary>Gemerkte Groessen von JPEGs ohne Metadaten, je Pfad und Fingerabdruck der Datei.
        ''' Das erste ZIP einer Freigabe muss jedes JPEG einmal durchlaufen, um die Laenge vorab zu
        ''' kennen; jedes weitere ZIP derselben Dateien nicht mehr.</summary>
        Private Shared ReadOnly StrippedSizes As New Concurrent.ConcurrentDictionary(Of String, Long)(StringComparer.Ordinal)
        Private Const MaxStrippedSizes As Integer = 50_000

        ''' <summary>Wie oft eine Groesse gerechnet statt gemerkt wurde. Fuer den Pruefstand.</summary>
        Friend Shared StrippedSizeComputations As Integer

        ''' <summary>Die Laenge des JPEG ohne Metadaten, -1, wenn es sich nicht lesen laesst.</summary>
        Friend Shared Function StrippedJpegSizeOf(filePath As String) As Long
            Dim stamp = ShareStore.StampOf(filePath)
            If stamp.Length = 0 Then Return -1
            Dim key = LibraryService.PathKey(filePath) & "|" & stamp
            Dim known As Long
            If StrippedSizes.TryGetValue(key, known) Then Return known
            Threading.Interlocked.Increment(StrippedSizeComputations)
            Dim stripped = StrippedJpegOf(filePath)
            If stripped Is Nothing Then Return -1
            If StrippedSizes.Count >= MaxStrippedSizes Then StrippedSizes.Clear()
            StrippedSizes(key) = stripped.LongLength
            Return stripped.LongLength
        End Function

        ''' <summary>Der Name des ZIP: der Titel der Freigabe ohne Zeichen, die ein Dateiname nicht
        ''' traegt, sonst "FerrumPix".</summary>
        Private Shared Function ShareZipName(record As ShareRecord) As String
            Dim stem = CleanShareFileName(record.Title).Trim("."c)
            If stem.Length > 80 Then stem = stem.Substring(0, 80).Trim()
            Return If(stem.Length = 0, "FerrumPix", stem) & ".zip"
        End Function

        ' --- Anlegen und Verwalten aus der mobilen Seite ---------------------------------------------------

        ''' <summary>Schreibt der Weg unter /api/m/ (POST mit Herkunftspruefung)?</summary>
        Friend Shared Function IsMobileShareWrite(route As String) As Boolean
            Return route = "share" OrElse route = "share-edit" OrElse route = "share-revoke" OrElse
                   route = "share-link" OrElse route = "shares-pause"
        End Function

        ''' <summary>Die Wege der Freigaben unter /api/m/. False, wenn der Weg keiner von ihnen ist.
        ''' Methode und Herkunft sind schon geprueft (HandleMobileApiAsync).</summary>
        Friend Shared Async Function HandleMobileShareApiAsync(context As HttpListenerContext, route As String, mobile As MobileContext) As Task(Of Boolean)
            If Not IsMobileShareWrite(route) AndAlso route <> "shares" Then Return False
            Dim failure As Exception = Nothing
            Try
                Await HandleMobileShareRouteAsync(context, route, mobile)
            Catch ex As Exception
                failure = ex
            End Try
            ' Ein Fehler soll als Antwort mit Satz ankommen, nicht als abgebrochene Verbindung.
            If failure IsNot Nothing Then
                DiagnosticLogService.LogException("BrowserBridge.Shares", failure)
                Await WriteMobileJsonAsync(context, 500, MobileProblem(LocalizationService.T("Die Änderung konnte nicht gespeichert werden")))
            End If
            Return True
        End Function

        Private Shared Async Function HandleMobileShareRouteAsync(context As HttpListenerContext, route As String, mobile As MobileContext) As Task(Of Boolean)
            Select Case route
                Case "shares" : Await HandleMobileSharesListAsync(context, mobile)
                Case "share" : Await HandleMobileShareCreateAsync(context, mobile)
                Case "share-edit" : Await HandleMobileShareEditAsync(context, mobile)
                Case "share-revoke" : Await HandleMobileShareRevokeAsync(context, mobile)
                Case "share-link" : Await HandleMobileShareRelinkAsync(context, mobile)
                Case "shares-pause" : Await HandleMobileSharesPauseAsync(context, mobile)
                Case Else : Return False
            End Select
            Return True
        End Function

        ''' <summary>Der Anfang eines Links: die eingetragene oeffentliche Adresse, sonst die, ueber die
        ''' der Browser gerade kommt (RequestLinkBase).</summary>
        Friend Shared Function ShareLinkFor(request As HttpListenerRequest, token As String) As String
            Return RequestLinkBase(request) & SharePrefix & token
        End Function

        ''' <summary>Darf dieser Benutzer die Freigabe verwalten? Wer sie angelegt hat, und ein
        ''' Administrator jede.</summary>
        Private Shared Function CanManageShare(mobile As MobileContext, record As ShareRecord) As Boolean
            Return record IsNot Nothing AndAlso (mobile.IsAdmin OrElse String.Equals(record.OwnerUserId, mobile.UserKey, StringComparison.Ordinal))
        End Function

        ''' <summary>Eine Freigabe fuer die Verwaltung. Ohne Pfade: Name des Ordners und Anzahl genuegen.</summary>
        Friend Shared Function ShareJson(record As ShareRecord) As Dictionary(Of String, Object)
            Dim kind = record.KindValue()
            Return New Dictionary(Of String, Object) From {
                {"id", record.Id},
                {"title", record.Title},
                {"kind", kind.ToString().ToLowerInvariant()},
                {"count", If(kind = ShareKind.Folder, -1, record.Paths.Count)},
                {"folder", If(kind = ShareKind.Folder, FolderDisplayName(record.Folder), "")},
                {"sub", record.IncludeSubfolders},
                {"created", record.CreatedUtc},
                {"expires", record.ExpiresUtc},
                {"expired", ShareStore.IsExpired(record, AppLockService.Clock())},
                {"active", IsShareActive(record)},
                {"views", record.Views},
                {"password", Not String.IsNullOrEmpty(record.PasswordHash)},
                {"download", record.AllowDownload},
                {"place", record.ShareLocation},
                {"people", record.SharePeople},
                {"camera", record.ShareCamera}}
        End Function

        ''' <summary>Die Texte der Verwaltung in der mobilen Seite (shares.js).</summary>
        Private Shared Function MobileShareTexts() As Dictionary(Of String, String)
            Return New Dictionary(Of String, String) From {
                {"create", LocalizationService.T("Link zum Teilen erstellen")},
                {"sendToApp", LocalizationService.T("An App senden")},
                {"myShares", LocalizationService.T("Meine Freigaben")},
                {"title", LocalizationService.T("Titel")},
                {"expiry", LocalizationService.T("Gültig")},
                {"days", LocalizationService.T("{0} Tage")},
                {"oneYear", LocalizationService.T("1 Jahr")},
                {"never", LocalizationService.T("Unbegrenzt")},
                {"withPassword", LocalizationService.T("Mit Kennwort schützen")},
                {"password", LocalizationService.T("Kennwort")},
                {"passwordKeep", LocalizationService.T("Leer lassen, um das Kennwort zu behalten")},
                {"allowDownload", LocalizationService.T("Herunterladen erlauben")},
                {"sharePlace", LocalizationService.T("Ort mitgeben")},
                {"sharePeople", LocalizationService.T("Personen mitgeben")},
                {"shareCamera", LocalizationService.T("Kamera und Belichtung mitgeben")},
                {"subfolders", LocalizationService.T("Mit Unterordnern")},
                {"createButton", LocalizationService.T("Link erstellen")},
                {"save", LocalizationService.T("Speichern")},
                {"cancel", LocalizationService.T("Abbrechen")},
                {"copy", LocalizationService.T("Link kopieren")},
                {"copied", LocalizationService.T("Link kopiert")},
                {"share", LocalizationService.T("Teilen")},
                {"edit", LocalizationService.T("Bearbeiten")},
                {"revoke", LocalizationService.T("Widerrufen")},
                {"revokeConfirm", LocalizationService.T("Diese Freigabe widerrufen? Der Link funktioniert danach nicht mehr.")},
                {"relinkConfirm", LocalizationService.T("Der Link steht nur auf dem Gerät, auf dem er erstellt wurde. Einen neuen Link erzeugen? Der bisherige gilt dann nicht mehr.")},
                {"views", LocalizationService.T("{0} Aufrufe")},
                {"until", LocalizationService.T("Verfügbar bis {0}")},
                {"noEnd", LocalizationService.T("Unbegrenzt")},
                {"expired", LocalizationService.T("Abgelaufen")},
                {"inactive", LocalizationService.T("Ohne Wirkung")},
                {"count", LocalizationService.T("{0} Bilder")},
                {"folder", LocalizationService.T("Ordner")},
                {"none", LocalizationService.T("Noch keine Freigaben")},
                {"pauseAll", LocalizationService.T("Alle Freigaben pausieren")},
                {"paused", LocalizationService.T("Alle Freigaben sind pausiert.")},
                {"created", LocalizationService.T("Link erstellt")},
                {"saveFailed", LocalizationService.T("Die Änderung konnte nicht gespeichert werden")},
                {"loadFailed", LocalizationService.T("Fehler beim Laden")},
                {"tooShort", LocalizationService.T("Das Kennwort braucht mindestens 8 Zeichen.")}}
        End Function

        ''' <summary>GET /api/m/shares[?texts=1]: die eigenen Freigaben, ob alle pausiert sind und ob
        ''' dieser Benutzer sie pausieren darf; mit texts=1 dazu die Texte der Verwaltung.</summary>
        Private Shared Async Function HandleMobileSharesListAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim result = Await Task.Run(
                Function()
                    Return New Dictionary(Of String, Object) From {
                        {"ok", True},
                        {"admin", mobile.IsAdmin},
                        {"paused", ShareStore.Paused},
                        {"shares", ShareStore.OwnedBy(mobile.UserKey).Select(AddressOf ShareJson).ToList()}}
                End Function)
            If context.Request.QueryString("texts") = "1" Then result("texts") = MobileShareTexts()
            Await WriteMobileJsonAsync(context, 200, result)
        End Function

        Private Shared Function JsonText(body As JsonElement, name As String) As String
            Dim value As JsonElement
            If body.TryGetProperty(name, value) AndAlso value.ValueKind = JsonValueKind.String Then Return value.GetString()
            Return Nothing
        End Function

        Private Shared Function JsonFlag(body As JsonElement, name As String, fallback As Boolean) As Boolean
            Dim value As JsonElement
            If body.TryGetProperty(name, value) Then
                If value.ValueKind = JsonValueKind.True Then Return True
                If value.ValueKind = JsonValueKind.False Then Return False
            End If
            Return fallback
        End Function

        Private Shared Function JsonNumber(body As JsonElement, name As String, fallback As Integer) As Integer
            Dim value As JsonElement
            Dim number As Integer
            If body.TryGetProperty(name, value) AndAlso value.ValueKind = JsonValueKind.Number AndAlso value.TryGetInt32(number) Then Return number
            Return fallback
        End Function

        ''' <summary>Ein Titel ohne Steuerzeichen, hoechstens 120 Zeichen.</summary>
        Friend Shared Function CleanShareTitle(title As String) As String
            Dim clean = New String(If(title, "").Where(Function(c) Not Char.IsControl(c)).ToArray()).Trim()
            If clean.Length > MaxShareTitleLength Then clean = clean.Substring(0, MaxShareTitleLength).Trim()
            Return clean
        End Function

        ''' <summary>Tage bis zum Ende: eine der Stufen, sonst die Vorgabe.</summary>
        Friend Shared Function CleanShareDays(days As Integer) As Integer
            Return If(ShareDayChoices.Contains(days), days, DefaultShareDays)
        End Function

        ''' <summary>Was geteilt werden soll, aus der Anfrage aufgeloest und geprueft: die Art, die
        ''' Dateien bzw. der Ordner und ein Titel als Vorschlag. Status 0 heisst in Ordnung.</summary>
        Private Shared Function ResolveShareTarget(mobile As MobileContext, body As JsonElement) As (Status As Integer, Kind As ShareKind, Paths As List(Of String), Folder As String, Title As String)
            Dim kindText = If(JsonText(body, "kind"), "")
            Dim kind As ShareKind
            If Not [Enum].TryParse(kindText, ignoreCase:=True, result:=kind) OrElse Not [Enum].IsDefined(kind) Then Return (400, ShareKind.Selection, Nothing, "", "")
            If kind = ShareKind.Folder Then
                Dim folder = mobile.ResolveFolder(JsonText(body, "folder"))
                If folder Is Nothing Then Return (404, kind, Nothing, "", "")
                If Not CanShareFolder(mobile, folder) Then Return (403, kind, Nothing, "", "")
                Return (0, kind, New List(Of String)(), folder, FolderDisplayName(folder))
            End If
            Dim paths As New List(Of String)()
            Dim list As JsonElement
            If Not body.TryGetProperty("ids", list) OrElse list.ValueKind <> JsonValueKind.Array Then Return (400, kind, Nothing, "", "")
            ' Zu viele Bilder lehnt die ganze Freigabe ab, mit Meldung; frueher fielen die
            ' ueberzaehligen still weg.
            If list.GetArrayLength() > MaxShareListPaths Then Return (413, kind, Nothing, "", "")
            For Each entry In list.EnumerateArray()
                If entry.ValueKind <> JsonValueKind.String Then Return (400, kind, Nothing, "", "")
                ' Eine Datei oder eine ihrer zusaetzlichen Varianten: die Variante geht als eigenes
                ' Bild mit ihrem Rezept in die Freigabe (ihre Kachel steht in der Seite fuer sich).
                Dim key = mobile.ResolveImage(entry.GetString())
                If key Is Nothing OrElse Not MobileContext.ImageExists(key) Then Return (404, kind, Nothing, "", "")
                ' Mitarbeiten auf JEDEM Bild; eines ohne Recht lehnt die ganze Freigabe ab.
                If Not CanShareFile(mobile, key) Then Return (403, kind, Nothing, "", "")
                paths.Add(key)
            Next
            paths = paths.Distinct(PathIdentity.Comparer).ToList()
            If paths.Count = 0 Then Return (400, kind, Nothing, "", "")
            If kind = ShareKind.Image AndAlso paths.Count <> 1 Then kind = ShareKind.Selection
            Dim title = If(paths.Count = 1, ShareImageTitle(paths(0)),
                           String.Format(CultureInfo.CurrentCulture, LocalizationService.T("{0} Bilder"), paths.Count))
            Return (0, kind, paths, "", title)
        End Function

        ''' <summary>POST /api/m/share: legt eine Freigabe an. {"kind": "image" | "selection" |
        ''' "search" | "folder", "ids" bzw. "folder", "sub", "title", "days", "password", "download",
        ''' "place", "people", "camera"}. Antwortet mit dem Link, genau dieses eine Mal.</summary>
        Private Shared Async Function HandleMobileShareCreateAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            If Not body.HasValue Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If
            Dim target = Await Task.Run(Function() ResolveShareTarget(mobile, body.Value))
            If target.Status <> 0 Then
                Dim text = If(target.Status = 403, LocalizationService.T("Zum Teilen braucht es auf jedem Bild mindestens das Recht Mitarbeiten."),
                           If(target.Status = 413, ShareCreateProblemText(ShareStore.CreateProblem.TooManyPaths),
                              LocalizationService.T("Das Bild konnte nicht geladen werden")))
                Await WriteMobileJsonAsync(context, target.Status, MobileProblem(text))
                Return
            End If
            Dim password = If(JsonText(body.Value, "password"), "")
            If password.Length > 0 AndAlso password.Length < MinSharePasswordLength Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem(LocalizationService.T("Das Kennwort braucht mindestens 8 Zeichen.")))
                Return
            End If
            Dim title = CleanShareTitle(JsonText(body.Value, "title"))
            If title.Length = 0 Then title = CleanShareTitle(target.Title)
            Dim record As New ShareRecord With {
                .OwnerUserId = mobile.UserKey,
                .Title = title,
                .Kind = target.Kind.ToString(),
                .Folder = target.Folder,
                .IncludeSubfolders = target.Kind = ShareKind.Folder AndAlso JsonFlag(body.Value, "sub", False),
                .Paths = target.Paths,
                .ExpiresUtc = ShareStore.ExpiryText(CleanShareDays(JsonNumber(body.Value, "days", DefaultShareDays)), AppLockService.Clock()),
                .AllowDownload = JsonFlag(body.Value, "download", True),
                .ShareLocation = JsonFlag(body.Value, "place", False),
                .SharePeople = JsonFlag(body.Value, "people", False),
                .ShareCamera = JsonFlag(body.Value, "camera", True)}
            Dim token As String = Nothing
            Dim problem As ShareStore.CreateProblem
            Dim created = Await Task.Run(
                Function()
                    If password.Length > 0 Then record.PasswordHash = AppLockService.HashPassword(password)
                    Return ShareStore.Create(record, token, problem)
                End Function)
            If created Is Nothing Then
                Await WriteMobileJsonAsync(context, If(problem = ShareStore.CreateProblem.SaveFailed, 500, 409), MobileProblem(ShareCreateProblemText(problem)))
                Return
            End If
            LogShareCreated(created, SecurityLog.UserNameOf(mobile.UserKey), LogAddress(context.Request))
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {
                {"ok", True}, {"url", ShareLinkFor(context.Request, token)}, {"share", ShareJson(created)}})
        End Function

        Private Shared Async Function FindManagedShareAsync(context As HttpListenerContext, mobile As MobileContext, body As JsonElement?) As Task(Of ShareRecord)
            If Not body.HasValue Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return Nothing
            End If
            Dim record = ShareStore.Find(JsonText(body.Value, "share"))
            If Not CanManageShare(mobile, record) Then
                ' Eine fremde Freigabe antwortet wie eine, die es nicht gibt.
                Await WriteMobileJsonAsync(context, 404, MobileProblem(LocalizationService.T("Fehler beim Laden")))
                Return Nothing
            End If
            Return record
        End Function

        ''' <summary>POST /api/m/share-edit: {"share", und was sich aendert: "title", "days" (ab
        ''' jetzt), "password" (neu), "removePassword", "download", "place", "people", "camera"}.
        ''' Das Ende aendert sich NUR mit "days": die Seite schickt es nur, wenn die Laufzeit im
        ''' Formular ausdruecklich neu gewaehlt wurde. Ein Speichern ohne verlaengert also nichts,
        ''' und eine abgelaufene Freigabe bleibt abgelaufen.</summary>
        Private Shared Async Function HandleMobileShareEditAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            Dim record = Await FindManagedShareAsync(context, mobile, body)
            If record Is Nothing Then Return
            Dim values = body.Value
            Dim password = If(JsonText(values, "password"), "")
            If password.Length > 0 AndAlso password.Length < MinSharePasswordLength Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem(LocalizationService.T("Das Kennwort braucht mindestens 8 Zeichen.")))
                Return
            End If
            Dim newHash = If(password.Length > 0, Await Task.Run(Function() AppLockService.HashPassword(password)), Nothing)
            Dim removePassword = JsonFlag(values, "removePassword", False)
            Dim title = If(JsonText(values, "title") Is Nothing, Nothing, CleanShareTitle(JsonText(values, "title")))
            Dim days = JsonNumber(values, "days", Integer.MinValue)
            Dim saved = ShareStore.Update(record.Id,
                Sub(s)
                    If title IsNot Nothing AndAlso title.Length > 0 Then s.Title = title
                    If days <> Integer.MinValue Then s.ExpiresUtc = ShareStore.ExpiryText(CleanShareDays(days), AppLockService.Clock())
                    If newHash IsNot Nothing Then
                        s.PasswordHash = newHash
                    ElseIf removePassword Then
                        s.PasswordHash = ""
                    End If
                    s.AllowDownload = JsonFlag(values, "download", s.AllowDownload)
                    s.ShareLocation = JsonFlag(values, "place", s.ShareLocation)
                    s.SharePeople = JsonFlag(values, "people", s.SharePeople)
                    s.ShareCamera = JsonFlag(values, "camera", s.ShareCamera)
                End Sub)
            If Not saved Then
                Await WriteMobileJsonAsync(context, 500, MobileProblem(LocalizationService.T("Die Änderung konnte nicht gespeichert werden")))
                Return
            End If
            Dim changed = ShareStore.Find(record.Id)
            SecurityLog.ShareEvent("share.changed", SecurityLog.UserNameOf(mobile.UserKey), LogAddress(context.Request), record.Id,
                                   "password", If(newHash IsNot Nothing, "new", If(removePassword, "removed", "kept")),
                                   "expires", If(changed?.ExpiresUtc, ""), "download", If(changed IsNot Nothing AndAlso changed.AllowDownload, "yes", "no"))
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}, {"share", ShareJson(changed)}})
        End Function

        ''' <summary>POST /api/m/share-revoke: {"share"}.</summary>
        Private Shared Async Function HandleMobileShareRevokeAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            Dim record = Await FindManagedShareAsync(context, mobile, body)
            If record Is Nothing Then Return
            ShareStore.Revoke(record.Id)
            SecurityLog.ShareEvent("share.revoked", SecurityLog.UserNameOf(mobile.UserKey), LogAddress(context.Request), record.Id)
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}})
        End Function

        ''' <summary>POST /api/m/share-link: {"share"}. Ein neuer Link; der alte gilt nicht mehr.</summary>
        Private Shared Async Function HandleMobileShareRelinkAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            Dim record = Await FindManagedShareAsync(context, mobile, body)
            If record Is Nothing Then Return
            Dim token As String = Nothing
            If Not ShareStore.Relink(record.Id, token) Then
                Await WriteMobileJsonAsync(context, 500, MobileProblem(LocalizationService.T("Die Änderung konnte nicht gespeichert werden")))
                Return
            End If
            SecurityLog.ShareEvent("share.relinked", SecurityLog.UserNameOf(mobile.UserKey), LogAddress(context.Request), record.Id)
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}, {"url", ShareLinkFor(context.Request, token)}})
        End Function

        ''' <summary>POST /api/m/shares-pause: {"paused": true | false}, nur ein Administrator.</summary>
        Private Shared Async Function HandleMobileSharesPauseAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            If Not mobile.IsAdmin OrElse Not body.HasValue Then
                Await WriteMobileJsonAsync(context, 403, MobileProblem("forbidden"))
                Return
            End If
            Dim pause = JsonFlag(body.Value, "paused", False)
            Dim before = ShareStore.Paused
            ShareStore.SetPaused(pause)
            If before <> pause Then SecurityLog.SharesPaused(pause, SecurityLog.UserNameOf(mobile.UserKey), LogAddress(context.Request))
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}, {"paused", ShareStore.Paused}})
        End Function

        ' --- Skript und Stil der Verwaltung in der mobilen Seite --------------------------------------

        ''' <summary>/m/shares.js und /m/shares.css: die Verwaltung der Freigaben in der mobilen Seite,
        ''' als eigene Dateien neben app.js; ohne Daten, wie die uebrige Huelle.</summary>
        Friend Shared Async Function ServeMobileSharesAssetAsync(context As HttpListenerContext, path As String) As Task
            Dim isScript = path = MobileSharesScriptRoute
            Dim text = MobileAssetText(If(isScript, "shares.js", "shares.css"))
            Dim bytes = Encoding.UTF8.GetBytes(text)
            Dim response = context.Response
            response.StatusCode = 200
            response.ContentType = If(isScript, "text/javascript; charset=utf-8", "text/css; charset=utf-8")
            response.Headers("Cache-Control") = "public, max-age=604800"
            response.Headers("X-Content-Type-Options") = "nosniff"
            response.ContentLength64 = bytes.Length
            Await response.OutputStream.WriteAsync(bytes)
            response.Close()
        End Function

        ' --- Am Desktop -------------------------------------------------------------------------------------

        ''' <summary>Merkt sich, ueber welche Adresse die volle Oberflaeche zuletzt geladen wurde. Der
        ''' Link aus dem Kontextmenue der Galerie hat keine Anfrage, an der er sie ablesen koennte.</summary>
        Friend Shared Sub RememberShareLinkBase(request As HttpListenerRequest)
            Try
                _lastShareLinkBase = RequestLinkBase(request)
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ArgumentException
            End Try
        End Sub

        ''' <summary>Der Anfang eines Links aus der vollen Oberflaeche: die eingetragene oeffentliche
        ''' Adresse, sonst die, ueber die der Browser die Oberflaeche zuletzt geladen hat.</summary>
        Friend Shared Function DesktopShareLinkBase() As String
            Dim configured = If(AppSettingsService.Load().BrowserPublicAddress, "").Trim().TrimEnd("/"c)
            If IsUsablePublicAddress(configured) Then Return configured
            Dim remembered = _lastShareLinkBase
            Return If(String.IsNullOrEmpty(remembered), "http://127.0.0.1", remembered)
        End Function

        ''' <summary>Wer gerade an der vollen Oberflaeche sitzt: der Benutzer der Sitzung des
        ''' verbundenen Browsers, sonst der Eigentuemer.</summary>
        Friend Shared Function DesktopUserId() As String
            Dim user = SessionUser(_clientSessionKey)
            Return If(String.IsNullOrEmpty(user), AppLockService.OwnerUserId, user)
        End Function

        ''' <summary>Legt aus der Galerie eine Freigabe mit den Vorgaben an (30 Tage, ohne Kennwort,
        ''' Herunterladen erlaubt, ohne Ort und Personen, mit Kamera): fuer Bilder oder fuer EINEN
        ''' Ordner samt Unterordnern. Liefert den Link, oder ohne Link einen Satz, warum nicht. Nur
        ''' im Browser, und nur fuer jemanden an der vollen Oberflaeche (Administrator). Langsam
        ''' (Ordner, Rechte), deshalb nicht auf dem Anzeigefaden.</summary>
        Public Shared Function CreateShareFromDesktop(paths As IEnumerable(Of String), folder As String) As (Url As String, Problem As String)
            If Not IsActive Then Return (Nothing, Nothing)
            Dim user = DesktopUserId()
            Dim mobile As New MobileContext(user, "")
            Dim record As New ShareRecord With {
                .OwnerUserId = user,
                .ExpiresUtc = ShareStore.ExpiryText(DefaultShareDays, AppLockService.Clock()),
                .AllowDownload = True, .ShareLocation = False, .SharePeople = False, .ShareCamera = True}
            If Not String.IsNullOrEmpty(folder) Then
                If Not CanShareFolder(mobile, folder) Then
                    Return (Nothing, LocalizationService.T("Dieser Ordner lässt sich nicht teilen."))
                End If
                record.Kind = ShareKind.Folder.ToString()
                record.Folder = folder
                record.IncludeSubfolders = True
                record.Title = CleanShareTitle(FolderDisplayName(folder))
            Else
                ' Beistelldateien und andere Begleiter der Auswahl gehen nie mit.
                ' Eine Variante (VariantKey) geht als eigenes Bild mit ihrem Rezept mit.
                Dim files = If(paths, Enumerable.Empty(Of String)()).
                                Where(Function(p) Not String.IsNullOrEmpty(p) AndAlso MediaFileTypes.IsDisplayable(VariantKey.FileOf(p))).
                                Distinct(PathIdentity.Comparer).ToList()
                If files.Count > MaxShareListPaths Then Return (Nothing, ShareCreateProblemText(ShareStore.CreateProblem.TooManyPaths))
                If files.Count = 0 OrElse files.Any(Function(p) Not CanShareFile(mobile, p)) Then
                    Return (Nothing, LocalizationService.T("Zum Teilen braucht es auf jedem Bild mindestens das Recht Mitarbeiten."))
                End If
                record.Kind = If(files.Count = 1, ShareKind.Image, ShareKind.Selection).ToString()
                record.Paths = files
                record.Title = CleanShareTitle(If(files.Count = 1, ShareImageTitle(files(0)),
                                                  String.Format(CultureInfo.CurrentCulture, LocalizationService.T("{0} Bilder"), files.Count)))
            End If
            Dim token As String = Nothing
            Dim problem As ShareStore.CreateProblem
            Dim created = ShareStore.Create(record, token, problem)
            If created Is Nothing Then
                Return (Nothing, ShareCreateProblemText(problem))
            End If
            Dim actor = SecurityLog.DesktopActor()
            LogShareCreated(created, actor.User, actor.From)
            Return (DesktopShareLinkBase() & SharePrefix & token, Nothing)
        End Function

        ''' <summary>Eine neue Freigabe im Protokoll (SecurityLog): Art, Ende, Kennwort ja oder nein,
        ''' Herunterladen erlaubt; der Ordner relativ zur Wurzel, die Zahl der Bilder einer Auswahl.
        ''' Nie das Token.</summary>
        Private Shared Sub LogShareCreated(record As ShareRecord, user As String, from As String)
            Dim kind = If(record.Kind, "").ToLowerInvariant()
            Dim what = If(String.IsNullOrEmpty(record.Folder),
                          If(record.Paths?.Count, 0).ToString(CultureInfo.InvariantCulture),
                          SecurityLog.ShownPath(record.Folder, LogRoots()))
            SecurityLog.ShareEvent("share.created", user, from, record.Id,
                                   "kind", kind, If(String.IsNullOrEmpty(record.Folder), "images", "folder"), what,
                                   "expires", If(record.ExpiresUtc, ""), "password", If(String.IsNullOrEmpty(record.PasswordHash), "no", "yes"),
                                   "download", If(record.AllowDownload, "yes", "no"))
        End Sub

        ''' <summary>Der Satz zu einem gescheiterten Anlegen, mit der jeweiligen Grenze.</summary>
        Friend Shared Function ShareCreateProblemText(problem As ShareStore.CreateProblem) As String
            Select Case problem
                Case ShareStore.CreateProblem.UserLimit
                    Return String.Format(CultureInfo.CurrentCulture,
                        LocalizationService.T("Es gibt schon {0} Freigaben von Ihnen. Bitte zuerst eine alte widerrufen."), ShareStore.MaxSharesPerUser)
                Case ShareStore.CreateProblem.StoreFull
                    Return String.Format(CultureInfo.CurrentCulture,
                        LocalizationService.T("Es gibt schon {0} Freigaben, mehr nimmt FerrumPix nicht auf."), ShareStore.MaxShares)
                Case ShareStore.CreateProblem.TooManyPaths
                    Return String.Format(CultureInfo.CurrentCulture,
                        LocalizationService.T("Eine Freigabe nimmt höchstens {0} Bilder auf."), ShareStore.MaxPathsPerShare)
                Case Else
                    Return LocalizationService.T("Die Änderung konnte nicht gespeichert werden")
            End Select
        End Function

        ''' <summary>Legt einen Text in die Zwischenablage des verbundenen Browsers (die Seite
        ''' versucht es selbst, sonst zeigt sie ihn zum Abschreiben).</summary>
        Public Shared Sub CopyTextInBrowser(text As String)
            If Not IsActive OrElse String.IsNullOrEmpty(text) Then Return
            Dim json = JsonSerializer.Serialize(New Dictionary(Of String, String) From {{"t", "copy"}, {"text", text}})
            Dim ignored = SendTextAsync(json)
        End Sub

    End Class

End Namespace
