Imports System
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Das kleine Fenster, solange der Datenordner beim Start umzieht
    ''' (DataFolderMoveWindow): Kreisel, Stand in Dateien und Groesse, die Datei, die gerade dran
    ''' ist, Quelle und Ziel. ABBRECHEN GIBT ES NICHT: solange der Umzug laeuft, laesst sich das
    ''' Fenster nicht schliessen (<see cref="CanCloseWindow"/>), und es hat keinen Knopf. Erst ein
    ''' Fehler bringt einen Knopf "Schliessen"; danach geht der Start weiter.</summary>
    Public Class DataFolderMoveViewModel
        Inherits ReactiveObject

        ''' <summary>So lang darf die angezeigte Datei hoechstens sein; laengere verlieren ihre Mitte.</summary>
        Friend Const MaxFileTextLength As Integer = 64
        Friend Const MaxFolderTextLength As Integer = 70

        Private _source As String = ""

        Public Sub New(plan As StartupMovePlan)
            CloseCommand = ReactiveCommand.Create(AddressOf RequestClose)
            If plan IsNot Nothing Then SetFolders(plan.Source, plan.Target)
            _progressText = LocalizationService.T("Wird vorbereitet")
        End Sub

        ''' <summary>Der Knopf im Fehlerfall. Solange der Umzug laeuft, ohne Wirkung.</summary>
        Public ReadOnly Property CloseCommand As ICommand

        ''' <summary>Das Fenster will geschlossen werden (nur nach dem Ende des Umzugs).</summary>
        Public Event CloseRequested As EventHandler

        Private _isRunning As Boolean = True
        ''' <summary>Der Umzug laeuft noch. Dann laesst sich das Fenster nicht schliessen.</summary>
        Public Property IsRunning As Boolean
            Get
                Return _isRunning
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isRunning, value)
            End Set
        End Property

        Private _hasError As Boolean
        Public Property HasError As Boolean
            Get
                Return _hasError
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_hasError, value)
            End Set
        End Property

        Private _errorTitle As String = ""
        Public Property ErrorTitle As String
            Get
                Return _errorTitle
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_errorTitle, If(value, ""))
            End Set
        End Property

        Private _errorText As String = ""
        Public Property ErrorText As String
            Get
                Return _errorText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_errorText, If(value, ""))
            End Set
        End Property

        Private _sourceText As String = ""
        Public Property SourceText As String
            Get
                Return _sourceText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_sourceText, If(value, ""))
            End Set
        End Property

        Private _targetText As String = ""
        Public Property TargetText As String
            Get
                Return _targetText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_targetText, If(value, ""))
            End Set
        End Property

        Private _progressText As String = ""
        ''' <summary>"12 von 340 Dateien, 1,2 GB von 8,4 GB", ohne bekannte Gesamtzahl nur die Anzahl.</summary>
        Public Property ProgressText As String
            Get
                Return _progressText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_progressText, If(value, ""))
            End Set
        End Property

        Private _currentFileText As String = ""
        ''' <summary>Die Datei, die gerade umzieht, relativ zur Quelle und gekuerzt.</summary>
        Public Property CurrentFileText As String
            Get
                Return _currentFileText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_currentFileText, If(value, ""))
            End Set
        End Property

        Private _progressPercent As Double
        ''' <summary>Anteil der Bytes von 0 bis 100, nur mit bekannter Gesamtgroesse.</summary>
        Public Property ProgressPercent As Double
            Get
                Return _progressPercent
            End Get
            Private Set(value As Double)
                Me.RaiseAndSetIfChanged(_progressPercent, value)
            End Set
        End Property

        Private _hasKnownTotal As Boolean
        Public Property HasKnownTotal As Boolean
            Get
                Return _hasKnownTotal
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_hasKnownTotal, value)
            End Set
        End Property

        ''' <summary>Darf das Fenster jetzt zugehen (Kreuz, ALT+F4, CMD+Q)? Nur, wenn der Umzug
        ''' zu Ende ist.</summary>
        Public Function CanCloseWindow() As Boolean
            Return Not IsRunning
        End Function

        Private Sub SetFolders(source As String, target As String)
            _source = If(source, "")
            SourceText = AdjustmentPresetManagerViewModel.ShortenPath(source, MaxFolderTextLength)
            TargetText = AdjustmentPresetManagerViewModel.ShortenPath(target, MaxFolderTextLength)
        End Sub

        ''' <summary>Uebernimmt einen Stand des Umzugs (auf dem Faden der Oberflaeche).</summary>
        Public Sub ApplyProgress(value As FolderMoveProgress)
            If value Is Nothing OrElse Not IsRunning Then Return
            If value.Source.Length > 0 AndAlso Not String.Equals(value.Source, _source, StringComparison.Ordinal) Then
                SetFolders(value.Source, value.Target)
            End If
            ProgressText = DescribeProgress(value)
            HasKnownTotal = value.BytesTotal > 0 AndAlso value.FilesTotal > 0
            ProgressPercent = If(HasKnownTotal, Math.Min(100.0, 100.0 * value.BytesDone / value.BytesTotal), 0.0)
            If value.CurrentFile.Length > 0 Then CurrentFileText = DescribeFile(value.CurrentFile, _source)
        End Sub

        ''' <summary>Der Stand als Satz in der geltenden Sprache.</summary>
        Public Shared Function DescribeProgress(value As FolderMoveProgress) As String
            If value Is Nothing Then Return ""
            If value.FilesTotal > 0 Then
                If value.BytesTotal > 0 Then
                    Return String.Format(LocalizationService.T("{0} von {1} Dateien, {2} von {3}"),
                                         value.FilesDone, value.FilesTotal,
                                         CardImportService.FormatBytes(value.BytesDone), CardImportService.FormatBytes(value.BytesTotal))
                End If
                Return String.Format(LocalizationService.T("{0} von {1} Dateien"), value.FilesDone, value.FilesTotal)
            End If
            Return String.Format(LocalizationService.T("{0} Dateien verschoben"), value.FilesDone)
        End Function

        ''' <summary>Die Datei relativ zur Quelle, gekuerzt auf <see cref="MaxFileTextLength"/>.</summary>
        Public Shared Function DescribeFile(file As String, sourceRoot As String) As String
            Dim text = If(file, "")
            Try
                If sourceRoot.Length > 0 AndAlso PathIdentity.IsAncestorOrSelf(sourceRoot, text) Then
                    text = Path.GetRelativePath(sourceRoot, text)
                End If
            Catch
            End Try
            Return AdjustmentPresetManagerViewModel.ShortenPath(text, MaxFileTextLength)
        End Function

        ''' <summary>Der Umzug ist durch, ohne Fehler.</summary>
        Public Sub MarkFinished()
            CurrentFileText = ""
            IsRunning = False
        End Sub

        ''' <summary>Der Umzug ist mit einem Fehler zu Ende: ein Satz und der Knopf "Schliessen".
        ''' Was dann gilt (Rueckfall auf den alten Ort), hat der Umzug schon entschieden.</summary>
        Public Sub ShowError(title As String, message As String)
            ErrorTitle = If(String.IsNullOrEmpty(title), LocalizationService.T("Datenordner nicht umgezogen"), title)
            ErrorText = message
            CurrentFileText = ""
            HasError = True
            IsRunning = False
        End Sub

        Private Sub RequestClose()
            If IsRunning Then Return
            RaiseEvent CloseRequested(Me, EventArgs.Empty)
        End Sub

    End Class

    ''' <summary>Fuehrt den Umzug beim Start und das Fenster dazu zusammen, ohne selbst ein Fenster
    ''' zu kennen (der Pruefstand reicht eigene Rueckrufe herein).
    '''
    ''' DER ABLAUF: der Umzug laeuft auf einem Hintergrundfaden. Ist er nach
    ''' <c>delay</c> fertig, erscheint kein Fenster, und der Start geht sofort weiter, noch auf dem
    ''' Faden des Aufrufers; eine Meldung zeigt dann wie bisher das Hauptfenster. Sonst erscheint
    ''' das Fenster, und der Start geht erst weiter, wenn der Umzug durch ist, nach einem Fehler
    ''' erst, wenn das Fenster geschlossen wurde. Der Start geht GENAU EINMAL weiter.</summary>
    Public NotInheritable Class StartupMoveCoordinator

        Private ReadOnly _work As Action(Of IProgress(Of FolderMoveProgress))
        Private ReadOnly _describeError As Func(Of Exception, String())
        Private ReadOnly _post As Action(Of Action)
        Private ReadOnly _showWindow As Action(Of DataFolderMoveViewModel)
        Private ReadOnly _closeWindow As Action
        Private ReadOnly _continueStart As Action
        Private _continued As Integer
        Private _windowShown As Boolean
        Private _running As Integer = 1

        ''' <param name="work">Der Umzug samt allem, was danach im Datenordner geschieht; laeuft
        ''' auf einem Hintergrundfaden und meldet seinen Stand an den uebergebenen Empfaenger.</param>
        ''' <param name="describeError">Nach dem Umzug: Ueberschrift und Satz eines Fehlers, oder
        ''' Nothing. Bekommt die Ausnahme, falls der Umzug geworfen hat.</param>
        ''' <param name="post">Bringt einen Schritt auf den Faden der Oberflaeche.</param>
        ''' <param name="showWindow">Zeigt das Fenster zum Modell.</param>
        ''' <param name="closeWindow">Schliesst es wieder.</param>
        ''' <param name="continueStart">Der normale Start (Anmeldefenster, Galerie).</param>
        Public Sub New(plan As StartupMovePlan,
                       work As Action(Of IProgress(Of FolderMoveProgress)),
                       describeError As Func(Of Exception, String()),
                       post As Action(Of Action),
                       showWindow As Action(Of DataFolderMoveViewModel),
                       closeWindow As Action,
                       continueStart As Action)
            _work = work
            _describeError = describeError
            _post = post
            _showWindow = showWindow
            _closeWindow = closeWindow
            _continueStart = continueStart
            ViewModel = New DataFolderMoveViewModel(plan)
            AddHandler ViewModel.CloseRequested, Sub(s, e) CloseAndContinue()
        End Sub

        Public ReadOnly Property ViewModel As DataFolderMoveViewModel

        ''' <summary>Ob das Fenster gezeigt wurde.</summary>
        Public ReadOnly Property WindowShown As Boolean
            Get
                Return _windowShown
            End Get
        End Property

        ''' <summary>Ob der Start schon weitergegangen ist.</summary>
        Public ReadOnly Property StartContinued As Boolean
            Get
                Return Volatile.Read(_continued) = 1
            End Get
        End Property

        ''' <summary>Laeuft der Umzug noch? Dann wird jedes Beenden abgewiesen.</summary>
        Public ReadOnly Property IsRunning As Boolean
            Get
                Return Volatile.Read(_running) = 1
            End Get
        End Property

        ''' <summary>Startet den Umzug und wartet hoechstens <paramref name="delay"/> auf ihn.</summary>
        Public Sub Start(delay As TimeSpan)
            Dim sink As New ThrottledProgress(Me)
            Dim work = Task.Run(Sub() _work(sink))
            Dim quick = False
            Try
                quick = work.Wait(delay)
            Catch ex As AggregateException
                quick = True
            End Try
            If quick Then
                ' Kurz: kein Fenster, auch nicht nach einem Fehler; den meldet das Hauptfenster.
                LogFault(work)
                Volatile.Write(_running, 0)
                ViewModel.MarkFinished()
                ContinueOnce()
                Return
            End If
            _windowShown = True
            _showWindow?.Invoke(ViewModel)
            work.ContinueWith(Sub(t) _post(Sub() OnWorkFinished(t)), TaskScheduler.Default)
        End Sub

        Private Sub OnWorkFinished(work As Task)
            LogFault(work)
            Volatile.Write(_running, 0)
            Dim failure As String() = Nothing
            Try
                failure = _describeError?.Invoke(If(work.IsFaulted, work.Exception?.GetBaseException(), Nothing))
            Catch ex As Exception
                DiagnosticLogService.LogException("StartupMove.DescribeError", ex)
            End Try
            If failure Is Nothing OrElse failure.Length < 2 Then
                ViewModel.MarkFinished()
                CloseAndContinue()
            Else
                ' Der Start wartet, bis jemand "Schliessen" drueckt oder das Fenster schliesst.
                ViewModel.ShowError(failure(0), failure(1))
            End If
        End Sub

        ''' <summary>Das Fenster wurde nach dem Ende geschlossen (Knopf, Kreuz): weiter mit dem
        ''' Start. Waehrend der Umzug laeuft, ohne Wirkung.</summary>
        Public Sub NotifyWindowClosed()
            If IsRunning Then Return
            ContinueOnce()
        End Sub

        Private Sub CloseAndContinue()
            If IsRunning Then Return
            Try
                _closeWindow?.Invoke()
            Catch ex As Exception
                DiagnosticLogService.LogException("StartupMove.CloseWindow", ex)
            End Try
            ContinueOnce()
        End Sub

        Private Sub ContinueOnce()
            If Interlocked.Exchange(_continued, 1) = 1 Then Return
            _continueStart?.Invoke()
        End Sub

        Private Shared Sub LogFault(work As Task)
            If work.IsFaulted Then DiagnosticLogService.LogException("StartupMove", work.Exception?.GetBaseException())
        End Sub

        Private Sub Deliver(value As FolderMoveProgress)
            ViewModel.ApplyProgress(value)
        End Sub

        ''' <summary>Gibt den Stand an die Oberflaeche weiter, hoechstens einen Schritt zur Zeit:
        ''' meldet der Umzug schneller, als die Oberflaeche zeichnet, gilt jeweils der neueste.</summary>
        Private NotInheritable Class ThrottledProgress
            Implements IProgress(Of FolderMoveProgress)

            Private ReadOnly _owner As StartupMoveCoordinator
            Private _latest As FolderMoveProgress
            Private _pending As Integer

            Public Sub New(owner As StartupMoveCoordinator)
                _owner = owner
            End Sub

            Public Sub Report(value As FolderMoveProgress) Implements IProgress(Of FolderMoveProgress).Report
                If value Is Nothing Then Return
                Interlocked.Exchange(_latest, value.Clone())
                If Interlocked.CompareExchange(_pending, 1, 0) <> 0 Then Return
                _owner._post(Sub()
                                 Volatile.Write(_pending, 0)
                                 Dim current = Interlocked.Exchange(_latest, Nothing)
                                 If current IsNot Nothing Then _owner.Deliver(current)
                             End Sub)
            End Sub
        End Class

    End Class

End Namespace
