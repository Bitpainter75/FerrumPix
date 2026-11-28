Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Runtime.InteropServices
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ''' <summary>Motiv auswaehlen: das Hauptmotiv des Bildes als weiche Maske, ohne Klick.
    '''
    ''' Anders als die Objektauswahl (<see cref="SubjectMaskService"/>) beantwortet dieses Modell
    ''' keine Frage nach einem Punkt, sondern immer dieselbe: was ist hier das Motiv? Dafuer liefert
    ''' es eine MATTE, also Deckung zwischen null und eins schon aus dem Netz heraus, auf einem Raster
    ''' von 1024 Punkten statt 256. Haare und Zweige bekommen damit ihre Teildeckung vom Modell
    ''' selbst, nicht erst aus einer Kennlinie danach.
    '''
    ''' Das Bild wird OHNE Rand auf das Quadrat gestreckt, nicht eingepasst - so ist das Modell
    ''' trainiert (Vorverarbeitung der Ablage: Groesse 1024 mal 1024, bilinear, ImageNet-Normierung).
    ''' Die Ausgabe ist ein Rohwert; Deckung entsteht erst mit der Sigmoidfunktion.
    '''
    ''' Danach wird die Matte mit dem Fuehrungsfilter an das Bild angelegt
    ''' (<see cref="ImageProcessor.RefineModelMaskEdge"/>), in einem Saum, der zur Rastergroesse des
    ''' Modells passt.</summary>
    Public NotInheritable Class SalientSubjectService

        Private Sub New()
        End Sub

        ''' <summary>Schluessel im Modellregister, ohne Version.</summary>
        Public Const ModelFile As String = "birefnet-lite"

        ''' <summary>Kantenlaenge des Quadrats, auf dem das Modell rechnet.</summary>
        Public Const ModelEdge As Integer = 1024

        ''' <summary>Steht die Funktion zur Verfuegung? Nur mit Laufzeit UND Modelldatei.</summary>
        Public Shared ReadOnly Property Available As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(ModelFile))
            End Get
        End Property

        ''' <summary>Die rohe Matte des Modells, als Alpha8 in der Groesse von
        ''' <paramref name="image"/>, OHNE Anlegen an die Kanten. Nothing, wenn das Modell fehlt oder
        ''' nicht laeuft.</summary>
        Public Shared Function Matte(image As SKBitmap) As SKBitmap
            If image Is Nothing OrElse image.Width <= 0 OrElse image.Height <= 0 Then Return Nothing
            Dim session = AiModelService.SessionFor(ModelFile)
            If session Is Nothing Then Return Nothing

            Dim inputName = session.InputMetadata.Keys.First()
            Dim inputIsHalf = session.InputMetadata(inputName).ElementDataType = TensorElementType.Float16
            Dim layer = ModelEdge * ModelEdge
            Dim values(3 * layer - 1) As Single

            ' Gestreckt, nicht eingepasst: das Seitenverhaeltnis geht verloren, und genau so hat das
            ' Modell gelernt. Mit Mipmaps, weil ein 24-MP-Bild sonst beim Verkleinern flimmert.
            Using small = New SKBitmap(New SKImageInfo(ModelEdge, ModelEdge, SKColorType.Bgra8888, SKAlphaType.Unpremul))
                If Not ImageProcessor.ScalePixelsFast(image, small,
                                                      New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) Then Return Nothing
                Dim pixels(small.RowBytes * ModelEdge - 1) As Byte
                Marshal.Copy(small.GetPixels(), pixels, 0, pixels.Length)
                Dim rowBytes = small.RowBytes
                ' Die Normierung stammt aus dem Training (ImageNet) und darf nicht geraten werden.
                Const MeanR As Single = 0.485F, MeanG As Single = 0.456F, MeanB As Single = 0.406F
                Const StdR As Single = 0.229F, StdG As Single = 0.224F, StdB As Single = 0.225F
                For y = 0 To ModelEdge - 1
                    Dim row = y * rowBytes
                    For x = 0 To ModelEdge - 1
                        Dim p = row + x * 4
                        Dim i = y * ModelEdge + x
                        values(i) = (pixels(p + 2) / 255.0F - MeanR) / StdR
                        values(layer + i) = (pixels(p + 1) / 255.0F - MeanG) / StdG
                        values(2 * layer + i) = (pixels(p) / 255.0F - MeanB) / StdB
                    Next
                Next
            End Using

            Dim dims = New Integer() {1, 3, ModelEdge, ModelEdge}
            Dim input As NamedOnnxValue
            If inputIsHalf Then
                Dim half(values.Length - 1) As Float16
                For i = 0 To values.Length - 1
                    half(i) = CType(values(i), Float16)
                Next
                input = NamedOnnxValue.CreateFromTensor(inputName, New DenseTensor(Of Float16)(half, dims))
            Else
                input = NamedOnnxValue.CreateFromTensor(inputName, New DenseTensor(Of Single)(values, dims))
            End If

            Dim logits As Single()
            Dim outW As Integer, outH As Integer
            Try
                Using runGuard = GpuModelBlockService.GuardRun(session),
                      result = session.Run(New List(Of NamedOnnxValue) From {input})
                    ' Der letzte Ausgang ist die feinste Stufe; die Ablage hat ohnehin nur einen.
                    Dim output = result.Last()
                    If output.ElementType = TensorElementType.Float16 Then
                        Dim t = output.AsTensor(Of Float16)()
                        Dim d = t.Dimensions.ToArray()
                        outH = d(d.Length - 2) : outW = d(d.Length - 1)
                        logits = t.Select(Function(h) CSng(h)).ToArray()
                    Else
                        Dim t = output.AsTensor(Of Single)()
                        Dim d = t.Dimensions.ToArray()
                        outH = d(d.Length - 2) : outW = d(d.Length - 1)
                        logits = t.ToArray()
                    End If
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogAlways("MotivAuswahl", "Modell: " & ex.Message)
                Return Nothing
            End Try
            If outW <= 0 OrElse outH <= 0 OrElse logits.Length < outW * outH Then Return Nothing

            ' Rohwert in Deckung, auf dem Raster des Modells.
            Dim cover(outW * outH - 1) As Single
            For i = 0 To cover.Length - 1
                cover(i) = 1.0F / (1.0F + CSng(Math.Exp(-logits(i))))
            Next
            Return UpsampleCover(cover, outW, outH, image.Width, image.Height)
        End Function

        ''' <summary>Das Raster bilinear auf die Bildgroesse, als Alpha8. Die Werte sitzen in der MITTE
        ''' ihres Feldes (halbes Feld Versatz), wie bei der Objektauswahl.</summary>
        Friend Shared Function UpsampleCover(cover As Single(), gridW As Integer, gridH As Integer,
                                             width As Integer, height As Integer) As SKBitmap
            Dim result = New SKBitmap(New SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul))
            Dim rowBytes = result.RowBytes
            Dim buffer(rowBytes * height - 1) As Byte
            Dim sx = gridW / CDbl(width), sy = gridH / CDbl(height)
            Dim columnA(width - 1) As Integer, columnB(width - 1) As Integer, weightB(width - 1) As Single
            For x = 0 To width - 1
                Dim fx = (x + 0.5) * sx - 0.5
                Dim x0 = CInt(Math.Floor(fx))
                columnA(x) = Math.Max(0, Math.Min(gridW - 1, x0))
                columnB(x) = Math.Max(0, Math.Min(gridW - 1, x0 + 1))
                weightB(x) = CSng(fx - x0)
            Next
            ImageProcessor.ForEachRow(width, height,
                Sub(y)
                Dim fy = (y + 0.5) * sy - 0.5
                Dim y0 = CInt(Math.Floor(fy))
                Dim ty = CSng(fy - y0)
                Dim rowA = Math.Max(0, Math.Min(gridH - 1, y0)) * gridW
                Dim rowB = Math.Max(0, Math.Min(gridH - 1, y0 + 1)) * gridW
                Dim target = y * rowBytes
                For x = 0 To width - 1
                    Dim tx = weightB(x)
                    Dim top = cover(rowA + columnA(x)) * (1.0F - tx) + cover(rowA + columnB(x)) * tx
                    Dim bottom = cover(rowB + columnA(x)) * (1.0F - tx) + cover(rowB + columnB(x)) * tx
                    Dim v = top * (1.0F - ty) + bottom * ty
                    buffer(target + x) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(v * 255.0F)))))
                Next
                End Sub)
            Marshal.Copy(buffer, 0, result.GetPixels(), buffer.Length)
            Return result
        End Function

        ''' <summary>Die Motivmaske, fertig an das Bild angelegt, als Alpha8 in voller Bildgroesse.
        ''' <paramref name="image"/> ist zugleich Eingang des Modells und Fuehrung des Filters - es
        ''' muss also das Bild in der Groesse des Koordinatensystems der Auswahl sein. Nothing, wenn
        ''' das Modell nicht laeuft oder kein Motiv findet.</summary>
        Public Shared Function SubjectMask(image As SKBitmap) As SKBitmap
            Using raw = Matte(image)
                If raw Is Nothing Then Return Nothing
                Return ImageProcessor.RefineModelMaskEdgeFull(raw, image, ModelEdge)
            End Using
        End Function

    End Class

End Namespace
