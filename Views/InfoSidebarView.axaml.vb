Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Markup.Xaml
Imports Avalonia.Interactivity
Imports Avalonia.VisualTree
Imports FerrumPix.Models
Imports FerrumPix.ViewModels

Namespace Views

    Public Class InfoSidebarView
        Inherits UserControl

        ''' <summary>Inhalte aus einem DataTemplate entstehen oft erst NACH dem Sprachdurchlauf
        ''' ueber das Fenster. Jedes neu materialisierte Element uebersetzt deshalb seinen eigenen
        ''' Teilbaum - ueber BEIDE Baeume, siehe
        ''' <see cref="LocalizationService.ApplyToMaterialized"/>.</summary>
        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Avalonia.Visual)
            If itemRoot IsNot Nothing Then FerrumPix.Services.LocalizationService.ApplyToMaterialized(itemRoot)
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            Me.AddHandler(InputElement.PointerWheelChangedEvent, AddressOf HandlePointerWheelChanged, RoutingStrategies.Bubble)
        End Sub

        Private Sub HandlePointerWheelChanged(sender As Object, e As PointerWheelEventArgs)
            e.Handled = True
        End Sub

        ''' <summary>Der getippte Name wird uebernommen, sobald das Feld den Fokus verliert oder die
        ''' Eingabetaste kommt. Kein Knopf daneben: ein Name ist ein Wort, und ein Bestaetigen-Knopf
        ''' je Person waere mehr Oberflaeche als Inhalt.
        '''
        ''' Die Id steht im Tag des Feldes, nicht im Text - benannt wird ueber die Id, damit eine
        ''' Umbenennung die Zuordnung nicht verliert.
        '''
        ''' Uebernommen wird beim Verlassen des Feldes - ABER NICHT, solange die
        ''' Vorschlagsliste offen steht.
        '''
        ''' BEFUND: ein Klick auf einen Vorschlag nimmt dem Feld zuerst den Fokus. Ohne diese Sperre
        ''' lief die Uebernahme also mit dem Text, der VOR dem Klick dastand - und weil sie danach
        ''' die Gesichtszeilen neu aufbaut, verschwand das Feld samt offener Liste, bevor der Klick
        ''' ankam. Fuer den Benutzer sah das so aus, als liesse sich in der Liste nichts anklicken.
        '''
        ''' Schlimmer noch war die stille Nebenwirkung: wer "Chr" tippte und dann "Christina" anklickte,
        ''' benannte die Gruppe "Chr". In einem gewachsenen Bestand steht genau so ein abgeschnittener Name.
        '''
        ''' Ein Klick in die Liste waehlt damit nur aus; uebernommen wird danach mit der Eingabetaste
        ''' oder beim Verlassen des Feldes. Dieselbe Zweistufigkeit gilt seit jeher fuer die
        ''' Eingabetaste, und aus demselben Grund: ein versehentlich uebernommener Vorschlag schiebt
        ''' ein Gesicht zu einer fremden Person.</summary>
        Private Sub OnPersonNameCommitted(sender As Object, e As RoutedEventArgs)
            Dim box = TryCast(sender, AutoCompleteBox)
            If box Is Nothing Then Return
            ' EINEN DURCHLAUF SPAETER uebernehmen. Ein Klick auf einen Vorschlag nimmt dem Feld
            ' ZUERST den Fokus und traegt den gewaehlten Text erst DANACH ein. Sofort uebernommen
            ' lief die Uebernahme deshalb mit dem Text von VOR dem Klick - und weil sie die
            ' Gesichtszeilen neu aufbaut, verschwand das Feld samt offener Liste, bevor der Klick
            ' ankam. Fuer den Benutzer sah das aus, als liesse sich in der Liste nichts anklicken.
            '
            ' Das Stichwortfeld daneben hat diese Handler nicht und war deshalb nie betroffen -
            ' genau daran liess sich die Ursache festmachen.
            '
            ' EIN DURCHLAUF AUFSCHUB REICHT NICHT (GitHub #143): zwischen Druecken und Loslassen der
            ' Maustaste liegen viele Durchlaeufe, und "Dad" wurde uebernommen, obwohl "Daddy (me)"
            ' angeklickt war. Deshalb wird nachgesehen, wohin der Fokus ging (PersonNameField): liegt
            ' er auf einem Vorschlag, gilt dieser; steht die Liste offen oder der Fokus wieder im
            ' Feld, geschieht nichts.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                Sub()
                    Dim suggestion As String = Nothing
                    Select Case PersonNameField.AfterLostFocus(box, suggestion)
                        Case PersonNameField.LostFocusAction.CommitSuggestion
                            box.Text = suggestion
                            CommitPersonName(box)
                        Case PersonNameField.LostFocusAction.CommitText
                            CommitPersonName(box)
                    End Select
                End Sub)
        End Sub

        ''' <summary>ENTER im Stichwortfeld fuegt hinzu wie der Knopf daneben; das Feld leert sich
        ''' (AddTag setzt den Text zurueck) und behaelt den Fokus fuer das naechste Stichwort. Steht
        ''' die Vorschlagsliste offen, verbraucht der Rahmen den ersten Druck fuer die Auswahl, und
        ''' dieses Ereignis kommt nicht an: erst der zweite Druck fuegt den gewaehlten Vorschlag
        ''' hinzu. Der Knopf wird als Nachbar im selben Raster gesucht, nicht ueber seinen Namen.</summary>
        Private Sub OnNewTagKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter OrElse e.KeyModifiers <> KeyModifiers.None Then Return
            Dim box = TryCast(sender, AutoCompleteBox)
            Dim addButton = TryCast(box?.Parent, Panel)?.Children.OfType(Of Button)().FirstOrDefault()
            Dim command = addButton?.Command
            If command Is Nothing Then Return
            If command.CanExecute(Nothing) Then command.Execute(Nothing)
            box.Text = ""
            e.Handled = True
            ' Erst NACH dem Neuaufbau, den das Hinzufuegen ausloest (Stichwortliste, Trennlinie,
            ' Filteroptionen der Galerie): dabei setzt Avalonia den Fokus auf niemanden, ein
            ' sofortiges Focus() ginge dabei verloren. Gezielt das innere Textfeld, damit der
            ' Cursor wirklich darin steht.
            Avalonia.Threading.Dispatcher.UIThread.Post(Sub() FocusTagInput(box), Avalonia.Threading.DispatcherPriority.Background)
        End Sub

        Private Shared Sub FocusTagInput(box As AutoCompleteBox)
            If box Is Nothing OrElse Not box.IsEffectivelyVisible OrElse Not box.IsEffectivelyEnabled Then Return
            Dim input = box.GetVisualDescendants().OfType(Of TextBox)().FirstOrDefault()
            If input IsNot Nothing Then
                input.Focus()
                input.CaretIndex = If(input.Text, "").Length
            Else
                box.Focus()
            End If
        End Sub

        ''' <summary>Steht die Vorschlagsliste offen, hat der Rahmen die Eingabetaste schon fuer die
        ''' Auswahl verbraucht und dieses Ereignis kommt gar nicht erst an - der erste Druck waehlt
        ''' den Vorschlag, der zweite uebernimmt ihn. Genau so soll es sein: ein versehentlich
        ''' uebernommener Vorschlag schiebt ein Gesicht zu einer fremden Person.
        '''
        ''' ENTER auf einem UNVERAENDERTEN Namen, den die Erkennung vergeben hat, bestaetigt ihn -
        ''' beim Durchgehen mit der Tastatur der Weg, ohne zur Maus zu greifen. Das blosse Verlassen
        ''' des Feldes bestaetigt nichts: wer durchtabbt, hat noch nichts entschieden.</summary>
        Private Sub OnPersonNameKeyDown(sender As Object, e As KeyEventArgs)
            ' Nur TAB und SHIFT+TAB. Mit STRG wechselt der Editor das Werkzeug, das bleibt ihm.
            If e.Key = Key.Tab AndAlso (e.KeyModifiers And (KeyModifiers.Control Or KeyModifiers.Alt Or KeyModifiers.Meta)) = KeyModifiers.None Then
                MoveToAdjacentName(TryCast(sender, AutoCompleteBox), e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                e.Handled = True
                Return
            End If
            If e.Key <> Key.Enter Then Return
            Dim box = TryCast(sender, AutoCompleteBox)
            Dim entry = TryCast(box?.DataContext, PersonFaceEntry)
            If entry IsNot Nothing AndAlso entry.IsSuggested AndAlso
               String.Equals(If(entry.Name, ""), If(box.Text, ""), StringComparison.Ordinal) Then
                Dim panel = TryCast(Me.DataContext, IInfoSidebarPanel)
                If panel IsNot Nothing Then panel.ConfirmFace(entry.FaceId)
            Else
                CommitPersonName(box)
            End If
            e.Handled = True
        End Sub

        ''' <summary>Laeuft gerade eine Uebernahme samt Rueckfrage? Die Rueckfrage nimmt dem Feld den
        ''' Fokus, und das loeste sonst dieselbe Uebernahme ein zweites Mal aus.</summary>
        Private _personNameCommitRunning As Boolean

        Private Async Sub CommitPersonName(box As AutoCompleteBox)
            If box Is Nothing OrElse _personNameCommitRunning Then Return
            ' Der Datenkontext der Zeile statt des Tags: gebraucht werden BEIDE Ids. Die Person sagt,
            ' welche Gruppe den Namen bekommt, das Gesicht, was der Benutzer angefasst hat - und
            ' Angefasstes bleibt bei einem erneuten Durchlauf stehen.
            Dim entry = TryCast(box.DataContext, PersonFaceEntry)
            If entry Is Nothing OrElse String.IsNullOrWhiteSpace(entry.Id) Then Return
            ' Nichts geaendert, nichts zu tun. Ohne das lief bei JEDEM Verlassen des Feldes ein
            ' Schreibvorgang samt Neuaufbau der Zeilen - auch beim blossen Durchtabben.
            Dim newName = If(box.Text, "")
            If String.Equals(If(entry.Name, ""), newName, StringComparison.Ordinal) Then Return

            ' Die Leiste kennt drei Datenkontexte - Galerie, Betrachter und Editor. Alle drei sagen
            ' ueber IInfoSidebarPanel zu, was hier gebraucht wird; eine Fallunterscheidung je Modus
            ' braucht es deshalb nicht.
            Dim panel = TryCast(Me.DataContext, IInfoSidebarPanel)
            If panel Is Nothing Then Return

            _personNameCommitRunning = True
            Try
                ' DAS FELD STEHT AN EINEM GESICHT, der Name kann aber die GANZE PERSON treffen: ein
                ' neuer Name an einer benannten Gruppe benennt sie um, ein geleertes Feld nimmt allen
                ' ihren Gesichtern den Namen. Das ist gewollt (ein Tippfehler wird so geradegezogen),
                ' aber nie still: aus einem abgeschnittenen Text wurde sonst der Name einer ganzen
                ' Sammlung (GitHub #143). Gefragt wird nur, wenn mehr als dieses eine Gesicht
                ' betroffen ist.
                If Not Await ConfirmGroupRenameAsync(entry, newName) Then
                    box.Text = If(entry.Name, "")
                    Return
                End If
                panel.RenamePerson(entry.Id, newName, entry.FaceId)
            Finally
                _personNameCommitRunning = False
            End Try
        End Sub

        ''' <summary>Fragt nach, bevor ein Name an einem Gesicht eine ganze benannte Person umbenennt
        ''' oder ihr den Namen nimmt. True heisst: weitermachen (auch dann, wenn gar nicht gefragt
        ''' werden musste oder kein Fenster zum Fragen da ist).</summary>
        Private Async Function ConfirmGroupRenameAsync(entry As PersonFaceEntry, newName As String) As Threading.Tasks.Task(Of Boolean)
            Dim affected = 0
            Try
                affected = FerrumPix.Services.LibraryService.Instance.CountFacesRenamedBy(entry.Id, newName)
            Catch ex As Exception
                FerrumPix.Services.DiagnosticLogService.LogException("InfoSidebar.ConfirmGroupRename", ex)
            End Try
            If affected <= 1 Then Return True
            Dim host = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            If host Is Nothing Then Return True
            Dim clean = newName.Trim()
            If clean.Length = 0 Then
                Return Await host.ShowConfirmAsync(
                    FerrumPix.Services.LocalizationService.T("Namen der ganzen Person entfernen?"),
                    String.Format(FerrumPix.Services.LocalizationService.T("{0} hat {1} Gesichter. Der Name wird bei allen entfernt, nicht nur bei diesem Gesicht."),
                                  entry.Name, affected),
                    "Namen entfernen")
            End If
            Return Await host.ShowConfirmAsync(
                FerrumPix.Services.LocalizationService.T("Ganze Person umbenennen?"),
                String.Format(FerrumPix.Services.LocalizationService.T("{0} hat {1} Gesichter. Die ganze Person heißt danach {2}, nicht nur dieses Gesicht."),
                              entry.Name, affected, clean),
                "Umbenennen")
        End Function

        ''' <summary>Loest ein falsch zugeordnetes Gesicht aus seiner Person.
        '''
        ''' Nur DIESES Gesicht: die Zuordnung anderer Bilder derselben Person bleibt, wie sie ist.
        ''' Das Gesicht bekommt eine eigene, namenlose Gruppe - danach traegt man den richtigen Namen
        ''' ein, und heisst schon jemand so, verschmelzen beide.</summary>
        Private Sub OnDetachFaceClick(sender As Object, e As RoutedEventArgs)
            Dim faceId = TryCast(TryCast(sender, Button)?.Tag, String)
            If String.IsNullOrWhiteSpace(faceId) Then Return
            Dim panel = TryCast(Me.DataContext, IInfoSidebarPanel)
            If panel Is Nothing Then Return
            panel.DetachFace(faceId)
        End Sub

        ''' <summary>TAB springt von Name zu Name, SHIFT+TAB zurueck; am Ende der Liste ins erste
        ''' Feld des naechsten Bildes, am Anfang ins letzte des vorigen.
        '''
        ''' Von Hand gefuehrt statt der Tab-Reihenfolge ueberlassen: dazwischen stuenden je Zeile die
        ''' beiden Knoepfe, und wer Bild fuer Bild Namen vergibt, will von Feld zu Feld. Der Name
        ''' wird VOR dem Sprung uebernommen; die Zeilen bleiben dabei stehen (RefreshPeopleInPlace),
        ''' sonst hinge der Fokus danach an einem Feld, das es nicht mehr gibt.</summary>
        Private Sub MoveToAdjacentName(box As AutoCompleteBox, backwards As Boolean)
            If box Is Nothing Then Return
            box.IsDropDownOpen = False
            CommitPersonName(box)

            Dim boxes = NameBoxes()
            Dim index = boxes.IndexOf(box)
            Dim target = index + If(backwards, -1, 1)
            If index >= 0 AndAlso target >= 0 AndAlso target < boxes.Count Then
                boxes(target).Focus(NavigationMethod.Tab)
                Return
            End If

            Dim panel = TryCast(Me.DataContext, IInfoSidebarPanel)
            If panel IsNot Nothing Then panel.RequestAdjacentImage(If(backwards, -1, 1))
        End Sub

        ''' <summary>TAB auf dem Hinweis eines Bildes ohne Gesichter: weiter zum naechsten Bild.
        ''' Ohne das endete die Kette am ersten solchen Bild, denn dort gibt es kein Namensfeld.</summary>
        Private Sub OnNoPeopleHintKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Tab OrElse
               (e.KeyModifiers And (KeyModifiers.Control Or KeyModifiers.Alt Or KeyModifiers.Meta)) <> KeyModifiers.None Then Return
            Dim panel = TryCast(Me.DataContext, IInfoSidebarPanel)
            If panel IsNot Nothing Then panel.RequestAdjacentImage(If(e.KeyModifiers.HasFlag(KeyModifiers.Shift), -1, 1))
            e.Handled = True
        End Sub

        ''' <summary>Die Namensfelder in der Reihenfolge der Liste, also links vor rechts im Bild.</summary>
        Private Function NameBoxes() As List(Of AutoCompleteBox)
            Dim list = Me.FindControl(Of ItemsControl)("PeopleList")
            If list Is Nothing Then Return New List(Of AutoCompleteBox)()
            Return list.GetVisualDescendants().OfType(Of AutoCompleteBox)().ToList()
        End Function

        Private _focusSource As IInfoSidebarPanel

        Protected Overrides Sub OnDataContextChanged(e As EventArgs)
            MyBase.OnDataContextChanged(e)
            If _focusSource IsNot Nothing Then RemoveHandler _focusSource.FocusFaceRequested, AddressOf OnFocusFaceRequested
            _focusSource = TryCast(Me.DataContext, IInfoSidebarPanel)
            If _focusSource IsNot Nothing Then AddHandler _focusSource.FocusFaceRequested, AddressOf OnFocusFaceRequested
        End Sub

        ''' <summary>Das neue Bild steht in der Leiste. Die Zeilen sind dann gerade erst in die
        ''' Sammlung gekommen, ihre Felder entstehen aber erst beim naechsten Layout - deshalb nach
        ''' hinten gestellt. Ohne Gesichter bekommt der Hinweis den Fokus, damit TAB weiterfuehrt.</summary>
        Private Sub OnFocusFaceRequested(sender As Object, e As FaceFocusRequest)
            Avalonia.Threading.Dispatcher.UIThread.Post(
                Sub()
                    Dim boxes = NameBoxes()
                    ' Ein bestimmtes Gesicht (eben von Hand markiert): sein Feld, damit der Name
                    ' gleich hineingeht.
                    Dim wanted = If(String.IsNullOrEmpty(e.FaceId), Nothing,
                                    boxes.FirstOrDefault(Function(b) String.Equals(TryCast(b.DataContext, PersonFaceEntry)?.FaceId, e.FaceId, StringComparison.Ordinal)))
                    If wanted IsNot Nothing Then
                        FocusTagInput(wanted)
                    ElseIf boxes.Count > 0 Then
                        boxes(If(e.ToLast, boxes.Count - 1, 0)).Focus(NavigationMethod.Tab)
                    Else
                        Me.FindControl(Of Border)("NoPeopleHint")?.Focus(NavigationMethod.Tab)
                    End If
                End Sub, Avalonia.Threading.DispatcherPriority.Background)
        End Sub

        ''' <summary>Bestaetigt einen Namen, den die Erkennung vergeben hat. Name und Gruppe bleiben;
        ''' nur dieses Gesicht gilt danach als von Hand gesetzt.</summary>
        Private Sub OnConfirmFaceClick(sender As Object, e As RoutedEventArgs)
            Dim faceId = TryCast(TryCast(sender, Button)?.Tag, String)
            If String.IsNullOrWhiteSpace(faceId) Then Return
            Dim panel = TryCast(Me.DataContext, IInfoSidebarPanel)
            If panel Is Nothing Then Return
            panel.ConfirmFace(faceId)
        End Sub
    End Class

End Namespace
