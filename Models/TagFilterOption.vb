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
                       Optional serverSource As String = "", Optional serverId As String = "",
                       Optional displayTag As String = "", Optional isAiTag As Boolean = False,
                       Optional filterTags As IEnumerable(Of String) = Nothing)
            Me.Tag = If(tag, "")
            Me.DisplayTag = If(String.IsNullOrWhiteSpace(displayTag), Me.Tag, displayTag)
            Me.Count = count
            Me.ServerSource = If(serverSource, "")
            Me.ServerId = If(serverId, "")
            Me.IsAiTag = isAiTag
            Dim merged = If(filterTags, Enumerable.Empty(Of String)()).
                         Where(Function(t) Not String.IsNullOrWhiteSpace(t)).
                         Select(Function(t) t.Trim()).
                         Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            If merged.Count = 0 AndAlso Me.Tag.Length > 0 Then merged.Add(Me.Tag)
            Me.FilterTags = merged
            _isSelected = isSelected
        End Sub

        ''' <summary>Wonach gefiltert wird. Bei einem erkannten Stichwort ist das der kanonische
        ''' englische Modellbegriff, nicht das, was danebensteht (siehe <see cref="DisplayTag"/>).</summary>
        Public ReadOnly Property Tag As String

        ''' <summary>ALLE Filterwerte dieser Zeile. Meist genau einer, naemlich <see cref="Tag"/>.
        '''
        ''' Mehrere stehen dort, wo eine Zeile mehrere Begriffe zusammenfasst, die gleich heissen:
        ''' "road" und "street" lesen sich beide als "Strasse", und ein von Hand vergebenes Wort kann
        ''' genauso lauten wie ein erkanntes. Zwei gleich beschriftete Zeilen mit verschiedenen Zahlen
        ''' waeren fuer den Benutzer ein Raetsel, deshalb steht dort EINE Zeile - und ein Klick darauf
        ''' meint jeden der zusammengefassten Begriffe.</summary>
        Public ReadOnly Property FilterTags As IReadOnlyList(Of String)

        ''' <summary>Was der Benutzer liest. Weicht nur bei erkannten Stichwoertern vom Filterwert
        ''' ab: die bleiben in der Datenbank englisch, damit ein Bild den Sprachwechsel ueberlebt.</summary>
        Public ReadOnly Property DisplayTag As String
        Public ReadOnly Property Count As Integer

        ''' <summary>Vom Modell erkannt statt von Hand vergeben. Wahr nur, wenn ALLE in dieser Zeile
        ''' zusammengefassten Begriffe erkannt sind.</summary>
        Public ReadOnly Property IsAiTag As Boolean

        ''' <summary>Von welchem Server dieses Stichwort kommt; leer heisst aus dem lokalen Katalog.
        ''' Der NAME der Quelle steht hier, kein Ja/Nein: er entscheidet, welche Eintraege in der
        ''' Liste der gerade sichtbaren Quelle stehenbleiben.</summary>
        Public ReadOnly Property ServerSource As String

        ''' <summary>Kennung des Stichworts auf dem Server - der Filter laeuft ueber sie, nicht ueber
        ''' den Namen.</summary>
        Public ReadOnly Property ServerId As String

        Public ReadOnly Property IsFromServer As Boolean
            Get
                Return ServerSource.Length > 0
            End Get
        End Property

        ''' <summary>Beschriftung mit der Anzahl in Klammern, etwa "urlaub (42)". OHNE Anzahl, wo es
        ''' keine gibt - "(0)" hinter einem Stichwort liest sich wie "keine Bilder".</summary>
        Public ReadOnly Property Label As String
            Get
                Return If(Count > 0, $"{DisplayTag} ({Count})", DisplayTag)
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
