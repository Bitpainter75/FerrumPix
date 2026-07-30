Imports System
Imports System.Windows.Input

Namespace ViewModels

    ''' <summary>
    ''' Ein Kommando, das einfach einen uebergebenen Aufruf ausfuehrt.
    '''
    ''' Gebraucht fuer Menueeintraege, deren Arbeit in der VIEW liegt und nicht im ViewModel: die
    ''' Zwischenablage haengt am TopLevel, Ordner anlegen und Vergleichen oeffnen Dialoge ueber den
    ''' Fensterrahmen. Diese Aktionen als Kommando am ViewModel nachzubauen hiesse, sie dorthin zu
    ''' verschieben, wo sie nicht hingehoeren.
    '''
    ''' So bleibt der Bauplan frei von Ansichtswissen: er bekommt Kommandos, und woher die kommen,
    ''' ist seine Sache nicht.
    ''' </summary>
    Public NotInheritable Class DelegateCommand
        Implements ICommand

        Private ReadOnly _execute As Action(Of Object)
        Private ReadOnly _canExecute As Func(Of Boolean)

        Public Sub New(execute As Action)
            Me.New(Sub(ignored) execute?.Invoke())
        End Sub

        Public Sub New(execute As Action(Of Object), Optional canExecute As Func(Of Boolean) = Nothing)
            _execute = execute
            _canExecute = canExecute
        End Sub

        Public Event CanExecuteChanged As EventHandler Implements ICommand.CanExecuteChanged

        Public Function CanExecute(parameter As Object) As Boolean Implements ICommand.CanExecute
            Return _execute IsNot Nothing AndAlso (_canExecute Is Nothing OrElse _canExecute())
        End Function

        Public Sub Execute(parameter As Object) Implements ICommand.Execute
            _execute?.Invoke(parameter)
        End Sub

        ''' <summary>Meldet, dass sich die Ausfuehrbarkeit geaendert haben koennte.</summary>
        Public Sub RaiseCanExecuteChanged()
            RaiseEvent CanExecuteChanged(Me, EventArgs.Empty)
        End Sub

    End Class

End Namespace
