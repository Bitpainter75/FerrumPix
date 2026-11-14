Imports System
Imports System.Buffers
Imports System.Runtime.CompilerServices
Imports System.Runtime.InteropServices
Imports System.Threading
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Das TIEFE BILD fuer die Ausgabe in 16 Bit: neben dem 8-Bit-Bild der Kette laeuft ein zweites
    ''' mit 16 Bit je Kanal (Rgba16161616) durch dieselben Stufen.
    '''
    ''' WARUM DANEBEN UND NICHT STATTDESSEN. Die Kette misst an ihrem Bild (Helligkeit fuer Lichter und
    ''' Tiefen, Filmnegativ, oertliche Basis, Fuehrung des Farbmischers), und Masken, Objekte und
    ''' Zwischenspeicher sind 8 Bit. Das 8-Bit-Bild bleibt deshalb das Bild, an dem gemessen und
    ''' entschieden wird, genau wie in der Vorschau; das tiefe traegt nur die Tonwerte feiner. Fuer
    ''' jedes Paar gilt: das 8-Bit-Bild ist das tiefe, mit Dither auf 8 Bit gebracht.
    '''
    ''' WIE ES MITLAEUFT. Das tiefe Bild haengt als Begleiter an seinem 8-Bit-Bild (schwacher
    ''' Verweis, wie das Ursprungsbild der Protokollebene). Eine Stufe, die tief rechnen kann, holt
    ''' den Begleiter ihrer Quelle, rechnet ihn mit und haengt das Ergebnis an ihr Ergebnis. Eine
    ''' Stufe, die das nicht kann, weiss von alldem nichts: ihr Ergebnis hat keinen Begleiter, und
    ''' der der Quelle wird beim Ersetzen freigegeben (ReplaceBitmap, ReplaceBitmapOwned). Am Ende
    ''' der Kette gibt es also nur dann ein tiefes Bild, wenn JEDE wirksame Stufe es mitgerechnet
    ''' hat. Eine 16-Bit-Datei mit 8-Bit-Inhalt kann so nicht entstehen.
    '''
    ''' EINE STUFE DARF IHRE QUELLE NICHT AN ORT UND STELLE AENDERN, solange sie den Begleiter nicht
    ''' mitfuehrt: der bliebe sonst haengen und waere veraltet. Dagegen steht am Ende der Vergleich
    ''' in <see cref="DeepMatchesShallow"/>.
    ''' </summary>
    Partial Public Class ImageProcessor

        ''' <summary>Was SaveImage ueber die geschriebene Datei meldet. Der Aufrufer gibt ein Objekt
        ''' hinein und liest es danach; so erfaehrt er, ob aus verlangten 16 Bit doch 8 wurden.
        ''' (Kein ByRef-Parameter: den muesste jeder Aufrufer angeben, auch der Pruefstand.)</summary>
        Public NotInheritable Class SaveOutcome
            ''' Farbtiefe der geschriebenen Datei je Kanal, 8 oder 16.
            Public Property WrittenBitDepth As Integer = 8
        End Class

        Private Shared ReadOnly _deepCompanions As New ConditionalWeakTable(Of SKBitmap, SKBitmap)()

        ''' <summary>Wie viele tiefe Bilder gerade unterwegs sind. Ausserhalb einer tiefen Ausgabe
        ''' ist das 0, und die Kette fragt die Tabelle dann gar nicht erst.</summary>
        Private Shared _deepCompanionCount As Integer

        ''' <summary>Der Farbtyp des tiefen Bilds.</summary>
        Friend Const DeepColorType As SKColorType = SKColorType.Rgba16161616

        ''' <summary>Haengt <paramref name="deep"/> als tiefes Bild an <paramref name="bitmap"/>.
        ''' Der Besitz geht an die Tabelle: freigegeben wird es ueber <see cref="DropDeep"/> oder
        ''' herausgenommen ueber <see cref="DetachDeep"/>. Passen Masse oder Farbtyp nicht, wird es
        ''' gleich freigegeben und nichts angehaengt.</summary>
        Friend Shared Sub AttachDeep(bitmap As SKBitmap, deep As SKBitmap)
            If deep Is Nothing Then Return
            If bitmap Is Nothing OrElse deep.ColorType <> DeepColorType OrElse
               deep.Width <> bitmap.Width OrElse deep.Height <> bitmap.Height Then
                deep.Dispose()
                Return
            End If
            DropDeep(bitmap)
            SyncLock _deepCompanions
                _deepCompanions.Add(bitmap, deep)
            End SyncLock
            Interlocked.Increment(_deepCompanionCount)
        End Sub

        ''' <summary>Das tiefe Bild zu <paramref name="bitmap"/>, oder Nothing. Es bleibt angehaengt
        ''' und gehoert weiter der Tabelle.</summary>
        Friend Shared Function DeepOf(bitmap As SKBitmap) As SKBitmap
            If bitmap Is Nothing OrElse Volatile.Read(_deepCompanionCount) <= 0 Then Return Nothing
            Dim deep As SKBitmap = Nothing
            SyncLock _deepCompanions
                If Not _deepCompanions.TryGetValue(bitmap, deep) Then Return Nothing
            End SyncLock
            If deep Is Nothing OrElse deep.Handle = IntPtr.Zero Then Return Nothing
            Return deep
        End Function

        ''' <summary>Nimmt das tiefe Bild von <paramref name="bitmap"/> ab; der Besitz geht an den
        ''' Aufrufer.</summary>
        Friend Shared Function DetachDeep(bitmap As SKBitmap) As SKBitmap
            If bitmap Is Nothing OrElse Volatile.Read(_deepCompanionCount) <= 0 Then Return Nothing
            Dim deep As SKBitmap = Nothing
            SyncLock _deepCompanions
                If Not _deepCompanions.TryGetValue(bitmap, deep) Then Return Nothing
                _deepCompanions.Remove(bitmap)
            End SyncLock
            Interlocked.Decrement(_deepCompanionCount)
            Return deep
        End Function

        ''' <summary>Gibt das tiefe Bild von <paramref name="bitmap"/> frei, wenn eines daran
        ''' haengt.</summary>
        Friend Shared Sub DropDeep(bitmap As SKBitmap)
            Dim deep = DetachDeep(bitmap)
            deep?.Dispose()
        End Sub

        ''' <summary>Fuer einen Using-Block: gibt am Ende das tiefe Bild frei, das dann an
        ''' <paramref name="bitmap"/> haengt. Daneben steht das Bitmap selbst im Block; mit ihm
        ''' allein bliebe sein Begleiter liegen, bis die Speicherbereinigung ihn findet.</summary>
        Friend Shared Function DeepGuard(bitmap As SKBitmap) As IDisposable
            Return New DeepRelease(bitmap)
        End Function

        Private NotInheritable Class DeepRelease
            Implements IDisposable

            Private ReadOnly _bitmap As SKBitmap

            Public Sub New(bitmap As SKBitmap)
                _bitmap = bitmap
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                DropDeep(_bitmap)
            End Sub
        End Class

        ''' <summary>Schreibt dieses Ziel ein tiefes Bild mit 16 Bit je Kanal? TIFF, PNG und
        ''' JPEG XL; JPEG, WebP und PDF kennen nur 8 Bit.</summary>
        Friend Shared Function CanEncodeDeep(targetPath As String) As Boolean
            If String.IsNullOrWhiteSpace(targetPath) Then Return False
            If TiffPreviewService.IsSupportedTiff(targetPath) OrElse JxlDecodeService.IsSupportedJxl(targetPath) Then Return True
            Return String.Equals(IO.Path.GetExtension(targetPath), ".png", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Kann aus dieser Quelle eine Datei mit 16 Bit entstehen? Nur aus einer RAW-Datei,
        ''' die durch die RAW-Stufe laeuft: dort liegen die Tonwerte feiner vor als 8 Bit. Eine
        ''' 8-Bit-Quelle (JPEG, WebP, PNG) bekommt keine Ausgabe in 16 Bit, und ein TIFF oder PNG
        ''' mit 16 Bit wird beim Lesen auf 8 gebracht.
        '''
        ''' Die Frage fuer die Dialoge: danach richtet sich, ob sie die Farbtiefe anbieten. Ob die
        ''' Datei am Ende wirklich 16 Bit traegt, entscheidet erst SaveImage (Maskenebenen, Objekte
        ''' oder Retusche beenden das tiefe Bild).</summary>
        Public Shared Function CanSourceGoDeep(sourcePath As String) As Boolean
            If String.IsNullOrWhiteSpace(sourcePath) Then Return False
            Return RawPreviewService.IsSupportedRaw(sourcePath) AndAlso RawDecodeService.DevelopsThroughRawStage(sourcePath)
        End Function

        ''' <summary>Traegt dieses Rezept etwas, womit die Kette das tiefe Bild sicher nicht bis ans
        ''' Ende bringt? Dann entstuende trotz gewaehlter 16 Bit eine Datei mit 8 Bit, und der Dialog
        ''' bietet die Farbtiefe fuer dieses Bild gar nicht erst an.
        '''
        ''' Die Liste folgt den Stellen, an denen das tiefe Bild heute endet: gebackene Vorgaenge
        ''' (Retusche, Striche, Objekt entfernen, Entrauschen am fertigen Bild: sie stecken als
        ''' 8-Bit-Bildpunkte im Arbeitsbild), Hochskalieren mit Modell, ein ausgeblendeter
        ''' Hintergrund, Korrekturebenen im Objektstapel, Gruppen als eigener Schritt und Staub und
        ''' Kratzer entfernen. Wer eine dieser Stellen tief rechnen laesst, nimmt sie hier heraus.
        '''
        ''' Eine Vorabfrage, keine Zusage: Nein heisst nur, dass das Rezept nichts dagegen hat. Ob
        ''' die Datei wirklich 16 Bit traegt, entscheidet SaveImage und meldet es in
        ''' <see cref="SaveOutcome"/>.</summary>
        Public Shared Function RecipeBlocksDeep(adj As ImageAdjustments) As Boolean
            If adj Is Nothing Then Return False
            If adj.BakedOperations IsNot Nothing AndAlso adj.BakedOperations.Count > 0 Then Return True
            If Not String.IsNullOrWhiteSpace(adj.UpscaleModel) Then Return True
            If adj.BackgroundHidden Then Return True
            If adj.DustScratches < 0 Then Return True
            If adj.MaskedAdjustmentLayers IsNot Nothing AndAlso
               adj.MaskedAdjustmentLayers.Any(Function(l) l IsNot Nothing AndAlso Not String.IsNullOrEmpty(l.StackAboveAnnotationId)) Then
                Return True
            End If
            Return HasRenderStepGroupMember(adj)
        End Function

        ''' <summary>Haengt an ein frisch entwickeltes RAW-Bild sein tiefes Bild: derselbe Decode mit
        ''' denselben Angaben wie in <see cref="DecodeForRender"/>, aus den linearen Daten, die nach
        ''' dem 8-Bit-Decode bereitliegen. Nichts geschieht, wenn die Datei nicht durch die RAW-Stufe
        ''' laeuft; dann hat die Kette kein tiefes Bild, und die Ausgabe bleibt bei 8 Bit.</summary>
        Friend Shared Sub AttachDeepRawSource(decoded As SKBitmap, path As String, adj As ImageAdjustments,
                                              developRaw As Boolean)
            If decoded Is Nothing OrElse Not developRaw OrElse Not RawPreviewService.IsSupportedRaw(path) Then Return
            If Not RawDecodeService.DevelopsThroughRawStage(path) Then Return
            Dim deep = RawDecodeService.TryDecodeDeep(path, LensChoiceFrom(adj),
                                                      adj IsNot Nothing AndAlso adj.RawHighlightRecovery,
                                                      If(adj Is Nothing, RawDecodeService.HighlightRecoveryKnee, adj.RawHighlightKnee()),
                                                      If(adj Is Nothing, 0.0, adj.RawExposureEv()))
            ' Passt es nicht zum 8-Bit-Bild (andere Masse, anderer Inhalt), ist es nicht sein
            ' tiefes Bild: etwa wenn statt der Entwicklung die eingebettete Vorschau einsprang.
            If deep IsNot Nothing AndAlso
               (deep.Width <> decoded.Width OrElse deep.Height <> decoded.Height OrElse Not DeepMatchesShallow(decoded, deep)) Then
                deep.Dispose()
                deep = Nothing
            End If
            AttachDeep(decoded, deep)
        End Sub

        ''' <summary>Haengt an das ARBEITSBILD des Editors das tiefe Bild der RAW-Stufe, wenn beide
        ''' dasselbe Bild sind: die Quelle ist eine RAW, und im Arbeitsbild steckt nichts als ihre
        ''' Entwicklung, mit der Belichtung und dem Knie, die das Rezept als "im Quellbild" vermerkt
        ''' (ExposureInSource, KneeInSource).
        '''
        ''' Ob das so ist, sagt kein Schalter, sondern der Vergleich Bildpunkt fuer Bildpunkt
        ''' (<see cref="DeepMatchesShallow"/> mit everyPixel). Retusche, Malerei, gebackene Vorgaenge
        ''' oder ein Arbeitsbild, das mit anderen Angaben entstand, fallen dort durch; dann bleibt
        ''' das Arbeitsbild ohne tiefes Bild, und die Ausgabe bei 8 Bit.</summary>
        Friend Shared Sub AttachDeepWorkingSource(working As SKBitmap, path As String, adj As ImageAdjustments,
                                                  developRaw As Boolean)
            If working Is Nothing OrElse adj Is Nothing OrElse Not developRaw OrElse Not RawPreviewService.IsSupportedRaw(path) Then Return
            If Not RawDecodeService.DevelopsThroughRawStage(path) Then Return
            Dim deep = RawDecodeService.TryDecodeDeep(path, LensChoiceFrom(adj), adj.RawHighlightRecovery,
                                                      adj.KneeInSource,
                                                      adj.ExposureInSource / ImageAdjustments.ExposurePointsPerStop)
            If deep Is Nothing Then Return
            If deep.Width <> working.Width OrElse deep.Height <> working.Height OrElse
               Not DeepMatchesShallow(working, deep, everyPixel:=True) Then
                DiagnosticLogService.LogAlways("ImageProcessor.Deep",
                    "das Arbeitsbild ist nicht die Entwicklung der RAW-Stufe (Retusche, Malerei oder andere Angaben), es bleibt bei 8 Bit")
                deep.Dispose()
                Return
            End If
            AttachDeep(working, deep)
        End Sub

        ''' <summary>Fuehrt eine Stufe aus, die mit dem Farbtyp ihrer Quelle rechnet (sie zeichnet
        ''' ueber Skia in ein Bild derselben Art), und rechnet das tiefe Bild mit DERSELBEN Stufe
        ''' mit: ein zweiter Aufruf, diesmal mit dem tiefen Bild als Quelle. Liefert das Ergebnis
        ''' der Stufe fuer das 8-Bit-Bild, mit seinem tiefen Bild als Begleiter.
        '''
        ''' Kommt aus dem zweiten Aufruf kein tiefes Bild in denselben Massen (die Stufe legt ihr
        ''' Ergebnis doch mit festem Farbtyp an, oder sie scheitert), hat das Ergebnis keinen
        ''' Begleiter, und die Ausgabe bleibt bei 8 Bit.</summary>
        Private Shared Function RunStepWithDeep(source As SKBitmap, stage As Func(Of SKBitmap, SKBitmap)) As SKBitmap
            Dim result = stage(source)
            If result Is Nothing OrElse ReferenceEquals(result, source) Then Return result
            Dim deep = DeepOf(source)
            If deep Is Nothing Then Return result
            Dim deepResult As SKBitmap = Nothing
            Try
                deepResult = stage(deep)
            Catch ex As Exception
                DiagnosticLogService.LogException("ImageProcessor.Deep", ex)
                Return result
            End Try
            If deepResult Is Nothing OrElse ReferenceEquals(deepResult, deep) Then Return result
            AttachDeep(result, deepResult)
            Return result
        End Function

        ''' <summary>Die Objekte der Buehne auf dem tiefen Bild, fuer den EINFACHEN Fall von
        ''' ApplyAnnotationsCore: das Bild als Grund, alle Objekte in einem Zug auf dieselbe Flaeche
        ''' gezeichnet. Das Zeichnen selbst (Formen, Text, Bilder, Effekte) rechnet Skia, hier auf
        ''' einer Flaeche mit 16 Bit; was ein Objekt an Farbe mitbringt, ist dabei so fein wie seine
        ''' Quelle, das Foto darunter bleibt tief.
        '''
        ''' Nothing, und damit kein tiefes Ergebnis, fuer die Faelle, die dort eigene 8-Bit-Zwischenbilder
        ''' brauchen: ein ausgeblendeter Hintergrund, Korrekturebenen im Objektstapel und Gruppen, die
        ''' als eigener Schritt gerechnet werden.</summary>
        Private Shared Function ApplyAnnotationsDeep(deep As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If deep Is Nothing OrElse adj Is Nothing OrElse adj.BackgroundHidden Then Return Nothing
            If adj.Annotations Is Nothing OrElse adj.Annotations.Count = 0 Then Return Nothing
            If adj.MaskedAdjustmentLayers IsNot Nothing AndAlso
               adj.MaskedAdjustmentLayers.Any(Function(l) l IsNot Nothing AndAlso Not String.IsNullOrEmpty(l.StackAboveAnnotationId)) Then
                Return Nothing
            End If
            If HasRenderStepGroupMember(adj) Then Return Nothing
            ' Vormultipliziert wie das 8-Bit-Bild dieser Stufe (CloneBitmapForAnnotationComposite).
            Dim result = NewDeepBitmap(deep.Width, deep.Height, SKAlphaType.Premul)
            If result Is Nothing Then Return Nothing
            Try
                Using canvas = New SKCanvas(result)
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(deep, 0, 0, paint)
                    End Using
                    DrawAnnotationsOnCanvas(canvas, adj, deep.Width, deep.Height, 0, 0, deep.Width, deep.Height, adj.Annotations)
                End Using
                Return result
            Catch ex As Exception
                DiagnosticLogService.LogException("ImageProcessor.Deep", ex)
                result.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Gibt <paramref name="copy"/>, einer Kopie von <paramref name="original"/>, eine
        ''' eigene Kopie von dessen tiefem Bild mit. Ohne tiefes Bild am Original geschieht nichts.</summary>
        Private Shared Sub CopyDeepTo(original As SKBitmap, copy As SKBitmap)
            Dim deep = DeepOf(original)
            If deep Is Nothing OrElse copy Is Nothing OrElse ReferenceEquals(original, copy) Then Return
            AttachDeep(copy, CopyBitmap(deep))
        End Sub

        ''' <summary>Die Begrenzung auf eine Auswahl fuer das tiefe Bild, dieselbe Mischung wie
        ''' <see cref="CompositeSelectionScoped"/>: ausserhalb der Maske der Stand vor den
        ''' Anpassungen, innerhalb der angepasste, dazwischen anteilig. Haengt an
        ''' <paramref name="composite"/> (dem 8-Bit-Ergebnis derselben Mischung) danach das tiefe
        ''' Ergebnis; fehlt einem der beiden Eingaenge das tiefe Bild, bleibt es ohne.</summary>
        Private Shared Sub AttachSelectionScopedDeep(composite As SKBitmap, baseline As SKBitmap, adjusted As SKBitmap, mask As SKBitmap)
            Dim deepBaseline = DeepOf(baseline), deepAdjusted = DeepOf(adjusted)
            If composite Is Nothing OrElse deepBaseline Is Nothing OrElse deepAdjusted Is Nothing OrElse mask Is Nothing Then Return
            If mask.ColorType <> SKColorType.Alpha8 Then Return
            Dim w = composite.Width, h = composite.Height
            If deepBaseline.Width <> w OrElse deepBaseline.Height <> h OrElse deepAdjusted.Width <> w OrElse deepAdjusted.Height <> h Then Return
            Dim result = NewDeepBitmap(w, h, deepAdjusted.AlphaType)
            If result Is Nothing Then Return
            Dim baselinePixels = deepBaseline.GetPixels(), adjustedPixels = deepAdjusted.GetPixels()
            Dim maskPixels = mask.GetPixels(), resultPixels = result.GetPixels()
            Dim maskStride = mask.RowBytes
            Dim maskW = Math.Min(w, mask.Width), maskH = mask.Height
            Threading.Tasks.Parallel.For(0, h,
                Sub(y)
                    Dim adjustedRow(w * 4 - 1) As Short
                    Dim baselineRow(w * 4 - 1) As Short
                    Dim maskRow(Math.Max(1, w) - 1) As Byte
                    ReadDeepRow(deepAdjusted, adjustedPixels, y, adjustedRow)
                    ReadDeepRow(deepBaseline, baselinePixels, y, baselineRow)
                    ' Ausserhalb der Maske gilt 0, wie in der 8-Bit-Mischung.
                    Dim covered = If(y < maskH, maskW, 0)
                    If covered > 0 Then Marshal.Copy(IntPtr.Add(maskPixels, y * maskStride), maskRow, 0, covered)
                    For x = 0 To w - 1
                        Dim m = If(x < covered, CInt(maskRow(x)), 0)
                        If m >= 255 Then Continue For
                        Dim o = x * 4
                        For c = 0 To 3
                            If m = 0 Then
                                adjustedRow(o + c) = baselineRow(o + c)
                            Else
                                adjustedRow(o + c) = ToDeepShort(((adjustedRow(o + c) And &HFFFF) * m +
                                                                  (baselineRow(o + c) And &HFFFF) * (255 - m) + 127) \ 255)
                            End If
                        Next
                    Next
                    WriteDeepRow(result, resultPixels, y, adjustedRow)
                End Sub)
            AttachDeep(composite, result)
        End Sub

        ''' <summary>Ein leeres tiefes Bild in den genannten Massen, oder Nothing ohne Speicher.</summary>
        Friend Shared Function NewDeepBitmap(width As Integer, height As Integer, alphaType As SKAlphaType) As SKBitmap
            Dim deep = New SKBitmap(New SKImageInfo(width, height, DeepColorType, alphaType))
            If deep.GetPixels() = IntPtr.Zero Then
                deep.Dispose()
                Return Nothing
            End If
            Return deep
        End Function

        ''' <summary>Ein 16-Bit-Wert (0 bis 65535) als Short, wie Marshal.Copy ihn schreibt.</summary>
        Friend Shared Function ToDeepShort(value As Integer) As Short
            If value <= 0 Then Return 0S
            If value >= 65535 Then Return -1S
            Return CShort(If(value > 32767, value - 65536, value))
        End Function

        ''' <summary>Quantisierung auf 16 Bit, ohne Dither: die Stufe ist ein 257stel der 8-Bit-Stufe.</summary>
        Friend Shared Function QuantizeDeep(v As Single) As Short
            Dim scaled = v * 65535.0F + 0.5F
            If Not scaled > 0.0F Then Return 0S
            If scaled >= 65535.0F Then Return -1S
            Return ToDeepShort(CInt(Math.Floor(scaled)))
        End Function

        ''' <summary>Eine Zeile des tiefen Bilds in <paramref name="row"/> (vier Werte je Bildpunkt,
        ''' Rot, Gruen, Blau, Alpha; gelesen wird mit <c>And &amp;HFFFF</c>).</summary>
        Friend Shared Sub ReadDeepRow(deep As SKBitmap, pixels As IntPtr, y As Integer, row As Short())
            Marshal.Copy(IntPtr.Add(pixels, y * deep.RowBytes), row, 0, deep.Width * 4)
        End Sub

        Friend Shared Sub WriteDeepRow(deep As SKBitmap, pixels As IntPtr, y As Integer, row As Short())
            Marshal.Copy(row, 0, IntPtr.Add(pixels, y * deep.RowBytes), deep.Width * 4)
        End Sub

        ''' <summary>Hoechste Abweichung in 8-Bit-Stufen, bis zu der ein tiefes Bild als dasselbe Bild
        ''' wie sein 8-Bit-Bild gilt. Der Dither des 8-Bit-Bilds liegt unter einer Stufe; der Rest ist
        ''' Spielraum fuer Stufen, die beide Bilder getrennt neu abtasten.</summary>
        Private Const DeepMatchTolerance As Integer = 3

        ''' <summary>Anteil der verglichenen Bildpunkte, der ueber der Toleranz liegen darf.</summary>
        Private Const DeepMatchOutlierShare As Double = 0.001

        ''' <summary>Ist <paramref name="deep"/> dasselbe Bild wie <paramref name="shallow"/>, nur
        ''' feiner? Die letzte Sicherung vor dem Schreiben einer 16-Bit-Datei: hat eine Stufe das
        ''' 8-Bit-Bild veraendert, ohne das tiefe mitzurechnen, weichen beide dort ab, wo die Stufe
        ''' gewirkt hat. Verglichen wird jeder Bildpunkt eines Rasters, das hoechstens rund eine
        ''' Million Punkte liest.</summary>
        Friend Shared Function DeepMatchesShallow(shallow As SKBitmap, deep As SKBitmap,
                                                  Optional everyPixel As Boolean = False) As Boolean
            ' <everyPixel>: die strenge Fassung fuer das Arbeitsbild des Editors
            ' (AttachDeepWorkingSource). Jeder Bildpunkt wird gelesen, die Toleranz sind 2 Stufen
            ' (Dither unter einer, die Verzeichnung auf Karte und Prozessor hoechstens eine), und
            ' darueber darf nur ein Millionstel der Punkte liegen. Eine Retusche oder ein Strich
            ' aendert mehr als das; dann ist das tiefe Bild der RAW-Stufe nicht das des Arbeitsbilds.
            If shallow Is Nothing OrElse deep Is Nothing Then Return False
            If deep.ColorType <> DeepColorType OrElse deep.Width <> shallow.Width OrElse deep.Height <> shallow.Height Then Return False
            Dim ri, gi, bi As Integer
            Select Case shallow.ColorType
                Case SKColorType.Bgra8888
                    ri = 2 : gi = 1 : bi = 0
                Case SKColorType.Rgba8888
                    ri = 0 : gi = 1 : bi = 2
                Case Else
                    Return False
            End Select
            Dim width = shallow.Width, height = shallow.Height
            Dim stepSize = If(everyPixel, 1, Math.Max(1, CInt(Math.Ceiling(Math.Sqrt(CDbl(width) * height / 1000000.0)))))
            Dim tolerance = If(everyPixel, 2, DeepMatchTolerance)
            Dim allowedShare = If(everyPixel, 0.000001, DeepMatchOutlierShare)
            Dim shallowPixels = shallow.GetPixels()
            Dim deepPixels = deep.GetPixels()
            If shallowPixels = IntPtr.Zero OrElse deepPixels = IntPtr.Zero Then Return False
            Dim shallowRow(width * 4 - 1) As Byte
            Dim deepRow(width * 4 - 1) As Short
            Dim compared = 0L, outliers = 0L
            For y = 0 To height - 1 Step stepSize
                Marshal.Copy(IntPtr.Add(shallowPixels, y * shallow.RowBytes), shallowRow, 0, width * 4)
                ReadDeepRow(deep, deepPixels, y, deepRow)
                For x = 0 To width - 1 Step stepSize
                    Dim o = x * 4
                    Dim worst = 0
                    Dim dr = Math.Abs(CInt(shallowRow(o + ri)) * 257 - (deepRow(o) And &HFFFF))
                    Dim dg = Math.Abs(CInt(shallowRow(o + gi)) * 257 - (deepRow(o + 1) And &HFFFF))
                    Dim db = Math.Abs(CInt(shallowRow(o + bi)) * 257 - (deepRow(o + 2) And &HFFFF))
                    Dim da = Math.Abs(CInt(shallowRow(o + 3)) * 257 - (deepRow(o + 3) And &HFFFF))
                    worst = Math.Max(Math.Max(dr, dg), Math.Max(db, da))
                    compared += 1
                    If worst > tolerance * 257 Then outliers += 1
                Next
            Next
            If compared = 0 Then Return False
            Dim share = outliers / CDbl(compared)
            If share > allowedShare Then
                DiagnosticLogService.LogAlways("ImageProcessor.Deep", String.Format(Globalization.CultureInfo.InvariantCulture,
                    "tiefes Bild weicht vom 8-Bit-Bild ab: {0:P4} der Punkte ueber {1} Stufen", share, tolerance))
                Return False
            End If
            Return True
        End Function

    End Class

End Namespace
