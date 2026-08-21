Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO

Namespace Services

    ''' <summary>Schreibt die Zeit der Aufnahme. JPEGs erhalten echte EXIF-Felder, alle anderen
    ''' Formate eine XMP-Beistelldatei. Der TIFF-Block wird dabei nur hinten erweitert.
    '''
    ''' DREI ZEITEN TRAGEN DENSELBEN WERT: Aufnahme, "geaendert am" im Bild und "geaendert am" der
    ''' DATEI. Der Anlass fuer diese Funktion ist eine falsch gestellte Kamerauhr, und danach soll
    ''' das Bild an EINER Stelle in der Zeit stehen - die Galerie sortiert wahlweise nach jeder der
    ''' drei, und drei verschiedene Antworten waeren genau das, was der Nutzer loswerden wollte.</summary>
    Public NotInheritable Class CaptureDateService
        Private Const ExifIfdPointerTag As Integer = &H8769
        Private Const DateTimeTag As Integer = &H132
        Private Const DateTimeOriginalTag As Integer = &H9003
        Private Const DateTimeDigitizedTag As Integer = &H9004

        Private Sub New()
        End Sub

        ''' <summary>Die geltende Aufnahmezeit eines Bildes - aus der Datei, und wo eine
        ''' Beistelldatei danebenliegt, aus dieser (siehe ExifService.WithSidecarOverrides).
        ''' Nothing, wenn keine zu finden ist; dann laesst sich auch nichts verschieben.</summary>
        Public Shared Function ReadCaptureDate(imagePath As String) As DateTime?
            If String.IsNullOrWhiteSpace(imagePath) Then Return Nothing
            Try
                Return ExifService.ParseExifDateTime(ExifService.ReadExif(imagePath).DateTaken)
            Catch ex As Exception
                DiagnosticLogService.LogException("CaptureDate.ReadCaptureDate", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Die Grenzen, in denen eine Aufnahmezeit ueberhaupt Sinn ergibt. Das EXIF-Feld
        ''' traegt vier Ziffern fuer das Jahr, und eine Verschiebung, die darueber hinausfuehrt, ist
        ''' ein Vertipper und kein Wunsch. Geprueft wird VOR dem Rechnen, damit ein einzelner
        ''' krummer Wert nicht mitten in einer Auswahl abbricht.</summary>
        Public Shared ReadOnly Property EarliestCaptureDate As DateTime = New DateTime(1900, 1, 1)
        Public Shared ReadOnly Property LatestCaptureDate As DateTime = New DateTime(9999, 12, 31, 23, 59, 59)

        Public Shared Function IsInRange(value As DateTime) As Boolean
            Return value >= EarliestCaptureDate AndAlso value <= LatestCaptureDate
        End Function

        ''' <summary>Verschiebt eine Zeit um eine Spanne, ohne ueber die Grenzen zu laufen.
        ''' Nothing heisst "ausserhalb" - der Aufrufer zaehlt das Bild dann als uebersprungen.</summary>
        Public Shared Function Shift(value As DateTime, offset As TimeSpan) As DateTime?
            Try
                Dim shifted = value.Add(offset)
                Return If(IsInRange(shifted), shifted, CType(Nothing, DateTime?))
            Catch ex As ArgumentOutOfRangeException
                Return Nothing
            End Try
        End Function

        ''' <summary>Die Uhrzeit aus dem Dialogfeld. Sekunden duerfen fehlen: wer eine Serie auf
        ''' "14:30" setzt, soll nicht wegen der fehlenden Nullen abgewiesen werden. Mehr als 24
        ''' Stunden sind keine Uhrzeit mehr, deshalb die obere Schranke.</summary>
        Public Shared Function TryParseTime(text As String, ByRef value As TimeSpan) As Boolean
            Dim trimmed = If(text, "").Trim()
            For Each timeFormat In {"hh\:mm\:ss", "h\:mm\:ss", "hh\:mm", "h\:mm"}
                If TimeSpan.TryParseExact(trimmed, timeFormat, CultureInfo.InvariantCulture, value) AndAlso
                   value >= TimeSpan.Zero AndAlso value.TotalHours < 24 Then Return True
            Next
            value = TimeSpan.Zero
            Return False
        End Function

        Public Shared Function Write(imagePath As String, capturedAt As DateTime) As Boolean
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then Return False
            If Not IsInRange(capturedAt) Then Return False
            Dim raw = capturedAt.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)
            Try
                If GeotagService.IsJpegPath(imagePath) Then
                    ' JPEG: die Zeit gehoert IN die Datei. Klappt das nicht, wird gemeldet, dass es
                    ' nicht ging - und KEINE Beistelldatei angelegt. Eine .xmp neben einem JPEG waere
                    ' eine Datei, mit der der Nutzer nicht rechnet, und andere Programme laesen aus
                    ' ihr fortan eine Zeit, die im Bild selbst nicht steht.
                    If Not GeotagService.RewriteJpegExifAtomic(imagePath,
                           Function(segment)
                               Dim tiff(segment.Length - 11) As Byte
                               Buffer.BlockCopy(segment, 10, tiff, 0, tiff.Length)
                               Dim rewritten = SetCaptureDateInTiff(tiff, raw)
                               Return If(rewritten Is Nothing, Nothing, GeotagService.BuildExifSegment(rewritten))
                           End Function,
                           Function() GeotagService.BuildExifSegment(CreateTiff(raw))) Then Return False
                    ' Ein vorhandenes Sidecar muss denselben Wert tragen, weil XMP bei vielen
                    ' Programmen Vorrang hat - auch bei uns (ExifService.WithSidecarOverrides).
                    ExifService.WriteXmpCaptureDateSidecar(imagePath, capturedAt, False)
                Else
                    If ExifService.WriteXmpCaptureDateSidecar(imagePath, capturedAt, True).Length = 0 Then Return False
                End If

                ' ZULETZT das Dateidatum, denn jeder Schreibvorgang darueber setzt es neu. Es steht
                ' in der Info-Leiste als "Geaendert" und ist eine der Sortierungen der Galerie.
                SetFileModifiedTime(imagePath, capturedAt)
                ExifService.Invalidate(imagePath)
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("CaptureDate.Write", ex)
                Return False
            End Try
        End Function

        ''' <summary>Zieht das "geaendert am" der DATEI auf die Aufnahmezeit nach.
        '''
        ''' Scheitert das, ist die Aufnahmezeit trotzdem geschrieben - deshalb kein Rueckgabewert,
        ''' der den ganzen Vorgang zu Fall braechte. Auf einem Netzlaufwerk oder einer
        ''' schreibgeschuetzten Freigabe ist es der wahrscheinlichste Teil, der nicht geht.</summary>
        Private Shared Sub SetFileModifiedTime(imagePath As String, value As DateTime)
            Try
                File.SetLastWriteTime(imagePath, value)
            Catch ex As Exception
                DiagnosticLogService.LogException("CaptureDate.SetFileModifiedTime", ex)
            End Try
        End Sub

        Private Shared Function SetCaptureDateInTiff(tiff As Byte(), raw As String) As Byte()
            If tiff Is Nothing OrElse tiff.Length < 8 Then Return Nothing
            Dim le = tiff(0) = AscW("I"c) AndAlso tiff(1) = AscW("I"c)
            If Not le AndAlso Not (tiff(0) = AscW("M"c) AndAlso tiff(1) = AscW("M"c)) Then Return Nothing
            If GeotagService.ReadUInt16(tiff, 2, le) <> 42 Then Return Nothing
            Dim ifd0 = CInt(GeotagService.ReadUInt32(tiff, 4, le))
            If ifd0 < 8 OrElse ifd0 + 2 > tiff.Length Then Return Nothing
            Dim count = GeotagService.ReadUInt16(tiff, ifd0, le)
            Dim end0 = ifd0 + 2 + count * 12
            If count > GeotagService.MaxIfdEntries OrElse end0 + 4 > tiff.Length Then Return Nothing

            Dim oldExif = -1
            For i = 0 To count - 1
                Dim pos = ifd0 + 2 + i * 12
                If GeotagService.ReadUInt16(tiff, pos, le) = ExifIfdPointerTag Then oldExif = CInt(GeotagService.ReadUInt32(tiff, pos + 8, le))
            Next
            Dim exifEntries As New List(Of Byte())()
            If oldExif >= 8 AndAlso oldExif + 2 <= tiff.Length Then
                Dim subCount = GeotagService.ReadUInt16(tiff, oldExif, le)
                If subCount <= GeotagService.MaxIfdEntries AndAlso oldExif + 2 + subCount * 12 + 4 <= tiff.Length Then
                    For i = 0 To subCount - 1
                        Dim entry(11) As Byte : Buffer.BlockCopy(tiff, oldExif + 2 + i * 12, entry, 0, 12) : exifEntries.Add(entry)
                    Next
                End If
            End If
            Dim output As New List(Of Byte)(tiff) : GeotagService.PadToEven(output)
            Dim dateOffset = output.Count : output.AddRange(System.Text.Encoding.ASCII.GetBytes(raw & ChrW(0))) : GeotagService.PadToEven(output)
            Dim newExif = output.Count
            UpsertAscii(exifEntries, DateTimeOriginalTag, dateOffset, le)
            UpsertAscii(exifEntries, DateTimeDigitizedTag, dateOffset, le)
            WriteIfd(output, exifEntries, le)
            GeotagService.PadToEven(output)
            Dim ifd0Entries As New List(Of Byte())()
            For i = 0 To count - 1
                Dim entry(11) As Byte : Buffer.BlockCopy(tiff, ifd0 + 2 + i * 12, entry, 0, 12) : ifd0Entries.Add(entry)
            Next
            UpsertLong(ifd0Entries, ExifIfdPointerTag, newExif, le)
            UpsertAscii(ifd0Entries, DateTimeTag, dateOffset, le)
            ' IFD0 von Hand statt ueber WriteIfd: dessen abschliessende Null waere hier der Verweis
            ' auf das NAECHSTE IFD, und das ist bei IFD0 das eingebettete Vorschaubild (IFD1). Mit
            ' einer Null davor waere es nach dem Schreiben verwaist - die Bilddaten blieben in der
            ' Datei, aber kein Leser fande sie mehr. Der Verweis wird deshalb woertlich uebernommen:
            ' er zeigt in den unveraenderten Bereich und stimmt weiterhin (gleiche Stelle und
            ' gleiche Begruendung wie in GeotagService).
            Dim newIfd0 = output.Count
            ifd0Entries.Sort(Function(a, b) GeotagService.ReadUInt16(a, 0, le).CompareTo(GeotagService.ReadUInt16(b, 0, le)))
            GeotagService.AppendUInt16(output, ifd0Entries.Count, le)
            For Each entry In ifd0Entries : output.AddRange(entry) : Next
            For i = 0 To 3 : output.Add(tiff(end0 + i)) : Next
            Dim rebuilt = output.ToArray() : GeotagService.WriteUInt32(rebuilt, 4, CUInt(newIfd0), le)
            Return rebuilt
        End Function

        Private Shared Function CreateTiff(raw As String) As Byte()
            Dim bytes As New List(Of Byte) From {AscW("I"c), AscW("I"c), 42, 0, 8, 0, 0, 0}
            Dim dateOffset = 8 + 2 + 2 * 12 + 4
            GeotagService.AppendUInt16(bytes, 2, True)
            ' AUFSTEIGEND nach Tag, das verlangt TIFF: erst DateTime (0x132), dann der Exif-Zeiger
            ' (0x8769). Andersherum lesen es die meisten zwar trotzdem, aber eben nicht alle - und
            ' der Tausch kostet nichts, weil beide Eintraege zwoelf Byte lang sind.
            Dim dateEntry(11) As Byte : GeotagService.WriteUInt16(dateEntry, 0, DateTimeTag, True) : GeotagService.WriteUInt16(dateEntry, 2, 2, True) : GeotagService.WriteUInt32(dateEntry, 4, 20UI, True) : GeotagService.WriteUInt32(dateEntry, 8, CUInt(dateOffset), True) : bytes.AddRange(dateEntry)
            Dim pointer(11) As Byte : GeotagService.WriteUInt16(pointer, 0, ExifIfdPointerTag, True) : GeotagService.WriteUInt16(pointer, 2, 4, True) : GeotagService.WriteUInt32(pointer, 4, 1UI, True) : GeotagService.WriteUInt32(pointer, 8, CUInt(dateOffset + 20), True) : bytes.AddRange(pointer) : GeotagService.AppendUInt32(bytes, 0UI, True)
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(raw & ChrW(0)))
            GeotagService.AppendUInt16(bytes, 2, True) : Dim o(11) As Byte : GeotagService.WriteUInt16(o, 0, DateTimeOriginalTag, True) : GeotagService.WriteUInt16(o, 2, 2, True) : GeotagService.WriteUInt32(o, 4, 20UI, True) : GeotagService.WriteUInt32(o, 8, CUInt(dateOffset), True) : bytes.AddRange(o) : Dim d(11) As Byte : Buffer.BlockCopy(o, 0, d, 0, 12) : GeotagService.WriteUInt16(d, 0, DateTimeDigitizedTag, True) : bytes.AddRange(d) : GeotagService.AppendUInt32(bytes, 0UI, True)
            Return bytes.ToArray()
        End Function

        Private Shared Sub UpsertAscii(entries As List(Of Byte()), tag As Integer, offset As Integer, le As Boolean)
            Dim entry = entries.FirstOrDefault(Function(e) GeotagService.ReadUInt16(e, 0, le) = tag)
            If entry Is Nothing Then entry = New Byte(11) {} : entries.Add(entry)
            GeotagService.WriteUInt16(entry, 0, tag, le) : GeotagService.WriteUInt16(entry, 2, 2, le) : GeotagService.WriteUInt32(entry, 4, 20UI, le) : GeotagService.WriteUInt32(entry, 8, CUInt(offset), le)
        End Sub
        Private Shared Sub UpsertLong(entries As List(Of Byte()), tag As Integer, value As Integer, le As Boolean)
            Dim entry = entries.FirstOrDefault(Function(e) GeotagService.ReadUInt16(e, 0, le) = tag)
            If entry Is Nothing Then entry = New Byte(11) {} : entries.Add(entry)
            GeotagService.WriteUInt16(entry, 0, tag, le) : GeotagService.WriteUInt16(entry, 2, 4, le) : GeotagService.WriteUInt32(entry, 4, 1UI, le) : GeotagService.WriteUInt32(entry, 8, CUInt(value), le)
        End Sub
        Private Shared Sub WriteIfd(output As List(Of Byte), entries As List(Of Byte()), le As Boolean)
            entries.Sort(Function(a, b) GeotagService.ReadUInt16(a, 0, le).CompareTo(GeotagService.ReadUInt16(b, 0, le))) : GeotagService.AppendUInt16(output, entries.Count, le) : For Each e In entries : output.AddRange(e) : Next : GeotagService.AppendUInt32(output, 0UI, le)
        End Sub
    End Class
End Namespace
