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
    ''' Der Sidecar ist NICHT abschaltbar (die Einstellung dafuer ist am 2026-07-20 entfallen): fuer
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
        Public Shared Function TryWrite(rawPath As String, adjustments As ImageAdjustments) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse adjustments Is Nothing Then Return False
            Try
                Dim catalog = New RawSidecarCatalogData With {
                    .Rating = Math.Max(0, Math.Min(5, LibraryService.Instance.GetRating(rawPath))),
                    .IsFavorite = LibraryService.Instance.GetFavorite(rawPath),
                    .ColorLabel = If(LibraryService.Instance.GetColorLabel(rawPath), ""),
                    .Keywords = NormalizeKeywords(LibraryService.Instance.GetTags(rawPath)),
                    .HasKeywords = True
                }
                Return TryWriteCore(rawPath, adjustments, catalog)
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Schreibt eine bewusste Katalogaenderung in die .fpxmp und erhaelt dabei das
        ''' vorhandene Bearbeitungsrezept. Gibt es noch keine Sidecar, wird eine mit neutralem Rezept
        ''' angelegt - Katalogdaten sind seit Formatversion 2 selbst ein legitimer Sidecar-Inhalt.</summary>
        Public Shared Function TryWriteCatalog(rawPath As String, rating As Integer, isFavorite As Boolean, colorLabel As String,
                                               keywords As IEnumerable(Of String)) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not IsSidecarFormat(rawPath) Then Return False
            Dim adjustments = If(TryRead(rawPath), New ImageAdjustments())
            Dim catalog = New RawSidecarCatalogData With {
                .Rating = Math.Max(0, Math.Min(5, rating)),
                .IsFavorite = isFavorite,
                .ColorLabel = If(colorLabel, "").Trim(),
                .Keywords = NormalizeKeywords(keywords),
                .HasKeywords = True
            }
            Return TryWriteCore(rawPath, adjustments, catalog)
        End Function

        Private Shared Function TryWriteCore(rawPath As String, adjustments As ImageAdjustments,
                                             catalog As RawSidecarCatalogData) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) OrElse adjustments Is Nothing Then Return False
            SyncLock _writeLock
                Try
                    Dim json = FpxService.SerializeAdjustments(adjustments)
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
                        keywordsNode)
                    Dim doc = New XDocument(
                        New XDeclaration("1.0", "utf-8", Nothing),
                        New XElement(Ns + "recipe",
                            New XAttribute("version", FormatVersion),
                            New XAttribute("generator", "FerrumPix"),
                            New XElement(Ns + "source",
                                New XAttribute("fileName", Path.GetFileName(rawPath))),
                            New XElement(Ns + "savedUtc", DateTime.UtcNow.ToString("O")),
                            catalogNode,
                            New XElement(Ns + "adjustments",
                                New XAttribute("format", "fpx-json"),
                                New XCData(json))))

                    Dim target = SidecarPathFor(rawPath)
                    Dim temp = target & ".tmp"
                    Using writer = XmlWriter.Create(temp, New XmlWriterSettings With {.Indent = True})
                        doc.Save(writer)
                    End Using
                    File.Move(temp, target, overwrite:=True)
                    ' Den gemerkten Drehwinkel verwerfen, statt auf einen geaenderten Zeitstempel zu
                    ' hoffen: zwei Schreibvorgaenge kurz hintereinander koennen auf grob aufloesenden
                    ' Dateisystemen dieselbe mtime tragen.
                    Dim ignored As CachedRotation = Nothing
                    _rotationCache.TryRemove(target, ignored)
                    Return True
                Catch
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
        ' Wie XMP-Sidecars in Lightroom: Verschieben/Kopieren/Umbenennen/Loeschen der RAW-Datei
        ' nimmt die Begleitdatei mit. Immer best effort - ein Fehler an der Begleitdatei darf die
        ' Hauptoperation nie scheitern lassen (deshalb schlucken alle drei Methoden Ausnahmen).

        Public Shared Sub AccompanyMove(sourcePath As String, targetPath As String)
            Try
                Dim sidecar = SidecarPathFor(sourcePath)
                If File.Exists(sidecar) Then File.Move(sidecar, SidecarPathFor(targetPath), overwrite:=True)
            Catch
            End Try
        End Sub

        Public Shared Sub AccompanyCopy(sourcePath As String, targetPath As String)
            Try
                Dim sidecar = SidecarPathFor(sourcePath)
                If File.Exists(sidecar) Then File.Copy(sidecar, SidecarPathFor(targetPath), overwrite:=True)
            Catch
            End Try
        End Sub

        ''' <summary>Beim Papierkorb wandert die Begleitdatei ebenfalls in den Papierkorb (die
        ''' RAW-Datei laesst sich so mitsamt Rezept wiederherstellen), sonst wird sie geloescht.</summary>
        Public Shared Sub AccompanyDelete(rawPath As String, useTrash As Boolean)
            Try
                Dim sidecar = SidecarPathFor(rawPath)
                If Not File.Exists(sidecar) Then Return
                If useTrash Then
                    TrashService.MoveToTrash(sidecar)
                Else
                    File.Delete(sidecar)
                End If
            Catch
            End Try
        End Sub

        ''' <summary>Nur die Drehung aus dem Sidecar (0/90/180/270; 0 wenn keiner da ist). Der
        ''' ANZEIGE-Weg braucht ausschliesslich diesen einen Wert: Viewer, Filmstreifen und Kacheln
        ''' zeigen die schnelle eingebettete RAW-Vorschau, nicht die entwickelte Datei - Belichtung
        ''' und Farbe aus dem Rezept wirken dort bewusst nicht, die Geometrie muss aber stimmen,
        ''' sonst haette das Drehen im Viewer sichtbar keine Wirkung.
        '''
        ''' Ergebnisse werden je Sidecar-Zeitstempel gemerkt: der Aufruf sitzt im Thumbnail-Pfad und
        ''' liefe sonst pro Kachel durch einen XML-Parse.</summary>
        Public Shared Function ReadRotationDegrees(rawPath As String) As Integer
            If String.IsNullOrWhiteSpace(rawPath) Then Return 0
            Dim sidecar = SidecarPathFor(rawPath)
            Dim stampTicks As Long
            Try
                If Not File.Exists(sidecar) Then Return 0
                stampTicks = File.GetLastWriteTimeUtc(sidecar).Ticks
            Catch
                Return 0
            End Try

            Dim cached As CachedRotation = Nothing
            If _rotationCache.TryGetValue(sidecar, cached) AndAlso cached.StampTicks = stampTicks Then Return cached.Degrees

            Dim adjustments = TryRead(rawPath)
            Dim degrees = If(adjustments Is Nothing, 0, ImageOrientationService.NormalizeQuarterTurn(adjustments.RotationDegrees))
            _rotationCache(sidecar) = New CachedRotation(stampTicks, degrees)
            Return degrees
        End Function

        Private NotInheritable Class CachedRotation
            Public ReadOnly StampTicks As Long
            Public ReadOnly Degrees As Integer
            Public Sub New(stampTicks As Long, degrees As Integer)
                Me.StampTicks = stampTicks
                Me.Degrees = degrees
            End Sub
        End Class

        Private Shared ReadOnly _rotationCache As New ConcurrentDictionary(Of String, CachedRotation)(PathIdentity.Comparer)

        ''' <summary>Übernimmt die Entwicklungseinstellungen aus einer Lightroom-/Camera-Raw-Sidecar
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
                Dim look = LightroomPresetService.LoadLook(xmpPath)
                If look Is Nothing OrElse Not HasAnyAdjustment(look) Then Return False
                Return TryWrite(rawPath, look)
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

        ''' <summary>Liest das Rezept aus dem Sidecar. Nothing, wenn keiner da ist, die Version
        ''' unbekannt oder die Datei defekt ist - der Editor startet dann wie ohne Sidecar.</summary>
        Public Shared Function TryRead(rawPath As String) As ImageAdjustments
            Try
                Dim sidecar = SidecarPathFor(rawPath)
                If Not File.Exists(sidecar) Then Return Nothing
                Dim doc = XDocument.Load(sidecar)
                Dim root = doc.Root
                If root Is Nothing OrElse root.Name <> Ns + "recipe" Then Return Nothing
                Dim version = CInt(root.Attribute("version")?.Value)
                If version < 1 OrElse version > FormatVersion Then Return Nothing
                Dim adjustmentsNode = root.Element(Ns + "adjustments")
                If adjustmentsNode Is Nothing Then Return Nothing
                Return FpxService.DeserializeAdjustments(adjustmentsNode.Value)
            Catch
                Return Nothing
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
