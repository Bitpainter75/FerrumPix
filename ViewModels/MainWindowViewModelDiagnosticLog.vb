Imports System
Imports System.Globalization
Imports System.IO
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Der Dialog "Protokoll anzeigen": das Ende der Fehlerdatei und der ausfuehrlichen
    ''' Protokolldatei (DiagnosticLogService.ReadErrorTail, ReadTail) untereinander in einem
    ''' Textfeld, das sich markieren und kopieren laesst, dazu der Ordner und ein Knopf dorthin.
    '''
    ''' Gedacht fuer Nutzer, die ein Protokoll schicken sollen: der Ordner liegt je System woanders,
    ''' unter macOS in der Bibliothek, die der Finder ab Werk ausblendet. Mit dem Dialog braucht
    ''' niemand den Ordner zu finden.</summary>
    Partial Public Class MainWindowViewModel

        ''' <summary>So viel vom Ende jeder Datei zeigt der Dialog. Ein Textfeld legt bei jeder
        ''' Aenderung der Markierung den ganzen Text neu aus; mit einem Megabyte je Datei ging dabei
        ''' der Prozessor hoch und das Markieren hing (Befund beim Testen). Fuer eine Fehlersuche
        ''' reichen die letzten Eintraege; mehr kopiert auch der Kopierknopf nicht.</summary>
        Friend Const DiagnosticLogShownBytes As Integer = 64 * 1024

        Private _dialogLogText As String = ""
        Private _openLogFolderCommand As ICommand
        Private _clearLogCommand As ICommand

        ''' <summary>Leert beide Protokolle und zeigt danach den leeren Stand. Ohne Rueckfrage: der
        ''' Dialog ist schon die Ansicht dessen, was weggeht, und eine zweite Rueckfrage wuerde ihn
        ''' ersetzen (es gibt nur einen Dialog zur Zeit).</summary>
        Public ReadOnly Property ClearLogCommand As ICommand
            Get
                If _clearLogCommand Is Nothing Then
                    _clearLogCommand = ReactiveCommand.CreateFromTask(
                        Async Function()
                            Dim problem = Await Task.Run(Function() DiagnosticLogService.Clear())
                            DialogLogText = If(problem.Length = 0, Await Task.Run(Function() BuildDiagnosticLogText()),
                                               LocalizationService.T("Das Protokoll ließ sich nicht leeren.") & " " & problem)
                        End Function)
                End If
                Return _clearLogCommand
            End Get
        End Property

        Public ReadOnly Property DialogShowsDiagnosticLog As Boolean
            Get
                Return _dialogKind = AppDialogKind.DiagnosticLog
            End Get
        End Property

        ''' <summary>Der gezeigte Ausschnitt der Protokolldatei.</summary>
        Public Property DialogLogText As String
            Get
                Return _dialogLogText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogLogText, If(value, ""))
            End Set
        End Property

        Public ReadOnly Property OpenLogFolderCommand As ICommand
            Get
                If _openLogFolderCommand Is Nothing Then
                    _openLogFolderCommand = ReactiveCommand.Create(Sub() OpenDiagnosticLogFolder())
                End If
                Return _openLogFolderCommand
            End Get
        End Property

        ''' <summary>Oeffnet den Ordner der Protokolldateien im Dateimanager. Gibt es ihn noch
        ''' nicht, wird er angelegt: sonst oeffnete sich nichts, und das saehe wie ein Fehler aus.</summary>
        Public Shared Sub OpenDiagnosticLogFolder()
            Dim folder = DiagnosticLogService.LogFolder
            Try
                Directory.CreateDirectory(folder)
            Catch ex As Exception
                DiagnosticLogService.LogException("Diagnostics.LogFolder", ex)
            End Try
            ShellOpenService.Open(folder, "Diagnostics.LogFolder")
        End Sub

        ''' <summary>Liest das Ende der Datei und zeigt es. Der Dialog hat nur "Schliessen"; Kopieren
        ''' und den Ordner bietet die Ansicht selbst an.</summary>
        Public Async Function ShowDiagnosticLogDialogAsync() As Task
            DialogLogText = Await Task.Run(Function() BuildDiagnosticLogText())
            Dim message = String.Format(CultureInfo.CurrentCulture,
                                        LocalizationService.T("Die letzten Einträge aus errors.log und diagnostics.log im Ordner {0}. Der Text lässt sich markieren und kopieren."),
                                        DiagnosticLogService.LogFolder)
            Await ShowDialogAsync(AppDialogKind.DiagnosticLog, "Fehlerdiagnose-Protokoll", message, "", "Schließen", "")
            DialogLogText = ""
        End Function

        ''' <summary>Beide Dateien untereinander, jede unter einer Kopfzeile mit ihrem Namen: die
        ''' Fehlerdatei zuerst, sie ist kurz und wird immer geschrieben, darunter das ausfuehrliche
        ''' Protokoll. So geht mit einem Kopieren alles mit, was zur Fehlersuche gebraucht wird.</summary>
        Friend Shared Function BuildDiagnosticLogText() As String
            Dim empty = LocalizationService.T("Das Protokoll ist noch leer.")
            Dim errors = DiagnosticLogService.ReadErrorTail(DiagnosticLogShownBytes)
            Dim details = DiagnosticLogService.ReadTail(DiagnosticLogShownBytes)
            Return "===== errors.log =====" & Environment.NewLine &
                   If(errors.Length = 0, empty & Environment.NewLine, errors) & Environment.NewLine &
                   "===== diagnostics.log =====" & Environment.NewLine &
                   If(details.Length = 0, empty & Environment.NewLine, details)
        End Function

    End Class

End Namespace
