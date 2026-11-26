Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.Json
Imports System.Threading

Namespace Services

    ''' <summary>Das System, fuer das <see cref="AppFolders.ComputeLayout"/> die Orte bildet.</summary>
    Public Enum AppPlatform
        Linux
        Windows
        MacOS
    End Enum

    ''' <summary>Die beiden festen Orte eines Systems: der Ordner der Einstellungen und der
    ''' Datenordner ab Werk. Beide tragen schon den Namen "FerrumPix".</summary>
    Public NotInheritable Class AppFolderLayout
        ''' <summary>Hier liegt nur settings.json, und darin steht, wo der Datenordner liegt. Bis
        ''' 0.9.91 lagen hier auch Modelle, Erweiterungen, Favoriten und Suchlisten.</summary>
        Public Property ConfigRoot As String = ""
        ''' <summary>Der Datenordner, solange keiner gewaehlt ist.</summary>
        Public Property DefaultDataRoot As String = ""
    End Class

    ''' <summary>EINE Stelle fuer alle Orte, an denen FerrumPix etwas ablegt
    ''' (packaging/Plaene/ABLAGEORTE.md).
    '''
    ''' ZWEI ORTE. Im Ordner der Einstellungen liegt nur settings.json; darin steht, wo der
    ''' Datenordner liegt, und das muss ausserhalb von ihm stehen. Alles andere liegt im
    ''' Datenordner: Katalog samt Sicherungen, Kachelspeicher, Modelle, Erweiterungen, gespeicherte
    ''' Anpassungen, Favoriten, Suchlisten, Server- und Kartenzwischenspeicher, Tabellen,
    ''' Arbeitsordner, Protokolle und Sperren. Wird der Datenordner gewechselt, zieht all das mit.
    '''
    ''' JE SYSTEM. Unter Linux XDG_CONFIG_HOME und XDG_DATA_HOME, sonst ~/.config und
    ''' ~/.local/share (genau wie .NET sie bildet: eine Angabe zaehlt nur, wenn sie mit "/"
    ''' anfaengt). Im Flatpak setzt die Sandbox beide Variablen auf ~/.var/app/&lt;id&gt;/config und
    ''' data; einen Umzug zwischen System und Sandbox gibt es deshalb nicht. Unter Windows
    ''' %APPDATA% (Roaming) fuer die Einstellungen und %LOCALAPPDATA% fuer die Daten; im MSIX-Paket
    ''' leitet Windows beide in den Paketordner um, ohne dass die Anwendung davon etwas merkt.
    ''' Unter macOS liefert .NET 10 fuer BEIDE SpecialFolder ~/Library/Application Support
    ''' (nachgesehen in System.Private.CoreLib fuer osx-arm64, GetSpecialFolder:
    ''' NSApplicationSupportDirectory); Einstellungen und Daten teilen sich dort also ab Werk einen
    ''' Ordner, und ein Wechsel des Datenordners laesst die Einstellungen zurueck.
    '''
    ''' DER GEWAEHLTE ORT gilt nur, wenn er da ist UND die Marke traegt
    ''' (<see cref="DataMarkerFileName"/>). Ohne die Marke koennte ein leerer Einhaengepunkt einer
    ''' abgezogenen Platte als Datenordner gelten, und FerrumPix legte darunter auf der Systemplatte
    ''' einen neuen an. Fehlt er, gilt der Datenordner ab Werk (<see cref="IsDataRootFallback"/>),
    ''' der Katalog bleibt dann zu (CatalogLock), und das meldet sich beim Start sichtbar.
    '''
    ''' BESTIMMT WIRD DER ORT EINMAL je Prozess, beim Start (<see cref="PrepareAtStartup"/>). Ein
    ''' Wechsel gilt ab dem naechsten Start: die Dienste halten ihre Pfade, Datenbanken sind
    ''' offen, und erst ein Start, an dem nichts davon laeuft, kann alles sicher verschieben.
    '''
    ''' Die Angabe des Datenordners wird hier aus settings.json SELBST gelesen und nicht ueber
    ''' AppSettingsService: der schreibt im Fehlerfall ins Protokoll, und das Protokoll fragt nach
    ''' dem Datenordner.</summary>
    Partial Public NotInheritable Class AppFolders
        Private Sub New()
        End Sub

        ''' <summary>Der Name, unter dem FerrumPix seine Ordner anlegt.</summary>
        Public Const AppFolderName As String = "FerrumPix"

        ''' <summary>Die Marke eines gewaehlten Datenordners. Sie entsteht beim Umzug.</summary>
        Friend Const DataMarkerFileName As String = ".ferrumpix-data"

        ''' <summary>Der Unterordner, der angelegt wird, wenn der gewaehlte Ordner schon andere
        ''' Dateien enthaelt: zwischen die eigenen Dateien des Nutzers gehoeren die Daten nicht.</summary>
        Friend Const DataSubfolderName As String = "FerrumPix"

        Public Const SettingsFileName As String = "settings.json"
        Public Const ModelsFolderName As String = "Models"
        ''' <summary>Der Name des Modellordners bis 0.9.91, im Ordner der Einstellungen.</summary>
        Friend Const LegacyModelsFolderName As String = "Modelle"
        Public Const ExtensionsFolderName As String = "Extensions"
        ''' <summary>Der Erweiterungsordner vor der Umbenennung ins Englische; kein Code liest ihn.</summary>
        Friend Const OlderExtensionsFolderName As String = "Erweiterungen"
        Public Const PresetsFolderName As String = "presets"
        Public Const ThumbnailCacheFolderName As String = "ThumbnailCache"
        Public Const FontsFolderName As String = "Fonts"
        Public Const CatalogFileName As String = "library.db"
        Public Const FavoritesFileName As String = "favorites.json"
        Public Const SearchListsFileName As String = "searchlists.json"
        Public Const LogsFolderName As String = "logs"
        Friend Const StartGuardFolderName As String = "start-guard"
        ''' <summary>Die Endung der Merkdateien im Ordner des Startschutzes.</summary>
        Friend Const GuardMarkerExtension As String = ".marker"
        Friend Const BackgroundRunLockName As String = "background-run"
        Friend Const GpuBusyMarkerName As String = "gpu-busy"
        ''' <summary>Die Sperren von Modell und Karte, ohne Endung (".json"); in einem gewaehlten
        ''' Datenordner steht der Rechnername dazwischen.</summary>
        Friend Const GpuBlockFileName As String = "gpu-blocked"
        ''' <summary>Die deutschen Namen frueherer Fassungen: Ordner des Startschutzes, Endung seiner
        ''' Merkdateien und Sperrdatei der Karte. Nur noch der Umzug beim Start liest sie
        ''' (<see cref="MigrateGermanGuardNames"/>).</summary>
        Friend Const LegacyStartGuardFolderName As String = "startschutz"
        Friend Const LegacyGuardMarkerExtension As String = ".marke"
        Friend Const LegacyGpuBlockFileName As String = "karte-gesperrt"

        ' --- Fuer den Pruefstand ----------------------------------------------------------

        ''' <summary>Ersetzt den Rechnernamen, etwa um zwei Rechner an einem Ordner nachzustellen.</summary>
        Friend Shared MachineNameOverride As String = Nothing

        ' --- Die festen Orte --------------------------------------------------------------

        Private Shared _layout As AppFolderLayout

        ''' <summary>Bildet die beiden festen Orte eines Systems. Rein rechnend, damit der
        ''' Pruefstand jedes System und jede Belegung der Variablen nachstellen kann.</summary>
        ''' <param name="platform">Das System.</param>
        ''' <param name="home">Der persoenliche Ordner.</param>
        ''' <param name="getEnvironment">Liest eine Umgebungsvariable (Linux: XDG_*).</param>
        ''' <param name="getSpecialFolder">Liefert einen Systemordner (Windows).</param>
        Friend Shared Function ComputeLayout(platform As AppPlatform, home As String,
                                             getEnvironment As Func(Of String, String),
                                             getSpecialFolder As Func(Of Environment.SpecialFolder, String)) As AppFolderLayout
            Dim homeFolder = If(String.IsNullOrEmpty(home), "/", home)
            Dim configBase As String
            Dim dataBase As String
            Select Case platform
                Case AppPlatform.Windows
                    configBase = If(getSpecialFolder?.Invoke(Environment.SpecialFolder.ApplicationData), "")
                    dataBase = If(getSpecialFolder?.Invoke(Environment.SpecialFolder.LocalApplicationData), "")
                Case AppPlatform.MacOS
                    configBase = Path.Combine(homeFolder, "Library", "Application Support")
                    dataBase = configBase
                Case Else
                    configBase = XdgFolder(getEnvironment, "XDG_CONFIG_HOME", Path.Combine(homeFolder, ".config"))
                    dataBase = XdgFolder(getEnvironment, "XDG_DATA_HOME", Path.Combine(homeFolder, ".local", "share"))
            End Select
            Return New AppFolderLayout With {
                .ConfigRoot = Path.Combine(configBase, AppFolderName),
                .DefaultDataRoot = Path.Combine(dataBase, AppFolderName)}
        End Function

        ''' <summary>Eine XDG-Angabe, wie .NET sie liest: nur ein absoluter Pfad zaehlt.</summary>
        Private Shared Function XdgFolder(getEnvironment As Func(Of String, String), name As String, fallback As String) As String
            Dim value = getEnvironment?.Invoke(name)
            If String.IsNullOrEmpty(value) OrElse Not value.StartsWith("/"c) Then Return fallback
            Return value
        End Function

        ''' <summary>Das laufende System.</summary>
        Friend Shared ReadOnly Property CurrentPlatform As AppPlatform
            Get
                If OperatingSystem.IsWindows() Then Return AppPlatform.Windows
                If OperatingSystem.IsMacOS() Then Return AppPlatform.MacOS
                Return AppPlatform.Linux
            End Get
        End Property

        ''' <summary>Die festen Orte des laufenden Systems.</summary>
        Public Shared ReadOnly Property Layout As AppFolderLayout
            Get
                Dim current = Volatile.Read(_layout)
                If current IsNot Nothing Then Return current
                ' DoNotVerify: ohne die Angabe liefert .NET fuer einen noch fehlenden Ordner eine
                ' leere Zeichenkette, und aus Path.Combine("", "FerrumPix") wuerde ein relativer Pfad.
                current = ComputeLayout(CurrentPlatform,
                                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
                                        AddressOf Environment.GetEnvironmentVariable,
                                        Function(folder) Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify))
                Volatile.Write(_layout, current)
                Return current
            End Get
        End Property

        ''' <summary>Der Ordner der Einstellungen.</summary>
        Public Shared ReadOnly Property ConfigRoot As String
            Get
                Return Layout.ConfigRoot
            End Get
        End Property

        ''' <summary>Der Datenordner ab Werk.</summary>
        Public Shared ReadOnly Property DefaultDataRoot As String
            Get
                Return Layout.DefaultDataRoot
            End Get
        End Property

        Public Shared ReadOnly Property SettingsPath As String
            Get
                Return Path.Combine(ConfigRoot, SettingsFileName)
            End Get
        End Property

        ' --- Der geltende Datenordner -----------------------------------------------------

        Private Shared ReadOnly _gate As New Object()
        Private Shared _dataRoot As String
        Private Shared _dataRootFallback As Boolean
        Private Shared _configuredDataFolder As String = ""

        ''' <summary>Der Datenordner dieses Prozesses.</summary>
        Public Shared ReadOnly Property DataRoot As String
            Get
                Dim root = Volatile.Read(_dataRoot)
                If root IsNot Nothing Then Return root
                SyncLock _gate
                    If _dataRoot Is Nothing Then ResolveDataRoot()
                    Return _dataRoot
                End SyncLock
            End Get
        End Property

        ''' <summary>Der gewaehlte Datenordner ist nicht erreichbar; es gilt der ab Werk.</summary>
        Public Shared ReadOnly Property IsDataRootFallback As Boolean
            Get
                Dim unused = DataRoot
                Return Volatile.Read(_dataRootFallback)
            End Get
        End Property

        ''' <summary>Der in den Einstellungen gewaehlte Datenordner, "" fuer den ab Werk.</summary>
        Public Shared ReadOnly Property ConfiguredDataFolder As String
            Get
                Dim unused = DataRoot
                Return _configuredDataFolder
            End Get
        End Property

        ''' <summary>Gilt ein gewaehlter Datenordner? Dann kann er von mehreren Rechnern benutzt
        ''' werden, und Protokolle und Sperren tragen den Rechnernamen.</summary>
        Public Shared ReadOnly Property UsesChosenDataFolder As Boolean
            Get
                Return Not PathIdentity.AreSame(DataRoot, DefaultDataRoot)
            End Get
        End Property

        Private Shared Sub ResolveDataRoot()
            Dim saved = ReadFolderSettings(SettingsPath)
            Dim configured = NormalizeDataFolderSetting(saved.DataFolder, DefaultDataRoot)
            Dim root = DefaultDataRoot
            Dim fallback = False
            If configured.Length > 0 Then
                If IsUsableDataFolder(configured) Then
                    root = configured
                Else
                    fallback = True
                End If
            End If
            _configuredDataFolder = configured
            Volatile.Write(_dataRootFallback, fallback)
            Volatile.Write(_dataRoot, root)
        End Sub

        ''' <summary>Setzt den geltenden Datenordner, nach einem Umzug beim Start.</summary>
        Private Shared Sub ApplyDataRoot(root As String, configured As String, fallback As Boolean)
            SyncLock _gate
                _configuredDataFolder = configured
                Volatile.Write(_dataRootFallback, fallback)
                Volatile.Write(_dataRoot, root)
            End SyncLock
        End Sub

        ''' <summary>Fuer den Pruefstand: Orte und Zustand vergessen, damit sie aus den jetzigen
        ''' Umgebungsvariablen neu gebildet werden.</summary>
        Friend Shared Sub ResetForTest()
            SyncLock _gate
                Volatile.Write(_layout, Nothing)
                Volatile.Write(_dataRoot, Nothing)
                _dataRootFallback = False
                _configuredDataFolder = ""
                _legacyParts.Clear()
            End SyncLock
        End Sub

        ''' <summary>Ist dieser Ordner ein Datenordner von FerrumPix? Da und mit der Marke.</summary>
        Friend Shared Function IsUsableDataFolder(folder As String) As Boolean
            Try
                Return Directory.Exists(folder) AndAlso File.Exists(Path.Combine(folder, DataMarkerFileName))
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Die Einstellung in fester Form: voller Pfad ohne Trenner am Ende, "" fuer den
        ''' Datenordner ab Werk (auch wenn er ausdruecklich eingetragen ist).</summary>
        Friend Shared Function NormalizeDataFolderSetting(value As String, defaultRoot As String) As String
            If String.IsNullOrWhiteSpace(value) Then Return ""
            Dim full As String
            Try
                full = Path.GetFullPath(value.Trim())
            Catch
                Return ""
            End Try
            If PathIdentity.IsFilesystemRoot(full) Then Return ""
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            If PathIdentity.AreSame(full, defaultRoot) Then Return ""
            Return full
        End Function

        ''' <summary>Was settings.json ueber die Orte sagt.</summary>
        Friend Structure FolderSettings
            Public DataFolder As String
            Public PendingFolder As String
            Public PendingAdopt As Boolean
            Public LayoutVersion As Integer
            Public LegacyPresetFolder As String
        End Structure

        ''' <summary>Liest die Felder der Orte aus settings.json, ohne AppSettingsService. Eine
        ''' fehlende oder unlesbare Datei heisst: nichts gesetzt.</summary>
        Friend Shared Function ReadFolderSettings(settingsFile As String) As FolderSettings
            Dim result As New FolderSettings With {.DataFolder = "", .PendingFolder = "", .LegacyPresetFolder = ""}
            Try
                If Not File.Exists(settingsFile) Then Return result
                Using document = JsonDocument.Parse(File.ReadAllText(settingsFile))
                    Dim root = document.RootElement
                    If root.ValueKind <> JsonValueKind.Object Then Return result
                    result.DataFolder = StringField(root, NameOf(AppSettings.DataFolder))
                    result.PendingFolder = StringField(root, NameOf(AppSettings.DataFolderPending))
                    result.LegacyPresetFolder = StringField(root, NameOf(AppSettings.AdjustmentPresetFolder))
                    Dim element As JsonElement
                    If root.TryGetProperty(NameOf(AppSettings.DataFolderPendingAdopt), element) AndAlso
                       (element.ValueKind = JsonValueKind.True OrElse element.ValueKind = JsonValueKind.False) Then
                        result.PendingAdopt = element.GetBoolean()
                    End If
                    Dim version As Integer
                    If root.TryGetProperty(NameOf(AppSettings.FolderLayoutVersion), element) AndAlso
                       element.ValueKind = JsonValueKind.Number AndAlso element.TryGetInt32(version) Then
                        result.LayoutVersion = version
                    End If
                End Using
            Catch
            End Try
            Return result
        End Function

        Private Shared Function StringField(root As JsonElement, name As String) As String
            Dim element As JsonElement
            If root.TryGetProperty(name, element) AndAlso element.ValueKind = JsonValueKind.String Then Return If(element.GetString(), "")
            Return ""
        End Function

        ' --- Was im Datenordner liegt -----------------------------------------------------

        ''' <summary>Die Teile, die beim ersten Start von 0.9.92 aus dem Ordner der Einstellungen
        ''' kommen. Hat einer nicht umziehen koennen, gilt fuer ihn bis zum naechsten Versuch der
        ''' alte Ort.</summary>
        Friend Enum LegacyPart
            Extensions
            Favorites
            SearchLists
        End Enum

        Private Shared ReadOnly _legacyParts As New HashSet(Of LegacyPart)()

        Friend Shared Sub SetLegacy(part As LegacyPart, stillLegacy As Boolean)
            SyncLock _gate
                If stillLegacy Then
                    _legacyParts.Add(part)
                Else
                    _legacyParts.Remove(part)
                End If
            End SyncLock
        End Sub

        Private Shared Function IsLegacy(part As LegacyPart) As Boolean
            SyncLock _gate
                Return _legacyParts.Contains(part)
            End SyncLock
        End Function

        ''' <summary>Die Modelle. Ein abgebrochener Umzug laesst Dateien am alten Ort; die findet
        ''' AiModelService ueber <see cref="LegacyModelsFolder"/> weiter.</summary>
        Public Shared ReadOnly Property ModelsFolder As String
            Get
                Return Path.Combine(DataRoot, ModelsFolderName)
            End Get
        End Property

        ''' <summary>Der Modellordner bis 0.9.91.</summary>
        Public Shared ReadOnly Property LegacyModelsFolder As String
            Get
                Return Path.Combine(ConfigRoot, LegacyModelsFolderName)
            End Get
        End Property

        Public Shared ReadOnly Property ExtensionsFolder As String
            Get
                Return Path.Combine(If(IsLegacy(LegacyPart.Extensions), ConfigRoot, DataRoot), ExtensionsFolderName)
            End Get
        End Property

        Public Shared ReadOnly Property FavoritesPath As String
            Get
                Return Path.Combine(If(IsLegacy(LegacyPart.Favorites), ConfigRoot, DataRoot), FavoritesFileName)
            End Get
        End Property

        Public Shared ReadOnly Property SearchListsPath As String
            Get
                Return Path.Combine(If(IsLegacy(LegacyPart.SearchLists), ConfigRoot, DataRoot), SearchListsFileName)
            End Get
        End Property

        Public Shared ReadOnly Property PresetsFolder As String
            Get
                Return Path.Combine(DataRoot, PresetsFolderName)
            End Get
        End Property

        ''' <summary>Eigene Schriften (packaging/Plaene/SCHRIFTENORDNER.md). Angelegt wird der Ordner
        ''' erst, wenn jemand etwas hineinlegt, nicht beim Start; ein Wechsel des Datenordners nimmt
        ''' ihn mit wie alles andere darin.</summary>
        Public Shared ReadOnly Property FontsFolder As String
            Get
                Return Path.Combine(DataRoot, FontsFolderName)
            End Get
        End Property

        Public Shared ReadOnly Property ThumbnailCacheFolder As String
            Get
                Return Path.Combine(DataRoot, ThumbnailCacheFolderName)
            End Get
        End Property

        Public Shared ReadOnly Property CatalogPath As String
            Get
                Return Path.Combine(DataRoot, CatalogFileName)
            End Get
        End Property

        ''' <summary>Die Protokolle; in einem gewaehlten Datenordner je Rechner ein Unterordner,
        ''' damit zwei Rechner nicht in dieselbe Datei schreiben.</summary>
        Public Shared ReadOnly Property LogsFolder As String
            Get
                Dim logs = Path.Combine(DataRoot, LogsFolderName)
                Return If(UsesChosenDataFolder, Path.Combine(logs, MachineTag), logs)
            End Get
        End Property

        ''' <summary>Die Merkdateien des Startschutzes. Sie tragen Prozessnummern, die nur auf
        ''' einem Rechner etwas bedeuten.</summary>
        Public Shared ReadOnly Property StartGuardFolder As String
            Get
                Return Path.Combine(DataRoot, MachineSpecificName(StartGuardFolderName, ""))
            End Get
        End Property

        Public Shared ReadOnly Property BackgroundRunLockPath As String
            Get
                Return Path.Combine(DataRoot, MachineSpecificName(BackgroundRunLockName, ".lock"))
            End Get
        End Property

        ''' <summary>Die Marke, solange die Grafikkarte arbeitet (GpuImageService).</summary>
        Public Shared ReadOnly Property GpuBusyMarkerPath As String
            Get
                Return Path.Combine(DataRoot, MachineSpecificName(GpuBusyMarkerName, ".marker"))
            End Get
        End Property

        ''' <summary>Die Sperren von Modell und Karte (GpuModelBlockService). Sie gelten fuer die
        ''' Karte dieses Rechners.</summary>
        Public Shared ReadOnly Property GpuBlockPath As String
            Get
                Return Path.Combine(DataRoot, MachineSpecificName(GpuBlockFileName, ".json"))
            End Get
        End Property

        ''' <summary>Ein Unterordner oder eine Datei direkt im Datenordner, fuer die Zwischenspeicher
        ''' und Tabellen, die keine eigene Eigenschaft brauchen.</summary>
        Public Shared Function InData(ParamArray parts As String()) As String
            Return Path.Combine({DataRoot}.Concat(parts).ToArray())
        End Function

        ''' <summary>"name&lt;endung&gt;" im Datenordner ab Werk, "name.&lt;rechner&gt;&lt;endung&gt;"
        ''' in einem gewaehlten.</summary>
        Friend Shared Function MachineSpecificName(baseName As String, extension As String) As String
            If Not UsesChosenDataFolder Then Return baseName & extension
            Return baseName & "." & MachineTag & extension
        End Function

        ''' <summary>Der Rechnername in einer Form, die in jedem Dateinamen stehen darf.</summary>
        Public Shared ReadOnly Property MachineTag As String
            Get
                Return SafeMachineName(If(MachineNameOverride, Environment.MachineName))
            End Get
        End Property

        Friend Shared Function SafeMachineName(name As String) As String
            Dim text As New StringBuilder()
            For Each c In If(name, "")
                If Char.IsLetterOrDigit(c) OrElse c = "-"c OrElse c = "_"c Then
                    text.Append(c)
                Else
                    text.Append("_"c)
                End If
            Next
            Dim result = text.ToString().Trim("_"c)
            Return If(result.Length = 0, "host", result)
        End Function

        ' --- Einen neuen Datenordner pruefen ----------------------------------------------

        ''' <summary>Was mit einem gewaehlten Ordner geschieht.</summary>
        Public Enum DataFolderChoice
            ''' <summary>Nicht verwendbar; die Begruendung steht daneben.</summary>
            Rejected
            ''' <summary>Die Daten ziehen dorthin um.</summary>
            Move
            ''' <summary>Dort liegt schon ein Datenordner; FerrumPix arbeitet kuenftig mit ihm, die
            ''' bisherigen Daten bleiben, wo sie sind.</summary>
            Adopt
        End Enum

        ''' <summary>Prueft einen gewaehlten Ordner und nennt den Ort, an dem die Daten dann liegen:
        ''' der Ordner selbst, wenn er leer ist oder schon die Marke traegt, sonst ein Unterordner
        ''' <see cref="DataSubfolderName"/>. Die Begruendung einer Ablehnung kann so im Dialog
        ''' stehen.</summary>
        Public Shared Function ValidateNewDataFolder(chosen As String, ByRef target As String, ByRef reason As String) As DataFolderChoice
            Return ValidateNewDataFolder(chosen, DataRoot, ConfigRoot, DefaultDataRoot, target, reason)
        End Function

        Friend Shared Function ValidateNewDataFolder(chosen As String, currentRoot As String, configRootFolder As String,
                                                     defaultRoot As String,
                                                     ByRef target As String, ByRef reason As String) As DataFolderChoice
            target = ""
            reason = ""
            If String.IsNullOrWhiteSpace(chosen) Then
                reason = LocalizationService.T("Es wurde kein Ordner gewählt.")
                Return DataFolderChoice.Rejected
            End If
            Dim full As String
            Try
                full = Path.GetFullPath(chosen.Trim())
            Catch
                reason = LocalizationService.T("Dieser Ordner lässt sich nicht verwenden.")
                Return DataFolderChoice.Rejected
            End Try
            If PathIdentity.IsFilesystemRoot(full) Then
                reason = LocalizationService.T("Die Wurzel eines Laufwerks lässt sich nicht verwenden. Bitte einen Ordner darauf wählen.")
                Return DataFolderChoice.Rejected
            End If
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            If IsInFlatpakDocumentPortal(full) Then
                reason = LocalizationService.T("Im Flatpak lassen sich nur Ordner verwenden, die FerrumPix direkt sieht, etwa im persönlichen Ordner oder auf einem eingehängten Laufwerk. Ordner, die nur über die Dateiauswahl des Systems freigegeben sind, verschwinden für FerrumPix nach einem Neustart.")
                Return DataFolderChoice.Rejected
            End If
            If IsTemporaryFolder(full) Then
                reason = LocalizationService.T("Das temporäre Verzeichnis wird beim Neustart geleert. Bitte einen anderen Ordner wählen.")
                Return DataFolderChoice.Rejected
            End If
            ' Zurueck an den Ort ab Werk geht immer, auch wenn er noch nicht da ist.
            Dim toDefault = PathIdentity.AreSame(full, defaultRoot)
            Try
                If toDefault Then Directory.CreateDirectory(full)
                If Not Directory.Exists(full) Then
                    reason = LocalizationService.T("Der Ordner ist nicht erreichbar.")
                    Return DataFolderChoice.Rejected
                End If
            Catch
                reason = LocalizationService.T("Der Ordner ist nicht erreichbar.")
                Return DataFolderChoice.Rejected
            End Try

            Dim candidate As String
            If toDefault OrElse IsUsableDataFolder(full) OrElse FolderRelocation.IsEmptyDirectory(full) Then
                candidate = full
            Else
                candidate = Path.Combine(full, DataSubfolderName)
                If Directory.Exists(candidate) AndAlso Not IsUsableDataFolder(candidate) AndAlso
                   Not FolderRelocation.IsEmptyDirectory(candidate) Then
                    reason = String.Format(LocalizationService.T("In diesem Ordner liegt schon ein Ordner {0} mit anderen Dateien."), DataSubfolderName)
                    Return DataFolderChoice.Rejected
                End If
            End If

            If PathIdentity.AreSame(candidate, currentRoot) Then
                reason = LocalizationService.T("Die Daten liegen schon dort.")
                Return DataFolderChoice.Rejected
            End If
            ' Die Einstellungen bleiben, wo sie sind; unter macOS teilen sie sich den Ordner ab
            ' Werk mit den Daten, und dorthin zurueck geht es trotzdem.
            If Not toDefault AndAlso (PathIdentity.IsAncestorOrSelf(configRootFolder, candidate) OrElse
                                      PathIdentity.IsAncestorOrSelf(candidate, configRootFolder)) Then
                reason = LocalizationService.T("Der Ordner der Einstellungen lässt sich nicht als Datenordner verwenden.")
                Return DataFolderChoice.Rejected
            End If
            If PathIdentity.IsAncestorOrSelf(currentRoot, candidate) OrElse PathIdentity.IsAncestorOrSelf(candidate, currentRoot) Then
                reason = LocalizationService.T("Der neue Ort darf nicht im bisherigen liegen und der bisherige nicht im neuen.")
                Return DataFolderChoice.Rejected
            End If
            If Not CanWriteInto(full) Then
                reason = LocalizationService.T("FerrumPix darf in diesem Ordner nicht schreiben.")
                If Not String.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID")) Then
                    reason &= " " & LocalizationService.T("Im Flatpak sind die Systemordner des Rechners wie /usr, /etc und /tmp nicht erreichbar.")
                End If
                Return DataFolderChoice.Rejected
            End If
            Dim holder = CatalogLock.ForeignHolderAt(currentRoot)
            If holder IsNot Nothing Then
                reason = String.Format(LocalizationService.T("Der Katalog wird gerade von {0} benutzt. Der Datenordner kann erst umziehen, wenn FerrumPix dort beendet ist."), holder.Machine)
                Return DataFolderChoice.Rejected
            End If
            target = candidate
            If Not toDefault AndAlso IsUsableDataFolder(candidate) Then Return DataFolderChoice.Adopt
            Return DataFolderChoice.Move
        End Function

        Private Shared Function IsInFlatpakDocumentPortal(full As String) As Boolean
            If String.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID")) Then Return False
            Dim portal = FileOperationPolicy.DocumentPortalRoot
            If Not String.IsNullOrEmpty(portal) AndAlso PathIdentity.IsAncestorOrSelf(portal, full) Then Return True
            Return PathIdentity.IsAncestorOrSelf("/run/flatpak/doc", full)
        End Function

        Private Shared Function IsTemporaryFolder(full As String) As Boolean
            Dim temp = ""
            Try
                temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            Catch
            End Try
            If temp.Length > 0 AndAlso Not PathIdentity.IsFilesystemRoot(temp) AndAlso PathIdentity.IsAncestorOrSelf(temp, full) Then Return True
            Return Not OperatingSystem.IsWindows() AndAlso PathIdentity.IsAncestorOrSelf("/tmp", full)
        End Function

        ''' <summary>Legt eine Probedatei an und entfernt sie wieder.</summary>
        Friend Shared Function CanWriteInto(folder As String) As Boolean
            Dim probe = Path.Combine(folder, ".ferrumpix-probe-" & Guid.NewGuid().ToString("N"))
            Try
                File.WriteAllText(probe, "probe")
                File.Delete(probe)
                Return True
            Catch
                Try
                    If File.Exists(probe) Then File.Delete(probe)
                Catch
                End Try
                Return False
            End Try
        End Function

        Friend Shared Sub WriteDataMarker(root As String)
            Dim marker = Path.Combine(root, DataMarkerFileName)
            If File.Exists(marker) Then Return
            File.WriteAllText(marker, "FerrumPix data folder" & vbLf, Encoding.UTF8)
        End Sub

    End Class

End Namespace
