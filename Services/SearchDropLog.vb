Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Was ein Suchlauf ueber den Katalog verworfen hat, und warum - fuer das
    ''' Diagnoseprotokoll.
    '''
    ''' Die Zahl in den Listen von Stichwoertern, Personen und Orten zaehlt Katalogzeilen; die
    ''' Trefferliste zeigt nur, was davon wirklich auf der Platte liegt und angezeigt werden darf.
    ''' Klafft beides auseinander (Nutzerbefund: 33 gezaehlt, nichts gezeigt), stand bisher
    ''' nirgends, WELCHE Zeilen herausgefallen sind. Diese Klasse sammelt sie waehrend des Laufs und
    ''' schreibt am Ende je Grund die Zahl und einige Beispielpfade.
    '''
    ''' Gemeldet wird nur, was die Suche GETROFFEN haette. Ohne Stichwort, Person oder Ort geht der
    ''' Lauf ueber den ganzen Katalog, und jede fehlende Datei des Bestands fiele heraus - eine Zahl,
    ''' die mit der Frage nichts zu tun hat. Geprueft wird deshalb erst am Ende und nur ueber die
    ''' verworfenen Zeilen, mit derselben Bedingung wie fuer die Treffer.
    '''
    ''' Nur bei eingeschaltetem Diagnoseprotokoll: <see cref="CreateIfEnabled"/> gibt sonst Nothing
    ''' zurueck, und der Lauf merkt sich nichts.</summary>
    Public NotInheritable Class SearchDropLog

        Public Const ReasonMissing As String = "Datei fehlt"
        Public Const ReasonTrash As String = "Papierkorb"
        Public Const ReasonHidden As String = "versteckter Ordner"
        Public Const ReasonExtension As String = "Endung wird nicht angezeigt"

        ''' <summary>So viele Pfade je Grund stehen als Beispiel im Protokoll.</summary>
        Public Const MaxSamples As Integer = 10

        Private ReadOnly _dropped As New List(Of (Reason As String, Meta As LibraryImageMeta))()
        Private ReadOnly _lock As New Object()

        ''' <summary>Ein neues Protokoll fuer einen Lauf, oder Nothing, wenn das Diagnoseprotokoll aus
        ''' ist.</summary>
        Public Shared Function CreateIfEnabled() As SearchDropLog
            Return If(DiagnosticLogService.IsVerboseEnabled, New SearchDropLog(), Nothing)
        End Function

        Public Sub Record(reason As String, meta As LibraryImageMeta)
            If meta Is Nothing Then Return
            SyncLock _lock
                _dropped.Add((reason, meta))
            End SyncLock
        End Sub

        Public ReadOnly Property Count As Integer
            Get
                SyncLock _lock
                    Return _dropped.Count
                End SyncLock
            End Get
        End Property

        ''' <summary>Je Grund die Zahl der verworfenen Zeilen, die <paramref name="matches"/> als
        ''' Treffer erkennt, mit bis zu <see cref="MaxSamples"/> Pfaden. Leer, wenn keine davon
        ''' gepasst haette.</summary>
        Public Async Function SummarizeAsync(matches As Func(Of LibraryImageMeta, Task(Of Boolean)),
                                             token As CancellationToken) As Task(Of String)
            Dim snapshot As List(Of (Reason As String, Meta As LibraryImageMeta))
            SyncLock _lock
                snapshot = _dropped.ToList()
            End SyncLock
            If snapshot.Count = 0 OrElse matches Is Nothing Then Return ""

            ' Reihenfolge der Gruende fest, damit zwei Protokolle sich Zeile fuer Zeile vergleichen
            ' lassen.
            Dim order = {ReasonMissing, ReasonTrash, ReasonHidden, ReasonExtension}
            Dim counts As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
            Dim samples As New Dictionary(Of String, List(Of String))(StringComparer.Ordinal)
            For Each entry In snapshot
                token.ThrowIfCancellationRequested()
                If Not Await matches(entry.Meta) Then Continue For
                Dim before = 0
                counts.TryGetValue(entry.Reason, before)
                counts(entry.Reason) = before + 1
                Dim list As List(Of String) = Nothing
                If Not samples.TryGetValue(entry.Reason, list) Then
                    list = New List(Of String)()
                    samples(entry.Reason) = list
                End If
                If list.Count < MaxSamples Then list.Add(entry.Meta.FilePath)
            Next
            If counts.Count = 0 Then Return ""

            Dim reasons = order.Where(Function(r) counts.ContainsKey(r)).
                Concat(counts.Keys.Where(Function(r) Not order.Contains(r)).OrderBy(Function(r) r, StringComparer.Ordinal))
            Dim parts As New List(Of String)()
            For Each reason In reasons
                parts.Add($"{reason}: {counts(reason)} ({String.Join(" | ", samples(reason))})")
            Next
            Return String.Join("; ", parts)
        End Function

    End Class

End Namespace
