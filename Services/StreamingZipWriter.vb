Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Ein Eintrag eines ZIP, das <see cref="StreamingZipWriter"/> schreibt: woher die
    ''' Bytes kommen, unter welchem Namen sie im Archiv stehen und wie gross sie sind.</summary>
    Friend NotInheritable Class ZipEntryPlan
        ''' <summary>Die Datei, aus der beim Schreiben gelesen wird. Leer bei einem Eintrag, der
        ''' schon gepackt im Speicher liegt.</summary>
        Public SourcePath As String = ""
        Public EntryName As String = ""
        ''' <summary>Groesse der Datei, wie sie beim Planen auf der Platte stand.</summary>
        Public Size As Long
        Public Modified As DateTime = DateTime.Now
        ''' <summary>Gesetzt bei einem kleinen Eintrag, der im Speicher gepackt wurde (Deflate);
        ''' Nothing heisst: ungepackt (Stored) und beim Schreiben von der Platte gelesen.</summary>
        Public Packed As Byte()
        ''' <summary>Pruefsumme eines im Speicher gepackten Eintrags. Fuer die ungepackten entsteht
        ''' sie erst beim Schreiben und steht dann im Datennachsatz.</summary>
        Public Crc As UInteger
        ''' <summary>Gesetzt bei einem ungepackten Eintrag, dessen Bytes erst beim Schreiben
        ''' entstehen (ein JPEG ohne Metadaten fuer eine Freigabe); dann wird nicht von
        ''' <see cref="SourcePath"/> gelesen. Liefert sie nicht genau <see cref="Size"/> Bytes, endet
        ''' das Archiv mit einem Abbruch.</summary>
        Public Producer As Func(Of Byte())
    End Class

    ''' <summary>
    ''' Schreibt ein ZIP STROEMEND in einen Ausgabestrom, ohne das Archiv vorher auf die Platte oder
    ''' in den Speicher zu legen: eine Auswahl von RAWs ergibt schnell Gigabytes.
    '''
    ''' <para>WARUM EIN EIGENER SCHREIBER und nicht <c>ZipArchive</c>: dessen Verhalten an einem Strom
    ''' ohne Suchen ist fuer Eintraege ueber 4 GB nicht zugesagt, und die Gesamtlaenge liesse sich
    ''' vorher nicht ausrechnen. Hier steht beides fest: die Laenge kommt aus <see cref="TotalLength"/>,
    ''' damit der Browser einen Fortschritt zeigen kann, und ZIP64 wird je Eintrag vorab
    ''' entschieden.</para>
    '''
    ''' <para>UNGEPACKT (Stored) ist die Regel. Fotos, RAWs und Videos sind schon gepackt, Deflate
    ''' kostete nur Rechenzeit. Gepackt werden nur KLEINE Dateien in ungepackten Formaten
    ''' (Beistelldateien, BMP, TIFF, PSD bis <see cref="InMemoryLimit"/>), und die im Speicher:
    ''' ihre Groesse steht dann vor dem Schreiben fest.</para>
    '''
    ''' <para>Ein ungepackter Eintrag traegt im Kopf keine Pruefsumme (Bit 3); sie steht im
    ''' Datennachsatz, gerechnet beim Durchlauf. Eine Datei wird so nur einmal gelesen.</para>
    ''' </summary>
    Friend NotInheritable Class StreamingZipWriter

        Private Sub New()
        End Sub

        ''' <summary>Bis zu dieser Groesse wird ein Eintrag in einem ungepackten Format im Speicher
        ''' gepackt.</summary>
        Friend Const InMemoryLimit As Long = 4L * 1024 * 1024

        ''' <summary>Ab hier bekommt ein Eintrag ZIP64. Deutlich unter 4 GB, weil Deflate bei nicht
        ''' packbaren Daten ein wenig zulegt; eine Grenze genau auf 4 GB koennte gepackt darueber
        ''' landen.</summary>
        Friend Const Zip64EntryThreshold As Long = &HF0000000L

        Private Const Max32 As Long = &HFFFFFFFFL
        Private Const MaxEntries16 As Integer = &HFFFF
        Private Const FlagDataDescriptor As Integer = &H8
        Private Const FlagUtf8 As Integer = &H800
        Private Const MethodStored As Integer = 0
        Private Const MethodDeflate As Integer = 8

        ''' <summary>Endungen ungepackter Formate, die sich klein im Speicher packen lassen.</summary>
        Private Shared ReadOnly PackableExtensions As New HashSet(Of String)(
            {".xmp", ".fpxmp", ".bmp", ".tif", ".tiff", ".psd", ".psb"}, StringComparer.OrdinalIgnoreCase)

        ''' <summary>Plant einen Eintrag aus einer Datei. Kleine Dateien ungepackter Formate werden
        ''' dabei gepackt, ausser <paramref name="allowPacking"/> ist False (der Ordner-Download,
        ''' sobald sein Speicherrahmen fuer gepackte Eintraege aufgebraucht ist).</summary>
        Friend Shared Function PlanFile(sourcePath As String, entryName As String, Optional allowPacking As Boolean = True) As ZipEntryPlan
            Dim info As New FileInfo(sourcePath)
            Dim plan As New ZipEntryPlan With {
                .SourcePath = sourcePath,
                .EntryName = entryName,
                .Size = info.Length,
                .Modified = info.LastWriteTime}
            If allowPacking AndAlso info.Length <= InMemoryLimit AndAlso PackableExtensions.Contains(info.Extension) Then
                Dim raw = File.ReadAllBytes(sourcePath)
                plan.Size = raw.Length
                Dim checksum As New Crc32Accumulator()
                checksum.Append(raw, 0, raw.Length)
                plan.Crc = checksum.Value
                Using packed As New MemoryStream()
                    Using deflate As New DeflateStream(packed, CompressionLevel.Optimal, leaveOpen:=True)
                        deflate.Write(raw, 0, raw.Length)
                    End Using
                    ' Ein Eintrag, der gepackt nicht kleiner wird, bleibt ungepackt.
                    If packed.Length < raw.Length Then plan.Packed = packed.ToArray()
                End Using
            End If
            Return plan
        End Function

        ''' <summary>Braucht der Eintrag selbst ZIP64 (seiner Groesse wegen)?</summary>
        Friend Shared Function EntryNeedsZip64(entry As ZipEntryPlan, forceZip64 As Boolean) As Boolean
            Return forceZip64 OrElse entry.Size >= Zip64EntryThreshold
        End Function

        Private Shared Function StoredSize(entry As ZipEntryPlan) As Long
            Return If(entry.Packed IsNot Nothing, entry.Packed.LongLength, entry.Size)
        End Function

        Private Shared Function LocalHeaderLength(entry As ZipEntryPlan, zip64 As Boolean) As Long
            Return 30 + Encoding.UTF8.GetByteCount(entry.EntryName) + If(zip64, 20, 0)
        End Function

        ''' <summary>Der Datennachsatz: nur bei ungepackten Eintraegen, deren Pruefsumme erst beim
        ''' Schreiben entsteht. Mit ZIP64 tragen die Groessen acht Bytes.</summary>
        Private Shared Function DescriptorLength(entry As ZipEntryPlan, zip64 As Boolean) As Long
            If entry.Packed IsNot Nothing Then Return 0
            Return If(zip64, 24, 16)
        End Function

        ''' <summary>Welche Felder des Verzeichniseintrags ins ZIP64-Feld ausweichen: Groesse,
        ''' gepackte Groesse, Lage des Kopfs, in dieser Reihenfolge.</summary>
        Private Shared Function CentralZip64Fields(entry As ZipEntryPlan, zip64 As Boolean, headerOffset As Long) As Integer
            Dim count = 0
            If zip64 OrElse entry.Size >= Max32 Then count += 1
            If zip64 OrElse StoredSize(entry) >= Max32 Then count += 1
            If zip64 OrElse headerOffset >= Max32 Then count += 1
            Return count
        End Function

        Private Shared Function CentralRecordLength(entry As ZipEntryPlan, zip64 As Boolean, headerOffset As Long) As Long
            Dim fields = CentralZip64Fields(entry, zip64, headerOffset)
            Return 46 + Encoding.UTF8.GetByteCount(entry.EntryName) + If(fields > 0, 4 + 8 * fields, 0)
        End Function

        ''' <summary>Braucht das Archiv als Ganzes die ZIP64-Schlussdaten?</summary>
        Friend Shared Function ArchiveNeedsZip64(entries As IList(Of ZipEntryPlan), forceZip64 As Boolean) As Boolean
            Dim offset = 0L
            Dim directorySize = 0L
            Dim anyZip64 = forceZip64
            For Each entry In entries
                Dim zip64 = EntryNeedsZip64(entry, forceZip64)
                anyZip64 = anyZip64 OrElse zip64
                directorySize += CentralRecordLength(entry, zip64, offset)
                offset += LocalHeaderLength(entry, zip64) + StoredSize(entry) + DescriptorLength(entry, zip64)
            Next
            Return anyZip64 OrElse entries.Count >= MaxEntries16 OrElse offset >= Max32 OrElse directorySize >= Max32
        End Function

        ''' <summary>Die Laenge des fertigen Archivs in Bytes, ohne etwas zu schreiben.</summary>
        Friend Shared Function TotalLength(entries As IList(Of ZipEntryPlan), Optional forceZip64 As Boolean = False) As Long
            Dim offset = 0L
            Dim directorySize = 0L
            For Each entry In entries
                Dim zip64 = EntryNeedsZip64(entry, forceZip64)
                directorySize += CentralRecordLength(entry, zip64, offset)
                offset += LocalHeaderLength(entry, zip64) + StoredSize(entry) + DescriptorLength(entry, zip64)
            Next
            Dim tail = 22L
            If ArchiveNeedsZip64(entries, forceZip64) Then tail += 56 + 20
            Return offset + directorySize + tail
        End Function

        ''' <summary>Schreibt das Archiv. Stimmt die Groesse einer Datei beim Lesen nicht mehr mit der
        ''' geplanten, endet es mit einer IOException: die angekuendigte Laenge waere sonst falsch,
        ''' und der Browser hielte ein kaputtes Archiv fuer fertig.</summary>
        Friend Shared Async Function WriteAsync(target As Stream, entries As IList(Of ZipEntryPlan),
                                                Optional forceZip64 As Boolean = False) As Task
            Dim offset = 0L
            Dim offsets As New List(Of Long)()
            Dim checksums As New List(Of UInteger)()
            Dim buffer(1024 * 1024 - 1) As Byte

            For Each entry In entries
                Dim zip64 = EntryNeedsZip64(entry, forceZip64)
                offsets.Add(offset)
                Dim header = LocalHeader(entry, zip64)
                Await target.WriteAsync(header, 0, header.Length)
                offset += header.Length

                If entry.Packed IsNot Nothing Then
                    Await target.WriteAsync(entry.Packed, 0, entry.Packed.Length)
                    offset += entry.Packed.Length
                    checksums.Add(entry.Crc)
                    Continue For
                End If

                Dim checksum As New Crc32Accumulator()
                If entry.Producer IsNot Nothing Then
                    Dim produced = entry.Producer()
                    If produced Is Nothing OrElse produced.LongLength <> entry.Size Then
                        Throw New IOException($"{entry.EntryName} changed while it was being sent.")
                    End If
                    checksum.Append(produced, 0, produced.Length)
                    Await target.WriteAsync(produced, 0, produced.Length)
                    offset += produced.Length
                    checksums.Add(checksum.Value)
                    Dim producedDescriptor = DataDescriptor(checksum.Value, entry.Size, zip64)
                    Await target.WriteAsync(producedDescriptor, 0, producedDescriptor.Length)
                    offset += producedDescriptor.Length
                    Continue For
                End If
                Dim written = 0L
                Using source As New FileStream(entry.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous Or FileOptions.SequentialScan)
                    Do
                        Dim wanted = CInt(Math.Min(buffer.Length, entry.Size - written))
                        If wanted <= 0 Then Exit Do
                        Dim read = Await source.ReadAsync(buffer, 0, wanted)
                        If read <= 0 Then Exit Do
                        checksum.Append(buffer, 0, read)
                        Await target.WriteAsync(buffer, 0, read)
                        written += read
                    Loop
                End Using
                If written <> entry.Size Then
                    Throw New IOException($"{entry.SourcePath} changed while it was being sent.")
                End If
                offset += written
                checksums.Add(checksum.Value)
                Dim descriptor = DataDescriptor(checksum.Value, entry.Size, zip64)
                Await target.WriteAsync(descriptor, 0, descriptor.Length)
                offset += descriptor.Length
            Next

            Dim directoryStart = offset
            For index = 0 To entries.Count - 1
                Dim entry = entries(index)
                Dim record = CentralRecord(entry, EntryNeedsZip64(entry, forceZip64), offsets(index), checksums(index))
                Await target.WriteAsync(record, 0, record.Length)
                offset += record.Length
            Next
            Dim tail = EndRecords(entries.Count, directoryStart, offset - directoryStart, ArchiveNeedsZip64(entries, forceZip64))
            Await target.WriteAsync(tail, 0, tail.Length)
            Await target.FlushAsync()
        End Function

        Private Shared Function Flags(entry As ZipEntryPlan) As Integer
            Return FlagUtf8 Or If(entry.Packed Is Nothing, FlagDataDescriptor, 0)
        End Function

        Private Shared Function Method(entry As ZipEntryPlan) As Integer
            Return If(entry.Packed IsNot Nothing, MethodDeflate, MethodStored)
        End Function

        ''' <summary>Der Kopf vor den Daten eines Eintrags. Bei einem ungepackten Eintrag stehen
        ''' Pruefsumme und Groessen auf null (Bit 3, sie folgen im Datennachsatz); mit ZIP64 stehen
        ''' die Groessen auf 0xFFFFFFFF und das ZIP64-Feld folgt mit Nullen.</summary>
        Friend Shared Function LocalHeader(entry As ZipEntryPlan, zip64 As Boolean) As Byte()
            Dim name = Encoding.UTF8.GetBytes(entry.EntryName)
            Using stream As New MemoryStream(), writer As New BinaryWriter(stream)
                writer.Write(&H4034B50UI)
                writer.Write(CUShort(If(zip64, 45, 20)))
                writer.Write(CUShort(Flags(entry)))
                writer.Write(CUShort(Method(entry)))
                WriteDosTime(writer, entry.Modified)
                Dim known = entry.Packed IsNot Nothing
                writer.Write(If(known, entry.Crc, 0UI))
                If zip64 Then
                    writer.Write(&HFFFFFFFFUI)
                    writer.Write(&HFFFFFFFFUI)
                Else
                    writer.Write(If(known, CUInt(entry.Packed.LongLength), 0UI))
                    writer.Write(If(known, CUInt(entry.Size), 0UI))
                End If
                writer.Write(CUShort(name.Length))
                writer.Write(CUShort(If(zip64, 20, 0)))
                writer.Write(name)
                If zip64 Then
                    writer.Write(CUShort(1))
                    writer.Write(CUShort(16))
                    writer.Write(If(known, entry.Size, 0L))
                    writer.Write(If(known, entry.Packed.LongLength, 0L))
                End If
                writer.Flush()
                Return stream.ToArray()
            End Using
        End Function

        Private Shared Function DataDescriptor(crc As UInteger, size As Long, zip64 As Boolean) As Byte()
            Using stream As New MemoryStream(), writer As New BinaryWriter(stream)
                writer.Write(&H8074B50UI)
                writer.Write(crc)
                If zip64 Then
                    writer.Write(size)
                    writer.Write(size)
                Else
                    writer.Write(CUInt(size))
                    writer.Write(CUInt(size))
                End If
                writer.Flush()
                Return stream.ToArray()
            End Using
        End Function

        Friend Shared Function CentralRecord(entry As ZipEntryPlan, zip64 As Boolean, headerOffset As Long, crc As UInteger) As Byte()
            Dim name = Encoding.UTF8.GetBytes(entry.EntryName)
            Dim stored = StoredSize(entry)
            Dim sizeAside = zip64 OrElse entry.Size >= Max32
            Dim storedAside = zip64 OrElse stored >= Max32
            Dim offsetAside = zip64 OrElse headerOffset >= Max32
            Dim fields = CentralZip64Fields(entry, zip64, headerOffset)
            Dim needed = If(fields > 0, 45, 20)
            Using stream As New MemoryStream(), writer As New BinaryWriter(stream)
                writer.Write(&H2014B50UI)
                writer.Write(CUShort(needed))
                writer.Write(CUShort(needed))
                writer.Write(CUShort(Flags(entry)))
                writer.Write(CUShort(Method(entry)))
                WriteDosTime(writer, entry.Modified)
                writer.Write(crc)
                writer.Write(If(storedAside, &HFFFFFFFFUI, CUInt(stored)))
                writer.Write(If(sizeAside, &HFFFFFFFFUI, CUInt(entry.Size)))
                writer.Write(CUShort(name.Length))
                writer.Write(CUShort(If(fields > 0, 4 + 8 * fields, 0)))
                writer.Write(CUShort(0))
                writer.Write(CUShort(0))
                writer.Write(CUShort(0))
                writer.Write(0UI)
                writer.Write(If(offsetAside, &HFFFFFFFFUI, CUInt(headerOffset)))
                writer.Write(name)
                If fields > 0 Then
                    writer.Write(CUShort(1))
                    writer.Write(CUShort(8 * fields))
                    If sizeAside Then writer.Write(entry.Size)
                    If storedAside Then writer.Write(stored)
                    If offsetAside Then writer.Write(headerOffset)
                End If
                writer.Flush()
                Return stream.ToArray()
            End Using
        End Function

        ''' <summary>Der Schluss des Archivs: mit ZIP64 erst dessen Verzeichnisende und der
        ''' Verweis darauf, dann das gewoehnliche Ende mit den Platzhaltern 0xFFFF und
        ''' 0xFFFFFFFF.</summary>
        Friend Shared Function EndRecords(count As Integer, directoryStart As Long, directorySize As Long, zip64 As Boolean) As Byte()
            Using stream As New MemoryStream(), writer As New BinaryWriter(stream)
                If zip64 Then
                    Dim zip64End = directoryStart + directorySize
                    writer.Write(&H6064B50UI)
                    writer.Write(44L)
                    writer.Write(CUShort(45))
                    writer.Write(CUShort(45))
                    writer.Write(0UI)
                    writer.Write(0UI)
                    writer.Write(CLng(count))
                    writer.Write(CLng(count))
                    writer.Write(directorySize)
                    writer.Write(directoryStart)
                    writer.Write(&H7064B50UI)
                    writer.Write(0UI)
                    writer.Write(zip64End)
                    writer.Write(1UI)
                End If
                writer.Write(&H6054B50UI)
                writer.Write(CUShort(0))
                writer.Write(CUShort(0))
                Dim shortCount = If(zip64 OrElse count >= MaxEntries16, &HFFFF, count)
                writer.Write(CUShort(shortCount))
                writer.Write(CUShort(shortCount))
                writer.Write(If(zip64 OrElse directorySize >= Max32, &HFFFFFFFFUI, CUInt(directorySize)))
                writer.Write(If(zip64 OrElse directoryStart >= Max32, &HFFFFFFFFUI, CUInt(directoryStart)))
                writer.Write(CUShort(0))
                writer.Flush()
                Return stream.ToArray()
            End Using
        End Function

        ''' <summary>Zeit und Datum im Format von DOS, auf zwei Sekunden genau, von 1980 bis 2107.</summary>
        Private Shared Sub WriteDosTime(writer As BinaryWriter, moment As DateTime)
            Dim year = Math.Clamp(moment.Year, 1980, 2107)
            Dim time = (moment.Hour << 11) Or (moment.Minute << 5) Or (moment.Second \ 2)
            Dim day = ((year - 1980) << 9) Or (moment.Month << 5) Or moment.Day
            writer.Write(CUShort(time))
            writer.Write(CUShort(day))
        End Sub

    End Class

    ''' <summary>Die Pruefsumme CRC-32 eines ZIP-Eintrags, Stueck fuer Stueck. Nur Schiebungen und
    ''' Xor, also ohne Ueberlaufpruefung.</summary>
    Friend NotInheritable Class Crc32Accumulator
        Private Shared ReadOnly Table As UInteger() = BuildTable()
        Private _state As UInteger = &HFFFFFFFFUI

        Private Shared Function BuildTable() As UInteger()
            Dim result(255) As UInteger
            For index = 0 To 255
                Dim value = CUInt(index)
                For bit = 0 To 7
                    If (value And 1UI) <> 0UI Then
                        value = (value >> 1) Xor &HEDB88320UI
                    Else
                        value >>= 1
                    End If
                Next
                result(index) = value
            Next
            Return result
        End Function

        Public Sub Append(data As Byte(), offset As Integer, count As Integer)
            Dim state = _state
            For index = offset To offset + count - 1
                state = Table(CInt((state Xor data(index)) And &HFFUI)) Xor (state >> 8)
            Next
            _state = state
        End Sub

        Public ReadOnly Property Value As UInteger
            Get
                Return _state Xor &HFFFFFFFFUI
            End Get
        End Property
    End Class

End Namespace
