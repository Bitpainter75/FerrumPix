Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.LogicalTree
Imports Avalonia.Styling

Namespace Services

    ''' <summary>
    ''' Der Zeichentablett-Modus: alles loest beim DRUECKEN aus statt beim Loslassen.
    '''
    ''' Hintergrund (Nutzerbefund 2026-08-28): mit einem Stift liessen sich die Regler bedienen, die
    ''' Schaltflaechen der Galerieleiste aber nicht. Das passt zu dem, was Avalonia tut. Ein
    ''' <see cref="Button"/> setzt beim Druecken nur <c>IsPressed</c>; der Klick entsteht erst beim
    ''' Loslassen, und dafuer muessen DREI Dinge gleichzeitig zutreffen: <c>IsPressed</c> ist noch
    ''' gesetzt, die Ersttaste war die linke, und die Loslass-Stelle trifft per Trefferpruefung
    ''' wieder auf die Schaltflaeche. Unsere eigenen Regler brauchen diese zweite Haelfte nie, sie
    ''' arbeiten auf Druecken und Bewegen. Dass die Regler gehen, belegt also nur, dass das Druecken
    ''' ankommt - nicht, dass der Stift insgesamt funktioniert.
    '''
    ''' Welche der drei Bedingungen beim Stift ausfaellt, laesst sich von hier aus nicht feststellen;
    ''' der Modus deckt alle drei ab, weil er auf der Haelfte ausloest, die nachweislich ankommt.
    '''
    ''' Der Preis, und er gilt dann auch fuer die Maus: ein Klick laesst sich nicht mehr zuruecknehmen,
    ''' indem man vor dem Loslassen wegzieht. Deshalb ist es eine Einstellung und keine Vorgabe.
    ''' </summary>
    Public NotInheritable Class TabletInputService

        Private Sub New()
        End Sub

        ''' Die Stile werden EINMAL gebaut und danach nur noch an- und abgehaengt. Neu gebaut waeren
        ''' sie bei jedem Umschalten andere Objekte, und Avalonia muesste jedes Steuerelement erneut
        ''' durchstilen.
        Private Shared _styles As Styles

        Private Shared _enabled As Boolean

        ''' <summary>Ob der Modus gerade gilt.</summary>
        Public Shared ReadOnly Property IsEnabled As Boolean
            Get
                Return _enabled
            End Get
        End Property

        ''' <summary>Haengt die Stile an die Anwendung oder nimmt sie wieder weg.
        ''' Mehrfach mit demselben Wert aufgerufen tut sie nichts.</summary>
        Public Shared Sub Apply(enabled As Boolean)
            Dim app = Application.Current
            If app Is Nothing Then Return
            If _enabled = enabled AndAlso _styles IsNot Nothing Then Return
            _enabled = enabled

            If _styles Is Nothing Then _styles = BuildStyles()

            Dim vorhanden = app.Styles.Contains(_styles)
            If enabled AndAlso Not vorhanden Then
                app.Styles.Add(_styles)
            ElseIf Not enabled AndAlso vorhanden Then
                app.Styles.Remove(_styles)
            End If
        End Sub

        Private Shared Function BuildStyles() As Styles
            Dim result As New Styles()

            ' ":is" ist Pflicht. Ein blosses "Button" trifft in Avalonia NUR genau diesen Typ; mit
            ' ":is" sind ToggleButton, ToggleSwitch, CheckBox und RadioButton mit dabei, denn die
            ' leiten alle von Button ab.
            Dim buttons As New Style(Function(x) x.[Is](Of Button)())
            buttons.Setters.Add(New Setter(Button.ClickModeProperty, ClickMode.Press))
            result.Add(buttons)

            ' RepeatButton wieder zurueck. Der Stil wirkt auch in die Vorlagen hinein, und dort
            ' sitzen die Schrittknoepfe von NumericUpDown und die Enden der Rollbalken. Ein
            ' RepeatButton startet beim Druecken seinen Wiederholtakt; mit ClickMode.Press kaeme ein
            ' zusaetzlicher Klick obendrauf und der Wert sprang um zwei Stufen. Der SPAETERE Stil
            ' gewinnt, deshalb steht er hier unten.
            Dim repeats As New Style(Function(x) x.[Is](Of RepeatButton)())
            repeats.Setters.Add(New Setter(Button.ClickModeProperty, ClickMode.Release))
            result.Add(repeats)

            ' Menueeintraege haben gar kein ClickMode. Bei ihnen entsteht der Klick im
            ' DefaultMenuInteractionHandler, und zwar ebenfalls erst beim Loslassen (dort zusaetzlich
            ' unter der Bedingung, dass die Ersttaste die linke war). Sie brauchen deshalb einen
            ' eigenen Weg, siehe OnMenuPointerPressed. Angehaengt wird er ueber eine angehaengte
            ' Eigenschaft, damit auch Menues erfasst sind, die erst spaeter entstehen.
            Dim contextMenus As New Style(Function(x) x.[Is](Of ContextMenu)())
            contextMenus.Setters.Add(New Setter(PressActivatesProperty, True))
            result.Add(contextMenus)

            Dim flyoutMenus As New Style(Function(x) x.[Is](Of MenuFlyoutPresenter)())
            flyoutMenus.Setters.Add(New Setter(PressActivatesProperty, True))
            result.Add(flyoutMenus)

            Return result
        End Function

        ''' <summary>Setzt auf einem Menue den Tunnel-Behandler fuer das Druecken. Nur ueber die
        ''' Stile oben gesetzt - faellt der Modus weg, faellt die Eigenschaft auf ihren Vorgabewert
        ''' zurueck und der Behandler geht mit.</summary>
        Public Shared ReadOnly PressActivatesProperty As AttachedProperty(Of Boolean) =
            AvaloniaProperty.RegisterAttached(Of TabletInputService, Control, Boolean)("PressActivates")

        Public Shared Function GetPressActivates(element As Control) As Boolean
            Return element.GetValue(PressActivatesProperty)
        End Function

        Public Shared Sub SetPressActivates(element As Control, value As Boolean)
            element.SetValue(PressActivatesProperty, value)
        End Sub

        Shared Sub New()
            PressActivatesProperty.Changed.AddClassHandler(Of Control)(AddressOf OnPressActivatesChanged)
        End Sub

        Private Shared Sub OnPressActivatesChanged(menu As Control, e As AvaloniaPropertyChangedEventArgs)
            ' Immer erst abhaengen: ein zweimal gesetzter Wert wuerde den Behandler sonst doppelt
            ' fuehren, und der Eintrag loeste zweimal aus.
            menu.RemoveHandler(InputElement.PointerPressedEvent, AddressOf OnMenuPointerPressed)
            If GetPressActivates(menu) Then
                menu.AddHandler(InputElement.PointerPressedEvent, AddressOf OnMenuPointerPressed,
                                RoutingStrategies.Tunnel)
            End If
        End Sub

        ''' <summary>Loest den Menueeintrag unter dem Zeiger schon beim Druecken aus.
        '''
        ''' Der Tunnel laeuft VOR dem DefaultMenuInteractionHandler, der seine Behandler als
        ''' gewoehnliche Ereignisse am Menue fuehrt. Eintraege MIT Untermenue bleiben aussen vor: die
        ''' oeffnen ihr Untermenue ohnehin schon beim Druecken, und ein Klick darauf gibt es nicht.
        '''
        ''' Das Ereignis ClickEvent zu erheben ist der richtige Weg und nicht nur das Kommando
        ''' auszufuehren: an ihm haengen BEIDE Wege, die die Anwendung benutzt - das Kommando ueber
        ''' den Klassenbehandler von MenuItem und die Click-Behandler aus dem XAML.</summary>
        Private Shared Sub OnMenuPointerPressed(sender As Object, e As PointerPressedEventArgs)
            ' Der Modus wird hier NOCH EINMAL gefragt, obwohl der Behandler nur ueber den Stil
            ' angehaengt wird. Grund: ein Menue, das beim Abschalten in einem Aufklappfenster steht,
            ' bekommt das Wegnehmen des Stils nicht immer mit - der Behandler ueberlebt dann. Mit
            ' dieser Zeile ist er in dem Fall wenigstens wirkungslos.
            If Not _enabled Then Return
            Dim source = TryCast(e.Source, Control)
            If source Is Nothing Then Return
            Dim item = source.FindLogicalAncestorOfType(Of MenuItem)(True)
            If item Is Nothing Then Return
            If item.HasSubMenu OrElse Not item.IsEffectivelyEnabled Then Return
            If Not e.GetCurrentPoint(item).Properties.IsLeftButtonPressed Then Return

            item.RaiseEvent(New RoutedEventArgs(MenuItem.ClickEvent))

            ' Das Menue schliesst sich sonst nicht: das Schliessen steckt im Handler, den wir hier
            ' gerade ueberholt haben.
            item.FindLogicalAncestorOfType(Of MenuBase)()?.Close()
            e.Handled = True
        End Sub

    End Class

End Namespace
