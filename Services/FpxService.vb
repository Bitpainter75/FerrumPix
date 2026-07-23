Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text.Json
Imports System.Text.Json.Serialization

Namespace Services

    ''' <summary>
    ''' Das native FerrumPix-Projektformat <c>.fpx</c>: ein ZIP-Bündel, das die Bearbeitung
    ''' festhält, damit man sie später weiterbearbeiten kann. Regler und Objekt-Ebenen bleiben
    ''' voll editierbar; Retusche/Pinselstriche/gerasterte Ebenen sind seit dem
    ''' ARBEITSBILD-Umbau (2026-07-17) beim Wiederöffnen ENDGÜLTIG eingebacken.
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

        ''' Feature-Schalter: mit 0.9.4 aktiviert (2026-07-16). Während des Rendering-Umbaus war das
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
            Return JsonSerializer.Serialize(adjustments, JsonOptions)
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
                        .Name = LocalizationService.T("Migrierte lokale Korrektur"),
                        .MaskId = mask.Id,
                        .Adjustments = adj.ExtractPixelAdjustments()
                    })
                    adj.CopyPixelAdjustmentsFrom(New ImageAdjustments())
                    adj.SelectionScopeEnabled = False
                Else
                    adj.SelectionScopeEnabled = True
                End If
            End If
            adj.HasActiveSelection = False
            Return adj
        End Function

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
            ' die Ameisenlinie sichtbar war, ist nur UI-Zustand und darf beim Wiederladen weder die Auswahl
            ' reaktivieren noch globale Regler nachträglich auf diese Auswahl begrenzen.
            recipeAdj.HasActiveSelection = False
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

                    ' Retusche-Stufe (optional, erst seit 2026-07-17 im Bündel) mit entpacken.
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
