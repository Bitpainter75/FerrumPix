Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.VisualTree

Namespace Views

    ''' <summary>Was ein Namensfeld mit Vorschlagsliste uebernimmt, wenn es den Fokus verliert.
    ''' Gemeinsam fuer die Infoleiste und den Personenbereich, damit beide dieselbe Regel haben.
    '''
    ''' DER BEFUND (GitHub #143): wer "Dad" tippte und in der Liste "Daddy (me)" anklickte, bekam
    ''' "Dad". Der Klick in die Liste nimmt dem Feld den Fokus, die Uebernahme lief mit dem Text
    ''' von vor dem Klick, und der gewaehlte Vorschlag kam nie an. Ein Durchlauf Aufschub reichte
    ''' dagegen nicht: zwischen Druecken und Loslassen der Maustaste liegen viele Durchlaeufe.
    '''
    ''' Deshalb wird nicht mehr auf den richtigen Zeitpunkt gehofft, sondern nachgesehen, WOHIN
    ''' der Fokus gegangen ist.</summary>
    Friend Module PersonNameField

        ''' <summary>Was beim Fokusverlust zu tun ist.</summary>
        Friend Enum LostFocusAction
            ''' <summary>Nichts uebernehmen: die Liste ist noch offen oder der Fokus steht wieder im Feld.</summary>
            Wait
            ''' <summary>Den Text des Feldes uebernehmen: der Benutzer hat es verlassen.</summary>
            CommitText
            ''' <summary>Den angeklickten Vorschlag uebernehmen.</summary>
            CommitSuggestion
        End Enum

        ''' <summary>Entscheidet nach einem Fokusverlust von <paramref name="box"/>, gerufen einen
        ''' Durchlauf spaeter, wenn der neue Fokus feststeht.
        '''
        ''' - Liegt der Fokus auf einem Eintrag der EIGENEN Vorschlagsliste, ist dieser Eintrag
        '''   gemeint: <paramref name="suggestion"/> traegt seinen Namen.
        ''' - Steht die Liste noch offen oder ist der Fokus wieder im Feld, laeuft eine Auswahl
        '''   oder der Benutzer tippt weiter: warten.
        ''' - Sonst hat er das Feld verlassen, und sein Text gilt.</summary>
        Friend Function AfterLostFocus(box As AutoCompleteBox, ByRef suggestion As String) As LostFocusAction
            suggestion = Nothing
            If box Is Nothing Then Return LostFocusAction.Wait
            Dim focused = TryCast(TopLevel.GetTopLevel(box)?.FocusManager?.GetFocusedElement(), Visual)
            Dim picked = SuggestionUnder(focused, box)
            If picked IsNot Nothing Then
                suggestion = picked
                Return LostFocusAction.CommitSuggestion
            End If
            If box.IsDropDownOpen OrElse box.IsKeyboardFocusWithin Then Return LostFocusAction.Wait
            Return LostFocusAction.CommitText
        End Function

        ''' <summary>Der Vorschlag, auf dem <paramref name="element"/> liegt, wenn er zur Liste von
        ''' <paramref name="box"/> gehoert; sonst Nothing. Die Liste steht in der Vorlage des Feldes,
        ''' ihr Besitzer ist also das Feld selbst, auch wenn sie in einem eigenen Fenster gezeichnet
        ''' wird.</summary>
        Private Function SuggestionUnder(element As Visual, box As AutoCompleteBox) As String
            If element Is Nothing Then Return Nothing
            Dim item = TryCast(element, ListBoxItem)
            If item Is Nothing Then item = element.FindAncestorOfType(Of ListBoxItem)()
            If item Is Nothing Then Return Nothing
            Dim list = item.FindAncestorOfType(Of ListBox)()
            If list Is Nothing OrElse Not ReferenceEquals(list.TemplatedParent, box) Then Return Nothing
            Dim name = TryCast(item.DataContext, String)
            If name Is Nothing Then name = TryCast(item.Content, String)
            Return If(String.IsNullOrWhiteSpace(name), Nothing, name)
        End Function

    End Module

End Namespace
