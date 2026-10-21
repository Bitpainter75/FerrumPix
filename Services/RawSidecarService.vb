Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Xml
Imports System.Xml.Linq

Namespace Services

    ''' <summary>
    ''' Rezept-Begleitdatei fuer NUR-LESBARE Bildformate ("foto.cr2" -> "foto.cr2.fpxmp",
    ''' "foto.psd" -> "foto.psd.fpxmp"): am XMP-Sidecar-Konzept
    ''' orientiert (kleine XML-Datei NEBEN dem Original, wird beim Oeffnen automatisch geladen und
    ''' beim Verlassen aktualisiert), aber ein EIGENES Format - die Reglermodelle sind nicht
    ''' Adobe-kompatibel, ein echtes XMP wuerde nur so tun als ob.
    '''
    ''' Inhalt: eine XML-Huelle (Version, Quelldatei, Zeitstempel) mit dem Rezept-JSON des
    ''' .fpx-Formats als CDATA sowie Bewertung, Favorit, Farbetikett und Stichwoertern aus dem Katalog. Bewusst
    ''' KEINE eigene XML-Abbildung aller Regler: die JSON-Regeln aus FpxService sind
    ''' konstruktorbasiert erprobt (VB kann keine JsonConverter schreiben), und beide Formate
    ''' koennen so nie auseinanderdriften.
    '''
    ''' Grenzen (bewusst): gebackene Pixel-Bearbeitungen (Pinsel/Radierer/Retusche/gerasterte
    ''' Ebenen) stecken im ARBEITSBILD, nicht im Rezept. Auch Objekte gehoeren in eine .fpx:
    ''' externe Bild-Assets wuerden im Sidecar nur ueber absolute Pfade referenziert und nicht wie
    ''' im .fpx-Buendel eingebettet. Der Editor bietet den Sidecar-Speicherweg deshalb nur fuer
    ''' reine Entwicklungseinstellungen an.
    '''
    ''' Der Sidecar ist NICHT abschaltbar (die Einstellung dafuer ist entfallen): fuer
    ''' RAW und PSD ist er der EINZIGE Weg, eine Bearbeitung zu behalten, ohne die Quelldatei zu
    ''' zerstoeren - beide Formate koennen wir nicht schreiben. Ein Schalter dafuer hiess in der
    ''' Praxis "Bearbeitung stillschweigend wegwerfen", und er liess Viewer und Editor
    ''' auseinanderlaufen: der Viewer las den Sidecar bedingungslos, der Editor nur bei aktiver
    ''' Einstellung, womit dieselbe Datei je nach Ansicht anders gedreht aussah.
    ''' </summary>
    Public NotInheritable Class RawSidecarService

        Private Sub New()
        End Sub

        ''' <summary>Formate, deren Bearbeitung in den Sidecar geht statt in die Datei: RAW und
        ''' PSD/PSB. Beide sind nur-lesend (siehe die Schreibsperre in ImageProcessor.SaveImage),
        ''' beide sollen trotzdem Reglerwerte behalten koennen.</summary>
        Public Shared Function IsSidecarFormat(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Return RawPreviewService.IsSupportedRaw(path) OrElse PsdPreviewService.IsSupportedPsd(path)
        End Function

        Public Const Extension As String = ".fpxmp"
        Private Const FormatVersion As Integer = 2
        Private Shared ReadOnly Ns As XNamespace = "https://github.com/Bitpainter75/FerrumPix/ns/recipe/1.0"
        Private Shared ReadOnly _writeLock As New Object()

        ''' <summary>Katalogteil einer .fpxmp. Nullable Werte unterscheiden alte Version-1-Dateien
        ''' ohne Katalogblock von einem bewusst gesetzten 0/False; HasKeywords tut dasselbe fuer eine
        ''' bewusst geleerte Stichwortliste.</summary>
        Public NotInheritable Class RawSidecarCatalogData
            Public Property Rating As Integer?
            Public Property IsFavorite As Boolean?
            ''' Nothing = Feld fehlt (alte Sidecar), Leerstring = Etikett bewusst entfernt.
            Public Property ColorLabel As String = Nothing
            Public Property Keywords As New List(Of String)()
            Public Property HasKeywords As Boolean
            ''' Nothing = Feld fehlt (aeltere Sidecar); sonst -1 verworfen, 0 keine, 1 behalten.
            Public Property PickState As Integer?
        End Class

        ''' <summary>"foto.cr2" -> "foto.cr2.fpxmp" (voller Name bleibt erhalten, damit
        ''' foto.cr2 und foto.dng nie um denselben Sidecar konkurrieren).</summary>
        Public Shared Function SidecarPathFor(rawPath As String) As String
            Return rawPath & Extension
        End Function

        Public Shared Function Exists(rawPath As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(rawPath) AndAlso File.Exists(SidecarPathFor(rawPath))
        End Function

        ''' <summary>Schreibt das Rezept neben die RAW-Datei (atomar via Temp+Move). False bei Fehler -
        ''' der Aufrufer behaelt dann sein normales "ungespeicherte Aenderungen"-Verhalten.</summary>
        ''' <param name="developedThumbnail">Der Vermerk, dass die Kachel dieser Datei entwickelt
        ''' gezeigt wird, auch wenn die Einstellung dafuer aus ist (siehe
        ''' <see cref="ReadDevelopedThumbnail"/>). Nothing laesst einen vorhandenen Vermerk stehen:
        ''' der Editor und jeder andere Schreiber sollen ihn nicht still verlieren.</param>
        ''' <param name="requireAbsent">Nur anlegen, wenn am Ziel noch keine Datei liegt. Fuer Aufrufer,
        ''' die vorher "fehlt" gelesen haben: entsteht die Datei dazwischen, wird sie nicht ersetzt,
        ''' und das Ergebnis ist False.</param>
        ''' <param name="edited">Der Vermerk "bearbeitet" (siehe <see cref="IsEditedFile"/>). Gesetzt
        ''' wird er dort, wo bearbeitet wird; Nothing laesst einen vorhandenen stehen, wie beim
        ''' Vermerk der entwickelten Kachel.</param>
        Public Shared Function TryWrite(rawPath As String, adjustments As ImageAdjustments,
                                        Optional developedThumbnail As Boolean? = Nothing,
                                        Optional requireAbsent As Boolean = False,
                                        Optional edited As Boolean? = Nothing) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse adjustments Is Nothing Then Return False
            Try
                SyncLock _writeLock
                    Dim existing As ImageAdjustments = Nothing
                    Dim state = ReadRecipe(rawPath, existing)
                    If state = SidecarReadResult.Unreadable Then Return False
                    Dim catalog = LibraryService.Instance.GetSidecarCatalogData(rawPath)
                    Return TryWriteCore(rawPath, adjustments, catalog, developedThumbnail, edited,
                                        requireAbsent OrElse state = SidecarReadResult.Missing)
                End SyncLock
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Der Datenbankstand wird erst unter der Schreibsperre gelesen, damit ein
        ''' wartender Schreiber keinen aelteren Katalogstand nach einem neueren zurueckschreibt.</summary>
        Friend Shared Function TryWriteCatalogFromLibrary(rawPath As String, library As LibraryService) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsSidecarFormat(rawPath) OrElse library Is Nothing Then Return False
            SyncLock _writeLock
                Dim catalog = library.GetSidecarCatalogData(rawPath)
                Return TryWriteCatalog(rawPath, catalog.Rating.Value, catalog.IsFavorite.Value,
                                       catalog.ColorLabel, catalog.Keywords, catalog.PickState.Value)
            End SyncLock
        End Function

        ''' <summary>Schreibt eine bewusste Katalogaenderung in die .fpxmp und erhaelt dabei das
        ''' vorhandene Bearbeitungsrezept. Gibt es noch keine Sidecar, wird eine mit neutralem Rezept
        ''' angelegt - Katalogdaten sind seit Formatversion 2 selbst ein legitimer Sidecar-Inhalt.
        '''
        ''' EINE VORHANDENE, ABER NICHT LESBARE DATEI BLEIBT UNANGETASTET, und das Ergebnis ist False.
        ''' Defektes XML, eine Formatversion aus einer neueren Fassung, ein unlesbares Rezept oder ein
        ''' gerade nicht erreichbarer Speicher: in jedem dieser Faelle steckt vielleicht eine ganze
        ''' Entwicklung in der Datei. Frueher wurde daraus ein neutrales Rezept, und ein einziger
        ''' Stern ersetzte sie endgueltig. Die Katalogaenderung steht dann nur in der Datenbank.</summary>
        Public Shared Function TryWriteCatalog(rawPath As String, rating As Integer, isFavorite As Boolean, colorLabel As String,
                                               keywords As IEnumerable(Of String),
                                               Optional pickState As Integer = 0) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsSidecarFormat(rawPath) Then Return False
            ' Lesen und Schreiben unter DERSELBEN Sperre: sonst kann ein anderer Schreiber dieser
            ' Anwendung die Datei zwischen beiden aendern, und sie wuerde mit dem alten Rezept ersetzt.
            SyncLock _writeLock
                Dim adjustments As ImageAdjustments = Nothing
                Dim state = ReadRecipe(rawPath, adjustments)
                If state = SidecarReadResult.Unreadable Then Return False
                If state = SidecarReadResult.Missing Then adjustments = New ImageAdjustments()
                Dim catalog = New RawSidecarCatalogData With {
                    .Rating = Math.Max(0, Math.Min(5, rating)),
                    .IsFavorite = isFavorite,
                    .ColorLabel = If(colorLabel, "").Trim(),
                    .Keywords = NormalizeKeywords(keywords),
                    .HasKeywords = True,
                    .PickState = LibraryService.NormalizePickState(pickState)
                }
                ' Hiess es "fehlt", wird nur angelegt, wenn am Ziel noch immer nichts liegt: eine Datei,
                ' die ein anderes Programm inzwischen hingelegt hat, wird nicht ersetzt.
                Return TryWriteCore(rawPath, adjustments, catalog, Nothing, Nothing,
                                    requireAbsent:=state = SidecarReadResult.Missing)
            End SyncLock
        End Function

        ''' <summary>Liest das Rezept, aendert es und schreibt es zurueck, alles unter EINER Sperre wie
        ''' beim Katalog. Fuer Aufrufer, die ein vorhandenes Rezept ergaenzen statt es zu ersetzen (etwa
        ''' einen Drehschritt anhaengen): sonst koennte ein anderer Schreiber dieser Anwendung die Datei
        ''' zwischen Lesen und Schreiben aendern, und seine Aenderung waere weg.
        '''
        ''' Eine unlesbare Datei bleibt unangetastet (False). Fehlt sie, kommt das Rezept aus
        ''' <paramref name="createWhenMissing"/> und wird nur angelegt, wenn dort noch immer nichts
        ''' liegt.</summary>
        Public Shared Function TryModifyRecipe(rawPath As String, createWhenMissing As Func(Of ImageAdjustments),
                                               modify As Action(Of ImageAdjustments),
                                               Optional developedThumbnail As Boolean? = Nothing,
                                               Optional edited As Boolean? = Nothing) As Boolean
            Return TryModifyRecipeCore(rawPath, createWhenMissing, modify, developedThumbnail, edited) = ModifyRecipeResult.Written
        End Function

        ''' <summary>Was <see cref="TryModifyRecipeDetailed"/> herausbekam. Unreadable steht fuer sich,
        ''' weil der Aufrufer es anders melden soll als einen Schreibfehler: dort liegt eine Datei, die
        ''' wir nicht verstehen und deshalb nicht anfassen.</summary>
        Public Enum ModifyRecipeResult
            Written
            Unreadable
            Failed
        End Enum

        ''' <summary>Wie <see cref="TryModifyRecipe"/>, sagt aber, warum nichts geschrieben wurde.</summary>
        Public Shared Function TryModifyRecipeDetailed(rawPath As String, createWhenMissing As Func(Of ImageAdjustments),
                                                       modify As Action(Of ImageAdjustments),
                                                       Optional developedThumbnail As Boolean? = Nothing,
                                                       Optional edited As Boolean? = Nothing) As ModifyRecipeResult
            Return TryModifyRecipeCore(rawPath, createWhenMissing, modify, developedThumbnail, edited)
        End Function

        ''' <paramref name="createWhenMissing"/> laeuft NUR, wenn die Datei fehlt. Er darf also teuer
        ''' sein (die Startwerte einer RAW kommen aus ihren Metadaten), ein vorhandenes Rezept kostet
        ''' ihn nicht.
        Private Shared Function TryModifyRecipeCore(rawPath As String, createWhenMissing As Func(Of ImageAdjustments),
                                                    modify As Action(Of ImageAdjustments),
                                                    developedThumbnail As Boolean?,
                                                    edited As Boolean?) As ModifyRecipeResult
            If String.IsNullOrWhiteSpace(rawPath) OrElse createWhenMissing Is Nothing OrElse modify Is Nothing Then Return ModifyRecipeResult.Failed
            Try
                SyncLock _writeLock
                    Dim adjustments As ImageAdjustments = Nothing
                    Dim state = ReadRecipe(rawPath, adjustments)
                    If state = SidecarReadResult.Unreadable Then Return ModifyRecipeResult.Unreadable
                    If state = SidecarReadResult.Missing Then adjustments = createWhenMissing()
                    If adjustments Is Nothing Then Return ModifyRecipeResult.Failed
                    modify(adjustments)
                    Dim catalog = LibraryService.Instance.GetSidecarCatalogData(rawPath)
                    Dim written = TryWriteCore(rawPath, adjustments, catalog, developedThumbnail, edited,
                                               requireAbsent:=state = SidecarReadResult.Missing)
                    Return If(written, ModifyRecipeResult.Written, ModifyRecipeResult.Failed)
                End SyncLock
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.ModifyRecipe", ex)
                Return ModifyRecipeResult.Failed
            End Try
        End Function

        Private Shared Function TryWriteCore(rawPath As String, adjustments As ImageAdjustments,
                                             catalog As RawSidecarCatalogData,
                                             developedThumbnail As Boolean?,
                                             edited As Boolean?,
                                             Optional requireAbsent As Boolean = False) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse adjustments Is Nothing Then Return False
            SyncLock _writeLock
                Dim temp As String = Nothing
                Dim target As String = Nothing
                Try
                    Dim keepDevelopedThumbnail = If(developedThumbnail.HasValue, developedThumbnail.Value,
                                                    ReadDevelopedThumbnail(rawPath))
                    ' Der Vermerk "bearbeitet" bleibt, wie er war, wenn der Schreiber ihn nicht nennt
                    ' (Katalog: Sterne, Etikett, Stichwoerter). Fehlte er in einer aelteren Datei,
                    ' fehlt er weiter: dann entscheidet beim Lesen der Vergleich (IsEditedFile).
                    Dim keepEdited = If(edited.HasValue, edited, ReadEditedAttribute(rawPath))
                    ' HIER wird entschieden, was der Vermerk ueber gebackene Vorgaenge BEDEUTET.
                    ' Neben dieser Datei liegt kein einziges Pixel: die Quelle bleibt unangetastet,
                    ' und beim naechsten Oeffnen wird sie neu entwickelt. Also sind Entrauschen,
                    ' Objektentfernen, Retusche und Striche dort NICHT drin, sondern noch anzuwenden.
                    ' Der Editor kann das nicht setzen - er weiss beim Entrauschen noch nicht, ob
                    ' spaeter eine .fpxmp oder eine .fpx daraus wird.
                    Dim sidecarAdj = adjustments.Clone()
                    sidecarAdj.BakedOperationsApplied = False
                    Dim json = FpxService.SerializeAdjustments(sidecarAdj)
                    Dim keywordsNode = New XElement(Ns + "keywords")
                    For Each keyword In NormalizeKeywords(catalog?.Keywords)
                        keywordsNode.Add(New XElement(Ns + "keyword", keyword))
                    Next
                    Dim safeRating = Math.Max(0, Math.Min(5, If(catalog Is Nothing, 0, catalog.Rating.GetValueOrDefault())))
                    Dim isFavorite = catalog IsNot Nothing AndAlso catalog.IsFavorite.GetValueOrDefault()
                    Dim catalogNode = New XElement(Ns + "catalog",
                        New XElement(Ns + "rating", safeRating.ToString(Globalization.CultureInfo.InvariantCulture)),
                        New XElement(Ns + "favorite", If(isFavorite, "true", "false")),
                        New XElement(Ns + "colorLabel", If(catalog?.ColorLabel, "").Trim()),
                        New XElement(Ns + "pick", LibraryService.NormalizePickState(
                            If(catalog Is Nothing, 0, catalog.PickState.GetValueOrDefault())).ToString(Globalization.CultureInfo.InvariantCulture)),
                        keywordsNode)
                    Dim doc = New XDocument(
                        New XDeclaration("1.0", "utf-8", Nothing),
                        New XElement(Ns + "recipe",
                            New XAttribute("version", FormatVersion),
                            New XAttribute("generator", "FerrumPix"),
                            If(keepDevelopedThumbnail, New XAttribute(DevelopedThumbnailAttribute, "true"), Nothing),
                            If(keepEdited.HasValue, New XAttribute(EditedAttribute, If(keepEdited.Value, "true", "false")), Nothing),
                            New XElement(Ns + "source",
                                New XAttribute("fileName", Path.GetFileName(rawPath))),
                            New XElement(Ns + "savedUtc", DateTime.UtcNow.ToString("O")),
                            catalogNode,
                            New XElement(Ns + "adjustments",
                                New XAttribute("format", "fpx-json"),
                                New XCData(json))))

                    target = SidecarPathFor(rawPath)
                    ' Eine Nachbardatei je Vorgang: eine feste ".tmp" teilten sich zwei Instanzen, die
                    ' dieselbe Datei gleichzeitig schreiben, und eine haengte die halbe der anderen um.
                    temp = ImageProcessor.AtomicSiblingPath(target)
                    Using fs = New FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                        Using writer = XmlWriter.Create(fs, New XmlWriterSettings With {.Indent = True})
                            doc.Save(writer)
                        End Using
                        ' Bis auf die Platte, bevor umbenannt wird: sonst kann nach einem
                        ' Stromausfall der neue Name gelten und der Inhalt fehlen.
                        fs.Flush(flushToDisk:=True)
                    End Using
                    ' Ohne Ueberschreiben wirft Move, wenn am Ziel inzwischen eine Datei liegt.
                    File.Move(temp, target, overwrite:=Not requireAbsent)
                    ' Den gemerkten Drehwinkel verwerfen, statt auf einen geaenderten Zeitstempel zu
                    ' hoffen: zwei Schreibvorgaenge kurz hintereinander koennen auf grob aufloesenden
                    ' Dateisystemen dieselbe mtime tragen.
                    Dim ignored As CachedDisplayFacts = Nothing
                    _displayFactsCache.TryRemove(target, ignored)
                    Return True
                Catch ex As Exception
                    If requireAbsent AndAlso target IsNot Nothing AndAlso File.Exists(target) Then
                        ' Kein Fehler, sondern der vereinbarte Ausgang: der Aufrufer hatte "fehlt"
                        ' gelesen, und inzwischen liegt dort eine Datei. Sie bleibt, wie sie ist.
                        DiagnosticLogService.LogAlways("Sidecar.Write",
                            $"inzwischen vorhanden, nicht ersetzt: {Path.GetFileName(target)}")
                    Else
                        DiagnosticLogService.LogException("Sidecar.Write", ex)
                    End If
                    Try
                        If temp IsNot Nothing AndAlso File.Exists(temp) Then File.Delete(temp)
                    Catch
                    End Try
                    Return False
                End Try
            End SyncLock
        End Function

        Private Shared Function NormalizeKeywords(keywords As IEnumerable(Of String)) As List(Of String)
            Return If(keywords, Enumerable.Empty(Of String)()).
                Select(Function(value) If(value, "").Trim()).
                Where(Function(value) value.Length > 0).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
        End Function

        ' ── Mitwandern bei Dateioperationen ──────────────────────────────────────
        ' Wie XMP-Sidecars ueblicherweise: Verschieben/Kopieren/Umbenennen/Loeschen der Bilddatei
        ' nimmt die Begleitdateien mit, und zwar ALLE drei: die eigene .fpxmp, die fremde XMP in der
        ' angehaengten Form ("foto.cr2.xmp") und in der ersetzten Form ("foto.xmp"). Frueher ging
        ' nur die .fpxmp mit; Bewertung, Stichwoerter und eine fremde Entwicklung blieben am alten
        ' Ort liegen und wurden am neuen nicht mehr gefunden.
        '
        ' Ein Fehler an einer Begleitdatei laesst die Hauptoperation stehen (sie ist schon geschehen),
        ' wird protokolliert und als Pfad an den Aufrufer zur sichtbaren Meldung zurueckgegeben.
        '
        ' DIE ERSETZTE FORM KANN MEHREREN DATEIEN GEHOEREN: "foto.xmp" passt zu foto.cr2 UND foto.jpg.
        ' Sie wird deshalb nur verschoben oder geloescht, wenn keine andere Datei desselben Stamms
        ' am alten Ort bleibt; sonst kopiert beziehungsweise stehen gelassen. Eine vorhandene XMP am
        ' Ziel wird nie ueberschrieben: sie gehoert dort womoeglich einer anderen Datei.

        Public Shared Function AccompanyMove(sourcePath As String, targetPath As String) As List(Of String)
            Dim failed As New List(Of String)()
            ' Die Bilddatei hat der Aufrufer schon verschoben; die Listen des Stapels erfahren es.
            NoteInBatch(sourcePath, added:=False)
            NoteInBatch(targetPath, added:=True)
            Try
                Dim sidecar = SidecarPathFor(sourcePath)
                If CompanionExists(sidecar) Then File.Move(sidecar, SidecarPathFor(targetPath), overwrite:=False)
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyMove", ex)
                failed.Add(SidecarPathFor(sourcePath))
            End Try
            CarryXmpCompanions(sourcePath, targetPath, moveFiles:=True, failed:=failed)
            Return failed
        End Function

        Public Shared Function AccompanyCopy(sourcePath As String, targetPath As String) As List(Of String)
            Dim failed As New List(Of String)()
            NoteInBatch(targetPath, added:=True)
            Try
                Dim sidecar = SidecarPathFor(sourcePath)
                If CompanionExists(sidecar) Then File.Copy(sidecar, SidecarPathFor(targetPath), overwrite:=False)
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyCopy", ex)
                failed.Add(SidecarPathFor(sourcePath))
            End Try
            CarryXmpCompanions(sourcePath, targetPath, moveFiles:=False, failed:=failed)
            Return failed
        End Function

        ''' <summary>Beim Papierkorb wandern die Begleitdateien ebenfalls in den Papierkorb (die
        ''' Bilddatei laesst sich so mitsamt Rezept wiederherstellen), sonst werden sie geloescht. Die
        ''' ersetzte XMP-Form nur, wenn keine andere Datei desselben Stamms mehr da ist. Liefert die
        ''' Pfade der Begleiter, die nicht entfernt werden konnten.</summary>
        Public Shared Function AccompanyDelete(rawPath As String, useTrash As Boolean) As List(Of String)
            Return AccompanyDeleteCore(rawPath, useTrash, AddressOf TrashService.MoveToTrash)
        End Function

        Private Shared Function AccompanyDeleteCore(rawPath As String, useTrash As Boolean,
                                                    moveToTrash As Func(Of String, Boolean)) As List(Of String)
            Dim failed As New List(Of String)()
            NoteInBatch(rawPath, added:=False)
            Dim companions As New List(Of String) From {SidecarPathFor(rawPath), rawPath & ".xmp"}
            Dim stemXmp = StemXmpPathFor(rawPath)
            ' Erst nachsehen, ob es die Stamm-XMP gibt: HasStemSibling zaehlt den Ordner auf. Ist sie
            ' nicht erreichbar, wird sie gemeldet statt still uebergangen (CompanionExists).
            Dim stemExists As Boolean
            Try
                stemExists = stemXmp IsNot Nothing AndAlso CompanionExists(stemXmp)
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyDelete", ex)
                failed.Add(stemXmp)
                stemExists = False
            End Try
            If stemExists Then
                Dim scope = _currentStemScope.Value
                If scope IsNot Nothing Then
                    ' Im Stapel entscheidet erst dessen Ende, mit frisch gelesenem Ordner.
                    AddPending(scope, New PendingStemRemoval With {
                        .StemXmp = stemXmp, .ImagePath = rawPath, .UseTrash = useTrash, .MoveToTrash = moveToTrash})
                ElseIf Not HasStemSibling(rawPath) Then
                    companions.Add(stemXmp)
                End If
            End If
            For Each companion In companions
                Try
                    If Not CompanionExists(companion) Then Continue For
                    If useTrash Then
                        If Not moveToTrash(companion) Then Throw New IOException("Papierkorb nicht verfuegbar: " & companion)
                    Else
                        File.Delete(companion)
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("Sidecar.AccompanyDelete", ex)
                    failed.Add(companion)
                End Try
            Next
            Return failed
        End Function

        ''' <summary>Liegt dort eine Begleitdatei? False NUR, wenn sie fehlt (Datei oder Ordner nicht
        ''' gefunden), und fuer einen Ordner dieses Namens. Jeder andere Fehler (Freigabe weg, keine
        ''' Rechte) fliegt als Ausnahme weiter: File.Exists sagte dann ebenfalls "fehlt", und die
        ''' Begleitdatei blieb nach dem Loeschen oder Verschieben des Bildes still am alten Ort
        ''' liegen. Die Aufrufer melden sie dann.</summary>
        Private Shared Function CompanionExists(filePath As String) As Boolean
            Try
                Return (File.GetAttributes(filePath) And FileAttributes.Directory) = 0
            Catch ex As FileNotFoundException
                Return False
            Catch ex As DirectoryNotFoundException
                Return False
            End Try
        End Function

        ''' <summary>Nimmt die fremden XMP-Dateien mit. Siehe den Block oben fuer die Regeln.</summary>
        Private Shared Sub CarryXmpCompanions(sourcePath As String, targetPath As String, moveFiles As Boolean,
                                              failed As List(Of String))
            ' Angehaengte Form: gehoert eindeutig dieser einen Datei.
            CarryOne(sourcePath & ".xmp", targetPath & ".xmp", moveFiles, removeSourceIfSame:=moveFiles, failed:=failed)
            ' Ersetzte Form: geteilt moeglich.
            Dim sourceStem = StemXmpPathFor(sourcePath)
            Dim targetStem = StemXmpPathFor(targetPath)
            If sourceStem Is Nothing OrElse targetStem Is Nothing Then Return
            If String.Equals(sourceStem, targetStem, PathIdentity.Comparison) Then Return
            ' Ohne Stamm-XMP gibt es nichts mitzunehmen, und die Aufzaehlung des Ordners in
            ' HasStemSibling entfaellt (siehe AccompanyDeleteCore). Nicht erreichbar heisst nicht
            ' "fehlt": dann wird sie gemeldet.
            Try
                If Not CompanionExists(sourceStem) Then Return
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyXmp", ex)
                failed.Add(sourceStem)
                Return
            End Try
            ' Die ersetzte Form am Ziel kann einem anderen Bild gehoeren: weil dort schon eine liegt,
            ' oder weil dort ein anderes Bild desselben Stamms liegt (foto.jpg neben dem neuen
            ' foto.cr2), das eine neue foto.xmp sonst ebenso faende und fremde Bewertungen und
            ' Stichwoerter uebernaehme. Der eindeutige Name nimmt die Quell-XMP dann auf und hat beim
            ' Lesen Vorrang. Im Zweifel (Ordner nicht lesbar) der eindeutige Name.
            Dim stemTaken = File.Exists(targetStem)
            Dim targetHasSibling = Not stemTaken AndAlso HasStemSiblingInBatch(targetPath, whenUnknown:=True)
            If (stemTaken OrElse targetHasSibling) AndAlso Not File.Exists(targetPath & ".xmp") Then
                targetStem = targetPath & ".xmp"
            ElseIf targetHasSibling Then
                ' Der eindeutige Name ist schon belegt (die angehaengte Form kam eben mit), die
                ' Stamm-Form wuerde dem Nachbarn zugeordnet: kein freier Platz, die Quell-XMP bleibt
                ' und wird gemeldet, wie bei einer fremden XMP am Ziel.
                DiagnosticLogService.LogAlways("Sidecar.Accompany",
                    $"Stamm-XMP nicht mitgenommen, am Ziel liegt ein Bild desselben Stamms: {Path.GetFileName(targetStem)}")
                failed.Add(sourceStem)
                Return
            End If
            Dim scope = _currentStemScope.Value
            If moveFiles AndAlso scope IsNot Nothing Then
                ' Im Stapel nur kopieren und vormerken: ob die Quelle gehen darf, entscheidet das Ende
                ' mit frisch gelesenem Ordner. Liegt dieselbe Datei schon am Ziel (das Geschwister
                ' zog im selben Stapel vorher um), ist das kein Fehler.
                CarryOne(sourceStem, targetStem, moveFile:=False, removeSourceIfSame:=False, failed:=failed, sameIsDone:=True)
                AddPending(scope, New PendingStemRemoval With {.StemXmp = sourceStem, .ImagePath = sourcePath, .CopyAt = targetStem})
                Return
            End If
            ' Beim Kopieren bleibt die Quell-XMP ohnehin, ob ein Geschwister da ist oder nicht.
            Dim siblingStays = moveFiles AndAlso HasStemSibling(sourcePath)
            CarryOne(sourceStem, targetStem, moveFiles AndAlso Not siblingStays,
                     removeSourceIfSame:=moveFiles AndAlso Not siblingStays, failed:=failed)
        End Sub

        Private Shared Sub CarryOne(source As String, target As String, moveFile As Boolean, removeSourceIfSame As Boolean,
                                    failed As List(Of String), Optional sameIsDone As Boolean = False)
            Try
                If Not CompanionExists(source) Then Return
                If File.Exists(target) Then
                    ' Liegt dort schon DIESELBE Datei (ein Stapel, der foto.cr2 und foto.jpg nacheinander
                    ' umbenennt, hat sie beim ersten Schritt kopiert), bleibt die Quelle sonst als Waise
                    ' zurueck. Eine ANDERE Datei am Ziel gehoert jemand anderem und bleibt.
                    Dim same = (removeSourceIfSame OrElse sameIsDone) AndAlso
                               File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(target))
                    If same AndAlso removeSourceIfSame Then
                        File.Delete(source)
                    ElseIf same Then
                        ' Schon da, nichts zu tun.
                    Else
                        DiagnosticLogService.LogAlways("Sidecar.Accompany",
                            $"XMP am Ziel vorhanden, nicht ueberschrieben: {Path.GetFileName(target)}")
                        failed.Add(source)
                    End If
                    Return
                End If
                If moveFile Then
                    File.Move(source, target)
                Else
                    File.Copy(source, target)
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyXmp", ex)
                failed.Add(source)
            End Try
        End Sub

        ''' <summary>Ein verwaister Begleiter darf nicht ersetzt und eine fremde Stamm-XMP nicht
        ''' an ein Bild ohne eigene XMP angehaengt werden. Liefert den kollidierenden Pfad.
        '''
        ''' <paramref name="replacingTarget"/>: das vorhandene Ziel ist zum Ersetzen freigegeben. Dann
        ''' gehoert seine Stamm-XMP ("ziel/foto.xmp" neben "ziel/foto.cr2") ihm und geht mit ihm
        ''' beiseite (<see cref="OwnedStemXmp"/>), statt den Vorgang aufzuhalten - es sei denn, ein
        ''' anderes Bild desselben Stamms liegt dort und kann sie ebenso meinen.</summary>
        Public Shared Function UnrelatedTargetSidecar(sourcePath As String, targetPath As String,
                                                      Optional replacingTarget As Boolean = False) As String
            If Not File.Exists(targetPath) AndAlso File.Exists(SidecarPathFor(targetPath)) Then Return SidecarPathFor(targetPath)
            If Not File.Exists(targetPath) AndAlso File.Exists(targetPath & ".xmp") AndAlso
               Not String.Equals(sourcePath, targetPath, PathIdentity.Comparison) Then Return targetPath & ".xmp"
            Dim sourceStem = StemXmpPathFor(sourcePath)
            Dim targetStem = StemXmpPathFor(targetPath)
            If targetStem Is Nothing OrElse Not File.Exists(targetStem) Then Return Nothing
            If String.Equals(sourceStem, targetStem, PathIdentity.Comparison) Then Return Nothing
            If replacingTarget AndAlso File.Exists(targetPath) AndAlso OwnedStemXmp(targetPath) IsNot Nothing Then Return Nothing
            If Not File.Exists(sourcePath & ".xmp") AndAlso
               (sourceStem Is Nothing OrElse Not File.Exists(sourceStem)) Then Return targetStem
            Return Nothing
        End Function

        ''' <summary>Die Stamm-XMP ("ordner/foto.xmp"), wenn sie vorhanden ist und allein dieser Datei
        ''' gehoert, weil kein anderes Bild desselben Stamms daneben liegt. Sonst Nothing: dann kann
        ''' sie ebenso einem Nachbarn gehoeren (foto.jpg neben foto.cr2) und wird nicht angefasst.
        '''
        ''' Im Stapel wird der Ordner dafuer nur einmal gelesen: beim Ersetzen vieler Bilder lief
        ''' sonst fuer jedes eine Aufzaehlung des Zielordners. Sagt die gemerkte Liste faelschlich
        ''' "allein", geht die XMP beiseite, und ihr Loeschen entscheidet erst das Ende des Stapels mit
        ''' frisch gelesenem Ordner (<see cref="DeferAsideStemRemoval"/>); verloren geht nichts.</summary>
        Public Shared Function OwnedStemXmp(imagePath As String) As String
            Dim stem = StemXmpPathFor(imagePath)
            If stem Is Nothing OrElse Not File.Exists(stem) Then Return Nothing
            ' Im Zweifel geteilt: dann bleibt sie, wo sie ist.
            If HasStemSiblingInBatch(imagePath, whenUnknown:=True) Then Return Nothing
            Return stem
        End Function

        ''' <summary>HasStemSibling, im Stapel aus der Liste des Ordners (StemListing, nach ihrer
        ''' Gueltigkeit neu gelesen) statt mit einer Aufzaehlung je Bild. Laesst sich der Ordner nicht
        ''' lesen, gilt <paramref name="whenUnknown"/>: der Aufrufer waehlt die sichere Seite.</summary>
        Private Shared Function HasStemSiblingInBatch(imagePath As String, whenUnknown As Boolean) As Boolean
            Dim scope = _currentStemScope.Value
            If scope Is Nothing Then Return HasStemSibling(imagePath)
            Dim folder = Path.GetDirectoryName(imagePath)
            If String.IsNullOrEmpty(folder) Then Return whenUnknown
            Dim sameStem As List(Of String)
            Try
                SyncLock scope.TargetListings
                    Dim listing As StemListing = Nothing
                    If Not scope.TargetListings.TryGetValue(folder, listing) OrElse listing.IsStale Then
                        listing = StemListing.Read(folder)
                        scope.TargetListings(folder) = listing
                    End If
                    sameStem = listing.WithStem(Path.GetFileNameWithoutExtension(imagePath))
                End SyncLock
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.HasStemSiblingInBatch", ex)
                Return whenUnknown
            End Try
            Return HasStemSiblingAmong(sameStem, imagePath)
        End Function

        ''' <summary>Nach einem gelungenen Ersetzen: die mit dem alten Ziel beiseitegelegte Stamm-XMP
        ''' nicht sofort loeschen, sondern am Ende des Stapels mit frisch gelesenem Ordner. Hat das Bild
        ''' am Ziel dann doch ein Geschwister, gehoerte die XMP auch ihm und kommt an ihren Namen
        ''' zurueck. True, wenn vorgemerkt (im Stapel); False ausserhalb, dann loescht der Aufrufer.</summary>
        Public Shared Function DeferAsideStemRemoval(asideStem As String, originalStem As String, imagePath As String) As Boolean
            Dim scope = _currentStemScope.Value
            If scope Is Nothing Then Return False
            AddPending(scope, New PendingStemRemoval With {.StemXmp = asideStem, .AsideOriginal = originalStem, .ImagePath = imagePath})
            Return True
        End Function

        ''' <summary>"ordner/foto.cr2" -> "ordner/foto.xmp"; Nothing, wenn die Datei selbst eine .xmp
        ''' ist oder keine Endung hat (dann gibt es keine ersetzte Form).</summary>
        Friend Shared Function StemXmpPathFor(imagePath As String) As String
            If String.IsNullOrWhiteSpace(imagePath) Then Return Nothing
            Dim ext = Path.GetExtension(imagePath)
            If String.IsNullOrEmpty(ext) OrElse String.Equals(ext, ".xmp", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Return Path.ChangeExtension(imagePath, ".xmp")
        End Function

        ' ── Stamm-XMP im Stapel ──────────────────────────────────────────────────
        ' HasStemSibling zaehlt den Ordner auf, und unter Unix liest die Aufzaehlung dafuer ALLE
        ' Eintraege, das Namensmuster filtert erst danach. Ein Stapel ueber viele Bilder mit
        ' Stamm-XMP in einem grossen Ordner lief so fuer jedes Bild einmal durch den Bestand, auf der
        ' Netzfreigabe quadratisch langsam.
        '
        ' Eine gemerkte Ordnerliste waere schnell, aber falsch in der gefaehrlichen Richtung: kommt
        ' waehrenddessen von aussen foto.jpg neben foto.cr2 und foto.xmp, saehe sie das Geschwister
        ' nicht, und die geteilte XMP wuerde verschoben oder geloescht. Innerhalb eines Stapels
        ' (BatchScope) wird deshalb gar nicht mehr nach Geschwistern gefragt: die Stamm-XMP wird
        ' beim Verschieben nur KOPIERT, beim Loeschen stehen gelassen, und beides vorgemerkt. Erst am
        ' Ende des Stapels liest Finish jeden betroffenen Ordner EINMAL frisch und entfernt, was dann
        ' wirklich kein Geschwister mehr hat. Eine Fehlentscheidung kostet so hoechstens eine Kopie zu
        ' viel, nie eine verlorene XMP. Ausserhalb eines Stapels wird wie bisher sofort gefragt.

        Friend NotInheritable Class PendingStemRemoval
            ''' Die Stamm-XMP am alten Ort.
            Friend StemXmp As String
            ''' Das Bild, das umgezogen oder geloescht ist; es zaehlt nicht als Geschwister.
            Friend ImagePath As String
            ''' Beim Verschieben die Kopie am Ziel. Entfernt wird nur, wenn sie bytegleich da ist.
            Friend CopyAt As String
            ''' Beim Loeschen: in den Papierkorb statt endgueltig.
            Friend UseTrash As Boolean
            Friend MoveToTrash As Func(Of String, Boolean)
            ''' Beim Ersetzen eines Ziels: der urspruengliche Name der beiseitegelegten Stamm-XMP
            ''' (StemXmp ist dann die beiseitegelegte Datei, ImagePath das neue Bild am Ziel).
            Friend AsideOriginal As String
        End Class

        ''' <summary>Ein laufender Stapel (<see cref="BatchScope"/>). <see cref="Finish"/> entscheidet
        ''' ueber die vorgemerkten Stamm-XMP und liefert, was sich nicht entfernen liess; der Aufrufer
        ''' ruft es am Ende der Schleife IM HINTERGRUND und meldet das Ergebnis. Dispose beendet den
        ''' Rahmen und holt Finish nur nach, wenn es nicht gerufen wurde (etwa nach einer Ausnahme);
        ''' dann bleibt es beim Protokoll.</summary>
        Public NotInheritable Class SidecarBatch
            Implements IDisposable

            Friend ReadOnly Pending As New List(Of PendingStemRemoval)()
            ''' Zielordner, gelesen und nach Stamm geordnet, fuer OwnedStemXmp; nach
            ''' StemListing.MaxAgeSeconds neu gelesen.
            Friend ReadOnly TargetListings As New Dictionary(Of String, StemListing)(PathIdentity.Comparer)
            Private ReadOnly _previous As SidecarBatch
            Private _finished As Boolean
            Private _disposed As Boolean

            Friend Sub New(previous As SidecarBatch)
                _previous = previous
            End Sub

            ''' <summary>Entfernt die vorgemerkten Stamm-XMP, die kein Geschwister mehr haben, und
            ''' liefert die Pfade derer, bei denen das scheiterte. Ein zweiter Aufruf tut nichts.
            ''' Liest Ordner und Dateien: nicht auf dem Anzeigefaden rufen.</summary>
            Public Function Finish() As List(Of String)
                Dim toFinish As List(Of PendingStemRemoval)
                SyncLock Pending
                    If _finished Then Return New List(Of String)()
                    _finished = True
                    toFinish = Pending.ToList()
                    Pending.Clear()
                End SyncLock
                Return FinishPendingStemRemovals(toFinish)
            End Function

            ''' <summary>Beendet den Rahmen. Wurde Finish nicht gerufen, laeuft es hier im Hintergrund
            ''' nach, nie auf dem Faden des Aufrufers (das kann der Anzeigefaden sein); was dann
            ''' scheitert, steht nur im Protokoll. Die Aufrufer rufen Finish deshalb auch nach einer
            ''' Ausnahme selbst.</summary>
            Public Sub Dispose() Implements IDisposable.Dispose
                If _disposed Then Return
                _disposed = True
                _currentStemScope.Value = _previous
                Dim open As Boolean
                SyncLock Pending
                    open = Not _finished
                End SyncLock
                If Not open Then Return
                Task.Run(Sub()
                             Dim failed = Finish()
                             If failed.Count > 0 Then
                                 DiagnosticLogService.LogAlways("Sidecar.FinishStemXmp", "nicht entfernt: " & String.Join(", ", failed))
                             End If
                         End Sub)
            End Sub
        End Class

        ''' <summary>Ein Ordner, nach Stamm geordnet, mit dem Zeitpunkt des Lesens. Aelter als
        ''' <see cref="MaxAgeSeconds"/> wird er neu gelesen: so sieht eine Entscheidung, was von aussen
        ''' dazukam, spaetestens nach dieser Zeit, und ein grosser Ordner wird trotzdem hoechstens
        ''' einmal je Zeitspanne gelesen statt je Bild.</summary>
        Friend NotInheritable Class StemListing
            Friend Const MaxAgeSeconds As Double = 1.0
            ''' Stamm -> Pfade mit diesem Stamm.
            Private ReadOnly _byStem As New Dictionary(Of String, List(Of String))(PathIdentity.Comparer)
            ''' Alle Dateien des Ordners, fuer die Frage, ob eine bestimmte da ist.
            Private ReadOnly _all As New HashSet(Of String)(PathIdentity.Comparer)
            ''' Beginn des Lesens: was danach von AUSSEN entstand, kennt die Liste nicht. Was der
            ''' eigene Stapel anlegt oder wegnimmt, traegt er nach (Add, Remove).
            Friend ReadUtc As DateTime
            Friend ReadSeconds As Double

            Friend Function Contains(filePath As String) As Boolean
                Return _all.Contains(filePath)
            End Function

            ''' <summary>Die Pfade mit diesem Stamm, als Kopie.</summary>
            Friend Function WithStem(stem As String) As List(Of String)
                Dim paths As List(Of String) = Nothing
                Return If(_byStem.TryGetValue(stem, paths), paths.ToList(), New List(Of String)())
            End Function

            Friend Sub Add(filePath As String)
                If Not _all.Add(filePath) Then Return
                Dim stem = Path.GetFileNameWithoutExtension(filePath)
                Dim paths As List(Of String) = Nothing
                If Not _byStem.TryGetValue(stem, paths) Then
                    paths = New List(Of String)()
                    _byStem(stem) = paths
                End If
                paths.Add(filePath)
            End Sub

            Friend Sub Remove(filePath As String)
                If Not _all.Remove(filePath) Then Return
                Dim paths As List(Of String) = Nothing
                If _byStem.TryGetValue(Path.GetFileNameWithoutExtension(filePath), paths) Then
                    paths.RemoveAll(Function(p) String.Equals(p, filePath, PathIdentity.Comparison))
                End If
            End Sub

            ''' <summary>Aelter als eine Sekunde, mindestens aber als das Vierfache der Lesezeit. Ohne
            ''' den zweiten Teil waere eine Liste, deren Lesen auf einer langsamen Freigabe laenger
            ''' dauert als die Sekunde, schon bei der Rueckgabe veraltet, und jedes Bild laese den
            ''' Ordner neu. So geht hoechstens ein Fuenftel der Zeit ins Lesen.</summary>
            Friend ReadOnly Property IsStale As Boolean
                Get
                    Return (DateTime.UtcNow - ReadUtc).TotalSeconds > Math.Max(MaxAgeSeconds, 4.0 * ReadSeconds)
                End Get
            End Property

            Friend Shared Function Read(folder As String) As StemListing
                Dim readUtc = DateTime.UtcNow
                Dim listing = New StemListing With {.ReadUtc = readUtc}
                For Each file In Directory.EnumerateFiles(folder)
                    listing.Add(file)
                Next
                listing.ReadSeconds = (DateTime.UtcNow - readUtc).TotalSeconds
                Return listing
            End Function
        End Class

        ''' <summary>Traegt in die Zielliste des laufenden Stapels nach, was er selbst in einen
        ''' Ordner gelegt oder daraus genommen hat. Ohne das saehe ein Stapel, der erst foto.jpg und
        ''' danach foto.cr2 samt foto.xmp ins selbe Ziel kopiert, das eben kopierte JPEG nicht, und
        ''' die XMP landete unter dem gemeinsamen Stamm. Ohne Stapel oder ohne Liste nichts.</summary>
        Private Shared Sub NoteInBatch(filePath As String, added As Boolean)
            Dim scope = _currentStemScope.Value
            If scope Is Nothing OrElse String.IsNullOrEmpty(filePath) Then Return
            Dim folder = Path.GetDirectoryName(filePath)
            If String.IsNullOrEmpty(folder) Then Return
            SyncLock scope.TargetListings
                Dim listing As StemListing = Nothing
                If Not scope.TargetListings.TryGetValue(folder, listing) Then Return
                If added Then listing.Add(filePath) Else listing.Remove(filePath)
            End SyncLock
        End Sub

        ' AsyncLocal und nicht je Faden: die Stapel laufen ueber Await und Task.Run.
        Private Shared ReadOnly _currentStemScope As New Threading.AsyncLocal(Of SidecarBatch)()

        ''' <summary>Fuer die Dauer des Using entscheidet ein Stapel ueber geteilte Stamm-XMP erst an
        ''' seinem Ende (siehe den Block oben). Um die Schleife eines Stapels legen, der Bilder
        ''' verschiebt, umbenennt oder loescht, und danach <see cref="SidecarBatch.Finish"/> rufen.</summary>
        Public Shared Function BatchScope() As SidecarBatch
            Dim scope = New SidecarBatch(_currentStemScope.Value)
            _currentStemScope.Value = scope
            Return scope
        End Function

        Private Shared Sub AddPending(scope As SidecarBatch, removal As PendingStemRemoval)
            SyncLock scope.Pending
                scope.Pending.Add(removal)
            End SyncLock
        End Sub

        ''' <summary>Das Ende eines Stapels: Ordner fuer Ordner EINE frische Aufzaehlung, und gleich
        ''' danach wird jede vorgemerkte Stamm-XMP dieses Ordners entfernt, deren Bild kein Geschwister
        ''' mehr hat und die beim Verschieben bytegleich am Ziel liegt. Liefert die Pfade, bei denen das
        ''' Entfernen scheiterte; was unsicher ist, bleibt stehen und zaehlt nicht als Fehler.
        '''
        ''' Je Ordner gelesen und SOFORT abgearbeitet, nicht alle Ordner vorweg: die Zeit zwischen
        ''' Lesen und Entfernen ist so nur die eines Ordners. Ganz schliessen laesst sie sich nicht,
        ''' das gilt fuer jede Pruefung vor einer Dateioperation, auch ausserhalb eines Stapels. Liegt
        ''' am urspruenglichen Bildpfad inzwischen wieder eine Datei, gehoert die XMP ihr und bleibt.</summary>
        Private Shared Function FinishPendingStemRemovals(pending As List(Of PendingStemRemoval)) As List(Of String)
            Dim failed As New List(Of String)()
            If pending Is Nothing OrElse pending.Count = 0 Then Return failed
            For Each folderGroup In pending.GroupBy(Function(r) If(Path.GetDirectoryName(r.ImagePath), ""), PathIdentity.Comparer)
                If String.IsNullOrEmpty(folderGroup.Key) Then Continue For
                ' Nach Stamm geordnet: ein grosser Ordner wird gelesen und dann je Bild nur noch
                ' nachgeschlagen. Dauert das Abarbeiten (der Papierkorb ist unter Linux ein eigener
                ' Prozess je Datei), wird nach StemListing.MaxAgeSeconds neu gelesen.
                Dim listing As StemListing = Nothing
                For Each removal In folderGroup
                    Try
                        ' ERST DEN ORDNER LESEN, dann fragen, was da ist. File.Exists sagt auch bei
                        ' einer weggebrochenen Freigabe "fehlt", und die XMP waere still uebergangen
                        ' worden; die Aufzaehlung wirft dann und wird gemeldet.
                        If listing Is Nothing OrElse listing.IsStale Then
                            Try
                                listing = StemListing.Read(folderGroup.Key)
                            Catch ex As Exception
                                ' Ordner nicht lesbar: im Zweifel bleiben alle stehen, und der
                                ' Nutzer erfaehrt es. Ob sie noch da sind, laesst sich nicht sagen.
                                DiagnosticLogService.LogException("Sidecar.FinishStemXmp", ex)
                                failed.AddRange(folderGroup.SkipWhile(Function(r) r IsNot removal).Select(Function(r) r.StemXmp).
                                                Except(failed, PathIdentity.Comparer))
                                Exit For
                            End Try
                        End If
                        ' ALLEIN DIE LISTE sagt, was da ist, nachgefuehrt um das, was dieser Abschluss
                        ' selbst entfernt oder zurueckgelegt hat (zwei Bilder desselben Stamms hatten
                        ' dieselbe XMP vorgemerkt). Kein File.Exists: das sagt bei einer Freigabe, die
                        ' nach dem Lesen wegbricht, still "fehlt". Ist sie weg, wirft der Versuch
                        ' unten, und die XMP wird gemeldet.
                        If Not listing.Contains(removal.StemXmp) Then Continue For
                        Dim sameStem = listing.WithStem(Path.GetFileNameWithoutExtension(removal.ImagePath))
                        If removal.AsideOriginal IsNot Nothing Then
                            ' Beiseitegelegt beim Ersetzen: das neue Bild am Ziel zaehlt nicht als
                            ' Geschwister, jedes andere schon. Dann gehoerte die XMP auch ihm.
                            If HasStemSiblingAmong(sameStem, removal.ImagePath) Then
                                If listing.Contains(removal.AsideOriginal) Then
                                    ' Der Name ist inzwischen belegt: die alte Fassung bleibt unter dem
                                    ' Beiseite-Namen liegen und wird gemeldet.
                                    failed.Add(removal.StemXmp)
                                Else
                                    File.Move(removal.StemXmp, removal.AsideOriginal)
                                    listing.Remove(removal.StemXmp)
                                    listing.Add(removal.AsideOriginal)
                                End If
                            Else
                                DeleteExisting(removal.StemXmp)
                                listing.Remove(removal.StemXmp)
                            End If
                            Continue For
                        End If
                        If listing.Contains(removal.ImagePath) Then Continue For
                        If HasStemSiblingAmong(sameStem, removal.ImagePath) Then Continue For
                        If removal.CopyAt IsNot Nothing Then
                            ' Die Kopie liegt in einem anderen Ordner. Fehlt sie, bleibt die Quelle und
                            ' wird gemeldet; ist sie anders (eine fremde XMP am Ziel), hat das Mitnehmen
                            ' das schon gemeldet.
                            If Not File.Exists(removal.CopyAt) Then Throw New IOException("Kopie am Ziel nicht da: " & removal.CopyAt)
                            If Not File.ReadAllBytes(removal.StemXmp).AsSpan().SequenceEqual(File.ReadAllBytes(removal.CopyAt)) Then Continue For
                            DeleteExisting(removal.StemXmp)
                        ElseIf removal.UseTrash Then
                            If Not removal.MoveToTrash(removal.StemXmp) Then Throw New IOException("Papierkorb nicht verfuegbar: " & removal.StemXmp)
                        Else
                            DeleteExisting(removal.StemXmp)
                        End If
                        listing.Remove(removal.StemXmp)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Sidecar.FinishStemXmp", ex)
                        failed.Add(removal.StemXmp)
                    End Try
                Next
            Next
            Return failed
        End Function

        ''' <summary>Loescht eine Datei, von der die Liste sagt, dass sie da ist. File.Delete wirft
        ''' bei einer fehlenden Datei NICHT; ist sie danach nicht weg, ebenso wenig. Deshalb danach
        ''' geprueft: steht sie noch da oder laesst sich das nicht sagen, ist es ein Fehler.</summary>
        Private Shared Sub DeleteExisting(filePath As String)
            File.Delete(filePath)
            If File.Exists(filePath) Then Throw New IOException("nicht entfernt: " & filePath)
        End Sub

        ''' <summary>Liegt neben der Datei noch eine ANDERE Datei desselben Stamms (foto.jpg neben
        ''' foto.cr2)? Begleitdateien und die Datei selbst zaehlen nicht.</summary>
        Private Shared Function HasStemSibling(imagePath As String) As Boolean
            Try
                Dim folder = Path.GetDirectoryName(imagePath)
                Dim stem = Path.GetFileNameWithoutExtension(imagePath)
                If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then Return False
                Return HasStemSiblingAmong(Directory.EnumerateFiles(folder, stem & ".*"), imagePath)
            Catch ex As Exception
                ' Im Zweifel annehmen, dass eine andere Datei die XMP noch braucht: dann wird kopiert
                ' statt verschoben und nicht geloescht - lieber eine Kopie zu viel als eine verloren.
                DiagnosticLogService.LogException("Sidecar.HasStemSibling", ex)
                Return True
            End Try
        End Function

        ''' <summary>Dieselbe Frage ueber eine schon gelesene Liste von Pfaden desselben Ordners.</summary>
        Private Shared Function HasStemSiblingAmong(candidates As IEnumerable(Of String), imagePath As String) As Boolean
            Dim stem = Path.GetFileNameWithoutExtension(imagePath)
            For Each candidate In candidates
                If String.Equals(candidate, imagePath, PathIdentity.Comparison) Then Continue For
                If Not String.Equals(Path.GetFileNameWithoutExtension(candidate), stem, PathIdentity.Comparison) Then Continue For
                Dim ext = Path.GetExtension(candidate)
                If String.Equals(ext, ".xmp", StringComparison.OrdinalIgnoreCase) OrElse
                   String.Equals(ext, Extension, StringComparison.OrdinalIgnoreCase) Then Continue For
                Return True
            Next
            Return False
        End Function

        ''' <summary>Nur die Drehung aus dem Sidecar (0/90/180/270; 0 wenn keiner da ist). Der
        ''' ANZEIGE-Weg braucht ausschliesslich diesen einen Wert: Viewer, Filmstreifen und Kacheln
        ''' zeigen die schnelle eingebettete RAW-Vorschau, nicht die entwickelte Datei - Belichtung
        ''' und Farbe aus dem Rezept wirken dort bewusst nicht, die Geometrie muss aber stimmen,
        ''' sonst haette das Drehen im Viewer sichtbar keine Wirkung.
        '''
        ''' Ergebnisse werden je Sidecar-Zeitstempel gemerkt: der Aufruf sitzt im Thumbnail-Pfad und
        ''' liefe sonst pro Kachel durch einen XML-Parse.</summary>
        Public Shared Function ReadRotationDegrees(rawPath As String) As Integer
            Return If(ReadDisplayFacts(rawPath)?.Degrees, 0)
        End Function

        ''' <summary>Soll die Kachel dieser Datei ENTWICKELT gezeigt werden, auch wenn die Einstellung
        ''' "Entwickelte RAW-Vorschau" aus ist? Der Vermerk steht als Attribut in der .fpxmp und wird
        ''' gesetzt, wenn der Stapel "Anpassungen anwenden" ein Rezept schreibt: sonst saehe man in
        ''' der Galerie nichts von dem, was man gerade ueber eine Serie gelegt hat.
        '''
        ''' EINMAL GESETZT, BLEIBT ER. Jeder spaetere Schreiber (Editor, Katalog, naechster Stapel)
        ''' uebernimmt ihn, siehe TryWriteCore. Sonst fiele die Kachel nach dem ersten Nachbessern im
        ''' Editor auf das Kamerabild zurueck, obwohl gerade dort mehr Bearbeitung drinsteckt.
        '''
        ''' In der .fpxmp und nicht im Kachelspeicher, weil er mit der Datei wandern muss (Verschieben,
        ''' Umbenennen, Leeren des Caches) und weil die .fpxmp je Kachel ohnehin schon gelesen wird,
        ''' fuer die Drehung. Beide Werte kommen deshalb aus demselben gemerkten Lesevorgang.</summary>
        Public Shared Function ReadDevelopedThumbnail(rawPath As String) As Boolean
            Return If(ReadDisplayFacts(rawPath)?.DevelopedThumbnail, False)
        End Function

        Private Shared Function ReadDisplayFacts(rawPath As String) As CachedDisplayFacts
            If String.IsNullOrWhiteSpace(rawPath) Then Return Nothing
            Dim sidecar = SidecarPathFor(rawPath)
            Dim stampTicks As Long
            Try
                If Not File.Exists(sidecar) Then Return Nothing
                stampTicks = File.GetLastWriteTimeUtc(sidecar).Ticks
            Catch
                Return Nothing
            End Try

            Dim cached As CachedDisplayFacts = Nothing
            If _displayFactsCache.TryGetValue(sidecar, cached) AndAlso cached.StampTicks = stampTicks Then Return cached

            Dim developedThumbnail As Boolean
            Dim editedAttribute As Boolean? = Nothing
            Dim adjustments As ImageAdjustments = Nothing
            ReadRecipeCore(rawPath, adjustments, developedThumbnail, editedAttribute)
            Dim degrees As Integer = 0
            If adjustments IsNot Nothing Then
                ' DIESELBE REGEL WIE IM EDITOR (EditorViewModel.AppliedTransformState): Drehung und
                ' Spiegelung sind nicht vertauschbar. Eine einzelne Spiegelung kehrt den Drehsinn
                ' um, also geht die Drehung eines Schrittes mit umgekehrtem Vorzeichen ein, wenn
                ' vor ihm eine ungerade Zahl von Spiegelungen steht. Blosses Aufsummieren gab
                ' "spiegeln, dann 90 Grad" dieselbe Zahl wie "90 Grad, dann spiegeln", und die
                ' Kachel lag danach quer.
                ' Die oberen Felder zaehlen nicht mehr mit: sie werden beim Laden geraeumt
                ' (FpxService), die Drehung steht ausschliesslich in den Schritten.
                Dim flipH = False, flipV = False
                If adjustments.GeometryOperations IsNot Nothing Then
                    For Each operation In adjustments.GeometryOperations
                        If operation Is Nothing OrElse operation.Adjustments Is Nothing Then Continue For
                        If Not String.Equals(operation.Kind, "transform", StringComparison.OrdinalIgnoreCase) Then Continue For
                        Dim a = operation.Adjustments
                        degrees += If(flipH Xor flipV, -a.RotationDegrees, a.RotationDegrees)
                        flipH = flipH Xor a.FlipHorizontal
                        flipV = flipV Xor a.FlipVertical
                    Next
                End If
                degrees = ImageOrientationService.NormalizeQuarterTurn(degrees)
            End If
            Dim facts = New CachedDisplayFacts(stampTicks, degrees, developedThumbnail, editedAttribute)
            _displayFactsCache(sidecar) = facts
            Return facts
        End Function

        Private NotInheritable Class CachedDisplayFacts
            Public ReadOnly StampTicks As Long
            Public ReadOnly Degrees As Integer
            Public ReadOnly DevelopedThumbnail As Boolean
            ''' Der Vermerk, wie er in der Datei steht; Nothing in einer aelteren Datei ohne ihn.
            Public ReadOnly EditedAttribute As Boolean?
            ''' Ohne Vermerk: das Ergebnis des Vergleichs mit den Startwerten, erst bei der ersten
            ''' Frage danach gerechnet (IsEditedFile). Die Drehung der Kachel braucht ihn nicht, und er
            ''' liest die Metadaten der RAW.
            Public ComparedEdited As Boolean?
            Public Sub New(stampTicks As Long, degrees As Integer, developedThumbnail As Boolean,
                           editedAttribute As Boolean?)
                Me.StampTicks = stampTicks
                Me.Degrees = degrees
                Me.DevelopedThumbnail = developedThumbnail
                Me.EditedAttribute = editedAttribute
            End Sub
        End Class

        Private Shared ReadOnly _displayFactsCache As New ConcurrentDictionary(Of String, CachedDisplayFacts)(PathIdentity.Comparer)

        Private Const DevelopedThumbnailAttribute As String = "developedThumbnail"
        Private Const EditedAttribute As String = "edited"

        ''' <summary>Der Vermerk "bearbeitet", wie er in der Begleitdatei steht; Nothing ohne Datei oder
        ''' in einer aelteren Datei ohne Vermerk.</summary>
        Private Shared Function ReadEditedAttribute(rawPath As String) As Boolean?
            Return ReadDisplayFacts(rawPath)?.EditedAttribute
        End Function

        ''' <summary>Die Startwerte einer Datei ohne Bearbeitung: bei einer RAW die des Editors
        ''' (ImageAdjustments.ForUneditedRaw, mit Grundbelichtung und Kamerakalibrierung), sonst ein
        ''' neutrales Rezept.</summary>
        Public Shared Function StartValuesFor(path As String) As ImageAdjustments
            Return If(RawPreviewService.IsSupportedRaw(path), ImageAdjustments.ForUneditedRaw(path), New ImageAdjustments())
        End Function

        ''' <summary>Ist diese Datei bearbeitet? Ein Projektbuendel (.fpx) immer; eine RAW oder PSD,
        ''' wenn ihre Begleitdatei den Vermerk traegt oder, in einer aelteren Datei ohne ihn, ihr Rezept
        ''' von den Startwerten abweicht. Eine Begleitdatei allein ist KEIN Beleg: sie entsteht schon
        ''' durch einen Stern oder ein Stichwort. Andere Formate tragen keine Begleitdatei und gelten
        ''' als unbearbeitet. Liest dieselbe gemerkte Datei wie die Drehung der Kachel.</summary>
        Public Shared Function IsEditedFile(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            If FpxService.IsFpx(path) Then Return True
            If Not IsSidecarFormat(path) Then Return False
            Dim facts = ReadDisplayFacts(path)
            If facts Is Nothing Then Return False
            If facts.EditedAttribute.HasValue Then Return facts.EditedAttribute.Value
            ' Eine aeltere Begleitdatei ohne Vermerk: der nachsichtige Vergleich mit den Startwerten,
            ' gemerkt mit den uebrigen Werten dieses Zeitstempels und NICHT zurueckgeschrieben - im
            ' Bestand schreibt nur ein bewusstes Speichern. Beim naechsten Speichern steht der echte
            ' Vermerk darin.
            If Not facts.ComparedEdited.HasValue Then
                Dim adjustments = TryRead(path)
                facts.ComparedEdited = adjustments IsNot Nothing AndAlso
                                       adjustments.IsEditedComparedTo(StartValuesFor(path), lenient:=True)
            End If
            Return facts.ComparedEdited.Value
        End Function

        ''' <summary>Der Vermerk fuer ein Rezept, das ein Bearbeitungsweg gerade schreibt: weicht es
        ''' von den Startwerten der Datei ab? Streng, die Startwerte sind ja die von jetzt.</summary>
        Public Shared Function IsEditedRecipe(path As String, adjustments As ImageAdjustments) As Boolean
            Return adjustments IsNot Nothing AndAlso adjustments.IsEditedComparedTo(StartValuesFor(path))
        End Function

        ''' <summary>Übernimmt die Entwicklungseinstellungen aus einer XMP-Sidecar
        ''' ("foto.cr2.xmp" oder "foto.xmp") in eine neue .fpxmp - EINMALIG, solange es noch keine gibt.
        '''
        ''' Damit gibt es genau EINEN Ort, an dem crs:-Werte in unser Rezeptformat übersetzt werden: der
        ''' Ordner-Scan, der die Beistelldateien ohnehin schon wegen Bewertung und Stichworten anfasst.
        ''' Editor und Viewer müssen XMP dadurch überhaupt nicht kennen und arbeiten weiter allein auf
        ''' der .fpxmp.
        '''
        ''' Drei Bedingungen, alle nötig: nur für RAW/PSD (bei schreibbaren Formaten gibt es kein Rezept
        ''' daneben), nur wenn noch keine .fpxmp existiert (ein eigenes Rezept wird NIE überschrieben),
        ''' und nur wenn die Sidecar wirklich Reglerwerte trägt. Letzteres ist wichtig: die allermeisten
        ''' XMP-Beistelldateien enthalten nur Katalogdaten, und eine neutrale .fpxmp neben jedes Foto zu
        ''' legen wäre eine Dateiflut ohne jeden Inhalt.</summary>
        Public Shared Function TryImportFromXmpSidecar(rawPath As String) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsSidecarFormat(rawPath) Then Return False
            If Exists(rawPath) Then Return False
            Try
                Dim xmpPath = XmpSidecarService.FindSidecar(rawPath)
                If String.IsNullOrEmpty(xmpPath) Then Return False
                ' Der RAW-Pfad geht mit: trägt die Sidecar ein absolutes crs:Temperature ohne
                ' crs:AsShotTemperature, holt der Import die Aufnahmetemperatur aus der RAW-Datei
                ' selbst, statt D65 anzunehmen.
                Dim look = XmpPresetService.LoadLook(xmpPath, imagePath:=rawPath)
                If look Is Nothing OrElse Not HasAnyAdjustment(look) Then Return False
                ' Nur anlegen, wenn noch immer keine .fpxmp da ist: zwischen der Pruefung oben und
                ' hier kann eine entstanden sein, und ein eigenes Rezept wird NIE ueberschrieben.
                ' Bearbeitet: die Uebernahme laeuft nur, wenn die fremde Datei Regler setzt.
                Return TryWrite(rawPath, look, requireAbsent:=True, edited:=True)
            Catch
                Return False
            End Try
        End Function

        ''' Vergleicht gegen ein frisches ImageAdjustments statt gegen eine handgepflegte Feldliste -
        ''' ein neuer Regler ist damit automatisch dabei und kann nicht vergessen werden.
        Private Shared Function HasAnyAdjustment(look As ImageAdjustments) As Boolean
            Dim neutral As New ImageAdjustments()
            For Each p In ImageAdjustments.PixelAdjustmentProperties()
                If Not Equals(p.GetValue(look), p.GetValue(neutral)) Then Return True
            Next
            Return False
        End Function

        ''' <summary>Was beim Lesen einer .fpxmp herauskam. Fehlend und unlesbar MUESSEN getrennt
        ''' bleiben: nur eine fehlende Datei darf ein Schreiber neu anlegen.</summary>
        Public Enum SidecarReadResult
            Missing
            Read
            ''' Vorhanden, aber nicht verstanden: defektes XML, unbekannte oder neuere Formatversion,
            ''' fehlendes oder unlesbares Rezept, Lesefehler.
            Unreadable
        End Enum

        ''' <summary>Liest das Rezept aus dem Sidecar. Nothing, wenn keiner da ist, die Version
        ''' unbekannt oder die Datei defekt ist - der Editor startet dann wie ohne Sidecar. Wer danach
        ''' SCHREIBEN will, nimmt <see cref="ReadRecipe"/> und unterscheidet die Faelle.</summary>
        Public Shared Function TryRead(rawPath As String) As ImageAdjustments
            Dim developedThumbnail As Boolean
            Return TryReadCore(rawPath, developedThumbnail)
        End Function

        ''' <summary>Wie <see cref="TryRead"/>, sagt aber, ob die Datei fehlt oder nur nicht lesbar ist.</summary>
        Public Shared Function ReadRecipe(rawPath As String, ByRef adjustments As ImageAdjustments) As SidecarReadResult
            Dim developedThumbnail As Boolean
            Return ReadRecipeCore(rawPath, adjustments, developedThumbnail)
        End Function

        Private Shared Function TryReadCore(rawPath As String, ByRef developedThumbnail As Boolean) As ImageAdjustments
            Dim adjustments As ImageAdjustments = Nothing
            ReadRecipeCore(rawPath, adjustments, developedThumbnail)
            Return adjustments
        End Function

        ''' <summary>Gibt es die Datei? Read heisst hier nur "vorhanden".
        '''
        ''' NICHT File.Exists: das sagt auch bei einem Zugriffsfehler False (Netzlaufwerk weg,
        ''' fehlende Rechte am Ordner), und dann sah eine vorhandene Datei wie eine fehlende aus und
        ''' wurde neu angelegt. File.GetAttributes wirft dagegen und sagt mit dem Ausnahmetyp, was
        ''' los ist: Datei oder Ordner nicht gefunden heisst Missing, jeder andere Fehler Unreadable.
        '''
        ''' AUCH KEINE ORDNERAUFZAEHLUNG mit dem Namen als Muster: unter Unix geht die durch den
        ''' ganzen Ordner und filtert erst danach. Ein Stapel ueber tausend Bilder auf einem
        ''' Netzlaufwerk las so tausendmal den ganzen Ordner.
        '''
        ''' Ein ORDNER mit dem Namen der Begleitdatei ist Unreadable: er laesst sich weder lesen
        ''' noch ersetzen, und ihn als "fehlt" zu melden, liesse einen Schreiber gegen ihn laufen.</summary>
        Private Shared Function ProbeSidecar(sidecar As String) As SidecarReadResult
            Try
                Dim attributes = File.GetAttributes(sidecar)
                If (attributes And FileAttributes.Directory) = FileAttributes.Directory Then Return SidecarReadResult.Unreadable
                Return SidecarReadResult.Read
            Catch ex As FileNotFoundException
                Return SidecarReadResult.Missing
            Catch ex As DirectoryNotFoundException
                Return SidecarReadResult.Missing
            Catch
                Return SidecarReadResult.Unreadable
            End Try
        End Function

        Private Shared Function ReadRecipeCore(rawPath As String, ByRef adjustments As ImageAdjustments,
                                               ByRef developedThumbnail As Boolean) As SidecarReadResult
            Dim edited As Boolean? = Nothing
            Return ReadRecipeCore(rawPath, adjustments, developedThumbnail, edited)
        End Function

        Private Shared Function ReadRecipeCore(rawPath As String, ByRef adjustments As ImageAdjustments,
                                               ByRef developedThumbnail As Boolean,
                                               ByRef edited As Boolean?) As SidecarReadResult
            adjustments = Nothing
            developedThumbnail = False
            edited = Nothing
            Dim sidecar = SidecarPathFor(rawPath)
            Select Case ProbeSidecar(sidecar)
                Case SidecarReadResult.Missing
                    Return SidecarReadResult.Missing
                Case SidecarReadResult.Unreadable
                    Return SidecarReadResult.Unreadable
            End Select
            Try
                Dim doc = XDocument.Load(sidecar)
                Dim root = doc.Root
                If root Is Nothing OrElse root.Name <> Ns + "recipe" Then Return SidecarReadResult.Unreadable
                developedThumbnail = String.Equals(root.Attribute(DevelopedThumbnailAttribute)?.Value, "true",
                                                   StringComparison.OrdinalIgnoreCase)
                Dim editedValue = root.Attribute(EditedAttribute)?.Value
                If editedValue IsNot Nothing Then edited = String.Equals(editedValue, "true", StringComparison.OrdinalIgnoreCase)
                Dim version As Integer
                If Not Integer.TryParse(root.Attribute("version")?.Value, Globalization.NumberStyles.Integer,
                                        Globalization.CultureInfo.InvariantCulture, version) Then
                    Return SidecarReadResult.Unreadable
                End If
                If version < 1 OrElse version > FormatVersion Then Return SidecarReadResult.Unreadable
                Dim adjustmentsNode = root.Element(Ns + "adjustments")
                If adjustmentsNode Is Nothing Then Return SidecarReadResult.Unreadable
                adjustments = FpxService.DeserializeAdjustments(adjustmentsNode.Value)
                Return If(adjustments Is Nothing, SidecarReadResult.Unreadable, SidecarReadResult.Read)
            Catch
                adjustments = Nothing
                Return SidecarReadResult.Unreadable
            End Try
        End Function

        ''' <summary>Liest nur den Katalogblock. Nothing bedeutet: keine Sidecar, alte Version-1-Datei
        ''' ohne Katalogdaten oder defekte Datei. Ein vorhandener Block liefert auch bewusst leere
        ''' Werte (Rating 0, Favorit False, keine Stichwoerter).</summary>
        Public Shared Function TryReadCatalog(rawPath As String) As RawSidecarCatalogData
            Try
                Dim sidecar = SidecarPathFor(rawPath)
                If Not File.Exists(sidecar) Then Return Nothing
                Dim doc = XDocument.Load(sidecar)
                Dim root = doc.Root
                If root Is Nothing OrElse root.Name <> Ns + "recipe" Then Return Nothing
                Dim version = CInt(root.Attribute("version")?.Value)
                If version < 1 OrElse version > FormatVersion Then Return Nothing
                Dim catalogNode = root.Element(Ns + "catalog")
                If catalogNode Is Nothing Then Return Nothing

                Dim result As New RawSidecarCatalogData()
                Dim ratingNode = catalogNode.Element(Ns + "rating")
                Dim rating As Integer
                If ratingNode IsNot Nothing AndAlso Integer.TryParse(ratingNode.Value, rating) Then
                    result.Rating = Math.Max(0, Math.Min(5, rating))
                End If
                Dim favoriteNode = catalogNode.Element(Ns + "favorite")
                Dim favorite As Boolean
                If favoriteNode IsNot Nothing AndAlso Boolean.TryParse(favoriteNode.Value, favorite) Then
                    result.IsFavorite = favorite
                End If
                Dim colorLabelNode = catalogNode.Element(Ns + "colorLabel")
                If colorLabelNode IsNot Nothing Then result.ColorLabel = colorLabelNode.Value.Trim()
                Dim pickNode = catalogNode.Element(Ns + "pick")
                Dim pick As Integer
                If pickNode IsNot Nothing AndAlso Integer.TryParse(pickNode.Value, pick) Then
                    result.PickState = LibraryService.NormalizePickState(pick)
                End If
                Dim keywordsNode = catalogNode.Element(Ns + "keywords")
                If keywordsNode IsNot Nothing Then
                    result.HasKeywords = True
                    result.Keywords = NormalizeKeywords(keywordsNode.Elements(Ns + "keyword").Select(Function(node) node.Value))
                End If
                Return result
            Catch
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
