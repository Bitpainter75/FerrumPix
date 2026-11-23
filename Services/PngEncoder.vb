Imports System
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' <summary>Wofuer ein PNG geschrieben wird. Danach richtet sich, wie stark gepackt wird;
    ''' verlustfrei ist es in jedem Fall.</summary>
    Public Enum PngPurpose
        ''' <summary>Wird sofort wieder gelesen und dann verworfen: die Fremdformate auf dem Weg
        ''' zu Betrachter, Miniatur und Editor, kleine Zwischenbilder. Ungepackt.</summary>
        Transient
        ''' <summary>Bleibt liegen, aber nur fuer die Anwendung selbst: Ebenen, die Retusche-Ebene,
        ''' Masken, Basis- und Anzeigebild im Buendel. Schnell gepackt.</summary>
        Stored
        ''' <summary>Die Datei, die der Nutzer als PNG speichert. Groesse zaehlt, die Zeit
        ''' zahlt er einmal. Alle Filter und zlib 6 wie Skias Vorgabe, bei 8 Bit RGB oder RGBA
        ''' und bei 16 Bit in Streifen auf allen Kernen gepackt, sonst von Skia.</summary>
        Export
    End Enum

    ''' <summary>
    ''' DER EINE WEG, ein PNG zu schreiben. Jede Stelle sagt dabei, wofuer (<see cref="PngPurpose"/>);
    ''' die Packstufe dazu steht nur hier. Eine Pruefung haelt fest, dass niemand daran vorbei
    ''' kodiert. Ausnahmen mit eigenem Kodierer: SVG (die Bibliothek schreibt selbst), HEIF auf
    ''' macOS (ImageIO) und das Standbild aus libmpv.
    '''
    ''' DIE QUALITAETSZAHL WIRKT BEI PNG NICHT. Skia nimmt sie nur fuer JPEG und WebP; bei PNG waren
    ''' 100, 90, 60 und 0 gemessen dieselbe Datei in derselben Zeit. Stellen, die mit "Stufe 60"
    ''' schnell sein wollten, liefen deshalb mit der vollen Vorgabe. Was wirkt, sind zlib-Stufe und
    ''' Filter, und die bestimmen nur Zeit und Groesse: die Bildpunkte sind in jeder Stufe dieselben.
    '''
    ''' Gemessen an einem 18-MP-Foto (Kodieren, Einlesen, Groesse):
    '''   Vorgabe, alle Filter, zlib 6   1,9 s    0,19 s   41,7 MB
    '''   Filter Sub, zlib 1             0,6 s    0,21 s   39,9 MB
    '''   ungepackt                      0,28 s   0,05 s   71,8 MB
    ''' Bei Fotos ist die schnelle Stufe also nicht einmal groesser. Bei glatten Flaechen schon: eine
    ''' fast leere Malebene hatte 427 statt 94 KB. Fuer Dateien der Anwendung ist das der richtige
    ''' Tausch, fuer die Ausgabe des Nutzers nicht. Die Ausgabe packt deshalb stark, aber bei 8 Bit
    ''' RGB oder RGBA und bei 16 Bit mit dem eigenen Schreiber in Streifen auf allen Kernen
    ''' (<see cref="EncodeShallow"/>, <see cref="EncodeDeep"/>).
    '''
    ''' Ungepackt bleibt auf <see cref="TransientUncompressedLimitBytes"/> begrenzt: ein
    ''' MemoryStream endet bei 2 GB, und ein sehr grosses TIFF passte ungepackt nicht mehr hinein.
    ''' Darueber wird auch das Durchreichen schnell gepackt.
    '''
    ''' Kodiert wird ueber das Pixmap, nicht ueber SKImage.FromBitmap: Raster-SKImages verlangen
    ''' Premul oder Opaque, ein Bild mit Alpha liegt aber oft als Unpremul vor (PNG speichert
    ''' geradliniges Alpha, so bleibt es verlustfrei).
    ''' </summary>
    Public NotInheritable Class PngEncoder

        Private Sub New()
        End Sub

        ''' <summary>Rohgroesse, bis zu der zum Durchreichen ungepackt geschrieben wird (512 MB, gut
        ''' 130 MP bei 8 Bit mit Alpha).</summary>
        Friend Const TransientUncompressedLimitBytes As Long = 512L * 1024L * 1024L

        ''' <summary>Das Bild als PNG, oder Nothing. Besitz beim Aufrufer.</summary>
        Public Shared Function Encode(bitmap As SKBitmap, purpose As PngPurpose) As SKData
            If bitmap Is Nothing Then Return Nothing
            ' Ein tiefes Bild schreibt der eigene Schreiber, Skia kodiert diese Anordnung nicht.
            If bitmap.ColorType = SKColorType.Rgba16161616 Then Return EncodeDeep(bitmap)
            Using pixmap = bitmap.PeekPixels()
                Return Encode(pixmap, purpose)
            End Using
        End Function

        ''' <summary>Dasselbe fuer Pixel ohne eigenes Bitmap, etwa den Schnappschuss einer
        ''' Zeichenflaeche (SKImage.PeekPixels). Besitz beim Aufrufer.</summary>
        Public Shared Function Encode(pixmap As SKPixmap, purpose As PngPurpose) As SKData
            If pixmap Is Nothing Then Return Nothing
            ' Die Ausgabe mit 8 Bit packt der eigene Schreiber in Streifen; was er nicht kennt, Skia.
            If purpose = PngPurpose.Export Then
                Dim strips = EncodeShallow(pixmap)
                If strips IsNot Nothing Then Return strips
            End If
            Return pixmap.Encode(OptionsFor(purpose, CLng(pixmap.RowBytes) * pixmap.Height))
        End Function

        ''' <summary>Dasselbe als Speicherstrom, an den Anfang gespult, oder Nothing.</summary>
        Public Shared Function EncodeToStream(bitmap As SKBitmap, purpose As PngPurpose) As MemoryStream
            Using data = Encode(bitmap, purpose)
                If data Is Nothing Then Return Nothing
                Dim ms As New MemoryStream()
                data.SaveTo(ms)
                ms.Position = 0
                Return ms
            End Using
        End Function

        ''' <summary>Dasselbe als Bytes, oder Nothing.</summary>
        Public Shared Function EncodeToBytes(bitmap As SKBitmap, purpose As PngPurpose) As Byte()
            Using data = Encode(bitmap, purpose)
                Return data?.ToArray()
            End Using
        End Function

        ''' <summary>Ein tiefes Bild (Rgba16161616, ImageProcessorDeep.vb) als PNG mit 16 Bit je
        ''' Kanal, oder Nothing. Besitz beim Aufrufer.
        '''
        ''' EIGENER SCHREIBER, weil Skia diese Anordnung nicht als PNG kodiert (das Ergebnis ist
        ''' leer). Der Umweg ueber halbe Gleitkomma, die Skia mit 16 Bit schreibt, truege nur rund
        ''' elf Bit. Hier gehen die Werte unveraendert hinein: zeilenweise ohne vormultipliziertes
        ''' Alpha gelesen, je Zeile der Filter mit der kleinsten Betragssumme (die uebliche
        ''' Heuristik), gepackt mit zlib. Der Alphakanal kommt wie beim TIFF nur mit, wenn das Bild
        ''' wirklich Transparenz traegt.
        '''
        ''' GEPACKT WIRD IN STREIFEN, PARALLEL (<see cref="StripEncoding"/>), und doch entsteht EIN
        ''' gueltiger zlib-Strom: der Kopf von hier, dann je Streifen ein roher Deflate-Teil, der
        ''' mit einem Sync-Flush auf einer Bytegrenze endet (nur der letzte schliesst mit dem
        ''' Endblock), am Schluss die Adler-32 ueber alles, zusammengesetzt aus den Summen der
        ''' Streifen. Ein Leser sieht davon nichts; die Teile verweisen nur nicht ueber ihre Grenze
        ''' zurueck. Die Filterwahl je Zeile haengt nur an der Zeile und der davor, und die liest
        ''' jeder Streifen selbst mit: die gefilterten Zeilen sind dieselben wie in einem Durchgang.</summary>
        Public Shared Function EncodeDeep(bitmap As SKBitmap) As SKData
            If bitmap Is Nothing OrElse bitmap.ColorType <> SKColorType.Rgba16161616 Then Return Nothing
            Dim width = bitmap.Width, height = bitmap.Height
            If width <= 0 OrElse height <= 0 Then Return Nothing
            Using pixmap = bitmap.PeekPixels()
                If pixmap Is Nothing Then Return Nothing
                Dim hasAlpha = bitmap.AlphaType <> SKAlphaType.Opaque AndAlso
                               StripEncoding.HasTransparency(pixmap, deep:=True)

                Using head As New MemoryStream()
                    head.Write(PngSignature, 0, PngSignature.Length)
                    Dim header(12) As Byte
                    WriteBigEndian(header, 0, width)
                    WriteBigEndian(header, 4, height)
                    header(8) = 16
                    header(9) = CByte(If(hasAlpha, 6, 2))
                    WriteChunk(head, "IHDR", header, header.Length)
                    Return EncodeStrips(pixmap, True, hasAlpha, head.ToArray())
                End Using
            End Using
        End Function

        ''' <summary>Die Ausgabe mit 8 Bit (Bgra8888 oder Rgba8888) als PNG, gepackt in Streifen wie
        ''' <see cref="EncodeDeep"/>, oder Nothing, wenn der Weg dieses Bild nicht kennt; dann
        ''' kodiert Skia wie vorher. Besitz beim Aufrufer.
        '''
        ''' DIESELBE DATEI WIE SKIA, NUR SCHNELLER. Skia kodierte die Ausgabe mit allen Filtern und
        ''' zlib 6 auf einem Kern (61 MP: rund 7 s). Alles vor den Bilddaten kommt deshalb aus Skia
        ''' selbst (<see cref="SkiaHeader"/>): Farbart (RGB fuer deckend, RGBA sonst, nach der
        ''' Alpha-Art des Bildes und nicht nach seinem Inhalt), sBIT und das Farbprofil (sRGB oder
        ''' iCCP, ohne Farbraum keines). Die Bildpunkte liest Skia ohne vormultipliziertes Alpha wie
        ''' bei 16 Bit; ein vormultipliziertes Bild rechnet der Weg selbst zurueck, und zwar genau
        ''' wie Skias Kodierer (<see cref="BuildUnpremultiplyTable"/>). Die Filterwahl ist dieselbe
        ''' Heuristik wie bei 16 Bit (kleinste Betragssumme, so waehlt auch libpng mit allen
        ''' Filtern), gepackt mit <see cref="ShallowDeflateOptions"/>. Was ImageProcessorSave danach
        ''' in die Datei setzt (eXIf, iTXt, pHYs und so weiter), haengt am Aufbau der Datei und nicht
        ''' am Kodierer.</summary>
        Private Shared Function EncodeShallow(pixmap As SKPixmap) As SKData
            If pixmap.ColorType <> SKColorType.Bgra8888 AndAlso pixmap.ColorType <> SKColorType.Rgba8888 Then Return Nothing
            If pixmap.AlphaType = SKAlphaType.Unknown Then Return Nothing
            Dim width = pixmap.Width, height = pixmap.Height
            If width <= 0 OrElse height <= 0 Then Return Nothing
            ' Die Datei entsteht in einem MemoryStream, und der endet bei 2 GB; so grosse Bilder
            ' bleiben bei Skia.
            If CLng(width * 4L + 1L) * height > ShallowLimitBytes Then Return Nothing
            Dim hasAlpha As Boolean
            Dim head = SkiaHeader(pixmap.Info, hasAlpha)
            If head Is Nothing Then Return Nothing
            Dim unpremultiply As Byte() = Nothing
            If pixmap.AlphaType = SKAlphaType.Premul Then
                unpremultiply = If(pixmap.ColorType = SKColorType.Bgra8888, _unpremultiplyBgra.Value, _unpremultiplyRgba.Value)
                If unpremultiply Is Nothing Then Return Nothing
            End If
            Return EncodeStrips(pixmap, False, hasAlpha, head, unpremultiply)
        End Function

        Private Shared ReadOnly _unpremultiplyBgra As New Lazy(Of Byte())(Function() BuildUnpremultiplyTable(SKColorType.Bgra8888))
        Private Shared ReadOnly _unpremultiplyRgba As New Lazy(Of Byte())(Function() BuildUnpremultiplyTable(SKColorType.Rgba8888))

        ''' <summary>Wie Skias PNG-Kodierer einen vormultiplizierten Wert zurueckrechnet, als Tafel
        ''' (Index Alpha mal 256 plus Wert), oder Nothing.
        '''
        ''' NICHT SELBST GERECHNET, SONDERN BEI SKIA ABGELESEN. Skias Kodierer rechnet anders zurueck
        ''' als SKPixmap.ReadPixels: an 130 der 32 385 gueltigen Paare um eins (etwa Wert 5 bei Alpha
        ''' 6: 213 gegen 212). Die Tafel entsteht deshalb einmal je Programmlauf aus Skias Kodierung
        ''' aller 65 536 Paare, gelesen ohne Umrechnung; so gilt sie auch dort, wo Skia auf einer
        ''' anderen Plattform anders rundet. Liefert das Lesen nicht dasselbe Alpha oder nicht in
        ''' allen drei Farben denselben Wert zurueck, gibt es keine Tafel, und vormultiplizierte
        ''' Bilder kodiert weiter Skia. Je Anordnung der Kanaele eine eigene Tafel, weil Skia sie
        ''' auf eigenen Wegen umsetzt.</summary>
        Private Shared Function BuildUnpremultiplyTable(colorType As SKColorType) As Byte()
            Try
                Dim encoded As Byte()
                Using probe As New SKBitmap(New SKImageInfo(256, 256, colorType, SKAlphaType.Premul))
                    Dim source(256 * 256 * 4 - 1) As Byte
                    For alpha = 0 To 255
                        For value = 0 To 255
                            Dim i = (alpha * 256 + value) * 4
                            ' Alle drei Farben gleich: die Anordnung der Kanaele aendert die Bytes
                            ' nicht, Alpha steht in beiden zuletzt.
                            source(i) = CByte(value) : source(i + 1) = CByte(value) : source(i + 2) = CByte(value)
                            source(i + 3) = CByte(alpha)
                        Next
                    Next
                    If probe.RowBytes <> 256 * 4 Then Return Nothing
                    Runtime.InteropServices.Marshal.Copy(source, 0, probe.GetPixels(), source.Length)
                    Using pixels = probe.PeekPixels()
                        Using data = pixels.Encode(OptionsFor(PngPurpose.Export, source.Length))
                            If data Is Nothing Then Return Nothing
                            encoded = data.ToArray()
                        End Using
                    End Using
                End Using
                Dim stored(256 * 256 * 4 - 1) As Byte
                Using codec = SKCodec.Create(New MemoryStream(encoded))
                    If codec Is Nothing Then Return Nothing
                    Dim info = New SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Unpremul)
                    Dim handle = Runtime.InteropServices.GCHandle.Alloc(stored, Runtime.InteropServices.GCHandleType.Pinned)
                    Try
                        If codec.GetPixels(info, handle.AddrOfPinnedObject()) <> SKCodecResult.Success Then Return Nothing
                    Finally
                        handle.Free()
                    End Try
                End Using
                Dim table(256 * 256 - 1) As Byte
                For alpha = 0 To 255
                    For value = 0 To 255
                        Dim i = (alpha * 256 + value) * 4
                        If stored(i + 3) <> alpha OrElse stored(i + 1) <> stored(i) OrElse stored(i + 2) <> stored(i) Then Return Nothing
                        table(alpha * 256 + value) = stored(i)
                    Next
                Next
                Return table
            Catch ex As Exception
                DiagnosticLogService.LogException("PngEncoder.UnpremultiplyTable", ex)
                Return Nothing
            End Try
        End Function

        ''' Rohgroesse, bis zu der die Ausgabe mit 8 Bit in Streifen gepackt wird (1 GB, gut 260 MP).
        Private Const ShallowLimitBytes As Long = 1024L * 1024L * 1024L

        ''' <summary>Signatur und alle Abschnitte vor den Bilddaten, so wie Skia sie fuer ein Bild
        ''' dieser Art schreibt, mit Breite und Hoehe des Bildes; dazu, ob Skia Alpha schreibt.
        ''' Gewonnen aus Skias Kodierung EINES Bildpunkts derselben Farbart, Alpha-Art und desselben
        ''' Farbraums. Nothing, wenn Skia etwas anderes als 8 Bit RGB oder RGBA ohne Zeilensprung
        ''' schreiben wuerde; dann bleibt das ganze Bild bei Skia.</summary>
        Private Shared Function SkiaHeader(info As SKImageInfo, ByRef hasAlpha As Boolean) As Byte()
            Dim encoded As Byte()
            Using probe As New SKBitmap(New SKImageInfo(1, 1, info.ColorType, info.AlphaType, info.ColorSpace))
                If probe.GetPixels() = IntPtr.Zero Then Return Nothing
                probe.Erase(SKColors.Transparent)
                Using pixels = probe.PeekPixels()
                    If pixels Is Nothing Then Return Nothing
                    Using data = pixels.Encode(OptionsFor(PngPurpose.Export, 4))
                        If data Is Nothing Then Return Nothing
                        encoded = data.ToArray()
                    End Using
                End Using
            End Using
            If encoded.Length < PngSignature.Length Then Return Nothing
            For i = 0 To PngSignature.Length - 1
                If encoded(i) <> PngSignature(i) Then Return Nothing
            Next
            Using head As New MemoryStream()
                head.Write(PngSignature, 0, PngSignature.Length)
                Dim offset = PngSignature.Length
                Dim sawHeader = False
                Do
                    If offset + 12 > encoded.Length Then Return Nothing
                    Dim length = (CInt(encoded(offset)) << 24) Or (CInt(encoded(offset + 1)) << 16) Or
                                 (CInt(encoded(offset + 2)) << 8) Or CInt(encoded(offset + 3))
                    If length < 0 OrElse offset + 12 + length > encoded.Length Then Return Nothing
                    Dim kind = Text.Encoding.ASCII.GetString(encoded, offset + 4, 4)
                    If kind = "IDAT" Then Exit Do
                    If kind = "IHDR" Then
                        If sawHeader OrElse length <> 13 Then Return Nothing
                        ' Tiefe 8, Farbart 2 oder 6, Packart, Filterart und Zeilensprung 0.
                        Dim colorType = encoded(offset + 17)
                        If encoded(offset + 16) <> 8 OrElse (colorType <> 2 AndAlso colorType <> 6) OrElse
                           encoded(offset + 18) <> 0 OrElse encoded(offset + 19) <> 0 OrElse encoded(offset + 20) <> 0 Then Return Nothing
                        hasAlpha = colorType = 6
                        Dim header(12) As Byte
                        Array.Copy(encoded, offset + 8, header, 0, 13)
                        WriteBigEndian(header, 0, info.Width)
                        WriteBigEndian(header, 4, info.Height)
                        WriteChunk(head, "IHDR", header, header.Length)
                        sawHeader = True
                    ElseIf Not sawHeader Then
                        Return Nothing
                    Else
                        ' Alles Weitere (sBIT, sRGB, iCCP, ...) Byte fuer Byte, samt Pruefsumme.
                        head.Write(encoded, offset, 12 + length)
                    End If
                    offset += 12 + length
                Loop
                If Not sawHeader Then Return Nothing
                Return head.ToArray()
            End Using
        End Function

        ''' <summary>Schreibt das PNG: <paramref name="head"/> (Signatur und die Abschnitte vor den
        ''' Bilddaten), dann die Bilddaten in Streifen gefiltert und gepackt, dann IEND. Mit
        ''' <paramref name="deep"/> 16 Bit je Wert, sonst 8. Mit <paramref name="unpremultiply"/>
        ''' werden die Werte vormultipliziert gelesen und ueber diese Tafel zurueckgerechnet.
        '''
        ''' GEPACKT WIRD IN STREIFEN, PARALLEL (<see cref="StripEncoding"/>), und doch entsteht EIN
        ''' gueltiger zlib-Strom: der Kopf von hier, dann je Streifen ein roher Deflate-Teil, der
        ''' mit einem Sync-Flush auf einer Bytegrenze endet (nur der letzte schliesst mit dem
        ''' Endblock), am Schluss die Adler-32 ueber alles, zusammengesetzt aus den Summen der
        ''' Streifen. Ein Leser sieht davon nichts; die Teile verweisen nur nicht ueber ihre Grenze
        ''' zurueck. Die Filterwahl je Zeile haengt nur an der Zeile und der davor, und die liest
        ''' jeder Streifen selbst mit: die gefilterten Zeilen sind dieselben wie in einem Durchgang.</summary>
        Private Shared Function EncodeStrips(pixmap As SKPixmap, deep As Boolean, hasAlpha As Boolean,
                                             head As Byte(), Optional unpremultiply As Byte() = Nothing) As SKData
            Dim width = pixmap.Width, height = pixmap.Height
            Dim rowsPerStrip = StripRows(width, deep, hasAlpha)
            If rowsPerStrip = 0 Then Return Nothing
            ' Die Puffer fassen nur so viele Zeilen, wie ein Streifen wirklich hat: ein flaches
            ' Panorama braucht keinen Platz fuer 64.
            Dim bufferRows = Math.Min(rowsPerStrip, height)
            Using output As New MemoryStream()
                output.Write(head, 0, head.Length)
                Using idat As New IdatStream(output)
                    Dim zlibHeader = If(deep, ZlibHeaderDefault, ZlibHeaderStrong)
                    idat.Write(zlibHeader, 0, zlibHeader.Length)
                    Dim adler = 1UI
                    Dim stripCount = StripEncoding.StripCount(height, rowsPerStrip)
                    Dim outputs As New StripEncoding.PackedBuffers(
                        bufferRows * (CLng(width) * PngPixelBytes(deep, hasAlpha) + 1))
                    Dim ok = StripEncoding.EncodeInOrder(Of StripBuffers, PackedStrip)(
                        stripCount,
                        Function() New StripBuffers(width, deep, hasAlpha, bufferRows),
                        Function(buffers, strip) PackStrip(pixmap, strip, rowsPerStrip, deep, hasAlpha, strip = stripCount - 1,
                                                           unpremultiply, buffers, outputs),
                        Function(strip, packed)
                            idat.Write(packed.Data.GetBuffer(), 0, packed.Length)
                            outputs.Return(packed.Data)
                            adler = StripEncoding.CombineAdler32(adler, packed.Adler, packed.RawLength)
                            Return True
                        End Function)
                    If Not ok Then Return Nothing
                    ' Die Pruefsumme steht wie alle Zahlen im zlib-Strom mit dem hohen Byte zuerst.
                    Dim tail = {CByte(adler >> 24), CByte((adler >> 16) And &HFFUI),
                                CByte((adler >> 8) And &HFFUI), CByte(adler And &HFFUI)}
                    idat.Write(tail, 0, 4)
                End Using
                WriteChunk(output, "IEND", Array.Empty(Of Byte)(), 0)
                Return SKData.CreateCopy(output.GetBuffer(), CULng(output.Length))
            End Using
        End Function

        ''' <summary>Zeilen je Streifen fuer ein Bild dieser Breite, oder 0, wenn nicht einmal eine
        ''' Zeile in einen Puffer passt (<see cref="StripEncoding.RowsPerStripFor"/>). Der groessere
        ''' der beiden Puffer zaehlt: RGBA samt der Zeile davor und der gepackte Streifen.</summary>
        Friend Shared Function StripRows(width As Integer, deep As Boolean, hasAlpha As Boolean) As Integer
            Dim sourcePixelBytes = If(deep, 8L, 4L)
            Dim pixelBytes = CLng(PngPixelBytes(deep, hasAlpha))
            Return StripEncoding.RowsPerStripFor(
                Function(r) Math.Max((r + 1L) * width * sourcePixelBytes,
                                     StripEncoding.PackedBound(r * (width * pixelBytes + 1L))))
        End Function

        ''' Bytes je Bildpunkt im PNG: drei oder vier Werte zu einem oder zwei Byte.
        Private Shared Function PngPixelBytes(deep As Boolean, hasAlpha As Boolean) As Integer
            Return If(hasAlpha, 4, 3) * If(deep, 2, 1)
        End Function

        ''' <summary>Ein gepackter Streifen des PNG: die Bytes des Deflate-Teils
        ''' (<see cref="Length"/> gilt, der Puffer kann laenger sein), die Adler-32 der gefilterten
        ''' Zeilen und deren Laenge.</summary>
        Private NotInheritable Class PackedStrip
            Public Data As MemoryStream
            Public Length As Integer
            Public Adler As UInteger
            Public RawLength As Long
        End Class

        ''' Kopf des zlib-Stroms: Deflate mit 32-KB-Fenster, Vorgabestufe, ohne Woerterbuch.
        Private Shared ReadOnly ZlibHeaderDefault As Byte() = {&H78, &H9C}
        ''' Derselbe Kopf mit der Angabe "starke Stufe" (ab 7, <see cref="ShallowDeflateOptions"/>).
        ''' Die Angabe ist nur Auskunft, entpackt wird beides gleich.
        Private Shared ReadOnly ZlibHeaderStrong As Byte() = {&H78, &HDA}

        ''' <summary>Die Packstufe der Ausgabe mit 8 Bit: zlib 7 fuer gefilterte Daten. Skia packt
        ''' mit zlib 6 (die zlib von libpng, gefilterte Zeilen gehen dort mit der Strategie fuer
        ''' gefilterte Daten hinein); die zlib von .NET packt auf Stufe 6 gemessen gut 5 Prozent
        ''' groesser und laesst dort die Strategie unbeachtet. Mit Stufe 7 und dieser Strategie ist
        ''' die Datei so gross wie die von Skia (61 MP: 89,1 gegen 89,2 MB) und das Packen auf
        ''' allen Kernen kostet 0,54 statt 0,37 s; Stufe 9 kostete doppelt so viel und packte nicht
        ''' kleiner. Die 16 Bit bleiben bei der Vorgabestufe, dort war die Groesse schon gleich.</summary>
        Private Shared Function ShallowDeflateOptions() As Compression.ZLibCompressionOptions
            Return New Compression.ZLibCompressionOptions With {
                .CompressionLevel = 7,
                .CompressionStrategy = Compression.ZLibCompressionStrategy.Filtered}
        End Function

        ''' <summary>Filtert und packt einen Streifen von <paramref name="rowsPerStrip"/>
        ''' Zeilen. Die Zeile vor dem Streifen wird mitgelesen, weil die Filter sie brauchen; vor der
        ''' ersten Zeile des Bildes steht eine Zeile aus Nullen, wie es PNG vorsieht. Gerechnet wird
        ''' in den Puffern des Kerns (<paramref name="buffers"/>), gepackt in einen Puffer aus
        ''' <paramref name="outputs"/>. Nothing, wenn Skia nicht lesen kann.</summary>
        Private Shared Function PackStrip(pixmap As SKPixmap, strip As Integer, rowsPerStrip As Integer, deep As Boolean,
                                          hasAlpha As Boolean, isLast As Boolean, unpremultiply As Byte(),
                                          buffers As StripBuffers, outputs As StripEncoding.PackedBuffers) As PackedStrip
            Dim width = pixmap.Width
            Dim firstRow = strip * rowsPerStrip
            Dim rows = Math.Min(rowsPerStrip, pixmap.Height - firstRow)
            Dim readFrom = If(firstRow > 0, firstRow - 1, 0)
            Dim readRows = rows + (firstRow - readFrom)
            Dim sourceRowBytes = width * If(deep, 8, 4)
            Dim pixelBytes = PngPixelBytes(deep, hasAlpha)
            Dim rowBytes = width * pixelBytes
            Dim rgba = buffers.Rgba
            If unpremultiply Is Nothing Then
                If Not StripEncoding.ReadRows(pixmap, readFrom, readRows, deep, rgba) Then Return Nothing
            Else
                If Not StripEncoding.ReadRows(pixmap, readFrom, readRows, deep, rgba, SKAlphaType.Premul) Then Return Nothing
                Dim stopAt = readRows * sourceRowBytes
                For i = 0 To stopAt - 1 Step 4
                    Dim row = CInt(rgba(i + 3)) << 8
                    rgba(i) = unpremultiply(row Or rgba(i))
                    rgba(i + 1) = unpremultiply(row Or rgba(i + 1))
                    rgba(i + 2) = unpremultiply(row Or rgba(i + 2))
                Next
            End If
            Dim current = buffers.Current
            Dim previous = buffers.Previous
            Dim candidates = buffers.Candidates
            If firstRow > 0 Then
                ToPngRow(rgba, 0, previous, width, deep, hasAlpha)
            Else
                Array.Clear(previous)
            End If

            Dim deflated = outputs.Rent()
            Dim adler = 1UI
            Dim length As Long
            Using deflate = If(deep,
                               New Compression.DeflateStream(deflated, Compression.CompressionLevel.Optimal, leaveOpen:=True),
                               New Compression.DeflateStream(deflated, ShallowDeflateOptions(), leaveOpen:=True))
                For row = 0 To rows - 1
                    ToPngRow(rgba, (row + firstRow - readFrom) * sourceRowBytes, current, width, deep, hasAlpha)
                    Dim best = FilterRow(current, previous, pixelBytes, candidates)
                    deflate.Write(candidates(best), 0, rowBytes + 1)
                    adler = StripEncoding.UpdateAdler32(adler, candidates(best), 0, rowBytes + 1)
                    Dim swap = previous
                    previous = current
                    current = swap
                Next
                ' Sync-Flush: alles bisher Geschriebene steht vollstaendig und endet auf einer
                ' Bytegrenze. Was das Schliessen danach anhaengt, ist nur noch der leere
                ' Endblock, und den traegt allein der letzte Streifen.
                deflate.Flush()
                length = deflated.Length
            End Using
            If isLast Then length = deflated.Length
            Return New PackedStrip With {.Data = deflated, .Length = CInt(length), .Adler = adler,
                                         .RawLength = CLng(rows) * (rowBytes + 1)}
        End Function

        ''' <summary>Die Arbeitspuffer eines Kerns fuer <see cref="PackStrip"/>, einmal angelegt
        ''' und fuer alle seine Streifen benutzt: ein Streifen RGBA von <paramref name="rows"/> Zeilen
        ''' samt der Zeile davor, die aktuelle und die vorige Zeile in der Anordnung des PNG und die
        ''' fuenf gefilterten Fassungen einer Zeile, je mit ihrem Filterbyte vorweg.</summary>
        Private NotInheritable Class StripBuffers
            Public ReadOnly Rgba As Byte()
            Public ReadOnly Current As Byte()
            Public ReadOnly Previous As Byte()
            Public ReadOnly Candidates As Byte()()

            Public Sub New(width As Integer, deep As Boolean, hasAlpha As Boolean, rows As Integer)
                Dim rowBytes = width * PngPixelBytes(deep, hasAlpha)
                Rgba = New Byte((rows + 1) * width * If(deep, 8, 4) - 1) {}
                Current = New Byte(rowBytes - 1) {}
                Previous = New Byte(rowBytes - 1) {}
                Candidates = New Byte(4)() {}
                For f = 0 To 4
                    Candidates(f) = New Byte(rowBytes) {}
                    Candidates(f)(0) = CByte(f)
                Next
            End Sub
        End Class

        ''' <summary>Eine gelesene Zeile (RGBA) in der Anordnung des PNG, ohne Alpha nur die drei
        ''' Farbwerte. Mit 16 Bit kommt das niedrige Byte zuerst und geht mit dem hohen zuerst
        ''' hinaus.</summary>
        Private Shared Sub ToPngRow(rgba As Byte(), offset As Integer, current As Byte(), width As Integer,
                                    deep As Boolean, hasAlpha As Boolean)
            If deep Then
                ToBigEndianRow(rgba, offset, current, width, hasAlpha)
            ElseIf hasAlpha Then
                Buffer.BlockCopy(rgba, offset, current, 0, width * 4)
            Else
                Dim t = 0
                Dim stopAt = offset + width * 4
                For s = offset To stopAt - 1 Step 4
                    current(t) = rgba(s) : current(t + 1) = rgba(s + 1) : current(t + 2) = rgba(s + 2)
                    t += 3
                Next
            End If
        End Sub

        ''' <summary>Eine gelesene Zeile (RGBA mit 16 Bit, niedriges Byte zuerst) in der
        ''' Anordnung des PNG: das hohe Byte zuerst, ohne Alpha nur die drei Farbwerte.</summary>
        Private Shared Sub ToBigEndianRow(rgba As Byte(), offset As Integer, current As Byte(), width As Integer,
                                          hasAlpha As Boolean)
            Dim pixelBytes = If(hasAlpha, 8, 6)
            Dim t = 0
            Dim stopAt = offset + width * 8
            For s = offset To stopAt - 1 Step 8
                current(t) = rgba(s + 1) : current(t + 1) = rgba(s)
                current(t + 2) = rgba(s + 3) : current(t + 3) = rgba(s + 2)
                current(t + 4) = rgba(s + 5) : current(t + 5) = rgba(s + 4)
                If hasAlpha Then
                    current(t + 6) = rgba(s + 7) : current(t + 7) = rgba(s + 6)
                End If
                t += pixelBytes
            Next
        End Sub

        Private Shared ReadOnly PngSignature As Byte() = {137, 80, 78, 71, 13, 10, 26, 10}

        ''' <summary>Rechnet die fuenf PNG-Filter (keiner, Sub, Up, Average, Paeth) fuer eine Zeile
        ''' nach <paramref name="candidates"/> (je ein Filterbyte vorweg) und liefert den mit der
        ''' kleinsten Summe der Betraege, gelesen als Zahlen mit Vorzeichen.</summary>
        Private Shared Function FilterRow(current As Byte(), previous As Byte(), pixelBytes As Integer,
                                          candidates As Byte()()) As Integer
            Dim length = current.Length
            Dim none = candidates(0), subRow = candidates(1), upRow = candidates(2),
                averageRow = candidates(3), paethRow = candidates(4)
            Dim sumNone = 0L, sumSub = 0L, sumUp = 0L, sumAverage = 0L, sumPaeth = 0L
            For i = 0 To length - 1
                Dim x = CInt(current(i))
                Dim a = If(i >= pixelBytes, CInt(current(i - pixelBytes)), 0)
                Dim b = CInt(previous(i))
                Dim c = If(i >= pixelBytes, CInt(previous(i - pixelBytes)), 0)
                Dim p = a + b - c
                Dim pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c)
                Dim nearest = If(pa <= pb AndAlso pa <= pc, a, If(pb <= pc, b, c))
                Dim v = x
                none(i + 1) = CByte(v)
                sumNone += If(v < 128, v, 256 - v)
                v = (x - a) And 255
                subRow(i + 1) = CByte(v)
                sumSub += If(v < 128, v, 256 - v)
                v = (x - b) And 255
                upRow(i + 1) = CByte(v)
                sumUp += If(v < 128, v, 256 - v)
                v = (x - ((a + b) >> 1)) And 255
                averageRow(i + 1) = CByte(v)
                sumAverage += If(v < 128, v, 256 - v)
                v = (x - nearest) And 255
                paethRow(i + 1) = CByte(v)
                sumPaeth += If(v < 128, v, 256 - v)
            Next
            Dim best = 0
            Dim bestSum = sumNone
            If sumSub < bestSum Then
                best = 1 : bestSum = sumSub
            End If
            If sumUp < bestSum Then
                best = 2 : bestSum = sumUp
            End If
            If sumAverage < bestSum Then
                best = 3 : bestSum = sumAverage
            End If
            If sumPaeth < bestSum Then best = 4
            Return best
        End Function

        Private Shared Sub WriteBigEndian(target As Byte(), offset As Integer, value As Integer)
            target(offset) = CByte((value >> 24) And 255)
            target(offset + 1) = CByte((value >> 16) And 255)
            target(offset + 2) = CByte((value >> 8) And 255)
            target(offset + 3) = CByte(value And 255)
        End Sub

        Private Shared ReadOnly CrcTable As UInteger() = BuildCrcTable()

        Private Shared Function BuildCrcTable() As UInteger()
            Dim table(255) As UInteger
            For n = 0 To 255
                Dim c = CUInt(n)
                For k = 0 To 7
                    c = If((c And 1UI) <> 0UI, &HEDB88320UI Xor (c >> 1), c >> 1)
                Next
                table(n) = c
            Next
            Return table
        End Function

        Private Shared Function UpdateCrc(crc As UInteger, data As Byte(), count As Integer) As UInteger
            For i = 0 To count - 1
                crc = CrcTable(CInt((crc Xor data(i)) And &HFFUI)) Xor (crc >> 8)
            Next
            Return crc
        End Function

        ''' <summary>Ein PNG-Abschnitt: Laenge, Kennung, Inhalt, Pruefsumme ueber Kennung und Inhalt.</summary>
        Private Shared Sub WriteChunk(output As Stream, kind As String, data As Byte(), count As Integer)
            Dim head(7) As Byte
            WriteBigEndian(head, 0, count)
            For i = 0 To 3
                head(4 + i) = CByte(AscW(kind(i)))
            Next
            output.Write(head, 0, 8)
            If count > 0 Then output.Write(data, 0, count)
            Dim kindBytes = {head(4), head(5), head(6), head(7)}
            Dim crc = UpdateCrc(UpdateCrc(&HFFFFFFFFUI, kindBytes, 4), data, count) Xor &HFFFFFFFFUI
            Dim tail(3) As Byte
            tail(0) = CByte((crc >> 24) And &HFFUI)
            tail(1) = CByte((crc >> 16) And &HFFUI)
            tail(2) = CByte((crc >> 8) And &HFFUI)
            tail(3) = CByte(crc And &HFFUI)
            output.Write(tail, 0, 4)
        End Sub

        ''' <summary>Nimmt den gepackten Datenstrom entgegen und schreibt ihn in IDAT-Abschnitten
        ''' fester Groesse weiter, damit er nicht erst als Ganzes im Speicher liegen muss.</summary>
        Private NotInheritable Class IdatStream
            Inherits Stream

            Private Const ChunkBytes As Integer = 1 << 16
            Private ReadOnly _output As Stream
            Private ReadOnly _buffer(ChunkBytes - 1) As Byte
            Private _filled As Integer

            Public Sub New(output As Stream)
                _output = output
            End Sub

            Public Overrides Sub Write(buffer As Byte(), offset As Integer, count As Integer)
                While count > 0
                    Dim part = Math.Min(count, ChunkBytes - _filled)
                    Array.Copy(buffer, offset, _buffer, _filled, part)
                    _filled += part
                    offset += part
                    count -= part
                    If _filled = ChunkBytes Then Emit()
                End While
            End Sub

            Private Sub Emit()
                If _filled = 0 Then Return
                WriteChunk(_output, "IDAT", _buffer, _filled)
                _filled = 0
            End Sub

            Protected Overrides Sub Dispose(disposing As Boolean)
                If disposing Then Emit()
                MyBase.Dispose(disposing)
            End Sub

            Public Overrides Sub Flush()
            End Sub

            Public Overrides ReadOnly Property CanRead As Boolean = False
            Public Overrides ReadOnly Property CanSeek As Boolean = False
            Public Overrides ReadOnly Property CanWrite As Boolean = True

            Public Overrides ReadOnly Property Length As Long
                Get
                    Throw New NotSupportedException()
                End Get
            End Property

            Public Overrides Property Position As Long
                Get
                    Throw New NotSupportedException()
                End Get
                Set(value As Long)
                    Throw New NotSupportedException()
                End Set
            End Property

            Public Overrides Function Read(buffer As Byte(), offset As Integer, count As Integer) As Integer
                Throw New NotSupportedException()
            End Function

            Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
                Throw New NotSupportedException()
            End Function

            Public Overrides Sub SetLength(value As Long)
                Throw New NotSupportedException()
            End Sub
        End Class

        Friend Shared Function OptionsFor(purpose As PngPurpose, rawBytes As Long) As SKPngEncoderOptions
            Select Case purpose
                Case PngPurpose.Transient
                    If rawBytes <= TransientUncompressedLimitBytes Then
                        Return New SKPngEncoderOptions(SKPngEncoderFilterFlags.NoFilters, 0)
                    End If
                    Return FastCompressed()
                Case PngPurpose.Stored
                    Return FastCompressed()
                Case Else
                    Return New SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)
            End Select
        End Function

        ''' <summary>Gemessen die schnellste gepackte Form: der Filter Sub vor zlib 1 schlug "ohne
        ''' Filter" (0,6 gegen 0,8 s) und packt Fotos so gut wie die Vorgabe.</summary>
        Private Shared Function FastCompressed() As SKPngEncoderOptions
            Return New SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1)
        End Function

    End Class

End Namespace
