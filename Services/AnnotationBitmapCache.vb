Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Objekt-Bitmap-Cache des Kompositor-Umbaus (OFFENE_PUNKTE Abschnitt 2, Stufe 2): haelt je
    ''' Objekt eine fertig gezeichnete Bitmap (inkl. Schatten, Gluehen und eigenen Pixel-Anpassungen,
    ''' gezeichnet vom Skia-Kern des Auswahl-Overlays), damit Verschieben und Drehen reine
    ''' Transformation werden - kein Render, kein Ghost, keine Uebergabe.
    '''
    ''' Der SCHLUESSEL ist das AUSSEHEN des Objekts, ausdruecklich OHNE Lage und Drehung: er
    ''' entsteht aus der Rezept-Serialisierung eines Klons, an dem alles Nicht-Aussehen
    ''' neutralisiert ist, plus der Zielaufloesung. Damit gilt dieselbe Regel wie beim Basis-Cache
    ''' von selbst: JEDES Feld, das das Rendern liest, steht im Schluessel - ein NEUES Rezeptfeld
    ''' ist automatisch drin, und nur wer es BEWUSST als Nicht-Aussehen einstuft, traegt es in
    ''' NeutralizeNonAppearance ein.
    '''
    ''' Die Eintraege haengen an der Objekt-Id (je Objekt genau eine Bitmap); der Speicher ist
    ''' ueber ein LRU-Budget gedeckelt. Die Zielaufloesung kommt vom Aufrufer und ist die
    ''' SZENEN-Groesse des Objekts, nicht die Zoomstufe - beim Zoomen wird NICHT neu gecacht.
    '''
    ''' Grenze mit Absicht: aendert sich der INHALT einer Bilddatei auf der Platte, ohne dass sich
    ''' der Pfad aendert, sieht der Schluessel das nicht - dieselbe Grenze hat das Rezept selbst.
    ''' </summary>
    Public NotInheritable Class AnnotationBitmapCache

        ''' <summary>Ein Cache-Eintrag: das fertig gezeichnete Objekt als UNVERAENDERLICHES SKImage
        ''' (einmal beim Fuellen umgewandelt - so kostet das Zeichnen je Blit keine Pixelkopie und
        ''' laesst sich mit Abtastung interpolieren) und die Lage des Objekts darin (Bild-Pixel,
        ''' Effektraender liegen aussen herum). Das Bild GEHOERT dem Cache - Aufrufer zeichnen es
        ''' nur und duerfen es nicht disposen. Und sie duerfen es NICHT ueber den naechsten
        ''' Cache-Zugriff hinaus festhalten: jede Aussehens-Aenderung, Remove oder Clear entsorgt
        ''' es sofort - ein festgehaltener Eintrag zeigt dann auf freigegebenen Speicher
        ''' (nativer Absturz, kein .NET-Fehler). Je Zeichenvorgang frisch per GetOrRender holen.</summary>
        Public NotInheritable Class Entry
            Public Property Image As SKImage
            Public Property ObjectX As Integer
            Public Property ObjectY As Integer
            Public Property ObjectWidth As Integer
            Public Property ObjectHeight As Integer
            Friend Property AppearanceKey As String
            Friend Property LastUse As Long
        End Class

        ' 256 MB Budget: bei 36 Kacheln zu je ~430x430 Bitmap-Pixeln (~0,7 MB) ist das reichlich,
        ' bei sehr vielen grossen Objekten faellt der aelteste Eintrag heraus und wird beim
        ' naechsten Zeichnen neu gerendert - falsch wird nie etwas, nur langsamer.
        Private Const DefaultMaxTotalBytes As Long = 256L * 1024L * 1024L

        Private ReadOnly _maxTotalBytes As Long
        Private ReadOnly _lock As New Object()
        Private ReadOnly _entries As New Dictionary(Of String, Entry)(StringComparer.Ordinal)
        Private _totalBytes As Long = 0
        Private _useCounter As Long = 0

        Private Shared ReadOnly KeyJsonOptions As New JsonSerializerOptions()

        ''' <param name="budgetBytes">Nur fuer den Pruefstand gedacht: ein kleines Budget macht die
        ''' LRU-Verdraengung ohne hunderte Megabyte messbar.</param>
        Public Sub New(Optional budgetBytes As Long = DefaultMaxTotalBytes)
            _maxTotalBytes = Math.Max(1, budgetBytes)
        End Sub

        ''' <summary>Liefert die Bitmap des Objekts fuer die Ziel-Szenengroesse - aus dem Cache oder
        ''' frisch gerendert. Nothing bei Objekten, die der Cache nicht traegt (Pinsel-/Radierer-
        ''' Ebenen: ihr Inhalt sind Striche im Bildraum, keine freistehende Objektflaeche).</summary>
        Public Function GetOrRender(annotation As ImageAnnotation, targetWidthPixels As Integer, targetHeightPixels As Integer) As Entry
            If annotation Is Nothing OrElse String.IsNullOrEmpty(annotation.Id) Then Return Nothing
            If targetWidthPixels <= 0 OrElse targetHeightPixels <= 0 Then Return Nothing
            Dim kind = If(annotation.Kind, "").Trim().ToLowerInvariant()
            If kind = "brush" OrElse kind = "eraser" Then Return Nothing

            Dim key = ComputeAppearanceKey(annotation, targetWidthPixels, targetHeightPixels)

            SyncLock _lock
                Dim existing As Entry = Nothing
                If _entries.TryGetValue(annotation.Id, existing) Then
                    If String.Equals(existing.AppearanceKey, key, StringComparison.Ordinal) Then
                        _useCounter += 1
                        existing.LastUse = _useCounter
                        Return existing
                    End If
                    ' Aussehen geaendert: der alte Stand ist wertlos.
                    RemoveLocked(annotation.Id)
                End If
            End SyncLock

            ' Rendern AUSSERHALB der Sperre - Effekt-Renders grosser Objekte kosten dreistellige
            ' Millisekunden, und andere Objekte sollen waehrenddessen an ihre Bitmaps kommen.
            ' UNGEDECKELT in Szenenaufloesung: das komponierte Bild muss pixelgleich zur gebackenen
            ' Qualitaet sein (der 720er-Deckel gehoert nur dem Ghost).
            Dim rendered = ImageProcessor.RenderAnnotationOverlaySk(annotation, targetWidthPixels, targetHeightPixels,
                                                                    maxRenderDimension:=Single.PositiveInfinity)
            If rendered Is Nothing OrElse rendered.Bitmap Is Nothing Then Return Nothing

            ' Einmalige Umwandlung in ein unveraenderliches SKImage - jedes spaetere Zeichnen ist
            ' dann kopiefrei.
            Dim renderedImage = SKImage.FromBitmap(rendered.Bitmap)
            rendered.Bitmap.Dispose()
            If renderedImage Is Nothing Then Return Nothing

            Dim fresh As New Entry With {
                .Image = renderedImage,
                .ObjectX = rendered.ObjectX,
                .ObjectY = rendered.ObjectY,
                .ObjectWidth = rendered.ObjectWidth,
                .ObjectHeight = rendered.ObjectHeight,
                .AppearanceKey = key
            }

            SyncLock _lock
                ' Ein paralleler Renderer kann schneller gewesen sein - dann gewinnt der Bestand,
                ' und die eigene Arbeit wird verworfen (Bitmap gehoert sonst niemandem).
                Dim winner As Entry = Nothing
                If _entries.TryGetValue(annotation.Id, winner) AndAlso
                   String.Equals(winner.AppearanceKey, key, StringComparison.Ordinal) Then
                    fresh.Image.Dispose()
                    _useCounter += 1
                    winner.LastUse = _useCounter
                    Return winner
                End If
                RemoveLocked(annotation.Id)
                _useCounter += 1
                fresh.LastUse = _useCounter
                _entries(annotation.Id) = fresh
                _totalBytes += ImageBytes(fresh.Image)
                EvictOverBudgetLocked(keepId:=annotation.Id)
                Return fresh
            End SyncLock
        End Function

        ''' <summary>Wirft den Eintrag eines Objekts weg (Objekt geloescht oder gerastert).</summary>
        Public Sub Remove(annotationId As String)
            If String.IsNullOrEmpty(annotationId) Then Return
            SyncLock _lock
                RemoveLocked(annotationId)
            End SyncLock
        End Sub

        ''' <summary>Leert den Cache vollstaendig (Dokumentwechsel).</summary>
        Public Sub Clear()
            SyncLock _lock
                For Each entry In _entries.Values
                    entry.Image?.Dispose()
                Next
                _entries.Clear()
                _totalBytes = 0
            End SyncLock
        End Sub

        ''' <summary>Belegter Speicher in Bytes - fuer Diagnose und LRU-Pruefung.</summary>
        Public ReadOnly Property TotalBytes As Long
            Get
                SyncLock _lock
                    Return _totalBytes
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property Count As Integer
            Get
                SyncLock _lock
                    Return _entries.Count
                End SyncLock
            End Get
        End Property

        Private Sub RemoveLocked(annotationId As String)
            Dim entry As Entry = Nothing
            If Not _entries.TryGetValue(annotationId, entry) Then Return
            _totalBytes -= ImageBytes(entry.Image)
            entry.Image?.Dispose()
            _entries.Remove(annotationId)
        End Sub

        ''' <summary>LRU: solange das Budget ueberschritten ist, faellt der am laengsten unbenutzte
        ''' Eintrag - nie der gerade angeforderte, sonst rendert ein einzelnes Riesenobjekt sich
        ''' bei jedem Zugriff selbst aus dem Cache.</summary>
        Private Sub EvictOverBudgetLocked(keepId As String)
            While _totalBytes > _maxTotalBytes AndAlso _entries.Count > 1
                Dim victimId As String = Nothing
                Dim oldest = Long.MaxValue
                For Each pair In _entries
                    If String.Equals(pair.Key, keepId, StringComparison.Ordinal) Then Continue For
                    If pair.Value.LastUse < oldest Then
                        oldest = pair.Value.LastUse
                        victimId = pair.Key
                    End If
                Next
                If victimId Is Nothing Then Return
                RemoveLocked(victimId)
            End While
        End Sub

        Private Shared Function ImageBytes(image As SKImage) As Long
            If image Is Nothing Then Return 0
            Return CLng(image.Width) * image.Height * 4L
        End Function

        ''' <summary>Der Aussehens-Schluessel: Rezept-Serialisierung eines Klons, an dem alles
        ''' neutralisiert ist, was NICHT das Aussehen der Bitmap bestimmt, plus Zielaufloesung.
        ''' Gehasht, damit lange Texte und eingebettete Anpassungssaetze den Schluessel nicht
        ''' aufblaehen.</summary>
        Friend Shared Function ComputeAppearanceKey(annotation As ImageAnnotation, targetWidthPixels As Integer, targetHeightPixels As Integer) As String
            Dim clone = annotation.Clone()
            NeutralizeNonAppearance(clone)
            Dim json = JsonSerializer.Serialize(clone, KeyJsonOptions)
            Using sha = SHA256.Create()
                Dim hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json))
                Return $"{Convert.ToHexString(hash)}|{targetWidthPixels}x{targetHeightPixels}"
            End Using
        End Function

        ''' <summary>Alles, was NICHT in den Aussehens-Schluessel gehoert, auf feste Werte setzen.
        ''' Die Liste ist bewusst die AUSNAHME-Liste: ein neues Rezeptfeld steht automatisch im
        ''' Schluessel, bis es hier BEWUSST als Nicht-Aussehen eingestuft wird.
        '''
        ''' - Lage, Groesse und Drehung: der Kompositor transformiert, der Kern zeichnet ohne sie
        '''   (die Zielaufloesung steht getrennt im Schluessel).
        ''' - Id, Gruppe, Name, Sperre, Sichtbarkeit, Vorlagenname: Verwaltung, keine Pixel.
        ''' - Anker und ScaleWithImage: bestimmen die LAGE bzw. die Export-Skalierung.
        ''' - Mischmodus samt Kontur-Schalter: wirkt beim KOMPONIEREN, nicht in der Bitmap -
        '''   Objekte mit Mischmodus bleiben ohnehin im gebackenen Block.</summary>
        Private Shared Sub NeutralizeNonAppearance(clone As ImageAnnotation)
            clone.XPixels = 0
            clone.YPixels = 0
            clone.WidthPixels = 0
            clone.HeightPixels = 0
            clone.RotationDegrees = 0
            ' FESTER Wert statt Leerstring: eine leere Id erzeugt sich beim naechsten Lesen selbst
            ' neu (Guid), und der Serialisierer LIEST sie - mit "" waere jeder Schluessel einmalig
            ' und der Cache traefe nie.
            clone.Id = "appearance"
            clone.GroupId = ""
            clone.CustomName = ""
            clone.SourceFileName = ""
            clone.WatermarkPresetName = ""
            clone.IsLocked = False
            clone.IsVisible = True
            clone.Anchor = ""
            clone.ScaleWithImage = True
            clone.BlendMode = "Normal"
            clone.BlendIncludesStroke = True
        End Sub
    End Class
End Namespace
