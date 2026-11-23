Imports System
Imports System.Runtime.ExceptionServices
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Gemeinsames Werkzeug der beiden verlustfreien Schreiber, die in Streifen packen
    ''' (<see cref="TiffWriterService"/> und die Ausgabe mit 8 und 16 Bit in <see cref="PngEncoder"/>).
    '''
    ''' WARUM IN STREIFEN. Deflate rechnet auf einem Kern. Bei einem Bild mit 61 Megapixeln und
    ''' 16 Bit lagen so gut elf Sekunden einer TIFF-Ausgabe allein im Packen. Ein Streifen von 64
    ''' Zeilen haengt beim Packen nur von sich selbst ab (TIFF), beim PNG zusaetzlich von der
    ''' Zeile davor (die Filter Up, Average und Paeth lesen sie). Beides laesst sich deshalb je
    ''' Streifen parallel rechnen und danach in der Reihenfolge des Bildes zusammensetzen
    ''' (<see cref="EncodeInOrder"/>).
    '''
    ''' SPEICHER. Jeder Kern hat einen eigenen Arbeitspuffer fuer seine Streifen, und gepackte
    ''' Streifen, die noch nicht geschrieben sind, gibt es hoechstens <see cref="WindowStrips"/>.
    ''' Das Bild liegt also nie ein zweites Mal im Speicher, weder roh noch gepackt.
    '''
    ''' DIE BILDPUNKTE liest jeder Streifen selbst ueber SKPixmap.ReadPixels, ohne vormultipliziertes
    ''' Alpha, wie vorher zeilenweise. Skia rechnet dabei je Bildpunkt um, gleich ob eine oder
    ''' vierundsechzig Zeilen auf einmal gelesen werden; die Werte sind dieselben.
    ''' </summary>
    Friend NotInheritable Class StripEncoding

        Private Sub New()
        End Sub

        ''' Zeilen je Streifen, fuer beide Schreiber. Beim TIFF steht dieselbe Zahl im Tag
        ''' ROWSPERSTRIP.
        Friend Const RowsPerStrip As Integer = 64

        ''' Wie viele Kerne gleichzeitig packen.
        Friend Shared ReadOnly Property Workers As Integer
            Get
                Return Math.Max(1, Environment.ProcessorCount)
            End Get
        End Property

        ''' Gepackte Streifen, die hoechstens auf das Schreiben warten: einer je Kern und ein paar
        ''' dazu, damit ein langsamer Streifen die anderen Kerne nicht sofort anhaelt.
        Friend Shared ReadOnly Property WindowStrips As Integer
            Get
                Return Workers + Math.Max(2, Workers \ 4)
            End Get
        End Property

        ''' Obergrenze eines Streifenpuffers, wenn ein Bild so breit ist, dass 64 Zeilen nicht mehr
        ''' in einen Puffer passen (256 MB).
        Friend Const WideStripBytes As Long = 256L * 1024L * 1024L

        ''' Wie viel Speicher die Puffer aller Kerne zusammen hoechstens belegen sollen (1 GB). Bei
        ''' einem normalen Foto liegt ein Streifen bei wenigen Megabyte, die Grenze greift nie; bei
        ''' einem breiten Panorama in 16 Bit kann ein Streifen von 64 Zeilen Hunderte Megabyte haben,
        ''' und einer je Kern sprengte den Speicher. Dann rechnen weniger Kerne. Die Datei bleibt
        ''' dieselbe: die Streifen sind voneinander unabhaengig, nur ihre Einteilung bestimmt die Bytes.
        '''
        ''' UNTERGRENZE: ein Kern braucht immer seine Arbeitspuffer, den Streifen, den er gerade
        ''' packt, und den, der gerade geschrieben wird, also rund das Dreifache eines Streifens (bei
        ''' einem flachen, sehr breiten PNG mehr, dort zaehlen die Zeilenpuffer), auch wenn das ueber
        ''' dem Budget liegt. Weniger ginge nur mit weniger Zeilen je Streifen, und die
        ''' bleiben bis Array.MaxLength bei 64, damit jede frueher geschriebene Datei gleich entsteht
        ''' (<see cref="RowsPerStripFor"/>).
        Friend Const StripMemoryBudget As Long = 1024L * 1024L * 1024L

        ''' <summary>Wie viele Kerne packen, wenn ein Kern <paramref name="workBytes"/> Bytes an
        ''' Arbeitspuffern anlegt (ALLE, beim PNG auch die Zeilen und die fuenf gefilterten
        ''' Fassungen: bei einem flachen, sehr breiten Bild sind sie der groessere Teil) und ein
        ''' gepackter Streifen bis <paramref name="packedBytes"/> Bytes hat. Beides 0 heisst
        ''' unbekannt: dann alle.</summary>
        Friend Shared Function WorkersFor(workBytes As Long, packedBytes As Long) As Integer
            If workBytes <= 0 AndAlso packedBytes <= 0 Then Return Workers
            ' Je Kern seine Arbeitspuffer, der Streifen, den er packt, und einer, der geschrieben wird.
            Dim perWorker = Math.Max(0L, workBytes) + 2L * Math.Max(0L, packedBytes)
            Dim allowed = StripMemoryBudget \ Math.Max(1L, perWorker)
            Return CInt(Math.Max(1L, Math.Min(CLng(Workers), allowed)))
        End Function

        ''' <summary>Zeilen je Streifen fuer ein Bild, dessen Puffer fuer einen Streifen von r Zeilen
        ''' <paramref name="stripBytes"/>(r) Bytes brauchen (der groesste Puffer des Schreibers), oder
        ''' 0, wenn nicht einmal eine Zeile in einen Puffer passt; dann schreibt der Aufrufer nicht
        ''' in Streifen.
        '''
        ''' DIE EINTEILUNG BESTIMMT DIE DATEI: beim PNG endet jeder Streifen mit einem Sync-Flush,
        ''' beim TIFF ist jeder ein eigener zlib-Strom. Es bleibt deshalb bei
        ''' <see cref="RowsPerStrip"/>, solange jeder Puffer dafuer angelegt werden kann, und genau
        ''' das ging auch frueher, als die Groessen noch als Integer fuer 64 Zeilen gerechnet wurden:
        ''' jede Datei, die damals entstand, entsteht Byte fuer Byte gleich. Nur darueber, bei einem
        ''' Panorama mit Millionen Bildpunkten Breite, wo die Rechnung frueher ueberlief, werden es
        ''' so viele Zeilen, dass ein Puffer <see cref="WideStripBytes"/> nicht uebersteigt, und
        ''' mindestens eine.</summary>
        Friend Shared Function RowsPerStripFor(stripBytes As Func(Of Integer, Long)) As Integer
            If stripBytes(RowsPerStrip) <= Array.MaxLength Then Return RowsPerStrip
            For rows = RowsPerStrip - 1 To 2 Step -1
                If stripBytes(rows) <= WideStripBytes Then Return rows
            Next
            Return If(stripBytes(1) <= Array.MaxLength, 1, 0)
        End Function

        Friend Shared Function StripCount(height As Integer, rowsPerStrip As Integer) As Integer
            Return CInt((CLng(height) + rowsPerStrip - 1) \ rowsPerStrip)
        End Function

        ''' <summary>Packt <paramref name="stripCount"/> Streifen auf allen Kernen und schreibt sie
        ''' in ihrer Reihenfolge, auf dem rufenden Faden.
        '''
        ''' Jeder Kern holt sich den naechsten Streifen der Reihe nach und rechnet ihn mit seinem
        ''' eigenen Zustand (<paramref name="createState"/>, einmal je Kern: die Arbeitspuffer).
        ''' Liegt er mehr als <see cref="WindowStrips"/> Streifen vor dem Schreiben, wartet er. Das
        ''' kann nicht haengen: der Streifen, auf den das Schreiben wartet, ist immer der kleinste
        ''' vergebene, und wer ihn rechnet, wartet nie.
        '''
        ''' <paramref name="pack"/> liefert Nothing, wenn der Streifen nicht entstehen kann;
        ''' <paramref name="write"/> Falsch, wenn er nicht geschrieben werden kann. Beides beendet
        ''' den Lauf mit Falsch. Eine Ausnahme beim Packen kommt nach dem Ende aller Kerne beim
        ''' Aufrufer an, wie vorher aus dem einzelnen Durchgang.</summary>
        Friend Shared Function EncodeInOrder(Of TState, TResult As Class)(stripCount As Integer,
                                                                        createState As Func(Of TState),
                                                                        pack As Func(Of TState, Integer, TResult),
                                                                        write As Func(Of Integer, TResult, Boolean),
                                                                        Optional workBytes As Long = 0,
                                                                        Optional packedBytes As Long = 0) As Boolean
            If stripCount <= 0 Then Return True
            Dim results(stripCount - 1) As TResult
            Dim gate As New Object()
            Dim nextStrip = 0
            Dim written = 0
            Dim failed = False
            Dim failure As ExceptionDispatchInfo = Nothing
            ' Kerne und Fenster nach dem Speicher (WorkersFor). Wird gekuerzt, rechnet kein Kern
            ' einem nicht geschriebenen Streifen voraus: neben dem, der gerade geschrieben wird
            ' (written steht schon dahinter), liegt dann nur noch der eigene. Sonst stapelten sich
            ' riesige Streifen.
            Dim workerLimit = WorkersFor(workBytes, packedBytes)
            Dim window = If(workerLimit < Workers, workerLimit, WindowStrips)

            Dim work As Action =
                Sub()
                    Try
                        Dim state = createState()
                        Do
                            Dim strip = Interlocked.Increment(nextStrip) - 1
                            If strip >= stripCount Then Exit Do
                            SyncLock gate
                                While Not failed AndAlso strip - written >= window
                                    Monitor.Wait(gate)
                                End While
                                If failed Then Exit Do
                            End SyncLock
                            Dim result = pack(state, strip)
                            SyncLock gate
                                If result Is Nothing Then failed = True Else results(strip) = result
                                Monitor.PulseAll(gate)
                                If failed Then Exit Do
                            End SyncLock
                        Loop
                    Catch ex As Exception
                        SyncLock gate
                            If failure Is Nothing Then failure = ExceptionDispatchInfo.Capture(ex)
                            failed = True
                            Monitor.PulseAll(gate)
                        End SyncLock
                    End Try
                End Sub

            Dim workerCount = Math.Min(workerLimit, stripCount)
            Dim tasks(workerCount - 1) As Task
            For i = 0 To workerCount - 1
                tasks(i) = Task.Run(work)
            Next

            Dim ok = True
            Dim loopDone = False
            Try
                For strip = 0 To stripCount - 1
                    Dim result As TResult
                    SyncLock gate
                        While results(strip) Is Nothing AndAlso Not failed
                            Monitor.Wait(gate)
                        End While
                        result = results(strip)
                        results(strip) = Nothing
                        written = strip + 1
                        Monitor.PulseAll(gate)
                    End SyncLock
                    If result Is Nothing OrElse Not write(strip, result) Then
                        ok = False
                        Exit For
                    End If
                Next
                loopDone = True
            Finally
                ' Die Kerne anhalten, die noch warten oder rechnen. Auch wenn das Schreiben selbst
                ' wirft (volle Platte, Strom ueber seiner Grenze): ohne diesen Block warteten sie
                ' fuer immer auf einen Streifen, der nie mehr geschrieben wird.
                If Not ok OrElse Not loopDone Then
                    SyncLock gate
                        failed = True
                        Monitor.PulseAll(gate)
                    End SyncLock
                End If
                Task.WaitAll(tasks)
            End Try
            failure?.Throw()
            Return ok
        End Function

        ''' <summary>Platz, den Deflate fuer <paramref name="rawLength"/> Bytes hoechstens braucht,
        ''' samt zlib-Kopf, Pruefsumme und den Abschluessen eines Streifens. Deflate legt im
        ''' schlechtesten Fall je Block von 64 KB fuenf Bytes dazu; hier ist reichlich Luft.</summary>
        Friend Shared Function PackedBound(rawLength As Long) As Long
            Return rawLength + rawLength \ 100 + 1024
        End Function

        ''' <summary>Die Puffer fuer gepackte Streifen EINER Ausgabe. Ein Streifen bekommt einen
        ''' Puffer in der Groesse von <see cref="PackedBound"/> (der Strom waechst nie, es bleibt kein
        ''' Abfall aus dem Wachsen liegen), und nach dem Schreiben geht er zurueck an den naechsten
        ''' Streifen. Es gibt also hoechstens so viele, wie Streifen gleichzeitig unterwegs sind, und
        ''' mit dem Ende der Ausgabe gibt die Speicherbereinigung sie frei.</summary>
        Friend NotInheritable Class PackedBuffers
            Private ReadOnly _free As New System.Collections.Concurrent.ConcurrentBag(Of Byte())()
            Private ReadOnly _size As Integer

            ''' <summary><paramref name="rawLength"/> ist die Rohgroesse des laengsten Streifens.
            ''' Dass der Puffer dafuer angelegt werden kann, sichert der Aufrufer ueber
            ''' <see cref="RowsPerStripFor"/>.</summary>
            Public Sub New(rawLength As Long)
                _size = CInt(PackedBound(rawLength))
            End Sub

            ''' <summary>Ein leerer Strom fester Groesse ueber einem freien oder neuen Puffer.</summary>
            Public Function Rent() As IO.MemoryStream
                Dim buffer As Byte() = Nothing
                If Not _free.TryTake(buffer) Then buffer = New Byte(_size - 1) {}
                Dim stream As New IO.MemoryStream(buffer, 0, buffer.Length, writable:=True, publiclyVisible:=True)
                ' Ueber einem vorhandenen Puffer zaehlt der Strom ihn sonst ganz als Inhalt.
                stream.SetLength(0)
                Return stream
            End Function

            ''' <summary>Gibt den Puffer des Stroms zurueck; der Strom selbst ist danach verbraucht.</summary>
            Public Sub [Return](stream As IO.MemoryStream)
                If stream Is Nothing Then Return
                _free.Add(stream.GetBuffer())
            End Sub
        End Class

        ''' <summary>Liest <paramref name="rows"/> Zeilen ab <paramref name="firstRow"/> in
        ''' <paramref name="target"/> (ab Index 0), RGBA ohne vormultipliziertes Alpha, mit 8 oder
        ''' 16 Bit je Wert. Mit <paramref name="alphaType"/> Premul bleiben die Werte
        ''' vormultipliziert (der PNG-Schreiber rechnet dann selbst zurueck). Falsch, wenn Skia
        ''' nicht lesen kann.</summary>
        Friend Shared Function ReadRows(pixmap As SKPixmap, firstRow As Integer, rows As Integer, deep As Boolean,
                                        target As Byte(), Optional alphaType As SKAlphaType = SKAlphaType.Unpremul) As Boolean
            Dim width = pixmap.Width
            Dim rowBytes = width * If(deep, 8, 4)
            Dim info = New SKImageInfo(width, rows, If(deep, SKColorType.Rgba16161616, SKColorType.Rgba8888),
                                       alphaType)
            Dim handle = GCHandle.Alloc(target, GCHandleType.Pinned)
            Try
                Return pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), rowBytes, 0, firstRow)
            Finally
                handle.Free()
            End Try
        End Function

        ''' <summary>Traegt irgendein Bildpunkt Transparenz? Je Streifen parallel, mit fruehem
        ''' Ausstieg, sobald ein Streifen fuendig wird. Im Zweifel (Skia kann nicht lesen) Ja: ein
        ''' ueberfluessiger Kanal kostet Platz, ein fehlender das Bild.</summary>
        Friend Shared Function HasTransparency(pixmap As SKPixmap, deep As Boolean) As Boolean
            Dim width = pixmap.Width
            Dim height = pixmap.Height
            Dim sampleBytes = If(deep, 2, 1)
            Dim pixelBytes = 4 * sampleBytes
            ' Die Einteilung beeinflusst hier nur, wie gelesen wird, nicht die Datei.
            Dim rowsPerStrip = RowsPerStripFor(Function(r) r * CLng(width) * pixelBytes)
            If rowsPerStrip = 0 Then Return True
            Dim bufferRows = Math.Min(rowsPerStrip, height)
            Dim found = 0
            ' Auch hier je Kern ein Puffer fuer einen Streifen: bei einem sehr breiten Bild so wenige
            ' Kerne, wie ins Speicherbudget passen (WorkersFor), sonst scheiterte schon die Pruefung.
            Dim readers = WorkersFor(CLng(bufferRows) * width * pixelBytes, 0)
            Parallel.For(0, StripCount(height, rowsPerStrip), New ParallelOptions With {.MaxDegreeOfParallelism = readers},
                Function() New Byte(bufferRows * width * pixelBytes - 1) {},
                Function(strip, loopState, rgba)
                    If Volatile.Read(found) <> 0 Then
                        loopState.Stop()
                        Return rgba
                    End If
                    Dim firstRow = strip * rowsPerStrip
                    Dim rows = Math.Min(rowsPerStrip, height - firstRow)
                    If Not ReadRows(pixmap, firstRow, rows, deep, rgba) Then
                        Interlocked.Exchange(found, 1)
                        Return rgba
                    End If
                    ' Bei 16 Bit beide Bytes des Alphawerts: deckend sind sie beide 255.
                    Dim length = rows * width * pixelBytes
                    For i = 3 * sampleBytes To length - 1 Step pixelBytes
                        If rgba(i) <> 255 OrElse rgba(i + sampleBytes - 1) <> 255 Then
                            Interlocked.Exchange(found, 1)
                            Exit For
                        End If
                    Next
                    Return rgba
                End Function,
                Sub(rgba)
                End Sub)
            Return found <> 0
        End Function

        Private Const AdlerBase As Long = 65521
        ''' Laengster Abschnitt, nach dem die Summen zurueck in den Bereich muessen (wie in zlib).
        Private Const AdlerChunk As Integer = 5552

        ''' <summary>Fuehrt die Pruefsumme Adler-32 (zlib) ueber <paramref name="count"/> Bytes
        ''' fort. Anfangswert 1.</summary>
        Friend Shared Function UpdateAdler32(adler As UInteger, data As Byte(), offset As Integer, count As Integer) As UInteger
            Dim s1 = CLng(adler And &HFFFFUI)
            Dim s2 = CLng(adler >> 16)
            While count > 0
                Dim n = Math.Min(count, AdlerChunk)
                Dim stopAt = offset + n
                For i = offset To stopAt - 1
                    s1 += data(i)
                    s2 += s1
                Next
                s1 = s1 Mod AdlerBase
                s2 = s2 Mod AdlerBase
                offset = stopAt
                count -= n
            End While
            Return CUInt((s2 << 16) Or s1)
        End Function

        ''' <summary>Die Adler-32 zweier aneinandergehaengter Abschnitte aus den beiden einzelnen
        ''' Summen und der Laenge des zweiten (wie adler32_combine in zlib).</summary>
        Friend Shared Function CombineAdler32(first As UInteger, second As UInteger, secondLength As Long) As UInteger
            Dim remainder = secondLength Mod AdlerBase
            Dim sum1 = CLng(first And &HFFFFUI)
            Dim sum2 = (remainder * sum1) Mod AdlerBase
            sum1 += CLng(second And &HFFFFUI) + AdlerBase - 1
            sum2 += CLng(first >> 16) + CLng(second >> 16) + AdlerBase - remainder
            sum1 = sum1 Mod AdlerBase
            sum2 = sum2 Mod AdlerBase
            Return CUInt((sum2 << 16) Or sum1)
        End Function

    End Class

End Namespace
