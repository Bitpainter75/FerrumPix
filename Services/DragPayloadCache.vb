Imports System.Collections.Generic

Namespace Services

    ''' <summary>Was die Anwendung gerade SELBST zieht - im Prozess gemerkt, nicht ueber das
    ''' Fenstersystem gelesen.
    '''
    ''' DER GRUND IST GEMESSEN AM FREMDEN QUELLTEXT, nicht vermutet: unter X11 laeuft auch ein
    ''' anwendungsinterner Zug ueber Xdnd, und das Lesen der Ziehlast geht dort so:
    '''
    '''   IDataTransfer.TryGetValue(...)  -&gt;  DragDropDataReader.TryGet(format)
    '''                                   -&gt;  TryGetAsync(...).GetAwaiter().GetResult()
    '''                                   -&gt;  SynchronousXEventWaiter.WaitForEvent(...)
    '''
    ''' Der letzte Schritt BLOCKIERT den aufrufenden Faden und pollt dabei X-Ereignisse. Bei einem
    ''' eigenen Zug ist die QUELLE dieselbe Anwendung auf demselben Faden: sie muesste die Anfrage
    ''' nach den Daten beantworten, sitzt aber selbst in dieser Warteschleife. Ergebnis ist ein
    ''' Poll-Lauf bis zum Zeitablauf - und zwar bei JEDEM Zeigerbericht waehrend des Ziehens.
    ''' Sichtbar wurde das als hohe Prozessorlast und eine stehende Anwendung, sobald jemand ein
    ''' Bild auf einen Baumknoten zog (Nutzerbefund 2026-08-10).
    '''
    ''' Deshalb merkt sich die Ziehquelle ihre Pfade hier. Wer waehrend eines eigenen Zuges wissen
    ''' will, was gezogen wird, fragt DIESE Stelle; das Fenstersystem wird nur noch fuer FREMDE
    ''' Zuege befragt, und dort antwortet ein anderer Prozess, der nicht auf uns wartet.</summary>
    Public NotInheritable Class DragPayloadCache

        Private Sub New()
        End Sub

        Private Shared _paths As List(Of String) = Nothing
        Private Shared ReadOnly _lock As New Object()

        ''' <summary>Beginnt einen eigenen Zug. Die Liste wird KOPIERT: der Aufrufer darf seine
        ''' Auswahl waehrend des Zuges aendern, ohne dass die Ziehlast mitwandert.</summary>
        Public Shared Sub BeginDrag(paths As IEnumerable(Of String))
            SyncLock _lock
                _paths = If(paths, Enumerable.Empty(Of String)()).
                         Where(Function(p) Not String.IsNullOrEmpty(p)).ToList()
            End SyncLock
        End Sub

        ''' <summary>Beendet den eigenen Zug. MUSS in einem Finally stehen - bleibt der Stand
        ''' liegen, hielte die Anwendung einen fremden Zug spaeter faelschlich fuer den eigenen.</summary>
        Public Shared Sub EndDrag()
            SyncLock _lock
                _paths = Nothing
            End SyncLock
        End Sub

        Public Shared ReadOnly Property IsDragging As Boolean
            Get
                SyncLock _lock
                    Return _paths IsNot Nothing
                End SyncLock
            End Get
        End Property

        ''' <summary>Die gezogenen Pfade, oder eine leere Liste ausserhalb eines eigenen Zuges.</summary>
        Public Shared Function Paths() As List(Of String)
            SyncLock _lock
                Return If(_paths Is Nothing, New List(Of String)(), New List(Of String)(_paths))
            End SyncLock
        End Function

    End Class

End Namespace
