Namespace Models

    ''' <summary>
    ''' WO ein Kontextmenue geoeffnet wurde. Zusammen mit den betroffenen <see cref="ImageItem"/>
    ''' ist das alles, was der Bauplan braucht, um zu entscheiden, welche Aktionen erlaubt sind.
    '''
    ''' Warum der Ort ueberhaupt zaehlt: dieselbe Aktion bedeutet nicht ueberall dasselbe. Auf der
    ''' Buehne des Editors ist "Anpassen" sinnlos, wir SIND dort. Im Filmstreifen meint ein
    ''' Rechtsklick die angeklickte Kachel und nicht das Bild auf der Buehne. In der Galerie haengt
    ''' fast alles an der Auswahl, im Betrachter gibt es keine.
    '''
    ''' Vorher gab es fuenf Menues, die ihren Ort nur zufaellig kannten - daher kam der Fehler, dass
    ''' eine Aktion aus dem Filmstreifen das falsche Bild traf.
    ''' </summary>
    Public Enum MenuSite
        ''' Kachel in der Rasteransicht der Galerie.
        GalleryTile
        ''' Zeile in der Listenansicht der Galerie.
        GalleryRow
        ''' Schaltflaeche in der Fusszeile der Galerie - gemeint ist die AUSWAHL.
        GalleryFooter

        ''' Das grosse Bild im Betrachter.
        ViewerStage
        ''' Eine Kachel des Filmstreifens im Betrachter.
        ViewerFilmstrip
        ''' Fusszeile des Betrachters - gemeint ist das Bild auf der Buehne.
        ViewerFooter

        ''' Das Bild im Editor.
        EditorStage
        ''' Eine Kachel des Filmstreifens im Editor.
        EditorFilmstrip
        ''' Fusszeile des Editors - gemeint ist das geoeffnete Bild.
        EditorFooter
    End Enum

    Public Module MenuSiteExtensions

        <System.Runtime.CompilerServices.Extension>
        Public Function IsGallery(site As MenuSite) As Boolean
            Return site = MenuSite.GalleryTile OrElse
                   site = MenuSite.GalleryRow OrElse
                   site = MenuSite.GalleryFooter
        End Function

        <System.Runtime.CompilerServices.Extension>
        Public Function IsEditor(site As MenuSite) As Boolean
            Return site = MenuSite.EditorStage OrElse
                   site = MenuSite.EditorFilmstrip OrElse
                   site = MenuSite.EditorFooter
        End Function

        ''' <summary>Auf der Buehne des Editors ist "Anpassen" sinnlos, wir sind bereits dort.
        ''' Ebenso im Filmstreifen des Editors, solange es um das geoeffnete Bild geht.</summary>
        <System.Runtime.CompilerServices.Extension>
        Public Function ShowsAdjust(site As MenuSite) As Boolean
            Return Not site.IsEditor()
        End Function

    End Module

End Namespace
