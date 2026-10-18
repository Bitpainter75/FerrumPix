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
        Public Shared Function TryWrite(rawPath As String, adjustments As ImageAdjustments,
                                        Optional developedThumbnail As Boolean? = Nothing,
                                        Optional requireAbsent As Boolean = False) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse adjustments Is Nothing Then Return False
            Try
                SyncLock _writeLock
                    Dim catalog = LibraryService.Instance.GetSidecarCatalogData(rawPath)
                    Return TryWriteCore(rawPath, adjustments, catalog, developedThumbnail, requireAbsent)
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
                Return TryWriteCore(rawPath, adjustments, catalog, Nothing,
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
                                               modify As Action(Of ImageAdjustments)) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse createWhenMissing Is Nothing OrElse modify Is Nothing Then Return False
            SyncLock _writeLock
                Dim adjustments As ImageAdjustments = Nothing
                Dim state = ReadRecipe(rawPath, adjustments)
                If state = SidecarReadResult.Unreadable Then Return False
                If state = SidecarReadResult.Missing Then adjustments = createWhenMissing()
                If adjustments Is Nothing Then Return False
                modify(adjustments)
                Return TryWrite(rawPath, adjustments, requireAbsent:=state = SidecarReadResult.Missing)
            End SyncLock
        End Function

        Private Shared Function TryWriteCore(rawPath As String, adjustments As ImageAdjustments,
                                             catalog As RawSidecarCatalogData,
                                             developedThumbnail As Boolean?,
                                             Optional requireAbsent As Boolean = False) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse adjustments Is Nothing Then Return False
            SyncLock _writeLock
                Dim temp As String = Nothing
                Try
                    Dim keepDevelopedThumbnail = If(developedThumbnail.HasValue, developedThumbnail.Value,
                                                    ReadDevelopedThumbnail(rawPath))
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
                        New XElement(Ns + "colorLabel", If(catalog?.ColorLabel, "")),
                        New XElement(Ns + "pick", LibraryService.NormalizePickState(
                            If(catalog Is Nothing, 0, catalog.PickState.GetValueOrDefault())).ToString(Globalization.CultureInfo.InvariantCulture)),
                        keywordsNode)
                    Dim doc = New XDocument(
                        New XDeclaration("1.0", "utf-8", Nothing),
                        New XElement(Ns + "recipe",
                            New XAttribute("version", FormatVersion),
                            New XAttribute("generator", "FerrumPix"),
                            If(keepDevelopedThumbnail, New XAttribute(DevelopedThumbnailAttribute, "true"), Nothing),
                            New XElement(Ns + "source",
                                New XAttribute("fileName", Path.GetFileName(rawPath))),
                            New XElement(Ns + "savedUtc", DateTime.UtcNow.ToString("O")),
                            catalogNode,
                            New XElement(Ns + "adjustments",
                                New XAttribute("format", "fpx-json"),
                                New XCData(json))))

                    Dim target = SidecarPathFor(rawPath)
                    temp = target & ".tmp"
                    Using writer = XmlWriter.Create(temp, New XmlWriterSettings With {.Indent = True})
                        doc.Save(writer)
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
                    DiagnosticLogService.LogException("Sidecar.Write", ex)
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
        ' Ein Fehler an einer Begleitdatei laesst die Hauptoperation nicht scheitern (sie ist schon
        ' geschehen, wenn diese Methoden laufen), wird aber protokolliert statt geschluckt.
        '
        ' DIE ERSETZTE FORM KANN MEHREREN DATEIEN GEHOEREN: "foto.xmp" passt zu foto.cr2 UND foto.jpg.
        ' Sie wird deshalb nur verschoben oder geloescht, wenn keine andere Datei desselben Stamms
        ' am alten Ort bleibt; sonst kopiert beziehungsweise stehen gelassen. Eine vorhandene XMP am
        ' Ziel wird nie ueberschrieben: sie gehoert dort womoeglich einer anderen Datei.

        Public Shared Sub AccompanyMove(sourcePath As String, targetPath As String)
            Try
                Dim sidecar = SidecarPathFor(sourcePath)
                If File.Exists(sidecar) Then File.Move(sidecar, SidecarPathFor(targetPath), overwrite:=True)
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyMove", ex)
            End Try
            CarryXmpCompanions(sourcePath, targetPath, moveFiles:=True)
        End Sub

        Public Shared Sub AccompanyCopy(sourcePath As String, targetPath As String)
            Try
                Dim sidecar = SidecarPathFor(sourcePath)
                If File.Exists(sidecar) Then File.Copy(sidecar, SidecarPathFor(targetPath), overwrite:=True)
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.AccompanyCopy", ex)
            End Try
            CarryXmpCompanions(sourcePath, targetPath, moveFiles:=False)
        End Sub

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
            Dim companions As New List(Of String) From {SidecarPathFor(rawPath), rawPath & ".xmp"}
            Dim stemXmp = StemXmpPathFor(rawPath)
            If stemXmp IsNot Nothing AndAlso Not HasStemSibling(rawPath) Then companions.Add(stemXmp)
            For Each companion In companions
                Try
                    If Not File.Exists(companion) Then Continue For
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

        ''' <summary>Nimmt die fremden XMP-Dateien mit. Siehe den Block oben fuer die Regeln.</summary>
        Private Shared Sub CarryXmpCompanions(sourcePath As String, targetPath As String, moveFiles As Boolean)
            ' Angehaengte Form: gehoert eindeutig dieser einen Datei.
            CarryOne(sourcePath & ".xmp", targetPath & ".xmp", moveFiles, removeSourceIfSame:=moveFiles)
            ' Ersetzte Form: geteilt moeglich.
            Dim sourceStem = StemXmpPathFor(sourcePath)
            Dim targetStem = StemXmpPathFor(targetPath)
            If sourceStem Is Nothing OrElse targetStem Is Nothing Then Return
            If String.Equals(sourceStem, targetStem, PathIdentity.Comparison) Then Return
            Dim siblingStays = HasStemSibling(sourcePath)
            CarryOne(sourceStem, targetStem, moveFiles AndAlso Not siblingStays,
                     removeSourceIfSame:=moveFiles AndAlso Not siblingStays)
        End Sub

        Private Shared Sub CarryOne(source As String, target As String, moveFile As Boolean, removeSourceIfSame As Boolean)
            Try
                If Not File.Exists(source) Then Return
                If File.Exists(target) Then
                    ' Liegt dort schon DIESELBE Datei (ein Stapel, der foto.cr2 und foto.jpg nacheinander
                    ' umbenennt, hat sie beim ersten Schritt kopiert), bleibt die Quelle sonst als Waise
                    ' zurueck. Eine ANDERE Datei am Ziel gehoert jemand anderem und bleibt.
                    If removeSourceIfSame AndAlso File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(target)) Then
                        File.Delete(source)
                    Else
                        DiagnosticLogService.LogAlways("Sidecar.Accompany",
                            $"XMP am Ziel vorhanden, nicht ueberschrieben: {Path.GetFileName(target)}")
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
            End Try
        End Sub

        ''' <summary>"ordner/foto.cr2" -> "ordner/foto.xmp"; Nothing, wenn die Datei selbst eine .xmp
        ''' ist oder keine Endung hat (dann gibt es keine ersetzte Form).</summary>
        Private Shared Function StemXmpPathFor(imagePath As String) As String
            If String.IsNullOrWhiteSpace(imagePath) Then Return Nothing
            Dim ext = Path.GetExtension(imagePath)
            If String.IsNullOrEmpty(ext) OrElse String.Equals(ext, ".xmp", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Return Path.ChangeExtension(imagePath, ".xmp")
        End Function

        ''' <summary>Liegt neben der Datei noch eine ANDERE Datei desselben Stamms (foto.jpg neben
        ''' foto.cr2)? Begleitdateien und die Datei selbst zaehlen nicht.</summary>
        Private Shared Function HasStemSibling(imagePath As String) As Boolean
            Try
                Dim folder = Path.GetDirectoryName(imagePath)
                Dim stem = Path.GetFileNameWithoutExtension(imagePath)
                If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then Return False
                For Each candidate In Directory.EnumerateFiles(folder, stem & ".*")
                    If String.Equals(candidate, imagePath, PathIdentity.Comparison) Then Continue For
                    If Not String.Equals(Path.GetFileNameWithoutExtension(candidate), stem, PathIdentity.Comparison) Then Continue For
                    Dim ext = Path.GetExtension(candidate)
                    If String.Equals(ext, ".xmp", StringComparison.OrdinalIgnoreCase) OrElse
                       String.Equals(ext, Extension, StringComparison.OrdinalIgnoreCase) Then Continue For
                    Return True
                Next
            Catch ex As Exception
                ' Im Zweifel annehmen, dass eine andere Datei die XMP noch braucht: dann wird kopiert
                ' statt verschoben und nicht geloescht - lieber eine Kopie zu viel als eine verloren.
                DiagnosticLogService.LogException("Sidecar.HasStemSibling", ex)
                Return True
            End Try
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
            Dim adjustments = TryReadCore(rawPath, developedThumbnail)
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
            Dim facts = New CachedDisplayFacts(stampTicks, degrees, developedThumbnail)
            _displayFactsCache(sidecar) = facts
            Return facts
        End Function

        Private NotInheritable Class CachedDisplayFacts
            Public ReadOnly StampTicks As Long
            Public ReadOnly Degrees As Integer
            Public ReadOnly DevelopedThumbnail As Boolean
            Public Sub New(stampTicks As Long, degrees As Integer, developedThumbnail As Boolean)
                Me.StampTicks = stampTicks
                Me.Degrees = degrees
                Me.DevelopedThumbnail = developedThumbnail
            End Sub
        End Class

        Private Shared ReadOnly _displayFactsCache As New ConcurrentDictionary(Of String, CachedDisplayFacts)(PathIdentity.Comparer)

        Private Const DevelopedThumbnailAttribute As String = "developedThumbnail"

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
                Return TryWrite(rawPath, look, requireAbsent:=True)
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
        ''' wurde neu angelegt. Gefragt wird der Ordner selbst; scheitert die Frage, ist das
        ''' Unreadable.</summary>
        Private Shared Function ProbeSidecar(sidecar As String) As SidecarReadResult
            Try
                Dim folder = Path.GetDirectoryName(sidecar)
                If String.IsNullOrEmpty(folder) Then Return If(File.Exists(sidecar), SidecarReadResult.Read, SidecarReadResult.Missing)
                Dim name = Path.GetFileName(sidecar)
                ' Fehlt der Ordner, fehlt auch die Datei. Directory.Exists hat dieselbe Schwaeche wie
                ' File.Exists; die Aufzaehlung darunter wirft dagegen bei einem Zugriffsfehler.
                If Not Directory.Exists(folder) Then Return SidecarReadResult.Missing
                For Each candidate In Directory.EnumerateFiles(folder, name)
                    If String.Equals(Path.GetFileName(candidate), name, PathIdentity.Comparison) Then Return SidecarReadResult.Read
                Next
                Return SidecarReadResult.Missing
            Catch
                Return SidecarReadResult.Unreadable
            End Try
        End Function

        Private Shared Function ReadRecipeCore(rawPath As String, ByRef adjustments As ImageAdjustments,
                                               ByRef developedThumbnail As Boolean) As SidecarReadResult
            adjustments = Nothing
            developedThumbnail = False
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
