Imports System.Diagnostics
Imports System.IO

Namespace Services

    ''' <summary>Verschiebt Dateien/Ordner in den Papierkorb des Betriebssystems statt sie dauerhaft zu
    ''' löschen. Linux: <c>gio trash</c> (glib, auf Desktop-Systemen vorhanden). Windows: Papierkorb über
    ''' Microsoft.VisualBasic.FileIO. Liefert False, wenn kein Papierkorb verfügbar ist oder die Verschiebung
    ''' scheitert - der Aufrufer entscheidet dann, ob er eine Fehlermeldung zeigt, statt still dauerhaft zu
    ''' löschen.</summary>
    Public NotInheritable Class TrashService
        Private Sub New()
        End Sub

        Public Shared Function MoveToTrash(path As String) As Boolean
            If String.IsNullOrEmpty(path) Then Return False
            If Not (File.Exists(path) OrElse Directory.Exists(path)) Then Return False
            Try
                If OperatingSystem.IsWindows() Then
                    Return MoveToTrashWindows(path)
                Else
                    Return MoveToTrashViaGio(path)
                End If
            Catch
                Return False
            End Try
        End Function

        Private Shared Function MoveToTrashWindows(path As String) As Boolean
            ' RecycleOption/RecycleBin funktioniert nur unter Windows; der Aufruf ist durch
            ' OperatingSystem.IsWindows() abgesichert. OnlyErrorDialogs = keine Rückfrage, nur bei Fehlern eine.
            If Directory.Exists(path) Then
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin)
            Else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin)
            End If
            Return True
        End Function

        Private Shared Function MoveToTrashViaGio(path As String) As Boolean
            Dim psi As New ProcessStartInfo("gio") With {
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }
            psi.ArgumentList.Add("trash")
            psi.ArgumentList.Add("--")
            psi.ArgumentList.Add(path)

            Using proc = Process.Start(psi)
                If proc Is Nothing Then Return False
                If Not proc.WaitForExit(15000) Then
                    Try : proc.Kill(True) : Catch : End Try
                    Return False
                End If
                Return proc.ExitCode = 0
            End Using
        End Function
    End Class

End Namespace
