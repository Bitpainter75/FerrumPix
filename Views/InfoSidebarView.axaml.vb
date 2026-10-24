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
        ''' Umbenennung die Zuordnung nicht verliert.</summary>
        ''' <summary>Uebernommen wird beim Verlassen des Feldes - ABER NICHT, solange die
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
            Avalonia.Threading.Dispatcher.UIThread.Post(Sub() CommitPersonName(box))
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

        Private Sub CommitPersonName(box As AutoCompleteBox)
            If box Is Nothing Then Return
            ' Der Datenkontext der Zeile statt des Tags: gebraucht werden BEIDE Ids. Die Person sagt,
            ' welche Gruppe den Namen bekommt, das Gesicht, was der Benutzer angefasst hat - und
            ' Angefasstes bleibt bei einem erneuten Durchlauf stehen.
            Dim entry = TryCast(box.DataContext, PersonFaceEntry)
            If entry Is Nothing OrElse String.IsNullOrWhiteSpace(entry.Id) Then Return
            ' Nichts geaendert, nichts zu tun. Ohne das lief bei JEDEM Verlassen des Feldes ein
            ' Schreibvorgang samt Neuaufbau der Zeilen - auch beim blossen Durchtabben.
            If String.Equals(If(entry.Name, ""), If(box.Text, ""), StringComparison.Ordinal) Then Return

            ' Die Leiste kennt drei Datenkontexte - Galerie, Betrachter und Editor. Alle drei sagen
            ' ueber IInfoSidebarPanel zu, was hier gebraucht wird; eine Fallunterscheidung je Modus
            ' braucht es deshalb nicht.
            Dim panel = TryCast(Me.DataContext, IInfoSidebarPanel)
            If panel Is Nothing Then Return
            panel.RenamePerson(entry.Id, If(box.Text, ""), entry.FaceId)
        End Sub

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
                    If boxes.Count > 0 Then
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
