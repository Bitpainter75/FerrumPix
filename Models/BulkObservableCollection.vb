Imports System
Imports System.Collections.Generic
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

        ''' <summary>Bringt die Sammlung auf <paramref name="newItems"/> und meldet dabei nur, was sich
        ''' wirklich geaendert hat. Liefert, was gemeldet wurde.
        '''
        ''' WOZU. <see cref="ReplaceAll"/> meldet immer eine Ruecksetzung, und eine Ansicht, die an der
        ''' Sammlung haengt und selbst virtualisiert, gibt darauf JEDE sichtbare Kachel zurueck und
        ''' holt sie neu: gemessen 2,5 ms je Kachel gegen 0,1 bis 0,4 ms fuer eine Kachel, die nur ein
        ''' anderes Bild bekommt. Die Galerie tauscht ihre Liste nach jedem Filter- und Sortierlauf,
        ''' also auch dann, wenn dieselben Bilder in derselben Reihenfolge herauskommen (nach dem
        ''' Nachlesen der Metadaten) oder nur hinten welche dazukommen (ein Schub aus den Unterordnern).
        '''
        ''' WIE. Gleicher Anfang und gleiches Ende bleiben stehen. Ist alles gleich, wird NICHTS
        ''' gemeldet. Sonst geht der Abschnitt dazwischen als EIN Entfernen und EIN Hinzufuegen
        ''' hinaus, hoechstens zwei Meldungen. Haben alt und neu weder Anfang noch Ende gemeinsam,
        ''' oder war die Sammlung leer, bleibt es bei der Ruecksetzung.</summary>
        Public Function ReplaceAllMinimal(newItems As IList(Of T)) As BulkChange
            CheckReentrancy()
            Dim oldCount = Items.Count
            Dim newCount = newItems.Count
            Dim comparer = EqualityComparer(Of T).Default
            Dim prefix = 0
            Dim limit = Math.Min(oldCount, newCount)
            While prefix < limit AndAlso comparer.Equals(Items(prefix), newItems(prefix))
                prefix += 1
            End While
            If prefix = oldCount AndAlso prefix = newCount Then Return BulkChange.None
            Dim suffix = 0
            While suffix < limit - prefix AndAlso comparer.Equals(Items(oldCount - 1 - suffix), newItems(newCount - 1 - suffix))
                suffix += 1
            End While
            If oldCount = 0 OrElse (prefix = 0 AndAlso suffix = 0) Then
                ReplaceAll(newItems)
                Return BulkChange.Reset
            End If

            Dim removeCount = oldCount - prefix - suffix
            Dim addCount = newCount - prefix - suffix
            If removeCount > 0 Then
                Dim removed = New List(Of T)(removeCount)
                For i = 0 To removeCount - 1
                    removed.Add(Items(prefix + i))
                Next
                ' Von hinten: so verschiebt jedes Entfernen nur, was schon weg soll.
                For i = removeCount - 1 To 0 Step -1
                    Items.RemoveAt(prefix + i)
                Next
                OnPropertyChanged(New PropertyChangedEventArgs("Count"))
                OnPropertyChanged(New PropertyChangedEventArgs("Item[]"))
                OnCollectionChanged(New NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, prefix))
            End If
            If addCount > 0 Then
                Dim added = New List(Of T)(addCount)
                For i = 0 To addCount - 1
                    Dim item = newItems(prefix + i)
                    added.Add(item)
                    Items.Insert(prefix + i, item)
                Next
                OnPropertyChanged(New PropertyChangedEventArgs("Count"))
                OnPropertyChanged(New PropertyChangedEventArgs("Item[]"))
                OnCollectionChanged(New NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, prefix))
            End If
            Return BulkChange.Range
        End Function

    End Class

    ''' <summary>Was <see cref="BulkObservableCollection(Of T).ReplaceAllMinimal"/> gemeldet hat.</summary>
    Public Enum BulkChange
        ''' <summary>Nichts: die Sammlung stand schon so da.</summary>
        None = 0
        ''' <summary>Ein Abschnitt wurde entfernt und/oder eingefuegt.</summary>
        Range = 1
        ''' <summary>Eine Ruecksetzung wie bei ReplaceAll.</summary>
        Reset = 2
    End Enum

End Namespace
