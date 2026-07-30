Imports System.ComponentModel
Imports System.Runtime.CompilerServices

Namespace Models

    ''' <summary>
    ''' Ein Stichwort im Auswahlmenue der Galerie, mit der Anzahl Bilder, die es tragen.
    '''
    ''' Eigene Klasse und kein Tupel, weil die Markierung sich zur Laufzeit aendert: wer im
    ''' Infopanel auf ein Stichwort klickt, soll den Haken hier gesetzt sehen, ohne dass die ganze
    ''' Liste neu gebaut wird.
    ''' </summary>
    Public Class TagFilterOption
        Implements INotifyPropertyChanged

        Private _isSelected As Boolean

        Public Sub New(tag As String, count As Integer, isSelected As Boolean)
            Me.Tag = If(tag, "")
            Me.Count = count
            _isSelected = isSelected
        End Sub

        Public ReadOnly Property Tag As String
        Public ReadOnly Property Count As Integer

        ''' <summary>Beschriftung mit der Anzahl in Klammern, etwa "urlaub (42)".</summary>
        Public ReadOnly Property Label As String
            Get
                Return $"{Tag} ({Count})"
            End Get
        End Property

        Public Property IsSelected As Boolean
            Get
                Return _isSelected
            End Get
            Set(value As Boolean)
                If _isSelected = value Then Return
                _isSelected = value
                Melden()
            End Set
        End Property

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub Melden(<CallerMemberName> Optional name As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

    End Class

End Namespace
