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

        ''' <summary>Steht der Abschnitt ueberhaupt? Ein leerer Kasten unter jedem Landschaftsfoto
        ''' waere nur Platzverbrauch.</summary>
        ReadOnly Property HasPeople As Boolean

        ''' <summary>Benennt die GANZE Gruppe, also jedes Bild darin. Das angefasste Gesicht
        ''' gilt danach als von Hand gesetzt und ueberlebt jeden weiteren Durchlauf.</summary>
        Sub RenamePerson(personId As String, newName As String, faceId As String)

        ''' <summary>Loest GENAU DIESES Gesicht aus seiner Gruppe. Fuer eine Fehlzuordnung, und
        ''' ausdruecklich ohne Wirkung auf andere Bilder derselben Person.</summary>
        Sub DetachFace(faceId As String)

        ''' <summary>Der Aufnahmeort als fertige Zeile, etwa "Norden, Deutschland". Leer, wenn keiner
        ''' bekannt ist - dann faellt der Abschnitt weg.</summary>
        ReadOnly Property PlaceText As String

        ReadOnly Property HasPlace As Boolean

    End Interface

End Namespace
