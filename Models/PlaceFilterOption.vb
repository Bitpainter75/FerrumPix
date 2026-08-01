Imports System.ComponentModel
Imports System.Runtime.CompilerServices

Namespace Models

    ''' <summary>
    ''' Ein Ort im Auswahlmenue der Galerie, mit der Anzahl Bilder, die dort aufgenommen wurden.
    '''
    ''' Aufgebaut wie <see cref="PersonFilterOption"/>, und das ist Absicht: beide sind dieselbe Sorte
    ''' Einschraenkung und sollen sich gleich anfuehlen. Der Unterschied liegt im Schluessel - eine
    ''' Person hat eine Id, ein Ort ist sein Name.
    ''' </summary>
    Public Class PlaceFilterOption
        Implements INotifyPropertyChanged

        Private _isSelected As Boolean

        Public Sub New(city As String, country As String, count As Integer, isSelected As Boolean)
            Me.City = If(city, "")
            Me.Country = If(country, "")
            Me.Count = count
            _isSelected = isSelected
        End Sub

        Public ReadOnly Property City As String
        Public ReadOnly Property Country As String
        Public ReadOnly Property Count As Integer

        ''' <summary>"Norden, Deutschland (42)". Fehlt das Land, faellt es samt Komma weg.</summary>
        Public ReadOnly Property Label As String
            Get
                Dim place = If(Country.Length > 0, $"{City}, {Country}", City)
                Return $"{place} ({Count})"
            End Get
        End Property

        Public Property IsSelected As Boolean
            Get
                Return _isSelected
            End Get
            Set(value As Boolean)
                If _isSelected = value Then Return
                _isSelected = value
                Notify()
            End Set
        End Property

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub Notify(<CallerMemberName> Optional name As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

    End Class

End Namespace
