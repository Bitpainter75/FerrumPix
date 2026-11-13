Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq

Namespace Services

    ''' <summary>Wie ein lokaler Ordner fuer Ordnerbaum und Galerie dasteht.</summary>
    Public Enum FolderState
        ''' <summary>Auf der Platte vorhanden.</summary>
        Present
        ''' <summary>Liegt auf einem Laufwerk, das gerade nicht erreichbar ist (OfflineVolumes).
        ''' Geht immer vor <see cref="Missing"/>.</summary>
        Unreachable
        ''' <summary>Auf einem erreichbaren Laufwerk nicht mehr vorhanden und nicht im Papierkorb,
        ''' im Katalog aber noch mit Zeilen: ausserhalb der Anwendung geloescht.</summary>
        Missing
        ''' <summary>Weder auf der Platte noch im Katalog, oder im Papierkorb.</summary>
        Absent
    End Enum

    ''' <summary>Ein Unterordner mit seinem Zustand.</summary>
    Public Structure FolderEntry
        Public ReadOnly Path As String
        Public ReadOnly State As FolderState

        Public Sub New(folderPath As String, folderState As FolderState)
            Path = folderPath
            State = folderState
        End Sub
    End Structure

    ''' <summary>
    ''' Die EINE Regel, welche Unterordner ein Ordner hat und wie jeder davon dasteht. Ordnerbaum
    ''' (FolderNode) und Galerie (Ordnerkacheln, Sofortbestand, Abgleich, Ansicht offline) bauen
    ''' ihre Liste hier; vorher las jede Stelle die Platte selbst, und die Kacheln kannten den
    ''' Zustand nicht, den der Baum schon zeigte.
    '''
    ''' Ein erreichbarer Ordner liefert seine Unterordner von der Platte, dazu die, die nur noch
    ''' der Katalog kennt: liegt so einer auf einem nicht erreichbaren Laufwerk (ein Einhaengeort,
    ''' der beim Aushaengen verschwindet, etwa unter /run/media oder /Volumes), ist er
    ''' <see cref="FolderState.Unreachable"/>; sonst wurde er geloescht, und wenn er nicht im
    ''' Papierkorb liegt (TrashedFolders), ist er <see cref="FolderState.Missing"/>. EINE
    ''' Katalogabfrage je Ordner, nicht je Kind (LibraryService.GetCatalogSubfolders).
    '''
    ''' Ein Ordner, der selbst nicht erreichbar oder nicht mehr vorhanden ist, hat nur die
    ''' Unterordner aus dem Katalog, alle im selben Zustand. Die Platte wird dann nicht gefragt.
    ''' </summary>
    Public NotInheritable Class FolderListing

        Private Sub New()
        End Sub

        ''' <summary>Der Zustand eines Ordners. Zuerst das Laufwerk, dann die Platte, zuletzt der
        ''' Katalog und der Papierkorb; die beiden letzten nur fuer einen Ordner, den es nicht
        ''' gibt.</summary>
        Public Shared Function StateOf(folderPath As String) As FolderState
            If String.IsNullOrWhiteSpace(folderPath) OrElse folderPath.Contains("://", StringComparison.Ordinal) Then Return FolderState.Absent
            If OfflineVolumes.IsFolderUnreachable(folderPath) Then Return FolderState.Unreachable
            If Directory.Exists(folderPath) Then Return FolderState.Present
            Return If(IsMissingCatalogFolder(folderPath), FolderState.Missing, FolderState.Absent)
        End Function

        ''' <summary>Die Unterordner eines Ordners im Zustand <paramref name="state"/>, nach Namen
        ''' sortiert, versteckte nur mit <paramref name="showHidden"/>. Fehler beim Lesen der
        ''' Platte gehen an den Aufrufer, wie bei Directory.GetDirectories: ein Ordner, der sich
        ''' nicht lesen laesst, darf seine Katalogordner nicht als geloescht zeigen.</summary>
        Public Shared Function ListSubfolders(folderPath As String, state As FolderState, showHidden As Boolean) As List(Of FolderEntry)
            Dim result As New List(Of FolderEntry)()
            If String.IsNullOrEmpty(folderPath) OrElse state = FolderState.Absent Then Return result
            If state <> FolderState.Present Then
                For Each child In CatalogSubfolders(folderPath)
                    If IsShown(child, showHidden) Then result.Add(New FolderEntry(child, state))
                Next
                Return result
            End If

            Dim onDisk = Directory.GetDirectories(folderPath)
            Dim diskSet As New HashSet(Of String)(onDisk, PathIdentity.Comparer)
            For Each child In onDisk
                If Not IsShown(child, showHidden) Then Continue For
                result.Add(New FolderEntry(child, If(OfflineVolumes.IsFolderUnreachable(child), FolderState.Unreachable, FolderState.Present)))
            Next
            For Each child In CatalogSubfolders(folderPath)
                If diskSet.Contains(child) OrElse Not IsShown(child, showHidden) Then Continue For
                If OfflineVolumes.IsFolderUnreachable(child) Then
                    result.Add(New FolderEntry(child, FolderState.Unreachable))
                ElseIf Not TrashedFolders.IsTrashed(child) Then
                    result.Add(New FolderEntry(child, FolderState.Missing))
                End If
            Next
            result.Sort(Function(a, b) StringComparer.CurrentCultureIgnoreCase.Compare(Path.GetFileName(a.Path), Path.GetFileName(b.Path)))
            Return result
        End Function

        ''' <summary>Liegt eine DATEI in einem Ordner, den es nicht mehr gibt? Fuer die letzten
        ''' Riegel vor Betrachter und Editor; der Katalog wird dafuer nicht gefragt.</summary>
        Public Shared Function IsInMissingFolder(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) OrElse filePath.Contains("://", StringComparison.Ordinal) Then Return False
            If OfflineVolumes.IsUnreachable(filePath) Then Return False
            Dim folder = Path.GetDirectoryName(filePath)
            Return Not String.IsNullOrEmpty(folder) AndAlso Not Directory.Exists(folder)
        End Function

        ''' <summary>Der kurze Grund, warum ein Element nur zum Ansehen da ist. EIN Text je Grund
        ''' fuer Statuszeile, Hinweise und Menues.</summary>
        Public Shared Function ReasonText(folderMissing As Boolean) As String
            Return If(folderMissing,
                      LocalizationService.T("Ordner nicht mehr vorhanden"),
                      LocalizationService.T("Laufwerk nicht erreichbar"))
        End Function

        ''' <summary>Der Satz ueber einer Ansicht aus dem Katalog.</summary>
        Public Shared Function ViewText(folderMissing As Boolean) As String
            Return If(folderMissing,
                      LocalizationService.T("Ordner nicht mehr vorhanden. Angezeigt wird der Stand aus dem Katalog, nur zum Ansehen."),
                      LocalizationService.T("Laufwerk nicht erreichbar. Angezeigt wird der Stand aus dem Katalog, nur zum Ansehen."))
        End Function

        Private Shared Function IsMissingCatalogFolder(folderPath As String) As Boolean
            Try
                Return LibraryService.Instance.HasCatalogRowsUnder(folderPath) AndAlso Not TrashedFolders.IsTrashed(folderPath)
            Catch ex As Exception
                DiagnosticLogService.LogException("FolderListing.MissingFolder", ex)
                Return False
            End Try
        End Function

        Private Shared Function CatalogSubfolders(folderPath As String) As List(Of String)
            Try
                Return LibraryService.Instance.GetCatalogSubfolders(folderPath)
            Catch ex As Exception
                DiagnosticLogService.LogException("FolderListing.CatalogSubfolders", ex)
                Return New List(Of String)()
            End Try
        End Function

        Private Shared Function IsShown(folderPath As String, showHidden As Boolean) As Boolean
            Return showHidden OrElse Not Path.GetFileName(folderPath).StartsWith("."c)
        End Function

    End Class

End Namespace
