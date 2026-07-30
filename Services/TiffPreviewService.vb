Imports System
Imports System.IO
Imports System.Runtime.InteropServices
Imports BitMiracle.LibTiff.Classic
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Liest TIFF/TIF - SkiaSharp kann das Format gar nicht (weder lesen noch schreiben). Ohne
    ''' diesen Weg standen TIFF-Dateien zwar in der Galerie, ließen sich aber nicht öffnen.
    '''
    ''' Benutzt BitMiracle.LibTiff.NET: eine reine .NET-Übersetzung von libtiff, also OHNE native
    ''' Beigabe - damit funktioniert es auf allen Paketen (Linux, Windows, macOS, portabel) ohne
    ''' zusätzliche Systembibliothek. Lizenz BSD-3-Clause, verträglich mit der GPL-3 der Anwendung.
    '''
    ''' NUR LESEN, wie bei PSD und HEIC: geschrieben wird in ein Format, für das es einen Encoder
    ''' gibt. Ein TIFF-Ziel weist <see cref="ImageProcessor.CanEncodeToTargetExtension"/> ab, damit
    ''' keine Datei still JPEG-Bytes unter der Endung .tif bekommt.
    '''
    ''' Gelesen wird über ReadRGBAImageOriented: libtiff setzt dabei die vielen TIFF-Spielarten
    ''' (8/16 Bit, Graustufen, Palette, CMYK, LZW/Deflate/JPEG-komprimiert, Streifen und Kacheln)
    ''' selbst auf 8-Bit-RGBA um und wendet das Orientierungs-Tag an. Mehrseitige TIFFs liefern
    ''' die erste Seite - wie bei GIF das erste Einzelbild.
    ''' </summary>
    Public NotInheritable Class TiffPreviewService

        Private Sub New()
        End Sub

        ''' <summary>Obergrenze gegen Speicherausfälle: ReadRGBAImage legt EINEN Integer je Pixel an
        ''' (4 Byte), dazu kommt das Ziel-Bitmap. 400 MP wären damit schon 3,2 GB - solche Dateien
        ''' werden abgewiesen statt den Prozess zu sprengen.</summary>
        Private Const MaxPixels As Long = 300L * 1000L * 1000L

        Public Shared Function IsSupportedTiff(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Select Case IO.Path.GetExtension(filePath).ToLowerInvariant()
                Case ".tif", ".tiff"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        ''' <summary>Unterdrückt libtiffs Warnungen/Fehler auf der Konsole. Viele Kamera- und
        ''' Scanner-TIFFs enthalten unbekannte private Tags; das ist kein Grund für Rauschen im
        ''' Protokoll, und die eigentlichen Fehler erkennt der Aufrufer am Rückgabewert.</summary>
        Private NotInheritable Class StillerFehlerHandler
            Inherits TiffErrorHandler

            Public Overrides Sub WarningHandler(tif As Tiff, method As String, fileFormat As String, ParamArray args() As Object)
            End Sub

            Public Overrides Sub WarningHandlerExt(tif As Tiff, clientData As Object, method As String, fileFormat As String, ParamArray args() As Object)
            End Sub

            Public Overrides Sub ErrorHandler(tif As Tiff, method As String, fileFormat As String, ParamArray args() As Object)
            End Sub

            Public Overrides Sub ErrorHandlerExt(tif As Tiff, clientData As Object, method As String, fileFormat As String, ParamArray args() As Object)
            End Sub
        End Class

        Private Shared ReadOnly _handlerLock As New Object()
        Private Shared _handlerGesetzt As Boolean

        Private Shared Sub EnsureSilentHandler()
            SyncLock _handlerLock
                If _handlerGesetzt Then Return
                _handlerGesetzt = True
                Try
                    Tiff.SetErrorHandler(New StillerFehlerHandler())
                Catch
                End Try
            End SyncLock
        End Sub

        ''' <summary>Erste Seite als Bgra8888 (Besitz beim Aufrufer) oder Nothing. Die Orientierung
        ''' aus dem TIFF-Tag ist bereits angewandt - KEINE zweite Korrektur nachschalten.</summary>
        Public Shared Function TryDecode(path As String) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return Nothing
            EnsureSilentHandler()
            Try
                Using tif = Tiff.Open(path, "r")
                    If tif Is Nothing Then Return Nothing

                    Dim widthField = tif.GetField(TiffTag.IMAGEWIDTH)
                    Dim heightField = tif.GetField(TiffTag.IMAGELENGTH)
                    If widthField Is Nothing OrElse heightField Is Nothing Then Return Nothing
                    Dim width = widthField(0).ToInt()
                    Dim height = heightField(0).ToInt()
                    If width <= 0 OrElse height <= 0 Then Return Nothing
                    If CLng(width) * CLng(height) > MaxPixels Then Return Nothing

                    Dim raster(width * height - 1) As Integer
                    If Not tif.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT, stopOnError:=False) Then Return Nothing

                    ' libtiff packt je Pixel R,G,B,A in ein Integer (Bit 0-7 = Rot). Auf
                    ' Little-Endian liegen die Bytes damit als R,G,B,A im Speicher - Skia erwartet
                    ' bei Bgra8888 aber B,G,R,A. Rot und Blau also tauschen, zeilenweise.
                    Dim bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul))
                    Try
                        Dim rowBytes = width * 4
                        Dim row(rowBytes - 1) As Byte
                        Dim target = bitmap.GetPixels()
                        Dim targetStride = bitmap.RowBytes
                        For y = 0 To height - 1
                            Buffer.BlockCopy(raster, y * rowBytes, row, 0, rowBytes)
                            For x = 0 To rowBytes - 4 Step 4
                                Dim r = row(x)
                                row(x) = row(x + 2)
                                row(x + 2) = r
                            Next
                            ' Versatz in Integer, siehe HeifDecodeService: IntPtr addiert nur
                            ' Integer. Die Schranke ist hier MaxPixels (300 Millionen).
                            Marshal.Copy(row, 0, target + y * targetStride, rowBytes)
                        Next
                        Return bitmap
                    Catch
                        bitmap.Dispose()
                        Return Nothing
                    End Try
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Die Datei als PNG-Strom - die Form, die OpenSourceStream und die
        ''' Thumbnail-Erzeugung erwarten (gleiches Muster wie PSD/ICO/HEIC).</summary>
        Public Shared Function ExtractPreview(path As String) As MemoryStream
            Using bmp = TryDecode(path)
                If bmp Is Nothing Then Return Nothing
                Using image = SKImage.FromBitmap(bmp)
                    If image Is Nothing Then Return Nothing
                    Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                        If data Is Nothing Then Return Nothing
                        Dim ms As New MemoryStream()
                        data.SaveTo(ms)
                        ms.Position = 0
                        Return ms
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Maße aus den Kopfdaten, ohne die Bilddaten zu lesen.</summary>
        Public Shared Function TryGetSize(path As String) As (Width As Integer, Height As Integer)
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return (0, 0)
            EnsureSilentHandler()
            Try
                Using tif = Tiff.Open(path, "r")
                    If tif Is Nothing Then Return (0, 0)
                    Dim widthField = tif.GetField(TiffTag.IMAGEWIDTH)
                    Dim heightField = tif.GetField(TiffTag.IMAGELENGTH)
                    If widthField Is Nothing OrElse heightField Is Nothing Then Return (0, 0)
                    Return (widthField(0).ToInt(), heightField(0).ToInt())
                End Using
            Catch
                Return (0, 0)
            End Try
        End Function

    End Class

End Namespace
