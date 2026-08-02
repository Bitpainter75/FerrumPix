Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Markup.Xaml
Imports Avalonia.Interactivity
Imports FerrumPix.Models
Imports FerrumPix.ViewModels

Namespace Views

    Public Class InfoSidebarView
        Inherits UserControl

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
        ''' benannte die Gruppe "Chr". In Patricks Bibliothek steht genau so ein abgeschnittener Name.
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
        ''' uebernommener Vorschlag schiebt ein Gesicht zu einer fremden Person.</summary>
        Private Sub OnPersonNameKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then Return
            CommitPersonName(TryCast(sender, AutoCompleteBox))
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
    End Class

End Namespace
