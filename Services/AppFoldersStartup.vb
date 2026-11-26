Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Threading

Namespace Services

    ''' <summary>Was beim Start mit den Orten schiefging und sichtbar gesagt werden muss.</summary>
    Public Enum FolderStartupNoticeKind
        ''' <summary>Der gewaehlte Datenordner ist nicht erreichbar.</summary>
        DataFolderUnreachable
        ''' <summary>Der vorgemerkte Umzug scheiterte am Platz.</summary>
        MoveTargetFull
        ''' <summary>Der vorgemerkte Umzug oder Wechsel scheiterte anders.</summary>
        MoveFailed
        ''' <summary>Der einmalige Umzug von 0.9.92 ist nicht vollstaendig.</summary>
        LayoutIncomplete
    End Enum

    ''' <summary>Eine Meldung des Starts; der Text entsteht erst beim Zeigen, in der dann
    ''' geltenden Sprache.</summary>
    Public NotInheritable Class FolderStartupNotice
        Public Property Kind As FolderStartupNoticeKind
        ''' <summary>Der Ort, um den es geht.</summary>
        Public Property Folder As String = ""
    End Class

    Partial Public NotInheritable Class AppFolders

        Private Const StartupLogFileName As String = "data-folder.log"

        Private Shared _prepared As Integer
        Private Shared ReadOnly _notices As New List(Of FolderStartupNotice)()

        ''' <summary>Der Start, vor allem anderen, was einen Ort benutzt (Program.Main): erst der
        ''' einmalige Umzug von 0.9.92, dann ein vorgemerkter Wechsel des Datenordners, dann die
        ''' Sperre des Katalogs. Was dabei geschieht, steht in data-folder.log im Protokollordner
        ''' (immer, nicht nur mit eingeschaltetem Protokoll); sichtbar wird nur ein Fehler
        ''' (<see cref="TakeStartupNotices"/>).</summary>
        Public Shared Sub PrepareAtStartup()
            If Interlocked.Exchange(_prepared, 1) = 1 Then Return
            Dim log As New List(Of String)()
            Dim saved = ReadFolderSettings(SettingsPath)
            Try
                If saved.LayoutVersion < FolderLayoutMigration.LayoutVersion AndAlso Not IsDataRootFallback Then
                    RunLayoutMigration(saved, log)
                End If
            Catch ex As Exception
                log.Add("startup failed: " & ex.Message)
                DiagnosticLogService.LogException("AppFolders.Startup", ex)
            End Try
            ' Eigener Versuch: scheitert der Umzug von 0.9.92, soll ein vorgemerkter Wechsel trotzdem
            ' laufen oder sich melden, statt still bis zum naechsten Start zu warten.
            Try
                If saved.PendingFolder.Length > 0 Then RunPendingChange(saved, log)
            Catch ex As Exception
                ' Etwa ein Ordner im Datenordner, den FerrumPix nicht lesen darf. Bis dahin ist am
                ' alten Ort nichts geloescht; die Vormerkung faellt weg und es meldet sich, statt bei
                ' jedem Start erneut still zu scheitern.
                log.Add("pending data folder change failed: " & ex.Message)
                DiagnosticLogService.LogException("AppFolders.PendingChange", ex)
                ClearPending()
                AddNotice(FolderStartupNoticeKind.MoveFailed, saved.PendingFolder)
            End Try
            If IsDataRootFallback Then
                log.Add($"chosen data folder not reachable, using {DataRoot}: {ConfiguredDataFolder}")
                AddNotice(FolderStartupNoticeKind.DataFolderUnreachable, ConfiguredDataFolder)
            End If
            ' Erst hier, wenn der Datenordner dieses Laufs feststeht, und vor allem, was eine Marke
            ' liest oder schreibt: NativeStartGuard und GpuModelBlockService greifen erst beim ersten
            ' Gebrauch der Laufzeit oder der Karte zu, lange nach Program.Main.
            Try
                MigrateGermanGuardNames(DataRoot, OwnGuardNameTags(), log)
            Catch ex As Exception
                log.Add("start guard names not migrated: " & ex.Message)
                DiagnosticLogService.LogException("AppFolders.GuardNames", ex)
            End Try
            WriteStartupLog(log)
            CatalogLock.Acquire()
        End Sub

        Private Shared Sub RunLayoutMigration(saved As FolderSettings, log As List(Of String))
            Dim result = FolderLayoutMigration.Run(ConfigRoot, DataRoot, saved.LegacyPresetFolder, log)
            SetLegacy(LegacyPart.Extensions, Not result.ExtensionsDone)
            SetLegacy(LegacyPart.Favorites, Not result.FavoritesDone)
            SetLegacy(LegacyPart.SearchLists, Not result.SearchListsDone)
            If result.PresetsDone AndAlso saved.LegacyPresetFolder.Length > 0 Then
                AppSettingsService.Update(Sub(s) s.AdjustmentPresetFolder = "")
            End If
            If result.Complete Then
                AppSettingsService.Update(Sub(s) s.FolderLayoutVersion = FolderLayoutMigration.LayoutVersion)
            Else
                AddNotice(FolderStartupNoticeKind.LayoutIncomplete, DataRoot)
            End If
        End Sub

        ''' <summary>Die Rechnerteile der Namen, die dieser Rechner umbenennt: "" (der Name ab Werk)
        ''' und in einem gewaehlten Datenordner zusaetzlich der eigene Rechnername. Die Fassungen
        ''' ANDERER Rechner bleiben, bis deren naechster Start sie umbenennt: ihre Marken tragen
        ''' Prozessnummern, die nur dort etwas bedeuten, und ein dort laufendes FerrumPix schriebe
        ''' noch in den alten Ordner.</summary>
        Private Shared Function OwnGuardNameTags() As String()
            If Not UsesChosenDataFolder Then Return {""}
            Return {"", MachineTag}
        End Function

        ''' <summary>Benennt die deutschen Namen frueherer Fassungen um: den Ordner des Startschutzes
        ''' (<see cref="LegacyStartGuardFolderName"/> wird <see cref="StartGuardFolderName"/>), darin
        ''' die Merkdateien (Endung und Teilname englisch) und die Sperrdatei der Karte
        ''' (<see cref="LegacyGpuBlockFileName"/> wird <see cref="GpuBlockFileName"/>), je mit dem
        ''' Rechnerteil aus <paramref name="machineTags"/>.
        '''
        ''' WARUM UEBERHAUPT: eine Merkdatei ist die Spur eines Absturzes, die Sperrdatei eine
        ''' dauerhafte Entscheidung. Unter dem alten Namen saehe sie keiner mehr, und der heikle Teil
        ''' liefe beim naechsten Gebrauch wieder in denselben Absturz.
        '''
        ''' EIN VORHANDENES ZIEL BLEIBT. Eine Merkdatei gleichen Namens am Ziel ist dieselbe Spur
        ''' (gleicher Teil, gleiche Prozessnummer), die alte faellt dann weg. Eine abweichende
        ''' Sperrdatei kommt als "&lt;name&gt;.old.json" daneben (FolderLayoutMigration.MoveFileMerging).
        ''' Ein danach leerer alter Ordner wird entfernt.
        '''
        ''' OHNE ALTES kostet es je Rechnerteil zwei Abfragen, ob es Ordner und Datei gibt.</summary>
        Friend Shared Sub MigrateGermanGuardNames(dataRoot As String, machineTags As IEnumerable(Of String), log As List(Of String))
            For Each tag In machineTags.Distinct(StringComparer.Ordinal)
                Dim suffix = If(String.IsNullOrEmpty(tag), "", "." & tag)
                Dim oldFolder = Path.Combine(dataRoot, LegacyStartGuardFolderName & suffix)
                If Directory.Exists(oldFolder) Then
                    MigrateGuardFolder(oldFolder, Path.Combine(dataRoot, StartGuardFolderName & suffix), log)
                End If
                Dim oldBlock = Path.Combine(dataRoot, LegacyGpuBlockFileName & suffix & ".json")
                If File.Exists(oldBlock) Then
                    FolderLayoutMigration.MoveFileMerging(oldBlock, Path.Combine(dataRoot, GpuBlockFileName & suffix & ".json"), log)
                End If
            Next
        End Sub

        Private Shared Sub MigrateGuardFolder(oldFolder As String, newFolder As String, log As List(Of String))
            Directory.CreateDirectory(newFolder)
            Dim moved = 0
            Dim dropped = 0
            For Each source In Directory.GetFiles(oldFolder)
                Dim target = Path.Combine(newFolder, EnglishGuardMarkerName(Path.GetFileName(source)))
                Try
                    If File.Exists(target) Then
                        File.Delete(source)
                        dropped += 1
                    Else
                        File.Move(source, target)
                        moved += 1
                    End If
                Catch ex As Exception
                    log.Add($"start guard: {source} not moved - {ex.Message}")
                End Try
            Next
            Dim removed = False
            Try
                If FolderRelocation.IsEmptyDirectory(oldFolder) Then
                    Directory.Delete(oldFolder)
                    removed = True
                End If
            Catch
            End Try
            log.Add($"start guard: {oldFolder} to {newFolder}, {moved} markers moved, {dropped} already there" &
                    If(removed, ", old folder removed", ", old folder kept (not empty)"))
        End Sub

        ''' <summary>Der englische Name einer Merkdatei frueherer Fassungen: Endung ".marke" wird ".marker",
        ''' die Teilnamen des Startschutzes und der Anfang der Laufmarken der Karte werden englisch.
        ''' Ein Name, der nicht so aussieht, bleibt, wie er ist.</summary>
        Friend Shared Function EnglishGuardMarkerName(name As String) As String
            If Not name.EndsWith(LegacyGuardMarkerExtension, StringComparison.Ordinal) Then Return name
            Dim stem = name.Substring(0, name.Length - LegacyGuardMarkerExtension.Length)
            For Each part In NativeStartGuard.LegacyPartNames
                If stem.StartsWith(part.OldName & ".", StringComparison.Ordinal) Then
                    stem = part.NewName & stem.Substring(part.OldName.Length)
                    Return stem & GuardMarkerExtension
                End If
            Next
            If stem.StartsWith(GpuModelBlockService.LegacyMarkerPrefix, StringComparison.Ordinal) Then
                stem = GpuModelBlockService.MarkerPrefix & stem.Substring(GpuModelBlockService.LegacyMarkerPrefix.Length)
            End If
            Return stem & GuardMarkerExtension
        End Function

        ''' <summary>Ein vorgemerkter Wechsel. Gelingt er, gilt ab sofort der neue Ort. Scheitert
        ''' er, bleibt alles am alten Ort, die Vormerkung faellt weg und es meldet sich einmal.
        ''' Bricht der Prozess mittendrin ab, bleibt die Vormerkung stehen, und der naechste Start
        ''' setzt fort.
        '''
        ''' DER ALTE ORT GILT, BIS DER NEUE VOLLSTAENDIG IST. Die Marke wird nicht mitkopiert: erst
        ''' wenn alles am Ziel liegt, bekommt es die Marke und settings.json den neuen Ort
        ''' (<see cref="CommitDataFolder"/>), und erst danach wird am alten Ort geloescht, zuerst
        ''' dessen Marke. Ein Abbruch davor laesst den alten Ort vollstaendig und das Ziel ohne Marke
        ''' (der naechste Start setzt fort, und eine halbe Kopie laesst sich nicht als vorhandener
        ''' Datenordner uebernehmen); einer danach laesst den neuen gelten und am alten hoechstens
        ''' Reste ohne Marke, die nichts mehr liest.
        '''
        ''' FEHLT DER ALTE ORT beim naechsten Start und traegt das Ziel schon die Marke, ist der
        ''' Umzug fertig geworden, nur der Eintrag fehlte noch (ein Umbenennen des ganzen Ordners
        ''' nimmt die Marke mit): dann gilt das Ziel. Wartete der Wechsel hier auf den alten Ort,
        ''' kaeme der nie wieder, und FerrumPix bliebe bei jedem Start im Rueckfall ohne Katalog.
        ''' Ohne Marke am Ziel wartet er dagegen, bis der alte Ort wieder erreichbar ist (eine
        ''' abgezogene Platte); das Hauptfenster meldet ihn als nicht erreichbar.</summary>
        Private Shared Sub RunPendingChange(saved As FolderSettings, log As List(Of String))
            Dim target As String
            Try
                target = Path.GetFullPath(saved.PendingFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            Catch ex As Exception
                log.Add("pending data folder not usable: " & saved.PendingFolder)
                ClearPending()
                Return
            End Try
            Dim setting = NormalizeDataFolderSetting(target, DefaultDataRoot)
            If IsDataRootFallback Then
                ' Ein vorhandener Datenordner braucht den alten Ort nicht, und ein Ziel mit Marke
                ' ist ein fertiger Umzug.
                If IsUsableDataFolder(target) Then
                    CommitDataFolder(target, setting, Nothing)
                    ApplyDataRoot(target, setting, False)
                    log.Add($"data folder switched to {target}; the previous one is not reachable, the move had finished")
                Else
                    log.Add("pending change waits, current data folder not reachable")
                End If
                Return
            End If
            Dim source = DataRoot
            If PathIdentity.AreSame(source, target) Then
                ClearPending()
                Return
            End If
            If saved.PendingAdopt Then
                If IsUsableDataFolder(target) Then
                    CommitDataFolder(target, setting, Nothing)
                    ApplyDataRoot(target, setting, False)
                    log.Add($"data folder switched to existing {target}, previous data stays in {source}")
                Else
                    log.Add($"pending data folder {target} carries no FerrumPix data, nothing changed")
                    ClearPending()
                    AddNotice(FolderStartupNoticeKind.MoveFailed, target)
                End If
                Return
            End If
            Dim holder = CatalogLock.ForeignHolderAt(source)
            If holder IsNot Nothing Then
                log.Add($"data folder not moved, catalog in use by {holder.Machine} (process {holder.ProcessId})")
                ClearPending()
                AddNotice(FolderStartupNoticeKind.MoveFailed, target)
                Return
            End If
            Dim moved = MoveDataFolder(source, target, ConfigRoot, Nothing, Sub() CommitDataFolder(target, setting, source),
                                       Volatile.Read(_startupProgress))
            log.Add($"data folder move {source} to {target}: {moved.Outcome}, {moved.MovedFiles} files, copied={moved.Copied} {moved.Detail}")
            Select Case moved.Outcome
                Case FolderRelocationOutcome.Moved
                    Try
                        ' Nach dem Kopieren schon geschehen und dann ohne Wirkung; nach dem
                        ' Umbenennen des ganzen Ordners steht es hier zum ersten Mal an.
                        CommitDataFolder(target, setting, source)
                        ApplyDataRoot(target, setting, False)
                    Catch ex As Exception
                        ' Nur nach dem Umbenennen moeglich: die Daten liegen am Ziel, die Marke liess
                        ' sich nicht schreiben. Diese Sitzung arbeitet dort, die Vormerkung bleibt,
                        ' und der naechste Start versucht den Eintrag erneut.
                        log.Add("marker: " & ex.Message)
                        If Not Directory.Exists(source) Then ApplyDataRoot(target, setting, False)
                        AddNotice(FolderStartupNoticeKind.MoveFailed, target)
                    End Try
                Case FolderRelocationOutcome.TargetFull
                    ClearPending()
                    AddNotice(FolderStartupNoticeKind.MoveTargetFull, target)
                Case Else
                    ClearPending()
                    AddNotice(FolderStartupNoticeKind.MoveFailed, target)
            End Select
        End Sub

        ''' <summary>Zieht den ganzen Datenordner um, ueber FolderRelocation: auf derselben Platte
        ''' ein Umbenennen, sonst Kopieren mit Pruefung, und erst am Ende wird am alten Ort
        ''' geloescht. Teilt sich der Datenordner den Ort mit den Einstellungen (macOS ab Werk),
        ''' bleiben settings.json und der alte Modellordner zurueck.
        '''
        ''' Die Marke kopiert der Umzug NICHT mit: eine halbe Kopie darf nicht wie ein Datenordner
        ''' aussehen. Am alten Ort wird sie wie der Rest entfernt; das Umbenennen des ganzen Ordners
        ''' nimmt sie dagegen mit, dort gibt es keinen halben Zustand.</summary>
        ''' <param name="beforeRemovingSource">Laeuft, wenn alles kopiert ist und bevor am alten Ort
        ''' etwas geloescht wird (nur beim Kopieren). Eine Ausnahme darin baut das Ziel zurueck.</param>
        Friend Shared Function MoveDataFolder(source As String, target As String, configRootFolder As String,
                                              freeSpaceProbe As Func(Of String, Long),
                                              beforeRemovingSource As Action) As FolderRelocationResult
            Return MoveDataFolder(source, target, configRootFolder, freeSpaceProbe, beforeRemovingSource, Nothing)
        End Function

        ''' <summary>Wie oben, mit dem Stand je Datei fuer das Fenster beim Start.</summary>
        Friend Shared Function MoveDataFolder(source As String, target As String, configRootFolder As String,
                                              freeSpaceProbe As Func(Of String, Long),
                                              beforeRemovingSource As Action,
                                              detailProgress As IProgress(Of FolderMoveProgress)) As FolderRelocationResult
            Dim options As New FolderRelocationOptions With {.RequireSameTimestamp = True, .FreeSpaceProbe = freeSpaceProbe,
                                                             .DetailProgress = detailProgress}
            If PathIdentity.AreSame(source, configRootFolder) Then
                options.IncludeEntry = Function(name) Not BelongsToConfig(name)
            End If
            options.CallerHandledFiles.Add(DataMarkerFileName)
            If beforeRemovingSource IsNot Nothing Then options.CompleteCopy = Sub(oldRoot, newRoot) beforeRemovingSource()
            Return FolderRelocation.MoveContents(source, target, options)
        End Function

        ''' <summary>Macht das Ziel zum Datenordner: erst die Marke dort (am Ort ab Werk keine),
        ''' dann der Eintrag in settings.json, dann fort mit der Marke am alten Ort, damit dort
        ''' zurueckbleibende Reste nicht mehr als Datenordner gelten. Darf mehrfach laufen. Wirft
        ''' nur, wenn die Marke am Ziel nicht entsteht; dann ist nichts eingetragen.</summary>
        ''' <param name="previousRoot">Der alte Ort, Nothing, wenn er bleiben soll, wie er ist
        ''' (ein uebernommener Datenordner, ein nicht erreichbarer).</param>
        Private Shared Sub CommitDataFolder(target As String, setting As String, previousRoot As String)
            If setting.Length > 0 Then
                WriteDataMarker(target)
            Else
                ' Zurueck am Ort ab Werk braucht es keine Marke.
                Try
                    Dim marker = Path.Combine(target, DataMarkerFileName)
                    If File.Exists(marker) Then File.Delete(marker)
                Catch
                End Try
            End If
            UpdateFolderSettings(Sub(s)
                                     s.DataFolder = setting
                                     s.DataFolderPending = ""
                                     s.DataFolderPendingAdopt = False
                                 End Sub)
            If previousRoot Is Nothing Then Return
            Try
                Dim oldMarker = Path.Combine(previousRoot, DataMarkerFileName)
                If File.Exists(oldMarker) Then File.Delete(oldMarker)
            Catch
            End Try
        End Sub

        ''' <summary>Fuer den Pruefstand: schreibt statt AppSettingsService, damit ein Wechsel in
        ''' einem Wegwerf-Heim nachgestellt werden kann.</summary>
        Friend Shared FolderSettingsWriterOverride As Action(Of Action(Of AppSettings))

        Private Shared Sub UpdateFolderSettings(mutate As Action(Of AppSettings))
            Dim writer = FolderSettingsWriterOverride
            If writer IsNot Nothing Then
                writer(mutate)
            Else
                AppSettingsService.Update(mutate)
            End If
        End Sub

        ''' <summary>Gehoert ein Eintrag zum Ordner der Einstellungen und nicht zu den Daten?</summary>
        Friend Shared Function BelongsToConfig(name As String) As Boolean
            Return name.StartsWith(SettingsFileName, StringComparison.Ordinal) OrElse
                   String.Equals(name, LegacyModelsFolderName, StringComparison.Ordinal)
        End Function

        Private Shared Sub ClearPending()
            UpdateFolderSettings(Sub(s)
                                     s.DataFolderPending = ""
                                     s.DataFolderPendingAdopt = False
                                 End Sub)
        End Sub

        Private Shared Sub AddNotice(kind As FolderStartupNoticeKind, folder As String)
            SyncLock _notices
                _notices.Add(New FolderStartupNotice With {.Kind = kind, .Folder = If(folder, "")})
            End SyncLock
        End Sub

        ''' <summary>Die Meldungen des Starts, genau einmal: wer sie nimmt, zeigt sie.</summary>
        Public Shared Function TakeStartupNotices() As List(Of FolderStartupNotice)
            SyncLock _notices
                Dim taken = _notices.ToList()
                _notices.Clear()
                Return taken
            End SyncLock
        End Function

        Private Shared Sub WriteStartupLog(log As List(Of String))
            If log.Count = 0 Then Return
            For Each line In log
                DiagnosticLogService.LogAlways("AppFolders", line)
            Next
            Try
                Dim folder = LogsFolder
                Directory.CreateDirectory(folder)
                Dim stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Globalization.CultureInfo.InvariantCulture)
                File.AppendAllLines(Path.Combine(folder, StartupLogFileName), log.Select(Function(l) stamp & " " & l))
            Catch ex As Exception
                DiagnosticLogService.LogException("AppFolders.StartupLog", ex)
            End Try
        End Sub

        ' --- Fuer die Einstellungen ------------------------------------------------------

        ''' <summary>Der vorgemerkte Datenordner, "" wenn keiner.</summary>
        Public Shared ReadOnly Property PendingDataFolder As String
            Get
                Return If(AppSettingsService.Load().DataFolderPending, "")
            End Get
        End Property

        Public Shared ReadOnly Property PendingDataFolderIsAdopt As Boolean
            Get
                Return AppSettingsService.Load().DataFolderPendingAdopt
            End Get
        End Property

        ''' <summary>Merkt einen Wechsel fuer den naechsten Start vor (der Ort aus
        ''' <see cref="ValidateNewDataFolder"/>).</summary>
        Public Shared Sub RequestDataFolderChange(target As String, adopt As Boolean)
            AppSettingsService.Update(Sub(s)
                                          s.DataFolderPending = If(target, "")
                                          s.DataFolderPendingAdopt = adopt
                                      End Sub)
        End Sub

        ''' <summary>Gibt einen gewaehlten Datenordner auf, der nicht erreichbar ist: ab dem naechsten
        ''' Start gilt wieder der ab Werk, ohne Umzug (vom fehlenden Ort laesst sich nichts holen).
        ''' Am gewaehlten Ort wird nichts angefasst.</summary>
        Public Shared Sub ForgetUnreachableDataFolder()
            AppSettingsService.Update(Sub(s)
                                          s.DataFolder = ""
                                          s.DataFolderPending = ""
                                          s.DataFolderPendingAdopt = False
                                      End Sub)
        End Sub

        ''' <summary>Nimmt einen vorgemerkten Wechsel zurueck.</summary>
        Public Shared Sub CancelDataFolderChange()
            ClearPending()
        End Sub

    End Class

End Namespace
