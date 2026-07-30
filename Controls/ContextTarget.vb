Imports System
Imports System.Collections.Generic
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.VisualTree
Imports FerrumPix.Models

Namespace Controls

    ''' <summary>
    ''' Welches Element ein Rechtsklick MEINT. Eine Stelle fuer alle Kontextmenues.
    '''
    ''' Vorher hatte jeder Aufrufort seine eigene Fassung, und keine davon stimmte: im Filmstreifen
    ''' traf die Aktion das Bild auf der Buehne statt die angeklickte Kachel. Der Fehler steckte
    ''' nicht in der Absicht, sondern in der Suche - deshalb gibt es sie jetzt genau einmal.
    '''
    ''' Gesucht wird ueber einen echten TREFFERTEST an der Zeigerposition und dann durch den
    ''' SICHTBAREN Baum nach oben. Zwei Gruende:
    '''
    ''' - `e.Source` taugt nicht: das Ereignis blubbert bis zu dem Element, an dem das Menue haengt,
    '''   und dessen Quelle ist dort nicht verlaesslich die Kachel.
    ''' - Der LOGISCHE Elternteil taugt nicht: bei einer virtualisierten Liste ist er nicht gesetzt.
    ''' </summary>
    Public NotInheritable Class ContextTarget

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Das Element unter dem Zeiger, oder Nothing wenn dort keines liegt.
        ''' </summary>
        ''' <param name="e">Das Ereignis mit der Zeigerposition.</param>
        ''' <param name="container">Worin gesucht wird - die Liste oder das Raster. Nothing oder
        ''' unsichtbar heisst: kein Treffer.</param>
        Public Shared Function UnderPointer(e As ContextRequestedEventArgs, container As Control) As ImageItem
            If e Is Nothing OrElse container Is Nothing OrElse Not container.IsVisible Then Return Nothing

            Dim pos As Point
            ' Kommt das Menue ueber die Tastatur, gibt es keine Position - dann ist nichts gemeint.
            If Not e.TryGetPosition(container, pos) Then Return Nothing
            If pos.X < 0 OrElse pos.Y < 0 OrElse
               pos.X > container.Bounds.Width OrElse pos.Y > container.Bounds.Height Then Return Nothing

            Return FromVisualTree(TryCast(container.InputHitTest(pos), Visual))
        End Function

        ''' <summary>Vom getroffenen Element aus nach oben, bis eines einen <see cref="ImageItem"/>
        ''' als Datenkontext traegt. Getroffen wird immer ein Bild oder ein Rahmen INNERHALB der
        ''' Kachel, nie die Kachel selbst.</summary>
        Private Shared Function FromVisualTree(target As Visual) As ImageItem
            While target IsNot Nothing
                Dim ctrl = TryCast(target, Control)
                If ctrl IsNot Nothing Then
                    Dim item = TryCast(ctrl.DataContext, ImageItem)
                    If item IsNot Nothing Then Return item
                End If
                target = TryCast(target.GetVisualParent(), Visual)
            End While
            Return Nothing
        End Function

        ''' <summary>
        ''' Die betroffenen Elemente fuer ein Kontextmenue.
        '''
        ''' Die Regel dahinter: wer auf ein Element klickt, das NICHT in der Auswahl liegt, meint
        ''' dieses eine. Wer auf ein Element der Auswahl klickt, meint die ganze Auswahl. Genau so
        ''' verhaelt sich jeder Dateimanager, und genau das erwartet man beim Loeschen.
        ''' </summary>
        ''' <param name="hit">Das Element unter dem Zeiger, oder Nothing.</param>
        ''' <param name="selection">Die aktuelle Auswahl des Bereichs, kann leer sein.</param>
        ''' <param name="fallback">Was gilt, wenn weder getroffen noch ausgewaehlt etwas ist -
        ''' etwa das Bild auf der Buehne des Betrachters.</param>
        Public Shared Function Affected(hit As ImageItem,
                                          selection As IEnumerable(Of ImageItem),
                                          fallback As ImageItem) As IList(Of ImageItem)
            Dim selected As New List(Of ImageItem)()
            If selection IsNot Nothing Then
                For Each i In selection
                    If i IsNot Nothing Then selected.Add(i)
                Next
            End If

            If hit IsNot Nothing Then
                Dim isInSelection = selected.Any(Function(i) Object.ReferenceEquals(i, hit) OrElse
                                                              SamePathIdentity(i, hit))
                If isInSelection AndAlso selected.Count > 1 Then Return selected
                Return New List(Of ImageItem) From {hit}
            End If

            If selected.Count > 0 Then Return selected
            If fallback IsNot Nothing Then Return New List(Of ImageItem) From {fallback}
            Return New List(Of ImageItem)()
        End Function

        Private Shared Function SamePathIdentity(a As ImageItem, b As ImageItem) As Boolean
            If a Is Nothing OrElse b Is Nothing Then Return False
            If String.IsNullOrEmpty(a.FilePath) OrElse String.IsNullOrEmpty(b.FilePath) Then Return False
            Return Services.PathIdentity.AreSame(a.FilePath, b.FilePath)
        End Function

    End Class

End Namespace
