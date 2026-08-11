Imports System
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Threading
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>
    ''' Was ein Hintergrundlauf der Oberflaeche zeigt: laeuft er, wie weit ist er, und die Knoepfe
    ''' zum Starten und Stoppen.
    '''
    ''' Zwei Laeufe teilen sich das - der Katalogindex und die Gesichtssuche ueber dieselben
    ''' ueberwachten Ordner. Sie zeigen an denselben zwei Stellen (Einstellungen und Fusszeile der
    ''' Galerie), melden ihren Fortschritt aus einem Hintergrund-Thread und muessen ihn deshalb
    ''' beide drosseln und beide ueber den Dispatcher setzen. Zweimal dieselbe Mechanik waere zweimal
    ''' dieselbe Falle: ein vergessener Dispatcher-Wechsel faellt nicht sofort auf, sondern erst,
    ''' wenn Avalonia irgendwann aus dem falschen Thread heraus zeichnet.
    ''' </summary>
    Public MustInherit Class BackgroundRunViewModel
        Inherits ViewModelBase

        Private _isRunning As Boolean
        Private _statusText As String = ""
        Private _progressPercent As Double
        Private _hasProgress As Boolean
        Private _lastReportTicks As Long
        Private _ownRunActive As Integer

        ''' <summary>Betritt den Lauf. False heisst: DIESES ViewModel haelt schon einen.
        '''
        ''' Beide Laeufe lassen sich von mehreren Stellen ausloesen - der Index vom Programmstart und
        ''' vom Knopf, die Gesichtssuche aus jeder Ordnerzeile und ueber die gefilterte Menge. Zwei
        ''' davon fast gleichzeitig, und der zweite kaeme bis zur Anzeige, wuerde vom Dienst
        ''' abgewiesen und raeumte sie hinterher ab - waehrend der erste weiterarbeitet.
        '''
        ''' Interlocked und kein Merker mit If: zwischen Lesen und Setzen liegt sonst genau das
        ''' Fenster, um das es hier geht. AUFZURUFEN VOR der ersten Anzeige.</summary>
        Protected Function TryEnterRun() As Boolean
            Return Interlocked.CompareExchange(_ownRunActive, 1, 0) = 0
        End Function

        ''' <summary>Verlaesst den Lauf. ZULETZT aufrufen - nach dem letzten Zugriff auf die Anzeige.
        '''
        ''' Wird er frueher freigegeben, kann in der Zwischenzeit ein neuer Lauf durch den Riegel und
        ''' seine Anzeige setzen; der alte Aufruf schreibt danach seinen Abschlussstand darueber, und
        ''' Fortschritt und Stopp-Knopf des neuen sind weg.</summary>
        Protected Sub LeaveRun()
            Volatile.Write(_ownRunActive, 0)
        End Sub

        Protected Sub New()
            StartCommand = ReactiveCommand.CreateFromTask(Function() StartAsync())
            StopCommand = ReactiveCommand.Create(Sub() RequestCancel())
        End Sub

        Public ReadOnly Property StartCommand As ICommand
        Public ReadOnly Property StopCommand As ICommand

        ''' <summary>Startet den Lauf. Die Ableitung bringt ihre eigene Anzeige nach.</summary>
        Public MustOverride Function StartAsync() As Task

        ''' <summary>Bittet den laufenden Durchgang aufzuhoeren.</summary>
        Protected MustOverride Sub RequestCancel()

        ''' <summary>Laeuft gerade einer? Traegt die Sichtbarkeit der Anzeige und den Stopp-Knopf.</summary>
        Public Property IsRunning As Boolean
            Get
                Return _isRunning
            End Get
            Protected Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isRunning, value)
                Me.RaisePropertyChanged(NameOf(CanStart))
            End Set
        End Property

        ''' <summary>Startbar? Die Ableitung entscheidet, was ausser "laeuft nicht" noch dazugehoert -
        ''' ohne eingetragene Ordner gibt es nichts zu tun, und ein Knopf, der nichts tut, sagt
        ''' niemandem, warum.</summary>
        Public Overridable ReadOnly Property CanStart As Boolean
            Get
                Return Not _isRunning
            End Get
        End Property

        ''' <summary>Was gerade geschieht, in einem Satz. Leer heisst: nichts zu melden.</summary>
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Protected Set(value As String)
                Me.RaiseAndSetIfChanged(_statusText, If(value, ""))
                Me.RaisePropertyChanged(NameOf(HasStatus))
            End Set
        End Property

        Public ReadOnly Property HasStatus As Boolean
            Get
                Return Not String.IsNullOrEmpty(_statusText)
            End Get
        End Property

        ''' <summary>Fortschritt von 0 bis 100 fuer den Balken.</summary>
        Public Property ProgressPercent As Double
            Get
                Return _progressPercent
            End Get
            Protected Set(value As Double)
                Me.RaiseAndSetIfChanged(_progressPercent, value)
            End Set
        End Property

        ''' <summary>Steht die Gesamtzahl schon? Solange nicht, gibt es keinen Anteil, den ein Balken
        ''' zeigen koennte - er laeuft dann unbestimmt, statt auf null zu stehen.</summary>
        Public Property HasProgress As Boolean
            Get
                Return _hasProgress
            End Get
            Protected Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_hasProgress, value)
            End Set
        End Property

        ''' <summary>Meldet der Oberflaeche, dass sich die Startbedingungen geaendert haben.</summary>
        Public Sub RefreshCanStart()
            Me.RaisePropertyChanged(NameOf(CanStart))
        End Sub

        ''' <summary>Fortschritt melden - HOECHSTENS ZEHNMAL JE SEKUNDE.
        '''
        ''' Der Lauf meldet nach jeder Datei. Bei kleinen JPEGs sind das Hunderte Meldungen in der
        ''' Sekunde, und jede einzelne kostete einen Wechsel auf den Oberflaechen-Thread und einen
        ''' neuen Text - die Anzeige flackerte, und der Lauf wartete auf sie. Zehnmal je Sekunde ist
        ''' mehr, als ein Auge unterscheidet.
        '''
        ''' Die LETZTE Meldung darf nicht wegfallen, sonst bliebe der Balken kurz vor dem Ende
        ''' stehen - deshalb kommt sie ungedrosselt durch.</summary>
        Protected Sub ReportThrottled(text As String, done As Integer, total As Integer)
            Dim isLast = total > 0 AndAlso done >= total
            Dim now = Environment.TickCount64
            If Not isLast AndAlso now - Volatile.Read(_lastReportTicks) < 100 Then Return
            Volatile.Write(_lastReportTicks, now)

            Dim percent = If(total > 0, Math.Min(100.0, done * 100.0 / total), 0.0)
            Dim ignored = SetStatusAsync(text, percent, total > 0)
        End Sub

        ''' <summary>Setzt Text und Balken auf dem Oberflaechen-Thread. Eigener Weg, weil jede
        ''' Meldung aus dem Hintergrund kommt und ein gebundener Wert nur dort gesetzt werden darf.</summary>
        Protected Function SetStatusAsync(text As String, percent As Double, hasProgress As Boolean) As Task
            Return Dispatcher.UIThread.InvokeAsync(Sub()
                                                       StatusText = text
                                                       ProgressPercent = percent
                                                       HasProgress = hasProgress
                                                   End Sub).GetTask()
        End Function

        Protected Function SetRunningAsync(running As Boolean) As Task
            Return Dispatcher.UIThread.InvokeAsync(Sub() IsRunning = running).GetTask()
        End Function

    End Class

End Namespace
