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

        Private ReadOnly _owner As Control
        Private ReadOnly _tracker As ViewportThumbnailTracker
        Private ReadOnly _getItems As Func(Of IReadOnlyList(Of ImageItem))
        Private ReadOnly _getCurrentIndex As Func(Of Integer)
        Private _filmstripScrollViewer As ScrollViewer
        Private _thumbnailRefreshQueued As Boolean = False

        Public Sub New(owner As Control, tracker As ViewportThumbnailTracker,
                       getItems As Func(Of IReadOnlyList(Of ImageItem)), getCurrentIndex As Func(Of Integer))
            _owner = owner
            _tracker = tracker
            _getItems = getItems
            _getCurrentIndex = getCurrentIndex
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
            filmstrip.AddHandler(ScrollViewer.ScrollChangedEvent, AddressOf OnFilmstripScrollChanged, RoutingStrategies.Bubble)
        End Sub

        Public Sub ScrollToCurrent()
            Dispatcher.UIThread.Post(Sub()
                                          Dim items = _getItems()
                                          Dim idx = _getCurrentIndex()
                                          If idx < 0 OrElse items Is Nothing OrElse idx >= items.Count Then Return
                                          Dim listBox = _owner.FindControl(Of ListBox)("FilmstripListBox")
                                          If listBox Is Nothing Then Return
                                          listBox.SelectedIndex = idx
                                          listBox.ScrollIntoView(items(idx))
                                          QueueThumbnailRefresh()
                                          Dispatcher.UIThread.Post(Sub() RefreshThumbnails(), DispatcherPriority.Background)
                                      End Sub, DispatcherPriority.Loaded)
        End Sub

        Private Sub OnFilmstripScrollChanged(sender As Object, e As ScrollChangedEventArgs)
            QueueThumbnailRefresh()
        End Sub

        Private Sub OnFilmstripPropertyChanged(sender As Object, e As Avalonia.AvaloniaPropertyChangedEventArgs)
            If e.Property <> Avalonia.Visual.BoundsProperty AndAlso e.Property <> ScrollViewer.ViewportProperty Then Return
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
            Dim listBox = _owner.FindControl(Of ListBox)("FilmstripListBox")
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
            Dim scrollX = If(sv IsNot Nothing, sv.Offset.X, 0.0)
            Dim viewWidth = If(sv IsNot Nothing, sv.Viewport.Width, 0.0)
            Dim firstVisible = Math.Max(0, CInt(Math.Floor(scrollX / ItemWidth)) - 1)
            Dim lastVisible = Math.Min(items.Count - 1, CInt(Math.Ceiling((scrollX + Math.Max(viewWidth, ItemWidth)) / ItemWidth)) + 1)
            Dim currentIndex = _getCurrentIndex()
            If currentIndex >= 0 AndAlso currentIndex < items.Count Then
                Dim currentBuffer = Math.Max(8, CInt(Math.Ceiling(Math.Max(viewWidth, ItemWidth * 6) / ItemWidth)))
                firstVisible = Math.Min(firstVisible, Math.Max(0, currentIndex - currentBuffer))
                lastVisible = Math.Max(lastVisible, Math.Min(items.Count - 1, currentIndex + currentBuffer))
            End If
            _tracker.RequestRange(items, firstVisible, lastVisible)
        End Sub

        Public Sub ShowPreview(item As ImageItem)
            If item Is Nothing Then Return
            Dim overlay = _owner.FindControl(Of Panel)("FilmstripPreviewOverlay")
            Dim img = _owner.FindControl(Of Avalonia.Controls.Image)("FilmstripPreviewImage")
            If overlay Is Nothing OrElse img Is Nothing Then Return
            img.Source = item.Thumbnail
            overlay.IsVisible = True
            _owner.Focus()
            LoadFullPreviewAsync(overlay, img, item)
        End Sub

        Private Async Sub LoadFullPreviewAsync(overlay As Panel, img As Avalonia.Controls.Image, item As ImageItem)
            ' Videos lassen sich nicht als Vollbild-Bitmap dekodieren - das würde nur leise
            ' fehlschlagen und die Vorschau leer lassen. Stattdessen bleibt das bereits gesetzte
            ' Thumbnail (Standbild) sichtbar.
            If item.IsVideoFile Then Return
            Try
                Dim bmp = Await Task.Run(Function() ImageOrientationService.LoadOrientedAvaloniaBitmap(item.FilePath))
                If overlay.IsVisible Then img.Source = bmp
            Catch
            End Try
        End Sub

        Public Sub HidePreview()
            Dim overlay = _owner.FindControl(Of Panel)("FilmstripPreviewOverlay")
            If overlay IsNot Nothing Then overlay.IsVisible = False
        End Sub
    End Class

End Namespace
