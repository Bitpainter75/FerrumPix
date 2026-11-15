Imports System
Imports System.Diagnostics
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>
    ''' Gibt freien Speicher der C-Bibliothek ans System zurueck.
    '''
    ''' WOZU. Ein RAW-Decode legt nativ mehrere hundert Megabyte an und gibt sie wieder frei. Die
    ''' grossen Bloecke gehen dabei direkt ans System zurueck, die vielen kleineren (Zeilen,
    ''' Tabellen, Vorschaubilder) bleiben als freie Stuecke im Haufen der C-Bibliothek liegen: der
    ''' Prozess belegt sie weiter, obwohl niemand sie benutzt. Gemessen am Betrachter nach 60
    ''' geloeschten RAWs mit 33 Megapixeln und voller Bereinigung: 1117 MB belegt, nach dem
    ''' Zurueckgeben 1007 MB.
    '''
    ''' NUR UNTER LINUX MIT GLIBC (malloc_trim). Windows und macOS haben eigene Haufen, die das
    ''' selbst tun; fehlt der Einstieg (eine andere C-Bibliothek), bleibt es beim ersten Versuch.
    ''' Aufgerufen wird nach einem RAW-Decode, hoechstens alle <see cref="MinimumIntervalMs"/>
    ''' Millisekunden und im Hintergrund: der Aufruf geht ueber alle Bereiche des Haufens und soll
    ''' keinen Decode aufhalten.
    ''' </summary>
    Friend NotInheritable Class NativeHeap

        Private Sub New()
        End Sub

        ''' <summary>So lange liegt mindestens zwischen zwei Aufrufen.</summary>
        Private Const MinimumIntervalMs As Long = 5000

        <DllImport("libc", EntryPoint:="malloc_trim")>
        Private Shared Function MallocTrim(pad As UIntPtr) As Integer
        End Function

        ''' <summary>0 = noch nicht versucht, 1 = geht, -1 = gibt es hier nicht.</summary>
        Private Shared _available As Integer

        Private Shared _lastTicks As Long

        Private Shared _queued As Integer

        ''' <summary>Merkt das Zurueckgeben vor. Kehrt sofort zurueck; mehrere Anstoesse kurz
        ''' nacheinander ergeben einen Aufruf.</summary>
        Friend Shared Sub TrimSoon()
            If Volatile.Read(_available) < 0 OrElse Not OperatingSystem.IsLinux() Then Return
            Dim now = Environment.TickCount64
            If now - Interlocked.Read(_lastTicks) < MinimumIntervalMs Then Return
            If Interlocked.Exchange(_queued, 1) <> 0 Then Return
            Task.Run(Sub()
                         Try
                             Interlocked.Exchange(_lastTicks, Environment.TickCount64)
                             MallocTrim(UIntPtr.Zero)
                             Volatile.Write(_available, 1)
                         Catch ex As Exception When TypeOf ex Is EntryPointNotFoundException OrElse TypeOf ex Is DllNotFoundException
                             Volatile.Write(_available, -1)
                         Catch ex As Exception
                             DiagnosticLogService.LogException("NativeHeap.Trim", ex)
                             Volatile.Write(_available, -1)
                         Finally
                             Interlocked.Exchange(_queued, 0)
                         End Try
                     End Sub)
        End Sub
    End Class

End Namespace
