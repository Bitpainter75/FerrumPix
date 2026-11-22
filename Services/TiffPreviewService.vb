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
    ''' Hier wird nur GELESEN. Geschrieben wird TIFF über <see cref="TiffWriterService"/>, und zwar nur
    ''' als neue Datei: <see cref="ImageProcessor.CanEncodeToTargetExtension"/> bleibt bei TIFF False,
    ''' damit nichts über ein TIFF-Original schreibt, das 16 Bit oder mehrere Seiten tragen kann.
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

        ''' <summary>Obergrenze fuer den Lesepuffer EINER Kachel oder Zeile im Gleitkomma-Leser
        ''' (TryReadLinear). 256 MB reichen fuer eine Kachel von 4096 mal 4096 Punkten mit vier
        ''' Kanaelen in 32 Bit; was mehr meldet, ist kaputt oder boeswillig.</summary>
        Private Const MaxFloatBufferBytes As Long = 256L * 1024L * 1024L

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
            ' Gleitkomma lehnt libtiffs RGBA-Schnittstelle ab; die Datei traegt lineares Licht und
            ' geht wie ein entwickeltes RAW durch die RAW-Grundstufe, hier mit deren Vorgaben.
            If IsFloatTiff(path) Then Return RawDecodeService.TryDecode(path)
            EnsureSilentHandler()
            ' Eine Datei mit 16 Bit, die der tiefe Leser kann, kommt AUS dem tiefen Bild (siehe
            ' ShallowFromDeep). Alle anderen gehen den Weg darunter.
            Dim fromDeep = ShallowFromDeep(path)
            If fromDeep IsNot Nothing Then Return fromDeep
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
                    ' Den Tausch Skia zu ueberlassen wie bei JPEG XL brachte hier nichts (74 gegen
                    ' 77 ms bei 20 MP): die Zeit steckt im Lesen durch libtiff, und das Feld muesste
                    ' dafuer einmal mehr kopiert werden.
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

                        ' Farbmanagement muss HIER geschehen und nicht spaeter: der Leser reicht ein
                        ' Bitmap ohne Profil weiter (ForeignImageDecoder), und ColorManagementService
                        ' bekaeme es danach nie zu sehen. Ein TIFF in Adobe RGB waere sonst der eine
                        ' Dateityp, der weiterhin unverwaltet ankaeme.
                        '
                        ' Das Profil wird FREIGEGEBEN: es ist ein eigens erzeugtes natives Objekt,
                        ' und ueber einen Kachellauf mit tausend Bildern summiert sich das, bis der
                        ' Aufraeumer von selbst vorbeikommt.
                        Using profile = ReadIccProfile(tif)
                            Dim managed = ColorManagementService.ToSrgb(bitmap, profile)
                            If Not Object.ReferenceEquals(managed, bitmap) Then bitmap.Dispose()
                            Return managed
                        End Using
                    Catch
                        bitmap.Dispose()
                        Return Nothing
                    End Try
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ' ---------------------------------------------------------------- 16 Bit

        ''' <summary>Was der tiefe Leser kann, aus den Tags einer geöffneten Datei. Bewusst eng: 16
        ''' Bit ganzzahlig ohne Vorzeichen, RGB oder Graustufen, OHNE weitere Kanäle (kein Alpha),
        ''' zusammenhängend oder in getrennten Ebenen, Streifen oder Kacheln. Alles andere (CMYK,
        ''' Lab, Palette, Gleitkomma, Alpha) bleibt beim 8-Bit-Weg, der es über libtiffs
        ''' RGBA-Schnittstelle liest; dessen Rechnung für Alpha und fremde Farbmodelle hier
        ''' nachzubauen, wäre eine zweite Wahrheit neben der Bibliothek.</summary>
        Private Structure DeepLayout
            Public Width As Integer
            Public Height As Integer
            Public Samples As Integer
            Public Separate As Boolean
            Public Inverted As Boolean
            Public FlipHorizontal As Boolean
            Public FlipVertical As Boolean
        End Structure

        Private Shared Function ReadDeepLayout(tif As Tiff, ByRef layout As DeepLayout) As Boolean
            Dim widthField = tif.GetField(TiffTag.IMAGEWIDTH)
            Dim heightField = tif.GetField(TiffTag.IMAGELENGTH)
            If widthField Is Nothing OrElse heightField Is Nothing Then Return False
            layout.Width = widthField(0).ToInt()
            layout.Height = heightField(0).ToInt()
            If layout.Width <= 0 OrElse layout.Height <= 0 Then Return False
            If CLng(layout.Width) * CLng(layout.Height) > MaxPixels Then Return False

            Dim bits = tif.GetFieldDefaulted(TiffTag.BITSPERSAMPLE)
            If bits Is Nothing OrElse bits(0).ToInt() <> 16 Then Return False
            Dim format = tif.GetFieldDefaulted(TiffTag.SAMPLEFORMAT)
            If format IsNot Nothing AndAlso format(0).ToInt() <> CInt(SampleFormat.UINT) Then Return False
            Dim samples = tif.GetFieldDefaulted(TiffTag.SAMPLESPERPIXEL)
            layout.Samples = If(samples Is Nothing, 1, samples(0).ToInt())
            Dim extra = tif.GetField(TiffTag.EXTRASAMPLES)
            If extra IsNot Nothing AndAlso extra(0).ToInt() > 0 Then Return False

            Dim photometricField = tif.GetField(TiffTag.PHOTOMETRIC)
            If photometricField Is Nothing Then Return False
            Select Case CType(photometricField(0).ToInt(), Photometric)
                Case Photometric.RGB
                    If layout.Samples <> 3 Then Return False
                Case Photometric.MINISBLACK
                    If layout.Samples <> 1 Then Return False
                Case Photometric.MINISWHITE
                    If layout.Samples <> 1 Then Return False
                    layout.Inverted = True
                Case Else
                    Return False
            End Select

            Dim planar = tif.GetFieldDefaulted(TiffTag.PLANARCONFIG)
            layout.Separate = planar IsNot Nothing AndAlso planar(0).ToInt() = CInt(PlanarConfig.SEPARATE) AndAlso layout.Samples > 1

            ' Die Ausrichtung wie libtiffs ReadRGBAImageOriented mit dem Ziel "oben links": die
            ' gestürzten Lagen (5 bis 8) behandelt die Bibliothek wie ihre aufrechten Gegenstücke,
            ' ohne Breite und Höhe zu tauschen. Dasselbe hier, damit beide Bilder deckungsgleich
            ' bleiben.
            Dim orientationField = tif.GetFieldDefaulted(TiffTag.ORIENTATION)
            Dim orientationValue = If(orientationField Is Nothing, 1, orientationField(0).ToInt())
            Select Case orientationValue
                Case 2, 6 : layout.FlipHorizontal = True
                Case 3, 7 : layout.FlipHorizontal = True : layout.FlipVertical = True
                Case 4, 8 : layout.FlipVertical = True
            End Select
            Return True
        End Function

        ''' <summary>Trägt diese Datei 16 Bit je Kanal in einer Form, die sich tief lesen lässt?
        ''' Liest nur die Tags der ersten Seite. Danach richtet sich, ob die Dialoge 16 Bit
        ''' anbieten und ob die Kette ein tiefes Bild bekommt.</summary>
        Public Shared Function IsDeepTiff(path As String) As Boolean
            If Not IsSupportedTiff(path) OrElse Not File.Exists(path) Then Return False
            EnsureSilentHandler()
            Try
                Using tif = Tiff.Open(path, "r")
                    If tif Is Nothing Then Return False
                    Dim layout As DeepLayout
                    Return ReadDeepLayout(tif, layout)
                End Using
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Die erste Seite mit 16 Bit je Kanal (Rgba16161616, deckend, Besitz beim
        ''' Aufrufer) oder Nothing. DAS TIEFE GEGENSTÜCK ZU <see cref="TryDecode"/>: dieselbe
        ''' Seite, dieselbe Ausrichtung, dieselbe Umrechnung des eingebetteten Profils nach sRGB,
        ''' nur ohne den Schritt auf 8 Bit. Das Paar muss deckungsgleich sein, die Kette prüft es
        ''' vor dem Schreiben (ImageProcessor.DeepMatchesShallow).
        '''
        ''' Gelesen wird zeilenweise bzw. kachelweise in der Tiefe der Datei; libtiff gibt die
        ''' Werte in der Byte-Reihenfolge des Rechners zurück.</summary>
        Friend Shared Function TryDecodeDeep(path As String) As SKBitmap
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return Nothing
            EnsureSilentHandler()
            Try
                Using tif = Tiff.Open(path, "r")
                    If tif Is Nothing Then Return Nothing
                    Dim layout As DeepLayout
                    If Not ReadDeepLayout(tif, layout) Then Return Nothing
                    Dim width = layout.Width
                    Dim height = layout.Height
                    Dim planes = If(layout.Separate, layout.Samples, 1)
                    Dim samplesPerPlane = If(layout.Separate, 1, layout.Samples)

                    ' Alle Werte der Seite, Zeile für Zeile, je Bildpunkt Samples Werte.
                    Dim values(width * layout.Samples * height - 1) As UShort
                    If tif.IsTiled() Then
                        Dim tileWidthField = tif.GetField(TiffTag.TILEWIDTH)
                        Dim tileHeightField = tif.GetField(TiffTag.TILELENGTH)
                        If tileWidthField Is Nothing OrElse tileHeightField Is Nothing Then Return Nothing
                        Dim tileWidth = tileWidthField(0).ToInt()
                        Dim tileHeight = tileHeightField(0).ToInt()
                        If tileWidth <= 0 OrElse tileHeight <= 0 Then Return Nothing
                        Dim tile(tif.TileSize() - 1) As Byte
                        For plane = 0 To planes - 1
                            For tileY = 0 To height - 1 Step tileHeight
                                For tileX = 0 To width - 1 Step tileWidth
                                    If tif.ReadTile(tile, 0, tileX, tileY, 0, CShort(plane)) < 0 Then Return Nothing
                                    Dim rows = Math.Min(tileHeight, height - tileY)
                                    Dim columns = Math.Min(tileWidth, width - tileX)
                                    For y = 0 To rows - 1
                                        Dim sourceIndex = y * tileWidth * samplesPerPlane * 2
                                        For x = 0 To columns - 1
                                            For s = 0 To samplesPerPlane - 1
                                                Dim sample = If(layout.Separate, plane, s)
                                                values(((tileY + y) * width + tileX + x) * layout.Samples + sample) =
                                                    BitConverter.ToUInt16(tile, sourceIndex + (x * samplesPerPlane + s) * 2)
                                            Next
                                        Next
                                    Next
                                Next
                            Next
                        Next
                    Else
                        Dim line(tif.ScanlineSize() - 1) As Byte
                        If line.Length < width * samplesPerPlane * 2 Then Return Nothing
                        For plane = 0 To planes - 1
                            For y = 0 To height - 1
                                If Not tif.ReadScanline(line, y, CShort(plane)) Then Return Nothing
                                If Not layout.Separate Then
                                    ' Der Normalfall: die Zeile liegt schon in der Reihenfolge des Felds.
                                    Buffer.BlockCopy(line, 0, values, y * width * layout.Samples * 2, width * layout.Samples * 2)
                                    Continue For
                                End If
                                For x = 0 To width - 1
                                    For s = 0 To samplesPerPlane - 1
                                        Dim sample = If(layout.Separate, plane, s)
                                        values((y * width + x) * layout.Samples + sample) =
                                            BitConverter.ToUInt16(line, (x * samplesPerPlane + s) * 2)
                                    Next
                                Next
                            Next
                        Next
                    End If

                    Dim bitmap = ImageProcessor.NewDeepBitmap(width, height, SKAlphaType.Opaque)
                    If bitmap Is Nothing Then Return Nothing
                    Try
                        Dim row(width * 4 - 1) As Short
                        Dim target = bitmap.GetPixels()
                        Dim targetStride = bitmap.RowBytes
                        For y = 0 To height - 1
                            Dim sourceY = If(layout.FlipVertical, height - 1 - y, y)
                            For x = 0 To width - 1
                                Dim sourceX = If(layout.FlipHorizontal, width - 1 - x, x)
                                Dim index = (sourceY * width + sourceX) * layout.Samples
                                Dim r = values(index)
                                Dim g = If(layout.Samples = 1, r, values(index + 1))
                                Dim b = If(layout.Samples = 1, r, values(index + 2))
                                If layout.Inverted Then
                                    r = CUShort(65535 - r)
                                    g = r
                                    b = r
                                End If
                                ' Dieselbe Bitfolge als Short: Marshal.Copy kennt kein UShort-Feld.
                                row(x * 4) = CShort(If(r >= 32768, CInt(r) - 65536, CInt(r)))
                                row(x * 4 + 1) = CShort(If(g >= 32768, CInt(g) - 65536, CInt(g)))
                                row(x * 4 + 2) = CShort(If(b >= 32768, CInt(b) - 65536, CInt(b)))
                                row(x * 4 + 3) = -1
                            Next
                            Marshal.Copy(row, 0, target + y * targetStride, width * 4)
                        Next

                        Using profile = ReadIccProfile(tif)
                            Dim managed = ColorManagementService.ToSrgb(bitmap, profile)
                            If Not Object.ReferenceEquals(managed, bitmap) Then bitmap.Dispose()
                            Return managed
                        End Using
                    Catch
                        bitmap.Dispose()
                        Return Nothing
                    End Try
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Tiff.TryDecodeDeep", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Das 8-Bit-Bild einer 16-Bit-Datei, gerundet aus ihrem tiefen Bild. Nothing,
        ''' wenn der tiefe Leser die Datei nicht annimmt.
        '''
        ''' WARUM NICHT UEBER LIBTIFF WIE BEI ALLEN ANDEREN. Die Bibliothek schneidet 16 Bit auf 8
        ''' ab (das obere Byte), statt zu runden, und das Profil wurde danach auf den
        ''' abgeschnittenen Werten nach sRGB umgerechnet. Beides zusammen legte das 8-Bit-Bild bis
        ''' zu drei Stufen neben das tiefe (gemessen an einem Verlauf in Adobe RGB), und in glatten
        ''' Flaechen nicht zufaellig, sondern durchgehend in eine Richtung. Die Kette haelt eine
        ''' solche Flaeche fuer bearbeitet und nimmt dort die 8-Bit-Werte (PatchDeepFromShallow):
        ''' gerade kraeftige, glatte Farben eines Scans verloeren ihre Tiefe. Aus dem tiefen Bild
        ''' gerundet liegt jeder Punkt hoechstens eine halbe Stufe daneben, und die Umrechnung des
        ''' Profils geschieht einmal, in 16 Bit.
        '''
        ''' Der Preis ist Speicher: das tiefe Bild (8 Byte je Bildpunkt) liegt kurz neben dem
        ''' 8-Bit-Bild.</summary>
        Private Shared Function ShallowFromDeep(path As String) As SKBitmap
            Dim deep = TryDecodeDeep(path)
            If deep Is Nothing Then Return Nothing
            Using deep
                Dim width = deep.Width
                Dim height = deep.Height
                Dim bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul))
                Try
                    Dim source = deep.GetPixels()
                    Dim target = bitmap.GetPixels()
                    If source = IntPtr.Zero OrElse target = IntPtr.Zero Then
                        bitmap.Dispose()
                        Return Nothing
                    End If
                    Dim sourceStride = deep.RowBytes
                    Dim targetStride = bitmap.RowBytes
                    Threading.Tasks.Parallel.For(0, height,
                        Sub(y)
                            Dim deepRow(width * 4 - 1) As Short
                            Dim row(width * 4 - 1) As Byte
                            Marshal.Copy(source + y * sourceStride, deepRow, 0, width * 4)
                            For x = 0 To width - 1
                                Dim o = x * 4
                                ' Gerundet, nicht abgeschnitten: 65535 wird 255, 128 wird 0, 129 wird 1.
                                row(o) = CByte(((deepRow(o + 2) And &HFFFF) + 128) \ 257)
                                row(o + 1) = CByte(((deepRow(o + 1) And &HFFFF) + 128) \ 257)
                                row(o + 2) = CByte(((deepRow(o) And &HFFFF) + 128) \ 257)
                                row(o + 3) = 255
                            Next
                            Marshal.Copy(row, 0, target + y * targetStride, width * 4)
                        End Sub)
                    Return bitmap
                Catch
                    bitmap.Dispose()
                    Return Nothing
                End Try
            End Using
        End Function

        ''' <summary>Das eingebettete ICC-Profil (TIFF-Tag 34675), oder Nothing.
        '''
        ''' libtiff gibt den Tag als zwei Felder zurueck: Laenge und Datenblock. Fehlt er, kommt
        ''' Nothing zurueck - dann ist die Datei unverwaltet und bleibt es, genau wie bisher.</summary>
        Private Shared Function ReadIccProfile(tif As Tiff) As SKColorSpace
            Try
                Dim field = tif.GetField(TiffTag.ICCPROFILE)
                If field Is Nothing OrElse field.Length < 2 Then Return Nothing
                Dim length = field(0).ToInt()
                If length <= 0 OrElse length > 16 * 1024 * 1024 Then Return Nothing
                Dim bytes = field(1).ToByteArray()
                If bytes Is Nothing OrElse bytes.Length = 0 Then Return Nothing
                Return SKColorSpace.CreateIcc(bytes)
            Catch ex As Exception
                DiagnosticLogService.LogException("Tiff.ReadIccProfile", ex)
                Return Nothing
            End Try
        End Function

        ' ---------------------------------------------------------------- Gleitkomma

        ''' <summary>Je Datei die letzte Antwort von <see cref="IsFloatTiff"/>, mit Groesse und
        ''' Zeitstempel als Schluessel. Gefragt wird sie fuer jedes TIFF an jeder Weiche, weil ein
        ''' TIFF mit Gleitkomma als RAW gilt (RawPreviewService.IsSupportedRaw); geoeffnet werden
        ''' soll die Datei dafuer nur einmal.</summary>
        Private Shared ReadOnly _floatCache As New Collections.Concurrent.ConcurrentDictionary(Of String, (Length As Long, Stamp As Long, IsFloat As Boolean))(StringComparer.Ordinal)
        Private Const FloatCacheLimit As Integer = 4096

        ''' <summary>Traegt die erste Seite Gleitkomma (16, 32 oder 64 Bit, SampleFormat 3) in RGB
        ''' oder Graustufen? Dann ist sie lineares Licht und geht durch die RAW-Grundstufe
        ''' (RawDecodeServiceLinear.vb) statt durch libtiffs RGBA-Schnittstelle, die Gleitkomma
        ''' ablehnt. 24 Bit Gleitkomma (eine Form aus DNG) bleibt aussen vor.</summary>
        Public Shared Function IsFloatTiff(path As String) As Boolean
            If Not IsSupportedTiff(path) Then Return False
            Try
                Dim info = New FileInfo(path)
                If Not info.Exists Then Return False
                Dim stamp = info.LastWriteTimeUtc.Ticks
                Dim cached As (Length As Long, Stamp As Long, IsFloat As Boolean) = Nothing
                If _floatCache.TryGetValue(path, cached) AndAlso cached.Length = info.Length AndAlso cached.Stamp = stamp Then Return cached.IsFloat
                Dim result = False
                EnsureSilentHandler()
                Using tif = Tiff.Open(path, "r")
                    If tif IsNot Nothing Then
                        Dim layout As FloatLayout
                        result = ReadFloatLayout(tif, layout)
                    End If
                End Using
                If _floatCache.Count >= FloatCacheLimit Then _floatCache.Clear()
                _floatCache(path) = (info.Length, stamp, result)
                Return result
            Catch
                Return False
            End Try
        End Function

        Private Structure FloatLayout
            Public Width As Integer
            Public Height As Integer
            Public Bits As Integer
            ''' <summary>Werte je Bildpunkt in der Datei, Farbe und weitere (Alpha) zusammen.</summary>
            Public Samples As Integer
            Public IsGray As Boolean
            Public Separate As Boolean
            Public FlipHorizontal As Boolean
            Public FlipVertical As Boolean
        End Structure

        Private Shared Function ReadFloatLayout(tif As Tiff, ByRef layout As FloatLayout) As Boolean
            Dim format = tif.GetFieldDefaulted(TiffTag.SAMPLEFORMAT)
            If format Is Nothing OrElse format(0).ToInt() <> CInt(SampleFormat.IEEEFP) Then Return False
            Dim bits = tif.GetFieldDefaulted(TiffTag.BITSPERSAMPLE)
            If bits Is Nothing Then Return False
            layout.Bits = bits(0).ToInt()
            If layout.Bits <> 16 AndAlso layout.Bits <> 32 AndAlso layout.Bits <> 64 Then Return False
            Dim widthField = tif.GetField(TiffTag.IMAGEWIDTH)
            Dim heightField = tif.GetField(TiffTag.IMAGELENGTH)
            If widthField Is Nothing OrElse heightField Is Nothing Then Return False
            layout.Width = widthField(0).ToInt()
            layout.Height = heightField(0).ToInt()
            If Not LinearLightImage.SizeAllowed(layout.Width, layout.Height) Then Return False
            Dim samples = tif.GetFieldDefaulted(TiffTag.SAMPLESPERPIXEL)
            layout.Samples = If(samples Is Nothing, 1, samples(0).ToInt())
            Dim photometricField = tif.GetField(TiffTag.PHOTOMETRIC)
            If photometricField Is Nothing Then Return False
            Select Case CType(photometricField(0).ToInt(), Photometric)
                Case Photometric.RGB
                    If layout.Samples < 3 Then Return False
                Case Photometric.MINISBLACK
                    If layout.Samples < 1 Then Return False
                    layout.IsGray = True
                Case Else
                    Return False
            End Select
            If layout.Samples > 16 Then Return False
            Dim planar = tif.GetFieldDefaulted(TiffTag.PLANARCONFIG)
            layout.Separate = planar IsNot Nothing AndAlso planar(0).ToInt() = CInt(PlanarConfig.SEPARATE) AndAlso layout.Samples > 1
            ' Die Ausrichtung wie beim tiefen Leser (ReadDeepLayout): die gestuerzten Lagen wie
            ' ihre aufrechten Gegenstuecke, ohne Breite und Hoehe zu tauschen.
            Dim orientationField = tif.GetFieldDefaulted(TiffTag.ORIENTATION)
            Dim orientationValue = If(orientationField Is Nothing, 1, orientationField(0).ToInt())
            Select Case orientationValue
                Case 2, 6 : layout.FlipHorizontal = True
                Case 3, 7 : layout.FlipHorizontal = True : layout.FlipVertical = True
                Case 4, 8 : layout.FlipVertical = True
            End Select
            Return True
        End Function

        ''' <summary>Die erste Seite eines TIFF mit Gleitkomma als lineares Licht (Rot, Gruen, Blau;
        ''' Grau auf alle drei, weitere Kanaele wie Alpha bleiben weg), oder Nothing. Die Werte gelten
        ''' als lineares sRGB; ein eingebettetes Profil wird nicht angewandt (Gleitkomma-TIFF tragen
        ''' selten eines, und Skia rechnet ein Profil nur auf 8 und 16 Bit ohne Kopfraum).</summary>
        Friend Shared Function TryReadLinear(path As String) As LinearLightImage
            If Not IsSupportedTiff(path) OrElse Not File.Exists(path) Then Return Nothing
            EnsureSilentHandler()
            Try
                Using tif = Tiff.Open(path, "r")
                    If tif Is Nothing Then Return Nothing
                    Dim layout As FloatLayout
                    If Not ReadFloatLayout(tif, layout) Then Return Nothing
                    Dim width = layout.Width
                    Dim height = layout.Height
                    Dim colors = If(layout.IsGray, 1, 3)
                    Dim bytesPerSample = layout.Bits \ 8
                    Dim planes = If(layout.Separate, layout.Samples, 1)
                    Dim samplesPerPlane = If(layout.Separate, 1, layout.Samples)
                    Dim rgb(CInt(CLng(width) * height * 3) - 1) As Single

                    ' Ein Wert aus den Bytes der Datei; libtiff liefert sie in der Reihenfolge des Rechners.
                    Dim ValueAt = Function(data As Byte(), offset As Integer) As Single
                                      Select Case bytesPerSample
                                          Case 2 : Return CSng(BitConverter.ToHalf(data, offset))
                                          Case 4 : Return BitConverter.ToSingle(data, offset)
                                          Case Else : Return CSng(BitConverter.ToDouble(data, offset))
                                      End Select
                                  End Function
                    ' Legt einen Wert an seinen Platz im aufrechten Bild.
                    Dim Store = Sub(x As Integer, y As Integer, sample As Integer, value As Single)
                                    If sample >= colors Then Return
                                    Dim tx = If(layout.FlipHorizontal, width - 1 - x, x)
                                    Dim ty = If(layout.FlipVertical, height - 1 - y, y)
                                    Dim o = (ty * width + tx) * 3
                                    If Not Single.IsFinite(value) Then value = 0.0F
                                    If colors = 1 Then
                                        rgb(o) = value : rgb(o + 1) = value : rgb(o + 2) = value
                                    Else
                                        rgb(o + sample) = value
                                    End If
                                End Sub

                    If tif.IsTiled() Then
                        Dim tileWidthField = tif.GetField(TiffTag.TILEWIDTH)
                        Dim tileHeightField = tif.GetField(TiffTag.TILELENGTH)
                        If tileWidthField Is Nothing OrElse tileHeightField Is Nothing Then Return Nothing
                        Dim tileWidth = tileWidthField(0).ToInt()
                        Dim tileHeight = tileHeightField(0).ToInt()
                        If tileWidth <= 0 OrElse tileHeight <= 0 OrElse CLng(tileWidth) * tileHeight > 64L * 1024 * 1024 Then Return Nothing
                        ' Erst aus den geprueften Feldern rechnen, dann anlegen: eine fremde Datei
                        ' kann eine uebergrosse Kachel melden, und in Integer liefe das Produkt ueber.
                        Dim tileBytes = CLng(tileWidth) * tileHeight * samplesPerPlane * bytesPerSample
                        Dim nativeTileBytes = CLng(tif.TileSize())
                        If tileBytes > MaxFloatBufferBytes OrElse nativeTileBytes < tileBytes OrElse
                           nativeTileBytes > MaxFloatBufferBytes Then Return Nothing
                        Dim tile(CInt(nativeTileBytes) - 1) As Byte
                        For plane = 0 To planes - 1
                            If layout.Separate AndAlso plane >= colors Then Exit For
                            For tileY = 0 To height - 1 Step tileHeight
                                For tileX = 0 To width - 1 Step tileWidth
                                    If tif.ReadTile(tile, 0, tileX, tileY, 0, CShort(plane)) < 0 Then Return Nothing
                                    Dim rows = Math.Min(tileHeight, height - tileY)
                                    Dim columns = Math.Min(tileWidth, width - tileX)
                                    For y = 0 To rows - 1
                                        Dim rowStart = y * tileWidth * samplesPerPlane * bytesPerSample
                                        For x = 0 To columns - 1
                                            For s = 0 To samplesPerPlane - 1
                                                Dim sample = If(layout.Separate, plane, s)
                                                Store(tileX + x, tileY + y, sample, ValueAt(tile, rowStart + (x * samplesPerPlane + s) * bytesPerSample))
                                            Next
                                        Next
                                    Next
                                Next
                            Next
                        Next
                    Else
                        Dim lineBytes = CLng(width) * samplesPerPlane * bytesPerSample
                        Dim nativeLineBytes = CLng(tif.ScanlineSize())
                        If lineBytes > MaxFloatBufferBytes OrElse nativeLineBytes < lineBytes OrElse
                           nativeLineBytes > MaxFloatBufferBytes Then Return Nothing
                        Dim line(CInt(nativeLineBytes) - 1) As Byte
                        For plane = 0 To planes - 1
                            If layout.Separate AndAlso plane >= colors Then Exit For
                            For y = 0 To height - 1
                                If Not tif.ReadScanline(line, y, CShort(plane)) Then Return Nothing
                                For x = 0 To width - 1
                                    For s = 0 To Math.Min(samplesPerPlane, colors) - 1
                                        Dim sample = If(layout.Separate, plane, s)
                                        Store(x, y, sample, ValueAt(line, (x * samplesPerPlane + s) * bytesPerSample))
                                    Next
                                Next
                            Next
                        Next
                    End If
                    Return New LinearLightImage With {
                        .Width = width, .Height = height, .Rgb = rgb,
                        .Description = $"TIFF {layout.Bits} Bit Gleitkomma, {If(layout.IsGray, "Grau", "RGB")}"}
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Tiff.TryReadLinear", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Die Datei als PNG-Strom - die Form, die OpenSourceStream und die
        ''' Thumbnail-Erzeugung erwarten (gleiches Muster wie PSD/ICO/HEIC).</summary>
        Public Shared Function ExtractPreview(path As String) As MemoryStream
            Using bmp = TryDecode(path)
                Return PngEncoder.EncodeToStream(bmp, PngPurpose.Transient)
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
