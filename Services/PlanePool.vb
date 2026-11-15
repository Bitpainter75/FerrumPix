Imports System
Imports System.Buffers
Imports System.Collections.Generic
Imports System.Runtime.CompilerServices
Imports System.Threading

Namespace Services

    ''' <summary>
    ''' Der Vorrat fuer Felder in BILDGROESSE: die Ebenen, Quell- und Zielpuffer der Filterstufen.
    '''
    ''' WARUM NICHT EINFACH ArrayPool.Shared. Der rundet jede Miete auf die naechste Zweierpotenz:
    ''' ein Feld fuer 45,4 Millionen Bildpunkte wird eines fuer 67,1 Millionen, also 48 Prozent mehr.
    ''' Und er BEHAELT, was zurueckkommt, je Groessenklasse: die Felder der vorigen Stufe liegen noch
    ''' da, waehrend die naechste ihre eigenen in einer anderen Klasse holt. Bei einer Ausgabe mit 45
    ''' Megapixeln und allen vier Detailstufen waren das gemessen 3,5 GB Spitze.
    '''
    ''' WAS HIER ANDERS IST. Waehrend einer Ausgabe (<see cref="BeginOutput"/>) bekommt eine Miete
    ''' ab <see cref="LargeBytes"/> ein Feld in GENAU der verlangten Laenge. Zurueckgegeben liegt es
    ''' bereit fuer die naechste Miete derselben Laenge, und in einer Ausgabe haben alle Ebenen eines
    ''' Bildes dieselbe. Endet die letzte laufende Ausgabe, wird der Vorrat geleert.
    '''
    ''' AUSSERHALB EINER AUSGABE BLEIBT ALLES BEIM ALTEN, und fuer kleine Felder immer: die Vorschau
    ''' rechnet Regionen in staendig wechselnden Groessen, dort faende ein Vorrat mit genauer Laenge
    ''' kaum je ein passendes Feld, und die Zweierpotenzen des gemeinsamen Vorrats sind genau das
    ''' Richtige.
    '''
    ''' Wohin ein Feld beim Zurueckgeben gehoert, sagt nicht seine Laenge, sondern ein Merkposten je
    ''' vermietetem Feld: ein Feld von hier darf nie in den gemeinsamen Vorrat (der nimmt nur seine
    ''' eigenen Laengen und wirft sonst), und eines von dort nie hierher.
    ''' </summary>
    Friend NotInheritable Class PlanePool(Of T)

        Private Sub New()
        End Sub

        Private Shared ReadOnly _lock As New Object()

        ''' <summary>Bereitliegende Felder je Laenge.</summary>
        Private Shared ReadOnly _free As New Dictionary(Of Integer, Stack(Of T()))()

        ''' <summary>Die Felder, die von hier stammen, vermietet oder bereitliegend. Schwach
        ''' gehalten: ein Feld, das niemand zurueckgibt, bleibt nicht daran haengen.</summary>
        Private Shared ReadOnly _own As New ConditionalWeakTable(Of T(), Object)()

        Private Shared ReadOnly _marker As New Object()

        ''' <summary>Ein Feld mit mindestens <paramref name="minimumLength"/> Elementen. Der Inhalt
        ''' ist beliebig, wie beim gemeinsamen Vorrat. Zurueck ueber <see cref="[Return]"/>.</summary>
        Public Shared Function Rent(minimumLength As Integer) As T()
            If Not PlanePoolScope.IsOutputRunning OrElse
               CLng(minimumLength) * Unsafe.SizeOf(Of T)() < PlanePoolScope.LargeBytes Then
                Return ArrayPool(Of T).Shared.Rent(minimumLength)
            End If
            SyncLock _lock
                Dim stack As Stack(Of T()) = Nothing
                If _free.TryGetValue(minimumLength, stack) AndAlso stack.Count > 0 Then Return stack.Pop()
            End SyncLock
            Dim fresh = New T(minimumLength - 1) {}
            _own.Add(fresh, _marker)
            PlanePoolScope.Register(AddressOf Clear)
            Return fresh
        End Function

        ''' <summary>Gibt ein Feld aus <see cref="Rent"/> zurueck. Nothing geschieht nichts.</summary>
        Public Shared Sub [Return](array As T())
            If array Is Nothing Then Return
            Dim marker As Object = Nothing
            If Not _own.TryGetValue(array, marker) Then
                ArrayPool(Of T).Shared.Return(array)
                Return
            End If
            ' Nach dem Ende der Ausgabe nichts mehr aufheben: das Feld geht an die
            ' Speicherbereinigung.
            If Not PlanePoolScope.IsOutputRunning Then Return
            SyncLock _lock
                Dim stack As Stack(Of T()) = Nothing
                If Not _free.TryGetValue(array.Length, stack) Then
                    stack = New Stack(Of T())()
                    _free(array.Length) = stack
                End If
                stack.Push(array)
            End SyncLock
        End Sub

        ''' <summary>Laesst alle bereitliegenden Felder los.</summary>
        Private Shared Sub Clear()
            SyncLock _lock
                _free.Clear()
            End SyncLock
        End Sub

        ''' <summary>Fuer den Pruefstand: wie viele Felder gerade bereitliegen.</summary>
        Friend Shared ReadOnly Property FreeCount As Integer
            Get
                SyncLock _lock
                    Dim count = 0
                    For Each stack In _free.Values
                        count += stack.Count
                    Next
                    Return count
                End SyncLock
            End Get
        End Property
    End Class

    ''' <summary>Der Rahmen einer Ausgabe fuer <see cref="PlanePool(Of T)"/>: solange mindestens eine
    ''' laeuft, vermietet der Vorrat grosse Felder in genauer Laenge; mit dem Ende der letzten wird
    ''' er geleert.</summary>
    Friend NotInheritable Class PlanePoolScope
        Implements IDisposable

        ''' <summary>Ab dieser Groesse in Bytes gilt ein Feld als bildgross. Darunter (Zeilen,
        ''' Kacheln) bleibt es beim gemeinsamen Vorrat.</summary>
        Friend Const LargeBytes As Long = 4L * 1024L * 1024L

        Private Shared _running As Integer
        Private Shared ReadOnly _clearLock As New Object()
        Private Shared ReadOnly _clearers As New HashSet(Of Action)()

        Private _ended As Integer

        Private Sub New()
        End Sub

        Friend Shared ReadOnly Property IsOutputRunning As Boolean
            Get
                Return Volatile.Read(_running) > 0
            End Get
        End Property

        ''' <summary>Beginnt eine Ausgabe. Das Ergebnis gehoert in einen Using-Block um sie.</summary>
        Friend Shared Function BeginOutput() As PlanePoolScope
            Interlocked.Increment(_running)
            Return New PlanePoolScope()
        End Function

        ''' <summary>Meldet das Leeren eines Vorrats an (je Elementtyp einer).</summary>
        Friend Shared Sub Register(clear As Action)
            SyncLock _clearLock
                _clearers.Add(clear)
            End SyncLock
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Interlocked.Exchange(_ended, 1) <> 0 Then Return
            If Interlocked.Decrement(_running) > 0 Then Return
            Dim clearers As Action()
            SyncLock _clearLock
                clearers = New Action(_clearers.Count - 1) {}
                _clearers.CopyTo(clearers)
            End SyncLock
            For Each clear In clearers
                clear()
            Next
        End Sub
    End Class

End Namespace
