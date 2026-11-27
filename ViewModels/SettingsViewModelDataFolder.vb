Imports System
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Die Einstellung "Datenordner" auf der Seite "Katalog": wo Katalog,
    ''' Vorschaubilder, Modelle, Erweiterungen, Anpassungen, Favoriten und Suchlisten liegen
    ''' (AppFolders). Ein Wechsel wird vorgemerkt und beim naechsten Start ausgefuehrt, bevor
    ''' irgendetwas davon geoeffnet ist; bis dahin bleibt alles, wo es ist. Der Ort gehoert NICHT zu
    ''' Abbrechen und Werkseinstellung der Seite: er aendert sich nur zusammen mit den Daten.</summary>
    Partial Public Class SettingsViewModel

        Public ReadOnly Property ResetDataFolderCommand As ICommand =
            ReactiveCommand.CreateFromTask(Function() ChangeDataFolderAsync(AppFolders.DefaultDataRoot))

        Public ReadOnly Property CancelDataFolderChangeCommand As ICommand =
            ReactiveCommand.Create(Sub()
                                       AppFolders.CancelDataFolderChange()
                                       RaiseDataFolderChanged()
                                   End Sub)

        ''' <summary>Wo die Daten gerade liegen.</summary>
        Public ReadOnly Property DataFolderText As String
            Get
                Return AppFolders.DataRoot
            End Get
        End Property

        ''' <summary>Ob ein eigener Ort gilt oder vorgemerkt ist; dann gibt es den Knopf zurueck.</summary>
        Public ReadOnly Property HasCustomDataFolder As Boolean
            Get
                Return AppFolders.ConfiguredDataFolder.Length > 0
            End Get
        End Property

        Public ReadOnly Property HasPendingDataFolder As Boolean
            Get
                Return AppFolders.PendingDataFolder.Length > 0
            End Get
        End Property

        ''' <summary>Was beim naechsten Start geschieht.</summary>
        Public ReadOnly Property DataFolderPendingText As String
            Get
                Dim pending = AppFolders.PendingDataFolder
                If pending.Length = 0 Then Return ""
                If AppFolders.PendingDataFolderIsAdopt Then
                    Return String.Format(LocalizationService.T("Beim nächsten Start verwendet FerrumPix den Datenordner in {0}."), pending)
                End If
                Return String.Format(LocalizationService.T("Beim nächsten Start zieht der Datenordner nach {0} um. Je nach Größe kann das dauern; ein Fenster zeigt so lange den Fortschritt, abbrechen lässt sich der Umzug nicht."), pending)
            End Get
        End Property

        ''' <summary>Steht da, solange der gewaehlte Ort nicht erreichbar ist.</summary>
        Public ReadOnly Property DataFolderFallbackText As String
            Get
                If Not AppFolders.IsDataRootFallback Then Return ""
                Return String.Format(LocalizationService.T("Der Datenordner {0} ist nicht erreichbar. Bis er wieder da ist, arbeitet FerrumPix ohne Katalog, und Modelle, Erweiterungen und gespeicherte Anpassungen fehlen."),
                                     AppFolders.ConfiguredDataFolder)
            End Get
        End Property

        ''' <summary>Laesst sich ein anderer Datenordner waehlen? Nicht mit festen Fotoordnern
        ''' (FolderRoots, im Behaelter /photos). Entschieden: dort ist der Datenordner ein eigenes
        ''' Laufwerk (/data), das der Behaelter vorgibt, und der Waehler liesse ohnehin nur Orte in
        ''' den Fotoordnern zu; Katalog und Vorschaubilder zwischen den Fotos waeren der einzige
        ''' moegliche Wechsel und keiner, den jemand will. Zurueck an den Standardort bleibt
        ''' moeglich, falls frueher ein anderer gewaehlt war.</summary>
        Public ReadOnly Property CanChooseDataFolder As Boolean
            Get
                Return Not FolderRoots.IsActive
            End Get
        End Property

        ''' <summary>Wirkt der Schalter "Dateioperationen ausserhalb des persoenlichen Ordners"? Mit
        ''' festen Fotoordnern nicht: dann gelten genau sie (FileOperationPolicy.IsInAllowedArea).</summary>
        Public ReadOnly Property CanChangeFileOperationArea As Boolean
            Get
                Return Not FolderRoots.IsActive
            End Get
        End Property

        ''' <summary>Gelten feste Fotoordner? Dann zeigen die Einstellungen, warum Datenordner und
        ''' der Schalter fuer Dateiarbeit ausserhalb des persoenlichen Ordners nicht wirken.</summary>
        Public ReadOnly Property HasFolderRoots As Boolean
            Get
                Return FolderRoots.IsActive
            End Get
        End Property

        Friend Sub RaiseDataFolderChanged()
            Me.RaisePropertyChanged(NameOf(DataFolderText))
            Me.RaisePropertyChanged(NameOf(HasCustomDataFolder))
            Me.RaisePropertyChanged(NameOf(HasPendingDataFolder))
            Me.RaisePropertyChanged(NameOf(DataFolderPendingText))
            Me.RaisePropertyChanged(NameOf(DataFolderFallbackText))
        End Sub

        ''' <summary>Einen anderen Datenordner vormerken: erst pruefen, dann nachfragen. Der
        ''' Ordnerwaehler haengt am Fenster und ruft hierher.</summary>
        ''' <param name="confirmed">Die Antwort auf die Rueckfrage, wenn sie nicht gestellt werden
        ''' soll (Pruefstand).</param>
        Public Async Function ChangeDataFolderAsync(chosen As String, Optional confirmed As Boolean? = Nothing) As Task
            ' Ist der gewaehlte Ort nicht erreichbar, gilt schon der ab Werk; zurueck dorthin heisst
            ' dann nur, den gewaehlten aufzugeben. Umziehen laesst sich von einem fehlenden Ort nichts.
            If AppFolders.IsDataRootFallback AndAlso PathIdentity.AreSame(If(chosen, ""), AppFolders.DefaultDataRoot) Then
                AppFolders.ForgetUnreachableDataFolder()
                If _mainVm IsNot Nothing AndAlso Not confirmed.HasValue Then
                    Await _mainVm.ShowMessageAsync(LocalizationService.T("Datenordner"),
                        String.Format(LocalizationService.T("Beim nächsten Start verwendet FerrumPix den Datenordner in {0}."), AppFolders.DefaultDataRoot))
                End If
                RaiseDataFolderChanged()
                Return
            End If
            ' Mit festen Fotoordnern nur zurueck an den Standardort (siehe CanChooseDataFolder).
            If FolderRoots.IsActive AndAlso Not PathIdentity.AreSame(If(chosen, ""), AppFolders.DefaultDataRoot) Then Return
            Dim target As String = Nothing
            Dim reason As String = Nothing
            Dim choice = Await Task.Run(Function() AppFolders.ValidateNewDataFolder(chosen, target, reason))
            If choice = AppFolders.DataFolderChoice.Rejected Then
                If _mainVm IsNot Nothing AndAlso Not confirmed.HasValue Then
                    Await _mainVm.ShowMessageAsync(LocalizationService.T("Ort nicht verwendbar"), reason)
                End If
                Return
            End If

            Dim agreed = confirmed.GetValueOrDefault(False)
            If Not confirmed.HasValue AndAlso _mainVm IsNot Nothing Then
                If choice = AppFolders.DataFolderChoice.Adopt Then
                    agreed = Await _mainVm.ShowConfirmAsync(
                        LocalizationService.T("Vorhandenen Datenordner verwenden?"),
                        String.Format(LocalizationService.T("In {0} liegt schon ein Datenordner von FerrumPix. Ab dem nächsten Start arbeitet FerrumPix mit ihm; die bisherigen Daten bleiben in {1} liegen."),
                                      target, AppFolders.DataRoot),
                        LocalizationService.T("Verwenden"), LocalizationService.T("Abbrechen"))
                Else
                    Dim size = Await Task.Run(Function() MeasureFolderBytes(AppFolders.DataRoot))
                    agreed = Await _mainVm.ShowConfirmAsync(
                        LocalizationService.T("Datenordner verschieben?"),
                        String.Format(LocalizationService.T("Die Daten von FerrumPix ({0}) ziehen beim nächsten Start nach {1} um. Auf derselben Platte geht das sofort, auf eine andere wird kopiert, und dieser Start dauert entsprechend länger. Am alten Ort wird erst gelöscht, wenn alles angekommen ist."),
                                      FormatBytes(size), target),
                        LocalizationService.T("Verschieben"), LocalizationService.T("Abbrechen"))
                End If
            End If
            If Not agreed Then Return
            If PathIdentity.AreSame(target, AppFolders.DataRoot) Then
                AppFolders.CancelDataFolderChange()
            Else
                AppFolders.RequestDataFolderChange(target, choice = AppFolders.DataFolderChoice.Adopt)
            End If
            RaiseDataFolderChanged()
        End Function

        ''' <summary>Setzt Thema und Akzent aus den Einstellungen, bevor es ein Einstellungsmodell
        ''' gibt: fuer das Fenster beim Umzug des Datenordners, das vor dem Hauptfenster steht.</summary>
        Friend Shared Sub ApplySavedTheme()
            Try
                Dim saved = AppSettingsService.Load()
                ApplyTheme(saved.ThemeMode, saved.AccentColor, saved.AccentStrength)
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.ApplySavedTheme", ex)
            End Try
        End Sub

        ''' <summary>Der belegte Platz eines Ordners samt Unterordnern; unlesbare zaehlen nicht.</summary>
        Private Shared Function MeasureFolderBytes(folder As String) As Long
            Dim total = 0L
            Try
                If Not Directory.Exists(folder) Then Return 0
                Dim options As New EnumerationOptions With {.RecurseSubdirectories = True, .IgnoreInaccessible = True}
                For Each info In New DirectoryInfo(folder).EnumerateFiles("*", options)
                    total += info.Length
                Next
            Catch
            End Try
            Return total
        End Function

    End Class

End Namespace
