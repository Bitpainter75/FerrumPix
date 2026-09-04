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

        Public Sub New(city As String, country As String, count As Integer, isSelected As Boolean,
                       Optional countryCode As String = "", Optional serverSource As String = "",
                       Optional serverId As String = "")
            Me.City = If(city, "")
            Me.Country = If(country, "")
            Me.CountryCode = If(countryCode, "")
            Me.Count = count
            Me.ServerSource = If(serverSource, "")
            ' Ohne eigene Kennung ist der Ortsname selbst der Schluessel - so filtert Immich.
            Me.ServerId = If(String.IsNullOrEmpty(serverId), Me.City, serverId)
            _isSelected = isSelected
        End Sub

        Public ReadOnly Property City As String
        Public ReadOnly Property Country As String

        ''' <summary>Von welchem Server dieser Ort kommt; leer heisst aus dem lokalen Katalog. Gleiche
        ''' Regel wie bei den Personen (siehe <see cref="PersonFilterOption.ServerSource"/>): allein
        ''' waehlbar, oeffnet die Server-Ansicht.</summary>
        Public ReadOnly Property ServerSource As String

        ''' <summary>Kennung des Orts auf dem Server. Bei Nextcloud ist sie eine eigene Zeichenkette
        ''' (der Cluster), bei Immich der Stadtname selbst.</summary>
        Public ReadOnly Property ServerId As String

        Public ReadOnly Property IsFromServer As Boolean
            Get
                Return ServerSource.Length > 0
            End Get
        End Property

        ''' <summary>Das Laenderkuerzel. Der ANGEZEIGTE Landesname kommt daraus in der Sprache der
        ''' Oberflaeche; gefiltert wird weiter ueber den gespeicherten Ortsnamen, der sich nicht mit
        ''' der Sprache aendert.</summary>
        Public ReadOnly Property CountryCode As String
        Public ReadOnly Property Count As Integer

        ''' <summary>"Norden, Deutschland (42)". Fehlt das Land, faellt es samt Komma weg; fehlt die
        ''' Anzahl (Immich liefert seine Staedte ohne), faellt auch sie weg - "(0)" liest sich sonst
        ''' wie "keine Bilder".</summary>
        Public ReadOnly Property Label As String
            Get
                Dim land = Services.PlaceLookupService.LocalizedCountry(CountryCode, Country)
                Dim place = If(land.Length > 0, $"{City}, {land}", City)
                Return If(Count > 0, $"{place} ({Count})", place)
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
