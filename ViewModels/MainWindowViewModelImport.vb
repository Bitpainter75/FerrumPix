Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia.Threading
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Was der Dialog "Importieren" herausgibt.</summary>
    Public Class ImportDialogResult
        Public Property Candidates As List(Of ImportCandidate)
        Public Property TargetFolder As String = ""
        Public Property FolderPattern As String = ""
        Public Property BackupFolder As String = ""
        Public Property TotalBytes As Long
    End Class

    ''' <summary>Der Dialog "Importieren" und der Lauf danach (CardImportService).
    '''
    ''' Oben die Quelle: erkannte Wechseldatentraeger zur Auswahl, oder ein beliebiger Ordner.
    ''' Gelesen wird sie beim Waehlen im Hintergrund; darunter stehen die Aufnahmetage zum An- und
    ''' Abwaehlen und eine Zusammenfassung. Dann das Ziel mit dem Muster fuer die Unterordner und auf
    ''' Wunsch ein zweites Ziel als Sicherung. Bestaetigt werden kann erst, wenn etwas gewaehlt ist.</summary>
    Partial Public Class MainWindowViewModel

        Private _dialogImportCandidates As New List(Of ImportCandidate)()
        Private _dialogImportScanCancel As CancellationTokenSource
        Private _dialogImportScanning As Boolean
        Private _dialogImportSourceFolder As String = ""
        Private _dialogImportSelectedSource As ImportSource
        Private _dialogImportTargetFolder As String = ""
        Private _dialogImportSelectedPattern As ImportFolderPattern
        Private _dialogImportIncludeRawPartners As Boolean = True
        Private _dialogImportUseBackup As Boolean
        Private _dialogImportBackupFolder As String = ""

        Public ReadOnly Property DialogImportSources As New ObservableCollection(Of ImportSource)()
        Public ReadOnly Property DialogImportDays As New ObservableCollection(Of ImportDay)()
        Public ReadOnly Property DialogImportPatterns As New ObservableCollection(Of ImportFolderPattern)()

        Public ReadOnly Property DialogShowsImport As Boolean
            Get
                Return _dialogKind = AppDialogKind.Import
            End Get
        End Property

        Public ReadOnly Property HasDialogImportSources As Boolean
            Get
                Return DialogImportSources.Count > 0
            End Get
        End Property

        Public Property DialogImportSelectedSource As ImportSource
            Get
                Return _dialogImportSelectedSource
            End Get
            Set(value As ImportSource)
                If ReferenceEquals(_dialogImportSelectedSource, value) Then Return
                Me.RaiseAndSetIfChanged(_dialogImportSelectedSource, value)
                If value IsNot Nothing Then DialogImportSourceFolder = value.Path
            End Set
        End Property

        ''' <summary>Der gelesene Ordner. Jede Aenderung liest neu ein.</summary>
        Public Property DialogImportSourceFolder As String
            Get
                Return _dialogImportSourceFolder
            End Get
            Set(value As String)
                value = If(value, "").Trim()
                If _dialogImportSourceFolder = value Then Return
                Me.RaiseAndSetIfChanged(_dialogImportSourceFolder, value)
                StartDialogImportScan()
            End Set
        End Property

        Public Property DialogImportTargetFolder As String
            Get
                Return _dialogImportTargetFolder
            End Get
            Set(value As String)
                value = If(value, "").Trim()
                If _dialogImportTargetFolder = value Then Return
                Me.RaiseAndSetIfChanged(_dialogImportTargetFolder, value)
                Me.RaisePropertyChanged(NameOf(IsDialogImportPatternVisible))
                RaiseDialogImportSummary()
            End Set
        End Property

        ''' <summary>Die Ziele zur Auswahl: der aktuelle Ordner der Galerie und die in den
        ''' Einstellungen definierten. Eine Wahl schreibt ihre Vorlage ins Zielfeld; dort laesst sie
        ''' sich weiter aendern oder ueber "Ordner wählen" ersetzen.</summary>
        Public ReadOnly Property DialogImportTargetChoices As New ObservableCollection(Of ImportFolderPattern)()

        Private _dialogImportSelectedTargetChoice As ImportFolderPattern
        Public Property DialogImportSelectedTargetChoice As ImportFolderPattern
            Get
                Return _dialogImportSelectedTargetChoice
            End Get
            Set(value As ImportFolderPattern)
                If ReferenceEquals(_dialogImportSelectedTargetChoice, value) Then Return
                Me.RaiseAndSetIfChanged(_dialogImportSelectedTargetChoice, value)
                If value IsNot Nothing Then DialogImportTargetFolder = value.Pattern
            End Set
        End Property

        ''' <summary>Die Unterordner nach Datum gibt es nur fuer einen schlichten Ordner. Eine
        ''' Vorlage mit Platzhaltern legt den Weg selbst fest.</summary>
        Public ReadOnly Property IsDialogImportPatternVisible As Boolean
            Get
                Return Not CardImportService.HasPlaceholders(_dialogImportTargetFolder)
            End Get
        End Property

        Public Property DialogImportSelectedPattern As ImportFolderPattern
            Get
                Return _dialogImportSelectedPattern
            End Get
            Set(value As ImportFolderPattern)
                If ReferenceEquals(_dialogImportSelectedPattern, value) Then Return
                Me.RaiseAndSetIfChanged(_dialogImportSelectedPattern, value)
                RaiseDialogImportSummary()
            End Set
        End Property

        Public Property DialogImportIncludeRawPartners As Boolean
            Get
                Return _dialogImportIncludeRawPartners
            End Get
            Set(value As Boolean)
                If _dialogImportIncludeRawPartners = value Then Return
                Me.RaiseAndSetIfChanged(_dialogImportIncludeRawPartners, value)
                RaiseDialogImportSummary()
            End Set
        End Property

        Public Property DialogImportUseBackup As Boolean
            Get
                Return _dialogImportUseBackup
            End Get
            Set(value As Boolean)
                If _dialogImportUseBackup = value Then Return
                Me.RaiseAndSetIfChanged(_dialogImportUseBackup, value)
                RaiseDialogImportSummary()
            End Set
        End Property

        Public Property DialogImportBackupFolder As String
            Get
                Return _dialogImportBackupFolder
            End Get
            Set(value As String)
                value = If(value, "").Trim()
                If _dialogImportBackupFolder = value Then Return
                Me.RaiseAndSetIfChanged(_dialogImportBackupFolder, value)
                RaiseDialogImportSummary()
            End Set
        End Property

        Public ReadOnly Property HasDialogImportRawPartners As Boolean
            Get
                Return _dialogImportCandidates.Any(Function(c) c.IsRawPartner)
            End Get
        End Property

        Public ReadOnly Property HasDialogImportDays As Boolean
            Get
                Return DialogImportDays.Count > 0
            End Get
        End Property

        ''' <summary>Was eingelesen wuerde, oder warum nichts: "Wird gelesen", "Keine Bilder
        ''' gefunden", sonst Zahl, Groesse, schon vorhandene und der freie Platz.</summary>
        Public ReadOnly Property DialogImportSummary As String
            Get
                If String.IsNullOrEmpty(_dialogImportSourceFolder) Then Return ""
                If _dialogImportScanning Then Return LocalizationService.T("Quelle wird gelesen…")
                If _dialogImportCandidates.Count = 0 Then Return LocalizationService.T("Keine Bilder oder Videos gefunden")
                Dim chosen = DialogImportChosen()
                Dim bytes = chosen.Sum(Function(c) c.Size)
                Dim text = String.Format(LocalizationService.T("{0} Dateien gewählt, {1}"), chosen.Count, CardImportService.FormatBytes(bytes))
                Dim target = _dialogImportTargetFolder
                If Not String.IsNullOrEmpty(target) Then
                    Dim present = CardImportService.CountProbablyPresent(chosen, target, If(_dialogImportSelectedPattern?.Pattern, ""))
                    If present > 0 Then text &= ". " & String.Format(LocalizationService.T("{0} davon liegen schon im Ziel und werden übersprungen"), present)
                    Dim free = CardImportService.FreeSpace(target)
                    If free >= 0 AndAlso free < bytes Then
                        text &= ". " & String.Format(LocalizationService.T("Am Ziel sind nur {0} frei"), CardImportService.FormatBytes(free))
                    End If
                End If
                Return text
            End Get
        End Property

        Private Function DialogImportChosen() As List(Of ImportCandidate)
            Dim days = New HashSet(Of Date)(DialogImportDays.Where(Function(d) d.IsSelected).Select(Function(d) d.Day))
            Return _dialogImportCandidates.Where(Function(c) days.Contains(c.CapturedAt.Date) AndAlso
                                                             (_dialogImportIncludeRawPartners OrElse Not c.IsRawPartner)).ToList()
        End Function

        Private Function IsDialogImportPrimaryEnabled() As Boolean
            If _dialogImportScanning OrElse String.IsNullOrWhiteSpace(_dialogImportTargetFolder) Then Return False
            If _dialogImportUseBackup AndAlso String.IsNullOrWhiteSpace(_dialogImportBackupFolder) Then Return False
            If Not FileOperationPolicy.IsInAllowedArea(CardImportService.FixedRootOf(_dialogImportTargetFolder)) Then Return False
            ' Die Sicherung ist ein Ziel wie das erste und unterliegt derselben Regel.
            If _dialogImportUseBackup AndAlso
               Not FileOperationPolicy.IsInAllowedArea(CardImportService.FixedRootOf(_dialogImportBackupFolder)) Then Return False
            Return DialogImportChosen().Count > 0
        End Function

        Private Sub RaiseDialogImportSummary()
            Me.RaisePropertyChanged(NameOf(DialogImportSummary))
            Me.RaisePropertyChanged(NameOf(HasDialogImportSummary))
            Me.RaisePropertyChanged(NameOf(IsDialogPrimaryEnabled))
        End Sub

        ''' <summary>Die Zusammenfassung erscheint erst mit einer Quelle; davor gaebe es nichts
        ''' zusammenzufassen.</summary>
        Public ReadOnly Property HasDialogImportSummary As Boolean
            Get
                Return Not String.IsNullOrEmpty(_dialogImportSourceFolder)
            End Get
        End Property

        ''' <summary>Liest die Quelle im Hintergrund. Ein neuer Ordner bricht den laufenden Lauf ab.</summary>
        Private Sub StartDialogImportScan()
            _dialogImportScanCancel?.Cancel()
            _dialogImportCandidates = New List(Of ImportCandidate)()
            DialogImportDays.Clear()
            Dim folder = _dialogImportSourceFolder
            If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then
                _dialogImportScanning = False
                RaiseDialogImportScan()
                Return
            End If
            Dim cancel As New CancellationTokenSource()
            _dialogImportScanCancel = cancel
            _dialogImportScanning = True
            RaiseDialogImportScan()
            Dim ignored = Task.Run(
                Sub()
                    Dim found As List(Of ImportCandidate) = Nothing
                    Try
                        found = CardImportService.Scan(folder, cancel.Token)
                    Catch ex As OperationCanceledException
                        Return
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Import.ScanDialog", ex)
                        found = New List(Of ImportCandidate)()
                    End Try
                    Dispatcher.UIThread.Post(
                        Sub()
                            If cancel.IsCancellationRequested OrElse Not ReferenceEquals(_dialogImportScanCancel, cancel) Then Return
                            _dialogImportCandidates = found
                            DialogImportDays.Clear()
                            For Each group In found.GroupBy(Function(c) c.CapturedAt.Date).OrderBy(Function(g) g.Key)
                                DialogImportDays.Add(New ImportDay With {
                                    .Day = group.Key, .Count = group.Count(), .Bytes = group.Sum(Function(c) c.Size),
                                    .OnSelectionChanged = AddressOf RaiseDialogImportSummary})
                            Next
                            _dialogImportScanning = False
                            RaiseDialogImportScan()
                        End Sub)
                End Sub)
        End Sub

        Private Sub RaiseDialogImportScan()
            Me.RaisePropertyChanged(NameOf(HasDialogImportDays))
            Me.RaisePropertyChanged(NameOf(HasDialogImportRawPartners))
            RaiseDialogImportSummary()
        End Sub

        Public Sub RefreshDialogImportSources()
            Dim selectedPath = _dialogImportSelectedSource?.Path
            DialogImportSources.Clear()
            For Each source In CardImportService.DetectSources()
                DialogImportSources.Add(source)
            Next
            Me.RaisePropertyChanged(NameOf(HasDialogImportSources))
            Dim again = DialogImportSources.FirstOrDefault(Function(s) s.Path = selectedPath)
            _dialogImportSelectedSource = Nothing
            DialogImportSelectedSource = If(again, DialogImportSources.FirstOrDefault())
        End Sub

        ''' <summary>Oeffnet den Dialog. Als Ziel steht zuerst <paramref name="suggestedTarget"/>, der
        ''' aktuelle Ordner der Galerie; ohne ihn der Bilder-Ordner. Zur Auswahl stehen dazu die
        ''' Ziele aus den Einstellungen. Nothing heisst abgebrochen.</summary>
        Public Async Function ShowImportDialogAsync(suggestedTarget As String) As Task(Of ImportDialogResult)
            Dim settings = AppSettingsService.Load()
            DialogImportPatterns.Clear()
            For Each choice In CardImportService.FolderPatterns()
                DialogImportPatterns.Add(choice)
            Next
            _dialogImportSelectedPattern = If(DialogImportPatterns.FirstOrDefault(Function(p) p.Pattern = settings.ImportFolderPattern),
                                              DialogImportPatterns.First())
            Dim target = suggestedTarget
            If String.IsNullOrWhiteSpace(target) OrElse Not Directory.Exists(target) Then target = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            _dialogImportTargetFolder = If(target, "")
            DialogImportTargetChoices.Clear()
            Dim current = New ImportFolderPattern With {
                .Pattern = _dialogImportTargetFolder,
                .Label = String.Format(LocalizationService.T("Aktueller Ordner: {0}"), _dialogImportTargetFolder)}
            DialogImportTargetChoices.Add(current)
            For Each defined In If(settings.ImportTargets, New List(Of ImportTargetSettings)())
                If defined Is Nothing OrElse String.IsNullOrWhiteSpace(defined.Template) Then Continue For
                Dim name = If(String.IsNullOrWhiteSpace(defined.Name), defined.Template, defined.Name & ": " & defined.Template)
                DialogImportTargetChoices.Add(New ImportFolderPattern With {.Pattern = defined.Template.Trim(), .Label = name})
            Next
            _dialogImportSelectedTargetChoice = current
            Me.RaisePropertyChanged(NameOf(DialogImportSelectedTargetChoice))
            Me.RaisePropertyChanged(NameOf(IsDialogImportPatternVisible))
            _dialogImportIncludeRawPartners = settings.ImportIncludeRawPartners
            _dialogImportUseBackup = settings.ImportUseBackup
            _dialogImportBackupFolder = If(settings.ImportBackupFolder, "")
            _dialogImportSourceFolder = ""
            _dialogImportSelectedSource = Nothing
            Me.RaisePropertyChanged(NameOf(DialogImportSelectedPattern))
            Me.RaisePropertyChanged(NameOf(DialogImportTargetFolder))
            Me.RaisePropertyChanged(NameOf(DialogImportIncludeRawPartners))
            Me.RaisePropertyChanged(NameOf(DialogImportUseBackup))
            Me.RaisePropertyChanged(NameOf(DialogImportBackupFolder))
            Me.RaisePropertyChanged(NameOf(DialogImportSourceFolder))
            RefreshDialogImportSources()

            Dim answer = Await ShowDialogAsync(AppDialogKind.Import,
                                               "Importieren",
                                               "Kopiert Bilder und Videos geprüft in den Bestand. Auf der Quelle wird nichts verändert, und im Ziel wird keine vorhandene Datei ersetzt.",
                                               "",
                                               "Importieren",
                                               "Abbrechen")
            _dialogImportScanCancel?.Cancel()
            If answer Is Nothing Then Return Nothing

            Dim chosen = DialogImportChosen()
            If chosen.Count = 0 Then Return Nothing
            Dim pattern = If(_dialogImportSelectedPattern?.Pattern, "")
            Dim backup = If(_dialogImportUseBackup, _dialogImportBackupFolder, "")
            AppSettingsService.Update(Sub(s)
                                          s.ImportTargetFolder = _dialogImportTargetFolder
                                          s.ImportFolderPattern = pattern
                                          s.ImportIncludeRawPartners = _dialogImportIncludeRawPartners
                                          s.ImportUseBackup = _dialogImportUseBackup
                                          s.ImportBackupFolder = _dialogImportBackupFolder
                                      End Sub)
            Return New ImportDialogResult With {
                .Candidates = chosen, .TargetFolder = _dialogImportTargetFolder, .FolderPattern = pattern,
                .BackupFolder = backup, .TotalBytes = chosen.Sum(Function(c) c.Size)}
        End Function

        ''' <summary>Der Lauf mit Warteanzeige, abbrechbar. Vorher der Platz: reicht er an einem der
        ''' Ziele nicht, wird nichts kopiert.</summary>
        Public Async Function RunImportAsync(request As ImportDialogResult) As Task(Of ImportResult)
            If request Is Nothing Then Return Nothing
            For Each root In {request.TargetFolder, request.BackupFolder}.Where(Function(r) Not String.IsNullOrWhiteSpace(r))
                ' Noch einmal hier und nicht nur am Knopf des Dialogs: der Lauf ist der Ort, an dem
                ' geschrieben wird, und jedes Ziel, auch die Sicherung, unterliegt der Dateiregel.
                If Not FileOperationPolicy.IsInAllowedArea(CardImportService.FixedRootOf(root)) Then
                    Await ShowMessageAsync(LocalizationService.T("Ziel nicht erlaubt"),
                                           String.Format(LocalizationService.T("{0} liegt außerhalb der Ordner, in die FerrumPix schreiben darf. Es wurde nichts kopiert."), root))
                    Return Nothing
                End If
                Dim free = CardImportService.FreeSpace(root)
                If free >= 0 AndAlso free < request.TotalBytes Then
                    Await ShowMessageAsync(LocalizationService.T("Nicht genug Platz"),
                                           String.Format(LocalizationService.T("Am Ziel {0} sind {1} frei, gebraucht werden {2}. Es wurde nichts kopiert."),
                                                         root, CardImportService.FormatBytes(free), CardImportService.FormatBytes(request.TotalBytes)))
                    Return Nothing
                End If
            Next

            Dim token = BeginBusyOverlayCancellation()
            Dim total = request.Candidates.Count
            Try
                BeginBusyOverlay(String.Format(LocalizationService.T("Wird importiert: {0} von {1}"), 0, total))
                Return Await Task.Run(
                    Function() CardImportService.Run(request.Candidates, request.TargetFolder, request.FolderPattern,
                                                     request.BackupFolder, token,
                                                     Sub(done, count)
                                                         Dispatcher.UIThread.Post(Sub()
                                                                                      If Not token.IsCancellationRequested Then
                                                                                          UpdateBusyOverlay(String.Format(LocalizationService.T("Wird importiert: {0} von {1}"), done, count))
                                                                                      End If
                                                                                  End Sub)
                                                     End Sub))
            Finally
                EndBusyOverlay()
                EndBusyOverlayCancellation()
            End Try
        End Function

    End Class

End Namespace
