Imports System
Imports System.IO
Imports System.Linq
Imports SkiaSharp

Namespace Services

    ''' Holt die eingebettete JPEG-Vorschau aus einer RAW-Datei, ohne fremde Bibliotheken.
    ''' Vorgehen: die ersten 16 MB der Datei nach eingebetteten JPEG-Daten absuchen und dabei die
    ''' verlustfrei gepackten Sensordaten aussortieren (SOF3, SOF5 bis 7, SOF11, SOF13 bis 15).
    ''' Bei CR3 von Canon kommt der Weg ueber die BMFF-Kastenstruktur dazu.
    Public Class RawPreviewService

        ''' <summary>DIE kanonische Liste der RAW-Endungen - alle anderen Stellen (Galerie,
        ''' Betrachter, Transparenzpruefung, Diagnose) leiten davon ab, statt eigene Kopien zu
        ''' fuehren. Beim PSD-Einbau waren fuenf getrennte Listen zu pflegen, das driftet
        ''' zwangslaeufig auseinander.
        ''' Der Umfang folgt dem, was LibRaw dekodieren kann; ohne LibRaw greift fuer alle
        ''' dieselbe eingebettete JPEG-Vorschau, die formatunabhaengig gesucht wird.
        ''' ".raw" ist mit dabei (Leica/Panasonic): die Endung ist generisch, eine gleichnamige
        ''' Nicht-Bilddatei erscheint deshalb als leere Kachel - bewusst in Kauf genommen.</summary>
        Public Shared ReadOnly SupportedExtensions As String() = {
            ".cr2", ".cr3", ".crw",            ' Canon
            ".nef", ".nrw",                    ' Nikon
            ".arw", ".srf", ".sr2", ".arq",    ' Sony
            ".raf",                            ' Fujifilm (X-Trans)
            ".orf",                            ' Olympus/OM System
            ".rw2",                            ' Panasonic
            ".pef", ".dng",                    ' Pentax / Adobe
            ".srw",                            ' Samsung
            ".rwl", ".raw",                    ' Leica (.raw auch Panasonic)
            ".3fr", ".fff",                    ' Hasselblad
            ".iiq", ".cap",                    ' Phase One
            ".mrw",                            ' Minolta
            ".erf",                            ' Epson
            ".mef",                            ' Mamiya
            ".mos",                            ' Leaf
            ".kdc", ".dcr"                     ' Kodak
        }

        ''' <summary>
        ''' .x3f (Sigma Foveon) steht bewusst NICHT in der Liste. Gemessen am Pruefbestand mit 335
        ''' RAW-Dateien aus 21 Formaten und 19 Herstellern dekodieren 327 sauber; ALLE acht
        ''' Fehlschlaege sind x3f, und auch die eingebettete Vorschau laesst sich dort nicht
        ''' auslesen. Das ist eine Grenze von LibRaw, kein Fehler bei uns.
        '''
        ''' Aufgefuehrt und nicht funktionierend war die schlechtere der beiden Moeglichkeiten: ein
        ''' Sigma-Nutzer bekam eine leere Kachel statt eines Bildes und keinen Hinweis, woran es
        ''' liegt. Nicht aufgefuehrt taucht die Datei gar nicht erst auf. Sobald LibRaw das Format
        ''' traegt, gehoert die Endung zurueck in die Liste oben.
        ''' </summary>
        Public Const UnsupportedRawFormat As String = ".x3f"

        Public Shared Function IsSupportedRaw(filePath As String) As Boolean
            Dim ext = Path.GetExtension(filePath).ToLowerInvariant()
            Return SupportedExtensions.Contains(ext)
        End Function

        ''' <summary>Grosse Vorschau fuer Betrachter/Editor-Anzeige in ZWEI Stufen: erst der eigene
        ''' Scanner (er sucht das GROESSTE eingebettete JPEG - fuer die Anzeige zaehlt Aufloesung),
        ''' sonst LibRaws Thumbnail-API. Der Rueckfall ist nicht theoretisch: Leica-DNGs betten kein
        ''' vom Scanner auffindbares JPEG ein, dort blieb die Anzeige sonst leer (gemessen
        '''). Umgekehrte Reihenfolge als bei den Galerie-Kacheln, wo LibRaw zuerst
        ''' kommt - dort zaehlt Tempo, nicht Aufloesung.</summary>
        Public Shared Function ExtractPreviewWithFallback(filePath As String) As MemoryStream
            Dim scanned = ExtractPreview(filePath)
            If IsBigEnoughForDisplay(scanned) Then Return scanned

            Dim thumb = RawDecodeService.TryExtractThumbnail(filePath)
            If IsBigEnoughForDisplay(thumb) Then
                scanned?.Dispose()
                Return thumb
            End If

            ' Letzte Stufe: manche Dateien betten GAR KEINE brauchbare Vorschau ein - dann bleibt nur
            ' das echte Entwickeln. Teuer (Demosaic), aber die Alternative ist ein winziges Bild auf
            ' Bildschirmgroesse gezogen. Die Leica M8 legt als einzige Vorschau ein 320x240-TIFF ab
            ' (kein JPEG, der Scanner findet also nichts); LibRaws Thumb-API lieferte genau dieses
            ' Miniaturbild, und WEIL sie etwas lieferte, wurde nie entwickelt - der Betrachter zeigte
            ' 320x240 formatfuellend hochskaliert.
            Dim developed = RawDecodeService.TryRenderPreviewPng(filePath)
            If developed IsNot Nothing AndAlso developed.Length > 0 Then
                scanned?.Dispose()
                thumb?.Dispose()
                Return developed
            End If
            developed?.Dispose()

            ' Entwickeln ging nicht (libraw fehlt oder kennt die Datei nicht): dann ist ein kleines
            ' Bild immer noch besser als ein schwarzer Betrachter. Groesseres von beiden nehmen.
            If LongestEdge(scanned) >= LongestEdge(thumb) Then
                thumb?.Dispose()
                Return If(LongestEdge(scanned) > 0, scanned, Nothing)
            End If
            scanned?.Dispose()
            Return thumb
        End Function

        ''' <summary>Ab dieser Kantenlaenge gilt eine eingebettete Vorschau als anzeigetauglich.
        ''' Darunter wird lieber entwickelt. Bewusst nicht hoeher: die allermeisten Kameras betten
        ''' eine nahezu vollaufgeloeste Vorschau ein (gemessen: PENTAX 3872, Sony 8640) - die soll
        ''' weiterhin sofort und ohne Demosaic angezeigt werden.</summary>
        Private Const MinDisplayEdge As Integer = 1024

        Private Shared Function IsBigEnoughForDisplay(stream As MemoryStream) As Boolean
            Return LongestEdge(stream) >= MinDisplayEdge
        End Function

        ''' <summary>Laengste Kante eines Vorschau-Stroms, 0 wenn nicht lesbar. Liest NUR den Kopf.
        ''' Ueber eine Kopie der Bytes, weil SKCodec.Create den Strom sonst uebernimmt und schliesst -
        ''' der Aufrufer braucht ihn danach aber noch (dieselbe Falle wie in DecodeOriented).</summary>
        Private Shared Function LongestEdge(stream As MemoryStream) As Integer
            If stream Is Nothing OrElse stream.Length = 0 Then Return 0
            Try
                stream.Position = 0
                Using data = SKData.CreateCopy(stream.ToArray())
                    Using codec = SKCodec.Create(data)
                        If codec Is Nothing Then Return 0
                        Return Math.Max(codec.Info.Width, codec.Info.Height)
                    End Using
                End Using
            Catch
                Return 0
            Finally
                Try
                    stream.Position = 0
                Catch
                End Try
            End Try
        End Function

        ''' Liefert einen Speicherstrom mit den JPEG-Bytes, auf Position 0 gestellt - oder
        ''' Nothing, wenn nichts zu finden war.
        Public Shared Function ExtractPreview(filePath As String) As MemoryStream
            Try
                Dim ext = Path.GetExtension(filePath).ToLowerInvariant()

                ' CR3: zuerst ueber die Kastenstruktur - schneller, weil sie gezielt die
                ' richtigen Kaesten ansteuert statt die Datei abzusuchen.
                If ext = ".cr3" Then
                    Dim r = ExtractBmffPreview(filePath)
                    If r IsNot Nothing Then Return r
                End If

                ' Fuer alle anderen: die Datei nach dem groessten eingebetteten und anzeigbaren
                ' JPEG absuchen.
                Return ScanForJpeg(filePath)
            Catch
                Return Nothing
            End Try
        End Function

        ' ── Universal JPEG scanner ────────────────────────────────────────────────

        Private Const MaxScanBytes As Long = 16L * 1024 * 1024   ' 16 MB

        Private Shared Function ScanForJpeg(filePath As String) As MemoryStream
            Using fs = File.OpenRead(filePath)
                Dim readLen = CInt(Math.Min(fs.Length, MaxScanBytes))
                Dim data(readLen - 1) As Byte
                Dim totalRead = 0
                Do While totalRead < readLen
                    Dim n = fs.Read(data, totalRead, readLen - totalRead)
                    If n = 0 Then Exit Do
                    totalRead += n
                Loop
                If totalRead < 4 Then Return Nothing

                Dim bestStart As Integer = -1
                Dim bestLen As Integer = 0

                Dim i = 0
                Do While i < totalRead - 3
                    If data(i) = &HFF AndAlso data(i + 1) = &HD8 AndAlso data(i + 2) = &HFF Then
                        If IsDisplayableJpeg(data, i) Then
                            Dim jLen = WalkJpegLength(data, i)
                            If jLen > bestLen Then
                                bestLen = jLen
                                bestStart = i
                            End If
                        End If
                    End If
                    i += 1
                Loop

                If bestStart < 0 OrElse bestLen < 8192 Then Return Nothing

                Dim result(bestLen - 1) As Byte
                Array.Copy(data, bestStart, result, 0, bestLen)
                Return New MemoryStream(result)
            End Using
        End Function

        ''' Wahr, wenn das JPEG an dieser Stelle eine ANZEIGBARE Rahmenart benutzt. Verlustfreies
        ''' JPEG (SOF3, SOF5 bis 7, SOF11, SOF13 bis 15) dient zum Packen der Sensordaten einer RAW
        ''' und laesst sich mit einem gewoehnlichen Bilddecoder nicht lesen.
        Private Shared Function IsDisplayableJpeg(data As Byte(), offset As Integer) As Boolean
            Dim pos = offset + 2           ' die Startmarke SOI (FF D8) ueberspringen
            Dim limit = Math.Min(data.Length, offset + 8192)
            Do While pos + 3 < limit
                If data(pos) <> &HFF Then Return True   ' unexpected – assume OK
                Dim marker = data(pos + 1)
                If marker >= &HC0 AndAlso marker <= &HCF AndAlso
                   marker <> &HC4 AndAlso marker <> &HC8 AndAlso marker <> &HCC Then
                    ' C0=baseline, C1=extended, C2=progressive → displayable
                    Return marker = &HC0 OrElse marker = &HC1 OrElse marker = &HC2
                End If
                If (marker >= &HD0 AndAlso marker <= &HD9) OrElse marker = &H01 Then
                    pos += 2
                Else
                    Dim segLen = CInt(data(pos + 2)) * 256 + CInt(data(pos + 3))
                    If segLen < 2 Then Return True
                    pos += 2 + segLen
                End If
            Loop
            Return False   ' SOF not found → not a recognisable standard JPEG
        End Function

        ''' Walk JPEG segment chain (including SOS entropy data) to find exact byte length.
        Private Shared Function WalkJpegLength(data As Byte(), start As Integer) As Integer
            Dim pos = start + 2           ' skip SOI
            Dim limit = data.Length
            Do While pos + 3 < limit
                If data(pos) <> &HFF Then Return 0
                Dim marker = data(pos + 1)
                If marker = &HD9 Then Return pos - start + 2   ' EOI found
                If (marker >= &HD0 AndAlso marker <= &HD7) OrElse marker = &H01 Then
                    pos += 2 : Continue Do
                End If
                Dim segLen = CInt(data(pos + 2)) * 256 + CInt(data(pos + 3))
                If segLen < 2 Then Return 0
                If marker = &HDA Then   ' SOS – walk entropy-coded data
                    pos += 2 + segLen
                    Do While pos + 1 < limit
                        If data(pos) = &HFF Then
                            Dim nxt = data(pos + 1)
                            If nxt = &H00 OrElse nxt = &HFF Then
                                pos += 2
                            ElseIf nxt >= &HD0 AndAlso nxt <= &HD7 Then
                                pos += 2          ' RST marker
                            ElseIf nxt = &HD9 Then
                                Return pos - start + 2   ' EOI
                            Else
                                Exit Do           ' back to segment-walk
                            End If
                        Else
                            pos += 1
                        End If
                    Loop
                    Continue Do
                End If
                pos += 2 + segLen
            Loop
            Return 0
        End Function

        ' ── BMFF-based (CR3) ─────────────────────────────────────────────────────
        ' Aufbau einer CR3 (Canon EOS):
        '   ftyp → moov → uuid[85c0b687...] → THMB (Miniatur 160×120)
        '                → uuid[eaf42b5e...] → Canon-Datenblock, grosses JPEG ab Position 32

        Private Shared Function ExtractBmffPreview(filePath As String) As MemoryStream
            Dim best As MemoryStream = Nothing
            Try
                Using fs = File.OpenRead(filePath)
                    Using br = New BinaryReader(fs)
                        CollectBmffJpegs(fs, br, 0, fs.Length, best)
                    End Using
                End Using
            Catch
                best?.Dispose()
                Return Nothing
            End Try
            Return best
        End Function

        Private Shared Sub CollectBmffJpegs(fs As FileStream, br As BinaryReader, rangeStart As Long, rangeEnd As Long, ByRef best As MemoryStream)
            If rangeStart < 0 OrElse rangeEnd <= rangeStart OrElse rangeEnd > fs.Length Then Return
            fs.Seek(rangeStart, SeekOrigin.Begin)

            Do While fs.Position + 8 <= rangeEnd
                Dim boxStart = fs.Position
                Dim size32 = ReadU32BE(br)
                Dim typBuf(3) As Byte
                If br.Read(typBuf, 0, 4) <> 4 Then Exit Do
                Dim boxType = Text.Encoding.ASCII.GetString(typBuf)

                Dim boxSize As Long
                Dim dataStart As Long
                If size32 = 1 Then
                    If fs.Position + 8 > rangeEnd Then Exit Do
                    boxSize = ReadU64BE(br)
                    dataStart = boxStart + 16
                Else
                    boxSize = If(size32 = 0, rangeEnd - boxStart, CLng(size32))
                    dataStart = boxStart + 8
                End If
                Dim boxEnd = Math.Min(boxStart + boxSize, rangeEnd)
                If boxSize < dataStart - boxStart OrElse boxEnd <= dataStart Then Exit Do

                Select Case boxType
                    Case "moov", "trak", "mdia", "minf", "stbl", "CRAW"
                        CollectBmffJpegs(fs, br, dataStart, boxEnd, best)

                    Case "uuid"
                        ' Skip 16-byte UUID, recurse into sub-boxes
                        Dim uuidContent = dataStart + 16
                        If uuidContent < boxEnd Then
                            CollectBmffJpegs(fs, br, uuidContent, boxEnd, best)
                            ' Canon legt das JPEG in den uuid-Kaesten als rohe Daten ab, nicht als
                            ' Unterkaesten - deshalb den Inhalt absuchen.
                            Dim scanLen = CInt(Math.Min(boxEnd - uuidContent, 8 * 1024 * 1024L))
                            fs.Seek(uuidContent, SeekOrigin.Begin)
                            Dim buf(scanLen - 1) As Byte
                            Dim n = br.Read(buf, 0, scanLen)
                            If n > 0 Then TryKeepLarger(FindJpeg(buf, n), best)
                        End If

                    Case "PRVW", "THMB"
                        Dim dataLen = CInt(Math.Min(boxEnd - dataStart, 4 * 1024 * 1024L))
                        If dataLen > 0 Then
                            fs.Seek(dataStart, SeekOrigin.Begin)
                            Dim boxData(dataLen - 1) As Byte
                            Dim n = br.Read(boxData, 0, dataLen)
                            If n > 0 Then TryKeepLarger(FindJpeg(boxData, n), best)
                        End If
                End Select

                fs.Seek(boxEnd, SeekOrigin.Begin)
            Loop
        End Sub

        Private Shared Sub TryKeepLarger(candidate As MemoryStream, ByRef best As MemoryStream)
            If candidate Is Nothing Then Return
            If best Is Nothing OrElse candidate.Length > best.Length Then
                best?.Dispose()
                best = candidate
            Else
                candidate.Dispose()
            End If
        End Sub

        ''' Schnelle Suche nach einem JPEG in einem begrenzten Puffer, von hinten nach vorn.
        ''' Wird vom Durchlauf durch die Kastenstruktur benutzt.
        Private Shared Function FindJpeg(data As Byte(), length As Integer) As MemoryStream
            length = Math.Max(0, Math.Min(length, data.Length))
            For i = 0 To length - 4
                If data(i) = &HFF AndAlso data(i + 1) = &HD8 AndAlso data(i + 2) = &HFF Then
                    For j = length - 2 To i + 512 Step -1
                        If data(j) = &HFF AndAlso data(j + 1) = &HD9 Then
                            Dim len = j - i + 2
                            If len > 8192 Then Return New MemoryStream(data, i, len, False)
                            Exit For
                        End If
                    Next
                End If
            Next
            Return Nothing
        End Function

        ' ── BMFF read helpers ─────────────────────────────────────────────────────

        ' In VB.NET liefert "byteWert << n" wieder einen Byte und maskiert die Schiebeweite mit 7 -
        ' die Operanden müssen deshalb vor dem Shift geweitet werden.
        Private Shared Function ReadU32BE(br As BinaryReader) As UInteger
            Dim b = br.ReadBytes(4)
            If b.Length <> 4 Then Throw New EndOfStreamException()
            Return CUInt((CLng(b(0)) << 24) Or (CLng(b(1)) << 16) Or (CLng(b(2)) << 8) Or CLng(b(3)))
        End Function

        Private Shared Function ReadU64BE(br As BinaryReader) As Long
            Dim b = br.ReadBytes(8)
            If b.Length <> 8 Then Throw New EndOfStreamException()
            Return (CLng(b(0)) << 56) Or (CLng(b(1)) << 48) Or (CLng(b(2)) << 40) Or (CLng(b(3)) << 32) Or
                   (CLng(b(4)) << 24) Or (CLng(b(5)) << 16) Or (CLng(b(6)) << 8) Or CLng(b(7))
        End Function

    End Class

End Namespace
