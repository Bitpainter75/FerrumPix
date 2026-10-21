Imports System.Threading.Tasks
Imports Avalonia.Controls
Imports Avalonia.Platform.Storage

Namespace Views

    ''' <summary>Der Startordner einer Ordnerauswahl. Ohne ihn beginnt das Auswahlfenster des Systems
    ''' im persoenlichen Ordner, auch wenn im Feld daneben schon ein Ordner steht, und wer in einen
    ''' Unterordner auf einem anderen Laufwerk will, klickt sich jedes Mal von vorn durch
    ''' (Diskussion 95: aus /home/... nach /run/media/.../2026/10/20/converted).</summary>
    Friend NotInheritable Class FolderPickerStart
        Private Sub New()
        End Sub

        ''' <summary>Der Ordner zu <paramref name="path"/>, oder sein naechster vorhandener
        ''' Elternordner: ein eben eingetippter Unterordner wie "converted" gibt es oft noch nicht.
        ''' Nothing ohne Pfad oder wenn kein Teil davon existiert; dann bleibt es beim Systemstandard.</summary>
        Public Shared Async Function ForPathAsync(topLevel As TopLevel, path As String) As Task(Of IStorageFolder)
            If topLevel Is Nothing OrElse String.IsNullOrWhiteSpace(path) Then Return Nothing
            Try
                Dim candidate = IO.Path.GetFullPath(path.Trim())
                While Not String.IsNullOrEmpty(candidate) AndAlso Not IO.Directory.Exists(candidate)
                    candidate = IO.Path.GetDirectoryName(candidate)
                End While
                If String.IsNullOrEmpty(candidate) Then Return Nothing
                Return Await topLevel.StorageProvider.TryGetFolderFromPathAsync(candidate)
            Catch
                Return Nothing
            End Try
        End Function
    End Class

End Namespace
