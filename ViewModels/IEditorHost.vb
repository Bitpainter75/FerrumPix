Imports System
Imports System.Collections.Generic
Imports System.Threading.Tasks

Namespace ViewModels

    ''' <summary>Alles, was der Editor vom Anwendungsrahmen braucht - und sonst nichts.
    '''
    ''' Dieselbe Begruendung wie bei <see cref="IViewerHost"/>: der Konstruktor verlangte vorher das
    ''' ganze <see cref="MainWindowViewModel"/>. Das ist der Anwendungsrumpf, dessen Konstruktor
    ''' Galerie, Betrachter und Einstellungen aufbaut, die gespeicherten Einstellungen liest und
    ''' gleich den Startordner oeffnet. Ein Pruefstand, der nur eine Rechenstufe des Editors messen
    ''' will, zieht damit die halbe Anwendung samt Ordnerscan hoch - und bekaeme einen Editor, dessen
    ''' Ausgangszustand von den Einstellungen des Nutzers abhaengt.
    '''
    ''' Genau deshalb lagen die geometrischen Umrechnungen des Editors (Anzeigeraum gegen Quellraum,
    ''' Stuetzpunktraster der Gitterverzerrung, Objekt- und Maskenkoordinaten unter Beschnitt und
    ''' Drehung) ohne einen einzigen Waechter in der Luft: sie sind reine Rechnung, aber ohne
    ''' instanziierbares ViewModel nicht messbar.
    '''
    ''' Die Liste hier ist vollstaendig - der Editor fasst nichts anderes am Rahmen an. Wer eine
    ''' weitere Abhaengigkeit einfuehrt, traegt sie hier ein; waechst die Liste, ist das das Zeichen,
    ''' dass die Zustaendigkeit verrutscht.
    '''
    ''' <see cref="Settings"/>, <see cref="Gallery"/> und <see cref="Viewer"/> duerfen Nothing sein:
    ''' der Editor prueft an jeder Lesestelle darauf und faellt auf die Vorgabewerte zurueck. Der
    ''' Rahmen selbst darf ebenfalls Nothing sein - dann sind Dialoge und Moduswechsel wirkungslos,
    ''' was fuer einen Pruefstand ohne Fenster genau richtig ist.</summary>
    Public Interface IEditorHost

        ReadOnly Property Settings As SettingsViewModel
        ReadOnly Property Gallery As GalleryViewModel
        ReadOnly Property Viewer As ViewerViewModel

        Property CurrentMode As AppMode
        Sub ToggleFullscreen()
        Sub RefreshWindowTitle()
        ''' <summary>Zur Galerie wechseln und dort alle Bilder mit diesem Stichwort zeigen.</summary>
        Sub OpenTagSearchInGallery(tag As String)
        Sub ShowNewDocumentDialog()
        Sub ShowGalleryAtRealFolder()
        ''' <summary>Umbenennen laeuft ueber den Rahmen, weil dort der Dialog sitzt. Der
        ''' Rueckruf bekommt den NEUEN Pfad - der Editor muss sein geoeffnetes Bild nachziehen.</summary>
        Sub RequestRenamePath(itemPath As String, Optional afterRename As Action(Of String) = Nothing)

        Function ShowMessageAsync(titleText As String, messageText As String,
                                  Optional confirmText As String = "OK") As Task
        Function ShowConfirmAsync(titleText As String, messageText As String,
                                  Optional confirmText As String = "OK",
                                  Optional cancelText As String = "Abbrechen") As Task(Of Boolean)
        Function ShowSaveChangesAsync(titleText As String,
                                      messageText As String) As Task(Of SaveChangesDialogResult)
        ''' <summary>Die Frage nach einer gleichnamigen Datei. Gehoert hierher und nicht in den
        ''' Editor: den Dialog kennt der Anwendungsrahmen, der Editor kennt nur die Antwort.</summary>
        Function ShowFileConflictAsync(existingPath As String,
                                       incomingPath As String) As Task(Of FileConflictDialogResult)
        Function ShowSaveAsAsync(titleText As String,
                                 messageText As String,
                                 initialBaseName As String,
                                 initialFormat As String,
                                 Optional initialJpgQuality As Integer = 0,
                                 Optional confirmText As String = "Speichern",
                                 Optional cancelText As String = "Abbrechen") As Task(Of SaveAsDialogResult)

        Sub RequestDeletePaths(paths As IEnumerable(Of String),
                               Optional afterDelete As Action = Nothing,
                               Optional beforeDelete As Action = Nothing)
        Sub ReloadThumbnailsForFile(path As String)
        Sub ShowPrintDialog(imagePaths As IEnumerable(Of String),
                            Optional title As String = Nothing,
                            Optional tempFile As String = Nothing)

    End Interface

End Namespace
