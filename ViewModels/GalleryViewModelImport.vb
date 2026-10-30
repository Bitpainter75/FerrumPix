Imports System
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ' Der Einstieg "Von Speicherkarte einlesen" in der Galerie: Dialog und Lauf stehen im
    ' Hauptfenster (MainWindowViewModelImport), hier nur der Knopf und was danach passiert.
    Partial Public Class GalleryViewModel

        Private _importFromCardCommand As ICommand
        Public ReadOnly Property ImportFromCardCommand As ICommand
            Get
                If _importFromCardCommand Is Nothing Then
                    _importFromCardCommand = ReactiveCommand.CreateFromTask(AddressOf ImportFromCardAsync)
                End If
                Return _importFromCardCommand
            End Get
        End Property

        ''' <summary>Dialog, Lauf, Meldung. Danach steht die Galerie im Ordner der ersten
        ''' eingelesenen Aufnahme, damit man gleich mit dem Sichten anfangen kann.</summary>
        Public Async Function ImportFromCardAsync() As Task
            If _mainVm Is Nothing Then Return
            Dim suggested = If(CurrentFolder IsNot Nothing AndAlso IO.Directory.Exists(CurrentFolder), CurrentFolder, "")
            Dim request = Await _mainVm.ShowImportDialogAsync(suggested)
            If request Is Nothing Then Return
            Dim result = Await _mainVm.RunImportAsync(request)
            If result Is Nothing Then Return

            Dim text = String.Format(LocalizationService.T("Eingelesen: {0} neu, {1} schon vorhanden"), result.Copied, result.AlreadyPresent)
            If result.Failed.Count > 0 Then
                text &= ", " & String.Format(LocalizationService.T("{0} fehlgeschlagen"), result.Failed.Count)
            End If
            If result.Cancelled Then text &= ". " & LocalizationService.T("Abgebrochen")
            StatusText = text
            If result.Failed.Count > 0 Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Nicht alles eingelesen"),
                                               text & Environment.NewLine & String.Join(", ", result.Failed))
            End If
            If Not String.IsNullOrEmpty(result.FirstTargetFolder) AndAlso IO.Directory.Exists(result.FirstTargetFolder) Then
                Await NavigateToFolderAsync(result.FirstTargetFolder)
            End If
        End Function

    End Class

End Namespace
