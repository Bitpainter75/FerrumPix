Imports Avalonia.Controls
Imports Avalonia.Controls.Templates
Imports FerrumPix.Models

Namespace Controls

    ''' <summary>Baut die Elemente der Gruppenansicht: eine Kopfzeile fuer eine Gruppenkopfzeile,
    ''' eine Kachel fuer jedes andere Element, und legt beide in GETRENNTE Vorraete zurueck.
    '''
    ''' <para>WARUM ZWEI VORRAETE. Vorher stand in der Vorlage des Repeaters ein Panel mit zwei
    ''' ContentControls, eines fuer die Kopfzeile und eines fuer die Kachel, und das jeweils andere
    ''' bekam keinen Inhalt. Der Repeater kennt dabei nur EINEN Vorrat: ein Element, das eben noch
    ''' Kopfzeile war, kam als Kachel zurueck und umgekehrt. Ein ContentPresenter, dessen Inhalt
    ''' zwischendurch leer war, verwirft beim naechsten Inhalt seine Vorlage und baut neu
    ''' (ContentPresenter.CreateChild nimmt das alte Kind nur bei derselben Vorlage) - also eine
    ''' ganze Kachel aus rund sechzig Steuerelementen. Gemessen wurden so je Bildschirmhoehe
    ''' ueber fuenfhundert neu angehaengte Steuerelemente und rund 150 ms Layout, im Raster mit
    ''' derselben Kachel keines und 4 ms (Pruefstand, "MESSUNG Galerie: Rollschritt").</para>
    '''
    ''' <para>Mit eigenem Schluessel je Gestalt bekommt eine Kachel immer eine Kachel und eine
    ''' Kopfzeile immer eine Kopfzeile; beim Rollen wechselt nur noch der Datenkontext, wie im
    ''' Raster.</para></summary>
    Public Class GalleryGroupElementFactory
        Inherits RecyclingElementFactory

        Public Const HeaderKey As String = "header"
        Public Const TileKey As String = "tile"

        Public Sub New(headerTemplate As IDataTemplate, tileTemplate As IDataTemplate)
            Templates(HeaderKey) = headerTemplate
            Templates(TileKey) = tileTemplate
        End Sub

        Protected Overrides Function OnSelectTemplateKeyCore(dataContext As Object, owner As Control) As String
            Dim entry = TryCast(dataContext, ImageItem)
            Return If(entry IsNot Nothing AndAlso entry.IsGroupHeader, HeaderKey, TileKey)
        End Function

    End Class

End Namespace
