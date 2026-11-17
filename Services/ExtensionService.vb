Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports SkiaSharp

Namespace Services

    ''' <summary>Ein Bedienelement einer Erweiterung: ein Regler oder ein Schalter. Der Schluessel
    ''' ist zugleich der Name, unter dem die Rechnung den Wert sieht.</summary>
    Public NotInheritable Class ExtensionControl
        Public Property Key As String = ""
        ''' <summary>"slider" oder "toggle".</summary>
        Public Property Kind As String = ExtensionControlKinds.Slider
        Public Property Labels As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Zwischenueberschrift VOR diesem Element; leer heisst keine.</summary>
        Public Property Sections As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Public Property Minimum As Double = 0
        Public Property Maximum As Double = 100
        Public Property DefaultValue As Double = 0
        ''' <summary>Nachkommastellen der Anzeige, 0 bis 2.</summary>
        Public Property Decimals As Integer = 0
        ''' <summary>Die Farbspur des Reglers: eine der festen Spuren aus
        ''' <see cref="ExtensionTracks"/> oder leer. Bei eigenen Farben steht hier
        ''' <see cref="ExtensionTracks.Custom"/> und die Farben in <see cref="TrackColors"/>.</summary>
        Public Property Track As String = ""
        ''' <summary>Die Farben einer eigenen Spur als "#RRGGBB", von links nach rechts.</summary>
        Public Property TrackColors As New List(Of String)()
        ''' <summary>Bei <see cref="ExtensionTracks.SaturationOf"/> und
        ''' <see cref="ExtensionTracks.LuminanceOf"/>: der Schluessel des Farbtonreglers (0 bis
        ''' 360), dessen Farbe die Spur zeigt. Die Spur folgt ihm beim Ziehen.</summary>
        Public Property TrackSource As String = ""

        Public ReadOnly Property IsToggle As Boolean
            Get
                Return String.Equals(Kind, ExtensionControlKinds.Toggle, StringComparison.Ordinal)
            End Get
        End Property

        ''' <summary>Ein Wert in den Grenzen dieses Elements; alles, was keine Zahl ist, wird zur
        ''' Vorgabe. Ein Schalter kennt nur 0 und 1.</summary>
        Public Function Clamp(value As Double) As Double
            If Not Double.IsFinite(value) Then Return DefaultValue
            If IsToggle Then Return If(value >= 0.5, 1.0, 0.0)
            ' Auf die Nachkommastellen der Datei gerundet: der Regler selbst liefert beliebige
            ' Zwischenwerte, das Zahlenfeld zeigt aber nur so viele Stellen. Ohne Rundung rechnete
            ' das Bild mit 37,41, waehrend 37 dastuende, und das Rezept truege lauter krumme Werte.
            Dim rounded = Math.Round(value, Decimals, MidpointRounding.AwayFromZero)
            Return Math.Max(Minimum, Math.Min(Maximum, rounded))
        End Function

        Public ReadOnly Property Label As String
            Get
                Return ExtensionService.Localized(Labels, Key)
            End Get
        End Property

        Public ReadOnly Property Section As String
            Get
                Return ExtensionService.Localized(Sections, "")
            End Get
        End Property
    End Class

    Public NotInheritable Class ExtensionControlKinds
        Public Const Slider As String = "slider"
        Public Const Toggle As String = "toggle"

        Private Sub New()
        End Sub
    End Class

    ''' <summary>Die festen Farbspuren, die ein Regler einer Erweiterung tragen kann. Sie sind
    ''' dieselben wie bei den eingebauten Reglern und erscheinen wie dort nur mit der Einstellung
    ''' "Farbige Regler".</summary>
    Public NotInheritable Class ExtensionTracks
        ''' <summary>Der ganze Farbkreis, fuer einen Regler von 0 bis 360.</summary>
        Public Const Hue As String = "hue"
        Public Const BlackWhite As String = "black-white"
        Public Const Temperature As String = "temperature"
        Public Const Tint As String = "tint"
        ''' <summary>Steht fuer eigene Farben (TrackColors); in der Datei als Liste geschrieben.</summary>
        Public Const Custom As String = "custom"
        ''' <summary>Grau nach der Farbe eines anderen Reglers; in der Datei als
        ''' { "saturation": "schluessel" }.</summary>
        Public Const SaturationOf As String = "saturation"
        ''' <summary>Dunkel nach hell in der Farbe eines anderen Reglers; in der Datei als
        ''' { "luminance": "schluessel" }.</summary>
        Public Const LuminanceOf As String = "luminance"

        Public Shared ReadOnly Property Named As String() = {Hue, BlackWhite, Temperature, Tint}

        Private Sub New()
        End Sub
    End Class

    ''' <summary>Wo in der Kette eine Erweiterung rechnet. Es gibt nur diese festen Stellen, damit
    ''' die Reihenfolge der eingebauten Stufen berechenbar bleibt.</summary>
    Public NotInheritable Class ExtensionStages
        ''' <summary>Hinter der Vignette, vor der Koernung: was die Erweiterung aus dem Bild macht,
        ''' bekommt danach sein Korn.</summary>
        Public Const BeforeGrain As String = "before-grain"
        ''' <summary>Hinter der Koernung: das Korn wird mitgerechnet, etwa mitgetont.</summary>
        Public Const AfterGrain As String = "after-grain"

        Private Sub New()
        End Sub
    End Class

    ''' <summary>Eine geladene Erweiterung: Beschreibung, Bedienelemente und die Rechnung als
    ''' Shader. Unveraenderlich nach dem Laden; eine geaenderte Datei ergibt eine neue Erweiterung.</summary>
    Public NotInheritable Class ExtensionDefinition
        Public Property Id As String = ""
        Public Property Version As Integer = 1
        Public Property Names As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Public Property Descriptions As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Public Property Author As String = ""
        Public Property License As String = ""
        Public Property Url As String = ""
        Public Property Stage As String = ExtensionStages.BeforeGrain
        Public Property Controls As New List(Of ExtensionControl)()
        Public Property ShaderSource As String = ""
        ''' <summary>Die Datei, aus der sie stammt.</summary>
        Public Property FilePath As String = ""
        ''' <summary>Liegt sie im Ordner neben dem Programm (mitgeliefert) statt im Ordner des
        ''' Nutzers? Mitgelieferte lassen sich nicht entfernen.</summary>
        Public Property IsBundled As Boolean
        ''' <summary>Die uebersetzte Rechnung fuer 8-Bit-Bilder und fuer das tiefe Bild der
        ''' 16-Bit-Ausgabe (siehe ImageProcessor.BuildExtensionEffects).</summary>
        Friend Property Effect As SKRuntimeEffect
        Friend Property DeepEffect As SKRuntimeEffect

        Public ReadOnly Property Name As String
            Get
                Return ExtensionService.Localized(Names, Id)
            End Get
        End Property

        Public ReadOnly Property Description As String
            Get
                Return ExtensionService.Localized(Descriptions, "")
            End Get
        End Property

        Public Function FindControl(key As String) As ExtensionControl
            Return Controls.FirstOrDefault(Function(c) String.Equals(c.Key, key, StringComparison.Ordinal))
        End Function
    End Class

    ''' <summary>Was beim Laden einer Erweiterungsdatei herauskam: die Erweiterung oder der Grund,
    ''' warum sie nicht genommen wird.</summary>
    Public NotInheritable Class ExtensionLoadResult
        Public Property Definition As ExtensionDefinition
        Public Property FilePath As String = ""
        ''' <summary>Leer bei Erfolg. Der Text ist fuer den Autor der Erweiterung gedacht und
        ''' nennt die Stelle in der Datei oder den Fehler des Shader-Uebersetzers.</summary>
        Public Property ErrorText As String = ""

        Public ReadOnly Property Succeeded As Boolean
            Get
                Return Definition IsNot Nothing
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Die Erweiterungen dieses Rechners: Dateien, die ein Werkzeug mit eigenen Reglern und einer
    ''' eigenen Farbrechnung beschreiben.
    '''
    ''' EINE ERWEITERUNG IST EINE BESCHREIBUNG, KEIN PROGRAMM. Die Datei nennt Bedienelemente und
    ''' eine Rechnung "Farbe hinein, Farbe heraus" in der Shadersprache, in der auch die
    ''' eingebauten Stufen geschrieben sind. So eine Rechnung kann keine Datei lesen, nichts
    ''' nachladen und nichts ausser der Farbe des einen Bildpunkts sehen; sie laeuft deshalb in
    ''' jedem Paket unveraendert. Eine Erweiterung als Programmbibliothek duerfte alles, braeuchte
    ''' eine feste Programmierschnittstelle und vertruege sich mit keinem der Paketwege.
    '''
    ''' ZWEI ORTE: der Ordner des Nutzers (<see cref="UserFolder"/>) und ein Ordner
    ''' "Erweiterungen" neben dem Programm fuer Mitgeliefertes. Bei gleicher Kennung gewinnt der
    ''' Nutzer, wie bei den Modellen.
    '''
    ''' EINE DATEI MIT FEHLER WIRD UEBERSPRUNGEN, nicht halb geladen: sie steht mit ihrem Fehler in
    ''' <see cref="Failures"/>, die Anwendung startet trotzdem. Ein Rezept, das sie nennt, behaelt
    ''' seinen Eintrag (<see cref="ExtensionRecipe"/>), die Stufe rechnet nur nicht.
    ''' </summary>
    Public NotInheritable Class ExtensionService

        Public Const FileExtension As String = ".json"
        ''' <summary>Fassung des Dateiformats, die diese Anwendung liest.</summary>
        Public Const FormatVersion As Integer = 1
        Private Const MaxFileBytes As Integer = 256 * 1024
        Private Const MaxControls As Integer = 32

        Private Shared ReadOnly _lock As New Object()
        Private Shared _definitions As List(Of ExtensionDefinition)
        Private Shared _failures As List(Of ExtensionLoadResult)
        Private Shared _revision As Long

        ''' <summary>Ausgeloest, wenn sich der Bestand geaendert hat (geladen, entfernt, neu
        ''' eingelesen). Auf dem Faden des Ausloesers.</summary>
        Public Shared Event Changed As EventHandler

        Private Sub New()
        End Sub

        ''' <summary>Der Ordner des Nutzers.</summary>
        Public Shared ReadOnly Property UserFolder As String
            Get
                Dim override = Environment.GetEnvironmentVariable("FERRUMPIX_EXTENSIONS")
                If Not String.IsNullOrWhiteSpace(override) Then Return override
                Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "FerrumPix", "Erweiterungen")
            End Get
        End Property

        ''' <summary>Der Ordner neben dem Programm, fuer Mitgeliefertes.</summary>
        Public Shared ReadOnly Property BundledFolder As String
            Get
                Return Path.Combine(AppContext.BaseDirectory, "Erweiterungen")
            End Get
        End Property

        ''' <summary>Zaehlt bei jeder Aenderung des Bestands hoch. Geht in den Schluessel der
        ''' Zwischenspeicher der Bildkette ein: dieselben Reglerwerte ergeben nach dem Tausch einer
        ''' Erweiterungsdatei ein anderes Bild.</summary>
        Public Shared ReadOnly Property Revision As Long
            Get
                Return Interlocked.Read(_revision)
            End Get
        End Property

        ''' <summary>Alle geladenen Erweiterungen in der Reihenfolge, die der Nutzer in den
        ''' Einstellungen festgelegt hat (AppSettings.ExtensionOrder); was dort nicht steht, folgt
        ''' nach Name. Das ist die Reihenfolge der Gruppen im Werkzeug, NICHT die der Rechnung: die
        ''' steht im Rezept des Bildes, damit es auf jedem Rechner gleich aussieht.</summary>
        Public Shared ReadOnly Property All As IReadOnlyList(Of ExtensionDefinition)
            Get
                Dim order = ReadOrder()
                SyncLock _lock
                    EnsureLoaded()
                    Return Ordered(_definitions, order)
                End SyncLock
            End Get
        End Property

        ''' <summary>Die gespeicherte Reihenfolge als Liste von Kennungen.</summary>
        Private Shared Function ReadOrder() As List(Of String)
            Dim text = ""
            Try
                text = If(AppSettingsService.Load().ExtensionOrder, "")
            Catch ex As Exception
                DiagnosticLogService.LogException("Extensions.Order", ex)
            End Try
            Return text.Split(","c).Select(Function(t) t.Trim()).Where(Function(t) t.Length > 0).Distinct(StringComparer.Ordinal).ToList()
        End Function

        Friend Shared Function Ordered(definitions As IEnumerable(Of ExtensionDefinition), order As IList(Of String)) As List(Of ExtensionDefinition)
            Return definitions.
                OrderBy(Function(d)
                            Dim i = order.IndexOf(d.Id)
                            Return If(i < 0, Integer.MaxValue, i)
                        End Function).
                ThenBy(Function(d) d.Name, StringComparer.CurrentCultureIgnoreCase).
                ToList()
        End Function

        ''' <summary>Schiebt eine Erweiterung in der Reihenfolge um <paramref name="delta"/> Plaetze
        ''' (-1 nach oben, +1 nach unten) und speichert die ganze Reihenfolge, damit eine spaeter
        ''' geladene Erweiterung die festgelegte nicht verschiebt.</summary>
        Public Shared Function MoveInOrder(id As String, delta As Integer) As Boolean
            Dim ids = All.Select(Function(d) d.Id).ToList()
            Dim index = ids.IndexOf(id)
            Dim target = index + delta
            If index < 0 OrElse target < 0 OrElse target >= ids.Count Then Return False
            ids.RemoveAt(index)
            ids.Insert(target, id)
            ' Kennungen, die gerade nicht geladen sind, behalten ihren Platz hinten in der Liste:
            ' kommen sie wieder, stehen sie, wo sie standen.
            Dim kept = ReadOrder().Where(Function(k) Not ids.Contains(k))
            AppSettingsService.SaveExtensionOrder(String.Join(",", ids.Concat(kept)))
            Interlocked.Increment(_panelOrderRevision)
            RaiseEvent Changed(Nothing, EventArgs.Empty)
            Return True
        End Function

        Private Shared _panelOrderRevision As Long

        ''' <summary>Zaehlt bei jeder Aenderung des Bestands UND der Reihenfolge hoch. Die Gruppen im
        ''' Werkzeug bauen sich daran neu; der Schluessel der Bildkette haengt nur an
        ''' <see cref="Revision"/>, denn die Reihenfolge der Gruppen aendert kein Bild.</summary>
        Public Shared ReadOnly Property PanelRevision As Long
            Get
                Return Interlocked.Read(_revision) * 1_000_003L + Interlocked.Read(_panelOrderRevision)
            End Get
        End Property

        ''' <summary>Dateien, die nicht geladen werden konnten, mit ihrem Fehler.</summary>
        Public Shared ReadOnly Property Failures As IReadOnlyList(Of ExtensionLoadResult)
            Get
                SyncLock _lock
                    EnsureLoaded()
                    Return _failures.ToList()
                End SyncLock
            End Get
        End Property

        Public Shared Function Find(id As String) As ExtensionDefinition
            If String.IsNullOrWhiteSpace(id) Then Return Nothing
            SyncLock _lock
                EnsureLoaded()
                Return _definitions.FirstOrDefault(Function(d) String.Equals(d.Id, id, StringComparison.Ordinal))
            End SyncLock
        End Function

        ''' <summary>Liest beide Ordner neu ein.</summary>
        Public Shared Sub Reload()
            SyncLock _lock
                LoadAll()
            End SyncLock
            RaiseEvent Changed(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>"Neu einlesen" in den Einstellungen: holt jede Erweiterung des Nutzers erneut
        ''' von der Datei, aus der sie geladen wurde, und liest dann beide Ordner neu ein.
        '''
        ''' Wer an einer Erweiterung arbeitet, aendert seine eigene Datei, nicht die Kopie im
        ''' Erweiterungsordner. Ohne diesen Schritt haette er nach jeder Aenderung erneut "Laden"
        ''' waehlen muessen.
        '''
        ''' Fehlt die Ursprungsdatei, gilt still die Kopie im Erweiterungsordner, wie ohne Ursprung.
        ''' Liegt sie noch dort, laesst sich aber nicht mehr laden, ersetzt sie die Kopie NICHT: die
        ''' zuletzt gute bleibt, und der Grund steht in der Rueckgabe. Liefert je betroffener
        ''' Erweiterung eine Zeile, leer wenn nichts zu berichten ist.</summary>
        Public Shared Function RefreshFromSources() As List(Of String)
            Dim notes As New List(Of String)()
            Dim sources As Dictionary(Of String, String) = Nothing
            Try
                sources = AppSettingsService.Load().ExtensionSources
            Catch ex As Exception
                DiagnosticLogService.LogException("Extensions.Sources", ex)
            End Try
            If sources IsNot Nothing Then
                For Each pair In sources.ToList()
                    Dim installed = Path.Combine(UserFolder, pair.Key & FileExtension)
                    If Not File.Exists(installed) Then Continue For
                    ' Liegt sie nicht mehr am Ursprung, gilt die Kopie im Erweiterungsordner, ohne
                    ' Meldung: verschobene oder geloeschte Quellen sind der Normalfall, kein Fehler.
                    If Not File.Exists(pair.Value) Then Continue For
                    Try
                        If File.ReadAllBytes(pair.Value).AsSpan().SequenceEqual(File.ReadAllBytes(installed)) Then Continue For
                    Catch ex As Exception
                        notes.Add($"{pair.Key}: {ex.Message}")
                        Continue For
                    End Try
                    Dim loaded = LoadFile(pair.Value)
                    If Not loaded.Succeeded Then
                        notes.Add($"{pair.Key}: {loaded.ErrorText}")
                        Continue For
                    End If
                    If Not String.Equals(loaded.Definition.Id, pair.Key, StringComparison.Ordinal) Then
                        notes.Add($"{pair.Key}: the file now has the id ""{loaded.Definition.Id}"", load it again to add it")
                        Continue For
                    End If
                    Try
                        ImageProcessor.WriteAllBytesAtomic(installed, File.ReadAllBytes(pair.Value))
                    Catch ex As Exception
                        notes.Add($"{pair.Key}: {ex.Message}")
                    End Try
                Next
            End If
            Reload()
            Return notes
        End Function

        Private Shared Sub EnsureLoaded()
            If _definitions Is Nothing Then LoadAll()
        End Sub

        Private Shared Sub LoadAll()
            Dim definitions As New List(Of ExtensionDefinition)()
            Dim failures As New List(Of ExtensionLoadResult)()
            ' Erst der Nutzer, dann das Mitgelieferte: wer zuerst kommt, behaelt die Kennung.
            For Each source In {(Folder:=UserFolder, Bundled:=False), (Folder:=BundledFolder, Bundled:=True)}
                Dim files As String() = Array.Empty(Of String)()
                Try
                    If Directory.Exists(source.Folder) Then
                        files = Directory.GetFiles(source.Folder, "*" & FileExtension).
                            OrderBy(Function(f) f, StringComparer.Ordinal).ToArray()
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("Extensions.List", ex)
                End Try
                For Each file In files
                    Dim loaded = LoadFile(file)
                    If Not loaded.Succeeded Then
                        failures.Add(loaded)
                        DiagnosticLogService.LogAlways("Extensions.Load", Path.GetFileName(file) & ": " & loaded.ErrorText)
                        Continue For
                    End If
                    If definitions.Any(Function(d) String.Equals(d.Id, loaded.Definition.Id, StringComparison.Ordinal)) Then Continue For
                    loaded.Definition.IsBundled = source.Bundled
                    definitions.Add(loaded.Definition)
                Next
            Next
            _definitions = definitions
            _failures = failures
            Interlocked.Increment(_revision)
        End Sub

        ''' <summary>Nimmt eine Erweiterungsdatei in den Ordner des Nutzers auf. Sie wird erst
        ''' vollstaendig geprueft (Aufbau, Grenzen, Shader uebersetzt) und nur bei Erfolg kopiert;
        ''' eine vorhandene mit derselben Kennung wird ersetzt.</summary>
        Public Shared Function Install(sourcePath As String) As ExtensionLoadResult
            Dim loaded = LoadFile(sourcePath)
            If Not loaded.Succeeded Then Return loaded
            Try
                Directory.CreateDirectory(UserFolder)
                Dim target = Path.Combine(UserFolder, loaded.Definition.Id & FileExtension)
                ' Eine fruehere Datei derselben Kennung unter anderem Namen weicht, sonst laege
                ' die Erweiterung zweimal im Ordner und die aeltere koennte gewinnen.
                For Each existing In Directory.GetFiles(UserFolder, "*" & FileExtension)
                    If PathIdentity.Equals(existing, target) Then Continue For
                    Dim other = LoadFile(existing)
                    If other.Succeeded AndAlso String.Equals(other.Definition.Id, loaded.Definition.Id, StringComparison.Ordinal) Then
                        File.Delete(existing)
                    End If
                Next
                If Not PathIdentity.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target)) Then
                    ImageProcessor.WriteAllBytesAtomic(target, File.ReadAllBytes(sourcePath))
                    ' Der Ursprung, damit "Neu einlesen" ihn wieder findet.
                    AppSettingsService.SaveExtensionSource(loaded.Definition.Id, Path.GetFullPath(sourcePath))
                End If
                loaded.FilePath = target
            Catch ex As Exception
                DiagnosticLogService.LogException("Extensions.Install", ex)
                Return New ExtensionLoadResult With {.FilePath = sourcePath, .ErrorText = ex.Message}
            End Try
            Reload()
            loaded.Definition = If(Find(loaded.Definition.Id), loaded.Definition)
            Return loaded
        End Function

        ''' <summary>Entfernt eine Erweiterung des Nutzers. Rezepte, die sie nennen, behalten ihren
        ''' Eintrag; er rechnet wieder, sobald sie erneut geladen ist.</summary>
        Public Shared Function Remove(id As String) As Boolean
            Dim definition = Find(id)
            If definition Is Nothing OrElse definition.IsBundled Then Return False
            Try
                If File.Exists(definition.FilePath) Then File.Delete(definition.FilePath)
            Catch ex As Exception
                DiagnosticLogService.LogException("Extensions.Remove", ex)
                Return False
            End Try
            AppSettingsService.SaveExtensionSource(definition.Id, "")
            Reload()
            Return True
        End Function

        ''' <summary>Der Text in der Sprache der Oberflaeche, sonst Englisch, sonst der erste, den
        ''' die Erweiterung mitbringt, sonst <paramref name="fallback"/>.</summary>
        Friend Shared Function Localized(texts As Dictionary(Of String, String), fallback As String) As String
            If texts Is Nothing OrElse texts.Count = 0 Then Return fallback
            ' DER KULTURNAME, nicht EffectiveLanguage: das liefert den englischen Namen der Sprache
            ' ("German"), und unter dem fand sich kein Text der Datei. Die Oberflaeche zeigte dann
            ' immer Englisch.
            Dim culture = LocalizationService.EffectiveCulture
            Dim text As String = Nothing
            ' Erst der volle Name ("zh-CN", "pt-BR"), dann die Sprache allein ("zh", "pt").
            For Each code In {If(culture.Name, ""), If(culture.TwoLetterISOLanguageName, "")}
                If code.Length = 0 OrElse code = "iv" Then Continue For
                If texts.TryGetValue(code, text) AndAlso Not String.IsNullOrWhiteSpace(text) Then Return text
            Next
            ' Bokmaal liegt bei uns als "nb"; Nynorsk und die Makrosprache landen dort wie bei den
            ' Ressourcen der Anwendung.
            If {"nn", "no"}.Contains(culture.TwoLetterISOLanguageName) AndAlso texts.TryGetValue("nb", text) AndAlso
               Not String.IsNullOrWhiteSpace(text) Then Return text
            If texts.TryGetValue("en", text) AndAlso Not String.IsNullOrWhiteSpace(text) Then Return text
            Dim first = texts.Values.FirstOrDefault(Function(v) Not String.IsNullOrWhiteSpace(v))
            Return If(first, fallback)
        End Function

        ' ── Lesen und Pruefen einer Datei ───────────────────────────────────────

        Private Shared ReadOnly IdPattern As New Regex("^[a-z0-9]+([.-][a-z0-9]+)+$", RegexOptions.CultureInvariant)
        Private Shared ReadOnly TrackColorPattern As New Regex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)
        Private Shared ReadOnly KeyPattern As New Regex("^[a-z][A-Za-z0-9]{0,31}$", RegexOptions.CultureInvariant)

        ''' <summary>Namen, die in der Rechnung schon vergeben sind (der Rahmen um die Rechnung und
        ''' die mitgegebenen Hilfsfunktionen) oder die die Shadersprache selbst belegt. Ein Regler
        ''' unter so einem Namen ergaebe einen Uebersetzungsfehler, den der Autor nicht deuten kann.</summary>
        Private Shared ReadOnly ReservedKeys As New HashSet(Of String)(StringComparer.Ordinal) From {
            "src", "main", "effect", "p", "color", "half", "float", "int", "bool", "uniform", "shader", "return",
            "if", "else", "for", "while", "do", "const", "in", "out", "inout", "true", "false", "void", "struct",
            "vec2", "vec3", "vec4", "float2", "float3", "float4", "half2", "half3", "half4", "mat2", "mat3", "mat4",
            "break", "continue", "discard", "switch", "case", "default"}

        ''' <summary>Liest und prueft eine Datei, ohne sie in den Bestand aufzunehmen.</summary>
        Public Shared Function LoadFile(filePath As String) As ExtensionLoadResult
            Dim result As New ExtensionLoadResult With {.FilePath = filePath}
            Try
                Dim info = New FileInfo(filePath)
                If Not info.Exists Then
                    result.ErrorText = "file not found"
                    Return result
                End If
                If info.Length > MaxFileBytes Then
                    result.ErrorText = $"file is larger than {MaxFileBytes \ 1024} KB"
                    Return result
                End If
                Dim definition As ExtensionDefinition = Nothing
                Dim problem = ParseDefinition(File.ReadAllText(filePath, Encoding.UTF8), definition)
                If problem.Length > 0 Then
                    result.ErrorText = problem
                    Return result
                End If
                definition.FilePath = filePath
                problem = ImageProcessor.BuildExtensionEffects(definition)
                If problem.Length > 0 Then
                    result.ErrorText = "shader: " & problem
                    Return result
                End If
                result.Definition = definition
            Catch ex As Exception
                result.ErrorText = ex.Message
            End Try
            Return result
        End Function

        ''' <summary>Der Text einer Erweiterungsdatei als Erweiterung, ohne Shader zu uebersetzen.
        ''' Liefert leer bei Erfolg, sonst den Fehler in der Sprache der Autoren (Englisch, wie die
        ''' Anleitung).</summary>
        Friend Shared Function ParseDefinition(json As String, ByRef definition As ExtensionDefinition) As String
            definition = Nothing
            Dim document As JsonDocument
            Try
                document = JsonDocument.Parse(json, New JsonDocumentOptions With {
                    .CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})
            Catch ex As JsonException
                Return "not valid JSON: " & ex.Message
            End Try
            Using document
                Dim root = document.RootElement
                If root.ValueKind <> JsonValueKind.Object Then Return "the file must contain one JSON object"

                Dim format = 0
                Dim element As JsonElement
                If Not root.TryGetProperty("format", element) OrElse element.ValueKind <> JsonValueKind.Number OrElse
                   Not element.TryGetInt32(format) Then Return """format"" is missing (use 1)"
                If format <> FormatVersion Then Return $"""format"" {format} is not supported by this version (it reads {FormatVersion})"

                Dim result As New ExtensionDefinition()
                result.Id = ReadString(root, "id")
                If result.Id.Length = 0 Then Return """id"" is missing"
                If result.Id.Length > 64 OrElse Not IdPattern.IsMatch(result.Id) Then
                    Return """id"" must look like ""author.name"": lower case letters, digits, dots and hyphens, at least two parts"
                End If

                Dim version = 0
                If Not root.TryGetProperty("version", element) OrElse element.ValueKind <> JsonValueKind.Number OrElse
                   Not element.TryGetInt32(version) OrElse version < 1 Then Return """version"" must be a whole number from 1"
                result.Version = version

                Dim problem = ReadTexts(root, "name", result.Names)
                If problem.Length > 0 Then Return problem
                If result.Names.Count = 0 Then Return """name"" is missing"
                problem = ReadTexts(root, "description", result.Descriptions)
                If problem.Length > 0 Then Return problem
                result.Author = ReadString(root, "author")
                result.License = ReadString(root, "license")
                result.Url = ReadString(root, "url")

                Dim stage = ReadString(root, "stage")
                If stage.Length = 0 Then stage = ExtensionStages.BeforeGrain
                If stage <> ExtensionStages.BeforeGrain AndAlso stage <> ExtensionStages.AfterGrain Then
                    Return $"""stage"" must be ""{ExtensionStages.BeforeGrain}"" or ""{ExtensionStages.AfterGrain}"""
                End If
                result.Stage = stage

                If root.TryGetProperty("controls", element) Then
                    If element.ValueKind <> JsonValueKind.Array Then Return """controls"" must be a list"
                    If element.GetArrayLength() > MaxControls Then Return $"more than {MaxControls} controls"
                    Dim index = 0
                    For Each item In element.EnumerateArray()
                        index += 1
                        Dim control As ExtensionControl = Nothing
                        problem = ReadControl(item, control)
                        If problem.Length > 0 Then Return $"control {index}: {problem}"
                        If result.FindControl(control.Key) IsNot Nothing Then Return $"control {index}: key ""{control.Key}"" is used twice"
                        result.Controls.Add(control)
                    Next
                    ' Eine Spur, die einem anderen Regler folgt, braucht diesen Regler, und zwar einen
                    ' Regler und keinen Schalter.
                    index = 0
                    For Each control In result.Controls
                        index += 1
                        If control.TrackSource.Length = 0 Then Continue For
                        Dim source = result.FindControl(control.TrackSource)
                        If source Is Nothing OrElse source.IsToggle Then
                            Return $"control {index}: ""track"" refers to ""{control.TrackSource}"", which is not a slider of this extension"
                        End If
                    Next
                End If

                If Not root.TryGetProperty("shader", element) Then Return """shader"" is missing"
                If element.ValueKind = JsonValueKind.String Then
                    result.ShaderSource = If(element.GetString(), "")
                ElseIf element.ValueKind = JsonValueKind.Array Then
                    Dim lines As New List(Of String)()
                    For Each line In element.EnumerateArray()
                        If line.ValueKind <> JsonValueKind.String Then Return """shader"" as a list must contain only text lines"
                        lines.Add(If(line.GetString(), ""))
                    Next
                    result.ShaderSource = String.Join(vbLf, lines)
                Else
                    Return """shader"" must be text or a list of text lines"
                End If
                If String.IsNullOrWhiteSpace(result.ShaderSource) Then Return """shader"" is empty"

                definition = result
                Return ""
            End Using
        End Function

        Private Shared Function ReadControl(item As JsonElement, ByRef control As ExtensionControl) As String
            control = Nothing
            If item.ValueKind <> JsonValueKind.Object Then Return "must be an object"
            Dim result As New ExtensionControl()
            result.Key = ReadString(item, "key")
            If Not KeyPattern.IsMatch(result.Key) Then
                Return """key"" must start with a lower case letter and contain only letters and digits (at most 32)"
            End If
            If ReservedKeys.Contains(result.Key) OrElse result.Key.StartsWith("fp", StringComparison.Ordinal) Then
                Return $"key ""{result.Key}"" is reserved (names of the shader language and names starting with ""fp"")"
            End If

            Dim kind = ReadString(item, "type")
            If kind.Length = 0 Then kind = ExtensionControlKinds.Slider
            If kind <> ExtensionControlKinds.Slider AndAlso kind <> ExtensionControlKinds.Toggle Then
                Return $"""type"" must be ""{ExtensionControlKinds.Slider}"" or ""{ExtensionControlKinds.Toggle}"""
            End If
            result.Kind = kind

            Dim problem = ReadTexts(item, "label", result.Labels)
            If problem.Length > 0 Then Return problem
            If result.Labels.Count = 0 Then Return """label"" is missing"
            problem = ReadTexts(item, "section", result.Sections)
            If problem.Length > 0 Then Return problem

            Dim element As JsonElement
            If result.IsToggle Then
                result.Minimum = 0 : result.Maximum = 1
                If item.TryGetProperty("default", element) Then
                    If element.ValueKind <> JsonValueKind.True AndAlso element.ValueKind <> JsonValueKind.False Then
                        Return """default"" of a toggle must be true or false"
                    End If
                    result.DefaultValue = If(element.ValueKind = JsonValueKind.True, 1.0, 0.0)
                End If
            Else
                Dim minimum, maximum, standard As Double
                If Not ReadNumber(item, "min", minimum) OrElse Not ReadNumber(item, "max", maximum) Then
                    Return """min"" and ""max"" must be numbers"
                End If
                If Not (maximum > minimum) Then Return """max"" must be greater than ""min"""
                If Not ReadNumber(item, "default", standard) Then Return """default"" must be a number"
                If standard < minimum OrElse standard > maximum Then Return """default"" lies outside ""min"" and ""max"""
                result.Minimum = minimum : result.Maximum = maximum : result.DefaultValue = standard
                Dim decimals = 0
                If item.TryGetProperty("decimals", element) Then
                    If element.ValueKind <> JsonValueKind.Number OrElse Not element.TryGetInt32(decimals) OrElse
                       decimals < 0 OrElse decimals > 2 Then Return """decimals"" must be 0, 1 or 2"
                End If
                result.Decimals = decimals
                If item.TryGetProperty("track", element) Then
                    If element.ValueKind = JsonValueKind.String Then
                        Dim name = If(element.GetString(), "").Trim()
                        If Not ExtensionTracks.Named.Contains(name) Then
                            Return """track"" must be one of " & String.Join(", ", ExtensionTracks.Named.Select(Function(n) """" & n & """")) &
                                   " or a list of colors"
                        End If
                        result.Track = name
                    ElseIf element.ValueKind = JsonValueKind.Array Then
                        For Each entry In element.EnumerateArray()
                            Dim color = If(entry.ValueKind = JsonValueKind.String, If(entry.GetString(), "").Trim(), "")
                            If Not TrackColorPattern.IsMatch(color) Then Return """track"": colors must be written as ""#RRGGBB"""
                            result.TrackColors.Add(color)
                        Next
                        If result.TrackColors.Count < 2 OrElse result.TrackColors.Count > 8 Then
                            Return """track"" as a list needs 2 to 8 colors"
                        End If
                        result.Track = ExtensionTracks.Custom
                    ElseIf element.ValueKind = JsonValueKind.Object Then
                        For Each pair In element.EnumerateObject()
                            If result.Track.Length > 0 Then Return """track"" as an object takes exactly one entry"
                            If pair.Name <> ExtensionTracks.SaturationOf AndAlso pair.Name <> ExtensionTracks.LuminanceOf Then
                                Return """track"" as an object must be { ""saturation"": key } or { ""luminance"": key }"
                            End If
                            If pair.Value.ValueKind <> JsonValueKind.String Then Return """track"": the key of the hue control must be text"
                            result.Track = pair.Name
                            result.TrackSource = If(pair.Value.GetString(), "").Trim()
                        Next
                        If result.Track.Length = 0 Then Return """track"" as an object takes exactly one entry"
                    Else
                        Return """track"" must be a name, a list of colors or an object"
                    End If
                End If
            End If
            control = result
            Return ""
        End Function

        Private Shared Function ReadString(parent As JsonElement, name As String) As String
            Dim element As JsonElement
            If parent.TryGetProperty(name, element) AndAlso element.ValueKind = JsonValueKind.String Then
                Return If(element.GetString(), "").Trim()
            End If
            Return ""
        End Function

        Private Shared Function ReadNumber(parent As JsonElement, name As String, ByRef value As Double) As Boolean
            Dim element As JsonElement
            Return parent.TryGetProperty(name, element) AndAlso element.ValueKind = JsonValueKind.Number AndAlso
                   element.TryGetDouble(value) AndAlso Double.IsFinite(value)
        End Function

        ''' <summary>Ein Text, entweder schlicht (gilt dann als Englisch) oder je Sprache. Fehlt das
        ''' Feld, bleibt <paramref name="target"/> leer und es ist kein Fehler.</summary>
        Private Shared Function ReadTexts(parent As JsonElement, name As String, target As Dictionary(Of String, String)) As String
            Dim element As JsonElement
            If Not parent.TryGetProperty(name, element) Then Return ""
            If element.ValueKind = JsonValueKind.String Then
                Dim text = If(element.GetString(), "").Trim()
                If text.Length > 0 Then target("en") = text
                Return ""
            End If
            If element.ValueKind <> JsonValueKind.Object Then Return $"""{name}"" must be text or an object with one text per language"
            For Each pair In element.EnumerateObject()
                If pair.Value.ValueKind <> JsonValueKind.String Then Return $"""{name}"": the text for ""{pair.Name}"" is not text"
                Dim text = If(pair.Value.GetString(), "").Trim()
                If text.Length > 0 Then target(pair.Name.Trim()) = text
            Next
            Return ""
        End Function

    End Class

End Namespace
