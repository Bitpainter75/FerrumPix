Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading.Tasks
Imports Avalonia.Threading
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Wer eine Anfrage der mobilen Seite stellt und was er darf. JEDE Anfrage unter /api/m/ holt
    ''' sich ihren Kontext an genau einer Stelle (<see cref="BrowserBridge.ResolveMobileContext"/>)
    ''' und fragt nur ihn: welche Ordner sichtbar sind, welches Bild sich ueber eine Kennung holen
    ''' laesst, wo hochgeladen, was bewertet, was heruntergeladen werden darf.
    '''
    ''' <para>BENUTZER UND RECHTE. Ein Administrator (der Eigentuemer oder ein weiterer mit dieser
    ''' Rolle) sieht alles, was die Fotoordner hergeben, und darf alles, was die Regeln der
    ''' Anwendung erlauben. Jeder andere sieht nur, wofuer seine Regeln je Ordner mindestens
    ''' "Ansehen" ergeben (UserAccountService.LevelFor: die naechstliegende Regel gewinnt, ohne Regel
    ''' kein Zugriff); bewerten, markieren und hochladen verlangen "Mitarbeiten", umbenennen und
    ''' loeschen "Verwalten". Die Regeln liest der Kontext einmal je Anfrage. Keine Abfrage geht an
    ''' ihm vorbei, auch nicht ueber eine Kennung: was ein Benutzer nicht sehen darf, antwortet wie
    ''' unbekannt.</para>
    '''
    ''' <para>SICHTBAR ist, was in den Fotoordnern liegt: mit FERRUMPIX_FOLDER_ROOTS nur darin,
    ''' sonst jede lokale Datei des Katalogs; nie versteckte Ordner, nie der Papierkorb, nie ein
    ''' Serverbild. Zum Blaettern in Ordnern gelten als Wurzeln die festen Fotoordner, ohne sie der
    ''' Benutzerordner und die ueberwachten Ordner des Katalogs; fuer einen Benutzer ohne Rolle
    ''' die Ordner seiner Regeln, die er sehen darf (<see cref="BrowseRoots"/>).</para>
    ''' </summary>
    Friend NotInheritable Class MobileContext

        ''' <summary>Die Kennung des Eigentuemers.</summary>
        Public Const OwnerUser As String = AppLockService.OwnerUserId

        ''' <summary>Wer anfragt, aus der Sitzung (BrowserBridge.SignedInUser).</summary>
        Public ReadOnly Property UserKey As String

        ''' <summary>Der Schluessel der Sitzung (Hash des Cookies), leer fuer Werkzeuge mit Basic
        ''' oder ohne Kennwort. Trennt die Abbrueche verschiedener Browser.</summary>
        Public ReadOnly Property SessionKey As String

        ''' <summary>Administrator: sieht alles, Rechte je Ordner gelten fuer ihn nicht.</summary>
        Public ReadOnly Property IsAdmin As Boolean

        ''' <summary>Die Regeln dieses Benutzers, einmal je Anfrage gelesen. Leer fuer einen
        ''' Administrator.</summary>
        Private ReadOnly _rules As List(Of (Folder As String, Level As FolderAccessLevel))

        Public Sub New(userKey As String, sessionKey As String)
            Me.UserKey = If(userKey, OwnerUser)
            Me.SessionKey = If(sessionKey, "")
            IsAdmin = UserAccountService.IsAdmin(Me.UserKey)
            _rules = If(IsAdmin, New List(Of (Folder As String, Level As FolderAccessLevel))(), UserAccountService.RulesFor(Me.UserKey))
        End Sub

        ''' <summary>Was dieser Benutzer an einem Pfad darf. Ein Administrator alles. Mit Blick auf
        ''' die Platte: fuehrt ein symbolischer Verweis im Pfad (die Datei selbst oder ein Ordner
        ''' darueber) anderswohin, gilt die kleinere Stufe von Pfad und Ziel
        ''' (<see cref="LinkedLevel"/>). Fuer Listen ueber den ganzen Katalog ohne Platte gibt es
        ''' <see cref="NamedLevel"/>.</summary>
        Public Function LevelAt(path As String) As FolderAccessLevel
            If IsAdmin Then Return FolderAccessLevel.Manage
            Return LinkedLevel(path)
        End Function

        ''' <summary>Die Stufe nach dem Pfad, wie er dasteht: reine Zeichenkettenarbeit.</summary>
        Private Function NamedLevel(path As String) As FolderAccessLevel
            If IsAdmin Then Return FolderAccessLevel.Manage
            Return UserAccountService.LevelFor(_rules, path)
        End Function

        ''' <summary>Die Ordner der Regeln mit aufgeloesten Verweisen, einmal je Anfrage.</summary>
        Private _resolvedRules As List(Of (Folder As String, Level As FolderAccessLevel))

        ''' <summary>Die Stufe eines Pfads, wenn Verweise darin aufgeloest werden.
        '''
        ''' <para>SYMBOLISCHE VERWEISE: die Regeln sind Zeichenketten. Liegt in einem freigegebenen
        ''' Ordner ein Verweis auf einen Ordner oder eine Datei, fuer die der Benutzer keine oder eine
        ''' kleinere Stufe hat, kaeme er sonst ueber den Namen im freigegebenen Ordner hinein. Deshalb
        ''' zaehlt die kleinere Stufe aus dem Pfad, wie er dasteht, und dem Ziel, verglichen mit den
        ''' ebenfalls aufgeloesten Ordnern der Regeln (liegt ein Ordner einer Regel selbst hinter
        ''' einem Verweis, aendert das nichts). Ohne Verweis im Pfad bleibt es bei der Stufe nach dem
        ''' Namen.</para></summary>
        Private Function LinkedLevel(path As String) As FolderAccessLevel
            Dim named = NamedLevel(path)
            If named = FolderAccessLevel.None OrElse String.IsNullOrWhiteSpace(path) Then Return named
            Dim resolved = ResolvedPath(path)
            If String.IsNullOrEmpty(resolved) OrElse PathIdentity.AreSame(resolved, path) Then Return named
            If _resolvedRules Is Nothing Then
                _resolvedRules = _rules.Select(Function(r) (Folder:=FileOperationPolicy.ResolveLinks(r.Folder), Level:=r.Level)).ToList()
            End If
            Dim target = UserAccountService.LevelFor(_resolvedRules, resolved)
            Return If(target < named, target, named)
        End Function

        ''' <summary>Die Ziele der Ordner, je Anfrage einmal aufgeloest: Listen ueber den Katalog
        ''' fragen viele Dateien desselben Ordners.</summary>
        Private ReadOnly _folderTargets As New Dictionary(Of String, String)(PathIdentity.Comparer)

        ''' <summary>Wohin ein Pfad wirklich zeigt: der Ordner darueber mit aufgeloesten Verweisen
        ''' (je Anfrage einmal), dazu die Datei oder der Ordner selbst, wenn er ein Verweis ist. Fuer
        ''' eine Datei ohne Verweis kostet das einen Blick auf ihren Eintrag.</summary>
        Private Function ResolvedPath(path As String) As String
            Dim full As String
            Try
                full = IO.Path.TrimEndingDirectorySeparator(IO.Path.GetFullPath(path))
            Catch
                Return path
            End Try
            Dim folder = IO.Path.GetDirectoryName(full)
            If String.IsNullOrEmpty(folder) Then Return full
            Dim folderTarget As String = Nothing
            SyncLock _folderTargets
                If Not _folderTargets.TryGetValue(folder, folderTarget) Then
                    folderTarget = FileOperationPolicy.ResolveLinks(folder)
                    _folderTargets(folder) = folderTarget
                End If
            End SyncLock
            Dim candidate = IO.Path.Combine(folderTarget, IO.Path.GetFileName(full))
            Try
                ' FileInfo fragt den Eintrag selbst (lstat), auch wenn er auf einen Ordner zeigt.
                If New FileInfo(candidate).LinkTarget IsNot Nothing Then Return FileOperationPolicy.ResolveLinks(candidate)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Return candidate
        End Function

        ''' <summary>Fuer Zwischenspeicher, die je Sichtbarkeit gelten (Zeitleiste): gleicher
        ''' Benutzer, gleiche Wurzeln und gleicher Stand der Regeln heisst gleiche Sicht.</summary>
        Public ReadOnly Property ScopeKey As String
            Get
                Return UserKey & "|" & UserAccountService.Version.ToString(CultureInfo.InvariantCulture) & "|" & String.Join("|", FolderRoots.Roots)
            End Get
        End Property

        ''' <summary>Die Ordner, von denen aus geblaettert wird. Fuer einen Benutzer ohne Rolle die
        ''' Ordner seiner Regeln mit mindestens "Ansehen", ausser denen, die er schon vom Ordner
        ''' darueber aus erreicht.</summary>
        Public Function BrowseRoots() As List(Of String)
            If Not IsAdmin Then
                Return UserAccountService.StartFolders(_rules, Function(folder) DirectoryExists(folder) AndAlso CanSeeFolder(folder))
            End If
            If FolderRoots.IsActive Then Return FolderRoots.Roots.Where(AddressOf DirectoryExists).ToList()
            ' Fuer den Administrator ohne feste Fotoordner: ZUERST die ueberwachten Ordner des
            ' Katalogs, als eigene Eintraege, auch wenn sie unter dem Home-Ordner liegen (dort
            ' liegen die Fotos meist, und tief unter Home waeren sie sonst nur ueber mehrere
            ' Ebenen erreichbar), danach Home fuer alles Uebrige.
            Dim result As New List(Of String)()
            Try
                For Each watched In CatalogIndexRunner.ConfiguredFolders()
                    If String.IsNullOrWhiteSpace(watched) OrElse Not DirectoryExists(watched) Then Continue For
                    If result.Any(Function(r) PathIdentity.IsAncestorOrSelf(r, watched)) Then Continue For
                    result.Add(watched)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Mobile.Roots", ex)
            End Try
            Dim home = FolderRoots.HomeFolder()
            If DirectoryExists(home) AndAlso Not result.Any(Function(r) PathIdentity.AreSame(r, home)) Then result.Add(home)
            Return result
        End Function

        ''' <summary>Darf dieser Benutzer die Datei in Listen sehen? Reine Zeichenkettenarbeit, ohne
        ''' Blick auf die Platte und damit ohne Verweise: der Vorfilter, der ueber jede Zeile des
        ''' Katalogs laeuft. Was hinausgeht, geht danach durch <see cref="CanSeeOnDisk"/>.</summary>
        Public Function CanSee(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) OrElse path.Contains("://", StringComparison.Ordinal) Then Return False
            If VariantKey.IsVariant(path) Then Return False
            If Not IO.Path.IsPathRooted(path) Then Return False
            If FolderRoots.IsActive AndAlso Not FolderRoots.Roots.Any(Function(r) PathIdentity.IsAncestorOrSelf(r, path)) Then Return False
            ' Papierkorb und die Systemordner von NAS und Betriebssystem ("@eaDir", "#snapshot")
            ' nie, auch nicht aus alten Katalogzeilen in der Zeitleiste.
            If FileOperationPolicy.IsInHiddenFolder(path) OrElse FileOperationPolicy.IsTrashOrSystemFolder(path) Then Return False
            Return NamedLevel(path) >= FolderAccessLevel.View
        End Function

        ''' <summary>Wie <see cref="CanSee"/>, dazu mit Blick auf die Platte: ein symbolischer
        ''' Verweis, der zu etwas fuehrt, das dieser Benutzer nicht sehen darf, faellt weg
        ''' (<see cref="LevelAt"/>). Fuer Listen und jeden Zugriff auf eine Datei; ein
        ''' Administrator bekommt dasselbe wie CanSee.</summary>
        Public Function CanSeeOnDisk(path As String) As Boolean
            Return CanSee(path) AndAlso LevelAt(path) >= FolderAccessLevel.View
        End Function

        ''' <summary>Darf dieser Benutzer in den Ordner sehen? Ein Administrator, wenn er unter einer
        ''' Wurzel liegt (<see cref="BrowseRoots"/>); jeder andere, wenn seine Regeln dort mindestens
        ''' "Ansehen" ergeben. Dazu mit aufgeloesten Verweisen in den Fotoordnern, nicht versteckt,
        ''' nicht im Papierkorb und in keinem Systemordner ("@eaDir", "#snapshot"). Ordnerliste und
        ''' Freigaben mit Unterordnern fragen hier.</summary>
        Public Function CanSeeFolder(folder As String) As Boolean
            If String.IsNullOrWhiteSpace(folder) OrElse Not IO.Path.IsPathRooted(folder) Then Return False
            If IsAdmin Then
                If Not BrowseRoots().Any(Function(r) PathIdentity.IsAncestorOrSelf(r, folder)) Then Return False
            ElseIf LevelAt(folder) < FolderAccessLevel.View Then
                Return False
            End If
            If FolderRoots.IsActive AndAlso Not FolderRoots.Allows(folder) Then Return False
            Return Not FileOperationPolicy.IsHiddenPath(folder) AndAlso Not FileOperationPolicy.IsTrashOrSystemFolder(folder)
        End Function

        ''' <summary>Darf die Datei selbst (Vorschau, Original, ZIP) ausgeliefert werden? Dieselbe
        ''' Regel wie beim Herunterladen am Desktop, dazu die Sicht dieses Benutzers ("Ansehen").</summary>
        Public Function CanDownload(path As String) As Boolean
            Return CanSeeOnDisk(path) AndAlso File.Exists(path) AndAlso FileOperationPolicy.CanDownload(path)
        End Function

        ''' <summary>Die Leseregel fuer Dateien, die ein Rezept nennt (RecipeReferences): fuer einen
        ''' Administrator Nothing (keine Grenze, wie am Desktop), sonst <see cref="CanDownload"/>.
        ''' Dieselbe Regel nimmt eine Freigabe mit dem Kontext des Teilenden.</summary>
        Public ReadOnly Property ReferenceReader As Func(Of String, Boolean)
            Get
                If IsAdmin Then Return Nothing
                Return AddressOf CanDownload
            End Get
        End Property

        ''' <summary>Darf dieser Benutzer Bewertung, Favorit, Etikett und Markierung setzen? Verlangt
        ''' "Mitarbeiten". Die Werte sind fuer alle gemeinsam.</summary>
        Public Function CanRate(path As String) As Boolean
            Return CanSee(path) AndAlso LevelAt(path) >= FolderAccessLevel.Contribute AndAlso
                   File.Exists(path) AndAlso Not OfflineVolumes.IsUnreachable(path)
        End Function

        ''' <summary>Darf dieser Benutzer in den Ordner hochladen? Verlangt "Mitarbeiten", dazu
        ''' dieselbe Regel wie beim Einfuegen in der Galerie.</summary>
        Public Function CanUpload(folder As String) As Boolean
            Return CanSeeFolder(folder) AndAlso LevelAt(folder) >= FolderAccessLevel.Contribute AndAlso
                   FileOperationPolicy.CanPasteInto(folder)
        End Function

        ''' <summary>Darf dieser Benutzer die Datei umbenennen? Verlangt "Verwalten", dazu dieselbe
        ''' Regel wie in der Galerie.</summary>
        Public Function CanRename(path As String) As Boolean
            Return CanSee(path) AndAlso LevelAt(path) >= FolderAccessLevel.Manage AndAlso
                   File.Exists(path) AndAlso FileOperationPolicy.CanRename(path)
        End Function

        ''' <summary>Darf dieser Benutzer die Datei loeschen? Verlangt "Verwalten", dazu dieselbe
        ''' Regel wie in der Galerie.</summary>
        Public Function CanDelete(path As String) As Boolean
            Return CanSee(path) AndAlso LevelAt(path) >= FolderAccessLevel.Manage AndAlso
                   File.Exists(path) AndAlso FileOperationPolicy.CanDelete(path)
        End Function

        ''' <summary>Darf dieser Benutzer die ganze Anwendung sperren? Nur ein Administrator: die
        ''' Sperre beendet alle Sitzungen, auch die der anderen.</summary>
        Public ReadOnly Property CanLockApp As Boolean
            Get
                Return IsAdmin
            End Get
        End Property

        ''' <summary>Die Datei zu einer Kennung, wenn dieser Benutzer sie sehen darf und es sie
        ''' gibt; sonst Nothing. Der einzige Weg von einer Kennung zu einem Pfad.</summary>
        Public Function ResolveFile(id As String) As String
            Dim path = BrowserBridge.MobileValueOf(BrowserBridge.MobileFileKind, id)
            If path Is Nothing OrElse Not CanSeeOnDisk(path) OrElse Not File.Exists(path) Then Return Nothing
            Return path
        End Function

        ''' <summary>Darf dieser Benutzer das Bild in Listen sehen? Wie <see cref="CanSee"/>, nimmt
        ''' aber auch den Schluessel einer zusaetzlichen Variante (VariantKey): dann gilt die Sicht
        ''' auf ihre Datei. CanSee selbst bleibt bei Dateien, denn alles, was eine Datei anfasst
        ''' (Original, Umbenennen, Loeschen, Hochladen, Teilen), geht darueber.</summary>
        Public Function CanSeeItem(key As String) As Boolean
            If String.IsNullOrWhiteSpace(key) Then Return False
            Return CanSeeOnDisk(VariantKey.FileOf(key))
        End Function

        ''' <summary>Das Bild zu einer Kennung fuer Kachel, Vorschau, Infoblatt und Markierungen:
        ''' der Pfad der Datei oder der Schluessel einer ihrer Varianten, wenn dieser Benutzer die
        ''' Datei sehen darf. Ob es die Datei noch gibt, prueft der Aufrufer
        ''' (<see cref="ImageExists"/>): eine fehlende Datei bekommt dort eine eigene Antwort.</summary>
        Public Function ResolveImage(id As String) As String
            Dim key = BrowserBridge.MobileValueOf(BrowserBridge.MobileFileKind, id)
            If key Is Nothing OrElse Not CanSeeItem(key) Then Return Nothing
            Return key
        End Function

        ''' <summary>Gibt es das Bild hinter einem Schluessel: die Datei und, bei einer Variante,
        ''' ihren Eintrag in der Begleitdatei.</summary>
        Public Shared Function ImageExists(key As String) As Boolean
            If VariantKey.IsVariant(key) Then Return RawSidecarService.VariantExists(key)
            Return Not String.IsNullOrEmpty(key) AndAlso File.Exists(key)
        End Function

        ''' <summary>Die Datei zu einer Kennung zum Herunterladen: bei einer Variante ihre Datei, die
        ''' fuer alle Varianten dieselbe ist. Nur fuer lesende Wege (Original, ZIP).</summary>
        Public Function ResolveOriginal(id As String) As String
            Dim key = ResolveImage(id)
            If key Is Nothing OrElse Not ImageExists(key) Then Return Nothing
            Return VariantKey.FileOf(key)
        End Function

        ''' <summary>Der Ordner zu einer Kennung, wenn dieser Benutzer hineinsehen darf.</summary>
        Public Function ResolveFolder(id As String) As String
            Dim folder = BrowserBridge.MobileValueOf(BrowserBridge.MobileFolderKind, id)
            If folder Is Nothing OrElse Not DirectoryExists(folder) OrElse Not CanSeeFolder(folder) Then Return Nothing
            Return folder
        End Function

        Private Shared Function DirectoryExists(folder As String) As Boolean
            Try
                Return Not String.IsNullOrEmpty(folder) AndAlso Directory.Exists(folder)
            Catch
                Return False
            End Try
        End Function
    End Class

    ''' <summary>
    ''' Die mobile Seite im Browser: eine eigene, schlanke Oberflaeche aus HTML, CSS und JavaScript
    ''' unter /m/, mit eigener JSON-Schnittstelle unter /api/m/. Kein Bild des Desktops: die Seite
    ''' fragt den Katalog und die Dateien direkt und laesst die Oberflaeche der Anwendung in Ruhe.
    '''
    ''' <para>DREI TEILDATEIEN: hier Weiche, Kontext, Kennungen, Huelle der Seite und das Setzen von
    ''' Bewertungen; <c>BrowserBridgeMobileCatalog.vb</c> die Listen (Zeitleiste, Ordner, Suche,
    ''' Personen, Orte, Infoblatt); <c>BrowserBridgeMobileImages.vb</c> Kacheln, Vorschauen, die
    ''' Warteschlange, Herunterladen und Hochladen.</para>
    '''
    ''' <para>OHNE SITZUNG gibt es nur die feste Huelle ohne Daten: Manifest, Service Worker,
    ''' Skript, Stil, Offline-Seite und Symbole. Sie liegen in jedem Paket der Anwendung und
    ''' verraten nichts aus dem Bestand; das Manifest holt der Browser ohne Cookie, und der Service
    ''' Worker muss ladbar sein, bevor eine Seite ihn anmeldet. /m/ selbst zeigt ohne Sitzung das
    ''' Anmeldefenster, /api/m/ antwortet 401. Die Pruefung davor (Einrichtung, Anmeldung) steht
    ''' in BrowserBridgeSessions.vb (HandleMobileRouteAsync); hier wird sie fuer jeden Weg noch
    ''' einmal ueber den Kontext gefragt.</para>
    '''
    ''' <para>KEINE PFADE NACH AUSSEN. Jede Datei, jeder Ordner und jeder Ort geht als Kennung hinaus
    ''' (<see cref="MobileIdFor"/>): ein HMAC mit einem Schluessel je Lauf. Eine Kennung, die der
    ''' Server nicht selbst ausgegeben hat, fuehrt nirgendwohin, und auch eine ausgegebene geht
    ''' noch einmal durch den Kontext. Pfadausbruch ist damit ausgeschlossen, nicht nur geprueft.</para>
    ''' </summary>
    Partial Public NotInheritable Class BrowserBridge

        Friend Const MobilePagePrefix As String = "/m/"
        Friend Const MobileApiPrefix As String = "/api/m/"
        Friend Const MobileManifestRoute As String = "/m/manifest.webmanifest"
        Friend Const MobileWorkerRoute As String = "/m/sw.js"
        Friend Const MobileScriptRoute As String = "/m/app.js"
        Friend Const MobileStyleRoute As String = "/m/app.css"
        Friend Const MobileOfflineRoute As String = "/m/offline.html"

        Friend Const MobileFileKind As Char = "f"c
        Friend Const MobileFolderKind As Char = "d"c
        Friend Const MobilePlaceKind As Char = "c"c

        Private Const MobileAssetFolder As String = "avares://FerrumPix/Assets/BrowserBridge/mobile/"

        ''' <summary>So viele Kennungen haelt die Bruecke; danach beginnt sie von vorn, und eine
        ''' Seite mit alten Kennungen laedt ihre Listen neu.</summary>
        Private Const MaxMobileIds As Integer = 2_000_000

        Private Const MaxMobileBodyBytes As Integer = 256 * 1024

        Private Shared ReadOnly MobileIdKey As Byte() = RandomNumberGenerator.GetBytes(32)
        Private Shared ReadOnly MobileIds As New ConcurrentDictionary(Of String, String)(StringComparer.Ordinal)
        Private Shared ReadOnly MobileAssets As New ConcurrentDictionary(Of String, String)(StringComparer.Ordinal)
        Private Shared _mobileVersion As String

        ''' <summary>Die Farbetiketten, die die Seite setzen darf: genau die neun der Galerie
        ''' (Kontextmenue, Filter, ALT+1 bis ALT+9; MenuWidgets.LabelColorValues) und leer zum
        ''' Entfernen, sonst nichts.</summary>
        Private Shared ReadOnly MobileLabels As String() = {""}.Concat(Controls.MenuWidgets.LabelColorValues).ToArray()

        ''' <summary>Die Etiketten fuer die Seite als JSON-Text [[Farbe, Name], ...], in der
        ''' Reihenfolge der Galerie und mit ihren uebersetzten Namen. Als Text im Block der Texte,
        ''' damit die Seite ihre Liste nicht selbst fuehrt.</summary>
        Private Shared Function MobileLabelPaletteJson() As String
            Return JsonSerializer.Serialize(Controls.MenuWidgets.LabelColorEntries.
                Select(Function(e) New String() {e.Hex, LocalizationService.T(e.Name)}).ToList())
        End Function

        ''' <summary>Gehoert der Pfad zur mobilen Seite oder ihrer Schnittstelle?</summary>
        Friend Shared Function IsMobileRoute(path As String) As Boolean
            If String.IsNullOrEmpty(path) Then Return False
            Return path = "/m" OrElse path = "/api/m" OrElse
                   path.StartsWith(MobilePagePrefix, StringComparison.Ordinal) OrElse
                   path.StartsWith(MobileApiPrefix, StringComparison.Ordinal)
        End Function

        ''' <summary>Der Kontext einer Anfrage: Nothing ohne gueltige Anmeldung. Die EINE Stelle,
        ''' an der eine Anfrage zu ihrem Benutzer kommt; heute immer der Eigentuemer.</summary>
        Friend Shared Function ResolveMobileContext(context As HttpListenerContext) As MobileContext
            Dim user = SignedInUser(context)
            If user Is Nothing Then Return Nothing
            Dim cookie = ReadSessionCookie(context.Request)
            Return New MobileContext(user, If(cookie Is Nothing, "", SessionKey(cookie)))
        End Function

        ' --- Kennungen -----------------------------------------------------------------------

        ''' <summary>Die Kennung fuer einen Pfad (oder einen Ort): ein Buchstabe fuer die Art und 16
        ''' Zeichen aus einem HMAC mit dem Schluessel dieses Laufs. Dieselbe Datei hat im selben Lauf
        ''' immer dieselbe Kennung, so bleiben Zwischenspeicher des Browsers gueltig.</summary>
        Friend Shared Function MobileIdFor(kind As Char, value As String) As String
            Dim key = If(kind = MobilePlaceKind, value, LibraryService.PathKey(value))
            Dim hash = HMACSHA256.HashData(MobileIdKey, Encoding.UTF8.GetBytes(kind & "|" & key))
            Dim id = kind & Convert.ToBase64String(hash, 0, 12).Replace("+"c, "-"c).Replace("/"c, "_"c)
            If MobileIds.Count >= MaxMobileIds Then MobileIds.Clear()
            MobileIds(id) = value
            Return id
        End Function

        ''' <summary>Was hinter einer ausgegebenen Kennung steht, Nothing fuer jede andere. Nur
        ''' der Kontext ruft das (MobileContext.ResolveFile, ResolveFolder) und der Weg zu den Orten.</summary>
        Friend Shared Function MobileValueOf(kind As Char, id As String) As String
            If String.IsNullOrEmpty(id) OrElse id.Length <> 17 OrElse id(0) <> kind Then Return Nothing
            Dim value As String = Nothing
            If Not MobileIds.TryGetValue(id, value) Then Return Nothing
            Return value
        End Function

        ' --- Weiche ----------------------------------------------------------------------------

        ''' <summary>Alles unter /m/ und /api/m/. Weg und Absender sind schon geprueft
        ''' (IsAcceptedRoute in HandleContextAsync).</summary>
        Friend Shared Async Function HandleMobileAsync(context As HttpListenerContext, path As String) As Task
            If path = "/m" Then
                RedirectMobile(context, "/m/")
                Return
            End If
            ' Die feste Huelle ohne Daten: ohne Sitzung abrufbar.
            Select Case path
                Case MobileManifestRoute
                    Await ServeMobileManifestAsync(context)
                    Return
                Case MobileWorkerRoute
                    Await ServeMobileShellAsync(context, "sw.js", "text/javascript; charset=utf-8", noCache:=True)
                    Return
                Case MobileScriptRoute
                    Await ServeMobileShellAsync(context, "app.js", "text/javascript; charset=utf-8", noCache:=False)
                    Return
                Case MobileStyleRoute
                    Await ServeMobileShellAsync(context, "app.css", "text/css; charset=utf-8", noCache:=False)
                    Return
                Case MobileOfflineRoute
                    Await ServeMobileOfflineAsync(context)
                    Return
                Case MobileSharesScriptRoute, MobileSharesStyleRoute
                    ' Die Verwaltung der Freigaben (BrowserBridgeShares.vb), ebenfalls ohne Daten.
                    Await ServeMobileSharesAssetAsync(context, path)
                    Return
            End Select
            If IsMobileIconRoute(path) Then
                Await ServeMobileIconAsync(context, path)
                Return
            End If

            Dim mobile = ResolveMobileContext(context)
            If path.StartsWith(MobilePagePrefix, StringComparison.Ordinal) Then
                If path <> MobilePagePrefix AndAlso path <> "/m/index.html" Then
                    context.Response.StatusCode = 404
                    context.Response.Close()
                    Return
                End If
                If mobile Is Nothing Then
                    ' Ohne Sitzung das Anmeldefenster mit Mobil vorgewaehlt (BrowserBridgeSessions.vb).
                    Await ServeLoginPageAsync(context, mobile:=True)
                    Return
                End If
                Await ServeMobilePageAsync(context)
                Return
            End If

            If mobile Is Nothing Then
                Await WriteMobileJsonAsync(context, 401, New Dictionary(Of String, Object) From {{"ok", False}})
                Return
            End If
            If path.Length <= MobileApiPrefix.Length Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            Await HandleMobileApiAsync(context, path.Substring(MobileApiPrefix.Length), mobile)
        End Function

        ''' <summary>Die Schnittstelle. Lesende Wege nur mit GET, schreibende nur mit POST und von der
        ''' eigenen Herkunft; jeder Weg bekommt den Kontext und fragt nur ihn.</summary>
        Private Shared Async Function HandleMobileApiAsync(context As HttpListenerContext, route As String, mobile As MobileContext) As Task
            ' Ab hier auf einem eigenen Faden: die Wege fragen die Platte (Datei vorhanden, Stand,
            ' Beistelldatei), auf einer Netzfreigabe auch mal laenger. Ohne das liefe dieser Teil
            ' noch in der Annahmeschleife (BrowserBridge.AcceptLoopAsync), und die naechste
            ' Anfrage, etwa eine Kachel aus dem Speicher, wartete dahinter.
            Await Task.Yield()
            Dim request = context.Request
            Dim isPost = String.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
            Dim isGet = String.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) OrElse
                        String.Equals(request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
            Dim writes = route = "mark" OrElse route = "cancel" OrElse route = "upload" OrElse route = "lock" OrElse
                         route = "delete" OrElse route = "rename" OrElse route = "language" OrElse route = "username" OrElse (route = "zip" AndAlso isPost) OrElse
                         IsMobileShareWrite(route) OrElse IsMobileTwoFactorWrite(route)
            If writes Then
                If Not isPost Then
                    context.Response.StatusCode = 405
                    context.Response.Close()
                    Return
                End If
                ' Gegen fremde Seiten: die eigene Herkunft. Dazu verlangen alle schreibenden Wege
                ' JSON oder einen eigenen Kopf, was jeder fremden Seite eine Vorabfrage aufzwingt,
                ' die hier nie beantwortet wird.
                If Not IsOwnOrigin(request) Then
                    Await WriteMobileJsonAsync(context, 403, New Dictionary(Of String, Object) From {{"ok", False}})
                    Return
                End If
            ElseIf Not isGet Then
                context.Response.StatusCode = 405
                context.Response.Close()
                Return
            End If

            Select Case route
                Case "timeline" : Await HandleMobileTimelineAsync(context, mobile)
                Case "folder" : Await HandleMobileFolderAsync(context, mobile)
                Case "search" : Await HandleMobileSearchAsync(context, mobile)
                Case "suggest" : Await HandleMobileSuggestAsync(context, mobile)
                Case "people" : Await HandleMobilePeopleAsync(context, mobile)
                Case "person" : Await HandleMobilePersonAsync(context, mobile)
                Case "places" : Await HandleMobilePlacesAsync(context, mobile)
                Case "place" : Await HandleMobilePlaceAsync(context, mobile)
                Case "info" : Await HandleMobileInfoAsync(context, mobile)
                Case "thumb" : Await HandleMobileThumbAsync(context, mobile)
                Case "preview" : Await HandleMobilePreviewAsync(context, mobile)
                Case "face" : Await HandleMobileFaceAsync(context, mobile)
                Case "original" : Await HandleMobileOriginalAsync(context, mobile)
                Case "motion" : Await HandleMobileMotionAsync(context, mobile)
                Case "jpeg" : Await HandleMobileJpegAsync(context, mobile)
                Case "zip" : Await HandleMobileZipAsync(context, mobile, isPost)
                Case "mark" : Await HandleMobileMarkAsync(context, mobile)
                Case "cancel" : Await HandleMobileCancelAsync(context, mobile)
                Case "upload" : Await HandleMobileUploadAsync(context, mobile)
                Case "lock" : Await HandleMobileLockAsync(context, mobile)
                Case "me" : Await HandleMobileMeAsync(context, mobile)
                Case "delete" : Await HandleMobileDeleteAsync(context, mobile)
                Case "rename" : Await HandleMobileRenameAsync(context, mobile)
                Case "language" : Await HandleMobileLanguageAsync(context, mobile)
                Case "username" : Await HandleMobileUserNameAsync(context, mobile)
                Case Else
                    ' Die Freigaben per Link (BrowserBridgeShares.vb).
                    If Await HandleMobileShareApiAsync(context, route, mobile) Then Return
                    ' Der zweite Faktor des eigenen Kontos (BrowserBridgeTwoFactor.vb).
                    If Await HandleMobileTwoFactorApiAsync(context, route, mobile) Then Return
                    context.Response.StatusCode = 404
                    context.Response.Close()
            End Select
        End Function

        Private Shared Sub RedirectMobile(context As HttpListenerContext, location As String)
            Dim response = context.Response
            response.StatusCode = 302
            response.Headers("Location") = location
            response.Headers("Cache-Control") = "no-store"
            response.ContentLength64 = 0
            response.Close()
        End Sub

        ' --- Die Huelle der Seite ------------------------------------------------------------

        ''' <summary>Eine Datei der Seite, einmal gelesen und gehalten.</summary>
        Private Shared Function MobileAssetText(name As String) As String
            Return MobileAssets.GetOrAdd(name, Function(n) LoadAssetText(MobileAssetFolder & n))
        End Function

        ''' <summary>Der Versionsschluessel der Huelle: aendert sich, sobald sich Seite, Skript, Stil
        ''' oder Offline-Seite aendern. Er steht in den Adressen von Skript und Stil und im Service
        ''' Worker, der damit bei einer neuen Fassung seinen Zwischenspeicher austauscht.</summary>
        Friend Shared Function MobileVersion() As String
            If _mobileVersion Is Nothing Then
                Dim all = String.Concat(MobileAssetText("index.html"), MobileAssetText("app.js"), MobileAssetText("app.css"),
                                        MobileAssetText("offline.html"), MobileAssetText("sw.js"),
                                        MobileAssetText("shares.js"), MobileAssetText("shares.css"))
                _mobileVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all))).Substring(0, 12).ToLowerInvariant()
            End If
            Return _mobileVersion
        End Function

        ''' <summary>Die Kopfzeilen, die jede Seite unter /m/ traegt. Skript und Stil nur vom eigenen
        ''' Server, nichts eingebettet: die Texte stehen als JSON in einem Block, den der Browser
        ''' nicht ausfuehrt.</summary>
        Private Shared Sub SetMobilePageHeaders(response As HttpListenerResponse)
            response.Headers("Cache-Control") = "no-store"
            response.Headers("Content-Security-Policy") =
                "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; media-src 'self' blob:; " &
                "connect-src 'self'; manifest-src 'self'; worker-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'"
            response.Headers("X-Content-Type-Options") = "nosniff"
            response.Headers("X-Frame-Options") = "DENY"
            response.Headers("Referrer-Policy") = "no-referrer"
        End Sub

        Private Shared Async Function ServeMobilePageAsync(context As HttpListenerContext) As Task
            Dim parts = Await Dispatcher.UIThread.InvokeAsync(Function() (Texts:=JsonSerializer.Serialize(MobilePageTexts()), Language:=MobileLanguage()))
            Dim page = MobileAssetText("index.html").
                Replace("__FERRUMPIX_LANG__", parts.Language, StringComparison.Ordinal).
                Replace("__FERRUMPIX_VERSION__", MobileVersion(), StringComparison.Ordinal).
                Replace("__FERRUMPIX_MOBILE_TEXTS__", parts.Texts, StringComparison.Ordinal)
            ' Mit Sitzung zaehlen die 30 Tage des Cookies wieder von hier an, wie bei der Desktop-Seite.
            RefreshSessionCookie(context)
            ' Die Sprache dieses Benutzers merkt sich der Browser fuer die naechste Anmeldeseite.
            RememberLanguage(context, SignedInUser(context))
            Dim bytes = Encoding.UTF8.GetBytes(page)
            Dim response = context.Response
            response.StatusCode = 200
            response.ContentType = "text/html; charset=utf-8"
            SetMobilePageHeaders(response)
            response.ContentLength64 = bytes.Length
            Await response.OutputStream.WriteAsync(bytes)
            response.Close()
        End Function

        ''' <summary>Skript, Stil und Service Worker. Skript und Stil tragen den Versionsschluessel in
        ''' der Adresse und duerfen lange liegen; der Service Worker wird immer neu gefragt, sonst
        ''' kaeme eine neue Fassung nie an.</summary>
        Private Shared Async Function ServeMobileShellAsync(context As HttpListenerContext, name As String, contentType As String, noCache As Boolean) As Task
            Dim text = MobileAssetText(name).Replace("__FERRUMPIX_VERSION__", MobileVersion(), StringComparison.Ordinal)
            Dim bytes = Encoding.UTF8.GetBytes(text)
            Dim response = context.Response
            response.StatusCode = 200
            response.ContentType = contentType
            response.Headers("Cache-Control") = If(noCache, "no-cache", "public, max-age=604800")
            response.Headers("X-Content-Type-Options") = "nosniff"
            response.ContentLength64 = bytes.Length
            Await response.OutputStream.WriteAsync(bytes)
            response.Close()
        End Function

        ''' <summary>Die Seite fuer den Service Worker, wenn keine Verbindung besteht. Ohne Daten, mit
        ''' Texten in der Sprache der Anwendung; ein Verweis bringt zurueck zu /m/.</summary>
        Private Shared Async Function ServeMobileOfflineAsync(context As HttpListenerContext) As Task
            Dim parts = Await Dispatcher.UIThread.InvokeAsync(
                Function() (Title:=LocalizationService.T("Keine Verbindung zu FerrumPix"),
                            Retry:=LocalizationService.T("Erneut versuchen"), Language:=MobileLanguage()))
            Dim page = MobileAssetText("offline.html").
                Replace("__FERRUMPIX_LANG__", parts.Language, StringComparison.Ordinal).
                Replace("__FERRUMPIX_VERSION__", MobileVersion(), StringComparison.Ordinal).
                Replace("__FERRUMPIX_OFFLINE_TITLE__", WebUtility.HtmlEncode(parts.Title), StringComparison.Ordinal).
                Replace("__FERRUMPIX_OFFLINE_RETRY__", WebUtility.HtmlEncode(parts.Retry), StringComparison.Ordinal)
            Dim bytes = Encoding.UTF8.GetBytes(page)
            Dim response = context.Response
            response.StatusCode = 200
            response.ContentType = "text/html; charset=utf-8"
            SetMobilePageHeaders(response)
            response.ContentLength64 = bytes.Length
            Await response.OutputStream.WriteAsync(bytes)
            response.Close()
        End Function

        ''' <summary>Das Manifest der Web-App: Start und Geltungsbereich /m/, eigenstaendiges Fenster,
        ''' Farben des dunklen Erscheinungsbilds, Symbole in 192 und 512 Punkten, auch maskierbar.</summary>
        Friend Shared Function MobileManifestJson() As String
            Dim icons As New List(Of Dictionary(Of String, String)) From {
                New Dictionary(Of String, String) From {{"src", "/m/icon-192.png"}, {"sizes", "192x192"}, {"type", "image/png"}, {"purpose", "any"}},
                New Dictionary(Of String, String) From {{"src", "/m/icon-512.png"}, {"sizes", "512x512"}, {"type", "image/png"}, {"purpose", "any"}},
                New Dictionary(Of String, String) From {{"src", "/m/icon-maskable-192.png"}, {"sizes", "192x192"}, {"type", "image/png"}, {"purpose", "maskable"}},
                New Dictionary(Of String, String) From {{"src", "/m/icon-maskable-512.png"}, {"sizes", "512x512"}, {"type", "image/png"}, {"purpose", "maskable"}}}
            Dim manifest As New Dictionary(Of String, Object) From {
                {"id", "/m/"},
                {"name", "FerrumPix"},
                {"short_name", "FerrumPix"},
                {"start_url", "/m/"},
                {"scope", "/m/"},
                {"display", "standalone"},
                {"orientation", "any"},
                {"background_color", "#0B0E11"},
                {"theme_color", "#0B0E11"},
                {"icons", icons}}
            Return JsonSerializer.Serialize(manifest)
        End Function

        Private Shared Async Function ServeMobileManifestAsync(context As HttpListenerContext) As Task
            Dim bytes = Encoding.UTF8.GetBytes(MobileManifestJson())
            Dim response = context.Response
            response.StatusCode = 200
            response.ContentType = "application/manifest+json; charset=utf-8"
            response.Headers("Cache-Control") = "no-cache"
            response.Headers("X-Content-Type-Options") = "nosniff"
            response.ContentLength64 = bytes.Length
            Await response.OutputStream.WriteAsync(bytes)
            response.Close()
        End Function

        Private Shared Function IsMobileIconRoute(path As String) As Boolean
            Return path = "/m/icon-192.png" OrElse path = "/m/icon-512.png" OrElse
                   path = "/m/icon-maskable-192.png" OrElse path = "/m/icon-maskable-512.png"
        End Function

        ''' <summary>Die Symbole der Web-App, aus dem Programmsymbol gerechnet wie die Symbole der
        ''' Desktop-Seite (BrowserBridgeIcons.vb). Die maskierbaren tragen das Symbol auf dem
        ''' Hintergrund der Anwendung in der sicheren Zone (80 Prozent): ein Telefon schneidet sie
        ''' rund oder eckig zu, ohne vom Symbol etwas abzuschneiden.</summary>
        Private Shared Async Function ServeMobileIconAsync(context As HttpListenerContext, path As String) As Task
            Dim bytes = IconCache.GetOrAdd(path, AddressOf BuildMobileIcon)
            Dim response = context.Response
            response.StatusCode = 200
            response.ContentType = "image/png"
            response.Headers("Cache-Control") = "public, max-age=86400"
            response.Headers("X-Content-Type-Options") = "nosniff"
            response.ContentLength64 = bytes.Length
            Await response.OutputStream.WriteAsync(bytes)
            response.Close()
        End Function

        Private Shared Function BuildMobileIcon(path As String) As Byte()
            Dim size = If(path.Contains("512", StringComparison.Ordinal), 512, 192)
            Dim maskable = path.Contains("maskable", StringComparison.Ordinal)
            Using source = SKBitmap.Decode(ReadAsset(AppIconPngAsset))
                Using target As New SKBitmap(New SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul))
                    Using canvas As New SKCanvas(target)
                        canvas.Clear(If(maskable, SKColor.Parse("#0B0E11"), SKColors.Transparent))
                        Dim edge = If(maskable, size * 0.72F, CSng(size))
                        Dim offset = (size - edge) / 2.0F
                        Using image = SKImage.FromBitmap(source)
                            canvas.DrawImage(image, SKRect.Create(offset, offset, edge, edge),
                                             New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                        End Using
                    End Using
                    Return PngEncoder.EncodeToBytes(target, PngPurpose.Export)
                End Using
            End Using
        End Function

        ''' <summary>Die Sprache der Seite: die dieser Anfrage (Benutzer bzw. Browser), sonst die der
        ''' Anwendung.</summary>
        Private Shared Function MobileLanguage() As String
            Return LocalizationService.HtmlLanguage(LocalizationService.TextCulture)
        End Function

        ''' <summary>Die Texte der mobilen Seite in der Sprache der Anwendung. Hier und nicht in der
        ''' Seite, damit sie durch dieselbe Uebersetzung und denselben Sammler gehen wie jeder andere
        ''' Text. Monats- und Tagesnamen rechnet die Seite selbst in der Sprache des Dokuments.</summary>
        Private Shared Function MobilePageTexts() As Dictionary(Of String, String)
            Return New Dictionary(Of String, String) From {
                {"photos", LocalizationService.T("Fotos")},
                {"folders", LocalizationService.T("Ordner")},
                {"search", LocalizationService.T("Suche")},
                {"more", LocalizationService.T("Mehr")},
                {"people", LocalizationService.T("Personen")},
                {"places", LocalizationService.T("Orte")},
                {"noMatches", LocalizationService.T("Keine Treffer")},
                {"noPhotos", LocalizationService.T("Hier gibt es keine Fotos")},
                {"emptyFolder", LocalizationService.T("In diesem Ordner liegen keine Fotos")},
                {"searchHint", LocalizationService.T("Suche")},
                {"searchSyntax", LocalizationService.T("Mehrere Wörter: eines davon genügt. +Wort muss vorkommen, -Wort darf nicht vorkommen.")},
                {"upload", LocalizationService.T("Hochladen")},
                {"uploadHere", LocalizationService.T("Hierher hochladen")},
                {"uploading", LocalizationService.T("Wird hochgeladen")},
                {"progress", LocalizationService.T("{0} von {1} Dateien hochgeladen")},
                {"uploaded", LocalizationService.T("Hochgeladen")},
                {"failed", LocalizationService.T("Fehlgeschlagen")},
                {"cancelled", LocalizationService.T("Abgebrochen")},
                {"cancel", LocalizationService.T("Abbrechen")},
                {"close", LocalizationService.T("Schließen")},
                {"back", LocalizationService.T("Zurück")},
                {"download", LocalizationService.T("Herunterladen")},
                {"original", LocalizationService.T("Original")},
                {"edited", LocalizationService.T("Als JPEG mit Bearbeitung")},
                {"zip", LocalizationService.T("Als ZIP herunterladen")},
                {"share", LocalizationService.T("Teilen")},
                {"select", LocalizationService.T("Auswählen")},
                {"selected", LocalizationService.T("{0} ausgewählt")},
                {"clearSelection", LocalizationService.T("Auswahl aufheben")},
                {"deselectAll", LocalizationService.T("Alle abwählen")},
                {"selectDay", LocalizationService.T("Ganzen Tag auswählen")},
                {"selectionLimit", MobileSelectionLimitText()},
                {"maxSelection", MaxMobileSelection.ToString(CultureInfo.InvariantCulture)},
                {"changedMany", LocalizationService.T("{0} Bilder geändert")},
                {"deleteManyConfirm", LocalizationService.T("{0} Bilder löschen?")},
                {"deleteManyForeverConfirm", LocalizationService.T("{0} Bilder endgültig löschen? Der Papierkorb ist abgeschaltet, das lässt sich nicht rückgängig machen.")},
                {"preparing", LocalizationService.T("Bilder werden vorbereitet")},
                {"sendToApp", LocalizationService.T("An App senden")},
                {"noRating", LocalizationService.T("Keine Bewertung")},
                {"info", LocalizationService.T("Informationen")},
                {"date", LocalizationService.T("Aufnahmedatum")},
                {"camera", LocalizationService.T("Kamera")},
                {"lens", LocalizationService.T("Objektiv")},
                {"exposure", LocalizationService.T("Belichtung")},
                {"place", LocalizationService.T("Ort")},
                {"keywords", LocalizationService.T("Stichwörter")},
                {"file", LocalizationService.T("Datei")},
                {"size", LocalizationService.T("Größe")},
                {"resolution", LocalizationService.T("Auflösung")},
                {"fileSize", LocalizationService.T("Dateigröße")},
                {"format", LocalizationService.T("Format")},
                {"missing", LocalizationService.T("Datei fehlt")},
                {"unreadable", LocalizationService.T("Format nicht lesbar")},
                {"rating", LocalizationService.T("Bewertung")},
                {"favorite", LocalizationService.T("Favorit")},
                {"label", LocalizationService.T("Farbetikett")},
                {"noLabel", LocalizationService.T("Kein Etikett")},
                {"labels", MobileLabelPaletteJson()},
                {"loading", LocalizationService.T("Wird geladen…")},
                {"loadError", LocalizationService.T("Fehler beim Laden")},
                {"previous", LocalizationService.T("Vorheriges Bild")},
                {"next", LocalizationService.T("Nächstes Bild")},
                {"keep", LocalizationService.T("Behalten")},
                {"reject", LocalizationService.T("Verwerfen")},
                {"video", LocalizationService.T("Video")},
                {"motion", LocalizationService.T("Bewegtbild")},
                {"motionPhoto", LocalizationService.T("Motion Photo, Film in der Datei")},
                {"livePhoto", LocalizationService.T("Live Photo, Bild")},
                {"playMotion", LocalizationService.T("Film zu diesem Bild abspielen")},
                {"stopMotion", LocalizationService.T("Film anhalten und zum Bild zurück")},
                {"motionFailed", LocalizationService.T("Der Film zu diesem Bild ließ sich nicht abspielen")},
                {"mute", LocalizationService.T("Stumm")},
                {"noDate", LocalizationService.T("Ohne Datum")},
                {"desktop", LocalizationService.T("Zur Desktop-Ansicht")},
                {"signOut", LocalizationService.T("Abmelden")},
                {"lock", LocalizationService.T("Sperren")},
                {"retry", LocalizationService.T("Erneut versuchen")},
                {"loadFailed", LocalizationService.T("Das Bild konnte nicht geladen werden")},
                {"saveFailed", LocalizationService.T("Die Änderung konnte nicht gespeichert werden")},
                {"offline", LocalizationService.T("Keine Verbindung zu FerrumPix")},
                {"manage", LocalizationService.T("Verwalten")},
                {"language", LocalizationService.T("Sprache")},
                {"userName", LocalizationService.T("Benutzername")},
                {"saved", LocalizationService.T("Gespeichert.")},
                {"newUserName", LocalizationService.T("Neuen Benutzernamen eingeben. Danach gilt bei der Anmeldung nur noch er.")},
                {"rename", LocalizationService.T("Umbenennen")},
                {"newName", LocalizationService.T("Neuen Namen eingeben")},
                {"delete", LocalizationService.T("Löschen")},
                {"deleteConfirm", LocalizationService.T("{0} löschen?")},
                {"deleteForeverConfirm", LocalizationService.T("{0} endgültig löschen? Der Papierkorb ist abgeschaltet, das lässt sich nicht rückgängig machen.")},
                {"tooLarge", MaxUploadText()},
                {"maxBytes", MaxUploadBytes.ToString(CultureInfo.InvariantCulture)}}
        End Function

        ' --- Antworten -------------------------------------------------------------------------

        ''' <summary>Eine JSON-Antwort der Schnittstelle. Nie zwischengespeichert: sie haengt an der
        ''' Sitzung und am Stand des Katalogs.</summary>
        Friend Shared Async Function WriteMobileJsonAsync(context As HttpListenerContext, status As Integer, value As Object) As Task
            Try
                Dim bytes = JsonSerializer.SerializeToUtf8Bytes(value)
                Dim response = context.Response
                response.StatusCode = status
                response.ContentType = "application/json; charset=utf-8"
                response.Headers("Cache-Control") = "no-store"
                response.Headers("X-Content-Type-Options") = "nosniff"
                response.ContentLength64 = bytes.Length
                If Not String.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase) Then
                    Await response.OutputStream.WriteAsync(bytes)
                End If
                response.Close()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                ' Der Browser hat die Anfrage schon aufgegeben.
                Try
                    context.Response.Abort()
                Catch
                End Try
            End Try
        End Function

        Private Shared Function MobileProblem(text As String) As Dictionary(Of String, Object)
            Return New Dictionary(Of String, Object) From {{"ok", False}, {"error", text}}
        End Function

        ''' <summary>Liest den Rumpf einer Anfrage als JSON. Nothing, wenn er fehlt, zu gross ist,
        ''' kein JSON ist oder nicht als JSON angekuendigt wurde.</summary>
        Private Shared Async Function ReadMobileJsonAsync(request As HttpListenerRequest) As Task(Of JsonElement?)
            Dim contentType = If(request.ContentType, "")
            If Not contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            If request.ContentLength64 <= 0 OrElse request.ContentLength64 > MaxMobileBodyBytes Then Return Nothing
            Try
                Dim buffer(CInt(request.ContentLength64) - 1) As Byte
                Dim read = 0
                While read < buffer.Length
                    Dim count = Await request.InputStream.ReadAsync(buffer, read, buffer.Length - read)
                    If count <= 0 Then Exit While
                    read += count
                End While
                Using document = JsonDocument.Parse(New ReadOnlyMemory(Of Byte)(buffer, 0, read))
                    If document.RootElement.ValueKind <> JsonValueKind.Object Then Return Nothing
                    Return document.RootElement.Clone()
                End Using
            Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is HttpListenerException
                Return Nothing
            End Try
        End Function

        ' --- Bewerten und Markieren -------------------------------------------------------------

        ''' <summary>POST /api/m/mark mit {"id", und einem von "rating" (0 bis 5), "favorite",
        ''' "label" (Farbe aus der Galerie oder leer), "pick" (-1, 0, 1)}. Geschrieben wird ueber
        ''' dieselben Wege wie in der Galerie: Katalog, .fpxmp und, wie dort, mit dem Abgleich in die
        ''' XMP-Beistelldatei. Die Galerie hoert auf LibraryService.AttributesChanged und zeigt die
        ''' Aenderung an ihren Kacheln. Eine RAW nimmt ihr JPEG daneben mit, wie die Galerie ihre
        ''' verborgenen Partner (siehe <see cref="MobilePartnersOf"/>). Mit "ids" statt "id" gilt
        ''' die Aenderung fuer eine Auswahl (<see cref="HandleMobileMarkManyAsync"/>).</summary>
        Private Shared Async Function HandleMobileMarkAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            If Not body.HasValue Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If
            Dim message = body.Value
            Dim list As JsonElement
            If message.TryGetProperty("ids", list) Then
                Await HandleMobileMarkManyAsync(context, mobile, list, ReadMobileMarkChange(message))
                Return
            End If
            ' Datei oder Variante: eine Variante traegt eigene Katalogdaten (unter ihrem Schluessel),
            ' wie ihre Kachel in der Galerie, und hat keine Partner.
            Dim path = mobile.ResolveImage(ReadString(message, "id"))
            If path Is Nothing OrElse Not MobileContext.ImageExists(path) Then
                Await WriteMobileJsonAsync(context, 404, MobileProblem(LocalizationService.T("Das Bild konnte nicht geladen werden")))
                Return
            End If
            ' Ein RAW-Paar ist eine Kachel: bewertet werden beide Dateien, also muessen beide erlaubt
            ' sein. Ein Partner, den dieser Benutzer gar nicht sieht, zaehlt nicht mit.
            Dim partners = If(VariantKey.IsVariant(path), New List(Of String)(), MobilePartnersOf(path).Where(Function(p) mobile.CanSeeOnDisk(p)).ToList())
            If Not mobile.CanRate(VariantKey.FileOf(path)) OrElse partners.Any(Function(p) Not mobile.CanRate(p)) Then
                Await WriteMobileJsonAsync(context, 403, MobileProblem(LocalizationService.T("Die Änderung konnte nicht gespeichert werden")))
                Return
            End If

            Dim change = ReadMobileMarkChange(message)
            If change.IsEmpty Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If

            Dim written = True
            Try
                Await Task.Run(
                    Sub()
                        Dim targets = New List(Of String) From {path}
                        targets.AddRange(partners)
                        ApplyMobileMarkChange(targets, change)
                    End Sub)
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Mobile.Mark", ex)
                written = False
            End Try
            If Not written Then
                Await WriteMobileJsonAsync(context, 500, MobileProblem(LocalizationService.T("Die Änderung konnte nicht gespeichert werden")))
                Return
            End If
            Await WriteMobileJsonAsync(context, 200, MobileMarksOf(path))
        End Function

        ''' <summary>So viele Bilder nimmt ein Aufruf fuer eine Auswahl der mobilen Seite hoechstens
        ''' (Markieren, Loeschen); dieselbe Grenze wie fuer eine Freigabe, damit eine Auswahl, die
        ''' sich bewerten laesst, auch teilen laesst. Die Seite haelt ihre Auswahl darunter.</summary>
        Friend Const MaxMobileSelection As Integer = ShareStore.MaxPathsPerShare

        ''' <summary>Was eine Markierung aendern soll; was fehlt oder nicht taugt, bleibt leer.</summary>
        Private NotInheritable Class MobileMarkChange
            Public Rating As Integer?
            Public Favorite As Boolean?
            Public Label As String
            Public Pick As Integer?

            Public ReadOnly Property IsEmpty As Boolean
                Get
                    Return Not Rating.HasValue AndAlso Not Favorite.HasValue AndAlso Label Is Nothing AndAlso Not Pick.HasValue
                End Get
            End Property
        End Class

        ''' <summary>Liest "rating" (0 bis 5), "favorite", "label" (eines der neun Etiketten der
        ''' Galerie oder leer) und "pick" (-1, 0, 1) aus einer Anfrage.</summary>
        Private Shared Function ReadMobileMarkChange(message As JsonElement) As MobileMarkChange
            Dim change As New MobileMarkChange()
            Dim value As JsonElement
            Dim number As Integer
            If message.TryGetProperty("rating", value) AndAlso value.ValueKind = JsonValueKind.Number AndAlso
               value.TryGetInt32(number) AndAlso number >= 0 AndAlso number <= 5 Then change.Rating = number
            If message.TryGetProperty("favorite", value) AndAlso
               (value.ValueKind = JsonValueKind.True OrElse value.ValueKind = JsonValueKind.False) Then change.Favorite = value.GetBoolean()
            If message.TryGetProperty("label", value) AndAlso value.ValueKind = JsonValueKind.String Then
                Dim given = value.GetString().Trim().ToUpperInvariant()
                If MobileLabels.Contains(given) Then change.Label = given
            End If
            If message.TryGetProperty("pick", value) AndAlso value.ValueKind = JsonValueKind.Number AndAlso
               value.TryGetInt32(number) AndAlso number >= -1 AndAlso number <= 1 Then change.Pick = number
            Return change
        End Function

        ''' <summary>Schreibt eine Markierung ueber die Wege der Galerie fuer viele Bilder: je Art EIN
        ''' Durchgang ueber alle Ziele, mit dem Abgleich in .fpxmp und XMP-Beistelldatei.</summary>
        Private Shared Sub ApplyMobileMarkChange(targets As List(Of String), change As MobileMarkChange)
            Dim library = LibraryService.Instance
            If change.Rating.HasValue Then library.SetRatingForMany(targets, change.Rating.Value, syncToXmp:=True)
            If change.Favorite.HasValue Then
                For Each target In targets
                    library.SetFavorite(target, change.Favorite.Value)
                Next
            End If
            If change.Label IsNot Nothing Then library.SetColorLabelForMany(targets, change.Label, syncToXmp:=True)
            If change.Pick.HasValue Then library.SetPickStateForMany(targets, change.Pick.Value, syncToXmp:=True)
        End Sub

        ''' <summary>POST /api/m/mark mit {"ids": [...], und den Feldern wie fuer ein Bild}: dieselbe
        ''' Aenderung fuer eine Auswahl der mobilen Seite, hoechstens <see cref="MaxMobileSelection"/>
        ''' Kennungen (mehr: 413). Jedes Bild geht fuer sich durch den Kontext, samt den Partnern
        ''' seines RAW-Paars und als Variante wie im Vollbild; geaendert wird, was der Benutzer darf,
        ''' die uebrigen zaehlt die Antwort ("3 von 10 ohne Recht"), statt die ganze Auswahl
        ''' abzulehnen. Geschrieben wird in einem Durchgang ueber die Wege der Galerie fuer viele
        ''' Bilder. Antwort: changed, refused (ohne Recht), missing (nicht mehr vorhanden), total, die
        ''' Kennungen der geaenderten Bilder (done) und der Satz dazu (message). Durfte keines geaendert
        ''' werden: 403 bzw. 404, wenn keines mehr da war.</summary>
        Private Shared Async Function HandleMobileMarkManyAsync(context As HttpListenerContext, mobile As MobileContext,
                                                                list As JsonElement, change As MobileMarkChange) As Task
            If list.ValueKind <> JsonValueKind.Array OrElse change.IsEmpty Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If
            Dim ids = list.EnumerateArray().Where(Function(e) e.ValueKind = JsonValueKind.String).
                           Select(Function(e) e.GetString()).Distinct(StringComparer.Ordinal).ToList()
            If ids.Count = 0 Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If
            If ids.Count > MaxMobileSelection Then
                Await WriteMobileJsonAsync(context, 413, MobileProblem(MobileSelectionLimitText()))
                Return
            End If
            Dim outcome = Await Task.Run(Function() SortMobileSelection(mobile, ids, True, AddressOf mobile.CanRate))
            If outcome.Done.Count = 0 Then
                Dim status = If(outcome.Refused > 0, 403, 404)
                Await WriteMobileJsonAsync(context, status, MobileProblem(MobileSelectionProblemText(outcome.Refused, outcome.Missing, ids.Count)))
                Return
            End If
            Dim written = True
            Try
                Await Task.Run(Sub() ApplyMobileMarkChange(outcome.Files, change))
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Mobile.MarkMany", ex)
                written = False
            End Try
            If Not written Then
                Await WriteMobileJsonAsync(context, 500, MobileProblem(LocalizationService.T("Die Änderung konnte nicht gespeichert werden")))
                Return
            End If
            Await WriteMobileJsonAsync(context, 200, MobileSelectionAnswer(outcome, ids.Count))
        End Function

        ''' <summary>Eine Auswahl der Seite, nach Bildern sortiert: welche Kennungen der Benutzer mit
        ''' allen ihren Dateien aendern darf (Done, dazu ihre Dateien samt Partnern in Files), wie viele
        ''' er nicht darf (Refused) und wie viele es nicht mehr gibt (Missing).</summary>
        Private NotInheritable Class MobileSelectionOutcome
            Public ReadOnly Done As New List(Of String)()
            Public ReadOnly Files As New List(Of String)()
            Public Refused As Integer
            Public Missing As Integer
        End Class

        ''' <summary>Geht jede Kennung einzeln durch den Kontext: eine unbekannte, fremde oder
        ''' verschwundene zaehlt als nicht mehr vorhanden; ein Bild, bei dem <paramref name="allowed"/>
        ''' fuer die Datei oder einen sichtbaren Partner ihres RAW-Paars nein sagt, als ohne Recht.
        ''' Eine Variante nur mit <paramref name="allowVariants"/> (Markieren), dann ohne Partner und
        ''' mit dem Recht an ihrer Datei; sonst (Loeschen) zaehlt sie als ohne Recht, wie
        ''' ResolveFile sie fuer jeden Weg ablehnt, der eine Datei veraendert.</summary>
        Private Shared Function SortMobileSelection(mobile As MobileContext, ids As List(Of String), allowVariants As Boolean,
                                                    allowed As Func(Of String, Boolean)) As MobileSelectionOutcome
            Dim outcome As New MobileSelectionOutcome()
            Dim seen As New HashSet(Of String)(PathIdentity.Comparer)
            For Each id In ids
                Dim key = mobile.ResolveImage(id)
                If key Is Nothing OrElse Not MobileContext.ImageExists(key) Then
                    outcome.Missing += 1
                    Continue For
                End If
                Dim isVariant = VariantKey.IsVariant(key)
                If isVariant AndAlso Not allowVariants Then
                    outcome.Refused += 1
                    Continue For
                End If
                Dim partners = If(isVariant, New List(Of String)(), MobilePartnersOf(key).Where(Function(p) mobile.CanSeeOnDisk(p)).ToList())
                If Not allowed(VariantKey.FileOf(key)) OrElse partners.Any(Function(p) Not allowed(p)) Then
                    outcome.Refused += 1
                    Continue For
                End If
                outcome.Done.Add(id)
                If seen.Add(key) Then outcome.Files.Add(key)
                For Each partner In partners
                    If seen.Add(partner) Then outcome.Files.Add(partner)
                Next
            Next
            Return outcome
        End Function

        ''' <summary>Die Antwort auf eine Aenderung an einer Auswahl, mit dem Satz ueber das, was
        ''' nicht geaendert wurde (leer, wenn alles ging).</summary>
        Private Shared Function MobileSelectionAnswer(outcome As MobileSelectionOutcome, total As Integer) As Dictionary(Of String, Object)
            Return New Dictionary(Of String, Object) From {
                {"ok", True}, {"changed", outcome.Done.Count}, {"refused", outcome.Refused}, {"missing", outcome.Missing},
                {"total", total}, {"done", outcome.Done}, {"message", MobileSelectionProblemText(outcome.Refused, outcome.Missing, total)}}
        End Function

        ''' <summary>"3 von 10 ohne Recht", "1 von 10 nicht mehr vorhanden" oder beides; leer ohne beides.</summary>
        Friend Shared Function MobileSelectionProblemText(refused As Integer, missing As Integer, total As Integer) As String
            Dim parts As New List(Of String)()
            If refused > 0 Then parts.Add(String.Format(CultureInfo.CurrentCulture, LocalizationService.T("{0} von {1} ohne Recht"), refused, total))
            If missing > 0 Then parts.Add(String.Format(CultureInfo.CurrentCulture, LocalizationService.T("{0} von {1} nicht mehr vorhanden"), missing, total))
            Return String.Join(", ", parts)
        End Function

        ''' <summary>Der Satz zur Grenze einer Auswahl, fuer die Seite und fuer die Antwort 413.</summary>
        Friend Shared Function MobileSelectionLimitText() As String
            Return String.Format(CultureInfo.CurrentCulture, LocalizationService.T("Höchstens {0} Bilder auf einmal"), MaxMobileSelection)
        End Function

        ''' <summary>Bewertung, Favorit, Etikett und Markierung einer Datei, wie sie im Katalog stehen.</summary>
        Private Shared Function MobileMarksOf(path As String) As Dictionary(Of String, Object)
            Dim meta As LibraryImageMeta = Nothing
            LibraryService.Instance.GetMetaForPaths({path}).TryGetValue(path, meta)
            Return New Dictionary(Of String, Object) From {
                {"ok", True},
                {"rating", If(meta Is Nothing, 0, Math.Max(0, Math.Min(5, meta.Rating)))},
                {"favorite", meta IsNot Nothing AndAlso meta.IsFavorite},
                {"label", If(meta?.ColorLabel, "").Trim().ToUpperInvariant()},
                {"pick", If(meta Is Nothing, 0, LibraryService.NormalizePickState(meta.PickState))}}
        End Function

        ''' <summary>POST /api/m/lock: sperrt die Anwendung wie "Sperren" in der Galerie. Das beendet
        ''' alle Sitzungen, auch die der Desktop-Seite.</summary>
        Private Shared Async Function HandleMobileLockAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            If Not mobile.CanLockApp Then
                Await WriteMobileJsonAsync(context, 403, MobileProblem("forbidden"))
                Return
            End If
            Dim user = SecurityLog.UserNameOf(mobile.UserKey)
            Dim from = LogAddress(context.Request)
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}})
            If AppLockService.IsProtected Then
                PostToApp(Sub() AppLockService.Lock("manual", user, from))
            End If
        End Function

        ''' <summary>GET /api/m/me: wer angemeldet ist und was die Seite ihm anbietet (die volle
        ''' Oberflaeche und das Sperren nur einem Administrator). Die Knoepfe je Bild und Ordner
        ''' sagen info und folder; geprueft wird jeder Weg trotzdem am Server.</summary>
        Private Shared Async Function HandleMobileMeAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim account = UserAccountService.Find(mobile.UserKey)
            ' Die Sprachen zur Auswahl: "automatisch" und jede der Anwendung, mit ihrem Namen in der
            ' eigenen Sprache.
            Dim languages As New List(Of Object) From {New Object() {"System", LocalizationService.T("Automatisch (Sprache des Browsers)")}}
            For Each language In LocalizationService.Languages.Where(Function(l) l.Key <> "System")
                languages.Add(New Object() {language.Key, language.Name})
            Next
            Dim current = If(account?.Language, "")
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {
                {"name", If(account?.Name, "")}, {"admin", mobile.IsAdmin}, {"desktop", CanUseDesktop(mobile.UserKey)},
                {"lock", mobile.CanLockApp}, {"language", If(current.Length = 0, "System", current)}, {"languages", languages},
                {"deleteForever", AppSettingsService.Load().DeleteSkipTrash}})
        End Function

        ''' <summary>POST /api/m/username mit {"name"}: der Benutzer benennt sein eigenes Konto um,
        ''' mit denselben Regeln wie in der Verwaltung (UserAccountService.RenameUser). Die Sitzung
        ''' bleibt; die Anmeldung gilt danach nur mit dem neuen Namen. 400 mit Grund fuer einen
        ''' ungueltigen, 409 fuer einen vergebenen Namen.</summary>
        Private Shared Async Function HandleMobileUserNameAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            Dim wanted = If(body.HasValue, ReadString(body.Value, "name"), Nothing)
            If wanted Is Nothing Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If
            Dim outcome = UserAccountService.RenameUser(mobile.UserKey, wanted, mobile.UserKey, LogAddress(context.Request))
            Select Case outcome
                Case UserChangeOutcome.Done
                    Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {
                        {"ok", True}, {"name", If(UserAccountService.Find(mobile.UserKey)?.Name, "")}})
                Case UserChangeOutcome.InvalidName
                    Await WriteMobileJsonAsync(context, 400, MobileProblem(ViewModels.SettingsViewModel.UserOutcomeText(outcome)))
                Case UserChangeOutcome.NameTaken
                    Await WriteMobileJsonAsync(context, 409, MobileProblem(ViewModels.SettingsViewModel.UserOutcomeText(outcome)))
                Case Else
                    Await WriteMobileJsonAsync(context, 404, MobileProblem("not found"))
            End Select
        End Function

        ''' <summary>POST /api/m/language mit {"language": Schluessel aus LocalizationService.Languages
        ''' oder "System"}: der Benutzer stellt seine Sprache selbst ein. Gespeichert sofort; die
        ''' Seite laedt danach neu und erscheint in ihr. Die Anwendung selbst bleibt in ihrer Sprache.</summary>
        Private Shared Async Function HandleMobileLanguageAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            Dim wanted = If(body.HasValue, ReadString(body.Value, "language"), Nothing)
            If wanted Is Nothing OrElse (wanted <> "System" AndAlso LocalizationService.NormalizeLanguageMode(wanted) <> wanted) Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If
            Dim outcome = UserAccountService.SetLanguage(mobile.UserKey, wanted)
            If outcome <> UserChangeOutcome.Done Then
                Await WriteMobileJsonAsync(context, 404, MobileProblem("not found"))
                Return
            End If
            RememberLanguage(context, mobile.UserKey)
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}})
        End Function

        ''' <summary>Die Datei einer Kennung samt ihren Partnern (RAW und JPEG gleichen Namens), wenn
        ''' der Benutzer mit allen darf, was <paramref name="allowed"/> prueft. Sonst ist die Antwort
        ''' geschrieben (404 fuer eine unbekannte Kennung, 403 ohne Recht) und das Ergebnis Nothing.</summary>
        Private Shared Async Function ResolveForChangeAsync(context As HttpListenerContext, mobile As MobileContext, message As JsonElement,
                                                            allowed As Func(Of String, Boolean)) As Task(Of List(Of String))
            Dim path = mobile.ResolveFile(ReadString(message, "id"))
            If path Is Nothing Then
                Await WriteMobileJsonAsync(context, 404, MobileProblem(LocalizationService.T("Das Bild konnte nicht geladen werden")))
                Return Nothing
            End If
            Dim files As New List(Of String) From {path}
            files.AddRange(MobilePartnersOf(path).Where(Function(p) mobile.CanSeeOnDisk(p)))
            If files.Any(Function(f) Not allowed(f)) Then
                Await WriteMobileJsonAsync(context, 403, MobileProblem(LocalizationService.T("Die Änderung konnte nicht gespeichert werden")))
                Return Nothing
            End If
            Return files
        End Function

        ''' <summary>POST /api/m/delete mit {"id"}: loescht das Bild (samt dem Partner eines RAW-Paars
        ''' und den Begleitdateien) wie die Galerie, ab Werk in den Papierkorb. Verlangt "Verwalten".
        ''' Nimmt der Papierkorb es nicht, wird NICHT still endgueltig geloescht: die Antwort nennt
        ''' den Grund. Die Seite fragt vorher nach. Mit "ids" statt "id" eine Auswahl
        ''' (<see cref="HandleMobileDeleteManyAsync"/>).</summary>
        Private Shared Async Function HandleMobileDeleteAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            If Not body.HasValue Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If
            Dim list As JsonElement
            If body.Value.TryGetProperty("ids", list) Then
                Await HandleMobileDeleteManyAsync(context, mobile, list)
                Return
            End If
            Dim files = Await ResolveForChangeAsync(context, mobile, body.Value, AddressOf mobile.CanDelete)
            If files Is Nothing Then Return
            Dim useTrash = Not AppSettingsService.Load().DeleteSkipTrash
            Dim refused = Await Task.Run(
                Function() As String
                    Using batch = RawSidecarService.BatchScope()
                        For Each doomed In files
                            Try
                                If useTrash Then
                                    Dim reason As String = Nothing
                                    If Not TrashService.MoveToTrash(doomed, reason) Then Return IO.Path.GetFileName(doomed)
                                Else
                                    IO.File.Delete(doomed)
                                End If
                                RawSidecarService.AccompanyDelete(doomed, useTrash)
                            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                                DiagnosticLogService.LogException("BrowserBridge.Mobile.Delete", ex)
                                Return IO.Path.GetFileName(doomed)
                            End Try
                        Next
                        batch.Finish()
                    End Using
                    Return Nothing
                End Function)
            ForgetMobileTimelines()
            Dim stored = UploadStored
            If stored IsNot Nothing Then
                Dim first = files(0)
                PostToApp(Sub() stored(first))
            End If
            SecurityLog.FilesDeleted(SecurityLog.UserNameOf(mobile.UserKey), LogAddress(context.Request), files, mobile.BrowseRoots(), useTrash)
            If refused IsNot Nothing Then
                Await WriteMobileJsonAsync(context, 409, MobileProblem(
                    String.Format(LocalizationService.T("{0} ließ sich nicht in den Papierkorb verschieben."), refused)))
                Return
            End If
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}})
        End Function

        ''' <summary>POST /api/m/delete mit {"ids": [...]}: loescht eine Auswahl wie ein einzelnes Bild
        ''' (Papierkorb ab Werk, Partner des RAW-Paars und Begleitdateien mit), hoechstens
        ''' <see cref="MaxMobileSelection"/> Kennungen. Jedes Bild geht fuer sich durch den Kontext
        ''' ("Verwalten"); was der Benutzer nicht loeschen darf oder was es nicht mehr gibt, zaehlt die
        ''' Antwort wie beim Markieren. Eine Variante wird nie geloescht (ihre Datei traegt alle
        ''' Varianten). Nimmt der Papierkorb eine Datei nicht, hoert das Loeschen dort auf, NICHT
        ''' still endgueltig: 409 mit dem Namen. Das Protokoll bekommt EINE Zeile mit allen Dateien,
        ''' die wirklich weg sind.</summary>
        Private Shared Async Function HandleMobileDeleteManyAsync(context As HttpListenerContext, mobile As MobileContext, list As JsonElement) As Task
            If list.ValueKind <> JsonValueKind.Array Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If
            Dim ids = list.EnumerateArray().Where(Function(e) e.ValueKind = JsonValueKind.String).
                           Select(Function(e) e.GetString()).Distinct(StringComparer.Ordinal).ToList()
            If ids.Count = 0 Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If
            If ids.Count > MaxMobileSelection Then
                Await WriteMobileJsonAsync(context, 413, MobileProblem(MobileSelectionLimitText()))
                Return
            End If
            Dim outcome = Await Task.Run(Function() SortMobileSelection(mobile, ids, False, AddressOf mobile.CanDelete))
            If outcome.Done.Count = 0 Then
                Dim status = If(outcome.Refused > 0, 403, 404)
                Await WriteMobileJsonAsync(context, status, MobileProblem(MobileSelectionProblemText(outcome.Refused, outcome.Missing, ids.Count)))
                Return
            End If
            Dim useTrash = Not AppSettingsService.Load().DeleteSkipTrash
            Dim deleted As New List(Of String)()
            Dim refused = Await Task.Run(
                Function() As String
                    Using batch = RawSidecarService.BatchScope()
                        For Each doomed In outcome.Files
                            Try
                                If useTrash Then
                                    Dim reason As String = Nothing
                                    If Not TrashService.MoveToTrash(doomed, reason) Then Return IO.Path.GetFileName(doomed)
                                Else
                                    IO.File.Delete(doomed)
                                End If
                                deleted.Add(doomed)
                                RawSidecarService.AccompanyDelete(doomed, useTrash)
                            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                                DiagnosticLogService.LogException("BrowserBridge.Mobile.DeleteMany", ex)
                                Return IO.Path.GetFileName(doomed)
                            End Try
                        Next
                        batch.Finish()
                    End Using
                    Return Nothing
                End Function)
            ForgetMobileTimelines()
            Dim stored = UploadStored
            If stored IsNot Nothing AndAlso deleted.Count > 0 Then
                Dim first = deleted(0)
                PostToApp(Sub() stored(first))
            End If
            If deleted.Count > 0 Then
                SecurityLog.FilesDeleted(SecurityLog.UserNameOf(mobile.UserKey), LogAddress(context.Request), deleted, mobile.BrowseRoots(), useTrash)
            End If
            If refused IsNot Nothing Then
                Await WriteMobileJsonAsync(context, 409, MobileProblem(
                    String.Format(LocalizationService.T("{0} ließ sich nicht in den Papierkorb verschieben."), refused)))
                Return
            End If
            Await WriteMobileJsonAsync(context, 200, MobileSelectionAnswer(outcome, ids.Count))
        End Function

        ''' <summary>POST /api/m/rename mit {"id", "name"}: ein neuer Name ohne Endung; die Endung
        ''' bleibt (klein geschrieben, wie in der Galerie). Der Partner eines RAW-Paars bekommt
        ''' denselben neuen Namen, Begleitdateien und Katalog gehen mit. Verlangt "Verwalten". Ist
        ''' ein Zielname schon vergeben, wird nichts umbenannt.</summary>
        Private Shared Async Function HandleMobileRenameAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            If Not body.HasValue Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem("bad request"))
                Return
            End If
            Dim files = Await ResolveForChangeAsync(context, mobile, body.Value, AddressOf mobile.CanRename)
            If files Is Nothing Then Return
            Dim stem = If(ReadString(body.Value, "name"), "").Trim()
            If Not IsValidMobileStem(stem) Then
                Await WriteMobileJsonAsync(context, 400, MobileProblem(LocalizationService.T("Der Dateiname ist ungültig")))
                Return
            End If
            Dim moves = files.Select(Function(f) (Source:=f, Target:=IO.Path.Combine(IO.Path.GetDirectoryName(f), stem & IO.Path.GetExtension(f).ToLowerInvariant()))).ToList()
            If moves.Any(Function(m) Not PathIdentity.AreSame(m.Source, m.Target) AndAlso (IO.File.Exists(m.Target) OrElse IO.Directory.Exists(m.Target))) Then
                Await WriteMobileJsonAsync(context, 409, MobileProblem(LocalizationService.T("Ein Element mit diesem Namen existiert bereits.")))
                Return
            End If
            Dim problem = Await Task.Run(
                Function() As String
                    Try
                        For Each move In moves
                            ViewModels.GalleryViewModel.ThrowIfTargetSidecarConflict(move.Source, move.Target, replaceConfirmed:=False)
                        Next
                        For Each move In moves
                            If String.Equals(move.Source, move.Target, StringComparison.Ordinal) Then Continue For
                            IO.File.Move(move.Source, move.Target)
                            RawSidecarService.AccompanyMove(move.Source, move.Target)
                            LibraryService.Instance.MoveEverythingForPath(move.Source, move.Target)
                        Next
                        Return Nothing
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                        DiagnosticLogService.LogException("BrowserBridge.Mobile.Rename", ex)
                        Return ex.Message
                    End Try
                End Function)
            ForgetMobileTimelines()
            Dim stored = UploadStored
            If stored IsNot Nothing Then
                Dim renamed = moves(0).Target
                PostToApp(Sub() stored(renamed))
            End If
            If problem IsNot Nothing Then
                Await WriteMobileJsonAsync(context, 409, MobileProblem(problem))
                Return
            End If
            SecurityLog.FileRenamed(SecurityLog.UserNameOf(mobile.UserKey), LogAddress(context.Request), moves(0).Source, moves(0).Target, mobile.BrowseRoots())
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {
                {"ok", True}, {"id", MobileIdFor(MobileFileKind, moves(0).Target)}, {"name", IO.Path.GetFileName(moves(0).Target)}})
        End Function

        ''' <summary>Ein neuer Name ohne Endung: nicht leer, hoechstens 200 Zeichen, ohne Zeichen, die
        ''' in einem Dateinamen nicht gehen, ohne Steuerzeichen und nicht mit einem Punkt vorn (das
        ''' versteckte die Datei).</summary>
        Friend Shared Function IsValidMobileStem(stem As String) As Boolean
            If String.IsNullOrWhiteSpace(stem) OrElse stem.Length > 200 OrElse stem.StartsWith("."c) Then Return False
            If stem.IndexOfAny(IO.Path.GetInvalidFileNameChars()) >= 0 OrElse stem.IndexOfAny({"/"c, "\"c, ":"c}) >= 0 Then Return False
            Return Not stem.Any(Function(c) Char.IsControl(c))
        End Function

    End Class

End Namespace
