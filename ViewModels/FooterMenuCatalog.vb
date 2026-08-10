Imports System
Imports System.Collections.Generic
Imports System.Windows.Input
Imports FerrumPix.Models
Imports FerrumPix.Services
Imports Avalonia.Controls

Namespace ViewModels

    ''' <summary>
    ''' Beschriftung und Symbol jeder Fussmenue-Aktion, an EINER Stelle.
    '''
    ''' Der Bereich stellt seine Liste selbst zusammen und uebergibt nur die Kommandos - was er
    ''' nicht aufnimmt, gibt es dort nicht. So steht der Text einmal, das Symbol einmal, und die
    ''' Uebersetzung geht durch einen einzigen Aufruf.
    '''
    ''' Hier steht nur, WIE ein Eintrag heisst und aussieht. WANN er erscheint, entscheidet
    ''' <see cref="ContextMenuBuilder"/> aus Aufrufort und Auswahl - die Regeln dazu stehen in
    ''' Audits/KONTEXTMENUE.md.
    ''' </summary>
    Public NotInheritable Class FooterMenuCatalog

        Private Sub New()
        End Sub

        ''' <summary>Ein Trenner. Avalonia nimmt ein Separator-Objekt aus der Liste direkt als
        ''' Behaelter, es braucht also keine Sondermarke im Modell. Jeder Aufruf liefert ein
        ''' EIGENES - ein Steuerelement kann nur an genau einer Stelle im Baum haengen.</summary>
        Public Shared Function Divider() As Object
            Return New Separator()
        End Function

        Private Shared Function Build(text As String, iconName As String, command As ICommand) As AppAction
            Return New AppAction(LocalizationService.T(text), iconName, command)
        End Function

        Public Shared Function NewImage(c As ICommand) As AppAction
            Return Build("Neues Bild", "photo-plus", c)
        End Function

        Public Shared Function Fullscreen(c As ICommand) As AppAction
            Return Build("Vollbild", "arrows-maximize", c)
        End Function

        Public Shared Function Adjust(c As ICommand) As AppAction
            Return Build("Anpassen", "exposure", c)
        End Function

        Public Shared Function Save(c As ICommand) As AppAction
            Return Build("Speichern", "device-floppy", c)
        End Function

        Public Shared Function SaveAs(c As ICommand) As AppAction
            Return Build("Speichern unter", "file-export", c)
        End Function

        Public Shared Function PinImage(c As ICommand) As AppAction
            Return Build("Bild anheften", "pin", c)
        End Function

        ' Bewertung, Favorit und Etikett stehen NICHT hier: sie sind keine Beschriftungen mit
        ' Symbol, sondern eigene Zeilen aus Sternen, Herz und Farbkreisen. Siehe MenuWidgets.

        Public Shared Function ShowImage(c As ICommand) As AppAction
            Return Build("Anzeigen", "eye", c)
        End Function

        Public Shared Function Compare(c As ICommand) As AppAction
            Return Build("Vergleichen", "columns", c)
        End Function

        Public Shared Function NewFolder(c As ICommand) As AppAction
            Return Build("Neuer Ordner", "folder-plus", c)
        End Function

        Public Shared Function Copy(c As ICommand) As AppAction
            Return Build("Kopieren", "copy", c)
        End Function

        Public Shared Function Cut(c As ICommand) As AppAction
            Return Build("Ausschneiden", "cut", c)
        End Function

        Public Shared Function Paste(c As ICommand) As AppAction
            Return Build("Einfügen", "clipboard", c)
        End Function

        Public Shared Function Duplicate(c As ICommand) As AppAction
            Return Build("Duplizieren", "copy", c)
        End Function

        Public Shared Function RemoveMetadata(c As ICommand) As AppAction
            Return Build("Metadaten entfernen", "eraser", c)
        End Function

        Public Shared Function CreateCollage(c As ICommand) As AppAction
            Return Build("Collage erstellen", "layout-grid", c)
        End Function

        Public Shared Function Rename(c As ICommand) As AppAction
            Return Build("Umbenennen", "cursor-text", c)
        End Function

        Public Shared Function ResizeImage(c As ICommand) As AppAction
            Return Build("Bildgröße ändern", "resize", c)
        End Function

        Public Shared Function ApplyWatermark(c As ICommand) As AppAction
            Return Build("Wasserzeichen anwenden", "droplet", c)
        End Function

        Public Shared Function ApplyFilter(c As ICommand) As AppAction
            Return Build("Filter anwenden", "filter", c)
        End Function

        Public Shared Function ConvertTo(c As ICommand) As AppAction
            Return Build("Konvertieren nach", "transform", c)
        End Function

        Public Shared Function ExportTo(c As ICommand) As AppAction
            Return Build("Exportieren nach", "upload", c)
        End Function

        Public Shared Function Print(c As ICommand) As AppAction
            Return Build("Drucken", "printer", c)
        End Function

        Public Shared Function RotateLeft(c As ICommand) As AppAction
            Return Build("Nach links drehen", "rotate-2", c)
        End Function

        Public Shared Function RotateRight(c As ICommand) As AppAction
            Return Build("Nach rechts drehen", "rotate-clockwise-2", c)
        End Function

        Public Shared Function CopyPath(c As ICommand) As AppAction
            Return Build("Pfad kopieren", "copy", c)
        End Function

        Public Shared Function ShowInFileManager(c As ICommand) As AppAction
            Return Build("Im Dateimanager zeigen", "folder-open", c)
        End Function

        Public Shared Function Delete(c As ICommand) As AppAction
            Return Build("Löschen", "trash", c)
        End Function

        ''' <summary>Die einzige Geste im Papierkorb einer Serverquelle. Der Server legt die Datei
        ''' dorthin zurueck, wo sie herkam.</summary>
        Public Shared Function RestoreFromTrash(c As ICommand) As AppAction
            Return Build("Wiederherstellen", "restore", c)
        End Function

    End Class

End Namespace
