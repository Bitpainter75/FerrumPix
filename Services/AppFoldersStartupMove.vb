Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading

Namespace Services

    ''' <summary>Welcher Umzug beim Start ansteht.</summary>
    Public Enum StartupMoveKind
        ''' <summary>Der vorgemerkte Wechsel des Datenordners.</summary>
        DataFolder
        ''' <summary>Der einmalige Umzug von 0.9.92 aus dem Ordner der Einstellungen.</summary>
        Layout
    End Enum

    ''' <summary>Ein Umzug, der beim Start ansteht, mit seinen beiden Orten.</summary>
    Public NotInheritable Class StartupMovePlan
        Public Property Kind As StartupMoveKind
        Public Property Source As String = ""
        Public Property Target As String = ""
    End Class

    ''' <summary>Der Teil des Starts, der den Umzug SICHTBAR macht: ob einer ansteht, sein Stand
    ''' je Datei und die Meldungen danach (StartupMoveCoordinator, DataFolderMoveWindow). Der Umzug
    ''' selbst steht unveraendert in AppFoldersStartup.vb.</summary>
    Partial Public NotInheritable Class AppFolders

        ''' <summary>Bekommt waehrend <see cref="PrepareAtStartup(IProgress(Of FolderMoveProgress))"/>
        ''' den Stand des Wechsels; sonst Nothing.</summary>
        Private Shared _startupProgress As IProgress(Of FolderMoveProgress)

        ''' <summary>Steht beim Start ein Umzug an, der spuerbar dauern kann? Ein vorgemerkter
        ''' Wechsel (nicht das Uebernehmen eines vorhandenen Datenordners, das ist ein Eintrag) oder
        ''' der einmalige Umzug von 0.9.92, wenn bei den Einstellungen noch Modelle oder
        ''' Erweiterungen liegen. Nothing sonst.
        '''
        ''' LIEST NUR: settings.json und ob es die Ordner gibt. Im Datenordner wird dabei nichts
        ''' geoeffnet oder angelegt; ob er gilt, entscheidet seine Marke wie beim Start auch.</summary>
        Public Shared Function PlanStartupMove() As StartupMovePlan
            Return PlanStartupMove(ReadFolderSettings(SettingsPath))
        End Function

        Friend Shared Function PlanStartupMove(saved As FolderSettings) As StartupMovePlan
            Try
                ' Ohne erreichbaren Datenordner zieht nichts um (RunPendingChange wartet dann).
                If IsDataRootFallback Then Return Nothing
                If saved.PendingFolder.Length > 0 AndAlso Not saved.PendingAdopt Then
                    Dim target = Path.GetFullPath(saved.PendingFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    If Not PathIdentity.AreSame(DataRoot, target) Then
                        Return New StartupMovePlan With {.Kind = StartupMoveKind.DataFolder, .Source = DataRoot, .Target = target}
                    End If
                End If
                If saved.LayoutVersion < FolderLayoutMigration.LayoutVersion AndAlso
                   (Directory.Exists(LegacyModelsFolder) OrElse
                    Directory.Exists(Path.Combine(ConfigRoot, ExtensionsFolderName)) OrElse
                    Directory.Exists(Path.Combine(ConfigRoot, OlderExtensionsFolderName))) Then
                    Return New StartupMovePlan With {.Kind = StartupMoveKind.Layout, .Source = ConfigRoot, .Target = DataRoot}
                End If
            Catch
                ' Ein unlesbarer Pfad: dann eben ohne Fenster, der Start selbst meldet sich.
            End Try
            Return Nothing
        End Function

        ''' <summary>Wie <see cref="PrepareAtStartup()"/>, mit dem Stand je Datei an
        ''' <paramref name="progress"/>: beim Wechsel aus FolderRelocation (Anzahl und Groesse
        ''' bekannt), beim Umzug von 0.9.92 je verschobener Datei (Gesamtzahl unbekannt). Am Umzug
        ''' aendert das nichts. Laeuft auf dem Faden des Aufrufers; die Meldungen kommen auf
        ''' demselben Faden an.</summary>
        Public Shared Sub PrepareAtStartup(progress As IProgress(Of FolderMoveProgress))
            If progress Is Nothing Then
                PrepareAtStartup()
                Return
            End If
            Dim layoutFiles = 0
            Volatile.Write(_startupProgress, progress)
            FolderLayoutMigration.MoveReporter =
                Sub(file)
                    layoutFiles += 1
                    progress.Report(New FolderMoveProgress With {
                        .FilesDone = layoutFiles, .CurrentFile = If(file, ""), .Source = ConfigRoot, .Target = DataRoot})
                End Sub
            Try
                PrepareAtStartup()
            Finally
                FolderLayoutMigration.MoveReporter = Nothing
                Volatile.Write(_startupProgress, Nothing)
            End Try
        End Sub

        ''' <summary>Die Meldungen, die ein Umzug beim Start hinterlassen hat (gescheitert, zu
        ''' wenig Platz, nicht vollstaendig), genau einmal. Das Fenster des Umzugs nimmt sie und
        ''' zeigt sie; das Hauptfenster bekommt sie dann nicht noch einmal. "Nicht erreichbar"
        ''' bleibt fuer das Hauptfenster: das ist kein Ausgang eines Umzugs.</summary>
        Public Shared Function TakeMoveNotices() As List(Of FolderStartupNotice)
            SyncLock _notices
                Dim taken = _notices.Where(Function(n) n.Kind <> FolderStartupNoticeKind.DataFolderUnreachable).ToList()
                _notices.RemoveAll(Function(n) n.Kind <> FolderStartupNoticeKind.DataFolderUnreachable)
                Return taken
            End SyncLock
        End Function

        ''' <summary>Die Ueberschrift einer Meldung des Starts in der geltenden Sprache.</summary>
        Public Shared Function NoticeTitle(notice As FolderStartupNotice) As String
            If notice Is Nothing Then Return ""
            Select Case notice.Kind
                Case FolderStartupNoticeKind.DataFolderUnreachable
                    Return LocalizationService.T("Datenordner nicht erreichbar")
                Case FolderStartupNoticeKind.LayoutIncomplete
                    Return LocalizationService.T("Daten nicht vollständig umgezogen")
                Case Else
                    Return LocalizationService.T("Datenordner nicht umgezogen")
            End Select
        End Function

        ''' <summary>Der Text einer Meldung des Starts in der geltenden Sprache. Dieselben Saetze im
        ''' Fenster des Umzugs und im Hauptfenster.</summary>
        Public Shared Function NoticeMessage(notice As FolderStartupNotice) As String
            If notice Is Nothing Then Return ""
            Select Case notice.Kind
                Case FolderStartupNoticeKind.DataFolderUnreachable
                    Return String.Format(LocalizationService.T("Der Datenordner {0} ist nicht erreichbar. Bis er wieder da ist, arbeitet FerrumPix ohne Katalog, und Modelle, Erweiterungen und gespeicherte Anpassungen fehlen."), notice.Folder)
                Case FolderStartupNoticeKind.MoveTargetFull
                    Return String.Format(LocalizationService.T("Der Datenordner konnte nicht nach {0} umziehen, weil dort nicht genug Platz ist. Alles liegt weiter am bisherigen Ort."), notice.Folder)
                Case FolderStartupNoticeKind.MoveFailed
                    Return String.Format(LocalizationService.T("Der Datenordner konnte nicht nach {0} umziehen. Alles liegt weiter am bisherigen Ort, Einzelheiten stehen im Protokoll."), notice.Folder)
                Case Else
                    Return String.Format(LocalizationService.T("FerrumPix legt seine Daten jetzt gesammelt in {0} ab. Ein Teil ließ sich nicht dorthin bringen und wird am alten Ort weiter benutzt. Beim nächsten Start versucht FerrumPix es erneut; Einzelheiten stehen im Protokoll."), notice.Folder)
            End Select
        End Function

    End Class

    ''' <summary>Der Stand des Umzugs auf der Konsole, fuer den Start ohne Fenster (Browser,
    ''' Behaelter). Das Protokoll liegt selbst im Datenordner, der gerade umzieht; es bekommt
    ''' danach wie immer die Zusammenfassung (data-folder.log). Hoechstens alle zwei Sekunden eine
    ''' Zeile, englisch wie die uebrigen Zeilen des Starts auf der Konsole.</summary>
    Public NotInheritable Class StartupMoveConsoleProgress
        Implements IProgress(Of FolderMoveProgress)

        Private ReadOnly _writeLine As Action(Of String)
        Private ReadOnly _interval As TimeSpan
        Private _lastWrite As DateTime = DateTime.MinValue
        Private _lines As Integer

        ''' <param name="writeLine">Wohin die Zeile geht; Nothing: Standardausgabe.</param>
        Public Sub New(Optional writeLine As Action(Of String) = Nothing, Optional interval As TimeSpan? = Nothing)
            _writeLine = If(writeLine, AddressOf WriteToConsole)
            _interval = interval.GetValueOrDefault(TimeSpan.FromSeconds(2))
        End Sub

        ''' <summary>Wie viele Zeilen geschrieben wurden (Pruefstand).</summary>
        Public ReadOnly Property LinesWritten As Integer
            Get
                Return _lines
            End Get
        End Property

        Public Sub Announce(plan As StartupMovePlan)
            If plan Is Nothing Then Return
            Write($"Moving the FerrumPix data folder from {plan.Source} to {plan.Target}. Depending on its size this can take a while; please do not stop FerrumPix.")
        End Sub

        Public Sub Finish()
            Write("Data folder move finished, FerrumPix is starting.")
        End Sub

        Public Sub Report(value As FolderMoveProgress) Implements IProgress(Of FolderMoveProgress).Report
            If value Is Nothing Then Return
            Dim now = DateTime.UtcNow
            Dim last = value.FilesTotal > 0 AndAlso value.FilesDone >= value.FilesTotal
            If Not last AndAlso now - _lastWrite < _interval Then Return
            _lastWrite = now
            Dim inv = CultureInfo.InvariantCulture
            Dim text As String
            If value.FilesTotal > 0 Then
                text = String.Format(inv, "Data folder move: {0} of {1} files, {2:0} of {3:0} MB",
                                     value.FilesDone, value.FilesTotal, value.BytesDone / 1048576.0, value.BytesTotal / 1048576.0)
            Else
                text = String.Format(inv, "Data folder move: {0} files", value.FilesDone)
            End If
            If value.CurrentFile.Length > 0 Then text &= ": " & Path.GetFileName(value.CurrentFile)
            Write(text)
        End Sub

        Private Sub Write(text As String)
            _lines += 1
            Try
                _writeLine(text)
            Catch
            End Try
        End Sub

        Private Shared Sub WriteToConsole(text As String)
            Console.Out.WriteLine(text)
        End Sub
    End Class

End Namespace
