Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Text.Json.Nodes

Namespace Services

    ''' <summary>
    ''' Das native FerrumPix-Projektformat <c>.fpx</c>: ein ZIP-Bündel, das die Bearbeitung
    ''' festhält, damit man sie später weiterbearbeiten kann. Regler und Objekt-Ebenen bleiben
    ''' voll editierbar; Retusche/Pinselstriche/gerasterte Ebenen sind seit dem
    ''' ARBEITSBILD-Umbau beim Wiederöffnen ENDGÜLTIG eingebacken.
    '''
    ''' Aufbau des Bündels:
    '''   recipe.json    - Regler-Anpassungen + Objekt-Ebenenstapel (ImageAdjustments; Spot-/
    '''                    Strichlisten alter Dateien werden ignoriert - keine Alt-Kompatibilität).
    '''   base.&lt;ext&gt;      - eine Kopie des Originalbilds (Referenz, „Vorher"-Ansicht).
    '''   assets/aN.&lt;ext&gt;  - binäre Ebeneninhalte (eingefügte Bilder, ausgeschnittene Auswahl-Ebenen).
    '''   composite.png  - das fertig gerenderte Ergebnis, damit Galerie/Betrachter/Vollbild ein Bild
    '''                    anzeigen können, ohne das Rezept neu berechnen zu müssen.
    '''   retouch.png    - optional: das ARBEITSBILD in VOLLER Auflösung (Original + eingebackene
    '''                    Retusche/Striche/gerasterte Ebenen). Ist es vorhanden und maßgleich zur
    '''                    Basis, ist ES der Pipeline-Eingang beim Laden; andere Maße (alter
    '''                    Vorschau-Seed) werden verworfen.
    '''
    ''' Weil die Pipeline deterministisch ist (siehe Golden-Hash-Prüfung der Diagnose), ergibt das
    ''' wieder angewandte Rezept exakt dasselbe Bild.
    ''' </summary>
    Public Class FpxService

        ''' Feature-Schalter: mit 0.9.4 aktiviert. Während des Rendering-Umbaus war das
        ''' Format ausgeknipst; die Diagnose-Round-Trips (Rezept, Composite, Assets) laufen nur bei
        ''' Enabled=True mit - nach dem Aktivieren also immer.
        Public Shared ReadOnly Enabled As Boolean = True
        Public Const Extension As String = ".fpx"
        Private Const FormatVersion As Integer = 1
        Private Const RecipeEntry As String = "recipe.json"
        Private Const CompositeEntry As String = "composite.png"
        Private Const RetouchEntry As String = "retouch.png"
        Private Const BasePrefix As String = "base"
        Private Const AssetsDir As String = "assets/"

        Public Shared Function IsFpx(filePath As String) As Boolean
            Return Enabled AndAlso Not String.IsNullOrEmpty(filePath) AndAlso
                   String.Equals(Path.GetExtension(filePath), Extension, StringComparison.OrdinalIgnoreCase)
        End Function

        ' ── Serialisierung ──────────────────────────────────────────────────────

        ''' <summary>Umschlag der Rezeptdatei: Metadaten + die Bearbeitung selbst.</summary>
        Public Class FpxRecipe
            Public Property FormatVersion As Integer
            Public Property BaseFileName As String = ""
            Public Property BaseSha256 As String = ""
            Public Property Adjustments As ImageAdjustments
        End Class

        Private Shared ReadOnly JsonOptions As JsonSerializerOptions = BuildJsonOptions()

        Private Shared Function BuildJsonOptions() As JsonSerializerOptions
            ' VB.NET kann keine eigenen JsonConverter schreiben (Utf8JsonReader ist ein ref struct, den VB
            ' nicht unterstützt). Deshalb konstruktorbasiert serialisieren: StrokePoint/BrushStroke tragen ein
            ' <JsonConstructor>-Attribut, und IncludeFields deckt StrokePoints Nur-Lese-Felder X/Y ab. Berechnete
            ' Nur-Lese-Eigenschaften (LayerLabel, IconSource, EditableName, HasCloneSource) werden zwar mit
            ' geschrieben, beim Laden aber ignoriert (kein Setter) - unkritisch.
            Return New JsonSerializerOptions With {
                .WriteIndented = True,
                .IncludeFields = True,
                .PropertyNameCaseInsensitive = True,
                .DefaultIgnoreCondition = JsonIgnoreCondition.Never
            }
        End Function

        ''' <summary>Rezept-JSON fuer den RAW-Sidecar (RawSidecarService): exakt dieselben
        ''' Serialisierungsregeln wie im .fpx-Buendel, damit beide Formate nie auseinanderdriften.</summary>
        Friend Shared Function SerializeAdjustments(adjustments As ImageAdjustments) As String
            ' Die globalen Regler bleiben absichtlich vollständig serialisiert (die Datei ist
            ' zugleich ein stabiles Rezeptformat). Ein Geometrieschritt benötigt dagegen nur seine
            ' wenigen Eingabefelder. Json.NET kann das pro verschachteltem Typ nicht ohne Converter
            ' ausdrücken; deshalb wird genau dieser Teil nach der normalen Serialisierung verdichtet.
            Dim root = JsonNode.Parse(JsonSerializer.Serialize(adjustments, JsonOptions))?.AsObject()
            Dim operations = root?("GeometryOperations")?.AsArray()
            If operations IsNot Nothing Then
                For Each item In operations
                    Dim operation = TryCast(item, JsonObject)
                    Dim source = TryCast(operation?("Adjustments"), JsonObject)
                    If source Is Nothing Then Continue For
                    Dim compact As New JsonObject()
                    For Each name In GeometryJsonFields(operation?("Kind")?.GetValue(Of String)())
                        Dim value As JsonNode = Nothing
                        If source.TryGetPropertyValue(name, value) Then compact(name) = value?.DeepClone()
                    Next
                    operation("Adjustments") = compact
                Next
            End If
            Return If(root Is Nothing, "{}", root.ToJsonString(New JsonSerializerOptions With {.WriteIndented = True}))
        End Function

        Private Shared Function GeometryJsonFields(kind As String) As String()
            Const crop = "CropLeftPercent|CropTopPercent|CropRightPercent|CropBottomPercent"
            Const transform = "RotationDegrees|StraightenDegrees|StraightenExpandCanvas|FlipHorizontal|FlipVertical"
            Const perspective = "PerspectiveHorizontal|PerspectiveVertical|PerspectiveAspect|PerspectiveScale|PerspectiveCorner0X|PerspectiveCorner0Y|PerspectiveCorner1X|PerspectiveCorner1Y|PerspectiveCorner2X|PerspectiveCorner2Y|PerspectiveCorner3X|PerspectiveCorner3Y"
            Const warp = "ImageWarp"
            Const resize = "ResizeWidth|ResizeHeight|ResizeScalePercent|ResizeFitInsideBox|LockResizeAspect|NoResizeUpscale|ResizeInterpolation"
            Const canvas = "CanvasWidth|CanvasHeight|CanvasAnchor|CanvasBackgroundColor"
            Dim names As String
            Select Case If(kind, "").Trim().ToLowerInvariant()
                Case "crop" : names = crop
                Case "transform" : names = transform
                Case "perspective" : names = perspective
                Case "warp" : names = warp
                Case "resize" : names = resize
                Case "canvas" : names = canvas
                Case Else : names = String.Join("|", {crop, transform, perspective, warp, resize, canvas})
            End Select
            Return names.Split("|"c)
        End Function

        Friend Shared Function DeserializeAdjustments(json As String) As ImageAdjustments
            If String.IsNullOrWhiteSpace(json) Then Return Nothing
            Return NormalizeLoadedAdjustments(JsonSerializer.Deserialize(Of ImageAdjustments)(json, JsonOptions))
        End Function


        ''' <summary>Migration alter Rezepte: Früher bedeutete HasActiveSelection zugleich Render-Skopus.
        ''' Heute ist es reiner UI-Zustand und wird nie aus einem Dokument wiederhergestellt.</summary>
        Private Shared Function NormalizeLoadedAdjustments(adj As ImageAdjustments) As ImageAdjustments
            If adj Is Nothing Then Return Nothing
            Dim hadLegacyScope = adj.SelectionScopeEnabled OrElse adj.HasActiveSelection
            If hadLegacyScope AndAlso (adj.MaskedAdjustmentLayers Is Nothing OrElse adj.MaskedAdjustmentLayers.Count = 0) Then
                ' Alte Ein-Auswahl-Rezepte direkt in das neue Modell überführen. Dabei sind exakt die
                ' damals maskierten Pixelwerte lokal; die globale Ebene wird neutral. Scheitert die
                ' Maskendekodierung, bleibt der alte explizite Skopus als verlustfreier Fallback erhalten.
                Dim mask = ImageProcessor.CreateSourceMaskFromSelection(adj, LocalizationService.T("Migrierte Auswahlmaske"))
                If mask IsNot Nothing Then
                    If adj.Masks Is Nothing Then adj.Masks = New List(Of ImageMask)()
                    If adj.MaskedAdjustmentLayers Is Nothing Then adj.MaskedAdjustmentLayers = New List(Of MaskedAdjustmentLayer)()
                    adj.Masks.Add(mask)
                    adj.MaskedAdjustmentLayers.Add(New MaskedAdjustmentLayer With {
                        .Name = LocalizationService.T("Übernommene Maskenebene"),
                        .MaskId = mask.Id,
                        .Adjustments = adj.ExtractPixelAdjustments()
                    })
                    adj.CopyPixelAdjustmentsFrom(New ImageAdjustments())
                    adj.SelectionScopeEnabled = False
                Else
                    adj.SelectionScopeEnabled = True
                End If
            End If
            StripTransientSelectionState(adj)
            DropRecipeGeometryFields(adj)
            Return adj
        End Function

        ''' <summary>Nach dem Laden lebt die Geometrie AUSSCHLIESSLICH in der Schrittliste.
        '''
        ''' Ein Rezept aus der Zeit vor der geordneten Schrittfolge trug sie stattdessen in oberen
        ''' Feldern. Die wurden bisher beim Oeffnen in einen einzigen Schritt der Art "legacy"
        ''' ueberfuehrt - eine ganze Feldgruppe in einem Klumpen, waehrend jeder andere Schritt genau
        ''' eine Sache traegt. Damit gab es zwei Wege durch dieselbe Kette, und zwei Wege laufen
        ''' frueher oder spaeter auseinander.
        '''
        ''' DIE ALTE GEOMETRIE WIRD DESHALB VERWORFEN (Entscheidung vom 2026-09-03). Regler, Objekte,
        ''' Masken, Retusche und Pinselstriche bleiben vollstaendig erhalten; weg sind Beschnitt,
        ''' Drehung, Ausrichten, Spiegelung, Perspektive, Bildverzerrung, Groesse und Leinwand eines
        ''' Dokuments, das noch keine Schrittliste hat.
        '''
        ''' Die Stelle ist mit Absicht die Deserialisierung: hier kommen BEIDE Sorten Dokument
        ''' vorbei, das Buendel und die Beistelldatei neben einer RAW-Datei (RawSidecarService liest
        ''' ueber dieselbe Funktion). Im Editor allein zu verwerfen hiesse, dass der Stapel dasselbe
        ''' Rezept weiterhin mit seiner alten Geometrie rendert.
        '''
        ''' GERAEUMT WIRD IMMER, nicht nur ohne Schrittliste: so gilt die Regel "Geometrie steht in
        ''' Schritten" ohne Ausnahme, und ein stehengebliebenes Feld kann nicht spaeter still
        ''' mitwirken.</summary>
        Private Shared Sub DropRecipeGeometryFields(adj As ImageAdjustments)
            If adj Is Nothing Then Return
            Dim hatte = adj.CropLeftPercent <> 0 OrElse adj.CropTopPercent <> 0 OrElse
                        adj.CropRightPercent <> 0 OrElse adj.CropBottomPercent <> 0 OrElse
                        adj.RotationDegrees <> 0 OrElse adj.StraightenDegrees <> 0 OrElse
                        adj.StraightenExpandCanvas OrElse adj.FlipHorizontal OrElse adj.FlipVertical OrElse
                        adj.PerspectiveHorizontal <> 0 OrElse adj.PerspectiveVertical <> 0 OrElse
                        adj.PerspectiveAspect <> 0 OrElse adj.PerspectiveScale <> 0 OrElse
                        adj.PerspectiveCorner0X <> 0 OrElse adj.PerspectiveCorner0Y <> 0 OrElse
                        adj.PerspectiveCorner1X <> 0 OrElse adj.PerspectiveCorner1Y <> 0 OrElse
                        adj.PerspectiveCorner2X <> 0 OrElse adj.PerspectiveCorner2Y <> 0 OrElse
                        adj.PerspectiveCorner3X <> 0 OrElse adj.PerspectiveCorner3Y <> 0 OrElse
                        (adj.ImageWarp IsNot Nothing AndAlso Not adj.ImageWarp.IsEmpty) OrElse
                        adj.ResizeWidth > 0 OrElse adj.ResizeHeight > 0 OrElse adj.ResizeScalePercent > 0 OrElse
                        adj.CanvasWidth > 0 OrElse adj.CanvasHeight > 0

            adj.CropLeftPercent = 0 : adj.CropTopPercent = 0
            adj.CropRightPercent = 0 : adj.CropBottomPercent = 0
            adj.RotationDegrees = 0 : adj.StraightenDegrees = 0 : adj.StraightenExpandCanvas = False
            adj.FlipHorizontal = False : adj.FlipVertical = False
            adj.PerspectiveHorizontal = 0 : adj.PerspectiveVertical = 0
            adj.PerspectiveAspect = 0 : adj.PerspectiveScale = 0
            adj.PerspectiveCorner0X = 0 : adj.PerspectiveCorner0Y = 0
            adj.PerspectiveCorner1X = 0 : adj.PerspectiveCorner1Y = 0
            adj.PerspectiveCorner2X = 0 : adj.PerspectiveCorner2Y = 0
            adj.PerspectiveCorner3X = 0 : adj.PerspectiveCorner3Y = 0
            adj.ImageWarp = Nothing
            adj.ResizeWidth = 0 : adj.ResizeHeight = 0 : adj.ResizeScalePercent = 0
            adj.CanvasWidth = 0 : adj.CanvasHeight = 0

            If hatte AndAlso (adj.GeometryOperations Is Nothing OrElse adj.GeometryOperations.Count = 0) Then
                DiagnosticLogService.LogAlways("Fpx.LegacyGeometryDropped",
                                               "Ein Rezept ohne Schrittliste trug Geometrie in den alten Feldern - sie wurde beim Laden verworfen.")
            End If
        End Sub

        ''' <summary>Raeumt den TRANSIENTEN Auswahlzustand aus einem Dokumentrezept: ob gerade eine
        ''' Auswahl lief (<c>HasActiveSelection</c>), welcher ART sie war (<c>ActiveSelectionIsMask</c>)
        ''' und ihre Arbeitskopie (<c>SelectionMaskPngBase64</c> samt Rechteck und Form).
        '''
        ''' Bisher stand hier nur die erste Haelfte, und das reichte nicht: die ART und die
        ''' Arbeitskopie kamen weiter mit. Beim Oeffnen setzte das Wiederherstellen die Art auf
        ''' "Maske" und legte die Arbeitskopie zurueck, und weil JEDE Aenderung der Auswahlmaske bei
        ''' einer Masken-Art das rote Overlay veroeffentlicht, lag es sofort ueber dem Bild. Die
        ''' Ansicht zeigt es allein an der Art und am Vorhandensein des Bildes, nicht an
        ''' HasActiveSelection. Sichtbar wurde es als "rotes Overlay einer Maske, obwohl im
        ''' Ebenenpanel nichts markiert ist und das Auswahlwerkzeug steht" (Nutzerbefund 2026-08-08) -
        ''' und weil das Wiederherstellen die Bindung an die Ebene loescht, gehoerte das Rot zu gar
        ''' nichts mehr.
        '''
        ''' Die Arbeitskopie bleibt NUR bei gesetztem <c>SelectionScopeEnabled</c> stehen: dort ist
        ''' sie kein UI-Zustand, sondern der Render-Skopus alter Rezepte, deren Auswahl sich nicht in
        ''' eine Maskenebene ueberfuehren liess. Die weiche Kante bleibt immer erhalten, sie gehoert
        ''' zum Rezept.</summary>
        Private Shared Sub StripTransientSelectionState(adj As ImageAdjustments)
            If adj Is Nothing Then Return
            adj.HasActiveSelection = False
            adj.ActiveSelectionIsMask = False
            If adj.SelectionScopeEnabled Then Return
            adj.SelectionMaskPngBase64 = ""
            adj.SelectionMaskLeft = 0
            adj.SelectionMaskTop = 0
            adj.SelectionMaskRight = 0
            adj.SelectionMaskBottom = 0
            adj.SelectionMaskSoftBaked = False
            adj.SelectionXPercent = 0
            adj.SelectionYPercent = 0
            adj.SelectionWidthPercent = 0
            adj.SelectionHeightPercent = 0
            adj.SelectionShapeMode = "Rectangle"
            adj.SelectionShapePointsX = Nothing
            adj.SelectionShapePointsY = Nothing
        End Sub

        ' ── Speichern ───────────────────────────────────────────────────────────

        ''' <summary>Schreibt die Bearbeitung als <c>.fpx</c>-Bündel. <paramref name="compositePng"/> ist das
        ''' fertig gerenderte Ergebnis (für die Anzeige); es wird als composite.png eingebettet.
        ''' <paramref name="retouchStagePng"/> ist optional die fertig retuschierte Stufe (retouch.png),
        ''' die das Laden vom Neu-Abspielen aller Retusche-Spots befreit.</summary>
        Public Shared Sub Save(fpxPath As String, adjustments As ImageAdjustments, baseImagePath As String, compositePng As Stream,
                               Optional retouchStagePng As Stream = Nothing)
            If String.IsNullOrWhiteSpace(fpxPath) OrElse adjustments Is Nothing Then Throw New ArgumentException("fpxPath/adjustments")
            If String.IsNullOrWhiteSpace(baseImagePath) OrElse Not File.Exists(baseImagePath) Then Throw New FileNotFoundException("Basisbild fehlt", baseImagePath)

            ' Auf einer Kopie arbeiten: die Objekt-Bildpfade werden auf bündel-relative Namen umgeschrieben,
            ' ohne die im Editor lebende Bearbeitung anzufassen.
            Dim recipeAdj = adjustments.Clone()
            ' Persistente lokale Korrekturen liegen in Masks + MaskedAdjustmentLayers. Ob daneben gerade
            ' eine Auswahl lief, welcher Art sie war und wie sie aussah, ist nur UI-Zustand und darf beim
            ' Wiederladen weder die Auswahl reaktivieren noch ein rotes Overlay heraufholen noch globale
            ' Regler nachträglich auf diese Auswahl begrenzen (siehe StripTransientSelectionState).
            StripTransientSelectionState(recipeAdj)
            ' Das Gegenstueck zur .fpxmp: dieses Buendel traegt das Arbeitsbild in voller Aufloesung
            ' mit, Entrauschen und Retusche stecken also in seinen Pixeln. Der Vermerk darueber wird
            ' hier trotzdem NICHT auf wahr gezwungen, sondern uebernommen wie er kommt: wer eine
            ' .fpx speichert, ohne einen offenen Vorgang angewandt zu haben, hat ihn auch nicht in
            ' den Pixeln - dann bleibt er offen und wird beim naechsten Oeffnen wieder angeboten.
            ' Ein festes Wahr waere an dieser Stelle eine Behauptung ueber fremde Pixel.
            Dim assetMap As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            If recipeAdj.Annotations IsNot Nothing Then
                For Each ann In recipeAdj.Annotations
                    If ann Is Nothing OrElse String.IsNullOrWhiteSpace(ann.ImagePath) Then Continue For
                    ' Nur echte Dateien einbetten (avares://-Symbole bleiben als Referenz erhalten).
                    If Not File.Exists(ann.ImagePath) Then Continue For
                    ' Anzeigenamen retten, BEVOR ImagePath auf assets/aN.ext umgeschrieben wird: die
                    ' Ebenen-Beschriftung fällt sonst nach dem Wiederöffnen auf „a0.png" zurück. Nur für
                    ' Arten, deren Beschriftung aus dem Dateinamen kommt - bei ausgeschnittenen
                    ' Auswahl-Ebenen ist der Pfad ein bedeutungsloser Temp-Name.
                    Dim labelFromFileName = ann.Kind IsNot Nothing AndAlso
                        (ann.Kind.Equals("Image", StringComparison.OrdinalIgnoreCase) OrElse ann.Kind.Equals("Svg", StringComparison.OrdinalIgnoreCase))
                    If labelFromFileName AndAlso String.IsNullOrWhiteSpace(ann.SourceFileName) Then
                        ann.SourceFileName = Path.GetFileName(ann.ImagePath)
                    End If
                    Dim assetName As String = Nothing
                    If Not assetMap.TryGetValue(ann.ImagePath, assetName) Then
                        assetName = AssetsDir & "a" & assetMap.Count.ToString() & Path.GetExtension(ann.ImagePath)
                        assetMap(ann.ImagePath) = assetName
                    End If
                    ann.ImagePath = assetName
                Next
            End If

            Dim recipe As New FpxRecipe With {
                .FormatVersion = FormatVersion,
                .BaseFileName = Path.GetFileName(baseImagePath),
                .BaseSha256 = "",
                .Adjustments = recipeAdj
            }

            Dim baseExt = Path.GetExtension(baseImagePath)
            If String.IsNullOrEmpty(baseExt) Then baseExt = ".png"

            Dim tempPath = fpxPath & ".tmp"
            If File.Exists(tempPath) Then File.Delete(tempPath)
            Using zip = ZipFile.Open(tempPath, ZipArchiveMode.Create)
                ' Bilddaten (Basisbild, Objekt-Assets, Komposit) UNKOMPRIMIERT ablegen: es sind bereits
                ' komprimierte Formate (PNG/JPEG …); ein zusätzlicher ZIP-Deflate kostet beim Speichern und
                ' vor allem beim Extrahieren im Viewer CPU, ohne nennenswert zu schrumpfen. Größe ist zweitrangig,
                ' Anzeigetempo wichtiger -> "Store" macht das Extrahieren des Komposits fast zum reinen Kopieren.
                zip.CreateEntryFromFile(baseImagePath, BasePrefix & baseExt, CompressionLevel.NoCompression)
                For Each kv In assetMap
                    zip.CreateEntryFromFile(kv.Key, kv.Value, CompressionLevel.NoCompression)
                Next
                If compositePng IsNot Nothing Then
                    Dim entry = zip.CreateEntry(CompositeEntry, CompressionLevel.NoCompression)
                    Using es = entry.Open()
                        If compositePng.CanSeek Then compositePng.Position = 0
                        compositePng.CopyTo(es)
                    End Using
                End If
                If retouchStagePng IsNot Nothing Then
                    Dim entry = zip.CreateEntry(RetouchEntry, CompressionLevel.NoCompression)
                    Using es = entry.Open()
                        If retouchStagePng.CanSeek Then retouchStagePng.Position = 0
                        retouchStagePng.CopyTo(es)
                    End Using
                End If
                ' Das Rezept ist Text und komprimiert gut -> weiter komprimieren.
                Dim recipeZip = zip.CreateEntry(RecipeEntry, CompressionLevel.Optimal)
                Using es = recipeZip.Open()
                    JsonSerializer.Serialize(es, recipe, JsonOptions)
                End Using
            End Using

            File.Move(tempPath, fpxPath, overwrite:=True)
        End Sub

        ' ── Laden ───────────────────────────────────────────────────────────────

        Public Class FpxLoadResult
            Public Property BaseImagePath As String = ""
            Public Property Adjustments As ImageAdjustments
            Public Property TempDir As String = ""
            ''' <summary>Entpackte retouch.png (fertig retuschierte Stufe) oder "" bei älteren
            ''' Bündeln ohne den Eintrag - dann spielt der erste Render die Spots neu ab.</summary>
            Public Property RetouchStagePath As String = ""
        End Class

        ''' <summary>Entpackt Basisbild und Objekt-Assets in einen Temp-Ordner und liest das Rezept. Die
        ''' Objekt-Bildpfade werden auf die entpackten Temp-Dateien zurückgeschrieben, sodass der Editor die
        ''' Bearbeitung wie eine normale Bild-Bearbeitung fortsetzen kann.</summary>
        Public Shared Function Load(fpxPath As String) As FpxLoadResult
            If Not File.Exists(fpxPath) Then Return Nothing
            Dim tempDir = Path.Combine(Path.GetTempPath(), "FerrumPix", "fpx", Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir)
            Dim success = False

            Try
                Using zip = ZipFile.OpenRead(fpxPath)
                    Dim baseEntry = zip.Entries.FirstOrDefault(Function(e) e.FullName.StartsWith(BasePrefix & ".", StringComparison.OrdinalIgnoreCase))
                    If baseEntry Is Nothing Then Return Nothing
                    Dim basePath = SafeExtractPath(tempDir, baseEntry.Name)
                    If String.IsNullOrEmpty(basePath) Then Return Nothing
                    baseEntry.ExtractToFile(basePath, True)

                    For Each e In zip.Entries.Where(Function(x) x.FullName.StartsWith(AssetsDir, StringComparison.OrdinalIgnoreCase) AndAlso Not String.IsNullOrEmpty(x.Name))
                        Dim assetPath = SafeExtractPath(tempDir, e.FullName)
                        If String.IsNullOrEmpty(assetPath) Then Return Nothing
                        Directory.CreateDirectory(Path.GetDirectoryName(assetPath))
                        e.ExtractToFile(assetPath, True)
                    Next

                    ' Retusche-Stufe (optional, erst in neueren Bündeln) mit entpacken.
                    Dim retouchPath = ""
                    Dim retouchZip = zip.GetEntry(RetouchEntry)
                    If retouchZip IsNot Nothing Then
                        retouchPath = If(SafeExtractPath(tempDir, RetouchEntry), "")
                        If Not String.IsNullOrEmpty(retouchPath) Then retouchZip.ExtractToFile(retouchPath, True)
                    End If

                    Dim recipeZip = zip.GetEntry(RecipeEntry)
                    If recipeZip Is Nothing Then Return Nothing
                    Dim recipe As FpxRecipe
                    Using es = recipeZip.Open()
                        recipe = JsonSerializer.Deserialize(Of FpxRecipe)(es, JsonOptions)
                    End Using
                    If recipe?.Adjustments Is Nothing Then Return Nothing
                    recipe.Adjustments = NormalizeLoadedAdjustments(recipe.Adjustments)

                    ' Objekt-Bildpfade von bündel-relativ auf die entpackten Temp-Dateien umschreiben.
                    If recipe.Adjustments.Annotations IsNot Nothing Then
                        For Each ann In recipe.Adjustments.Annotations
                            If ann Is Nothing OrElse String.IsNullOrWhiteSpace(ann.ImagePath) Then Continue For
                            If ann.ImagePath.StartsWith(AssetsDir, StringComparison.OrdinalIgnoreCase) Then
                                ann.ImagePath = SafeExtractPath(tempDir, ann.ImagePath)
                                If String.IsNullOrEmpty(ann.ImagePath) Then Return Nothing
                            End If
                        Next
                    End If

                    success = True
                    Return New FpxLoadResult With {.BaseImagePath = basePath, .Adjustments = recipe.Adjustments, .TempDir = tempDir, .RetouchStagePath = retouchPath}
                End Using
            Catch
                Return Nothing
            Finally
                ' Bei erfolgreichem Laden übernimmt der Editor den Temp-Ordner. Bei jedem Fehler wird er
                ' sofort entfernt, sonst bleiben halbe FPX-Extraktionen liegen.
                If Not success Then
                    Try : Directory.Delete(tempDir, True) : Catch : End Try
                End If
            End Try
        End Function

        Private Shared Function SafeExtractPath(tempDir As String, entryName As String) As String
            If String.IsNullOrWhiteSpace(entryName) Then Return Nothing
            Dim normalized = entryName.Replace("/"c, Path.DirectorySeparatorChar)
            Dim target = Path.GetFullPath(Path.Combine(tempDir, normalized))
            Dim root = Path.GetFullPath(tempDir)
            If Not root.EndsWith(Path.DirectorySeparatorChar) Then root &= Path.DirectorySeparatorChar
            If Not target.StartsWith(root, StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Return target
        End Function

        ''' <summary>Liefert das eingebettete Komposit als dekodierbaren PNG-Stream (Position 0) - wie
        ''' RawPreviewService/SvgPreviewService, sodass Thumbnail- und Betrachterpfade es wie ein normales
        ''' Bild weiterverarbeiten. Nothing bei Fehler oder fehlendem Komposit.</summary>
        Public Shared Function ExtractComposite(fpxPath As String) As MemoryStream
            Try
                Using zip = ZipFile.OpenRead(fpxPath)
                    Dim entry = zip.GetEntry(CompositeEntry)
                    If entry Is Nothing Then Return Nothing
                    Dim ms As New MemoryStream()
                    Using es = entry.Open()
                        es.CopyTo(ms)
                    End Using
                    ms.Position = 0
                    Return ms
                End Using
            Catch
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
