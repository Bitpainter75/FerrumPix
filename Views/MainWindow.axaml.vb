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
        ''' Fensterzustand vor dem Wechsel ins Viewer-Vollbild - dorthin geht es beim Verlassen zurueck,
        ''' statt immer auf Normalgroesse zu fallen.
        Private _stateBeforeFullscreen As WindowState = WindowState.Normal
        Private _allowWindowClose As Boolean = False

        ''' <summary>Wer den Tastaturfokus hatte, bevor ein Overlay-Dialog ihn an sich gezogen hat.</summary>
        Private _focusBeforeDialog As Control = Nothing

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
            ' Eigener Handler statt Anhängen an OnWindowPlacementChanged: das dort steigt bei
            ' maximiert/Vollbild sofort aus (es speichert nur die Normalgröße) - die Leisten
            ' müssten dann ausgerechnet im breitesten Zustand ohne Meldung auskommen.
            AddHandler SizeChanged, Sub(s, e) UpdateToolbarWidth(e.NewSize.Width)
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
                ' Der erste SizeChanged kann vor dem DataContext liegen - dann bliebe die Breite
                ' bis zur ersten Größenänderung unbekannt.
                If Bounds.Width > 0 Then vm.UpdateWindowWidth(Bounds.Width)
            End If
        End Sub

        Private Sub OnVmPropertyChanged(sender As Object, e As System.ComponentModel.PropertyChangedEventArgs)
            If e.PropertyName = NameOf(MainWindowViewModel.Title) Then
                Return
            End If
            If e.PropertyName = NameOf(MainWindowViewModel.IsFullscreen) Then
                ApplyFullscreenState()
            ElseIf e.PropertyName = NameOf(MainWindowViewModel.IsDialogOpen) Then
                Dim vm = TryCast(sender, MainWindowViewModel)
                If vm IsNot Nothing AndAlso vm.IsDialogOpen Then
                    _focusBeforeDialog = TryCast(TopLevel.GetTopLevel(Me)?.FocusManager?.GetFocusedElement(), Control)
                    FocusDialog()
                Else
                    RestoreFocusAfterDialog()
                End If
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
                ' Erst Groesse/Position, dann der Zustand: beim spaeteren Wiederherstellen
                ' faellt das Fenster damit auf die zuletzt genutzte Normalgroesse zurueck.
                If settings.MainWindowMaximized Then WindowState = WindowState.Maximized
                UpdateMaximizeGlyph()
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

        ''' <summary>Meldet die Fensterbreite an die ViewModels, damit die Leisten ihre
        ''' Beschriftungen ausblenden, bevor sie sich gegenseitig überlaufen.</summary>
        Private Sub UpdateToolbarWidth(width As Double)
            TryCast(DataContext, MainWindowViewModel)?.UpdateWindowWidth(width)
        End Sub

        Private Sub OnWindowPlacementChanged()
            If Not _hasRestoredPlacement OrElse _isRestoringPlacement OrElse WindowState <> WindowState.Normal Then Return
            AppSettingsService.SaveMainWindowPlacement(Position.X, Position.Y, Width, Height)
        End Sub

        Private Async Sub HandleWindowClosing(sender As Object, e As WindowClosingEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)

            If _allowWindowClose Then
                vm?.Viewer?.ShutdownVideo()
                ' Szene-/Zoom-Worker stilllegen: eine ausstehende Fortsetzung griff sonst waehrend
                ' des Fenster-Teardowns auf die bereits disposte Anzeige zu (Absturz beim Beenden).
                vm?.Editor?.ShutdownSceneWork()
                If WindowState = WindowState.Normal Then
                    AppSettingsService.SaveMainWindowPlacement(Position.X, Position.Y, Width, Height)
                End If
                SaveWindowStateOnExit()
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

            If vm IsNot Nothing AndAlso
               vm.CurrentMode = AppMode.Viewer AndAlso
               vm.Viewer IsNot Nothing AndAlso
               e.CloseReason = WindowCloseReason.WindowClosing Then
                e.Cancel = True
                If Await vm.Viewer.ConfirmPendingRotationAsync("die Anwendung schließt") Then
                    _allowWindowClose = True
                    Close()
                End If
                Return
            End If

            vm?.Viewer?.ShutdownVideo()
            vm?.Editor?.ShutdownSceneWork()
            If WindowState = WindowState.Normal Then
                AppSettingsService.SaveMainWindowPlacement(Position.X, Position.Y, Width, Height)
            End If
            SaveWindowStateOnExit()
            AppSettingsService.Flush()
        End Sub

        Private Sub ApplyLocalization()
            Dispatcher.UIThread.Post(Sub() LocalizationService.ApplyTo(Me), DispatcherPriority.Loaded)
        End Sub

        ''' <summary>Gibt den Tastaturfokus nach dem Schließen eines Overlay-Dialogs an die Ansicht
        ''' zurück. Ohne das bleibt er beim verschwundenen Dialog hängen: Galerie/Viewer/Editor sehen
        ''' danach KEINE Tastendrücke mehr (ihre Kürzel hängen am KeyDown der jeweiligen View), und erst
        ''' ein Klick auf ein Bild belebt sie wieder — genau das Muster „zweiter Shortcut tot"
        ''' (Nutzerbericht 2026-07-17). Die Fensterkürzel (Strg+C/V, Strg+1–5) liefen weiter, weil die
        ''' im Tunnel des Fensters hängen — deshalb wirkte es so willkürlich.</summary>
        Private Sub RestoreFocusAfterDialog()
            Dim restore = Sub()
                Dim vm = TryCast(DataContext, MainWindowViewModel)
                If vm Is Nothing OrElse vm.IsDialogOpen Then Return

                ' Bevorzugt das Element von vorher (z. B. die angeklickte Kachel) - aber nur, wenn es
                ' noch im Baum hängt: Stapelaktionen bauen die Galerie-Liste komplett neu auf.
                If _focusBeforeDialog IsNot Nothing AndAlso
                   _focusBeforeDialog.IsAttachedToVisualTree AndAlso
                   _focusBeforeDialog.IsEffectivelyVisible AndAlso
                   _focusBeforeDialog.Focus() Then
                    _focusBeforeDialog = Nothing
                    Return
                End If

                _focusBeforeDialog = Nothing
                ' Fallback: die aktive View selbst - GalleryView/ViewerView/EditorView sind Focusable
                ' und tragen die Tastenkürzel an ihrem Wurzelelement.
                Dim host = Me.FindControl(Of ContentControl)("MainContentHost")
                Dim view = TryCast(host?.Content, Control)
                If view IsNot Nothing AndAlso view.Focusable Then view.Focus()
            End Sub

            ' Wie beim Öffnen doppelt geplant: der schließende Dialog (und ein evtl. beteiligtes Popup)
            ' schiebt den Fokus selbst noch einmal, nachdem IsDialogOpen gemeldet wurde.
            Dispatcher.UIThread.Post(restore, DispatcherPriority.Loaded)
            Dispatcher.UIThread.Post(restore, DispatcherPriority.Background)
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
            UpdateMaximizeGlyph()
        End Sub

        Private Sub UpdateMaximizeGlyph()
            Dim glyph = Me.FindControl(Of TextBlock)("MaximizeGlyph")
            If glyph IsNot Nothing Then glyph.Text = If(WindowState = WindowState.Maximized, "❐", "□")
        End Sub

        ''' <summary>Merkt sich beim Beenden, ob das Fenster gross war. Der Viewer-Vollbildmodus zaehlt
        ''' mit: er ist zwar ein Ansichts-Modus (der beim Start nicht wiederkehren soll), aber wer darin
        ''' beendet, erwartet auch kein kleines Fenster zurueck - deshalb kommt er maximiert wieder.</summary>
        Private Sub SaveWindowStateOnExit()
            AppSettingsService.SaveMainWindowMaximized(
                WindowState = WindowState.Maximized OrElse WindowState = WindowState.FullScreen)
        End Sub

        Private Sub OnCloseClick(sender As Object, e As RoutedEventArgs)
            Close()
        End Sub

        Private Sub ApplyFullscreenState()
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing Then Return
            If vm.IsFullscreen Then
                ' Zustand vor dem Vollbild merken, damit das Verlassen dorthin zurueckfuehrt.
                If WindowState <> WindowState.FullScreen Then _stateBeforeFullscreen = WindowState
                WindowState = WindowState.FullScreen
                Cursor = HiddenCursor
            Else
                Cursor = Nothing
                ' Delay the window resize by one render cycle so the FullscreenViewport
                ' can be hidden first, preventing the image from being briefly clipped
                ' to the normal window bounds while still visible.
                '
                ' Nur zurueckschalten, wenn wir wirklich im Vollbild SIND. Vorher lief das
                ' bedingungslos - und weil HandleDataContextChanged diese Methode beim Start
                ' aufruft, kam das maximiert wiederhergestellte Fenster kurz gross und fiel dann
                ' auf Normalgroesse zurueck (Nutzerbefund 2026-07-19).
                Dispatcher.UIThread.Post(
                    Sub()
                        If WindowState <> WindowState.FullScreen Then Return
                        WindowState = _stateBeforeFullscreen
                        UpdateMaximizeGlyph()
                    End Sub, DispatcherPriority.Background)
            End If
        End Sub

        ''' App-weite Kürzel (Nutzerwunsch 2026-07-17): Strg+1–5 setzt die Bewertung (Strg+0
        ''' entfernt sie), Strg+Q schaltet den Favoriten - in Galerie, Viewer (auch Vollbild)
        ''' und Editor, jeweils auf dem aktuellen Bild bzw. der Galerie-Auswahl.
        Private Function TryHandleRatingShortcut(vm As MainWindowViewModel, e As KeyEventArgs) As Boolean
            If Not e.KeyModifiers.HasFlag(KeyModifiers.Control) Then Return False
            If IsTextInputSource(e.Source) Then Return False

            Dim rating As String = Nothing
            Select Case e.Key
                Case Key.D0, Key.NumPad0 : rating = "0"
                Case Key.D1, Key.NumPad1 : rating = "1"
                Case Key.D2, Key.NumPad2 : rating = "2"
                Case Key.D3, Key.NumPad3 : rating = "3"
                Case Key.D4, Key.NumPad4 : rating = "4"
                Case Key.D5, Key.NumPad5 : rating = "5"
                Case Key.Q
                    If vm.IsFullscreen OrElse vm.CurrentMode = AppMode.Viewer Then
                        vm.Viewer?.ToggleFavoriteCommand.Execute(Nothing)
                    ElseIf vm.CurrentMode = AppMode.Gallery Then
                        ' ToggleFavoriteCommand erwartet ein ImageItem (Kachel-Herz); mit Nothing lief es
                        ' als stiller No-Op. Für die Auswahl gibt es ToggleSelectedFavoriteCommand.
                        vm.Gallery?.ToggleSelectedFavoriteCommand.Execute(Nothing)
                    ElseIf vm.CurrentMode = AppMode.Editor Then
                        vm.Editor?.ToggleFavoriteCommand.Execute(Nothing)
                    Else
                        Return False
                    End If
                    Return True
                Case Else
                    Return False
            End Select

            If vm.IsFullscreen OrElse vm.CurrentMode = AppMode.Viewer Then
                vm.Viewer?.SetRatingCommand.Execute(rating)
            ElseIf vm.CurrentMode = AppMode.Gallery Then
                vm.Gallery?.SetSelectedRatingCommand.Execute(rating)
            ElseIf vm.CurrentMode = AppMode.Editor Then
                vm.Editor?.SetRatingCommand.Execute(rating)
            Else
                Return False
            End If
            Return True
        End Function

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

            ' Solange der Druckdialog offen ist, darf KEINE Taste an die Ansicht dahinter
            ' durchfallen - sonst blättert Links/Rechts im Betrachter weiter, während vorne der
            ' Dialog steht.
            If vm.IsPrintDialogOpen Then
                Select Case e.Key
                    Case Key.Escape
                        vm.ClosePrintDialog()
                    Case Key.Return, Key.Enter
                        vm.ConfirmPrint()
                End Select
                e.Handled = True
                Return
            End If

            ' Strg+P zentral im Fenster-Tunnel statt in den einzelnen Ansichten: so greift es in
            ' jedem Modus, im Vollbild und - weil der Tunnel vor den View-Kürzeln feuert - auch
            ' noch, nachdem ein Overlay-Dialog den Fokus hatte.
            ' Ohne den Umschalt-Ausschluss würde dieser Zweig auch Strg+Umschalt+P schlucken -
            ' das ist im Editor „Vorschau anwenden".
            If e.Key = Key.P AndAlso e.KeyModifiers.HasFlag(KeyModifiers.Control) AndAlso
               Not e.KeyModifiers.HasFlag(KeyModifiers.Shift) Then
                Select Case vm.CurrentMode
                    Case AppMode.Editor
                        vm.Editor.PrintCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                    Case AppMode.Viewer
                        vm.Viewer.PrintCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                    Case AppMode.Gallery
                        vm.Gallery.PrintSelectedCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                End Select
            End If

            ' F11 schaltet in jedem Modus um - hier oben im Tunnel, damit es auch im Vollbild greift,
            ' wo die darunterliegenden Ansichten keine Tasten mehr sehen.
            If e.Key = Key.F11 Then
                If vm.IsFullscreen Then vm.ExitFullscreen() Else vm.EnterFullscreen()
                e.Handled = True
                Return
            End If

            If TryHandleRatingShortcut(vm, e) Then
                e.Handled = True
                Return
            End If

            If vm.IsFullscreen Then
                Select Case e.Key
                    Case Key.Escape, Key.Back
                        vm.ExitFullscreen()
                        e.Handled = True
                    Case Key.Left, Key.PageUp
                        vm.Viewer.PreviousCommand.Execute(Nothing)
                        e.Handled = True
                    Case Key.Right, Key.PageDown
                        vm.Viewer.NextCommand.Execute(Nothing)
                        e.Handled = True
                    Case Key.Space
                        If vm.Viewer.IsVideoFile Then
                            vm.Viewer.PlayPauseVideoCommand.Execute(Nothing)
                        Else
                            vm.Viewer.ToggleSlideshowCommand.Execute(Nothing)
                        End If
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

            If vm.CurrentMode = AppMode.Editor AndAlso e.KeyModifiers.HasFlag(KeyModifiers.Control) AndAlso e.Key = Key.S Then
                If vm.Editor.CanSaveInPlace Then
                    vm.Editor.SaveCommand.Execute(Nothing)
                Else
                    vm.Editor.SaveAsCommand.Execute(Nothing)
                End If
                e.Handled = True
                Return
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
