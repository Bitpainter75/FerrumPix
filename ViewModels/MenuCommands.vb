Imports System.Windows.Input

Namespace ViewModels

    ''' <summary>
    ''' Die Kommandos, die ein Bereich fuer sein Kontextmenue anbietet. Nicht gesetzte bleiben
    ''' Nothing - der Bauplan laesst den zugehoerigen Eintrag dann weg.
    '''
    ''' Warum ein Buendel und keine Schnittstelle je ViewModel: Galerie, Betrachter und Editor
    ''' benennen dieselbe Sache verschieden (ResizeSelectedCommand gegen ResizeCurrentCommand), und
    ''' nicht jeder kann alles. Ein Buendel macht diese Zuordnung an EINER Stelle sichtbar, statt
    ''' sie ueber drei ViewModels zu verteilen.
    ''' </summary>
    Public NotInheritable Class MenuCommands

        ' Immer da, unabhaengig vom Bild
        Public Property NewImage As ICommand
        Public Property Fullscreen As ICommand

        ' Oeffnen und Vergleichen
        Public Property ShowImage As ICommand
        Public Property Adjust As ICommand
        Public Property Compare As ICommand
        Public Property PinImage As ICommand

        ' Speichern (nur Editor)
        Public Property Save As ICommand
        Public Property SaveAs As ICommand

        ' Dateiarbeit
        Public Property NewFolder As ICommand
        Public Property Rename As ICommand
        Public Property Copy As ICommand
        Public Property Cut As ICommand
        Public Property Paste As ICommand
        Public Property Duplicate As ICommand
        Public Property Delete As ICommand

        ''' <summary>Zurueck aus dem Papierkorb einer Serverquelle. Nur die Galerie bietet es an, und
        ''' auch dort nur in der Papierkorbansicht.</summary>
        Public Property RestoreFromTrash As ICommand

        ' Stapelarbeit am Bild
        Public Property ResizeImage As ICommand
        Public Property ApplyWatermark As ICommand
        Public Property ApplyFilter As ICommand
        Public Property ConvertTo As ICommand
        Public Property ExportTo As ICommand
        Public Property RemoveMetadata As ICommand
        Public Property CreateCollage As ICommand
        Public Property Print As ICommand

        ' Bild drehen (Betrachter und Editor)
        Public Property RotateLeft As ICommand
        Public Property RotateRight As ICommand

        ' Bewertung, Favorit, Etikett
        Public Property Favorite As ICommand
        Public Property Rating As ICommand
        Public Property ColorLabel As ICommand

        ''' <summary>Aufnahmeort: den eines Bildes merken, den gemerkten auf eine Auswahl anwenden,
        ''' oder einen im Dialog bestimmen. Kopieren gilt fuer EIN Bild, Einfuegen und Setzen fuer
        ''' die ganze Auswahl - der Regelfall ist eine Reihe Aufnahmen vom selben Ort.</summary>
        Public Property CopyPlace As ICommand
        Public Property PastePlace As ICommand
        Public Property SetPlace As ICommand
        Public Property RemovePlace As ICommand
        Public Property SetCopyright As ICommand
        Public Property OpenPlaceInOsm As ICommand

        ' Wege nach draussen
        Public Property CopyPath As ICommand
        Public Property ShowInFileManager As ICommand

    End Class

End Namespace
