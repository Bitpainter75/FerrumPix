Imports System
Imports System.Collections.Generic
Imports System.Threading.Tasks

Namespace ViewModels

    ''' <summary>Alles, was der Betrachter vom Anwendungsrahmen braucht — und sonst nichts.
    '''
    ''' Vorher verlangte sein Konstruktor das ganze <see cref="MainWindowViewModel"/>. Das ist der
    ''' Anwendungsrumpf: sein Konstruktor baut Galerie, Editor und Einstellungen auf, liest die
    ''' gespeicherten Einstellungen und öffnet gleich den Startordner. Ein Prüfstand, der nur den
    ''' Bildvergleich messen will, zieht damit die halbe Anwendung samt Ordnerscan hoch — und
    ''' bekommt einen Betrachter, dessen Ausgangszustand von den Einstellungen des Nutzers abhängt.
    ''' Deshalb hing der Vergleich (geteilter Zoom, Fokus, Anheften, Weiterblättern der rechten
    ''' Fläche) ohne einen einzigen Wächter in der Luft.
    '''
    ''' Die Liste hier ist vollständig — der Betrachter fasst nichts anderes am Rahmen an. Wer eine
    ''' weitere Abhängigkeit einführt, trägt sie hier ein; wächst die Liste, ist das das Zeichen,
    ''' dass die Zuständigkeit verrutscht.
    '''
    ''' <see cref="Gallery"/>, <see cref="Editor"/> und <see cref="Settings"/> dürfen Nothing sein:
    ''' der Betrachter prüft an jeder Lesestelle darauf und fällt auf die Vorgabewerte zurück.</summary>
    Public Interface IViewerHost

        ReadOnly Property Settings As SettingsViewModel
        ReadOnly Property Gallery As GalleryViewModel
        ReadOnly Property Editor As EditorViewModel

        Property CurrentMode As AppMode
        Sub ToggleFullscreen()
        Sub RefreshWindowTitle()
        Sub ShowNewDocumentDialog()
        ReadOnly Property IsFullscreen As Boolean
        Sub EnterFullscreen()

        Function OpenImageInEditor(path As String,
                                   Optional allPaths As List(Of String) = Nothing,
                                   Optional cacheScopeId As String = Nothing,
                                   Optional cacheScopeName As String = Nothing,
                                   Optional forceSaveAsOnly As Boolean = False,
                                   Optional immichAlbumId As String = Nothing) As Task
        Sub BackToGallery(Optional sourcePath As String = Nothing)
        ''' <summary>Zur Galerie wechseln und dort alle Bilder mit diesem Stichwort zeigen.</summary>
        Sub OpenTagSearchInGallery(tag As String)

        Function ShowMessageAsync(titleText As String, messageText As String,
                                  Optional confirmText As String = "OK") As Task
        Function ShowConfirmAsync(titleText As String, messageText As String,
                                  Optional confirmText As String = "OK",
                                  Optional cancelText As String = "Abbrechen") As Task(Of Boolean)

        Sub RequestDeletePaths(paths As IEnumerable(Of String),
                               Optional afterDelete As Action = Nothing,
                               Optional beforeDelete As Action = Nothing)
        Sub RequestRenamePath(itemPath As String, Optional afterRename As Action(Of String) = Nothing)

        Sub ReloadThumbnailsForFile(path As String)
        Sub ShowPrintDialog(imagePaths As IEnumerable(Of String),
                            Optional title As String = Nothing,
                            Optional tempFile As String = Nothing)

    End Interface

End Namespace
