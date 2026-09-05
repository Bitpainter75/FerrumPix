Imports System.Collections.Generic
Imports System.Collections.Specialized
Imports Avalonia
Imports Avalonia.Layout
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace Controls

    ''' <summary>Die virtualisierende Anordnung der Gruppenansicht.
    '''
    ''' <para>WARUM EINE EIGENE. Die Gruppenansicht schiebt Kopfzeilen mit voller Zeilenbreite
    ''' zwischen die Kacheln. <c>UniformGridLayout</c> kennt nur ein einziges Kachelmass und kann
    ''' damit nichts anfangen, <c>WrapLayout</c> verliert bei gemischten Breiten beim Rollen seine
    ''' Zeilenhoehe, und <c>StackLayout</c> ueber fertige Zeilen SCHAETZT den Rollbereich aus den
    ''' gerade sichtbaren Zeilen - bei zwei sehr verschiedenen Zeilenhoehen wandert er damit beim
    ''' Rollen, und jede Rechnung, die den Scrollversatz in eine Zeile uebersetzt, geht mit ihm
    ''' daneben.</para>
    '''
    ''' <para>Hier gibt es keine Schaetzung: die Zeilentabelle im ViewModel kennt Oberkante und Hoehe
    ''' JEDER Zeile, auch der nie gebauten. Der Rollbereich ist damit von Anfang an der endgueltige,
    ''' und der Sprung zu einem Bild, das Blaettern und der sichtbare Bereich fuer die Vorschaubilder
    ''' rechnen mit denselben Zahlen wie die Anordnung.</para>
    '''
    ''' <para>Gebaut wird nur, was in den Sichtbereich des Repeaters faellt (<c>RealizationRect</c>,
    ''' das ist der Ausschnitt plus Vorhaltepuffer). Alles andere gibt der Repeater nach dem
    ''' Durchgang von selbst zurueck: was eine Messung nicht angefordert hat, wandert in den
    ''' Wiederverwendungsvorrat. Genau daher kommt der Gewinn - eine Kachel aus rund sechzig
    ''' Steuerelementen wird beim Rollen weitergereicht statt neu gebaut.</para>
    '''
    ''' <para>Die Anordnung haelt ihren Stand in Feldern und gehoert deshalb zu GENAU EINEM
    ''' Repeater. Sie wird in <c>GalleryView.axaml</c> an Ort und Stelle erzeugt; eine geteilte
    ''' Instanz muesste den Stand in <c>LayoutState</c> ablegen.</para></summary>
    Public Class GalleryGroupLayout
        Inherits VirtualizingLayout

        ''' <summary>Die Zeilentabelle. Setzt die Ansicht beim Anbinden des ViewModels.</summary>
        Public Property RowSource As IGalleryGroupRowSource

        ' Das Kachelmass, gemessen an einer wirklich gebauten Kachel. Vor der ersten Messung gilt der
        ' Schaetzwert des ViewModels - er traegt genau einen Durchgang.
        Private _tileWidth As Double = 0
        Private _tileHeight As Double = 0
        Private _columns As Integer = 1
        ' Woran das gemessene Mass festgemacht ist: Kachelbreite und Schriftgrad. Siehe AdoptTileSize.
        Private _latchedWidth As Double = 0
        Private _latchedFontOffset As Integer = Integer.MinValue

        ' Was dieser Durchgang gebaut hat, mit der Lage, in die es gehoert. ArrangeOverride laeuft
        ' unmittelbar nach MeasureOverride und ordnet genau diese Elemente an.
        Private ReadOnly _realizedElements As New List(Of Layoutable)()
        Private ReadOnly _realizedBounds As New List(Of Rect)()

        ''' <summary>Die Spaltenzahl des letzten Durchgangs. Die Ansicht braucht sie fuer das
        ''' Blaettern und die Tastaturbewegung und darf sie NICHT selbst ausrechnen: eine zweite
        ''' Rechnung liefe an dieser hier vorbei.</summary>
        Public ReadOnly Property ColumnCount As Integer
            Get
                Return Math.Max(1, _columns)
            End Get
        End Property

        ''' <summary>Die gemessene Hoehe einer Kachelzeile, oder 0 vor der ersten Messung.</summary>
        Public ReadOnly Property TileHeight As Double
            Get
                Return _tileHeight
            End Get
        End Property

        Protected Overrides Sub OnItemsChangedCore(context As VirtualizingLayoutContext, source As Object,
                                                   args As NotifyCollectionChangedEventArgs)
            _realizedElements.Clear()
            _realizedBounds.Clear()
            MyBase.OnItemsChangedCore(context, source, args)
        End Sub

        Protected Overrides Function MeasureOverride(context As VirtualizingLayoutContext, availableSize As Size) As Size
            _realizedElements.Clear()
            _realizedBounds.Clear()

            Dim rowTable = RowSource
            Dim entryCount = If(context IsNot Nothing, context.ItemCount, 0)
            If rowTable Is Nothing OrElse entryCount <= 0 Then Return New Size(0, 0)

            If _tileWidth <= 0 Then _tileWidth = Math.Max(1, rowTable.GroupTileWidthEstimate)
            If _tileHeight <= 0 Then _tileHeight = Math.Max(1, rowTable.GroupTileHeightEstimate)

            Dim usableWidth = availableSize.Width
            If Double.IsInfinity(usableWidth) OrElse Double.IsNaN(usableWidth) OrElse usableWidth <= 0 Then
                usableWidth = _tileWidth
            End If

            _columns = Math.Max(1, CInt(Math.Floor(usableWidth / _tileWidth)))
            rowTable.UpdateGroupRows(_columns, _tileHeight)

            Dim rows = rowTable.GroupRows
            If rows Is Nothing OrElse rows.Count = 0 Then Return New Size(0, 0)

            Dim rowWidth = _columns * _tileWidth
            Dim window = context.RealizationRect
            Dim firstRow = RowIndexAt(rows, window.Top)
            Dim lastRow = RowIndexAt(rows, window.Bottom)

            Dim measuredTile As Size = Nothing
            Dim tileMeasured = False

            For rowIndex = firstRow To lastRow
                Dim row = rows(rowIndex)
                If row.First < 0 OrElse row.First >= entryCount Then Continue For
                Dim isHeaderRow = IsGroupHeaderAt(context, row.First)

                For column = 0 To row.Count - 1
                    Dim entryIndex = row.First + column
                    If entryIndex >= entryCount Then Exit For
                    Dim element = context.GetOrCreateElementAt(entryIndex)
                    If element Is Nothing Then Continue For

                    Dim bounds As Rect
                    If isHeaderRow Then
                        ' Die Kopfzeile nimmt die ganze Zeile ein; ihre Hoehe steht in der Vorlage
                        ' fest und muss mit der Zeilentabelle uebereinstimmen.
                        bounds = New Rect(0, row.Top, rowWidth, row.Height)
                        element.Measure(bounds.Size)
                    Else
                        ' Offen messen, nicht gegen die Slotbreite: die Kachel traegt ihre Breite
                        ' selbst (gebundene Breite plus Aussenabstand). Gaebe man ihr das gemerkte
                        ' Mass als Grenze mit, koennte sie nie melden, dass sie inzwischen groesser
                        ' ist - der Wert bestaetigte sich immer nur selbst.
                        bounds = New Rect(column * _tileWidth, row.Top, _tileWidth, row.Height)
                        element.Measure(New Size(Double.PositiveInfinity, Double.PositiveInfinity))
                        If Not tileMeasured Then
                            tileMeasured = True
                            measuredTile = element.DesiredSize
                        End If
                    End If

                    _realizedElements.Add(element)
                    _realizedBounds.Add(bounds)
                Next
            Next

            ' Das gemessene Mass uebernehmen und den Durchgang wiederholen lassen. Betrifft den
            ' Anlauf (bis dahin galt der Schaetzwert) und jede Aenderung an Kachelgroesse oder
            ' Schrift. Danach stimmen gemessenes und gemerktes Mass ueberein, und es bleibt bei
            ' einem Durchgang.
            If tileMeasured AndAlso AdoptTileSize(measuredTile) Then InvalidateMeasure()

            Return New Size(rowWidth, rowTable.GroupContentHeight)
        End Function

        Protected Overrides Function ArrangeOverride(context As VirtualizingLayoutContext, finalSize As Size) As Size
            For i = 0 To _realizedElements.Count - 1
                _realizedElements(i).Arrange(_realizedBounds(i))
            Next
            Return finalSize
        End Function

        ''' <summary>Uebernimmt ein neu gemessenes Kachelmass. Meldet True, wenn es sich wirklich
        ''' geaendert hat - dann ist die Zeilentabelle von eben ueberholt.
        '''
        ''' <para>EINMAL GEMESSEN, BLEIBT DER WERT STEHEN. Gemessen wird die erste Kachel im
        ''' Sichtfenster, und die ist beim Rollen jedes Mal eine andere. Duerfte jede von ihnen die
        ''' Zeilenhoehe neu setzen, geriete die Ansicht bei zwei nur minimal verschieden hohen Kacheln
        ''' in einen Kreislauf: neue Hoehe, neue Zeilentabelle, neue Messung. Der Wert wird deshalb an
        ''' Kachelbreite und Schriftgrad festgemacht - die beiden Dinge, die ihn wirklich aendern - und
        ''' nur bei deren Wechsel neu genommen. Dieselbe Regel galt schon fuer die gemessene
        ''' Zeilenhoehe des Rasters, aus demselben Grund.</para></summary>
        Private Function AdoptTileSize(measured As Size) As Boolean
            Dim width = measured.Width
            Dim height = measured.Height
            If width <= 0 OrElse height <= 0 Then Return False

            Dim fontOffset = FontScaleService.CurrentOffset
            If Math.Abs(width - _latchedWidth) < 0.5 AndAlso fontOffset = _latchedFontOffset Then Return False
            _latchedWidth = width
            _latchedFontOffset = fontOffset

            If Math.Abs(width - _tileWidth) < 0.5 AndAlso Math.Abs(height - _tileHeight) < 0.5 Then Return False
            _tileWidth = width
            _tileHeight = height
            Return True
        End Function

        Private Shared Function IsGroupHeaderAt(context As VirtualizingLayoutContext, entryIndex As Integer) As Boolean
            Dim entry = TryCast(context.GetItemAt(entryIndex), ImageItem)
            Return entry IsNot Nothing AndAlso entry.IsGroupHeader
        End Function

        ''' <summary>Die Zeile, in der ein Bildpunkt des Inhalts liegt. Binaersuche, damit auch
        ''' 30000 Eintraege je Rollschritt nichts kosten.</summary>
        Private Shared Function RowIndexAt(rows As IReadOnlyList(Of GalleryGroupRow), contentY As Double) As Integer
            If rows.Count = 0 Then Return 0
            If contentY <= 0 Then Return 0
            Dim low = 0
            Dim high = rows.Count - 1
            While low < high
                Dim middle = (low + high + 1) \ 2
                If rows(middle).Top <= contentY Then
                    low = middle
                Else
                    high = middle - 1
                End If
            End While
            Return low
        End Function

    End Class

End Namespace
