Imports System.Buffers.Binary
Imports System.Collections.Generic
Imports System.IO

Namespace Services

    ''' <summary>Liest den EXIF-Eintrag einer HEIF-Datei selbst aus dem Container, ohne Dekoder.
    '''
    ''' Der Rueckfall fuer <see cref="ExifService.ReadMetadataDirectories"/>: MetadataExtractor liest
    ''' eine HEIF nur ganz oder gar nicht. Huawei legt neben EXIF einen eigenen Eintrag vom Typ
    ''' "mime" (application/vnd.huawei) ab, den MetadataExtractor fuer XMP haelt; die XMP-Bibliothek
    ''' erkennt darin UTF-16, wirft, und mit der Ausnahme fiel auch der intakte EXIF-Block weg:
    ''' Kamera, Aufnahmedaten und Ort blieben leer.
    '''
    ''' Gelesen wird nur, was dafuer noetig ist: die Box "meta", darin "iinf" (welcher Eintrag ist
    ''' EXIF) und "iloc" (wo liegen seine Bytes), bei Bedarf "idat". Kein libheif: auf dem Mac
    ''' liest das System die Bilder und libheif fehlt meist, und eine Metadatenabfrage soll nicht
    ''' hinter dem Decode-Schloss warten.</summary>
    Public NotInheritable Class HeifExifReader

        Private Sub New()
        End Sub

        ''' Obergrenze fuer die Box "meta" und fuer den EXIF-Block. Beide sind in der Praxis einige
        ''' Kilobyte gross; die Schranke faengt einen unsinnigen Wert ab, bevor er zur Anforderung wird.
        Private Const MaxBlockBytes As Long = 16L * 1024 * 1024

        ''' <summary>Die EXIF-Verzeichnisse, wie MetadataExtractor sie aus einem JPEG liefern wuerde,
        ''' oder eine leere Liste, wenn die Datei keinen lesbaren EXIF-Eintrag traegt.</summary>
        Public Shared Function ReadExifDirectories(path As String) As IReadOnlyList(Of MetadataExtractor.Directory)
            Dim payload = ReadExifPayload(path)
            ' Der Block beginnt mit dem Abstand zum TIFF-Kopf (vier Byte, gross-endian); davor kann
            ' noch "Exif\0\0" stehen.
            If payload Is Nothing OrElse payload.Length < 12 Then Return Array.Empty(Of MetadataExtractor.Directory)()
            Dim tiffStart = 4L + BinaryPrimitives.ReadUInt32BigEndian(payload)
            If tiffStart + 8 > payload.Length Then Return Array.Empty(Of MetadataExtractor.Directory)()
            Return New MetadataExtractor.Formats.Exif.ExifReader().Extract(
                New MetadataExtractor.IO.ByteArrayReader(payload, CInt(tiffStart)), CInt(tiffStart))
        End Function

        Private Structure ItemLocation
            Public ConstructionMethod As Integer
            Public Extents As List(Of (Offset As Long, Length As Long))
        End Structure

        Private Shared Function ReadExifPayload(path As String) As Byte()
            Using stream = New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                Dim meta = ReadTopLevelBox(stream, "meta")
                If meta Is Nothing OrElse meta.Length < 4 Then Return Nothing

                ' "meta" ist eine FullBox: vier Byte Fassung und Merker vor den Kindern.
                Dim exifIds As New HashSet(Of UInteger)
                Dim locations As New Dictionary(Of UInteger, ItemLocation)
                Dim idat As (Start As Integer, Length As Integer) = (-1, 0)
                For Each child In ChildBoxes(meta, 4, meta.Length)
                    Select Case child.Type
                        Case "iinf" : CollectExifItems(meta, child.Start, child.Finish, exifIds)
                        Case "iloc" : CollectLocations(meta, child.Start, child.Finish, locations)
                        Case "idat" : idat = (child.Start, child.Finish - child.Start)
                    End Select
                Next

                For Each id In exifIds
                    Dim location As ItemLocation = Nothing
                    If Not locations.TryGetValue(id, location) Then Continue For
                    Dim total = 0L
                    For Each extent In location.Extents
                        total += extent.Length
                    Next
                    If location.Extents.Count = 0 OrElse total <= 0 OrElse total > MaxBlockBytes Then Continue For

                    Dim payload(CInt(total) - 1) As Byte
                    Dim written = 0
                    Dim complete = True
                    For Each extent In location.Extents
                        Dim length = CInt(extent.Length)
                        If location.ConstructionMethod = 0 Then
                            If extent.Offset < 0 OrElse extent.Offset + length > stream.Length Then complete = False : Exit For
                            stream.Position = extent.Offset
                            stream.ReadExactly(payload, written, length)
                        ElseIf location.ConstructionMethod = 1 AndAlso idat.Start >= 0 Then
                            If extent.Offset < 0 OrElse extent.Offset + length > idat.Length Then complete = False : Exit For
                            Buffer.BlockCopy(meta, idat.Start + CInt(extent.Offset), payload, written, length)
                        Else
                            complete = False
                            Exit For
                        End If
                        written += length
                    Next
                    If complete Then Return payload
                Next
                Return Nothing
            End Using
        End Function

        ''' <summary>Der Inhalt der ersten Box dieses Typs auf oberster Ebene, ohne ihren Kopf.</summary>
        Private Shared Function ReadTopLevelBox(stream As Stream, wanted As String) As Byte()
            Dim header(15) As Byte
            Dim position = 0L
            While position + 8 <= stream.Length
                stream.Position = position
                stream.ReadExactly(header, 0, 8)
                Dim size = CLng(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)))
                Dim type = Text.Encoding.ASCII.GetString(header, 4, 4)
                Dim headerLength = 8L
                If size = 1 Then
                    stream.ReadExactly(header, 8, 8)
                    Dim large = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8))
                    If large > CULng(Long.MaxValue) Then Return Nothing
                    size = CLng(large)
                    headerLength = 16
                ElseIf size = 0 Then
                    size = stream.Length - position
                End If
                If size < headerLength OrElse position + size > stream.Length Then Return Nothing
                If type = wanted Then
                    Dim contentLength = size - headerLength
                    If contentLength > MaxBlockBytes Then Return Nothing
                    Dim content(CInt(contentLength) - 1) As Byte
                    stream.Position = position + headerLength
                    stream.ReadExactly(content, 0, content.Length)
                    Return content
                End If
                position += size
            End While
            Return Nothing
        End Function

        ''' <summary>Die Boxen zwischen <paramref name="start"/> und <paramref name="finish"/>, je mit
        ''' Typ und den Grenzen ihres Inhalts. Endet still an der ersten, die nicht passt.</summary>
        Private Shared Iterator Function ChildBoxes(data As Byte(), start As Integer, finish As Integer) As IEnumerable(Of (Type As String, Start As Integer, Finish As Integer))
            Dim position = start
            While position + 8 <= finish
                Dim size = CLng(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4)))
                Dim type = Text.Encoding.ASCII.GetString(data, position + 4, 4)
                Dim headerLength = 8
                If size = 1 Then
                    If position + 16 > finish Then Exit While
                    Dim large = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position + 8, 8))
                    If large > CULng(finish - position) Then Exit While
                    size = CLng(large)
                    headerLength = 16
                ElseIf size = 0 Then
                    size = finish - position
                End If
                If size < headerLength OrElse position + size > finish Then Exit While
                Yield (type, position + headerLength, position + CInt(size))
                position += CInt(size)
            End While
        End Function

        ''' <summary>Aus "iinf" die Nummern der Eintraege vom Typ "Exif". Nur die Fassungen 2 und 3
        ''' von "infe" tragen einen Typ; aeltere kommen in HEIF nicht vor.</summary>
        Private Shared Sub CollectExifItems(data As Byte(), start As Integer, finish As Integer, exifIds As HashSet(Of UInteger))
            If finish - start < 6 Then Return
            Dim version = data(start)
            Dim first = start + 4 + If(version = 0, 2, 4)
            For Each entry In ChildBoxes(data, first, finish)
                If entry.Type <> "infe" OrElse entry.Finish - entry.Start < 12 Then Continue For
                Dim entryVersion = data(entry.Start)
                If entryVersion < 2 Then Continue For
                Dim p = entry.Start + 4
                Dim id As UInteger
                If entryVersion = 2 Then
                    id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2))
                    p += 2
                Else
                    If entry.Finish - entry.Start < 14 Then Continue For
                    id = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4))
                    p += 4
                End If
                p += 2 ' protection_index
                If p + 4 > entry.Finish Then Continue For
                If Text.Encoding.ASCII.GetString(data, p, 4) = "Exif" Then exifIds.Add(id)
            Next
        End Sub

        ''' <summary>Aus "iloc" je Eintrag die Bauart und die Abschnitte seiner Bytes.</summary>
        Private Shared Sub CollectLocations(data As Byte(), start As Integer, finish As Integer, locations As Dictionary(Of UInteger, ItemLocation))
            If finish - start < 8 Then Return
            Dim version = data(start)
            Dim p = start + 4
            Dim offsetSize = data(p) >> 4
            Dim lengthSize = data(p) And &HF
            Dim baseOffsetSize = data(p + 1) >> 4
            Dim indexSize = If(version = 1 OrElse version = 2, data(p + 1) And &HF, 0)
            p += 2
            Dim itemCount As Long
            If version < 2 Then
                itemCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2)) : p += 2
            Else
                If p + 4 > finish Then Return
                itemCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4)) : p += 4
            End If

            For item = 1L To itemCount
                Dim id As UInteger
                If version < 2 Then
                    If p + 2 > finish Then Return
                    id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2)) : p += 2
                Else
                    If p + 4 > finish Then Return
                    id = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4)) : p += 4
                End If
                Dim method = 0
                If version = 1 OrElse version = 2 Then
                    If p + 2 > finish Then Return
                    method = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2)) And &HF
                    p += 2
                End If
                p += 2 ' data_reference_index
                Dim baseOffset As Long
                If Not TryReadSized(data, p, baseOffsetSize, finish, baseOffset) Then Return
                If p + 2 > finish Then Return
                Dim extentCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2)) : p += 2
                Dim extents As New List(Of (Offset As Long, Length As Long))
                For extent = 1 To CInt(extentCount)
                    Dim ignored As Long
                    If indexSize > 0 AndAlso Not TryReadSized(data, p, indexSize, finish, ignored) Then Return
                    Dim offset As Long, length As Long
                    If Not TryReadSized(data, p, offsetSize, finish, offset) Then Return
                    If Not TryReadSized(data, p, lengthSize, finish, length) Then Return
                    extents.Add((baseOffset + offset, length))
                Next
                locations(id) = New ItemLocation With {.ConstructionMethod = method, .Extents = extents}
            Next
        End Sub

        ''' <summary>Eine Zahl von 0, 4 oder 8 Byte Breite, gross-endian; schiebt
        ''' <paramref name="p"/> weiter. False bei unbekannter Breite oder zu kurzem Rest.</summary>
        Private Shared Function TryReadSized(data As Byte(), ByRef p As Integer, size As Integer, finish As Integer, ByRef value As Long) As Boolean
            value = 0
            Select Case size
                Case 0
                    Return True
                Case 4
                    If p + 4 > finish Then Return False
                    value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4))
                    p += 4
                    Return True
                Case 8
                    If p + 8 > finish Then Return False
                    Dim wide = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p, 8))
                    If wide > CULng(Long.MaxValue) Then Return False
                    value = CLng(wide)
                    p += 8
                    Return True
                Case Else
                    Return False
            End Select
        End Function

    End Class

End Namespace
