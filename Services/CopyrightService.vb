Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text

Namespace Services

    Public Enum CopyrightTarget
        None = 0
        EmbeddedExif = 1
        XmpSidecar = 2
    End Enum

    Public Class CopyrightWriteResult
        Public Property Success As Boolean
        Public Property Target As CopyrightTarget = CopyrightTarget.None
        Public Property SidecarPath As String = ""
        Public Property FailureReason As String = ""
    End Class

    ''' <summary>
    ''' Schreibt den Urheberrechtshinweis an ein Bild.
    '''
    ''' WOHIN: dieselbe Aufteilung wie beim Aufnahmeort (siehe <see cref="GeotagService"/>) - eine
    ''' JPEG bekommt ihn IN die Datei, weil das die Datei ist, die weitergegeben wird; alles andere
    ''' bekommt eine XMP-Beistelldatei, und an eine RAW-Datei geht nichts direkt heran.
    '''
    ''' IN WELCHES FELD: der Standard kennt fuer den Hinweis drei gleichwertige Orte, und die
    ''' Metadata Working Group verlangt sie synchron. Wir bedienen zwei davon - `dc:rights` im XMP
    ''' als den massgeblichen Wert und EXIF `Copyright` (IFD0, Tag 0x8298, ASCII) fuer die
    ''' Vertraeglichkeit mit allem, was kein XMP liest. Das dritte, IPTC IIM 2:116, lesen wir zwar,
    ''' schreiben es aber nicht: ein eigener IIM-Schreibweg waere neue Mechanik fuer einen Aufbau,
    ''' den heute kaum ein Programm allein auswertet.
    '''
    ''' WIE: Byte fuer Byte nach demselben Versprechen wie der Aufnahmeort - es wird NICHTS
    ''' verschoben. Der vorhandene TIFF-Block bleibt liegen, hinten kommen der Text und eine Kopie
    ''' von IFD0 dazu, und nur der Zeiger im Kopf wird umgebogen. Damit koennen weder die
    ''' Herstellernotizen mit ihren absoluten Zeigern noch das eingebettete Vorschaubild kaputtgehen.
    ''' </summary>
    Public NotInheritable Class CopyrightService

        ''' EXIF-Tag 0x8298 in IFD0. Derselbe, den <see cref="ExifService"/> beim Lesen auswertet.
        Friend Const CopyrightTag As Integer = &H8298

        ''' Ein Hinweis ist eine Zeile, kein Aufsatz. Die Grenze haelt vor allem den Fall ab, dass
        ''' ein versehentlich eingefuegter Textblock den EXIF-Block ueber die Segmentgrenze treibt.
        Public Const MaxLength As Integer = 512

        Private Sub New()
        End Sub

        ''' <summary>Raeumt den eingegebenen Text auf: Zeilenumbrueche und Steuerzeichen raus,
        ''' Laenge begrenzt. Ein leerer Text bedeutet ueberall „nichts tun" und nie „Feld leeren" -
        ''' zum Entfernen gibt es <see cref="RemoveCopyright"/>.</summary>
        Public Shared Function NormalizeText(text As String) As String
            If String.IsNullOrWhiteSpace(text) Then Return ""
            Dim builder As New StringBuilder(text.Length)
            For Each character In text.Trim()
                ' Ein Zeilenumbruch im EXIF-Feld ist zwar erlaubt, wird aber von vielen Anzeigen
                ' als Ende des Wertes gelesen. Er wird deshalb zum Leerzeichen.
                If character = ControlChars.Cr OrElse character = ControlChars.Lf OrElse character = ControlChars.Tab Then
                    builder.Append(" "c)
                ElseIf Not Char.IsControl(character) Then
                    builder.Append(character)
                End If
            Next
            Dim cleaned = builder.ToString().Trim()
            While cleaned.Contains("  ")
                cleaned = cleaned.Replace("  ", " ")
            End While
            If cleaned.Length > MaxLength Then cleaned = cleaned.Substring(0, MaxLength).Trim()
            Return cleaned
        End Function

        ''' <summary>Der Urheberrechtshinweis, der bereits an der Datei steht. Leer, wenn keiner
        ''' drinsteht.
        '''
        ''' Gelesen wird ueber `ExifService.ReadExif` und damit ueber DENSELBEN Weg, den auch die
        ''' Info-Leiste nimmt. Das ist Absicht und keine Bequemlichkeit: dort steht die Reihenfolge
        ''' des Standards (XMP vor EXIF) an genau EINER Stelle. Eine zweite Leseregel hier hiesse,
        ''' dass die Leiste den einen und dieser Dialog den anderen Wert zeigt, sobald eine Datei
        ''' beides traegt und die beiden auseinanderlaufen.</summary>
        Public Shared Function ReadCopyright(imagePath As String) As String
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then Return ""
            Try
                Return NormalizeText(ExifService.ReadExif(imagePath)?.Copyright)
            Catch ex As Exception
                DiagnosticLogService.LogException("Copyright.Read", ex)
                Return ""
            End Try
        End Function

        ''' <summary>Schreibt den Hinweis an die Datei: ins JPEG hinein, sonst in die Beistelldatei.
        ''' Ein LEERER Text laesst die Datei unangetastet - das ist die Regel, auf der die
        ''' Stapelformulare aufbauen: ein leeres Feld heisst „dieses Feld nicht anfassen".</summary>
        Public Shared Function WriteCopyright(imagePath As String,
                                              copyrightText As String,
                                              Optional createSidecarIfMissing As Boolean = True) As CopyrightWriteResult
            Dim result As New CopyrightWriteResult()
            Dim text = NormalizeText(copyrightText)
            If text.Length = 0 Then
                result.FailureReason = "Kein Text"
                Return result
            End If
            If String.IsNullOrWhiteSpace(imagePath) Then
                result.FailureReason = "Kein Pfad"
                Return result
            End If
            If Not File.Exists(imagePath) Then
                result.FailureReason = "Datei nicht gefunden"
                Return result
            End If

            ' Der Weg in die Datei bekommt sein EIGENES Netz - genau wie beim Aufnahmeort: scheitert
            ' er, ist die Beistelldatei immer noch besser als gar nichts.
            If GeotagService.IsJpegPath(imagePath) Then
                result.Target = CopyrightTarget.EmbeddedExif
                Try
                    If GeotagService.RewriteJpegExifAtomic(imagePath,
                        Function(existingSegment)
                            Dim existing(existingSegment.Length - 10 - 1) As Byte
                            Buffer.BlockCopy(existingSegment, 10, existing, 0, existing.Length)
                            Dim tiff = SetCopyrightInTiff(existing, text)
                            If tiff Is Nothing Then
                                SetReason(result, "EXIF-Block nicht lesbar")
                                Return Nothing
                            End If
                            If tiff.Length > GeotagService.MaxTiffBlockInJpeg Then
                                SetReason(result, "EXIF-Block passt nicht mehr in ein JPEG-Segment")
                                Return Nothing
                            End If
                            Return GeotagService.BuildExifSegment(tiff)
                        End Function,
                        Function()
                            Return GeotagService.BuildExifSegment(CreateTiffWithCopyright(text))
                        End Function) Then
                        ' EINE VORHANDENE Beistelldatei muss mit. Sie wird NICHT neu angelegt - das
                        ' waere eine Datei, um die niemand gebeten hat -, aber wenn schon eine
                        ' danebenliegt, traegt sie sonst weiter den alten Hinweis. Programme, die
                        ' XMP dem EXIF vorziehen (das ist die Regel des Standards), zeigten danach
                        ' den alten Stand. Das Entfernen raeumt beide Orte, das Setzen muss dieselbe
                        ' Einigkeit herstellen.
                        Try
                            ExifService.WriteXmpCopyrightSidecar(imagePath, text, createIfMissing:=False)
                        Catch ex As Exception
                            DiagnosticLogService.LogException("Copyright.SyncSidecar", ex)
                        End Try
                        result.Success = True
                        result.FailureReason = ""
                        Return result
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("Copyright.WriteJpeg", ex)
                    SetReason(result, ex.Message)
                End Try
            End If

            Try
                result.Target = CopyrightTarget.XmpSidecar
                Dim sidecarPath = ExifService.WriteXmpCopyrightSidecar(imagePath, text, createSidecarIfMissing)
                If String.IsNullOrEmpty(sidecarPath) Then
                    SetReason(result, "Beistelldatei nicht geschrieben")
                    Return result
                End If
                result.SidecarPath = sidecarPath
                result.Success = True
                result.FailureReason = ""
                Return result
            Catch ex As Exception
                DiagnosticLogService.LogException("Copyright.WriteSidecar", ex)
                SetReason(result, ex.Message)
                Return result
            End Try
        End Function

        ''' <summary>Nimmt den Hinweis wieder weg - aus der Datei UND aus der Beistelldatei. Wie beim
        ''' Aufnahmeort werden dabei die alten Textbytes ueberschrieben und nicht nur der Eintrag
        ''' entfernt: ein Hinweis, der als toter Ballast im Klartext in der Datei stehen bleibt, ist
        ''' nicht geloescht.</summary>
        Public Shared Function RemoveCopyright(imagePath As String) As CopyrightWriteResult
            Dim result As New CopyrightWriteResult()
            If String.IsNullOrWhiteSpace(imagePath) Then
                result.FailureReason = "Kein Pfad"
                Return result
            End If

            Dim removedSomething = False
            If GeotagService.IsJpegPath(imagePath) AndAlso File.Exists(imagePath) Then
                Try
                    If GeotagService.RewriteJpegExifAtomic(imagePath,
                        Function(existingSegment)
                            Dim existing(existingSegment.Length - 10 - 1) As Byte
                            Buffer.BlockCopy(existingSegment, 10, existing, 0, existing.Length)
                            Dim tiff = SetCopyrightInTiff(existing, "")
                            Return If(tiff Is Nothing, Nothing, GeotagService.BuildExifSegment(tiff))
                        End Function) Then
                        removedSomething = True
                        result.Target = CopyrightTarget.EmbeddedExif
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("Copyright.RemoveFromJpeg", ex)
                    SetReason(result, ex.Message)
                End Try
            End If

            Try
                If ExifService.RemoveXmpCopyrightSidecar(imagePath) Then
                    removedSomething = True
                    If result.Target = CopyrightTarget.None Then result.Target = CopyrightTarget.XmpSidecar
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Copyright.RemoveFromSidecar", ex)
            End Try

            result.Success = removedSomething
            If Not removedSomething Then SetReason(result, "Es stand kein Urheberrechtshinweis in der Datei")
            Return result
        End Function

        ''' <summary>Die fertigen JPEG-Bytes mit gesetztem (oder bei leerem Text: entferntem)
        ''' Hinweis, sonst Nothing. Getrennt vom Schreiben, damit der Pruefstand den Umbau ohne
        ''' Datei pruefen kann - genau wie bei <c>BuildJpegWithGps</c>.</summary>
        Friend Shared Function BuildJpegWithCopyright(bytes As Byte(),
                                                      copyrightText As String,
                                                      result As CopyrightWriteResult) As Byte()
            If bytes Is Nothing OrElse bytes.Length < 4 OrElse bytes(0) <> &HFF OrElse bytes(1) <> &HD8 Then
                SetReason(result, "Keine JPEG-Datei")
                Return Nothing
            End If

            Dim text = NormalizeText(copyrightText)
            Dim exifStart = -1
            Dim exifLength = 0
            Dim insertAt = 2

            ' Ein Durchlauf durch die Segmente: den EXIF-Block finden und zugleich die Stelle
            ' merken, an der ein neuer stehen muesste - hinter JFIF (APP0), vor allem anderen.
            ' Wortgleich zum Aufnahmeort, weil es dieselbe Frage an dieselbe Datei ist.
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

                Dim length = GeotagService.ReadUInt16BigEndian(bytes, offset + 2)
                If length < 2 OrElse offset + 2 + length > bytes.Length Then Exit While
                Dim totalLength = 2 + length

                If Not pastLeadingApp0 Then
                    If marker = &HE0 OrElse marker = &HEE Then
                        insertAt = offset + totalLength
                    Else
                        pastLeadingApp0 = True
                    End If
                End If

                If marker = &HE1 AndAlso GeotagService.IsExifSegment(bytes, offset, totalLength) Then
                    exifStart = offset
                    exifLength = totalLength
                    Exit While
                End If

                offset += totalLength
            End While

            Dim tiff As Byte()
            If exifStart >= 0 Then
                Dim existing(exifLength - 10 - 1) As Byte
                Buffer.BlockCopy(bytes, exifStart + 10, existing, 0, existing.Length)
                tiff = SetCopyrightInTiff(existing, text)
                If tiff Is Nothing Then
                    ' Beim ENTFERNEN heisst das schlicht: es stand keiner drin. Beim Setzen: der
                    ' Block ist nicht sicher lesbar, und dann wird er nicht angefasst.
                    SetReason(result, If(text.Length = 0, "Es stand kein Urheberrechtshinweis in der Datei", "EXIF-Block nicht lesbar"))
                    Return Nothing
                End If
            ElseIf text.Length = 0 Then
                ' Nichts zu entfernen, wo gar kein EXIF steht.
                Return Nothing
            Else
                tiff = CreateTiffWithCopyright(text)
            End If

            If tiff.Length > GeotagService.MaxTiffBlockInJpeg Then
                ' Kein halber Schreibvorgang: lieber gar nicht in die Datei als ein abgeschnittenes
                ' Segment. Der Aufrufer weicht dann auf die Beistelldatei aus.
                SetReason(result, "EXIF-Block passt nicht mehr in ein JPEG-Segment")
                Return Nothing
            End If

            Dim segment = GeotagService.BuildExifSegment(tiff)
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

        ''' <summary>Setzt den Hinweis in einen vorhandenen TIFF-Block, ohne ein einziges vorhandenes
        ''' Byte zu VERSCHIEBEN: der Block bleibt liegen, hinten kommen Text und eine Kopie von IFD0
        ''' dazu, der Kopfzeiger zeigt auf die Kopie. Ein leerer Text entfernt den Eintrag.
        ''' Nothing, wenn der Block nicht sicher gelesen werden kann (dann lieber gar nichts) oder
        ''' wenn beim Entfernen gar keiner drinstand.</summary>
        Friend Shared Function SetCopyrightInTiff(tiff As Byte(), copyrightText As String) As Byte()
            If tiff Is Nothing OrElse tiff.Length < 8 Then Return Nothing

            Dim littleEndian = tiff(0) = AscW("I"c) AndAlso tiff(1) = AscW("I"c)
            Dim bigEndian = tiff(0) = AscW("M"c) AndAlso tiff(1) = AscW("M"c)
            If Not littleEndian AndAlso Not bigEndian Then Return Nothing
            If GeotagService.ReadUInt16(tiff, 2, littleEndian) <> 42 Then Return Nothing

            Dim ifd0 = CLng(GeotagService.ReadUInt32(tiff, 4, littleEndian))
            If ifd0 < 8 OrElse ifd0 + 2 > tiff.Length Then Return Nothing
            Dim entryCount = GeotagService.ReadUInt16(tiff, CInt(ifd0), littleEndian)
            If entryCount > GeotagService.MaxIfdEntries Then Return Nothing
            Dim entriesEnd = CInt(ifd0) + 2 + entryCount * 12
            If entriesEnd + 4 > tiff.Length Then Return Nothing

            Dim text = NormalizeText(copyrightText)
            Dim value = If(text.Length = 0, Array.Empty(Of Byte)(), AsciiWithTerminator(text))

            Dim output As New List(Of Byte)(tiff.Length + value.Length + 64)
            output.AddRange(tiff)

            ' DIE ALTEN TEXTBYTES AUSLOESCHEN. Der alte IFD0-Rumpf bleibt als toter Ballast liegen
            ' (das ist der Preis dafuer, dass nichts umgerechnet wird) - sein Wert aber nicht. Sonst
            ' stuende der vorige Hinweis weiterhin im Klartext in der Datei, unsichtbar fuer jedes
            ' Programm und lesbar fuer jeden, der sich die Bytes ansieht. Beim Aufnahmeort gilt
            ' dasselbe Versprechen, aus demselben Grund.
            Dim hadEntry = False
            For i = 0 To entryCount - 1
                Dim entryStart = CInt(ifd0) + 2 + i * 12
                If GeotagService.ReadUInt16(tiff, entryStart, littleEndian) <> CopyrightTag Then Continue For
                hadEntry = True
                ' Aufbau eines Eintrags: 0..1 Tag, 2..3 Typ, 4..7 Anzahl, 8..11 Wert oder Adresse.
                Dim oldCount = CInt(GeotagService.ReadUInt32(tiff, entryStart + 4, littleEndian))
                If oldCount > 4 Then
                    Dim oldOffset = CInt(GeotagService.ReadUInt32(tiff, entryStart + 8, littleEndian))
                    If oldOffset >= 8 AndAlso oldOffset + oldCount <= tiff.Length Then
                        For byteIndex = 0 To oldCount - 1
                            output(oldOffset + byteIndex) = 0
                        Next
                    End If
                End If
            Next

            ' Beim Entfernen ohne vorhandenen Eintrag bleibt die Datei unangetastet.
            If text.Length = 0 AndAlso Not hadEntry Then Return Nothing

            GeotagService.PadToEven(output)
            Dim valueOffset = 0
            If value.Length > 4 Then
                valueOffset = output.Count
                output.AddRange(value)
                GeotagService.PadToEven(output)
            End If

            Dim entries As New List(Of Byte())()
            Dim replaced = False
            For i = 0 To entryCount - 1
                Dim entry(11) As Byte
                Buffer.BlockCopy(tiff, CInt(ifd0) + 2 + i * 12, entry, 0, 12)
                If GeotagService.ReadUInt16(entry, 0, littleEndian) = CopyrightTag Then
                    ' Ein vorhandener Eintrag wird UMGESCHRIEBEN statt ein zweiter angelegt: zwei
                    ' Eintraege mit derselben Tag-Nummer sind ein kaputtes IFD.
                    If text.Length = 0 Then Continue For
                    FillCopyrightEntry(entry, value, valueOffset, littleEndian)
                    replaced = True
                End If
                entries.Add(entry)
            Next

            If text.Length > 0 AndAlso Not replaced Then
                Dim entry(11) As Byte
                FillCopyrightEntry(entry, value, valueOffset, littleEndian)
                ' Die Eintraege eines IFD stehen nach Tag-Nummer aufsteigend; der neue gehoert an
                ' seinen Platz, nicht ans Ende.
                Dim insertAt = entries.Count
                For i = 0 To entries.Count - 1
                    If GeotagService.ReadUInt16(entries(i), 0, littleEndian) > CopyrightTag Then
                        insertAt = i
                        Exit For
                    End If
                Next
                entries.Insert(insertAt, entry)
            End If

            Dim newIfd0Offset = output.Count
            GeotagService.AppendUInt16(output, entries.Count, littleEndian)
            For Each entry In entries
                output.AddRange(entry)
            Next
            ' Der Verweis auf IFD1 - das eingebettete Vorschaubild - wird woertlich uebernommen.
            ' Er zeigt in den unveraenderten Bereich und stimmt deshalb weiterhin.
            For i = 0 To 3
                output.Add(tiff(entriesEnd + i))
            Next

            Dim rebuilt = output.ToArray()
            GeotagService.WriteUInt32(rebuilt, 4, CUInt(newIfd0Offset), littleEndian)
            Return rebuilt
        End Function

        ''' <summary>Ein TIFF-Block, der nur den Hinweis traegt - fuer Bilder ohne jedes EXIF.
        ''' Kleinschreibendes „II", weil das die verbreitete Form ist und der Block ohnehin neu
        ''' ist.</summary>
        Friend Shared Function CreateTiffWithCopyright(copyrightText As String) As Byte()
            Const littleEndian As Boolean = True
            Dim value = AsciiWithTerminator(NormalizeText(copyrightText))

            Dim output As New List(Of Byte)(64 + value.Length)
            output.Add(CByte(AscW("I"c)))
            output.Add(CByte(AscW("I"c)))
            GeotagService.AppendUInt16(output, 42, littleEndian)
            GeotagService.AppendUInt32(output, 8UI, littleEndian)

            ' IFD0 liegt auf 8 und traegt genau einen Eintrag: 2 + 12 + 4 Byte, der Text beginnt
            ' also auf 26.
            Const valueOffset As Integer = 26
            GeotagService.AppendUInt16(output, 1, littleEndian)
            Dim entry(11) As Byte
            FillCopyrightEntry(entry, value, valueOffset, littleEndian)
            output.AddRange(entry)
            GeotagService.AppendUInt32(output, 0UI, littleEndian)
            If value.Length > 4 Then output.AddRange(value)
            Return output.ToArray()
        End Function

        ''' <summary>Der Eintrag im IFD: Tag, Typ ASCII, Anzahl Bytes und der Wert. Bis zu VIER
        ''' Bytes stehen unmittelbar im Eintrag - der Aufbau verlangt das ausdruecklich, ein Zeiger
        ''' waere dort falsch. Erst darueber steht die Adresse.</summary>
        Private Shared Sub FillCopyrightEntry(entry As Byte(), value As Byte(), valueOffset As Integer, littleEndian As Boolean)
            Array.Clear(entry, 0, entry.Length)
            GeotagService.WriteUInt16(entry, 0, CopyrightTag, littleEndian)
            GeotagService.WriteUInt16(entry, 2, GeotagService.TiffTypeAscii, littleEndian)
            GeotagService.WriteUInt32(entry, 4, CUInt(value.Length), littleEndian)
            If value.Length <= 4 Then
                Buffer.BlockCopy(value, 0, entry, 8, value.Length)
            Else
                GeotagService.WriteUInt32(entry, 8, CUInt(valueOffset), littleEndian)
            End If
        End Sub

        ''' <summary>Der Text als ASCII mit abschliessender Null, wie der Aufbau es fuer diesen
        ''' Feldtyp verlangt. Zeichen ausserhalb von ASCII werden ersetzt: das EXIF-Feld kennt
        ''' keine Kodierung, und ein roh eingesetztes Byte laese sich anderswo als Zufallszeichen.
        ''' Der volle Text steht im XMP, das Unicode kann.</summary>
        Private Shared Function AsciiWithTerminator(text As String) As Byte()
            Dim ascii = Encoding.ASCII.GetBytes(text)
            Dim withTerminator(ascii.Length) As Byte
            Buffer.BlockCopy(ascii, 0, withTerminator, 0, ascii.Length)
            withTerminator(ascii.Length) = 0
            Return withTerminator
        End Function

        Private Shared Sub SetReason(result As CopyrightWriteResult, reason As String)
            If result IsNot Nothing AndAlso String.IsNullOrEmpty(result.FailureReason) Then
                result.FailureReason = reason
            End If
        End Sub

    End Class

End Namespace
