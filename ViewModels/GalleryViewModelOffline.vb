Imports System
Imports System.Collections.Concurrent
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
        Private _isOfflineViewMissing As Boolean

        ''' <summary>Steht gerade ein Ordner offen, dessen Laufwerk nicht erreichbar ist oder den es
        ''' nicht mehr gibt (<see cref="IsOfflineViewMissing"/>)? Dann ist die Ansicht nur zum
        ''' Ansehen: kein Betrachter, kein Editor, keine Dateioperation, keine Aenderung an
        ''' Bewertung, Etikett oder Stichwoertern. Die Leiste ueber den Kacheln sagt es.</summary>
        Public ReadOnly Property IsOfflineView As Boolean
            Get
                Return _isOfflineView
            End Get
        End Property

        ''' <summary>Die Ansicht aus dem Katalog gilt einem Ordner, den es nicht mehr gibt.</summary>
        Public ReadOnly Property IsOfflineViewMissing As Boolean
            Get
                Return _isOfflineView AndAlso _isOfflineViewMissing
            End Get
        End Property

        ''' <summary>Die Ansicht aus dem Katalog gilt einem nicht erreichbaren Laufwerk.</summary>
        Public ReadOnly Property IsOfflineViewDrive As Boolean
            Get
                Return _isOfflineView AndAlso Not _isOfflineViewMissing
            End Get
        End Property

        ''' <summary>Der Satz in der Leiste ueber den Kacheln, solange <see cref="IsOfflineView"/>
        ''' gilt.</summary>
        Public ReadOnly Property OfflineViewText As String
            Get
                Return FolderListing.ViewText(_isOfflineViewMissing)
            End Get
        End Property

        Private Sub SetOfflineView(value As Boolean, Optional missing As Boolean = False)
            If _isOfflineView = value AndAlso _isOfflineViewMissing = missing Then Return
            _isOfflineView = value
            _isOfflineViewMissing = value AndAlso missing
            For Each propertyName In {NameOf(IsOfflineView), NameOf(IsOfflineViewMissing), NameOf(IsOfflineViewDrive), NameOf(OfflineViewText)}
                Me.RaisePropertyChanged(propertyName)
            Next
            ' Das Kontextmenue haengt am selben Zustand (ContextMenuBuilder, Parameter offlineView).
            RefreshContextActions()
        End Sub

        ''' <summary>Der kurze Grund, warum etwas nicht geht. EIN Text fuer Statuszeile,
        ''' Kontextmenue und Ordnerbaum, damit ueberall dasselbe steht.</summary>
        Public Shared ReadOnly Property DriveUnreachableText As String
            Get
                Return FolderListing.ReasonText(folderMissing:=False)
            End Get
        End Property

        ''' <summary>Der Grund fuer Elemente aus dem Katalog: das Laufwerk, sobald eines davon auf
        ''' einem nicht erreichbaren liegt, sonst der Ordner, den es nicht mehr gibt.</summary>
        Public Shared Function OfflineReasonText(items As IEnumerable(Of ImageItem)) As String
            Dim offline = If(items, Enumerable.Empty(Of ImageItem)()).Where(Function(i) i IsNot Nothing AndAlso i.IsOffline).ToList()
            Return FolderListing.ReasonText(offline.Count > 0 AndAlso offline.All(Function(i) i.IsFolderMissing))
        End Function

        ''' <summary>True, wenn eines der Elemente nur aus dem Katalog stammt (Laufwerk nicht
        ''' erreichbar, Ordner nicht mehr vorhanden). Dann steht der Grund in der Statuszeile, und
        ''' der Aufrufer bricht ab. Die eine Stelle, an der jeder schreibende Weg der Galerie
        ''' fragt - so steht die Regel einmal und nicht an jedem Knopf.</summary>
        Private Function RefuseOffline(items As IEnumerable(Of ImageItem)) As Boolean
            If items Is Nothing Then Return False
            Dim list = items.ToList()
            If Not list.Any(Function(i) i IsNot Nothing AndAlso i.IsOffline) Then Return False
            StatusText = OfflineReasonText(list)
            Return True
        End Function

        ''' <summary>Wie <see cref="RefuseOffline"/> fuer die Auswahl, und fuer die ganze Ansicht,
        ''' solange sie offline ist: auch ein Weg ohne Auswahl (Gesichter suchen ueber die Ansicht)
        ''' wuerde dort die Dateien lesen.</summary>
        Private Function RefuseOfflineSelection() As Boolean
            If _isOfflineView Then
                StatusText = FolderListing.ReasonText(_isOfflineViewMissing)
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
        ''' einmal je Einhaengeort gemerkt, nicht je Datei gelesen (OfflineVolumes).
        '''
        ''' Dasselbe gilt fuer einen Treffer in einem Ordner, den es nicht mehr gibt (ausserhalb
        ''' geloescht, nicht im Papierkorb): er bleibt und wird als "nicht mehr vorhanden" vermerkt
        ''' (siehe <see cref="IsKnownInMissingFolder"/>). Eine einzeln geloeschte Datei in einem
        ''' vorhandenen Ordner faellt weiter heraus.
        '''
        ''' Mit FolderListing.ShowCatalogOnlyFolders aus faellt beides heraus wie eine fehlende
        ''' Datei, der Treffer auf dem fehlenden Laufwerk ohne die Platte zu fragen.</summary>
        Private Shared Function ExistsOrUnreachable(path As String) As Boolean
            Return CatalogFileState(path) <> FolderState.Absent
        End Function

        ''' <summary>Treffer, deren Ordner es nicht mehr gibt, so wie <see cref="CatalogFileState"/>
        ''' sie im Hintergrund festgestellt hat. Die Anzeige liest nur hier nach und fragt selbst
        ''' nicht die Platte; eine Datei, die wieder da ist, geht beim naechsten Nachsehen heraus.</summary>
        Private Shared ReadOnly _filesInMissingFolders As New ConcurrentDictionary(Of String, Byte)(PathIdentity.Comparer)

        ''' <summary>Wie eine Katalogzeile auf der Platte dasteht: Present (die Datei ist da),
        ''' Unreachable (Laufwerk fehlt, gefragt VOR der Platte), Missing (der Ordner ist ausserhalb
        ''' geloescht und nicht im Papierkorb) oder Absent (fehlt, oder die Ansicht aus dem Katalog
        ''' ist ausgeschaltet). Nur im Hintergrund rufen: fuer eine fehlende Datei fragt es auch nach
        ''' ihrem Ordner.</summary>
        Private Shared Function CatalogFileState(path As String) As FolderState
            If String.IsNullOrWhiteSpace(path) Then Return FolderState.Absent
            Dim showCatalog = FolderListing.ShowCatalogOnlyFolders
            If OfflineVolumes.IsUnreachable(path) Then Return If(showCatalog, FolderState.Unreachable, FolderState.Absent)
            Dim dummy As Byte
            If IO.File.Exists(path) Then
                _filesInMissingFolders.TryRemove(path, dummy)
                Return FolderState.Present
            End If
            If showCatalog AndAlso Not path.Contains("://", StringComparison.Ordinal) AndAlso FolderListing.IsInMissingFolder(path) AndAlso
               Not TrashedFolders.IsTrashed(IO.Path.GetDirectoryName(path)) Then
                _filesInMissingFolders(path) = 0
                Return FolderState.Missing
            End If
            _filesInMissingFolders.TryRemove(path, dummy)
            Return FolderState.Absent
        End Function

        ''' <summary>Liegt die Datei nach dem letzten Nachsehen in einem Ordner, den es nicht mehr
        ''' gibt? Ohne Plattenzugriff, fuer den Anzeigefaden.</summary>
        Private Shared Function IsKnownInMissingFolder(path As String) As Boolean
            Return FolderListing.ShowCatalogOnlyFolders AndAlso Not String.IsNullOrEmpty(path) AndAlso
                   _filesInMissingFolders.ContainsKey(path)
        End Function

        ''' <summary>Am Ende eines Suchlaufs: Treffer, die schon ohne Pruefung in der Liste stehen
        ''' (die gemerkten aus der ersten Stufe), bekommen ihren Vermerk nachtraeglich, sobald der
        ''' Hintergrund weiss, dass ihr Ordner fehlt.</summary>
        Private Sub MarkItemsInMissingFolders()
            For Each item In _allItems
                If item Is Nothing OrElse item.IsFolder OrElse item.IsRemoteAsset OrElse item.IsOffline Then Continue For
                If Not IsKnownInMissingFolder(item.FilePath) Then Continue For
                item.IsFolderMissing = True
                item.IsOffline = True
            Next
        End Sub

        ''' <summary>Die Einstellung "Ordner aus dem Katalog anzeigen" hat sich geaendert: Wurzeln
        ''' der ueberwachten Ordner, die geladenen Knoten des Baums und die Ansicht werden neu
        ''' gelesen, ohne Neustart. Steht gerade eine gemischte Liste da, gehen ausgeschaltet ihre
        ''' Elemente aus dem Katalog hinaus (wie eine fehlende Datei); eingeschaltet kommen sie erst
        ''' mit dem naechsten Suchlauf, die Liste wird dafuer nicht neu gesucht.</summary>
        Public Sub ApplyCatalogOnlyFolderSetting()
            OfflineVolumes.Invalidate()
            SyncExtraFolderRoots()
            For Each root In FolderTree.ToList()
                root?.RefreshSubtree()
            Next
            If _isVirtualFolder Then
                If Not FolderListing.ShowCatalogOnlyFolders Then
                    RemoveItemsFromVirtualFolder(_allItems.Where(Function(i) i IsNot Nothing AndAlso i.IsOffline).
                                                           Select(Function(i) i.FilePath).ToList())
                    FilterAndSort()
                End If
            Else
                LoadCurrentFolder()
            End If
        End Sub

        ''' <summary>Fuehrt den Ordnerbaum nach, sobald die Galerie weiss, wie ein Ordner dasteht.
        ''' Kommt ein Laufwerk zurueck, liest der Knoten seine Unterordner wieder von der Platte;
        ''' faellt es weg oder gibt es den Ordner nicht mehr, aus dem Katalog.</summary>
        Private Sub NoteFolderReachability(folderPath As String, state As FolderState)
            Dim node = FindLoadedFolderNode(FolderTree, folderPath)
            If state = FolderState.Absent Then state = FolderState.Present
            If node Is Nothing OrElse node.State = state Then Return
            node.State = state
            node.ReloadChildren()
        End Sub

        ''' <summary>Zeigt einen Ordner allein aus dem Katalog. Der Gegenweg zu Sofortbestand und
        ''' Dateisystemlauf in LoadFolderImagesAsync, ohne den Lauf: kein Directory.Exists, kein
        ''' Beobachter, kein Nachlesen der Metadaten. Die Unterordner kommen aus dem Katalog.
        ''' <paramref name="missing"/>: es gibt den Ordner nicht mehr, das Laufwerk ist da.</summary>
        Private Async Function LoadOfflineFolderAsync(folderPath As String, thumbnailToken As CancellationToken,
                                                      Optional missing As Boolean = False) As Task
            SetupWatcher(Nothing)
            SetOfflineView(True, missing)
            NoteFolderReachability(folderPath, If(missing, FolderState.Missing, FolderState.Unreachable))
            StatusText = FolderListing.ReasonText(missing)

            Dim items As List(Of ImageItem)
            Try
                items = Await Task.Run(Function() BuildOfflineCatalogSnapshot(folderPath, thumbnailToken, missing), thumbnailToken)
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
            Dim template = If(missing,
                              LocalizationService.T("Ordner nicht mehr vorhanden: {0} Bilder aus dem Katalog"),
                              LocalizationService.T("Laufwerk nicht erreichbar: {0} Bilder aus dem Katalog"))
            StatusText = String.Format(Globalization.CultureInfo.CurrentCulture, template, imageCount)
        End Function

        ''' <summary>Der Bestand eines Ordners offline: "..", die Unterordner aus dem Katalog und
        ''' die Katalogzeilen der Ebene, jedes Element mit <see cref="ImageItem.IsOffline"/> (und
        ''' bei einem Ordner, den es nicht mehr gibt, mit <see cref="ImageItem.IsFolderMissing"/>),
        ''' BEVOR etwas daran bindet. Laeuft im Hintergrund und beruehrt keine gebundene Sammlung.</summary>
        Private Function BuildOfflineCatalogSnapshot(folderPath As String, thumbnailToken As CancellationToken,
                                                     missing As Boolean) As List(Of ImageItem)
            Dim items As New List(Of ImageItem)()
            Dim state = If(missing, FolderState.Missing, FolderState.Unreachable)
            If _showFolders Then
                If _showParentFolder Then
                    Dim parentPath = IO.Path.GetDirectoryName(folderPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
                    If Not String.IsNullOrEmpty(parentPath) AndAlso Not IsAncestorOrSelf(folderPath, parentPath) Then
                        AddOfflineParentEntry(items, parentPath)
                    End If
                End If
                For Each folder In FolderListing.ListSubfolders(folderPath, state, FolderNode.ShowHiddenFolders)
                    items.Add(ImageItem.FromFolder(folder.Path, offline:=True, missing:=missing))
                Next
            End If
            thumbnailToken.ThrowIfCancellationRequested()
            Dim rows = LibraryService.Instance.GetImagesInFolder(folderPath, restoreSpelling:=False).
                Where(Function(meta) meta IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(meta.FilePath) AndAlso
                                     _imageExtensions.Contains(IO.Path.GetExtension(meta.FilePath).ToLowerInvariant()))
            For Each item In BuildCatalogItems(rows, thumbnailToken)
                item.IsOffline = True
                item.IsFolderMissing = missing
                items.Add(item)
            Next
            Return items
        End Function

        ''' <summary>Der Eintrag ".." offline. Liegt der Elternordner selbst wieder auf einem
        ''' erreichbaren Laufwerk (die Wurzel des Laufwerks etwa), ist er ein gewoehnlicher Ordner;
        ''' sonst traegt auch er <see cref="ImageItem.IsOffline"/> und wird nicht auf der Platte
        ''' nachgesehen. Gibt es ihn weder auf der Platte noch im Katalog, fehlt der Eintrag.</summary>
        Private Shared Sub AddOfflineParentEntry(items As List(Of ImageItem), parentPath As String)
            Dim parentState = FolderListing.StateOf(parentPath)
            If parentState = FolderState.Absent Then Return
            items.Add(ImageItem.CreateParentFolderEntry(parentPath, parentState <> FolderState.Present,
                                                        parentState = FolderState.Missing))
        End Sub

        ''' <summary>Darf ein Ordner ueber den Ordnerbaum aus dem Katalog genommen werden? Nur, wenn
        ''' sein Laufwerk nicht erreichbar ist oder es ihn nicht mehr gibt. Fuer einen vorhandenen
        ''' Ordner gibt es die Wege in den Einstellungen; hier geht es um die Reste eines Laufwerks,
        ''' das nicht wiederkommt, oder eines Ordners, der ausserhalb der Anwendung geloescht wurde.</summary>
        Public Function CanRemoveFolderFromCatalog(folderPath As String) As Boolean
            If String.IsNullOrWhiteSpace(folderPath) Then Return False
            Dim state = FolderListing.StateOf(folderPath)
            Return state = FolderState.Unreachable OrElse state = FolderState.Missing
        End Function

        ''' <summary>Nach dem ENDGUELTIGEN Loeschen eines Ordners aus der Anwendung heraus: seine
        ''' Katalogzeilen und Kacheln samt Unterordnern gehen mit, ueber denselben Weg wie "Aus dem
        ''' Katalog entfernen" (RemoveFolderFromCatalogCore, unter der Aufraeumklammer). Sonst
        ''' stuende der Ordner danach als "nicht mehr vorhanden" da. Nur, wenn er wirklich weg
        ''' ist. Ein Ordner im Papierkorb behaelt alles (siehe TrashedFolders): er kann
        ''' zurueckkommen. Laeuft im Hintergrund, beruehrt keine gebundene Sammlung.</summary>
        Friend Shared Function ForgetDeletedFolder(folderPath As String) As (Rows As Integer, Tiles As Integer)
            If String.IsNullOrWhiteSpace(folderPath) OrElse IO.Directory.Exists(folderPath) Then Return (0, 0)
            Dim result = RemoveFolderFromCatalogCore(folderPath)
            If result.Rows < 0 Then
                DiagnosticLogService.LogAlways("Gallery.ForgetDeletedFolder", "Katalog nicht bereinigt, ein Hintergrundlauf haelt die Sperre: " & folderPath)
            End If
            Return result
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
            ForgetRemovedFolderInView(folderPath)
            _mainVm?.People?.RefreshPeople()
            Return result
        End Function

        ''' <summary>Nach dem Entfernen aus dem Katalog verschwindet der Ordner sofort aus der
        ''' Ansicht, ohne Aktualisieren. Steht die Galerie in ihm oder darunter, geht sie in seinen
        ''' Elternordner (der Baum waehlt ihn mit); steht sie im Elternordner, geht nur die Kachel
        ''' weg; in einer gemischten Liste gehen seine Treffer. Der gemerkte Vermerk "Ordner nicht
        ''' mehr vorhanden" seiner Dateien geht mit: Zeilen hat er keine mehr, also gibt es nichts,
        ''' was ihn als offline oder fehlend zurueckbraechte. Die Staende von Laufwerken und
        ''' Papierkorb haengen nicht an Zeilen, und die Unterordner aus dem Katalog werden je
        ''' Einlesen neu abgefragt.</summary>
        Private Sub ForgetRemovedFolderInView(folderPath As String)
            For Each known In _filesInMissingFolders.Keys.Where(Function(p) PathIdentity.IsAncestorOrSelf(folderPath, p)).ToList()
                Dim dummy As Byte
                _filesInMissingFolders.TryRemove(known, dummy)
            Next
            Dim parentOfRemoved = IO.Path.GetDirectoryName(folderPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
            If _isVirtualFolder Then
                RemoveItemsFromVirtualFolder(_allItems.Where(Function(i) i IsNot Nothing AndAlso Not i.IsRemoteAsset AndAlso
                                                                         PathIdentity.IsAncestorOrSelf(folderPath, i.FilePath)).
                                                       Select(Function(i) i.FilePath).ToList())
                FilterAndSort()
                Return
            End If
            If String.IsNullOrEmpty(_currentFolder) Then Return
            If PathIdentity.IsAncestorOrSelf(folderPath, _currentFolder) Then
                If String.IsNullOrEmpty(parentOfRemoved) Then
                    LoadCurrentFolder()
                Else
                    SetInitialFolderNodeForPath(parentOfRemoved)
                    NavigateToFolder(parentOfRemoved)
                End If
            ElseIf Not String.IsNullOrEmpty(parentOfRemoved) AndAlso PathIdentity.AreSame(parentOfRemoved, _currentFolder) Then
                If _allItems.RemoveAll(Function(i) i IsNot Nothing AndAlso i.IsFolder AndAlso Not i.IsParentFolderEntry AndAlso
                                                   PathIdentity.AreSame(i.FilePath, folderPath)) > 0 Then
                    FilterAndSort()
                End If
            End If
        End Sub

        ''' <summary>"Aus dem Katalog entfernen" an einer oder mehreren Ordnerkacheln: EINE Rueckfrage
        ''' fuer alle, dann derselbe Weg wie im Ordnerbaum je Ordner (<see cref="RemoveFolderFromCatalogAsync"/>,
        ''' samt ueberwachter Wurzel). Ein einzelner Ordner fragt mit Pfad wie im Baum.</summary>
        Public Async Function ConfirmRemoveFoldersFromCatalogAsync(folderPaths As IEnumerable(Of String)) As Task
            Dim folders = If(folderPaths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrWhiteSpace(p)).Distinct(PathIdentity.Comparer).ToList()
            ' Ein Ordner unter einem anderen der Auswahl geht mit diesem; einzeln entfernt zaehlte
            ' er doppelt.
            folders = folders.Where(Function(p) Not folders.Any(Function(o) Not PathIdentity.AreSame(o, p) AndAlso PathIdentity.IsAncestorOrSelf(o, p))).ToList()
            If folders.Count = 0 Then Return
            If folders.Count = 1 Then
                Await ConfirmRemoveFolderFromCatalogAsync(folders(0))
                Return
            End If
            Try
                If _mainVm Is Nothing OrElse Not folders.All(Function(f) CanRemoveFolderFromCatalog(f)) Then Return
                Dim count = Await Task.Run(Function() folders.Sum(Function(f) LibraryService.Instance.CountCatalogRowsUnder(f)))
                Dim template = If(folders.Any(Function(f) IsWatchedFolder(f)),
                    LocalizationService.T("{0} Katalogeinträge in {1} Ordnern samt Unterordnern und Vorschaubildern aus dem Katalog entfernen? Überwachte Ordner darunter werden danach nicht mehr überwacht. Bewertungen, Etiketten, Stichwörter und Personen dieser Bilder gehen dabei verloren. Die Dateien auf dem Laufwerk bleiben unberührt."),
                    LocalizationService.T("{0} Katalogeinträge in {1} Ordnern samt Unterordnern und Vorschaubildern aus dem Katalog entfernen? Bewertungen, Etiketten, Stichwörter und Personen dieser Bilder gehen dabei verloren. Die Dateien auf dem Laufwerk bleiben unberührt."))
                Dim question = String.Format(Globalization.CultureInfo.CurrentCulture, template, count, folders.Count)
                If Not Await _mainVm.ShowConfirmAsync(LocalizationService.T("Aus dem Katalog entfernen"), question,
                                                      LocalizationService.T("Entfernen"), LocalizationService.T("Abbrechen")) Then Return
                Dim rows = 0, tiles = 0
                For Each folder In folders
                    Dim result = Await RemoveFolderFromCatalogAsync(folder)
                    If result.Rows < 0 Then
                        StatusText = LocalizationService.T("In einem anderen Fenster läuft gerade ein Hintergrundlauf. Bitte danach noch einmal versuchen.")
                        Return
                    End If
                    rows += result.Rows
                    tiles += result.Tiles
                Next
                StatusText = String.Format(Globalization.CultureInfo.CurrentCulture,
                                           LocalizationService.T("{0} Katalogeinträge und {1} Vorschaubilder entfernt."), rows, tiles)
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.ConfirmRemoveFoldersFromCatalog", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
            End Try
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
