Imports System
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Der Einrichtungsassistent beim ersten Start (SetupAssistantViewModel): Oeffnen,
    ''' Schliessen und ob er schon gezeigt wurde.</summary>
    Partial Public Class MainWindowViewModel

        Private _setupAssistant As SetupAssistantViewModel

        ''' <summary>Der laufende Assistent, sonst Nothing. Das Fenster baut seine Ansicht erst,
        ''' wenn er zum ersten Mal erscheint.</summary>
        Public ReadOnly Property SetupAssistant As SetupAssistantViewModel
            Get
                Return _setupAssistant
            End Get
        End Property

        Public ReadOnly Property IsSetupAssistantOpen As Boolean
            Get
                Return _setupAssistant IsNot Nothing
            End Get
        End Property

        ''' <summary>Darf der Assistent beim Start ueberhaupt erscheinen? Der Pruefstand schaltet das
        ''' ab: er laeuft mit frischer Konfiguration und baut echte Hauptfenster, und der Assistent
        ''' laege dort ueber allem und behielte die Tasten fuer sich.</summary>
        Public Shared Property SetupAssistantOnStartAllowed As Boolean = True

        ''' <summary>Soll der Assistent beim Start erscheinen? Einmal fuer jede Installation, auch
        ''' fuer bestehende, die den Wert noch nicht kennen.</summary>
        Public Shared Function ShouldShowSetupAssistant() As Boolean
            Return SetupAssistantOnStartAllowed AndAlso Not AppSettingsService.Load().SetupAssistantDone
        End Function

        ''' <summary>Zeigt den Assistenten, beim ersten Start oder ueber "Einrichtung erneut starten"
        ''' in den Einstellungen.</summary>
        Public Sub ShowSetupAssistant()
            If _setupAssistant IsNot Nothing OrElse Settings Is Nothing Then Return
            _setupAssistant = New SetupAssistantViewModel(Me)
            Me.RaisePropertyChanged(NameOf(SetupAssistant))
            Me.RaisePropertyChanged(NameOf(IsSetupAssistantOpen))
        End Sub

        ''' <summary>Schliesst den Assistenten, ob fertig oder uebersprungen, und merkt sich, dass er
        ''' gezeigt wurde. Was darin eingestellt wurde, ist schon gespeichert. Laedt noch ein
        ''' Modell, wird vorher gefragt, wie beim Verlassen der Einstellungen: einen Download
        ''' ausserhalb der Oberflaeche, die ihn gestartet hat, gibt es nicht.</summary>
        Public Async Sub CloseSetupAssistant(openSettings As Boolean)
            Try
                If _setupAssistant Is Nothing Then Return
                ' Der eigene Download der Modellseite: ConfirmCancelModelDownloadsAsync braeche nur
                ' die Downloads der Einstellungen ab, diesen nicht. Also selbst fragen und warten,
                ' bis er steht.
                If _setupAssistant.IsDownloadingModels Then
                    Dim cancelIt = Await ShowConfirmAsync(LocalizationService.T("Einrichtung"),
                        LocalizationService.T("Es wird noch ein Modell heruntergeladen. Den Download abbrechen und die Einrichtung schließen?"),
                        "Download abbrechen", "Weiter laden")
                    If Not cancelIt OrElse _setupAssistant Is Nothing Then Return
                    BeginModalModelDownload(LocalizationService.T("Download wird abgebrochen…"))
                    Try
                        Await _setupAssistant.CancelModelDownloadAndWaitAsync()
                    Finally
                        EndModalModelDownload()
                    End Try
                End If
                If Not Await ConfirmCancelModelDownloadsAsync(LocalizationService.T("Einrichtung"),
                        LocalizationService.T("Es wird noch ein Modell heruntergeladen. Den Download abbrechen und die Einrichtung schließen?")) Then Return
                FinishSetupAssistant(openSettings)
            Catch ex As Exception
                DiagnosticLogService.LogException("MainWindowViewModel.CloseSetupAssistant", ex)
            End Try
        End Sub

        Private Sub FinishSetupAssistant(openSettings As Boolean)
            If _setupAssistant Is Nothing Then Return
            _setupAssistant = Nothing
            AppSettingsService.SaveSetupAssistantDone(True)
            Me.RaisePropertyChanged(NameOf(SetupAssistant))
            Me.RaisePropertyChanged(NameOf(IsSetupAssistantOpen))
            If openSettings Then Me.OpenSettings()
        End Sub

    End Class

End Namespace
