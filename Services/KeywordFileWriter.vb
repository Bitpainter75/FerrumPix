Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>
    ''' Zieht die manuellen Stichwoerter eines Bildes in die Datei selbst nach, wenn die Einstellung
    ''' "Stichwoerter in die Bilddatei schreiben" (XMP, IPTC oder beides) an ist.
    '''
    ''' WANN: nur aus einer bewussten Aenderung (LibraryService.SetTags mit syncToXmp, also Galerie,
    ''' Betrachter und Editor), nie aus Kopier- oder Importwegen. Das ist dieselbe Grenze wie beim
    ''' XMP-Katalogabgleich: die Datei aendert sich, weil der Nutzer ein Stichwort vergeben hat, und
    ''' nur dann.
    '''
    ''' WIE: auf einem eigenen Faden und je Datei zusammengefasst. Ein JPEG wird dabei ganz neu
    ''' geschrieben; ein Stichwort auf fuenfhundert markierten Bildern haelt so die Oberflaeche nicht
    ''' an, und drei schnelle Aenderungen am selben Bild ergeben einen Schreibvorgang. Gelesen wird
    ''' der Stand des Katalogs erst beim Schreiben, nicht beim Einreihen.
    '''
    ''' WAS NIE: KI-Stichwoerter. Sie stehen in einer eigenen Tabelle, und kaemen sie in die Datei,
    ''' laese sie der naechste Import als manuelle zurueck (siehe KeywordImportService).
    ''' </summary>
    Public NotInheritable Class KeywordFileWriter

        Private Shared ReadOnly _lock As New Object()
        Private Shared ReadOnly _pending As New List(Of String)()
        Private Shared ReadOnly _pendingSet As New HashSet(Of String)(StringComparer.Ordinal)
        Private Shared _running As Boolean

        Private Sub New()
        End Sub

        ''' <summary>Die Felder laut Einstellung. None, wenn beide Schalter aus sind.</summary>
        Public Shared Function ConfiguredFields() As KeywordFileFields
            Dim settings = AppSettingsService.Load()
            Dim fields = KeywordFileFields.None
            If settings.WriteKeywordsToFileXmp Then fields = fields Or KeywordFileFields.Xmp
            If settings.WriteKeywordsToFileIptc Then fields = fields Or KeywordFileFields.Iptc
            Return fields
        End Function

        ''' <summary>Reiht eine Datei ein, wenn die Einstellung an ist und das Format Stichwoerter
        ''' aufnehmen kann. Serverbilder und Dateien, die es nicht gibt, fallen weg.</summary>
        Public Shared Sub Queue(filePath As String)
            If String.IsNullOrWhiteSpace(filePath) Then Return
            If ConfiguredFields() = KeywordFileFields.None Then Return
            If LibraryService.IsServerPseudoPath(filePath) OrElse Not ImageProcessor.CanWriteKeywordsInto(filePath) Then Return
            SyncLock _lock
                If _pendingSet.Add(filePath) Then _pending.Add(filePath)
                If _running Then Return
                _running = True
            End SyncLock
            Dim ignored = Task.Run(AddressOf Run)
        End Sub

        Private Shared Sub Run()
            While True
                Dim path As String
                SyncLock _lock
                    If _pending.Count = 0 Then
                        _running = False
                        Return
                    End If
                    path = _pending(0)
                    _pending.RemoveAt(0)
                    _pendingSet.Remove(path)
                End SyncLock
                Try
                    Dim fields = ConfiguredFields()
                    If fields = KeywordFileFields.None OrElse Not IO.File.Exists(path) Then Continue While
                    ImageProcessor.WriteKeywordsIntoFile(path, LibraryService.Instance.GetTags(path), fields)
                Catch ex As Exception
                    DiagnosticLogService.LogException("Keywords.FileWriter", ex)
                End Try
            End While
        End Sub

        ''' <summary>Fuer die Pruefung: wartet, bis die Schlange leer ist (hoechstens
        ''' <paramref name="timeoutMs"/>).</summary>
        Friend Shared Function WaitIdle(timeoutMs As Integer) As Boolean
            Dim deadline = Environment.TickCount64 + timeoutMs
            While Environment.TickCount64 < deadline
                SyncLock _lock
                    If Not _running AndAlso _pending.Count = 0 Then Return True
                End SyncLock
                Thread.Sleep(20)
            End While
            Return False
        End Function

    End Class

End Namespace
