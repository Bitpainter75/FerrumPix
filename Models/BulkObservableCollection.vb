Imports System.Collections.ObjectModel
Imports System.Collections.Specialized
Imports System.ComponentModel

Namespace Models

    Public Class BulkObservableCollection(Of T)
        Inherits ObservableCollection(Of T)

        ''' Replaces all items and fires exactly one Reset event instead of N Add + 1 Clear.
        Public Sub ReplaceAll(newItems As IEnumerable(Of T))
            CheckReentrancy()
            Items.Clear()
            For Each elem In newItems
                Items.Add(elem)
            Next
            OnPropertyChanged(New PropertyChangedEventArgs("Count"))
            OnPropertyChanged(New PropertyChangedEventArgs("Item[]"))
            OnCollectionChanged(New NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset))
        End Sub

    End Class

End Namespace
