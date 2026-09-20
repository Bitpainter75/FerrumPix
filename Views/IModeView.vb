Namespace Views

    ''' <summary>
    ''' Eine Ansicht, die beim Betreten ihres Modus etwas nachholen muss.
    '''
    ''' Seit die Ansichten BEHALTEN statt neu gebaut werden (siehe MainWindow.ShowCurrentContent),
    ''' feuert `AttachedToVisualTree` nur noch EINMAL je Ansicht. Alles, was dort bisher bei jedem
    ''' Moduswechsel lief - Fokus setzen, Einpassen, zum aktuellen Bild rollen, Kacheln nachladen -,
    ''' braucht deshalb einen eigenen Anlass. Genau der ist das hier.
    '''
    ''' Die Trennung ist auch die richtige: was EINMAL gilt (Ereignisse anmelden, Steuerelemente
    ''' suchen), gehoert weiter ans Anhaengen; was bei JEDEM Betreten gilt, hierher. Vorher lag
    ''' beides im selben Handler und war nur deshalb nicht zu unterscheiden, weil die Ansicht
    ''' ohnehin jedes Mal neu entstand.
    ''' </summary>
    Public Interface IModeView

        ''' <summary>Der Modus dieser Ansicht ist gerade betreten worden. Wird NICHT beim ersten Mal
        ''' gerufen - da erledigt das Anhaengen an den Baum dieselbe Arbeit.</summary>
        Sub OnModeEntered()

    End Interface

End Namespace
