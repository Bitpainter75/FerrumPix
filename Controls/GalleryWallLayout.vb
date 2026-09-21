Imports System.Collections.Generic
Imports System.Collections.Specialized
Imports Avalonia
Imports Avalonia.Layout
Imports FerrumPix.Models

Namespace Controls

    ''' <summary>Die virtualisierende Anordnung der Fotowand.
    '''
    ''' <para>WAS SIE ANDERS MACHT ALS DAS RASTER. Im Raster ist jede Kachel gleich gross und das
    ''' Bild wird auf dieses Mass zugeschnitten; hochkant und quer sehen danach gleich aus. Hier ist
    ''' die BREITE fest (die Spalte) und die HOEHE kommt aus dem Seitenverhaeltnis des Bildes. Jede
    ''' neue Kachel kommt in die Spalte, die gerade am kuerzesten ist - daher der versetzte Satz.
    ''' Zugeschnitten wird nichts.</para>
    '''
    ''' <para>DIE TABELLE GEHOERT DEM VIEWMODEL, genau wie bei der Gruppenansicht und aus demselben
    ''' Grund: an derselben Geometrie haengen die Tastaturbewegung, der Sprung zu einem Bild und der
    ''' sichtbare Bereich, aus dem die Vorschaubilder ihre Dringlichkeit ziehen. Zwei Rechnungen
    ''' liefen frueher oder spaeter auseinander. Diese Anordnung rechnet deshalb nur Spaltenzahl und
    ''' Spaltenbreite aus der Flaeche und laesst die Tabelle bauen.</para>
    '''
    ''' <para>DIE SPALTEN FUELLEN DIE FLAECHE GENAU AUS. Die gewuenschte Spaltenbreite ist ein
    ''' Richtwert; die tatsaechliche ergibt sich aus der Flaeche geteilt durch die Spaltenzahl. Sonst
    ''' bliebe rechts ein Rest stehen, der mit der Fenstergroesse wandert - im Raster faellt das
    ''' nicht auf, weil dort ohnehin jede Kachel gleich breit ist.</para>
    '''
    ''' <para>Die Anordnung haelt ihren Stand in Feldern und gehoert deshalb zu GENAU EINEM
    ''' Repeater.</para></summary>
    Public Class GalleryWallLayout
        Inherits VirtualizingLayout

        ''' <summary>Die Kacheltabelle. Setzt die Ansicht beim Anbinden des ViewModels.</summary>
        Public Property TileSource As IGalleryWallSource

        Private _columns As Integer = 1
        Private _columnWidth As Double = 0

        ' Was dieser Durchgang gebaut hat, mit der Lage, in die es gehoert.
        Private ReadOnly _realizedElements As New List(Of Layoutable)()
        Private ReadOnly _realizedBounds As New List(Of Rect)()

        ''' <summary>Die Spaltenzahl des letzten Durchgangs. Die Ansicht braucht sie fuers Blaettern
        ''' und darf sie NICHT selbst ausrechnen.</summary>
        Public ReadOnly Property ColumnCount As Integer
            Get
                Return Math.Max(1, _columns)
            End Get
        End Property

        ''' <summary>Einen neuen Durchgang anfordern. Die Ansicht ruft das, wenn das ViewModel seine
        ''' Tabelle neu gebaut hat - eine geaenderte Tabelle ist keine geaenderte Sammlung, und von
        ''' selbst merkt die Anordnung davon nichts. Eigene Methode, weil InvalidateMeasure
        ''' geschuetzt ist.</summary>
        Public Sub RequestRelayout()
            InvalidateMeasure()
        End Sub

        Protected Overrides Sub OnItemsChangedCore(context As VirtualizingLayoutContext, source As Object,
                                                   args As NotifyCollectionChangedEventArgs)
            _realizedElements.Clear()
            _realizedBounds.Clear()
            MyBase.OnItemsChangedCore(context, source, args)
        End Sub

        Protected Overrides Function MeasureOverride(context As VirtualizingLayoutContext, availableSize As Size) As Size
            _realizedElements.Clear()
            _realizedBounds.Clear()

            Dim source = TileSource
            Dim itemCount = If(context IsNot Nothing, context.ItemCount, 0)
            If source Is Nothing OrElse itemCount <= 0 Then Return New Size(0, 0)

            Dim gap = Math.Max(0, source.WallGap)
            Dim target = Math.Max(1, source.WallColumnWidthTarget)
            Dim usableWidth = availableSize.Width
            If Double.IsInfinity(usableWidth) OrElse Double.IsNaN(usableWidth) OrElse usableWidth <= 0 Then
                usableWidth = target
            End If

            ' Spaltenzahl aus dem Richtwert, danach die Breite aus der Flaeche: die letzte Spalte
            ' endet damit genau am rechten Rand.
            _columns = Math.Max(1, CInt(Math.Floor((usableWidth + gap) / (target + gap))))
            _columnWidth = Math.Max(1, (usableWidth - (_columns - 1) * gap) / _columns)
            source.UpdateWallTiles(_columns, _columnWidth)

            Dim tiles = source.WallTiles
            If tiles Is Nothing OrElse tiles.Count = 0 Then Return New Size(0, 0)

            ' Die Tabelle ist nach Oberkante sortiert, die UNTERKANTEN sind es nicht: eine hohe
            ' Kachel weiter oben reicht in das Fenster hinein, obwohl ihre Oberkante darueber liegt.
            ' Hoeher als MaxTileHeightFactor mal Spaltenbreite wird keine - so weit vor dem Fenster
            ' beginnt die Suche, und damit ist keine uebersehbar.
            Dim window = context.RealizationRect
            Dim first = FirstIndexAtOrAfter(tiles, window.Top - MaxTileHeightFactor * _columnWidth)

            ' Wie weit die Schleife wirklich gekommen ist. NUR bis hierhin darf die Meldung unten
            ' reichen: gaebe man ihr das Ende der Sammlung mit, liefe sie bei jedem Rollschritt ueber
            ' den ganzen Bestand - und damit waere die Virtualisierung gerade dort wieder aufgegeben,
            ' wo sie zaehlt.
            Dim lastExamined = first
            For index = first To Math.Min(itemCount, tiles.Count) - 1
                Dim tile = tiles(index)
                If tile.Top > window.Bottom Then Exit For
                lastExamined = index
                If tile.Bottom < window.Top Then Continue For

                Dim element = context.GetOrCreateElementAt(index)
                If element Is Nothing Then Continue For
                Dim bounds = New Rect(tile.Left, tile.Top, tile.Width, tile.Height)
                ' GEGEN DIE SLOTGROESSE messen, nicht offen: anders als im Raster traegt die Kachel
                ' hier keine eigene Breite und keine eigene Hoehe, sie fuellt aus, was die Anordnung
                ' ihr gibt.
                element.Measure(bounds.Size)
                _realizedElements.Add(element)
                _realizedBounds.Add(bounds)
            Next

            If _realizedElements.Count > 0 Then
                source.NoteWallRange(first, lastExamined)
            End If

            Return New Size(usableWidth, source.WallContentHeight)
        End Function

        Protected Overrides Function ArrangeOverride(context As VirtualizingLayoutContext, finalSize As Size) As Size
            For i = 0 To _realizedElements.Count - 1
                _realizedElements(i).Arrange(_realizedBounds(i))
            Next
            Return finalSize
        End Function

        ''' <summary>Wie hoch eine Kachel hoechstens wird, als Vielfaches der Spaltenbreite. Muss mit
        ''' der Deckelung im ViewModel uebereinstimmen - aus ihr folgt, wie weit die Suche nach oben
        ''' zurueckgreifen muss.</summary>
        Public Const MaxTileHeightFactor As Double = 2.0

        ''' <summary>Der erste Eintrag, dessen Oberkante nicht mehr ueber der Marke liegt.
        ''' Binaersuche - die Tabelle ist nach Oberkante sortiert (siehe IGalleryWallSource).</summary>
        Private Shared Function FirstIndexAtOrAfter(tiles As IReadOnlyList(Of GalleryWallTile), contentY As Double) As Integer
            If tiles.Count = 0 Then Return 0
            If contentY <= 0 Then Return 0
            Dim low = 0
            Dim high = tiles.Count - 1
            While low < high
                Dim middle = (low + high) \ 2
                If tiles(middle).Top < contentY Then
                    low = middle + 1
                Else
                    high = middle
                End If
            End While
            Return low
        End Function

    End Class

End Namespace
