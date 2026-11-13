Imports System
Imports System.Collections.Generic
Imports System.IO

Namespace Services

    ''' <summary>
    ''' In welcher Reihenfolge die Ladewege fuer LibRaw, libheif und libjxl ihre Kandidaten probieren.
    '''
    ''' DIE MITGELIEFERTE KOMMT UEBERALL ZUERST, auf jeder Plattform und in jedem Paket, das eine
    ''' mitbringt. Die Bibliothek des Systems oder aus Homebrew ist nur noch der Rueckfall, falls die
    ''' eigene fehlt oder nicht laedt. Der Grund ist der Support: auf jedem Rechner entwickelt und dekodiert dieselbe Fassung,
    ''' ein Befund laesst sich also ohne die Frage nachstellen, welche Bibliothek gerade geladen ist.
    ''' Bei LibRaw kommt dazu, dass die mitgelieferte ein Snapshot ist und Kameras kennt, die das
    ''' letzte Release der Distribution noch nicht kennt (Nikon High Efficiency etwa), und dass Ubuntu
    ''' 22.04 und Linux Mint 21 nur LibRaw 0.20 (Soname 20, den FerrumPix nicht laedt), libheif 1.12
    ''' und gar keine libjxl fuehren.
    '''
    ''' Mit eigenen Dateien kommen Windows, macOS, AppImage, portables ZIP (und damit AUR), deb und
    ''' rpm. Nur der Flatpak bringt LibRaw als eigenes Modul mit und nimmt libheif und libjxl aus
    ''' seiner Laufzeit; keine davon liegt unter dem Verzeichnis der Anwendung, er laedt also ueber
    ''' den Suchpfad.
    ''' </summary>
    Friend NotInheritable Class BundledLibraryOrder
        Private Sub New()
        End Sub

        ''' <summary>Ist der Kandidat eine mitgelieferte Datei, also ein voller Pfad unterhalb des
        ''' Verzeichnisses der Anwendung?</summary>
        Friend Shared Function IsBundledPath(candidate As String, baseDirectory As String) As Boolean
            If String.IsNullOrEmpty(candidate) OrElse Not Path.IsPathRooted(candidate) Then Return False
            Dim app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)) & Path.DirectorySeparatorChar
            Return Path.GetFullPath(candidate).StartsWith(app, StringComparison.Ordinal)
        End Function

        ''' <summary>Die Kandidaten in der Reihenfolge, in der sie probiert werden: erst die
        ''' mitgelieferten, dann die uebrigen (System, Homebrew), jeweils in der Folge, in der der
        ''' Aufrufer sie liefert. Reine Rechnung ohne Umgebung, damit sie sich pruefen laesst.</summary>
        Friend Shared Function Arrange(Of T)(candidates As IEnumerable(Of T), pathOf As Func(Of T, String),
                                             baseDirectory As String) As List(Of T)
            Dim bundled As New List(Of T)()
            Dim others As New List(Of T)()
            For Each item In candidates
                If IsBundledPath(pathOf(item), baseDirectory) Then bundled.Add(item) Else others.Add(item)
            Next
            bundled.AddRange(others)
            Return bundled
        End Function

        ''' <summary>Dasselbe fuer die laufende Anwendung.</summary>
        Friend Shared Function Arrange(candidates As IEnumerable(Of String)) As List(Of String)
            Return Arrange(candidates, Function(c) c, AppContext.BaseDirectory)
        End Function
    End Class

End Namespace
