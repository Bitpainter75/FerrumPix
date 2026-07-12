Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform
Imports Avalonia.Threading
Imports Avalonia.VisualTree
Imports FerrumPix.Services
Imports FerrumPix.ViewModels
Imports System.Linq

Namespace Views

    Public Class MainWindow
        Inherits Window

        Private Shared ReadOnly HiddenCursor As New Cursor(StandardCursorType.None)
        Private _isRestoringPlacement As Boolean = False
        Private _hasRestoredPlacement As Boolean = False
        Private _allowWindowClose As Boolean = False

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            ApplyInitialWindowSize()
            Icon = App.AppIcon
            AddHandler DataContextChanged, AddressOf HandleDataContextChanged
            AddHandler Loaded, Sub(s, e) ApplyLocalization()
            AddHandler Opened, Sub(s, e) RestoreWindowPlacement()
            AddHandler Closing, AddressOf HandleWindowClosing
            AddHandler PositionChanged, Sub(s, e) OnWindowPlacementChanged()
            AddHandler SizeChanged, Sub(s, e) OnWindowPlacementChanged()
            AddHandler LocalizationService.LanguageChanged, Sub(s, e) ApplyLocalization()
            Me.AddHandler(InputElement.KeyDownEvent, AddressOf OnWindowKeyDown, RoutingStrategies.Tunnel)
            AddHandler PointerPressed, AddressOf OnWindowPointerPressed
            WireWindowChrome()
        End Sub

        Private Sub ApplyInitialWindowSize()
            Dim settings = AppSettingsService.Load()
            Width = AppSettingsService.NormalizeWindowDimension(settings.MainWindowWidth, 1536)
            Height = AppSettingsService.NormalizeWindowDimension(settings.MainWindowHeight, 1024)
        End Sub

        Private Sub HandleDataContextChanged(sender As Object, e As EventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm IsNot Nothing Then
                AddHandler vm.PropertyChanged, AddressOf OnVmPropertyChanged
                ApplyFullscreenState()
            End If
        End Sub

        Private Sub OnVmPropertyChanged(sender As Object, e As System.ComponentModel.PropertyChangedEventArgs)
            If e.PropertyName = NameOf(MainWindowViewModel.Title) Then
                Return
            End If
            If e.PropertyName = NameOf(MainWindowViewModel.IsFullscreen) Then
                ApplyFullscreenState()
            ElseIf e.PropertyName = NameOf(MainWindowViewModel.IsDialogOpen) Then
                FocusDialog()
                ApplyLocalization()
            ElseIf e.PropertyName = NameOf(MainWindowViewModel.CurrentContent) OrElse
                   e.PropertyName = NameOf(MainWindowViewModel.CurrentMode) Then
                ApplyLocalization()
            End If
        End Sub

        Private Sub RestoreWindowPlacement()
            If _isRestoringPlacement OrElse _hasRestoredPlacement Then Return
            _isRestoringPlacement = True
            Try
                Dim settings = AppSettingsService.Load()
                Dim width = AppSettingsService.NormalizeWindowDimension(settings.MainWindowWidth, 1536)
                Dim height = AppSettingsService.NormalizeWindowDimension(settings.MainWindowHeight, 1024)
                Dim placement = ResolveWindowPlacement(settings.MainWindowLeft, settings.MainWindowTop, width, height)
                Width = placement.Width
                Height = placement.Height
                Position = placement.Position
            Finally
                _isRestoringPlacement = False
                _hasRestoredPlacement = True
            End Try
        End Sub

        Private Function ResolveWindowPlacement(left As Integer, top As Integer, width As Double, height As Double) As (Width As Double, Height As Double, Position As PixelPoint)
            Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
            If topLevel Is Nothing OrElse topLevel.Screens Is Nothing OrElse topLevel.Screens.All Is Nothing Then
                Return (width, height, Position)
            End If

            Dim screen = FindBestScreenForPlacement(left, top, width, height)
            If screen IsNot Nothing AndAlso IsWindowSizeUsableOnScreen(width, height, screen) Then
                Return (width, height, ClampPositionToScreen(left, top, width, height, screen))
            End If

            screen = topLevel.Screens.Primary
            If screen Is Nothing AndAlso topLevel.Screens.All IsNot Nothing Then
                screen = topLevel.Screens.All.FirstOrDefault()
            End If
            If screen Is Nothing Then Return (width, height, Position)

            Dim fallbackSize = ClampSizeToScreen(width, height, screen)
            Return (fallbackSize.Width, fallbackSize.Height, CenterPositionOnScreen(fallbackSize.Width, fallbackSize.Height, screen))
        End Function

        Private Function FindBestScreenForPlacement(left As Integer, top As Integer, width As Double, height As Double) As Screen
            Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
            If topLevel Is Nothing OrElse topLevel.Screens Is Nothing OrElse topLevel.Screens.All Is Nothing Then Return Nothing
            If left = -1 AndAlso top = -1 Then Return Nothing
            If left < -32000 OrElse top < -32000 OrElse width < 200 OrElse height < 200 Then Return Nothing

            Dim rect As New PixelRect(left, top, CInt(Math.Max(1, Math.Round(width))), CInt(Math.Max(1, Math.Round(height))))
            Dim bestScreen As Screen = Nothing
            Dim bestArea = 0
            For Each screen In topLevel.Screens.All
                If screen Is Nothing Then Continue For
                Dim visibleArea = GetIntersectionArea(rect, screen.WorkingArea)
                If visibleArea > bestArea Then
                    bestArea = visibleArea
                    bestScreen = screen
                End If
            Next
            Return If(bestArea > 0, bestScreen, Nothing)
        End Function

        Private Function IsWindowSizeUsableOnScreen(width As Double, height As Double, screen As Screen) As Boolean
            Dim area = screen.WorkingArea
            Return width <= area.Width AndAlso height <= area.Height
        End Function

        Private Function ClampSizeToScreen(width As Double, height As Double, screen As Screen) As (Width As Double, Height As Double)
            Dim area = screen.WorkingArea
            Return (Math.Max(200, Math.Min(width, area.Width)),
                    Math.Max(200, Math.Min(height, area.Height)))
        End Function

        Private Function ClampPositionToScreen(left As Integer, top As Integer, width As Double, height As Double, screen As Screen) As PixelPoint
            Dim area = screen.WorkingArea
            Dim maxLeft = area.X + area.Width - CInt(Math.Round(width))
            Dim maxTop = area.Y + area.Height - CInt(Math.Round(height))
            Return New PixelPoint(CInt(Math.Max(area.X, Math.Min(left, maxLeft))),
                                  CInt(Math.Max(area.Y, Math.Min(top, maxTop))))
        End Function

        Private Function CenterPositionOnScreen(width As Double, height As Double, screen As Screen) As PixelPoint
            Dim area = screen.WorkingArea
            Dim left = CInt(area.X + Math.Max(0, (area.Width - width) / 2.0))
            Dim top = CInt(area.Y + Math.Max(0, (area.Height - height) / 2.0))
            Return New PixelPoint(left, top)
        End Function

        Private Function GetIntersectionArea(a As PixelRect, b As PixelRect) As Integer
            Dim x1 = Math.Max(a.X, b.X)
            Dim y1 = Math.Max(a.Y, b.Y)
            Dim x2 = Math.Min(a.X + a.Width, b.X + b.Width)
            Dim y2 = Math.Min(a.Y + a.Height, b.Y + b.Height)
            If x2 <= x1 OrElse y2 <= y1 Then Return 0
            Return (x2 - x1) * (y2 - y1)
        End Function

        Private Sub OnWindowPlacementChanged()
            If Not _hasRestoredPlacement OrElse _isRestoringPlacement OrElse WindowState <> WindowState.Normal Then Return
            AppSettingsService.SaveMainWindowPlacement(Position.X, Position.Y, Width, Height)
        End Sub

        Private Async Sub HandleWindowClosing(sender As Object, e As WindowClosingEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)

            If _allowWindowClose Then
                vm?.Viewer?.ShutdownVideo()
                If WindowState = WindowState.Normal Then
                    AppSettingsService.SaveMainWindowPlacement(Position.X, Position.Y, Width, Height)
                End If
                ' Einstellungen werden entprellt geschrieben; beim Schließen darf nichts ausstehen.
                AppSettingsService.Flush()
                Return
            End If

            ' Das Video erst abräumen, wenn feststeht, dass wirklich geschlossen wird: bricht der Nutzer
            ' die Nachfrage nach ungespeicherten Änderungen ab, bleibt die App offen - dann darf der Player
            ' nicht schon tot sein.
            If vm IsNot Nothing AndAlso
               vm.CurrentMode = AppMode.Editor AndAlso
               vm.Editor IsNot Nothing AndAlso
               vm.Editor.HasUnsavedChanges AndAlso
               e.CloseReason = WindowCloseReason.WindowClosing Then
                e.Cancel = True
                If Await vm.Editor.ConfirmSaveBeforeLeavingAsync("die Anwendung schließt") Then
                    _allowWindowClose = True
                    Close()
                End If
                Return
            End If

            vm?.Viewer?.ShutdownVideo()
            If WindowState = WindowState.Normal Then
                AppSettingsService.SaveMainWindowPlacement(Position.X, Position.Y, Width, Height)
            End If
            AppSettingsService.Flush()
        End Sub

        Private Sub ApplyLocalization()
            Dispatcher.UIThread.Post(Sub() LocalizationService.ApplyTo(Me), DispatcherPriority.Loaded)
        End Sub

        Private Sub FocusDialog()
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing OrElse Not vm.IsDialogOpen Then Return

            Dim applyFocus = Sub()
                Dim overlay = Me.FindControl(Of DialogOverlayView)("DialogOverlay")
                Dim input = overlay?.FindControl(Of TextBox)("DialogInputBox")
                ' "BatchResizeWidthTextBox" liegt in der eigenen NameScope von BatchResizeDialogView -
                ' FindControl auf dem Overlay findet es nicht, deshalb über die UserControl selbst fokussieren.
                Dim batchResizeView = overlay?.FindControl(Of BatchResizeDialogView)("BatchResizeDialog")
                If input IsNot Nothing AndAlso vm.DialogShowsInput Then
                    input.Focus()
                    input.SelectAll()
                ElseIf batchResizeView IsNot Nothing AndAlso vm.DialogShowsBatchResize Then
                    batchResizeView.FocusWidthField()
                Else
                    Focus()
                End If
            End Sub

            ' Doppelt geplant (Loaded + Background), da ein aus dem Kontextmenü geöffneter Dialog
            ' sonst gegen den Fokus-Rückgabe-Mechanismus des schließenden Popups verlieren kann.
            Avalonia.Threading.Dispatcher.UIThread.Post(applyFocus, Avalonia.Threading.DispatcherPriority.Loaded)
            Avalonia.Threading.Dispatcher.UIThread.Post(applyFocus, Avalonia.Threading.DispatcherPriority.Background)
        End Sub

        ''' KeyboardNavigation.TabNavigation="Cycle" allein reicht nicht, weil sich die fokussierbaren
        ''' Elemente über mehrere verschachtelte UserControls (BatchResizeDialogView usw.) verteilen und
        ''' Avalonia die Tab-Suche dann in die Gallery dahinter eskalieren lässt. Deshalb wird der Fokus
        ''' hier manuell auf die fokussierbaren Nachfahren des DialogOverlay begrenzt.
        Private Sub CycleDialogFocus(backwards As Boolean)
            Dim overlay = Me.FindControl(Of DialogOverlayView)("DialogOverlay")
            If overlay Is Nothing Then Return

            Dim focusables = overlay.GetVisualDescendants().
                OfType(Of Control)().
                Where(Function(c) c.Focusable AndAlso c.IsEffectivelyEnabled AndAlso c.IsEffectivelyVisible).
                ToList()
            If focusables.Count = 0 Then Return

            Dim current = TryCast(TopLevel.GetTopLevel(Me)?.FocusManager?.GetFocusedElement(), Control)
            Dim index = If(current IsNot Nothing, focusables.IndexOf(current), -1)

            Dim nextIndex As Integer
            If index < 0 Then
                nextIndex = If(backwards, focusables.Count - 1, 0)
            Else
                nextIndex = index + If(backwards, -1, 1)
                If nextIndex < 0 Then nextIndex = focusables.Count - 1
                If nextIndex >= focusables.Count Then nextIndex = 0
            End If

            focusables(nextIndex).Focus()
        End Sub

        Private Sub WireWindowChrome()
            WireBtn("MinimizeButton", AddressOf OnMinimizeClick)
            WireBtn("MaximizeButton", AddressOf OnMaximizeClick)
            WireBtn("CloseButton", AddressOf OnCloseClick)

            WireResizeBorder("ResizeTop", WindowEdge.North)
            WireResizeBorder("ResizeBottom", WindowEdge.South)
            WireResizeBorder("ResizeLeft", WindowEdge.West)
            WireResizeBorder("ResizeRight", WindowEdge.East)
            WireResizeBorder("ResizeTopLeft", WindowEdge.NorthWest)
            WireResizeBorder("ResizeTopRight", WindowEdge.NorthEast)
            WireResizeBorder("ResizeBottomLeft", WindowEdge.SouthWest)
            WireResizeBorder("ResizeBottomRight", WindowEdge.SouthEast)

            Dim titleBar = Me.FindControl(Of Grid)("TitleBar")
            If titleBar IsNot Nothing Then
                AddHandler titleBar.PointerPressed, AddressOf TitleBarPointerPressed
            End If
        End Sub

        Private Sub WireBtn(name As String, handler As EventHandler(Of RoutedEventArgs))
            Dim btn = Me.FindControl(Of Button)(name)
            If btn IsNot Nothing Then AddHandler btn.Click, handler
        End Sub

        Private Sub WireResizeBorder(name As String, edge As WindowEdge)
            Dim border = Me.FindControl(Of Border)(name)
            If border Is Nothing Then Return
            AddHandler border.PointerPressed, Sub(s, e)
                If WindowState = WindowState.Maximized Then Return
                If e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then
                    BeginResizeDrag(edge, e)
                    e.Handled = True
                End If
            End Sub
        End Sub

        Private Sub TitleBarPointerPressed(sender As Object, e As PointerPressedEventArgs)
            Dim ctrl = TryCast(e.Source, Control)
            While ctrl IsNot Nothing
                If TypeOf ctrl Is Button OrElse TypeOf ctrl Is TextBox Then Return
                ctrl = TryCast(ctrl.Parent, Control)
            End While

            If e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then BeginMoveDrag(e)
        End Sub

        Private Sub OnMinimizeClick(sender As Object, e As RoutedEventArgs)
            WindowState = WindowState.Minimized
        End Sub

        Private Sub OnMaximizeClick(sender As Object, e As RoutedEventArgs)
            WindowState = If(WindowState = WindowState.Maximized, WindowState.Normal, WindowState.Maximized)
            Dim glyph = Me.FindControl(Of TextBlock)("MaximizeGlyph")
            If glyph IsNot Nothing Then glyph.Text = If(WindowState = WindowState.Maximized, "❐", "□")
        End Sub

        Private Sub OnCloseClick(sender As Object, e As RoutedEventArgs)
            Close()
        End Sub

        Private Sub ApplyFullscreenState()
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing Then Return
            If vm.IsFullscreen Then
                WindowState = WindowState.FullScreen
                Cursor = HiddenCursor
            Else
                Cursor = Nothing
                ' Delay the window resize by one render cycle so the FullscreenViewport
                ' can be hidden first, preventing the image from being briefly clipped
                ' to the normal window bounds while still visible.
                Dispatcher.UIThread.Post(Sub() WindowState = WindowState.Normal, DispatcherPriority.Background)
            End If
        End Sub

        Private Async Sub OnWindowKeyDown(sender As Object, e As KeyEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing Then Return

            If vm.IsDialogOpen Then
                Select Case e.Key
                    Case Key.Escape
                        vm.CancelDialog()
                        e.Handled = True
                    Case Key.Return, Key.Enter
                        vm.ConfirmDialog()
                        e.Handled = True
                    Case Key.Tab
                        CycleDialogFocus(e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                        e.Handled = True
                    Case Else
                        e.Handled = False
                End Select
                Return
            End If

            ' F11 schaltet in jedem Modus um - hier oben im Tunnel, damit es auch im Vollbild greift,
            ' wo die darunterliegenden Ansichten keine Tasten mehr sehen.
            If e.Key = Key.F11 Then
                If vm.IsFullscreen Then vm.ExitFullscreen() Else vm.EnterFullscreen()
                e.Handled = True
                Return
            End If

            If vm.IsFullscreen Then
                Select Case e.Key
                    Case Key.Escape, Key.Space
                        vm.ExitFullscreen()
                        e.Handled = True
                    Case Key.Delete
                        vm.Viewer.DeleteCurrentCommand.Execute(Nothing)
                        e.Handled = True
                    Case Key.F2
                        vm.Viewer.RenameCurrentCommand.Execute(Nothing)
                        e.Handled = True
                End Select
                Return
            End If

            If vm.CurrentMode = AppMode.Viewer Then
                Select Case e.Key
                    Case Key.Delete
                        vm.Viewer.DeleteCurrentCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                    Case Key.F2
                        vm.Viewer.RenameCurrentCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                End Select
            End If

            If vm.CurrentMode = AppMode.Gallery AndAlso e.KeyModifiers.HasFlag(KeyModifiers.Control) AndAlso Not IsTextInputSource(e.Source) Then
                Select Case e.Key
                    Case Key.A
                        vm.Gallery?.SelectAllVisible()
                        e.Handled = True
                        Return
                    ' e.Handled MUSS vor dem Await stehen. Dieser Handler läuft in der Tunnel-Phase, also
                    ' VOR der Galerie - aber ein Await gibt die Kontrolle an die Ereignisweiterleitung zurück,
                    ' und die sieht dann ein noch UNbehandeltes Ereignis. Die Galerie hat einen eigenen
                    ' Ctrl+V-Handler: das Einfügen lief dadurch zweimal, und jedes markierte Bild landete als
                    ' "Kopie" UND "Kopie 2" im Ordner.
                    Case Key.C
                        e.Handled = True
                        Await CopyGallerySelectionToClipboard(vm, False)
                        Return
                    Case Key.X
                        e.Handled = True
                        Await CopyGallerySelectionToClipboard(vm, True)
                        Return
                    Case Key.V
                        e.Handled = True
                        Await PasteClipboardIntoGallery(vm)
                        Return
                End Select
            End If

            If e.Key = Key.Escape Then
                If vm.CurrentMode = AppMode.Editor Then
                    vm.Editor.BackToViewerCommand.Execute(Nothing)
                    e.Handled = True
                ElseIf vm.CurrentMode = AppMode.Viewer Then
                    vm.Viewer.BackToGalleryCommand.Execute(Nothing)
                    e.Handled = True
                ElseIf vm.CurrentMode = AppMode.Settings Then
                    vm.Settings.CancelCommand.Execute(Nothing)
                    e.Handled = True
                End If
            End If
        End Sub

        Private Async Function CopyGallerySelectionToClipboard(vm As MainWindowViewModel, cut As Boolean) As Task
            Dim gallery = vm.Gallery
            If gallery Is Nothing Then Return

            Dim paths = gallery.GetSelectedPaths()
            gallery.StoreClipboardPaths(paths, cut)
            Await ClipboardPathService.CopyPathsAsync(Clipboard, StorageProvider, paths, cut)
        End Function

        Private Async Function PasteClipboardIntoGallery(vm As MainWindowViewModel) As Task
            Dim gallery = vm.Gallery
            If gallery Is Nothing OrElse String.IsNullOrEmpty(gallery.CurrentFolder) Then Return

            Dim clipboardData = Await ClipboardPathService.ReadPathDataAsync(Clipboard)
            If clipboardData.Paths.Count > 0 Then
                Await gallery.PastePathsIntoFolderAsync(clipboardData.Paths, gallery.CurrentFolder, clipboardData.IsCut)
            ElseIf clipboardData.ClipboardWasReadable Then
                Return
            Else
                Await gallery.PasteIntoFolderAsync(gallery.CurrentFolder)
            End If
        End Function

        Private Function IsTextInputSource(source As Object) As Boolean
            Dim ctrl = TryCast(source, Control)
            While ctrl IsNot Nothing
                If TypeOf ctrl Is TextBox Then Return True
                ctrl = TryCast(ctrl.Parent, Control)
            End While
            Return False
        End Function

        Private Sub OnWindowPointerPressed(sender As Object, e As PointerPressedEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing OrElse Not vm.IsFullscreen Then Return

            If e.GetCurrentPoint(Me).Properties.IsRightButtonPressed Then
                vm.ExitFullscreen()
                e.Handled = True
            End If
        End Sub
    End Class

End Namespace
