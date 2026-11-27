Imports System.Collections.Generic
Imports Avalonia.Controls

Namespace Models

    ''' <summary>
    ''' Haengt die Vorschaubilder einer Galerieansicht an das, was ihr Repeater WIRKLICH aufgebaut
    ''' hat: ElementPrepared schuetzt das Element vor der Verdraengung und fragt sein Bild sofort
    ''' an, ElementClearing gibt es wieder frei.
    '''
    ''' <para>Bis dahin entschied allein der <see cref="ViewportThumbnailTracker"/>, und der RECHNET
    ''' den sichtbaren Bereich aus dem Rollversatz (Raster ueber Spaltenzahl und Zeilenhoehe,
    ''' Fotowand und Gruppenansicht ueber die Tabellen im ViewModel). Lag die Rechnung neben dem,
    ''' was der Repeater zeigte, waren die gezeigten Kacheln weder angefragt noch geschuetzt. Mit
    ''' vollem Vorschau-Speicher bekamen sie dann gar kein Bild mehr, denn das Vorladen verdraengt
    ''' nichts (ImageItem.TakeBackgroundItemsWhileRoomLocked). Die Rechnung bleibt fuer das
    ''' Vorhalten ober- und unterhalb; die Pflicht, das Gezeigte zu laden, liegt hier.</para>
    '''
    ''' <para>Gefuehrt wird je Element, nicht je Datum: ein wiederverwendetes Element meldet zuerst
    ''' sein altes Datum ab (ElementClearing, oder spaetestens beim naechsten ElementPrepared, falls
    ''' ein Abmelden ausblieb). So kann kein Element zwei Bilder zugleich festhalten.</para>
    ''' </summary>
    Public NotInheritable Class RealizedTileThumbnails
        Private ReadOnly _shownByElement As New Dictionary(Of Control, ImageItem)()

        ''' <summary>Meldet sich an den Ereignissen des Repeaters an. Ein Repeater ohne Instanz
        ''' (Ansicht nicht geladen) wird uebergangen.</summary>
        Public Sub Attach(repeater As ItemsRepeater)
            If repeater Is Nothing Then Return
            AddHandler repeater.ElementPrepared, AddressOf OnElementPrepared
            AddHandler repeater.ElementClearing, AddressOf OnElementClearing
        End Sub

        ''' <summary>Wie viele Elemente gerade ein Bild festhalten. Fuer den Pruefstand.</summary>
        Public ReadOnly Property Count As Integer
            Get
                Return _shownByElement.Count
            End Get
        End Property

        Private Sub OnElementPrepared(sender As Object, e As ItemsRepeaterElementPreparedEventArgs)
            Dim repeater = TryCast(sender, ItemsRepeater)
            Dim item As ImageItem = Nothing
            ' Das Datum ueber den Index aus der Quelle, nicht ueber den DataContext: der steht bei
            ' einer Fabrik (Gruppenansicht) nicht zwingend schon fest.
            Dim source = repeater?.ItemsSourceView
            If source IsNot Nothing AndAlso e.Index >= 0 AndAlso e.Index < source.Count Then
                item = TryCast(source.GetAt(e.Index), ImageItem)
            End If
            If item Is Nothing Then item = TryCast(e.Element?.DataContext, ImageItem)
            Show(e.Element, item)
        End Sub

        Private Sub OnElementClearing(sender As Object, e As ItemsRepeaterElementClearingEventArgs)
            Show(e.Element, Nothing)
        End Sub

        Private Sub Show(element As Control, item As ImageItem)
            If element Is Nothing Then Return
            Dim previous As ImageItem = Nothing
            If _shownByElement.TryGetValue(element, previous) Then
                If Object.ReferenceEquals(previous, item) Then Return
                _shownByElement.Remove(element)
                previous.MarkTileCleared()
            End If
            If item Is Nothing Then Return
            _shownByElement(element) = item
            item.MarkTileRealized()
        End Sub
    End Class

End Namespace
