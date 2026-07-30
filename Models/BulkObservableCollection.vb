Imports System.Collections.ObjectModel
Imports System.Collections.Specialized
Imports System.ComponentModel

Namespace Models

    Public Class BulkObservableCollection(Of T)
        Inherits ObservableCollection(Of T)

        ''' Tauscht ALLE Elemente aus und meldet dabei genau EINE Ruecksetzung statt N Zugaenge
        ''' und ein Leeren. Bei einem Ordner mit tausend Bildern ist das der Unterschied zwischen
        ''' einem Neuaufbau der Liste und tausend.
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
