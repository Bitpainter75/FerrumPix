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
        ''' Die Id steht im Tag der Textbox, nicht im Text - benannt wird ueber die Id, damit eine
        ''' Umbenennung die Zuordnung nicht verliert.</summary>
        Private Sub OnPersonNameCommitted(sender As Object, e As RoutedEventArgs)
            CommitPersonName(TryCast(sender, TextBox))
        End Sub

        Private Sub OnPersonNameKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then Return
            CommitPersonName(TryCast(sender, TextBox))
            e.Handled = True
        End Sub

        Private Sub CommitPersonName(box As TextBox)
            If box Is Nothing Then Return
            ' Der Datenkontext der Zeile statt des Tags: gebraucht werden BEIDE Ids. Die Person sagt,
            ' welche Gruppe den Namen bekommt, das Gesicht, was der Benutzer angefasst hat - und
            ' Angefasstes bleibt bei einem erneuten Durchlauf stehen.
            Dim entry = TryCast(box.DataContext, PersonFaceEntry)
            If entry Is Nothing OrElse String.IsNullOrWhiteSpace(entry.Id) Then Return

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
