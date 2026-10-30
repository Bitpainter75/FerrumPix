Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text

Namespace Services

    ''' <summary>
    ''' Sagt fuer einen Pfad, ob der Datentraeger, auf dem er liegt, gerade erreichbar ist.
    '''
    ''' WOZU: eine Datei, die fehlt, weil ihre Platte nicht eingehaengt ist, sieht fuer
    ''' <c>File.Exists</c> genauso aus wie eine geloeschte. Wer dann "Datenbank bereinigen" drueckt,
    ''' verliert Bewertungen, Stichwoerter und Personen eines ganzen Laufwerks. Mit dieser Auskunft
    ''' bleiben solche Eintraege stehen, bis der Nutzer ausdruecklich anders entscheidet.
    '''
    ''' WIE, je System:
    ''' <list type="bullet">
    ''' <item>Linux: Ein Pfad unter einem Einhaengeort fuer Wechseldatentraeger
    ''' (<c>/run/media/BENUTZER/NAME</c>, <c>/media/BENUTZER/NAME</c>, <c>/media/NAME</c>,
    ''' <c>/mnt/NAME</c>) oder unter einem Eintrag aus <c>/etc/fstab</c> verlangt, dass genau dieser
    ''' Ort in <c>/proc/self/mountinfo</c> steht. Ein leerer Ordner an der Stelle genuegt nicht:
    ''' unter <c>/mnt</c> bleibt er nach dem Aushaengen stehen. Eine Freigabe ueber GVFS
    ''' (<c>/run/user/UID/gvfs/NAME</c>) gilt als erreichbar, solange ihr Ordner da ist.</item>
    ''' <item>macOS: <c>/Volumes/NAME</c> muss da sein; der Ordner verschwindet beim Auswerfen.</item>
    ''' <item>Windows: das Laufwerk muss bereit sein, eine Freigabe (\\server\name) erreichbar.</item>
    ''' </list>
    ''' Alles andere (der Benutzerordner, die Systemplatte) gilt als erreichbar.
    '''
    ''' Die Liste der Einhaengeorte wird EINMAL je Auskunftsobjekt gelesen (<see cref="Snapshot"/>),
    ''' nicht je Pfad: ein Katalog hat Zehntausende Zeilen.
    ''' </summary>
    Public NotInheritable Class VolumePresence

        Private ReadOnly _mounted As HashSet(Of String)
        Private ReadOnly _fstabRoots As List(Of String)
        Private ReadOnly _verdicts As New Dictionary(Of String, String)(StringComparer.Ordinal)

        Private Sub New(mounted As HashSet(Of String), fstabRoots As List(Of String))
            _mounted = mounted
            _fstabRoots = fstabRoots
        End Sub

        ''' <summary>Liest den Stand der Einhaengeorte jetzt.</summary>
        Public Shared Function Snapshot() As VolumePresence
            Dim mounted As New HashSet(Of String)(StringComparer.Ordinal)
            Dim fstab As New List(Of String)()
            If OperatingSystem.IsLinux() Then
                Try
                    For Each line In File.ReadLines("/proc/self/mountinfo")
                        Dim fields = line.Split(" "c)
                        If fields.Length > 4 Then mounted.Add(Unescape(fields(4)))
                    Next
                Catch ex As Exception
                    DiagnosticLogService.LogException("VolumePresence.Mountinfo", ex)
                End Try
                Try
                    If File.Exists("/etc/fstab") Then
                        For Each raw In File.ReadLines("/etc/fstab")
                            Dim line = raw.Trim()
                            If line.Length = 0 OrElse line.StartsWith("#"c) Then Continue For
                            Dim fields = line.Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                            If fields.Length < 2 Then Continue For
                            Dim root = Unescape(fields(1))
                            If root.StartsWith("/"c) AndAlso root <> "/" Then fstab.Add(root.TrimEnd("/"c))
                        Next
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("VolumePresence.Fstab", ex)
                End Try
            End If
            Return New VolumePresence(mounted, fstab)
        End Function

        ''' <summary>Fuer den Pruefstand: ein Stand mit vorgegebenen Einhaengeorten.</summary>
        Friend Shared Function ForTest(mounted As IEnumerable(Of String), fstabRoots As IEnumerable(Of String)) As VolumePresence
            Return New VolumePresence(New HashSet(Of String)(mounted, StringComparer.Ordinal), fstabRoots.ToList())
        End Function

        ''' <summary>Leer, wenn der Datentraeger des Pfads erreichbar ist; sonst sein Einhaengeort
        ''' (etwa <c>/run/media/patrick/T7</c>), der in einer Rueckfrage genannt werden kann.</summary>
        Public Function UnreachableRootOf(path As String) As String
            If String.IsNullOrWhiteSpace(path) Then Return ""
            Dim root = ExpectedRoot(path)
            If root.Length = 0 Then Return ""
            Dim verdict As String = Nothing
            If _verdicts.TryGetValue(root, verdict) Then Return verdict
            verdict = If(IsRootReachable(root), "", root)
            _verdicts(root) = verdict
            Return verdict
        End Function

        ''' <summary>Der Einhaengeort, auf den es fuer diesen Pfad ankommt, oder "", wenn keiner
        ''' gesondert zu pruefen ist.</summary>
        Private Function ExpectedRoot(path As String) As String
            If OperatingSystem.IsWindows() Then
                Try
                    Return If(IO.Path.GetPathRoot(path), "")
                Catch
                    Return ""
                End Try
            End If

            Dim parts = path.Split("/"c, StringSplitOptions.RemoveEmptyEntries)
            If OperatingSystem.IsMacOS() Then
                If parts.Length >= 3 AndAlso parts(0) = "Volumes" Then Return "/Volumes/" & parts(1)
                Return ""
            End If

            ' Ein Eintrag aus fstab geht vor: er ist ausdruecklich als eigener Datentraeger gedacht.
            ' Der laengste passende gewinnt (/mnt/nas/fotos vor /mnt/nas).
            Dim best = ""
            For Each fstabRoot In _fstabRoots
                If (path = fstabRoot OrElse path.StartsWith(fstabRoot & "/", StringComparison.Ordinal)) AndAlso
                   fstabRoot.Length > best.Length Then best = fstabRoot
            Next
            If best.Length > 0 Then Return best

            If parts.Length >= 5 AndAlso parts(0) = "run" AndAlso parts(1) = "media" Then Return "/run/media/" & parts(2) & "/" & parts(3)
            If parts.Length >= 5 AndAlso parts(0) = "run" AndAlso parts(1) = "user" AndAlso parts(3) = "gvfs" Then
                Return "/run/user/" & parts(2) & "/gvfs/" & parts(4)
            End If
            If parts.Length >= 3 AndAlso parts(0) = "media" Then
                If parts.Length >= 4 AndAlso parts(1) = Environment.UserName Then Return "/media/" & parts(1) & "/" & parts(2)
                Return "/media/" & parts(1)
            End If
            If parts.Length >= 3 AndAlso parts(0) = "mnt" Then Return "/mnt/" & parts(1)
            Return ""
        End Function

        Private Function IsRootReachable(root As String) As Boolean
            Try
                If OperatingSystem.IsWindows() Then
                    If root.StartsWith("\\", StringComparison.Ordinal) Then Return Directory.Exists(root)
                    Return New DriveInfo(root).IsReady
                End If
                If OperatingSystem.IsMacOS() Then Return Directory.Exists(root)
                If root.Contains("/gvfs/") Then Return Directory.Exists(root)
                ' Ohne lesbare mountinfo laesst sich nichts sagen; dann lieber "erreichbar" und das
                ' alte Verhalten als jede Datei auf jeder Platte festzuhalten.
                If _mounted.Count = 0 Then Return True
                Return _mounted.Contains(root)
            Catch
                Return False
            End Try
        End Function

        ''' <summary>mountinfo und fstab schreiben Leerzeichen und Tabulatoren als Oktalfolge
        ''' (\040, \011).</summary>
        Private Shared Function Unescape(value As String) As String
            If value.IndexOf("\"c) < 0 Then Return value
            Dim result As New StringBuilder(value.Length)
            Dim i = 0
            While i < value.Length
                If value(i) = "\"c AndAlso i + 3 < value.Length AndAlso
                   Char.IsDigit(value(i + 1)) AndAlso Char.IsDigit(value(i + 2)) AndAlso Char.IsDigit(value(i + 3)) Then
                    result.Append(ChrW(Convert.ToInt32(value.Substring(i + 1, 3), 8)))
                    i += 4
                Else
                    result.Append(value(i))
                    i += 1
                End If
            End While
            Return result.ToString()
        End Function

    End Class

End Namespace
