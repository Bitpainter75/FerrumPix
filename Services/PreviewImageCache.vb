Imports System
Imports System.Collections.Generic
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Die Bilder der Live-Vorschau von Collage und Druck, einmal dekodiert und verkleinert.
    '''
    ''' WARUM. Beide Vorschauen rechnen nach jeder Aenderung im Dialog neu, und jeder Durchgang
    ''' dekodierte bisher jedes Bild aus der Datei: bei 60 Bildern auf der Netzfreigabe 60 volle
    ''' Decodes je Reglerschritt, beim Druck sogar mit RAW-Entwicklung. Die Vorschau ist hoechstens
    ''' 1200 Punkte gross; eine Fassung mit <see cref="MaxEdge"/> Punkten Kante reicht fuer jede
    ''' Kachel und jede Druckzelle darin.
    '''
    ''' WAS NICHT HIER LIEGT. Die gespeicherte Collage und das Druck-PDF rechnen weiter aus den vollen
    ''' Dateien, sonst kaeme die Ausgabe unscharf heraus.
    '''
    ''' LEBENSDAUER. Geleert wird beim Schliessen des Dialogs (<see cref="Clear"/>). Wer zeichnet,
    ''' bekommt eine KOPIE: ein Vorschaurender, der beim Schliessen noch laeuft, haelt so nie ein
    ''' Bitmap, das gerade freigegeben wird. Bei einer verkleinerten Fassung kostet die Kopie
    ''' Bruchteile einer Millisekunde.
    ''' </summary>
    Public NotInheritable Class PreviewImageCache

        Private Sub New()
        End Sub

        ''' <summary>Lange Kante der gehaltenen Fassung.</summary>
        Public Const MaxEdge As Integer = 1200

        ''' <summary>Obergrenze des Speichers; darueber fallen die aeltesten Eintraege heraus.</summary>
        Private Const MaxBytes As Long = 512L * 1024L * 1024L

        Private Shared ReadOnly _lock As New Object()
        Private Shared ReadOnly _entries As New Dictionary(Of String, SKBitmap)(StringComparer.Ordinal)
        Private Shared ReadOnly _order As New LinkedList(Of String)()
        Private Shared _bytes As Long
        ''' <summary>Steigt bei jedem <see cref="Clear"/>. Ein Render, der vorher begann, traegt
        ''' danach nichts mehr ein.</summary>
        Private Shared _generation As Long

        ''' <summary>Die aktuelle Generation; ein Render liest sie einmal zu Beginn und gibt sie an
        ''' jedes <see cref="GetCopy"/> weiter.</summary>
        Public Shared ReadOnly Property Generation As Long
            Get
                SyncLock _lock
                    Return _generation
                End SyncLock
            End Get
        End Property

        ''' <summary>Eine Kopie der verkleinerten Fassung von <paramref name="path"/>, beim ersten Mal
        ''' ueber <paramref name="decode"/> geholt. Nothing, wenn sich das Bild nicht dekodieren
        ''' laesst. Der Aufrufer besitzt das Ergebnis.</summary>
        ''' <param name="decodeKind">Alles, was das dekodierte Bild ausser Pfad und Aenderungszeit
        ''' bestimmt (RAW entwickeln, vermerkte Vorgaenge mitrechnen).</param>
        ''' <param name="generation">Die <see cref="Generation"/> beim START des Renders, nicht des
        ''' einzelnen Abrufs: ein Collage-Render holt nach dem Schliessen noch weitere Bilder, und mit
        ''' der Generation des Abrufs fuellten die den eben geleerten Speicher wieder.</param>
        Public Shared Function GetCopy(path As String, decodeKind As String, generation As Long, decode As Func(Of SKBitmap)) As SKBitmap
            Dim writeTime As DateTime
            Dim sidecarTime As DateTime
            Try
                writeTime = File.GetLastWriteTimeUtc(path)
                ' Die Beistelldatei mit dem Rezept gehoert zum Bild: aendert sie sich, ist es ein
                ' anderes Bild (DecodeDevelopedForOutput wendet sie an). Fehlt sie, liefert das ein
                ' festes Datum, und der Schluessel bleibt stabil.
                sidecarTime = File.GetLastWriteTimeUtc(RawSidecarService.SidecarPathFor(path))
            Catch
                Return Nothing
            End Try
            Dim key = path & "|" & writeTime.Ticks.ToString(Globalization.CultureInfo.InvariantCulture) & "|" &
                      sidecarTime.Ticks.ToString(Globalization.CultureInfo.InvariantCulture) & "|" & decodeKind
            SyncLock _lock
                Dim held As SKBitmap = Nothing
                If generation = _generation AndAlso _entries.TryGetValue(key, held) Then Return held.Copy()
            End SyncLock

            ' Ausserhalb der Sperre dekodieren: das dauert, und eine zweite Vorschau soll derweil
            ' die schon gehaltenen Bilder bekommen.
            Dim reduced As SKBitmap
            Using full = decode()
                If full Is Nothing Then Return Nothing
                reduced = Reduce(full)
            End Using
            If reduced Is Nothing Then Return Nothing

            SyncLock _lock
                ' Seit dem Start des Renders geleert (Dialog geschlossen): nicht mehr eintragen,
                ' sonst bliebe das Bild nach dem Schliessen im Speicher, und ein spaeteres Oeffnen
                ' faende eine Fassung von davor. Der Aufrufer bekommt es trotzdem.
                If generation <> _generation Then Return reduced
                Dim held As SKBitmap = Nothing
                If _entries.TryGetValue(key, held) Then
                    ' Ein paralleler Durchgang war schneller.
                    reduced.Dispose()
                    Return held.Copy()
                End If
                _entries(key) = reduced
                _order.AddLast(key)
                _bytes += reduced.ByteCount
                While _bytes > MaxBytes AndAlso _order.Count > 1
                    Dim oldest = _order.First.Value
                    _order.RemoveFirst()
                    Dim gone As SKBitmap = Nothing
                    If _entries.TryGetValue(oldest, gone) Then
                        _entries.Remove(oldest)
                        _bytes -= gone.ByteCount
                        gone.Dispose()
                    End If
                End While
                Return reduced.Copy()
            End SyncLock
        End Function

        ''' <summary>Alles freigeben; beim Schliessen des Dialogs.</summary>
        Public Shared Sub Clear()
            SyncLock _lock
                For Each held In _entries.Values
                    held.Dispose()
                Next
                _entries.Clear()
                _order.Clear()
                _bytes = 0
                _generation += 1
            End SyncLock
        End Sub

        ''' <summary>Wie viele Bilder gerade gehalten werden; fuer die Pruefung.</summary>
        Public Shared ReadOnly Property Count As Integer
            Get
                SyncLock _lock
                    Return _entries.Count
                End SyncLock
            End Get
        End Property

        Private Shared Function Reduce(full As SKBitmap) As SKBitmap
            Dim longEdge = Math.Max(full.Width, full.Height)
            If longEdge <= MaxEdge Then Return full.Copy()
            Dim factor = MaxEdge / CDbl(longEdge)
            Dim w = Math.Max(1, CInt(Math.Round(full.Width * factor)))
            Dim h = Math.Max(1, CInt(Math.Round(full.Height * factor)))
            ' Derselbe Verkleinerer wie fuer die Ausgabe (ImageResampler): Skias fester Filter faltet
            ' feines Muster in Streifen.
            If full.ColorType = SKColorType.Bgra8888 OrElse full.ColorType = SKColorType.Rgba8888 Then
                Dim resized = ImageResampler.Resize(full, w, h, ImageResampler.Kernel.CatmullRom)
                If resized IsNot Nothing Then Return resized
            End If
            Return full.Resize(New SKImageInfo(w, h, full.ColorType, full.AlphaType, full.ColorSpace),
                               New SKSamplingOptions(SKCubicResampler.Mitchell))
        End Function

    End Class

End Namespace
