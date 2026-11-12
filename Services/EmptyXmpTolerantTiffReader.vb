Imports System
Imports System.Collections.Generic
Imports System.IO
Imports MetadataExtractor.Formats.Exif
Imports MetadataExtractor.Formats.FileSystem
Imports MetadataExtractor.Formats.Tiff
Imports MetadataExtractor.IO

Namespace Services

    ''' <summary>Der zweite Versuch fuer eine TIFF-artige Datei, an der der TIFF-Leser von
    ''' MetadataExtractor scheitert, weil sie einen LEEREN XMP-Eintrag traegt (Tag 0x02BC mit Laenge
    ''' 0). Der Leser reicht die null Bytes ungeprueft an seinen XMP-Leser weiter, der wirft
    ''' (IndexOutOfRangeException), und mit ihm geht die ganze Datei verloren: Kamera, Belichtung,
    ''' Ausrichtung, Objektiv. Gefunden an einer DNG aus dem Bestand.
    '''
    ''' Den Eintrag zu ueberspringen ginge am saubersten ueber eine abgeleitete
    ''' <c>ExifTiffHandler.CustomProcessTag</c>, doch die nimmt ihren Kontext als <c>in</c>-Parameter,
    ''' und den kann VB nicht ueberschreiben (BC30657). Deshalb liest dieser Weg die Datei ueber einen
    ''' Strom, der die Tag-Nummer genau dieser Eintraege als 0 ausgibt: einen Eintrag mit Tag 0 und
    ''' Laenge 0 uebergeht der Leser ausdruecklich. Alles andere liest er unveraendert.
    '''
    ''' Gesucht wird nur in IFD0 und im EXIF-Verzeichnis, denn nur dort wertet der Leser XMP aus.</summary>
    Friend NotInheritable Class EmptyXmpTolerantTiffReader

        Private Const XmpTag As Integer = &H2BC
        Private Const ExifIfdPointerTag As Integer = &H8769

        Private Sub New()
        End Sub

        ''' <summary>Die Verzeichnisse wie <c>TiffMetadataReader.ReadMetadata(Pfad)</c>, aber ohne die
        ''' leeren XMP-Eintraege. Nothing, wenn die Datei keinen solchen Eintrag hat: dann lag der
        ''' Fehler woanders, und der Aufrufer meldet den urspruenglichen.</summary>
        Public Shared Function TryReadMetadata(path As String) As IReadOnlyList(Of MetadataExtractor.Directory)
            Dim result As New List(Of MetadataExtractor.Directory)()
            Using stream = New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess)
                Dim patches = FindEmptyXmpEntries(stream)
                If patches.Count = 0 Then Return Nothing
                stream.Position = 0
                TiffReader.ProcessTiff(New IndexedSeekingReader(New PatchedReadStream(stream, patches)),
                                       New ExifTiffHandler(result, 0))
            End Using
            result.Add(New FileMetadataReader().Read(path))
            Return result
        End Function

        ''' <summary>Die Dateipositionen der Tag-Nummern leerer XMP-Eintraege, je zwei Bytes, mit dem
        ''' Ersatzwert 0. Liest nur Kopf und Eintragstabellen, nie Bilddaten.</summary>
        Friend Shared Function FindEmptyXmpEntries(stream As Stream) As Dictionary(Of Long, Byte)
            Dim patches As New Dictionary(Of Long, Byte)()
            Dim head(7) As Byte
            stream.Position = 0
            If stream.Read(head, 0, head.Length) < head.Length Then Return patches
            Dim motorola = head(0) = &H4D AndAlso head(1) = &H4D
            If Not motorola AndAlso Not (head(0) = &H49 AndAlso head(1) = &H49) Then Return patches

            Dim pending As New Queue(Of Long)()
            pending.Enqueue(ReadUInt32(head, 4, motorola))
            Dim visited As New HashSet(Of Long)()
            While pending.Count > 0
                Dim ifd = pending.Dequeue()
                If ifd < head.Length OrElse ifd + 2 > stream.Length OrElse Not visited.Add(ifd) Then Continue While
                Dim countBytes(1) As Byte
                stream.Position = ifd
                stream.ReadExactly(countBytes, 0, countBytes.Length)
                Dim entries = ReadUInt16(countBytes, 0, motorola)
                If entries = 0 OrElse ifd + 2 + 12L * entries > stream.Length Then Continue While
                Dim table(12 * entries - 1) As Byte
                stream.ReadExactly(table, 0, table.Length)
                For i = 0 To entries - 1
                    Dim entry = 12 * i
                    Dim tag = ReadUInt16(table, entry, motorola)
                    If tag = XmpTag AndAlso ReadUInt32(table, entry + 4, motorola) = 0 Then
                        patches(ifd + 2 + entry) = 0
                        patches(ifd + 3 + entry) = 0
                    ElseIf tag = ExifIfdPointerTag Then
                        pending.Enqueue(ReadUInt32(table, entry + 8, motorola))
                    End If
                Next
            End While
            Return patches
        End Function

        Private Shared Function ReadUInt16(bytes() As Byte, index As Integer, motorola As Boolean) As Integer
            Return If(motorola,
                      (CInt(bytes(index)) << 8) Or bytes(index + 1),
                      CInt(bytes(index)) Or (CInt(bytes(index + 1)) << 8))
        End Function

        Private Shared Function ReadUInt32(bytes() As Byte, index As Integer, motorola As Boolean) As Long
            Return If(motorola,
                      (CLng(bytes(index)) << 24) Or (CLng(bytes(index + 1)) << 16) Or (CLng(bytes(index + 2)) << 8) Or bytes(index + 3),
                      CLng(bytes(index)) Or (CLng(bytes(index + 1)) << 8) Or (CLng(bytes(index + 2)) << 16) Or (CLng(bytes(index + 3)) << 24))
        End Function

        ''' <summary>Ein Lesestrom ueber der Datei, der an einzelnen Positionen andere Bytes ausgibt.
        ''' Die Datei selbst bleibt unberuehrt.</summary>
        Private NotInheritable Class PatchedReadStream
            Inherits Stream

            Private ReadOnly _inner As Stream
            Private ReadOnly _patches As IReadOnlyDictionary(Of Long, Byte)

            Public Sub New(inner As Stream, patches As IReadOnlyDictionary(Of Long, Byte))
                _inner = inner
                _patches = patches
            End Sub

            Public Overrides ReadOnly Property CanRead As Boolean
                Get
                    Return True
                End Get
            End Property

            Public Overrides ReadOnly Property CanSeek As Boolean
                Get
                    Return True
                End Get
            End Property

            Public Overrides ReadOnly Property CanWrite As Boolean
                Get
                    Return False
                End Get
            End Property

            Public Overrides ReadOnly Property Length As Long
                Get
                    Return _inner.Length
                End Get
            End Property

            Public Overrides Property Position As Long
                Get
                    Return _inner.Position
                End Get
                Set(value As Long)
                    _inner.Position = value
                End Set
            End Property

            Public Overrides Function Read(buffer() As Byte, offset As Integer, count As Integer) As Integer
                Dim start = _inner.Position
                Dim delivered = _inner.Read(buffer, offset, count)
                For Each patch In _patches
                    If patch.Key >= start AndAlso patch.Key < start + delivered Then
                        buffer(offset + CInt(patch.Key - start)) = patch.Value
                    End If
                Next
                Return delivered
            End Function

            Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
                Return _inner.Seek(offset, origin)
            End Function

            Public Overrides Sub Flush()
            End Sub

            Public Overrides Sub SetLength(value As Long)
                Throw New NotSupportedException()
            End Sub

            Public Overrides Sub Write(buffer() As Byte, offset As Integer, count As Integer)
                Throw New NotSupportedException()
            End Sub
        End Class
    End Class
End Namespace
