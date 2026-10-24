Imports System.Collections.ObjectModel
Imports FerrumPix.Models

Namespace ViewModels

    ''' <summary>Was die Infoleiste braucht, um Personen zu zeigen und zu berichtigen.
    '''
    ''' Die Leiste ist EIN Steuerelement fuer Galerie, Betrachter und Editor, hat aber drei
    ''' Datenkontexte. Ohne gemeinsame Zusage muesste ihr Quelltext dreimal dieselbe Fallunterscheidung
    ''' fuehren und bei jedem neuen Handgriff wieder - mit ihr fragt er einmal danach.</summary>
    Public Interface IInfoSidebarPanel

        ''' <summary>Die Gesichter des gezeigten Bildes. Leer, solange die Erkennung aus ist oder
        ''' mehrere Bilder markiert sind.</summary>
        ReadOnly Property People As ObservableCollection(Of PersonFaceEntry)

        ''' <summary>Steht auf dem Bild jemand? Entscheidet zwischen Liste und Hinweis.</summary>
        ReadOnly Property HasPeople As Boolean

        ''' <summary>Steht der Personen-Reiter? Bei eingeschalteter Erkennung und einem Bild immer,
        ''' auch ohne Gesichter - sonst springt die Leiste beim Durchblaettern weg.</summary>
        ReadOnly Property HasPeopleTab As Boolean

        ''' <summary>Die schon vergebenen Namen, fuer die Vorschlagsliste am Namensfeld. Einen
        ''' vorhandenen Namen ein zweites Mal zu tippen ist die Stelle, an der aus einer Person zwei
        ''' werden - fuer die Bibliothek sind "Anna" und "anna" zwei Menschen.</summary>
        ReadOnly Property PersonNameSuggestions As ObservableCollection(Of String)

        ''' <summary>Benennt die GANZE Gruppe, also jedes Bild darin. Das angefasste Gesicht
        ''' gilt danach als von Hand gesetzt und ueberlebt jeden weiteren Durchlauf.</summary>
        Sub RenamePerson(personId As String, newName As String, faceId As String)

        ''' <summary>Loest GENAU DIESES Gesicht aus seiner Gruppe. Fuer eine Fehlzuordnung, und
        ''' ausdruecklich ohne Wirkung auf andere Bilder derselben Person.</summary>
        Sub DetachFace(faceId As String)

        ''' <summary>Bestaetigt eine selbsttaetige Zuordnung, ohne den Namen anzufassen. Das Gesicht
        ''' gilt danach als von Hand gesetzt, genau wie nach einer Benennung.</summary>
        Sub ConfirmFace(faceId As String)

        ''' <summary>Wechselt zum Nachbarbild (+1 weiter, -1 zurueck) und merkt sich, danach ein
        ''' Namensfeld zu fokussieren. False, wenn der Besitzer kein Blaettern anbietet.</summary>
        Function RequestAdjacentImage(delta As Integer) As Boolean

        ''' <summary>Das neue Bild steht: jetzt ein Namensfeld fokussieren.</summary>
        Event FocusFaceRequested As EventHandler(Of FaceFocusRequest)

        ''' <summary>Der Aufnahmeort als fertige Zeile, etwa "Norden, Deutschland". Leer, wenn keiner
        ''' bekannt ist - dann faellt der Abschnitt weg.</summary>
        ReadOnly Property PlaceText As String

        ReadOnly Property HasPlace As Boolean

    End Interface

    ''' <summary>Welches Namensfeld nach dem Bildwechsel den Fokus bekommt: das erste beim Weg
    ''' vorwaerts, das letzte beim Weg zurueck.</summary>
    Public NotInheritable Class FaceFocusRequest
        Inherits EventArgs

        Public Sub New(toLast As Boolean)
            Me.ToLast = toLast
        End Sub

        Public ReadOnly Property ToLast As Boolean
    End Class

End Namespace
