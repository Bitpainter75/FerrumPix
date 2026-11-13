Imports System
Imports System.Collections.Generic
Imports System.IO

Namespace Services

    ''' <summary>
    ''' In welcher Reihenfolge die Ladewege fuer LibRaw, libheif und libjxl ihre Kandidaten probieren:
    ''' die Bibliothek des Systems oder die mitgelieferte zuerst.
    '''
    ''' UEBERALL GILT DAS SYSTEM ZUERST, die mitgelieferte Datei ist der Rueckfall. Eine vom
    ''' Paketverwalter gepflegte Bibliothek bekommt Sicherheitsaktualisierungen und neue Kameras.
    ''' Das gilt auch fuer das portable Linux-ZIP, auf dem das AUR-Paket aufsetzt.
    '''
    ''' DIE AUSNAHME IST DIE APPIMAGE. Sie ist fuer Systeme gedacht, deren eigene Bibliotheken zu alt
    ''' sind oder ganz fehlen: Ubuntu 22.04 und Linux Mint 21 bringen LibRaw 0.20 (Soname 20, den
    ''' FerrumPix nicht laedt), libheif 1.12 und gar keine libjxl. Dort bringt die AppImage ihre
    ''' eigenen mit, gebaut gegen die glibc von Ubuntu 22.04 (packaging/libraw, libheif und libjxl,
    ''' je build-linux.sh), und nimmt diese ZUERST. Sonst gewaenne auf einem System mit einer alten,
    ''' aber ladbaren Fassung die alte, und die AppImage verhielte sich auf jedem Rechner anders.
    '''
    ''' Erkannt wird die AppImage an der Variablen APPDIR, die ihre Laufzeit beim Start setzt, UND
    ''' daran, dass die Anwendung selbst darin liegt. Die Variable allein reicht nicht: ein aus einer
    ''' AppImage heraus gestartetes Programm erbt sie, auch ein installiertes FerrumPix.
    ''' </summary>
    Friend NotInheritable Class BundledLibraryOrder
        Private Sub New()
        End Sub

        Private Shared ReadOnly _preferBundled As Lazy(Of Boolean) = New Lazy(Of Boolean)(
            Function() OperatingSystem.IsLinux() AndAlso
                       IsInsideAppDir(Environment.GetEnvironmentVariable("APPDIR"), AppContext.BaseDirectory))

        ''' <summary>True, wenn die mitgelieferten Bibliotheken vor denen des Systems kommen: nur in
        ''' einer laufenden AppImage.</summary>
        Friend Shared ReadOnly Property PreferBundled As Boolean
            Get
                Return _preferBundled.Value
            End Get
        End Property

        ''' <summary>Liegt das Verzeichnis der Anwendung im eingehaengten AppDir? Reine Rechnung ohne
        ''' Umgebung, damit sie sich pruefen laesst.</summary>
        Friend Shared Function IsInsideAppDir(appDir As String, baseDirectory As String) As Boolean
            If String.IsNullOrWhiteSpace(appDir) OrElse String.IsNullOrWhiteSpace(baseDirectory) Then Return False
            Dim root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDir)) & Path.DirectorySeparatorChar
            Dim app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)) & Path.DirectorySeparatorChar
            Return app.StartsWith(root, StringComparison.Ordinal)
        End Function

        ''' <summary>Ist der Kandidat eine mitgelieferte Datei, also ein voller Pfad unterhalb des
        ''' Verzeichnisses der Anwendung?</summary>
        Friend Shared Function IsBundledPath(candidate As String, baseDirectory As String) As Boolean
            If String.IsNullOrEmpty(candidate) OrElse Not Path.IsPathRooted(candidate) Then Return False
            Dim app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)) & Path.DirectorySeparatorChar
            Return Path.GetFullPath(candidate).StartsWith(app, StringComparison.Ordinal)
        End Function

        ''' <summary>Die Kandidaten in der Reihenfolge, in der sie probiert werden. Ohne Vorrang
        ''' unveraendert (System, Homebrew, mitgeliefert, wie der Aufrufer sie liefert); mit Vorrang
        ''' erst die mitgelieferten und dann die uebrigen, jeweils in ihrer bisherigen Folge.</summary>
        Friend Shared Function Arrange(Of T)(candidates As IEnumerable(Of T), pathOf As Func(Of T, String),
                                             preferBundled As Boolean, baseDirectory As String) As List(Of T)
            Dim all As New List(Of T)(candidates)
            If Not preferBundled Then Return all
            Dim bundled As New List(Of T)()
            Dim others As New List(Of T)()
            For Each item In all
                If IsBundledPath(pathOf(item), baseDirectory) Then bundled.Add(item) Else others.Add(item)
            Next
            bundled.AddRange(others)
            Return bundled
        End Function

        ''' <summary>Dasselbe fuer die laufende Anwendung: Vorrang nur in der AppImage.</summary>
        Friend Shared Function Arrange(candidates As IEnumerable(Of String)) As List(Of String)
            Return Arrange(candidates, Function(c) c, PreferBundled, AppContext.BaseDirectory)
        End Function
    End Class

End Namespace
