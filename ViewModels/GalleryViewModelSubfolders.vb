Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Threading
Imports FerrumPix.Models
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ' Unterordner einbeziehen: die Galerie zeigt zu einem Ordner auch die Bilder aus allem, was
    ' darunter liegt.
    '
    ' ES BLEIBT DER ORDNER. Die Ansicht wird nicht zu einer Trefferliste: Baum, Verlauf,
    ' Brotkrumen, Einfuegen und Aktualisieren gelten weiter dem gewaehlten Ordner, und die Kacheln
    ' seiner direkten Unterordner stehen weiter da. Dazu kommen nur die Bilder von weiter unten.
    ' Eine gespeicherte Suche ohne Bedingung konnte das schon, aber je Ordner eine, und wer viele
    ' verschachtelte Ordner hat, braucht viele davon.
    '
    ' DREI STELLEN muessen dieselben Ordner sehen, sonst faellt ein Bild beim naechsten Abgleich
    ' wieder heraus: das Laden (hier), der Abgleich nach Aenderungen (BuildFolderSyncResult) und
    ' der Beobachter (SetupWatcher). Alle drei fragen ListDescendantFolders.
    '
    ' Nur im lokalen Ordner. Trefferlisten, Immich, Nextcloud und Favoriten haben keine
    ' Unterordner; ein Ordner, den es gerade nur im Katalog gibt (Laufwerk nicht erreichbar),
    ' zeigt weiter nur seine eigene Ebene.

    Partial Public Class GalleryViewModel

        ''' <summary>So lange sammelt der Lauf, bevor er einen Schub in die Ansicht gibt. Jeder
        ''' Schub sortiert die ganze Liste neu; je Unterordner einer waere bei hunderten davon
        ''' die eigentliche Last.</summary>
        Private Const SubfolderBatchMilliseconds As Long = 400

        Private _includeSubfolders As Boolean

        Public ReadOnly Property ToggleIncludeSubfoldersCommand As ICommand

        Public Property IncludeSubfolders As Boolean
            Get
                Return _includeSubfolders
            End Get
            Set(value As Boolean)
                If _includeSubfolders = value Then Return
                Me.RaiseAndSetIfChanged(_includeSubfolders, value)
                Me.RaisePropertyChanged(NameOf(IncludeSubfoldersToolTip))
                SaveFileBrowserSettings()
                ' Der Merkposten der Filterlisten gehoert zum alten Umfang.
                _scopePaths = Nothing
                _scopePathsFolder = ""
                ' Neu laden und nicht nur anhaengen oder wegnehmen: der Beobachter muss mit
                ' umgestellt werden, und der Weg ist derselbe wie beim Betreten des Ordners.
                If CanIncludeSubfolders Then LoadCurrentFolder()
            End Set
        End Property

        ''' <summary>Steht die Galerie in einem Ordner auf der Platte? Nur dort gibt es
        ''' Unterordner, die sich einbeziehen lassen.</summary>
        Public ReadOnly Property CanIncludeSubfolders As Boolean
            Get
                If _isVirtualFolder Then Return False
                Dim folder = If(_currentFolder, "")
                Return folder.Length > 0 AndAlso Not IsVirtualFolderPath(folder)
            End Get
        End Property

        Public ReadOnly Property IncludeSubfoldersToolTip As String
            Get
                If Not CanIncludeSubfolders Then
                    Return LocalizationService.T("Unterordner lassen sich nur in einem Ordner einbeziehen")
                End If
                If _includeSubfolders Then
                    Return LocalizationService.T("Die Galerie zeigt auch die Bilder aus allen Unterordnern. Klicken, um nur die Bilder dieses Ordners zu zeigen")
                End If
                Return LocalizationService.T("Die Galerie zeigt nur die Bilder dieses Ordners. Klicken, um auch die Bilder aus allen Unterordnern zu zeigen")
            End Get
        End Property

        ''' <summary>Alle Ordner unterhalb von <paramref name="root"/>, ohne ihn selbst.
        '''
        ''' Gelistet wird wie fuer die Ordnerkacheln (<see cref="FolderListing.ListSubfolders"/>):
        ''' versteckte Ordner nur, wenn sie angezeigt werden. Ordner, die es nur im Katalog gibt,
        ''' und der Papierkorb bleiben draussen. Ein nicht lesbarer Ordner fehlt, der Lauf geht
        ''' weiter.
        '''
        ''' <para>EIN ORDNER NUR EINMAL, gemessen an seinem wirklichen Ort. Eine Verknuepfung, die
        ''' auf einen Ordner weiter oben zeigt, liesse den Lauf sonst im Kreis laufen.</para></summary>
        Friend Shared Function ListDescendantFolders(root As String, token As CancellationToken) As List(Of String)
            Dim result As New List(Of String)()
            If String.IsNullOrEmpty(root) Then Return result
            Dim visited As New HashSet(Of String)(PathIdentity.Comparer) From {ResolveFolder(root)}
            Dim pending As New Stack(Of String)()
            pending.Push(root)

            While pending.Count > 0
                token.ThrowIfCancellationRequested()
                Dim folder = pending.Pop()
                Dim children As List(Of FolderEntry)
                Try
                    children = FolderListing.ListSubfolders(folder, FolderState.Present, FolderNode.ShowHiddenFolders)
                Catch ex As UnauthorizedAccessException
                    Continue While
                Catch ex As IOException
                    Continue While
                End Try

                For Each child In children
                    If child.State <> FolderState.Present Then Continue For
                    If FileOperationPolicy.IsTrashFolder(child.Path) Then Continue For
                    If Not visited.Add(ResolveFolder(child.Path)) Then Continue For
                    result.Add(child.Path)
                    pending.Push(child.Path)
                Next
            End While
            Return result
        End Function

        ''' <summary>Der wirkliche Ort eines Ordners: bei einer Verknuepfung ihr Ziel, sonst er selbst.</summary>
        Private Shared Function ResolveFolder(folderPath As String) As String
            Try
                Dim info As New DirectoryInfo(folderPath)
                If info.LinkTarget Is Nothing Then Return info.FullName
                Dim target = info.ResolveLinkTarget(returnFinalTarget:=True)
                Return If(target?.FullName, info.FullName)
            Catch ex As IOException
                Return folderPath
            Catch ex As UnauthorizedAccessException
                Return folderPath
            End Try
        End Function

        ''' <summary>Startet das Nachladen der Unterordner und kehrt zurueck. Der Ordnerwechsel
        ''' selbst wartet nicht darauf: wer navigiert, soll den Ordner sofort bedienen koennen.</summary>
        Private Sub StartSubfolderLoad(folderPath As String, thumbnailToken As CancellationToken)
            AppendSubfolderImagesAsync(folderPath, thumbnailToken).ContinueWith(
                Sub(run) DiagnosticLogService.LogException("Gallery.Subfolders", run.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default)
        End Sub

        ''' <summary>Liest die Bilder aller Unterordner und haengt sie in Schueben an die Ansicht.
        '''
        ''' Aufzaehlen, Katalogabfrage und der Aufbau der Kacheln laufen im Hintergrund, je Ordner
        ''' ueber denselben Weg wie der Ordner selbst (<see cref="BuildFileItems"/>). Auf dem
        ''' Anzeigefaden liegt nur das Uebernehmen eines fertigen Schubs.
        '''
        ''' <para>Der Abbruch-Token ist der des Ordnerwechsels. Ein neuer Ordner, eine Trefferliste
        ''' oder das Ausschalten beenden den Lauf, und ein Schub, der danach noch ankommt, wird
        ''' verworfen.</para></summary>
        Private Async Function AppendSubfolderImagesAsync(folderPath As String, thumbnailToken As CancellationToken) As Task
            Dim adopted As New HashSet(Of ImageItem)()
            Dim needsMetaRefresh As New List(Of ImageItem)()
            Try
                Await Task.Run(
                    Async Function()
                        Dim batch As New List(Of ImageItem)()
                        Dim lastFlush = Environment.TickCount64
                        For Each folder In ListDescendantFolders(folderPath, thumbnailToken)
                            thumbnailToken.ThrowIfCancellationRequested()
                            Dim files As FileInfo()
                            Try
                                files = EnumerateImageFiles(folder)
                            Catch ex As UnauthorizedAccessException
                                Continue For
                            Catch ex As IOException
                                Continue For
                            End Try
                            If files.Length = 0 Then Continue For
                            batch.AddRange(BuildFileItems(files, folder, thumbnailToken, needsMetaRefresh))

                            If Environment.TickCount64 - lastFlush < SubfolderBatchMilliseconds Then Continue For
                            Dim ready = batch
                            batch = New List(Of ImageItem)()
                            Await Dispatcher.UIThread.InvokeAsync(Sub() AdoptSubfolderItems(folderPath, thumbnailToken, ready, adopted))
                            lastFlush = Environment.TickCount64
                        Next
                        If batch.Count > 0 Then
                            Await Dispatcher.UIThread.InvokeAsync(Sub() AdoptSubfolderItems(folderPath, thumbnailToken, batch, adopted))
                        End If
                    End Function, thumbnailToken)
            Catch ex As OperationCanceledException
                Return
            End Try

            If Not IsCurrentSubfolderLoad(folderPath, thumbnailToken) OrElse adopted.Count = 0 Then Return
            ' Nachlesen und Vorschaubilder einmal am Ende und nur fuer das, was wirklich in der
            ' Ansicht steht. Je Schub gestartet, liefen mehrere Metadatenlaeufe nebeneinander, und
            ' jeder sortierte am Ende die ganze Liste.
            Dim stale = needsMetaRefresh.Where(Function(i) i IsNot Nothing AndAlso adopted.Contains(i)).ToList()
            If stale.Count > 0 Then QueueBackgroundMetaRefresh(stale, thumbnailToken)
            Dim thumbnails = adopted.ToList()
            Dispatcher.UIThread.Post(Sub() ImageItem.QueueBackgroundThumbnails(thumbnails), DispatcherPriority.Background)
        End Function

        ''' <summary>Gehoert ein Ergebnis des Nachladens noch zur Ansicht? Derselbe Ordner, derselbe
        ''' Ladevorgang, und die Unterordner sind weiter einbezogen.</summary>
        Private Function IsCurrentSubfolderLoad(folderPath As String, thumbnailToken As CancellationToken) As Boolean
            Return _includeSubfolders AndAlso IsCurrentFolderLoad(folderPath, thumbnailToken)
        End Function

        ''' <summary>Uebernimmt einen Schub in die Ansicht. Laeuft auf dem Anzeigefaden.
        '''
        ''' WAS SCHON DA IST, BLEIBT. Zwischen zwei Schueben kann ein Abgleich gelaufen sein (der
        ''' Beobachter meldet eine Aenderung, eine Dateiaktion endet), und der hat die Unterordner
        ''' dann selbst schon gelesen. Ohne die Pruefung staende jedes dieser Bilder doppelt in der
        ''' Liste.</summary>
        Private Sub AdoptSubfolderItems(folderPath As String, thumbnailToken As CancellationToken,
                                        ready As List(Of ImageItem), adopted As HashSet(Of ImageItem))
            If Not IsCurrentSubfolderLoad(folderPath, thumbnailToken) Then Return
            Dim known As New HashSet(Of String)(PathIdentity.Comparer)
            For Each item In _allItems
                If item IsNot Nothing AndAlso Not String.IsNullOrEmpty(item.FilePath) Then known.Add(item.FilePath)
            Next
            Dim fresh = ready.Where(Function(i) i IsNot Nothing AndAlso Not String.IsNullOrEmpty(i.FilePath) AndAlso
                                                known.Add(i.FilePath)).ToList()
            If fresh.Count = 0 Then Return
            _allItems.AddRange(fresh)
            For Each item In fresh
                adopted.Add(item)
            Next
            FilterAndSort()
        End Sub

    End Class

End Namespace
