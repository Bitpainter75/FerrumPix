Imports System
Imports System.Collections.Generic
Imports System.Linq

Namespace Services

    ''' <summary>Die Ordnung der Stichwörter als Baum: zu jedem Wort höchstens EIN übergeordnetes.
    '''
    ''' DIE ORDNUNG GEHÖRT DEM WORT, NICHT DEM BILD. Am Bild steht weiter die flache Liste
    ''' ("Hunde"), und nur hier steht, dass "Hunde" unter "Tiere" hängt. Suche, Beistelldateien und
    ''' die Server bleiben davon unberührt, und ein Bestand muss nicht umgeschrieben werden, wenn
    ''' jemand ein Wort umhängt. Der Preis: ein Wort kann nur an einer Stelle stehen.
    '''
    ''' Eine Momentaufnahme ohne eigene Datenhaltung; gelesen und geschrieben wird über
    ''' <see cref="LibraryService.GetKeywordTree"/> und <see cref="LibraryService.SetKeywordParent"/>.
    ''' Jeder Gang durch den Baum merkt sich, wo er war: eine Schleife in der Tabelle (von Hand
    ''' hineingeschrieben oder aus zwei Katalogen zusammengeführt) darf nichts aufhängen.</summary>
    Public NotInheritable Class KeywordTree

        Private ReadOnly _parents As Dictionary(Of String, String)
        Private ReadOnly _children As New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)

        Public Sub New(parents As IEnumerable(Of KeyValuePair(Of String, String)))
            _parents = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For Each pair In If(parents, Enumerable.Empty(Of KeyValuePair(Of String, String))())
                Dim name = If(pair.Key, "").Trim()
                Dim parent = If(pair.Value, "").Trim()
                If name.Length = 0 OrElse parent.Length = 0 Then Continue For
                If String.Equals(name, parent, StringComparison.OrdinalIgnoreCase) Then Continue For
                _parents(name) = parent
            Next
            For Each pair In _parents
                Dim list As List(Of String) = Nothing
                If Not _children.TryGetValue(pair.Value, list) Then
                    list = New List(Of String)()
                    _children(pair.Value) = list
                End If
                list.Add(pair.Key)
            Next
        End Sub

        ''' <summary>Gibt es überhaupt eine Ordnung? Ohne sie bleibt die Stichwortauswahl die
        ''' flache Liste, die sie immer war.</summary>
        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return _parents.Count = 0
            End Get
        End Property

        ''' <summary>Das übergeordnete Wort, leer auf der obersten Ebene.</summary>
        Public Function ParentOf(name As String) As String
            Dim parent As String = Nothing
            If name IsNot Nothing AndAlso _parents.TryGetValue(name.Trim(), parent) Then Return parent
            Return ""
        End Function

        ''' <summary>Die direkt untergeordneten Wörter, nach dem Alphabet.</summary>
        Public Function ChildrenOf(name As String) As List(Of String)
            Dim list As List(Of String) = Nothing
            If name Is Nothing OrElse Not _children.TryGetValue(name.Trim(), list) Then Return New List(Of String)()
            Return list.OrderBy(Function(t) t, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        Public Function HasChildren(name As String) As Boolean
            Return name IsNot Nothing AndAlso _children.ContainsKey(name.Trim())
        End Function

        ''' <summary>Alles, was unter dem Wort hängt, über alle Stufen und ohne das Wort selbst.</summary>
        Public Function DescendantsOf(name As String) As List(Of String)
            Dim result As New List(Of String)()
            If String.IsNullOrWhiteSpace(name) Then Return result
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {name.Trim()}
            Dim pending As New Queue(Of String)()
            pending.Enqueue(name.Trim())
            While pending.Count > 0
                Dim list As List(Of String) = Nothing
                If Not _children.TryGetValue(pending.Dequeue(), list) Then Continue While
                For Each child In list
                    If Not seen.Add(child) Then Continue For
                    result.Add(child)
                    pending.Enqueue(child)
                Next
            End While
            Return result
        End Function

        ''' <summary>Der Weg von der obersten Ebene bis zum Wort, das Wort als letztes.</summary>
        Public Function PathOf(name As String) As List(Of String)
            Dim result As New List(Of String)()
            If String.IsNullOrWhiteSpace(name) Then Return result
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim current = name.Trim()
            While current.Length > 0 AndAlso seen.Add(current)
                result.Insert(0, current)
                current = ParentOf(current)
            End While
            Return result
        End Function

        ''' <summary>Schlösse sich ein Kreis, wenn <paramref name="name"/> unter
        ''' <paramref name="parent"/> käme? Das ist der Fall, wenn das neue übergeordnete Wort das
        ''' Wort selbst ist oder schon irgendwo unter ihm hängt.</summary>
        Public Function WouldCycle(name As String, parent As String) As Boolean
            Dim child = If(name, "").Trim()
            Dim above = If(parent, "").Trim()
            If child.Length = 0 OrElse above.Length = 0 Then Return False
            If String.Equals(child, above, StringComparison.OrdinalIgnoreCase) Then Return True
            Return DescendantsOf(child).Any(Function(t) String.Equals(t, above, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Alle Wörter, die in der Ordnung vorkommen, als über- oder untergeordnetes.</summary>
        Public Function AllNames() As List(Of String)
            Dim names As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each pair In _parents
                names.Add(pair.Key)
                names.Add(pair.Value)
            Next
            Return names.ToList()
        End Function

        ''' <summary>Ein getippter Weg in seine Stufen: "Tiere > Hunde" oder "Tiere|Hunde". Der
        ''' Schrägstrich trennt bewusst NICHT, er steht in gewöhnlichen Stichwörtern ("s/w").
        ''' Ein Text ohne Trennzeichen ergibt eine einzige Stufe.</summary>
        Public Shared Function SplitPath(text As String) As List(Of String)
            Return If(text, "").Split({">"c, "|"c}, StringSplitOptions.RemoveEmptyEntries).
                Select(Function(part) LibraryService.NormalizeKeyword(part)).
                Where(Function(part) part.Length > 0).
                ToList()
        End Function

    End Class

End Namespace
