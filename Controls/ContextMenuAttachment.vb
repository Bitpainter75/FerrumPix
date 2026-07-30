Imports System
Imports Avalonia.Controls
Imports Avalonia.Input

Namespace Controls

    ''' <summary>
    ''' Haengt das Kontextmenue einer Ansicht so an, dass ZUERST die Eintraege gebaut werden und
    ''' DANACH das Menue aufgeht.
    '''
    ''' Warum das eine eigene Stelle braucht: ein ContextMenu meldet sich beim Anhaengen selbst auf
    ''' ContextRequested des Elements an und oeffnet sich dort. Ein Handler weiter OBEN im Baum
    ''' kommt in der Blasenphase erst danach dran - das Menue stand dann schon offen, gefuellt mit
    ''' dem, was zuletzt gebaut worden war. Genau so erschien in Betrachter und Editor an jeder
    ''' Stelle dasselbe Menue, und der Aufrufort blieb wirkungslos.
    '''
    ''' Zwei Handler am GLEICHEN Element laufen dagegen in der Reihenfolge ihrer Anmeldung. Also
    ''' wird das Menue kurz abgehaengt, unser Handler angemeldet und das Menue wieder angehaengt.
    ''' Danach steht die Reihenfolge fest: fuellen, dann oeffnen.
    '''
    ''' Der Handler darf das Ereignis NICHT als behandelt melden - sonst oeffnet das Menue nicht.
    ''' Umgekehrt ist genau das der Weg, das Oeffnen zu verhindern: die rechte Taste hat auf den
    ''' Buehnen bereits eine Bedeutung (zoomen im Betrachter, schwenken im Editor).
    ''' </summary>
    Public NotInheritable Class ContextMenuAttachment

        Private Sub New()
        End Sub

        Public Shared Sub Attach(root As Control, populate As EventHandler(Of ContextRequestedEventArgs))
            If root Is Nothing OrElse populate Is Nothing Then Return

            Dim menu = root.ContextMenu
            root.ContextMenu = Nothing
            root.AddHandler(Control.ContextRequestedEvent, populate)
            root.ContextMenu = menu
        End Sub

    End Class

End Namespace
