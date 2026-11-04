Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ''' <summary>Fokus-Peaking: eine Ebene, die scharfe Kanten des angezeigten Bildes farbig
    ''' markiert, damit man beim Aussortieren sieht, wo das Bild scharf ist, ohne auf 100 Prozent zu
    ''' gehen.
    '''
    ''' <para>WIE: Helligkeit (Rec. 709 auf den sRGB-Werten), darauf der Sobel-Betrag, geteilt durch
    ''' vier, damit eine harte Stufe der Hoehe h den Wert h ergibt. Markiert wird ab
    ''' <see cref="Threshold"/>, danach um einen Bildpunkt verbreitert, sonst gingen die Linien beim
    ''' Einpassen in der Verkleinerung unter. Fester Wert, keine Verteilung des Bildes: ein ganz
    ''' unscharfes Bild soll leer bleiben, nicht seine "schaerfsten" zwei Prozent zeigen.</para>
    '''
    ''' <para>Eine verwaschene Kante ueber vier Bildpunkte mit Stufe 0,3 ergibt rund 0,15, dieselbe
    ''' Stufe hart 0,3; Rauschen um 0,02 Helligkeit bleibt unter 0,03. Gerechnet wird auf hoechstens
    ''' <see cref="MaxEdge"/> Bildpunkten der laengeren Kante, die Ebene wird wie das Bild auf die
    ''' Anzeige gestreckt. Das Verkleinern macht Kanten steiler; der Betrachter zeigt RAW ohnehin in
    ''' halber Groesse oder als eingebettete Vorschau, die Schwelle ist an solchen Groessen gewaehlt
    ''' und nicht an echten Bildern geeicht.</para></summary>
    Public NotInheritable Class FocusPeakingService
        Private Sub New()
        End Sub

        Public Const MaxEdge As Integer = 3000
        Public Const Threshold As Double = 0.2

        ''' <summary>Die Farbe der Markierung: ein kraeftiges Rot, das auf Haut, Laub und Himmel
        ''' gleichermassen heraussticht.</summary>
        Public Shared ReadOnly MarkColor As New SKColor(255, 40, 40, 255)

        ''' <summary>Die Markierungsebene zu <paramref name="source"/>: durchsichtig, scharfe Kanten in
        ''' <see cref="MarkColor"/>. Nothing bei leerer Quelle. Beliebiger Faden; die Quelle wird nur
        ''' gelesen.</summary>
        Public Shared Function Build(source As SKBitmap) As SKBitmap
            If source Is Nothing OrElse source.Width < 3 OrElse source.Height < 3 Then Return Nothing
            Dim scale = Math.Min(1.0, MaxEdge / CDbl(Math.Max(source.Width, source.Height)))
            Dim width = Math.Max(3, CInt(Math.Round(source.Width * scale)))
            Dim height = Math.Max(3, CInt(Math.Round(source.Height * scale)))

            Dim luma(width * height - 1) As Single
            Using work = If(scale < 1.0, source.Resize(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)), Nothing)
                Dim pixels = If(work, source)
                If pixels Is Nothing Then Return Nothing
                Using readable = If(pixels.ColorType = SKColorType.Bgra8888, Nothing, pixels.Copy(SKColorType.Bgra8888))
                    Dim bgra = If(readable, pixels)
                    Dim bytes = bgra.Bytes
                    Dim stride = bgra.RowBytes
                    Parallel.For(0, height,
                        Sub(y)
                            Dim row = y * stride
                            Dim o = y * width
                            For x = 0 To width - 1
                                Dim i = row + x * 4
                                luma(o + x) = CSng((0.0722 * bytes(i) + 0.7152 * bytes(i + 1) + 0.2126 * bytes(i + 2)) / 255.0)
                            Next
                        End Sub)
                End Using
            End Using

            ' Kanten markieren (Rand bleibt frei, dort fehlt die Nachbarschaft).
            Dim marked(width * height - 1) As Boolean
            Dim limit = CSng(Threshold * 4.0)
            Dim limitSquared = limit * limit
            Parallel.For(1, height - 1,
                Sub(y)
                    For x = 1 To width - 2
                        Dim a = (y - 1) * width + x
                        Dim b = y * width + x
                        Dim c = (y + 1) * width + x
                        Dim gx = (luma(a + 1) + 2.0F * luma(b + 1) + luma(c + 1)) - (luma(a - 1) + 2.0F * luma(b - 1) + luma(c - 1))
                        Dim gy = (luma(c - 1) + 2.0F * luma(c) + luma(c + 1)) - (luma(a - 1) + 2.0F * luma(a) + luma(a + 1))
                        If gx * gx + gy * gy >= limitSquared Then marked(b) = True
                    Next
                End Sub)

            ' Um einen Bildpunkt verbreitern und in die Ebene schreiben.
            Dim overlay = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            Dim target = overlay.GetPixels()
            Dim outStride = overlay.RowBytes
            Dim line(width * 4 - 1) As Byte
            Dim cb = MarkColor.Blue, cg = MarkColor.Green, cr = MarkColor.Red, ca = MarkColor.Alpha
            For y = 0 To height - 1
                Array.Clear(line, 0, line.Length)
                For x = 0 To width - 1
                    Dim hit = False
                    For dy = -1 To 1
                        Dim yy = y + dy
                        If yy < 0 OrElse yy >= height Then Continue For
                        For dx = -1 To 1
                            Dim xx = x + dx
                            If xx < 0 OrElse xx >= width Then Continue For
                            If marked(yy * width + xx) Then hit = True : Exit For
                        Next
                        If hit Then Exit For
                    Next
                    If hit Then
                        Dim i = x * 4
                        line(i) = cb : line(i + 1) = cg : line(i + 2) = cr : line(i + 3) = ca
                    End If
                Next
                System.Runtime.InteropServices.Marshal.Copy(line, 0, IntPtr.Add(target, y * outStride), line.Length)
            Next
            Return overlay
        End Function
    End Class

End Namespace
