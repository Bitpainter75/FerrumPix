Imports System
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Die Einstellung "Ordner für Anpassungen": wo die im Editor gespeicherten
    ''' Anpassungen als Dateien liegen (AdjustmentPresetStore). Sie gilt sofort, ohne den
    ''' Speichern-Knopf der Einstellungen: mit dem Wechsel koennen Dateien umziehen, und das
    ''' laesst sich nicht verwerfen.</summary>
    Partial Public Class SettingsViewModel

        Private _adjustmentPresetFolderStatus As String = ""

        ''' <summary>Zurueck zur Vorgabe im Datenordner.</summary>
        Public ReadOnly Property ResetAdjustmentPresetFolderCommand As ICommand =
            ReactiveCommand.CreateFromTask(Function() ChangeAdjustmentPresetFolderAsync(""))

        ''' <summary>Der geltende Ordner, wie er dasteht.</summary>
        Public ReadOnly Property AdjustmentPresetFolderText As String
            Get
                Return AdjustmentPresetStore.ConfiguredRoot
            End Get
        End Property

        ''' <summary>Ist ein eigener Ordner gesetzt? Sonst gilt die Vorgabe im Datenordner.</summary>
        Public ReadOnly Property IsAdjustmentPresetFolderCustom As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(AppSettingsService.Load().AdjustmentPresetFolder)
            End Get
        End Property

        ''' <summary>Was der letzte Wechsel getan hat.</summary>
        Public ReadOnly Property AdjustmentPresetFolderStatus As String
            Get
                Return _adjustmentPresetFolderStatus
            End Get
        End Property

        Public ReadOnly Property HasAdjustmentPresetFolderStatus As Boolean
            Get
                Return _adjustmentPresetFolderStatus.Length > 0
            End Get
        End Property

        ''' <summary>Legt den Ordner der Anpassungen fest; leer heisst die Vorgabe. Liegen im
        ''' bisherigen Ordner Anpassungen, wird gefragt, ob sie mitziehen. Sie ziehen VOR dem
        ''' Umstellen um, und eine Datei verlaesst den alten Ordner erst, wenn sie im neuen belegt
        ''' geschrieben ist (AdjustmentPresetStore.MoveAllTo). Die Dateien werden im Hintergrund
        ''' bewegt, beide Ordner koennen entfernt liegen. Liegt der eine Ordner im anderen, lehnt
        ''' MoveAllTo das Mitziehen ab; dann bleibt auch die Einstellung, wie sie war, und die
        ''' Zeile sagt warum.</summary>
        ''' <param name="moveExisting">Die Antwort auf die Frage, wenn sie nicht gestellt werden
        ''' soll (Pruefstand).</param>
        Public Async Function ChangeAdjustmentPresetFolderAsync(newFolder As String,
                                                                Optional moveExisting As Boolean? = Nothing) As Task
            Dim wanted = AppSettingsService.NormalizeFolderPath(newFolder)
            Dim oldRoot = AdjustmentPresetStore.ConfiguredRoot
            Dim newRoot = If(wanted.Length = 0, AdjustmentPresetStore.DefaultRoot, wanted)
            If String.Equals(IO.Path.GetFullPath(oldRoot).TrimEnd(IO.Path.DirectorySeparatorChar),
                             IO.Path.GetFullPath(newRoot).TrimEnd(IO.Path.DirectorySeparatorChar), StringComparison.Ordinal) Then
                ' Derselbe Ordner, nur anders eingetragen: die Einstellung nachziehen, nichts bewegen.
                AppSettingsService.SaveAdjustmentPresetFolder(wanted)
                RaiseAdjustmentPresetFolderChanged("")
                Return
            End If
            Dim source As New AdjustmentPresetStore(oldRoot)
            Dim target As New AdjustmentPresetStore(newRoot)
            Dim existing = Await Task.Run(Function() If(source.IsReachable(), source.ReadAll().Presets.Count, 0))
            Dim moveAlong = False
            If existing > 0 Then
                If moveExisting.HasValue Then
                    moveAlong = moveExisting.Value
                ElseIf _mainVm IsNot Nothing Then
                    moveAlong = Await _mainVm.ShowConfirmAsync(
                        LocalizationService.T("Ordner für Anpassungen"),
                        String.Format(LocalizationService.T("Im bisherigen Ordner liegen {0} Anpassung(en). Sollen sie in den neuen Ordner mitziehen? Sonst bleiben sie im bisherigen Ordner liegen und erscheinen nicht mehr in der Liste."), existing),
                        "Mitziehen", "Dort lassen")
                End If
            End If
            Dim status = ""
            If moveAlong Then
                Dim outcome = Await Task.Run(Function() source.MoveAllTo(target))
                If outcome.Refused Then
                    ' Der eine Ordner liegt im anderen: nichts bewegt, und der Ordner bleibt, wie er
                    ' war. Umgestellt wuerde sonst auf einen Bestand, der den alten als Unterordner
                    ' traegt oder in ihm steckt.
                    RaiseAdjustmentPresetFolderChanged(LocalizationService.T("Der neue Ordner liegt im bisherigen oder der bisherige im neuen. Es wurde nichts verschoben, und der Ordner bleibt, wie er war."))
                    Return
                End If
                status = String.Format(LocalizationService.T("{0} Anpassung(en) in den neuen Ordner verschoben"), outcome.Moved)
                If outcome.KeptBeside > 0 Then
                    status &= ", " & String.Format(LocalizationService.T("{0} davon gab es im neuen Ordner schon mit anderem Inhalt, sie stehen jetzt als eigene Anpassung daneben"), outcome.KeptBeside)
                End If
                If outcome.Failed > 0 Then
                    status &= ", " & String.Format(LocalizationService.T("{0} ließen sich nicht verschieben und liegen weiter im bisherigen Ordner"), outcome.Failed)
                End If
            End If
            AppSettingsService.SaveAdjustmentPresetFolder(wanted)
            RaiseAdjustmentPresetFolderChanged(status)
        End Function

        Private Sub RaiseAdjustmentPresetFolderChanged(status As String)
            ' Jede Liste liest danach neu: der gemerkte Stand gehoert dem alten Ordner.
            AdjustmentPresetStore.Invalidate()
            _adjustmentPresetFolderStatus = If(status, "")
            Me.RaisePropertyChanged(NameOf(AdjustmentPresetFolderText))
            Me.RaisePropertyChanged(NameOf(IsAdjustmentPresetFolderCustom))
            Me.RaisePropertyChanged(NameOf(AdjustmentPresetFolderStatus))
            Me.RaisePropertyChanged(NameOf(HasAdjustmentPresetFolderStatus))
        End Sub

    End Class

End Namespace
