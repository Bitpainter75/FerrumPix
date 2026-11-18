Imports System
Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Eine Zeile im Bereich "Erweiterungen" der Einstellungen: eine geladene Erweiterung
    ''' oder eine Datei, die nicht geladen werden konnte.</summary>
    Public NotInheritable Class ExtensionRow
        Public Property Definition As ExtensionDefinition
        Public Property Name As String = ""
        Public Property Details As String = ""
        Public Property ErrorText As String = ""
        Public Property CanRemove As Boolean
        Public Property RemoveText As String = ""
        Public Property CanMoveUp As Boolean
        Public Property CanMoveDown As Boolean
        Public Property IsShown As Boolean = True
        Public Property MoveUpText As String = ""
        Public Property MoveDownText As String = ""
        ''' <summary>Wo die Beschreibung liegt, und der Text des Links dazu.</summary>
        Public Property Url As String = ""
        Public Property UrlText As String = ""

        Public ReadOnly Property HasUrl As Boolean
            Get
                Return ExtensionService.IsSafeWebAddress(Url)
            End Get
        End Property

        Public ReadOnly Property IsLoaded As Boolean
            Get
                Return Definition IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property HasError As Boolean
            Get
                Return ErrorText.Length > 0
            End Get
        End Property

        Public ReadOnly Property HasDetails As Boolean
            Get
                Return Details.Length > 0
            End Get
        End Property
    End Class

    ' Die Verwaltung der Erweiterungen in den Einstellungen: laden, entfernen, neu einlesen, und
    ' was nicht geladen werden konnte, samt Grund. Das Werkzeug im Editor folgt dem Bestand ueber
    ' ExtensionService.Changed.
    Partial Public Class SettingsViewModel

        Private _extensionsHooked As Boolean
        Private _extensionMessage As String = ""
        Private _removeExtensionCommand As ICommand
        Private _reloadExtensionsCommand As ICommand
        Private _moveExtensionUpCommand As ICommand
        Private _moveExtensionDownCommand As ICommand
        Private _toggleExtensionVisibilityCommand As ICommand

        ''' <summary>Blendet eine Erweiterung im Werkzeug aus oder ein, wie das Auge der Gruppen im
        ''' kompakten Anpassungsmodus. Sie rechnet in Bildern weiter.</summary>
        Public ReadOnly Property ToggleExtensionVisibilityCommand As ICommand
            Get
                If _toggleExtensionVisibilityCommand Is Nothing Then
                    _toggleExtensionVisibilityCommand = ReactiveCommand.Create(Of ExtensionRow)(
                        Sub(row)
                            If row?.Definition Is Nothing Then Return
                            ExtensionService.SetHidden(row.Definition.Id, row.IsShown)
                        End Sub)
                End If
                Return _toggleExtensionVisibilityCommand
            End Get
        End Property

        ''' <summary>Schiebt eine Erweiterung in der Reihenfolge der Gruppen im Werkzeug nach oben.
        ''' Sofort wirksam und gespeichert, wie Laden und Entfernen.</summary>
        Public ReadOnly Property MoveExtensionUpCommand As ICommand
            Get
                If _moveExtensionUpCommand Is Nothing Then
                    _moveExtensionUpCommand = ReactiveCommand.Create(Of ExtensionRow)(
                        Sub(row) If row?.Definition IsNot Nothing Then ExtensionService.MoveInOrder(row.Definition.Id, -1))
                End If
                Return _moveExtensionUpCommand
            End Get
        End Property

        Public ReadOnly Property MoveExtensionDownCommand As ICommand
            Get
                If _moveExtensionDownCommand Is Nothing Then
                    _moveExtensionDownCommand = ReactiveCommand.Create(Of ExtensionRow)(
                        Sub(row) If row?.Definition IsNot Nothing Then ExtensionService.MoveInOrder(row.Definition.Id, 1))
                End If
                Return _moveExtensionDownCommand
            End Get
        End Property

        Public ReadOnly Property ExtensionRows As New ObservableCollection(Of ExtensionRow)()

        ''' <summary>Die geladenen Erweiterungen in der Reihenfolge der Gruppen im Werkzeug.</summary>
        Public ReadOnly Property ExtensionOrderItems As New ObservableCollection(Of ExtensionRow)()

        Public ReadOnly Property HasExtensionOrderItems As Boolean
            Get
                EnsureExtensionsHooked()
                ' Ab EINER Erweiterung: das Auge zum Ausblenden braucht keine zweite.
                Return ExtensionOrderItems.Count > 0
            End Get
        End Property

        Private _resetExtensionOrderCommand As ICommand

        Public ReadOnly Property ResetExtensionOrderCommand As ICommand
            Get
                If _resetExtensionOrderCommand Is Nothing Then
                    _resetExtensionOrderCommand = ReactiveCommand.Create(Sub() ExtensionService.ResetOrder())
                End If
                Return _resetExtensionOrderCommand
            End Get
        End Property

        Public ReadOnly Property HasExtensionRows As Boolean
            Get
                EnsureExtensionsHooked()
                Return ExtensionRows.Count > 0
            End Get
        End Property

        Public ReadOnly Property ExtensionFolder As String
            Get
                Return ExtensionService.UserFolder
            End Get
        End Property

        ''' <summary>Was das letzte Laden ergeben hat, oder leer.</summary>
        Public Property ExtensionMessage As String
            Get
                Return _extensionMessage
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_extensionMessage, If(value, ""))
                Me.RaisePropertyChanged(NameOf(HasExtensionMessage))
            End Set
        End Property

        Public ReadOnly Property HasExtensionMessage As Boolean
            Get
                Return _extensionMessage.Length > 0
            End Get
        End Property

        ''' <summary>Oeffnet die Beschreibung einer Erweiterung im Browser. Die Adresse wird hier
        ''' noch einmal geprueft: die Shell oeffnet sonst auch Dateien und Programme.</summary>
        Public ReadOnly Property OpenExtensionUrlCommand As ICommand
            Get
                If _openExtensionUrlCommand Is Nothing Then
                    _openExtensionUrlCommand = ReactiveCommand.Create(Of ExtensionRow)(
                        Sub(row)
                            If row Is Nothing OrElse Not ExtensionService.IsSafeWebAddress(row.Url) Then Return
                            ShellOpenService.Open(row.Url.Trim(), "Extensions.Url")
                        End Sub)
                End If
                Return _openExtensionUrlCommand
            End Get
        End Property

        Private _openExtensionUrlCommand As ICommand
        Private _downloadExtensionsCommand As ICommand

        ''' <summary>Oeffnet den Dialog "Erweiterungen herunterladen" (MainWindowViewModelExtensionDownload).</summary>
        Public ReadOnly Property DownloadExtensionsCommand As ICommand
            Get
                If _downloadExtensionsCommand Is Nothing Then
                    _downloadExtensionsCommand = ReactiveCommand.CreateFromTask(
                        Function() If(_mainVm Is Nothing, Task.CompletedTask, _mainVm.ShowExtensionDownloadDialogAsync()))
                End If
                Return _downloadExtensionsCommand
            End Get
        End Property

        Public ReadOnly Property RemoveExtensionCommand As ICommand
            Get
                If _removeExtensionCommand Is Nothing Then
                    _removeExtensionCommand = ReactiveCommand.CreateFromTask(Of ExtensionRow)(AddressOf RemoveExtensionAsync)
                End If
                Return _removeExtensionCommand
            End Get
        End Property

        Public ReadOnly Property ReloadExtensionsCommand As ICommand
            Get
                If _reloadExtensionsCommand Is Nothing Then
                    _reloadExtensionsCommand = ReactiveCommand.Create(
                        Sub()
                            Dim notes = ExtensionService.RefreshFromSources()
                            ExtensionMessage = If(notes.Count = 0, "",
                                                  LocalizationService.T("Nicht neu geladen:") & " " & String.Join("; ", notes))
                        End Sub)
                End If
                Return _reloadExtensionsCommand
            End Get
        End Property

        Private Sub EnsureExtensionsHooked()
            If _extensionsHooked Then Return
            _extensionsHooked = True
            AddHandler ExtensionService.Changed,
                Sub(s, e) Avalonia.Threading.Dispatcher.UIThread.Post(AddressOf RebuildExtensionRows)
            RebuildExtensionRows()
        End Sub

        Private Sub RebuildExtensionRows()
            ExtensionRows.Clear()
            ExtensionOrderItems.Clear()
            Dim loaded = ExtensionService.All
            For Each definition In loaded
                Dim parts As New List(Of String) From {
                    String.Format(CultureInfo.CurrentCulture, LocalizationService.T("Version {0}"), definition.Version)}
                If definition.Author.Length > 0 Then parts.Add(definition.Author)
                If definition.License.Length > 0 Then parts.Add(definition.License)
                If definition.IsBundled Then parts.Add(LocalizationService.T("mitgeliefert"))
                Dim details = String.Join(", ", parts)
                If definition.Description.Length > 0 Then details = definition.Description & vbLf & details
                ExtensionRows.Add(New ExtensionRow With {
                    .Definition = definition, .Name = definition.Name, .Details = details,
                    .CanRemove = Not definition.IsBundled, .RemoveText = LocalizationService.T("Entfernen"),
                    .CanMoveUp = ExtensionRows.Count > 0, .CanMoveDown = ExtensionRows.Count < loaded.Count - 1,
                    .MoveUpText = LocalizationService.T("Nach oben"), .MoveDownText = LocalizationService.T("Nach unten"),
                    .IsShown = Not ExtensionService.IsHidden(definition.Id),
                    .Url = definition.Url, .UrlText = LocalizationService.T("Beschreibung")})
            Next
            For Each row In ExtensionRows
                ExtensionOrderItems.Add(row)
            Next
            For Each failure In ExtensionService.Failures
                ExtensionRows.Add(New ExtensionRow With {
                    .Name = IO.Path.GetFileName(failure.FilePath),
                    .ErrorText = LocalizationService.T("Nicht geladen:") & " " & failure.ErrorText})
            Next
            Me.RaisePropertyChanged(NameOf(HasExtensionRows))
            Me.RaisePropertyChanged(NameOf(HasExtensionOrderItems))
        End Sub

        ''' <summary>Nimmt eine Erweiterungsdatei auf, aufgerufen nach dem Dateiwaehler der Seite.</summary>
        Public Sub InstallExtension(path As String)
            If String.IsNullOrWhiteSpace(path) Then Return
            Dim result = ExtensionService.Install(path)
            If result.Succeeded Then
                ExtensionMessage = String.Format(CultureInfo.CurrentCulture,
                                                 LocalizationService.T("Geladen: {0}. Das Werkzeug Erweiterungen im Editor zeigt ihre Regler."),
                                                 result.Definition.Name)
            Else
                ExtensionMessage = LocalizationService.T("Nicht geladen:") & " " & result.ErrorText
            End If
        End Sub

        Private Async Function RemoveExtensionAsync(row As ExtensionRow) As Task
            If row Is Nothing OrElse row.Definition Is Nothing OrElse Not row.CanRemove Then Return
            If _mainVm IsNot Nothing Then
                Dim confirmed = Await _mainVm.ShowConfirmAsync(
                    LocalizationService.T("Entfernen"),
                    row.Name & vbLf & LocalizationService.T("Bilder, die sie nutzen, behalten ihre Einstellungen. Sie wirken wieder, sobald die Erweiterung erneut geladen ist."),
                    LocalizationService.T("Entfernen"), LocalizationService.T("Abbrechen"))
                If Not confirmed Then Return
            End If
            ExtensionMessage = If(ExtensionService.Remove(row.Definition.Id), "", LocalizationService.T("Entfernen fehlgeschlagen"))
        End Function

    End Class

End Namespace
