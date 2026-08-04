Imports System.ComponentModel
Imports System.Runtime.CompilerServices
Imports FerrumPix.Services

Namespace Models

    ''' <summary>
    ''' Eine Person im Auswahlmenue der Galerie, mit der Anzahl Bilder, auf denen sie steht.
    '''
    ''' Eigene Klasse und kein Tupel, aus demselben Grund wie bei den Stichwoertern: die Markierung
    ''' aendert sich zur Laufzeit. Wer im Infopanel auf eine Person klickt, soll den Haken hier
    ''' gesetzt sehen, ohne dass die ganze Liste neu gebaut wird.
    '''
    ''' Anders als ein Stichwort hat eine Person eine ID, die vom Namen unabhaengig ist. Gefiltert
    ''' wird ueber die ID, angezeigt der Name - eine Umbenennung darf einen laufenden Filter nicht
    ''' ins Leere laufen lassen, und zwei Personen duerfen voruebergehend denselben Namen tragen,
    ''' bis jemand sie zusammenfuehrt.
    ''' </summary>
    Public Class PersonFilterOption
        Implements INotifyPropertyChanged

        Private _isSelected As Boolean

        Public Sub New(id As String, name As String, count As Integer, isSelected As Boolean,
                       Optional isFromImmich As Boolean = False)
            Me.Id = If(id, "")
            Me.Name = If(name, "")
            Me.Count = count
            Me.IsFromImmich = isFromImmich
            _isSelected = isSelected
        End Sub

        Public ReadOnly Property Id As String
        Public ReadOnly Property Name As String
        Public ReadOnly Property Count As Integer

        ''' <summary>Kommt dieser Eintrag vom Immich-Server statt aus dem lokalen Katalog?
        '''
        ''' Die beiden Bestaende lassen sich NICHT verunden: der Server filtert nach genau einer
        ''' Person oder einer Stadt, und ein Immich-Element steht in keiner lokalen Tabelle. Ein
        ''' Immich-Eintrag wird deshalb allein gewaehlt und oeffnet die Server-Abfrage; die Liste
        ''' zeigt ihn unter einer eigenen Ueberschrift, damit man nicht auf eine Verundung
        ''' hofft, die es nicht gibt.</summary>
        Public ReadOnly Property IsFromImmich As Boolean

        ''' <summary>Traegt dieser Eintrag die Zwischenueberschrift "Immich"? Gesetzt wird sie beim
        ''' ERSTEN Eintrag vom Server. Eine eigene Gruppierung der Liste waere dafuer zu viel
        ''' Maschinerie - die Ueberschrift gehoert zu genau einer Zeile.</summary>
        Public Property ShowsImmichHeader As Boolean

        ''' <summary>Hat die Gruppe schon einen Namen? Direkt nach der Erkennung hat sie keinen -
        ''' die Gruppierung entsteht von selbst, der Name kommt vom Benutzer. In die Filterliste
        ''' kommen nur benannte Gruppen (siehe GalleryViewModel.RefreshPersonFilterOptions); benannt
        ''' wird am Bild im Infopanel, wo man sieht, WEN man benennt.</summary>
        Public ReadOnly Property IsNamed As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(Name)
            End Get
        End Property

        ''' <summary>Beschriftung mit der Anzahl in Klammern, etwa "Christina (42)". OHNE Anzahl, wo
        ''' es keine gibt: der Immich-Server liefert seine Personen ohne Bildzahl, und "(0)" hinter
        ''' einem Namen liest sich wie "keine Bilder".</summary>
        Public ReadOnly Property Label As String
            Get
                Return If(Count > 0, $"{Name} ({Count})", Name)
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
