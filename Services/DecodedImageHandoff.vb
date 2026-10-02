Imports System
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' EIN Platz, ueber den der Betrachter sein schon dekodiertes Bild an den Editor uebergibt,
    ''' wenn dasselbe Bild dort geoeffnet wird. Ohne ihn dekodierte der Editor die Datei ein zweites
    ''' Mal, obwohl das Bild gerade auf dem Schirm stand.
    '''
    ''' DER EDITOR BEKOMMT IMMER DAS VOLL AUFGELOESTE BILD. Angeboten wird nur, was der Betrachter in
    ''' voller Groesse dekodiert hat, und genommen wird es nur, wenn Pfad, Aenderungszeit der Datei
    ''' und die Masse aus dem Dateikopf stimmen. Was nicht passt, wird verworfen, und der Editor
    ''' dekodiert wie bisher selbst. Fuer RAW gibt es diesen Weg nicht: der Betrachter zeigt dort das
    ''' fertig ENTWICKELTE Bild samt Rezept, nicht die Quelle, und seine volle Entwicklung erreicht den
    ''' Editor ohnehin ueber den Zwischenspeicher von RawDecodeService - den ein halbgrosser Decode
    ''' (half_size) ausdruecklich nicht beruehrt.
    '''
    ''' Der Platz haelt hoechstens ein Bild und nur fuer die Dauer des Oeffnens: angeboten direkt vor
    ''' EditorViewModel.OpenImageAsync, geraeumt danach (MainWindowViewModel.OpenImageInEditor). Kein
    ''' Vorhalten ueber Bildwechsel hinweg, das ist abgelehnt (FALLEN_UND_ENTSCHEIDUNGEN.md).
    ''' </summary>
    Public NotInheritable Class DecodedImageHandoff

        Private Sub New()
        End Sub

        Private Shared ReadOnly _lock As New Object()
        Private Shared _path As String
        Private Shared _writeTimeUtc As DateTime
        Private Shared _bitmap As SKBitmap
        Private Shared _takenCount As Long

        ''' <summary>Wie oft in diesem Lauf ein Bild wirklich uebernommen wurde. Fuer die Diagnose:
        ''' ein Zaehler statt einer Vermutung, ob der Weg im echten Ablauf ueberhaupt greift.</summary>
        Public Shared ReadOnly Property TakenCount As Long
            Get
                Return Threading.Interlocked.Read(_takenCount)
            End Get
        End Property

        ''' <summary>Legt ein Bild bereit; der Platz uebernimmt den Besitz und gibt ein vorher
        ''' liegendes frei. <paramref name="writeTimeUtc"/> ist die Aenderungszeit der Datei, zu der
        ''' das Bild dekodiert wurde.</summary>
        Public Shared Sub Offer(path As String, writeTimeUtc As DateTime, bitmap As SKBitmap)
            SyncLock _lock
                _bitmap?.Dispose()
                _path = path
                _writeTimeUtc = writeTimeUtc
                _bitmap = bitmap
            End SyncLock
        End Sub

        ''' <summary>Das bereitgelegte Bild, wenn es zu dieser Datei gehoert und voll aufgeloest ist
        ''' (Besitz geht an den Aufrufer), sonst Nothing. Der Platz ist danach in jedem Fall leer.</summary>
        Public Shared Function TryTake(path As String) As SKBitmap
            Dim bitmap As SKBitmap
            Dim offeredPath As String
            Dim offeredWriteTime As DateTime
            SyncLock _lock
                bitmap = _bitmap
                offeredPath = _path
                offeredWriteTime = _writeTimeUtc
                _bitmap = Nothing
                _path = Nothing
            End SyncLock
            If bitmap Is Nothing Then Return Nothing
            Dim reason = RejectReason(path, offeredPath, offeredWriteTime, bitmap)
            If reason Is Nothing Then
                Threading.Interlocked.Increment(_takenCount)
                DiagnosticLogService.LogAlways("Editor.Handoff", $"uebernommen {bitmap.Width}x{bitmap.Height} {IO.Path.GetFileName(path)}")
                Return bitmap
            End If
            DiagnosticLogService.LogAlways("Editor.Handoff", $"verworfen ({reason}) {IO.Path.GetFileName(path)}")
            bitmap.Dispose()
            Return Nothing
        End Function

        Public Shared Sub Clear()
            SyncLock _lock
                _bitmap?.Dispose()
                _bitmap = Nothing
                _path = Nothing
            End SyncLock
        End Sub

        ''' <summary>Warum ein bereitgelegtes Bild nicht genommen wird, oder Nothing.</summary>
        Private Shared Function RejectReason(path As String, offeredPath As String, offeredWriteTime As DateTime,
                                             bitmap As SKBitmap) As String
            If String.IsNullOrEmpty(path) OrElse Not String.Equals(path, offeredPath, StringComparison.Ordinal) Then Return "andere Datei"
            If RawPreviewService.IsSupportedRaw(path) Then Return "RAW"
            Try
                If File.GetLastWriteTimeUtc(path) <> offeredWriteTime Then Return "Datei geaendert"
            Catch
                Return "Datei nicht lesbar"
            End Try
            ' VOLLE GROESSE: die Masse muessen die des Dateikopfs sein, gedreht wie der Decode. Bei
            ' den Formaten mit eigenem Leser waere das ein zweiter voller Decode; dort nimmt der
            ' Aufrufer nur Bilder, die er selbst in voller Groesse dekodiert hat (Betrachter), und die
            ' Masse fragt der Leser ueber seinen Kopf ab, wo er kann.
            Dim size = HeaderSize(path)
            If size.Width <= 0 OrElse size.Height <= 0 Then Return "Masse unbekannt"
            If bitmap.Width <> size.Width OrElse bitmap.Height <> size.Height Then
                Return $"nicht voll aufgeloest ({bitmap.Width}x{bitmap.Height} statt {size.Width}x{size.Height})"
            End If
            Return Nothing
        End Function

        Private Shared Function HeaderSize(path As String) As (Width As Integer, Height As Integer)
            ' Bei den Formaten mit eigenem Leser nur der Kopf: ohne ihn waere es ein zweiter voller
            ' Decode, und den soll die Uebergabe gerade sparen.
            If ForeignImageDecoder.CanDecode(path) Then Return ImageProcessor.ForeignHeaderSize(path)
            Return ImageProcessor.GetOrientedImageSize(path)
        End Function

    End Class

End Namespace
