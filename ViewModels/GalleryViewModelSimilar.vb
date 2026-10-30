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

    ' Die Ansicht "Aehnliche Bilder": die Galerie zeigt nur noch Gruppen aehnlicher Aufnahmen des
    ' Ordners, je Gruppe eine Kopfzeile, mit Schaerfebalken und der schaerfsten Aufnahme hervor-
    ' gehoben. Gebaut nach dem Muster der Serienansicht (schwebende Leiste, ESC und die
    ' Navigationsknoepfe fuehren zurueck), dargestellt ueber die Gruppenansicht.
    Partial Public Class GalleryViewModel

        ''' <summary>Pfad zu Gruppennummer (ab 0, in der Reihenfolge der ersten Aufnahme), solange die
        ''' Ansicht offen ist; sonst Nothing.</summary>
        Private _similarGroups As Dictionary(Of String, Integer)
        ''' <summary>Die Fingerabdruecke des Ordners; ein Wechsel der Stufe gruppiert nur neu.</summary>
        Private _similarHashes As Dictionary(Of String, ULong)
        Private _similarLevel As SimilarityLevel = SimilarityLevel.Balanced
        Private _similarGroupCount As Integer
        Private _viewModeBeforeSimilar As String
        ''' <summary>Steht waehrend des Umschaltens der Ansicht durch die Aehnlichkeit: die Gruppenansicht
        ''' wird dann nicht als gewaehlte Ansicht gespeichert.</summary>
        Private _switchingViewForSimilar As Boolean
        Private _similarRun As Integer

        Public ReadOnly Property IsSimilarView As Boolean
            Get
                Return _similarGroups IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property SimilarViewText As String
            Get
                Return String.Format(Globalization.CultureInfo.CurrentCulture,
                                     LocalizationService.T("{0} Gruppen ähnlicher Bilder"), _similarGroupCount)
            End Get
        End Property

        Public Property IsSimilarLevelStrict As Boolean
            Get
                Return _similarLevel = SimilarityLevel.Strict
            End Get
            Set(value As Boolean)
                If value Then SetSimilarLevel(SimilarityLevel.Strict)
            End Set
        End Property

        Public Property IsSimilarLevelBalanced As Boolean
            Get
                Return _similarLevel = SimilarityLevel.Balanced
            End Get
            Set(value As Boolean)
                If value Then SetSimilarLevel(SimilarityLevel.Balanced)
            End Set
        End Property

        Public Property IsSimilarLevelLoose As Boolean
            Get
                Return _similarLevel = SimilarityLevel.Loose
            End Get
            Set(value As Boolean)
                If value Then SetSimilarLevel(SimilarityLevel.Loose)
            End Set
        End Property

        Private _openSimilarViewCommand As ICommand
        Public ReadOnly Property OpenSimilarViewCommand As ICommand
            Get
                If _openSimilarViewCommand Is Nothing Then
                    _openSimilarViewCommand = ReactiveCommand.CreateFromTask(AddressOf OpenSimilarViewAsync)
                End If
                Return _openSimilarViewCommand
            End Get
        End Property

        Private _closeSimilarViewCommand As ICommand
        Public ReadOnly Property CloseSimilarViewCommand As ICommand
            Get
                If _closeSimilarViewCommand Is Nothing Then
                    _closeSimilarViewCommand = ReactiveCommand.Create(Sub() CloseSimilarView())
                End If
                Return _closeSimilarViewCommand
            End Get
        End Property

        ''' <summary>Rechnet die Fingerabdruecke der lokalen Bilder des Ordners (mit Warteanzeige,
        ''' abbrechbar) und oeffnet die Ansicht, wenn sich Gruppen finden.
        '''
        ''' Vom Paar aus RAW und JPEG geht nur die RAW ein: beide zeigen dasselbe Bild, und jedes Paar
        ''' stuende sonst als eigene Gruppe da.</summary>
        Public Async Function OpenSimilarViewAsync() As Task
            If IsSimilarView OrElse _mainVm Is Nothing Then Return
            If IsStackFocused Then CloseStackFocus()
            Dim candidates = SimilarCandidates()
            If candidates.Count < 2 Then
                StatusText = LocalizationService.T("Keine ähnlichen Bilder gefunden")
                Return
            End If

            Dim run = Interlocked.Increment(_similarRun)
            Dim token = _mainVm.BeginBusyOverlayCancellation()
            Dim hashes As New Dictionary(Of String, ULong)(PathIdentity.Comparer)
            Try
                _mainVm.BeginBusyOverlay(SimilarProgressText(0, candidates.Count))
                Dim paths = candidates.Select(Function(i) i.FilePath).ToList()
                Await Task.Run(
                    Sub()
                        For index = 0 To paths.Count - 1
                            If token.IsCancellationRequested Then Exit For
                            Dim hash = SimilarityService.Fingerprint(paths(index))
                            If hash.HasValue Then hashes(paths(index)) = hash.Value
                            If index Mod 8 = 0 Then
                                Dim done = index + 1
                                Dispatcher.UIThread.Post(Sub()
                                                             If Not token.IsCancellationRequested Then
                                                                 _mainVm.UpdateBusyOverlay(SimilarProgressText(done, paths.Count))
                                                             End If
                                                         End Sub)
                            End If
                        Next
                    End Sub)
            Finally
                _mainVm.EndBusyOverlay()
                _mainVm.EndBusyOverlayCancellation()
            End Try
            If token.IsCancellationRequested OrElse run <> _similarRun Then Return

            _similarHashes = hashes
            Dim groups = BuildSimilarGroups()
            If groups.Count = 0 Then
                StatusText = LocalizationService.T("Keine ähnlichen Bilder gefunden")
                Return
            End If

            _similarGroups = groups
            _viewModeBeforeSimilar = ViewMode
            If Not IsGroupView Then
                _switchingViewForSimilar = True
                Try
                    ViewMode = "Group"
                Finally
                    _switchingViewForSimilar = False
                End Try
            End If
            RaiseSimilarViewChanged()
            FilterAndSort()
            QueueSimilarSharpness()
        End Function

        ''' <summary>Zurueck in den Ordner, in der Ansicht, die vorher gewaehlt war.</summary>
        Public Sub CloseSimilarView()
            If Not IsSimilarView Then Return
            Dim selected = GetSelectedImageItems().FirstOrDefault()
            EndSimilarState()
            RestoreViewModeAfterSimilar()
            FilterAndSort()
            If selected IsNot Nothing AndAlso Items.Contains(selected) Then
                ReplaceSelection({selected})
                RaiseEvent RequestScrollToItem(Me, EventArgs.Empty)
            End If
        End Sub

        Private Sub EndSimilarState()
            Interlocked.Increment(_similarRun)
            For Each item In _allItems
                If item Is Nothing OrElse Not item.IsInSimilarView Then Continue For
                item.IsInSimilarView = False
                item.SharpnessShare = -1
                item.IsSharpestInStack = False
            Next
            _similarGroups = Nothing
            _similarHashes = Nothing
            RaiseSimilarViewChanged()
        End Sub

        Private Sub RestoreViewModeAfterSimilar()
            Dim previous = _viewModeBeforeSimilar
            _viewModeBeforeSimilar = Nothing
            If String.IsNullOrEmpty(previous) OrElse previous = ViewMode Then Return
            ViewMode = previous
        End Sub

        Private Sub SetSimilarLevel(level As SimilarityLevel)
            If _similarLevel = level Then Return
            _similarLevel = level
            Me.RaisePropertyChanged(NameOf(IsSimilarLevelStrict))
            Me.RaisePropertyChanged(NameOf(IsSimilarLevelBalanced))
            Me.RaisePropertyChanged(NameOf(IsSimilarLevelLoose))
            If Not IsSimilarView Then Return
            Dim groups = BuildSimilarGroups()
            For Each item In _allItems
                If item Is Nothing OrElse Not item.IsInSimilarView Then Continue For
                item.IsInSimilarView = False
                item.SharpnessShare = -1
                item.IsSharpestInStack = False
            Next
            _similarGroups = groups
            RaiseSimilarViewChanged()
            FilterAndSort()
            QueueSimilarSharpness()
        End Sub

        Private Shared Function SimilarProgressText(done As Integer, total As Integer) As String
            Return String.Format(LocalizationService.T("Ähnliche Bilder werden gesucht: {0} von {1}"), done, total)
        End Function

        ''' <summary>Die lokalen Bilder des Ordners, ohne die JPEG-Haelfte eines RAW+JPEG-Paars.</summary>
        Private Function SimilarCandidates() As List(Of ImageItem)
            Dim images = _allItems.Where(Function(i) i IsNot Nothing AndAlso i.IsImage AndAlso Not i.IsFolder AndAlso
                                                     Not i.IsRemoteAsset AndAlso Not String.IsNullOrEmpty(i.FilePath)).ToList()
            Dim rawStems As New HashSet(Of String)(PathIdentity.Comparer)
            For Each item In images
                If item.IsRawFile Then rawStems.Add(Path.Combine(If(Path.GetDirectoryName(item.FilePath), ""),
                                                                  Path.GetFileNameWithoutExtension(item.FilePath)))
            Next
            Return images.Where(Function(i) i.IsRawFile OrElse
                                    Not rawStems.Contains(Path.Combine(If(Path.GetDirectoryName(i.FilePath), ""),
                                                                       Path.GetFileNameWithoutExtension(i.FilePath)))).ToList()
        End Function

        ''' <summary>Gruppiert die Bilder, die noch im Ordner stehen, nach der gewaehlten Stufe. Die
        ''' Gruppen stehen in der Zeitfolge ihrer ersten Aufnahme, die Bilder darin ebenso.</summary>
        Private Function BuildSimilarGroups() As Dictionary(Of String, Integer)
            Dim result As New Dictionary(Of String, Integer)(PathIdentity.Comparer)
            _similarGroupCount = 0
            If _similarHashes Is Nothing Then Return result
            Dim present = SimilarCandidates().Where(Function(i) _similarHashes.ContainsKey(i.FilePath)).
                OrderBy(Function(i) SimilarTimeOf(i)).ToList()
            Dim groups = SimilarityService.Cluster(present, Function(i) _similarHashes(i.FilePath), _similarLevel)
            Dim ordered = groups.OrderBy(Function(g) g.Min(Function(i) SimilarTimeOf(i))).ToList()
            For index = 0 To ordered.Count - 1
                For Each item In ordered(index)
                    result(item.FilePath) = index
                Next
            Next
            _similarGroupCount = ordered.Count
            Return result
        End Function

        Private Shared Function SimilarTimeOf(item As ImageItem) As DateTime
            If item.ExifDateTaken.HasValue AndAlso item.ExifDateTaken.Value <> DateTime.MinValue Then Return item.ExifDateTaken.Value
            Return item.DateModified
        End Function

        ''' <summary>In FilterAndSort: schraenkt auf die Bilder der Gruppen ein. Steht keine Gruppe mehr
        ''' im Bestand (Ordnerwechsel, alles geloescht), endet die Ansicht von selbst; die vorige
        ''' Ansicht kommt nach dem Neuaufbau zurueck, nicht mittendrin.</summary>
        Private Function ApplySimilarView(filtered As List(Of ImageItem)) As List(Of ImageItem)
            If _similarGroups Is Nothing Then Return filtered
            Dim remaining = _allItems.Where(Function(i) i IsNot Nothing AndAlso Not String.IsNullOrEmpty(i.FilePath) AndAlso
                                                        _similarGroups.ContainsKey(i.FilePath)).
                GroupBy(Function(i) _similarGroups(i.FilePath)).Any(Function(g) g.Count() >= 2)
            If Not remaining Then
                EndSimilarState()
                Dispatcher.UIThread.Post(Sub()
                                             RestoreViewModeAfterSimilar()
                                             FilterAndSort()
                                         End Sub)
                Return filtered
            End If
            Dim result = filtered.Where(Function(i) i.IsImage AndAlso Not String.IsNullOrEmpty(i.FilePath) AndAlso
                                                    _similarGroups.ContainsKey(i.FilePath)).ToList()
            For Each item In result
                item.IsInSimilarView = True
            Next
            Return result
        End Function

        ''' <summary>Ordnet die sortierte Liste nach Gruppen; innerhalb einer Gruppe bleibt die
        ''' Zeitfolge. Ausserhalb der Ansicht unveraendert.</summary>
        Private Function SimilarOrder(sorted As IEnumerable(Of ImageItem)) As IEnumerable(Of ImageItem)
            If _similarGroups Is Nothing Then Return sorted
            Return sorted.OrderBy(Function(i) _similarGroups(i.FilePath)).ThenBy(Function(i) SimilarTimeOf(i)).ToList()
        End Function

        Private Function SimilarGroupKey(item As ImageItem) As String
            Dim index As Integer
            If item Is Nothing OrElse Not _similarGroups.TryGetValue(If(item.FilePath, ""), index) Then Return "sim:?"
            Return "sim:" & index.ToString(Globalization.CultureInfo.InvariantCulture)
        End Function

        Private Function SimilarGroupTitle(item As ImageItem) As String
            Dim index As Integer
            If item Is Nothing OrElse Not _similarGroups.TryGetValue(If(item.FilePath, ""), index) Then Return ""
            Return String.Format(Globalization.CultureInfo.CurrentCulture, LocalizationService.T("Gruppe {0}"), index + 1)
        End Function

        ''' <summary>Misst die Schaerfe der Gruppenmitglieder im Hintergrund (SharpnessService, mit
        ''' Zwischenspeicher) und setzt je Gruppe die Balken, sobald sie ganz gemessen ist.</summary>
        Private Sub QueueSimilarSharpness()
            If _similarGroups Is Nothing Then Return
            Dim run = _similarRun
            Dim members = _allItems.Where(Function(i) i IsNot Nothing AndAlso Not String.IsNullOrEmpty(i.FilePath) AndAlso
                                                      _similarGroups.ContainsKey(i.FilePath)).ToList()
            Dim groups = _similarGroups
            Dim ignored = Task.Run(
                Sub()
                    For Each item In members
                        If run <> _similarRun Then Return
                        If item.Sharpness.HasValue Then Continue For
                        Dim score = SharpnessService.Measure(item.FilePath)
                        Dim current = item
                        Dispatcher.UIThread.Post(Sub() current.Sharpness = If(score, 0.0))
                    Next
                    Dispatcher.UIThread.Post(
                        Sub()
                            If run <> _similarRun Then Return
                            For Each group In members.GroupBy(Function(i) groups(i.FilePath))
                                Dim measured = group.Where(Function(i) i.Sharpness.HasValue).ToList()
                                If measured.Count < 2 Then Continue For
                                Dim best = measured.Max(Function(i) i.Sharpness.Value)
                                Dim sharpest = measured.First(Function(i) i.Sharpness.Value = best)
                                For Each item In measured
                                    item.SharpnessShare = If(best > 0, item.Sharpness.Value / best, 1.0)
                                    item.IsSharpestInStack = item Is sharpest
                                Next
                            Next
                        End Sub)
                End Sub)
        End Sub

        Private Sub RaiseSimilarViewChanged()
            Me.RaisePropertyChanged(NameOf(IsSimilarView))
            Me.RaisePropertyChanged(NameOf(SimilarViewText))
            Me.RaisePropertyChanged(NameOf(CanNavigateBack))
            Me.RaisePropertyChanged(NameOf(CanNavigateForward))
            Me.RaisePropertyChanged(NameOf(HasBreadcrumbParent))
            Me.RaisePropertyChanged(NameOf(CanNavigateParent))
        End Sub

    End Class

End Namespace
