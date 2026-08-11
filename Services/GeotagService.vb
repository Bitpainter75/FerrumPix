Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text.RegularExpressions

Namespace Services

    ''' <summary>Schreibt Koordinaten an ein Bild.
    '''
    ''' Gelesen wurde GPS schon immer (<see cref="ExifService"/>, Katalogspalten,
    ''' <see cref="PlaceLookupService"/> fuer den Ortsnamen). Hier ist der Rueckweg.
    '''
    ''' ZWEI ZIELE, nach Format getrennt:
    ''' JPEG bekommt die Koordinate in die Datei selbst, weil das die Datei ist, die weitergegeben
    ''' wird und die jedes fremde Programm liest. Alles andere - RAW, PSD, TIFF, HEIC - bekommt eine
    ''' XMP-Beistelldatei; an eine RAW-Datei gehen wir grundsaetzlich nicht heran. Der Katalog wird
    ''' in beiden Faellen bedient, das macht aber der Aufrufer (LibraryService.SetGpsCoordinates).
    '''
    ''' WARUM DER JPEG-WEG NICHTS VERSCHIEBT: Ein EXIF-Feld mitten in IFD0 einzufuegen waere der
    ''' naive Weg und der gefaehrliche - alle nachfolgenden Offsets im TIFF-Block wandern, und
    ''' Herstellernotizen mit absoluten Zeigern sind danach Schrott. Der Kopf des TIFF-Blocks traegt
    ''' aber einen ZEIGER auf IFD0, und der laesst sich umbiegen: der vorhandene Block bleibt Byte
    ''' fuer Byte liegen, hinten kommen das GPS-IFD und eine Kopie der IFD0-Eintraege dazu, und der
    ''' Kopf zeigt auf die Kopie. Die Eintraege der Kopie sind woertlich uebernommen, ihre Wertzeiger
    ''' deuten also weiter in den unveraenderten Bereich. Der alte IFD0-Rumpf bleibt als toter
    ''' Ballast liegen (ein paar hundert Byte) - das ist der Preis dafuer, dass nichts umgerechnet
    ''' werden muss und damit auch nichts falsch umgerechnet werden kann.</summary>
    ''' <summary>Was aus einem Stapel geworden ist. Getrennt nach Ziel, weil "steht in der Datei"
    ''' und "steht daneben" fuer den Nutzer nicht dasselbe sind.</summary>
    Public Class GeotagBatchResult
        Public Property Total As Integer
        Public Property WrittenToFile As Integer
        Public Property WrittenToSidecar As Integer
        ''' Nur der Katalog war gewuenscht - kein Fehlschlag.
        Public Property CatalogOnly As Integer
        Public Property Failed As New List(Of String)()

        Public ReadOnly Property SucceededCount As Integer
            Get
                Return WrittenToFile + WrittenToSidecar + CatalogOnly
            End Get
        End Property
    End Class

    Public Class GeotagService

        ' TIFF-Feldtypen, wie sie in der EXIF-Spezifikation nummeriert sind.
        Private Const TiffTypeByte As Integer = 1
        Private Const TiffTypeAscii As Integer = 2
        Private Const TiffTypeLong As Integer = 4
        Private Const TiffTypeRational As Integer = 5

        Private Const GpsInfoIfdPointerTag As Integer = &H8825

        ''' Ein IFD mit mehr Eintraegen als das ist keins mehr, sondern eine kaputt gelesene Zahl.
        Private Const MaxIfdEntries As Integer = 512

        ''' Ein JPEG-Segment traegt seine Laenge in zwei Byte, abzueglich der Laengenbytes selbst
        ''' und der Kennung "Exif" mit ihren beiden Nullbytes.
        Private Const MaxTiffBlockInJpeg As Integer = 65535 - 2 - 6

        ''' <summary>Wohin die Koordinate tatsaechlich gegangen ist. Der Aufrufer sagt es dem
        ''' Nutzer - "steht in der Datei" und "steht daneben" ist ein Unterschied, den man beim
        ''' Weitergeben eines Bildes merkt.</summary>
        Public Enum GeotagTarget
            None = 0
            EmbeddedExif = 1
            XmpSidecar = 2
        End Enum

        Public Class GeotagWriteResult
            Public Property Success As Boolean
            Public Property Target As GeotagTarget = GeotagTarget.None
            ''' Bei <see cref="GeotagTarget.XmpSidecar"/> die geschriebene Beistelldatei.
            Public Property SidecarPath As String = ""
            ''' Warum es nicht ging - fuer das Diagnoselog, nicht fuer die Oberflaeche.
            Public Property FailureReason As String = ""
        End Class

        Public Shared Function IsJpegPath(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Dim extension = IO.Path.GetExtension(path).ToLowerInvariant()
            Return extension = ".jpg" OrElse extension = ".jpeg"
        End Function

        ' ---------------------------------------------------------------------------------------
        ' Eingabe lesen: EIN Feld statt drei Betriebsarten
        ' ---------------------------------------------------------------------------------------

        ''' Grad, Minute, Sekunde - mit Gradzeichen, damit die Erkennung nicht raet.
        Private Shared ReadOnly DegreeMinuteSecondPattern As New Regex(
            "(\d{1,3})\s*°\s*(?:(\d{1,2}(?:[.,]\d+)?)\s*['′]\s*(?:(\d{1,2}(?:[.,]\d+)?)\s*(?:""|″|''))?)?",
            RegexOptions.Compiled)

        ''' Eine Zahl mit Vorzeichen und Nachkommastellen, irgendwo im Text.
        Private Shared ReadOnly NumberPattern As New Regex("[-+]?\d{1,3}(?:[.,]\d+)?", RegexOptions.Compiled)

        ''' <summary>Liest eine Koordinate aus dem, was jemand eintippt oder hineinwirft.
        '''
        ''' EIN Feld, kein Umschalter: der Nutzer soll nicht erst sagen muessen, in welcher Form er
        ''' gleich schreibt. Verstanden werden
        ''' Dezimalgrad ("48.137154, 11.576124" und mit deutschem Komma "48,137154 11,576124"),
        ''' Grad-Minute-Sekunde ("48°8'13.8""N 11°34'34.6""E") und eine aus einer Karte kopierte
        ''' Zeile, in der die Zahlen zwischen anderem stehen.
        '''
        ''' DIE FALLE BEI EINER KOPIERTEN ZEILE sind die anderen Zahlen darin - eine Kartenadresse
        ''' traegt oft die Zoomstufe direkt vor der Koordinate ("#map=15/48.1372/11.5761"). Deshalb
        ''' die Regel: steht die Koordinate ALLEIN im Feld, gehen auch runde Zahlen durch; steht sie
        ''' zwischen anderem Text, muessen beide Werte Nachkommastellen haben. Eine Zoomstufe hat
        ''' keine, ein Aufnahmeort praktisch immer.</summary>
        Public Shared Function TryParseCoordinates(text As String, ByRef latitude As Double, ByRef longitude As Double) As Boolean
            latitude = 0 : longitude = 0
            If String.IsNullOrWhiteSpace(text) Then Return False
            Dim input = text.Trim()

            If TryParseDegreeMinuteSecond(input, latitude, longitude) Then Return True
            Return TryParseDecimalPair(input, latitude, longitude)
        End Function

        Private Shared Function TryParseDegreeMinuteSecond(input As String, ByRef latitude As Double, ByRef longitude As Double) As Boolean
            If input.IndexOf("°"c) < 0 Then Return False
            Dim matches = DegreeMinuteSecondPattern.Matches(input)
            If matches.Count <> 2 Then Return False

            Dim values(1) As Double
            Dim signs(1) As Integer
            Dim isLatitude(1) As Boolean
            Dim anyHemisphere = False

            ' VOR ODER HINTER, aber fuer BEIDE Werte gleich. Beide Schreibweisen sind im Umlauf
            ' ("N 48°... E 11°..." und "48°...N 11°...E"), und wer je Wert einzeln in beide
            ' Richtungen schaut, greift daneben: hinter dem ersten Wert der Vorlaufform steht schon
            ' der Buchstabe des ZWEITEN. Deshalb wird die Form einmal am ersten Fundstueck bestimmt.
            Dim prefixNotation = HemisphereBefore(input, matches(0).Index) <> ChrW(0)

            For i = 0 To 1
                Dim m = matches(i)
                Dim degrees = ParseNumber(m.Groups(1).Value)
                Dim minutes = If(m.Groups(2).Success, ParseNumber(m.Groups(2).Value), 0.0)
                Dim seconds = If(m.Groups(3).Success, ParseNumber(m.Groups(3).Value), 0.0)
                If minutes >= 60.0 OrElse seconds >= 60.0 Then Return False
                values(i) = degrees + minutes / 60.0 + seconds / 3600.0

                Dim letter = If(prefixNotation,
                                HemisphereBefore(input, m.Index),
                                HemisphereAfter(input, m.Index + m.Length))
                signs(i) = If(letter = "S"c OrElse letter = "W"c, -1, 1)
                isLatitude(i) = (letter = "N"c OrElse letter = "S"c)
                If letter <> ChrW(0) Then anyHemisphere = True
            Next

            ' Ohne jede Himmelsrichtung gilt die uebliche Reihenfolge: erst Breite, dann Laenge.
            Dim firstIsLatitude = If(anyHemisphere, isLatitude(0), True)
            Dim lat = If(firstIsLatitude, values(0) * signs(0), values(1) * signs(1))
            Dim lon = If(firstIsLatitude, values(1) * signs(1), values(0) * signs(0))
            If Not IsValidCoordinate(lat, lon) Then Return False
            latitude = lat : longitude = lon
            Return True
        End Function

        ''' <summary>Der Himmelsrichtungsbuchstabe unmittelbar HINTER einer Stelle, oder das
        ''' Nullzeichen. Dazwischen darf nur Leerraum stehen.</summary>
        Private Shared Function HemisphereAfter(input As String, endIndex As Integer) As Char
            Dim i = endIndex
            While i < input.Length AndAlso Char.IsWhiteSpace(input(i))
                i += 1
            End While
            If i >= input.Length Then Return ChrW(0)
            Return AsHemisphere(input(i))
        End Function

        ''' <summary>Dasselbe unmittelbar DAVOR.</summary>
        Private Shared Function HemisphereBefore(input As String, startIndex As Integer) As Char
            Dim i = startIndex - 1
            While i >= 0 AndAlso Char.IsWhiteSpace(input(i))
                i -= 1
            End While
            If i < 0 Then Return ChrW(0)
            Return AsHemisphere(input(i))
        End Function

        Private Shared Function AsHemisphere(value As Char) As Char
            Dim c = Char.ToUpperInvariant(value)
            If c = "N"c OrElse c = "S"c OrElse c = "E"c OrElse c = "W"c Then Return c
            Return ChrW(0)
        End Function

        Private Shared Function TryParseDecimalPair(input As String, ByRef latitude As Double, ByRef longitude As Double) As Boolean
            ' WELCHES ZEICHEN TRENNT UND WELCHES IST DAS KOMMA: steht irgendwo ein Punkt zwischen
            ' zwei Ziffern, dann ist der Punkt das Dezimalzeichen und das Komma trennt. Sonst ist
            ' das Komma das Dezimalzeichen, und getrennt wird mit Leerraum oder Semikolon. Anders
            ' waeren "48.13, 11.57" und "48,13 11,57" nicht auseinanderzuhalten.
            Dim normalized = input
            If Regex.IsMatch(input, "\d\.\d") Then
                normalized = input.Replace(","c, " "c)
            Else
                normalized = Regex.Replace(input, "(\d),(\d)", "$1.$2")
            End If

            Dim numbers = NumberPattern.Matches(normalized)
            If numbers.Count < 2 Then Return False

            ' Steht die Koordinate allein im Feld, darf sie rund sein. Zwischen anderem Text nicht -
            ' sonst faengt die Zoomstufe einer Kartenadresse die Erkennung ab.
            Dim isBareInput = numbers.Count = 2 AndAlso
                              Regex.IsMatch(normalized.Trim(), "^[-+]?\d{1,3}(?:\.\d+)?\s*[;/ ]\s*[-+]?\d{1,3}(?:\.\d+)?$")

            For i = 0 To numbers.Count - 2
                Dim firstText = numbers(i).Value
                Dim secondText = numbers(i + 1).Value
                If Not isBareInput AndAlso
                   (firstText.IndexOf("."c) < 0 OrElse secondText.IndexOf("."c) < 0) Then Continue For

                Dim lat = ParseNumber(firstText)
                Dim lon = ParseNumber(secondText)
                If Not IsValidCoordinate(lat, lon) Then Continue For
                latitude = lat : longitude = lon
                Return True
            Next
            Return False
        End Function

        Private Shared Function ParseNumber(text As String) As Double
            Dim value As Double
            If Double.TryParse(text.Replace(","c, "."c), NumberStyles.Float, CultureInfo.InvariantCulture, value) Then Return value
            Return 0.0
        End Function

        ''' <summary>Die Koordinate so, wie die Anwendung sie sonst auch zeigt (siehe ExifService) -
        ''' damit die Vorschau im Dialog und die Zeile in der Infoleiste dieselbe Sprache sprechen.</summary>
        ''' <summary>Die Adresse, unter der OpenStreetMap diesen Punkt zeigt: Marke gesetzt, Karte
        ''' herangezoomt. Die Zahlen gehen mit dem PUNKT als Trenner hinaus - eine deutsche
        ''' Ländereinstellung schriebe sonst "50,809", und die Karte landete im Nirgendwo.</summary>
        Public Shared Function BuildOpenStreetMapUrl(latitude As Double, longitude As Double,
                                                     Optional zoom As Integer = 15) As String
            Dim lat = latitude.ToString("0.######", Globalization.CultureInfo.InvariantCulture)
            Dim lon = longitude.ToString("0.######", Globalization.CultureInfo.InvariantCulture)
            Return $"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map={zoom}/{lat}/{lon}"
        End Function

        Public Shared Function FormatCoordinates(latitude As Double, longitude As Double) As String
            Return $"{latitude:F5}°, {longitude:F5}°"
        End Function

        ''' <summary>True, wenn die Koordinate ueberhaupt eine sein kann. Ein vertauschtes
        ''' Breiten-/Laengenpaar faellt damit nicht auf, ein Tippfehler um eine Stelle schon.</summary>
        Public Shared Function IsValidCoordinate(latitude As Double, longitude As Double) As Boolean
            If Double.IsNaN(latitude) OrElse Double.IsNaN(longitude) Then Return False
            If Double.IsInfinity(latitude) OrElse Double.IsInfinity(longitude) Then Return False
            Return latitude >= -90.0 AndAlso latitude <= 90.0 AndAlso
                   longitude >= -180.0 AndAlso longitude <= 180.0
        End Function

        ''' <summary>Schreibt die Koordinate an die Datei: ins JPEG hinein, sonst in die
        ''' Beistelldatei. Der Katalog wird hier NICHT angefasst.
        '''
        ''' <paramref name="createSidecarIfMissing"/> entscheidet, ob fuer ein Bild ohne
        ''' Beistelldatei eine neue angelegt werden darf. Wie bei der Bewertung ist das eine
        ''' bewusste Entscheidung des Nutzers und keine Nebenwirkung.</summary>
        Public Shared Function WriteCoordinates(imagePath As String,
                                                latitude As Double,
                                                longitude As Double,
                                                Optional altitudeMeters As Double? = Nothing,
                                                Optional createSidecarIfMissing As Boolean = True) As GeotagWriteResult
            Dim result As New GeotagWriteResult()
            If String.IsNullOrWhiteSpace(imagePath) Then
                result.FailureReason = "Kein Pfad"
                Return result
            End If
            If Not IsValidCoordinate(latitude, longitude) Then
                result.FailureReason = "Koordinate ausserhalb des gueltigen Bereichs"
                Return result
            End If
            If Not File.Exists(imagePath) Then
                result.FailureReason = "Datei nicht gefunden"
                Return result
            End If

            ' Der Weg in die Datei bekommt sein EIGENES Netz: scheitert er, ist die Beistelldatei
            ' immer noch besser als gar nichts. Laege der Fang weiter aussen, faenge eine Ausnahme
            ' beim Schreiben den Ausweichweg gleich mit ab.
            If IsJpegPath(imagePath) Then
                result.Target = GeotagTarget.EmbeddedExif
                Try
                    If WriteJpegCoordinates(imagePath, latitude, longitude, altitudeMeters, result) Then
                        result.Success = True
                        result.FailureReason = ""
                        Return result
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("Geotag.WriteJpeg", ex)
                    SetReason(result, ex.Message)
                End Try
            End If

            Try
                result.Target = GeotagTarget.XmpSidecar
                Dim sidecarPath = ExifService.WriteXmpGpsSidecar(imagePath, latitude, longitude, altitudeMeters, createSidecarIfMissing)
                If String.IsNullOrEmpty(sidecarPath) Then
                    SetReason(result, "Beistelldatei nicht geschrieben")
                    result.Success = False
                    Return result
                End If
                result.SidecarPath = sidecarPath
                result.Success = True
                result.FailureReason = ""
                Return result
            Catch ex As Exception
                DiagnosticLogService.LogException("Geotag.WriteCoordinates", ex)
                result.Success = False
                SetReason(result, ex.Message)
                Return result
            End Try
        End Function

        ''' <summary>Nimmt den Aufnahmeort wieder weg - aus der Datei und aus der Beistelldatei.
        ''' Der Katalog gehoert dem Aufrufer (LibraryService.ClearGpsCoordinates).
        '''
        ''' ZWEI SCHRITTE, und der zweite ist der wichtige: der Verweis auf das GPS-IFD faellt weg,
        ''' damit kein Leser die Koordinate mehr findet. Danach werden die Bytes des GPS-Blocks
        ''' selbst UEBERSCHRIEBEN. Ohne den zweiten Schritt bliebe die Koordinate als toter Ballast
        ''' in der Datei stehen - unsichtbar fuer jedes Programm, aber lesbar fuer jeden, der sich
        ''' die Datei ansieht. Wer einen Aufnahmeort loescht, will genau das nicht.</summary>
        Public Shared Function RemoveCoordinates(imagePath As String) As GeotagWriteResult
            Dim result As New GeotagWriteResult()
            If String.IsNullOrWhiteSpace(imagePath) Then
                result.FailureReason = "Kein Pfad"
                Return result
            End If

            Dim removedSomething = False

            If IsJpegPath(imagePath) AndAlso File.Exists(imagePath) Then
                Try
                    Dim bytes = File.ReadAllBytes(imagePath)
                    Dim rebuilt = BuildJpegWithoutGps(bytes)
                    If rebuilt IsNot Nothing Then
                        removedSomething = ImageProcessor.WriteAllBytesAtomic(imagePath, rebuilt)
                        result.Target = GeotagTarget.EmbeddedExif
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("Geotag.RemoveFromJpeg", ex)
                    SetReason(result, ex.Message)
                End Try
            End If

            ' Die Beistelldatei IMMER mitnehmen: an einem JPEG kann trotzdem eine liegen, und ein
            ' halb geloeschter Ort waere schlimmer als keiner.
            Try
                If ExifService.RemoveXmpGpsSidecar(imagePath) Then
                    removedSomething = True
                    If result.Target = GeotagTarget.None Then result.Target = GeotagTarget.XmpSidecar
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Geotag.RemoveFromSidecar", ex)
            End Try

            result.Success = removedSomething
            If Not removedSomething Then SetReason(result, "Es stand kein Aufnahmeort in der Datei")
            Return result
        End Function

        ''' <summary>Die JPEG-Bytes ohne GPS, oder Nothing wenn gar keins drinstand.</summary>
        Friend Shared Function BuildJpegWithoutGps(bytes As Byte()) As Byte()
            If bytes Is Nothing OrElse bytes.Length < 4 OrElse bytes(0) <> &HFF OrElse bytes(1) <> &HD8 Then Return Nothing

            Dim offset = 2
            While offset + 4 <= bytes.Length
                If bytes(offset) <> &HFF Then Exit While
                Dim marker = bytes(offset + 1)
                If marker = &HDA OrElse marker = &HD9 Then Exit While
                If marker = &H1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) Then
                    offset += 2
                    Continue While
                End If

                Dim length = ReadUInt16BigEndian(bytes, offset + 2)
                If length < 2 OrElse offset + 2 + length > bytes.Length Then Exit While
                Dim totalLength = 2 + length

                If marker = &HE1 AndAlso IsExifSegment(bytes, offset, totalLength) Then
                    Dim tiff(totalLength - 10 - 1) As Byte
                    Buffer.BlockCopy(bytes, offset + 10, tiff, 0, tiff.Length)
                    Dim stripped = RemoveGpsFromTiff(tiff)
                    If stripped Is Nothing Then Return Nothing

                    Dim segment = BuildExifSegment(stripped)
                    Dim output As New List(Of Byte)(bytes.Length + segment.Length)
                    output.AddRange(bytes.Take(offset))
                    output.AddRange(segment)
                    output.AddRange(bytes.Skip(offset + totalLength))
                    Return output.ToArray()
                End If

                offset += totalLength
            End While
            Return Nothing
        End Function

        ''' <summary>Derselbe Anbau wie beim Setzen, nur ohne den GPS-Eintrag: eine Kopie von IFD0
        ''' hinten dran, der Kopfzeiger darauf. Zusaetzlich werden die Bytes des alten GPS-Blocks
        ''' genullt. Nothing, wenn gar kein GPS drinsteht - dann bleibt die Datei unangetastet.</summary>
        Friend Shared Function RemoveGpsFromTiff(tiff As Byte()) As Byte()
            If tiff Is Nothing OrElse tiff.Length < 8 Then Return Nothing

            Dim littleEndian = tiff(0) = AscW("I"c) AndAlso tiff(1) = AscW("I"c)
            Dim bigEndian = tiff(0) = AscW("M"c) AndAlso tiff(1) = AscW("M"c)
            If Not littleEndian AndAlso Not bigEndian Then Return Nothing
            If ReadUInt16(tiff, 2, littleEndian) <> 42 Then Return Nothing

            Dim ifd0 = CInt(ReadUInt32(tiff, 4, littleEndian))
            If ifd0 < 8 OrElse ifd0 + 2 > tiff.Length Then Return Nothing
            Dim entryCount = ReadUInt16(tiff, ifd0, littleEndian)
            If entryCount <= 0 OrElse entryCount > MaxIfdEntries Then Return Nothing
            Dim entriesEnd = ifd0 + 2 + entryCount * 12
            If entriesEnd + 4 > tiff.Length Then Return Nothing

            Dim gpsIfdOffset = -1
            Dim entries As New List(Of Byte())()
            For i = 0 To entryCount - 1
                Dim entry(11) As Byte
                Buffer.BlockCopy(tiff, ifd0 + 2 + i * 12, entry, 0, 12)
                If ReadUInt16(entry, 0, littleEndian) = GpsInfoIfdPointerTag Then
                    gpsIfdOffset = CInt(ReadUInt32(entry, 8, littleEndian))
                    Continue For   ' genau dieser Eintrag faellt weg
                End If
                entries.Add(entry)
            Next
            If gpsIfdOffset < 0 Then Return Nothing

            Dim output As New List(Of Byte)(tiff.Length + 64)
            output.AddRange(tiff)
            ' Den alten GPS-Block ausloeschen, solange er sicher zu vermessen ist.
            ZeroGpsIfd(output, gpsIfdOffset, littleEndian)
            PadToEven(output)

            Dim newIfd0Offset = output.Count
            AppendUInt16(output, entries.Count, littleEndian)
            For Each entry In entries
                output.AddRange(entry)
            Next
            For i = 0 To 3
                output.Add(tiff(entriesEnd + i))
            Next

            Dim rebuilt = output.ToArray()
            WriteUInt32(rebuilt, 4, CUInt(newIfd0Offset), littleEndian)
            Return rebuilt
        End Function

        ''' <summary>Nullt das GPS-Verzeichnis und die Bereiche, auf die seine Eintraege zeigen.
        '''
        ''' Nur mit Bodenhaftung: alles muss innerhalb des Blocks liegen und die Anzahl der
        ''' Eintraege plausibel sein. Passt etwas nicht, wird gar nichts genullt - der Verweis ist
        ''' dann schon weg, und ein zerschossener EXIF-Block waere der schlechtere Tausch.</summary>
        Private Shared Sub ZeroGpsIfd(buffer As List(Of Byte), gpsIfdOffset As Integer, littleEndian As Boolean)
            If gpsIfdOffset < 8 OrElse gpsIfdOffset + 2 > buffer.Count Then Return
            Dim raw = buffer.ToArray()
            Dim count = ReadUInt16(raw, gpsIfdOffset, littleEndian)
            If count <= 0 OrElse count > 64 Then Return
            Dim directoryEnd = gpsIfdOffset + 2 + count * 12 + 4
            If directoryEnd > buffer.Count Then Return

            Dim regions As New List(Of (Start As Integer, Length As Integer))()
            regions.Add((gpsIfdOffset, directoryEnd - gpsIfdOffset))
            For i = 0 To count - 1
                Dim entry = gpsIfdOffset + 2 + i * 12
                Dim fieldType = ReadUInt16(raw, entry + 2, littleEndian)
                Dim itemCount = CLng(ReadUInt32(raw, entry + 4, littleEndian))
                Dim unitSize = FieldTypeSize(fieldType)
                If unitSize = 0 OrElse itemCount <= 0 OrElse itemCount > 1024 Then Continue For
                Dim byteLength = itemCount * unitSize
                If byteLength <= 4 Then Continue For   ' steht im Eintrag selbst, wird schon genullt
                Dim valueOffset = CLng(ReadUInt32(raw, entry + 8, littleEndian))
                If valueOffset < 8 OrElse valueOffset + byteLength > buffer.Count Then Continue For
                regions.Add((CInt(valueOffset), CInt(byteLength)))
            Next

            For Each region In regions
                For i = region.Start To region.Start + region.Length - 1
                    buffer(i) = 0
                Next
            Next
        End Sub

        Private Shared Function FieldTypeSize(fieldType As Integer) As Integer
            Select Case fieldType
                Case 1, 2, 6, 7 : Return 1        ' BYTE, ASCII, SBYTE, UNDEFINED
                Case 3, 8 : Return 2              ' SHORT, SSHORT
                Case 4, 9, 11 : Return 4          ' LONG, SLONG, FLOAT
                Case 5, 10, 12 : Return 8         ' RATIONAL, SRATIONAL, DOUBLE
                Case Else : Return 0
            End Select
        End Function

        ' ---------------------------------------------------------------------------------------
        ' JPEG: das APP1-Segment mit dem EXIF-Block ersetzen oder eins einsetzen
        ' ---------------------------------------------------------------------------------------

        Private Shared Function WriteJpegCoordinates(imagePath As String,
                                                     latitude As Double,
                                                     longitude As Double,
                                                     altitudeMeters As Double?,
                                                     result As GeotagWriteResult) As Boolean
            Dim bytes = File.ReadAllBytes(imagePath)
            Dim rebuilt = BuildJpegWithGps(bytes, latitude, longitude, altitudeMeters, result)
            If rebuilt Is Nothing Then Return False
            Return ImageProcessor.WriteAllBytesAtomic(imagePath, rebuilt)
        End Function

        ''' <summary>Die fertigen JPEG-Bytes mit gesetzter Koordinate, oder Nothing wenn der
        ''' Aufbau der Datei nicht sicher gelesen werden konnte. Getrennt vom Schreiben, damit der
        ''' Pruefstand den Umbau ohne Datei pruefen kann.</summary>
        Friend Shared Function BuildJpegWithGps(bytes As Byte(),
                                                latitude As Double,
                                                longitude As Double,
                                                altitudeMeters As Double?,
                                                result As GeotagWriteResult) As Byte()
            If bytes Is Nothing OrElse bytes.Length < 4 OrElse bytes(0) <> &HFF OrElse bytes(1) <> &HD8 Then
                SetReason(result, "Keine JPEG-Datei")
                Return Nothing
            End If

            Dim exifStart = -1
            Dim exifLength = 0
            Dim insertAt = 2

            ' Ein Durchlauf durch die Segmente: den EXIF-Block finden und zugleich die Stelle
            ' merken, an der ein neuer stehen muesste - hinter JFIF (APP0), vor allem anderen.
            Dim offset = 2
            Dim pastLeadingApp0 = False
            While offset + 4 <= bytes.Length
                If bytes(offset) <> &HFF Then Exit While
                Dim marker = bytes(offset + 1)
                If marker = &HDA OrElse marker = &HD9 Then Exit While
                If marker = &H1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) Then
                    offset += 2
                    Continue While
                End If

                Dim length = ReadUInt16BigEndian(bytes, offset + 2)
                If length < 2 OrElse offset + 2 + length > bytes.Length Then Exit While
                Dim totalLength = 2 + length

                If Not pastLeadingApp0 Then
                    If marker = &HE0 OrElse marker = &HEE Then
                        insertAt = offset + totalLength
                    Else
                        pastLeadingApp0 = True
                    End If
                End If

                If marker = &HE1 AndAlso IsExifSegment(bytes, offset, totalLength) Then
                    exifStart = offset
                    exifLength = totalLength
                    Exit While
                End If

                offset += totalLength
            End While

            ' Den TIFF-Block bauen: entweder den vorhandenen erweitern oder einen neuen anlegen.
            Dim tiff As Byte()
            If exifStart >= 0 Then
                Dim existing(exifLength - 10 - 1) As Byte
                Buffer.BlockCopy(bytes, exifStart + 10, existing, 0, existing.Length)

                ' ERST den vorhandenen Aufnahmeort ausloeschen, dann den neuen anhaengen.
                '
                ' BEFUND an einem echten Bild: ohne diesen Schritt standen danach ZWEI Orte in der
                ' Datei. Das Anhaengen laesst den alten Block naemlich unberuehrt liegen und biegt
                ' nur den Verweis der KOPIE von IFD0 um - der urspruengliche IFD0 bleibt samt seinem
                ' alten Verweis im Block stehen. Wer ihm folgt, findet weiterhin den alten Ort, und
                ' selbst wer nur die Bytes durchsucht, liest die alte Koordinate im Klartext. Genau
                ' das soll beim Loeschen ausdruecklich nicht passieren (siehe RemoveCoordinates) -
                ' beim Setzen gilt dasselbe Versprechen.
                Dim ohneAltenOrt = RemoveGpsFromTiff(existing)
                If ohneAltenOrt IsNot Nothing Then existing = ohneAltenOrt

                tiff = AppendGpsToTiff(existing, latitude, longitude, altitudeMeters)
                If tiff Is Nothing Then
                    SetReason(result, "EXIF-Block nicht lesbar")
                    Return Nothing
                End If
            Else
                tiff = CreateTiffWithGps(latitude, longitude, altitudeMeters)
            End If

            If tiff.Length > MaxTiffBlockInJpeg Then
                ' Kein halber Schreibvorgang: lieber gar nicht in die Datei als ein abgeschnittenes
                ' Segment. Der Aufrufer weicht dann auf die Beistelldatei aus.
                SetReason(result, "EXIF-Block passt nicht mehr in ein JPEG-Segment")
                Return Nothing
            End If

            Dim segment = BuildExifSegment(tiff)
            Dim output As New List(Of Byte)(bytes.Length + segment.Length)
            If exifStart >= 0 Then
                output.AddRange(bytes.Take(exifStart))
                output.AddRange(segment)
                output.AddRange(bytes.Skip(exifStart + exifLength))
            Else
                output.AddRange(bytes.Take(insertAt))
                output.AddRange(segment)
                output.AddRange(bytes.Skip(insertAt))
            End If
            Return output.ToArray()
        End Function

        Private Shared Sub SetReason(result As GeotagWriteResult, reason As String)
            If result IsNot Nothing AndAlso String.IsNullOrEmpty(result.FailureReason) Then
                result.FailureReason = reason
            End If
        End Sub

        Private Shared Function IsExifSegment(bytes As Byte(), offset As Integer, totalLength As Integer) As Boolean
            If totalLength < 12 OrElse offset + 12 > bytes.Length Then Return False
            Return bytes(offset + 4) = AscW("E"c) AndAlso bytes(offset + 5) = AscW("x"c) AndAlso
                   bytes(offset + 6) = AscW("i"c) AndAlso bytes(offset + 7) = AscW("f"c) AndAlso
                   bytes(offset + 8) = 0 AndAlso bytes(offset + 9) = 0
        End Function

        Private Shared Function BuildExifSegment(tiff As Byte()) As Byte()
            Dim payloadLength = 6 + tiff.Length
            Dim segment(payloadLength + 3) As Byte
            segment(0) = &HFF
            segment(1) = &HE1
            Dim length = payloadLength + 2
            segment(2) = CByte((length >> 8) And &HFF)
            segment(3) = CByte(length And &HFF)
            segment(4) = CByte(AscW("E"c))
            segment(5) = CByte(AscW("x"c))
            segment(6) = CByte(AscW("i"c))
            segment(7) = CByte(AscW("f"c))
            segment(8) = 0
            segment(9) = 0
            Buffer.BlockCopy(tiff, 0, segment, 10, tiff.Length)
            Return segment
        End Function

        ' ---------------------------------------------------------------------------------------
        ' Der TIFF-Block
        ' ---------------------------------------------------------------------------------------

        ''' <summary>Haengt GPS an einen vorhandenen TIFF-Block an, ohne ein einziges vorhandenes
        ''' Byte zu verschieben. Nothing, wenn der Block nicht sicher gelesen werden kann - dann
        ''' wird er lieber gar nicht angefasst.</summary>
        Friend Shared Function AppendGpsToTiff(tiff As Byte(),
                                               latitude As Double,
                                               longitude As Double,
                                               altitudeMeters As Double?) As Byte()
            If tiff Is Nothing OrElse tiff.Length < 8 Then Return Nothing

            Dim littleEndian = tiff(0) = AscW("I"c) AndAlso tiff(1) = AscW("I"c)
            Dim bigEndian = tiff(0) = AscW("M"c) AndAlso tiff(1) = AscW("M"c)
            If Not littleEndian AndAlso Not bigEndian Then Return Nothing
            If ReadUInt16(tiff, 2, littleEndian) <> 42 Then Return Nothing

            Dim ifd0 = CLng(ReadUInt32(tiff, 4, littleEndian))
            If ifd0 < 8 OrElse ifd0 + 2 > tiff.Length Then Return Nothing
            Dim entryCount = ReadUInt16(tiff, CInt(ifd0), littleEndian)
            If entryCount > MaxIfdEntries Then Return Nothing
            Dim entriesEnd = CInt(ifd0) + 2 + entryCount * 12
            If entriesEnd + 4 > tiff.Length Then Return Nothing

            Dim output As New List(Of Byte)(tiff.Length + 256)
            output.AddRange(tiff)
            PadToEven(output)

            ' Erst das GPS-IFD, weil die Kopie von IFD0 seine Adresse braucht.
            Dim gpsIfdOffset = output.Count
            output.AddRange(BuildGpsIfd(BuildGpsFields(latitude, longitude, altitudeMeters, littleEndian),
                                        gpsIfdOffset, littleEndian))
            PadToEven(output)

            Dim entries As New List(Of Byte())()
            Dim pointerReplaced = False
            For i = 0 To entryCount - 1
                Dim entry(11) As Byte
                Buffer.BlockCopy(tiff, CInt(ifd0) + 2 + i * 12, entry, 0, 12)
                ' Ein schon vorhandener Verweis wird umgebogen statt ein zweiter angelegt: zwei
                ' Eintraege mit derselben Tag-Nummer sind ein kaputtes IFD.
                If ReadUInt16(entry, 0, littleEndian) = GpsInfoIfdPointerTag Then
                    WriteUInt16(entry, 2, TiffTypeLong, littleEndian)
                    WriteUInt32(entry, 4, 1UI, littleEndian)
                    WriteUInt32(entry, 8, CUInt(gpsIfdOffset), littleEndian)
                    pointerReplaced = True
                End If
                entries.Add(entry)
            Next

            If Not pointerReplaced Then
                Dim entry(11) As Byte
                WriteUInt16(entry, 0, GpsInfoIfdPointerTag, littleEndian)
                WriteUInt16(entry, 2, TiffTypeLong, littleEndian)
                WriteUInt32(entry, 4, 1UI, littleEndian)
                WriteUInt32(entry, 8, CUInt(gpsIfdOffset), littleEndian)

                ' Die Eintraege eines IFD stehen nach Tag-Nummer aufsteigend; der Verweis gehoert
                ' an seinen Platz, nicht ans Ende.
                Dim insertAt = entries.Count
                For i = 0 To entries.Count - 1
                    If ReadUInt16(entries(i), 0, littleEndian) > GpsInfoIfdPointerTag Then
                        insertAt = i
                        Exit For
                    End If
                Next
                entries.Insert(insertAt, entry)
            End If

            Dim newIfd0Offset = output.Count
            AppendUInt16(output, entries.Count, littleEndian)
            For Each entry In entries
                output.AddRange(entry)
            Next
            ' Der Verweis auf IFD1 - das eingebettete Vorschaubild - wird woertlich uebernommen.
            ' Er zeigt in den unveraenderten Bereich und stimmt deshalb weiterhin.
            For i = 0 To 3
                output.Add(tiff(entriesEnd + i))
            Next

            Dim rebuilt = output.ToArray()
            WriteUInt32(rebuilt, 4, CUInt(newIfd0Offset), littleEndian)
            Return rebuilt
        End Function

        ''' <summary>Ein TIFF-Block, der nur die Koordinate traegt - fuer Bilder ohne jedes EXIF.
        ''' Kleinschreibendes "II", weil das die verbreitete Form ist und der Block ohnehin neu ist.</summary>
        Friend Shared Function CreateTiffWithGps(latitude As Double,
                                                 longitude As Double,
                                                 altitudeMeters As Double?) As Byte()
            Const littleEndian As Boolean = True
            Dim output As New List(Of Byte)(128)
            output.Add(CByte(AscW("I"c)))
            output.Add(CByte(AscW("I"c)))
            AppendUInt16(output, 42, littleEndian)
            AppendUInt32(output, 8UI, littleEndian)

            ' IFD0 liegt auf 8 und traegt genau einen Eintrag: 2 + 12 + 4 Byte, das GPS-IFD
            ' beginnt also auf 26.
            Const gpsIfdOffset As Integer = 26
            AppendUInt16(output, 1, littleEndian)
            AppendUInt16(output, GpsInfoIfdPointerTag, littleEndian)
            AppendUInt16(output, TiffTypeLong, littleEndian)
            AppendUInt32(output, 1UI, littleEndian)
            AppendUInt32(output, CUInt(gpsIfdOffset), littleEndian)
            AppendUInt32(output, 0UI, littleEndian)

            output.AddRange(BuildGpsIfd(BuildGpsFields(latitude, longitude, altitudeMeters, littleEndian),
                                        gpsIfdOffset, littleEndian))
            Return output.ToArray()
        End Function

        ' ---------------------------------------------------------------------------------------
        ' Das GPS-IFD
        ' ---------------------------------------------------------------------------------------

        Private Structure GpsField
            Public Tag As Integer
            Public FieldType As Integer
            Public Count As Integer
            ''' Die vollstaendigen Nutzdaten. Bis vier Byte stehen sie im Eintrag selbst,
            ''' darueber hinaus im Wertebereich hinter dem Verzeichnis.
            Public Data As Byte()
        End Structure

        Private Shared Function BuildGpsFields(latitude As Double,
                                               longitude As Double,
                                               altitudeMeters As Double?,
                                               littleEndian As Boolean) As List(Of GpsField)
            Dim fields As New List(Of GpsField)()

            ' 2.3.0.0 - die Fassung, die Koordinaten in dieser Form beschreibt.
            fields.Add(New GpsField With {.Tag = &H0, .FieldType = TiffTypeByte, .Count = 4,
                                          .Data = New Byte() {2, 3, 0, 0}})
            fields.Add(AsciiField(&H1, If(latitude >= 0.0, "N", "S")))
            fields.Add(New GpsField With {.Tag = &H2, .FieldType = TiffTypeRational, .Count = 3,
                                          .Data = EncodeCoordinate(Math.Abs(latitude), littleEndian)})
            fields.Add(AsciiField(&H3, If(longitude >= 0.0, "E", "W")))
            fields.Add(New GpsField With {.Tag = &H4, .FieldType = TiffTypeRational, .Count = 3,
                                          .Data = EncodeCoordinate(Math.Abs(longitude), littleEndian)})

            If altitudeMeters.HasValue AndAlso Not Double.IsNaN(altitudeMeters.Value) AndAlso
               Not Double.IsInfinity(altitudeMeters.Value) Then
                ' Unter dem Meeresspiegel wird nicht negativ gerechnet, sondern ueber das
                ' Vorzeichenfeld ausgedrueckt - der Betrag selbst ist immer positiv.
                fields.Add(New GpsField With {.Tag = &H5, .FieldType = TiffTypeByte, .Count = 1,
                                              .Data = New Byte() {CByte(If(altitudeMeters.Value < 0.0, 1, 0))}})
                Dim centimeters = CLng(Math.Round(Math.Abs(altitudeMeters.Value) * 100.0))
                fields.Add(New GpsField With {.Tag = &H6, .FieldType = TiffTypeRational, .Count = 1,
                                              .Data = EncodeRational(centimeters, 100, littleEndian)})
            End If

            ' Ohne Bezugssystem ist eine Koordinate mehrdeutig. Alles, was wir bekommen und
            ' verrechnen, ist WGS-84.
            fields.Add(AsciiField(&H12, "WGS-84"))
            Return fields
        End Function

        Private Shared Function AsciiField(tag As Integer, value As String) As GpsField
            Dim raw = System.Text.Encoding.ASCII.GetBytes(value)
            Dim data(raw.Length) As Byte
            Buffer.BlockCopy(raw, 0, data, 0, raw.Length)
            ' Das abschliessende Nullbyte zaehlt in EXIF zur Laenge.
            Return New GpsField With {.Tag = tag, .FieldType = TiffTypeAscii, .Count = data.Length, .Data = data}
        End Function

        Private Shared Function BuildGpsIfd(fields As List(Of GpsField),
                                            ifdOffset As Integer,
                                            littleEndian As Boolean) As Byte()
            Dim directorySize = 2 + fields.Count * 12 + 4
            Dim valueBase = ifdOffset + directorySize
            Dim directory As New List(Of Byte)(directorySize)
            Dim values As New List(Of Byte)()

            AppendUInt16(directory, fields.Count, littleEndian)
            For Each field In fields
                AppendUInt16(directory, field.Tag, littleEndian)
                AppendUInt16(directory, field.FieldType, littleEndian)
                AppendUInt32(directory, CUInt(field.Count), littleEndian)
                If field.Data.Length <= 4 Then
                    Dim inline(3) As Byte
                    Buffer.BlockCopy(field.Data, 0, inline, 0, field.Data.Length)
                    directory.AddRange(inline)
                Else
                    AppendUInt32(directory, CUInt(valueBase + values.Count), littleEndian)
                    values.AddRange(field.Data)
                    PadToEven(values)
                End If
            Next
            ' Hinter dem GPS-IFD folgt kein weiteres.
            AppendUInt32(directory, 0UI, littleEndian)

            directory.AddRange(values)
            Return directory.ToArray()
        End Function

        ''' <summary>Grad, Minute, Sekunde als drei Brueche, so wie EXIF eine Koordinate fuehrt.
        '''
        ''' Gerechnet wird in Zehntausendstel-Bogensekunden: rundete man die Sekunden fuer sich,
        ''' koennte aus 59,99999 eine 60 werden - ein Uebertrag auf die Minuten, den niemand
        ''' nachtraegt. Ueber die ganze Zahl kann das nicht passieren.</summary>
        Private Shared Function EncodeCoordinate(value As Double, littleEndian As Boolean) As Byte()
            Dim total = CLng(Math.Round(value * 3600.0 * 10000.0))
            Dim degrees = total \ 36000000L
            Dim rest = total Mod 36000000L
            Dim minutes = rest \ 600000L
            Dim seconds = rest Mod 600000L

            Dim data As New List(Of Byte)(24)
            data.AddRange(EncodeRational(degrees, 1, littleEndian))
            data.AddRange(EncodeRational(minutes, 1, littleEndian))
            data.AddRange(EncodeRational(seconds, 10000, littleEndian))
            Return data.ToArray()
        End Function

        Private Shared Function EncodeRational(numerator As Long, denominator As Long, littleEndian As Boolean) As Byte()
            Dim data As New List(Of Byte)(8)
            AppendUInt32(data, CUInt(Math.Max(0L, numerator)), littleEndian)
            AppendUInt32(data, CUInt(Math.Max(1L, denominator)), littleEndian)
            Return data.ToArray()
        End Function

        ' ---------------------------------------------------------------------------------------
        ' Byte-Helfer. TIFF verlangt Wortausrichtung, deshalb das Auffuellen auf gerade Laenge.
        ' ---------------------------------------------------------------------------------------

        Private Shared Sub PadToEven(buffer As List(Of Byte))
            If buffer.Count Mod 2 <> 0 Then buffer.Add(0)
        End Sub

        Private Shared Function ReadUInt16BigEndian(bytes As Byte(), offset As Integer) As Integer
            Return (CInt(bytes(offset)) << 8) Or CInt(bytes(offset + 1))
        End Function

        Private Shared Function ReadUInt16(bytes As Byte(), offset As Integer, littleEndian As Boolean) As Integer
            If littleEndian Then Return (CInt(bytes(offset + 1)) << 8) Or CInt(bytes(offset))
            Return (CInt(bytes(offset)) << 8) Or CInt(bytes(offset + 1))
        End Function

        Private Shared Function ReadUInt32(bytes As Byte(), offset As Integer, littleEndian As Boolean) As UInteger
            If littleEndian Then
                Return CUInt(bytes(offset)) Or (CUInt(bytes(offset + 1)) << 8) Or
                       (CUInt(bytes(offset + 2)) << 16) Or (CUInt(bytes(offset + 3)) << 24)
            End If
            Return (CUInt(bytes(offset)) << 24) Or (CUInt(bytes(offset + 1)) << 16) Or
                   (CUInt(bytes(offset + 2)) << 8) Or CUInt(bytes(offset + 3))
        End Function

        Private Shared Sub WriteUInt16(bytes As Byte(), offset As Integer, value As Integer, littleEndian As Boolean)
            If littleEndian Then
                bytes(offset) = CByte(value And &HFF)
                bytes(offset + 1) = CByte((value >> 8) And &HFF)
            Else
                bytes(offset) = CByte((value >> 8) And &HFF)
                bytes(offset + 1) = CByte(value And &HFF)
            End If
        End Sub

        Private Shared Sub WriteUInt32(bytes As Byte(), offset As Integer, value As UInteger, littleEndian As Boolean)
            If littleEndian Then
                bytes(offset) = CByte(value And &HFFUI)
                bytes(offset + 1) = CByte((value >> 8) And &HFFUI)
                bytes(offset + 2) = CByte((value >> 16) And &HFFUI)
                bytes(offset + 3) = CByte((value >> 24) And &HFFUI)
            Else
                bytes(offset) = CByte((value >> 24) And &HFFUI)
                bytes(offset + 1) = CByte((value >> 16) And &HFFUI)
                bytes(offset + 2) = CByte((value >> 8) And &HFFUI)
                bytes(offset + 3) = CByte(value And &HFFUI)
            End If
        End Sub

        Private Shared Sub AppendUInt16(buffer As List(Of Byte), value As Integer, littleEndian As Boolean)
            Dim raw(1) As Byte
            WriteUInt16(raw, 0, value, littleEndian)
            buffer.AddRange(raw)
        End Sub

        Private Shared Sub AppendUInt32(buffer As List(Of Byte), value As UInteger, littleEndian As Boolean)
            Dim raw(3) As Byte
            WriteUInt32(raw, 0, value, littleEndian)
            buffer.AddRange(raw)
        End Sub

    End Class

End Namespace
