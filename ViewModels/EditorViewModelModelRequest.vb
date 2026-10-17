Imports System
Imports System.Linq
Imports System.Threading.Tasks
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Die Rueckfrage, wenn eine Funktion ein Modell braucht, das noch nicht
    ''' heruntergeladen ist.
    '''
    ''' Die Knoepfe und Regler dieser Funktionen sind deshalb NICHT mehr grau, solange nur die
    ''' Modelldatei fehlt: grau laesst sich nichts versuchen, und der Versuch ist der Moment, in dem
    ''' die Frage Sinn ergibt. Grau bleiben sie nur ohne Modell-Laufzeit, denn dann hilft auch der
    ''' Download nicht (<see cref="CanRequestModels"/>).
    '''
    ''' Gefragt und geholt wird in SettingsViewModel.RequestModelsAsync, damit der Einstellungsdialog
    ''' denselben Stand zeigt. Der Download dort ist MODAL: waehrend er laeuft, aendert sich im Editor
    ''' nichts, und die ausgeloeste Aktion laeuft danach einfach weiter. Die Pruefungen der
    ''' Funktionen selbst (IsBokehAvailable, CanDenoiseWithModel ...) bleiben, wie sie sind: sie
    ''' fragen nach der Datei und schuetzen jeden Weg, der an der Rueckfrage vorbei hineinkommt.</summary>
    Partial Public Class EditorViewModel

        ''' <summary>Laesst sich ein fehlendes Modell ueberhaupt holen und ausfuehren?</summary>
        Public ReadOnly Property CanRequestModels As Boolean
            Get
                Return AiModelService.RuntimeAvailable
            End Get
        End Property

        ''' <summary>Objekt entfernen anbieten: mit Markierung, und wenn das Modell da ist oder
        ''' sich holen laesst.</summary>
        Public ReadOnly Property CanRequestObjectRemoval As Boolean
            Get
                Return _hasActiveSelection AndAlso (ObjectRemovalService.Available OrElse AiModelService.RuntimeAvailable)
            End Get
        End Property

        ''' <summary>Fragt nach, wenn eine der Dateien fehlt, und holt sie auf ein Ja hin. True, wenn
        ''' danach alles da ist.</summary>
        ''' <param name="featureName">Die Funktion, schon uebersetzt.</param>
        Private Async Function EnsureModelsAsync(featureName As String, ParamArray keys As String()) As Task(Of Boolean)
            If AiModelService.RuntimeAvailable AndAlso
               keys.All(Function(k) Not String.IsNullOrEmpty(AiModelService.BestFile(k))) Then Return True
            Dim settings = _mainVm?.Settings
            If settings Is Nothing Then Return False
            Dim ok = Await settings.RequestModelsAsync(featureName, keys)
            If ok Then
                StatusText = LocalizationService.T("Modell heruntergeladen")
                RefreshModelAvailability()
            End If
            Return ok
        End Function

        ' ── Regler, die eine Vorschau mit Modell anstossen ───────────────────────
        '
        ' Bei Tiefen-Unschaerfe und Nebel ist der Versuch eine Reglerbewegung. Waehrend man zieht,
        ' kommen Dutzende Aenderungen; gefragt wird einmal, solange die Frage offen ist, zaehlt keine
        ' weitere Bewegung.
        '
        ' WURDE NICHT HERUNTERGELADEN, GEHEN DIE REGLER DER GRUPPE ZURUECK, und der naechste
        ' Versuch fragt wieder. Ohne Modell hat der Regler nichts bewirkt, und ein Wert, der dasteht,
        ' ohne zu wirken, sieht nach einer Einstellung aus, die es nicht gibt. Nach einem Download
        ' bleiben die Werte stehen, und die Vorschau rechnet: Frage und Download sind modal, das
        ' Werkzeug ist also noch dasselbe.

        Private _depthRequestOpen As Boolean

        ''' <summary>Vor einer Vorschau, die die Tiefe braucht. Kehrt sofort zurueck.</summary>
        ''' <param name="resetGroup">Setzt die Regler der Gruppe zurueck, wenn nicht heruntergeladen wurde.</param>
        ''' <param name="preview">Stoesst die Vorschau an, wenn das Modell danach da ist.</param>
        Private Sub RequestDepthForPreview(featureName As String, resetGroup As Action, preview As Action)
            If DepthMapService.Available OrElse Not AiModelService.RuntimeAvailable OrElse _depthRequestOpen Then Return
            _depthRequestOpen = True
            Dim ignoriert = RequestDepthForPreviewAsync(featureName, resetGroup, preview)
        End Sub

        Private Async Function RequestDepthForPreviewAsync(featureName As String, resetGroup As Action, preview As Action) As Task
            Try
                If Await EnsureModelsAsync(featureName, DepthMapService.ModelFile) Then
                    preview?.Invoke()
                Else
                    resetGroup?.Invoke()
                End If
            Finally
                _depthRequestOpen = False
            End Try
        End Function

        ' ── Die Einstiege mit Rueckfrage ─────────────────────────────────────────

        Private Async Function SetMaskModeWithModelAsync(mode As String) As Task
            Select Case mode
                Case "Tiefe"
                    If Not Await EnsureModelsAsync(LocalizationService.T("Maske nach Tiefe"), DepthMapService.ModelFile) Then Return
                Case "Objekt"
                    If Not Await EnsureModelsAsync(LocalizationService.T("Objektauswahl"), SubjectMaskService.EncoderFile, SubjectMaskService.DecoderFile) Then Return
            End Select
            MaskMode = mode
        End Function

        Private Async Function SetSelectionModeWithModelAsync(mode As String) As Task
            If String.Equals(mode, "Subject", StringComparison.Ordinal) AndAlso
               Not Await EnsureModelsAsync(LocalizationService.T("Objektauswahl"), SubjectMaskService.EncoderFile, SubjectMaskService.DecoderFile) Then Return
            SetSelectionMode(mode)
        End Function

        Private Async Function RemoveObjectWithModelAsync() As Task
            If Not Await EnsureModelsAsync(LocalizationService.T("Objekt entfernen"), ObjectRemovalService.ModelFile) Then Return
            ApplyObjectRemoval()
        End Function

        Private Async Function DenoiseWithModelAsync(kind As DenoiseModelService.DenoiseKind) As Task
            Dim key = If(kind = DenoiseModelService.DenoiseKind.Fast, DenoiseModelService.FastModelFile, DenoiseModelService.ModelFile)
            If Not Await EnsureModelsAsync(LocalizationService.T("Entrauschen"), key) Then Return
            ApplyModelDenoise(kind)
        End Function

        Private Async Function UpscaleWithModelAsync() As Task
            Dim model = CurrentUpscaleModel()
            If model Is Nothing Then Return
            If Not Await EnsureModelsAsync(LocalizationService.T("Hochskalieren"), model.Key) Then Return
            ApplyModelUpscale()
        End Function

        Private Async Function ApplyBokehWithModelAsync() As Task
            If Not Await EnsureModelsAsync(LocalizationService.T("Tiefen-Unschärfe"), DepthMapService.ModelFile) Then Return
            Await ApplyBokeh()
        End Function

        Private Async Function ApplyFogWithModelAsync() As Task
            If Not Await EnsureModelsAsync(LocalizationService.T("Nebel nach Tiefe"), DepthMapService.ModelFile) Then Return
            Await ApplyFog()
        End Function

    End Class

End Namespace
