Imports System.IO
Imports System.Runtime.InteropServices
Imports Avalonia
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports SkiaSharp

Namespace Services

    ' Liest und korrigiert das EXIF-Orientation-Tag (Werte 1-8), das Kameras/Handys setzen,
    ' wenn Bildpixel physisch anders gespeichert werden als sie angezeigt werden sollen.
    Public NotInheritable Class ImageOrientationService
        Private Sub New()
        End Sub

        ' SKCodec.Create liest nur den Datei-Header (keine Pixel-Decodierung) - günstig genug,
        ' um bei jeder Thumbnail-Erzeugung/jedem Bildladen aufgerufen zu werden.
        Public Shared Function ReadOrigin(stream As Stream) As SKEncodedOrigin
            Try
                Using codec = SKCodec.Create(stream)
                    If codec Is Nothing Then Return SKEncodedOrigin.TopLeft
                    Return codec.EncodedOrigin
                End Using
            Catch
                Return SKEncodedOrigin.TopLeft
            End Try
        End Function

        Public Shared Function ReadOrigin(filePath As String) As SKEncodedOrigin
            Try
                Using stream = File.OpenRead(filePath)
                    Return ReadOrigin(stream)
                End Using
            Catch
                Return SKEncodedOrigin.TopLeft
            End Try
        End Function

        ' Liefert bei TopLeft dieselbe Instanz zurück (kein Alloc, kein Draw). Andernfalls ein
        ' neues SKBitmap mit der EXIF-Korrektur gebacken (Breite/Höhe bei 5/6/7/8 vertauscht).
        ' Aufrufer müssen das Ergebnis nur disposen, wenn es NICHT dieselbe Referenz wie "source" ist.
        Public Shared Function ApplyOrientation(source As SKBitmap, origin As SKEncodedOrigin) As SKBitmap
            If source Is Nothing OrElse origin = SKEncodedOrigin.TopLeft Then Return source

            Dim sw = source.Width
            Dim sh = source.Height
            Dim swapped = origin = SKEncodedOrigin.LeftTop OrElse origin = SKEncodedOrigin.RightTop OrElse
                          origin = SKEncodedOrigin.RightBottom OrElse origin = SKEncodedOrigin.LeftBottom
            Dim resultWidth = If(swapped, sh, sw)
            Dim resultHeight = If(swapped, sw, sh)

            Dim result = New SKBitmap(resultWidth, resultHeight, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                Select Case origin
                    Case SKEncodedOrigin.TopRight            ' 2: horizontal spiegeln
                        canvas.Translate(sw, 0)
                        canvas.Scale(-1, 1)
                    Case SKEncodedOrigin.BottomRight          ' 3: 180 Grad drehen
                        canvas.Translate(sw, sh)
                        canvas.RotateDegrees(180)
                    Case SKEncodedOrigin.BottomLeft           ' 4: vertikal spiegeln
                        canvas.Translate(0, sh)
                        canvas.Scale(1, -1)
                    Case SKEncodedOrigin.LeftTop              ' 5: transponieren
                        canvas.RotateDegrees(90)
                        canvas.Scale(1, -1)
                    Case SKEncodedOrigin.RightTop             ' 6: 90 Grad im Uhrzeigersinn
                        canvas.Translate(sh, 0)
                        canvas.RotateDegrees(90)
                    Case SKEncodedOrigin.RightBottom          ' 7: transversal spiegeln
                        canvas.Translate(sh, sw)
                        canvas.RotateDegrees(270)
                        canvas.Scale(1, -1)
                    Case SKEncodedOrigin.LeftBottom           ' 8: 270 Grad im Uhrzeigersinn
                        canvas.Translate(0, sw)
                        canvas.RotateDegrees(270)
                End Select
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return result
        End Function

        ' Kompletter Decode-und-Korrektur-Vorgang für Stellen, die eine Avalonia-Bitmap brauchen.
        ' Der weit überwiegende Teil der Bilder braucht KEINE Korrektur (TopLeft) - für die wird
        ' sofort auf den nativen, schnellen Avalonia-Decoder zurückgefallen (New Bitmap(stream)),
        ' exakt wie vor der EXIF-Korrektur. Nur Bilder mit tatsächlich abweichender Orientierung
        ' nehmen den langsameren SKCodec-Pfad, und selbst der kopiert die Pixel direkt statt sie
        ' (wie in einer früheren Version) über einen PNG-Encode/Decode-Umweg zu schicken.
        ''' <paramref name="extraRotationDegrees"/>: zusaetzliche Viertel-Drehung NACH der
        ''' EXIF-Korrektur - fuer RAW-Dateien, deren Drehung im Sidecar steht statt in den Pixeln.
        ''' <paramref name="rawContainerPath"/>: Pfad der RAW-Datei, aus der dieser Strom stammt.
        ''' Traegt die eingebettete Vorschau kein eigenes Orientation-Tag, wird das des Containers
        ''' herangezogen (siehe RawPreviewOrigin) - sonst steht die Anzeige quer zur Entwicklung.
        Public Shared Function LoadOrientedAvaloniaBitmap(stream As Stream, Optional extraRotationDegrees As Integer = 0,
                                                          Optional rawContainerPath As String = Nothing) As Bitmap
            ' Den Inhalt EINMAL puffern und alle Decode-Wege daraus bedienen.
            ' Grund: SKCodec.Create(Stream) uebernimmt den Strom, und manche Codecs - allen voran
            ' WebP - schliessen ihn dabei sofort. Das spaetere stream.Seek fuer den Rueckfall warf
            ' dann ObjectDisposedException ("Cannot access a closed file"), womit sich WebP-Dateien
            ' im Editor gar nicht anzeigen liessen (gefunden 2026-07-20 durch die neue Pruefung
            ' "Anzeigebild aus dem Arbeitsbild stimmt mit dem separaten Decode ueberein").
            ' ImageProcessor.DecodeOriented hatte dieselbe Falle und loest sie genauso - der
            ' Anzeigeweg war nur nie nachgezogen worden.
            Dim data As SKData
            Try
                data = SKData.Create(stream)
            Catch
                data = Nothing
            End Try
            If data Is Nothing Then Return Nothing

            Using data
                Using codec = SKCodec.Create(data)
                    If codec Is Nothing Then Return BitmapFromData(data)

                    Dim origin = codec.EncodedOrigin
                    If Not String.IsNullOrWhiteSpace(rawContainerPath) Then
                        origin = RawPreviewOrigin(rawContainerPath, origin, codec.Info.Width, codec.Info.Height)
                    End If
                    If origin = SKEncodedOrigin.TopLeft AndAlso NormalizeQuarterTurn(extraRotationDegrees) = 0 Then
                        Return BitmapFromData(data)
                    End If

                    Dim info = codec.Info
                    Dim decodeInfo = New SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using original = New SKBitmap(decodeInfo)
                        Dim decodeResult = codec.GetPixels(decodeInfo, original.GetPixels())
                        If decodeResult <> SKCodecResult.Success AndAlso decodeResult <> SKCodecResult.IncompleteInput Then
                            Return BitmapFromData(data)
                        End If

                        Dim corrected = ApplyOrientation(original, origin)
                        Try
                            Dim rotated = ApplyQuarterRotation(corrected, extraRotationDegrees)
                            Try
                                Return ToAvaloniaBitmapFast(rotated)
                            Finally
                                If Not Object.ReferenceEquals(rotated, corrected) Then rotated.Dispose()
                            End Try
                        Finally
                            If Not Object.ReferenceEquals(corrected, original) Then corrected.Dispose()
                        End Try
                    End Using
                End Using
            End Using
        End Function

        ' ── Orientierung einer RAW-Datei aus ihrem TIFF-Kopf ──────────────────────
        ' Hintergrund (Nutzer-Befund 2026-07-20, belegt an PENTAX .PEF und Sony .ARW): die in eine
        ' RAW-Datei eingebettete JPEG-Vorschau traegt haeufig KEIN eigenes Orientation-Tag - sie
        ' liegt so quer da, wie der Sensor sie aufgenommen hat. libraw dreht die Entwicklung
        ' dagegen selbst anhand des Tags im RAW-CONTAINER. Ergebnis: der Editor zeigte das Bild
        ' hochkant, Viewer und Kacheln quer - bei derselben Datei, ohne jedes Zutun des Nutzers.
        '
        ' Deshalb wird die Orientierung des Containers gelesen und auf die Vorschau angewandt.
        ' Die meisten RAW-Formate (PEF, ARW, NEF, CR2, DNG, RW2, ORF) sind TIFF-basiert; alles
        ' andere liefert schlicht TopLeft, also das bisherige Verhalten.

        Private Structure RawContainerInfo
            Public Origin As SKEncodedOrigin
            Public SensorWidth As Integer
            Public SensorHeight As Integer
        End Structure

        ''' <summary>Effektive Orientierung fuer eine aus einer RAW-Datei extrahierte Vorschau.
        '''
        ''' Reihenfolge: bringt die Vorschau ein eigenes Tag mit, gilt das (die Kamera hat sie dann
        ''' bewusst so beschriftet). Sonst zaehlt das Tag des Containers - aber nur, wenn die
        ''' Vorschau auch wirklich noch ungedreht ist. Manche Kameras betten eine BEREITS aufrecht
        ''' stehende Vorschau ohne Tag ein; die wuerde ein blindes Anwenden ein zweites Mal drehen.
        ''' Erkannt wird das am Seitenverhaeltnis: liegt die Vorschau so herum wie der ungedrehte
        ''' Sensor, muss noch gedreht werden - steht sie schon anders, ist es bereits geschehen.</summary>
        Public Shared Function RawPreviewOrigin(rawPath As String, previewOrigin As SKEncodedOrigin,
                                                previewWidth As Integer, previewHeight As Integer) As SKEncodedOrigin
            If previewOrigin <> SKEncodedOrigin.TopLeft Then Return previewOrigin
            If String.IsNullOrWhiteSpace(rawPath) OrElse previewWidth <= 0 OrElse previewHeight <= 0 Then Return previewOrigin

            Dim container = ReadRawContainerInfo(rawPath)
            If container.Origin = SKEncodedOrigin.TopLeft Then Return previewOrigin

            Dim tauscht = container.Origin = SKEncodedOrigin.LeftTop OrElse container.Origin = SKEncodedOrigin.RightTop OrElse
                          container.Origin = SKEncodedOrigin.RightBottom OrElse container.Origin = SKEncodedOrigin.LeftBottom
            If tauscht AndAlso container.SensorWidth > 0 AndAlso container.SensorHeight > 0 Then
                Dim sensorQuer = container.SensorWidth >= container.SensorHeight
                Dim vorschauQuer = previewWidth >= previewHeight
                ' Vorschau steht schon anders herum als der Sensor -> sie wurde bereits gedreht.
                If sensorQuer <> vorschauQuer Then Return previewOrigin
            End If
            Return container.Origin
        End Function

        ''' <summary>Orientation (0x0112) und Sensormasse (0x0100/0x0101) aus der ersten IFD eines
        ''' TIFF-basierten RAW. Bei allem, was kein TIFF ist oder unterwegs nicht aufgeht: TopLeft.</summary>
        Private Shared Function ReadRawContainerInfo(path As String) As RawContainerInfo
            Dim result As RawContainerInfo
            result.Origin = SKEncodedOrigin.TopLeft
            Try
                Using fs = File.OpenRead(path)
                    Dim header(7) As Byte
                    If fs.Read(header, 0, 8) <> 8 Then Return result
                    Dim littleEndian As Boolean
                    If header(0) = &H49 AndAlso header(1) = &H49 Then
                        littleEndian = True
                    ElseIf header(0) = &H4D AndAlso header(1) = &H4D Then
                        littleEndian = False
                    Else
                        Return result
                    End If
                    ' 42 kennzeichnet TIFF. Manche Formate nutzen andere Magic-Werte (etwa 0x55 bei
                    ' Panasonic RW2) - deren IFD-Aufbau ist derselbe, deshalb nicht darauf bestehen.
                    Dim ifdOffset = CLng(ToU32(header, 4, littleEndian))
                    If ifdOffset <= 0 OrElse ifdOffset >= fs.Length Then Return result

                    fs.Position = ifdOffset
                    Dim countBuf(1) As Byte
                    If fs.Read(countBuf, 0, 2) <> 2 Then Return result
                    Dim entries = ToU16(countBuf, 0, littleEndian)
                    ' Ein Kopf mit tausenden Eintraegen ist kaputt oder kein TIFF - nicht weiterlesen.
                    If entries <= 0 OrElse entries > 512 Then Return result

                    Dim entry(11) As Byte
                    For i = 0 To entries - 1
                        If fs.Read(entry, 0, 12) <> 12 Then Exit For
                        Dim tag = ToU16(entry, 0, littleEndian)
                        Dim fieldType = ToU16(entry, 2, littleEndian)
                        ' Der Wert steht im Eintrag selbst, solange er in vier Bytes passt (SHORT/LONG).
                        Dim value As Long
                        If fieldType = 3 Then          ' SHORT
                            value = ToU16(entry, 8, littleEndian)
                        ElseIf fieldType = 4 Then      ' LONG
                            value = CLng(ToU32(entry, 8, littleEndian))
                        Else
                            Continue For
                        End If
                        Select Case tag
                            Case &H112 : result.Origin = ToEncodedOrigin(CInt(value))
                            Case &H100 : result.SensorWidth = CInt(Math.Min(value, Integer.MaxValue))
                            Case &H101 : result.SensorHeight = CInt(Math.Min(value, Integer.MaxValue))
                        End Select
                    Next
                End Using
            Catch
                result.Origin = SKEncodedOrigin.TopLeft
            End Try
            Return result
        End Function

        ' ACHTUNG: die Zwischenwerte MUESSEN Integer sein. In VB.NET ist "Byte << 8" ein No-Op
        ' (das Ergebnis bleibt ein Byte), was hier still falsche Zahlen liefern wuerde - dieselbe
        ' Falle hat schon die Metadatenkopie und die RAW-Vorschau zerlegt.
        Private Shared Function ToU16(buffer As Byte(), offset As Integer, littleEndian As Boolean) As Integer
            Dim a = CInt(buffer(offset))
            Dim b = CInt(buffer(offset + 1))
            Return If(littleEndian, (b << 8) Or a, (a << 8) Or b)
        End Function

        Private Shared Function ToU32(buffer As Byte(), offset As Integer, littleEndian As Boolean) As UInteger
            Dim a = CUInt(buffer(offset))
            Dim b = CUInt(buffer(offset + 1))
            Dim c = CUInt(buffer(offset + 2))
            Dim d = CUInt(buffer(offset + 3))
            Return If(littleEndian,
                      (d << 24) Or (c << 16) Or (b << 8) Or a,
                      (a << 24) Or (b << 16) Or (c << 8) Or d)
        End Function

        Private Shared Function ToEncodedOrigin(exifValue As Integer) As SKEncodedOrigin
            Select Case exifValue
                Case 1 : Return SKEncodedOrigin.TopLeft
                Case 2 : Return SKEncodedOrigin.TopRight
                Case 3 : Return SKEncodedOrigin.BottomRight
                Case 4 : Return SKEncodedOrigin.BottomLeft
                Case 5 : Return SKEncodedOrigin.LeftTop
                Case 6 : Return SKEncodedOrigin.RightTop
                Case 7 : Return SKEncodedOrigin.RightBottom
                Case 8 : Return SKEncodedOrigin.LeftBottom
                Case Else : Return SKEncodedOrigin.TopLeft
            End Select
        End Function

        ''' <summary>Winkel auf 0/90/180/270 normieren - alles andere kann eine Viertel-Drehung nicht
        ''' darstellen und gilt als "keine Drehung".</summary>
        Public Shared Function NormalizeQuarterTurn(degrees As Integer) As Integer
            Dim normalized = ((degrees Mod 360) + 360) Mod 360
            Return If(normalized = 90 OrElse normalized = 180 OrElse normalized = 270, normalized, 0)
        End Function

        ''' <summary>Dreht um ein Vielfaches von 90 Grad. Bei 0 kommt DIESELBE Instanz zurueck (kein
        ''' Alloc) - Aufrufer duerfen das Ergebnis nur disposen, wenn es eine andere Referenz ist.
        ''' Wird fuer die Drehung aus dem RAW-Sidecar gebraucht, die beim Anzeigen ueber die
        ''' EXIF-Korrektur gelegt wird (siehe RawSidecarService.ReadRotationDegrees).</summary>
        Public Shared Function ApplyQuarterRotation(source As SKBitmap, degrees As Integer) As SKBitmap
            If source Is Nothing Then Return source
            Dim normalized = NormalizeQuarterTurn(degrees)
            If normalized = 0 Then Return source

            Dim swapped = normalized = 90 OrElse normalized = 270
            Dim rw = If(swapped, source.Height, source.Width)
            Dim rh = If(swapped, source.Width, source.Height)

            Dim rotated = New SKBitmap(rw, rh, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(rotated)
                canvas.Clear(SKColors.Transparent)
                Select Case normalized
                    Case 90
                        canvas.Translate(rw, 0)
                        canvas.RotateDegrees(90)
                    Case 180
                        canvas.Translate(rw, rh)
                        canvas.RotateDegrees(180)
                    Case 270
                        canvas.Translate(0, rh)
                        canvas.RotateDegrees(270)
                End Select
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return rotated
        End Function

        ''' <summary>Avalonia-Bitmap aus gepufferten Bilddaten - der Rueckfall, wenn keine
        ''' Orientierungskorrektur noetig oder moeglich ist. Eigener Strom je Aufruf, damit der
        ''' Puffer wiederverwendbar bleibt.</summary>
        Private Shared Function BitmapFromData(data As SKData) As Bitmap
            Using ms As New MemoryStream(data.ToArray())
                Return New Bitmap(ms)
            End Using
        End Function

        Public Shared Function LoadOrientedAvaloniaBitmap(filePath As String) As Bitmap
            Using stream = File.OpenRead(filePath)
                Return LoadOrientedAvaloniaBitmap(stream)
            End Using
        End Function

        ''' Wie LoadOrientedAvaloniaBitmap(filePath), erkennt aber Formate, die SkiaSharp nicht
        ''' selbst dekodiert, und reicht stattdessen deren aufbereiteten Strom herein: RAW über
        ''' RawPreviewService.ExtractPreviewWithFallback, PSD/PSB über PsdPreviewService.
        ''' Liefert Nothing, wenn daraus nichts zu holen war.
        '''
        ''' Zur Einordnung der drei RAW-Wege - sie sind bewusst verschieden:
        '''   * ANZEIGE (hier): schnelle eingebettete Vorschau; findet der Scanner nichts, stößt
        '''     ExtractPreviewWithFallback doch noch libraw an (etwa bei Leica-RAWs).
        '''   * RENDER/ARBEITSBILD (ImageProcessor.DecodeOriented): echte Entwicklung über libraw,
        '''     erst danach der Vorschau-Rückfall.
        '''   * SPEICHERN: die RAW-Datei ist nie Ziel - Export in eine neue Datei, Reglerstände ins
        '''     .fpxmp-Sidecar.
        ''' WICHTIG: das ist der ANZEIGE-Pfad des Editors (CurrentImage) - er ist von
        ''' ImageProcessor.OpenSourceStream (Render/Export) getrennt, beide muessen dieselben
        ''' Sonderformate kennen. Genau das fehlte fuer PSD: die Render-Pipeline konnte es,
        ''' der Editor zeigte trotzdem nichts (Nutzerbefund 2026-07-19).
        ''' <paramref name="applySidecarRotation"/>: legt bei RAW die Drehung aus dem .fpxmp-Sidecar
        ''' oben drauf. Richtig fuer jeden Weg, der das Bild EINFACH ANZEIGT (Filmstreifen,
        ''' Konfliktvorschau). FALSCH fuer den Editor: der laedt dasselbe Sidecar als Rezept und
        ''' dreht in seiner Render-Pipeline - beides zusammen ergaebe eine doppelte Drehung.
        Public Shared Function LoadOrientedAvaloniaBitmapAuto(filePath As String, Optional applySidecarRotation As Boolean = True) As Bitmap
            If RawPreviewService.IsSupportedRaw(filePath) Then
                Using stream = RawPreviewService.ExtractPreviewWithFallback(filePath)
                    If stream Is Nothing Then Return Nothing
                    ' Eine im Viewer gedrehte RAW steckt ihre Drehung ins Sidecar, nicht in die Pixel.
                    Dim rotation = If(applySidecarRotation, RawSidecarService.ReadRotationDegrees(filePath), 0)
                    Return LoadOrientedAvaloniaBitmap(stream, rotation, rawContainerPath:=filePath)
                End Using
            End If
            If PsdPreviewService.IsSupportedPsd(filePath) Then
                Using stream = PsdPreviewService.ExtractPreview(filePath)
                    If stream Is Nothing Then Return Nothing
                    Dim psdRotation = If(applySidecarRotation, RawSidecarService.ReadRotationDegrees(filePath), 0)
                    Return LoadOrientedAvaloniaBitmap(stream, psdRotation)
                End Using
            End If
            Return LoadOrientedAvaloniaBitmap(filePath)
        End Function

        ' Kopiert die dekodierten Pixel direkt in eine WriteableBitmap (Bgra8888/Premul passt
        ' exakt zum Decode-Format oben) - kein Kompressions-Umweg über PNG wie zuvor, deutlich
        ' schneller, da hier nur pro Zeile eine reine Speicherkopie stattfindet.
        Friend Shared Function ToAvaloniaBitmapFast(skBitmap As SKBitmap) As Bitmap
            Dim width = skBitmap.Width
            Dim height = skBitmap.Height
            Dim wb = New WriteableBitmap(New PixelSize(width, height), New Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul)
            Using fb = wb.Lock()
                Dim srcStride = skBitmap.RowBytes
                Dim dstStride = fb.RowBytes
                Dim rowBytes = Math.Min(srcStride, dstStride)
                Dim srcBase = skBitmap.GetPixels()
                Dim buffer(rowBytes - 1) As Byte
                For y = 0 To height - 1
                    Marshal.Copy(IntPtr.Add(srcBase, y * srcStride), buffer, 0, rowBytes)
                    Marshal.Copy(buffer, 0, IntPtr.Add(fb.Address, y * dstStride), rowBytes)
                Next
            End Using
            Return wb
        End Function
    End Class

End Namespace
