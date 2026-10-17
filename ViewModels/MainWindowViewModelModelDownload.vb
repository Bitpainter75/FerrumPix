Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Der Dialog "Modell herunterladen": Frage, Grafikkarte, Fortschritt und Ergebnis
    ''' in EINEM Fenster, das vom Fragen bis zum Ergebnis offen bleibt.
    '''
    ''' Er ist MODAL, und darauf baut der Aufrufer: waehrend er offen ist, faengt er die Maus, der
    ''' Tastenhandler laesst nur ESC und RETURN zu ihm durch, und ShowDialogAsync oeffnet keinen
    ''' anderen Dialog (_modalModelDownload). Danach steht also alles noch so da wie beim Klick, und
    ''' die Funktion, die das Modell brauchte, laeuft einfach weiter.
    '''
    ''' Die GRAFIKKARTE wird angeboten, wenn das Modell auf ihr rechnen darf, eine Karte gefunden ist,
    ''' die Beschleunigung aber aus ist, und die Karte nicht schon als untauglich bekannt ist. Eine
    ''' fruehere bestandene Probe schliesst das Angebot NICHT aus: gerade dann laeuft das Modell dort
    ''' nachweislich, und ausgeschaltet ist die Beschleunigung womoeglich nur, weil sie nie jemand
    ''' eingeschaltet hat (so stand es auf dem Arbeitsrechner, und der Haken erschien nie). Vorbelegt
    ''' ist er bei einer eigenen Karte und bei einer, die schon bestanden hat; eine eingebaute, noch
    ''' nicht gepruefte teilt sich den Speicher mit der Oberflaeche (siehe
    ''' AppSettings.GpuAccelerationEnabled).
    '''
    ''' Eingeschaltet wird erst NACH einer bestandenen Probe, und ihr Ergebnis steht sichtbar im
    ''' Dialog. Eine Karte, auf der kein Modell anlaeuft, einzuschalten hiesse, jeden kommenden
    ''' Modelllauf erst scheitern zu lassen.</summary>
    Partial Public Class MainWindowViewModel

        Private Enum ModelDialogPhase
            Asking
            Downloading
            Probing
            Done
        End Enum

        Private _modelDialogPhase As ModelDialogPhase = ModelDialogPhase.Done
        Private _modelDialogStart As TaskCompletionSource(Of Boolean)
        Private _modelDialogCancellation As CancellationTokenSource
        Private _dialogModelOfferGpu As Boolean
        Private _dialogModelUseGpu As Boolean
        Private _dialogModelGpuText As String = ""
        Private _dialogModelProgress As Double
        Private _dialogModelStatus As String = ""
        Private _dialogModelFailed As Boolean

        Public ReadOnly Property DialogShowsModelDownload As Boolean
            Get
                Return _dialogKind = AppDialogKind.ModelDownload
            End Get
        End Property

        ''' <summary>Der Haken fuer die Grafikkarte erscheint nur, wenn sie angeboten wird.</summary>
        Public ReadOnly Property DialogModelOfferGpu As Boolean
            Get
                Return _dialogModelOfferGpu
            End Get
        End Property

        Public Property DialogModelUseGpu As Boolean
            Get
                Return _dialogModelUseGpu
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogModelUseGpu, value)
            End Set
        End Property

        ''' <summary>Welche Karte gefunden ist, und nach der Probe, ob ein Modell darauf laeuft.</summary>
        Public ReadOnly Property DialogModelGpuText As String
            Get
                Return _dialogModelGpuText
            End Get
        End Property

        Public ReadOnly Property IsDialogModelAsking As Boolean
            Get
                Return _modelDialogPhase = ModelDialogPhase.Asking
            End Get
        End Property

        Public ReadOnly Property IsDialogModelDownloading As Boolean
            Get
                Return _modelDialogPhase = ModelDialogPhase.Downloading
            End Get
        End Property

        Public ReadOnly Property DialogModelProgress As Double
            Get
                Return _dialogModelProgress
            End Get
        End Property

        ''' <summary>Was gerade geschieht oder wie es ausging, unter dem Fortschritt.</summary>
        Public ReadOnly Property DialogModelStatus As String
            Get
                Return _dialogModelStatus
            End Get
        End Property

        Public ReadOnly Property HasDialogModelStatus As Boolean
            Get
                Return Not String.IsNullOrEmpty(_dialogModelStatus)
            End Get
        End Property

        Public ReadOnly Property DialogModelFailed As Boolean
            Get
                Return _dialogModelFailed
            End Get
        End Property

        ''' <summary>Nur beim Fragen und am Ende ist der Hauptknopf frei; waehrend Download und Probe
        ''' gibt es nichts zu bestaetigen.</summary>
        Private ReadOnly Property IsModelDialogPrimaryEnabled As Boolean
            Get
                Return _modelDialogPhase = ModelDialogPhase.Asking OrElse _modelDialogPhase = ModelDialogPhase.Done
            End Get
        End Property

        ''' <summary>Fragt nach den Dateien, holt sie auf ein Ja hin, prueft auf Wunsch die
        ''' Grafikkarte und zeigt das Ergebnis. True, wenn danach alle Dateien vorliegen.</summary>
        ''' <param name="featureName">Die Funktion, schon uebersetzt (Titel des Dialogs).</param>
        ''' <param name="message">Die Frage samt Groesse und Bedingungen, schon uebersetzt.</param>
        Public Async Function ShowModelDownloadDialogAsync(featureName As String, message As String,
                                                           missing As IList(Of AiModelService.ModelEntry)) As Task(Of Boolean)
            If missing Is Nothing OrElse missing.Count = 0 Then Return True
            If _modalModelDownload Then Return False

            Dim settingsVm = Settings
            Dim offerGpu = settingsVm IsNot Nothing AndAlso
                           AiModelService.RuntimeAvailable AndAlso
                           GpuAccelerationService.Available AndAlso
                           Not settingsVm.GpuAccelerationEnabled AndAlso
                           GpuAccelerationService.ProbeState <> GpuAccelerationService.ProbeResult.Failed AndAlso
                           missing.Any(Function(e) e.GpuAllowed)
            _dialogModelOfferGpu = offerGpu
            _dialogModelUseGpu = offerGpu AndAlso
                (GpuAccelerationService.IsDiscrete OrElse
                 GpuAccelerationService.ProbeState = GpuAccelerationService.ProbeResult.Works)
            _dialogModelGpuText = If(offerGpu, settingsVm.GpuAccelerationStatusText, "")
            _dialogModelProgress = 0
            _dialogModelStatus = ""
            _dialogModelFailed = False
            SetModelDialogPhase(ModelDialogPhase.Asking)
            RaiseModelDialogState()

            Dim start = New TaskCompletionSource(Of Boolean)()
            _modelDialogStart = start
            Dim closed = ShowDialogAsync(AppDialogKind.ModelDownload, featureName, message, "",
                                         "Herunterladen", "Abbrechen")
            ' ERST nach dem Oeffnen: die Sperre laesst ShowDialogAsync sonst auch diesen Dialog nicht zu.
            _modalModelDownload = True
            Try
                Dim first = Await Task.WhenAny(start.Task, closed)
                If first IsNot start.Task OrElse Not start.Task.Result Then Return False

                ' ── Download ──
                _modelDialogCancellation = New CancellationTokenSource()
                SetModelDialogPhase(ModelDialogPhase.Downloading)
                SetModelDialogStatus(String.Format(LocalizationService.T("Modell wird heruntergeladen: {0} %"), 0), False)
                Dim failure = Await settingsVm.DownloadModelEntriesAsync(missing,
                    Sub(share)
                        ' Progress meldet ASYNCHRON: eine Meldung, die erst nach dem Ende ankommt,
                        ' setzte sonst das Ergebnis oder den Fehler wieder auf "wird heruntergeladen".
                        If _modelDialogPhase <> ModelDialogPhase.Downloading Then Return
                        _dialogModelProgress = share
                        Me.RaisePropertyChanged(NameOf(DialogModelProgress))
                        SetModelDialogStatus(String.Format(LocalizationService.T("Modell wird heruntergeladen: {0} %"),
                                                           CInt(Math.Round(share * 100.0))), False)
                    End Sub,
                    _modelDialogCancellation.Token)
                Dim ok = failure Is Nothing AndAlso
                         missing.All(Function(e) Not String.IsNullOrEmpty(AiModelService.BestFile(e.Key)))
                ' Die Download-Phase endet HIER, vor dem ersten Ergebnistext: ab jetzt laufen
                ' Nachzuegler der Fortschrittsmeldung ins Leere.
                SetModelDialogPhase(ModelDialogPhase.Probing)

                If Not ok Then
                    SetModelDialogStatus(If(failure, LocalizationService.T("Das Herunterladen ist fehlgeschlagen")), True)
                Else
                    _dialogModelProgress = 1
                    Me.RaisePropertyChanged(NameOf(DialogModelProgress))
                    SetModelDialogStatus(LocalizationService.T("Modell heruntergeladen"), False)
                    ' ── Grafikkarte ──
                    If offerGpu AndAlso _dialogModelUseGpu Then
                        SetModelDialogPhase(ModelDialogPhase.Probing)
                        SetModelDialogStatus(LocalizationService.T("Modell heruntergeladen. Die Grafikkarte wird geprüft…"), False)
                        Dim probe = Await Task.Run(Function() GpuAccelerationService.Probe())
                        If probe = GpuAccelerationService.ProbeResult.Works Then settingsVm.EnableGpuAccelerationAfterProbe()
                        _dialogModelGpuText = settingsVm.GpuAccelerationStatusText &
                            If(probe = GpuAccelerationService.ProbeResult.Works,
                               " " & LocalizationService.T("Die Grafikbeschleunigung ist eingeschaltet."),
                               " " & LocalizationService.T("Die Grafikbeschleunigung bleibt aus, die Modelle rechnen auf dem Prozessor."))
                        Me.RaisePropertyChanged(NameOf(DialogModelGpuText))
                        SetModelDialogStatus(LocalizationService.T("Modell heruntergeladen"), False)
                    End If
                End If

                ' ── Ergebnis ──
                _dialogModelOfferGpu = offerGpu AndAlso _dialogModelUseGpu
                Me.RaisePropertyChanged(NameOf(DialogModelOfferGpu))
                SetModelDialogPhase(ModelDialogPhase.Done)
                DialogConfirmText = LocalizationService.T("OK")
                DialogCancelText = ""
                Await closed
                Return ok
            Finally
                _modalModelDownload = False
                _modelDialogCancellation?.Dispose()
                _modelDialogCancellation = Nothing
                _modelDialogStart = Nothing
                SetModelDialogPhase(ModelDialogPhase.Done)
            End Try
        End Function

        ''' <summary>RETURN oder der Hauptknopf. Beim Fragen geht es los, am Ende schliesst er.</summary>
        Private Sub ConfirmModelDialog()
            Select Case _modelDialogPhase
                Case ModelDialogPhase.Asking
                    _modelDialogStart?.TrySetResult(True)
                Case ModelDialogPhase.Done
                    CompleteDialog("OK")
            End Select
        End Sub

        ''' <summary>ESC oder Abbrechen. Beim Fragen schliesst er, waehrend des Downloads bricht er
        ''' ihn ab und der Dialog zeigt den Abbruch; die Probe laeuft zu Ende, sie dauert Sekunden.</summary>
        Private Sub CancelModelDialog()
            Select Case _modelDialogPhase
                Case ModelDialogPhase.Asking
                    _modelDialogStart?.TrySetResult(False)
                    CompleteDialog(Nothing)
                Case ModelDialogPhase.Downloading
                    Try
                        _modelDialogCancellation?.Cancel()
                    Catch ex As ObjectDisposedException
                    End Try
                Case ModelDialogPhase.Done
                    CompleteDialog("OK")
            End Select
        End Sub

        Private Sub SetModelDialogPhase(phase As ModelDialogPhase)
            _modelDialogPhase = phase
            Me.RaisePropertyChanged(NameOf(IsDialogModelAsking))
            Me.RaisePropertyChanged(NameOf(IsDialogModelDownloading))
            Me.RaisePropertyChanged(NameOf(IsDialogPrimaryEnabled))
        End Sub

        Private Sub SetModelDialogStatus(text As String, failed As Boolean)
            _dialogModelStatus = If(text, "")
            _dialogModelFailed = failed
            Me.RaisePropertyChanged(NameOf(DialogModelStatus))
            Me.RaisePropertyChanged(NameOf(HasDialogModelStatus))
            Me.RaisePropertyChanged(NameOf(DialogModelFailed))
        End Sub

        Private Sub RaiseModelDialogState()
            For Each n In {NameOf(DialogModelOfferGpu), NameOf(DialogModelUseGpu), NameOf(DialogModelGpuText),
                           NameOf(DialogModelProgress), NameOf(DialogModelStatus), NameOf(HasDialogModelStatus),
                           NameOf(DialogModelFailed)}
                Me.RaisePropertyChanged(n)
            Next
        End Sub

    End Class

End Namespace
