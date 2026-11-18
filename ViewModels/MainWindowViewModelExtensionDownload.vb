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
    ''' Zustand auf diesem Rechner und Haken.</summary>
    Public Class ExtensionDownloadRow
        Inherits ReactiveObject

        Private _isChecked As Boolean
        Private ReadOnly _changed As Action
        Private _preview As Avalonia.Media.Imaging.Bitmap

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

        Public Sub New(entry As ExtensionCatalogEntry, state As ExtensionCatalogService.InstallState, isChecked As Boolean, changed As Action)
            Me.Entry = entry
            Me.State = state
            _isChecked = isChecked
            _changed = changed
            Dim parts As New List(Of String) From {
                String.Format(CultureInfo.CurrentCulture, LocalizationService.T("Fassung {0}"), entry.Version)}
            If entry.Author.Length > 0 Then parts.Add(entry.Author)
            Details = String.Join(", ", parts)
            Select Case state
                Case ExtensionCatalogService.InstallState.Installed
                    StateText = LocalizationService.T("Installiert")
                Case ExtensionCatalogService.InstallState.UpdateAvailable
                    StateText = String.Format(CultureInfo.CurrentCulture, LocalizationService.T("Aktualisierung von Fassung {0}"),
                                              ExtensionService.Find(entry.Id)?.Version)
                Case Else
                    StateText = ""
            End Select
        End Sub

        Public ReadOnly Property Entry As ExtensionCatalogEntry
        Public ReadOnly Property State As ExtensionCatalogService.InstallState
        Public ReadOnly Property Details As String
        Public ReadOnly Property StateText As String

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

        Public ReadOnly Property HasStateText As Boolean
            Get
                Return StateText.Length > 0
            End Get
        End Property

        Public ReadOnly Property IsUpdate As Boolean
            Get
                Return State = ExtensionCatalogService.InstallState.UpdateAvailable
            End Get
        End Property

        ''' <summary>Eine schon installierte Erweiterung in derselben Fassung gibt es nichts zu holen.</summary>
        Public ReadOnly Property CanCheck As Boolean
            Get
                Return State <> ExtensionCatalogService.InstallState.Installed
            End Get
        End Property

        Public ReadOnly Property HasPullRequest As Boolean
            Get
                Return Entry.PullRequest.Length > 0
            End Get
        End Property

        Public Property IsChecked As Boolean
            Get
                Return _isChecked
            End Get
            Set(value As Boolean)
                If Not CanCheck Then value = False
                Me.RaiseAndSetIfChanged(_isChecked, value)
                _changed?.Invoke()
            End Set
        End Property
    End Class

    ''' <summary>Der Dialog "Erweiterungen herunterladen" (ExtensionCatalogService): der Index aus
    ''' dem Repository als Liste zum Anhaken, mit Zustand je Eintrag (neu, installiert, Aktualisierung)
    ''' und dem Link zum Pull Request, in dem die Erweiterung beschrieben ist.
    '''
    ''' Vorgehakt sind die Aktualisierungen und die Erweiterungen, die ein Nutzer in 0.9.87 aus dem
    ''' Lieferumfang eingeblendet hatte und die seitdem fehlen (AppSettings.ShownExtensions); sonst
    ''' nichts, geholt wird nur, was der Nutzer ausdruecklich will.</summary>
    Partial Public Class MainWindowViewModel

        Public ReadOnly Property DialogExtensionRows As New ObservableCollection(Of ExtensionDownloadRow)()

        Public ReadOnly Property DialogShowsExtensionDownload As Boolean
            Get
                Return _dialogKind = AppDialogKind.ExtensionDownload
            End Get
        End Property

        Private _openExtensionPullRequestCommand As ICommand

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

        Private Function IsExtensionDownloadPrimaryEnabled() As Boolean
            Return DialogExtensionRows.Any(Function(r) r.IsChecked)
        End Function

        ''' <summary>Baut die Zeilen aus dem Index. Getrennt vom Dialog, damit der Pruefstand sie
        ''' ohne Netz und ohne Oberflaeche nachrechnen kann.</summary>
        Friend Sub FillExtensionDownloadRows(entries As IEnumerable(Of ExtensionCatalogEntry))
            ReleaseExtensionRows()
            Dim formerlyShown = New HashSet(Of String)(
                If(AppSettingsService.Load().ShownExtensions, "").Split(","c).Select(Function(t) t.Trim()).Where(Function(t) t.Length > 0),
                StringComparer.Ordinal)
            Dim refresh As Action = Sub() Me.RaisePropertyChanged(NameOf(IsDialogPrimaryEnabled))
            ' Aktualisierungen zuerst, dann was noch fehlt, zuletzt das Vorhandene; je nach Name.
            For Each entry In entries.
                OrderBy(Function(e) StateOrder(ExtensionCatalogService.StateOf(e))).
                ThenBy(Function(e) e.Name, StringComparer.CurrentCultureIgnoreCase)
                Dim state = ExtensionCatalogService.StateOf(entry)
                Dim preselect = state = ExtensionCatalogService.InstallState.UpdateAvailable OrElse
                                (state = ExtensionCatalogService.InstallState.NotInstalled AndAlso formerlyShown.Contains(entry.Id))
                DialogExtensionRows.Add(New ExtensionDownloadRow(entry, state, preselect, refresh))
            Next
            refresh()
        End Sub

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

        Private Shared Function StateOrder(state As ExtensionCatalogService.InstallState) As Integer
            Select Case state
                Case ExtensionCatalogService.InstallState.UpdateAvailable : Return 0
                Case ExtensionCatalogService.InstallState.NotInstalled : Return 1
                Case Else : Return 2
            End Select
        End Function

        ''' <summary>Index holen, Dialog zeigen, Gewaehltes holen und pruefen, Ergebnis melden.</summary>
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
            Dim answer = Await ShowDialogAsync(AppDialogKind.ExtensionDownload,
                                               "Erweiterungen herunterladen",
                                               "Erweiterungen aus dem Verzeichnis von FerrumPix. Jede kam als Pull Request, dort stehen Beschreibung und Beispielbilder. Geholt wird nur, was einen Haken hat.",
                                               "", "Herunterladen", "Schließen")
            Dim chosen = DialogExtensionRows.Where(Function(r) r.IsChecked).Select(Function(r) r.Entry).ToList()
            ReleaseExtensionRows()
            If answer Is Nothing OrElse chosen.Count = 0 Then Return

            Dim loaded As New List(Of String)()
            Dim failed As New List(Of String)()
            Try
                BeginBusyOverlay(LocalizationService.T("Erweiterungen werden heruntergeladen…"))
                For Each entry In chosen
                    Dim result = Await ExtensionCatalogService.DownloadAsync(entry)
                    If result.Succeeded Then loaded.Add(entry.Name) Else failed.Add(entry.Name & ": " & result.ErrorText)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Extensions.Download", ex)
                failed.Add(ex.Message)
            Finally
                EndBusyOverlay()
            End Try
            Dim lines As New List(Of String)()
            If loaded.Count > 0 Then
                lines.Add(String.Format(CultureInfo.CurrentCulture,
                                        LocalizationService.T("Geladen: {0}. Das Werkzeug Erweiterungen im Editor zeigt ihre Regler."),
                                        String.Join(", ", loaded)))
            End If
            If failed.Count > 0 Then lines.Add(LocalizationService.T("Nicht geladen:") & " " & String.Join("; ", failed))
            Await ShowMessageAsync(LocalizationService.T("Erweiterungen herunterladen"), String.Join(vbLf, lines))
        End Function

    End Class

End Namespace
