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

        Private Sub New()
        End Sub

        ''' <summary>Fuehrt die Arbeit aus, sobald kein anderer Decode mehr laeuft.</summary>
        Public Shared Function Run(Of T)(work As Func(Of T)) As T
            If work Is Nothing Then Return Nothing
            SyncLock _gate
                Interlocked.Increment(_running)
                Try
                    Return work()
                Finally
                    Interlocked.Decrement(_running)
                End Try
            End SyncLock
        End Function

        ''' <summary>Laeuft gerade einer? Nur zum Messen und Melden gedacht.</summary>
        Public Shared ReadOnly Property IsBusy As Boolean
            Get
                Return Volatile.Read(_running) > 0
            End Get
        End Property

    End Class

End Namespace
