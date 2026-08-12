Imports System
Imports System.Linq
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.VisualTree
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Controls.EditorPanels

    Public Class SharpnessPanel
        Inherits UserControl

        ''' <summary>Steht der Zeiger auf dem Maskierungsregler? DAS ist die Bedingung, nicht der
        ''' Zug.
        '''
        ''' Zuerst hing die Vorschau am Ziehen mit gedrueckter ALT-Taste, wie in Lightroom. Auf dem
        ''' Schreibtisch von Linux ist ALT plus Ziehen aber vielerorts die Geste des
        ''' FENSTERVERWALTERS zum Verschieben von Fenstern: er faengt sie ab, und in der Anwendung
        ''' kommt kein einziges Zeigerereignis mehr an (Nutzerbefund 2026-08-12, "klappt nicht").
        ''' Am Zeiger UEBER dem Regler zu haengen umgeht das - eine gedrueckte Taste allein nimmt
        ''' kein Fensterverwalter weg. Wer ziehen kann, zieht weiter; die Vorschau bleibt dabei
        ''' stehen.</summary>
        Private _maskingHovered As Boolean = False
        Private _maskingDragging As Boolean = False
        ''' <summary>Liegt die ALT-Taste gerade unten? Gegen die Tastenwiederholung, und als Bezug
        ''' fuer <see cref="_draggedWhileAlt"/>.</summary>
        Private _altHeld As Boolean = False
        ''' <summary>Wurde waehrend dieser gedrueckten Taste am Regler gezogen? Dann war es die
        ''' gehaltene Geste aus Lightroom, und das Loslassen beendet die Maske.</summary>
        Private _draggedWhileAlt As Boolean = False
        Private _keyHost As TopLevel = Nothing

        Private _slider As RoundSlider = Nothing

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            ' ANGEMELDET WIRD ERST BEIM EINHAENGEN, nicht hier. Im Konstruktor steht der Regler noch
            ' nicht sicher zur Verfuegung - er liegt im Inhalt eines Expanders, und der wird erst mit
            ' dem Baum aufgebaut. Ein FindControl, das Nothing liefert, haengt die Geste an nichts,
            ' und die Vorschau bleibt fuer immer aus: genau der gemeldete Fall.
            AddHandler Me.AttachedToVisualTree, AddressOf OnAttached
            AddHandler Me.DetachedFromVisualTree, AddressOf OnDetached
        End Sub

        Private Sub OnAttached(sender As Object, e As VisualTreeAttachmentEventArgs)
            ' HANDLEDEVENTSTOO ist hier Pflicht: der RoundSlider setzt Handled auf jedem
            ' Zeigerereignis (er nimmt den Zug ganz an sich). Ein gewoehnlicher Handler in der
            ' Blasenphase saehe davon nichts mehr.
            If _slider Is Nothing Then
                _slider = Me.FindControl(Of RoundSlider)("MaskingSlider")
                If _slider IsNot Nothing Then
                    ' ALLE DREI STRATEGIEN anmelden, und das ist kein Gießkannenprinzip: ein Handler
                    ' greift nur, wenn seine Strategie zu der des EREIGNISSES passt.
                    ' PointerEntered und PointerExited sind in Avalonia DIRECT registriert
                    ' (dekompiliert an 12.1.1), die uebrigen Tunnel|Bubble. Mit einer reinen
                    ' Blasen-Anmeldung kam das Ueberfahren des Reglers nie an - das Panel merkte den
                    ' Zeiger nicht, und die ALT-Vorschau blieb aus, ohne dass irgendwo etwas
                    ' schiefging. Genau so kam es aus dem Nutzerkreis zurueck: "klappt nicht".
                    Const alleWege As RoutingStrategies =
                        RoutingStrategies.Direct Or RoutingStrategies.Tunnel Or RoutingStrategies.Bubble
                    _slider.AddHandler(InputElement.PointerEnteredEvent, AddressOf OnMaskingPointerEntered,
                                       alleWege, handledEventsToo:=True)
                    _slider.AddHandler(InputElement.PointerExitedEvent, AddressOf OnMaskingPointerExited,
                                       alleWege, handledEventsToo:=True)
                    _slider.AddHandler(InputElement.PointerPressedEvent, AddressOf OnMaskingPointerPressed,
                                       alleWege, handledEventsToo:=True)
                    _slider.AddHandler(InputElement.PointerMovedEvent, AddressOf OnMaskingPointerMoved,
                                       alleWege, handledEventsToo:=True)
                    _slider.AddHandler(InputElement.PointerReleasedEvent, AddressOf OnMaskingPointerReleased,
                                       alleWege, handledEventsToo:=True)
                    _slider.AddHandler(InputElement.PointerCaptureLostEvent, AddressOf OnMaskingCaptureLost,
                                       alleWege, handledEventsToo:=True)
                End If
            End If

            ' Die TASTE selbst, damit die Vorschau auch ohne Mausbewegung kommt und geht: wer den
            ' Zeiger stillhaelt und ALT drueckt, bekommt sonst kein Ereignis. Am Fenster und im
            ' TUNNEL, weil ALT unterwegs als Menue-Zugriffstaste verbraucht werden kann.
            If _keyHost IsNot Nothing Then Return
            _keyHost = TopLevel.GetTopLevel(Me)
            If _keyHost Is Nothing Then Return
            _keyHost.AddHandler(InputElement.KeyDownEvent, AddressOf OnAnyKeyDown,
                                RoutingStrategies.Tunnel, handledEventsToo:=True)
            _keyHost.AddHandler(InputElement.KeyUpEvent, AddressOf OnAnyKeyUp,
                                RoutingStrategies.Tunnel, handledEventsToo:=True)
        End Sub

        Private Sub OnDetached(sender As Object, e As VisualTreeAttachmentEventArgs)
            If _keyHost IsNot Nothing Then
                _keyHost.RemoveHandler(InputElement.KeyDownEvent, AddressOf OnAnyKeyDown)
                _keyHost.RemoveHandler(InputElement.KeyUpEvent, AddressOf OnAnyKeyUp)
                _keyHost = Nothing
            End If
            ' Das Panel geht, die Vorschau darf nicht auf der Buehne stehenbleiben.
            _maskingHovered = False
            _maskingDragging = False
            Editor?.EndSharpenMaskPreview()
        End Sub

        Private ReadOnly Property Editor As EditorViewModel
            Get
                Return TryCast(DataContext, EditorViewModel)
            End Get
        End Property

        Private Shared Function IsAltKey(key As Key) As Boolean
            Return key = Key.LeftAlt OrElse key = Key.RightAlt
        End Function

        ''' <summary>ZWEI GESTEN, und die Anwendung merkt selbst, welche gemeint war.
        '''
        ''' In Lightroom haelt man ALT und zieht; laesst man los, ist die Ansicht sofort weg. Genau
        ''' so soll es sich hier auch anfuehlen - nur geht es auf vielen Linux-Schreibtischen nicht:
        ''' ALT plus Ziehen gehoert dort dem Fensterverwalter, der Regler bewegt sich also keinen
        ''' Pixel, solange die Taste unten ist ("eine Aenderung an dem Regler machte keinen
        ''' Unterschied").
        '''
        ''' Unterschieden wird deshalb am ZUG, nicht an der Plattform: Wurde zwischen Druecken und
        ''' Loslassen wirklich am Regler gezogen, war es die Lightroom-Geste, und das Loslassen nimmt
        ''' die Maske weg. Kam kein Zug an - weil nur getippt wurde ODER weil der Fensterverwalter
        ''' ihn geschluckt hat -, bleibt sie stehen und laesst sich mit einem zweiten Tippen oder mit
        ''' dem Zeiger wieder loswerden. Beide Gewohnheiten treffen damit auf dieselbe Taste, ohne
        ''' dass jemand eine Einstellung suchen muesste.</summary>
        Private Sub OnAnyKeyDown(sender As Object, e As KeyEventArgs)
            If Not IsAltKey(e.Key) Then Return
            ' EINE GEHALTENE TASTE WIEDERHOLT IHR KEYDOWN. Ohne diese Sperre schaltete die Vorschau
            ' im Takt der Tastenwiederholung an und aus, solange jemand ALT haelt.
            If _altHeld Then Return
            _altHeld = True
            _draggedWhileAlt = False
            If Not (_maskingHovered OrElse _maskingDragging) Then Return
            Dim vm = EditorOrLog()
            If vm Is Nothing Then Return
            If vm.IsSharpenMaskPreviewActive Then vm.EndSharpenMaskPreview() Else vm.BeginSharpenMaskPreview()
        End Sub

        Private Sub OnAnyKeyUp(sender As Object, e As KeyEventArgs)
            If Not IsAltKey(e.Key) Then Return
            _altHeld = False
            ' Nur wenn wirklich gezogen wurde: dann war es die gehaltene Geste, und die endet mit der
            ' Taste. Sonst bleibt die Maske stehen - sie ist per Tippen gekommen.
            If Not _draggedWhileAlt Then Return
            _draggedWhileAlt = False
            Editor?.EndSharpenMaskPreview()
        End Sub

        Private Sub OnMaskingPointerEntered(sender As Object, e As PointerEventArgs)
            _maskingHovered = True
            ' Wer mit gedrueckter Taste auf den Regler faehrt, meint dasselbe wie einer, der sie dort
            ' antippt.
            If e.KeyModifiers.HasFlag(KeyModifiers.Alt) Then EditorOrLog()?.BeginSharpenMaskPreview()
        End Sub

        Private Sub OnMaskingPointerExited(sender As Object, e As PointerEventArgs)
            _maskingHovered = False
            ' Waehrend eines Zuges bleibt sie stehen: der Zeiger verlaesst den Regler beim Ziehen
            ' regelmaessig, und die Vorschau ist genau dann gefragt.
            If Not _maskingDragging Then Editor?.EndSharpenMaskPreview()
        End Sub

        Private Sub OnMaskingPointerPressed(sender As Object, e As PointerPressedEventArgs)
            If Not e.GetCurrentPoint(TryCast(sender, Control)).Properties.IsLeftButtonPressed Then Return
            _maskingDragging = True
            ' HIER entscheidet sich, welche der beiden Gesten gemeint war: kommt ein Zug an, waehrend
            ' die Taste unten liegt, ist es die gehaltene Geste aus Lightroom.
            If e.KeyModifiers.HasFlag(KeyModifiers.Alt) Then
                _draggedWhileAlt = True
                EditorOrLog()?.BeginSharpenMaskPreview()
            End If
        End Sub

        ''' <summary>Die Bewegung schaltet nur EIN, nie aus. Wuerde sie bei fehlender ALT-Taste auch
        ''' ausschalten, waere die stehende Maske beim ersten Schubs an der Maus wieder weg - und
        ''' genau dann will man sie sehen: beim Ziehen des Reglers.</summary>
        Private Sub OnMaskingPointerMoved(sender As Object, e As PointerEventArgs)
            If Not (_maskingHovered OrElse _maskingDragging) Then Return
            If e.KeyModifiers.HasFlag(KeyModifiers.Alt) Then EditorOrLog()?.BeginSharpenMaskPreview()
        End Sub

        Private Sub OnMaskingPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            _maskingDragging = False
            ' Die Maske bleibt stehen: nach dem Zug will man sehen, was der neue Wert bedeutet.
        End Sub

        Private Sub OnMaskingCaptureLost(sender As Object, e As PointerCaptureLostEventArgs)
            _maskingDragging = False
            If Not _maskingHovered Then Editor?.EndSharpenMaskPreview()
        End Sub

        ''' <summary>Der Editor hinter dem Panel - und eine Zeile im Protokoll, wenn er fehlt. Ohne
        ''' ihn haengt die Geste an nichts, und das sieht von aussen aus wie eine Taste, die nicht
        ''' ankommt.</summary>
        Private Function EditorOrLog() As EditorViewModel
            Dim vm = Editor
            If vm Is Nothing Then
                DiagnosticLogService.LogAlways("Editor.SchaerfeMaske",
                                               "Geste ohne Editor am DataContext: " & TypeName(DataContext))
            End If
            Return vm
        End Function

        Public Sub OnMatchWidthDropDownOpened(sender As Object, e As EventArgs)
            Dim comboBox = TryCast(sender, ComboBox)
            If comboBox Is Nothing Then Return
            Dim popup = comboBox.GetVisualDescendants().OfType(Of Popup)().FirstOrDefault()
            If popup IsNot Nothing Then
                popup.Width = comboBox.Bounds.Width
            End If
        End Sub
    End Class

End Namespace
