Imports System
Imports System.Buffers
Imports System.Collections.Generic
Imports System.Linq
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
            ''' Waren 16 Bit verlangt und entstanden 8: die Stelle, an der das tiefe Bild endete
            ''' (<see cref="NoteDeepLost"/>), fuer Protokoll und Pruefstand. Sonst leer.
            Public Property DeepLostAt As String = ""
        End Class

        ''' <summary>Die erste Stelle, an der in diesem Aufruf ein tiefes Bild endete. Je Faden,
        ''' weil die Kette auf dem Faden ihres Aufrufers laeuft und mehrere Ausgaben nebeneinander
        ''' rechnen koennen.</summary>
        <ThreadStatic>
        Private Shared _deepLossNote As String

        ''' <summary>Vermerkt, wo ein tiefes Bild endete, und schreibt es ins Protokoll. Behalten
        ''' wird die erste Stelle: danach hat das Bild keinen Begleiter mehr, und was folgt, ist nur
        ''' die Folge davon.</summary>
        Friend Shared Sub NoteDeepLost(where As String)
            If _deepLossNote IsNot Nothing Then Return
            _deepLossNote = If(where, "")
            DiagnosticLogService.LogAlways("ImageProcessor.Deep", "das tiefe Bild endet hier: " & where)
        End Sub

        ''' <summary>Vor einer Ausgabe: den Vermerk leeren.</summary>
        Friend Shared Sub ResetDeepLossNote()
            _deepLossNote = Nothing
        End Sub

        ''' <summary>Der Vermerk dieser Ausgabe, oder leer.</summary>
        Friend Shared Function DeepLossNote() As String
            Return If(_deepLossNote, "")
        End Function

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
            ' Der Zaehler aendert sich unter derselben Sperre wie die Tabelle: so kann
            ' SweepDeepCompanions ihn aus der Tabelle neu setzen.
            SyncLock _deepCompanions
                _deepCompanions.Add(bitmap, deep)
                Interlocked.Increment(_deepCompanionCount)
            End SyncLock
        End Sub

        ''' <summary>Nach einer Ausgabe: gibt jedes tiefe Bild frei, dessen 8-Bit-Bild schon
        ''' freigegeben ist, und setzt den Zaehler auf das, was wirklich noch in der Tabelle steht.
        '''
        ''' Wird ein 8-Bit-Bild ohne <see cref="DropDeep"/> freigegeben (ein Ausstieg ueber eine
        ''' Ausnahme, eine Stelle, die den Begleiter nicht kennt), bliebe sein tiefes Bild mit
        ''' 8 Byte je Bildpunkt liegen, bis die Speicherbereinigung das 8-Bit-Bild findet, und die
        ''' weiss von diesem Speicher nichts. Und der Zaehler bliebe zu hoch, wenn sie den Eintrag
        ''' spaeter still entfernt. Ein Bild, das noch lebt (eine zweite Ausgabe auf einem anderen
        ''' Faden), bleibt unberuehrt.</summary>
        Friend Shared Sub SweepDeepCompanions()
            If Volatile.Read(_deepCompanionCount) <= 0 Then Return
            Dim orphaned As List(Of SKBitmap) = Nothing
            SyncLock _deepCompanions
                Dim alive = 0
                Dim stale As List(Of SKBitmap) = Nothing
                For Each pair In _deepCompanions
                    If pair.Key Is Nothing OrElse pair.Key.Handle = IntPtr.Zero Then
                        If stale Is Nothing Then stale = New List(Of SKBitmap)()
                        stale.Add(pair.Key)
                        If orphaned Is Nothing Then orphaned = New List(Of SKBitmap)()
                        If pair.Value IsNot Nothing Then orphaned.Add(pair.Value)
                    Else
                        alive += 1
                    End If
                Next
                If stale IsNot Nothing Then
                    For Each key In stale
                        If key IsNot Nothing Then _deepCompanions.Remove(key)
                    Next
                End If
                Interlocked.Exchange(_deepCompanionCount, alive)
            End SyncLock
            If orphaned Is Nothing Then Return
            For Each deep In orphaned
                deep.Dispose()
            Next
            DiagnosticLogService.LogAlways("ImageProcessor.Deep",
                $"{orphaned.Count} tiefe(s) Bild(er) ohne ihr 8-Bit-Bild freigegeben")
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
                Interlocked.Decrement(_deepCompanionCount)
            End SyncLock
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

        ''' <summary>Kann aus dieser Quelle eine Datei mit 16 Bit entstehen? Aus einer RAW-Datei, die
        ''' durch die RAW-Stufe laeuft (dort liegen die Tonwerte feiner vor als 8 Bit), und aus einer
        ''' Datei, die selbst mehr als 8 Bit traegt und sich tief lesen laesst (<see cref="IsDeepFileSource"/>,
        ''' heute TIFF, HEIF/AVIF und JPEG 2000). Eine 8-Bit-Quelle (JPEG, WebP, PNG) bekommt keine Ausgabe in 16 Bit, und ein
        ''' PNG oder JPEG XL mit 16 Bit wird beim Lesen noch auf 8 gebracht.
        '''
        ''' Die Frage fuer die Dialoge: danach richtet sich, ob sie die Farbtiefe anbieten. Ob die
        ''' Datei am Ende wirklich 16 Bit traegt, entscheidet erst SaveImage (ein Vorgang, der das
        ''' ganze Bild ersetzt, beendet das tiefe Bild, siehe <see cref="RecipeBlocksDeep"/>).</summary>
        Public Shared Function CanSourceGoDeep(sourcePath As String) As Boolean
            If String.IsNullOrWhiteSpace(sourcePath) Then Return False
            If IsDeepFileSource(sourcePath) Then Return True
            Return RawPreviewService.IsSupportedRaw(sourcePath) AndAlso RawDecodeService.DevelopsThroughRawStage(sourcePath)
        End Function

        ''' <summary>Eine Bilddatei, die selbst mehr als 8 Bit je Kanal traegt und sich tief lesen
        ''' laesst: ein TIFF mit 16 Bit (TiffPreviewService.IsDeepTiff) oder ein HEIF/AVIF mit 10
        ''' oder 12 Bit ohne Alpha (HeifDecodeService.IsDeepHeif) oder ein JPEG 2000 mit mehr als
        ''' 8 Bit (Jpeg2000DecodeService.IsDeepJpeg2000). Die zweite Herkunft eines tiefen Bilds
        ''' neben der RAW-Stufe. Der Unterschied: hier gibt es keine Entwicklung mit Angaben aus dem
        ''' Rezept, das tiefe Bild ist die Datei.</summary>
        Friend Shared Function IsDeepFileSource(path As String) As Boolean
            If HeifDecodeService.IsSupportedHeif(path) Then Return HeifDecodeService.IsDeepHeif(path)
            If Jpeg2000DecodeService.IsSupportedJpeg2000(path) Then Return Jpeg2000DecodeService.IsDeepJpeg2000(path)
            Return TiffPreviewService.IsDeepTiff(path)
        End Function

        ''' <summary>Das tiefe Bild einer solchen Datei, oder Nothing.</summary>
        Private Shared Function DecodeDeepFileSource(path As String) As SKBitmap
            If HeifDecodeService.IsSupportedHeif(path) Then Return HeifDecodeService.TryDecodeDeep(path)
            If Jpeg2000DecodeService.IsSupportedJpeg2000(path) Then Return Jpeg2000DecodeService.TryDecodeDeep(path)
            Return TiffPreviewService.TryDecodeDeep(path)
        End Function

        ''' <summary>Traegt dieses Rezept etwas, womit die Kette das tiefe Bild sicher nicht bis ans
        ''' Ende bringt? Dann entstuende trotz gewaehlter 16 Bit eine Datei mit 8 Bit, und der Dialog
        ''' bietet die Farbtiefe fuer dieses Bild gar nicht erst an.
        '''
        ''' Die Liste folgt den Stellen, an denen das tiefe Bild heute endet: Vorgaenge, die das
        ''' ganze Bild als 8-Bit-Bildpunkte ersetzen (Entrauschen am fertigen Bild, Hochskalieren
        ''' mit Modell). Retusche, Striche und Objekt entfernen gehoeren nicht dazu: sie aendern
        ''' einzelne Stellen, und nur diese tragen dann die Feinheit von 8 Bit
        ''' (<see cref="PatchDeepFromShallow"/>). Wer eine dieser Stellen tief rechnen laesst, nimmt
        ''' sie hier heraus.
        '''
        ''' Eine Vorabfrage, keine Zusage: Nein heisst nur, dass das Rezept nichts dagegen hat. Ob
        ''' die Datei wirklich 16 Bit traegt, entscheidet SaveImage und meldet es in
        ''' <see cref="SaveOutcome"/>.</summary>
        Public Shared Function RecipeBlocksDeep(adj As ImageAdjustments) As Boolean
            If adj Is Nothing Then Return False
            If HasWholeImageBakedOperation(adj) Then Return True
            Return Not String.IsNullOrWhiteSpace(adj.UpscaleModel)
        End Function

        ''' <summary>Traegt das Rezept einen gebackenen Vorgang, der das GANZE Bild anfasst
        ''' (Entrauschen am fertigen Bild, Hochskalieren)? Dann gibt es kein tiefes Bild dazu. Ein
        ''' Vorgang an einer Stelle (Objekt entfernen, wie Retusche und Striche) laesst das Bild
        ''' daneben unberuehrt; dort bleibt es tief (<see cref="PatchDeepFromShallow"/>).</summary>
        Friend Shared Function HasWholeImageBakedOperation(adj As ImageAdjustments) As Boolean
            If adj Is Nothing OrElse adj.BakedOperations Is Nothing Then Return False
            For Each op In adj.BakedOperations
                If op Is Nothing Then Continue For
                If Not String.Equals(op.Kind, BakedOperation.KindObjectRemoval, StringComparison.OrdinalIgnoreCase) Then Return True
            Next
            Return False
        End Function

        ''' <summary>Haengt an ein frisch entwickeltes RAW-Bild sein tiefes Bild: derselbe Decode mit
        ''' denselben Angaben wie in <see cref="DecodeForRender"/>, aus den linearen Daten, die nach
        ''' dem 8-Bit-Decode bereitliegen. Nichts geschieht, wenn die Datei nicht durch die RAW-Stufe
        ''' laeuft; dann hat die Kette kein tiefes Bild, und die Ausgabe bleibt bei 8 Bit.</summary>
        Friend Shared Sub AttachDeepRawSource(decoded As SKBitmap, path As String, adj As ImageAdjustments,
                                              developRaw As Boolean)
            If decoded Is Nothing Then Return
            Dim deep As SKBitmap
            If IsDeepFileSource(path) Then
                ' Eine Datei, die selbst 16 Bit traegt: ihr tiefes Bild ist die Datei.
                deep = DecodeDeepFileSource(path)
            Else
                If Not developRaw OrElse Not RawPreviewService.IsSupportedRaw(path) Then Return
                If Not RawDecodeService.DevelopsThroughRawStage(path) Then Return
                deep = RawDecodeService.TryDecodeDeep(path, LensChoiceFrom(adj),
                                                      adj IsNot Nothing AndAlso adj.RawHighlightRecovery,
                                                      If(adj Is Nothing, RawDecodeService.HighlightRecoveryKnee, adj.RawHighlightKnee()),
                                                      If(adj Is Nothing, 0.0, adj.RawExposureEv()))
            End If
            ' Passt es nicht zum 8-Bit-Bild (andere Masse, anderer Inhalt), ist es nicht sein
            ' tiefes Bild: etwa wenn statt der Entwicklung die eingebettete Vorschau einsprang.
            If deep Is Nothing Then
                NoteDeepLost("die Quelle lieferte kein tiefes Bild (RAW-Stufe oder 16-Bit-Datei)")
            ElseIf deep.Width <> decoded.Width OrElse deep.Height <> decoded.Height Then
                NoteDeepLost($"das tiefe Bild der Quelle hat {deep.Width}x{deep.Height}, das 8-Bit-Bild {decoded.Width}x{decoded.Height}")
                deep.Dispose()
                deep = Nothing
            ElseIf Not DeepMatchesShallow(decoded, deep) Then
                NoteDeepLost("das tiefe Bild der RAW-Stufe ist nicht das 8-Bit-Bild der RAW-Stufe")
                deep.Dispose()
                deep = Nothing
            End If
            AttachDeep(decoded, deep)
        End Sub

        ''' <summary>Haengt an das ARBEITSBILD des Editors das tiefe Bild der RAW-Stufe: die Quelle
        ''' ist eine RAW, und im Arbeitsbild steckt ihre Entwicklung, mit der Belichtung und dem
        ''' Knie, die das Rezept als "im Quellbild" vermerkt (ExposureInSource, KneeInSource).
        '''
        ''' Was im Arbeitsbild darueber hinaus steckt (Retusche, Malerei, Objekt entfernen), sagt
        ''' kein Schalter, sondern der Vergleich Bildpunkt fuer Bildpunkt: die Stellen, an denen es
        ''' von der Entwicklung abweicht, kommen aus dem Arbeitsbild
        ''' (<see cref="PatchDeepFromShallow"/>). Kaeme so mehr als die Haelfte von dort (ein Arbeitsbild,
        ''' das mit anderen Angaben entstand) oder hat ein Vorgang das ganze Bild ersetzt, bleibt
        ''' das Arbeitsbild ohne tiefes Bild, und die Ausgabe bei 8 Bit.</summary>
        Friend Shared Sub AttachDeepWorkingSource(working As SKBitmap, path As String, adj As ImageAdjustments,
                                                  developRaw As Boolean)
            If working IsNot Nothing AndAlso adj IsNot Nothing AndAlso IsDeepFileSource(path) Then
                ' Das Arbeitsbild einer 16-Bit-Datei: die Datei, tief bis auf die Stellen, an denen
                ' es von ihr abweicht.
                AttachPatchedDeep(working, DecodeDeepFileSource(path), adj)
                Return
            End If
            If working Is Nothing OrElse adj Is Nothing OrElse Not developRaw OrElse Not RawPreviewService.IsSupportedRaw(path) Then Return
            If Not RawDecodeService.DevelopsThroughRawStage(path) Then Return
            Dim deep = RawDecodeService.TryDecodeDeep(path, LensChoiceFrom(adj), adj.RawHighlightRecovery,
                                                      adj.KneeInSource,
                                                      adj.ExposureInSource / ImageAdjustments.ExposurePointsPerStop)
            AttachPatchedDeep(working, deep, adj)
        End Sub

        ''' <summary>Dasselbe fuer ein Bild, das SaveImage selbst entwickelt und auf das es danach
        ''' die vermerkten Vorgaenge nachgezogen hat (Retusche, Striche, Objekt entfernen): das
        ''' tiefe Bild der RAW-Stufe mit den Angaben des Decodes, an den veraenderten Stellen
        ''' ersetzt durch das bearbeitete Bild.</summary>
        Friend Shared Sub AttachDeepEditedRawSource(edited As SKBitmap, path As String, adj As ImageAdjustments,
                                                    developRaw As Boolean)
            If edited IsNot Nothing AndAlso IsDeepFileSource(path) Then
                AttachPatchedDeep(edited, DecodeDeepFileSource(path), adj)
                Return
            End If
            If edited Is Nothing OrElse Not developRaw OrElse Not RawPreviewService.IsSupportedRaw(path) Then Return
            If Not RawDecodeService.DevelopsThroughRawStage(path) Then Return
            Dim deep = RawDecodeService.TryDecodeDeep(path, LensChoiceFrom(adj),
                                                      adj IsNot Nothing AndAlso adj.RawHighlightRecovery,
                                                      If(adj Is Nothing, RawDecodeService.HighlightRecoveryKnee, adj.RawHighlightKnee()),
                                                      If(adj Is Nothing, 0.0, adj.RawExposureEv()))
            AttachPatchedDeep(edited, deep, adj)
        End Sub

        ''' <summary>Haengt <paramref name="deep"/> (die Entwicklung der RAW-Stufe) an
        ''' <paramref name="edited"/>, nachdem die bearbeiteten Stellen aus dem 8-Bit-Bild
        ''' uebernommen sind. Uebernimmt den Besitz von <paramref name="deep"/>; ohne Erfolg wird
        ''' es freigegeben, und das Bild bleibt bei 8 Bit.</summary>
        Private Shared Sub AttachPatchedDeep(edited As SKBitmap, deep As SKBitmap, adj As ImageAdjustments)
            If deep Is Nothing Then Return
            Dim reason As String = Nothing
            If deep.Width <> edited.Width OrElse deep.Height <> edited.Height Then
                reason = "andere Masse als die Entwicklung der RAW-Stufe"
            ElseIf HasWholeImageBakedOperation(adj) Then
                reason = "ein Vorgang hat das ganze Bild ersetzt (Entrauschen am fertigen Bild, Hochskalieren)"
            Else
                ' Die Mischung legt Felder ueber das ganze Bild an. Reicht der Speicher nicht, bleibt
                ' es bei 8 Bit; die Datei entsteht trotzdem.
                Try
                    If Not PatchDeepFromShallow(edited, deep) Then
                        reason = "mehr als die Haelfte des Bilds weicht von der Entwicklung der RAW-Stufe ab"
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("ImageProcessor.Deep", ex)
                    reason = "die Mischung mit dem 8-Bit-Bild scheiterte (" & ex.GetType().Name & ")"
                End Try
            End If
            If reason IsNot Nothing Then
                NoteDeepLost("das bearbeitete Bild: " & reason)
                DiagnosticLogService.LogAlways("ImageProcessor.Deep", "das bearbeitete Bild bleibt bei 8 Bit: " & reason)
                deep.Dispose()
                Return
            End If
            AttachDeep(edited, deep)
        End Sub

        ''' <summary>Anteil der Bildpunkte, der hoechstens aus dem 8-Bit-Bild kommen darf. Darueber
        ''' waere die Datei ueberwiegend 8-Bit-Inhalt in 16 Bit.</summary>
        Private Const DeepPatchShareLimit As Double = 0.5

        ''' <summary>Um so viele Bildpunkte waechst jede bearbeitete Stelle, bevor sie uebernommen
        ''' wird: der weiche Rand eines Tupfers aendert das Bild um weniger als die Toleranz und
        ''' fiele sonst durch den Vergleich.</summary>
        Private Const DeepPatchGrow As Integer = 6

        ''' <summary>Kante des Felds, ueber das die Abweichung gemittelt wird, um eine feine
        ''' Bearbeitung zu finden, und der Versatz von Feld zu Feld (halbe Kante).</summary>
        Private Const DeepPatchBlock As Integer = 8
        Private Const DeepPatchBlockStep As Integer = 4

        ''' <summary>Ab diesem Mittel der Abweichung in 8-Bit-Stufen gilt ein Feld als bearbeitet.
        ''' Ohne Bearbeitung bleibt das Mittel unter einem Viertel einer Stufe.</summary>
        Private Const DeepPatchBlockMean As Double = 0.5

        ''' <summary>Die Mischloesung fuer Retusche, Malerei und Objekt entfernen: diese Vorgaenge
        ''' stecken als 8-Bit-Bildpunkte in <paramref name="shallow"/>, das tiefe Bild der RAW-Stufe
        ''' kennt sie nicht. Wo beide um mehr als zwei Stufen auseinanderliegen (Dither unter einer,
        ''' die Verzeichnung auf Karte und Prozessor hoechstens eine) oder im Mittel ueber ein Feld
        ''' von 8 x 8 um mehr als eine halbe (die feine Bearbeitung), ist das Bild bearbeitet; dort
        ''' und in einem schmalen Saum darum bekommt <paramref name="deep"/> die Werte des
        ''' 8-Bit-Bilds. Ueberall sonst bleibt es so fein wie die RAW-Stufe.
        '''
        ''' Falsch, wenn mehr als <see cref="DeepPatchShareLimit"/> der Flaeche aus dem 8-Bit-Bild
        ''' kaeme, gemessen MIT dem Saum; <paramref name="deep"/> ist dann unberuehrt.</summary>
        Friend Shared Function PatchDeepFromShallow(shallow As SKBitmap, deep As SKBitmap) As Boolean
            If shallow Is Nothing OrElse deep Is Nothing Then Return False
            If deep.ColorType <> DeepColorType OrElse deep.Width <> shallow.Width OrElse deep.Height <> shallow.Height Then Return False
            Dim ri, bi As Integer
            Select Case shallow.ColorType
                Case SKColorType.Bgra8888
                    ri = 2 : bi = 0
                Case SKColorType.Rgba8888
                    ri = 0 : bi = 2
                Case Else
                    Return False
            End Select
            Dim w = shallow.Width, h = shallow.Height
            Dim shallowPixels = shallow.GetPixels(), deepPixels = deep.GetPixels()
            If shallowPixels = IntPtr.Zero OrElse deepPixels = IntPtr.Zero OrElse w <= 0 OrElse h <= 0 Then Return False
            Dim shallowStride = shallow.RowBytes
            Const limit As Integer = 2 * 257
            Dim changed(w * h - 1) As Byte
            Dim counts(h - 1) As Integer
            Threading.Tasks.Parallel.For(0, h,
                Sub(y)
                    Dim shallowRow(w * 4 - 1) As Byte
                    Dim deepRow(w * 4 - 1) As Short
                    Marshal.Copy(IntPtr.Add(shallowPixels, y * shallowStride), shallowRow, 0, w * 4)
                    ReadDeepRow(deep, deepPixels, y, deepRow)
                    For x = 0 To w - 1
                        Dim o = x * 4
                        If Math.Abs(CInt(shallowRow(o + ri)) * 257 - (deepRow(o) And &HFFFF)) > limit OrElse
                           Math.Abs(CInt(shallowRow(o + 1)) * 257 - (deepRow(o + 1) And &HFFFF)) > limit OrElse
                           Math.Abs(CInt(shallowRow(o + bi)) * 257 - (deepRow(o + 2) And &HFFFF)) > limit OrElse
                           Math.Abs(CInt(shallowRow(o + 3)) * 257 - (deepRow(o + 3) And &HFFFF)) > limit Then
                            changed(y * w + x) = 1
                        End If
                    Next
                End Sub)

            ' DIE FEINE BEARBEITUNG. Ein schwacher Pinselzug aendert das Bild flaechig um ein oder
            ' zwei Stufen und bleibt damit je Bildpunkt unter der Schwelle oben: die liegt bei zwei
            ' Stufen, weil Dither und Verzeichnung einen einzelnen Punkt so weit streuen. Im
            ' Mittel ueber ein Feld von 8 x 8 heben sich Dither und Verzeichnung aber auf (gemessen
            ' hoechstens ein Viertel einer Stufe), eine Bearbeitung nicht. Liegt das Mittel eines
            ' Felds in einem Kanal um mehr als eine halbe Stufe daneben, gilt das ganze Feld als
            ' bearbeitet. Die Felder ueberlappen sich zur Haelfte, damit ein Zug, der zwischen zwei
            ' Feldern liegt, eines davon ueberwiegend trifft. Was darunter bleibt (eine Linie von
            ' ein oder zwei Bildpunkten Breite mit einer Stufe Unterschied), laesst sich vom
            ' Dither nicht trennen.
            If w >= DeepPatchBlock AndAlso h >= DeepPatchBlock Then
                Dim blockRows = (h - DeepPatchBlock) \ DeepPatchBlockStep + 1
                Dim blockLimit = CInt(DeepPatchBlockMean * 257 * DeepPatchBlock * DeepPatchBlock)
                Threading.Tasks.Parallel.For(0, blockRows,
                    Sub(blockRow)
                        Dim top = blockRow * DeepPatchBlockStep
                        ' Je Spalte die Summe der Abweichung ueber die Zeilen des Felds, je Kanal.
                        Dim sums(w * 3 - 1) As Integer
                        Dim shallowRow(w * 4 - 1) As Byte
                        Dim deepRow(w * 4 - 1) As Short
                        For y = top To top + DeepPatchBlock - 1
                            Marshal.Copy(IntPtr.Add(shallowPixels, y * shallowStride), shallowRow, 0, w * 4)
                            ReadDeepRow(deep, deepPixels, y, deepRow)
                            For x = 0 To w - 1
                                Dim o = x * 4
                                Dim s = x * 3
                                sums(s) += CInt(shallowRow(o + ri)) * 257 - (deepRow(o) And &HFFFF)
                                sums(s + 1) += CInt(shallowRow(o + 1)) * 257 - (deepRow(o + 1) And &HFFFF)
                                sums(s + 2) += CInt(shallowRow(o + bi)) * 257 - (deepRow(o + 2) And &HFFFF)
                            Next
                        Next
                        Dim left = 0
                        While left + DeepPatchBlock <= w
                            Dim sumR = 0, sumG = 0, sumB = 0
                            For x = left To left + DeepPatchBlock - 1
                                sumR += sums(x * 3)
                                sumG += sums(x * 3 + 1)
                                sumB += sums(x * 3 + 2)
                            Next
                            If Math.Abs(sumR) > blockLimit OrElse Math.Abs(sumG) > blockLimit OrElse Math.Abs(sumB) > blockLimit Then
                                ' Nachbarfelder schreiben dieselbe Eins an dieselbe Stelle; gezaehlt wird danach.
                                For y = top To top + DeepPatchBlock - 1
                                    Array.Fill(changed, CByte(1), y * w + left, DeepPatchBlock)
                                Next
                            End If
                            left += DeepPatchBlockStep
                        End While
                    End Sub)
            End If

            Threading.Tasks.Parallel.For(0, h,
                Sub(y)
                    Dim rowStart = y * w
                    Dim count = 0
                    For x = 0 To w - 1
                        count += changed(rowStart + x)
                    Next
                    counts(y) = count
                End Sub)
            Dim total = 0L
            For Each count In counts
                total += count
            Next
            If total = 0 Then Return True
            Dim share = total / (CDbl(w) * h)
            If share > DeepPatchShareLimit Then Return False

            ' Der Saum: erst waagerecht, dann senkrecht wachsen lassen. Das Ergebnis steht danach
            ' wieder in changed und wird je Zeile gezaehlt. Erst diese Zahl sagt, wie viel der
            ' Flaeche aus dem 8-Bit-Bild kaeme: verstreute Stellen wachsen auf ein Vielfaches,
            ' ein Prozent einzelner Bildpunkte auf weit ueber die Haelfte des Bilds.
            Dim grownAcross(w * h - 1) As Byte
            Threading.Tasks.Parallel.For(0, h,
                Sub(y)
                    If counts(y) = 0 Then Return
                    Dim rowStart = y * w
                    Dim reach = -1
                    For x = 0 To w - 1
                        If changed(rowStart + x) <> 0 Then
                            For back = Math.Max(0, x - DeepPatchGrow) To x
                                grownAcross(rowStart + back) = 1
                            Next
                            reach = Math.Min(w - 1, x + DeepPatchGrow)
                        ElseIf x <= reach Then
                            grownAcross(rowStart + x) = 1
                        End If
                    Next
                End Sub)
            Dim grownCounts(h - 1) As Integer
            Threading.Tasks.Parallel.For(0, h,
                Sub(y)
                    Dim yFrom = Math.Max(0, y - DeepPatchGrow), yTo = Math.Min(h - 1, y + DeepPatchGrow)
                    Dim any = False
                    For yy = yFrom To yTo
                        If counts(yy) <> 0 Then
                            any = True
                            Exit For
                        End If
                    Next
                    ' Ohne bearbeitete Stelle in Reichweite ist die Zeile in changed schon leer.
                    If Not any Then Return
                    Dim rowStart = y * w
                    Dim count = 0
                    For x = 0 To w - 1
                        Dim take As Byte = 0
                        For yy = yFrom To yTo
                            If grownAcross(yy * w + x) <> 0 Then
                                take = 1
                                Exit For
                            End If
                        Next
                        changed(rowStart + x) = take
                        count += take
                    Next
                    grownCounts(y) = count
                End Sub)
            Dim grownTotal = 0L
            For Each count In grownCounts
                grownTotal += count
            Next
            Dim grownShare = grownTotal / (CDbl(w) * h)
            If grownShare > DeepPatchShareLimit Then
                DiagnosticLogService.LogAlways("ImageProcessor.Deep", String.Format(Globalization.CultureInfo.InvariantCulture,
                    "bearbeitete Stellen: {0:P3} der Flaeche, mit Saum {1:P3}, das ist mehr als die Haelfte", share, grownShare))
                Return False
            End If

            ' Erst jetzt wird das tiefe Bild angefasst.
            Threading.Tasks.Parallel.For(0, h,
                Sub(y)
                    If grownCounts(y) = 0 Then Return
                    Dim rowStart = y * w
                    Dim shallowRow(w * 4 - 1) As Byte
                    Dim deepRow(w * 4 - 1) As Short
                    Marshal.Copy(IntPtr.Add(shallowPixels, y * shallowStride), shallowRow, 0, w * 4)
                    ReadDeepRow(deep, deepPixels, y, deepRow)
                    For x = 0 To w - 1
                        If changed(rowStart + x) = 0 Then Continue For
                        Dim o = x * 4
                        deepRow(o) = ToDeepShort(CInt(shallowRow(o + ri)) * 257)
                        deepRow(o + 1) = ToDeepShort(CInt(shallowRow(o + 1)) * 257)
                        deepRow(o + 2) = ToDeepShort(CInt(shallowRow(o + bi)) * 257)
                        deepRow(o + 3) = ToDeepShort(CInt(shallowRow(o + 3)) * 257)
                    Next
                    WriteDeepRow(deep, deepPixels, y, deepRow)
                End Sub)
            DiagnosticLogService.LogAlways("ImageProcessor.Deep", String.Format(Globalization.CultureInfo.InvariantCulture,
                "bearbeitete Stellen aus dem 8-Bit-Bild uebernommen: {0:P3} der Flaeche, mit Saum {1:P3}", share, grownShare))
            Return True
        End Function

        ''' <summary>Der PSD-Export mit verlangten 16 Bit: entwickelt die Quelle selbst (oder nimmt
        ''' das hereingereichte Arbeitsbild), haengt das tiefe Bild an und rechnet die Kette zweimal,
        ''' mit den Objekten fuer das Gesamtbild und ohne sie fuer die unterste Ebene. Kommt das
        ''' tiefe Bild bei beiden bis ans Ende und ist es dasselbe Bild wie das 8-Bit-Ergebnis,
        ''' entsteht die Datei mit 16 Bit, sonst dieselbe Datei mit 8 Bit;
        ''' <paramref name="outcome"/> sagt, was geschrieben wurde.
        '''
        ''' <paramref name="workingFull"/> geht in den Besitz dieses Aufrufs ueber, wie bei SaveImage.</summary>
        Public Shared Function ExportLayeredPsdDeep(targetPath As String, sourcePath As String, adj As ImageAdjustments,
                                                    Optional workingFull As SKBitmap = Nothing,
                                                    Optional preserveMetadata As Boolean = False,
                                                    Optional developRaw As Boolean = True,
                                                    Optional outcome As SaveOutcome = Nothing) As Boolean
            If outcome IsNot Nothing Then outcome.WrittenBitDepth = 8
            ResetDeepLossNote()
            If String.IsNullOrWhiteSpace(targetPath) OrElse adj Is Nothing Then
                workingFull?.Dispose()
                Return False
            End If
            Dim renderAdj = adj
            ' Wie in SaveImage: der Decode nimmt das tiefe Bild gleich mit (BeginDeepCapture).
            Using RawDecodeService.BeginDeepCapture(workingFull Is Nothing)
                Dim source = If(workingFull, DecodeForRender(sourcePath, adj, renderAdj, developRaw))
                If source Is Nothing Then Return False
                Try
                    Return ExportLayeredPsdDeepCore(targetPath, sourcePath, adj, renderAdj, source, workingFull IsNot Nothing,
                                                    preserveMetadata, developRaw, outcome)
                Finally
                    SweepDeepCompanions()
                End Try
            End Using
        End Function

        Private Shared Function ExportLayeredPsdDeepCore(targetPath As String, sourcePath As String, adj As ImageAdjustments,
                                                         renderAdj As ImageAdjustments, source As SKBitmap,
                                                         isWorkingImage As Boolean, preserveMetadata As Boolean,
                                                         developRaw As Boolean, outcome As SaveOutcome) As Boolean
            Using sourceBitmap = source, sourceDeep = DeepGuard(source)
                If Not isWorkingImage Then
                    AttachDeepRawSource(source, sourcePath, adj, developRaw)
                ElseIf Not FpxService.IsFpx(sourcePath) Then
                    AttachDeepWorkingSource(source, sourcePath, adj, developRaw)
                End If
                Dim withoutObjects = renderAdj.Clone()
                withoutObjects.Annotations?.Clear()
                Dim composite = ProcessBitmap(source, renderAdj)
                If composite Is Nothing Then Return False
                Dim background As SKBitmap = Nothing
                Try
                    background = ProcessBitmap(source, withoutObjects)
                    Dim deepComposite = If(ReferenceEquals(composite, source), Nothing, DeepOf(composite))
                    Dim deepBackground = If(background Is Nothing OrElse ReferenceEquals(background, source), Nothing, DeepOf(background))
                    ' Ohne Regler gibt die Kette die Quelle selbst zurueck; ihr tiefes Bild gilt dann.
                    If ReferenceEquals(composite, source) Then deepComposite = DeepOf(source)
                    If background IsNot Nothing AndAlso ReferenceEquals(background, source) Then deepBackground = DeepOf(source)
                    Dim deepOk = deepComposite IsNot Nothing AndAlso deepBackground IsNot Nothing AndAlso
                                 DeepMatchesShallow(composite, deepComposite) AndAlso DeepMatchesShallow(background, deepBackground)
                    If Not deepOk Then
                        NoteDeepLost("Gesamtbild oder unterste Ebene der PSD kam ohne tiefes Bild an")
                        If outcome IsNot Nothing Then outcome.DeepLostAt = DeepLossNote()
                        DiagnosticLogService.LogAlways("ImageProcessor.Save",
                            $"PSD mit 16 Bit verlangt, die Kette hat kein tiefes Bild geliefert, gespeichert mit 8 Bit target={IO.Path.GetFileName(targetPath)}")
                    End If
                    Dim ok = ExportLayeredPsd(targetPath, composite, background, adj, sourcePath, preserveMetadata,
                                              If(deepOk, deepComposite, Nothing), If(deepOk, deepBackground, Nothing))
                    If ok AndAlso deepOk AndAlso outcome IsNot Nothing Then outcome.WrittenBitDepth = 16
                    Return ok
                Finally
                    If background IsNot Nothing AndAlso Not ReferenceEquals(background, source) Then
                        DropDeep(background)
                        background.Dispose()
                    End If
                    If Not ReferenceEquals(composite, source) Then
                        DropDeep(composite)
                        composite.Dispose()
                    End If
                End Try
            End Using
        End Function

        ''' <summary>Fuehrt eine Stufe aus, die mit dem Farbtyp ihrer Quelle rechnet (sie zeichnet
        ''' ueber Skia in ein Bild derselben Art), und rechnet das tiefe Bild mit DERSELBEN Stufe
        ''' mit: ein zweiter Aufruf, diesmal mit dem tiefen Bild als Quelle. Liefert das Ergebnis
        ''' der Stufe fuer das 8-Bit-Bild, mit seinem tiefen Bild als Begleiter.
        '''
        ''' Kommt aus dem zweiten Aufruf kein tiefes Bild in denselben Massen (die Stufe legt ihr
        ''' Ergebnis doch mit festem Farbtyp an, oder sie scheitert), hat das Ergebnis keinen
        ''' Begleiter, und die Ausgabe bleibt bei 8 Bit.
        '''
        ''' NEBENEINANDER. Beide Aufrufe sind voneinander unabhaengig: jeder liest nur seine Quelle
        ''' (und was die Stufe sonst mitbringt, etwa eine Maske, nur lesend) und legt sein Ergebnis
        ''' neu an. Die Stufen zeichnen ueber Skia auf dem Prozessor, und das zeichnet auf einem Faden;
        ''' nacheinander stand ein Kern fuer das tiefe Bild ein zweites Mal so lange. Die Stufe fuer
        ''' das 8-Bit-Bild laeuft auf dem Faden des Aufrufers (dort stehen seine Angaben je Faden, etwa
        ''' der Vermerk, wo ein tiefes Bild endete), die fuer das tiefe Bild daneben. Ohne tiefes Bild
        ''' an der Quelle, also ausserhalb einer Ausgabe in 16 Bit, aendert sich nichts.</summary>
        Private Shared Function RunStepWithDeep(source As SKBitmap, stage As Func(Of SKBitmap, SKBitmap)) As SKBitmap
            If Not DeepStepsConcurrent Then Return RunStepWithDeepInSequence(source, stage)
            Dim deep = DeepOf(source)
            If deep Is Nothing Then Return stage(source)
            Dim deepTask = Threading.Tasks.Task.Run(Function() RunDeepStep(deep, stage))
            Dim result As SKBitmap
            Try
                result = stage(source)
            Catch
                ' Das tiefe Ergebnis wird nicht mehr gebraucht; erst abwarten, dann freigeben.
                ReleaseDeepStepResult(deepTask.Result, deep)
                Throw
            End Try
            Dim deepResult = deepTask.Result
            If result Is Nothing OrElse ReferenceEquals(result, source) Then
                ReleaseDeepStepResult(deepResult, deep)
                Return result
            End If
            If deepResult Is Nothing OrElse ReferenceEquals(deepResult, deep) Then Return result
            AttachDeep(result, deepResult)
            Return result
        End Function

        ''' <summary>MESSSCHALTER, ab Werk an: die beiden Aufrufe von <see cref="RunStepWithDeep"/>
        ''' nebeneinander. Aus laufen sie nacheinander wie zuvor; der Pruefstand vergleicht beide
        ''' Wege Byte fuer Byte (gesetzt per Reflexion).</summary>
        Friend Shared DeepStepsConcurrent As Boolean = True

        ''' <summary>Der Weg nacheinander: erst die Stufe fuer das 8-Bit-Bild, nur wenn sie wirkt
        ''' die fuer das tiefe.</summary>
        Private Shared Function RunStepWithDeepInSequence(source As SKBitmap, stage As Func(Of SKBitmap, SKBitmap)) As SKBitmap
            Dim result = stage(source)
            If result Is Nothing OrElse ReferenceEquals(result, source) Then Return result
            Dim deep = DeepOf(source)
            If deep Is Nothing Then Return result
            Dim deepResult = RunDeepStep(deep, stage)
            If deepResult Is Nothing OrElse ReferenceEquals(deepResult, deep) Then Return result
            AttachDeep(result, deepResult)
            Return result
        End Function

        ''' <summary>Die Stufe auf dem tiefen Bild. Scheitert sie, endet nur das tiefe Bild:
        ''' protokolliert, und das Ergebnis ist Nothing.</summary>
        Private Shared Function RunDeepStep(deep As SKBitmap, stage As Func(Of SKBitmap, SKBitmap)) As SKBitmap
            Try
                Return stage(deep)
            Catch ex As Exception
                DiagnosticLogService.LogException("ImageProcessor.Deep", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Gibt ein tiefes Ergebnis frei, das nicht angehaengt wird, ausser es ist die
        ''' Quelle selbst.</summary>
        Private Shared Sub ReleaseDeepStepResult(deepResult As SKBitmap, deep As SKBitmap)
            If deepResult IsNot Nothing AndAlso Not ReferenceEquals(deepResult, deep) Then deepResult.Dispose()
        End Sub

        ' ── Objekte der Buehne (ApplyAnnotationsCore) ────────────────────────────
        '
        ' Das Komposit der Objekte ist ein eigenes 8-Bit-Bild, auf das der Reihe nach gezeichnet
        ' wird. Mit tiefem Bild an der Quelle bekommt es einen Begleiter in derselben Form, und
        ' jeder Zeichenschritt laeuft danach auch auf ihm: das Zeichnen selbst (Formen, Text,
        ' Bilder, Effekte) rechnet Skia, hier auf einer Flaeche mit 16 Bit. Was ein Objekt an Farbe
        ' mitbringt, ist dabei so fein wie seine Quelle; das Foto darunter bleibt tief.
        ' Korrekturebenen im Objektstapel rechnen ueber ApplyMaskedAdjustmentLayers auf dem Paar.
        ' Die Gruppe als eigener Schritt wird als fertige 8-Bit-Ebene eingemischt, in das Komposit
        ' und mit derselben Mischung in sein tiefes Bild (CompositeGroupLayer): ihr Inhalt ist so
        ' fein wie 8 Bit, das Foto darunter bleibt tief.

        ''' <summary>Der Begleiter fuer das Komposit der Objekte: vormultipliziert wie
        ''' CloneBitmapForAnnotationComposite, mit dem tiefen Bild als Grund oder, bei ausgeblendetem
        ''' Hintergrund, mit der Hintergrundfarbe des Dokuments. Nothing ohne tiefes Bild.</summary>
        Private Shared Function DeepForAnnotationComposite(deepSource As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If deepSource Is Nothing OrElse adj Is Nothing Then Return Nothing
            Dim result = NewDeepBitmap(deepSource.Width, deepSource.Height, SKAlphaType.Premul)
            If result Is Nothing Then Return Nothing
            Using canvas = New SKCanvas(result)
                If adj.BackgroundHidden Then
                    canvas.Clear(DocumentBackgroundColor(adj))
                Else
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(deepSource, 0, 0, paint)
                    End Using
                End If
            End Using
            Return result
        End Function

        ''' <summary>Zeichnet dieselben Objekte, die eben auf <paramref name="composite"/> gezeichnet
        ''' wurden, auf dessen tiefes Bild. Scheitert das, endet das tiefe Bild hier.</summary>
        Private Shared Sub DrawAnnotationsOnDeep(composite As SKBitmap, adj As ImageAdjustments, width As Integer, height As Integer,
                                                 annotations As IReadOnlyList(Of ImageAnnotation))
            Dim deep = DeepOf(composite)
            If deep Is Nothing Then Return
            Try
                Using canvas = New SKCanvas(deep)
                    DrawAnnotationsOnCanvas(canvas, adj, width, height, 0, 0, width, height, annotations)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImageProcessor.Deep", ex)
                DropDeep(composite)
            End Try
        End Sub

        ''' <summary>Gibt das tiefe Bild von <paramref name="source"/> an <paramref name="target"/>
        ''' weiter, wenn das Ziel dasselbe Bild in anderer Anordnung ist (BGRA statt RGBA).</summary>
        Private Shared Sub MoveDeep(source As SKBitmap, target As SKBitmap)
            Dim deep = DetachDeep(source)
            If deep IsNot Nothing Then AttachDeep(target, deep)
        End Sub

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
        Friend Shared Function DeepMatchesShallow(shallow As SKBitmap, deep As SKBitmap) As Boolean
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
            Dim stepSize = Math.Max(1, CInt(Math.Ceiling(Math.Sqrt(CDbl(width) * height / 1000000.0))))
            Dim tolerance = DeepMatchTolerance
            Dim allowedShare = DeepMatchOutlierShare
            Dim shallowPixels = shallow.GetPixels()
            Dim deepPixels = deep.GetPixels()
            If shallowPixels = IntPtr.Zero OrElse deepPixels = IntPtr.Zero Then Return False
            ' Die Zeilen des Rasters nebeneinander, je Zeile gezaehlt und danach zusammengezaehlt:
            ' dieselben Zahlen wie in einer Schleife.
            Dim shallowStride = shallow.RowBytes
            Dim rasterRows = (height - 1) \ stepSize + 1
            Dim comparedPerRow(rasterRows - 1) As Integer
            Dim outliersPerRow(rasterRows - 1) As Integer
            ForEachRow(width, rasterRows,
                Sub(index)
                    Dim y = index * stepSize
                    Dim shallowRow(width * 4 - 1) As Byte
                    Dim deepRow(width * 4 - 1) As Short
                    Marshal.Copy(IntPtr.Add(shallowPixels, y * shallowStride), shallowRow, 0, width * 4)
                    ReadDeepRow(deep, deepPixels, y, deepRow)
                    Dim rowCompared = 0, rowOutliers = 0
                    For x = 0 To width - 1 Step stepSize
                        Dim o = x * 4
                        Dim dr = Math.Abs(CInt(shallowRow(o + ri)) * 257 - (deepRow(o) And &HFFFF))
                        Dim dg = Math.Abs(CInt(shallowRow(o + gi)) * 257 - (deepRow(o + 1) And &HFFFF))
                        Dim db = Math.Abs(CInt(shallowRow(o + bi)) * 257 - (deepRow(o + 2) And &HFFFF))
                        Dim da = Math.Abs(CInt(shallowRow(o + 3)) * 257 - (deepRow(o + 3) And &HFFFF))
                        Dim worst = Math.Max(Math.Max(dr, dg), Math.Max(db, da))
                        rowCompared += 1
                        If worst > tolerance * 257 Then rowOutliers += 1
                    Next
                    comparedPerRow(index) = rowCompared
                    outliersPerRow(index) = rowOutliers
                End Sub)
            Dim compared = 0L, outliers = 0L
            For index = 0 To rasterRows - 1
                compared += comparedPerRow(index)
                outliers += outliersPerRow(index)
            Next
            If compared = 0 Then Return False
            Dim share = outliers / CDbl(compared)
            If share > allowedShare Then
                DiagnosticLogService.LogAlways("ImageProcessor.Deep", String.Format(Globalization.CultureInfo.InvariantCulture,
                    "tiefes Bild weicht vom 8-Bit-Bild ab: {0:P4} der Punkte ueber {1} Stufen", share, tolerance))
                Return False
            End If
            Return DeepMatchesShallowInBlocks(shallow, deep, ri, gi, bi)
        End Function

        ''' <summary>Kante der Felder des zweiten Vergleichs und das Mittel der Abweichung in
        ''' 8-Bit-Stufen, ab dem ein Feld nicht mehr als dasselbe Bild gilt. Die Stufen der Kette
        ''' bleiben im Mittel ueber 8 x 8 unter 0,7 Stufen (Pruefung "jede Stufe der Kette rechnet
        ''' das tiefe Bild mit"); der Abstand dazu ist Spielraum fuer mehrere Stufen hintereinander.</summary>
        Private Const DeepMatchBlock As Integer = 8
        Private Const DeepMatchBlockMean As Double = 2.5

        ''' <summary>Der zweite Vergleich, fuer die KLEINE Stelle: das Raster oben liest jeden
        ''' soundsovielten Punkt und duldet ein Tausendstel Ausreisser, eine Stufe, die das
        ''' 8-Bit-Bild nur an einer kleinen Stelle aendert und das tiefe nicht mitfuehrt, sieht es
        ''' nicht. Hier wird JEDER Bildpunkt gelesen und die Abweichung je Feld von 8 x 8 gemittelt:
        ''' Dither und getrenntes Abtasten heben sich im Feld auf, eine fehlende Aenderung nicht.
        ''' Ein einziges Feld ueber der Grenze genuegt.</summary>
        Private Shared Function DeepMatchesShallowInBlocks(shallow As SKBitmap, deep As SKBitmap,
                                                           ri As Integer, gi As Integer, bi As Integer) As Boolean
            Dim w = shallow.Width, h = shallow.Height
            If w < DeepMatchBlock OrElse h < DeepMatchBlock Then Return True
            Dim shallowPixels = shallow.GetPixels(), deepPixels = deep.GetPixels()
            Dim shallowStride = shallow.RowBytes
            Dim blockLimit = CInt(DeepMatchBlockMean * 257 * DeepMatchBlock * DeepMatchBlock)
            Dim blocksAcross = w \ DeepMatchBlock
            Dim failed = 0
            Dim worstSum = 0, worstX = 0, worstY = 0
            Dim worstLock As New Object()
            Threading.Tasks.Parallel.For(0, h \ DeepMatchBlock,
                Sub(blockRow)
                    Dim top = blockRow * DeepMatchBlock
                    Dim sums(blocksAcross * 4 - 1) As Integer
                    Dim shallowRow(w * 4 - 1) As Byte
                    Dim deepRow(w * 4 - 1) As Short
                    For y = top To top + DeepMatchBlock - 1
                        Marshal.Copy(IntPtr.Add(shallowPixels, y * shallowStride), shallowRow, 0, w * 4)
                        ReadDeepRow(deep, deepPixels, y, deepRow)
                        For x = 0 To blocksAcross * DeepMatchBlock - 1
                            Dim o = x * 4
                            Dim s = (x \ DeepMatchBlock) * 4
                            sums(s) += CInt(shallowRow(o + ri)) * 257 - (deepRow(o) And &HFFFF)
                            sums(s + 1) += CInt(shallowRow(o + gi)) * 257 - (deepRow(o + 1) And &HFFFF)
                            sums(s + 2) += CInt(shallowRow(o + bi)) * 257 - (deepRow(o + 2) And &HFFFF)
                            sums(s + 3) += CInt(shallowRow(o + 3)) * 257 - (deepRow(o + 3) And &HFFFF)
                        Next
                    Next
                    For block = 0 To blocksAcross - 1
                        Dim worst = Math.Max(Math.Max(Math.Abs(sums(block * 4)), Math.Abs(sums(block * 4 + 1))),
                                             Math.Max(Math.Abs(sums(block * 4 + 2)), Math.Abs(sums(block * 4 + 3))))
                        If worst <= blockLimit Then Continue For
                        Interlocked.Increment(failed)
                        SyncLock worstLock
                            If worst > worstSum Then
                                worstSum = worst
                                worstX = block * DeepMatchBlock
                                worstY = top
                            End If
                        End SyncLock
                    Next
                End Sub)
            If failed = 0 Then Return True
            DiagnosticLogService.LogAlways("ImageProcessor.Deep", String.Format(Globalization.CultureInfo.InvariantCulture,
                "tiefes Bild weicht an einer Stelle vom 8-Bit-Bild ab: {0} Feld(er) von 8 x 8 im Mittel ueber {1} Stufen, am weitesten bei {2},{3} mit {4:F1} Stufen",
                failed, DeepMatchBlockMean, worstX, worstY, worstSum / (257.0 * DeepMatchBlock * DeepMatchBlock)))
            Return False
        End Function

    End Class

End Namespace
