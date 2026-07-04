Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Media.Imaging
Imports FerrumPix.ViewModels

Namespace Views

    Public Class CollageDialogOverlayView
        Inherits UserControl

        Private _observedVm As GalleryViewModel
        Private _isPanning As Boolean
        Private _panMoved As Boolean
        Private _panStartPoint As Point
        Private _panStartOffset As Vector

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            AddHandler Me.DataContextChanged, AddressOf OnOwnDataContextChanged
        End Sub

        Private Sub OnOwnDataContextChanged(sender As Object, e As EventArgs)
            If _observedVm IsNot Nothing Then
                RemoveHandler _observedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If
            _observedVm = TryCast(DataContext, GalleryViewModel)
            If _observedVm IsNot Nothing Then
                AddHandler _observedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If
            ApplyPreviewImageSize()
        End Sub

        Private Sub OnViewModelPropertyChanged(sender As Object, e As System.ComponentModel.PropertyChangedEventArgs)
            If e.PropertyName = NameOf(GalleryViewModel.CollagePreviewImage) OrElse
               e.PropertyName = NameOf(GalleryViewModel.CollagePreviewZoom) Then
                ApplyPreviewImageSize()
            End If
        End Sub

        ''' Das Vorschau-Image nutzt Stretch="Fill" mit EXPLIZIT gesetzter Width/Height (statt
        ''' Uniform-Stretch) - so entspricht die angezeigte Fläche 1:1 dem Zoom-Faktor mal den echten
        ''' Pixelmaßen, der ScrollViewer bekommt dadurch automatisch die richtige Scroll-Ausdehnung,
        ''' und Klick-Koordinaten lassen sich ohne Letterbox-Offset einfach durch den Zoom teilen.
        Private Sub ApplyPreviewImageSize()
            Dim image = Me.FindControl(Of Image)("PreviewImage")
            Dim vm = TryCast(DataContext, GalleryViewModel)
            If image Is Nothing OrElse vm Is Nothing Then Return
            Dim bitmap = vm.CollagePreviewImage
            If bitmap Is Nothing Then Return

            Dim zoom = Math.Max(0.05, vm.CollagePreviewZoom)
            image.Width = bitmap.PixelSize.Width * zoom
            image.Height = bitmap.PixelSize.Height * zoom
        End Sub

        Private Sub OnCreateClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, GalleryViewModel)
            vm?.CreateCollage()
            e.Handled = True
        End Sub

        Private Sub OnCancelClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, GalleryViewModel)
            vm?.CloseCollageDialog()
            e.Handled = True
        End Sub

        Private Sub OnLayoutModeClick(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, GalleryViewModel)
            If btn Is Nothing OrElse vm Is Nothing Then Return
            vm.CollageLayoutMode = TryCast(btn.Tag, String)
        End Sub

        Private Sub OnHeroPositionClick(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, GalleryViewModel)
            If btn Is Nothing OrElse vm Is Nothing Then Return
            vm.CollageHeroPosition = TryCast(btn.Tag, String)
        End Sub

        Private Sub OnReshuffleClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, GalleryViewModel)
            vm?.ReshuffleCollageRandom()
        End Sub

        Private Sub OnPreviewPointerWheelChanged(sender As Object, e As PointerWheelEventArgs)
            Dim vm = TryCast(DataContext, GalleryViewModel)
            If vm Is Nothing Then Return
            vm.CollagePreviewZoom = vm.CollagePreviewZoom + e.Delta.Y * 0.1
            e.Handled = True
        End Sub

        ''' Startet einen möglichen Pan-Vorgang UND merkt sich die Ausgangsposition für die
        ''' Hero-Klick-Erkennung - erst in OnPreviewPointerReleased wird anhand von _panMoved
        ''' entschieden, ob es ein Klick (Hero setzen) oder ein Ziehen (nur Verschieben) war.
        Private Sub OnPreviewPointerPressed(sender As Object, e As PointerPressedEventArgs)
            Dim image = TryCast(sender, Image)
            If image Is Nothing Then Return
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("PreviewScrollViewer")
            If scrollViewer Is Nothing Then Return

            _isPanning = True
            _panMoved = False
            _panStartPoint = e.GetPosition(scrollViewer)
            _panStartOffset = scrollViewer.Offset
            e.Pointer.Capture(image)
            e.Handled = True
        End Sub

        Private Sub OnPreviewPointerMoved(sender As Object, e As PointerEventArgs)
            If Not _isPanning Then Return
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("PreviewScrollViewer")
            If scrollViewer Is Nothing Then Return

            Dim pos = e.GetPosition(scrollViewer)
            Dim delta = pos - _panStartPoint
            Dim distance = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y)
            If Not _panMoved AndAlso distance > 4 Then _panMoved = True
            If Not _panMoved Then Return

            Dim maxX = Math.Max(0.0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width)
            Dim maxY = Math.Max(0.0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height)
            Dim newX = Math.Max(0.0, Math.Min(maxX, _panStartOffset.X - delta.X))
            Dim newY = Math.Max(0.0, Math.Min(maxY, _panStartOffset.Y - delta.Y))
            scrollViewer.Offset = New Vector(newX, newY)
            e.Handled = True
        End Sub

        ''' War es kein Ziehen (siehe _panMoved), zählt es als Klick auf ein Bild - im Hero-Layout
        ''' wird dann anhand der Klickposition das getroffene Bild als neues Hero-Bild gesetzt. Da
        ''' PreviewImage per Fill-Stretch mit expliziter, zoomabhängiger Width/Height gezeichnet wird
        ''' (siehe ApplyPreviewImageSize), liefert e.GetPosition bereits Pixel im gezoomten Bild -
        ''' einfach durch den Zoom teilen, um die Position im UNSKALIERTEN Vorschau-Bitmap
        ''' (== Koordinatenraum von LastPreviewSlots) zu erhalten.
        Private Sub OnPreviewPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            Dim image = TryCast(sender, Image)
            _isPanning = False
            e.Pointer.Capture(Nothing)
            e.Handled = True
            If image Is Nothing OrElse _panMoved Then Return

            Dim vm = TryCast(DataContext, GalleryViewModel)
            If vm Is Nothing OrElse Not vm.IsCollageHeroMode Then Return
            Dim zoom = Math.Max(0.05, vm.CollagePreviewZoom)

            Dim pos = e.GetPosition(image)
            vm.SetCollageHeroFromPreviewClick(pos.X / zoom, pos.Y / zoom)
        End Sub

        ''' Berechnet den Zoom-Faktor, der das komplette Vorschau-Bitmap in den sichtbaren
        ''' ScrollViewer-Viewport einpasst (wie "An Fenster anpassen" im Viewer), und setzt den
        ''' Offset zurück auf den Ursprung.
        Private Sub OnFitPreviewClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, GalleryViewModel)
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("PreviewScrollViewer")
            If vm Is Nothing OrElse scrollViewer Is Nothing Then Return
            Dim bitmap = vm.CollagePreviewImage
            If bitmap Is Nothing OrElse scrollViewer.Bounds.Width <= 0 OrElse scrollViewer.Bounds.Height <= 0 Then Return

            Dim scaleX = scrollViewer.Bounds.Width / bitmap.PixelSize.Width
            Dim scaleY = scrollViewer.Bounds.Height / bitmap.PixelSize.Height
            vm.CollagePreviewZoom = Math.Min(scaleX, scaleY)
            scrollViewer.Offset = New Vector(0, 0)
            e.Handled = True
        End Sub
    End Class

End Namespace
