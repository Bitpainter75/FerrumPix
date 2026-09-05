Imports System.Collections.Generic

Namespace Models

    ''' <summary>Eine Zeile der Gruppenansicht: entweder eine Kopfzeile oder eine Zeile Kacheln.
    ''' <c>First</c> und <c>Count</c> zeigen in die Anzeigereihenfolge (die Eintraege mit den
    ''' Kopfzeilen dazwischen), <c>Top</c> und <c>Height</c> sind Bildpunkte im Inhalt.</summary>
    Public Structure GalleryGroupRow
        Public First As Integer
        Public Count As Integer
        Public Top As Double
        Public Height As Double
    End Structure

    ''' <summary>Was die virtualisierende Anordnung der Gruppenansicht vom ViewModel braucht.
    '''
    ''' <para>Die Anordnung misst Spaltenzahl und Kachelhoehe am wirklich gebauten Element und meldet
    ''' beides ueber <see cref="UpdateGroupRows"/> zurueck; das ViewModel baut daraus die
    ''' Zeilentabelle. Damit gibt es fuer die Lage einer Zeile nur EINE Rechnung - dieselbe, aus der
    ''' auch der Sprung zu einem Bild, das Blaettern und der sichtbare Bereich fuer die
    ''' Vorschaubilder kommen.</para>
    '''
    ''' <para>Die Schaetzwerte sind der Anlauf: vor dem ersten Durchgang ist noch keine Kachel
    ''' gebaut, und ohne ein Mass gaebe es keine Spaltenzahl. Sie gelten genau einen Durchgang lang,
    ''' danach steht das gemessene Mass.</para></summary>
    Public Interface IGalleryGroupRowSource

        ''' <summary>Geschaetzte Breite einer Kachelspalte, solange keine gemessen ist.</summary>
        ReadOnly Property GroupTileWidthEstimate As Double

        ''' <summary>Geschaetzte Hoehe einer Kachelzeile, solange keine gemessen ist.</summary>
        ReadOnly Property GroupTileHeightEstimate As Double

        ''' <summary>Zeilentabelle zu Spaltenzahl und gemessener Kachelhoehe bereitstellen. Ruft NICHT
        ''' den Neuaufbau der Eintragsliste an - der laeuft nie waehrend eines Layoutdurchgangs.</summary>
        Sub UpdateGroupRows(columns As Integer, itemSlotHeight As Double)

        ReadOnly Property GroupRows As IReadOnlyList(Of GalleryGroupRow)

        ReadOnly Property GroupContentHeight As Double

    End Interface

End Namespace
