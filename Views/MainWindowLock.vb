Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Threading
Imports Avalonia.VisualTree
Imports FerrumPix.Controls
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>
    ''' Das Anmeldefenster im Fenster: zeigen, Eingaben sperren, die Zeit ohne Eingabe messen.
    '''
    ''' <para>GESPERRT IST: jede Taste ausser denen im Anmeldefenster (der Tunnel des Fensters
    ''' laesst nur sie durch, <see cref="HandleLockedKey"/>), jede Maus und jedes Ablegen (der deckende
    ''' Grund des Anmeldefensters liegt ueber allem und nimmt nichts an), Kontextmenue und offene
    ''' Aufklappfenster (beim Sperren geschlossen), Dialoge (sie liegen darunter und warten). Was im
    ''' Hintergrund laeuft, laeuft weiter.</para>
    '''
    ''' <para>DIE ZEIT OHNE EINGABE zaehlt jede Taste, jeden Mausdruck, jede Bewegung und jedes Rad
    ''' am Fenster, auch schon behandelte. Im Browser kommen dieselben Eingaben ueber die Bruecke
    ''' herein und zaehlen ebenso.</para>
    ''' </summary>
    Partial Public Class MainWindow

        Private _lockScreenView As LockScreenView
        Private _lockIdleTimer As DispatcherTimer

        ''' <summary>Die nativen Videoflaechen, die beim Sperren verborgen wurden, mit ihrem Stand
        ''' davor. Eine native Flaeche laege sonst ueber dem Anmeldefenster.</summary>
        Private ReadOnly _videoHiddenByLock As New List(Of Control)

        ''' <summary>Meldet die Eingaben fuer die Zeit ohne Eingabe an und startet die Uhr. Aus dem
        ''' Konstruktor.</summary>
        Private Sub InstallAppLockHooks()
            Me.AddHandler(InputElement.KeyDownEvent, New EventHandler(Of KeyEventArgs)(Sub(s, e) AppLockService.NoteActivity()),
                          RoutingStrategies.Tunnel, handledEventsToo:=True)
            Me.AddHandler(InputElement.PointerPressedEvent, New EventHandler(Of PointerPressedEventArgs)(Sub(s, e) AppLockService.NoteActivity()),
                          RoutingStrategies.Tunnel, handledEventsToo:=True)
            Me.AddHandler(InputElement.PointerMovedEvent, New EventHandler(Of PointerEventArgs)(Sub(s, e) AppLockService.NoteActivity()),
                          RoutingStrategies.Tunnel, handledEventsToo:=True)
            Me.AddHandler(InputElement.PointerWheelChangedEvent, New EventHandler(Of PointerWheelEventArgs)(Sub(s, e) AppLockService.NoteActivity()),
                          RoutingStrategies.Tunnel, handledEventsToo:=True)
            ' Gesperrt behaelt das Kennwortfeld den Fokus: holt ihn sich etwas anderes (eine Ansicht
            ' fragt nach dem Betreten mit Hintergrundvorrang nach ihm, ein Dialog darunter beim
            ' Oeffnen), geht er sofort zurueck.
            Me.AddHandler(InputElement.GotFocusEvent, New EventHandler(Of FocusChangedEventArgs)(AddressOf OnFocusWhileLocked),
                          RoutingStrategies.Bubble, handledEventsToo:=True)
            ' Alle zehn Sekunden reicht: die kuerzeste Zeit sind fuenf Minuten.
            _lockIdleTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(10)}
            AddHandler _lockIdleTimer.Tick, Sub(s, e) AppLockService.CheckIdle()
            _lockIdleTimer.Start()
        End Sub

        ''' <summary>Zeigt oder verbirgt das Anmeldefenster nach dem Stand des ViewModels. Die Ansicht
        ''' entsteht beim ersten Sperren und bleibt danach.</summary>
        Private Sub UpdateLockScreenView(vm As MainWindowViewModel)
            Dim host = Me.FindControl(Of ContentControl)("LockScreenHost")
            If host Is Nothing Then Return
            Dim lockScreen = vm?.LockScreen
            If lockScreen Is Nothing Then
                host.IsVisible = False
                host.Content = Nothing
                RestoreVideoAfterLock()
                Return
            End If
            If _lockScreenView Is Nothing Then
                _lockScreenView = New LockScreenView()
                LocalizationService.ApplyTo(_lockScreenView)
            End If
            _lockScreenView.DataContext = lockScreen
            host.Content = _lockScreenView
            host.IsVisible = True
            ClosePopupsForLock()
            HideVideoForLock()
            _lockScreenView.FocusPassword()
        End Sub

        ''' <summary>Gesperrt: Tasten erreichen nur das Anmeldefenster. True, wenn die Taste damit
        ''' erledigt ist und der Tunnel des Fensters aufhoeren soll.</summary>
        Private Function HandleLockedKey(vm As MainWindowViewModel, e As KeyEventArgs) As Boolean
            If Not vm.IsAppLocked Then Return False
            ' Steht der Fokus nicht im Anmeldefenster (etwa gleich nach dem Sperren), holt ihn die
            ' Taste dorthin und geht selbst verloren.
            If Not IsInsideLockScreen(e.Source) Then
                e.Handled = True
                _lockScreenView?.FocusPassword()
            End If
            Return True
        End Function

        ''' <summary>STRG+L (am Mac CMD+L) sperrt, in jedem Modus, auch hinter einem Dialog.</summary>
        Private Function HandleLockShortcut(vm As MainWindowViewModel, e As KeyEventArgs) As Boolean
            If e.Key <> Key.L OrElse Not vm.CanLockApp Then Return False
            Dim primary = If(PlatformShortcutService.IsMacOS, KeyModifiers.Meta, KeyModifiers.Control)
            If e.KeyModifiers <> primary Then Return False
            vm.LockApp()
            e.Handled = True
            Return True
        End Function

        Private Sub OnFocusWhileLocked(sender As Object, e As FocusChangedEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing OrElse Not vm.IsAppLocked OrElse _lockScreenView Is Nothing Then Return
            If IsInsideLockScreen(e.Source) Then Return
            _lockScreenView.FocusPassword()
        End Sub

        Private Function IsInsideLockScreen(source As Object) As Boolean
            Dim visual = TryCast(source, Visual)
            If visual Is Nothing OrElse _lockScreenView Is Nothing Then Return False
            Return visual Is _lockScreenView OrElse _lockScreenView.IsVisualAncestorOf(visual)
        End Function

        ''' <summary>Schliesst, was beim Sperren aufgeklappt ist: Kontextmenues, Flyouts und
        ''' Aufklapplisten. Am Rechner sind das eigene Fenster des Systems und laegen sonst ueber dem
        ''' Anmeldefenster.</summary>
        Private Sub ClosePopupsForLock()
            Try
                For Each control In Me.GetVisualDescendants().OfType(Of Control)().ToList()
                    If control.ContextMenu IsNot Nothing AndAlso control.ContextMenu.IsOpen Then control.ContextMenu.Close()
                    If control.ContextFlyout IsNot Nothing AndAlso control.ContextFlyout.IsOpen Then control.ContextFlyout.Hide()
                    Dim button = TryCast(control, Button)
                    If button?.Flyout IsNot Nothing AndAlso button.Flyout.IsOpen Then button.Flyout.Hide()
                    Dim popup = TryCast(control, Popup)
                    If popup IsNot Nothing AndAlso popup.IsOpen Then popup.IsOpen = False
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("MainWindow.ClosePopupsForLock", ex)
            End Try
        End Sub

        Private Sub HideVideoForLock()
            For Each video In Me.GetVisualDescendants().OfType(Of MpvVideoView)().ToList()
                If Not video.IsVisible Then Continue For
                video.IsVisible = False
                _videoHiddenByLock.Add(video)
            Next
        End Sub

        Private Sub RestoreVideoAfterLock()
            For Each video In _videoHiddenByLock
                video.IsVisible = True
            Next
            _videoHiddenByLock.Clear()
        End Sub

    End Class

End Namespace
