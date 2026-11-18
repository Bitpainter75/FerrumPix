Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Eine Zeile im Dialog "Erweiterungen herunterladen": ein Eintrag des Index samt
    ''' Zustand auf diesem Rechner und eigenem Knopf zum Herunterladen oder Aktualisieren.</summary>
    Public Class ExtensionDownloadRow
        Inherits ReactiveObject

        Private _state As ExtensionCatalogService.InstallState
        Private _isBusy As Boolean
        Private _message As String = ""
        Private _preview As Avalonia.Media.Imaging.Bitmap

        Public Sub New(entry As ExtensionCatalogEntry, state As ExtensionCatalogService.InstallState, Optional formerlyShipped As Boolean = False)
            Me.Entry = entry
            _state = state
            Me.FormerlyShipped = formerlyShipped
            Dim parts As New List(Of String) From {
                String.Format(CultureInfo.CurrentCulture, LocalizationService.T("Version {0}"), entry.Version)}
            If entry.Author.Length > 0 Then parts.Add(entry.Author)
            Details = String.Join(", ", parts)
        End Sub

        Public ReadOnly Property Entry As ExtensionCatalogEntry
        Public ReadOnly Property Details As String
        ''' <summary>War in 0.9.87 mitgeliefert, eingeblendet und fehlt jetzt; steht deshalb oben.</summary>
        Public ReadOnly Property FormerlyShipped As Boolean

        Public ReadOnly Property Name As String
            Get
                Return Entry.Name
            End Get
        End Property

        Public ReadOnly Property Description As String
            Get
                Return Entry.Description
            End Get
        End Property

        Public ReadOnly Property HasDescription As Boolean
            Get
                Return Entry.Description.Length > 0
            End Get
        End Property

        Public ReadOnly Property HasPullRequest As Boolean
            Get
                Return Entry.PullRequest.Length > 0
            End Get
        End Property

        Public Property State As ExtensionCatalogService.InstallState
            Get
                Return _state
            End Get
            Set(value As ExtensionCatalogService.InstallState)
                Me.RaiseAndSetIfChanged(_state, value)
                RaiseStateDependents()
            End Set
        End Property

        Public Property IsBusy As Boolean
            Get
                Return _isBusy
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isBusy, value)
                RaiseStateDependents()
            End Set
        End Property

        ''' <summary>Was beim letzten Herunterladen schiefging; leer sonst.</summary>
        Public Property Message As String
            Get
                Return _message
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_message, If(value, ""))
                Me.RaisePropertyChanged(NameOf(HasMessage))
            End Set
        End Property

        Public ReadOnly Property HasMessage As Boolean
            Get
                Return _message.Length > 0
            End Get
        End Property

        Public ReadOnly Property IsUpdate As Boolean
            Get
                Return _state = ExtensionCatalogService.InstallState.UpdateAvailable
            End Get
        End Property

        ''' <summary>Der Knopf ist frei, solange es etwas zu holen gibt und nicht schon geholt wird.</summary>
        Public ReadOnly Property CanDownload As Boolean
            Get
                Return Not _isBusy AndAlso _state <> ExtensionCatalogService.InstallState.Installed
            End Get
        End Property

        Public ReadOnly Property ButtonText As String
            Get
                If _isBusy Then Return LocalizationService.T("Wird geladen…")
                Select Case _state
                    Case ExtensionCatalogService.InstallState.Installed : Return LocalizationService.T("Installiert")
                    Case ExtensionCatalogService.InstallState.UpdateAvailable : Return LocalizationService.T("Aktualisieren")
                    Case Else : Return LocalizationService.T("Herunterladen")
                End Select
            End Get
        End Property

        ''' <summary>Unter dem Namen: bei einer Aktualisierung die vorhandene Fassung.</summary>
        Public ReadOnly Property StateText As String
            Get
                If _state <> ExtensionCatalogService.InstallState.UpdateAvailable Then Return ""
                Return String.Format(CultureInfo.CurrentCulture, LocalizationService.T("Aktualisierung von Version {0}"),
                                     ExtensionService.Find(Entry.Id)?.Version)
            End Get
        End Property

        Public ReadOnly Property HasStateText As Boolean
            Get
                Return StateText.Length > 0
            End Get
        End Property

        Private Sub RaiseStateDependents()
            Me.RaisePropertyChanged(NameOf(IsUpdate))
            Me.RaisePropertyChanged(NameOf(CanDownload))
            Me.RaisePropertyChanged(NameOf(ButtonText))
            Me.RaisePropertyChanged(NameOf(StateText))
            Me.RaisePropertyChanged(NameOf(HasStateText))
        End Sub

        ''' <summary>Das Beispielbild aus dem Index, nachgeladen, sobald der Dialog steht. Gehoert der
        ''' Zeile; der Dialog gibt es beim Schliessen frei.</summary>
        Public Property Preview As Avalonia.Media.Imaging.Bitmap
            Get
                Return _preview
            End Get
            Set(value As Avalonia.Media.Imaging.Bitmap)
                Me.RaiseAndSetIfChanged(_preview, value)
                Me.RaisePropertyChanged(NameOf(HasPreview))
            End Set
        End Property

        Public ReadOnly Property HasPreview As Boolean
            Get
                Return _preview IsNot Nothing
            End Get
        End Property
    End Class

    ''' <summary>Der Dialog "Erweiterungen herunterladen" (ExtensionCatalogService): der Index aus
    ''' dem Repository als Liste, je Eintrag Beispielbild, Beschreibung, Zustand auf diesem Rechner,
    ''' ein Knopf zur Beschreibung im Pull Request und ein Knopf, der sie sofort holt oder
    ''' aktualisiert. Der Dialog selbst hat nur "Schließen".
    '''
    ''' Oben stehen die Aktualisierungen und die Erweiterungen, die ein Nutzer in 0.9.87 aus dem
    ''' Lieferumfang eingeblendet hatte und die seitdem fehlen (AppSettings.ShownExtensions); geholt
    ''' wird nur auf den Knopf hin.</summary>
    Partial Public Class MainWindowViewModel

        Public ReadOnly Property DialogExtensionRows As New ObservableCollection(Of ExtensionDownloadRow)()

        Public ReadOnly Property DialogShowsExtensionDownload As Boolean
            Get
                Return _dialogKind = AppDialogKind.ExtensionDownload
            End Get
        End Property

        Private _openExtensionPullRequestCommand As ICommand
        Private _downloadExtensionRowCommand As ICommand

        ''' <summary>Oeffnet den Pull Request einer Erweiterung im Browser. Nur Adressen, die der
        ''' Index als Pull Request von FerrumPix angenommen hat.</summary>
        Public ReadOnly Property OpenExtensionPullRequestCommand As ICommand
            Get
                If _openExtensionPullRequestCommand Is Nothing Then
                    _openExtensionPullRequestCommand = ReactiveCommand.Create(Of ExtensionDownloadRow)(
                        Sub(row)
                            If row Is Nothing OrElse Not row.HasPullRequest OrElse Not ExtensionService.IsSafeWebAddress(row.Entry.PullRequest) Then Return
                            ShellOpenService.Open(row.Entry.PullRequest, "Extensions.PullRequest")
                        End Sub)
                End If
                Return _openExtensionPullRequestCommand
            End Get
        End Property

        ''' <summary>Holt die Erweiterung einer Zeile oder aktualisiert sie.</summary>
        Public ReadOnly Property DownloadExtensionRowCommand As ICommand
            Get
                If _downloadExtensionRowCommand Is Nothing Then
                    _downloadExtensionRowCommand = ReactiveCommand.CreateFromTask(Of ExtensionDownloadRow)(AddressOf DownloadExtensionRowAsync)
                End If
                Return _downloadExtensionRowCommand
            End Get
        End Property

        Friend Async Function DownloadExtensionRowAsync(row As ExtensionDownloadRow) As Task
            If row Is Nothing OrElse Not row.CanDownload Then Return
            row.IsBusy = True
            row.Message = ""
            Try
                Dim result = Await ExtensionCatalogService.DownloadAsync(row.Entry)
                If result.Succeeded Then
                    row.State = ExtensionCatalogService.StateOf(row.Entry)
                Else
                    row.Message = LocalizationService.T("Nicht geladen:") & " " & result.ErrorText
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Extensions.Download", ex)
                row.Message = LocalizationService.T("Nicht geladen:") & " " & ex.Message
            Finally
                row.IsBusy = False
            End Try
        End Function

        ''' <summary>Baut die Zeilen aus dem Index. Getrennt vom Dialog, damit der Pruefstand sie
        ''' ohne Netz und ohne Oberflaeche nachrechnen kann.</summary>
        Friend Sub FillExtensionDownloadRows(entries As IEnumerable(Of ExtensionCatalogEntry))
            ReleaseExtensionRows()
            Dim formerlyShown = New HashSet(Of String)(
                If(AppSettingsService.Load().ShownExtensions, "").Split(","c).Select(Function(t) t.Trim()).Where(Function(t) t.Length > 0),
                StringComparer.Ordinal)
            Dim rows = entries.Select(
                Function(e)
                    Dim state = ExtensionCatalogService.StateOf(e)
                    Return New ExtensionDownloadRow(e, state,
                        state = ExtensionCatalogService.InstallState.NotInstalled AndAlso formerlyShown.Contains(e.Id))
                End Function)
            ' Aktualisierungen und frueher Mitgeliefertes zuerst, dann was noch fehlt, zuletzt das
            ' Vorhandene; je nach Name. Die Beispiele der Anleitung (Kennung "example.") stehen
            ' ganz unten, gleich in welchem Zustand: sie zeigen, wie man schreibt, und sind keine
            ' Werkzeuge, die man sucht.
            For Each row In rows.OrderBy(Function(r) IsGuideExample(r)).
                                 ThenBy(Function(r) RowOrder(r)).
                                 ThenBy(Function(r) r.Name, StringComparer.CurrentCultureIgnoreCase)
                DialogExtensionRows.Add(row)
            Next
        End Sub

        Private Shared Function IsGuideExample(row As ExtensionDownloadRow) As Boolean
            Return row.Entry.Id.StartsWith("example.", StringComparison.Ordinal)
        End Function

        Private Shared Function RowOrder(row As ExtensionDownloadRow) As Integer
            If row.IsUpdate OrElse row.FormerlyShipped Then Return 0
            If row.State = ExtensionCatalogService.InstallState.NotInstalled Then Return 1
            Return 2
        End Function

        ''' <summary>Laedt die Beispielbilder nach, eines nach dem anderen. Ein Bild, das ankommt,
        ''' nachdem der Dialog zu ist, gehoert niemandem mehr und wird gleich freigegeben. Async Sub:
        ''' das eigene Try haelt eine Ausnahme vom Dispatcher fern.</summary>
        Private Async Sub LoadExtensionPreviews(rows As List(Of ExtensionDownloadRow))
            Try
                For Each row In rows.Where(Function(r) r.Entry.HasImage)
                    Dim bytes = Await ExtensionCatalogService.FetchImageAsync(row.Entry)
                    If bytes Is Nothing Then Continue For
                    Dim bitmap = Await Task.Run(
                        Function()
                            Try
                                Using stream As New IO.MemoryStream(bytes)
                                    Return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 480)
                                End Using
                            Catch
                                Return Nothing
                            End Try
                        End Function)
                    If bitmap Is Nothing Then Continue For
                    If Not DialogExtensionRows.Contains(row) Then
                        bitmap.Dispose()
                        Return
                    End If
                    row.Preview = bitmap
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Extensions.Previews", ex)
            End Try
        End Sub

        ''' <summary>Leert die Liste und gibt die Beispielbilder frei: erst aus der Anzeige, dann weg.</summary>
        Private Sub ReleaseExtensionRows()
            Dim rows = DialogExtensionRows.ToList()
            DialogExtensionRows.Clear()
            For Each row In rows
                Dim bitmap = row.Preview
                row.Preview = Nothing
                bitmap?.Dispose()
            Next
        End Sub

        ''' <summary>Index holen, Dialog zeigen; geholt wird je Zeile auf ihren Knopf hin.</summary>
        Public Async Function ShowExtensionDownloadDialogAsync() As Task
            Dim entries As List(Of ExtensionCatalogEntry) = Nothing
            Dim problem As String = Nothing
            Try
                BeginBusyOverlay(LocalizationService.T("Liste der Erweiterungen wird geladen…"))
                entries = Await ExtensionCatalogService.FetchIndexAsync()
            Catch ex As Exception
                DiagnosticLogService.LogException("Extensions.Index", ex)
                problem = ex.Message
            Finally
                EndBusyOverlay()
            End Try
            If problem IsNot Nothing Then
                Await ShowMessageAsync(LocalizationService.T("Erweiterungen herunterladen"),
                                       LocalizationService.T("Die Liste der Erweiterungen ließ sich nicht laden.") & vbLf & problem)
                Return
            End If

            FillExtensionDownloadRows(entries)
            LoadExtensionPreviews(DialogExtensionRows.ToList())
            Await ShowDialogAsync(AppDialogKind.ExtensionDownload,
                                  "Erweiterungen herunterladen",
                                  "Erweiterungen aus dem Verzeichnis von FerrumPix. Jede kam als Pull Request, dort stehen Beschreibung und Beispielbilder.",
                                  "", "Schließen", "")
            ReleaseExtensionRows()
        End Function

    End Class

End Namespace
