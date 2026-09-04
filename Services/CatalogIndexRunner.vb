Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Was ein Durchlauf des Katalogindex gebracht hat.</summary>
    Public Class CatalogIndexResult
        ''' <summary>Wie viele Dateien gelesen wurden.</summary>
        Public Property Indexed As Integer
        ''' <summary>Wie viele uebersprungen wurden, weil sie sich seit dem letzten Lauf nicht
        ''' geaendert haben.</summary>
        Public Property Unchanged As Integer
        ''' <summary>Wie viele Vorschaubilder neu entstanden sind.</summary>
        Public Property ThumbnailsCreated As Integer
        ''' <summary>Wie viele Dateien sich nicht lesen liessen.</summary>
        Public Property Failed As Integer
        ''' <summary>Wie viele Katalogeintraege einen Ortsnamen bekommen haben.</summary>
        Public Property PlacesResolved As Integer
        ''' <summary>Wie viele Bilder in diesem Durchlauf tatsächlich eine lokale KI-Analyse
        ''' erhielten. Auch ein leeres Ergebnis zählt: es verhindert, dass ein Motiv ohne Treffer
        ''' bei jedem nächsten Index erneut gerechnet wird.</summary>
        Public Property AiTagged As Integer

        ''' <summary>Wie viele Katalogeintraege unter den durchsuchten Ordnern auf eine Datei zeigen,
        ''' die es nicht mehr gibt. NUR GEZAEHLT: geloescht wird auf Ansage ueber "Datenbank
        ''' bereinigen". Eine Bewertung und ein Stichwort sind Handarbeit, und ein Ordner kann auch
        ''' nur voruebergehend fehlen - eine nicht eingehaengte Platte saehe fuer den Lauf aus wie
        ''' tausend geloeschte Fotos.</summary>
        Public Property Orphaned As Integer

        Public Property Cancelled As Boolean

        ''' <summary>Der Lauf hat gar nicht erst begonnen, weil schon einer laeuft.
        '''
        ''' Muss vom leeren Ergebnis unterscheidbar sein: der Anrufer haelt sonst seinen
        ''' Anzeigezustand fuer beendet und setzt ihn zurueck, waehrend der ERSTE Lauf weiterarbeitet
        ''' - Stopp-Knopf und Fortschritt verschwinden dann mitten im Lauf. Der Fall tritt auf, wenn
        ''' der Start nach dem Programmstart und ein Klick auf "Starten" sich ueberholen.</summary>
        Public Property NotStarted As Boolean

        ''' <summary>Nicht begonnen, weil ein ANDERES FENSTER gerade schreibt (siehe
        ''' BackgroundRunLock). Getrennt von <see cref="NotStarted"/> gemeldet, weil der Nutzer
        ''' etwas anderes tun muss: im eigenen Fenster warten hilft nicht, wenn die Arbeit anderswo
        ''' laeuft.</summary>
        Public Property BlockedByOtherWindow As Boolean
    End Class

    ''' <summary>Wie weit der Lauf ist. Der Gesamtstand steht erst, wenn die Ordner durchgezaehlt
    ''' sind - bis dahin ist <see cref="Total"/> null und die Anzeige sagt "wird gesucht".</summary>
    Public Structure CatalogIndexProgress
        Public Property Done As Integer
        Public Property Total As Integer
        ''' <summary>Der Ordner, in dem der Lauf gerade steckt. Fuer die Anzeige - ein Dateiname je
        ''' Bild flackerte bei tausend Dateien nur.</summary>
        Public Property CurrentFolder As String
    End Structure

    ''' <summary>
    ''' Geht die eingetragenen Ordner durch und legt fuer jedes Bild einen Katalogeintrag und ein
    ''' Vorschaubild an. Damit sind Suche, Filter und Galerie sofort schnell, ohne dass jemand jeden
    ''' Ordner einmal besucht haben muss - bisher entstand beides erst beim Ansehen (siehe
    ''' GalleryViewModel.QueueBackgroundMetaRefresh).
    '''
    ''' <para>NUR EIN LAUF, und er ist abbrechbar. Wie beim Personenlauf (siehe FaceScanRunner):
    ''' zwei Laeufe waeren nicht nur doppelte Arbeit, sie schrieben auch gleichzeitig in denselben
    ''' Katalog und denselben Vorschau-Ordner.</para>
    '''
    ''' <para>EINFAEDIG, mit Absicht. Der Ordnerlauf der Galerie nimmt die halbe Kernzahl, weil dort
    ''' jemand auf seine Kacheln wartet. Hier wartet niemand: der Lauf soll im Hintergrund
    ''' durchsickern und nicht die Maschine belegen, waehrend nebenher gearbeitet wird. Das
    ''' Dekodieren geht ohnehin durch die Schleuse, in der immer nur EINER laeuft
    ''' (siehe <see cref="DecodeGate"/>).</para>
    '''
    ''' <para>ER LEGT KEINE DATEIEN NEBEN DEN FOTOS AN. Beistelldateien werden gelesen, nicht
    ''' geschrieben - im Gegensatz zum Ordnerlauf der Galerie, der eine XMP-Beistelldatei mit
    ''' Entwicklungseinstellungen einmalig in eine .fpxmp uebersetzt. Beim Ansehen EINES Ordners ist
    ''' das eine bewusste Handlung; ueber den ganzen Bestand hinweg entstuenden auf einen Schlag
    ''' Tausende neuer Dateien im Fotobestand, ohne dass jemand danach gefragt hat.</para>
    '''
    ''' <para>GESICHTER SUCHT ER NICHT. Das ist ein eigener Lauf mit eigenem Schalter und eigener
    ''' Bedienung, und er kostet ein Vielfaches - gemessen rund 135 ms je Gesicht gegen wenige
    ''' Millisekunden fuer einen Katalogeintrag.</para>
    ''' </summary>
    Public NotInheritable Class CatalogIndexRunner

        Private Sub New()
        End Sub

        ' LAUFZUSTAND. Wie beim Personenlauf oeffentlich anhaltbar: der Benutzer kann in den
        ' Einstellungen und in der Galerie stoppen, und wer die Katalogdaten wegwirft, darf keinen
        ' Lauf danebenlaufen lassen, der sie gleich wieder hineinschreibt.
        Private Shared ReadOnly _runLock As New Object()
        Private Shared _stopSource As CancellationTokenSource
        Private Shared _running As Boolean

        ''' <summary>Laeuft gerade einer?</summary>
        Public Shared ReadOnly Property IsRunning As Boolean
            Get
                SyncLock _runLock
                    Return _running
                End SyncLock
            End Get
        End Property

        ''' <summary>Bittet einen laufenden Durchlauf aufzuhoeren. Kehrt sofort zurueck; was bis
        ''' dahin im Katalog steht, bleibt stehen - der naechste Lauf macht dort weiter, weil er
        ''' ohnehin nur Geaendertes anfasst.</summary>
        Public Shared Sub RequestCancel()
            SyncLock _runLock
                ' Innerhalb der Sperre: draussen koennte die Quelle zwischen Lesen und Cancel
                ' bereits freigegeben sein.
                If _stopSource IsNot Nothing Then
                    Try
                        _stopSource.Cancel()
                    Catch ex As ObjectDisposedException
                        ' Der Lauf war in derselben Sekunde von selbst fertig.
                    End Try
                End If
            End SyncLock
        End Sub

        ''' <summary>Bittet um Abbruch und wartet, bis der Durchlauf wirklich steht. Fuer jeden, der
        ''' die Katalogdaten leeren will - geschieht das mitten im Lauf, sind sie hinterher nicht
        ''' leer, sondern halb gefuellt.</summary>
        ''' <returns>True, wenn nichts mehr laeuft.</returns>
        Public Shared Function RequestStopAndWait(Optional timeoutMilliseconds As Integer = 10000) As Boolean
            RequestCancel()
            Dim waited = 0
            While IsRunning AndAlso waited < timeoutMilliseconds
                Thread.Sleep(50)
                waited += 50
            End While
            Return Not IsRunning
        End Function

        ''' <summary>Die eingetragenen Ordner aus den Einstellungen, aufgeraeumt und ohne die, die
        ''' es nicht mehr gibt. Ein geloeschter oder ausgehaengter Ordner ist kein Fehler - er soll
        ''' nur nicht mitgezaehlt werden.</summary>
        Public Shared Function ConfiguredFolders() As List(Of String)
            Return AppSettingsService.NormalizeCatalogWatchFolders(AppSettingsService.Load().CatalogWatchFolders).
                Where(AddressOf Directory.Exists).
                ToList()
        End Function

        ''' <summary>Die Bilddateien der uebergebenen Ordner, samt Unterordnern.
        '''
        ''' Oeffentlich, weil die Gesichtssuche ueber DIESELBEN ueberwachten Ordner laeuft und
        ''' dieselbe Liste braucht (siehe FaceIndexViewModel). Zwei Fassungen davon liefen
        ''' auseinander, sobald eine von beiden eine Endung oder eine ausgelassene Ordnerart anders
        ''' behandelt.</summary>
        ''' <param name="folders">Nothing heisst: die aus den Einstellungen.</param>
        Public Shared Function CollectImageFiles(Optional folders As IReadOnlyList(Of String) = Nothing,
                                                 Optional token As CancellationToken = Nothing) As List(Of String)
            ' AUFGERAEUMT, und zwar HIER: jeder Aufrufer koennte sonst eine Wurzel samt ihrer
            ' Unterordner uebergeben, und weil rekursiv gesammelt wird, liefe der Baum dann mehrfach
            ' durch. In der Zielliste stuende ein Vielfaches, der Fortschritt zaehlte zu hoch, und
            ' jede Datei wuerde mehrfach geprueft. NormalizeCatalogWatchFolders wirft enthaltene
            ' Ordner und Doppelte weg - dieselbe Regel wie fuer die eingetragenen Ordner.
            Dim wanted = If(folders Is Nothing, ConfiguredFolders(),
                            AppSettingsService.NormalizeCatalogWatchFolders(folders.ToList()))
            Dim roots = wanted.Where(AddressOf Directory.Exists).ToList()
            Return CollectFromRoots(roots, token)
        End Function

        ''' <summary>Sammelt ueber ALLE Wurzeln, mit EINER Besuchsliste.
        '''
        ''' Die Liste gehoert dem ganzen Durchlauf und nicht der einzelnen Wurzel: zwei eingetragene
        ''' Ordner koennen ueber Verweise auf denselben Bestand zeigen, und
        ''' NormalizeCatalogWatchFolders sieht das nicht - sie vergleicht die PFADE, und zwei
        ''' verschiedene Pfade auf dasselbe Ziel sind fuer sie zwei Ordner. Je Wurzel eine eigene
        ''' Liste haette den Ring innerhalb einer Wurzel gebrochen und den Bestand trotzdem doppelt
        ''' eingelesen.</summary>
        Private Shared Function CollectFromRoots(roots As IEnumerable(Of String),
                                                 token As CancellationToken) As List(Of String)
            Dim files As New List(Of String)()
            Dim visited As New HashSet(Of String)(PathIdentity.Comparer)
            For Each root In If(roots, Enumerable.Empty(Of String)())
                If token.IsCancellationRequested Then Exit For
                CollectFiles(root, files, token, visited, 0)
            Next
            Return files
        End Function

        ''' <summary>Laeuft ueber die Ordner und meldet nach jeder Datei, wie weit er ist.</summary>
        ''' <param name="folders">Die Wurzeln, jede samt Unterordnern. Nothing heisst: die aus den
        ''' Einstellungen.</param>
        ''' <param name="force">Auch Dateien lesen, die sich seit dem letzten Lauf nicht geaendert
        ''' haben. Sonst wird nur Geaendertes angefasst.</param>
        Public Shared Async Function RunAsync(Optional folders As IReadOnlyList(Of String) = Nothing,
                                              Optional progress As IProgress(Of CatalogIndexProgress) = Nothing,
                                              Optional token As CancellationToken = Nothing,
                                              Optional force As Boolean = False) As Task(Of CatalogIndexResult)
            Dim result As New CatalogIndexResult()
            Dim roots = If(folders Is Nothing, ConfiguredFolders(),
                           folders.Where(Function(f) Not String.IsNullOrWhiteSpace(f) AndAlso Directory.Exists(f)).ToList())
            If roots.Count = 0 Then Return result

            ' HOECHSTENS EIN DURCHLAUF. Wer abgewiesen wird, bekommt das GEMELDET und nicht nur ein
            ' leeres Ergebnis - sonst haelt er seinen Anzeigezustand fuer beendet und setzt ihn
            ' zurueck, waehrend der erste Lauf weiterarbeitet.
            Dim stopSource As CancellationTokenSource = Nothing
            SyncLock _runLock
                If _running Then
                    result.NotStarted = True
                    Return result
                End If
                stopSource = New CancellationTokenSource()
                _stopSource = stopSource
                _running = True
            End SyncLock

            ' NUR EIN FENSTER SCHREIBT. Zwei Anwendungen nebeneinander sind erlaubt und sinnvoll,
            ' zwei Schreiblaeufe nicht: sie ueberschreiben sich das Ordnerverzeichnis des
            ' Vorschau-Zwischenspeichers (siehe BackgroundRunLock).
            Dim crossProcess = BackgroundRunLock.TryAcquire()
            If crossProcess Is Nothing Then
                SyncLock _runLock
                    _running = False
                    _stopSource = Nothing
                    stopSource.Dispose()
                End SyncLock
                result.NotStarted = True
                result.BlockedByOtherWindow = True
                Return result
            End If

            Try
                ' Der eigene Abbruchweg und der des Aufrufers zusammengefuehrt: aufgehoert wird,
                ' sobald EINER von beiden es verlangt.
                Using linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopSource.Token)
                    Dim runToken = linked.Token
                    Dim filesSeen = Await Task.Run(Function() IndexLoop(roots, result, progress, runToken, force), runToken).ConfigureAwait(False)

                    ' Die Ortsnamen NACH dem Lauf und in einem Rutsch: FillMissingPlaces liest die
                    ' offenen Koordinaten in einer Abfrage und schreibt sie in einer Transaktion
                    ' zurueck. Je Bild einzeln waeren das zwei Plattenzugriffe pro Foto.
                    If Not result.Cancelled Then
                        result.PlacesResolved = Await Task.Run(Function() LibraryService.Instance.FillMissingPlaces(),
                                                               runToken).ConfigureAwait(False)

                        ' Was ins Leere zeigt, wird GEZAEHLT und gesagt - nicht weggeraeumt (siehe
                        ' CatalogIndexResult.Orphaned). Nur nach einem vollstaendigen Durchlauf:
                        ' nach einem Abbruch fehlt die halbe Wahrheit, und eine Zahl, die zu hoch
                        ' ist, waere schlimmer als keine.
                        result.Orphaned = Await Task.Run(Function() LibraryService.Instance.CountOrphanedRecordsUnder(roots, filesSeen),
                                                         runToken).ConfigureAwait(False)
                    End If
                End Using
            Catch ex As OperationCanceledException
                result.Cancelled = True
            Catch ex As Exception
                DiagnosticLogService.LogException("Katalogindex.Durchlauf", ex)
            Finally
                crossProcess.Dispose()
                SyncLock _runLock
                    _running = False
                    _stopSource = Nothing
                    stopSource.Dispose()
                End SyncLock
            End Try

            DiagnosticLogService.LogAlways("Katalogindex.Durchlauf",
                $"{result.Indexed} indiziert, {result.Unchanged} unveraendert, " &
                $"{result.ThumbnailsCreated} Vorschaubilder, {result.Failed} fehlgeschlagen, " &
                $"{result.PlacesResolved} Orte, {result.Orphaned} ohne Datei" &
                If(result.Cancelled, ", abgebrochen", ""))
            Return result
        End Function

        ''' <summary>Der Gang ueber die Ordner. Eigene Methode und keine lange Lambda, damit der
        ''' Rahmen darueber - ein Lauf, Abbruch, Orte nachtragen - auf einen Blick lesbar bleibt.</summary>
        Private Shared Function IndexLoop(roots As IReadOnlyList(Of String),
                                     result As CatalogIndexResult,
                                     progress As IProgress(Of CatalogIndexProgress),
                                     token As CancellationToken,
                                     force As Boolean) As ISet(Of String)
            ' ZUERST ZAEHLEN, dann arbeiten. Ohne Gesamtzahl gaebe es keinen Fortschritt, sondern nur
            ' eine Zahl, die hochlaeuft - und bei einem grossen Bestand weiss dann niemand, ob noch
            ' zehn Bilder kommen oder zehntausend. Das Aufzaehlen selbst ist billig gegen das Lesen.
            If token.IsCancellationRequested Then
                result.Cancelled = True
                Return Nothing
            End If
            Dim files = CollectFromRoots(roots, token)
            If token.IsCancellationRequested Then
                result.Cancelled = True
                Return Nothing
            End If
            If files.Count = 0 Then Return New HashSet(Of String)(PathIdentity.Comparer)

            ' Die Stempel je WURZEL in einer Abfrage, nicht je Datei. Eine Abfrage pro Bild waere
            ' bei zehntausend Fotos zehntausend Plattenzugriffe, bevor ueberhaupt etwas geschieht.
            Dim stamps As New Dictionary(Of String, CatalogIndexStamp)(PathIdentity.Comparer)
            If Not force Then
                For Each root In roots
                    For Each entry In LibraryService.Instance.GetIndexStamps(root)
                        stamps(entry.Key) = entry.Value
                    Next
                Next
            End If

            Dim summaryFormat = ExifService.CurrentSummaryFormat
            Dim lastFolder = ""
            Dim done = 0
            ' Nicht die rohe Directory-Aufzählung zurückgeben: zwischen Aufzählen und dem
            ' Bearbeiten kann eine Datei verschwinden. Nur ein erfolgreich bestätigtes Exists
            ' darf den späteren Orphan-Check ohne erneuten Dateisystemzugriff überspringen.
            Dim filesSeen As New HashSet(Of String)(PathIdentity.Comparer)
            For Each filePath In files
                If token.IsCancellationRequested Then
                    result.Cancelled = True
                    Return Nothing
                End If
                done += 1

                Try
                    Dim info As FileInfo
                    Try
                        info = New FileInfo(filePath)
                        If Not info.Exists Then
                            result.Failed += 1
                            Continue For
                        End If
                        filesSeen.Add(filePath)
                    Catch ex As Exception
                        ' Zwischen Aufzaehlen und Bearbeiten kann die Datei weg sein, und ein Pfad
                        ' kann fuer das Dateisystem zu lang oder gesperrt sein.
                        result.Failed += 1
                        Continue For
                    End Try

                    lastFolder = If(Path.GetDirectoryName(filePath), "")

                    ' UNVERAENDERT? Dann gar nicht erst lesen. Genau die drei Angaben, die auch
                    ' SyncExifData vergleicht - nur eben VOR dem teuren Teil. Ohne das liefe ein
                    ' zweiter Lauf ueber denselben Bestand genauso lange wie der erste.
                    If Not force AndAlso IsUnchanged(stamps, filePath, info, summaryFormat) Then
                        result.Unchanged += 1
                        ' Das Vorschaubild trotzdem sicherstellen: der Katalogeintrag kann von einem
                        ' frueheren Lauf stammen, waehrend die Kachel nie gebraucht wurde.
                        If EnsureThumbnail(filePath, info, token) Then result.ThumbnailsCreated += 1
                        TagIfNeeded(filePath, info, result, token)
                        Continue For
                    End If

                    IndexOne(filePath, result, token)
                    TagIfNeeded(filePath, info, result, token)
                Catch ex As OperationCanceledException
                    result.Cancelled = True
                    Return Nothing
                Catch ex As Exception
                    DiagnosticLogService.LogException("Katalogindex.Datei", ex)
                    result.Failed += 1
                Finally
                    progress?.Report(New CatalogIndexProgress With {
                        .Done = done, .Total = files.Count, .CurrentFolder = lastFolder})
                End Try
            Next
            Return filesSeen
        End Function

        ''' <summary>Eine Datei in den Katalog und ihre Kachel in den Zwischenspeicher.</summary>
        Private Shared Sub IndexOne(filePath As String, result As CatalogIndexResult, token As CancellationToken)
            Dim info = New FileInfo(filePath)

            ' Aufnahmedaten lesen und in den Katalog. Genau der Weg, den auch der Ordnerlauf der
            ' Galerie nimmt - dieselben Felder, dieselbe Zusammenfassung, derselbe Stempel.
            Dim data = ExifService.ReadExif(filePath)
            Dim fields = ExifService.ExtractSearchFields(data, filePath)
            Dim summary = ExifService.BuildCatalogSummary(data, fields)
            LibraryService.Instance.SyncExifData(filePath, fields, summary)

            ' Was in einer .fpxmp neben dem Bild steht, gehoert in den Katalog - das ist die
            ' portable Quelle fuer Bewertung, Etikett und Stichwoerter bei RAW und PSD. NUR LESEN:
            ' ImportFpxmpCatalogData legt keine an, im Unterschied zum Weg der Galerie, der eine
            ' XMP-Beistelldatei zusaetzlich in eine .fpxmp uebersetzt.
            LibraryService.Instance.ImportFpxmpCatalogData(filePath)

            ' Und die Stichwoerter aus der Beistelldatei UND aus der Bilddatei selbst - derselbe Weg,
            ' den auch der Ordnerlauf der Galerie nimmt. Ohne ihn faende die Suche sie erst, wenn
            ' jemand den Ordner einmal angesehen hat, bei einem Archiv auf einem Netzlaufwerk also
            ' nie; und eine geaenderte .xmp loeste hier zwar einen neuen Durchlauf aus (sie steht im
            ' Frische-Stempel), der ihre Stichwoerter dann nicht ansah.
            ' Der Import legt NICHTS neben dem Foto an; das ist die Bedingung, unter der er hier
            ' ueberhaupt stehen darf (siehe LibraryService.ImportFileKeywords).
            KeywordImportService.Import(filePath, data?.EmbeddedKeywords)

            Dim rating = ExifService.GetXmpRating(data)
            If rating.HasValue AndAlso rating.Value > 0 AndAlso
               LibraryService.Instance.GetRating(filePath) = 0 Then
                LibraryService.Instance.SetRating(filePath, rating.Value)
            End If

            result.Indexed += 1
            If EnsureThumbnail(filePath, info, token) Then result.ThumbnailsCreated += 1
        End Sub

        ''' <summary>Die KI ist ein eigener, bewusst aktivierter Schritt des Katalogs. Er steht
        ''' NACH Metadaten und Thumbnail, damit ein fehlendes Modell oder ein einzelner Modellfehler
        ''' nie die normale Indizierung verhindert. Bei unveränderten Bildern fragt er seinen
        ''' eigenen Stempel ab - erst so wird ein bereits vorhandener Bestand nach Aktivierung der
        ''' Einstellung einmal vollständig ergänzt.</summary>
        Private Shared Sub TagIfNeeded(filePath As String, info As FileInfo, result As CatalogIndexResult,
                                       token As CancellationToken)
            If token.IsCancellationRequested OrElse Not ImageTaggingService.NeedsAnalysis(filePath, info.LastWriteTime) Then Return
            ' NOTHING heisst "gar nicht gelaufen" (siehe ImageTaggingService.TagFile). Das mitzu-
            ' zaehlen meldete am Ende Bilder als verschlagwortet, an denen nichts geschehen ist.
            If ImageTaggingService.TagFile(filePath, token) IsNot Nothing Then result.AiTagged += 1
        End Sub

        ''' <summary>Die Kachel bereitlegen. DURCH DIE DECODE-SCHLEUSE: es laeuft immer nur einer in
        ''' der ganzen Anwendung, sonst stuende hier ein Decode neben dem Bild im Betrachter und
        ''' neben den Kacheln der Galerie (siehe <see cref="DecodeGate"/>).</summary>
        ''' <returns>True, wenn eine neue Kachel entstanden ist.</returns>
        Private Shared Function EnsureThumbnail(filePath As String, info As FileInfo, token As CancellationToken) As Boolean
            Try
                Dim outcome = DecodeGate.Run(Function() ThumbnailCacheService.EnsureCached(
                                                 filePath, info.LastWriteTime, info.Length, token))
                Return outcome = ThumbnailCacheService.ThumbnailCacheOutcome.Written
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Katalogindex.Vorschaubild", ex)
                Return False
            End Try
        End Function

        ''' <summary>Hat sich seit dem letzten Lauf nichts geaendert? Verglichen werden Bilddatei,
        ''' Beistelldateien und das Format der gespeicherten Zusammenfassungen - Letzteres, weil ein
        ''' Eintrag aus einer aelteren Fassung oder einer anderen Anzeigesprache stammen kann.</summary>
        Private Shared Function IsUnchanged(stamps As Dictionary(Of String, CatalogIndexStamp),
                                            filePath As String, info As FileInfo,
                                            summaryFormat As String) As Boolean
            Dim stamp As CatalogIndexStamp = Nothing
            If Not stamps.TryGetValue(filePath, stamp) Then Return False
            If Not String.Equals(stamp.SummaryFormat, If(summaryFormat, ""), StringComparison.Ordinal) Then Return False
            If Not String.Equals(stamp.SourceModifiedAt, info.LastWriteTime.ToString("o"), StringComparison.Ordinal) Then Return False
            Return String.Equals(stamp.SidecarModifiedAt, LibraryService.SidecarStamp(filePath), StringComparison.Ordinal)
        End Function

        ''' <summary>Sammelt die Bilddateien eines Ordners und aller Unterordner.
        '''
        ''' SELBST GESTIEGEN und nicht ueber EnumerateFiles mit AllDirectories: das bricht den
        ''' ganzen Lauf mit einer Ausnahme ab, sobald EIN Unterordner nicht lesbar ist - und in einem
        ''' Fotobestand steht so einer schnell, etwa ein fremder Einhaengepunkt. So bleibt der
        ''' Ausfall auf den einen Ordner beschraenkt.
        '''
        ''' Versteckte Ordner bleiben draussen, wie in der Galerie. Der Vorschau-Zwischenspeicher
        ''' liegt in einem davon, und ein Index, der seine eigenen Kacheln indiziert, waere ein
        ''' schoener Kreis.
        '''
        ''' Der PAPIERKORB ebenfalls, und zwar ueber FileOperationPolicy.IsTrashFolder statt ueber
        ''' den fuehrenden Punkt: der Papierkorb je Datentraeger heisst ".Trash-1000" und faellt
        ''' damit schon unter die versteckten, der des Benutzers liegt aber unter
        ''' "~/.local/share/Trash" - ein Ordner OHNE Punkt, der sonst mitgelaufen waere.
        '''
        ''' VERWEISE werden verfolgt, aber jedes Ziel nur EINMAL. Ein Verweis auf einen Ordner ist
        ''' eine gaengige Art, einen Fotobestand einzuhaengen, und ihn zu ueberspringen naehme dem
        ''' Lauf genau diese Bestaende. Ein Verweis auf einen Ordner WEITER OBEN ist dagegen ein
        ''' Ring: der Abstieg laeuft dann bis zur Pfadlaenge des Dateisystems und liest denselben
        ''' Bestand hundertfach ein. Beides zusammen geht nur ueber das kanonische Ziel und eine
        ''' Liste des schon Besuchten.</summary>
        ''' <summary>Eine EINZELNE Wurzel. Nur fuer Aufrufer, die wirklich nur einen Ordner meinen -
        ''' wer mehrere hat, nimmt <see cref="CollectFromRoots"/> mit der gemeinsamen
        ''' Besuchsliste.</summary>
        Private Shared Sub CollectFiles(folder As String, target As List(Of String), token As CancellationToken)
            CollectFiles(folder, target, token, New HashSet(Of String)(PathIdentity.Comparer), 0)
        End Sub

        ''' <summary>Wie tief der Abstieg hoechstens geht. ZWEITER Riegel neben der Besuchsliste:
        ''' laesst sich ein Verweis nicht aufloesen (Netzpfad, Ring, fehlendes Recht), traegt die
        ''' Liste nicht - und ein Lauf, der nie zurueckkommt, ist schlimmer als einer, der einen
        ''' sehr tief verschachtelten Ordner auslaesst. Sechzig Ebenen hat kein gewachsener
        ''' Fotobestand.</summary>
        Private Const MaxFolderDepth As Integer = 60

        Private Shared Sub CollectFiles(folder As String, target As List(Of String), token As CancellationToken,
                                        visited As HashSet(Of String), depth As Integer)
            If token.IsCancellationRequested Then Return
            If FileOperationPolicy.IsTrashFolder(folder) Then Return
            If depth > MaxFolderDepth Then
                DiagnosticLogService.LogAlways("Katalogindex.Ordner",
                                               $"Abstieg bei {MaxFolderDepth} Ebenen abgebrochen: {folder}")
                Return
            End If
            ' Das kanonische Ziel entscheidet, nicht der Weg dorthin: zwei Verweise auf denselben
            ' Ordner sind derselbe Bestand.
            Dim canonical = CanonicalFolder(folder)
            If canonical.Length = 0 Then Return
            If Not visited.Add(canonical) Then Return

            Try
                For Each file In Directory.EnumerateFiles(folder)
                    If token.IsCancellationRequested Then Return
                    If MediaFileTypes.IsDisplayable(file) Then target.Add(file)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Katalogindex.Ordner", ex)
                Return
            End Try

            Dim subFolders As String()
            Try
                subFolders = Directory.GetDirectories(folder)
            Catch ex As Exception
                DiagnosticLogService.LogException("Katalogindex.Ordner", ex)
                Return
            End Try

            For Each sub_ In subFolders
                If token.IsCancellationRequested Then Return
                Dim name = Path.GetFileName(sub_)
                If Not String.IsNullOrEmpty(name) AndAlso name.StartsWith(".", StringComparison.Ordinal) Then Continue For
                CollectFiles(sub_, target, token, visited, depth + 1)
            Next
        End Sub

        ''' <summary>Wohin ein Ordner WIRKLICH zeigt: der aufgeloeste Verweis, sonst der volle Pfad.
        '''
        ''' Leer heisst "nicht anfassen". Wirft das Aufloesen - genau das tut es bei einem Ring aus
        ''' Verweisen -, wird der Ordner ausgelassen: ohne kanonisches Ziel traegt die Besuchsliste
        ''' nicht, und der Ring liefe weiter.</summary>
        Private Shared Function CanonicalFolder(folder As String) As String
            Try
                Dim info = New DirectoryInfo(folder)
                Dim ziel = info.ResolveLinkTarget(returnFinalTarget:=True)
                Dim voll = If(ziel Is Nothing, info.FullName, ziel.FullName)
                Return Path.TrimEndingDirectorySeparator(Path.GetFullPath(voll))
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Katalogindex.Ordner",
                                               $"Verweis nicht aufloesbar, Ordner ausgelassen: {folder} ({ex.GetType().Name})")
                Return ""
            End Try
        End Function

    End Class

End Namespace
