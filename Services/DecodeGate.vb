Imports System
Imports System.Threading

Namespace Services

    ''' <summary>
    ''' Es laeuft immer nur EIN Decode. Nie zwei gleichzeitig.
    '''
    ''' Warum: ein RAW zu entwickeln kostet Sekunden und einen ganzen Kern. Laufen mehrere Laeufe
    ''' nebeneinander, wird keiner davon schneller - sie teilen sich dieselben Kerne, dieselbe
    ''' Speicherbandbreite und dieselbe Platte, und der Rechner steht waehrenddessen. Dazu haelt der
    ''' Decoder einen Zwischenspeicher fuer genau EIN Bild; vier Laeufe auf vier Dateien werfen ihn
    ''' staendig um. Aufgetreten, als das Infopanel der Galerie fuer jedes beruehrte Bild ein
    ''' Histogramm anwarf.
    '''
    ''' <para>Warum <c>SyncLock</c> und kein <c>SemaphoreSlim</c>: der Vorteil eines Semaphors waere
    ''' das Warten OHNE Thread, also <c>WaitAsync</c>. Das brauchen wir hier nicht - der Decode
    ''' laeuft ohnehin auf einem Hintergrund-Thread, und ein blockierendes <c>Wait</c> ist nichts
    ''' anderes als ein Sperrblock. Dafuer ist ein Semaphor NICHT wiedereintrittsfaehig: ruft
    ''' irgendwann ein Weg innerhalb eines laufenden Decodes erneut hier herein, wartet der Thread
    ''' auf sich selbst und kommt nie zurueck - ein Aufhaenger ohne Spur im Log. Ein Sperrblock
    ''' laesst denselben Thread durch.</para>
    '''
    ''' <para>Ein Grund fuer parallele Decodes ist nicht bekannt. Wer einen findet, aendert diese
    ''' Stelle und begruendet sie hier - nicht daneben.</para>
    '''
    ''' <para>NICHT betroffen: Kachelbilder aus der eingebetteten Vorschau und aus dem
    ''' Zwischenspeicher, und der Inhalt eines .fpx-Buendels - das ist ein gewoehnlicher Decode.
    ''' Steht die Einstellung "RAW-Thumbnails entwickeln" aber an, laufen auch die Kacheln
    ''' bearbeiteter RAWs hier durch.</para>
    ''' </summary>
    Public NotInheritable Class DecodeGate

        Private Shared ReadOnly _gate As New Object()
        Private Shared _running As Integer
        ''' Wie viele Auftraege vor der Schleuse stehen und nicht weichen (siehe WaitingCount).
        Private Shared _waiting As Integer
        ''' Wie viele verschiedene Faeden gerade in der Schleuse stehen, und der hoechste je
        ''' gesehene Wert. Verschachtelte Aufrufe desselben Fadens zaehlen nicht mit.
        Private Shared _holders As Integer
        Private Shared _peakHolders As Integer

        Private Sub New()
        End Sub

        ''' <summary>Fuehrt die Arbeit aus, sobald kein anderer Decode mehr laeuft. Solange der
        ''' Auftrag wartet, zaehlt er in <see cref="WaitingCount"/>: ein Vorlauf, der gerade die
        ''' Schleuse haelt, sieht ihn und bricht ab.</summary>
        Public Shared Function Run(Of T)(work As Func(Of T)) As T
            Return Enter(work, yielding:=False)
        End Function

        ''' <summary>Wie <see cref="Run"/>, aber fuer Arbeit, die jedem anderen Auftrag weicht (das
        ''' Vorentwickeln der Nachbarbilder, das Vorwaermen fuer den Editor). Sie zaehlt beim Warten
        ''' nicht in <see cref="WaitingCount"/>, bringt also keinen anderen Vorlauf zum Abbruch.
        ''' Weichen muss sie selbst: in ihrer Frage an RawDecodeService.RunWhileWanted steht
        ''' <see cref="WaitingCount"/> = 0.</summary>
        Public Shared Function RunYielding(Of T)(work As Func(Of T)) As T
            Return Enter(work, yielding:=True)
        End Function

        Private Shared Function Enter(Of T)(work As Func(Of T), yielding As Boolean) As T
            If work Is Nothing Then Return Nothing
            ' Ein verschachtelter Aufruf desselben Fadens kommt ohne Warten durch; er zaehlt weder
            ' als wartend noch als weiterer Halter.
            Dim outermost = Not Monitor.IsEntered(_gate)
            Dim counted = outermost AndAlso Not yielding
            If counted Then Interlocked.Increment(_waiting)
            Try
                SyncLock _gate
                    If counted Then
                        Interlocked.Decrement(_waiting)
                        counted = False
                    End If
                    If outermost Then NoteHolder(1)
                    Interlocked.Increment(_running)
                    Try
                        Return work()
                    Finally
                        Interlocked.Decrement(_running)
                        If outermost Then NoteHolder(-1)
                    End Try
                End SyncLock
            Finally
                ' Nur, wenn der Eintritt selbst scheiterte (Faden abgebrochen).
                If counted Then Interlocked.Decrement(_waiting)
            End Try
        End Function

        Private Shared Sub NoteHolder(delta As Integer)
            Dim now = Interlocked.Add(_holders, delta)
            If delta > 0 Then
                Dim peak = Volatile.Read(_peakHolders)
                While now > peak AndAlso Interlocked.CompareExchange(_peakHolders, now, peak) <> peak
                    peak = Volatile.Read(_peakHolders)
                End While
            End If
        End Sub

        ''' <summary>Laeuft gerade einer? Nur zum Messen und Melden gedacht.</summary>
        Public Shared ReadOnly Property IsBusy As Boolean
            Get
                Return Volatile.Read(_running) > 0
            End Get
        End Property

        ''' <summary>Wie viele Auftraege gerade vor der Schleuse warten, ohne zu weichen. Ein
        ''' Vorlauf fragt das in seinem Abbruch ab: steht hier mehr als null, braucht jemand die
        ''' Schleuse, und der Vorlauf gibt sie frei.</summary>
        Public Shared ReadOnly Property WaitingCount As Integer
            Get
                Return Volatile.Read(_waiting)
            End Get
        End Property

        ''' <summary>Haelt der aufrufende Faden die Schleuse?</summary>
        Friend Shared ReadOnly Property IsHeldByCurrentThread As Boolean
            Get
                Return Monitor.IsEntered(_gate)
            End Get
        End Property

        ''' <summary>Fuer den Pruefstand: der hoechste Wert gleichzeitiger Halter seit dem letzten
        ''' <see cref="ResetPeakHolders"/>. Die Schleuse laesst nur einen Faden hinein, ueber 1 kann
        ''' es nicht gehen; gemessen wird es trotzdem, damit ein Weg, der die Schleuse umgeht oder
        ''' eine kuenftige Aenderung an ihr, auffaellt.</summary>
        Friend Shared ReadOnly Property PeakHolders As Integer
            Get
                Return Volatile.Read(_peakHolders)
            End Get
        End Property

        Friend Shared Sub ResetPeakHolders()
            Volatile.Write(_peakHolders, Volatile.Read(_holders))
        End Sub

    End Class

End Namespace
