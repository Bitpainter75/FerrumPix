Imports System
Imports System.Linq
Imports System.Threading.Tasks
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Threading
Imports Avalonia.VisualTree
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace Controls

    ''' <summary>
    ''' Bündelt die Bedienlogik des Filmstrip-Steuerelements (Auto-Scroll zum aktuellen Bild,
    ''' virtualisiertes Thumbnail-Nachladen über einen ViewportThumbnailTracker, Mittelklick-
    ''' Schnellvorschau), die Viewer und Editor zuvor identisch dupliziert hatten. Jede Ansicht
    ''' besitzt weiterhin eine eigene Instanz und übergibt ihr eigenes owner-Control (für
    ''' FindControl-Lookups auf "FilmstripListBox"/"FilmstripPreviewOverlay"/"FilmstripPreviewImage")
    ''' sowie Zugriffsfunktionen auf die aktuellen Filmstrip-Items/den aktuellen Index, da sich deren
    ''' Quelle (ViewerViewModel vs. EditorViewModel) zwischen den Ansichten unterscheidet.
    ''' </summary>
    Public Class FilmstripInteractionController
        Private Const ItemWidth As Double = 148.0
        ''' <summary>Hoehe einer Kachel samt Aussenrand, wenn der Streifen senkrecht steht (links
        ''' oder rechts): 100 Kachel und je 5 oben und unten.</summary>
        Private Const ItemHeight As Double = 110.0

        ''' <summary>Der Streifen steht links oder rechts und laeuft senkrecht. Dann gelten fuer die
        ''' Schaetzung des sichtbaren Bereichs Hoehe und senkrechter Versatz statt Breite und
        ''' waagerechtem Versatz.</summary>
        Public Property IsVertical As Boolean

        ''' <summary>Liefert die Einstellung "gewähltes Bild mittig halten". Ist sie an, rollt der
        ''' Streifen bei jeder Auswahl so, dass die gewählte Kachel in der Mitte steht, und trägt an
        ''' beiden Enden ein Polster, damit auch das erste und das letzte Bild dorthin kommen. Nicht
        ''' gesetzt heißt aus (die Kartenansicht der Galerie).</summary>
        Public Property KeepSelectionCentered As Func(Of Boolean)

        ''' <summary>Halbe Breite der schmalsten Kachel (Hochformat 1:2 bei 96 Bildhöhe samt
        ''' Rahmen). Das Polster an den Enden ist die halbe Sichtbreite weniger dieses Werts: so kommt
        ''' jede Kachel in die Mitte, eine breitere lässt nur etwas mehr Rollweg am Ende.</summary>
        Private Const NarrowestHalfTile As Double = 26.0

        ''' <summary>Abstand des Panels zur Rollleiste, wie FilmstripPlacement ihn setzt: unten bei
        ''' waagerechtem, rechts bei senkrechtem Streifen.</summary>
        Private Const PanelScrollBarGap As Double = 8.0

        Private _centerPaddingApplied As Boolean = False
        Private _centerQueued As Boolean = False

        Private ReadOnly _owner As Control
        Private ReadOnly _tracker As ViewportThumbnailTracker
        Private ReadOnly _getItems As Func(Of IReadOnlyList(Of ImageItem))
        Private ReadOnly _getCurrentIndex As Func(Of Integer)
        ''' <summary>Name der Liste im owner. Betrachter und Editor heissen "FilmstripListBox"; die
        ''' Kartenansicht der Galerie hat ihren eigenen Streifen neben den Kacheln.</summary>
        Private ReadOnly _listBoxName As String
        Private _filmstripScrollViewer As ScrollViewer
        Private _thumbnailRefreshQueued As Boolean = False

        Public Sub New(owner As Control, tracker As ViewportThumbnailTracker,
                       getItems As Func(Of IReadOnlyList(Of ImageItem)), getCurrentIndex As Func(Of Integer),
                       Optional listBoxName As String = "FilmstripListBox")
            _owner = owner
            _tracker = tracker
            _getItems = getItems
            _getCurrentIndex = getCurrentIndex
            _listBoxName = listBoxName
        End Sub

        ''' Beim DataContext-Wechsel (anderes Bild/anderer Ordner) aufzurufen, damit Sichtbereichs-
        ''' Zustand und der zwischengespeicherte ScrollViewer nicht über den Wechsel hinweg bestehen.
        Public Sub Reset()
            _tracker.Reset()
            _filmstripScrollViewer = Nothing
        End Sub

        ''' An das "FilmstripListBox"-Control binden, um Scroll-/Bounds-Änderungen für das
        ''' Thumbnail-Nachladen zu beobachten.
        Public Sub AttachTo(filmstrip As ListBox)
            If filmstrip Is Nothing Then Return
            AddHandler filmstrip.PropertyChanged, AddressOf OnFilmstripPropertyChanged
            AddHandler filmstrip.SelectionChanged, AddressOf OnFilmstripSelectionChanged
            filmstrip.AddHandler(ScrollViewer.ScrollChangedEvent, AddressOf OnFilmstripScrollChanged, RoutingStrategies.Bubble)
        End Sub

        Public Sub ScrollToCurrent()
            Dispatcher.UIThread.Post(Sub()
                                          Dim items = _getItems()
                                          Dim idx = _getCurrentIndex()
                                          Dim listBox = _owner.FindControl(Of ListBox)(_listBoxName)
                                          ' Auch ohne aktuelles Bild: ein Ausschalten der Einstellung
                                          ' muss das Polster wieder abnehmen.
                                          If listBox IsNot Nothing Then ApplyCenterPadding(listBox)
                                          If idx < 0 OrElse items Is Nothing OrElse idx >= items.Count Then Return
                                          If listBox Is Nothing Then Return
                                          listBox.SelectedIndex = idx
                                          listBox.ScrollIntoView(items(idx))
                                          If IsCentering() Then QueueCenterSelection()
                                          QueueThumbnailRefresh()
                                          Dispatcher.UIThread.Post(Sub() RefreshThumbnails(), DispatcherPriority.Background)
                                      End Sub, DispatcherPriority.Loaded)
        End Sub

        Private Function IsCentering() As Boolean
            Dim getter = KeepSelectionCentered
            Return getter IsNot Nothing AndAlso getter()
        End Function

        ''' <summary>Eine Auswahl ist auch die Pfeiltaste im Editor, die nur wählt und nicht öffnet,
        ''' und der Klick auf eine Kachel. In beiden Fällen rückt der Streifen sie in die Mitte.</summary>
        Private Sub OnFilmstripSelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            If IsCentering() Then QueueCenterSelection()
        End Sub

        ''' <summary>Das Polster an beiden Enden, als Rand des Panels: er liegt im Rollbereich und
        ''' verlängert ihn. Bei jedem Aufruf neu gesetzt, weil FilmstripPlacement das Panel beim
        ''' Wechsel der Ausrichtung neu baut. Die Liste rollt bei eingeschalteter Einstellung nicht
        ''' mehr selbst zur Auswahl; sie liefe sonst erst an den Rand und dann in die Mitte.
        ''' Wahr, wenn sich das Polster geändert hat: Lage und Ausdehnung stimmen dann erst nach
        ''' dem nächsten Layout.</summary>
        Private Function ApplyCenterPadding(listBox As ListBox) As Boolean
            Dim panel = TryCast(listBox.ItemsPanelRoot, Control)
            Dim centering = IsCentering()
            listBox.AutoScrollToSelectedItem = Not centering
            If panel Is Nothing Then Return False
            Dim wanted As Avalonia.Thickness
            If Not centering Then
                If Not _centerPaddingApplied Then Return False
                wanted = If(IsVertical, New Avalonia.Thickness(0, 0, PanelScrollBarGap, 0), New Avalonia.Thickness(0, 0, 0, PanelScrollBarGap))
                _centerPaddingApplied = False
            Else
                Dim sv = GetFilmstripScrollViewer()
                If sv Is Nothing Then Return False
                Dim visible = If(IsVertical, sv.Viewport.Height, sv.Viewport.Width)
                If visible <= 0 Then Return False
                Dim pad = Math.Max(0.0, visible / 2.0 - NarrowestHalfTile)
                wanted = If(IsVertical, New Avalonia.Thickness(0, pad, PanelScrollBarGap, pad), New Avalonia.Thickness(pad, 0, pad, PanelScrollBarGap))
                _centerPaddingApplied = True
            End If
            If panel.Margin = wanted Then Return False
            panel.Margin = wanted
            Return True
        End Function

        Private Sub QueueCenterSelection()
            If _centerQueued Then Return
            _centerQueued = True
            Dispatcher.UIThread.Post(Sub()
                                          _centerQueued = False
                                          CenterSelection(0)
                                      End Sub, DispatcherPriority.Loaded)
        End Sub

        ''' <summary>Rollt so, dass die Mitte der gewählten Kachel auf der Mitte der Sichtfläche
        ''' liegt. Ist die Kachel noch nicht gebaut (Sprung weit weg, die Liste virtualisiert), holt
        ''' ScrollIntoView sie erst heran, und der nächste Durchgang zentriert.</summary>
        Private Sub CenterSelection(attempt As Integer)
            If Not IsCentering() Then Return
            Dim listBox = _owner.FindControl(Of ListBox)(_listBoxName)
            If listBox Is Nothing Then Return
            Dim idx = listBox.SelectedIndex
            If idx < 0 Then Return
            If ApplyCenterPadding(listBox) AndAlso attempt < 3 Then
                Dispatcher.UIThread.Post(Sub() CenterSelection(attempt + 1), DispatcherPriority.Loaded)
                Return
            End If
            Dim sv = GetFilmstripScrollViewer()
            If sv Is Nothing Then Return
            Dim container = listBox.ContainerFromIndex(idx)
            If container Is Nothing Then
                If attempt >= 3 Then Return
                listBox.ScrollIntoView(idx)
                Dispatcher.UIThread.Post(Sub() CenterSelection(attempt + 1), DispatcherPriority.Loaded)
                Return
            End If
            Dim topLeft = Avalonia.VisualExtensions.TranslatePoint(container, New Avalonia.Point(0, 0), sv)
            If Not topLeft.HasValue Then Return
            If IsVertical Then
                Dim target = sv.Offset.Y + topLeft.Value.Y + container.Bounds.Height / 2.0 - sv.Viewport.Height / 2.0
                Dim maxOffset = Math.Max(0.0, sv.Extent.Height - sv.Viewport.Height)
                sv.Offset = New Avalonia.Vector(sv.Offset.X, Math.Clamp(target, 0.0, maxOffset))
            Else
                Dim target = sv.Offset.X + topLeft.Value.X + container.Bounds.Width / 2.0 - sv.Viewport.Width / 2.0
                Dim maxOffset = Math.Max(0.0, sv.Extent.Width - sv.Viewport.Width)
                sv.Offset = New Avalonia.Vector(Math.Clamp(target, 0.0, maxOffset), sv.Offset.Y)
            End If
        End Sub

        Private Sub OnFilmstripScrollChanged(sender As Object, e As ScrollChangedEventArgs)
            QueueThumbnailRefresh()
        End Sub

        Private Sub OnFilmstripPropertyChanged(sender As Object, e As Avalonia.AvaloniaPropertyChangedEventArgs)
            If e.Property <> Avalonia.Visual.BoundsProperty AndAlso e.Property <> ScrollViewer.ViewportProperty Then Return
            ' Eine andere Fenstergroesse verschiebt die Mitte: Polster und Lage neu.
            If IsCentering() Then QueueCenterSelection()
            QueueThumbnailRefresh()
            Dispatcher.UIThread.Post(Sub() RefreshThumbnails(), DispatcherPriority.Background)
        End Sub

        Public Sub QueueThumbnailRefresh()
            QueueThumbnailRefresh(DispatcherPriority.Input)
        End Sub

        Public Sub QueueThumbnailRefresh(priority As DispatcherPriority)
            If _thumbnailRefreshQueued Then Return
            _thumbnailRefreshQueued = True
            Dispatcher.UIThread.Post(Sub()
                                          _thumbnailRefreshQueued = False
                                          RefreshThumbnails()
                                      End Sub, priority)
        End Sub

        Private Function GetFilmstripScrollViewer() As ScrollViewer
            If _filmstripScrollViewer IsNot Nothing Then Return _filmstripScrollViewer
            Dim listBox = _owner.FindControl(Of ListBox)(_listBoxName)
            If listBox Is Nothing Then Return Nothing
            _filmstripScrollViewer = listBox.GetVisualDescendants().OfType(Of ScrollViewer)().FirstOrDefault()
            If _filmstripScrollViewer IsNot Nothing Then
                AddHandler _filmstripScrollViewer.PropertyChanged, AddressOf OnFilmstripPropertyChanged
            End If
            Return _filmstripScrollViewer
        End Function

        Public Sub RefreshThumbnails()
            Dim items = _getItems()
            If items Is Nothing OrElse items.Count = 0 Then Return
            Dim sv = GetFilmstripScrollViewer()
            Dim itemExtent = If(IsVertical, ItemHeight, ItemWidth)
            Dim scrollX = If(sv IsNot Nothing, If(IsVertical, sv.Offset.Y, sv.Offset.X), 0.0)
            Dim viewWidth = If(sv IsNot Nothing, If(IsVertical, sv.Viewport.Height, sv.Viewport.Width), 0.0)
            Dim firstVisible = Math.Max(0, CInt(Math.Floor(scrollX / itemExtent)) - 1)
            Dim lastVisible = Math.Min(items.Count - 1, CInt(Math.Ceiling((scrollX + Math.Max(viewWidth, itemExtent)) / itemExtent)) + 1)
            ' DIE SCHAETZUNG OBEN STIMMT NUR BEI GLEICH BREITEN KACHELN. Stehen sie im
            ' Seitenverhaeltnis des Bildes (Einstellung), passen mehr Hochkant-Kacheln in den
            ' Streifen als geschaetzt, und die hinteren bekaemen kein Vorschaubild. Der wahre Bereich
            ' kommt deshalb aus den gebauten Kacheln; die Schaetzung bleibt fuer den Fall, dass noch
            ' keine steht.
            Dim measuredFirst As Integer
            Dim measuredLast As Integer
            If sv IsNot Nothing AndAlso TryGetVisibleRange(sv, items.Count, measuredFirst, measuredLast) Then
                firstVisible = Math.Max(0, measuredFirst - 1)
                lastVisible = Math.Min(items.Count - 1, measuredLast + 1)
            End If
            Dim currentIndex = _getCurrentIndex()
            If currentIndex >= 0 AndAlso currentIndex < items.Count Then
                Dim currentBuffer = Math.Max(8, CInt(Math.Ceiling(Math.Max(viewWidth, itemExtent * 6) / itemExtent)))
                firstVisible = Math.Min(firstVisible, Math.Max(0, currentIndex - currentBuffer))
                lastVisible = Math.Max(lastVisible, Math.Min(items.Count - 1, currentIndex + currentBuffer))
            End If
            _tracker.RequestRange(items, firstVisible, lastVisible)
        End Sub

        ''' <summary>Erste und letzte Kachel, die im Sichtfenster des Streifens liegen, gemessen an den
        ''' gebauten Kacheln der Liste. Falsch, wenn keine gebaut ist oder keine im Fenster liegt.</summary>
        Private Function TryGetVisibleRange(sv As ScrollViewer, itemCount As Integer,
                                            ByRef first As Integer, ByRef last As Integer) As Boolean
            first = Integer.MaxValue
            last = -1
            Dim listBox = _owner.FindControl(Of ListBox)(_listBoxName)
            If listBox Is Nothing Then Return False
            Dim viewWidth = If(IsVertical, sv.Viewport.Height, sv.Viewport.Width)
            For Each container In listBox.GetRealizedContainers()
                Dim index = listBox.IndexFromContainer(container)
                If index < 0 OrElse index >= itemCount Then Continue For
                Dim origin = Avalonia.VisualExtensions.TranslatePoint(container, New Avalonia.Point(0, 0), sv)
                If Not origin.HasValue Then Continue For
                Dim left = If(IsVertical, origin.Value.Y, origin.Value.X)
                Dim extent = If(IsVertical, container.Bounds.Height, container.Bounds.Width)
                If left + extent < 0 OrElse left > viewWidth Then Continue For
                first = Math.Min(first, index)
                last = Math.Max(last, index)
            Next
            Return last >= 0
        End Function

        ''' <summary>Die Schnellvorschau selbst liegt in <see cref="QuickPreviewController"/> - sie
        ''' arbeitet hier genauso wie in der Galerie, und zwei Umsetzungen daneben sind zweimal
        ''' dieselben Fallen (ueberholter Decode, nicht freigegebene Bitmap, Serverbild).</summary>
        Private ReadOnly _preview As New QuickPreviewController()

        Public Sub ShowPreview(item As ImageItem)
            If item Is Nothing Then Return
            Dim overlay = _owner.FindControl(Of Panel)("FilmstripPreviewOverlay")
            Dim img = _owner.FindControl(Of Avalonia.Controls.Image)("FilmstripPreviewImage")
            If overlay Is Nothing OrElse img Is Nothing Then Return
            _preview.Show(overlay, img, item)
            _owner.Focus()
        End Sub

        Public Sub HidePreview()
            _preview.Hide(_owner.FindControl(Of Panel)("FilmstripPreviewOverlay"),
                          _owner.FindControl(Of Avalonia.Controls.Image)("FilmstripPreviewImage"))
        End Sub
    End Class

End Namespace
