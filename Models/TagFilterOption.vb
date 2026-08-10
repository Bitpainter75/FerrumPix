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

        Public Sub New(tag As String, count As Integer, isSelected As Boolean,
                       Optional serverSource As String = "", Optional serverId As String = "")
            Me.Tag = If(tag, "")
            Me.Count = count
            Me.ServerSource = If(serverSource, "")
            Me.ServerId = If(serverId, "")
            _isSelected = isSelected
        End Sub

        Public ReadOnly Property Tag As String
        Public ReadOnly Property Count As Integer

        ''' <summary>Von welchem Server dieses Stichwort kommt; leer heisst aus dem lokalen Katalog.
        ''' Gleiche Regel wie bei Personen und Orten: allein waehlbar, oeffnet die Server-Ansicht.
        '''
        ''' Nur EIN Server kann hier stehen, und das ist kein Zufall: Nextcloud fuehrt Stichwoerter
        ''' als Cluster und kann danach filtern, Immichs Suche kennt keinen Stichwortfilter.</summary>
        Public ReadOnly Property ServerSource As String

        ''' <summary>Kennung des Stichworts auf dem Server - der Filter laeuft ueber sie, nicht ueber
        ''' den Namen.</summary>
        Public ReadOnly Property ServerId As String

        Public ReadOnly Property IsFromServer As Boolean
            Get
                Return ServerSource.Length > 0
            End Get
        End Property

        ''' <summary>Traegt die Zwischenueberschrift mit dem Servernamen - beim ERSTEN Eintrag je
        ''' Server.</summary>
        Public Property ShowsServerHeader As Boolean

        ''' <summary>Beschriftung mit der Anzahl in Klammern, etwa "urlaub (42)". OHNE Anzahl, wo es
        ''' keine gibt - "(0)" hinter einem Stichwort liest sich wie "keine Bilder".</summary>
        Public ReadOnly Property Label As String
            Get
                Return If(Count > 0, $"{Tag} ({Count})", Tag)
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
