Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Media
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace Controls

    ''' <summary>Die Karte der Galerie: Rasterkacheln eines Kachelservers, darauf die Bilder mit
    ''' Aufnahmeort als Häufchen mit Anzahl.
    '''
    ''' <para>EIGENES STEUERELEMENT statt einer Kartenbibliothek. Eine fertige gab es zur Zeit
    ''' des Baus nicht für Avalonia 12 ohne einen Schwung weiterer Abhängigkeiten; eine WebView
    ''' scheidet aus, weil ihre Anfragen die Kennung des eingebetteten Browsers tragen, die
    ''' Kachelrichtlinie aber eine eigene verlangt. Für Rasterkacheln bleibt übrig: Web-Mercator,
    ''' Kacheln holen und zeichnen, schwenken, zoomen.</para>
    '''
    ''' <para>GEHOLT WIRD NUR, WAS SICHTBAR IST. Keine Nachbarkacheln auf Vorrat und keine
    ''' anderen Zoomstufen - die Richtlinie verbietet das Vorabladen. Fehlt eine Kachel noch,
    ''' wird der passende Ausschnitt einer gröberen gezeigt, falls die schon im Speicher liegt.</para>
    '''
    ''' <para>Die Namensnennung "© OpenStreetMap contributors" steht immer unten rechts; ein
    ''' Klick darauf öffnet die Lizenzseite.</para></summary>
    Public Class PhotoMapControl
        Inherits Control

        Public Const AttributionText As String = "© OpenStreetMap contributors"
        Private Const AttributionUrl As String = "https://www.openstreetmap.org/copyright"
        Private Const ClusterCellSize As Double = 64
        Private Const MemoryTileLimit As Integer = 320
        Private Const FallbackDepth As Integer = 4
        Public Const MinZoom As Integer = 1
        Private Const DragThreshold As Double = 4
        ''' <summary>Die Nahansicht der Taste Z: Straßen und Hausnummern lesbar.</summary>
        Private Const CloseUpZoom As Integer = 16
        ''' <summary>Ein Achtel des Fensters je Pfeildruck, wie im Betrachter.</summary>
        Private Const KeyPanStepFraction As Double = 0.125

        Public Shared ReadOnly PointsProperty As StyledProperty(Of IReadOnlyList(Of MapPhotoPoint)) =
            AvaloniaProperty.Register(Of PhotoMapControl, IReadOnlyList(Of MapPhotoPoint))(NameOf(Points))
        Public Shared ReadOnly TileUrlProperty As StyledProperty(Of String) =
            AvaloniaProperty.Register(Of PhotoMapControl, String)(NameOf(TileUrl), MapTileService.DefaultTileUrl)
        Public Shared ReadOnly ClusterCommandProperty As StyledProperty(Of ICommand) =
            AvaloniaProperty.Register(Of PhotoMapControl, ICommand)(NameOf(ClusterCommand))
        Public Shared ReadOnly BackgroundProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of PhotoMapControl, IBrush)(NameOf(Background), Brushes.DimGray)
        Public Shared ReadOnly AccentBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of PhotoMapControl, IBrush)(NameOf(AccentBrush), Brushes.DarkOrange)
        Public Shared ReadOnly FailureTextProperty As StyledProperty(Of String) =
            AvaloniaProperty.Register(Of PhotoMapControl, String)(NameOf(FailureText), "")
        ''' <summary>Die Zoomstufe für den Regler in der Fußleiste, in beide Richtungen gebunden.
        ''' Gezeichnet wird nur auf ganzen Stufen; ein Zwischenwert vom Regler wird gerundet.</summary>
        Public Shared ReadOnly ZoomLevelProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of PhotoMapControl, Double)(NameOf(ZoomLevel), 2.0,
                                                                  defaultBindingMode:=Avalonia.Data.BindingMode.TwoWay)

        Public Property ZoomLevel As Double
            Get
                Return GetValue(ZoomLevelProperty)
            End Get
            Set(value As Double)
                SetValue(ZoomLevelProperty, value)
            End Set
        End Property

        Public Property Points As IReadOnlyList(Of MapPhotoPoint)
            Get
                Return GetValue(PointsProperty)
            End Get
            Set(value As IReadOnlyList(Of MapPhotoPoint))
                SetValue(PointsProperty, value)
            End Set
        End Property

        Public Property TileUrl As String
            Get
                Return GetValue(TileUrlProperty)
            End Get
            Set(value As String)
                SetValue(TileUrlProperty, value)
            End Set
        End Property

        ''' <summary>Wird mit den Bildern eines angeklickten Häufchens aufgerufen
        ''' (IReadOnlyList(Of ImageItem)).</summary>
        Public Property ClusterCommand As ICommand
            Get
                Return GetValue(ClusterCommandProperty)
            End Get
            Set(value As ICommand)
                SetValue(ClusterCommandProperty, value)
            End Set
        End Property

        Public Property Background As IBrush
            Get
                Return GetValue(BackgroundProperty)
            End Get
            Set(value As IBrush)
                SetValue(BackgroundProperty, value)
            End Set
        End Property

        Public Property AccentBrush As IBrush
            Get
                Return GetValue(AccentBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(AccentBrushProperty, value)
            End Set
        End Property

        ''' <summary>Der Hinweis, wenn Kacheln nicht zu holen waren. Übersetzt vom ViewModel -
        ''' eine Eigenschaft dieses Steuerelements erreicht der Baumdurchlauf der Übersetzung
        ''' nicht.</summary>
        Public Property FailureText As String
            Get
                Return GetValue(FailureTextProperty)
            End Get
            Set(value As String)
                SetValue(FailureTextProperty, value)
            End Set
        End Property

        Private NotInheritable Class MapCluster
            Public SumX As Double
            Public SumY As Double
            Public X As Double
            Public Y As Double
            Public ReadOnly Items As New List(Of ImageItem)()
        End Class

        Private Structure HitTarget
            Public Center As Point
            Public Radius As Double
            Public Cluster As MapCluster
        End Structure

        Private NotInheritable Class TileEntry
            Public Key As String
            Public Bitmap As Bitmap
        End Class

        Private _zoom As Integer = 2
        Private _originX As Double
        Private _originY As Double
        Private _needsFit As Boolean = True
        ''' <summary>Der Nutzer hat die Ansicht seit dem letzten Einpassen selbst verstellt
        ''' (geschwenkt oder gezoomt). Dann bleibt sie stehen, wenn zu derselben Liste nur Punkte
        ''' dazukommen: waehrend des Einlesens rechnet die Galerie alle paar Sekunden neu, und ein
        ''' Einpassen jedes Mal risse ihm die Ansicht weg.</summary>
        Private _userMoved As Boolean

        Private ReadOnly _tiles As New Dictionary(Of String, LinkedListNode(Of TileEntry))(StringComparer.Ordinal)
        Private ReadOnly _tileOrder As New LinkedList(Of TileEntry)()
        Private ReadOnly _pending As New HashSet(Of String)(StringComparer.Ordinal)
        ''' <summary>Fehlgeschlagene Kacheln mit dem Zeitpunkt des Fehlschlags. Eine Sperre fuer
        ''' immer machte aus einer kurzen Netz- oder Serverstoerung ein Loch in der Karte, das bis
        ''' zum naechsten Zoom stehen blieb. Nach <see cref="FailedTileRetryDelay"/> wird die
        ''' Kachel deshalb wieder angefragt, und zwar nur, wenn sie dann noch zu sehen ist.</summary>
        Private ReadOnly _failed As New Dictionary(Of String, DateTime)(StringComparer.Ordinal)
        ''' <summary>Lang genug, um einen Server nicht mit Wiederholungen zu belasten; kurz genug,
        ''' dass die Karte nach einer Stoerung ohne Zutun wieder vollstaendig wird.</summary>
        Public Shared ReadOnly FailedTileRetryDelay As TimeSpan = TimeSpan.FromSeconds(30)
        ''' <summary>Fuer die Pruefung: die Uhr, an der die Wartezeit gemessen wird.</summary>
        Public Property Clock As Func(Of DateTime) = Function() DateTime.UtcNow
        Private _retryScheduled As Boolean
        Private _loadCancellation As New CancellationTokenSource()

        Private _clusters As List(Of MapCluster)
        Private _clusterZoom As Integer = -1
        Private ReadOnly _hitTargets As New List(Of HitTarget)()
        Private _hoverCluster As MapCluster
        Private _attributionRect As Rect

        Private _dragStart As Point
        Private _dragOriginX As Double
        Private _dragOriginY As Double
        Private _isDragging As Boolean
        Private _moved As Boolean
        Private _wheelAccumulator As Double
        Private _publishingZoom As Boolean
        Private _lastPointer As Point?

        Shared Sub New()
            AffectsRender(Of PhotoMapControl)(BackgroundProperty, AccentBrushProperty, FailureTextProperty)
            FocusableProperty.OverrideDefaultValue(Of PhotoMapControl)(True)
            ClipToBoundsProperty.OverrideDefaultValue(Of PhotoMapControl)(True)
        End Sub

        ' ------------------------------------------------------------------------------------
        ' Lebenslauf
        ' ------------------------------------------------------------------------------------

        Protected Overrides Sub OnAttachedToVisualTree(e As VisualTreeAttachmentEventArgs)
            MyBase.OnAttachedToVisualTree(e)
            AddHandler MapTileService.Instance.TileUpdated, AddressOf OnTileUpdated
        End Sub

        Protected Overrides Sub OnDetachedFromVisualTree(e As VisualTreeAttachmentEventArgs)
            RemoveHandler MapTileService.Instance.TileUpdated, AddressOf OnTileUpdated
            _loadCancellation.Cancel()
            _loadCancellation = New CancellationTokenSource()
            _pending.Clear()
            ' Wer die Ansicht verlaesst und wiederkommt, erwartet einen neuen Versuch.
            _failed.Clear()
            ClearTiles()
            MyBase.OnDetachedFromVisualTree(e)
        End Sub

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property Is PointsProperty Then
                _clusters = Nothing
                _hoverCluster = Nothing
                ' Eingepasst wird bei einer NEUEN Liste (kein Bild der alten mehr dabei, etwa nach
                ' einem Ordnerwechsel) und immer, solange der Nutzer nichts verstellt hat.
                If Not _userMoved OrElse Not SharesItems(TryCast(change.OldValue, IReadOnlyList(Of MapPhotoPoint)),
                                                         TryCast(change.NewValue, IReadOnlyList(Of MapPhotoPoint))) Then
                    _needsFit = True
                End If
                InvalidateVisual()
            ElseIf change.Property Is ZoomLevelProperty Then
                If _publishingZoom OrElse _needsFit Then Return
                Dim target = CInt(Math.Round(ZoomLevel))
                If target <> _zoom Then
                    ZoomAt(New Point(Bounds.Width / 2, Bounds.Height / 2), target - _zoom, publish:=False)
                End If
            ElseIf change.Property Is TileUrlProperty Then
                _loadCancellation.Cancel()
                _loadCancellation = New CancellationTokenSource()
                _pending.Clear()
                _failed.Clear()
                ClearTiles()
                InvalidateVisual()
            End If
        End Sub

        Private Sub OnTileUpdated(sender As Object, e As MapTileUpdatedEventArgs)
            Dispatcher.UIThread.Post(
                Sub()
                    If Not String.Equals(e.Template, TileUrl, StringComparison.Ordinal) Then Return
                    Dim key = TileKey(e.Zoom, e.X, e.Y)
                    Dim node As LinkedListNode(Of TileEntry) = Nothing
                    If _tiles.TryGetValue(key, node) Then
                        _tiles.Remove(key)
                        _tileOrder.Remove(node)
                        node.Value.Bitmap?.Dispose()
                    End If
                    InvalidateVisual()
                End Sub)
        End Sub

        ' ------------------------------------------------------------------------------------
        ' Sichtfenster
        ' ------------------------------------------------------------------------------------

        ''' <summary>Stellt die Karte so, dass alle Bilder zu sehen sind. Ohne Bilder zeigt sie die
        ''' Welt; bei einem einzelnen Ort nicht näher als eine Stadtansicht.</summary>
        Private Shared Function SharesItems(oldPoints As IReadOnlyList(Of MapPhotoPoint),
                                            newPoints As IReadOnlyList(Of MapPhotoPoint)) As Boolean
            If oldPoints Is Nothing OrElse newPoints Is Nothing OrElse oldPoints.Count = 0 OrElse newPoints.Count = 0 Then Return False
            Dim oldItems As New HashSet(Of ImageItem)(oldPoints.Select(Function(p) p.Item))
            Return newPoints.Any(Function(p) oldItems.Contains(p.Item))
        End Function

        Private Sub FitToPoints(size As Size)
            _userMoved = False
            Dim points = Me.Points
            If points Is Nothing OrElse points.Count = 0 Then
                _zoom = 2
                CenterOn(20, 10, size)
                Return
            End If
            Dim minLat = points.Min(Function(p) p.Latitude)
            Dim maxLat = points.Max(Function(p) p.Latitude)
            Dim minLon = points.Min(Function(p) p.Longitude)
            Dim maxLon = points.Max(Function(p) p.Longitude)
            Dim chosen = MinZoom
            For z = 14 To MinZoom Step -1
                Dim width = LongitudeToWorldX(maxLon, z) - LongitudeToWorldX(minLon, z)
                Dim height = LatitudeToWorldY(minLat, z) - LatitudeToWorldY(maxLat, z)
                If width <= size.Width * 0.8 AndAlso height <= size.Height * 0.8 Then
                    chosen = z
                    Exit For
                End If
            Next
            _zoom = chosen
            CenterOn((minLat + maxLat) / 2, (minLon + maxLon) / 2, size)
        End Sub

        Private Sub CenterOn(latitude As Double, longitude As Double, size As Size)
            _originX = LongitudeToWorldX(longitude, _zoom) - size.Width / 2
            _originY = LatitudeToWorldY(latitude, _zoom) - size.Height / 2
            ClampOrigin(size)
        End Sub

        ''' <summary>Waagrecht läuft die Welt im Kreis, senkrecht hat sie ein Ende. Ist sie
        ''' niedriger als das Fenster, steht sie in der Mitte.</summary>
        Private Sub ClampOrigin(size As Size)
            Dim world = WorldSize(_zoom)
            If world <= size.Height Then
                _originY = (world - size.Height) / 2
            Else
                _originY = Math.Max(0, Math.Min(world - size.Height, _originY))
            End If
            _originX = _originX Mod world
            If _originX < 0 Then _originX += world
        End Sub

        ''' <param name="publish">Ob die neue Stufe an ZoomLevel (und damit an den Regler)
        ''' zurückgemeldet wird. Nicht, wenn der Anstoß vom Regler selbst kam: ein Rückschreiben
        ''' mitten im Ziehen risse ihm den Wert unter dem Zeiger weg.</param>
        Private Sub ZoomAt(anchor As Point, steps As Integer, Optional publish As Boolean = True)
            Dim target = Math.Max(MinZoom, Math.Min(MapTileService.MaxZoom, _zoom + steps))
            If target = _zoom Then Return
            _userMoved = True
            Dim factor = Math.Pow(2, target - _zoom)
            _originX = (_originX + anchor.X) * factor - anchor.X
            _originY = (_originY + anchor.Y) * factor - anchor.Y
            _zoom = target
            ResetLoadsForZoomChange()
            ClampOrigin(Bounds.Size)
            If publish Then PublishZoom()
            InvalidateVisual()
        End Sub

        ''' <summary>Kacheln der alten Stufe, die noch in der Schlange stehen, braucht niemand mehr.</summary>
        Private Sub ResetLoadsForZoomChange()
            _loadCancellation.Cancel()
            _loadCancellation = New CancellationTokenSource()
            _pending.Clear()
            _failed.Clear()
            _hoverCluster = Nothing
        End Sub

        Private Sub PublishZoom()
            If ZoomLevel = _zoom Then Return
            _publishingZoom = True
            Try
                SetCurrentValue(ZoomLevelProperty, CDbl(_zoom))
            Finally
                _publishingZoom = False
            End Try
        End Sub

        ''' <summary>Alle Bilder ins Fenster, sofort und nicht erst beim nächsten Zeichnen.</summary>
        Private Sub FitNow()
            Dim size = Bounds.Size
            If size.Width <= 0 OrElse size.Height <= 0 Then
                _needsFit = True
                InvalidateVisual()
                Return
            End If
            _needsFit = False
            FitToPoints(size)
            ResetLoadsForZoomChange()
            PublishZoom()
            InvalidateVisual()
        End Sub

        ''' <summary>Das Gegenstück zu Z in Betrachter und Editor. Eine 100-Prozent-Ansicht gibt es
        ''' bei einer Karte nicht; an ihrer Stelle steht die Nahansicht auf Straßenebene, an der
        ''' Stelle unter dem Zeiger. Von dort, oder schon nah dran, geht es zurück auf alle Bilder.</summary>
        Private Sub ToggleCloseUp()
            If _zoom >= CloseUpZoom - 1 Then
                FitNow()
                Return
            End If
            Dim anchor = New Point(Bounds.Width / 2, Bounds.Height / 2)
            If _lastPointer.HasValue AndAlso New Rect(Bounds.Size).Contains(_lastPointer.Value) Then anchor = _lastPointer.Value
            ZoomAt(anchor, CloseUpZoom - _zoom)
        End Sub

        ''' <summary>Die Tasten der Karte: Plus und Minus zoomen, F passt alle Bilder ein, Z wechselt
        ''' zur Nahansicht und zurück, Pfeile und SHIFT+Pfeile verschieben um ein Achtel des Fensters.
        ''' Öffentlich, weil die Galerie sie auch dann hierher reicht, wenn die Karte keinen Fokus
        ''' hat. Gibt zurück, ob die Taste etwas bewirkt hat.</summary>
        Public Function HandleKey(pressed As Key, modifiers As KeyModifiers) As Boolean
            If modifiers <> KeyModifiers.None AndAlso modifiers <> KeyModifiers.Shift Then Return False
            Dim center = New Point(Bounds.Width / 2, Bounds.Height / 2)
            Dim stepX = Bounds.Width * KeyPanStepFraction
            Dim stepY = Bounds.Height * KeyPanStepFraction
            Select Case pressed
                ' Plus liegt je nach Tastatur mit oder ohne SHIFT, deshalb beides.
                Case Key.Add, Key.OemPlus
                    ZoomAt(center, 1)
                Case Key.Subtract, Key.OemMinus
                    ZoomAt(center, -1)
                Case Key.F
                    If modifiers <> KeyModifiers.None Then Return False
                    FitNow()
                Case Key.Z
                    If modifiers <> KeyModifiers.None Then Return False
                    ToggleCloseUp()
                Case Key.Left
                    Pan(-stepX, 0)
                Case Key.Right
                    Pan(stepX, 0)
                Case Key.Up
                    Pan(0, -stepY)
                Case Key.Down
                    Pan(0, stepY)
                Case Else
                    Return False
            End Select
            Return True
        End Function

        ' ------------------------------------------------------------------------------------
        ' Zeichnen
        ' ------------------------------------------------------------------------------------

        Public Overrides Sub Render(context As DrawingContext)
            Dim size = Bounds.Size
            context.FillRectangle(Background, New Rect(size))
            If size.Width <= 0 OrElse size.Height <= 0 Then Return

            If _needsFit Then
                _needsFit = False
                FitToPoints(size)
                ' Nicht mitten im Zeichnen an eine Bindung schreiben.
                Dispatcher.UIThread.Post(AddressOf PublishZoom)
            End If

            DrawTiles(context, size)
            DrawClusters(context, size)
            DrawAttribution(context, size)
            If _failed.Count > 0 AndAlso Not String.IsNullOrEmpty(FailureText) Then
                DrawLabel(context, FailureText, New Point(10, 10), alignRight:=False)
            End If
        End Sub

        Private Sub DrawTiles(context As DrawingContext, size As Size)
            Dim count = TileCount(_zoom)
            Dim firstX = CLng(Math.Floor(_originX / TileSize))
            Dim lastX = CLng(Math.Floor((_originX + size.Width) / TileSize))
            Dim firstY = Math.Max(0, CInt(Math.Floor(_originY / TileSize)))
            Dim lastY = Math.Min(count - 1, CInt(Math.Floor((_originY + size.Height) / TileSize)))

            For ty = firstY To lastY
                For tx = firstX To lastX
                    Dim destination = New Rect(tx * TileSize - _originX, ty * TileSize - _originY, TileSize, TileSize)
                    Dim x = WrapTileX(tx, _zoom)
                    Dim bitmap = TileFromMemory(_zoom, x, ty)
                    If bitmap IsNot Nothing Then
                        context.DrawImage(bitmap, New Rect(bitmap.Size), destination)
                    Else
                        DrawCoarserTile(context, x, ty, destination)
                        RequestTile(_zoom, x, ty)
                    End If
                Next
            Next
        End Sub

        ''' <summary>Solange eine Kachel fehlt: der passende Ausschnitt einer gröberen Stufe, wenn
        ''' sie schon im Speicher liegt. Geholt wird dafür nichts.</summary>
        Private Sub DrawCoarserTile(context As DrawingContext, x As Integer, y As Integer, destination As Rect)
            For depth = 1 To FallbackDepth
                Dim parentZoom = _zoom - depth
                If parentZoom < 0 Then Return
                Dim parent = TileFromMemory(parentZoom, x >> depth, y >> depth)
                If parent Is Nothing Then Continue For
                Dim scale = parent.Size.Width / TileSize
                Dim part = TileSize / Math.Pow(2, depth)
                Dim sourceX = (x - ((x >> depth) << depth)) * part
                Dim sourceY = (y - ((y >> depth) << depth)) * part
                context.DrawImage(parent, New Rect(sourceX * scale, sourceY * scale, part * scale, part * scale), destination)
                Return
            Next
        End Sub

        Private Sub EnsureClusters()
            If _clusters IsNot Nothing AndAlso _clusterZoom = _zoom Then Return
            Dim cells As New Dictionary(Of Long, MapCluster)()
            Dim points = Me.Points
            If points IsNot Nothing Then
                For Each point In points
                    If point Is Nothing Then Continue For
                    Dim worldX = LongitudeToWorldX(point.Longitude, _zoom)
                    Dim worldY = LatitudeToWorldY(point.Latitude, _zoom)
                    ' 2^22 Zellen je Achse reichen bis Stufe 19 (256 * 2^19 / 64 = 2^21).
                    Dim key = CLng(Math.Floor(worldX / ClusterCellSize)) * 4194304L + CLng(Math.Floor(worldY / ClusterCellSize))
                    Dim cluster As MapCluster = Nothing
                    If Not cells.TryGetValue(key, cluster) Then
                        cluster = New MapCluster()
                        cells(key) = cluster
                    End If
                    cluster.SumX += worldX
                    cluster.SumY += worldY
                    cluster.Items.Add(point.Item)
                Next
            End If
            For Each cluster In cells.Values
                cluster.X = cluster.SumX / cluster.Items.Count
                cluster.Y = cluster.SumY / cluster.Items.Count
            Next
            _clusters = cells.Values.ToList()
            _clusterZoom = _zoom
        End Sub

        Private Shared Function RadiusOf(cluster As MapCluster) As Double
            If cluster.Items.Count <= 1 Then Return 8
            Return 12 + 4 * Math.Log10(cluster.Items.Count)
        End Function

        Private Sub DrawClusters(context As DrawingContext, size As Size)
            EnsureClusters()
            _hitTargets.Clear()
            Dim world = WorldSize(_zoom)
            Dim outline = New Pen(New SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 2)
            Dim hoverOutline = New Pen(Brushes.White, 3)
            Dim textBrush = New SolidColorBrush(Color.FromRgb(11, 14, 17))

            For Each cluster In _clusters
                Dim radius = RadiusOf(cluster)
                If cluster Is _hoverCluster Then radius += 2
                Dim screenY = cluster.Y - _originY
                If screenY < -radius OrElse screenY > size.Height + radius Then Continue For
                ' Dieselbe Stelle kann bei kleiner Zoomstufe mehrfach im Fenster liegen.
                Dim baseX = cluster.X - _originX
                Dim firstCopy = CLng(Math.Ceiling((-radius - baseX) / world))
                Dim lastCopy = CLng(Math.Floor((size.Width + radius - baseX) / world))
                For copy = firstCopy To lastCopy
                    Dim center = New Point(baseX + copy * world, screenY)
                    context.DrawEllipse(AccentBrush, If(cluster Is _hoverCluster, hoverOutline, outline), center, radius, radius)
                    If cluster.Items.Count > 1 Then
                        Dim text = New FormattedText(cluster.Items.Count.ToString(CultureInfo.CurrentCulture),
                                                     CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                                     Typeface.Default, 12, textBrush) With {.TextAlignment = TextAlignment.Left}
                        context.DrawText(text, New Point(center.X - text.Width / 2, center.Y - text.Height / 2))
                    End If
                    _hitTargets.Add(New HitTarget With {.Center = center, .Radius = radius, .Cluster = cluster})
                Next
            Next
        End Sub

        Private Sub DrawAttribution(context As DrawingContext, size As Size)
            _attributionRect = DrawLabel(context, AttributionText, New Point(size.Width - 4, size.Height - 4), alignRight:=True)
        End Sub

        ''' <summary>Ein Schild mit hellem Grund. Rechtsbündig heißt: der Punkt ist die Ecke unten
        ''' rechts, sonst oben links.</summary>
        Private Shared Function DrawLabel(context As DrawingContext, text As String, anchor As Point, alignRight As Boolean) As Rect
            Dim formatted = New FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                              Typeface.Default, 11, New SolidColorBrush(Color.FromRgb(40, 40, 40)))
            Dim width = formatted.Width + 10
            Dim height = formatted.Height + 4
            Dim rect = If(alignRight,
                          New Rect(anchor.X - width, anchor.Y - height, width, height),
                          New Rect(anchor.X, anchor.Y, width, height))
            context.FillRectangle(New SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), rect, 3)
            context.DrawText(formatted, New Point(rect.X + 5, rect.Y + 2))
            Return rect
        End Function

        ' ------------------------------------------------------------------------------------
        ' Kacheln
        ' ------------------------------------------------------------------------------------

        Private Shared Function TileKey(zoom As Integer, x As Integer, y As Integer) As String
            Return String.Concat(zoom.ToString(CultureInfo.InvariantCulture), "/",
                                 x.ToString(CultureInfo.InvariantCulture), "/", y.ToString(CultureInfo.InvariantCulture))
        End Function

        Private Function TileFromMemory(zoom As Integer, x As Integer, y As Integer) As Bitmap
            Dim node As LinkedListNode(Of TileEntry) = Nothing
            If Not _tiles.TryGetValue(TileKey(zoom, x, y), node) Then Return Nothing
            _tileOrder.Remove(node)
            _tileOrder.AddFirst(node)
            Return node.Value.Bitmap
        End Function

        Private Sub StoreTile(key As String, bitmap As Bitmap)
            Dim existing As LinkedListNode(Of TileEntry) = Nothing
            If _tiles.TryGetValue(key, existing) Then
                _tileOrder.Remove(existing)
                existing.Value.Bitmap?.Dispose()
            End If
            Dim node = _tileOrder.AddFirst(New TileEntry With {.Key = key, .Bitmap = bitmap})
            _tiles(key) = node
            While _tileOrder.Count > MemoryTileLimit
                Dim oldest = _tileOrder.Last
                _tileOrder.RemoveLast()
                _tiles.Remove(oldest.Value.Key)
                oldest.Value.Bitmap?.Dispose()
            End While
        End Sub

        Private Sub ClearTiles()
            For Each entry In _tileOrder
                entry.Bitmap?.Dispose()
            Next
            _tileOrder.Clear()
            _tiles.Clear()
        End Sub

        Private Sub RequestTile(zoom As Integer, x As Integer, y As Integer)
            Dim key = TileKey(zoom, x, y)
            If _pending.Contains(key) Then Return
            Dim failedAt As DateTime
            If _failed.TryGetValue(key, failedAt) Then
                If Clock.Invoke() - failedAt < FailedTileRetryDelay Then Return
                _failed.Remove(key)
            End If
            _pending.Add(key)
            Dim ignored = LoadTileAsync(TileUrl, zoom, x, y, key, _loadCancellation.Token)
        End Sub

        Private Async Function LoadTileAsync(template As String, zoom As Integer, x As Integer, y As Integer,
                                             key As String, cancellationToken As CancellationToken) As Task
            Dim bitmap As Bitmap = Nothing
            Dim failed = False
            Try
                Dim bytes = Await MapTileService.Instance.GetTileAsync(template, zoom, x, y, cancellationToken)
                If bytes Is Nothing Then
                    failed = True
                Else
                    bitmap = Await Task.Run(Function() DecodeTile(bytes))
                    failed = bitmap Is Nothing
                End If
            Catch ex As OperationCanceledException
                failed = False
            Catch ex As Exception
                DiagnosticLogService.LogException("PhotoMap.LoadTile", ex)
                failed = True
            End Try

            ' Zurück auf dem Oberflächenfaden (der Await kehrt dorthin zurück).
            Dim stillWanted = Not cancellationToken.IsCancellationRequested AndAlso
                              String.Equals(template, TileUrl, StringComparison.Ordinal)
            If stillWanted Then _pending.Remove(key)
            If bitmap IsNot Nothing Then
                If stillWanted Then
                    StoreTile(key, bitmap)
                Else
                    bitmap.Dispose()
                End If
            ElseIf failed AndAlso stillWanted Then
                _failed(key) = Clock.Invoke()
                ' Ohne neues Zeichnen fragte niemand die Kachel wieder an, solange die Karte still
                ' steht. Nach der Wartezeit einmal neu zeichnen; das stoesst den Versuch an.
                ' Ein Zeitgeber fuer alle: faellt das Netz aus, scheitert gleich eine ganze Seite.
                If Not _retryScheduled Then
                    _retryScheduled = True
                    DispatcherTimer.RunOnce(Sub()
                                                _retryScheduled = False
                                                InvalidateVisual()
                                            End Sub, FailedTileRetryDelay + TimeSpan.FromSeconds(1))
                End If
            End If
            If stillWanted AndAlso zoom = _zoom Then InvalidateVisual()
        End Function

        Private Shared Function DecodeTile(bytes As Byte()) As Bitmap
            Try
                Using stream = New MemoryStream(bytes)
                    Return New Bitmap(stream)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("PhotoMap.DecodeTile", ex)
                Return Nothing
            End Try
        End Function

        ' ------------------------------------------------------------------------------------
        ' Eingabe
        ' ------------------------------------------------------------------------------------

        Private Function HitCluster(position As Point) As MapCluster
            Dim best As MapCluster = Nothing
            Dim bestDistance = Double.MaxValue
            For Each target In _hitTargets
                Dim dx = target.Center.X - position.X
                Dim dy = target.Center.Y - position.Y
                Dim distance = Math.Sqrt(dx * dx + dy * dy)
                If distance <= target.Radius + 4 AndAlso distance < bestDistance Then
                    best = target.Cluster
                    bestDistance = distance
                End If
            Next
            Return best
        End Function

        Protected Overrides Sub OnPointerPressed(e As PointerPressedEventArgs)
            MyBase.OnPointerPressed(e)
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            Focus()
            _dragStart = e.GetPosition(Me)
            _dragOriginX = _originX
            _dragOriginY = _originY
            _isDragging = True
            _moved = False
            e.Pointer.Capture(Me)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerMoved(e As PointerEventArgs)
            MyBase.OnPointerMoved(e)
            Dim position = e.GetPosition(Me)
            _lastPointer = position
            If _isDragging Then
                Dim dx = position.X - _dragStart.X
                Dim dy = position.Y - _dragStart.Y
                If Not _moved AndAlso Math.Abs(dx) + Math.Abs(dy) < DragThreshold Then Return
                _moved = True
                _userMoved = True
                _originX = _dragOriginX - dx
                _originY = _dragOriginY - dy
                ClampOrigin(Bounds.Size)
                InvalidateVisual()
                Return
            End If
            Dim hovered = HitCluster(position)
            Dim overAttribution = _attributionRect.Contains(position)
            Cursor = If(hovered IsNot Nothing OrElse overAttribution, New Cursor(StandardCursorType.Hand), Cursor.Default)
            If hovered IsNot _hoverCluster Then
                _hoverCluster = hovered
                InvalidateVisual()
            End If
        End Sub

        Protected Overrides Sub OnPointerReleased(e As PointerReleasedEventArgs)
            MyBase.OnPointerReleased(e)
            If Not _isDragging Then Return
            _isDragging = False
            e.Pointer.Capture(Nothing)
            If _moved Then Return
            Dim position = e.GetPosition(Me)
            If _attributionRect.Contains(position) Then
                ShellOpenService.Open(AttributionUrl, "PhotoMap.Attribution")
                Return
            End If
            Dim cluster = HitCluster(position)
            If cluster Is Nothing Then Return
            Dim items As IReadOnlyList(Of ImageItem) = cluster.Items.ToList()
            Dim command = ClusterCommand
            If command IsNot Nothing AndAlso command.CanExecute(items) Then command.Execute(items)
        End Sub

        Protected Overrides Sub OnPointerCaptureLost(e As PointerCaptureLostEventArgs)
            MyBase.OnPointerCaptureLost(e)
            _isDragging = False
        End Sub

        Protected Overrides Sub OnPointerWheelChanged(e As PointerWheelEventArgs)
            MyBase.OnPointerWheelChanged(e)
            ' Ein Tastfeld liefert Bruchteile; erst eine ganze Raste zoomt.
            _wheelAccumulator += e.Delta.Y
            Dim steps = CInt(Math.Truncate(_wheelAccumulator))
            If steps <> 0 Then
                _wheelAccumulator -= steps
                ZoomAt(e.GetPosition(Me), steps)
            End If
            e.Handled = True
        End Sub

        Protected Overrides Sub OnDoubleTapped(e As TappedEventArgs)
            MyBase.OnDoubleTapped(e)
            If HitCluster(e.GetPosition(Me)) IsNot Nothing Then Return
            ZoomAt(e.GetPosition(Me), 1)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            MyBase.OnKeyDown(e)
            If HandleKey(e.Key, e.KeyModifiers) Then e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerExited(e As PointerEventArgs)
            MyBase.OnPointerExited(e)
            _lastPointer = Nothing
            If _hoverCluster IsNot Nothing Then
                _hoverCluster = Nothing
                InvalidateVisual()
            End If
        End Sub

        Private Sub Pan(dx As Double, dy As Double)
            _userMoved = True
            _originX += dx
            _originY += dy
            ClampOrigin(Bounds.Size)
            InvalidateVisual()
        End Sub

    End Class

End Namespace
