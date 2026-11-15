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
        ''' zahlt er einmal. Skias Vorgabe.</summary>
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
    ''' Tausch, fuer die Ausgabe des Nutzers nicht.
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
        ''' wirklich Transparenz traegt.</summary>
        Public Shared Function EncodeDeep(bitmap As SKBitmap) As SKData
            If bitmap Is Nothing OrElse bitmap.ColorType <> SKColorType.Rgba16161616 Then Return Nothing
            Dim width = bitmap.Width, height = bitmap.Height
            If width <= 0 OrElse height <= 0 Then Return Nothing
            Using pixmap = bitmap.PeekPixels()
                If pixmap Is Nothing Then Return Nothing
                Dim rowInfo = New SKImageInfo(width, 1, SKColorType.Rgba16161616, SKAlphaType.Unpremul)
                Dim rgba(width * 8 - 1) As Byte
                Dim handle = Runtime.InteropServices.GCHandle.Alloc(rgba, Runtime.InteropServices.GCHandleType.Pinned)
                Try
                    Dim pointer = handle.AddrOfPinnedObject()
                    Dim hasAlpha = False
                    If bitmap.AlphaType <> SKAlphaType.Opaque Then
                        For y = 0 To height - 1
                            If Not pixmap.ReadPixels(rowInfo, pointer, rgba.Length, 0, y) Then Return Nothing
                            For i = 6 To rgba.Length - 1 Step 8
                                If rgba(i) <> 255 OrElse rgba(i + 1) <> 255 Then
                                    hasAlpha = True
                                    Exit For
                                End If
                            Next
                            If hasAlpha Then Exit For
                        Next
                    End If

                    Dim pixelBytes = If(hasAlpha, 8, 6)
                    Dim rowBytes = width * pixelBytes
                    Dim current(rowBytes - 1) As Byte
                    Dim previous(rowBytes - 1) As Byte
                    Dim candidates(4)() As Byte
                    For f = 0 To 4
                        candidates(f) = New Byte(rowBytes) {}
                        candidates(f)(0) = CByte(f)
                    Next

                    Using output As New MemoryStream()
                        output.Write(PngSignature, 0, PngSignature.Length)
                        Dim header(12) As Byte
                        WriteBigEndian(header, 0, width)
                        WriteBigEndian(header, 4, height)
                        header(8) = 16
                        header(9) = CByte(If(hasAlpha, 6, 2))
                        WriteChunk(output, "IHDR", header, header.Length)

                        Using idat As New IdatStream(output)
                            Using zlib As New Compression.ZLibStream(idat, Compression.CompressionLevel.Optimal, leaveOpen:=True)
                                For y = 0 To height - 1
                                    If Not pixmap.ReadPixels(rowInfo, pointer, rgba.Length, 0, y) Then Return Nothing
                                    ' PNG schreibt das hohe Byte zuerst, der Rechner das niedrige.
                                    Dim t = 0
                                    For s = 0 To rgba.Length - 1 Step 8
                                        current(t) = rgba(s + 1) : current(t + 1) = rgba(s)
                                        current(t + 2) = rgba(s + 3) : current(t + 3) = rgba(s + 2)
                                        current(t + 4) = rgba(s + 5) : current(t + 5) = rgba(s + 4)
                                        If hasAlpha Then
                                            current(t + 6) = rgba(s + 7) : current(t + 7) = rgba(s + 6)
                                        End If
                                        t += pixelBytes
                                    Next
                                    Dim best = FilterRow(current, previous, pixelBytes, candidates)
                                    zlib.Write(candidates(best), 0, rowBytes + 1)
                                    Dim swap = previous
                                    previous = current
                                    current = swap
                                Next
                            End Using
                        End Using
                        WriteChunk(output, "IEND", Array.Empty(Of Byte)(), 0)
                        Return SKData.CreateCopy(output.GetBuffer(), CULng(output.Length))
                    End Using
                Finally
                    handle.Free()
                End Try
            End Using
        End Function

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
