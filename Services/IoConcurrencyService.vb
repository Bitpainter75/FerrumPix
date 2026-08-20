Imports System
Imports System.Collections.Concurrent
Imports System.IO
Imports System.Runtime.InteropServices

Namespace Services

    ''' <summary>Wie viele Leser duerfen gleichzeitig auf denselben Datentraeger?
    '''
    ''' Bisher stand diese Zahl fest: vier Kachel-Arbeiter, und fuer das Nachladen der Bilddaten die
    ''' halbe Kernzahl. Auf einem Rechner mit 24 Kernen sind das zwoelf gleichzeitige Leser. Das ist
    ''' auf einer SSD richtig und auf einer DREHENDEN Platte falsch: dort kostet jeder Wechsel
    ''' zwischen zwei Dateien eine Kopfbewegung, und zwoelf Leser bewegen den Kopf zwoelfmal so oft.
    ''' Die Platte wird dadurch nicht schneller, sondern langsamer, und die Oberflaeche haengt an
    ''' Lesevorgaengen, die einzeln in Bruchteilen einer Sekunde fertig waeren.
    '''
    ''' Zwei Grenzen also, beide erst zur Laufzeit bekannt:
    '''
    ''' 1. DER DATENTRAEGER. Dreht er, bleiben zwei Leser. Verlaesslich beantworten laesst sich das
    '''    nur unter Linux (<c>/sys/block/.../queue/rotational</c>); Windows und macOS bieten dafuer
    '''    keinen Weg ohne zusaetzliche Abhaengigkeit. Dort bleibt es deshalb beim bisherigen Wert -
    '''    das ist kein Rueckschritt, sondern der Zustand von vorher.
    '''
    ''' 2. DER FREIE ARBEITSSPEICHER. Jeder Leser haelt ein Bild im Speicher, und bei 45 Megapixeln
    '''    sind das 180 MB. Auf einem knappen Rechner ist die Kernzahl damit die falsche Grenze.
    '''    Diese Frage beantwortet <c>GC.GetGCMemoryInfo</c> auf allen drei Systemen gleich.
    '''
    ''' NICHT betroffen ist der Decode: davon laeuft ohnehin immer nur einer (<see cref="DecodeGate"/>).
    ''' Ebenfalls nicht betroffen sind Serverbilder - sie kommen ueber das Netz, dort gilt die
    ''' Kopfbewegung nicht und die hoehere Zahl bleibt richtig.</summary>
    Public NotInheritable Class IoConcurrencyService

        ' Je Ordner einmal ermittelt: der Weg dahin liest /proc/mounts und eine Datei unter /sys,
        ' und die Antwort aendert sich waehrend einer Sitzung nicht.
        Private Shared ReadOnly _rotationalByRoot As New ConcurrentDictionary(Of String, Boolean?)()

        ''' <summary>Leser auf einer drehenden Platte. Zwei und nicht einer: das Lesen einer Datei ist
        ''' nicht nur Kopfbewegung, und ein zweiter Leser fuellt die Wartezeit des ersten.</summary>
        Private Const RotationalReaderCap As Integer = 2

        ''' <summary>Speicher, den ein Leser im unguenstigsten Fall braucht (ein Bild mit 45
        ''' Megapixeln als RGBA plus Zwischenstand).</summary>
        Private Const BytesPerReader As Long = 384L * 1024L * 1024L

        Private Sub New()
        End Sub

        ''' <summary>Empfohlene Zahl gleichzeitiger Leser fuer diesen Pfad, hoechstens
        ''' <paramref name="preferred"/> und mindestens einer.</summary>
        ' Der Parameter heisst NICHT "path": VB unterscheidet keine Gross- und Kleinschreibung, ein
        ' Parameter dieses Namens verdeckt System.IO.Path im ganzen Koerper.
        Public Shared Function RecommendedReaders(targetPath As String, preferred As Integer) As Integer
            Dim result = Math.Max(1, preferred)

            If IsRotational(targetPath) Then result = Math.Min(result, RotationalReaderCap)

            Dim byMemory = ReadersByFreeMemory()
            If byMemory > 0 Then result = Math.Min(result, byMemory)

            Return Math.Max(1, result)
        End Function

        ''' <summary>Wie viele Leser traegt der freie Arbeitsspeicher? 0 heisst "nicht zu ermitteln",
        ''' dann entscheidet er nicht mit.</summary>
        Public Shared Function ReadersByFreeMemory() As Integer
            Try
                Dim info = GC.GetGCMemoryInfo()
                Dim total = info.TotalAvailableMemoryBytes
                If total <= 0 Then Return 0
                ' MemoryLoadBytes ist die Last des ganzen SYSTEMS, nicht nur dieses Prozesses -
                ' genau die richtige Groesse, denn die Platte teilen wir uns mit allem anderen.
                Dim free = total - info.MemoryLoadBytes
                If free <= 0 Then Return 1
                Return CInt(Math.Max(1L, Math.Min(CLng(Integer.MaxValue), free \ BytesPerReader)))
            Catch
                Return 0
            End Try
        End Function

        ''' <summary>Liegt dieser Pfad auf einer drehenden Platte? False auch dann, wenn es sich
        ''' nicht feststellen laesst - eine Vermutung darf die Zahl nicht senken.</summary>
        Public Shared Function IsRotational(targetPath As String) As Boolean
            Dim state = RotationalState(targetPath)
            Return state.HasValue AndAlso state.Value
        End Function

        ''' <summary>Die Auskunft selbst, mit dem Unterschied zwischen "erkannt, dreht nicht" und
        ''' "nicht feststellbar". Die Trennung ist nicht kosmetisch: nur an ihr sieht man, ob die
        ''' Erkennung auf einem Rechner ueberhaupt greift oder stumm durchfaellt.</summary>
        Public Shared Function RotationalState(targetPath As String) As Boolean?
            If String.IsNullOrWhiteSpace(targetPath) Then Return Nothing
            If Not RuntimeInformation.IsOSPlatform(OSPlatform.Linux) Then Return Nothing

            Dim root As String
            Try
                root = Path.GetFullPath(targetPath)
                If Not Directory.Exists(root) Then root = Path.GetDirectoryName(root)
                If String.IsNullOrEmpty(root) Then Return Nothing
            Catch
                Return Nothing
            End Try

            Return _rotationalByRoot.GetOrAdd(root, Function(key) DetermineRotational(key))
        End Function

        ''' <summary>Nothing = nicht feststellbar (Netzlaufwerk, verschluesselter Verbund, unbekannte
        ''' Bauform).</summary>
        Private Shared Function DetermineRotational(fullPath As String) As Boolean?
            Try
                Dim device = FindMountDevice(fullPath)
                If String.IsNullOrEmpty(device) Then Return Nothing
                If Not device.StartsWith("/dev/", StringComparison.Ordinal) Then Return Nothing

                ' /dev/sda1 -> sda, /dev/nvme0n1p2 -> nvme0n1. Die Partitionsnummer faellt weg, denn
                ' die Bauform gehoert dem Geraet.
                Dim name = device.Substring("/dev/".Length)
                Dim blockPath = "/sys/block/" & name & "/queue/rotational"
                If Not File.Exists(blockPath) Then
                    Dim trimmed = TrimPartitionSuffix(name)
                    If String.IsNullOrEmpty(trimmed) Then Return Nothing
                    blockPath = "/sys/block/" & trimmed & "/queue/rotational"
                    If Not File.Exists(blockPath) Then Return Nothing
                End If

                Dim value = File.ReadAllText(blockPath).Trim()
                If value = "1" Then Return True
                If value = "0" Then Return False
                Return Nothing
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Das Geraet zum laengsten passenden Einhaengepunkt. Der LAENGSTE gewinnt: liegt
        ''' der Fotobestand auf einer eigenen Platte unter /home/bilder, ist die Antwort deren
        ''' Bauform und nicht die von /.</summary>
        Private Shared Function FindMountDevice(fullPath As String) As String
            Dim bestMount As String = Nothing
            Dim bestDevice As String = Nothing

            For Each line In File.ReadLines("/proc/mounts")
                Dim parts = line.Split(" "c)
                If parts.Length < 2 Then Continue For
                Dim device = UnescapeMountPath(parts(0))
                Dim mountPoint = UnescapeMountPath(parts(1))
                If String.IsNullOrEmpty(mountPoint) Then Continue For
                If Not PathStartsWith(fullPath, mountPoint) Then Continue For
                If bestMount Is Nothing OrElse mountPoint.Length > bestMount.Length Then
                    bestMount = mountPoint
                    bestDevice = device
                End If
            Next

            Return bestDevice
        End Function

        ''' <summary>/proc/mounts schreibt Leerzeichen und Rueckwaertsschraegstriche in Pfaden als
        ''' Oktalfolge. Ohne das Zuruecksetzen findet ein Ordner mit Leerzeichen seinen
        ''' Einhaengepunkt nicht.</summary>
        Private Shared Function UnescapeMountPath(value As String) As String
            If String.IsNullOrEmpty(value) OrElse Not value.Contains("\") Then Return value
            Dim builder As New Text.StringBuilder(value.Length)
            Dim i = 0
            While i < value.Length
                If value(i) = "\"c AndAlso i + 3 < value.Length Then
                    Dim code = value.Substring(i + 1, 3)
                    Dim number As Integer
                    If code.Length = 3 AndAlso IsOctal(code) Then
                        number = Convert.ToInt32(code, 8)
                        builder.Append(ChrW(number))
                        i += 4
                        Continue While
                    End If
                End If
                builder.Append(value(i))
                i += 1
            End While
            Return builder.ToString()
        End Function

        Private Shared Function IsOctal(value As String) As Boolean
            For Each c In value
                If c < "0"c OrElse c > "7"c Then Return False
            Next
            Return True
        End Function

        ''' <summary>Pfadvergleich auf GANZE Abschnitte: /home/bild darf nicht als Einhaengepunkt
        ''' von /home/bilder durchgehen.</summary>
        Private Shared Function PathStartsWith(fullPath As String, mountPoint As String) As Boolean
            If mountPoint = "/" Then Return True
            If Not fullPath.StartsWith(mountPoint, StringComparison.Ordinal) Then Return False
            Return fullPath.Length = mountPoint.Length OrElse fullPath(mountPoint.Length) = "/"c
        End Function

        ''' <summary>sda1 -> sda, nvme0n1p2 -> nvme0n1, mmcblk0p1 -> mmcblk0.</summary>
        Private Shared Function TrimPartitionSuffix(name As String) As String
            If String.IsNullOrEmpty(name) Then Return Nothing

            ' Geraete mit "p" vor der Partitionsnummer (nvme, mmcblk, loop).
            Dim pIndex = name.LastIndexOf("p"c)
            If pIndex > 0 AndAlso pIndex < name.Length - 1 AndAlso AllDigits(name.Substring(pIndex + 1)) Then
                Dim head = name.Substring(0, pIndex)
                If head.Length > 0 AndAlso Char.IsDigit(head(head.Length - 1)) Then Return head
            End If

            ' Der einfache Fall: sda1, hdb2.
            Dim cut = name.Length
            While cut > 0 AndAlso Char.IsDigit(name(cut - 1))
                cut -= 1
            End While
            If cut = 0 OrElse cut = name.Length Then Return Nothing
            Return name.Substring(0, cut)
        End Function

        Private Shared Function AllDigits(value As String) As Boolean
            If String.IsNullOrEmpty(value) Then Return False
            For Each c In value
                If Not Char.IsDigit(c) Then Return False
            Next
            Return True
        End Function

    End Class

End Namespace
