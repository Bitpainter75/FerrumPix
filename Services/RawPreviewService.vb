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
            ".kdc", ".dcr",                    ' Kodak
            X3fPreviewService.Extension        ' Sigma (Foveon), Polaroid x530
        }

        ' .x3f entwickelt LibRaw nur mit dem Schalter USE_X3FTOOLS, der ihren X3F-Leser einschaltet;
        ' die mitgelieferten Bauten setzen ihn (packaging/libraw). Die Vorschau liest trotzdem
        ' X3fPreviewService und nicht der Scanner hier (siehe ExtractPreview), und sie bleibt der
        ' Rueckfall, wenn eine LibRaw ohne den Schalter geladen ist.

        ''' <summary>Geht die Datei den RAW-Weg? Jede RAW-Endung aus <see cref="SupportedExtensions"/>
        ''' und dazu LINEARES LICHT: OpenEXR, Radiance HDR und TIFF mit Gleitkomma
        ''' (RawDecodeService.IsLinearLightSource). Die laufen wie ein entwickeltes RAW durch die
        ''' RAW-Grundstufe, mit den RAW-Reglern im Editor und dem Rezept neben der Datei; sie stehen
        ''' aber nicht in der Liste der RAW-Endungen, weil sie keine Kameradateien sind (Filter RAW
        ''' der Galerie, Import, Umwandeln nach DNG).</summary>
        Public Shared Function IsSupportedRaw(filePath As String) As Boolean
            Dim ext = Path.GetExtension(filePath).ToLowerInvariant()
            Return SupportedExtensions.Contains(ext) OrElse RawDecodeService.IsLinearLightSource(filePath)
        End Function

        ''' <summary>Eine Kameradatei, also eine Endung aus <see cref="SupportedExtensions"/>, OHNE das
        ''' lineare Licht. Fuer die Stellen, die einen Sensor oder eine Vorschau der Kamera
        ''' voraussetzen: Umwandeln nach DNG, Kamera-JPEG daneben, Entrauschen mit Modell.</summary>
        Public Shared Function IsCameraRaw(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Return SupportedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())
        End Function

        ''' <summary>Grosse Vorschau fuer Betrachter/Editor-Anzeige in ZWEI Stufen: erst der eigene
        ''' Scanner (er sucht das GROESSTE eingebettete JPEG - fuer die Anzeige zaehlt Aufloesung),
        ''' sonst LibRaws Thumbnail-API. Der Rueckfall ist nicht theoretisch: Leica-DNGs betten kein
        ''' vom Scanner auffindbares JPEG ein, dort blieb die Anzeige sonst leer (gemessen).
        ''' Umgekehrte Reihenfolge als bei den Galerie-Kacheln, wo LibRaw zuerst
        ''' kommt - dort zaehlt Tempo, nicht Aufloesung.</summary>
        Public Shared Function ExtractPreviewWithFallback(filePath As String) As MemoryStream
            Dim scanned = ExtractPreview(filePath)
            If IsBigEnoughForDisplay(scanned) Then Return scanned

            Dim thumb = RawDecodeService.TryExtractThumbnail(filePath)
            If IsBigEnoughForDisplay(thumb) Then
                scanned?.Dispose()
                Return thumb
            End If

            ' DNG 1.7 mit JPEG XL: die grosse Vorschau ist selbst JPEG XL, und keiner der beiden
            ' Wege oben findet sie (der Scanner sucht JPEG, LibRaw meldet nur das Miniaturbild).
            ' Entwickeln geht bei diesen Dateien zwar (DngJxlRawService), kostet aber erst das
            ' Dekodieren aller Kacheln und dann LibRaw. Deshalb VOR dem Entwickeln (Issue 83: 1024
            ' statt 256 Punkte, ohne Entwicklung).
            If String.Equals(Path.GetExtension(filePath), ".dng", StringComparison.OrdinalIgnoreCase) Then
                Dim jxlPreview = DngJxlPreviewService.TryExtract(filePath)
                If IsBigEnoughForDisplay(jxlPreview) Then
                    scanned?.Dispose()
                    thumb?.Dispose()
                    Return jxlPreview
                End If
                jxlPreview?.Dispose()
            End If

            ' Letzte Stufe: manche Dateien betten GAR KEINE brauchbare Vorschau ein - dann bleibt nur
            ' das echte Entwickeln. Kostet einen Decode (halb, siehe TryRenderPreviewJpeg), aber die Alternative ist ein winziges Bild auf
            ' Bildschirmgroesse gezogen. Die Leica M8 legt als einzige Vorschau ein 320x240-TIFF ab
            ' (kein JPEG, der Scanner findet also nichts); LibRaws Thumb-API lieferte genau dieses
            ' Miniaturbild, und WEIL sie etwas lieferte, wurde nie entwickelt - der Betrachter zeigte
            ' 320x240 formatfuellend hochskaliert.
            Dim developed = RawDecodeService.TryRenderPreviewJpeg(filePath)
            If developed IsNot Nothing AndAlso developed.Length > 0 Then
                scanned?.Dispose()
                thumb?.Dispose()
                Return developed
            End If
            developed?.Dispose()

            ' Nichts gefunden und nicht entwickelbar: vielleicht ein gewoehnliches TIFF unter
            ' RAW-Endung (siehe PlainImageKind).
            If LongestEdge(scanned) = 0 AndAlso LongestEdge(thumb) = 0 Then
                Dim plainTiff = PlainTiffFallback(filePath)
                If plainTiff IsNot Nothing Then
                    scanned?.Dispose()
                    thumb?.Dispose()
                    Return plainTiff
                End If
            End If

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

        ''' <summary>Was am Anfang der Datei steht, wenn sie inhaltlich ein gewoehnliches Bild ist:
        ''' JPEG, PNG oder TIFF; sonst <see cref="PlainImage.None"/>. Die Endung entscheidet ueberall
        ''' sonst, und eine umbenannte Datei (ein Webexport als .nef, ein PNG als .cr2) fiel so durch
        ''' alle RAW-Wege: der Scanner nahm beim Kamera-JPEG das eingebettete Miniaturbild, PNG und
        ''' TIFF blieben leer. TIFF ist dabei nur ein Hinweis, denn die meisten RAWs sind selbst
        ''' TIFF-Abkoemmlinge; es zaehlt erst, wenn LibRaw die Datei nicht entwickeln kann.</summary>
        Friend Enum PlainImage
            None
            Jpeg
            Png
            Tiff
        End Enum

        Friend Shared Function PlainImageKind(filePath As String) As PlainImage
            Try
                Dim head(7) As Byte
                Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete, 16)
                    If fs.Read(head, 0, head.Length) < head.Length Then Return PlainImage.None
                End Using
                If head(0) = &HFF AndAlso head(1) = &HD8 AndAlso head(2) = &HFF Then Return PlainImage.Jpeg
                If head(0) = &H89 AndAlso head(1) = &H50 AndAlso head(2) = &H4E AndAlso head(3) = &H47 AndAlso
                   head(4) = &HD AndAlso head(5) = &HA AndAlso head(6) = &H1A AndAlso head(7) = &HA Then Return PlainImage.Png
                If (head(0) = &H49 AndAlso head(1) = &H49 AndAlso head(2) = &H2A AndAlso head(3) = 0) OrElse
                   (head(0) = &H4D AndAlso head(1) = &H4D AndAlso head(2) = 0 AndAlso head(3) = &H2A) Then Return PlainImage.Tiff
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Return PlainImage.None
        End Function

        ''' <summary>JPEG oder PNG unter RAW-Endung: sicher kein RAW, LibRaw muss nicht gefragt werden.</summary>
        Friend Shared Function IsJpegOrPngContent(filePath As String) As Boolean
            Dim kind = PlainImageKind(filePath)
            Return kind = PlainImage.Jpeg OrElse kind = PlainImage.Png
        End Function

        ''' <summary>Ein gewoehnliches TIFF unter RAW-Endung, als PNG-Strom, fuer die Stellen, an
        ''' denen LibRaw schon gescheitert ist und keine Vorschau gefunden wurde. Sensordaten (CFA,
        ''' LinearRaw) bleiben aussen vor, sonst kaeme ein Mosaik als Graubild.</summary>
        Friend Shared Function PlainTiffFallback(filePath As String) As MemoryStream
            If PlainImageKind(filePath) <> PlainImage.Tiff OrElse Not TiffPreviewService.HasPlainPhotometric(filePath) Then Return Nothing
            Return TiffPreviewService.ExtractPreview(filePath)
        End Function

        ''' Liefert einen Speicherstrom mit den JPEG-Bytes, auf Position 0 gestellt - oder
        ''' Nothing, wenn nichts zu finden war.
        Public Shared Function ExtractPreview(filePath As String) As MemoryStream
            Try
                Dim ext = Path.GetExtension(filePath).ToLowerInvariant()

                ' Lineares Licht traegt keine Vorschau; die Anzeigewege entwickeln es dann
                ' (ExtractPreviewWithFallback, Kachel). Der Scanner faende in einer EXR hoechstens
                ' zufaellige JPEG-Kennungen.
                If RawDecodeService.IsLinearLightSource(filePath) Then Return Nothing

                ' Ein JPEG oder PNG mit RAW-Endung ist selbst das Bild: die ganze Datei, nicht ein
                ' eingebettetes Miniaturbild aus seinem Exif-Block (siehe PlainImageKind).
                If IsJpegOrPngContent(filePath) Then
                    Dim whole As New MemoryStream()
                    Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
                        fs.CopyTo(whole)
                    End Using
                    whole.Position = 0
                    Return whole
                End If

                ' CR3: zuerst ueber die Kastenstruktur - schneller, weil sie gezielt die
                ' richtigen Kaesten ansteuert statt die Datei abzusuchen.
                If ext = ".cr3" Then
                    Dim r = ExtractBmffPreview(filePath)
                    If r IsNot Nothing Then Return r
                End If

                ' X3F: der eigene Leser ueber das Inhaltsverzeichnis der Datei. Er findet alle drei
                ' Vorschauformen; der Scanner faende nur ein JPEG, und die Vorschau der SD9 und
                ' SD10 ist keines.
                If X3fPreviewService.IsSupportedX3f(filePath) Then Return X3fPreviewService.ExtractPreview(filePath)

                ' Fuer alle anderen ZUERST gezielt ueber die Dateistruktur: ein paar Kilobyte
                ' Verzeichnis und dann genau das eine JPEG, statt bis zu 16 MB zu lesen. Erst wenn
                ' dort keine anzeigetaugliche Vorschau liegt, wird die Datei abgesucht.
                Dim located = TryLocatePreview(filePath)
                If located IsNot Nothing Then Return located
                Return ScanForJpeg(filePath)
            Catch
                Return Nothing
            End Try
        End Function

        ' ── Gezielt ueber die Dateistruktur ──────────────────────────────────────
        ' Erst die Struktur, dann wenige gezielte Lesezugriffe. Die meisten
        ' RAWs sind TIFF-Abkoemmlinge (CR2, NEF, ARW, DNG, ORF, RW2, PEF, SRW, 3FR, ERF, ...): ihre
        ' Vorschau steht als JPEGInterchangeFormat, als einzelner JPEG-Streifen eines Verzeichnisses
        ' oder (Panasonic) als JpgFromRaw im Kopf. Fujifilm nennt Ort und Laenge im festen Kopf der
        ' RAF. Was nur in einer MakerNote liegt (Olympus, aeltere Nikon und Pentax), findet dieser
        ' Weg nicht; dann ist sein Fund klein, und der Scanner uebernimmt.

        ''' <summary>Was der gezielte Weg ueber eine Datei herausfand, fuer die Messung im
        ''' Pruefstand: wie viele Bytes er gelesen hat und ob er eine Vorschau lieferte.</summary>
        Friend NotInheritable Class LocateTrace
            Public BytesRead As Long
            Public Candidates As Integer
        End Class

        ''' <summary>Die groesste anzeigetaugliche Vorschau, gefunden ueber die Dateistruktur, oder
        ''' Nothing, wenn die Datei keine bekannte Struktur hat oder ihr groesster Fund kuerzer als
        ''' <see cref="MinDisplayEdge"/> ist. Nothing heisst "Scanner fragen", nicht "keine
        ''' Vorschau".</summary>
        Friend Shared Function TryLocatePreview(filePath As String, Optional trace As LocateTrace = Nothing) As MemoryStream
            Try
                Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096)
                    Return LocatePreview(fs, trace)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        Private Const MaxLocatedPreviewBytes As Long = 64L * 1024 * 1024
        Private Const MaxLocateIfds As Integer = 32
        Private Const MaxLocateEntries As Integer = 1000

        Friend Shared Function LocatePreview(fs As Stream, Optional trace As LocateTrace = Nothing) As MemoryStream
            Dim ranges = LocatePreviewRanges(fs, trace)
            If ranges Is Nothing OrElse ranges.Count = 0 Then Return Nothing

            ' Jeden Fund am Kopf pruefen: anzeigbare Rahmenart, Masse. Das kostet je Fund einige
            ' Kilobyte, nicht das ganze Bild.
            Dim best As (Offset As Long, Length As Long, Edge As Integer) = (0, 0, 0)
            Dim seen As New HashSet(Of Long)()
            For Each r In ranges
                If r.Length < 8192 OrElse r.Length > MaxLocatedPreviewBytes OrElse Not seen.Add(r.Offset) Then Continue For
                If r.Offset < 0 OrElse r.Offset + r.Length > fs.Length Then Continue For
                Dim width = 0, height = 0
                If Not TryReadLocatedFrame(fs, r.Offset, r.Length, width, height, trace) Then Continue For
                If trace IsNot Nothing Then trace.Candidates += 1
                ' Wie der Scanner: das laengste JPEG gewinnt.
                If r.Length > best.Length Then best = (r.Offset, r.Length, Math.Max(width, height))
            Next
            If best.Length = 0 OrElse best.Edge < MinDisplayEdge Then Return Nothing

            Dim data = ReadRange(fs, best.Offset, CInt(best.Length), trace)
            If data Is Nothing Then Return Nothing
            ' Die angegebene Laenge kann Fuellbytes hinter dem Ende tragen; auf das Ende (EOI)
            ' kuerzen wie der Scanner.
            Dim exact = WalkJpegLength(data, 0)
            If exact > 0 AndAlso exact < data.Length Then Return New MemoryStream(data, 0, exact, False)
            Return New MemoryStream(data, 0, data.Length, False)
        End Function

        ''' <summary>Die Stellen, an denen die Struktur der Datei ein JPEG nennt. Nothing, wenn die
        ''' Datei weder TIFF-artig noch eine RAF ist.</summary>
        Private Shared Function LocatePreviewRanges(fs As Stream, trace As LocateTrace) As List(Of (Offset As Long, Length As Long))
            Dim header = ReadRange(fs, 0, 100, trace)
            If header Is Nothing OrElse header.Length < 16 Then Return Nothing
            Dim result As New List(Of (Offset As Long, Length As Long))()

            ' Fujifilm: "FUJIFILMCCD-RAW " und an Stelle 84 Ort und Laenge des JPEG (gross-endian).
            If Text.Encoding.ASCII.GetString(header, 0, 16) = "FUJIFILMCCD-RAW " Then
                If header.Length < 92 Then Return Nothing
                result.Add((ReadU32At(header, 84, False), ReadU32At(header, 88, False)))
                Return result
            End If

            Dim little As Boolean
            If header(0) = &H49 AndAlso header(1) = &H49 Then
                little = True
            ElseIf header(0) = &H4D AndAlso header(1) = &H4D Then
                little = False
            Else
                Return Nothing
            End If
            ' 42 fuer TIFF, 0x55 fuer Panasonic RW2, "RO"/"RS" fuer Olympus ORF.
            Dim magic = ReadU16At(header, 2, little)
            If magic <> 42 AndAlso magic <> &H55 AndAlso magic <> &H4F52 AndAlso magic <> &H5352 Then Return Nothing

            Dim pending As New Queue(Of Long)()
            Dim visited As New HashSet(Of Long)()
            pending.Enqueue(ReadU32At(header, 4, little))
            Do While pending.Count > 0 AndAlso visited.Count < MaxLocateIfds
                Dim offset = pending.Dequeue()
                If offset < 8 OrElse offset >= fs.Length OrElse Not visited.Add(offset) Then Continue Do
                Dim countBytes = ReadRange(fs, offset, 2, trace)
                If countBytes Is Nothing Then Continue Do
                Dim count = ReadU16At(countBytes, 0, little)
                If count <= 0 OrElse count > MaxLocateEntries Then Continue Do
                Dim entries = ReadRange(fs, offset + 2, count * 12 + 4, trace)
                If entries Is Nothing Then Continue Do
                Dim nextIfd = ReadU32At(entries, count * 12, little)
                If nextIfd > 0 Then pending.Enqueue(nextIfd)

                Dim jpegStart As Long = -1, jpegLength As Long = -1
                Dim compression = 0
                Dim stripOffsets As Long() = Nothing, stripCounts As Long() = Nothing
                For i = 0 To count - 1
                    Dim e = i * 12
                    Dim tag = ReadU16At(entries, e, little)
                    Dim type = ReadU16At(entries, e + 2, little)
                    Dim n = ReadU32At(entries, e + 4, little)
                    Select Case tag
                        Case &H201 : jpegStart = EntryValue(entries, e, type, little)
                        Case &H202 : jpegLength = EntryValue(entries, e, type, little)
                        Case &H103 : compression = CInt(EntryValue(entries, e, type, little))
                        Case &H111 : stripOffsets = EntryValues(fs, entries, e, type, n, little, trace)
                        Case &H117 : stripCounts = EntryValues(fs, entries, e, type, n, little, trace)
                        Case &H2E, &H127
                            ' Panasonic JpgFromRaw (0x2E) und, bei neueren Gehaeusen (G9 II, GH6),
                            ' JpgFromRaw2 (0x127) in voller Groesse: je ein Wert vom Typ UNDEFINED,
                            ' der das JPEG selbst ist. Ohne den zweiten gewann dort das 1920er.
                            If type = 7 AndAlso n > 4 Then result.Add((ReadU32At(entries, e + 8, little), n))
                        Case &H14A
                            Dim subs = EntryValues(fs, entries, e, type, n, little, trace)
                            If subs IsNot Nothing Then
                                For Each s In subs
                                    pending.Enqueue(s)
                                Next
                            End If
                        Case &H8769
                            pending.Enqueue(EntryValue(entries, e, type, little))
                        Case &H927C
                            ' Olympus legt die Vorschau in die MakerNote, nicht in ein Verzeichnis.
                            If n > 16 Then AddOlympusPreview(fs, ReadU32At(entries, e + 8, little), result, trace)
                    End Select
                Next
                If jpegStart > 0 AndAlso jpegLength > 0 Then result.Add((jpegStart, jpegLength))
                ' Ein Verzeichnis, dessen Bild ein einzelner JPEG-Streifen ist (DNG-Vorschau, NEF).
                ' Verlustfrei gepackte Sensordaten sortiert danach die Pruefung der Rahmenart aus.
                If (compression = 6 OrElse compression = 7) AndAlso stripOffsets IsNot Nothing AndAlso
                   stripCounts IsNot Nothing AndAlso stripOffsets.Length = 1 AndAlso stripCounts.Length = 1 Then
                    result.Add((stripOffsets(0), stripCounts(0)))
                End If
            Loop
            Return result
        End Function

        ''' <summary>Rahmenart und Masse eines gefundenen JPEG, Segment fuer Segment ueber die Datei
        ''' gelesen: je Segment nur seine vier Kopfbytes. Nicht ueber TryReadFrameHeader, das nur die
        ''' ersten 8 KB ansieht: bei Panasonic steht vor dem Rahmenkopf ein Exif-Block samt
        ''' Vorschaubild von gut 60 KB, und jeder RW2-Fund fiel dort durch.</summary>
        Private Shared Function TryReadLocatedFrame(fs As Stream, start As Long, length As Long,
                                                    ByRef width As Integer, ByRef height As Integer,
                                                    trace As LocateTrace) As Boolean
            width = 0
            height = 0
            Dim soi = ReadRange(fs, start, 2, trace)
            If soi Is Nothing OrElse soi(0) <> &HFF OrElse soi(1) <> &HD8 Then Return False
            Dim pos = start + 2
            Dim limit = start + length
            For hop = 1 To 64
                If pos + 4 > limit Then Return False
                Dim seg = ReadRange(fs, pos, 4, trace)
                If seg Is Nothing OrElse seg(0) <> &HFF Then Return False
                Dim marker = seg(1)
                If marker = &HFF Then
                    pos += 1
                    Continue For
                End If
                If marker >= &HC0 AndAlso marker <= &HCF AndAlso
                   marker <> &HC4 AndAlso marker <> &HC8 AndAlso marker <> &HCC Then
                    ' Nur Grundform, erweiterte und fortschreitende Codierung sind anzeigbar.
                    If marker <> &HC0 AndAlso marker <> &HC1 AndAlso marker <> &HC2 Then Return False
                    Dim frame = ReadRange(fs, pos + 5, 4, trace)
                    If frame Is Nothing Then Return False
                    height = CInt(frame(0)) * 256 + CInt(frame(1))
                    width = CInt(frame(2)) * 256 + CInt(frame(3))
                    Return width > 0 AndAlso height > 0
                End If
                If marker = &HDA OrElse marker = &HD9 Then Return False
                If (marker >= &HD0 AndAlso marker <= &HD7) OrElse marker = &H01 Then
                    pos += 2
                    Continue For
                End If
                Dim segLen = CInt(seg(2)) * 256 + CInt(seg(3))
                If segLen < 2 Then Return False
                pos += 2 + segLen
            Next
            Return False
        End Function

        ''' <summary>Die Vorschau einer Olympus-MakerNote neuer Form ("OLYMPUS", Byte-Folge, 3): das
        ''' Verzeichnis beginnt 12 Bytes danach, alle Stellen zaehlen ab dem Anfang der MakerNote.
        ''' Unter Eintrag 0x2020 (Kameraeinstellungen) stehen Anfang (0x0101) und Laenge (0x0102)
        ''' des JPEG. Die alte Form ("OLYMP") kennt das nicht; dort sucht der Scanner.</summary>
        Private Shared Sub AddOlympusPreview(fs As Stream, noteStart As Long,
                                             result As List(Of (Offset As Long, Length As Long)),
                                             trace As LocateTrace)
            Dim head = ReadRange(fs, noteStart, 12, trace)
            If head Is Nothing OrElse Text.Encoding.ASCII.GetString(head, 0, 8) <> "OLYMPUS" & ChrW(0) Then Return
            Dim little As Boolean
            If head(8) = &H49 AndAlso head(9) = &H49 Then
                little = True
            ElseIf head(8) = &H4D AndAlso head(9) = &H4D Then
                little = False
            Else
                Return
            End If
            Dim settings = ReadNoteIfdValue(fs, noteStart, noteStart + 12, &H2020, little, trace)
            If settings <= 0 Then Return
            Dim start = ReadNoteIfdValue(fs, noteStart, noteStart + settings, &H101, little, trace)
            Dim length = ReadNoteIfdValue(fs, noteStart, noteStart + settings, &H102, little, trace)
            If start > 0 AndAlso length > 0 Then result.Add((noteStart + start, length))
        End Sub

        ''' <summary>Der Wert (oder die Stelle) eines Eintrags in einem Verzeichnis der MakerNote,
        ''' -1 wenn er fehlt.</summary>
        Private Shared Function ReadNoteIfdValue(fs As Stream, noteStart As Long, ifdOffset As Long, wanted As Integer,
                                                 little As Boolean, trace As LocateTrace) As Long
            Dim countBytes = ReadRange(fs, ifdOffset, 2, trace)
            If countBytes Is Nothing Then Return -1
            Dim count = ReadU16At(countBytes, 0, little)
            If count <= 0 OrElse count > MaxLocateEntries Then Return -1
            Dim entries = ReadRange(fs, ifdOffset + 2, count * 12, trace)
            If entries Is Nothing Then Return -1
            For i = 0 To count - 1
                Dim e = i * 12
                If ReadU16At(entries, e, little) <> wanted Then Continue For
                Dim type = ReadU16At(entries, e + 2, little)
                If type = 3 Then Return ReadU16At(entries, e + 8, little)
                Return ReadU32At(entries, e + 8, little)
            Next
            Return -1
        End Function

        Private Shared Function ReadRange(fs As Stream, offset As Long, length As Integer, trace As LocateTrace) As Byte()
            If offset < 0 OrElse length <= 0 OrElse offset >= fs.Length Then Return Nothing
            length = CInt(Math.Min(length, fs.Length - offset))
            Dim buffer(length - 1) As Byte
            fs.Position = offset
            Dim read = 0
            Do While read < length
                Dim n = fs.Read(buffer, read, length - read)
                If n <= 0 Then Exit Do
                read += n
            Loop
            If trace IsNot Nothing Then trace.BytesRead += read
            If read < length Then Return Nothing
            Return buffer
        End Function

        Private Shared Function EntryValue(entries As Byte(), e As Integer, type As Integer, little As Boolean) As Long
            Select Case type
                Case 3 : Return ReadU16At(entries, e + 8, little)
                Case 4, 13 : Return ReadU32At(entries, e + 8, little)
                Case Else : Return 0
            End Select
        End Function

        Private Shared Function EntryValues(fs As Stream, entries As Byte(), e As Integer, type As Integer,
                                            count As Long, little As Boolean, trace As LocateTrace) As Long()
            Dim size = If(type = 3, 2, If(type = 4 OrElse type = 13, 4, 0))
            If size = 0 OrElse count <= 0 OrElse count > 4096 Then Return Nothing
            Dim total = CInt(count) * size
            Dim raw As Byte()
            Dim start As Integer
            If total <= 4 Then
                raw = entries
                start = e + 8
            Else
                raw = ReadRange(fs, ReadU32At(entries, e + 8, little), total, trace)
                If raw Is Nothing Then Return Nothing
                start = 0
            End If
            Dim values(CInt(count) - 1) As Long
            For i = 0 To CInt(count) - 1
                values(i) = If(size = 2, ReadU16At(raw, start + i * 2, little), ReadU32At(raw, start + i * 4, little))
            Next
            Return values
        End Function

        Private Shared Function ReadU16At(b As Byte(), o As Integer, little As Boolean) As Integer
            Return If(little, b(o) Or (CInt(b(o + 1)) << 8), (CInt(b(o)) << 8) Or b(o + 1))
        End Function

        Private Shared Function ReadU32At(b As Byte(), o As Integer, little As Boolean) As Long
            If little Then
                Return CLng(b(o)) Or (CLng(b(o + 1)) << 8) Or (CLng(b(o + 2)) << 16) Or (CLng(b(o + 3)) << 24)
            End If
            Return (CLng(b(o)) << 24) Or (CLng(b(o + 1)) << 16) Or (CLng(b(o + 2)) << 8) Or CLng(b(o + 3))
        End Function

        ''' <summary>Fuer die Messung: der alte Weg allein, ohne den gezielten davor.</summary>
        Friend Shared Function ScanOnly(filePath As String) As MemoryStream
            Try
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

                ' ALLE Kandidaten sammeln, nicht nur den groessten: der groesste kann die
                ' SENSORAUFNAHME selbst sein (siehe IsSensorFrame), und dann ist der
                ' naechstgroessere das gesuchte Vorschaubild.
                Dim candidates = New List(Of (Start As Integer, Length As Integer, Width As Integer, Height As Integer))()

                Dim i = 0
                Do While i < totalRead - 3
                    If data(i) = &HFF AndAlso data(i + 1) = &HD8 AndAlso data(i + 2) = &HFF Then
                        Dim width = 0, height = 0
                        If TryReadFrameHeader(data, i, width, height) Then
                            Dim jLen = WalkJpegLength(data, i)
                            If jLen >= 8192 AndAlso candidates.Count < MaxCandidates Then
                                candidates.Add((i, jLen, width, height))
                            End If
                        End If
                    End If
                    i += 1
                Loop

                If candidates.Count = 0 Then Return Nothing

                ' Groesste zuerst: fuer die Anzeige zaehlt Aufloesung.
                candidates.Sort(Function(a, b) b.Length.CompareTo(a.Length))
                Dim frame = SensorFrameSize(filePath)
                For Each candidate In candidates
                    If IsSensorFrame(candidate.Width, candidate.Height, frame) Then Continue For
                    Dim result(candidate.Length - 1) As Byte
                    Array.Copy(data, candidate.Start, result, 0, candidate.Length)
                    Return New MemoryStream(result)
                Next
                ' Jeder Fund war die Sensoraufnahme - dann gibt es hier nichts, und die Stufen
                ' darueber entwickeln die Datei.
                Return Nothing
            End Using
        End Function

        ''' <summary>Wie viele Funde hoechstens mitgeschrieben werden. Die Schranke schuetzt nur
        ''' gegen eine Datei, in der zufaellig hunderte Startmarken stehen; echte RAWs tragen eine
        ''' Handvoll eingebetteter Bilder.</summary>
        Private Const MaxCandidates As Integer = 32

        ''' <summary>Die Masse der Sensoraufnahme einer RAW-Datei, oder (0, 0), wenn sie sich nicht
        ''' feststellen lassen. Kostet ein open_file, unter einer Millisekunde, und nur dann, wenn
        ''' der Scanner ueberhaupt etwas gefunden hat.</summary>
        Private Shared Function SensorFrameSize(filePath As String) As (Width As Integer, Height As Integer)
            Try
                Dim facts = RawDecodeService.ReadFileMetadata(filePath)
                If facts Is Nothing Then Return (0, 0)
                Return (facts.RawFrameWidth, facts.RawFrameHeight)
            Catch
                Return (0, 0)
            End Try
        End Function

        ''' <summary>Ist dieser Fund in Wahrheit die SENSORAUFNAHME und kein Vorschaubild?
        '''
        ''' Canons altes CIFF-Format (.crw) legt die Sensordaten als JPEG mit einer gewoehnlichen,
        ''' anzeigbaren Rahmenart ab. Die Markenpruefung greift dort also nicht, und weil diese
        ''' Aufnahme das groesste eingebettete Bild der Datei ist, gewann sie: der Betrachter und
        ''' die Galerie zeigten graues Rauschen statt des Bildes. Gemessen an acht von 34 CRW im
        ''' Pruefbestand (G3, G5, G6, S45, S50, S60, S70, Pro1).
        '''
        ''' Erkannt wird es an der Groesse, und zwar GENAU: die Sensoraufnahme misst den ganzen
        ''' Sensorrahmen einschliesslich des abgedeckten Randes (bei einer G5 2672 x 1968), ein
        ''' Vorschaubild dagegen den sichtbaren Ausschnitt oder weniger (2616 x 1960 und kleiner).
        ''' Eine Toleranz gibt es nicht, damit kein echtes Vorschaubild abgelehnt wird.</summary>
        Private Shared Function IsSensorFrame(width As Integer, height As Integer,
                                              frame As (Width As Integer, Height As Integer)) As Boolean
            If frame.Width < 2 OrElse frame.Height < 2 Then Return False
            If width < 2 OrElse height < 2 Then Return False
            Return width = frame.Width AndAlso height = frame.Height
        End Function

        ''' Wahr, wenn das JPEG an dieser Stelle eine ANZEIGBARE Rahmenart benutzt. Verlustfreies
        ''' JPEG (SOF3, SOF5 bis 7, SOF11, SOF13 bis 15) dient zum Packen der Sensordaten einer RAW
        ''' und laesst sich mit einem gewoehnlichen Bilddecoder nicht lesen.
        '''
        ''' Die MASSE kommen im selben Durchgang heraus - sie stehen im Rahmenkopf, und ein zweiter
        ''' Lauf durch die Markenkette nur dafuer waere die gleiche Arbeit zweimal. Bleiben sie 0,
        ''' liess sich der Kopf nicht auswerten; der Aufrufer darf den Fund dann nicht anhand seiner
        ''' Groesse beurteilen.
        Private Shared Function TryReadFrameHeader(data As Byte(), offset As Integer,
                                                   ByRef width As Integer, ByRef height As Integer) As Boolean
            width = 0
            height = 0
            Dim pos = offset + 2           ' die Startmarke SOI (FF D8) ueberspringen
            Dim limit = Math.Min(data.Length, offset + 8192)
            Do While pos + 3 < limit
                If data(pos) <> &HFF Then Return True   ' unexpected – assume OK
                Dim marker = data(pos + 1)
                If marker >= &HC0 AndAlso marker <= &HCF AndAlso
                   marker <> &HC4 AndAlso marker <> &HC8 AndAlso marker <> &HCC Then
                    ' C0=baseline, C1=extended, C2=progressive → displayable
                    Dim displayable = marker = &HC0 OrElse marker = &HC1 OrElse marker = &HC2
                    ' Der Rahmenkopf: Laenge (2), Genauigkeit (1), Hoehe (2), Breite (2).
                    If displayable AndAlso pos + 8 < data.Length Then
                        height = CInt(data(pos + 5)) * 256 + CInt(data(pos + 6))
                        width = CInt(data(pos + 7)) * 256 + CInt(data(pos + 8))
                    End If
                    Return displayable
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
