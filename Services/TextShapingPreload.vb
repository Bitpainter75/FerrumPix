Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Runtime.InteropServices

Namespace Services

    ''' <summary>
    ''' Linux: laedt die mitgelieferte HarfBuzz-Bibliothek als Allererstes und bindet sie sofort.
    '''
    ''' <para>WOGEGEN: FreeType ab 2.14 laedt die HarfBuzz des SYSTEMS zur Laufzeit nach, sobald es
    ''' initialisiert wird, und zwar prozessweit sichtbar (RTLD_GLOBAL). Die mitgelieferte Bibliothek
    ''' ist nicht symbolisch gebunden: ihre internen Aufrufe gehen ueber die Sprungtabelle und werden
    ''' erst beim ersten Aufruf aufgeloest. Steht dann die Systemfassung schon im globalen
    ''' Suchbereich, gewinnt sie, und ein Objekt aus der einen Fassung landet in einer Funktion der
    ''' anderen. Das endet beim ersten Anlegen einer Schrift mit "free(): invalid pointer", noch
    ''' bevor ein Fenster steht. Betroffen ist jede Distribution, deren FreeType so gebaut ist; wo
    ''' FreeType fest gegen HarfBuzz gebunden oder aelter ist, passiert nichts.</para>
    '''
    ''' <para>WIE: die eigene Bibliothek wird mit sofortiger Bindung (RTLD_NOW) geladen, solange die
    ''' fremde noch nicht im Prozess ist. Damit sind alle ihre Verweise auf sie selbst festgelegt,
    ''' und was spaeter dazukommt, aendert daran nichts mehr. Die Laufzeit laedt mit spaeter Bindung,
    ''' deshalb geht der Aufruf direkt an dlopen. Laedt die Laufzeit dieselbe Datei danach noch
    ''' einmal, bekommt sie dieselbe, schon gebundene Bibliothek.</para>
    '''
    ''' <para>DIE REIHENFOLGE IST DER GANZE SCHUTZ: der Aufruf muss vor allem stehen, was Schriften
    ''' oder die Zeichenbibliothek anfasst. Er steht deshalb am Anfang von Main.</para>
    ''' </summary>
    Public NotInheritable Class TextShapingPreload

        ''' <summary>RTLD_NOW: alle Verweise beim Laden aufloesen statt beim ersten Aufruf.</summary>
        Private Const BindNow As Integer = 2

        Private Const LibraryFileName As String = "libHarfBuzzSharp.so"

        Private Sub New()
        End Sub

        ' libdl.so.2 und nicht libc: so heisst die Bibliothek auf jeder glibc, auf neueren ist sie
        ' ein leerer Verweis auf libc. Gesucht wird sie erst beim Aufruf, und der steht hinter der
        ' Linux-Abfrage.
        <DllImport("libdl.so.2", EntryPoint:="dlopen")>
        Private Shared Function OpenLibrary(<MarshalAs(UnmanagedType.LPUTF8Str)> path As String,
                                            flags As Integer) As IntPtr
        End Function

        ''' <summary>Wo die Bibliothek liegen kann: neben der Anwendung (jedes Paket) oder unter
        ''' <c>runtimes/&lt;rid&gt;/native</c> (Start aus dem Bauordner).</summary>
        Friend Shared Iterator Function CandidatePaths() As IEnumerable(Of String)
            Dim baseDir = AppContext.BaseDirectory
            Dim rid = If(RuntimeInformation.ProcessArchitecture = Architecture.Arm64, "linux-arm64", "linux-x64")
            Yield Path.Combine(baseDir, LibraryFileName)
            Yield Path.Combine(baseDir, "runtimes", rid, "native", LibraryFileName)
        End Function

        ''' <summary>Laedt die Bibliothek. Das Handle bleibt bis zum Ende des Prozesses offen, das
        ''' ist die Absicht. Ein Fehlschlag haelt den Start nicht auf: dann gilt, was ohne diesen
        ''' Schritt galt.</summary>
        Public Shared Sub Apply()
            If Not OperatingSystem.IsLinux() Then Return
            Try
                For Each candidate In CandidatePaths()
                    If Not File.Exists(candidate) Then Continue For
                    If OpenLibrary(candidate, BindNow) <> IntPtr.Zero Then Return
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("App.TextShapingPreload", ex)
            End Try
        End Sub
    End Class
End Namespace
