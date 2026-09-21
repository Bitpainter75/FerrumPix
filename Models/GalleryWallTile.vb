Imports System.Collections.Generic

Namespace Models

    ''' <summary>Die Lage EINER Kachel in der Fotowand, in Bildpunkten im Inhalt. Anders als im
    ''' Raster hat hier jede Kachel ihre eigene Hoehe, und anders als in der Gruppenansicht stehen
    ''' die Kacheln einer Spalte nicht auf gleicher Hoehe - deshalb eine Tabelle je Kachel und
    ''' nicht je Zeile.</summary>
    Public Structure GalleryWallTile
        Public Left As Double
        Public Top As Double
        Public Width As Double
        Public Height As Double

        Public ReadOnly Property Bottom As Double
            Get
                Return Top + Height
            End Get
        End Property

        Public ReadOnly Property CenterX As Double
            Get
                Return Left + Width / 2.0
            End Get
        End Property
    End Structure

    ''' <summary>Was die virtualisierende Anordnung der Fotowand vom ViewModel braucht.
    '''
    ''' <para>Dieselbe Aufteilung wie bei der Gruppenansicht: die ANORDNUNG kennt die Flaeche und
    ''' rechnet daraus Spaltenzahl und Spaltenbreite, das VIEWMODEL baut daraus die Tabelle. Damit
    ''' gibt es fuer die Lage einer Kachel nur EINE Rechnung - dieselbe, aus der auch die
    ''' Tastaturbewegung und der sichtbare Bereich kommen.</para>
    '''
    ''' <para>DIE TABELLE IST NACH OBERKANTE SORTIERT, und das ist keine Zufaelligkeit, sondern
    ''' folgt aus der Regel: jede Kachel kommt in die Spalte, die gerade am kuerzesten ist. Ihre
    ''' Oberkante ist damit das Minimum aller Spaltenhoehen, und das kann nicht kleiner werden.
    ''' Die Anordnung darf deshalb binaer suchen, statt bei jedem Rollschritt die ganze Tabelle
    ''' durchzugehen.</para></summary>
    Public Interface IGalleryWallSource

        ''' <summary>Gewuenschte Spaltenbreite. Die Anordnung nimmt sie als Richtwert und weicht
        ''' davon ab, damit die Spalten die Flaeche genau ausfuellen.</summary>
        ReadOnly Property WallColumnWidthTarget As Double

        ''' <summary>Der Abstand zwischen zwei Kacheln, waagerecht wie senkrecht.</summary>
        ReadOnly Property WallGap As Double

        ''' <summary>Tabelle zu Spaltenzahl und Spaltenbreite bereitstellen. Baut nur neu, wenn sich
        ''' etwas geaendert hat, und ruft NIE etwas an, das die Sammlung der Eintraege anfasst - das
        ''' liefe mitten im Layoutdurchgang.</summary>
        Sub UpdateWallTiles(columns As Integer, columnWidth As Double)

        ReadOnly Property WallTiles As IReadOnlyList(Of GalleryWallTile)

        ReadOnly Property WallContentHeight As Double

        ''' <summary>Der gerade gebaute Bereich. Daran erkennt das ViewModel, dass fuer diese
        ''' Kacheln inzwischen ein Vorschaubild da ist und ihr Seitenverhaeltnis damit besser bekannt
        ''' ist als beim Bau der Tabelle - es stoesst den Neuaufbau dann selbst an, aber erst nach
        ''' dem Durchgang.</summary>
        Sub NoteWallRange(firstIndex As Integer, lastIndex As Integer)

    End Interface

End Namespace
