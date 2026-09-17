Imports System
Imports System.Runtime.InteropServices
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform

Namespace Controls

    ''' <summary>Eine Ueberlagerung in Bildgroesse, aufgeteilt in Kacheln mit je eigener Bitmap.
    '''
    ''' WARUM KACHELN: Avalonia macht nach jedem Beschreiben einer WriteableBitmap deren Abbild
    ''' ungueltig, und der Zeichenfaden uebertraegt beim naechsten Zeichnen die GANZE Bitmap neu -
    ''' unter derselben Sperre, auf die das naechste Beschreiben wartet. Die Live-Vorschau von
    ''' Stempel und Verwischen wird beim Malen bis zu vierzigmal je Sekunde beschrieben; in einer
    ''' einzigen Bitmap von Vorschaugroesse hiess das jedes Mal zig Megabyte fuer ein paar Punkte
    ''' unter dem Pinsel. Hier wird nur die Kachel neu beschrieben, die der Pinsel beruehrt; die
    ''' uebrigen behalten ihr Abbild.
    '''
    ''' Kacheln entstehen erst beim ersten Beschreiben. Jede traegt einen Rand von
    ''' <see cref="TilePadding"/> Punkten aus den Nachbarkacheln mit, damit das Strecken beim
    ''' Anzeigen an der Kachelkante dieselben Nachbarn sieht wie in einer einzigen Bitmap.</summary>
    Public NotInheritable Class TiledOverlay
        Implements IDisposable

        Public Const TileSize As Integer = 512
        ''' <summary>Rand je Kachel. Am Pruefstand (Zeichnen ohne Grafikkarte) reichen schon 2 Punkte.
        ''' 32 sind Vorsicht fuer die Grafikkarte: verkleinert sie mit Zwischenstufen der Bitmap,
        ''' greift die Abtastung weiter aus, und die Stufen werden je Bitmap ab ihrer Ecke gebildet.
        ''' Liegt die Ecke auf einem Vielfachen von 32, fallen die ersten fuenf Stufen deckungsgleich
        ''' mit denen einer einzelnen Bitmap aus. Gemessen ist das nur ohne Grafikkarte.</summary>
        Public Const TilePadding As Integer = 32

        Private ReadOnly _tiles As WriteableBitmap()

        Public ReadOnly Property Width As Integer
        Public ReadOnly Property Height As Integer
        Public ReadOnly Property Columns As Integer
        Public ReadOnly Property Rows As Integer

        Public Sub New(width As Integer, height As Integer)
            Me.Width = Math.Max(1, width)
            Me.Height = Math.Max(1, height)
            Columns = (Me.Width + TileSize - 1) \ TileSize
            Rows = (Me.Height + TileSize - 1) \ TileSize
            _tiles = New WriteableBitmap(Columns * Rows - 1) {}
        End Sub

        ''' <summary>Das Feld der Kachel im Gesamtbild, ohne Rand.</summary>
        Public Function TileCore(column As Integer, row As Integer) As PixelRect
            Dim x = column * TileSize, y = row * TileSize
            Return New PixelRect(x, y, Math.Min(TileSize, Width - x), Math.Min(TileSize, Height - y))
        End Function

        ''' <summary>Das Feld der Kachel-Bitmap im Gesamtbild: das Kernfeld plus Rand, auf das Bild
        ''' begrenzt.</summary>
        Public Function TileBounds(column As Integer, row As Integer) As PixelRect
            Dim core = TileCore(column, row)
            Dim left = Math.Max(0, core.X - TilePadding)
            Dim top = Math.Max(0, core.Y - TilePadding)
            Dim right = Math.Min(Width, core.Right + TilePadding)
            Dim bottom = Math.Min(Height, core.Bottom + TilePadding)
            Return New PixelRect(left, top, right - left, bottom - top)
        End Function

        ''' <summary>Die Bitmap der Kachel, oder Nothing, solange nie in sie geschrieben wurde.</summary>
        Public Function TileAt(column As Integer, row As Integer) As WriteableBitmap
            Return _tiles(row * Columns + column)
        End Function

        ''' <summary>Schreibt einen Ausschnitt ins Gesamtbild. <paramref name="pixels"/> traegt die
        ''' Zeilen des Ausschnitts dicht hintereinander, je Punkt BGRA vormultipliziert. Beschrieben
        ''' wird jede Kachel, deren Feld samt Rand den Ausschnitt schneidet - der Rand gehoert zum
        ''' Inhalt der Nachbarn und muss mitgehen.</summary>
        Public Sub Write(region As PixelRect, pixels As Byte())
            If pixels Is Nothing Then Return
            Dim clipped = region.Intersect(New PixelRect(0, 0, Width, Height))
            If clipped.Width <= 0 OrElse clipped.Height <= 0 Then Return
            If pixels.Length < region.Width * 4 * region.Height Then Return

            Dim firstColumn = Math.Max(0, (clipped.X - TilePadding) \ TileSize)
            Dim lastColumn = Math.Min(Columns - 1, (clipped.Right - 1 + TilePadding) \ TileSize)
            Dim firstRow = Math.Max(0, (clipped.Y - TilePadding) \ TileSize)
            Dim lastRow = Math.Min(Rows - 1, (clipped.Bottom - 1 + TilePadding) \ TileSize)
            For row = firstRow To lastRow
                For column = firstColumn To lastColumn
                    Dim bounds = TileBounds(column, row)
                    Dim part = bounds.Intersect(clipped)
                    If part.Width <= 0 OrElse part.Height <= 0 Then Continue For
                    Dim tile = EnsureTile(column, row, bounds)
                    Using fb = tile.Lock()
                        For y = part.Y To part.Bottom - 1
                            Dim sourceOffset = ((y - region.Y) * region.Width + (part.X - region.X)) * 4
                            Dim target = IntPtr.Add(fb.Address, (y - bounds.Y) * fb.RowBytes + (part.X - bounds.X) * 4)
                            Marshal.Copy(pixels, sourceOffset, target, part.Width * 4)
                        Next
                    End Using
                Next
            Next
        End Sub

        Private Function EnsureTile(column As Integer, row As Integer, bounds As PixelRect) As WriteableBitmap
            Dim index = row * Columns + column
            Dim tile = _tiles(index)
            If tile IsNot Nothing Then Return tile
            tile = New WriteableBitmap(New PixelSize(bounds.Width, bounds.Height), New Vector(96, 96),
                                       PixelFormat.Bgra8888, AlphaFormat.Premul)
            ' Ausdruecklich leeren: was nie beschrieben wurde, muss durchsichtig sein.
            Using fb = tile.Lock()
                Dim zero = New Byte(fb.RowBytes - 1) {}
                For y = 0 To bounds.Height - 1
                    Marshal.Copy(zero, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), fb.RowBytes)
                Next
            End Using
            _tiles(index) = tile
            Return tile
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            For i = 0 To _tiles.Length - 1
                _tiles(i)?.Dispose()
                _tiles(i) = Nothing
            Next
        End Sub
    End Class

    ''' <summary>Zeigt eine <see cref="TiledOverlay"/> gestreckt auf die eigene Groesse, wie ein
    ''' Image mit Stretch=Fill.
    '''
    ''' Jede Kachel wird samt Rand gezeichnet und auf ihr Kernfeld beschnitten. Der Beschnitt ist in
    ''' Avalonia ohne Kantenglaettung: jeder Bildschirmpunkt gehoert damit genau einer Kachel, auch
    ''' an krummen Kanten, und eine halbtransparente Ueberlagerung bekommt keine Naht.</summary>
    Public Class TiledOverlayControl
        Inherits Control

        Public Shared ReadOnly OverlayProperty As StyledProperty(Of TiledOverlay) =
            AvaloniaProperty.Register(Of TiledOverlayControl, TiledOverlay)(NameOf(Overlay))

        Shared Sub New()
            AffectsRender(Of TiledOverlayControl)(OverlayProperty)
        End Sub

        Public Property Overlay As TiledOverlay
            Get
                Return GetValue(OverlayProperty)
            End Get
            Set(value As TiledOverlay)
                SetValue(OverlayProperty, value)
            End Set
        End Property

        Public Overrides Sub Render(context As DrawingContext)
            Dim source = Overlay
            If source Is Nothing OrElse Bounds.Width <= 0 OrElse Bounds.Height <= 0 Then Return
            Dim scaleX = Bounds.Width / source.Width
            Dim scaleY = Bounds.Height / source.Height

            ' DIE BESCHNITTKANTEN LIEGEN AUF GANZEN BILDSCHIRMPUNKTEN. Gezeichnet wird der Beschnitt
            ' mit Kantenglaettung; lag eine Kachelkante mitten in einem Bildschirmpunkt, deckten
            ' beide Nachbarn ihn nur anteilig, und die Kante stand als dunkle Linie da (Pruefung
            ' "Retusche-Vorschau in Kacheln", bei 40 % bis 32 Stufen). Die Buehne zoomt ueber Groesse
            ' und Lage, nicht ueber Transformationen - der Abstand zum Fenster und dessen Skalierung
            ' reichen deshalb, um den Bildschirmpunkt zu treffen.
            Dim root = TopLevel.GetTopLevel(Me)
            Dim scaling = If(root Is Nothing, 1.0, root.RenderScaling)
            Dim origin = If(root Is Nothing, Nothing, Me.TranslatePoint(New Point(0, 0), root))
            Dim snapX = Function(value As Double) As Double
                            If Not origin.HasValue Then Return value
                            Return Math.Round((origin.Value.X + value) * scaling) / scaling - origin.Value.X
                        End Function
            Dim snapY = Function(value As Double) As Double
                            If Not origin.HasValue Then Return value
                            Return Math.Round((origin.Value.Y + value) * scaling) / scaling - origin.Value.Y
                        End Function

            For row = 0 To source.Rows - 1
                For column = 0 To source.Columns - 1
                    Dim tile = source.TileAt(column, row)
                    If tile Is Nothing Then Continue For
                    Dim core = source.TileCore(column, row)
                    Dim bounds = source.TileBounds(column, row)
                    ' Die Kanten aus ganzen Bildpunkten gerechnet und dann gerundet: die gemeinsame
                    ' Kante zweier Nachbarn ergibt so exakt dieselbe Zahl, und kein Bildschirmpunkt
                    ' faellt zwischen sie oder in beide.
                    Dim clipLeft = snapX(core.X * scaleX), clipRight = snapX(core.Right * scaleX)
                    Dim clipTop = snapY(core.Y * scaleY), clipBottom = snapY(core.Bottom * scaleY)
                    If clipRight <= clipLeft OrElse clipBottom <= clipTop Then Continue For
                    Dim clip = New Rect(clipLeft, clipTop, clipRight - clipLeft, clipBottom - clipTop)
                    Dim dest = New Rect(bounds.X * scaleX, bounds.Y * scaleY,
                                        bounds.Right * scaleX - bounds.X * scaleX,
                                        bounds.Bottom * scaleY - bounds.Y * scaleY)
                    Using context.PushClip(clip)
                        context.DrawImage(tile, New Rect(0, 0, bounds.Width, bounds.Height), dest)
                    End Using
                Next
            Next
        End Sub
    End Class

End Namespace
