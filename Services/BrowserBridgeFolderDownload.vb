Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq

Namespace Services

    ''' <summary>
    ''' "Herunterladen" an einem Ordner im Browser: der ganze Ordner als EIN ZIP, mit dem Namen des
    ''' Ordners, samt Unterordnern und Beistelldateien.
    '''
    ''' <para>DAS ANGEBOT NENNT NUR DEN ORDNER. Welche Dateien hineingehen, entscheidet erst die
    ''' Auslieferung (<see cref="PlanFolderDownloadEntries"/>, im Faden der Anfrage): eine
    ''' Vollzaehlung vorab im Anzeigefaden haette bei einem grossen Ordner die Oberflaeche
    ''' angehalten, und zwischen Angebot und Klick auf "erneut versuchen" darf sich der Ordner
    ''' aendern. Vorab zaehlt nur <see cref="SurveyDownloadFolder"/>, im Hintergrund und nur bis
    ''' zu einer Grenze.</para>
    '''
    ''' <para>WAS HINEINGEHT: jede Datei des Ordners und seiner Unterordner unter ihrem Pfad
    ''' relativ zum Ordner, mit dem Ordnernamen davor, also auch Beistelldateien und Dateien, die
    ''' keine Bilder sind. NICHT hinein gehen versteckte Dateien und Ordner (Name mit Punkt vorn
    ''' oder als versteckt oder Systemdatei markiert) und symbolische Verweise, auf Dateien wie auf
    ''' Ordner: einer nach draussen fuehrte aus dem Ordner (und mit festen Fotoordnern aus ihnen)
    ''' heraus, einer nach drinnen braechte nur doppelt, was ohnehin im ZIP steht, und ein Verweis
    ''' auf einen Ordner darueber liefe im Kreis.</para>
    ''' </summary>
    Partial Public NotInheritable Class BrowserBridge

        ''' <summary>So viele Bytes duerfen die Eintraege eines Ordner-ZIP zusammen im Speicher
        ''' gepackt liegen (StreamingZipWriter.PlanFile packt kleine TIFF, PSD, BMP und
        ''' Beistelldateien vorab). Darueber gehen sie ungepackt hinaus: ein Ordner voller kleiner
        ''' TIFF haette sonst Gigabytes im Speicher gehalten, bevor das erste Byte hinausgeht.</summary>
        Private Const FolderPackBudgetBytes As Long = 64L * 1024 * 1024

        ''' <summary>Darf der Ordner heruntergeladen werden? Es muss ihn geben, und
        ''' FileOperationPolicy.CanDownload laesst ihn zu: nicht versteckt, nicht aus dem System,
        ''' mit festen Fotoordnern nur in ihnen, die Wurzel selbst eingeschlossen.</summary>
        Public Shared Function CanDownloadFolder(folder As String) As Boolean
            Return Not String.IsNullOrEmpty(folder) AndAlso Directory.Exists(folder) AndAlso FileOperationPolicy.CanDownload(folder)
        End Function

        ''' <summary>Der Name des Ordners, wie er dem Nutzer und im ZIP erscheint. Ein Ordner ohne
        ''' eigenen Namen (die Wurzel eines Laufwerks) heisst "FerrumPix".</summary>
        Friend Shared Function FolderDisplayName(folder As String) As String
            Dim name = ""
            Try
                name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))
            Catch ex As ArgumentException
            End Try
            Return If(String.IsNullOrWhiteSpace(name), "FerrumPix", name)
        End Function

        ''' <summary>Bietet den Ordner als ZIP an. Liefert die Kennung des Angebots oder Nothing
        ''' (kein Browser verbunden, Ordner fehlt oder ist nicht erlaubt). Ob etwas darin liegt,
        ''' prueft der Aufrufer vorher (<see cref="SurveyDownloadFolder"/>).</summary>
        Public Shared Function OfferFolderDownload(folder As String, Optional startNow As Boolean = True) As String
            If _listener Is Nothing OrElse Not CanDownloadFolder(folder) Then Return Nothing
            Return RegisterOffer(New List(Of String)(), startNow, Nothing, folder)
        End Function

        ''' <summary>Zaehlt, was ein Download des Ordners mitnaehme, aber nur bis eine der Grenzen
        ''' ueberschritten ist; dann endet die Zaehlung mit Exceeded. Fuer einen Hintergrundfaden:
        ''' sie liest Verzeichnisse.</summary>
        Public Shared Function SurveyDownloadFolder(folder As String, maxFiles As Integer, maxBytes As Long) As (Files As Integer, Bytes As Long, Exceeded As Boolean)
            Dim files = 0
            Dim bytes = 0L
            For Each entry In EnumerateFolderDownloadFiles(folder)
                files += 1
                bytes += entry.File.Length
                If files > maxFiles OrElse bytes > maxBytes Then Return (files, bytes, True)
            Next
            Return (files, bytes, False)
        End Function

        ''' <summary>Die Eintraege des Ordner-ZIP, jede Datei unter "Ordnername/relativer/Pfad".
        ''' Eine Datei, die sich jetzt nicht lesen laesst, faellt weg, statt den ganzen Download
        ''' abzubrechen.</summary>
        Friend Shared Function PlanFolderDownloadEntries(folder As String) As List(Of ZipEntryPlan)
            Dim result As New List(Of ZipEntryPlan)()
            Dim prefix = FolderDisplayName(folder) & "/"
            Dim packed = 0L
            For Each entry In EnumerateFolderDownloadFiles(folder)
                Try
                    Dim plan = StreamingZipWriter.PlanFile(entry.File.FullName, prefix & entry.RelativePath,
                                                           allowPacking:=packed < FolderPackBudgetBytes)
                    If plan.Packed IsNot Nothing Then packed += plan.Packed.LongLength
                    result.Add(plan)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    ' Inzwischen weg oder gesperrt: ohne sie weiter.
                End Try
            Next
            Return result
        End Function

        ''' <summary>Alle Dateien, die ein Download des Ordners mitnimmt, mit ihrem Pfad relativ zum
        ''' Ordner (Trenner "/"). Erst die Dateien eines Ordners, dann seine Unterordner, beides
        ''' nach Namen geordnet. Was sich nicht lesen laesst, wird uebergangen.</summary>
        Friend Shared Iterator Function EnumerateFolderDownloadFiles(folder As String) As IEnumerable(Of (File As FileInfo, RelativePath As String))
            If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then Return
            Dim pending As New Stack(Of (Folder As DirectoryInfo, RelativePath As String))()
            pending.Push((New DirectoryInfo(folder), ""))
            While pending.Count > 0
                Dim current = pending.Pop()
                Dim listing = ListDownloadFolder(current.Folder)
                For Each found In listing.Files
                    Yield (found, current.RelativePath & found.Name)
                Next
                ' Rueckwaerts auf den Stapel, damit der erste Unterordner zuerst drankommt.
                For index = listing.Folders.Count - 1 To 0 Step -1
                    Dim child = listing.Folders(index)
                    pending.Push((child, current.RelativePath & child.Name & "/"))
                Next
            End While
        End Function

        ''' <summary>Die Dateien und Unterordner eines Ordners, die mitgehen: ohne versteckte, ohne
        ''' Systemdateien, ohne Verweise. Ein Ordner ohne Leserecht ergibt nichts.</summary>
        Private Shared Function ListDownloadFolder(folder As DirectoryInfo) As (Files As List(Of FileInfo), Folders As List(Of DirectoryInfo))
            Dim files As New List(Of FileInfo)()
            Dim folders As New List(Of DirectoryInfo)()
            Try
                For Each entry In folder.EnumerateFileSystemInfos()
                    If Not IsDownloadableEntry(entry) Then Continue For
                    Dim asFile = TryCast(entry, FileInfo)
                    If asFile IsNot Nothing Then
                        files.Add(asFile)
                    Else
                        Dim asFolder = TryCast(entry, DirectoryInfo)
                        If asFolder IsNot Nothing Then folders.Add(asFolder)
                    End If
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                       TypeOf ex Is System.Security.SecurityException
                ' Kein Leserecht oder inzwischen weg: dieser Ordner bleibt leer.
            End Try
            files.Sort(Function(a, b) String.CompareOrdinal(a.Name, b.Name))
            folders.Sort(Function(a, b) String.CompareOrdinal(a.Name, b.Name))
            Return (files, folders)
        End Function

        ''' <summary>Geht der Eintrag mit? Nicht, wenn sein Name mit einem Punkt beginnt, er als
        ''' versteckt oder Systemdatei markiert ist oder ein symbolischer Verweis ist.</summary>
        Private Shared Function IsDownloadableEntry(entry As FileSystemInfo) As Boolean
            If entry Is Nothing OrElse entry.Name.StartsWith(".", StringComparison.Ordinal) Then Return False
            Try
                Dim attributes = entry.Attributes
                If (attributes And (FileAttributes.Hidden Or FileAttributes.System Or FileAttributes.ReparsePoint)) <> 0 Then Return False
                Return entry.LinkTarget Is Nothing
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return False
            End Try
        End Function
    End Class
End Namespace
