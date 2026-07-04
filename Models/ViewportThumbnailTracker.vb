Imports System.Collections.Generic

Namespace Models

    ''' <summary>
    ''' Gemeinsame Sichtbereichs-Logik für Thumbnail-Anfragen, von der Gallery-Grid und den
    ''' Filmstreifen in Viewer/Editor gleichermaßen genutzt wird, damit sich das Verhalten
    ''' (Keep-Alive-Puffer, verzögertes Evicten, Reihenfolge der Anfragen) nicht auseinander
    ''' entwickelt. Jede Ansicht hält ihre eigene Instanz, da der Sichtbereich pro Ansicht
    ''' unabhängig verfolgt werden muss.
    ''' </summary>
    Public NotInheritable Class ViewportThumbnailTracker
        Private _keepFirst As Integer = -1
        Private _keepLast As Integer = -1

        ''' <summary>Wie viele Elemente über den sichtbaren Bereich hinaus "warmgehalten" werden,
        ''' als Vielfaches der sichtbaren Anzahl (mindestens minKeepBuffer).</summary>
        Private Const KeepAliveMultiplier As Integer = 3
        Private Const MinKeepBuffer As Integer = 8

        ''' <summary>
        ''' items: die vollständige flache Liste, in der firstVisible/lastVisible indizieren
        ''' (vm.Items für die Gallery, vm.FilmstripItems für Viewer/Editor).
        ''' firstVisible/lastVisible: der aktuell tatsächlich sichtbare Bereich (bereits von der
        ''' aufrufenden Ansicht aus ihrer eigenen Geometrie berechnet - Grid-Zeilen vs. horizontale
        ''' Filmstreifen-Position bleibt bewusst ansichtsspezifisch).
        ''' </summary>
        Public Sub RequestRange(items As IReadOnlyList(Of ImageItem), firstVisible As Integer, lastVisible As Integer)
            If items Is Nothing OrElse items.Count = 0 Then Return
            firstVisible = Math.Max(0, firstVisible)
            lastVisible = Math.Min(items.Count - 1, lastVisible)
            If firstVisible > lastVisible Then Return

            Dim viewSize = Math.Max(1, lastVisible - firstVisible + 1)
            Dim keepBuffer = Math.Max(MinKeepBuffer, viewSize * KeepAliveMultiplier)
            Dim keepFirst = Math.Max(0, firstVisible - keepBuffer)
            Dim keepLast = Math.Min(items.Count - 1, lastVisible + keepBuffer)

            ' Delta-Evict: nur für Elemente aufgerufen, die den Keep-Alive-Bereich gerade verlassen
            ' haben. Bereits fertig geladene Thumbnails werden dadurch NICHT mehr sofort disposed -
            ' sie bleiben resident und werden nur noch über den globalen LRU-Cache in ImageItem
            ' (TouchResident/MaxResidentThumbnails) verdrängt. Hier storniert EvictThumbnail() nur
            ' noch angeforderte, aber noch nicht fertig geladene Elemente.
            If _keepFirst >= 0 Then
                Dim topEnd = Math.Min(keepFirst - 1, _keepLast)
                For i = _keepFirst To topEnd
                    If i < items.Count Then items(i)?.EvictThumbnail()
                Next
                Dim botStart = Math.Max(keepLast + 1, _keepFirst)
                For i = botStart To _keepLast
                    If i < items.Count Then items(i)?.EvictThumbnail()
                Next
            End If
            _keepFirst = keepFirst
            _keepLast = keepLast

            ' In umgekehrter Reihenfolge aufbauen, damit die LIFO-Warteschlange oben zuerst befüllt
            Dim viewportItems As New List(Of ImageItem)()
            For i = lastVisible To firstVisible Step -1
                Dim item = items(i)
                If item IsNot Nothing AndAlso item.IsImage Then viewportItems.Add(item)
            Next
            ImageItem.SetViewportThumbnailRequests(viewportItems)
        End Sub

        ''' <summary>Setzt die Verfolgung zurück (z.B. bei Ordnerwechsel), damit der nächste Aufruf
        ''' nicht fälschlich gegen einen veralteten Keep-Alive-Bereich delta-evicted.</summary>
        Public Sub Reset()
            _keepFirst = -1
            _keepLast = -1
        End Sub
    End Class

End Namespace
