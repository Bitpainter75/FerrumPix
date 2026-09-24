Imports System
Imports System.Collections.Generic
Imports SkiaSharp

Namespace Services

    ''' <summary>DIE ZULETZT GEMALTEN RASTER DER EBENEN, im Speicher, je Dateipfad.
    '''
    ''' Malen, Radieren und Retuschieren auf einer Ebene schreiben ihr Ergebnis weiterhin in eine
    ''' neue Datei: der Pfad IST das Modell der Ebene. Rückgängig merkt sich den Pfad, und das
    ''' Speichern als .fpx, der PSD-Export und G'MIC lesen die Datei. Teuer war aber nicht das
    ''' Schreiben allein, sondern dass derselbe Stand danach zweimal wieder dekodiert wurde: vom
    ''' nächsten Strich, der auf ihm aufbaut, und von der Anzeige, die den neuen Pfad zeichnet.
    ''' Beide holen ihn jetzt von hier.
    '''
    ''' Wer ein Raster bekommt, bekommt eine KOPIE. Der Speicher entsorgt seine Einträge, sobald er
    ''' voll ist, und ein Aufrufer, der gerade auf einem geteilten Bitmap zeichnete, stünde dann
    ''' auf freigegebenem Speicher (nativer Absturz). Eine Kopie kostet bei 24 MP eine
    ''' Speicherkopie; das Dekodieren, das sie ersetzt, kostet ein Vielfaches.
    '''
    ''' Speicher und Datei müssen dasselbe Bild sein: wer einen Stand später aus der Datei liest
    ''' (Rückgängig weiter zurück, als der Speicher reicht, .fpx, PSD), darf nichts anderes sehen.
    ''' Die Datei liegt nicht vormultipliziert vor, der Speicher schon - an weichen Kanten wäre das
    ''' die Stelle für eine Abweichung. Gemessen ist dort keine ("Malebene: der Stand im Speicher
    ''' gleicht der geschriebenen Datei", ganz weicher Pinsel, gut 40000 halbdurchsichtige
    ''' Punkte).</summary>
    Public NotInheritable Class ObjectImageMemory

        ''' <summary>Drei Stände: der aktuelle, der vorige für ein Rückgängig, und einer, der gerade
        ''' im Hintergrund entsteht. Mehr bringt beim Malen nichts, kostet aber bei 24 MP je
        ''' knapp 100 MB.</summary>
        Private Const MaxEntries As Integer = 3
        Private Const MaxBytes As Long = 400L * 1024L * 1024L

        Private Shared ReadOnly _lock As New Object()
        Private Shared ReadOnly _entries As New List(Of (Path As String, Bitmap As SKBitmap))()

        Private Sub New()
        End Sub

        ''' <summary>Legt ein Raster unter seinem Pfad ab. Der Speicher ÜBERNIMMT das Bitmap: der
        ''' Aufrufer darf es danach weder benutzen noch entsorgen.</summary>
        Public Shared Sub Put(path As String, bitmap As SKBitmap)
            If String.IsNullOrEmpty(path) OrElse bitmap Is Nothing Then
                bitmap?.Dispose()
                Return
            End If
            SyncLock _lock
                RemoveLocked(path)
                _entries.Add((path, bitmap))
                Dim total As Long = 0
                For Each e In _entries
                    total += CLng(e.Bitmap.ByteCount)
                Next
                While _entries.Count > 1 AndAlso (_entries.Count > MaxEntries OrElse total > MaxBytes)
                    total -= CLng(_entries(0).Bitmap.ByteCount)
                    _entries(0).Bitmap.Dispose()
                    _entries.RemoveAt(0)
                End While
            End SyncLock
        End Sub

        ''' <summary>Eine Kopie des Rasters zu diesem Pfad, oder Nothing, wenn es nicht (mehr) im
        ''' Speicher liegt.</summary>
        Public Shared Function TryGetCopy(path As String) As SKBitmap
            If String.IsNullOrEmpty(path) Then Return Nothing
            SyncLock _lock
                For Each e In _entries
                    If String.Equals(e.Path, path, StringComparison.Ordinal) Then Return FastCopy(e.Bitmap)
                Next
            End SyncLock
            Return Nothing
        End Function

        ''' <summary>EINE KOPIE OHNE UMWEG. SKBitmap.Copy brauchte für 24 MP rund 200 ms (gemessen
        ''' in "MESSUNG Malebene") - länger als das Dekodieren der Datei, die der Speicher ersparen
        ''' soll. ReadPixels in dasselbe Format ist ein bloßes Umkopieren des Speichers. Scheitert
        ''' es, bleibt Copy als Rückfall.</summary>
        Public Shared Function FastCopy(source As SKBitmap) As SKBitmap
            If source Is Nothing Then Return Nothing
            Dim copy = New SKBitmap(source.Info)
            Using pixmap = source.PeekPixels()
                If pixmap IsNot Nothing AndAlso pixmap.ReadPixels(copy.Info, copy.GetPixels(), copy.RowBytes) Then Return copy
            End Using
            copy.Dispose()
            Return source.Copy()
        End Function

        ''' <summary>Das Raster zu diesem Pfad: aus dem Speicher, sonst aus der Datei. Der Aufrufer
        ''' besitzt das Ergebnis.</summary>
        Public Shared Function DecodeOrCopy(path As String) As SKBitmap
            Dim fromMemory = TryGetCopy(path)
            If fromMemory IsNot Nothing Then Return fromMemory
            If String.IsNullOrEmpty(path) OrElse Not IO.File.Exists(path) Then Return Nothing
            Return SKBitmap.Decode(path)
        End Function

        Public Shared Sub Remove(path As String)
            SyncLock _lock
                RemoveLocked(path)
            End SyncLock
        End Sub

        ''' <summary>Beim Wechsel des Dokuments: seine Zwischenstände gelten nicht mehr.</summary>
        Public Shared Sub Clear()
            SyncLock _lock
                For Each e In _entries
                    e.Bitmap.Dispose()
                Next
                _entries.Clear()
            End SyncLock
        End Sub

        Private Shared Sub RemoveLocked(path As String)
            For i = _entries.Count - 1 To 0 Step -1
                If String.Equals(_entries(i).Path, path, StringComparison.Ordinal) Then
                    _entries(i).Bitmap.Dispose()
                    _entries.RemoveAt(i)
                End If
            Next
        End Sub

    End Class

End Namespace
