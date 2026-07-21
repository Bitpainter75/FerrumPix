Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Zentrale Abbildung zwischen ungedrehtem Rezept-/Arbeitsbildraum und sichtbarer Bildgeometrie.
    ''' Rezeptdaten bleiben in SourceSpace; Drehung/Flip werden erst fuer Anzeige und Rendern
    ''' zusammengesetzt.
    ''' </summary>
    Public NotInheritable Class ImageGeometryMapper

        Private Sub New()
        End Sub

        Public Shared Function NormalizeQuarterTurn(degrees As Integer) As Integer
            Dim normalized = ((degrees Mod 360) + 360) Mod 360
            Select Case normalized
                Case 90, 180, 270
                    Return normalized
                Case Else
                    Return 0
            End Select
        End Function

        Public Shared Function NormalizeRotation(degrees As Double) As Double
            Return ((degrees Mod 360.0) + 540.0) Mod 360.0 - 180.0
        End Function

        Public Shared Function DisplaySize(sourceWidth As Integer, sourceHeight As Integer, rotationDegrees As Integer) As SKSizeI
            Dim q = NormalizeQuarterTurn(rotationDegrees)
            If q = 90 OrElse q = 270 Then Return New SKSizeI(sourceHeight, sourceWidth)
            Return New SKSizeI(sourceWidth, sourceHeight)
        End Function

        Public Shared Function SourceObjectRotationToDisplay(localRotationDegrees As Double,
                                                             rotationDegrees As Integer,
                                                             flipHorizontal As Boolean,
                                                             flipVertical As Boolean) As Single
            Dim rotation = localRotationDegrees + NormalizeQuarterTurn(rotationDegrees)
            If flipHorizontal Xor flipVertical Then rotation = -rotation
            Return CSng(NormalizeRotation(rotation))
        End Function

        Public Shared Function DisplayObjectRotationToSource(displayRotationDegrees As Double,
                                                             rotationDegrees As Integer,
                                                             flipHorizontal As Boolean,
                                                             flipVertical As Boolean) As Single
            Dim rotation = displayRotationDegrees
            If flipHorizontal Xor flipVertical Then rotation = -rotation
            rotation -= NormalizeQuarterTurn(rotationDegrees)
            Return CSng(NormalizeRotation(rotation))
        End Function

        Public Shared Function SourcePointToDisplay(x As Double, y As Double,
                                                    sourceWidth As Double, sourceHeight As Double,
                                                    rotationDegrees As Integer,
                                                    flipHorizontal As Boolean,
                                                    flipVertical As Boolean) As SKPoint
            Dim display = DisplaySize(CInt(Math.Round(sourceWidth)), CInt(Math.Round(sourceHeight)), rotationDegrees)
            Dim dx = x
            Dim dy = y
            Select Case NormalizeQuarterTurn(rotationDegrees)
                Case 90
                    dx = sourceHeight - y
                    dy = x
                Case 180
                    dx = sourceWidth - x
                    dy = sourceHeight - y
                Case 270
                    dx = y
                    dy = sourceWidth - x
            End Select
            If flipHorizontal Then dx = display.Width - dx
            If flipVertical Then dy = display.Height - dy
            Return New SKPoint(CSng(dx), CSng(dy))
        End Function

        Public Shared Function DisplayPointToSource(x As Double, y As Double,
                                                    sourceWidth As Double, sourceHeight As Double,
                                                    rotationDegrees As Integer,
                                                    flipHorizontal As Boolean,
                                                    flipVertical As Boolean) As SKPoint
            Dim display = DisplaySize(CInt(Math.Round(sourceWidth)), CInt(Math.Round(sourceHeight)), rotationDegrees)
            Dim dx = x
            Dim dy = y
            If flipHorizontal Then dx = display.Width - dx
            If flipVertical Then dy = display.Height - dy

            Dim sx = dx
            Dim sy = dy
            Select Case NormalizeQuarterTurn(rotationDegrees)
                Case 90
                    sx = dy
                    sy = sourceHeight - dx
                Case 180
                    sx = sourceWidth - dx
                    sy = sourceHeight - dy
                Case 270
                    sx = sourceWidth - dy
                    sy = dx
            End Select
            Return New SKPoint(CSng(sx), CSng(sy))
        End Function

        Public Shared Function SourceObjectToDisplay(rect As SKRect, sourceWidth As Double, sourceHeight As Double,
                                                     outputWidth As Double, outputHeight As Double,
                                                     rotationDegrees As Integer,
                                                     flipHorizontal As Boolean,
                                                     flipVertical As Boolean,
                                                     localRotationDegrees As Double) As (Rect As SKRect, RotationDegrees As Single)
            Dim q = NormalizeQuarterTurn(rotationDegrees)
            Dim preWidth = If(q = 90 OrElse q = 270, outputHeight, outputWidth)
            Dim preHeight = If(q = 90 OrElse q = 270, outputWidth, outputHeight)
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse preWidth <= 0 OrElse preHeight <= 0 Then
                Return (rect, CSng(NormalizeRotation(localRotationDegrees)))
            End If

            Dim scaleX = preWidth / sourceWidth
            Dim scaleY = preHeight / sourceHeight
            Dim scaledRect = New SKRect(CSng(rect.Left * scaleX),
                                        CSng(rect.Top * scaleY),
                                        CSng(rect.Right * scaleX),
                                        CSng(rect.Bottom * scaleY))
            Dim center = SourcePointToDisplay(scaledRect.MidX, scaledRect.MidY,
                                              preWidth, preHeight,
                                              q, flipHorizontal, flipVertical)
            Dim displayRect = SKRect.Create(center.X - scaledRect.Width / 2.0F,
                                            center.Y - scaledRect.Height / 2.0F,
                                            scaledRect.Width,
                                            scaledRect.Height)
            Return (displayRect, SourceObjectRotationToDisplay(localRotationDegrees, q, flipHorizontal, flipVertical))
        End Function

        Public Shared Function DisplayObjectToSource(rect As SKRect, sourceWidth As Double, sourceHeight As Double,
                                                     displayWidth As Double, displayHeight As Double,
                                                     rotationDegrees As Integer,
                                                     flipHorizontal As Boolean,
                                                     flipVertical As Boolean,
                                                     displayRotationDegrees As Double) As (Rect As SKRect, RotationDegrees As Single)
            Dim q = NormalizeQuarterTurn(rotationDegrees)
            Dim preWidth = If(q = 90 OrElse q = 270, displayHeight, displayWidth)
            Dim preHeight = If(q = 90 OrElse q = 270, displayWidth, displayHeight)
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse preWidth <= 0 OrElse preHeight <= 0 Then
                Return (rect, CSng(NormalizeRotation(displayRotationDegrees)))
            End If

            Dim center = DisplayPointToSource(rect.MidX, rect.MidY,
                                              preWidth, preHeight,
                                              q, flipHorizontal, flipVertical)
            Dim sourceScaleX = sourceWidth / preWidth
            Dim sourceScaleY = sourceHeight / preHeight
            Dim sourceWidthPixels = rect.Width * sourceScaleX
            Dim sourceHeightPixels = rect.Height * sourceScaleY
            Dim sourceRect = SKRect.Create(CSng(center.X * sourceScaleX - sourceWidthPixels / 2.0),
                                           CSng(center.Y * sourceScaleY - sourceHeightPixels / 2.0),
                                           CSng(sourceWidthPixels),
                                           CSng(sourceHeightPixels))

            Return (sourceRect, DisplayObjectRotationToSource(displayRotationDegrees, q, flipHorizontal, flipVertical))
        End Function

    End Class

End Namespace
