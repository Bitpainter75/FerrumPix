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
    ' Unterordner. Ein Unterordner, den es gerade nur im Katalog gibt (Laufwerk nicht erreichbar,
    ' ausserhalb geloescht), gehoert dazu: seine Bilder kommen aus dem Katalog, nur zum Ansehen
    ' (ListDescendantEntries, BuildCatalogOnlyFolderItems).

    Partial Public Class GalleryViewModel

        ''' <summary>So lange sammelt der Lauf, bevor er einen Schub in die Ansicht gibt. Jeder
        ''' Schub sortiert die ganze Liste neu; je Unterordner einer waere bei hunderten davon
        ''' die eigentliche Last.</summary>
        Private Const SubfolderBatchMilliseconds As Long = 400

        Private _includeSubfolders As Boolean

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
                ' Auch der Schalter in der Fusszeile setzt hier; der Einstellungsdialog soll
                ' danach denselben Stand zeigen.
                _mainVm?.Settings?.SyncGalleryIncludeSubfolders(value)
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
            Return ListDescendantEntries(root, FolderState.Present, token).
                Where(Function(entry) entry.State = FolderState.Present).
                Select(Function(entry) entry.Path).ToList()
        End Function

        ''' <summary>Wie <see cref="ListDescendantFolders"/>, aber MIT den Ordnern, die nur noch der
        ''' Katalog kennt, jeder mit seinem Zustand: ein Unterordner auf einem nicht erreichbaren
        ''' Laufwerk und einer, den es nicht mehr gibt, stehen als Ordnerkachel da, also gehoeren
        ''' auch ihre Bilder in die Ansicht. Unter einem solchen Ordner geht es im Katalog weiter;
        ''' die Platte wird fuer ihn nicht gefragt, auch nicht nach einer Verknuepfung.
        '''
        ''' <paramref name="rootState"/> ist der Zustand des Ordners selbst: steht die Galerie in
        ''' einem Ordner aus dem Katalog, kommen alle Unterordner von dort. Mit
        ''' FolderListing.ShowCatalogOnlyFolders aus liefert FolderListing keine solchen Ordner,
        ''' und die Liste ist die der vorhandenen.</summary>
        Friend Shared Function ListDescendantEntries(root As String, rootState As FolderState,
                                                     token As CancellationToken) As List(Of FolderEntry)
            Dim result As New List(Of FolderEntry)()
            If String.IsNullOrEmpty(root) OrElse rootState = FolderState.Absent Then Return result
            Dim visited As New HashSet(Of String)(PathIdentity.Comparer) From {
                If(rootState = FolderState.Present, ResolveFolder(root), root)}
            Dim pending As New Stack(Of FolderEntry)()
            pending.Push(New FolderEntry(root, rootState))

            While pending.Count > 0
                token.ThrowIfCancellationRequested()
                Dim folder = pending.Pop()
                Dim children As List(Of FolderEntry)
                Try
                    children = FolderListing.ListSubfolders(folder.Path, folder.State, FolderNode.ShowHiddenFolders)
                Catch ex As UnauthorizedAccessException
                    Continue While
                Catch ex As IOException
                    Continue While
                End Try

                For Each child In children
                    If child.State = FolderState.Absent Then Continue For
                    If FileOperationPolicy.IsTrashFolder(child.Path) Then Continue For
                    If Not visited.Add(If(child.State = FolderState.Present, ResolveFolder(child.Path), child.Path)) Then Continue For
                    result.Add(child)
                    pending.Push(child)
                Next
            End While
            Return result
        End Function

        ''' <summary>Die Bilder eines Ordners, den nur noch der Katalog kennt, als Elemente aus dem
        ''' Katalog: jedes mit <see cref="ImageItem.IsOffline"/> (und bei einem Ordner, den es nicht
        ''' mehr gibt, mit <see cref="ImageItem.IsFolderMissing"/>), BEVOR etwas daran bindet.
        ''' <paramref name="known"/>: Pfade, die schon als Kachel dastehen und nicht noch einmal
        ''' gebaut werden. Laeuft im Hintergrund, die Platte wird nicht gefragt.</summary>
        Private Function BuildCatalogOnlyFolderItems(folder As String, missing As Boolean, thumbnailToken As CancellationToken,
                                                     Optional known As Func(Of String, Boolean) = Nothing) As List(Of ImageItem)
            Dim rows = LibraryService.Instance.GetImagesInFolder(folder, restoreSpelling:=False).
                Where(Function(meta) meta IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(meta.FilePath) AndAlso
                                     _imageExtensions.Contains(IO.Path.GetExtension(meta.FilePath).ToLowerInvariant()) AndAlso
                                     (known Is Nothing OrElse Not known(meta.FilePath)))
            Dim items = BuildCatalogItems(rows, thumbnailToken)
            For Each item In items
                item.IsOffline = True
                item.IsFolderMissing = missing
            Next
            Return items
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
        Private Sub StartSubfolderLoad(folderPath As String, thumbnailToken As CancellationToken,
                                       Optional folderState As FolderState = FolderState.Present)
            AppendSubfolderImagesAsync(folderPath, thumbnailToken, folderState).ContinueWith(
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
        Private Async Function AppendSubfolderImagesAsync(folderPath As String, thumbnailToken As CancellationToken,
                                                          Optional folderState As FolderState = FolderState.Present) As Task
            ' <folderState>: wie der Ordner selbst dasteht. Kommt er aus dem Katalog (Laufwerk nicht
            ' erreichbar, nicht mehr vorhanden), kommen auch alle Unterordner von dort.
            Dim adopted As New HashSet(Of ImageItem)()
            Dim needsMetaRefresh As New List(Of ImageItem)()
            Try
                Await Task.Run(
                    Async Function()
                        Dim batch As New List(Of ImageItem)()
                        Dim lastFlush = Environment.TickCount64
                        For Each entry In ListDescendantEntries(folderPath, folderState, thumbnailToken)
                            thumbnailToken.ThrowIfCancellationRequested()
                            Dim folder = entry.Path
                            If entry.State = FolderState.Present Then
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
                            Else
                                ' Ein Unterordner, den nur noch der Katalog kennt: seine Bilder
                                ' kommen von dort, wie in der Ansicht des Ordners selbst.
                                Dim fromCatalog = BuildCatalogOnlyFolderItems(folder, entry.State = FolderState.Missing, thumbnailToken)
                                If fromCatalog.Count = 0 Then Continue For
                                batch.AddRange(fromCatalog)
                            End If

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
            ' Steht der Ordner selbst aus dem Katalog da, nennt die Statuszeile die Zahl seiner
            ' Bilder; mit den Unterordnern sind es mehr geworden.
            If _isOfflineView Then
                Dim template = If(_isOfflineViewMissing,
                                  LocalizationService.T("Ordner nicht mehr vorhanden: {0} Bilder aus dem Katalog"),
                                  LocalizationService.T("Laufwerk nicht erreichbar: {0} Bilder aus dem Katalog"))
                StatusText = String.Format(Globalization.CultureInfo.CurrentCulture, template,
                                           _allItems.LongCount(Function(i) i IsNot Nothing AndAlso Not i.IsFolder))
            End If
            ' Vorschaubilder im Hintergrund nur fuer Dateien, die es gibt: ein Bild aus dem Katalog
            ' zeigt seine Kachel aus dem Zwischenspeicher, die Datei wird nie angefasst.
            Dim thumbnails = adopted.Where(Function(i) Not i.IsOffline).ToList()
            If thumbnails.Count = 0 Then Return
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
