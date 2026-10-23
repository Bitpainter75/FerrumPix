Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.ApplicationLifetimes
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform
Imports System.Threading.Tasks
Imports FerrumPix.ViewModels
Imports FerrumPix.Views
Imports FerrumPix.Services

Public Class App
    Inherits Application

    Public Shared AppIcon As WindowIcon

    Private Shared ReadOnly _mpvAvailable As New Lazy(Of Boolean)(
        Function()
            Try
                Return MpvInterop.IsAvailable()
            Catch
                Return False
            End Try
        End Function, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication)

    Public Shared ReadOnly Property IsInlineVideoPlaybackAvailable As Boolean
        Get
            Return _mpvAvailable.Value
        End Get
    End Property

    Public Shared ReadOnly Property IsVideoThumbnailAvailable As Boolean
        Get
            Return _mpvAvailable.Value
        End Get
    End Property

    ''' <summary>Den SQLite-Anbieter scharf schalten. NICHT einbetten lassen: sonst nennt der Typ
    ''' wieder die aufrufende Methode, und eine fehlende Assembly schlägt dort zu, wo kein Try mehr
    ''' um sie herum ist.</summary>
    <Runtime.CompilerServices.MethodImpl(Runtime.CompilerServices.MethodImplOptions.NoInlining)>
    Private Shared Sub InitializeSqliteProvider()
        SQLitePCL.Batteries_V2.Init()
    End Sub

    Public Overrides Sub Initialize()
        AvaloniaXamlLoader.Load(Me)

        ' Microsoft.Data.Sqlite bringt den SQLitePCL-Provider mit, aktiviert ihn aber nicht
        ' automatisch in jeder Veröffentlichungsart. Ohne diese Initialisierung schlugen
        ' Katalog, Ortsnachträge und Hintergrundindex erst beim ersten Datenbankzugriff fehl.
        '
        ' DER AUFRUF STEHT IN EINER EIGENEN METHODE, und das ist der ganze Zweck: eine fehlende
        ' Assembly meldet sich nicht beim Aufruf, sondern beim Übersetzen der Methode, die ihren
        ' Typ nennt. Stand die Zeile direkt hier, warf schon der Eintritt in Initialize, und das
        ' Try darum kam nie zum Zug - die Anwendung ließ sich in einer Umgebung ohne den Provider
        ' gar nicht mehr hochziehen.
        Try
            InitializeSqliteProvider()
        Catch ex As Exception
            DiagnosticLogService.LogException("App.SQLiteInit", ex)
        End Try

        ' ERST HIER, nicht vor dem Aufbau: LogToTrace setzt den Empfaenger der Oberflaechenschicht
        ' beim Bauen und wuerde einen frueher gesetzten ersetzen. Mit eingeschaltetem Diagnoselog
        ' schreibt sie danach mit, was sie beim Ziehen und Ablegen tut - die einzige Stelle, die
        ' ueber ihr eigenes Warten Auskunft gibt.
        ' NICHT die Einstellung fragen, sondern den GELTENDEN Schalter: der Startparameter --debug
        ' erzwingt das Protokoll für diesen Lauf, und dann sind es gerade die Meldungen der
        ' Oberflächenschicht, die gebraucht werden. IsVerboseEnabled liest die Einstellung von
        ' selbst, wenn nichts erzwungen wurde - ohne den Parameter ändert sich also nichts.
        Try
            If DiagnosticLogService.IsVerboseEnabled Then AvaloniaLogBridge.Install()
        Catch
        End Try
        AppIcon = New WindowIcon(AssetLoader.Open(New Uri("avares://FerrumPix/Assets/FerrumPix_Icon.ico")))

        ' Die beiden globalen Sicherheitsnetze stehen in Program.Main, nicht hier: dort greifen sie
        ' AB DEM ERSTEN BEFEHL, während diese Stelle erst erreicht wird, wenn Avalonia bereits steht.
        ' Ein Absturz beim Aufbau des Toolkits fällt vorher. Sie hier ein zweites Mal anzumelden
        ' schriebe jede Ausnahme doppelt ins Protokoll.
    End Sub

    ''' <summary>Ein Zug mit dem ZEICHENSTIFT bleibt bei dem Steuerelement, das ihn angenommen hat.
    '''
    ''' <para>Avalonias Scroll-Geste im ScrollViewer reagiert auf Touch UND Stift, nicht auf die
    ''' Maus (ScrollGestureRecognizer.PointerPressed prueft PointerType Touch oder Pen). Und sie
    ''' bekommt die Ereignisse auch dann, wenn ein Steuerelement darunter sie schon behandelt hat.
    ''' Wer mit dem Stift einen Kurvenpunkt, ein Farbrad oder einen Regler in einem Panel zog,
    ''' scrollte nach wenigen Pixeln das ganze Panel mit, und der Punkt rutschte dabei unter dem
    ''' Stift weg (Forumsbefund, Wacom unter Xubuntu; mit der Maus ging alles).</para>
    '''
    ''' <para>Die Regel: hat ein Steuerelement den Druck des Stifts fuer sich genommen (Handled),
    ''' darf keine Geste weiter oben den Zug uebernehmen. Die Markierung haelt, solange der Zeiger
    ''' gefangen ist, also bis zum Loslassen. Ein Druck auf freie Panelflaeche bleibt unbehandelt
    ''' und scrollt mit dem Stift weiter wie bisher - ein Stift hat kein Mausrad.</para>
    '''
    ''' <para>Nach dem eigenen Handler des Steuerelements, deshalb Bubble und auch fuer bereits
    ''' behandelte Ereignisse; die Geste sitzt weiter oben im Weg und kommt danach.</para></summary>
    Private Shared Sub KeepPenDragsOnTheirControl()
        Avalonia.Input.InputElement.PointerPressedEvent.AddClassHandler(Of Avalonia.Input.InputElement)(
            Sub(element, e)
                If e.Handled AndAlso e.Pointer.Type = Avalonia.Input.PointerType.Pen Then e.PreventGestureRecognition()
            End Sub,
            Avalonia.Interactivity.RoutingStrategies.Direct Or Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo:=True)
    End Sub

    ''' <summary>Ein Mausrad-Ereignis mit unplausiblem Delta (Messfehler des X11-Rads, siehe
    ''' <see cref="Models.WheelDeltaGuard"/>) erreicht kein Element.
    '''
    ''' <para>Am Fenster, auf dem Hinweg (Tunnel): das Fenster ist das erste Glied des Weges, also
    ''' sieht vor ihm niemand das Ereignis, auch kein eigener Tunnel-Handler weiter unten. Ein
    ''' Klassen-Handler fuer jedes Fenster, nicht einer je Stelle: die Galerie sprang beim ersten
    ''' Radschritt nach dem Start ans Listenende (Nutzerbefund), und jeder andere ScrollViewer haette
    ''' dasselbe getan - die Begrenzung auf den Rollbereich verhindert den Sprung nicht.</para></summary>
    Private Shared Sub DropImplausibleWheelEvents()
        Avalonia.Input.InputElement.PointerWheelChangedEvent.AddClassHandler(Of Avalonia.Controls.TopLevel)(
            Sub(topLevel, e)
                If Models.WheelDeltaGuard.IsImplausible(e.Delta) Then
                    Services.DiagnosticLogService.LogAlways("Input.Wheel", $"verworfen: Delta {e.Delta.X}/{e.Delta.Y}")
                    e.Handled = True
                End If
            End Sub,
            Avalonia.Interactivity.RoutingStrategies.Tunnel)
    End Sub

    ''' <summary>Die Scrollpfeile eines Menues erscheinen nur, wenn das Menue wirklich nicht passt.
    '''
    ''' <para>Kontextmenues, Fussleistenmenues und Untermenues scrollen ueber eine eigene
    ''' ScrollViewer-Vorlage mit je einem Pfeilknopf oben und unten statt einer Leiste. Ob ein Pfeil
    ''' steht, entscheidet die Vorlage ueber einen exakten Vergleich von Inhaltshoehe und
    ''' Sichthoehe. Bei gebrochener Skalierung (1,5) kann zwischen beiden ein Rundungsrest bleiben,
    ''' und schon der zeigte den unteren Pfeil. Der nimmt Platz weg, und ab da passt das Menue
    ''' tatsaechlich nicht mehr. Das ist eine Absicherung; die Hauptursache falsch bemessener
    ''' Menues steht bei <see cref="ResizeContextMenusToTheirContent"/>.</para>
    '''
    ''' <para>Die Vorlage erkennt man am Aufbau, nicht an ihrem Schluessel: ein DockPanel als Wurzel
    ''' mit den beiden Pfeilknoepfen. Deren Sichtbarkeit wird an eine eigene Eigenschaft neu
    ''' gebunden und erst ab einem ganzen Punkt Ueberhang gesetzt, gemessen an der Sichthoehe
    ''' ohne Pfeile.</para></summary>
    Private Shared Sub ShowMenuScrollArrowsOnlyOnOverflow()
        Avalonia.Controls.Primitives.TemplatedControl.TemplateAppliedEvent.AddClassHandler(Of ScrollViewer)(
            Sub(viewer, e)
                Dim arrows = MenuScrollArrows(viewer)
                If arrows Is Nothing Then Return
                ' Die Vorlage BINDET IsVisible, und zwar mit der Prioritaet eines lokalen Werts.
                ' Ein gesetzter Wert hebt eine solche Bindung nicht auf; die Vorlage ueberschrieb
                ' ihn bei jeder Aenderung von Inhalt oder Sichthoehe wieder, und der Pfeil stand,
                ' bis das Ueberfahren mit der Maus zufaellig den eigenen Wert zuletzt setzte
                ' (Nutzerbefund). Erst eine NEUE lokale Bindung ersetzt die alte
                ' (ValueStore.AddBinding entsorgt sie): IsVisible haengt deshalb ab hier an
                ' MenuArrowShown, und nur das wird gesetzt.
                For Each arrow In arrows
                    arrow.Bind(Visual.IsVisibleProperty, arrow.GetObservable(MenuArrowShownProperty))
                Next
                RemoveHandler viewer.PropertyChanged, AddressOf OnMenuScrollViewerChanged
                AddHandler viewer.PropertyChanged, AddressOf OnMenuScrollViewerChanged
                UpdateMenuScrollArrows(viewer)
            End Sub)
    End Sub

    ''' <summary>Ob ein Scrollpfeil eines Menues steht; die Quelle seiner Sichtbarkeit.</summary>
    Private Shared ReadOnly MenuArrowShownProperty As AttachedProperty(Of Boolean) =
        AvaloniaProperty.RegisterAttached(Of App, RepeatButton, Boolean)("MenuArrowShown")

    Private Shared Function MenuScrollArrows(viewer As ScrollViewer) As RepeatButton()
        Dim root = TryCast(Avalonia.VisualTree.VisualExtensions.GetVisualChildren(viewer).FirstOrDefault(), DockPanel)
        If root Is Nothing Then Return Nothing
        Dim arrows = root.Children.OfType(Of RepeatButton)().ToArray()
        Return If(arrows.Length = 2, arrows, Nothing)
    End Function

    Private Shared Sub OnMenuScrollViewerChanged(sender As Object, e As AvaloniaPropertyChangedEventArgs)
        If e.Property Is ScrollViewer.ExtentProperty OrElse e.Property Is ScrollViewer.ViewportProperty OrElse
           e.Property Is ScrollViewer.OffsetProperty OrElse e.Property Is ScrollViewer.VerticalScrollBarVisibilityProperty Then
            UpdateMenuScrollArrows(DirectCast(sender, ScrollViewer))
        End If
    End Sub

    Private Shared Sub UpdateMenuScrollArrows(viewer As ScrollViewer)
        Dim arrows = MenuScrollArrows(viewer)
        If arrows Is Nothing Then Return
        ' Ob gescrollt werden muss, entscheidet die Sichthoehe OHNE Pfeile. Ein sichtbarer Pfeil
        ' nimmt seine eigene Hoehe von der Sichtflaeche weg; gemessen an der verkleinerten Flaeche
        ' passt der Inhalt nie, und der Pfeil hielte sich selbst am Leben. Genau so blieb er ueber
        ' der letzten Zeile stehen, bis das Ueberfahren mit der Maus ihn zuruecksetzte
        ' (Nutzerbefund). overflow ist dagegen der tatsaechliche Scrollweg bei der jetzigen Flaeche.
        Dim overflow = viewer.Extent.Height - viewer.Viewport.Height
        Dim arrowSpace = arrows.Where(Function(a) a.IsVisible).Sum(Function(a) a.Bounds.Height)
        Dim overflowWithoutArrows = viewer.Extent.Height - (viewer.Viewport.Height + arrowSpace)
        Dim scrolls As Boolean
        Select Case viewer.VerticalScrollBarVisibility
            Case Avalonia.Controls.Primitives.ScrollBarVisibility.Visible
                scrolls = True
            Case Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                scrolls = overflowWithoutArrows >= 1.0
            Case Else
                scrolls = False
        End Select
        For Each arrow In arrows
            Dim atTop = DockPanel.GetDock(arrow) = Dock.Top
            Dim room = If(atTop, viewer.Offset.Y, overflow - viewer.Offset.Y)
            arrow.SetValue(MenuArrowShownProperty,
                           scrolls AndAlso (viewer.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible OrElse room >= 0.5))
        Next
    End Sub

    ''' <summary>Ein Kontextmenue bekommt die Groesse seines Inhalts, auch wenn der sich nach dem
    ''' Aufgehen noch aendert, und beginnt jedes Mal oben.
    '''
    ''' <para>Ein Menue an der Zeigerposition (die Vorgabe jedes ContextMenu) legt Avalonia beim
    ''' ersten Anordnen fest, Lage UND Fenstergroesse. Danach verwirft PopupRoot.ArrangeSetBounds
    ''' jede Groessenaenderung, solange die Lage "Pointer" ist: das Fenster bleibt so gross wie
    ''' beim ersten Durchgang, der Inhalt wird abgeschnitten, und die Vorlage zeigt Scrollpfeile.
    ''' Und dieser erste Durchgang laeuft noch mit Skalierung 1: das Popup-Fenster entsteht so,
    ''' erst danach gilt die echte Skalierung (1,5), und mit deren Pixelrundung braucht das Menue
    ''' ein paar Punkte mehr, als das Fenster hat (gemessen: Galerie 1010,7 statt 998,7).
    ''' Ergebnis war eine Groesse, die von Mal zu Mal wechselte, bis hin zu einer fehlenden Zeile
    ''' (Nutzerbefund, am staerksten im Ordnerbaum).</para>
    '''
    ''' <para>Abhilfe: beim Oeffnen und nach jedem Layoutdurchgang eines offenen Menues vergleichen, was es braucht
    ''' (DesiredSize), mit dem, was es bekommen hat (Bounds). Weicht beides ab, wird das Popup neu
    ''' platziert: den Versatz kurz verschieben und zuruecknehmen loest Popup.UpdateHostPosition
    ''' aus, und der Positionierer setzt das Fenster mit der inzwischen gemessenen Groesse neu.
    ''' Das gilt fuer jede Lage, also auch fuer die Fussleistenmenues an ihrer Schaltflaeche. Je
    ''' gemessener Groesse nur einmal, sonst liefe es in einer Schleife, wenn das Menue
    ''' wirklich nicht auf den Schirm passt und der Positionierer es kuerzt.</para>
    '''
    ''' <para>Das ContextMenu selbst wird wiederverwendet, samt dem ScrollViewer seiner Vorlage.
    ''' Dessen Scrollstand blieb vom letzten Mal stehen; er wird beim Schliessen auf oben
    ''' gesetzt.</para></summary>
    Private Shared Sub ResizeContextMenusToTheirContent()
        MenuBase.OpenedEvent.AddClassHandler(Of ContextMenu)(
            Sub(menu, e)
                If Not Object.ReferenceEquals(e.Source, menu) Then Return
                _openContextMenus(menu) = Nothing
                RemoveHandler menu.LayoutUpdated, AddressOf OnOpenContextMenuLayoutUpdated
                AddHandler menu.LayoutUpdated, AddressOf OnOpenContextMenuLayoutUpdated
                ' SOFORT pruefen, nicht erst beim naechsten Layoutdurchgang: die Durchgaenge mit
                ' Skalierung 1 und 1,5 sind vor Opened erledigt. Ohne diesen Aufruf kam die
                ' Korrektur erst beim Ueberfahren mit der Maus.
                FitContextMenu(menu)
            End Sub)
        MenuBase.ClosedEvent.AddClassHandler(Of ContextMenu)(
            Sub(menu, e)
                If Not Object.ReferenceEquals(e.Source, menu) Then Return
                RemoveHandler menu.LayoutUpdated, AddressOf OnOpenContextMenuLayoutUpdated
                _openContextMenus.Remove(menu)
                ScrollContextMenuToTop(menu)
            End Sub)
    End Sub

    ''' <summary>Jedes Popup ordnet seinen Inhalt neu an, wenn sein Fenster die Groesse aendert.
    '''
    ''' <para>Unter X11 kommt die neue Fenstergroesse eines Popups nicht sofort, sondern spaeter
    ''' als Meldung des Fenstersystems. WindowBase.HandleResized setzt dann ClientSize und
    ''' startet einen Layoutdurchgang, setzt aber nichts ungueltig: ist sonst nichts zu tun, tut
    ''' der Durchgang nichts, und der Inhalt bleibt in der alten, kleineren Groesse angeordnet.
    ''' Bei einem Menue hiess das: der untere Scrollpfeil stand, bis das Ueberfahren mit der Maus
    ''' irgendetwas neu anordnen liess, und dann sprang das Menue auf seine volle Groesse
    ''' (Nutzerbefund).</para>
    '''
    ''' <para>Resized kommt vor diesem Layoutdurchgang; was hier ungueltig gesetzt wird, ordnet
    ''' derselbe Durchgang neu an. Fuer JEDES Popup (Kontextmenue, Untermenue, Flyout,
    ''' Aufklappliste, Kurzhinweis), angemeldet beim Anwenden seiner Vorlage, also einmal je
    ''' Popup-Fenster.</para></summary>
    Private Shared Sub RearrangePopupsOnResize()
        Avalonia.Controls.Primitives.TemplatedControl.TemplateAppliedEvent.AddClassHandler(Of Avalonia.Controls.Primitives.PopupRoot)(
            Sub(root, e)
                RemoveHandler root.Resized, AddressOf OnPopupRootResized
                AddHandler root.Resized, AddressOf OnPopupRootResized
            End Sub)
    End Sub

    Private Shared Sub OnPopupRootResized(sender As Object, e As WindowResizedEventArgs)
        Dim root = TryCast(sender, Avalonia.Controls.Primitives.PopupRoot)
        If root Is Nothing Then Return
        root.InvalidateMeasure()
        root.InvalidateArrange()
    End Sub

    ''' <summary>Die offenen Kontextmenues, je mit der Groesse, fuer die zuletzt neu platziert
    ''' wurde (Nothing: noch nie).</summary>
    Private Shared ReadOnly _openContextMenus As New Dictionary(Of ContextMenu, Size?)()

    Private Shared Sub ScrollContextMenuToTop(menu As ContextMenu)
        Dim viewer = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(menu).OfType(Of ScrollViewer)().FirstOrDefault()
        If viewer IsNot Nothing Then viewer.Offset = New Vector(0, 0)
    End Sub

    Private Shared Sub OnOpenContextMenuLayoutUpdated(sender As Object, e As EventArgs)
        ' LayoutUpdated nennt das Menue nicht als Absender. Es sind selten mehr als eines offen,
        ' also werden einfach alle offenen geprueft.
        For Each menu In _openContextMenus.Keys.ToList()
            FitContextMenu(menu)
        Next
    End Sub

    Private Shared Sub FitContextMenu(menu As ContextMenu)
        Dim popup = TryCast(menu.Parent, Avalonia.Controls.Primitives.Popup)
        If popup Is Nothing OrElse Not popup.IsOpen Then Return
        Dim wanted = menu.DesiredSize
        Dim got = menu.Bounds.Size
        If Math.Abs(wanted.Height - got.Height) < 0.5 AndAlso Math.Abs(wanted.Width - got.Width) < 0.5 Then Return
        Dim last = _openContextMenus(menu)
        If last.HasValue AndAlso last.Value = wanted Then Return
        _openContextMenus(menu) = wanted

        Dim original = popup.HorizontalOffset
        popup.HorizontalOffset = original + 0.01
        popup.HorizontalOffset = original
    End Sub

    ''' <summary>Zwei Bauformen, deren Inhalt der Durchlauf ueber das Fenster nie zu sehen bekommt.
    ''' Je EIN Klassen-Handler fuer die ganze Anwendung, statt eines Ereignisses je Stelle.</summary>
    Private Shared Sub LocalizeLateContent()
        ' Ein Expander erzeugt seinen Inhalt erst beim Aufklappen. Der Durchlauf ueber das Fenster
        ' hat ihn dann schon hinter sich. Ueber BEIDE Baeume, aus demselben Grund wie bei den
        ' Listenzeilen: beim Aufklappen steht der Inhalt schon im logischen Baum, im sichtbaren erst
        ' nach dem naechsten Messen.
        Avalonia.Controls.Expander.ExpandedEvent.AddClassHandler(Of Avalonia.Controls.Expander)(
            Sub(expander, e)
                Services.LocalizationService.ApplyToMaterialized(expander)
            End Sub)

        ' EIN KURZHINWEIS AUS MEHR ALS EINEM TEXT ist kein String, sondern ein kleiner Baum
        ' (typisch: ein StackPanel mit Bezeichnung und Erklaerung darunter). Der Durchlauf
        ' uebersetzt an einem Steuerelement nur den Kurzhinweis, der ein String IST; ein Baum haengt
        ' weder im logischen noch im sichtbaren Baum seines Besitzers, sondern entsteht in einem
        ' eigenen Popup. Die Farbbaender des Farbmischers standen dadurch in jeder Sprache deutsch
        ' da (Nutzerbefund).
        '
        ' Das Ereignis wird auf dem Steuerelement ausgeloest, dem der Hinweis gehoert, und zwar VOR
        ' dem Anzeigen - also jedes Mal neu, damit auch ein Sprachwechsel mitten in der Sitzung
        ' ankommt. Gegangen wird ueber den logischen Baum des Hinweises: seine Kinder haengen daran,
        ' auch solange er nirgends angezeigt wird.
        Avalonia.Controls.ToolTip.ToolTipOpeningEvent.AddClassHandler(Of Avalonia.Controls.Control)(
            Sub(control, e)
                Dim tip = TryCast(Avalonia.Controls.ToolTip.GetTip(control), Avalonia.LogicalTree.ILogical)
                If tip IsNot Nothing Then Services.LocalizationService.ApplyTo(tip)
            End Sub)
    End Sub

    Public Overrides Sub OnFrameworkInitializationCompleted()
        LocalizeLateContent()
        KeepPenDragsOnTheirControl()
        DropImplausibleWheelEvents()
        ShowMenuScrollArrowsOnlyOnOverflow()
        ResizeContextMenusToTheirContent()
        RearrangePopupsOnResize()
        If TypeOf ApplicationLifetime Is IClassicDesktopStyleApplicationLifetime Then
            Dim desktop = CType(ApplicationLifetime, IClassicDesktopStyleApplicationLifetime)

            Dim initialImagePath As String = Nothing
            Dim initialFolderPath As String = Nothing
            Dim args = desktop.Args
            If args IsNot Nothing AndAlso args.Length > 0 Then
                If IO.File.Exists(args(0)) Then
                    initialImagePath = args(0)
                ElseIf IO.Directory.Exists(args(0)) Then
                    initialFolderPath = args(0)
                End If
            End If

            LocalizationService.LanguageMode = AppSettingsService.Load().LanguageMode
            LocalizationService.DateFormatMode = AppSettingsService.Load().DateFormatMode
            ' Zeichentablett-Modus VOR dem ersten Fenster anhaengen: die Stile setzen ClickMode, und
            ' ein spaeter angehaengter Stil muesste alles noch einmal durchstilen.
            TabletInputService.Apply(AppSettingsService.Load().TabletMode)
            ' Nur der Merker; die Karte wird erst beim ersten Bild angesprochen. Endete die letzte
            ' Sitzung im Treiber, geht der Schalter dabei aus.
            GpuImageService.StartFromSettings()
            ' EINMAL beim Start nachsehen, welche gelernten Modelle vorliegen. Danach steht fuer
            ' diese Sitzung fest, welche Funktionen es gibt; was fehlt, blendet die Oberflaeche aus.
            ' Vor dem ViewModel, damit dessen Bindungen schon den richtigen Stand sehen.
            AiModelService.CheckInventory()
            Dim vm = New MainWindowViewModel(initialImagePath, initialFolderPath)
            Dim win = New MainWindow()
            win.DataContext = vm
            desktop.MainWindow = win
            ' Die eine Abfrage nach einer neueren Fassung je Sitzung. Hier und nicht im ViewModel:
            ' der Pruefstand baut das ViewModel dutzendfach und soll dabei nichts abrufen.
            vm.Settings?.BeginUpdateCheck()

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose
            BuildApplicationMenu(vm)
        End If

        MyBase.OnFrameworkInitializationCompleted()
    End Sub

    ''' <summary>Das Programmmenue in der Menueleiste des Mac.
    '''
    ''' Auf macOS baut Avalonia dieses Menue selbst, wenn die Anwendung keines mitbringt - und dann
    ''' heisst der erste Eintrag "About Avalonia" und oeffnet Avalonias eigenes Fenster mit deren
    ''' Logo und Versionsnummer. Ein Nutzer hat genau das am 2026-09-06 gemeldet, mit Bildschirmfoto.
    ''' Nachgesehen im dekompilierten Avalonia.Native 12.1.2: liegt am Application-Objekt ein
    ''' NativeMenu, nimmt Avalonia dieses und legt nur noch Dienste, Ausblenden und Beenden dazu.
    '''
    ''' Der Name der Anwendung selbst kommt aus <c>Application.Name</c> (in App.axaml gesetzt) -
    ''' ohne ihn stuende in der Menueleiste "Avalonia Application".
    '''
    ''' Windows und Linux kennen kein Programmmenue dieser Art; dort bleibt der Aufbau folgenlos.
    ''' Deshalb steht er hier ohne Plattformabfrage: eine Abfrage mehr, die niemand pflegt, waere
    ''' die schlechtere Loesung als ein Menue, das nur einer der drei Systeme anzeigt.</summary>
    Private Sub BuildApplicationMenu(vm As MainWindowViewModel)
        Try
            Dim about As New NativeMenuItem(LocalizationService.T("Über FerrumPix"))
            AddHandler about.Click, Sub() vm?.OpenSettings()

            Dim settings As New NativeMenuItem(LocalizationService.T("Einstellungen")) With {
                .Gesture = New Avalonia.Input.KeyGesture(Avalonia.Input.Key.OemComma,
                                                         Avalonia.Input.KeyModifiers.Meta)}
            AddHandler settings.Click, Sub() vm?.OpenSettings()

            Dim menu As New NativeMenu()
            menu.Add(about)
            menu.Add(New NativeMenuItemSeparator())
            menu.Add(settings)
            NativeMenu.SetMenu(Me, menu)
        Catch ex As Exception
            ' Ein Menue ist kein Grund, den Start zu verlieren.
            DiagnosticLogService.LogException("App.Programmmenue", ex)
        End Try
    End Sub

    Public Shared Sub ApplyIcon(win As Window)
        If AppIcon IsNot Nothing Then win.Icon = AppIcon
    End Sub
End Class
