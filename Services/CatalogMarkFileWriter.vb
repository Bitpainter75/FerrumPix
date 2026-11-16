Imports System
Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>
    ''' Zieht Bewertung, Farbetikett und Markierung (behalten oder verworfen) eines Bildes in die
    ''' Datei selbst nach, wenn die Einstellung "Bewertung, Etikett und Markierung in die
    ''' Bilddatei schreiben" an ist (AppSettings.WriteMarksToFile, ab Werk aus).
    '''
    ''' WOZU: ohne sie stehen diese Angaben nur im Katalog und in der .fpxmp, und ein anderes
    ''' Programm sieht den neuen Stern erst nach "Katalogdaten in die Dateien schreiben". Wer
    ''' FerrumPix neben einer anderen Bildverwaltung benutzt, will beides gleich haben.
    '''
    ''' WANN: nur aus einer bewussten Aenderung (LibraryService mit syncToXmp, also Galerie,
    ''' Betrachter und Editor), nie aus Kopier- oder Importwegen und nie aus dem Einlesen. Dieselbe
    ''' Grenze wie beim Schreiber der Stichwoerter (KeywordFileWriter).
    '''
    ''' WOHIN: nur in Formate, die XMP in der Datei tragen (JPEG, PNG, WebP), ueber denselben
    ''' Schreibweg wie "Katalogdaten in die Dateien schreiben" (CatalogMetadataService.WriteToFile):
    ''' eine vorhandene Beistelldatei zieht mit, und im JPEG auch die Felder, in denen Windows die
    ''' Bewertung fuehrt. An eine RAW geht nichts; fuer sie gibt es den Abgleich in die
    ''' XMP-Beistelldatei.
    '''
    ''' WIE: auf einem eigenen Faden und je Datei zusammengefasst, gelesen wird der Stand des
    ''' Katalogs erst beim Schreiben. Fuenf Sterne auf fuenfhundert markierten Bildern halten so
    ''' die Oberflaeche nicht an.
    ''' </summary>
    Public NotInheritable Class CatalogMarkFileWriter

        Private Const Fields As CatalogField = CatalogField.Rating Or CatalogField.ColorLabel Or CatalogField.PickState

        Private Shared ReadOnly _lock As New Object()
        Private Shared ReadOnly _pending As New List(Of String)()
        Private Shared ReadOnly _pendingSet As New HashSet(Of String)(StringComparer.Ordinal)
        Private Shared _running As Boolean

        Private Sub New()
        End Sub

        ''' <summary>Reiht eine Datei ein, wenn die Einstellung an ist und das Format die Angaben
        ''' aufnehmen kann. Serverbilder fallen weg.</summary>
        Public Shared Sub Queue(filePath As String)
            If String.IsNullOrWhiteSpace(filePath) Then Return
            If Not AppSettingsService.Load().WriteMarksToFile Then Return
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
                    If Not AppSettingsService.Load().WriteMarksToFile OrElse Not IO.File.Exists(path) Then Continue While
                    Dim result = CatalogMetadataService.WriteToFile(path, CatalogMetadataService.ReadCatalog(path), Fields)
                    If Not result.Success Then
                        DiagnosticLogService.LogAlways("Catalog.MarkFileWriter", "rating, label and pick not written into the file: " & path)
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("Catalog.MarkFileWriter", ex)
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
