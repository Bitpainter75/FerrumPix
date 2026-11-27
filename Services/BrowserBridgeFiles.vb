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
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Threading

Namespace Services

    ''' <summary>Wohin eine Datei geht, die der Browser hochlaedt: in einen Ordner, als Ebene ins
    ''' offene Bild des Editors, oder gar nicht (dann mit dem Grund fuer die Seite).</summary>
    Public NotInheritable Class BrowserUploadTarget

        ''' <summary>Der Ordner, in den die Datei kommt. Leer, wenn sie als Ebene eingesetzt wird
        ''' oder abgewiesen ist.</summary>
        Public ReadOnly Property Folder As String

        ''' <summary>Setzt die fertig empfangene Datei als Ebene ein. Bekommt den Pfad der Datei und
        ''' ihre Stelle im abgelegten Stapel (fuer den Versatz). Laeuft auf dem Anzeigefaden. False:
        ''' das Bild, auf das abgelegt wurde, ist nicht mehr offen; dann ist nichts eingesetzt.</summary>
        Public ReadOnly Property InsertAsLayer As Func(Of String, Integer, Task(Of Boolean))

        ''' <summary>Warum nichts angenommen wird, schon uebersetzt. Leer heisst: angenommen.</summary>
        Public ReadOnly Property Problem As String

        Private Sub New(folder As String, insertAsLayer As Func(Of String, Integer, Task(Of Boolean)), problem As String)
            Me.Folder = If(folder, "")
            Me.InsertAsLayer = insertAsLayer
            Me.Problem = If(problem, "")
        End Sub

        Public Shared Function IntoFolder(folder As String) As BrowserUploadTarget
            Return New BrowserUploadTarget(folder, Nothing, Nothing)
        End Function

        Public Shared Function AsLayer(insert As Func(Of String, Integer, Task(Of Boolean))) As BrowserUploadTarget
            Return New BrowserUploadTarget(Nothing, insert, Nothing)
        End Function

        Public Shared Function Refused(problem As String) As BrowserUploadTarget
            Return New BrowserUploadTarget(Nothing, Nothing, problem)
        End Function
    End Class

    ''' <summary>
    ''' Dateien zwischen dem Rechner des Browsers und der Anwendung: Hochladen per Ablegen auf die
    ''' Seite oder ueber den Dateiwaehler, Herunterladen dessen, was die Anwendung ausdruecklich
    ''' anbietet.
    '''
    ''' <para>DIESELBE ANMELDUNG WIE DIE SEITE. Beide Wege laufen ueber den Webserver der Bruecke und
    ''' damit hinter derselben Adresspruefung und derselben Anmeldung (HTTP Basic, ist ein Kennwort
    ''' gesetzt). Dazu muss jede Anfrage die Kennung einer ausgelieferten Seite tragen; das Hochladen
    ''' zusaetzlich die eigene Herkunft (Kopf Origin). Eine fremde Seite im selben Browser kennt die
    ''' Kennung nicht und scheitert an der Herkunft.</para>
    '''
    ''' <para>HOCHLADEN GEHT STROEMEND AUF DIE PLATTE, in eine versteckte Teildatei neben dem Ziel,
    ''' und erst nach vollstaendigem Empfang wird sie unter einem freien Namen umbenannt. Bricht die
    ''' Uebertragung ab, wird die Teildatei geloescht. Ueberschrieben wird nie.</para>
    '''
    ''' <para>HERUNTERGELADEN WIRD NUR, WAS ANGEBOTEN IST: die Anwendung legt ein Angebot an (nach dem
    ''' Exportieren, nach Speichern unter, oder ueber "Herunterladen" im Kontextmenue), und die Seite
    ''' bekommt nur dessen zufaellige Kennung. Einen Pfad nimmt die Anfrage nie an.</para>
    ''' </summary>
    Partial Public NotInheritable Class BrowserBridge

        Private Const UploadRoute As String = "/upload"
        Private Const DownloadRoute As String = "/download"

        ''' <summary>Die Stelle in der Seite, an der ihre Texte in der Sprache der Anwendung
        ''' eingesetzt werden.</summary>
        Private Const TextsPlaceholder As String = "__FERRUMPIX_TEXTS__"

        ''' <summary>Hoechstgroesse einer hochgeladenen Datei: 4 GB. Kameras teilen ein Video auf der
        ''' Karte bei 4 GB (FAT32), eine RAW liegt bei hoechstens einigen hundert MB; jede Datei von
        ''' einer Speicherkarte passt also. Mehr ist ein Versehen, und weil der Empfang stroemend auf
        ''' die Platte geht, kostet die Grenze keinen Speicher.</summary>
        Friend Const MaxUploadBytes As Long = 4L * 1024 * 1024 * 1024

        ''' <summary>So viel muss auf dem Laufwerk nach dem Hochladen noch frei bleiben. Ein volles
        ''' Laufwerk legt sonst Katalog, Vorschaubilder und Einstellungen lahm.</summary>
        Private Const FreeSpaceReserveBytes As Long = 512L * 1024 * 1024

        Private Const MaxOffers As Integer = 64
        Private Shared ReadOnly OfferLifetime As TimeSpan = TimeSpan.FromHours(2)
        Private Const MaxRememberedBatches As Integer = 16

        ''' <summary>Fragt die Anwendung, wohin eine hochgeladene Datei gehoert. Bekommt das Fenster
        ''' unter der Stelle des Ablegens und die Stelle darin (Nothing beim Dateiwaehler) und laeuft
        ''' auf dem Anzeigefaden. Nicht gesetzt heisst: es gibt keinen Ordner.</summary>
        Public Shared Property UploadTargetResolver As Func(Of Window, Point?, BrowserUploadTarget)

        ''' <summary>Meldet eine fertig in einen Ordner gelegte Datei, auf dem Anzeigefaden, damit
        ''' die Galerie sie zeigt.</summary>
        Public Shared Property UploadStored As Action(Of String)

        Private Shared ReadOnly UploadNameLock As New Object()
        Private Shared ReadOnly Offers As New Dictionary(Of String, DownloadOffer)(StringComparer.Ordinal)
        Private Shared ReadOnly Batches As New Dictionary(Of String, UploadBatch)(StringComparer.Ordinal)
        Private Shared ReadOnly BatchOrder As New Queue(Of String)()

        ''' <summary>Was die Bruecke sich von EINEM Ablegen merkt (Kennung batch der Seite).</summary>
        Private NotInheritable Class UploadBatch
            ''' <summary>Die Bilder dieses Ablegens: Name aus dem Browser (bereinigt) und Name im
            ''' Ordner. Nothing heisst: es kam nicht an (abgewiesen, abgebrochen, zu gross).</summary>
            Public ReadOnly Images As New Dictionary(Of String, String)(PathIdentity.Comparer)
            ''' <summary>Das Ziel der ersten Datei. Es gilt fuer alle weiteren: wechselt waehrend des
            ''' Hochladens der Ordner der Galerie oder das Bild im Editor, landet der Rest sonst
            ''' anderswo als dort, wo abgelegt wurde.</summary>
            Public Target As BrowserUploadTarget
        End Class

        ''' <summary>Wo eine Datei liegt, die als Ebene eingesetzt wird. Je Lauf ein eigener Ordner
        ''' unter dem Temp-Ordner; der Editor uebernimmt sie von dort in seinen Ordner fuer
        ''' Ebenenbilder.</summary>
        Private Shared ReadOnly LayerUploadFolder As String =
            Path.Combine(Path.GetTempPath(), "FerrumPix", "BrowserUploads", Guid.NewGuid().ToString("N"))

        Private NotInheritable Class DownloadOffer
            Public Paths As List(Of String)
            Public FileName As String
            Public Created As DateTime
            ''' <summary>Der eigene Ordner der Bruecke, in den fuer das Ziel "Herunterladen"
            ''' geschrieben wurde (BrowserBridgeDownloads.vb). Er gehoert dem Angebot und geht mit
            ''' ihm: nach dem fertigen Download, beim Ablauf, beim Verdraengen. Nothing bei einem
            ''' Angebot von Dateien aus dem Bestand.</summary>
            Public OwnedFolder As String
            ''' <summary>Gesetzt bei "Herunterladen" an einem Ordner (BrowserBridgeFolderDownload.vb):
            ''' das ZIP entsteht aus dem Inhalt dieses Ordners, wie er bei der Auslieferung ist,
            ''' Paths bleibt leer. Der Ordner gehoert NICHT dem Angebot und wird nie geloescht.</summary>
            Public Folder As String
            ''' <summary>Wie viele Auslieferungen gerade laufen. Solange es eine gibt, bleibt der
            ''' eigene Ordner liegen, auch wenn das Angebot schon weg ist; die letzte nimmt ihn mit.
            ''' Gelesen und geschrieben nur unter der Sperre von Offers.</summary>
            Public ActiveDeliveries As Integer
            ''' <summary>Das Angebot ist aus Offers heraus (ausgeliefert, abgelaufen, verdraengt,
            ''' Bruecke angehalten); sein Ordner geht, sobald keine Auslieferung mehr laeuft.</summary>
            Public Retired As Boolean
        End Class

        ''' <summary>Die Texte der Seite in der Sprache der Anwendung. Sie stehen hier und nicht in
        ''' der Seite, damit sie durch dieselbe Uebersetzung gehen wie jeder andere Text.</summary>
        Private Shared Function PageTexts() As Dictionary(Of String, String)
            Dim texts = New Dictionary(Of String, String) From {
                {"dropHere", LocalizationService.T("Dateien zum Hochladen hier ablegen")},
                {"upload", LocalizationService.T("Dateien hochladen")},
                {"cancel", LocalizationService.T("Abbrechen")},
                {"close", LocalizationService.T("Schließen")},
                {"progress", LocalizationService.T("{0} von {1} Dateien hochgeladen")},
                {"done", LocalizationService.T("Hochgeladen")},
                {"failed", LocalizationService.T("Fehlgeschlagen")},
                {"cancelled", LocalizationService.T("Abgebrochen")},
                {"interrupted", LocalizationService.T("Die Übertragung wurde unterbrochen")},
                {"download", LocalizationService.T("Herunterladen")},
                {"ready", LocalizationService.T("Bereit zum Herunterladen")},
                {"tooLarge", MaxUploadText()},
                {"maxBytes", MaxUploadBytes.ToString(CultureInfo.InvariantCulture)}}
            ' Der Hinweis steht nur im unverschluesselten Betrieb im Heimnetz in der Seite; fehlt
            ' er, zeigt die Seite nichts.
            If _allowHttp Then
                texts("httpNotice") = LocalizationService.T("Unverschlüsselte Verbindung im Heimnetz")
                texts("httpNoticeDetail") = LocalizationService.T("Kennwort und Bilder gehen lesbar durch das Netz. Für den Zugriff von unterwegs HTTPS verwenden.")
            End If
            ' Einen Knopf zum Abmelden hat die Seite nicht mehr: abgemeldet wird ueber "Sperren" in
            ' der Galerie, das beendet alle Sitzungen. Den Weg /logout gibt es weiter.
            AddClipboardPageTexts(texts)
            Return texts
        End Function

        Private Shared Function PageTextsJson() As String
            Return JsonSerializer.Serialize(PageTexts())
        End Function

        ''' <summary>Hoechstgroesse in GB fuer die Meldung.</summary>
        Private Shared Function MaxUploadText() As String
            Return String.Format(LocalizationService.T("Die Datei ist größer als {0} GB"),
                                 (MaxUploadBytes \ (1024L * 1024 * 1024)).ToString(CultureInfo.CurrentCulture))
        End Function

        ' --- Hochladen -------------------------------------------------------------------------

        ''' <summary>Nimmt eine Datei an. Die Reihenfolge der Pruefungen ist Absicht: alles, was ohne
        ''' den Inhalt entschieden werden kann, kommt vor dem ersten gelesenen Byte, und vor dem
        ''' ersten geschriebenen.</summary>
        Private Shared Async Function HandleUploadAsync(context As HttpListenerContext) As Task
            Dim request = context.Request
            ' Jede Datei auf eigener Verbindung: wird eine Anfrage abgewiesen, bevor ihr Inhalt
            ' gelesen ist, laege der Rest sonst auf der Verbindung, und die naechste Anfrage darauf
            ' hinge oder laese ihn als ihren Kopf.
            context.Response.KeepAlive = False
            ' Sitzung oder, fuer Werkzeuge, Basic (BrowserBridgeSessions.vb). Nie WWW-Authenticate.
            If Not IsSignedIn(context) Then
                context.Response.StatusCode = 401
                context.Response.Close()
                Return
            End If
            ' Der Weg der vollen Oberflaeche: nur fuer Administratoren (CanUseDesktop). Weitere
            ' Benutzer laden ueber die mobile Seite hoch, mit den Rechten je Ordner.
            If Not IsSignedInForDesktop(context) Then
                context.Response.StatusCode = 403
                context.Response.Close()
                Return
            End If
            If Not IsOwnOrigin(request) OrElse Not IsKnownToken(request.Headers("X-FerrumPix-Token")) Then
                Await WriteResultAsync(context, 403, Nothing, LocalizationService.T("Die Seite ist veraltet. Bitte neu laden."))
                Return
            End If
            If Not String.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) Then
                context.Response.StatusCode = 405
                context.Response.Close()
                Return
            End If

            Dim fileName = SanitizeUploadName(request.QueryString("name"))
            If fileName Is Nothing Then
                Await WriteResultAsync(context, 400, Nothing, LocalizationService.T("Der Dateiname ist ungültig"))
                Return
            End If
            Dim batch = If(request.QueryString("batch"), "")
            If batch.Length > 64 Then batch = batch.Substring(0, 64)

            ' Jedes Bild eines Ablegens wird vermerkt, auch eines, das nicht ankommt: seine
            ' Beistelldatei darf dann nicht neben einem gleichnamigen anderen Bild im Ordner landen.
            Dim storedPath As String = Nothing
            ' Vor dem Empfang: nach der Antwort ist die Verbindung zu (KeepAlive aus), und ihre
            ' Adresse laesst sich nicht mehr lesen.
            Dim from = LogAddress(request)
            Dim userName = SecurityLog.UserNameOf(SignedInUser(context))
            ' Eine Zeile je Ablegen, nicht je Datei (SecurityLog.NoteUpload); vermerkt, bevor die
            ' Antwort hinausgeht.
            Dim noteStored As Action(Of String) =
                Sub(stored) SecurityLog.NoteUpload(batch, userName, from,
                                                   SecurityLog.ShownPath(Path.GetDirectoryName(stored), LogRoots()), "desktop")
            Try
                storedPath = Await ReceiveUploadAsync(context, fileName, batch, noteStored)
            Finally
                If Not IsSidecarName(fileName) Then
                    RememberBatchImage(batch, fileName, If(storedPath Is Nothing, Nothing, Path.GetFileName(storedPath)))
                End If
            End Try
        End Function

        ''' <summary>Der Rest des Hochladens, nach Anmeldung und Name. Liefert den Pfad der
        ''' angekommenen Datei, Nothing, wenn sie abgewiesen ist oder nicht ankam (die Antwort an
        ''' die Seite ist dann schon geschrieben).</summary>
        Private Shared Async Function ReceiveUploadAsync(context As HttpListenerContext, fileName As String, batch As String,
                                                         noteStored As Action(Of String)) As Task(Of String)
            Dim request = context.Request
            ' Erst als "nicht angekommen" vermerken: jede Abweisung unten schreibt ihre Antwort, bevor
            ' der Aufrufer vermerkt, und eine gleich danach geschickte Beistelldatei saehe ihr Bild
            ' sonst gar nicht. Angekommen traegt es kurz vor der Antwort seinen Namen.
            If Not IsSidecarName(fileName) Then RememberBatchImage(batch, fileName, Nothing)
            Dim length = request.ContentLength64
            If length < 0 Then
                context.Response.StatusCode = 411
                context.Response.Close()
                Return Nothing
            End If
            If length = 0 Then
                Await WriteResultAsync(context, 400, Nothing, LocalizationService.T("Die Datei ist leer"))
                Return Nothing
            End If
            If length > MaxUploadBytes Then
                Await WriteResultAsync(context, 413, Nothing, MaxUploadText())
                Return Nothing
            End If

            Dim x = ReadQueryNumber(request, "x")
            Dim y = ReadQueryNumber(request, "y")
            ' Fehlt die Stelle (Dateiwaehler, Ablegen in einen Ordner), ist sie NaN; CInt(NaN)
            ' wirft in VB, und die Anfrage bliebe ohne Antwort.
            Dim indexValue = ReadQueryNumber(request, "i")
            Dim index = If(Double.IsNaN(indexValue), 0, CInt(Math.Clamp(indexValue, 0, 1000)))
            Dim hasPoint = Not Double.IsNaN(x) AndAlso Not Double.IsNaN(y) AndAlso x >= 0 AndAlso y >= 0

            Dim target = Await Dispatcher.UIThread.InvokeAsync(Function() ResolveUploadTarget(x, y, hasPoint, batch))
            If target.Problem.Length > 0 Then
                Await WriteResultAsync(context, 409, Nothing, target.Problem)
                Return Nothing
            End If

            Dim folder As String
            ' Das Bild dieses Ablegens, zu dem eine Beistelldatei gehoert (Name im Browser), und
            ' wie es im Ordner heisst. Leer: keines dabei, sie behaelt ihren Namen.
            Dim sidecarImage As String = Nothing
            Dim sidecarImageStored As String = Nothing
            If target.InsertAsLayer IsNot Nothing Then
                ' Als Ebene nur ein Bild: ein Video oder eine Beistelldatei hat dort nichts zu suchen.
                If Not MediaFileTypes.IsDisplayable(fileName) OrElse MediaFileTypes.IsVideo(fileName) Then
                    Await WriteResultAsync(context, 415, Nothing, LocalizationService.T("Als Ebene lässt sich nur ein Bild einfügen"))
                    Return Nothing
                End If
                folder = LayerUploadFolder
                Directory.CreateDirectory(folder)
            Else
                If Not IsUploadableName(fileName) Then
                    Await WriteResultAsync(context, 415, Nothing, LocalizationService.T("Diese Dateiart nimmt FerrumPix nicht an"))
                    Return Nothing
                End If
                folder = target.Folder
                ' Dieselbe Regel wie beim Einfuegen in der Galerie, auch wenn die Anwendung den
                ' Ordner schon genannt hat: geschrieben wird nur, wo Dateiarbeit erlaubt ist.
                If String.IsNullOrEmpty(folder) OrElse Not FileOperationPolicy.CanPasteInto(folder) Then
                    Await WriteResultAsync(context, 403, Nothing, LocalizationService.T("Hier darf FerrumPix keine Dateien anlegen"))
                    Return Nothing
                End If
                If IsSidecarName(fileName) Then
                    Dim images = BatchImages(batch)
                    sidecarImage = FindSidecarImage(fileName, images.Keys)
                    If sidecarImage IsNot Nothing Then
                        sidecarImageStored = images(sidecarImage)
                        If sidecarImageStored Is Nothing Then
                            Await WriteResultAsync(context, 409, Nothing, LocalizationService.T("Das Bild zu dieser Beistelldatei ist nicht angekommen"))
                            Return Nothing
                        End If
                    End If
                End If
            End If

            If FreeSpace(folder) < length + FreeSpaceReserveBytes Then
                Await WriteResultAsync(context, 507, Nothing, LocalizationService.T("Auf dem Laufwerk ist nicht genug Platz frei"))
                Return Nothing
            End If

            ' Versteckt, damit weder Galerie noch Katalog eine halbe Datei sehen, und im Zielordner,
            ' damit das Umbenennen danach auf demselben Laufwerk bleibt und nichts kopiert.
            Dim partPath = Path.Combine(folder, "." & fileName & "." & Guid.NewGuid().ToString("N").Substring(0, 12) & ".part")
            Dim finalPath As String = Nothing
            Try
                Dim complete = Await ReceiveBodyAsync(request.InputStream, partPath, length)
                If Not complete Then
                    Await WriteResultAsync(context, 400, Nothing, LocalizationService.T("Die Übertragung wurde unterbrochen"))
                    Return Nothing
                End If
                Dim preferred As List(Of String) = Nothing
                If sidecarImage IsNot Nothing Then
                    preferred = SidecarNamesFor(fileName, sidecarImage, Path.Combine(folder, sidecarImageStored))
                End If
                finalPath = MovePartIntoPlace(partPath, folder, fileName, preferred)
            Finally
                If finalPath Is Nothing Then TryDelete(partPath)
            End Try

            If target.InsertAsLayer IsNot Nothing Then
                Dim insert As Func(Of Task(Of Boolean)) = Function() target.InsertAsLayer(finalPath, index)
                Dim inserted As Boolean = Await Dispatcher.UIThread.InvokeAsync(insert)
                If Not inserted Then
                    ' Das Bild, auf das abgelegt wurde, ist nicht mehr offen: nichts einsetzen,
                    ' schon gar nicht in das Bild, das jetzt offen ist.
                    TryDelete(finalPath)
                    Await WriteResultAsync(context, 409, Nothing, LocalizationService.T("Im Editor ist inzwischen ein anderes Bild offen, die Datei wurde nicht eingefügt"))
                    Return Nothing
                End If
            Else
                Dim stored = UploadStored
                If stored IsNot Nothing Then Dispatcher.UIThread.Post(Sub() stored(finalPath))
            End If
            ' VOR der Antwort vermerken: die Seite schickt die Beistelldatei, sobald die Antwort da
            ' ist, und faende das Bild sonst noch nicht im Ablegen (der Vermerk im Finally des
            ' Aufrufers kaeme zu spaet; er bleibt fuer Ausnahmen).
            If Not IsSidecarName(fileName) Then RememberBatchImage(batch, fileName, Path.GetFileName(finalPath))
            noteStored?.Invoke(finalPath)
            Await WriteResultAsync(context, 200, Path.GetFileName(finalPath), Nothing)
            Return finalPath
        End Function

        ''' <summary>Liest genau <paramref name="length"/> Bytes in die Teildatei. False, wenn die
        ''' Verbindung vorher abreisst; die Teildatei raeumt der Aufrufer weg.</summary>
        Private Shared Async Function ReceiveBodyAsync(input As Stream, partPath As String, length As Long) As Task(Of Boolean)
            Dim remaining = length
            Dim buffer(1024 * 1024 - 1) As Byte
            Try
                Using output As New FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous)
                    While remaining > 0
                        Dim read = Await input.ReadAsync(buffer, 0, CInt(Math.Min(buffer.Length, remaining)))
                        If read <= 0 Then Exit While
                        Await output.WriteAsync(buffer, 0, read)
                        remaining -= read
                    End While
                End Using
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is ObjectDisposedException
                Return False
            End Try
            Return remaining = 0
        End Function

        ''' <summary>Benennt die Teildatei unter einem freien Namen um. Unter einer Sperre, damit zwei
        ''' gleichnamige Dateien nicht denselben Namen bekommen; scheitert das Umbenennen doch, weil
        ''' ein anderes Programm den Namen dazwischen belegt hat, geht es mit dem naechsten.</summary>
        ''' <param name="preferred">Fuer eine Beistelldatei: die Namen, unter denen sie zu ihrem Bild
        ''' gehoert, der beste zuerst. Der erste freie wird genommen; sind alle belegt, gilt die
        ''' Regel fuer jeden anderen Namen (ueberschrieben wird nie).</param>
        Private Shared Function MovePartIntoPlace(partPath As String, folder As String, fileName As String,
                                                  preferred As IList(Of String)) As String
            SyncLock UploadNameLock
                If preferred IsNot Nothing Then
                    For Each name In preferred
                        Dim candidate = Path.Combine(folder, name)
                        If File.Exists(candidate) OrElse Directory.Exists(candidate) Then Continue For
                        Try
                            File.Move(partPath, candidate, overwrite:=False)
                            Return candidate
                        Catch ex As IOException When File.Exists(candidate) OrElse Directory.Exists(candidate)
                        End Try
                    Next
                    If preferred.Count > 0 Then fileName = preferred(0)
                End If
                For attempt = 1 To 5
                    Dim candidate = FreeUploadPath(folder, fileName)
                    Try
                        File.Move(partPath, candidate, overwrite:=False)
                        Return candidate
                    Catch ex As IOException When File.Exists(candidate) OrElse Directory.Exists(candidate)
                    End Try
                Next
            End SyncLock
            Throw New IOException("No free name for the uploaded file.")
        End Function

        ''' <summary>Ein freier Name im Ordner: der eigene, sonst "Name (1).jpg", "Name (2).jpg" -
        ''' dieselbe Regel wie beim Holen vom Server in einen Ordner.</summary>
        Friend Shared Function FreeUploadPath(folder As String, fileName As String) As String
            Dim candidate = Path.Combine(folder, fileName)
            If Not File.Exists(candidate) AndAlso Not Directory.Exists(candidate) Then Return candidate
            Dim stem = Path.GetFileNameWithoutExtension(fileName)
            Dim extension = Path.GetExtension(fileName)
            Dim number = 1
            Do
                candidate = Path.Combine(folder, $"{stem} ({number}){extension}")
                number += 1
            Loop While File.Exists(candidate) OrElse Directory.Exists(candidate)
            Return candidate
        End Function

        ''' <summary>Der Dateiname aus dem Browser, bereinigt: nur der letzte Teil eines Pfads, ohne
        ''' Steuerzeichen und ohne Zeichen, die ein Dateisystem nicht nimmt, ohne Punkte am Anfang
        ''' (eine versteckte Datei saehe niemand) und am Ende. Nothing, wenn nichts uebrig bleibt.</summary>
        Friend Shared Function SanitizeUploadName(raw As String) As String
            If String.IsNullOrWhiteSpace(raw) Then Return Nothing
            Dim name = raw.Replace("\"c, "/"c)
            Dim slash = name.LastIndexOf("/"c)
            If slash >= 0 Then name = name.Substring(slash + 1)
            Dim cleaned As New StringBuilder(name.Length)
            For Each character In name
                If Char.IsControl(character) Then Continue For
                If "<>:""|?*".IndexOf(character) >= 0 OrElse Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 Then
                    cleaned.Append("_"c)
                Else
                    cleaned.Append(character)
                End If
            Next
            name = cleaned.ToString().Trim().TrimStart("."c).TrimEnd("."c, " "c)
            If name.Length = 0 Then Return Nothing
            ' Die meisten Dateisysteme nehmen hoechstens 255 Bytes; gekuerzt wird der Stamm.
            Do While Encoding.UTF8.GetByteCount(name) > 200
                Dim extension = Path.GetExtension(name)
                Dim stem = Path.GetFileNameWithoutExtension(name)
                If stem.Length <= 1 Then Return Nothing
                name = stem.Substring(0, stem.Length - 1) & extension
            Loop
            Return name
        End Function

        ''' <summary>Was hochgeladen werden darf: was die Galerie als Medium zeigt (dieselbe Liste wie
        ''' Galerie und Katalog) und die Beistelldateien daneben.</summary>
        Friend Shared Function IsUploadableName(fileName As String) As Boolean
            Return MediaFileTypes.IsDisplayable(fileName) OrElse IsSidecarName(fileName)
        End Function

        Private Shared Function IsSidecarName(fileName As String) As Boolean
            Dim extension = Path.GetExtension(If(fileName, ""))
            Return String.Equals(extension, ".xmp", StringComparison.OrdinalIgnoreCase) OrElse
                   String.Equals(extension, RawSidecarService.Extension, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Traegt die Datei eine Bearbeitung, die beim Rechnen weitere Dateien nach ihrem
        ''' Pfad liest (Objektbilder, LUT)? Die Beistelldateien (.xmp, .fpxmp) und das Projekt
        ''' (.fpx). Solche Dateien nimmt die Bruecke nur von einem Administrator an: mit ihnen
        ''' liesse sich sonst ein Bild aus einem Ordner ohne Recht in ein eigenes Bild rechnen oder
        ''' die Bearbeitung eines fremden Bildes ersetzen (RecipeReferences ist die zweite Linie).</summary>
        Friend Shared Function CarriesRecipe(fileName As String) As Boolean
            Return IsSidecarName(fileName) OrElse
                   String.Equals(Path.GetExtension(If(fileName, "")), FpxService.Extension, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Der Name einer Beistelldatei, wenn ihr Bild einen anderen Namen bekommen hat.
        ''' Beide Formen: angehaengt ("IMG.CR2.xmp", "IMG.CR2.fpxmp") und ersetzt ("IMG.xmp").
        ''' Nothing, wenn die Beistelldatei nicht zu diesem Bild gehoert.</summary>
        Friend Shared Function SidecarNameAlong(sidecarName As String, imageName As String, newImageName As String) As String
            If String.IsNullOrEmpty(sidecarName) OrElse String.IsNullOrEmpty(imageName) OrElse String.IsNullOrEmpty(newImageName) Then Return Nothing
            If sidecarName.StartsWith(imageName & ".", StringComparison.OrdinalIgnoreCase) Then
                Return newImageName & sidecarName.Substring(imageName.Length)
            End If
            Dim stem = Path.GetFileNameWithoutExtension(imageName)
            If stem.Length > 0 AndAlso sidecarName.StartsWith(stem & ".", StringComparison.OrdinalIgnoreCase) Then
                Return Path.GetFileNameWithoutExtension(newImageName) & sidecarName.Substring(stem.Length)
            End If
            Return Nothing
        End Function

        ''' <summary>Zu welchem Bild eines Ablegens eine Beistelldatei gehoert, nach denselben Regeln
        ''' wie XmpSidecarService.FindSidecar und RawSidecarService.SidecarPathFor: die angehaengte
        ''' Form ("foto.jpg.xmp", "foto.jpg.fpxmp") gehoert genau zu "foto.jpg"; die ersetzte
        ''' ("foto.xmp", nur bei .xmp) zu einem Bild mit dem Stamm "foto", und gibt es davon mehrere,
        ''' zur RAW. Der Name vor der Endung muss ganz stimmen: "IMG_1.CR2.xmp" gehoert nicht zu
        ''' "IMG_1.jpg", auch wenn beide mit "IMG_1." anfangen. Nothing: keines dabei.</summary>
        Friend Shared Function FindSidecarImage(sidecarName As String, imageNames As IEnumerable(Of String)) As String
            If String.IsNullOrEmpty(sidecarName) OrElse imageNames Is Nothing Then Return Nothing
            Dim extension = Path.GetExtension(sidecarName)
            Dim baseName = sidecarName.Substring(0, sidecarName.Length - extension.Length)
            If baseName.Length = 0 Then Return Nothing
            Dim names = imageNames.ToList()
            For Each name In names
                If String.Equals(name, baseName, PathIdentity.Comparison) Then Return name
            Next
            If Not String.Equals(extension, ".xmp", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Dim sameStem = names.Where(Function(n) Path.GetExtension(n).Length > 0 AndAlso
                                                   String.Equals(Path.GetFileNameWithoutExtension(n), baseName, PathIdentity.Comparison)).ToList()
            If sameStem.Count = 0 Then Return Nothing
            Return If(sameStem.FirstOrDefault(Function(n) RawPreviewService.IsCameraRaw(n)), sameStem(0))
        End Function

        ''' <summary>Die Namen, unter denen eine Beistelldatei zu ihrem angekommenen Bild gehoert,
        ''' der beste zuerst. Die angehaengte Form bleibt angehaengt. Die ersetzte folgt dem Namen,
        ''' den das Bild bekommen hat ("foto (1).xmp"), ausser neben einem Nicht-RAW-Bild liegt eine
        ''' RAW desselben Stamms: der gehoert die ersetzte Form (XmpSidecarService.FindSidecar), das
        ''' Bild bekommt dann die angehaengte. Die angehaengte ist auch der Ersatz, wenn die
        ''' ersetzte schon belegt ist; sie geht beim Lesen ohnehin vor.</summary>
        Friend Shared Function SidecarNamesFor(sidecarName As String, imageName As String, storedImagePath As String) As List(Of String)
            Dim extension = Path.GetExtension(sidecarName)
            Dim storedName = Path.GetFileName(storedImagePath)
            Dim appended = storedName & extension
            Dim result As New List(Of String)()
            Dim wasAppended = String.Equals(sidecarName.Substring(0, sidecarName.Length - extension.Length), imageName, PathIdentity.Comparison)
            If Not wasAppended AndAlso Not RawSidecarService.HasCameraRawStemSibling(storedImagePath) Then
                result.Add(Path.GetFileNameWithoutExtension(storedName) & extension)
            End If
            result.Add(appended)
            Return result
        End Function

        ''' <summary>Die Bilder eines Ablegens bisher (eine Kopie), leer ohne Kennung.</summary>
        Private Shared Function BatchImages(batch As String) As Dictionary(Of String, String)
            SyncLock Batches
                Dim known As UploadBatch = Nothing
                If batch.Length = 0 OrElse Not Batches.TryGetValue(batch, known) Then Return New Dictionary(Of String, String)(PathIdentity.Comparer)
                Return New Dictionary(Of String, String)(known.Images, PathIdentity.Comparer)
            End SyncLock
        End Function

        ''' <summary>Das Ablegen zu einer Kennung, neu angelegt, wenn es noch keines gibt. Gemerkt
        ''' werden die letzten <see cref="MaxRememberedBatches"/>. Nur unter der Sperre Batches.</summary>
        Private Shared Function BatchFor(batch As String) As UploadBatch
            Dim known As UploadBatch = Nothing
            If Batches.TryGetValue(batch, known) Then Return known
            known = New UploadBatch()
            Batches(batch) = known
            BatchOrder.Enqueue(batch)
            While BatchOrder.Count > MaxRememberedBatches
                Batches.Remove(BatchOrder.Dequeue())
            End While
            Return known
        End Function

        ''' <summary>Merkt ein Bild eines Ablegens: den Namen im Ordner, oder Nothing, wenn es nicht
        ''' ankam.</summary>
        Private Shared Sub RememberBatchImage(batch As String, original As String, stored As String)
            If batch.Length = 0 Then Return
            SyncLock Batches
                BatchFor(batch).Images(original) = stored
            End SyncLock
        End Sub

        ''' <summary>Fragt die Anwendung nach dem Ziel. Ein offener Dialog sperrt alles darunter,
        ''' auch das Ablegen: wohin die Datei gehoert, waere dann eine Frage an das falsche Fenster.
        '''
        ''' EIN ZIEL JE ABLEGEN. Gefragt wird bei der ersten Datei; alle weiteren desselben Ablegens
        ''' gehen dorthin, auch wenn inzwischen die Galerie einen anderen Ordner oder der Editor ein
        ''' anderes Bild zeigt. Abgelegt wurde dort, nicht dort, wo der Nutzer waehrend des
        ''' Hochladens hinsieht. Eine Ebene prueft beim Einsetzen, ob ihr Bild noch offen ist.</summary>
        Private Shared Function ResolveUploadTarget(x As Double, y As Double, hasPoint As Boolean, batch As String) As BrowserUploadTarget
            Dim windows = VisibleWindows()
            If windows.Any(Function(w) w.Owner IsNot Nothing) Then
                Return BrowserUploadTarget.Refused(LocalizationService.T("Erst den offenen Dialog schließen"))
            End If
            If batch.Length > 0 Then
                SyncLock Batches
                    Dim known As UploadBatch = Nothing
                    If Batches.TryGetValue(batch, known) AndAlso known.Target IsNot Nothing Then Return known.Target
                End SyncLock
            End If
            Dim resolved = ResolveUploadTargetNow(x, y, hasPoint)
            If batch.Length > 0 AndAlso resolved.Problem.Length = 0 Then
                SyncLock Batches
                    BatchFor(batch).Target = resolved
                End SyncLock
            End If
            Return resolved
        End Function

        Private Shared Function ResolveUploadTargetNow(x As Double, y As Double, hasPoint As Boolean) As BrowserUploadTarget
            Dim windows = VisibleWindows()
            Dim win As Window = Nothing
            Dim local As Point? = Nothing
            If hasPoint Then
                Dim found As Point
                win = PointerTarget(x, y, found)
                If win IsNot Nothing Then local = found
            End If
            If win Is Nothing Then win = windows.FirstOrDefault(Function(w) w.Owner Is Nothing)
            Dim resolver = UploadTargetResolver
            Dim result = If(resolver Is Nothing, Nothing, resolver(win, local))
            If result Is Nothing Then
                Return BrowserUploadTarget.Refused(LocalizationService.T("Kein Ordner offen, in den die Dateien gehören"))
            End If
            Return result
        End Function

        Private Shared Function FreeSpace(folder As String) As Long
            Try
                Return New DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace
            Catch
                ' Ohne Auskunft des Laufwerks wird nicht abgewiesen; ein volles Laufwerk meldet sich
                ' dann beim Schreiben, und die Teildatei wird geloescht.
                Return Long.MaxValue \ 2
            End Try
        End Function

        Private Shared Function ReadQueryNumber(request As HttpListenerRequest, name As String) As Double
            Dim value As Double
            If Double.TryParse(request.QueryString(name), NumberStyles.Float, CultureInfo.InvariantCulture, value) AndAlso
               Not Double.IsInfinity(value) Then
                Return value
            End If
            Return Double.NaN
        End Function

        Private Shared Sub TryDelete(path As String)
            Try
                If File.Exists(path) Then File.Delete(path)
            Catch
            End Try
        End Sub

        Private Shared Async Function WriteResultAsync(context As HttpListenerContext, status As Integer,
                                                       storedName As String, problem As String) As Task
            Try
                Dim result As New Dictionary(Of String, Object) From {{"ok", status = 200}}
                If storedName IsNot Nothing Then result("name") = storedName
                If problem IsNot Nothing Then result("error") = problem
                Dim body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result))
                context.Response.StatusCode = status
                context.Response.ContentType = "application/json; charset=utf-8"
                context.Response.Headers("Cache-Control") = "no-store"
                context.Response.ContentLength64 = body.Length
                Await context.Response.OutputStream.WriteAsync(body)
                context.Response.Close()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                ' Der Browser hat die Anfrage schon aufgegeben.
                Try
                    context.Response.Abort()
                Catch
                End Try
            End Try
        End Function

        ' --- Herunterladen ----------------------------------------------------------------------

        ''' <summary>Bietet Dateien zum Herunterladen an, sofern die Anwendung im Browser laeuft und
        ''' ein Browser verbunden ist. Eine Datei geht, wie sie ist; mehrere gehen als EIN ZIP samt
        ''' ihrer Beistelldateien. Mit <paramref name="startNow"/> beginnt die Seite gleich (der
        ''' Weg ueber das Kontextmenue, das hat der Nutzer ja eben gewaehlt), sonst zeigt sie einen
        ''' Knopf (nach dem Exportieren). Liefert die Kennung des Angebots oder Nothing.</summary>
        Public Shared Function OfferDownload(paths As IEnumerable(Of String), Optional startNow As Boolean = False) As String
            If _listener Is Nothing OrElse paths Is Nothing Then Return Nothing
            Dim files = paths.Where(Function(p) Not String.IsNullOrEmpty(p)).
                              Select(Function(p) VariantKey.FileOf(p)).
                              Where(Function(p) File.Exists(p) AndAlso FileOperationPolicy.CanDownload(p)).
                              Distinct(PathIdentity.Comparer).ToList()
            Return RegisterOffer(files, startNow, Nothing)
        End Function

        ''' <summary>Legt das Angebot an und schickt es der Seite. Abgelaufene und verdraengte
        ''' Angebote fallen dabei weg, samt ihrem eigenen Ordner.</summary>
        Private Shared Function RegisterOffer(files As List(Of String), startNow As Boolean, ownedFolder As String,
                                              Optional folder As String = Nothing) As String
            If files Is Nothing OrElse (files.Count = 0 AndAlso folder Is Nothing) Then Return Nothing
            Dim offer As New DownloadOffer With {
                .Paths = files,
                .Created = DateTime.UtcNow,
                .OwnedFolder = ownedFolder,
                .Folder = folder,
                .FileName = If(folder IsNot Nothing, FolderDisplayName(folder) & ".zip",
                            If(files.Count = 1, Path.GetFileName(files(0)),
                               $"FerrumPix_{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{files.Count.ToString(CultureInfo.InvariantCulture)}.zip"))}
            Dim id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
            Dim dropped As New List(Of String)()
            SyncLock Offers
                For Each stale In Offers.Where(Function(o) DateTime.UtcNow - o.Value.Created > OfferLifetime).Select(Function(o) o.Key).ToList()
                    dropped.Add(RetireOffer(Offers(stale)))
                    Offers.Remove(stale)
                Next
                While Offers.Count >= MaxOffers
                    Dim oldest = Offers.OrderBy(Function(o) o.Value.Created).First().Key
                    dropped.Add(RetireOffer(Offers(oldest)))
                    Offers.Remove(oldest)
                End While
                Offers(id) = offer
            End SyncLock
            For Each folder In dropped
                DeleteDownloadFolder(folder)
            Next
            If ownedFolder IsNot Nothing Then EnsureOfferSweep()

            Dim message As New Dictionary(Of String, Object) From {
                {"t", "offer"}, {"id", id}, {"name", offer.FileName}, {"count", files.Count}, {"start", startNow}}
            Dim ignored = SendTextAsync(JsonSerializer.Serialize(message))
            Return id
        End Function

        ''' <summary>Liefert ein Angebot aus. Ohne Anmeldung, ohne gueltige Kennung der Seite und ohne
        ''' bekanntes Angebot gibt es nichts; einen Pfad nimmt diese Anfrage nicht an.</summary>
        Private Shared Async Function HandleDownloadAsync(context As HttpListenerContext) As Task
            Dim request = context.Request
            If Not IsSignedIn(context) Then
                context.Response.StatusCode = 401
                context.Response.Close()
                Return
            End If
            ' Nur fuer Administratoren, wie die volle Oberflaeche, die das Angebot gemacht hat.
            If Not IsSignedInForDesktop(context) Then
                context.Response.StatusCode = 403
                context.Response.Close()
                Return
            End If
            If Not String.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) Then
                context.Response.StatusCode = 405
                context.Response.Close()
                Return
            End If
            If Not IsKnownToken(request.QueryString("token")) Then
                context.Response.StatusCode = 403
                context.Response.Close()
                Return
            End If
            Dim offer As DownloadOffer = Nothing
            Dim id = If(request.QueryString("id"), "")
            SyncLock Offers
                If Not Offers.TryGetValue(id, offer) OrElse DateTime.UtcNow - offer.Created > OfferLifetime Then offer = Nothing
                ' Ab hier laeuft eine Auslieferung; der Ordner bleibt bis zu ihrem Ende
                ' (EndDelivery), auch wenn das Angebot unterdessen geht.
                If offer IsNot Nothing Then offer.ActiveDeliveries += 1
            End SyncLock
            If offer Is Nothing Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If

            Dim response = context.Response
            Dim delivered = False
            Try
                response.Headers("Cache-Control") = "no-store"
                response.Headers("Content-Disposition") = ContentDisposition(offer.FileName)
                If offer.Folder Is Nothing AndAlso offer.Paths.Count = 1 Then
                    Dim source = offer.Paths(0)
                    Using stream As New FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous Or FileOptions.SequentialScan)
                        response.ContentType = "application/octet-stream"
                        response.ContentLength64 = stream.Length
                        Await stream.CopyToAsync(response.OutputStream, 1024 * 1024)
                    End Using
                Else
                    ' Ein Ordner wird erst jetzt gelesen, im Hintergrund: so, wie er beim Abruf ist.
                    Dim folder = offer.Folder
                    Dim entries = If(folder IsNot Nothing,
                                     Await Task.Run(Function() PlanFolderDownloadEntries(folder)),
                                     PlanDownloadEntries(offer.Paths))
                    response.ContentType = "application/zip"
                    response.ContentLength64 = StreamingZipWriter.TotalLength(entries)
                    Await StreamingZipWriter.WriteAsync(response.OutputStream, entries)
                End If
                response.Close()
                delivered = True
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                       TypeOf ex Is InvalidOperationException
                ' Abgebrochen, oder eine Datei ist inzwischen weg oder anders gross. Abbrechen statt
                ' schliessen: der Browser soll eine unvollstaendige Datei nicht fuer fertig halten.
                Try
                    response.Abort()
                Catch
                End Try
            Finally
                EndDelivery(offer)
            End Try
            If delivered Then
                ' Was fuer das Ziel "Herunterladen" entstand, liegt nur fuer diesen Download im
                ' Datenordner: einmal vollstaendig ausgeliefert, geht es samt Angebot. Ein
                ' abgebrochener Download laesst beides stehen, der Knopf der Seite holt es noch einmal.
                ' Ein Download ueber einen Verweis meldet der Seite kein Ende; das tut die Bruecke,
                ' damit der Eintrag verschwindet, sobald die Datei ganz hinausgegangen ist. Laeuft
                ' noch ein zweiter Abruf desselben Angebots, nimmt erst er den Ordner mit.
                If offer.OwnedFolder IsNot Nothing Then
                    ReleaseOwnedOffer(id)
                Else
                    NotifyOfferState("delivered", id)
                End If
            Else
                ' Die Seite zeigt den Eintrag dann wieder, mit dem Grund und dem Knopf fuer einen
                ' neuen Versuch.
                NotifyOfferState("interrupted", id)
            End If
        End Function

        ''' <summary>Nimmt ein Angebot aus dem Verkehr und liefert seinen Ordner, wenn er jetzt
        ''' geloescht werden darf: nur ohne laufende Auslieferung, sonst nimmt ihn deren Ende mit
        ''' (<see cref="EndDelivery"/>). Nur unter der Sperre von Offers rufen; geloescht wird
        ''' danach, ausserhalb.</summary>
        Private Shared Function RetireOffer(offer As DownloadOffer) As String
            offer.Retired = True
            Return If(offer.ActiveDeliveries = 0, offer.OwnedFolder, Nothing)
        End Function

        ''' <summary>Eine Auslieferung ist zu Ende, gelungen oder nicht. War das Angebot inzwischen
        ''' aus dem Verkehr und war sie die letzte, geht jetzt sein Ordner.</summary>
        Private Shared Sub EndDelivery(offer As DownloadOffer)
            Dim folder As String = Nothing
            SyncLock Offers
                offer.ActiveDeliveries -= 1
                If offer.Retired AndAlso offer.ActiveDeliveries = 0 Then folder = offer.OwnedFolder
            End SyncLock
            DeleteDownloadFolder(folder)
        End Sub

        ''' <summary>Sagt der Seite, wie ein Download ausging: "delivered" (ganz hinaus),
        ''' "interrupted" (abgebrochen) oder "gone" (das Angebot gilt nicht mehr).</summary>
        Private Shared Sub NotifyOfferState(state As String, id As String)
            Dim message As New Dictionary(Of String, Object) From {{"t", state}, {"id", id}}
            Dim ignored = SendTextAsync(JsonSerializer.Serialize(message))
        End Sub

        ''' <summary>Die Eintraege des ZIP: jede Datei unter ihrem Namen, gleiche Namen aus
        ''' verschiedenen Ordnern mit " (1)", " (2)", und hinter jeder ihre Beistelldateien (.xmp und
        ''' .fpxmp), die einem umbenannten Bild im Namen folgen. Ohne <paramref name="includeSidecars"/>
        ''' (mobile Seite) nur die Dateien selbst; die Desktop-Seite nimmt sie mit.</summary>
        Friend Shared Function PlanDownloadEntries(paths As IList(Of String), Optional includeSidecars As Boolean = True) As List(Of ZipEntryPlan)
            Dim result As New List(Of ZipEntryPlan)()
            Dim usedNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim seenSources As New HashSet(Of String)(PathIdentity.Comparer)
            For Each source In paths
                If Not File.Exists(source) OrElse Not seenSources.Add(source) Then Continue For
                Dim original = Path.GetFileName(source)
                Dim entryName = UniqueEntryName(original, usedNames)
                result.Add(StreamingZipWriter.PlanFile(source, entryName))
                If Not includeSidecars Then Continue For
                For Each sidecar In SidecarsOf(source)
                    If Not seenSources.Add(sidecar) Then Continue For
                    Dim sidecarName = Path.GetFileName(sidecar)
                    If Not String.Equals(entryName, original, StringComparison.Ordinal) Then
                        sidecarName = If(SidecarNameAlong(sidecarName, original, entryName), sidecarName)
                    End If
                    result.Add(StreamingZipWriter.PlanFile(sidecar, UniqueEntryName(sidecarName, usedNames)))
                Next
            Next
            Return result
        End Function

        Private Shared Function SidecarsOf(imagePath As String) As List(Of String)
            Dim result As New List(Of String)()
            Dim xmp = XmpSidecarService.FindSidecar(imagePath)
            If Not String.IsNullOrEmpty(xmp) AndAlso File.Exists(xmp) Then result.Add(xmp)
            Dim recipe = RawSidecarService.SidecarPathFor(imagePath)
            If Not String.IsNullOrEmpty(recipe) AndAlso File.Exists(recipe) Then result.Add(recipe)
            Return result
        End Function

        ''' <summary>Gross- und Kleinschreibung zaehlen nicht: entpackt wird oft auf einem
        ''' Dateisystem, das sie nicht unterscheidet.</summary>
        Private Shared Function UniqueEntryName(fileName As String, used As HashSet(Of String)) As String
            If used.Add(fileName) Then Return fileName
            Dim stem = Path.GetFileNameWithoutExtension(fileName)
            Dim extension = Path.GetExtension(fileName)
            Dim number = 1
            Do
                Dim candidate = $"{stem} ({number}){extension}"
                If used.Add(candidate) Then Return candidate
                number += 1
            Loop
        End Function

        ''' <summary>Der Kopf, der den Browser speichern laesst statt anzeigen: der Name einmal in
        ''' ASCII fuer alte Browser und einmal vollstaendig nach RFC 5987.</summary>
        Private Shared Function ContentDisposition(fileName As String) As String
            Dim ascii As New StringBuilder(fileName.Length)
            For Each character In fileName
                ascii.Append(If(character < " "c OrElse character > "~"c OrElse character = """"c OrElse character = "\"c, "_"c, character))
            Next
            Return $"attachment; filename=""{ascii}""; filename*=UTF-8''{Uri.EscapeDataString(fileName)}"
        End Function

        ''' <summary>Vergisst Angebote und Ablegestapel. Fuer den Pruefstand, beim Anhalten.</summary>
        Private Shared Sub ResetFileTransfers()
            Dim owned As List(Of String)
            SyncLock Offers
                owned = Offers.Values.Select(Function(o) RetireOffer(o)).Where(Function(f) f IsNot Nothing).ToList()
                Offers.Clear()
            End SyncLock
            For Each folder In owned
                DeleteDownloadFolder(folder)
            Next
            StopOfferSweep()
            SyncLock Batches
                Batches.Clear()
                BatchOrder.Clear()
            End SyncLock
            UploadTargetResolver = Nothing
            UploadStored = Nothing
        End Sub
    End Class
End Namespace
