Imports System
Imports System.Globalization
Imports System.IO
Imports System.Threading.Tasks
Imports Avalonia.Controls.ApplicationLifetimes
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Die Dialoge "Sichern" und "Wiederherstellen" (CatalogBackupService). Beide zeigen die
    ''' Teile zum An- und Abwaehlen und eine Datei: beim Sichern das Ziel, beim Wiederherstellen die
    ''' Sicherung, deren Inhaltsverzeichnis darunter steht. Beim Wiederherstellen sind nur die Teile
    ''' waehlbar, die in der Sicherung stecken.</summary>
    Partial Public Class MainWindowViewModel

        Private _dialogBackupPath As String = ""
        Private _dialogBackupCatalog As Boolean = True
        Private _dialogBackupServerIndexes As Boolean = True
        Private _dialogBackupSettings As Boolean = True
        Private _dialogBackupFavorites As Boolean = True
        Private _dialogBackupSearches As Boolean = True
        Private _dialogBackupSecrets As Boolean
        Private _dialogRestoreManifest As BackupManifest
        Private _dialogRestoreProblem As String = ""

        Public ReadOnly Property DialogShowsBackup As Boolean
            Get
                Return _dialogKind = AppDialogKind.Backup
            End Get
        End Property

        Public ReadOnly Property DialogShowsRestore As Boolean
            Get
                Return _dialogKind = AppDialogKind.Restore
            End Get
        End Property

        Public ReadOnly Property DialogShowsBackupOrRestore As Boolean
            Get
                Return DialogShowsBackup OrElse DialogShowsRestore
            End Get
        End Property

        ''' <summary>Beim Sichern das Ziel, beim Wiederherstellen die gewaehlte Sicherung.</summary>
        Public Property DialogBackupPath As String
            Get
                Return _dialogBackupPath
            End Get
            Set(value As String)
                value = If(value, "").Trim()
                If _dialogBackupPath = value Then Return
                Me.RaiseAndSetIfChanged(_dialogBackupPath, value)
                If DialogShowsRestore Then ReadRestoreManifest()
                RaiseBackupDialog()
            End Set
        End Property

        Public Property DialogBackupCatalog As Boolean
            Get
                Return _dialogBackupCatalog
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogBackupCatalog, value)
                RaiseBackupDialog()
            End Set
        End Property

        Public Property DialogBackupServerIndexes As Boolean
            Get
                Return _dialogBackupServerIndexes
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogBackupServerIndexes, value)
                RaiseBackupDialog()
            End Set
        End Property

        Public Property DialogBackupSettings As Boolean
            Get
                Return _dialogBackupSettings
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogBackupSettings, value)
                RaiseBackupDialog()
            End Set
        End Property

        Public Property DialogBackupFavorites As Boolean
            Get
                Return _dialogBackupFavorites
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogBackupFavorites, value)
                RaiseBackupDialog()
            End Set
        End Property

        Public Property DialogBackupSearches As Boolean
            Get
                Return _dialogBackupSearches
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogBackupSearches, value)
                RaiseBackupDialog()
            End Set
        End Property

        ''' <summary>Die Zugangsdaten gibt es nur zusammen mit den Einstellungen.</summary>
        Public Property DialogBackupSecrets As Boolean
            Get
                Return _dialogBackupSecrets
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogBackupSecrets, value)
                RaiseBackupDialog()
            End Set
        End Property

        ' Beim Wiederherstellen: welche Teile die Sicherung enthaelt. Beim Sichern ist alles waehlbar.
        Public ReadOnly Property CanChooseBackupCatalog As Boolean
            Get
                Return PartAvailable(BackupParts.Catalog)
            End Get
        End Property
        Public ReadOnly Property CanChooseBackupServerIndexes As Boolean
            Get
                Return PartAvailable(BackupParts.ServerIndexes)
            End Get
        End Property
        Public ReadOnly Property CanChooseBackupSettings As Boolean
            Get
                Return PartAvailable(BackupParts.Settings)
            End Get
        End Property
        Public ReadOnly Property CanChooseBackupFavorites As Boolean
            Get
                Return PartAvailable(BackupParts.Favorites)
            End Get
        End Property
        Public ReadOnly Property CanChooseBackupSearches As Boolean
            Get
                Return PartAvailable(BackupParts.Searches)
            End Get
        End Property
        Public ReadOnly Property CanChooseBackupSecrets As Boolean
            Get
                Return _dialogBackupSettings AndAlso PartAvailable(BackupParts.Secrets)
            End Get
        End Property

        Private Function PartAvailable(part As BackupParts) As Boolean
            If DialogShowsBackup Then Return True
            Return _dialogRestoreManifest IsNot Nothing AndAlso (_dialogRestoreManifest.Parts And part) <> 0
        End Function

        ''' <summary>Was in der gewaehlten Sicherung steckt, oder warum sie nicht taugt.</summary>
        Public ReadOnly Property DialogRestoreSummary As String
            Get
                If Not DialogShowsRestore Then Return ""
                If String.IsNullOrEmpty(_dialogBackupPath) Then Return LocalizationService.T("Sicherung wählen")
                If _dialogRestoreManifest Is Nothing Then Return _dialogRestoreProblem
                Dim m = _dialogRestoreManifest
                Return String.Format(LocalizationService.T("Gesichert am {0} mit FerrumPix {1} auf {2}. Im Katalog: {3} Bilder, {4} davon mit Stichwörtern, {5} benannte Personen."),
                                     LocalizationService.FormatDateTime(m.CreatedUtc.ToLocalTime()), m.AppVersion, m.MachineName,
                                     m.ImageCount, m.KeywordImageCount, m.PersonCount)
            End Get
        End Property

        Private Function ChosenBackupParts() As BackupParts
            Dim parts = BackupParts.None
            If _dialogBackupCatalog AndAlso PartAvailable(BackupParts.Catalog) Then parts = parts Or BackupParts.Catalog
            If _dialogBackupServerIndexes AndAlso PartAvailable(BackupParts.ServerIndexes) Then parts = parts Or BackupParts.ServerIndexes
            If _dialogBackupSettings AndAlso PartAvailable(BackupParts.Settings) Then parts = parts Or BackupParts.Settings
            If _dialogBackupFavorites AndAlso PartAvailable(BackupParts.Favorites) Then parts = parts Or BackupParts.Favorites
            If _dialogBackupSearches AndAlso PartAvailable(BackupParts.Searches) Then parts = parts Or BackupParts.Searches
            If _dialogBackupSecrets AndAlso CanChooseBackupSecrets Then parts = parts Or BackupParts.Secrets
            Return parts
        End Function

        Private Function IsBackupDialogPrimaryEnabled() As Boolean
            If String.IsNullOrWhiteSpace(_dialogBackupPath) Then Return False
            If DialogShowsRestore AndAlso _dialogRestoreManifest Is Nothing Then Return False
            Return (ChosenBackupParts() And Not BackupParts.Secrets) <> BackupParts.None
        End Function

        Private Sub ReadRestoreManifest()
            _dialogRestoreProblem = ""
            _dialogRestoreManifest = Nothing
            If String.IsNullOrEmpty(_dialogBackupPath) Then Return
            If Not File.Exists(_dialogBackupPath) Then
                _dialogRestoreProblem = LocalizationService.T("Die Datei gibt es nicht.")
                Return
            End If
            _dialogRestoreManifest = CatalogBackupService.ReadManifest(_dialogBackupPath, _dialogRestoreProblem)
        End Sub

        Private Sub RaiseBackupDialog()
            For Each name In {NameOf(CanChooseBackupCatalog), NameOf(CanChooseBackupServerIndexes), NameOf(CanChooseBackupSettings),
                              NameOf(CanChooseBackupFavorites), NameOf(CanChooseBackupSearches), NameOf(CanChooseBackupSecrets),
                              NameOf(DialogRestoreSummary), NameOf(IsDialogPrimaryEnabled)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        Private Sub ResetBackupDialog(path As String)
            _dialogBackupCatalog = True
            _dialogBackupServerIndexes = True
            _dialogBackupSettings = True
            _dialogBackupFavorites = True
            _dialogBackupSearches = True
            _dialogBackupSecrets = False
            _dialogRestoreManifest = Nothing
            _dialogRestoreProblem = ""
            _dialogBackupPath = If(path, "")
            For Each name In {NameOf(DialogBackupCatalog), NameOf(DialogBackupServerIndexes), NameOf(DialogBackupSettings),
                              NameOf(DialogBackupFavorites), NameOf(DialogBackupSearches), NameOf(DialogBackupSecrets),
                              NameOf(DialogBackupPath)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        ''' <summary>Sichern: Dialog, dann der Lauf mit Warteanzeige, dann die Meldung.</summary>
        Public Async Function ShowBackupDialogAsync() As Task
            Dim folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            If String.IsNullOrEmpty(folder) Then folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            ResetBackupDialog(Path.Combine(folder, CatalogBackupService.SuggestedFileName()))
            Dim answer = Await ShowDialogAsync(AppDialogKind.Backup,
                                               "Sichern",
                                               "Sichert Katalog, Einstellungen und Listen in eine ZIP-Datei. Fotos, Vorschaubilder und Modelle sind nicht darin.",
                                               "", "Sichern", "Abbrechen")
            RaiseBackupDialog()
            If answer Is Nothing Then Return
            Dim target = _dialogBackupPath
            If Not target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then target &= ".zip"
            Dim parts = ChosenBackupParts()
            Dim errorText As String = Nothing
            Dim manifest As BackupManifest = Nothing
            Try
                BeginBusyOverlay(LocalizationService.T("Wird gesichert…"))
                manifest = Await Task.Run(Function() CatalogBackupService.Create(target, parts))
            Catch ex As Exception
                DiagnosticLogService.LogException("Backup.Create", ex)
                errorText = ex.Message
            Finally
                EndBusyOverlay()
            End Try
            If errorText IsNot Nothing Then
                Await ShowMessageAsync(LocalizationService.T("Sichern fehlgeschlagen"), errorText)
                Return
            End If
            Await ShowMessageAsync(LocalizationService.T("Gesichert"),
                                   String.Format(LocalizationService.T("Die Sicherung liegt unter {0}. Im Katalog: {1} Bilder, {2} benannte Personen."),
                                                 target, manifest.ImageCount, manifest.PersonCount))
        End Function

        ''' <summary>Wiederherstellen: Dialog, Bereitlegen samt Sicherung des jetzigen Stands, dann
        ''' die Frage, ob FerrumPix gleich beendet werden soll. Eingespielt wird beim naechsten Start.</summary>
        Public Async Function ShowRestoreDialogAsync() As Task
            ResetBackupDialog("")
            Dim answer = Await ShowDialogAsync(AppDialogKind.Restore,
                                               "Wiederherstellen",
                                               "Holt die gewählten Teile einer Sicherung zurück. Der jetzige Stand wird vorher gesichert, und eingespielt wird beim nächsten Start von FerrumPix.",
                                               "", "Wiederherstellen", "Abbrechen")
            RaiseBackupDialog()
            If answer Is Nothing Then Return
            Dim source = _dialogBackupPath
            Dim parts = ChosenBackupParts()
            Dim problem As String = Nothing
            Try
                BeginBusyOverlay(LocalizationService.T("Sicherung wird geprüft…"))
                problem = Await Task.Run(Function() CatalogBackupService.StageRestore(source, parts))
            Finally
                EndBusyOverlay()
            End Try
            If Not String.IsNullOrEmpty(problem) Then
                Await ShowMessageAsync(LocalizationService.T("Wiederherstellen fehlgeschlagen"), problem)
                Return
            End If
            Dim quit = Await ShowConfirmAsync(LocalizationService.T("Bereit zum Wiederherstellen"),
                                              String.Format(LocalizationService.T("Die Sicherung ist geprüft und wird beim nächsten Start von FerrumPix eingespielt. Der jetzige Stand liegt gesichert unter {0}. FerrumPix jetzt beenden?"),
                                                            CatalogBackupService.SafetyBackupDirectory),
                                              LocalizationService.T("Jetzt beenden"),
                                              LocalizationService.T("Später"))
            If quit Then
                TryCast(Avalonia.Application.Current?.ApplicationLifetime, IClassicDesktopStyleApplicationLifetime)?.Shutdown()
            End If
        End Function

    End Class

End Namespace
