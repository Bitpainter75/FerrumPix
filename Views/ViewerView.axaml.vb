Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Controls
Imports FerrumPix.Models
Imports FerrumPix.Services
Imports FerrumPix.ViewModels
Imports System.ComponentModel
Imports System.Threading.Tasks

Namespace Views

    Public Class ViewerView
        Inherits UserControl

        Private _subscribedVm As ViewerViewModel
        Private _isAttached As Boolean = False
        Private _isPanningImage As Boolean
        Private _panStartPoint As Point
        Private _panStartOffset As Vector
        Private _isCropDragging As Boolean
        Private _cropDragMoved As Boolean
        Private _cropDragStartNorm As Point
        Private _cropDragCurrentNorm As Point
        Private _suppressNextImageContextMenu As Boolean = False
        Private Const CropDragMinPixels As Double = 12
        Private Const FullscreenVideoControlsIdleMs As Integer = 2200
        Private Const FullscreenVideoCursorPollMs As Integer = 120
        Private ReadOnly _fullscreenVideoControlsHideTimer As DispatcherTimer
        Private ReadOnly _fullscreenVideoCursorPollTimer As DispatcherTimer
        Private _fullscreenVideoControlsVisible As Boolean = True
        Private _lastFullscreenCursorPosition As PixelPoint?
        Private ReadOnly _filmstripController As FilmstripInteractionController

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            _fullscreenVideoControlsHideTimer = New DispatcherTimer With {
                .Interval = TimeSpan.FromMilliseconds(FullscreenVideoControlsIdleMs)
            }
            _fullscreenVideoCursorPollTimer = New DispatcherTimer With {
                .Interval = TimeSpan.FromMilliseconds(FullscreenVideoCursorPollMs)
            }
            AddHandler _fullscreenVideoControlsHideTimer.Tick, AddressOf OnFullscreenVideoControlsIdle
            AddHandler _fullscreenVideoCursorPollTimer.Tick, AddressOf OnFullscreenVideoCursorPoll
            _filmstripController = New FilmstripInteractionController(Me, New ViewportThumbnailTracker(),
                Function() GetVm()?.FilmstripItems,
                Function() If(GetVm() Is Nothing, -1, GetVm().CurrentFilmstripIndex))
            AddHandler DataContextChanged, AddressOf HandleDataContextChanged
            ContextMenuAttachment.Attach(Me.FindControl(Of Grid)("ViewerRootGrid"), AddressOf OnContextRequested)
            AddHandler Loaded, Sub(s, e)
                                   Me.AddHandler(InputElement.PointerWheelChangedEvent, AddressOf OnPointerWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel)
                                   Me.AddHandler(InputElement.PointerMovedEvent, AddressOf OnGlobalPointerActivity, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo:=True)
                                   Me.AddHandler(InputElement.PointerPressedEvent, AddressOf OnGlobalPointerActivity, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo:=True)
                                   UpdateInfoSidebarLayoutState()
                                   _filmstripController.AttachTo(Me.FindControl(Of ListBox)("FilmstripListBox"))
                                   _filmstripController.QueueThumbnailRefresh()
                                   Dispatcher.UIThread.Post(Sub() _filmstripController.RefreshThumbnails(), DispatcherPriority.Background)

                                   ' RoundSlider markt PointerReleased in seiner eigenen Klassen-Behandlung bereits
                                   ' als "Handled" (siehe Controls/RoundSlider.vb) - ein normal per XAML angehängter
                                   ' Instanz-Handler würde dadurch riskieren, nie aufgerufen zu werden. handledEventsToo:=True
                                   ' erzwingt den Aufruf unabhängig davon.
                                   Dim seekSlider = Me.FindControl(Of Control)("VideoSeekSlider")
                                   If seekSlider IsNot Nothing Then
                                       seekSlider.AddHandler(InputElement.PointerPressedEvent, AddressOf OnVideoSeekSliderPointerPressed,
                                                              Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo:=True)
                                       seekSlider.AddHandler(InputElement.PointerReleasedEvent, AddressOf OnVideoSeekSliderPointerReleased,
                                                              Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo:=True)
                                   End If
                                   UpdateVideoPlaybackBarVisibility()
                               End Sub
            AddHandler Unloaded, Sub(s, e) StopFullscreenVideoControlTimers()
        End Sub

        Private Function GetVm() As ViewerViewModel
            Return TryCast(DataContext, ViewerViewModel)
        End Function

        Public Sub OnFilmstripItemPressed(sender As Object, e As PointerPressedEventArgs)
            Dim border = TryCast(sender, Border)
            If border Is Nothing Then Return
            Dim item = TryCast(border.DataContext, ImageItem)
            If item Is Nothing Then Return
            If e.GetCurrentPoint(Nothing).Properties.IsMiddleButtonPressed Then
                _filmstripController.ShowPreview(item)
                e.Handled = True
                Return
            End If
            If Not e.GetCurrentPoint(Nothing).Properties.IsLeftButtonPressed Then Return
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            ' Auch im Vergleich der normale Weg: LoadPathAt entscheidet dort, dass die RECHTE
            ' Flaeche weiterblaettert. Ein zweiter Sonderfall hier waere eine Gabelung, die beim
            ' naechsten Umbau auseinanderlaeuft.
            vm.NavigateToItem(item)
            Me.Focus()
        End Sub

        ''' <summary>Ausschnitt koppeln oder entkoppeln. Beim EINKOPPELN zieht die fokussierte
        ''' Flaeche die andere sofort auf ihren Ausschnitt - sonst blieben die beiden Seiten nach
        ''' dem Einschalten so lange auseinander, bis man eine von ihnen anfasst.</summary>
        Public Sub OnToggleCompareViewportLinkClick(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            vm.IsCompareViewportLinked = Not vm.IsCompareViewportLinked
            If Not vm.IsCompareViewportLinked Then Return
            Dim fuehrend = Me.FindControl(Of ScrollViewer)(
                If(vm.IsCompareRightFocused, "CompareRightScroll", "CompareLeftScroll"))
            If fuehrend IsNot Nothing Then OnCompareScrollChanged(fuehrend, Nothing)
        End Sub

        ''' <summary>Die Marken tragen ihre Flaeche im Tag ("0" links, "1" rechts), die Sterne
        ''' zusaetzlich den Wert ("0:3"). Ohne diese Zuordnung waere am Knopf nicht ablesbar, welches
        ''' der beiden Bilder er meint - und genau dafuer sitzen die Marken auf dem Bild.</summary>
        Private Shared Function PaneFrom(sender As Object) As Integer
            Dim tag = TryCast(TryCast(sender, Control)?.Tag, String)
            If String.IsNullOrEmpty(tag) Then Return 0
            Return If(tag.Split(":"c)(0) = "1", 1, 0)
        End Function

        ''' <summary>Das Fussmenue zeigen. Wie in der Galerie ueber die Schaltflaeche statt ueber
        ''' einen Rechtsklick: ein Rechtsklick ist nicht auf jedem Geraet bequem.</summary>
        Private Sub OnFooterMenuButtonClick(sender As Object, e As Avalonia.Interactivity.RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            ' Es gibt nur EIN Menue je Ansicht, und es haengt an der Wurzel. Die Schaltflaeche
            ' oeffnet dasselbe wie ein Rechtsklick und meldet nur den Aufrufort.
            Dim vm = GetVm()
            Dim menu = Me.FindControl(Of ContextMenu)("ViewerKontextMenu")
            If menu Is Nothing OrElse button Is Nothing OrElse vm Is Nothing Then Return

            vm.ContextSite = MenuSite.ViewerFooter
            vm.ContextItems = ContextTarget.Affected(Nothing, Nothing, StageItem(vm))

            ' Open(control) besteht darauf, dass control GENAU das Element ist, an dem das Menue
            ' haengt - das ist hier die Fussleiste, nicht die Schaltflaeche. Der parameterlose
            ' Aufruf oeffnet am angehaengten Element; die Lage steuert PlacementTarget.
            menu.PlacementTarget = button
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft
            menu.Open()
            e.Handled = True
        End Sub

        ''' <summary>Menues entstehen erst beim Oeffnen und verpassen den einmaligen
        ''' Fenster-Durchlauf der Lokalisierung - deshalb hier ihren eigenen Unterbaum.</summary>
        Private Sub OnLocalizedMenuOpened(sender As Object, e As Avalonia.Interactivity.RoutedEventArgs)
            Dim menu = TryCast(sender, Avalonia.LogicalTree.ILogical)
            If menu IsNot Nothing Then LocalizationService.ApplyTo(menu)
        End Sub

        ''' <summary>Die Eintraege kommen als Daten und tragen ihre Beschriftung bereits uebersetzt.
        ''' Beim Oeffnen einmal neu lesen lassen, damit ein Sprachwechsel ankommt.</summary>
        ''' <summary>Den Zwischenablage-Haken des ViewModels fuellen. Muss hier passieren: die
        ''' Zwischenablage haengt am TopLevel, das ViewModel kommt nicht daran. Ohne das waere
        ''' "Pfad kopieren" ein Eintrag, den es gibt und der nichts tut.</summary>
        Private Sub SetClipboardHook()
            Dim vm = TryCast(DataContext, ViewerViewModel)
            If vm Is Nothing Then Return
            vm.CopyPathToClipboard = Async Sub(path)
                                          Try
                                              If String.IsNullOrEmpty(path) Then Return
                                              Dim owner = TopLevel.GetTopLevel(Me)
                                              Await ClipboardPathService.CopyPathsAsync(owner?.Clipboard, owner?.StorageProvider, {path}, cut:=False)
                                          Catch ex As Exception
                                              DiagnosticLogService.LogException("Viewer.CopyPath", ex)
                                          End Try
                                      End Sub
        End Sub


        Private Sub OnFooterMenuOpened(sender As Object, e As Avalonia.Interactivity.RoutedEventArgs)
            SetClipboardHook()
            TryCast(DataContext, ViewerViewModel)?.RefreshContextActions()
        End Sub

        ''' <summary>Rechtsklick irgendwo im Betrachter. EIN Handler fuer alle drei Aufruforte:
        ''' Buehne, Filmstreifen und Fusszeile. Wo geklickt wurde, entscheidet sich hier, und die
        ''' betroffenen Elemente kommen aus <see cref="ContextTarget"/> - der einen Stelle, die das
        ''' fuer die ganze Anwendung beantwortet.</summary>
        Private Sub OnContextRequested(sender As Object, e As Avalonia.Input.ContextRequestedEventArgs)
            ' Nach einem Zoom mit gehaltener rechter Taste ist KEIN Menue gemeint. Das Merkmal war
            ' laengst gesetzt und wurde bisher nie ausgewertet.
            If _suppressNextImageContextMenu Then
                _suppressNextImageContextMenu = False
                e.Handled = True
                Return
            End If

            Dim vm = GetVm()
            Dim menu = Me.FindControl(Of ContextMenu)("ViewerKontextMenu")
            If vm Is Nothing OrElse menu Is Nothing Then Return

            Dim filmstrip = Me.FindControl(Of ListBox)("FilmstripListBox")
            Dim hit = ContextTarget.UnderPointer(e, filmstrip)

            ' Ein Rechtsklick im Filmstreifen meint DIESES Bild. Die Kommandos des Betrachters
            ' arbeiten aber alle auf dem Bild der Buehne - es reicht also nicht, das getroffene
            ' Element zu kennen, es muss auch das aktuelle werden. Genauso verhaelt sich die
            ' Galerie, wo ein Rechtsklick daneben die Auswahl umsetzt.
            If hit IsNot Nothing AndAlso Not Object.ReferenceEquals(hit, StageItem(vm)) Then
                vm.NavigateToItem(hit)
            End If

            Dim stage = Me.FindControl(Of ScrollViewer)("ImageScrollViewer")
            Dim onStage = hit Is Nothing AndAlso PointerIsWithin(e, stage)

            vm.ContextSite = If(hit IsNot Nothing, MenuSite.ViewerFilmstrip,
                                If(onStage, MenuSite.ViewerStage, MenuSite.ViewerFooter))
            vm.ContextItems = ContextTarget.Affected(hit, Nothing, StageItem(vm))

            ' NICHT selbst oeffnen und NICHT als behandelt melden: das Menue haengt am selben
            ' Element und oeffnet sich gleich nach diesem Handler von allein - siehe
            ' ContextMenuAttachment. Die Lage muss zurueckgesetzt werden, weil die Schaltflaeche
            ' in der Fusszeile sie vorher auf eine feste Kante gestellt haben kann.
            menu.PlacementTarget = Nothing
            menu.Placement = PlacementMode.Pointer
        End Sub

        ''' <summary>Liegt der Zeiger ueber diesem Bereich? Rein geometrisch geprueft, weil die
        ''' Quelle des Ereignisses bei geschachtelten Elementen nichts darueber sagt.</summary>
        Private Shared Function PointerIsWithin(e As Avalonia.Input.ContextRequestedEventArgs, area As Control) As Boolean
            If area Is Nothing OrElse Not area.IsVisible Then Return False
            Dim pos As Avalonia.Point
            If Not e.TryGetPosition(area, pos) Then Return False
            Return pos.X >= 0 AndAlso pos.Y >= 0 AndAlso
                   pos.X <= area.Bounds.Width AndAlso pos.Y <= area.Bounds.Height
        End Function

        ''' <summary>Das Bild auf der Buehne als Element - der Rueckfall, wenn der Klick keine
        ''' Kachel getroffen hat.</summary>
        Private Function StageItem(vm As ViewerViewModel) As ImageItem
            Dim path = vm?.CurrentImagePath
            If String.IsNullOrWhiteSpace(path) Then Return Nothing
            Dim aus = vm.FilmstripItems.FirstOrDefault(Function(i) i IsNot Nothing AndAlso
                                                           PathIdentity.AreSame(i.FilePath, path))
            If aus IsNot Nothing Then Return aus
            If Not IO.File.Exists(path) Then Return Nothing
            Return New ImageItem(path)
        End Function

        Private Sub OnCompareStarClick(sender As Object, e As RoutedEventArgs)
            Dim tag = TryCast(TryCast(sender, Control)?.Tag, String)
            If String.IsNullOrEmpty(tag) Then Return
            Dim parts = tag.Split(":"c)
            Dim sterne As Integer
            If parts.Length <> 2 OrElse Not Integer.TryParse(parts(1), sterne) Then Return
            GetVm()?.SetCompareRating(PaneFrom(sender), sterne)
            e.Handled = True
        End Sub

        Private Sub OnCompareFavoriteClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.ToggleCompareFavorite(PaneFrom(sender))
            e.Handled = True
        End Sub

        Private Sub OnCompareEditClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.OpenComparePaneInEditor(PaneFrom(sender))
            e.Handled = True
        End Sub

        Private Sub OnCompareDeleteClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.DeleteComparePane(PaneFrom(sender))
            e.Handled = True
        End Sub

        Public Sub OnSwapComparePanesClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.SwapComparePanes()
        End Sub

        Public Sub OnTogglePinClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.TogglePin()
        End Sub

        Public Sub OnToggleFilmstripClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            If mainVm Is Nothing OrElse mainVm.Settings Is Nothing Then Return
            mainVm.Settings.ViewerShowFilmstrip = Not mainVm.Settings.ViewerShowFilmstrip
        End Sub

        Public Sub OnGlobalPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            If e.InitialPressMouseButton = MouseButton.Middle Then
                _filmstripController.HidePreview()
            End If
        End Sub

        Public Sub OnGlobalPointerActivity(sender As Object, e As PointerEventArgs)
            NoteFullscreenVideoControlsActivity()
        End Sub

        Public Sub OnVideoInputPointerPressed(sender As Object, e As PointerPressedEventArgs)
            NoteFullscreenVideoControlsActivity()
        End Sub

        Public Shadows Sub OnKeyDown(sender As Object, e As KeyEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            If IsTextInputSource(e.Source) Then Return
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)

            If mainVm IsNot Nothing AndAlso mainVm.IsFullscreen AndAlso (e.Key = Key.Escape OrElse e.Key = Key.Back) Then
                mainVm.ExitFullscreen()
                e.Handled = True
                Return
            End If

            If e.Key <> Key.Escape Then
                NoteFullscreenVideoControlsActivity()
            End If

            If PlatformShortcutService.IsMacOS AndAlso
               PlatformShortcutService.HasPrimaryModifier(e.KeyModifiers) Then
                Select Case e.Key
                    Case Key.R
                        If e.KeyModifiers.HasFlag(KeyModifiers.Alt) Then
                            vm.RotateRightCommand.Execute(Nothing)
                        Else
                            vm.RotateLeftCommand.Execute(Nothing)
                        End If
                        e.Handled = True
                        Return
                    Case Key.I
                        vm.ToggleInfoSidebarCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                End Select
            End If

            If PlatformShortcutService.HasApplicationModifier(e.KeyModifiers) Then
                Select Case e.Key
                    Case Key.Left
                        ' Drehen liegt auf Strg+Pfeil (in Betrachter und Editor gleich),
                        ' damit Strg+R wie in der Galerie „Bildgröße ändern" öffnet.
                        vm.RotateLeftCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                    Case Key.Right
                        vm.RotateRightCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                    Case Key.I
                        vm.ToggleInfoSidebarCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                    Case Key.E
                        vm.EditCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                End Select
            End If

            Select Case e.Key
                Case Key.Left, Key.PageUp
                    vm.PreviousCommand.Execute(Nothing)
                    e.Handled = True
                Case Key.Right, Key.PageDown
                    vm.NextCommand.Execute(Nothing)
                    e.Handled = True
                Case Key.Add, Key.OemPlus
                    vm.ZoomIn()
                    e.Handled = True
                Case Key.Subtract, Key.OemMinus
                    vm.ZoomOut()
                    e.Handled = True
                Case Key.D0, Key.NumPad0
                    vm.ZoomFitCommand.Execute(Nothing)
                    ApplyImageFitMode()
                    e.Handled = True
                Case Key.Delete
                    ' Im Vergleich stehen zwei Bilder nebeneinander - welches die Taste traefe,
                    ' waere nicht ablesbar. Loeschen ist nicht umkehrbar, also lieber nichts tun.
                    If vm.CanDeleteCurrent Then
                        vm.DeleteCurrentCommand.Execute(Nothing)
                        e.Handled = True
                    End If
                Case Key.F2
                    vm.RenameCurrentCommand.Execute(Nothing)
                    e.Handled = True
                Case Key.Space
                    ' Im Vergleich tauscht die Leertaste die Seiten - eine Diashow waere dort
                    ' sinnlos, und Tauschen ist die Aktion, die man beim Durchsehen einer Serie am
                    ' haeufigsten braucht.
                    If vm.IsCompareMode Then
                        vm.SwapComparePanes()
                    Else
                        vm.ToggleSlideshowCommand.Execute(Nothing)
                    End If
                    e.Handled = True
                ' F11 behandelt MainWindow.OnWindowKeyDown im Tunnel, für alle Modi gemeinsam.
                Case Key.Escape, Key.Back
                    If mainVm IsNot Nothing AndAlso mainVm.IsFullscreen Then
                        mainVm.ExitFullscreen()
                    ElseIf vm.IsCompareMode Then
                        ' Erst den Vergleich verlassen, dann erst die Galerie - sonst springt man
                        ' aus zwei Zustaenden auf einmal heraus.
                        vm.ExitCompare()
                    Else
                        vm.BackToGalleryCommand.Execute(Nothing)
                    End If
                    e.Handled = True
            End Select
        End Sub

        Private Function IsTextInputSource(source As Object) As Boolean
            Dim ctrl = TryCast(source, Control)
            While ctrl IsNot Nothing
                If TypeOf ctrl Is TextBox Then Return True
                ctrl = TryCast(ctrl.Parent, Control)
            End While
            Return False
        End Function

        ' Kopiert die Datei über ClipboardPathService (DataFormat.File / text/uri-list), damit sie
        ' sich wie in der Galerie in einem Dateimanager (z.B. Dolphin) als echte Datei einfügen
        ' lässt - reines SetTextAsync(path) erzeugt dort keinen einfügbaren Dateiverweis.
        Public Async Sub OnCopyPathClick(sender As Object, e As RoutedEventArgs)
            Try
                Dim vm = GetVm()
                If vm Is Nothing OrElse String.IsNullOrEmpty(vm.CurrentImagePath) Then Return
                Dim owner = TopLevel.GetTopLevel(Me)
                Await ClipboardPathService.CopyPathsAsync(owner?.Clipboard, owner?.StorageProvider, {vm.CurrentImagePath}, cut:=False)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("ViewerView.OnCopyPathClick", ex)
            End Try
        End Sub

        Public Sub OnToggleFullscreenClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            If mainVm Is Nothing Then Return
            If mainVm.IsFullscreen Then
                mainVm.ExitFullscreen()
            Else
                mainVm.EnterFullscreen()
            End If
            If e IsNot Nothing Then e.Handled = True
        End Sub

        Public Sub OnSettingsClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            mainVm?.OpenSettings()
            e.Handled = True
        End Sub

        Public Sub OnPointerWheel(sender As Object, e As PointerWheelEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            If IsWithinInfoSidebar(e.Source) Then Return

            ' Im Vergleich zoomt das Rad BEIDE Flaechen: sie teilen sich ZoomLevel, es genuegt also,
            ' den Wert zu aendern. Ohne diesen Zweig griffe der Handler auf die versteckte
            ' Einzelbildflaeche zu und das Rad wuerde die Vergleichsflaeche nur scrollen.
            If vm.IsCompareMode Then
                If e.GetCurrentPoint(Me).Properties.IsRightButtonPressed OrElse
                   e.KeyModifiers.HasFlag(KeyModifiers.Control) Then
                    _suppressNextImageContextMenu = True
                    Dim underMouse = ComparePaneUnder(e)
                    If underMouse IsNot Nothing Then
                        ZoomCompareAtPoint(underMouse, e.GetPosition(underMouse), If(e.Delta.Y > 0, 1.25, 1.0 / 1.25))
                    Else
                        vm.ActiveZoomPreset = ZoomPresetMode.Manual
                        vm.ZoomLevel = Math.Max(0.05, vm.ZoomLevel) * If(e.Delta.Y > 0, 1.25, 1.0 / 1.25)
                    End If
                Else
                    ' Wie in der Einzelansicht blaettert das blanke Rad weiter - im Vergleich trifft
                    ' das die RECHTE Flaeche (die Weiche sitzt in LoadPathAt), die linke bleibt als
                    ' Bezug stehen.
                    vm.NavigateByWheel(e.Delta.Y)
                End If
                e.Handled = True
                Return
            End If

            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("ImageScrollViewer")
            Dim rightButtonZoom = scrollViewer IsNot Nothing AndAlso e.GetCurrentPoint(scrollViewer).Properties.IsRightButtonPressed

            If rightButtonZoom Then
                _suppressNextImageContextMenu = True
                ZoomImageAtViewportPoint(e.GetPosition(scrollViewer), If(e.Delta.Y > 0, 1.25, 1.0 / 1.25))
            ElseIf e.KeyModifiers.HasFlag(KeyModifiers.Control) Then
                If scrollViewer IsNot Nothing Then
                    ZoomImageAtViewportPoint(e.GetPosition(scrollViewer), If(e.Delta.Y > 0, 1.25, 1.0 / 1.25))
                ElseIf e.Delta.Y < 0 Then
                    vm.ZoomOut()
                    ApplyImageFitMode()
                ElseIf e.Delta.Y > 0 Then
                    vm.ZoomIn()
                    ApplyImageFitMode()
                End If
            Else
                vm.NavigateByWheel(e.Delta.Y)
            End If
            e.Handled = True
        End Sub

        ''' <summary>Zoomen im Vergleich, verankert am Punkt unter der Maus - wie in der Einzelansicht.
        ''' Ohne das springt die Ansicht beim Zoomen weg, weil nur der Zoomwert steigt und der
        ''' Ausschnitt stehen bleibt. Verankert wird an der Flaeche, ueber der die Maus steht; die
        ''' andere folgt ueber die Ausschnitt-Spiegelung.
        ''' Der Offset wird ZWEIMAL gesetzt: einmal sofort und einmal nach dem Layout-Durchlauf - vor
        ''' dem Neu-Vermessen der Bilder kennt der ScrollViewer seinen neuen Umfang noch nicht.</summary>
        Private Sub ZoomCompareAtPoint(source As ScrollViewer, pt As Point, factor As Double)
            Dim vm = GetVm()
            If vm Is Nothing OrElse source Is Nothing OrElse factor <= 0 Then Return

            Dim alterZoom = Math.Max(0.05, vm.ZoomLevel)
            Dim imageX = (source.Offset.X + pt.X) / alterZoom
            Dim imageY = (source.Offset.Y + pt.Y) / alterZoom

            vm.ActiveZoomPreset = ZoomPresetMode.Manual
            vm.ZoomLevel = alterZoom * factor
            ApplyCompareFitMode()

            Dim setzeOffset =
                Sub()
                    Dim neuerZoom = Math.Max(0.05, vm.ZoomLevel)
                    Dim target = New Vector(imageX * neuerZoom - pt.X, imageY * neuerZoom - pt.Y)
                    source.Offset = New Vector(
                        Math.Min(Math.Max(target.X, 0), Math.Max(0, source.Extent.Width - source.Viewport.Width)),
                        Math.Min(Math.Max(target.Y, 0), Math.Max(0, source.Extent.Height - source.Viewport.Height)))
                    ' Beim Ziehen mit gedrueckter Taste ist die gemerkte Basis nach dem Zoomen
                    ' veraltet - ohne Neu-Verankern springt die Ansicht beim Weiterziehen zurueck.
                    If _compareZiehtScroll IsNot Nothing Then
                        _compareZiehtVon = pt
                        _compareZiehtOffset = source.Offset
                    End If
                End Sub
            setzeOffset()
            Dispatcher.UIThread.Post(setzeOffset, DispatcherPriority.Background)
        End Sub

        ''' <summary>Die Vergleichsflaeche unter dem Mauszeiger. Verankert wird dort, wo die Maus
        ''' steht - nicht an der fokussierten Flaeche: man zoomt auf das, worauf man zeigt.</summary>
        Private Function ComparePaneUnder(e As PointerEventArgs) As ScrollViewer
            For Each flaechenName In {"CompareLeftScroll", "CompareRightScroll"}
                Dim sv = Me.FindControl(Of ScrollViewer)(flaechenName)
                If sv Is Nothing Then Continue For
                Dim p = e.GetPosition(sv)
                If p.X >= 0 AndAlso p.Y >= 0 AndAlso p.X <= sv.Bounds.Width AndAlso p.Y <= sv.Bounds.Height Then Return sv
            Next
            Return Nothing
        End Function

        Private Sub ZoomImageAtViewportPoint(viewportPoint As Point, factor As Double)
            Dim vm = GetVm()
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("ImageScrollViewer")
            If vm Is Nothing OrElse scrollViewer Is Nothing OrElse vm.CurrentImage Is Nothing OrElse factor <= 0 Then Return

            Dim oldZoom = Math.Max(0.05, vm.ZoomLevel)
            Dim imageX = (scrollViewer.Offset.X + viewportPoint.X) / oldZoom
            Dim imageY = (scrollViewer.Offset.Y + viewportPoint.Y) / oldZoom

            vm.ActiveZoomPreset = ZoomPresetMode.Manual
            vm.ZoomLevel = oldZoom * factor
            ApplyImageFitMode()

            Dim applyOffset =
                Sub()
                    Dim newZoom = Math.Max(0.05, vm.ZoomLevel)
                    Dim target = New Vector(imageX * newZoom - viewportPoint.X,
                                            imageY * newZoom - viewportPoint.Y)
                    scrollViewer.Offset = ClampOffset(scrollViewer, target)
                    ' Rad-Zoom bei GEDRUECKTER rechter Maustaste: das Zoomen hat den Offset veraendert,
                    ' die beim Pointer-Down gemerkte Pan-Basis ist veraltet. Ohne Neu-Verankern springt
                    ' die Ansicht beim anschliessenden Ziehen auf den Stand VOR dem Zoomen zurueck
                    ' (gleicher Bug wie im Editor). Drag-Basis deshalb auf JETZT setzen - auch im
                    ' verzoegerten zweiten Aufruf (Dispatcher-Post nach dem Layout-Durchlauf).
                    If _isPanningImage Then
                        _panStartPoint = viewportPoint
                        _panStartOffset = scrollViewer.Offset
                    End If
                End Sub
            applyOffset()
            Dispatcher.UIThread.Post(applyOffset, DispatcherPriority.Background)
        End Sub

        Private Function IsWithinInfoSidebar(source As Object) As Boolean
            Dim ctrl = TryCast(source, Control)
            While ctrl IsNot Nothing
                If TypeOf ctrl Is InfoSidebarView Then Return True
                ctrl = TryCast(ctrl.Parent, Control)
            End While
            Return False
        End Function

        Private Sub OnImagePointerPressed(sender As Object, e As PointerPressedEventArgs)
            Dim properties = e.GetCurrentPoint(Nothing).Properties
            Dim isLeftButton = properties.IsLeftButtonPressed
            Dim isRightButton = properties.IsRightButtonPressed
            If Not isLeftButton AndAlso Not isRightButton Then Return
            Dim vm = GetVm()
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("ImageScrollViewer")
            If vm Is Nothing OrElse scrollViewer Is Nothing Then Return

            If isRightButton AndAlso CanPanImage(vm, scrollViewer) Then
                _isPanningImage = True
                _panStartPoint = e.GetPosition(scrollViewer)
                _panStartOffset = scrollViewer.Offset
                If TypeOf sender Is Control Then
                    e.Pointer.Capture(DirectCast(sender, Control))
                Else
                    e.Pointer.Capture(Me)
                End If
                e.Handled = True
                Return
            End If

            If isRightButton Then
                e.Handled = True
                Return
            End If

            If CanCropDrag(vm) Then
                Dim image = Me.FindControl(Of Image)("MainImage")
                If image Is Nothing OrElse image.Bounds.Width <= 0 OrElse image.Bounds.Height <= 0 Then Return
                _cropDragStartNorm = NormalizeImagePoint(e.GetPosition(image), image.Bounds.Size)
                _cropDragCurrentNorm = _cropDragStartNorm
                _isCropDragging = True
                _cropDragMoved = False
                If TypeOf sender Is Control Then
                    e.Pointer.Capture(DirectCast(sender, Control))
                Else
                    e.Pointer.Capture(Me)
                End If
                e.Handled = True
            End If
        End Sub

        Private Sub OnImageDoubleTapped(sender As Object, e As TappedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not vm.CanEdit Then Return
            ' Der Handler haengt am Panel der Buehne (der Zuschneide-Zug faengt dort den Zeiger,
            ' am Bild kam der Doppelklick nicht mehr an). Ob das BILD gemeint war, entscheidet
            ' deshalb die Geometrie - ein Doppelklick ins Leere oeffnet weiterhin nichts.
            Dim image = Me.FindControl(Of Image)("MainImage")
            If image Is Nothing Then Return
            Dim position = e.GetPosition(image)
            If position.X < 0 OrElse position.Y < 0 OrElse
               position.X > image.Bounds.Width OrElse position.Y > image.Bounds.Height Then Return
            EndPanning()
            vm.EditCommand.Execute(Nothing)
            e.Handled = True
        End Sub

        Private Sub OnImagePointerMoved(sender As Object, e As PointerEventArgs)
            UpdateMousePositionText(e)

            If _isPanningImage Then
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("ImageScrollViewer")
                If scrollViewer Is Nothing Then Return

                Dim currentPoint = e.GetPosition(scrollViewer)
                Dim delta = currentPoint - _panStartPoint
                Dim targetOffset = New Vector(_panStartOffset.X - delta.X, _panStartOffset.Y - delta.Y)
                scrollViewer.Offset = ClampOffset(scrollViewer, targetOffset)
                e.Handled = True
                Return
            End If

            If _isCropDragging Then
                Dim image = Me.FindControl(Of Image)("MainImage")
                If image Is Nothing Then Return
                Dim rawPoint = e.GetPosition(image)
                _cropDragCurrentNorm = NormalizeImagePoint(rawPoint, image.Bounds.Size)
                If Not _cropDragMoved Then
                    Dim startPixel = New Point(_cropDragStartNorm.X * image.Bounds.Width, _cropDragStartNorm.Y * image.Bounds.Height)
                    Dim delta = rawPoint - startPixel
                    If Math.Abs(delta.X) > CropDragMinPixels OrElse Math.Abs(delta.Y) > CropDragMinPixels Then
                        _cropDragMoved = True
                    End If
                End If
                UpdateCropSelectionVisual()
                e.Handled = True
            End If
        End Sub

        Private Sub OnImagePointerReleased(sender As Object, e As PointerReleasedEventArgs)
            If _isCropDragging Then
                Dim wasMoved = _cropDragMoved
                Dim startNorm = _cropDragStartNorm
                Dim endNorm = _cropDragCurrentNorm
                _isCropDragging = False
                _cropDragMoved = False
                HideCropSelectionVisual()
                e.Pointer.Capture(Nothing)

                If wasMoved Then
                    Dim vm = GetVm()
                    If vm IsNot Nothing Then
                        Dim left = Math.Min(startNorm.X, endNorm.X) * 100.0
                        Dim right = (1.0 - Math.Max(startNorm.X, endNorm.X)) * 100.0
                        Dim top = Math.Min(startNorm.Y, endNorm.Y) * 100.0
                        Dim bottom = (1.0 - Math.Max(startNorm.Y, endNorm.Y)) * 100.0
                        vm.OpenCropInEditor(left, top, right, bottom)
                    End If
                End If
                Return
            End If

            EndPanning()
            e.Pointer.Capture(Nothing)
        End Sub

        Private Sub OnImagePointerCaptureLost(sender As Object, e As PointerCaptureLostEventArgs)
            EndPanning()
            If _isCropDragging Then
                _isCropDragging = False
                _cropDragMoved = False
                HideCropSelectionVisual()
            End If
        End Sub

        Private Function CanCropDrag(vm As ViewerViewModel) As Boolean
            Return vm IsNot Nothing AndAlso vm.CurrentImage IsNot Nothing AndAlso vm.CanEdit AndAlso
                   vm.RotationAngle = 0 AndAlso vm.ScaleX = 1.0
        End Function

        ''' <summary>Läuft unabhängig von Pan/Crop-Dragging bei jeder Mausbewegung über der Bühne, damit
        ''' die Bildpixel-Koordinate in der Fußleiste immer aktuell ist. Die Zieh-Ereignisse hängen am
        ''' Panel um das Bild - AUSSERHALB des Bildes gibt es keine Bildkoordinate, dort bleibt die
        ''' Fußleiste leer statt geklemmte Randwerte zu zeigen.</summary>
        Private Sub UpdateMousePositionText(e As PointerEventArgs)
            Dim vm = GetVm()
            Dim image = Me.FindControl(Of Image)("MainImage")
            If vm Is Nothing OrElse image Is Nothing OrElse vm.CurrentImage Is Nothing Then Return
            Dim position = e.GetPosition(image)
            If position.X < 0 OrElse position.Y < 0 OrElse
               position.X > image.Bounds.Width OrElse position.Y > image.Bounds.Height Then
                vm.MousePositionText = ""
                Return
            End If
            Dim norm = NormalizeImagePoint(position, image.Bounds.Size)
            Dim px = CInt(norm.X * vm.CurrentImage.PixelSize.Width)
            Dim py = CInt(norm.Y * vm.CurrentImage.PixelSize.Height)
            vm.MousePositionText = $"{px}, {py}"
        End Sub

        Private Sub OnImagePointerExited(sender As Object, e As PointerEventArgs)
            Dim vm = GetVm()
            If vm IsNot Nothing Then vm.MousePositionText = ""
        End Sub

        Private Function NormalizeImagePoint(p As Point, size As Size) As Point
            If size.Width <= 0 OrElse size.Height <= 0 Then Return New Point(0, 0)
            Dim nx = Math.Max(0, Math.Min(1, p.X / size.Width))
            Dim ny = Math.Max(0, Math.Min(1, p.Y / size.Height))
            Return New Point(nx, ny)
        End Function

        Private Sub UpdateCropSelectionVisual()
            Dim image = Me.FindControl(Of Image)("MainImage")
            Dim canvas = Me.FindControl(Of Canvas)("CropSelectionCanvas")
            Dim rect = Me.FindControl(Of Border)("CropSelectionRect")
            If image Is Nothing OrElse canvas Is Nothing OrElse rect Is Nothing Then Return

            Dim origin = image.TranslatePoint(New Point(0, 0), canvas)
            If Not origin.HasValue Then Return

            Dim size = image.Bounds.Size
            Dim x1 = origin.Value.X + Math.Min(_cropDragStartNorm.X, _cropDragCurrentNorm.X) * size.Width
            Dim y1 = origin.Value.Y + Math.Min(_cropDragStartNorm.Y, _cropDragCurrentNorm.Y) * size.Height
            Dim x2 = origin.Value.X + Math.Max(_cropDragStartNorm.X, _cropDragCurrentNorm.X) * size.Width
            Dim y2 = origin.Value.Y + Math.Max(_cropDragStartNorm.Y, _cropDragCurrentNorm.Y) * size.Height

            Canvas.SetLeft(rect, x1)
            Canvas.SetTop(rect, y1)
            rect.Width = Math.Max(0, x2 - x1)
            rect.Height = Math.Max(0, y2 - y1)
            rect.IsVisible = True

            Dim badge = Me.FindControl(Of TextBlock)("CropSelectionSizeBadgeText")
            Dim vm = GetVm()
            If badge IsNot Nothing AndAlso vm IsNot Nothing AndAlso vm.CurrentImage IsNot Nothing Then
                Dim pixelWidth = CInt(Math.Round(Math.Max(1, Math.Abs(_cropDragCurrentNorm.X - _cropDragStartNorm.X) * vm.CurrentImage.PixelSize.Width)))
                Dim pixelHeight = CInt(Math.Round(Math.Max(1, Math.Abs(_cropDragCurrentNorm.Y - _cropDragStartNorm.Y) * vm.CurrentImage.PixelSize.Height)))
                badge.Text = $"{pixelWidth} × {pixelHeight} px"
            End If
        End Sub

        Private Sub HideCropSelectionVisual()
            Dim rect = Me.FindControl(Of Border)("CropSelectionRect")
            If rect IsNot Nothing Then rect.IsVisible = False
        End Sub

        Private Sub EndPanning()
            _isPanningImage = False
        End Sub

        Public Sub OnImageViewportSizeChanged(sender As Object, e As SizeChangedEventArgs)
            Dim vm = GetVm()
            If vm IsNot Nothing Then
                vm.SetImageViewportSize(e.NewSize.Width, e.NewSize.Height)
            End If
            ApplyImageFitMode()
        End Sub

        Public Sub OnFullscreenViewportSizeChanged(sender As Object, e As SizeChangedEventArgs)
            ApplyFullscreenImageMode()
        End Sub

        ''' Das ViewerViewModel lebt über die ganze Sitzung, diese View wird bei jedem Moduswechsel neu
        ''' gebaut. Beim Verwerfen feuert kein DataContextChanged, deshalb hängt das Abo am Entfernen aus
        ''' dem visuellen Baum - sonst bliebe je Betrachter-Besuch eine tote View am ViewModel.
        ' ── Vergleichsmodus: Fokus, gemeinsamer Zoom, gespiegelter Ausschnitt ────

        ''' <summary>Sperre gegen Rueckkopplung beim Spiegeln des Ausschnitts: das Setzen der einen
        ''' Flaeche loest deren ScrollChanged aus, das sonst sofort wieder zurueckschriebe.</summary>
        Private _mirrorsRegion As Boolean

        Private Sub OnComparePanePressed(sender As Object, e As PointerPressedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not vm.IsCompareMode Then Return
            Dim sv = TryCast(sender, ScrollViewer)
            If sv Is Nothing Then Return
            vm.FocusedComparePane = If(TryCast(sv.Tag, String) = "1", 1, 0)
            ' Verschoben wird mit LINKER oder RECHTER Taste - im Einzelbild zieht die rechte, und
            ' der Vergleich soll sich nicht anders anfuehlen. Rechts+Mausrad bleibt Zoomen, das
            ' laeuft ueber den Rad-Handler und beisst sich damit nicht.
            Dim eigenschaften = e.GetCurrentPoint(sv).Properties
            If Not eigenschaften.IsLeftButtonPressed AndAlso Not eigenschaften.IsRightButtonPressed Then Return
            _compareZiehtScroll = sv
            _compareZiehtVon = e.GetPosition(sv)
            _compareZiehtOffset = sv.Offset
            e.Pointer.Capture(sv)
        End Sub

        ''' <summary>Die Groesse EINER Vergleichsflaeche an das ViewModel melden. Die einzelne
        ''' Bildflaeche ist waehrend des Vergleichs ausgeblendet und meldet Groesse null - ohne diese
        ''' Meldung rechnete das Einpassen mit einer Flaeche von null und lieferte stumpf 100 %.</summary>
        Private Sub OnCompareViewportSizeChanged(sender As Object, e As SizeChangedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            vm.SetCompareViewportSize(e.NewSize.Width, e.NewSize.Height)
            ApplyCompareFitMode()
        End Sub

        ''' <summary>Doppelklick auf eine Vergleichsflaeche oeffnet DEREN Bild im Editor - dasselbe,
        ''' was der Doppelklick in der Einzelansicht tut. Ohne das war der Vergleich die einzige
        ''' Ansicht, aus der man nicht per Doppelklick weiterkam.</summary>
        Private Sub OnComparePaneDoubleTapped(sender As Object, e As TappedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not vm.IsCompareMode Then Return
            Dim sv = TryCast(sender, ScrollViewer)
            If sv Is Nothing Then Return
            ' Ein laufendes Ziehen beenden, sonst haengt der Zeiger-Fang am ScrollViewer, waehrend
            ' der Editor nach vorn kommt.
            _compareZiehtScroll = Nothing
            vm.OpenComparePaneInEditor(If(TryCast(sv.Tag, String) = "1", 1, 0))
            e.Handled = True
        End Sub

        ''' <summary>Ziehen mit der Maus im Vergleich. Ein ScrollViewer kann das von sich aus nicht -
        ''' die einzelne Bildflaeche hat dafuer eigene Zeiger-Handler, und der Vergleich braucht sie
        ''' genauso. Der gespiegelte Ausschnitt folgt von selbst ueber ScrollChanged.</summary>
        Private _compareZiehtVon As Point
        Private _compareZiehtOffset As Vector
        Private _compareZiehtScroll As ScrollViewer

        Private Sub OnComparePaneMoved(sender As Object, e As PointerEventArgs)
            If _compareZiehtScroll Is Nothing Then Return
            Dim jetzt = e.GetPosition(_compareZiehtScroll)
            Dim dx = jetzt.X - _compareZiehtVon.X
            Dim dy = jetzt.Y - _compareZiehtVon.Y
            Dim maxX = Math.Max(0, _compareZiehtScroll.Extent.Width - _compareZiehtScroll.Viewport.Width)
            Dim maxY = Math.Max(0, _compareZiehtScroll.Extent.Height - _compareZiehtScroll.Viewport.Height)
            _compareZiehtScroll.Offset = New Vector(
                Math.Min(Math.Max(_compareZiehtOffset.X - dx, 0), maxX),
                Math.Min(Math.Max(_compareZiehtOffset.Y - dy, 0), maxY))
            e.Handled = True
        End Sub

        Private Sub OnComparePaneReleased(sender As Object, e As RoutedEventArgs)
            _compareZiehtScroll = Nothing
        End Sub


        Private Sub LinkComparePanes()
            Dim left = Me.FindControl(Of ScrollViewer)("CompareLeftScroll")
            Dim right = Me.FindControl(Of ScrollViewer)("CompareRightScroll")
            If left Is Nothing OrElse right Is Nothing Then Return
            RemoveHandler left.ScrollChanged, AddressOf OnCompareScrollChanged
            RemoveHandler right.ScrollChanged, AddressOf OnCompareScrollChanged
            AddHandler left.ScrollChanged, AddressOf OnCompareScrollChanged
            AddHandler right.ScrollChanged, AddressOf OnCompareScrollChanged
        End Sub

        ''' <summary>Der Ausschnitt wird als PIXEL-Offset gespiegelt, nicht als Anteil: zwei Aufnahmen
        ''' derselben Szene liegen damit exakt uebereinander, und genau dafuer ist der Vergleich da.
        ''' Bei sehr verschiedenen Bildgroessen laeuft es auseinander - das ist der bewusste Preis.</summary>
        Private Sub OnCompareScrollChanged(sender As Object, e As ScrollChangedEventArgs)
            If _mirrorsRegion Then Return
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not vm.IsCompareMode Then Return
            ' Entkoppelt scrollt jede Flaeche fuer sich - fuer Aufnahmen, die nicht deckungsgleich
            ' sind (anderer Ausschnitt, anderes Objektiv), wo das Spiegeln die zweite Flaeche vom
            ' interessanten Bildteil wegzieht.
            If Not vm.IsCompareViewportLinked Then Return
            Dim source = TryCast(sender, ScrollViewer)
            If source Is Nothing Then Return
            Dim left = Me.FindControl(Of ScrollViewer)("CompareLeftScroll")
            Dim right = Me.FindControl(Of ScrollViewer)("CompareRightScroll")
            Dim target = If(ReferenceEquals(source, left), right, left)
            If target Is Nothing Then Return
            Dim neu = New Vector(
                Math.Min(Math.Max(source.Offset.X, 0), Math.Max(0, target.Extent.Width - target.Viewport.Width)),
                Math.Min(Math.Max(source.Offset.Y, 0), Math.Max(0, target.Extent.Height - target.Viewport.Height)))
            If Math.Abs(target.Offset.X - neu.X) < 0.5 AndAlso Math.Abs(target.Offset.Y - neu.Y) < 0.5 Then Return
            _mirrorsRegion = True
            Try
                target.Offset = neu
            Finally
                _mirrorsRegion = False
            End Try
        End Sub

        ''' <summary>Beide Vergleichsbilder auf denselben Zoom bringen - dieselbe Rechnung wie fuer
        ''' die einzelne Flaeche (Bildgroesse mal ZoomLevel), damit ein Wert fuer beide gilt.</summary>
        Private Sub ApplyCompareFitMode()
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not vm.IsCompareMode Then Return
            Dim zoom = Math.Max(0.05, vm.ZoomLevel)

            Dim setze = Sub(imageName As String, source As Avalonia.Media.Imaging.Bitmap)
                            Dim imageControl = Me.FindControl(Of Image)(imageName)
                            If imageControl Is Nothing Then Return
                            If source Is Nothing Then
                                imageControl.Width = Double.NaN
                                imageControl.Height = Double.NaN
                                Return
                            End If
                            imageControl.Width = Math.Round(source.Size.Width * zoom, MidpointRounding.AwayFromZero)
                            imageControl.Height = Math.Round(source.Size.Height * zoom, MidpointRounding.AwayFromZero)
                            imageControl.MaxWidth = Double.PositiveInfinity
                            imageControl.MaxHeight = Double.PositiveInfinity
                        End Sub
            setze("CompareLeftImage", vm.CompareLeftImage)
            setze("CompareRightImage", vm.CompareRightImage)
        End Sub

        Protected Overrides Sub OnAttachedToVisualTree(e As VisualTreeAttachmentEventArgs)
            MyBase.OnAttachedToVisualTree(e)
            LinkComparePanes()
            _isAttached = True
            RebindViewModel()
            ApplyVideoLayout()
            ' Wie die Galerie: ohne Fokus im eigenen Teilbaum sieht diese View KEINE Taste - beim
            ' Wechsel aus der Galerie waren Strg+L/R, Strg+I/E und die Pfeiltasten deshalb tot, bis
            ' man ins Bild oder in den Filmstreifen geklickt hat.
            Dispatcher.UIThread.Post(Sub() Me.Focus(), DispatcherPriority.Background)
            Dispatcher.UIThread.Post(Sub()
                                         ApplyImageFitMode()
                                         _filmstripController.ScrollToCurrent()
                                         UpdateActiveVideoView()
                                     End Sub, DispatcherPriority.Loaded)
        End Sub

        Protected Overrides Sub OnDetachedFromVisualTree(e As VisualTreeAttachmentEventArgs)
            MyBase.OnDetachedFromVisualTree(e)
            _isAttached = False
            UnsubscribeViewModel()
        End Sub

        Private Sub RebindViewModel()
            UnsubscribeViewModel()
            If Not _isAttached Then Return
            _subscribedVm = GetVm()
            If _subscribedVm IsNot Nothing Then
                AddHandler _subscribedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If
        End Sub

        Private Sub UnsubscribeViewModel()
            If _subscribedVm Is Nothing Then Return
            RemoveHandler _subscribedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            _subscribedVm = Nothing
        End Sub

        Private Sub HandleDataContextChanged(sender As Object, e As EventArgs)
            RebindViewModel()

            _filmstripController.Reset()
            ApplyImageFitMode()
            _filmstripController.ScrollToCurrent()
            ApplyVideoLayout()
            UpdateActiveVideoView()
            UpdateVideoPlaybackBarVisibility()
        End Sub

        Private Sub OnViewModelPropertyChanged(sender As Object, e As PropertyChangedEventArgs)
            If e.PropertyName = NameOf(ViewerViewModel.IsFitToWindow) OrElse
               e.PropertyName = NameOf(ViewerViewModel.CurrentImage) OrElse
               e.PropertyName = NameOf(ViewerViewModel.ZoomLevel) OrElse
               e.PropertyName = NameOf(ViewerViewModel.RotationAngle) Then
                ApplyImageFitMode()
                ApplyFullscreenImageMode()
            End If

            ' Der Vergleich teilt sich denselben ZoomLevel - beide Flaechen muessen also bei jeder
            ' Zoomaenderung und bei jedem neu geladenen Vergleichsbild neu bemessen werden.
            If e.PropertyName = NameOf(ViewerViewModel.ZoomLevel) OrElse
               e.PropertyName = NameOf(ViewerViewModel.IsCompareMode) OrElse
               e.PropertyName = NameOf(ViewerViewModel.CompareLeftImage) OrElse
               e.PropertyName = NameOf(ViewerViewModel.CompareRightImage) Then
                ApplyCompareFitMode()
            End If

            If e.PropertyName = NameOf(ViewerViewModel.IsFullscreenMode) Then
                ApplyFullscreenImageMode()
                ApplyVideoLayout()
                ResetFullscreenVideoControlsVisibility()
            End If

            ' ShowVideoSurface kippt beim Videoende (Fläche verschwindet) und beim erneuten Abspielen
            ' (Fläche kommt zurück) - im zweiten Fall muss der Player an das neu erzeugte native Fenster.
            If e.PropertyName = NameOf(ViewerViewModel.IsVideoFile) OrElse
               e.PropertyName = NameOf(ViewerViewModel.ShowVideoSurface) Then
                UpdateActiveVideoView()
                ResetFullscreenVideoControlsVisibility()
            End If

            If e.PropertyName = NameOf(ViewerViewModel.CurrentFilmstripIndex) Then
                _filmstripController.ScrollToCurrent()
            End If

            If e.PropertyName = NameOf(ViewerViewModel.IsInfoSidebarVisible) Then
                UpdateInfoSidebarLayoutState()
            End If
        End Sub

        Private Sub NoteFullscreenVideoControlsActivity()
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not vm.IsFullscreenMode Then
                StopFullscreenVideoControlTimers()
                _fullscreenVideoControlsVisible = True
                UpdateVideoPlaybackBarVisibility()
                Return
            End If

            _fullscreenVideoControlsVisible = True
            UpdateVideoPlaybackBarVisibility()
            _fullscreenVideoControlsHideTimer.Stop()
            If vm.IsVideoFile AndAlso vm.IsVideoPlaybackAvailable Then
                _fullscreenVideoControlsHideTimer.Start()
                EnsureFullscreenVideoCursorPolling()
            End If
        End Sub

        Private Sub ResetFullscreenVideoControlsVisibility()
            StopFullscreenVideoControlTimers()
            _fullscreenVideoControlsVisible = True
            UpdateVideoPlaybackBarVisibility()

            Dim vm = GetVm()
            If vm IsNot Nothing AndAlso vm.IsFullscreenMode AndAlso vm.IsVideoFile AndAlso vm.IsVideoPlaybackAvailable Then
                _fullscreenVideoControlsHideTimer.Start()
                EnsureFullscreenVideoCursorPolling()
            End If
        End Sub

        Private Sub OnFullscreenVideoControlsIdle(sender As Object, e As EventArgs)
            _fullscreenVideoControlsHideTimer.Stop()
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not vm.IsFullscreenMode Then Return

            _fullscreenVideoControlsVisible = False
            UpdateVideoPlaybackBarVisibility()
        End Sub

        Private Sub OnFullscreenVideoCursorPoll(sender As Object, e As EventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not vm.IsFullscreenMode OrElse Not vm.IsVideoFile OrElse Not vm.IsVideoPlaybackAvailable Then
                StopFullscreenVideoControlTimers()
                Return
            End If

            Dim current As PixelPoint
            If Not NativePointerService.TryGetCursorPosition(current) Then Return
            If Not _lastFullscreenCursorPosition.HasValue Then
                _lastFullscreenCursorPosition = current
                Return
            End If

            Dim previous = _lastFullscreenCursorPosition.Value
            If Math.Abs(current.X - previous.X) <= 1 AndAlso Math.Abs(current.Y - previous.Y) <= 1 Then Return
            _lastFullscreenCursorPosition = current
            NoteFullscreenVideoControlsActivity()
        End Sub

        Private Sub EnsureFullscreenVideoCursorPolling()
            If Not _lastFullscreenCursorPosition.HasValue Then
                Dim current As PixelPoint
                If NativePointerService.TryGetCursorPosition(current) Then
                    _lastFullscreenCursorPosition = current
                End If
            End If

            If Not _fullscreenVideoCursorPollTimer.IsEnabled Then
                _fullscreenVideoCursorPollTimer.Start()
            End If
        End Sub

        Private Sub StopFullscreenVideoControlTimers()
            _fullscreenVideoControlsHideTimer.Stop()
            _fullscreenVideoCursorPollTimer.Stop()
            _lastFullscreenCursorPosition = Nothing
        End Sub

        Private Sub UpdateVideoPlaybackBarVisibility()
            Dim vm = GetVm()
            Dim bar = Me.FindControl(Of Border)("VideoPlaybackBar")
            If bar Is Nothing Then Return

            Dim available = vm IsNot Nothing AndAlso vm.IsVideoFile AndAlso vm.IsVideoPlaybackAvailable
            Dim visibleInMode = vm Is Nothing OrElse Not vm.IsFullscreenMode OrElse _fullscreenVideoControlsVisible
            bar.IsVisible = available AndAlso visibleInMode
        End Sub

        ''' Positioniert das einzige VideoOverlay-Grid je nach Vollbild-Status: im Fenstermodus
        ''' auf die Content-Zelle (Grid.Row=1/Column=0) mit demselben 88/0/80/0-Rand wie das
        ''' Bild, im Vollbildmodus über das gesamte Fenster (RowSpan=3/ColumnSpan=2, randlos) -
        ''' rein per Layout, ohne das VideoView selbst je ab- und wieder anzuhängen. Dadurch
        ''' bleibt sein natives Fenster-Handle über Vollbild-Wechsel hinweg unangetastet.
        Private Sub ApplyVideoLayout()
            Dim vm = GetVm()
            Dim overlay = Me.FindControl(Of Grid)("VideoOverlay")
            If overlay Is Nothing Then Return

            If vm IsNot Nothing AndAlso vm.IsFullscreenMode Then
                Grid.SetRow(overlay, 0)
                Grid.SetColumn(overlay, 0)
                Grid.SetRowSpan(overlay, 3)
                Grid.SetColumnSpan(overlay, 2)
                overlay.Margin = New Thickness(0)
            Else
                Grid.SetRow(overlay, 1)
                Grid.SetColumn(overlay, 0)
                Grid.SetRowSpan(overlay, 1)
                Grid.SetColumnSpan(overlay, 1)
                overlay.Margin = New Thickness(88, 0, 80, 0)
            End If
        End Sub

        ''' Weist den von ViewerViewModel gehaltenen MediaPlayer dem einzigen VideoOverlay zu
        ''' (bzw. leert es), wenn sich IsVideoFile ändert - der Vollbild-Wechsel selbst löst dies
        ''' NICHT mehr aus (siehe ApplyVideoLayout), wodurch das native Fenster-Handle über
        ''' Vollbild-Wechsel und Video-zu-Video-Navigation hinweg bestehen bleibt.
        Private _pendingVideoAttachHandler As EventHandler

        Private Sub UpdateActiveVideoView()
            Dim vm = GetVm()
            Dim videoView = Me.FindControl(Of MpvVideoView)("TheVideoView")
            If videoView Is Nothing Then Return

            If _pendingVideoAttachHandler IsNot Nothing Then
                RemoveHandler videoView.LayoutUpdated, _pendingVideoAttachHandler
                _pendingVideoAttachHandler = Nothing
            End If

            Dim isVideoActive = vm IsNot Nothing AndAlso vm.IsVideoFile AndAlso vm.IsVideoPlaybackAvailable
            If Not isVideoActive Then
                videoView.Player = Nothing
                Return
            End If

            AttachVideoPlayer(videoView, vm.VideoMediaPlayer, vm)
        End Sub

        ''' Das native Fenster-Handle eines MpvVideoView-Controls entsteht erst, sobald für es
        ''' tatsächlich ein Layout-Durchlauf stattgefunden hat (insbesondere direkt nachdem sein
        ''' Container durch einen Sichtbarkeits-Wechsel gerade erst sichtbar wurde). MediaPlayer
        ''' vorher zuzuweisen kann "ins Leere" binden (Ton läuft weiter, kein Bild) oder mpv
        ''' dazu bringen, mangels Ausgabeziel kurz ein eigenes Fenster zu erzeugen. Statt einer
        ''' geschätzten Dispatcher-Verzögerung wird hier direkt auf LayoutUpdated gewartet, bis
        ''' das Control tatsächlich eine reale Größe hat.
        Private Sub AttachVideoPlayer(target As MpvVideoView, mediaPlayer As MpvPlayer, vm As ViewerViewModel)
            If target.Bounds.Width > 0 AndAlso target.Bounds.Height > 0 Then
                target.Player = mediaPlayer
                StartPendingVideoAutoplayAfterHostReady(target, mediaPlayer, vm)
                Return
            End If

            Dim handler As EventHandler = Nothing
            handler = Sub(s As Object, e As EventArgs)
                          If target.Bounds.Width <= 0 OrElse target.Bounds.Height <= 0 Then Return
                          RemoveHandler target.LayoutUpdated, handler
                          If Object.ReferenceEquals(_pendingVideoAttachHandler, handler) Then _pendingVideoAttachHandler = Nothing
                          target.Player = mediaPlayer
                          StartPendingVideoAutoplayAfterHostReady(target, mediaPlayer, vm)
                      End Sub
            _pendingVideoAttachHandler = handler
            AddHandler target.LayoutUpdated, handler
        End Sub

        Private Async Sub StartPendingVideoAutoplayAfterHostReady(target As MpvVideoView, mediaPlayer As MpvPlayer, vm As ViewerViewModel)
            Try
                Await Task.Delay(180)
                If target Is Nothing OrElse vm Is Nothing Then Return
                If Not Object.ReferenceEquals(target.Player, mediaPlayer) Then Return
                If Not Object.ReferenceEquals(vm.VideoMediaPlayer, mediaPlayer) Then Return
                If Not vm.IsVideoFile Then Return
                vm.StartPendingVideoAutoplay()
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("ViewerView.StartPendingVideoAutoplayAfterHostReady", ex)
            End Try
        End Sub

        Public Sub OnVideoViewTapped(sender As Object, e As TappedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse vm.PlayPauseVideoCommand Is Nothing Then Return
            If vm.PlayPauseVideoCommand.CanExecute(Nothing) Then vm.PlayPauseVideoCommand.Execute(Nothing)
        End Sub

        Public Sub OnVideoSeekSliderPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            CommitVideoSeek(sender)
        End Sub

        Public Sub OnVideoSeekSliderPointerPressed(sender As Object, e As PointerPressedEventArgs)
            If Not e.GetCurrentPoint(TryCast(sender, Control)).Properties.IsLeftButtonPressed Then Return
            Dispatcher.UIThread.Post(Sub() CommitVideoSeek(sender), DispatcherPriority.Input)
        End Sub

        Private Sub CommitVideoSeek(sender As Object)
            Dim slider = TryCast(sender, RoundSlider)
            Dim vm = GetVm()
            If slider Is Nothing OrElse vm Is Nothing OrElse vm.SeekVideoCommand Is Nothing Then Return
            If vm.SeekVideoCommand.CanExecute(slider.Value) Then vm.SeekVideoCommand.Execute(slider.Value)
        End Sub

        Private Sub UpdateInfoSidebarLayoutState()
            Dim vm = GetVm()
            Dim root = Me.FindControl(Of Grid)("ViewerRootGrid")
            Dim sidebar = Me.FindControl(Of Border)("ViewerInfoSidebarBorder")
            If root Is Nothing Then Return

            If root.ColumnDefinitions.Count >= 2 Then
                root.ColumnDefinitions(1).Width = If(vm IsNot Nothing AndAlso vm.IsInfoSidebarVisible, New GridLength(300), New GridLength(0))
            End If

            If sidebar IsNot Nothing Then
                sidebar.IsVisible = vm IsNot Nothing AndAlso vm.IsInfoSidebarVisible
            End If
        End Sub

        Private Sub ApplyImageFitMode()
            Dim vm = GetVm()
            Dim image = Me.FindControl(Of Image)("MainImage")
            Dim background = Me.FindControl(Of Border)("MainImageBackgroundBorder")
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("ImageScrollViewer")
            If vm Is Nothing OrElse image Is Nothing OrElse scrollViewer Is Nothing Then Return

            If vm.CurrentImage Is Nothing Then Return

            Dim displayZoom = Math.Max(0.05, vm.ZoomLevel)
            ' Auf ganze Geräte-Pixel runden: Border (Schachbrettmuster) und Image werden sonst mit
            ' fraktionalen Werten unabhängig voneinander gerundet/gesnappt, was am rechten/unteren
            ' Rand einen ~1-2px durchscheinenden Schachbrett-Rand verursachen kann, auch bei
            ' komplett opaken Bildern.
            Dim imageWidth = Math.Round(vm.CurrentImage.Size.Width * displayZoom, MidpointRounding.AwayFromZero)
            Dim imageHeight = Math.Round(vm.CurrentImage.Size.Height * displayZoom, MidpointRounding.AwayFromZero)

            image.Width = imageWidth
            image.Height = imageHeight
            image.MaxWidth = Double.PositiveInfinity
            image.MaxHeight = Double.PositiveInfinity
            If background IsNot Nothing Then
                background.Width = Math.Max(0, imageWidth - 2.0)
                background.Height = Math.Max(0, imageHeight - 2.0)
                background.MaxWidth = Double.PositiveInfinity
                background.MaxHeight = Double.PositiveInfinity
            End If
            If vm.IsZoomFitActive Then
                scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                scrollViewer.Offset = New Vector(0, 0)
            Else
                scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            End If

            If Not CanPanImage(vm, scrollViewer) Then
                _isPanningImage = False
            End If
        End Sub

        Private Function CanPanImage(vm As ViewerViewModel, scrollViewer As ScrollViewer) As Boolean
            If vm Is Nothing OrElse vm.CurrentImage Is Nothing OrElse scrollViewer Is Nothing Then Return False
            Dim displayZoom = Math.Max(0.05, vm.ZoomLevel)
            Dim imageWidth = vm.CurrentImage.Size.Width * displayZoom
            Dim imageHeight = vm.CurrentImage.Size.Height * displayZoom
            Return imageWidth > scrollViewer.Bounds.Width OrElse imageHeight > scrollViewer.Bounds.Height
        End Function

        Private Function ClampOffset(scrollViewer As ScrollViewer, offset As Vector) As Vector
            Dim maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Bounds.Width)
            Dim maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Bounds.Height)
            Dim clampedX = Math.Max(0, Math.Min(offset.X, maxX))
            Dim clampedY = Math.Max(0, Math.Min(offset.Y, maxY))
            Return New Vector(clampedX, clampedY)
        End Function

        Private Sub ApplyFullscreenImageMode()
            Dim vm = GetVm()
            Dim image = Me.FindControl(Of Image)("FullscreenImage")
            Dim background = Me.FindControl(Of Border)("FullscreenImageBackgroundBorder")
            Dim viewport = Me.FindControl(Of Grid)("FullscreenViewport")
            If vm Is Nothing OrElse image Is Nothing OrElse viewport Is Nothing OrElse vm.CurrentImage Is Nothing Then Return

            Dim vw = viewport.Bounds.Width
            Dim vh = viewport.Bounds.Height
            If vw <= 0 OrElse vh <= 0 Then Return

            Dim iw = vm.CurrentImage.Size.Width
            Dim ih = vm.CurrentImage.Size.Height

            If iw <= vw AndAlso ih <= vh Then
                image.Stretch = Avalonia.Media.Stretch.None
                image.Width = iw
                image.Height = ih
                image.MaxWidth = Double.PositiveInfinity
                image.MaxHeight = Double.PositiveInfinity
            Else
                ' Auf die tatsächliche Uniform-skalierte Größe setzen statt auf die volle
                ' Viewport-Größe - sonst sizt sich der umgebende Border (Transparenz-Hintergrund/
                ' Schachbrettmuster) auf den vollen Viewport, während das Bild darin per Stretch
                ' kleiner (letterboxed) gerendert wird. Das ließ das Schachbrettmuster auch bei
                ' völlig undurchsichtigen Bildern in den Letterbox-/Pillarbox-Rändern durchscheinen.
                Dim scale = Math.Min(vw / iw, vh / ih)
                image.Stretch = Avalonia.Media.Stretch.Uniform
                image.Width = Math.Round(iw * scale, MidpointRounding.AwayFromZero)
                image.Height = Math.Round(ih * scale, MidpointRounding.AwayFromZero)
                image.MaxWidth = vw
                image.MaxHeight = vh
            End If
            If background IsNot Nothing Then
                background.Width = Math.Max(0, image.Width - 2.0)
                background.Height = Math.Max(0, image.Height - 2.0)
                background.MaxWidth = image.MaxWidth
                background.MaxHeight = image.MaxHeight
            End If
        End Sub

    End Class

End Namespace
