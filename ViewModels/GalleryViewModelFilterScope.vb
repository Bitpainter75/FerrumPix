Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ' Der Umfang von Suche und Filter: der gewaehlte Ordner oder der ganze Katalog.
    '
    ' Ein Schalter fuer ALLES - Suchfeld, Filterknopf, Personen, Orte und Stichwoerter. Vorher
    ' galten zwei Umfaenge nebeneinander, ohne dass man es sah: Suchfeld und Filterknopf wirkten
    ' auf die sichtbare Liste, Personen, Orte und Stichwoerter immer auf den ganzen Katalog. Wer im
    ' Baum einen Ordner waehlte und dann ein Stichwort, bekam Treffer von ueberall, und die Zahl in
    ' der Liste zaehlte Zeilen, deren Datei laengst woanders lag (Nutzerbefund).
    '
    ' "Ordner" heisst genau das, was die Galerie dort zeigt: der Ordner selbst, und nur wenn die
    ' Unterordner einbezogen sind (GalleryViewModelSubfolders), auch sie.
    ' Der Schalter wirkt nur im lokalen Bestand. Immich, Nextcloud, Favoriten und gespeicherte
    ' Suchen haben keinen Ordner; dort ist er ausgegraut, und die Filter laufen wie bisher ueber
    ' die ganze Quelle.

    Partial Public Class GalleryViewModel

        ''' <summary>An: Suche und Filter laufen ueber den ganzen Katalog. Aus: nur ueber den
        ''' gewaehlten Ordner.</summary>
        Private _catalogFilterScope As Boolean

        ''' <summary>Der Suchtext, der gerade als Katalogsuche im Filterknoten steckt. Leer, solange
        ''' das Suchfeld nur die sichtbare Liste filtert. Steht hier etwas, filtert FilterAndSort
        ''' den Text NICHT noch einmal: die Katalogsuche kennt eigene Regeln (mehrere Woerter als
        ''' ODER, KI-Begriffe auch uebersetzt), und ein zweites Sieben ueber den Suchtext der Kachel
        ''' naehme ihre Treffer wieder heraus.</summary>
        Private _catalogTextQuery As String = ""

        ''' <summary>Steht waehrend der Rueckkehr aus einer Trefferliste in ihren Ausgangsordner.
        ''' Das ist kein Bereichswechsel, die allgemeine Filterleiste bleibt also stehen.</summary>
        Private _keepFilterBarOnReturn As Boolean

        ''' <summary>Die Dateien des Ordners, ueber den zuletzt gezaehlt wurde. Gebraucht, sobald
        ''' eine Trefferliste offen ist: dann zeigt die Galerie nicht mehr den Ordner, die Zahlen
        ''' sollen aber weiter seine sein.</summary>
        Private _scopePathsFolder As String = ""
        Private _scopePaths As HashSet(Of String)

        Public ReadOnly Property ToggleFilterScopeCommand As ICommand

        Public Property IsCatalogFilterScope As Boolean
            Get
                Return _catalogFilterScope
            End Get
            Set(value As Boolean)
                If _catalogFilterScope = value Then Return
                Me.RaiseAndSetIfChanged(_catalogFilterScope, value)
                Me.RaisePropertyChanged(NameOf(FilterScopeToolTip))
                Me.RaisePropertyChanged(NameOf(IsCatalogScopeFiltering))
                SaveFileBrowserSettings()
                ReapplyFilterScope()
            End Set
        End Property

        ''' <summary>Gibt es hier einen Ordner, auf den sich Suche und Filter beschraenken lassen?</summary>
        Public ReadOnly Property CanToggleFilterScope As Boolean
            Get
                If SelectedFavoriteNode IsNot Nothing AndAlso IsFavoritesTab Then Return False
                If Not String.Equals(ActiveFilterSource(), "Local", StringComparison.Ordinal) Then Return False
                Return Not String.IsNullOrEmpty(FilterScopeFolder())
            End Get
        End Property

        Public ReadOnly Property FilterScopeToolTip As String
            Get
                If Not CanToggleFilterScope Then
                    Return LocalizationService.T("Suche und Filter wirken hier auf die ganze Quelle")
                End If
                If _catalogFilterScope Then
                    Return LocalizationService.T("Suche und Filter wirken auf den ganzen Katalog. Klicken, um nur in diesem Ordner zu suchen")
                End If
                Return LocalizationService.T("Suche und Filter wirken auf diesen Ordner. Klicken, um im ganzen Katalog zu suchen")
            End Get
        End Property

        ''' <summary>Fuer den Akzent des Knopfs: der Katalog ist der Umfang UND es wird gerade
        ''' gesucht oder gefiltert. Der Katalog allein schraenkt nichts ein; leuchten soll der
        ''' Knopf erst, wenn Treffer von ausserhalb des Ordners kommen koennen.</summary>
        Public ReadOnly Property IsCatalogScopeFiltering As Boolean
            Get
                If Not UsesCatalogTextSearch() Then Return False
                Return HasAnyButtonFilter() OrElse HasActiveFilter OrElse
                       Not String.IsNullOrWhiteSpace(_searchText)
            End Get
        End Property

        ''' <summary>Fuer den Knopf: alles, was davon abhaengt, wo die Galerie gerade steht. Aus
        ''' FilterAndSort gerufen, das nach jedem Ortswechsel laeuft - eine eigene Meldung an jeder
        ''' Stelle, die navigiert, waere genau die Liste, in der eine fehlt.</summary>
        Private Sub RaiseFilterScopeState()
            Me.RaisePropertyChanged(NameOf(CanToggleFilterScope))
            Me.RaisePropertyChanged(NameOf(FilterScopeToolTip))
            Me.RaisePropertyChanged(NameOf(IsCatalogScopeFiltering))
            Me.RaisePropertyChanged(NameOf(CanIncludeSubfolders))
            Me.RaisePropertyChanged(NameOf(IncludeSubfoldersToolTip))
        End Sub

        ''' <summary>Ist eine Knopfauswahl oder eine Katalogsuche aus dem Suchfeld aktiv?</summary>
        Private Function HasAnyButtonFilter() As Boolean
            Return _activeTagFilters.Count > 0 OrElse _activePersonFilters.Count > 0 OrElse
                   _activePlaceFilters.Count > 0 OrElse _catalogTextQuery.Length > 0
        End Function

        ''' <summary>Der Ordner, auf den sich Suche und Filter beziehen: der offene, oder bei einer
        ''' offenen Trefferliste der, aus dem heraus sie entstanden ist. Leer, wenn es keinen gibt
        ''' (Serverquelle, gespeicherte Suche, Favoriten).</summary>
        Private Function FilterScopeFolder() As String
            If _isVirtualFolder Then
                If Not HasAnyButtonFilter() Then Return ""
                Return If(_folderBeforeTagFilter, "")
            End If
            Dim folder = If(_currentFolder, "")
            If IsVirtualFolderPath(folder) Then Return ""
            Return folder
        End Function

        ''' <summary>Gilt gerade der Ordner als Umfang?</summary>
        Private Function IsFolderFilterScope() As Boolean
            Return Not _catalogFilterScope AndAlso CanToggleFilterScope
        End Function

        ''' <summary>Laeuft der Text des Suchfelds als Katalogsuche statt als Filter der Liste?</summary>
        Private Function UsesCatalogTextSearch() As Boolean
            Return _catalogFilterScope AndAlso CanToggleFilterScope
        End Function

        ''' <summary>Den Ausgangspunkt einer Auswahl merken, damit "Auswahl aufheben" dorthin
        ''' zurueckkehrt.
        '''
        ''' NUR, solange noch keine Auswahl besteht. Jede der drei Sorten hat das vorher selbst
        ''' entschieden, je nach ihrer EIGENEN Liste: wer erst ein Stichwort und dann eine Person
        ''' waehlte, ueberschrieb damit den Ordner mit der Trefferliste des Stichworts, und das
        ''' Aufheben fuehrte zurueck in diese statt in den Ordner.</summary>
        Private Sub RememberFilterOrigin()
            If HasAnyButtonFilter() Then Return
            If _isVirtualFolder Then
                _nodeBeforeTagFilter = If(SelectedImmichNode, If(SelectedNextcloudNode, SelectedSearchNode))
                _folderBeforeTagFilter = ""
            Else
                _folderBeforeTagFilter = If(_currentFolder, "")
                _nodeBeforeTagFilter = Nothing
            End If
        End Sub

        ''' <summary>Nach dem Umschalten alles, was gerade gilt, im neuen Umfang noch einmal.</summary>
        Private Sub ReapplyFilterScope()
            Dim text = If(_searchText, "").Trim()
            Dim hasLists = _activeTagFilters.Count > 0 OrElse _activePersonFilters.Count > 0 OrElse
                           _activePlaceFilters.Count > 0
            Dim needsRun = hasLists OrElse _catalogTextQuery.Length > 0 OrElse
                           (text.Length > 0 AndAlso UsesCatalogTextSearch())
            If needsRun Then
                ApplyButtonFilters()
                ' Zurueck im Ordner nimmt die Navigation den Suchtext mit; er gehoert aber weiter
                ' dazu und filtert jetzt eben die Liste.
                If Not hasLists AndAlso text.Length > 0 AndAlso String.IsNullOrEmpty(_searchText) Then
                    SearchText = text
                End If
            End If
            RaiseFilterScopeState()
            RefreshFilterListsForCurrentSource()
        End Sub

        ''' <summary>Die Bilddateien des Ordners, ueber den die Filterlisten zaehlen sollen. Nothing
        ''' heisst: ueber den ganzen Katalog zaehlen.
        '''
        ''' Steht die Galerie im Ordner, kommen sie aus der Liste, die sie ohnehin zeigt - ohne einen
        ''' Gang auf die Platte. Ist eine Trefferliste offen, aus dem Merkposten, und nur wenn der
        ''' zu einem anderen Ordner gehoert, aus einem Blick in den Ordner.</summary>
        Private Function FolderScopePaths() As HashSet(Of String)
            If Not IsFolderFilterScope() Then Return Nothing
            Dim folder = FilterScopeFolder()
            If String.IsNullOrEmpty(folder) Then Return Nothing
            If Not _isVirtualFolder Then
                Dim fromView As New HashSet(Of String)(PathIdentity.Comparer)
                For Each item In _allItems
                    If item Is Nothing OrElse item.IsFolder OrElse String.IsNullOrEmpty(item.FilePath) Then Continue For
                    fromView.Add(item.FilePath)
                Next
                _scopePathsFolder = folder
                _scopePaths = fromView
                Return fromView
            End If
            Dim listed As New HashSet(Of String)(PathIdentity.Comparer)
            Try
                ' Ein Ordner, den es gerade nur im Katalog gibt: seine Bilder stehen dort, die
                ' Platte wird nicht gefragt. Sonst zaehlten die Listen null, waehrend die Suche
                ' darin Treffer findet. VOR dem Merkposten: der kann aus dem Augenblick stammen, in
                ' dem die Ansicht aus dem Katalog noch leer war (die Listen werden schon beim
                ' Navigieren gezaehlt), und eine Katalogabfrage kostet hier nur eine Abfrage.
                Dim state = FolderListing.StateOf(folder)
                If state = FolderState.Present AndAlso _scopePaths IsNot Nothing AndAlso
                   String.Equals(_scopePathsFolder, folder, PathIdentity.Comparison) Then
                    Return _scopePaths
                End If
                If state = FolderState.Unreachable OrElse state = FolderState.Missing Then
                    For Each meta In LibraryService.Instance.GetImagesInFolder(folder, restoreSpelling:=False)
                        If meta Is Nothing OrElse String.IsNullOrWhiteSpace(meta.FilePath) Then Continue For
                        If Not _imageExtensions.Contains(Path.GetExtension(meta.FilePath).ToLowerInvariant()) Then Continue For
                        listed.Add(meta.FilePath)
                    Next
                    _scopePathsFolder = folder
                    _scopePaths = listed
                    Return listed
                End If
                If state <> FolderState.Present Then Return listed
                ' Mit einbezogenen Unterordnern zaehlen auch deren Bilder, wie in der Ansicht.
                Dim counted As New List(Of String) From {folder}
                If _includeSubfolders Then counted.AddRange(ListDescendantFolders(folder, Threading.CancellationToken.None))
                For Each countedFolder In counted
                    For Each file In Directory.EnumerateFiles(countedFolder)
                        If IsTrashedLocalPath(file) Then Continue For
                        If Not _imageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) Then Continue For
                        listed.Add(file)
                    Next
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.FolderScopePaths", ex)
            End Try
            _scopePathsFolder = folder
            _scopePaths = listed
            Return listed
        End Function

    End Class

End Namespace
