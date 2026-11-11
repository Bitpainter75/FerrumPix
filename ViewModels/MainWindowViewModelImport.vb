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
        ''' <summary>Die Quelle; kein Ziel darf darin liegen.</summary>
        Public Property SourceFolder As String = ""
        ''' <summary>Der Auftrag fuer den Platzhalter {job}.</summary>
        Public Property JobName As String = ""
        ''' <summary>Die Vorlage fuer den Dateinamen; leer behaelt den Namen der Quelle.</summary>
        Public Property NameTemplate As String = ""
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
        Private _dialogImportSourcesGeneration As Integer
        Private _dialogImportTargetFolder As String = ""
        Private _dialogImportIncludeRawPartners As Boolean = True
        Private _dialogImportIncludeSubfolders As Boolean = True
        Private _dialogImportUseBackup As Boolean
        Private _dialogImportBackupFolder As String = ""
        Private _dialogImportJobName As String = ""
        Private _dialogImportNameTemplate As String = ""
        ' Die zweite Stufe des Lesens: wie viele Dateien ihre EXIF-Daten schon haben.
        Private _dialogImportResolving As Boolean
        Private _dialogImportResolved As Integer
        Private _dialogImportToResolve As Integer
        ' Die Pruefung am Ziel im Hintergrund (ScheduleDialogImportCheck) und ihr letztes Ergebnis.
        Private _dialogImportCheckGeneration As Integer
        Private _dialogImportCheckPending As Boolean
        Private _dialogImportCheckPresent As Integer
        Private _dialogImportCheckFree As Long = -1
        Private _dialogImportCheckInSource As Boolean
        ' Fuer welches Ziel (mit Sicherung, Auftrag und Dateiname) das letzte Ergebnis galt.
        Private _dialogImportCheckKey As String = ""

        Public ReadOnly Property DialogImportSources As New ObservableCollection(Of ImportSource)()
        Public ReadOnly Property DialogImportDays As New ObservableCollection(Of ImportDay)()

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
                RaiseDialogImportSummary()
            End Set
        End Property

        ''' <summary>Die Ziele zur Auswahl: der aktuelle Ordner der Galerie und die in den
        ''' Einstellungen definierten. Eine Wahl schreibt ihre Vorlage ins Zielfeld; dort laesst sie
        ''' sich weiter aendern oder ueber "Ordner wählen" ersetzen.</summary>
        Public ReadOnly Property DialogImportTargetChoices As New ObservableCollection(Of ImportTargetChoice)()

        Private _dialogImportSelectedTargetChoice As ImportTargetChoice
        Public Property DialogImportSelectedTargetChoice As ImportTargetChoice
            Get
                Return _dialogImportSelectedTargetChoice
            End Get
            Set(value As ImportTargetChoice)
                If ReferenceEquals(_dialogImportSelectedTargetChoice, value) Then Return
                Me.RaiseAndSetIfChanged(_dialogImportSelectedTargetChoice, value)
                If value IsNot Nothing Then DialogImportTargetFolder = value.Pattern
            End Set
        End Property

        ''' <summary>Die Quelle mit ihren Unterordnern lesen. Eine Aenderung liest neu ein.</summary>
        Public Property DialogImportIncludeSubfolders As Boolean
            Get
                Return _dialogImportIncludeSubfolders
            End Get
            Set(value As Boolean)
                If _dialogImportIncludeSubfolders = value Then Return
                Me.RaiseAndSetIfChanged(_dialogImportIncludeSubfolders, value)
                StartDialogImportScan()
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

        ''' <summary>Die Wahl fuer den zweiten Ordner: dieselben Ziele wie fuer das erste
        ''' (DialogImportTargetChoices), eine Wahl schreibt ihre Vorlage ins Feld der Sicherung.</summary>
        Private _dialogImportSelectedBackupChoice As ImportTargetChoice
        Public Property DialogImportSelectedBackupChoice As ImportTargetChoice
            Get
                Return _dialogImportSelectedBackupChoice
            End Get
            Set(value As ImportTargetChoice)
                If ReferenceEquals(_dialogImportSelectedBackupChoice, value) Then Return
                Me.RaiseAndSetIfChanged(_dialogImportSelectedBackupChoice, value)
                If value IsNot Nothing Then DialogImportBackupFolder = value.Pattern
            End Set
        End Property

        ''' <summary>Der Auftrag (Fotoreise, Ort, Kunde) fuer den Platzhalter {job} in Ziel und
        ''' Dateiname. Der zuletzt benutzte steht beim naechsten Import wieder da.</summary>
        Public Property DialogImportJobName As String
            Get
                Return _dialogImportJobName
            End Get
            Set(value As String)
                value = If(value, "")
                If _dialogImportJobName = value Then Return
                Me.RaiseAndSetIfChanged(_dialogImportJobName, value)
                RaiseDialogImportSummary()
            End Set
        End Property

        ''' <summary>Die Vorlage fuer den Dateinamen im Ziel; leer behaelt den Namen der Quelle.</summary>
        Public Property DialogImportNameTemplate As String
            Get
                Return _dialogImportNameTemplate
            End Get
            Set(value As String)
                value = If(value, "")
                If _dialogImportNameTemplate = value Then Return
                Me.RaiseAndSetIfChanged(_dialogImportNameTemplate, value)
                RaiseDialogImportSummary()
            End Set
        End Property

        ''' <summary>Die Muster fuer den Dateinamen zur Auswahl: zuerst der Name der Quelle (leeres
        ''' Muster), dann die in den Einstellungen definierten. Eine Wahl schreibt ihr Muster ins Feld
        ''' darunter; dort laesst es sich weiter aendern, wie beim Ziel.</summary>
        Public ReadOnly Property DialogImportNameChoices As New ObservableCollection(Of ImportTargetChoice)()

        Private _dialogImportSelectedNameChoice As ImportTargetChoice
        Public Property DialogImportSelectedNameChoice As ImportTargetChoice
            Get
                Return _dialogImportSelectedNameChoice
            End Get
            Set(value As ImportTargetChoice)
                If ReferenceEquals(_dialogImportSelectedNameChoice, value) Then Return
                Me.RaiseAndSetIfChanged(_dialogImportSelectedNameChoice, value)
                If value IsNot Nothing Then DialogImportNameTemplate = value.Pattern
            End Set
        End Property

        ''' <summary>Wie die erste gewaehlte Datei im Ziel hiesse; leer ohne Vorlage.</summary>
        Public ReadOnly Property DialogImportNameExample As String
            Get
                If String.IsNullOrWhiteSpace(_dialogImportNameTemplate) Then Return ""
                Dim first = DialogImportChosen().FirstOrDefault()
                If first Is Nothing Then Return ""
                Return String.Format(LocalizationService.T("Zum Beispiel: {0}"),
                                     CardImportService.TargetFileNameFor(_dialogImportNameTemplate, first, _dialogImportJobName, 1))
            End Get
        End Property

        Public ReadOnly Property HasDialogImportNameExample As Boolean
            Get
                Return DialogImportNameExample.Length > 0
            End Get
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
        ''' gefunden", sonst Zahl und Groesse sofort, schon vorhandene und freier Platz aus der
        ''' Pruefung im Hintergrund (<see cref="ScheduleDialogImportCheck"/>); solange sie laeuft,
        ''' steht "Ziel wird geprüft…" dabei.</summary>
        Public ReadOnly Property DialogImportSummary As String
            Get
                If String.IsNullOrEmpty(_dialogImportSourceFolder) Then Return ""
                If _dialogImportScanning Then Return LocalizationService.T("Quelle wird gelesen…")
                If _dialogImportCandidates.Count = 0 Then Return LocalizationService.T("Keine Bilder oder Videos gefunden")
                If DialogImportTargetInSource() Then Return LocalizationService.T("Das Ziel liegt in der Quelle. Auf der Quelle wird nichts verändert, bitte ein anderes Ziel wählen.")
                ' Jede Sperre des Knopfes sagt ihren Grund (IsDialogImportPrimaryEnabled). Ein Ziel
                ' auf einer zweiten Platte war grau ohne ein Wort dazu (GitHub #117).
                If String.IsNullOrWhiteSpace(_dialogImportTargetFolder) Then
                    Return LocalizationService.T("Es fehlt noch das Ziel, in das importiert wird.")
                End If
                If Not String.IsNullOrWhiteSpace(_dialogImportTargetFolder) AndAlso
                   Not FileOperationPolicy.IsInAllowedArea(CardImportService.FixedRootOf(_dialogImportTargetFolder)) Then
                    Return LocalizationService.T("Das Ziel liegt außerhalb der Ordner, in die FerrumPix schreiben darf. Erlauben lässt sich das unter Einstellungen, Galerie, Dateioperationen außerhalb des persönlichen Ordners erlauben.")
                End If
                If _dialogImportUseBackup AndAlso String.IsNullOrWhiteSpace(_dialogImportBackupFolder) Then
                    Return LocalizationService.T("Für die Sicherung fehlt noch der zweite Ordner.")
                End If
                If _dialogImportUseBackup AndAlso
                   Not FileOperationPolicy.IsInAllowedArea(CardImportService.FixedRootOf(_dialogImportBackupFolder)) Then
                    Return LocalizationService.T("Der zweite Ordner liegt außerhalb der Ordner, in die FerrumPix schreiben darf. Erlauben lässt sich das unter Einstellungen, Galerie, Dateioperationen außerhalb des persönlichen Ordners erlauben.")
                End If
                Dim chosen = DialogImportChosen()
                Dim bytes = chosen.Sum(Function(c) c.Size)
                Dim text = String.Format(LocalizationService.T("{0} Dateien gewählt, {1}"), chosen.Count, CardImportService.FormatBytes(bytes))
                If Not String.IsNullOrEmpty(_dialogImportTargetFolder) Then
                    ' Waehrend einer neuen Pruefung bleibt das letzte Ergebnis stehen, wenn es
                    ' fuer dasselbe Ziel galt (die EXIF-Daten kommen in Schueben und stossen jedes
                    ' Mal eine Pruefung an); fuer ein anderes Ziel stuende dort Falsches.
                    If _dialogImportCheckPending AndAlso _dialogImportCheckKey <> DialogImportCheckKey() Then
                        text &= ". " & LocalizationService.T("Ziel wird geprüft…")
                    Else
                        If _dialogImportCheckPresent > 0 Then
                            text &= ". " & String.Format(LocalizationService.T("{0} davon liegen schon im Ziel und werden übersprungen"), _dialogImportCheckPresent)
                        End If
                        If _dialogImportCheckFree >= 0 AndAlso _dialogImportCheckFree < bytes Then
                            text &= ". " & String.Format(LocalizationService.T("Am Ziel sind nur {0} frei"), CardImportService.FormatBytes(_dialogImportCheckFree))
                        End If
                    End If
                End If
                If _dialogImportResolving Then
                    text &= ". " & String.Format(LocalizationService.T("Aufnahmedaten werden gelesen: {0} von {1}"),
                                                 _dialogImportResolved, _dialogImportToResolve)
                    If DialogImportSelectionWaitsForCaptureData() Then
                        text &= ". " & LocalizationService.T("Mit abgewählten Tagen geht es los, sobald alle Aufnahmedaten gelesen sind")
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

        ''' <summary>Liegt das Ziel oder die Sicherung in der Quelle? Sofort geprueft wird der feste
        ''' Anfang; ob bei einer Vorlage ein aufgeloester Ordner der gewaehlten Aufnahmen die Quelle
        ''' trifft (auch {pictures}/{datetaken:yyyy} kann das), sagt die Pruefung im Hintergrund. Bis
        ''' sie da ist, gilt ihr letztes Ergebnis; der Lauf prueft vor dem ersten Schreiben ohnehin
        ''' jeden Zielordner selbst (RunImportAsync, CardImportService.Run).</summary>
        Private Function DialogImportTargetInSource() As Boolean
            Dim source = _dialogImportSourceFolder
            If String.IsNullOrWhiteSpace(source) Then Return False
            For Each target In {_dialogImportTargetFolder, If(_dialogImportUseBackup, _dialogImportBackupFolder, "")}
                If String.IsNullOrWhiteSpace(target) Then Continue For
                If CardImportService.IsWithin(CardImportService.FixedRootOf(target), source) Then Return True
            Next
            Return _dialogImportCheckInSource
        End Function

        Private Function IsDialogImportPrimaryEnabled() As Boolean
            If _dialogImportScanning OrElse String.IsNullOrWhiteSpace(_dialogImportTargetFolder) Then Return False
            If DialogImportTargetInSource() Then Return False
            If _dialogImportUseBackup AndAlso String.IsNullOrWhiteSpace(_dialogImportBackupFolder) Then Return False
            If Not FileOperationPolicy.IsInAllowedArea(CardImportService.FixedRootOf(_dialogImportTargetFolder)) Then Return False
            ' Die Sicherung ist ein Ziel wie das erste und unterliegt derselben Regel.
            If _dialogImportUseBackup AndAlso
               Not FileOperationPolicy.IsInAllowedArea(CardImportService.FixedRootOf(_dialogImportBackupFolder)) Then Return False
            If DialogImportSelectionWaitsForCaptureData() Then Return False
            Return DialogImportChosen().Count > 0
        End Function

        ''' <summary>Haengt die Auswahl noch an vorlaeufigen Tagen? Solange die EXIF-Daten nachkommen,
        ''' steht eine Datei unter ihrer Aenderungszeit; erst danach steht fest, an welchem Tag sie
        ''' wirklich liegt. Ist ein Tag abgewaehlt, koennte ein Bestaetigen jetzt eine Datei mitnehmen,
        ''' deren Aufnahmetag abgewaehlt ist, oder eine auslassen. Sind alle Tage angehakt, haengt die
        ''' Auswahl an keinem Datum, und es darf gleich losgehen; den Rest liest der Lauf nach.</summary>
        Private Function DialogImportSelectionWaitsForCaptureData() As Boolean
            Return _dialogImportResolving AndAlso DialogImportDays.Any(Function(d) Not d.IsSelected)
        End Function

        ''' <summary>Nach jeder Aenderung, die das Ergebnis betrifft: Anzeige sofort, die Pruefung
        ''' am Ziel kurz danach im Hintergrund.</summary>
        Private Sub RaiseDialogImportSummary()
            RaiseDialogImportSummaryProperties()
            ScheduleDialogImportCheck()
        End Sub

        Private Sub RaiseDialogImportSummaryProperties()
            Me.RaisePropertyChanged(NameOf(DialogImportSummary))
            Me.RaisePropertyChanged(NameOf(DialogImportNameExample))
            Me.RaisePropertyChanged(NameOf(HasDialogImportNameExample))
            Me.RaisePropertyChanged(NameOf(HasDialogImportSummary))
            Me.RaisePropertyChanged(NameOf(IsDialogPrimaryEnabled))
        End Sub

        ''' <summary>Die Pruefung am Ziel: wie viele Dateien dort schon liegen, wie viel Platz frei ist
        ''' und ob eine Vorlage fuer eine der Aufnahmen in der Quelle landet. Sie schaut je Datei im
        ''' Ziel nach und lief frueher bei jedem Tastendruck auf dem UI-Faden; bei tausenden Dateien
        ''' und einem Ziel im Netz stockte damit das Tippen. Jetzt startet sie 300 ms nach der
        ''' letzten Aenderung im Hintergrund, auf einer Kopie der Kandidaten (das Nachlesen der
        ''' EXIF-Daten schreibt die Originale auf dem UI-Faden), und nur das Ergebnis der juengsten
        ''' Pruefung zaehlt.</summary>
        Private Function DialogImportCheckKey() As String
            Return String.Join("|", _dialogImportTargetFolder, If(_dialogImportUseBackup, _dialogImportBackupFolder, ""),
                               _dialogImportJobName, _dialogImportNameTemplate)
        End Function

        Private Sub ScheduleDialogImportCheck()
            _dialogImportCheckGeneration += 1
            Dim generation = _dialogImportCheckGeneration
            If _dialogImportScanning OrElse _dialogImportCandidates.Count = 0 OrElse String.IsNullOrEmpty(_dialogImportSourceFolder) Then
                _dialogImportCheckPending = False
                _dialogImportCheckInSource = False
                _dialogImportCheckKey = ""
                Return
            End If
            _dialogImportCheckPending = True
            Dim chosen = DialogImportChosen().Select(Function(c) New ImportCandidate With {
                .SourcePath = c.SourcePath, .Size = c.Size, .CapturedAt = c.CapturedAt, .IsRaw = c.IsRaw,
                .IsRawPartner = c.IsRawPartner, .Camera = c.Camera, .CameraMake = c.CameraMake, .CameraModel = c.CameraModel, .ModifiedAt = c.ModifiedAt,
                .MtpStorage = c.MtpStorage, .MtpPath = c.MtpPath, .CaptureResolved = c.CaptureResolved}).ToList()
            Dim target = _dialogImportTargetFolder
            Dim targets = {_dialogImportTargetFolder, If(_dialogImportUseBackup, _dialogImportBackupFolder, "")}.
                Where(Function(t) CardImportService.HasPlaceholders(t)).ToList()
            Dim source = _dialogImportSourceFolder
            Dim job = _dialogImportJobName
            Dim nameTemplate = _dialogImportNameTemplate
            Dim key = DialogImportCheckKey()
            Dim ignored = Task.Run(
                Async Function()
                    Await Task.Delay(300)
                    If generation <> _dialogImportCheckGeneration Then Return
                    Dim present = 0
                    Dim free = -1L
                    Dim inSource = False
                    Try
                        inSource = targets.Any(Function(t) chosen.Any(Function(c) CardImportService.IsWithin(CardImportService.TargetFolderFor(t, "", c, job), source)))
                        If Not String.IsNullOrEmpty(target) Then
                            present = CardImportService.CountProbablyPresent(chosen, target, "", job, nameTemplate)
                            free = CardImportService.FreeSpace(target)
                        End If
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Import.CheckDialog", ex)
                    End Try
                    Dispatcher.UIThread.Post(
                        Sub()
                            If generation <> _dialogImportCheckGeneration Then Return
                            _dialogImportCheckPresent = present
                            _dialogImportCheckFree = free
                            _dialogImportCheckInSource = inSource
                            _dialogImportCheckPending = False
                            _dialogImportCheckKey = key
                            RaiseDialogImportSummaryProperties()
                        End Sub)
                End Function)
        End Sub

        ''' <summary>Die Zusammenfassung erscheint erst mit einer Quelle; davor gaebe es nichts
        ''' zusammenzufassen.</summary>
        Public ReadOnly Property HasDialogImportSummary As Boolean
            Get
                Return Not String.IsNullOrEmpty(_dialogImportSourceFolder)
            End Get
        End Property

        ''' <summary>Liest die Quelle im Hintergrund, in zwei Stufen. Zuerst nur die Ordnerliste:
        ''' Tage, Optionen und Zusammenfassung stehen damit auch bei einer grossen Karte gleich da,
        ''' nach der Aenderungszeit, die eine Kamera beim Fotografieren setzt. Danach die EXIF-Daten
        ''' Datei fuer Datei; sie verschieben eine Aufnahme hoechstens auf ihren richtigen Tag. Die
        ''' Ergebnisse sammelt der Hintergrund und setzt sie in Schueben auf dem UI-Faden ein, so
        ''' fasst nur ein Faden die Kandidaten an. Bestaetigt jemand vorher, liest der Lauf den Rest
        ''' nach. Ein neuer Ordner bricht beides ab.</summary>
        Private Sub StartDialogImportScan()
            _dialogImportScanCancel?.Cancel()
            _dialogImportCandidates = New List(Of ImportCandidate)()
            _dialogImportResolving = False
            DialogImportDays.Clear()
            Dim folder = _dialogImportSourceFolder
            ' Ein Geraet hinter kmtpd ("mtp:/...") ist kein Ordner im Dateisystem; ob es die Adresse
            ' gibt, sagt erst das Lesen.
            If String.IsNullOrEmpty(folder) OrElse Not (Directory.Exists(folder) OrElse MtpSession.IsAddress(folder)) Then
                _dialogImportScanning = False
                RaiseDialogImportScan()
                Return
            End If
            Dim subfolders = _dialogImportIncludeSubfolders
            Dim cancel As New CancellationTokenSource()
            _dialogImportScanCancel = cancel
            _dialogImportScanning = True
            RaiseDialogImportScan()
            Dim ignored = Task.Run(
                Sub()
                    Dim found As List(Of ImportCandidate) = Nothing
                    Try
                        found = CardImportService.Scan(folder, cancel.Token, subfolders, readCaptureData:=False)
                    Catch ex As OperationCanceledException
                        Return
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Import.ScanDialog", ex)
                        found = New List(Of ImportCandidate)()
                    End Try
                    Dim open = found.Where(Function(c) Not c.CaptureResolved).ToList()
                    Dispatcher.UIThread.Post(
                        Sub()
                            If cancel.IsCancellationRequested OrElse Not ReferenceEquals(_dialogImportScanCancel, cancel) Then Return
                            _dialogImportCandidates = found
                            _dialogImportResolving = open.Count > 0
                            _dialogImportResolved = 0
                            _dialogImportToResolve = open.Count
                            SyncDialogImportDays()
                            _dialogImportScanning = False
                            RaiseDialogImportScan()
                        End Sub)
                    If open.Count = 0 Then Return

                    Dim batch As New List(Of (Candidate As ImportCandidate, Data As ImportCaptureData))()
                    Dim clock = Diagnostics.Stopwatch.StartNew()
                    Dim flush = Sub(last As Boolean)
                                    Dim ready = batch.ToList()
                                    batch.Clear()
                                    clock.Restart()
                                    Dispatcher.UIThread.Post(Sub() ApplyDialogImportCaptureData(cancel, ready, last))
                                End Sub
                    Try
                        For Each candidate In open
                            cancel.Token.ThrowIfCancellationRequested()
                            batch.Add((candidate, CardImportService.ReadCaptureData(candidate)))
                            If clock.ElapsedMilliseconds >= 400 Then flush(False)
                        Next
                        flush(True)
                    Catch ex As OperationCanceledException
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Import.ResolveDialog", ex)
                    End Try
                End Sub)
        End Sub

        ''' <summary>Setzt einen Schub nachgelesener EXIF-Daten ein (UI-Faden). Der letzte ordnet die
        ''' Kandidaten neu, denn ihre Reihenfolge bestimmt die Nummern fuer ###.</summary>
        Private Sub ApplyDialogImportCaptureData(cancel As CancellationTokenSource,
                                                 ready As List(Of (Candidate As ImportCandidate, Data As ImportCaptureData)),
                                                 last As Boolean)
            If cancel.IsCancellationRequested OrElse Not ReferenceEquals(_dialogImportScanCancel, cancel) Then Return
            For Each item In ready
                item.Candidate.ApplyCaptureData(item.Data)
            Next
            _dialogImportResolved += ready.Count
            If last Then
                _dialogImportCandidates = _dialogImportCandidates.
                    OrderBy(Function(c) c.CapturedAt).ThenBy(Function(c) c.SourcePath, StringComparer.Ordinal).ToList()
                _dialogImportResolving = False
            End If
            SyncDialogImportDays()
            RaiseDialogImportScan()
        End Sub

        ''' <summary>Gleicht die Aufnahmetage mit den Kandidaten ab, ohne die Liste neu zu bauen:
        ''' ein Tag behaelt seinen Haken, neue Tage kommen angehakt an ihrer Stelle dazu, leere fallen
        ''' weg. So springt nichts unter dem Zeiger, waehrend die EXIF-Daten nachkommen.</summary>
        Private Sub SyncDialogImportDays()
            Dim groups = _dialogImportCandidates.GroupBy(Function(c) c.CapturedAt.Date).
                ToDictionary(Function(g) g.Key, Function(g) (Count:=g.Count(), Bytes:=g.Sum(Function(c) c.Size)))
            For index = DialogImportDays.Count - 1 To 0 Step -1
                If Not groups.ContainsKey(DialogImportDays(index).Day) Then DialogImportDays.RemoveAt(index)
            Next
            For Each pair In groups.OrderBy(Function(p) p.Key)
                Dim existing = DialogImportDays.FirstOrDefault(Function(d) d.Day = pair.Key)
                If existing IsNot Nothing Then
                    existing.UpdateTotals(pair.Value.Count, pair.Value.Bytes)
                    Continue For
                End If
                Dim position = 0
                While position < DialogImportDays.Count AndAlso DialogImportDays(position).Day < pair.Key
                    position += 1
                End While
                DialogImportDays.Insert(position, New ImportDay With {
                    .Day = pair.Key, .Count = pair.Value.Count, .Bytes = pair.Value.Bytes,
                    .OnSelectionChanged = AddressOf RaiseDialogImportSummary})
            Next
        End Sub

        ''' <summary>Die Knoepfe ueber den Aufnahmetagen: alle, keinen oder nur den neuesten Tag
        ''' anhaken. Auf einer Karte, die selten geleert wird, stehen Dutzende Tage, und meist will
        ''' man nur die letzte Aufnahme. Die Liste ist aufsteigend sortiert, der neueste Tag steht
        ''' also unten.</summary>
        Public Sub SelectDialogImportDays(mode As String)
            If DialogImportDays.Count = 0 Then Return
            Dim newest = DialogImportDays(DialogImportDays.Count - 1)
            For Each entry In DialogImportDays
                Dim selected = mode = "All" OrElse (mode = "Newest" AndAlso entry Is newest)
                entry.SetSelectedQuietly(selected)
            Next
            RaiseDialogImportSummary()
        End Sub

        Private Sub RaiseDialogImportScan()
            Me.RaisePropertyChanged(NameOf(HasDialogImportDays))
            Me.RaisePropertyChanged(NameOf(HasDialogImportRawPartners))
            RaiseDialogImportSummary()
        End Sub

        ''' <summary>Sucht die Quellen im Hintergrund, denn ein traeges Geraet ueber MTP antwortet erst
        ''' nach Sekunden. Kommt waehrenddessen ein neuer Aufruf, gilt nur dessen Ergebnis. Hat der
        ''' Benutzer inzwischen selbst einen Ordner gewaehlt, bleibt der stehen.</summary>
        Public Sub RefreshDialogImportSources()
            _dialogImportSourcesGeneration += 1
            Dim generation = _dialogImportSourcesGeneration
            Dim selectedPath = _dialogImportSelectedSource?.Path
            Dim folderAtStart = _dialogImportSourceFolder
            Dim ignored = Task.Run(
                Sub()
                    Dim found As List(Of ImportSource)
                    Try
                        found = CardImportService.DetectSources()
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Import.RefreshSources", ex)
                        found = New List(Of ImportSource)()
                    End Try
                    Dispatcher.UIThread.Post(
                        Sub()
                            If generation <> _dialogImportSourcesGeneration Then Return
                            DialogImportSources.Clear()
                            For Each source In found
                                DialogImportSources.Add(source)
                            Next
                            Me.RaisePropertyChanged(NameOf(HasDialogImportSources))
                            Dim again = DialogImportSources.FirstOrDefault(Function(s) s.Path = selectedPath)
                            _dialogImportSelectedSource = Nothing
                            If _dialogImportSourceFolder <> folderAtStart Then
                                Me.RaisePropertyChanged(NameOf(DialogImportSelectedSource))
                                Return
                            End If
                            DialogImportSelectedSource = If(again, DialogImportSources.FirstOrDefault())
                        End Sub)
                End Sub)
        End Sub

        ''' <summary>Oeffnet den Dialog. Als Ziel steht zuerst <paramref name="suggestedTarget"/>, der
        ''' aktuelle Ordner der Galerie; ohne ihn der Bilder-Ordner. Zur Auswahl stehen dazu die
        ''' Ziele aus den Einstellungen. Nothing heisst abgebrochen.</summary>
        Public Async Function ShowImportDialogAsync(suggestedTarget As String) As Task(Of ImportDialogResult)
            Dim settings = AppSettingsService.Load()
            Dim target = suggestedTarget
            If String.IsNullOrWhiteSpace(target) OrElse Not Directory.Exists(target) Then target = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            _dialogImportTargetFolder = If(target, "")
            DialogImportTargetChoices.Clear()
            Dim current = New ImportTargetChoice With {
                .Pattern = _dialogImportTargetFolder,
                .Label = String.Format(LocalizationService.T("Aktueller Ordner: {0}"), _dialogImportTargetFolder)}
            DialogImportTargetChoices.Add(current)
            For Each defined In If(settings.ImportTargets, New List(Of ImportTargetSettings)())
                If defined Is Nothing OrElse String.IsNullOrWhiteSpace(defined.Template) Then Continue For
                Dim name = If(String.IsNullOrWhiteSpace(defined.Name), defined.Template, defined.Name & ": " & defined.Template)
                DialogImportTargetChoices.Add(New ImportTargetChoice With {.Pattern = defined.Template.Trim(), .Label = name})
            Next
            _dialogImportSelectedTargetChoice = current
            ' War das zuletzt benutzte Ziel eine Vorlage mit Platzhaltern, steht sie wieder da: ein
            ' Muster tippt niemand bei jeder Karte neu. Ein schlichter Ordner dagegen weicht wie
            ' bisher dem aktuellen Ordner der Galerie. Steht die Vorlage nicht unter den Zielen der
            ' Einstellungen, bekommt sie einen eigenen Eintrag und bleibt so nach einem Wechsel
            ' wieder waehlbar.
            ' Ein schlichter Ordner kommt ebenso wieder, wenn er beim letzten Mal das Ziel war und
            ' nicht der aktuelle Ordner (ImportTargetChoice "Last"), wie bei Speichern unter: die
            ' letzte Wahl der Liste gilt wieder.
            Dim lastTarget = If(settings.ImportTargetFolder, "").Trim()
            Dim lastPlainFolder = String.Equals(settings.ImportTargetChoice, "Last", StringComparison.Ordinal) AndAlso
                                  lastTarget.Length > 0 AndAlso Directory.Exists(lastTarget) AndAlso
                                  Not PathIdentity.AreSame(lastTarget, _dialogImportTargetFolder)
            If CardImportService.HasPlaceholders(lastTarget) OrElse lastPlainFolder Then
                Dim known = DialogImportTargetChoices.FirstOrDefault(Function(c) c.Pattern = lastTarget)
                If known Is Nothing Then
                    known = New ImportTargetChoice With {.Pattern = lastTarget,
                                                         .Label = String.Format(LocalizationService.T("Zuletzt benutzt: {0}"), lastTarget)}
                    DialogImportTargetChoices.Insert(1, known)
                End If
                _dialogImportSelectedTargetChoice = known
                _dialogImportTargetFolder = lastTarget
            End If
            Me.RaisePropertyChanged(NameOf(DialogImportSelectedTargetChoice))
            _dialogImportIncludeRawPartners = settings.ImportIncludeRawPartners
            _dialogImportIncludeSubfolders = settings.ImportIncludeSubfolders
            Me.RaisePropertyChanged(NameOf(DialogImportIncludeSubfolders))
            _dialogImportUseBackup = settings.ImportUseBackup
            _dialogImportBackupFolder = If(settings.ImportBackupFolder, "")
            _dialogImportJobName = If(settings.ImportJobName, "")
            _dialogImportNameTemplate = If(settings.ImportNameTemplate, "")
            DialogImportNameChoices.Clear()
            DialogImportNameChoices.Add(New ImportTargetChoice With {.Pattern = "", .Label = LocalizationService.T("Name der Quelle")})
            For Each defined In If(settings.ImportNameTemplates, New List(Of ImportTargetSettings)())
                If defined Is Nothing OrElse String.IsNullOrWhiteSpace(defined.Template) Then Continue For
                Dim name = If(String.IsNullOrWhiteSpace(defined.Name), defined.Template, defined.Name & ": " & defined.Template)
                DialogImportNameChoices.Add(New ImportTargetChoice With {.Pattern = defined.Template.Trim(), .Label = name})
            Next
            ' Das zuletzt benutzte Muster ist gewaehlt. Ein frei geschriebenes, das nicht in der Liste
            ' steht, bekommt wie bei Speichern unter einen Eintrag "Zuletzt benutzt" und bleibt so
            ' nach einem Wechsel wieder waehlbar.
            Dim lastName = _dialogImportNameTemplate.Trim()
            _dialogImportSelectedNameChoice = DialogImportNameChoices.FirstOrDefault(Function(c) c.Pattern = lastName)
            If _dialogImportSelectedNameChoice Is Nothing AndAlso lastName.Length > 0 Then
                _dialogImportSelectedNameChoice = New ImportTargetChoice With {.Pattern = lastName,
                    .Label = String.Format(LocalizationService.T("Zuletzt benutzt: {0}"), lastName)}
                DialogImportNameChoices.Insert(1, _dialogImportSelectedNameChoice)
            End If
            Me.RaisePropertyChanged(NameOf(DialogImportSelectedNameChoice))
            Me.RaisePropertyChanged(NameOf(DialogImportJobName))
            Me.RaisePropertyChanged(NameOf(DialogImportNameTemplate))
            _dialogImportSourceFolder = ""
            _dialogImportSelectedSource = Nothing
            Me.RaisePropertyChanged(NameOf(DialogImportTargetFolder))
            Me.RaisePropertyChanged(NameOf(DialogImportIncludeRawPartners))
            Me.RaisePropertyChanged(NameOf(DialogImportUseBackup))
            Me.RaisePropertyChanged(NameOf(DialogImportBackupFolder))
            ' Steht die gemerkte Sicherung in der Liste, ist sie gewaehlt; ein eigener Ordner steht nur im Feld.
            _dialogImportSelectedBackupChoice = DialogImportTargetChoices.FirstOrDefault(Function(c) c.Pattern = _dialogImportBackupFolder.Trim())
            Me.RaisePropertyChanged(NameOf(DialogImportSelectedBackupChoice))
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
            ' Keine Unterordner nach eigenem Muster: die Ordnerstruktur kommt allein aus einer Vorlage
            ' der Einstellungen, ein schlichter Ordner wird direkt beschrieben.
            Dim pattern = ""
            Dim backup = If(_dialogImportUseBackup, _dialogImportBackupFolder, "")
            ' War das Ziel der aktuelle Ordner, folgt das naechste Mal wieder der Galerie; sonst
            ' kommt dieses Ziel wieder (siehe oben).
            Dim targetWasCurrent = PathIdentity.AreSame(_dialogImportTargetFolder, current.Pattern)
            AppSettingsService.Update(Sub(s)
                                          s.ImportTargetFolder = _dialogImportTargetFolder
                                          s.ImportTargetChoice = If(targetWasCurrent, "Current", "Last")
                                          s.ImportIncludeRawPartners = _dialogImportIncludeRawPartners
                                          s.ImportIncludeSubfolders = _dialogImportIncludeSubfolders
                                          s.ImportUseBackup = _dialogImportUseBackup
                                          s.ImportBackupFolder = _dialogImportBackupFolder
                                          s.ImportJobName = _dialogImportJobName.Trim()
                                          s.ImportNameTemplate = _dialogImportNameTemplate.Trim()
                                      End Sub)
            Return New ImportDialogResult With {
                .Candidates = chosen, .TargetFolder = _dialogImportTargetFolder, .FolderPattern = pattern,
                .BackupFolder = backup, .TotalBytes = chosen.Sum(Function(c) c.Size),
                .SourceFolder = _dialogImportSourceFolder,
                .JobName = _dialogImportJobName.Trim(), .NameTemplate = _dialogImportNameTemplate.Trim()}
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

            ' Kein Zielordner in der Quelle, fuer keine einzige Aufnahme: sonst entstuenden dort
            ' Zwischendateien und Kopien, und die Zusage "auf der Quelle wird nichts veraendert" fiele.
            For Each root In {request.TargetFolder, request.BackupFolder}.Where(Function(r) Not String.IsNullOrWhiteSpace(r))
                If request.Candidates.Any(Function(c) CardImportService.IsWithin(CardImportService.TargetFolderFor(root, request.FolderPattern, c, request.JobName), request.SourceFolder)) Then
                    Await ShowMessageAsync(LocalizationService.T("Ziel nicht erlaubt"),
                                           LocalizationService.T("Das Ziel liegt in der Quelle. Auf der Quelle wird nichts verändert, bitte ein anderes Ziel wählen."))
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
                                                     End Sub,
                                                     request.SourceFolder, request.JobName, request.NameTemplate))
            Finally
                EndBusyOverlay()
                EndBusyOverlayCancellation()
            End Try
        End Function

    End Class

End Namespace
