Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Threading
Imports SkiaSharp

Namespace Services

    ''' <summary>DIE BILDER DER BILDOBJEKTE, einmal dekodiert und im Speicher gehalten.
    '''
    ''' Jedes Zeichnen eines Bildobjekts dekodierte seine Datei neu und machte daraus fuer das
    ''' Zeichnen ein SKImage, wobei Skia das veraenderbare Bitmap noch einmal ganz kopierte. Bei
    ''' einer Collage aus 31 JPEGs mit 1600 Punkten Kante waren das je Bild der Szene rund eine halbe
    ''' Sekunde, bei jedem Zug eines Objekts. Hier liegt je Datei ein unveraenderliches SKImage; Skia
    ''' haelt dazu seine verkleinerten Stufen, statt sie jedes Mal neu zu bilden.
    '''
    ''' Geprueft wird je Abruf Laenge und Aenderungszeit der Datei: wird sie ersetzt, gilt der
    ''' Eintrag nicht mehr. Gemalte Ebenen schreiben ohnehin jeden Stand in eine neue Datei
    ''' (ObjectImageMemory), ihr alter Pfad faellt mit der Zeit hinten heraus.
    '''
    ''' Ein Eintrag wird erst freigegeben, wenn ihn niemand mehr zeichnet: wer ein Bild holt,
    ''' bekommt eine Leihe und gibt sie mit Dispose zurueck. Fliegt ein Eintrag dazwischen aus dem
    ''' Speicher, entsorgt ihn die letzte Rueckgabe.</summary>
    Public NotInheritable Class ObjectImageCache

        ''' <summary>Obergrenze fuer alle gehaltenen Bilder zusammen. 31 Bilder mit 1600 Punkten
        ''' Kante belegen rund 320 MB.</summary>
        Private Const MaxBytes As Long = 512L * 1024L * 1024L

        Friend NotInheritable Class Entry
            Public Path As String
            Public Length As Long
            Public Stamp As DateTime
            Public Image As SKImage
            Public Bytes As Long
            Public Leases As Integer
            Public Evicted As Boolean
        End Class

        ''' <summary>Ein geliehenes Bild. Nur zum Zeichnen, solange die Leihe offen ist.</summary>
        Public NotInheritable Class Lease
            Implements IDisposable

            Private _entry As Entry

            Friend Sub New(entry As Entry)
                _entry = entry
            End Sub

            Public ReadOnly Property Image As SKImage
                Get
                    Return _entry?.Image
                End Get
            End Property

            Public Sub Dispose() Implements IDisposable.Dispose
                Dim entry = Interlocked.Exchange(_entry, Nothing)
                If entry Is Nothing Then Return
                SyncLock _lock
                    entry.Leases -= 1
                    If entry.Leases = 0 AndAlso entry.Evicted Then entry.Image.Dispose()
                End SyncLock
            End Sub
        End Class

        Private Shared ReadOnly _lock As New Object()
        ' Vorne der am laengsten nicht gebrauchte Eintrag.
        Private Shared ReadOnly _entries As New List(Of Entry)()
        Private Shared _totalBytes As Long

        Private Sub New()
        End Sub

        ''' <summary>Das Bild zu dieser Datei, aus dem Speicher oder frisch dekodiert. Nothing, wenn
        ''' die Datei fehlt oder sich nicht lesen laesst.</summary>
        Public Shared Function Acquire(path As String) As Lease
            If String.IsNullOrEmpty(path) Then Return Nothing
            Dim info As FileInfo
            Try
                info = New FileInfo(path)
                If Not info.Exists Then Return Nothing
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
                Return Nothing
            End Try
            Dim length = info.Length
            Dim stamp = info.LastWriteTimeUtc

            SyncLock _lock
                For i = 0 To _entries.Count - 1
                    Dim e = _entries(i)
                    If Not String.Equals(e.Path, path, StringComparison.Ordinal) Then Continue For
                    If e.Length = length AndAlso e.Stamp = stamp Then
                        _entries.RemoveAt(i)
                        _entries.Add(e)
                        e.Leases += 1
                        Return New Lease(e)
                    End If
                    EvictLocked(i)
                    Exit For
                Next
            End SyncLock

            ' Ausserhalb der Sperre dekodieren: zwei Faeden, die dasselbe Bild zugleich brauchen,
            ' dekodieren es im schlimmsten Fall zweimal, aber keiner wartet auf fremde Dateien.
            Dim image As SKImage
            Using bitmap = SKBitmap.Decode(path)
                If bitmap Is Nothing OrElse bitmap.Width <= 0 OrElse bitmap.Height <= 0 Then Return Nothing
                bitmap.SetImmutable()
                image = SKImage.FromBitmap(bitmap)
            End Using
            If image Is Nothing Then Return Nothing

            Dim fresh As New Entry With {
                .Path = path, .Length = length, .Stamp = stamp, .Image = image,
                .Bytes = CLng(image.Width) * image.Height * 4, .Leases = 1}
            SyncLock _lock
                For i = _entries.Count - 1 To 0 Step -1
                    If String.Equals(_entries(i).Path, path, StringComparison.Ordinal) Then EvictLocked(i)
                Next
                _entries.Add(fresh)
                _totalBytes += fresh.Bytes
                While _entries.Count > 1 AndAlso _totalBytes > MaxBytes
                    EvictLocked(0)
                End While
            End SyncLock
            Return New Lease(fresh)
        End Function

        ''' <summary>Beim Wechsel des Dokuments: seine Bilder werden nicht mehr gebraucht.</summary>
        Public Shared Sub Clear()
            SyncLock _lock
                For i = _entries.Count - 1 To 0 Step -1
                    EvictLocked(i)
                Next
            End SyncLock
        End Sub

        Private Shared Sub EvictLocked(index As Integer)
            Dim e = _entries(index)
            _entries.RemoveAt(index)
            _totalBytes -= e.Bytes
            e.Evicted = True
            If e.Leases = 0 Then e.Image.Dispose()
        End Sub

    End Class

End Namespace
