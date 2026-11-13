Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia.Threading
Imports FerrumPix.Models
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ' Die Galerie offline: ein Ordner auf einem Laufwerk, das gerade nicht erreichbar ist
    ' (Wechseldatentraeger abgezogen, Netzfreigabe nicht eingehaengt), steht aus dem Katalog da.
    ' Die Kacheln kommen aus dem Zwischenspeicher, die Daten aus dem Katalog; die Datei selbst wird
    ' nie angefasst, und alles, was in sie oder neben sie schreiben wuerde, ist aus. Ob ein Pfad
    ' betroffen ist, entscheidet OfflineVolumes, und zwar VOR jedem Blick auf die Platte.
    Partial Public Class GalleryViewModel

        Private _isOfflineView As Boolean

        ''' <summary>Steht gerade ein Ordner offen, dessen Laufwerk nicht erreichbar ist? Dann ist
        ''' die Ansicht nur zum Ansehen: kein Betrachter, kein Editor, keine Dateioperation, keine
        ''' Aenderung an Bewertung, Etikett oder Stichwoertern. Die Leiste ueber den Kacheln sagt es.</summary>
        Public ReadOnly Property IsOfflineView As Boolean
            Get
                Return _isOfflineView
            End Get
        End Property

        ''' <summary>Der Satz in der Leiste ueber den Kacheln, solange <see cref="IsOfflineView"/>
        ''' gilt.</summary>
        Public ReadOnly Property OfflineViewText As String
            Get
                Return LocalizationService.T("Laufwerk nicht erreichbar. Angezeigt wird der Stand aus dem Katalog, nur zum Ansehen.")
            End Get
        End Property

        Private Sub SetOfflineView(value As Boolean)
            If _isOfflineView = value Then Return
            _isOfflineView = value
            Me.RaisePropertyChanged(NameOf(IsOfflineView))
            Me.RaisePropertyChanged(NameOf(OfflineViewText))
            ' Das Kontextmenue haengt am selben Zustand (ContextMenuBuilder, Parameter offlineView).
            RefreshContextActions()
        End Sub

        ''' <summary>Der kurze Grund, warum etwas nicht geht. EIN Text fuer Statuszeile,
        ''' Kontextmenue und Ordnerbaum, damit ueberall dasselbe steht.</summary>
        Public Shared ReadOnly Property DriveUnreachableText As String
            Get
                Return LocalizationService.T("Laufwerk nicht erreichbar")
            End Get
        End Property

        ''' <summary>True, wenn eines der Elemente auf einem nicht erreichbaren Laufwerk liegt. Dann
        ''' steht der Grund in der Statuszeile, und der Aufrufer bricht ab. Die eine Stelle, an der
        ''' jeder schreibende Weg der Galerie fragt - so steht die Regel einmal und nicht an jedem
        ''' Knopf.</summary>
        Private Function RefuseOffline(items As IEnumerable(Of ImageItem)) As Boolean
            If items Is Nothing Then Return False
            If Not items.Any(Function(i) i IsNot Nothing AndAlso i.IsOffline) Then Return False
            StatusText = DriveUnreachableText
            Return True
        End Function

        ''' <summary>Wie <see cref="RefuseOffline"/> fuer die Auswahl, und fuer die ganze Ansicht,
        ''' solange sie offline ist: auch ein Weg ohne Auswahl (Gesichter suchen ueber die Ansicht)
        ''' wuerde dort die Dateien lesen.</summary>
        Private Function RefuseOfflineSelection() As Boolean
            If _isOfflineView Then
                StatusText = DriveUnreachableText
                Return True
            End If
            Dim selected As New List(Of ImageItem)()
            If SelectedItems IsNot Nothing Then selected.AddRange(SelectedItems)
            If _selectedItem IsNot Nothing Then selected.Add(_selectedItem)
            Return RefuseOffline(selected)
        End Function

        ''' <summary>Fuer Suchlisten und Katalogsuche: gibt es die Datei, oder liegt sie auf einem
        ''' nicht erreichbaren Laufwerk? Im zweiten Fall bleibt der Treffer in der Liste (er traegt
        ''' dann IsOffline, siehe AddPrebuiltItemsToVirtualFolder), und die Platte wird nicht gefragt.
        ''' Vorher fiel ein solcher Treffer als "fehlt" heraus, und auf einer getrennten Freigabe
        ''' konnte schon das File.Exists je Treffer haengen. Der Stand der Einhaengeorte wird dabei
        ''' einmal je Einhaengeort gemerkt, nicht je Datei gelesen (OfflineVolumes).</summary>
        Private Shared Function ExistsOrUnreachable(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            If OfflineVolumes.IsUnreachable(path) Then Return True
            Return IO.File.Exists(path)
        End Function

        ''' <summary>Fuehrt den Ordnerbaum nach, sobald die Galerie weiss, ob ein Ordner erreichbar
        ''' ist. Kommt ein Laufwerk zurueck, liest der Knoten seine Unterordner wieder von der
        ''' Platte; faellt es weg, aus dem Katalog.</summary>
        Private Sub NoteFolderReachability(folderPath As String, offline As Boolean)
            Dim node = FindLoadedFolderNode(FolderTree, folderPath)
            If node Is Nothing OrElse node.IsOffline = offline Then Return
            node.IsOffline = offline
            node.ReloadChildren()
        End Sub

        ''' <summary>Zeigt einen Ordner allein aus dem Katalog. Der Gegenweg zu Sofortbestand und
        ''' Dateisystemlauf in LoadFolderImagesAsync, ohne den Lauf: kein Directory.Exists, kein
        ''' Beobachter, kein Nachlesen der Metadaten. Die Unterordner kommen aus dem Katalog.</summary>
        Private Async Function LoadOfflineFolderAsync(folderPath As String, thumbnailToken As CancellationToken) As Task
            SetupWatcher(Nothing)
            SetOfflineView(True)
            NoteFolderReachability(folderPath, offline:=True)
            StatusText = DriveUnreachableText

            Dim items As List(Of ImageItem)
            Try
                items = Await Task.Run(Function() BuildOfflineCatalogSnapshot(folderPath, thumbnailToken), thumbnailToken)
            Catch ex As OperationCanceledException
                Return
            Catch ex As Exception
                If Not IsCurrentFolderLoad(folderPath, thumbnailToken) Then Return
                DiagnosticLogService.LogException("Gallery.LoadOfflineFolder", ex)
                StatusText = LocalizationService.T("Fehler beim Laden")
                Return
            End Try
            If Not IsCurrentFolderLoad(folderPath, thumbnailToken) Then Return

            _allItems.Clear()
            _allItems.AddRange(items)
            FilterAndSort()
            Dim imageCount = items.LongCount(Function(i) i IsNot Nothing AndAlso Not i.IsFolder)
            StatusText = String.Format(Globalization.CultureInfo.CurrentCulture,
                                       LocalizationService.T("Laufwerk nicht erreichbar: {0} Bilder aus dem Katalog"), imageCount)
        End Function

        ''' <summary>Der Bestand eines Ordners offline: "..", die Unterordner aus dem Katalog und
        ''' die Katalogzeilen der Ebene, jedes Element mit <see cref="ImageItem.IsOffline"/>, BEVOR
        ''' etwas daran bindet. Laeuft im Hintergrund und beruehrt keine gebundene Sammlung.</summary>
        Private Function BuildOfflineCatalogSnapshot(folderPath As String, thumbnailToken As CancellationToken) As List(Of ImageItem)
            Dim items As New List(Of ImageItem)()
            If _showFolders Then
                If _showParentFolder Then
                    Dim parentPath = IO.Path.GetDirectoryName(folderPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
                    If Not String.IsNullOrEmpty(parentPath) AndAlso Not IsAncestorOrSelf(folderPath, parentPath) Then
                        AddOfflineParentEntry(items, parentPath)
                    End If
                End If
                For Each folder In LibraryService.Instance.GetCatalogSubfolders(folderPath).
                    Where(Function(d) FolderNode.ShowHiddenFolders OrElse Not IO.Path.GetFileName(d).StartsWith("."))
                    items.Add(ImageItem.FromFolder(folder, offline:=True))
                Next
            End If
            thumbnailToken.ThrowIfCancellationRequested()
            Dim rows = LibraryService.Instance.GetImagesInFolder(folderPath, restoreSpelling:=False).
                Where(Function(meta) meta IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(meta.FilePath) AndAlso
                                     _imageExtensions.Contains(IO.Path.GetExtension(meta.FilePath).ToLowerInvariant()))
            For Each item In BuildCatalogItems(rows, thumbnailToken)
                item.IsOffline = True
                items.Add(item)
            Next
            Return items
        End Function

        ''' <summary>Der Eintrag ".." offline. Liegt der Elternordner selbst wieder auf einem
        ''' erreichbaren Laufwerk (die Wurzel des Laufwerks etwa), ist er ein gewoehnlicher Ordner;
        ''' sonst traegt auch er <see cref="ImageItem.IsOffline"/> und wird nicht auf der Platte
        ''' nachgesehen.</summary>
        Private Shared Sub AddOfflineParentEntry(items As List(Of ImageItem), parentPath As String)
            Dim parentOffline = OfflineVolumes.IsFolderUnreachable(parentPath)
            If Not parentOffline AndAlso Not IO.Directory.Exists(parentPath) Then Return
            items.Add(ImageItem.CreateParentFolderEntry(parentPath, parentOffline))
        End Sub

        ''' <summary>Darf ein Ordner ueber den Ordnerbaum aus dem Katalog genommen werden? Nur, wenn
        ''' sein Laufwerk nicht erreichbar ist. Fuer einen erreichbaren Ordner gibt es die Wege in
        ''' den Einstellungen; hier geht es um die Reste eines Laufwerks, das nicht wiederkommt.</summary>
        Public Function CanRemoveFolderFromCatalog(folderPath As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(folderPath) AndAlso OfflineVolumes.IsFolderUnreachable(folderPath)
        End Function

        ''' <summary>"Aus dem Katalog entfernen" im Ordnerbaum: fragt mit Zahl und Pfad nach und nimmt
        ''' dann den Ordner samt Unterordnern und Kacheln aus dem Katalog (<see cref="RemoveFolderFromCatalogAsync"/>).
        ''' Die Dateien auf dem Laufwerk bleiben, wie sie sind; das Laufwerk ist ja gar nicht da.</summary>
        Public Async Function ConfirmRemoveFolderFromCatalogAsync(folderPath As String) As Task
            Try
                If Not CanRemoveFolderFromCatalog(folderPath) OrElse _mainVm Is Nothing Then Return
                Dim count = Await Task.Run(Function() LibraryService.Instance.CountCatalogRowsUnder(folderPath))
                ' Ein ueberwachter Ordner geht dabei auch aus der Liste der ueberwachten Ordner; das
                ' muss die Frage sagen, sonst wundert man sich, wo er im Baum geblieben ist.
                ' Zwei ganze Saetze statt eines angehaengten: so bleibt jeder fuer sich uebersetzbar.
                Dim template = If(IsWatchedFolder(folderPath),
                    LocalizationService.T("{0} Katalogeinträge unter {1} samt Unterordnern und Vorschaubildern aus dem Katalog entfernen? Der Ordner wird danach auch nicht mehr überwacht. Bewertungen, Etiketten, Stichwörter und Personen dieser Bilder gehen dabei verloren. Die Dateien auf dem Laufwerk bleiben unberührt."),
                    LocalizationService.T("{0} Katalogeinträge unter {1} samt Unterordnern und Vorschaubildern aus dem Katalog entfernen? Bewertungen, Etiketten, Stichwörter und Personen dieser Bilder gehen dabei verloren. Die Dateien auf dem Laufwerk bleiben unberührt."))
                Dim question = String.Format(Globalization.CultureInfo.CurrentCulture, template, count, folderPath)
                If Not Await _mainVm.ShowConfirmAsync(LocalizationService.T("Aus dem Katalog entfernen"), question,
                                                      LocalizationService.T("Entfernen"), LocalizationService.T("Abbrechen")) Then Return
                Dim result = Await RemoveFolderFromCatalogAsync(folderPath)
                If result.Rows < 0 Then
                    StatusText = LocalizationService.T("In einem anderen Fenster läuft gerade ein Hintergrundlauf. Bitte danach noch einmal versuchen.")
                    Return
                End If
                StatusText = String.Format(Globalization.CultureInfo.CurrentCulture,
                                           LocalizationService.T("{0} Katalogeinträge und {1} Vorschaubilder entfernt."), result.Rows, result.Tiles)
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.ConfirmRemoveFolderFromCatalog", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
            End Try
        End Function

        ''' <summary>Nimmt einen Ordner auf einem nicht erreichbaren Laufwerk aus dem Katalog: seine
        ''' Zeilen und die aller Unterordner (Aufnahmedaten, Bewertung, Stichwoerter, Gesichter,
        ''' KI-Stichwoerter) ueber DeleteFolderCatalogData, dazu die Kacheln aller dieser Ordner im
        ''' Zwischenspeicher. Dieselben Loeschwege wie "Datenbank bereinigen" und das Aufraeumen
        ''' einer Ordnerzeile in den Einstellungen, unter derselben Aufraeumklammer, die jeden
        ''' schreibenden Hintergrundlauf anhaelt.
        '''
        ''' Rows -1 heisst: nicht geloescht, ein Lauf in einem anderen Fenster haelt die Sperre.
        ''' Ohne Rueckfrage; die stellt <see cref="ConfirmRemoveFolderFromCatalogAsync"/>.</summary>
        Public Async Function RemoveFolderFromCatalogAsync(folderPath As String) As Task(Of (Rows As Integer, Tiles As Integer))
            If Not CanRemoveFolderFromCatalog(folderPath) Then Return (0, 0)
            Dim result = Await Task.Run(Function() RemoveFolderFromCatalogCore(folderPath))
            If result.Rows < 0 Then Return result

            ' Ist der Ordner selbst ein ueberwachter, geht er auch aus dieser Liste und damit als
            ' Wurzel aus dem Baum. Ein Unterordner einer ueberwachten Wurzel aendert an der Liste
            ' nichts; er verschwindet nur aus seinem Elternknoten.
            If IsWatchedFolder(folderPath) Then
                StopWatchingFolder(folderPath)
                SyncExtraFolderRoots()
            End If
            DropFolderFromTree(folderPath)
            ' Steht die Galerie gerade in diesem Ordner oder darunter, zeigt sie danach den leeren
            ' Stand, statt Kacheln, die es im Katalog nicht mehr gibt.
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) AndAlso
               PathIdentity.IsAncestorOrSelf(folderPath, _currentFolder) Then
                LoadCurrentFolder()
            End If
            _mainVm?.People?.RefreshPeople()
            Return result
        End Function

        ''' <summary>Steht genau dieser Ordner in der Liste der ueberwachten Ordner?</summary>
        Public Shared Function IsWatchedFolder(folderPath As String) As Boolean
            If String.IsNullOrWhiteSpace(folderPath) Then Return False
            Return AppSettingsService.Load().CatalogWatchFolders.Any(Function(f) PathIdentity.AreSame(f, folderPath))
        End Function

        ''' <summary>Nimmt einen Ordner aus der Liste der ueberwachten Ordner, ueber denselben Weg wie
        ''' die Einstellungen (SettingsViewModel.RemoveCatalogWatchFolder: Liste, Speichern, Knoepfe
        ''' von Index und Gesichtssuche, Ordnerliste), wenn die Einstellungen den Ordner in ihrer
        ''' Liste fuehren. Die gespeicherte Liste wird in jedem Fall fortgeschrieben, und zwar
        ''' zuerst: sie ist die Quelle, aus der Baum und Index lesen.</summary>
        Private Sub StopWatchingFolder(folderPath As String)
            AppSettingsService.Update(Sub(s)
                                          s.CatalogWatchFolders = s.CatalogWatchFolders.
                                              Where(Function(f) Not PathIdentity.AreSame(f, folderPath)).ToList()
                                      End Sub)
            Dim settings = _mainVm?.Settings
            If settings IsNot Nothing AndAlso settings.CatalogWatchFolders.Any(Function(f) PathIdentity.AreSame(f, folderPath)) Then
                settings.RemoveCatalogWatchFolder(folderPath)
            End If
        End Sub

        Private Shared Function RemoveFolderFromCatalogCore(folderPath As String) As (Rows As Integer, Tiles As Integer)
            Dim cleanupLock = LibraryService.TryBeginCleanup("Gallery.RemoveFolderFromCatalog")
            If cleanupLock Is Nothing Then Return (-1, 0)
            Using cleanupLock
                Dim rows = LibraryService.Instance.DeleteFolderCatalogData(folderPath)
                Dim tiles = ThumbnailCacheService.DeleteFolderCachesUnder(folderPath)
                Return (rows, tiles)
            End Using
        End Function

        ''' <summary>Nimmt den Knoten eines aus dem Katalog genommenen Ordners aus dem Baum. Eine
        ''' Wurzel bleibt (ihre Unterordner werden neu gelesen, also leer), jeder andere Knoten geht
        ''' aus seinem Elternknoten.</summary>
        Private Sub DropFolderFromTree(folderPath As String)
            Dim node = FindLoadedFolderNode(FolderTree, folderPath)
            If node Is Nothing Then Return
            If FolderTree.Contains(node) Then
                node.ReloadChildren()
                Return
            End If
            Dim parentPath = IO.Path.GetDirectoryName(folderPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
            Dim parent = If(String.IsNullOrEmpty(parentPath), Nothing, FindLoadedFolderNode(FolderTree, parentPath))
            If parent IsNot Nothing Then
                parent.ReloadChildren()
            End If
        End Sub

    End Class

End Namespace
